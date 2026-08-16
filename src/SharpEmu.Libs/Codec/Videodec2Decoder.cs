// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using FFmpeg.AutoGen;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Media;

namespace SharpEmu.Libs.Codec;

/// <summary>
/// Asynchronous Annex-B H.264 decoder for one sceVideodec2 handle. The
/// decode/pacing concept comes from foufouadi's compatibility research, but
/// this version uses SharpEmu's shared FFmpeg loader and backend-neutral GPU
/// seam, and keeps all native teardown behind successful worker termination.
/// </summary>
internal sealed unsafe class Videodec2Decoder : IDisposable
{
    private const int FrameQueueCapacity = 4;
    private const double FallbackFramesPerSecond = 30.0;
    private const AVPixelFormat OutputPixelFormat = AVPixelFormat.AV_PIX_FMT_BGRA;

    private readonly Channel<DecodeWork> _workQueue =
        Channel.CreateUnbounded<DecodeWork>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly Channel<DecodedFrame> _frameQueue =
        Channel.CreateBounded<DecodedFrame>(new BoundedChannelOptions(FrameQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly ConcurrentQueue<ReadySignal> _readySignals = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread _worker;
    private readonly Thread _scheduler;

    private AVCodecContext* _codecContext;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _swsContext;
    private int _sourceWidth;
    private int _sourceHeight;
    private AVPixelFormat _sourceFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private long _generation;
    private int _drainQueued;
    private int _resetQueued;
    private int _disposeRequested;

    private static int _creationWarningCount;
    private static int _workerWarningCount;
    private static int _presentWarningCount;

    private enum DecodeWorkKind
    {
        AccessUnit,
        Drain,
        Reset,
    }

    private readonly record struct DecodeWork(DecodeWorkKind Kind, byte[]? AccessUnit);
    private readonly record struct ReadySignal(uint Width, uint Height, long Generation);
    private readonly record struct DecodedFrame(
        byte[] Bgra,
        uint Width,
        uint Height,
        double FramesPerSecond,
        long Generation);

    private Videodec2Decoder(
        AVCodecContext* codecContext,
        AVFrame* frame,
        AVPacket* packet)
    {
        _codecContext = codecContext;
        _frame = frame;
        _packet = packet;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SharpEmu Videodec2 Decoder",
        };
        _scheduler = new Thread(SchedulerLoop)
        {
            IsBackground = true,
            Name = "SharpEmu Videodec2 Presenter",
        };
        _worker.Start();
        _scheduler.Start();
    }

    public static Videodec2Decoder? TryCreate()
    {
        AVCodecContext* codecContext = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        var transferred = false;
        try
        {
            FfmpegRuntime.EnsureInitialized();
            var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
            if (codec == null)
            {
                return null;
            }

            codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (codecContext == null)
            {
                return null;
            }

            codecContext->thread_count = 0;
            codecContext->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
            if (ffmpeg.avcodec_open2(codecContext, codec, null) < 0)
            {
                return null;
            }

            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            if (frame == null || packet == null)
            {
                return null;
            }

            var decoder = new Videodec2Decoder(codecContext, frame, packet);
            transferred = true;
            return decoder;
        }
        catch (Exception exception)
        {
            if (Interlocked.Increment(ref _creationWarningCount) <= 4)
            {
                Console.Error.WriteLine(
                    $"[VIDEODEC2][WARN] Native H.264 decoder unavailable; using compatibility stub: {exception.Message}");
            }

            return null;
        }
        finally
        {
            if (!transferred)
            {
                TryFreeNative(ref codecContext, ref frame, ref packet);
            }
        }
    }

    public void EnqueueAccessUnit(byte[] accessUnit)
    {
        if (Volatile.Read(ref _disposeRequested) == 0)
        {
            _workQueue.Writer.TryWrite(new DecodeWork(DecodeWorkKind.AccessUnit, accessUnit));
        }
    }

    public void RequestDrain()
    {
        if (Volatile.Read(ref _disposeRequested) == 0 &&
            Interlocked.Exchange(ref _drainQueued, 1) == 0 &&
            !_workQueue.Writer.TryWrite(new DecodeWork(DecodeWorkKind.Drain, null)))
        {
            Interlocked.Exchange(ref _drainQueued, 0);
        }
    }

    public void RequestReset()
    {
        if (Volatile.Read(ref _disposeRequested) == 0 &&
            Interlocked.Exchange(ref _resetQueued, 1) == 0 &&
            !_workQueue.Writer.TryWrite(new DecodeWork(DecodeWorkKind.Reset, null)))
        {
            Interlocked.Exchange(ref _resetQueued, 0);
        }
    }

    public bool TryConsumeReadySignal(out uint width, out uint height)
    {
        var currentGeneration = Volatile.Read(ref _generation);
        while (_readySignals.TryDequeue(out var signal))
        {
            if (signal.Generation != currentGeneration)
            {
                continue;
            }

            width = signal.Width;
            height = signal.Height;
            return true;
        }

        width = 0;
        height = 0;
        return false;
    }

    private void WorkerLoop()
    {
        var reader = _workQueue.Reader;
        var cancellationToken = _cancellation.Token;
        try
        {
            while (reader.WaitToReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var work))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        switch (work.Kind)
                        {
                            case DecodeWorkKind.AccessUnit when work.AccessUnit is { } accessUnit:
                                DecodeAccessUnit(accessUnit, cancellationToken);
                                break;
                            case DecodeWorkKind.Drain:
                                try
                                {
                                    Drain(cancellationToken);
                                }
                                finally
                                {
                                    Interlocked.Exchange(ref _drainQueued, 0);
                                }
                                break;
                            case DecodeWorkKind.Reset:
                                try
                                {
                                    ResetDecoder();
                                }
                                finally
                                {
                                    Interlocked.Exchange(ref _resetQueued, 0);
                                }
                                break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        TraceWorkerFailure(exception);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
        finally
        {
            _frameQueue.Writer.TryComplete();
        }
    }

    private void SchedulerLoop()
    {
        var reader = _frameQueue.Reader;
        var cancellationToken = _cancellation.Token;
        long nextDeadline = 0;
        try
        {
            while (reader.WaitToReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var frame))
                {
                    if (frame.Generation != Volatile.Read(ref _generation))
                    {
                        continue;
                    }

                    var now = Stopwatch.GetTimestamp();
                    if (nextDeadline > now)
                    {
                        var delay = TimeSpan.FromSeconds(
                            (double)(nextDeadline - now) / Stopwatch.Frequency);
                        Task.Delay(delay, cancellationToken).GetAwaiter().GetResult();
                    }

                    try
                    {
                        GuestGpu.Current.Submit(frame.Bgra, frame.Width, frame.Height);
                    }
                    catch (Exception exception)
                    {
                        if (Interlocked.Increment(ref _presentWarningCount) <= 4)
                        {
                            Console.Error.WriteLine(
                                $"[VIDEODEC2][WARN] Frame presentation failed: {exception.Message}");
                        }
                    }

                    var intervalTicks = checked((long)Math.Max(
                        1,
                        Stopwatch.Frequency / NormalizeFrameRate(frame.FramesPerSecond)));
                    nextDeadline = Stopwatch.GetTimestamp() + intervalTicks;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private void DecodeAccessUnit(byte[] accessUnit, CancellationToken cancellationToken)
    {
        ffmpeg.av_packet_unref(_packet);
        var buffer = ffmpeg.av_malloc(
            checked((nuint)accessUnit.Length + (nuint)ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
        if (buffer == null)
        {
            return;
        }

        try
        {
            fixed (byte* source = accessUnit)
            {
                Buffer.MemoryCopy(source, buffer, accessUnit.Length, accessUnit.Length);
            }

            new Span<byte>(
                (byte*)buffer + accessUnit.Length,
                ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE).Clear();
            _packet->data = (byte*)buffer;
            _packet->size = accessUnit.Length;

            var sendResult = ffmpeg.avcodec_send_packet(_codecContext, _packet);
            if (sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                ReceiveAvailableFrames(cancellationToken);
                sendResult = ffmpeg.avcodec_send_packet(_codecContext, _packet);
            }

            if (sendResult >= 0)
            {
                ReceiveAvailableFrames(cancellationToken);
            }
        }
        finally
        {
            _packet->data = null;
            _packet->size = 0;
            ffmpeg.av_free(buffer);
        }
    }

    private void Drain(CancellationToken cancellationToken)
    {
        var sendResult = ffmpeg.avcodec_send_packet(_codecContext, null);
        if (sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            ReceiveAvailableFrames(cancellationToken);
            sendResult = ffmpeg.avcodec_send_packet(_codecContext, null);
        }

        if (sendResult >= 0 || sendResult == ffmpeg.AVERROR_EOF)
        {
            ReceiveAvailableFrames(cancellationToken);
        }
    }

    private void ResetDecoder()
    {
        ffmpeg.avcodec_flush_buffers(_codecContext);
        Interlocked.Increment(ref _generation);
        while (_readySignals.TryDequeue(out _))
        {
        }
        Interlocked.Exchange(ref _drainQueued, 0);
    }

    private void ReceiveAvailableFrames(CancellationToken cancellationToken)
    {
        while (true)
        {
            var receiveResult = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) ||
                receiveResult == ffmpeg.AVERROR_EOF)
            {
                return;
            }

            if (receiveResult < 0)
            {
                return;
            }

            try
            {
                var bgra = ConvertCurrentFrame(out var width, out var height);
                if (bgra is null)
                {
                    continue;
                }

                var generation = Volatile.Read(ref _generation);
                var frame = new DecodedFrame(
                    bgra,
                    width,
                    height,
                    ReadFrameRate(),
                    generation);
                _frameQueue.Writer.WriteAsync(frame, cancellationToken).AsTask().GetAwaiter().GetResult();
                _readySignals.Enqueue(new ReadySignal(width, height, generation));
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    private byte[]? ConvertCurrentFrame(out uint width, out uint height)
    {
        width = 0;
        height = 0;
        if (_frame->width <= 0 || _frame->height <= 0)
        {
            return null;
        }

        var requiredBytes = checked((long)_frame->width * _frame->height * 4);
        if (requiredBytes > int.MaxValue)
        {
            return null;
        }

        var sourceFormat = (AVPixelFormat)_frame->format;
        if (_swsContext == null ||
            _sourceWidth != _frame->width ||
            _sourceHeight != _frame->height ||
            _sourceFormat != sourceFormat)
        {
            _swsContext = ffmpeg.sws_getCachedContext(
                _swsContext,
                _frame->width,
                _frame->height,
                sourceFormat,
                _frame->width,
                _frame->height,
                OutputPixelFormat,
                ffmpeg.SWS_FAST_BILINEAR,
                null,
                null,
                null);
            if (_swsContext == null)
            {
                return null;
            }

            _sourceWidth = _frame->width;
            _sourceHeight = _frame->height;
            _sourceFormat = sourceFormat;
        }

        var output = GC.AllocateUninitializedArray<byte>(checked((int)requiredBytes));
        fixed (byte* destination = output)
        {
            var destinationPlanes = new byte*[4] { destination, null, null, null };
            var destinationStrides = new int[4] { checked(_frame->width * 4), 0, 0, 0 };
            var convertedRows = ffmpeg.sws_scale(
                _swsContext,
                _frame->data,
                _frame->linesize,
                0,
                _frame->height,
                destinationPlanes,
                destinationStrides);
            if (convertedRows != _frame->height)
            {
                return null;
            }
        }

        width = unchecked((uint)_frame->width);
        height = unchecked((uint)_frame->height);
        return output;
    }

    private double ReadFrameRate()
    {
        var rate = _codecContext->framerate;
        return rate.num > 0 && rate.den > 0
            ? NormalizeFrameRate((double)rate.num / rate.den)
            : FallbackFramesPerSecond;
    }

    private static double NormalizeFrameRate(double framesPerSecond) =>
        double.IsFinite(framesPerSecond) && framesPerSecond is >= 1.0 and <= 240.0
            ? framesPerSecond
            : FallbackFramesPerSecond;

    private static void TraceWorkerFailure(Exception exception)
    {
        if (Interlocked.Increment(ref _workerWarningCount) <= 4)
        {
            Console.Error.WriteLine(
                $"[VIDEODEC2][WARN] Decode worker recovered from {exception.GetType().Name}: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        _workQueue.Writer.TryComplete();
        _frameQueue.Writer.TryComplete();
        var workerStopped = _worker.Join(TimeSpan.FromSeconds(2));
        var schedulerStopped = _scheduler.Join(TimeSpan.FromSeconds(2));
        if (!workerStopped || !schedulerStopped)
        {
            // Do not free native state while a timed-out worker could still
            // be inside FFmpeg. A bounded leak is safer than a use-after-free.
            Console.Error.WriteLine(
                "[VIDEODEC2][WARN] Decoder shutdown timed out; native state retained for safety.");
            return;
        }

        _cancellation.Dispose();
        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

        TryFreeNative(ref _codecContext, ref _frame, ref _packet);
    }

    private static void TryFreeNative(
        ref AVCodecContext* codecContext,
        ref AVFrame* frame,
        ref AVPacket* packet)
    {
        try
        {
            if (packet != null)
            {
                var value = packet;
                ffmpeg.av_packet_free(&value);
                packet = null;
            }

            if (frame != null)
            {
                var value = frame;
                ffmpeg.av_frame_free(&value);
                frame = null;
            }

            if (codecContext != null)
            {
                var value = codecContext;
                ffmpeg.avcodec_free_context(&value);
                codecContext = null;
            }
        }
        catch
        {
            // Optional native runtime may have failed during initialization.
        }
    }
}
