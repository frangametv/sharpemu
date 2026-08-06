// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpEmu.Libs.Media;

// Attribution: the original host-side Bink2 bridge was authored by @xnetcat:
// https://github.com/xnetcat/sharpemu/commit/23cefcc69b32980724bfa9fb015f32fa518a02a9

/// <summary>
/// Host-side movie bridge for games that decode video inside their own
/// executable instead of going through an HLE decoder.
///
/// Such a game never imports libSceVideodec or sceAvPlayer, so no HLE export
/// can see its movie frames. Kernel file opens identify the active movie and
/// the presenter requests BGRA frames from <see cref="FfmpegVideoDecoder"/> —
/// the same decoder sceAvPlayer uses, so every format is handled in one place.
/// </summary>
internal static class HostMovieBridge
{
    private const int BinkHeaderSize = 0x24;
    private const uint MaxDimension = 16384;
    private const uint MaxFramesPerSecond = 1000;
    private const uint MaxHostVideoWidth = 1920;
    private const uint MaxHostVideoHeight = 1080;
    private static readonly string[] SelfDecodedMovieExtensions = [".bk2"];

    private static bool IsSelfDecodedMovie(string hostPath)
    {
        foreach (var extension in SelfDecodedMovieExtensions)
        {
            if (hostPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
    private static readonly object Gate = new();
    private static readonly HashSet<string> ObservedMovieRanges = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static NativeAdapter? _adapter;
    private static string? _activePath;
    private static long _activeOffset;
    private static long _activeLength;
    private static bool _activeIsRange;
    private static Bink2MovieInfo _activeInfo;
    private static byte[]? _frameBuffer;
    private static bool _frameBufferPresented;
    private static MediaFramePlayback? _playback;
    private static bool _usingDummyMovie;
    private static long _frameSerial;
    private static bool _loadAttempted;
    private static bool _availabilityReported;
    private static bool _rangeAdapterWarningReported;
    private static BinkMovieRangeResult? _lastRangeResult;
    private static uint _presentationWidth = MaxHostVideoWidth;
    private static uint _presentationHeight = MaxHostVideoHeight;

    internal static bool IsHostPlaybackActive
    {
        get
        {
            lock (Gate)
            {
                return _playback is not null || _frameBuffer is not null;
            }
        }
    }

    internal static void SetPresentationSize(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            return;
        }

        lock (Gate)
        {
            _presentationWidth = Math.Min(width, MaxHostVideoWidth);
            _presentationHeight = Math.Min(height, MaxHostVideoHeight);
        }
    }

    /// <summary>
    /// Returns true only when movie skipping was explicitly requested. Without
    /// a host adapter the guest must be allowed to run the Bink implementation
    /// statically linked into its executable.
    /// </summary>
    internal static bool ShouldSkipGuestMovie(string hostPath) =>
        IsSelfDecodedMovie(hostPath) &&
        ResolveMode() == MovieMode.Skip;

    /// <summary>
    /// Starts or queues host decoding. Decoded frames are only exposed as a
    /// sampled guest texture; presentation and UI composition remain guest-owned.
    /// </summary>
    internal static bool ObserveGuestMovie(string hostPath)
    {
        if (!IsSelfDecodedMovie(hostPath) || !File.Exists(hostPath))
        {
            return false;
        }

        lock (Gate)
        {
            if (!_activeIsRange &&
                string.Equals(_activePath, hostPath, StringComparison.OrdinalIgnoreCase))
            {
                return _playback is not null || _frameBuffer is not null;
            }

            var mode = ResolveMode();
            if (mode is MovieMode.Guest or MovieMode.Skip)
            {
                return false;
            }

            if (_playback is not null || _frameBuffer is not null)
            {
                if (PendingMoviePathSet.Add(hostPath))
                {
                    PendingMoviePaths.Enqueue(hostPath);
                    Console.Error.WriteLine(
                        "[LOADER][INFO] Bink2 bridge queued: " +
                        Path.GetFileName(hostPath));
                }
                return PendingMoviePathSet.Contains(hostPath);
            }

            AttachMovieLocked(hostPath, mode);
            return string.Equals(_activePath, hostPath, StringComparison.OrdinalIgnoreCase) &&
                   (_playback is not null || _frameBuffer is not null);
        }
    }

    /// <summary>
    /// Observes a positional read that begins with an embedded Bink movie. The
    /// returned result deliberately separates detection from policy: callers can
    /// inspect <see cref="BinkMovieRangeResult.Mode"/>, but pread itself must not
    /// turn a validated movie into EOF or an error without caller-contract evidence.
    /// </summary>
    internal static BinkMovieRangeResult? ObserveGuestMovieRange(
        string hostPath,
        long hostFileLength,
        int fileDescriptor,
        long fileOffset,
        int requestedLength,
        int readLength,
        ulong guestDestination,
        ulong guestRip,
        ulong guestReturnRip,
        ulong guestCallerReturnRip,
        ReadOnlySpan<byte> bytes)
    {
        if (string.IsNullOrWhiteSpace(hostPath) ||
            requestedLength < 0 ||
            readLength < 0 ||
            readLength > requestedLength ||
            readLength > bytes.Length ||
            !TryParseMovieRangeHeader(
                bytes[..readLength],
                fileOffset,
                hostFileLength,
                out var header))
        {
            return null;
        }

        lock (Gate)
        {
            var mode = ResolveRangeMode();
            var attachment = BinkMovieRangeAttachment.None;

            if (IsActiveRangeLocked(hostPath, fileOffset, header.ByteLength))
            {
                attachment = _usingDummyMovie
                    ? BinkMovieRangeAttachment.Dummy
                    : _playback is not null
                        ? BinkMovieRangeAttachment.Native
                        : BinkMovieRangeAttachment.None;
            }
            else if (mode == BinkMovieMode.Dummy)
            {
                AttachDummyMovieLocked(hostPath, fileOffset, header);
                attachment = _usingDummyMovie && IsActiveRangeLocked(hostPath, fileOffset, header.ByteLength)
                    ? BinkMovieRangeAttachment.Dummy
                    : BinkMovieRangeAttachment.None;
            }
            else if (mode == BinkMovieMode.Native)
            {
                attachment = TryAttachNativeMovieRangeLocked(hostPath, fileOffset, header);
            }

            var result = new BinkMovieRangeResult(
                hostPath,
                fileDescriptor,
                fileOffset,
                requestedLength,
                readLength,
                guestDestination,
                guestRip,
                guestReturnRip,
                guestCallerReturnRip,
                Environment.CurrentManagedThreadId,
                Thread.CurrentThread.Name,
                header,
                mode,
                attachment);
            RecordMovieRangeLocked(result);
            return result;
        }
    }

    internal static bool TryDecodeNextFrame(
        bool advanceClock,
        out byte[] pixels,
        out uint width,
        out uint height,
        out bool advanced,
        out long frameSerial,
        out string hostPath)
    {
        lock (Gate)
        {
            pixels = [];
            width = 0;
            height = 0;
            advanced = false;
            frameSerial = _frameSerial;
            hostPath = _activePath ?? string.Empty;

            if (_playback is not null)
            {
                if (!_playback.TryGetFrame(advanceClock, out pixels, out advanced))
                {
                    if (_playback.IsFinished)
                    {
                        var completedPath = _activePath;
                        var progress = _playback.PlaybackProgress;
                        CloseActiveLocked();
                        Console.Error.WriteLine(
                            "[LOADER][INFO] Bink2 bridge completed: " +
                            $"{Path.GetFileName(completedPath)} after " +
                            $"{progress.Seconds:F2}s at frame {progress.FrameIndex}");
                        AttachNextQueuedMovieLocked();
                    }
                    return false;
                }

                width = _activeInfo.Width;
                height = _activeInfo.Height;
                if (advanced)
                {
                    frameSerial = ++_frameSerial;
                }
                return true;
            }

            if (_frameBuffer is null)
            {
                return false;
            }

            pixels = _frameBuffer;
            width = _activeInfo.Width;
            height = _activeInfo.Height;
            advanced = !_frameBufferPresented;
            _frameBufferPresented = true;
            if (advanced)
            {
                frameSerial = ++_frameSerial;
            }
            return true;
        }
    }

    internal static bool TryDecodeNextFrame(
        out byte[] pixels,
        out uint width,
        out uint height) =>
        TryDecodeNextFrame(
            advanceClock: true,
            out pixels,
            out width,
            out height,
            out _,
            out _,
            out _);

    private static bool IsValid(Bink2MovieInfo info) =>
        info.Width > 0 && info.Height > 0 &&
        info.Width <= MaxDimension && info.Height <= MaxDimension &&
        (ulong)info.Width * info.Height * 4 <= int.MaxValue;

    private static int GetFrameBufferLength(Bink2MovieInfo info) =>
        checked((int)((ulong)info.Width * info.Height * 4));

    private static void AttachMovieLocked(string hostPath, MovieMode mode)
    {
        switch (mode)
        {
            case MovieMode.Dummy:
                AttachDummyMovieLocked(hostPath);
                return;
            case MovieMode.Native:
                AttachNativeMovieLocked(hostPath);
                return;
        }
    }

    private static void AttachNativeMovieLocked(string hostPath)
    {
        if (!FfmpegVideoDecoder.TryOpen(
                hostPath, _presentationWidth, _presentationHeight, out var source) ||
            source is null)
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] Bink2 bridge could not open movie '" +
                Path.GetFileName(hostPath) + "'.");
            return;
        }

        var info = new Bink2MovieInfo(
            source.Width, source.Height, source.FramesPerSecondNumerator, source.FramesPerSecondDenominator);
        if (!IsValid(info))
        {
            source.Dispose();
            Console.Error.WriteLine(
                "[LOADER][WARN] Bink2 bridge rejected invalid movie dimensions for '" +
                Path.GetFileName(hostPath) + "'.");
            return;
        }

        AttachPlaybackLocked(hostPath, info, source);
        Console.Error.WriteLine(
            "[LOADER][INFO] Bink2 bridge attached: " + Path.GetFileName(hostPath) + " " +
            info.Width + "x" + info.Height + " @ " +
            info.FramesPerSecondNumerator + "/" + info.FramesPerSecondDenominator + " fps.");
    }

    private static MovieMode ResolveMode()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_BINK_MODE");
        if (string.Equals(configured, "dummy", StringComparison.OrdinalIgnoreCase))
        {
            return MovieMode.Dummy;
        }

        if (string.Equals(configured, "native", StringComparison.OrdinalIgnoreCase))
        {
            return MovieMode.Native;
        }

        if (string.Equals(configured, "skip", StringComparison.OrdinalIgnoreCase))
        {
            return MovieMode.Skip;
        }

        if (string.Equals(configured, "guest", StringComparison.OrdinalIgnoreCase))
        {
            return MovieMode.Guest;
        }

        if (string.Equals(configured, "ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            return MovieMode.Native;
        }

        // Native is the default: FfmpegVideoDecoder.TryOpen degrades gracefully
        // (falls back to the guest's own decode, logging one informational line)
        // if the FFmpeg libraries SharpEmu.CLI.csproj downloads next to the
        // executable are genuinely unavailable, so defaulting to it is safe.
        return MovieMode.Native;
    }

    private static BinkMovieMode ResolveRangeMode() => ResolveMode() switch
    {
        MovieMode.Skip => BinkMovieMode.Skip,
        MovieMode.Dummy => BinkMovieMode.Dummy,
        MovieMode.Native => BinkMovieMode.Native,
        _ => BinkMovieMode.Guest,
    };

    private static void AttachDummyMovieLocked(string hostPath)
    {
        if (!TryReadBinkHeader(hostPath, out var header))
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] Bink dummy could not read movie header '" +
                Path.GetFileName(hostPath) + "'.");
            return;
        }

        AttachDummyMovieLocked(hostPath, 0, header, isRange: false);
    }

    private static void AttachDummyMovieLocked(
        string hostPath,
        long fileOffset,
        BinkMovieHeaderInfo header,
        bool isRange = true)
    {
        var info = ToMovieInfo(header);
        CloseActiveLocked();
        _activePath = hostPath;
        _activeOffset = fileOffset;
        _activeLength = header.ByteLength;
        _activeIsRange = isRange;
        _activeInfo = info;
        _usingDummyMovie = true;
        _frameBuffer = GC.AllocateUninitializedArray<byte>(GetFrameBufferLength(info));
        _frameBufferPresented = false;
        FillDummyFrame(_frameBuffer, info.Width, info.Height);
        Console.Error.WriteLine(
            "[LOADER][INFO] Bink dummy attached: " + Path.GetFileName(hostPath) +
            (isRange ? " offset=" + fileOffset + " length=" + header.ByteLength : string.Empty) +
            " " + info.Width + "x" + info.Height + ".");
    }

    private static bool TryReadBinkHeader(string path, out BinkMovieHeaderInfo header)
    {
        header = default;
        Span<byte> bytes = stackalloc byte[BinkHeaderSize];
        try
        {
            using var stream = File.OpenRead(path);
            stream.ReadExactly(bytes);
            return TryParseMovieRangeHeader(bytes, 0, stream.Length, out header);
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException)
        {
            return false;
        }
    }

    private static void AttachPlaybackLocked(
        string hostPath,
        Bink2MovieInfo info,
        IMediaFrameDecoder decoder)
    {
        CloseActiveLocked();
        _activePath = hostPath;
        _activeInfo = info;
        _usingDummyMovie = false;
        _playback = new MediaFramePlayback(decoder);
    }

    internal static bool TryReadBinkInfo(string path, out Bink2MovieInfo info)
    {
        info = default;
        Span<byte> header = stackalloc byte[36];
        try
        {
            using var stream = File.OpenRead(path);
            stream.ReadExactly(header);
            if (!header[..3].SequenceEqual("KB2"u8))
            {
                return false;
            }

            info = new Bink2MovieInfo(
                BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x14, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x18, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x1C, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x20, 4)));
            return info.FramesPerSecondNumerator != 0 &&
                   info.FramesPerSecondDenominator != 0;
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException)
        {
            return false;
        }
    }

    internal static bool TryParseMovieRangeHeader(
        ReadOnlySpan<byte> bytes,
        long fileOffset,
        long hostFileLength,
        out BinkMovieHeaderInfo info)
    {
        info = default;
        if (bytes.Length < BinkHeaderSize ||
            fileOffset < 0 ||
            hostFileLength < 0 ||
            !TryGetMovieFamily(bytes[..4], out var family))
        {
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0x04, 4));
        var byteLength = (long)payloadLength + 8;
        if (byteLength < BinkHeaderSize ||
            fileOffset > hostFileLength ||
            byteLength > hostFileLength - fileOffset)
        {
            return false;
        }

        var frameCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0x08, 4));
        var largestFrameSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0x0C, 4));
        var width = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0x14, 4));
        var height = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0x18, 4));
        var framesPerSecondNumerator = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0x1C, 4));
        var framesPerSecondDenominator = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(0x20, 4));
        var frameInfo = new Bink2MovieInfo(
            width,
            height,
            framesPerSecondNumerator,
            framesPerSecondDenominator);
        var minimumFrameIndexBytes = ((ulong)frameCount + 1) * sizeof(uint);

        if (frameCount == 0 ||
            largestFrameSize == 0 ||
            largestFrameSize > payloadLength ||
            (ulong)byteLength < BinkHeaderSize + minimumFrameIndexBytes ||
            framesPerSecondNumerator == 0 ||
            framesPerSecondDenominator == 0 ||
            framesPerSecondNumerator > (ulong)framesPerSecondDenominator * MaxFramesPerSecond ||
            !IsValid(frameInfo))
        {
            return false;
        }

        info = new BinkMovieHeaderInfo(
            Encoding.ASCII.GetString(bytes[..4]),
            family,
            byteLength,
            frameCount,
            largestFrameSize,
            width,
            height,
            framesPerSecondNumerator,
            framesPerSecondDenominator);
        return true;
    }

    private static bool TryGetMovieFamily(ReadOnlySpan<byte> signature, out BinkMovieFamily family)
    {
        family = default;
        if (signature.Length < 4)
        {
            return false;
        }

        var version = signature[3];
        if (signature[0] == (byte)'B' &&
            signature[1] == (byte)'I' &&
            signature[2] == (byte)'K' &&
            IsBink1Version(version))
        {
            family = BinkMovieFamily.Bink1;
            return true;
        }

        if (signature[0] == (byte)'K' &&
            signature[1] == (byte)'B' &&
            signature[2] == (byte)'2' &&
            IsBink2Version(version))
        {
            family = BinkMovieFamily.Bink2;
            return true;
        }

        return false;
    }

    private static bool IsBink1Version(byte version) =>
        version is (byte)'f' or (byte)'g' or (byte)'h' or (byte)'i' or (byte)'k';

    private static bool IsBink2Version(byte version) =>
        version is (byte)'f' or (byte)'g' or (byte)'h' or (byte)'i' or (byte)'j' or (byte)'k' or (byte)'m';

    private static Bink2MovieInfo ToMovieInfo(BinkMovieHeaderInfo header) =>
        new(
            header.Width,
            header.Height,
            header.FramesPerSecondNumerator,
            header.FramesPerSecondDenominator);

    private static void FillDummyFrame(byte[] pixels, uint width, uint height)
    {
        for (var y = 0u; y < height; y++)
        {
            for (var x = 0u; x < width; x++)
            {
                var offset = checked((int)(((ulong)y * width + x) * 4));
                var band = ((x / 96) + (y / 96)) & 1;
                pixels[offset] = band == 0 ? (byte)0x28 : (byte)0x18;
                pixels[offset + 1] = band == 0 ? (byte)0x18 : (byte)0x28;
                pixels[offset + 2] = 0x10;
                pixels[offset + 3] = 0xFF;
            }
        }
    }

    private static bool IsActiveRangeLocked(string hostPath, long fileOffset, long byteLength) =>
        _activeIsRange &&
        _activeOffset == fileOffset &&
        _activeLength == byteLength &&
        string.Equals(_activePath, hostPath, StringComparison.OrdinalIgnoreCase);

    private static BinkMovieRangeAttachment TryAttachNativeMovieRangeLocked(
        string hostPath,
        long fileOffset,
        BinkMovieHeaderInfo header)
    {
        var adapter = GetAdapterLocked();
        if (adapter is null)
        {
            return BinkMovieRangeAttachment.None;
        }

        if (!adapter.SupportsRangeOpen)
        {
            if (!_rangeAdapterWarningReported)
            {
                _rangeAdapterWarningReported = true;
                Console.Error.WriteLine(
                    "[LOADER][INFO] Bink2 bridge has no range entry point; embedded movies remain guest-decoded.");
            }

            return BinkMovieRangeAttachment.None;
        }

        CloseActiveLocked();
        if (!adapter.TryOpenRange(hostPath, fileOffset, header.ByteLength, out var movie, out var info))
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] Bink2 bridge could not open embedded movie '" +
                Path.GetFileName(hostPath) + "' offset=" + fileOffset +
                " length=" + header.ByteLength + ".");
            return BinkMovieRangeAttachment.None;
        }

        var expected = ToMovieInfo(header);
        if (!IsValid(info) ||
            info.Width != expected.Width ||
            info.Height != expected.Height ||
            info.FramesPerSecondNumerator != expected.FramesPerSecondNumerator ||
            info.FramesPerSecondDenominator != expected.FramesPerSecondDenominator)
        {
            adapter.Close(movie);
            Console.Error.WriteLine(
                "[LOADER][WARN] Bink2 bridge rejected mismatched embedded movie metadata for '" +
                Path.GetFileName(hostPath) + "'.");
            return BinkMovieRangeAttachment.None;
        }

        _activePath = hostPath;
        _activeOffset = fileOffset;
        _activeLength = header.ByteLength;
        _activeIsRange = true;
        _activeInfo = info;
        _usingDummyMovie = false;
        _playback = new MediaFramePlayback(new NativeFrameDecoder(adapter, movie, info));
        Console.Error.WriteLine(
            "[LOADER][INFO] Bink2 bridge attached embedded movie: " +
            Path.GetFileName(hostPath) + " offset=" + fileOffset +
            " length=" + header.ByteLength + " " +
            info.Width + "x" + info.Height + ".");
        return BinkMovieRangeAttachment.Native;
    }

    private static void RecordMovieRangeLocked(BinkMovieRangeResult result)
    {
        _lastRangeResult = result;
        var pathKey = result.HostPath + "\0" + result.FileOffset + "\0" + result.Header.ByteLength;
        if (!ObservedMovieRanges.Add(pathKey))
        {
            return;
        }

        Console.Error.WriteLine(
            "[LOADER][INFO] bink.range" +
            " mode=" + result.Mode.ToString().ToLowerInvariant() +
            " attachment=" + result.Attachment.ToString().ToLowerInvariant() +
            " family=" + result.Header.Family.ToString().ToLowerInvariant() +
            " format=" + result.Header.Signature +
            " fd=" + result.FileDescriptor +
            " offset=" + result.FileOffset +
            " length=" + result.Header.ByteLength +
            " requested=" + result.RequestedLength +
            " read=" + result.ReadLength +
            " guest=0x" + result.GuestDestination.ToString("X16") +
            " rip=0x" + result.GuestRip.ToString("X16") +
            " return_rip=0x" + result.GuestReturnRip.ToString("X16") +
            " caller_return_rip=0x" + result.GuestCallerReturnRip.ToString("X16") +
            " thread=" + result.ManagedThreadId +
            " thread_name='" + (result.ManagedThreadName ?? string.Empty).Replace("'", "''") + "'" +
            " frames=" + result.Header.FrameCount +
            " largest_frame=" + result.Header.LargestFrameSize +
            " width=" + result.Header.Width +
            " height=" + result.Header.Height +
            " fps=" + result.Header.FramesPerSecondNumerator + "/" + result.Header.FramesPerSecondDenominator +
            " path='" + result.HostPath.Replace("'", "''") + "'.");
    }

    /// <summary>
    /// Most recently validated embedded movie range. This is metadata only; it
    /// never owns or persists the range bytes.
    /// </summary>
    internal static BinkMovieRangeResult? LastObservedMovieRange
    {
        get
        {
            lock (Gate)
            {
                return _lastRangeResult;
            }
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            CloseActiveLocked();
            ObservedMovieRanges.Clear();
            _lastRangeResult = null;
            _rangeAdapterWarningReported = false;
        }
    }

    private static NativeAdapter? GetAdapterLocked()
    {
        if (_loadAttempted)
        {
            return _adapter;
        }

        _loadAttempted = true;

        // Assembly-relative resolution participates in the single-file
        // bundle's native-library extraction, so it finds the bridge whether
        // it was embedded in the publish or sits as a loose file next to the
        // executable, without us needing to know which. Skipped when the env
        // override is set so that override still takes priority below.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SHARPEMU_BINK2_BRIDGE")) &&
            NativeLibrary.TryLoad(
                "sharpemu_bink2_bridge", typeof(HostMovieBridge).Assembly, null, out var bundledLibrary))
        {
            if (NativeAdapter.TryCreate(bundledLibrary, out var bundledAdapter))
            {
                _adapter = bundledAdapter;
                Console.Error.WriteLine("[LOADER][INFO] Bink2 bridge loaded (bundled).");
                return bundledAdapter;
            }

            NativeLibrary.Free(bundledLibrary);
        }

        foreach (var candidate in EnumerateAdapterCandidates())
        {
            if (!NativeLibrary.TryLoad(candidate, out var library))
            {
                continue;
            }

            if (NativeAdapter.TryCreate(library, out var adapter))
            {
                _adapter = adapter;
                Console.Error.WriteLine("[LOADER][INFO] Bink2 bridge loaded: " + candidate);
                return adapter;
            }

            NativeLibrary.Free(library);
        }

        if (!_availabilityReported)
        {
            _availabilityReported = true;
            Console.Error.WriteLine(
                "[LOADER][INFO] Bink2 bridge unavailable; install the licensed adapter and set SHARPEMU_BINK2_BRIDGE.");
        }

        return null;
    }

    private static IEnumerable<string> EnumerateAdapterCandidates()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_BINK2_BRIDGE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        var baseDirectory = AppContext.BaseDirectory;
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(baseDirectory, "libsharpemu_bink2_bridge.dylib");
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(baseDirectory, "sharpemu_bink2_bridge.dll");
        }
        else
        {
            yield return Path.Combine(baseDirectory, "libsharpemu_bink2_bridge.so");
        }
    }

    private static void CloseActiveLocked()
    {
        _playback?.Dispose();
        _playback = null;
        _activePath = null;
        _activeOffset = 0;
        _activeLength = 0;
        _activeIsRange = false;
        _usingDummyMovie = false;
        _activeInfo = default;
        _frameBuffer = null;
        _frameBufferPresented = false;

        // Wake any guest _read() blocked in WaitForHostPlaybackToFinish: its
        // movie either just finished or is being pre-empted by a new attach.
        Monitor.PulseAll(Gate);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Bink2MovieInfo
    {
        public readonly uint Width;
        public readonly uint Height;
        public readonly uint FramesPerSecondNumerator;
        public readonly uint FramesPerSecondDenominator;

        internal Bink2MovieInfo(
            uint width,
            uint height,
            uint framesPerSecondNumerator,
            uint framesPerSecondDenominator)
        {
            Width = width;
            Height = height;
            FramesPerSecondNumerator = framesPerSecondNumerator;
            FramesPerSecondDenominator = framesPerSecondDenominator;
        }
    }

    private enum MovieMode
    {
        Guest,
        Skip,
        Dummy,
        Native,
    }

    private sealed class NativeFrameDecoder : IMediaFrameDecoder
    {
        private readonly NativeAdapter _adapter;
        private readonly IntPtr _movie;
        private int _disposed;

        internal NativeFrameDecoder(NativeAdapter adapter, IntPtr movie, Bink2MovieInfo info)
        {
            _adapter = adapter;
            _movie = movie;
            Width = info.Width;
            Height = info.Height;
            FramesPerSecondNumerator = info.FramesPerSecondNumerator;
            FramesPerSecondDenominator = info.FramesPerSecondDenominator;
        }

        public uint Width { get; }

        public uint Height { get; }

        public uint FramesPerSecondNumerator { get; }

        public uint FramesPerSecondDenominator { get; }

        public unsafe bool TryDecodeNextFrame(Span<byte> destination)
        {
            fixed (byte* pointer = destination)
            {
                return _adapter.DecodeNextBgra(
                    _movie,
                    (IntPtr)pointer,
                    Width * 4,
                    checked((uint)destination.Length));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _adapter.Close(_movie);
            }
        }
    }

    private sealed class NativeAdapter
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int OpenUtf8Delegate(IntPtr pathUtf8, out IntPtr movie, out Bink2MovieInfo info);

        // Optional adapter ABI:
        // sharpemu_bink2_open_range_utf8(path, offset, length, movie, info).
        // The adapter reads directly from the bounded host-file range; SharpEmu
        // does not materialize a temporary standalone movie.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int OpenRangeUtf8Delegate(
            IntPtr pathUtf8,
            ulong fileOffset,
            ulong byteLength,
            out IntPtr movie,
            out Bink2MovieInfo info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int OpenScaledUtf8Delegate(
            IntPtr pathUtf8,
            uint maximumWidth,
            uint maximumHeight,
            out IntPtr movie,
            out Bink2MovieInfo info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DecodeNextBgraDelegate(IntPtr movie, IntPtr destination, uint stride, uint destinationBytes);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CloseDelegate(IntPtr movie);

        private readonly OpenUtf8Delegate _openUtf8;
        private readonly OpenRangeUtf8Delegate? _openRangeUtf8;
        private readonly OpenScaledUtf8Delegate? _openScaledUtf8;
        private readonly DecodeNextBgraDelegate _decodeNextBgra;
        private readonly CloseDelegate _close;

        private NativeAdapter(
            OpenUtf8Delegate openUtf8,
            OpenRangeUtf8Delegate? openRangeUtf8,
            OpenScaledUtf8Delegate? openScaledUtf8,
            DecodeNextBgraDelegate decodeNextBgra,
            CloseDelegate close)
        {
            _openUtf8 = openUtf8;
            _openRangeUtf8 = openRangeUtf8;
            _openScaledUtf8 = openScaledUtf8;
            _decodeNextBgra = decodeNextBgra;
            _close = close;
        }

        internal bool SupportsRangeOpen => _openRangeUtf8 is not null;

        internal static bool TryCreate(IntPtr library, out NativeAdapter? adapter)
        {
            adapter = null;
            if (!NativeLibrary.TryGetExport(library, "sharpemu_bink2_open_utf8", out var open) ||
                !NativeLibrary.TryGetExport(library, "sharpemu_bink2_decode_next_bgra", out var decode) ||
                !NativeLibrary.TryGetExport(library, "sharpemu_bink2_close", out var close))
            {
                return false;
            }

            OpenRangeUtf8Delegate? openRangeUtf8 = null;
            if (NativeLibrary.TryGetExport(library, "sharpemu_bink2_open_range_utf8", out var openRange))
            {
                openRangeUtf8 = Marshal.GetDelegateForFunctionPointer<OpenRangeUtf8Delegate>(openRange);
            }

            OpenScaledUtf8Delegate? openScaled = null;
            if (NativeLibrary.TryGetExport(
                    library,
                    "sharpemu_bink2_open_scaled_utf8",
                    out var scaledOpen))
            {
                openScaled = Marshal.GetDelegateForFunctionPointer<OpenScaledUtf8Delegate>(scaledOpen);
            }

            adapter = new NativeAdapter(
                Marshal.GetDelegateForFunctionPointer<OpenUtf8Delegate>(open),
                openRangeUtf8,
                openScaled,
                Marshal.GetDelegateForFunctionPointer<DecodeNextBgraDelegate>(decode),
                Marshal.GetDelegateForFunctionPointer<CloseDelegate>(close));
            return true;
        }

        internal bool TryOpen(
            string path,
            uint maximumWidth,
            uint maximumHeight,
            out IntPtr movie,
            out Bink2MovieInfo info)
        {
            var utf8 = Marshal.StringToCoTaskMemUTF8(path);
            try
            {
                var result = _openScaledUtf8 is not null
                    ? _openScaledUtf8(
                        utf8,
                        maximumWidth,
                        maximumHeight,
                        out movie,
                        out info)
                    : _openUtf8(utf8, out movie, out info);
                return result != 0 && movie != IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8);
            }
        }

        internal bool TryOpenRange(
            string path,
            long fileOffset,
            long byteLength,
            out IntPtr movie,
            out Bink2MovieInfo info)
        {
            movie = IntPtr.Zero;
            info = default;
            if (_openRangeUtf8 is null || fileOffset < 0 || byteLength <= 0)
            {
                return false;
            }

            var utf8 = Marshal.StringToCoTaskMemUTF8(path);
            try
            {
                return _openRangeUtf8(
                    utf8,
                    unchecked((ulong)fileOffset),
                    unchecked((ulong)byteLength),
                    out movie,
                    out info) != 0 && movie != IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8);
            }
        }

        internal bool DecodeNextBgra(IntPtr movie, IntPtr destination, uint stride, uint destinationBytes) =>
            _decodeNextBgra(movie, destination, stride, destinationBytes) != 0;

        internal void Close(IntPtr movie) => _close(movie);
    }

    private static readonly Queue<string> PendingMoviePaths = new();
    private static readonly HashSet<string> PendingMoviePathSet =
        new(StringComparer.OrdinalIgnoreCase);
    private static void AttachNextQueuedMovieLocked()
    {
        while (PendingMoviePaths.Count > 0)
        {
            var path = PendingMoviePaths.Dequeue();
            PendingMoviePathSet.Remove(path);
            if (!File.Exists(path))
            {
                continue;
            }

            AttachMovieLocked(path, ResolveMode());
            if (_playback is not null || _frameBuffer is not null)
            {
                return;
            }
        }
    }
    // Longest a guest _read() will block waiting for real host playback to
    // finish. A safety net, not a target: real movies finish well under
    // this. Bounds the damage if a movie fails to attach/decode after being
    // queued, so the guest thread doesn't hang forever.
    private const long MaxCompletionWaitMilliseconds = 5 * 60 * 1000;
    /// <summary>
    /// Blocks the calling (guest I/O) thread until the host has actually
    /// finished presenting <paramref name="hostPath"/> — either because it
    /// played through, or because something else took over the timeline.
    ///
    /// The completion shim tells the guest's own Bink header parse "this
    /// movie is one frame and already done" so its native decoder never
    /// blocks the guest on real per-frame work. Without this wait, that lie
    /// lands the instant the guest reads the header, so guest-side game
    /// logic races far ahead of whatever the host is still showing on
    /// screen: pressing a button lands on the (already-advanced) guest
    /// state, but the video visibly keeps playing, and any real-time-gated
    /// trigger later in the guest's own flow can fire against a clock that
    /// no longer matches wall time. Gating the "done" read on real host
    /// completion keeps guest pacing and on-screen playback in lockstep.
    /// </summary>
    internal static void WaitForHostPlaybackToFinish(string hostPath)
    {
        var deadline = Environment.TickCount64 + MaxCompletionWaitMilliseconds;
        lock (Gate)
        {
            while (IsTrackedLocked(hostPath))
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    Console.Error.WriteLine(
                        "[LOADER][WARN] Bink2 bridge completion wait timed out for '" +
                        Path.GetFileName(hostPath) + "'.");
                    return;
                }

                Monitor.Wait(Gate, (int)Math.Min(remaining, 200));
            }
        }
    }

    private static bool IsTrackedLocked(string hostPath) =>
        string.Equals(_activePath, hostPath, StringComparison.OrdinalIgnoreCase) ||
        PendingMoviePathSet.Contains(hostPath);

    internal static bool TryTakeOverGuestMovie(
        string hostPath,
        out BinkGuestCompletionShim completionShim,
        out bool observed)
    {
        completionShim = default;
        observed = ObserveGuestMovie(hostPath);

        // Keep the real header visible so the guest creates its movie surface
        // and draw. Host-decoded pixels replace that sampled image later; a
        // one-frame completion shim would finish before the descriptor exists.
        return false;
    }

    internal static void NotifyGuestMovieClosed(string hostPath)
    {
        lock (Gate)
        {
            if (PendingMoviePathSet.Remove(hostPath))
            {
                var retained = PendingMoviePaths
                    .Where(path => !string.Equals(
                        path,
                        hostPath,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                PendingMoviePaths.Clear();
                foreach (var path in retained)
                {
                    PendingMoviePaths.Enqueue(path);
                }
            }

            if (!string.Equals(_activePath, hostPath, StringComparison.OrdinalIgnoreCase))
            {
                Monitor.PulseAll(Gate);
                return;
            }

            Console.Error.WriteLine(
                "[LOADER][INFO] Bink2 bridge stopped by guest close: " +
                Path.GetFileName(hostPath));
            CloseActiveLocked();
            AttachNextQueuedMovieLocked();
        }
    }

    internal static bool TryReadGuestCompletionShim(
        string hostPath,
        out BinkGuestCompletionShim completionShim)
    {
        completionShim = default;
        Span<byte> header = stackalloc byte[48];
        try
        {
            using var stream = File.OpenRead(hostPath);
            stream.ReadExactly(header);
            if (!header[..3].SequenceEqual("KB2"u8))
            {
                return false;
            }

            var frameCount = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
            var audioTrackCount = BinaryPrimitives.ReadUInt32LittleEndian(header[40..44]);
            if (frameCount < 2 || audioTrackCount > 256)
            {
                return false;
            }

            var revision = header[3];
            var frameIndexOffset = 44L + checked(12L * audioTrackCount);
            if (revision == (byte)'m')
            {
                frameIndexOffset += 16;
            }
            else if (revision is (byte)'i' or (byte)'j' or (byte)'k' or (byte)'n')
            {
                frameIndexOffset += 4;
            }

            Span<byte> frameOffsets = stackalloc byte[8];
            stream.Position = frameIndexOffset;
            stream.ReadExactly(frameOffsets);
            var firstFrameOffset = BinaryPrimitives.ReadUInt32LittleEndian(frameOffsets[..4]) & ~1u;
            var secondFrameOffset = BinaryPrimitives.ReadUInt32LittleEndian(frameOffsets[4..]) & ~1u;
            if (firstFrameOffset < frameIndexOffset + 8 ||
                secondFrameOffset <= firstFrameOffset ||
                secondFrameOffset > stream.Length)
            {
                return false;
            }

            completionShim = new BinkGuestCompletionShim(
                secondFrameOffset - 8,
                secondFrameOffset - firstFrameOffset);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or EndOfStreamException or OverflowException)
        {
            return false;
        }
    }

    internal readonly struct BinkGuestCompletionShim
    {
        private readonly uint _fileSizeMinusHeader;
        private readonly uint _largestFrameSize;

        internal BinkGuestCompletionShim(uint fileSizeMinusHeader, uint largestFrameSize)
        {
            _fileSizeMinusHeader = fileSizeMinusHeader;
            _largestFrameSize = largestFrameSize;
        }

        /// <summary>
        /// Rewrites the frame-count/size fields the guest's own Bink header
        /// parse reads, if this read covers them. Returns true when the
        /// NumFrames field (the field that tells the guest "this movie is
        /// done") was in range, so the caller can gate that specific read on
        /// the host's real playback actually finishing first.
        /// </summary>
        internal bool Patch(long fileOffset, Span<byte> bytes)
        {
            PatchUInt32(fileOffset, bytes, 4, _fileSizeMinusHeader);
            var touchedCompletionField = PatchUInt32(fileOffset, bytes, 8, 1);
            PatchUInt32(fileOffset, bytes, 12, _largestFrameSize);
            return touchedCompletionField;
        }

        private static bool PatchUInt32(
            long fileOffset,
            Span<byte> bytes,
            long fieldOffset,
            uint value)
        {
            var relativeOffset = fieldOffset - fileOffset;
            if (relativeOffset < 0 || relativeOffset + sizeof(uint) > bytes.Length)
            {
                return false;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.Slice((int)relativeOffset, sizeof(uint)),
                value);
            return true;
        }
    }
}

internal enum BinkMovieFamily
{
    Bink1,
    Bink2,
}

internal enum BinkMovieMode
{
    Guest,
    Skip,
    Dummy,
    Native,
}

internal enum BinkMovieRangeAttachment
{
    None,
    Dummy,
    Native,
}

internal readonly record struct BinkMovieHeaderInfo(
    string Signature,
    BinkMovieFamily Family,
    long ByteLength,
    uint FrameCount,
    uint LargestFrameSize,
    uint Width,
    uint Height,
    uint FramesPerSecondNumerator,
    uint FramesPerSecondDenominator);

internal readonly record struct BinkMovieRangeResult(
    string HostPath,
    int FileDescriptor,
    long FileOffset,
    int RequestedLength,
    int ReadLength,
    ulong GuestDestination,
    ulong GuestRip,
    ulong GuestReturnRip,
    ulong GuestCallerReturnRip,
    int ManagedThreadId,
    string? ManagedThreadName,
    BinkMovieHeaderInfo Header,
    BinkMovieMode Mode,
    BinkMovieRangeAttachment Attachment);
