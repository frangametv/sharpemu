// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace SharpEmu.Libs.Audio;

public static class AudioOut2Exports
{
    // FMOD's PS5 backend allocates this ABI structure as four 16-byte lanes.
    // Clearing 0x80 bytes here overwrote the caller's stack canary immediately
    // following the 0x40-byte parameter block.
    private const int AudioOut2ContextParamSize = 0x40;
    private const int AudioOut2ContextMemorySize = 0x4000;
    private const int AudioOut2ContextMemoryAlignment = 0x100;
    private const int SpeakerArrayHeaderSize = 0x40;
    private const int SpeakerArrayEntrySize = 0x100;
    private const int SpeakerArrayScratchBytes = 0x400;
    private const uint SpeakerArrayDefaultChannels = 8;
    private const uint SpeakerArrayMaxChannels = 32;
    private const int SpeakerArrayDivisorFieldOffset = 0x34;
    private const int SpeakerArrayResultFieldOffset = 0x3C;
    private const uint SpeakerArrayDefaultDivisor = 1;
    private const int SpeakerArrayCoefficientBytes = 0x400;
    private const int PortStateSize = 0x20;
    private const int SpeakerInfoSize = 0x20;
    private static readonly string _stackOutBufferModes =
        Environment.GetEnvironmentVariable("SHARPEMU_AUDIO_OUT2_STACK_WRITES") ?? "1";
    private static bool AllowStackOut(string which) =>
        string.Equals(_stackOutBufferModes, "1", StringComparison.Ordinal) ||
        _stackOutBufferModes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(which, StringComparer.OrdinalIgnoreCase);
    private const int PortParamSize = 0x40;
    private const int AttributeEntrySize = 0x18;
    private const uint PortAttributeIdPcm = 0;
    private const ushort PortStateOutputConnectedPrimary = 0x01;
    private const int AudioOut2ErrorInvalidArgument = unchecked((int)0x80268001);
    private const int SpeakerArrayDescriptorSize = 0x28;
    private const int SpeakerArrayFooterSize = 0x18;
    private const uint MaximumSpeakerArrayCount = 0x20;
    private static long _nextContextHandle = 1;
    private static long _nextUserHandle = 1;
    private static int _nextPortId;
    private static long _pushTraceCount;
    private static long _submitTraceCount;
    private static long _submitSkipTraceCount;
    private static long _attributePcmTraceCount;

    private static readonly ConcurrentDictionary<ulong, ContextState> Contexts = new();
    private static readonly ConcurrentDictionary<ulong, SpeakerArrayState> SpeakerArrays = new();
    private static readonly ConcurrentDictionary<ulong, PortState> Ports = new();

    private sealed class SpeakerArrayState
    {
        public SpeakerArrayState(
            ulong workspaceAddress,
            ulong workspaceSize,
            uint speakerCount,
            byte layout,
            int mode,
            uint coefficientConfiguration,
            bool coefficientFeature,
            byte[] positions)
        {
            WorkspaceAddress = workspaceAddress;
            WorkspaceSize = workspaceSize;
            SpeakerCount = speakerCount;
            Layout = layout;
            Mode = mode;
            CoefficientConfiguration = coefficientConfiguration;
            CoefficientFeature = coefficientFeature;
            Positions = positions;
        }

        public ulong WorkspaceAddress { get; }
        public ulong WorkspaceSize { get; }
        public uint SpeakerCount { get; }
        public byte Layout { get; }
        public int Mode { get; }
        public uint CoefficientConfiguration { get; }
        public bool CoefficientFeature { get; }
        public byte[] Positions { get; }
        public bool HasCoefficients => CoefficientConfiguration < 2;
    }

    private sealed class ContextState
    {
        private readonly object _paceGate = new();
        private long _nextAdvanceTimestamp;

        public ContextState(ulong handle, uint frequency, uint grainSamples, uint queueDepth, IHostAudioStream? backend)
        {
            Handle = handle;
            Frequency = frequency == 0 ? 48000 : frequency;
            GrainSamples = grainSamples == 0 ? 256 : grainSamples;
            QueueDepth = queueDepth == 0 ? 4 : queueDepth;
            Backend = backend;
        }

        public ulong Handle { get; }
        public uint Frequency { get; }
        public uint GrainSamples { get; }
        public uint QueueDepth { get; }
        public IHostAudioStream? Backend { get; }

        public void PaceAdvance()
        {
            long delay;
            lock (_paceGate)
            {
                var now = Stopwatch.GetTimestamp();
                if (_nextAdvanceTimestamp < now)
                {
                    _nextAdvanceTimestamp = now;
                }

                delay = _nextAdvanceTimestamp - now;
                _nextAdvanceTimestamp += checked(
                    (long)Math.Ceiling(Stopwatch.Frequency * (double)GrainSamples / Frequency));
            }

            if (delay > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds((double)delay / Stopwatch.Frequency));
            }
        }
    }

    private sealed class PortState
    {
        public PortState(
            ulong handle,
            ulong contextHandle,
            ushort portType,
            uint dataFormat,
            uint samplingFrequency,
            uint grainSamples)
        {
            Handle = handle;
            ContextHandle = contextHandle;
            PortType = portType;
            DataFormat = dataFormat;
            SamplingFrequency = samplingFrequency == 0 ? 48000 : samplingFrequency;
            GrainSamples = grainSamples == 0 ? 256 : grainSamples;
        }

        public ulong Handle { get; }
        public ulong ContextHandle { get; }
        /// <summary>Full Prospero port type (low byte = MAIN/BGM/…, 0x0100 = object).</summary>
        public ushort PortType { get; }
        public uint DataFormat { get; }
        public uint SamplingFrequency { get; }
        public uint GrainSamples { get; }
        public ulong PcmAddress;

        public int PcmPending;

    }

    // Two host streams: primary FMOD context (menus) and everything else
    // (Bink/intro). Mixing those into one waveOut re-crunched audio; the OS
    // mixer keeps separate devices clean.
    private static readonly object HostBackendGate = new();
    private static IHostAudioStream? PrimaryBackend;
    private static IHostAudioStream? SecondaryBackend;
    private static string PrimaryBackendName = "none";
    private static string SecondaryBackendName = "none";
    private static ulong PrimaryContextHandle;
    private static readonly object HostSubmitGate = new();

    [SysAbiExport(
        Nid = "g2tViFIohHE",
        ExportName = "sceAudioOut2Initialize",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2Initialize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Firmware 12.70 libSceAudioOut.sprx SHA-256
    // 948dfdc30b9c974c5447d9078853beb1555a2e548de6093b05a114e99445ab33:
    // G1YOKDJYX2Y at 0x4ec40 normalizes the two flags and delegates to the
    // shared speaker-array sizing routine at 0x4ef40. GTA V uses the returned
    // value directly as the size of a mandatory aligned allocation.
    [SysAbiExport(
        Nid = "G1YOKDJYX2Y",
        ExportName = "sceAudioOut2GetSpeakerArrayMemorySize",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2",
        PreferLle = true)]
    public static int AudioOut2GetSpeakerArrayMemorySize(CpuContext ctx)
    {
        var speakerCount = unchecked((uint)ctx[CpuRegister.Rdi]);
        var useObjectLayout = unchecked((uint)ctx[CpuRegister.Rsi]) != 0;
        var includeCoefficients = unchecked((uint)ctx[CpuRegister.Rdx]) != 0;
        var size = GetSpeakerArrayMemorySize(speakerCount, useObjectLayout, includeCoefficients);

        TraceAudioOut2(
            $"speaker-array-memory-size speakers={speakerCount} object-layout={useObjectLayout} " +
            $"coefficients={includeCoefficients} size=0x{size:X}");
        ctx[CpuRegister.Rax] = size;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Firmware 12.70 libSceAudioOut.sprx SHA-256
    // 948dfdc30b9c974c5447d9078853beb1555a2e548de6093b05a114e99445ab33:
    // +k91hoTuoA8 at 0x4ec60 delegates to FUN_0004efd0. The public ABI is
    // (outHandle, descriptor, auxiliary); the wrapper replaces RCX with a
    // provider-private feature byte, so callers such as GTA may leave RCX stale.
    [SysAbiExport(
        Nid = "+k91hoTuoA8",
        ExportName = "sceAudioOut2SpeakerArrayCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2",
        PreferLle = true)]
    public static int AudioOut2SpeakerArrayCreate(CpuContext ctx)
    {
        var outHandleAddress = ctx[CpuRegister.Rdi];
        var descriptorAddress = ctx[CpuRegister.Rsi];
        var auxiliaryAddress = ctx[CpuRegister.Rdx];
        if (outHandleAddress == 0 || descriptorAddress == 0)
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        Span<byte> descriptor = stackalloc byte[SpeakerArrayDescriptorSize];
        if (!ctx.Memory.TryRead(descriptorAddress, descriptor))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var positionsAddress = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[0x00..]);
        var speakerCount = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[0x08..]);
        var layout = descriptor[0x0C];
        var workspaceAddress = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[0x10..]);
        var workspaceSize = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[0x18..]);
        var mode = BinaryPrimitives.ReadInt32LittleEndian(descriptor[0x20..]);
        var modeParameter = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(descriptor[0x24..]));

        if (positionsAddress == 0 ||
            workspaceAddress == 0 ||
            workspaceSize == 0 ||
            speakerCount > MaximumSpeakerArrayCount ||
            (mode == 1 && (!float.IsFinite(modeParameter) || modeParameter < 0.0f)))
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        var coefficientConfiguration = uint.MaxValue;
        var coefficientFeature = false;
        if (auxiliaryAddress != 0)
        {
            Span<byte> auxiliaryConfiguration = stackalloc byte[sizeof(uint)];
            if (!ctx.Memory.TryRead(auxiliaryAddress, auxiliaryConfiguration))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            coefficientConfiguration = BinaryPrimitives.ReadUInt32LittleEndian(auxiliaryConfiguration);
            if (coefficientConfiguration < 2)
            {
                // Firmware 12.70's SDK gate is active, so coefficient creation
                // also consumes the feature byte at auxiliary +4.
                Span<byte> feature = stackalloc byte[1];
                if (auxiliaryAddress > ulong.MaxValue - sizeof(uint) ||
                    !ctx.Memory.TryRead(auxiliaryAddress + sizeof(uint), feature))
                {
                    return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                coefficientFeature = feature[0] != 0;
            }
        }

        var includeCoefficients = coefficientConfiguration < 2;
        var requiredSize = GetSpeakerArrayMemorySize(speakerCount, layout != 0, includeCoefficients);
        if (workspaceSize < requiredSize ||
            workspaceSize < SpeakerArrayFooterSize ||
            workspaceAddress > ulong.MaxValue - (workspaceSize - SpeakerArrayFooterSize))
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        var positionsSize = checked((int)(speakerCount * 3U * sizeof(float)));
        var positions = new byte[positionsSize];
        if (positions.Length != 0 && !ctx.Memory.TryRead(positionsAddress, positions))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var handle = workspaceAddress + workspaceSize - SpeakerArrayFooterSize;
        Span<byte> footer = stackalloc byte[SpeakerArrayFooterSize];
        footer.Clear();
        // The provider stores opaque primary/secondary implementation pointers
        // in the first two qwords. HLE owns equivalent host state instead, so it
        // leaves those pointers null while preserving the proven mode/layout ABI.
        BinaryPrimitives.WriteInt32LittleEndian(footer[0x10..], mode);
        footer[0x14] = layout;

        if (!ctx.Memory.TryWrite(handle, footer) || !TryWriteUInt64(ctx, outHandleAddress, handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        SpeakerArrays[handle] = new SpeakerArrayState(
            workspaceAddress,
            workspaceSize,
            speakerCount,
            layout,
            mode,
            coefficientConfiguration,
            coefficientFeature,
            positions);
        TraceAudioOut2(
            $"speaker-array-create handle=0x{handle:X} speakers={speakerCount} layout={layout} " +
            $"mode={mode} coefficients={includeCoefficients} workspace=0x{workspaceAddress:X}+0x{workspaceSize:X}");
        return SetReturn(ctx, 0);
    }

    // Firmware wrapper 28QqMnuuJ9Y at 0x4ee10 delegates to FUN_0004f540.
    // GTA's mode-zero speaker array requests all 36 fifth-order rows as indices
    // 64..99, with two floats per row. Exact decoder synthesis remains provider
    // work; zero-initializing every requested row is a deterministic progression
    // fallback and avoids exposing stale guest stack data as audio coefficients.
    [SysAbiExport(
        Nid = "28QqMnuuJ9Y",
        ExportName = "sceAudioOut2GetSpeakerArrayAmbisonicsCoefficients",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2",
        PreferLle = true)]
    public static int AudioOut2GetSpeakerArrayAmbisonicsCoefficients(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var coefficientIndex = unchecked((uint)ctx[CpuRegister.Rsi]);
        var outputAddress = ctx[CpuRegister.Rdx];
        var speakerCount = unchecked((uint)ctx[CpuRegister.Rcx]);
        if (handle == 0 ||
            outputAddress == 0 ||
            !SpeakerArrays.TryGetValue(handle, out var state) ||
            !state.HasCoefficients ||
            state.SpeakerCount != speakerCount ||
            !IsValidAmbisonicsCoefficientIndex(state.Mode, coefficientIndex))
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        Span<byte> coefficients = stackalloc byte[checked((int)speakerCount * sizeof(float))];
        coefficients.Clear();
        if (!ctx.Memory.TryWrite(outputAddress, coefficients))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetReturn(ctx, 0);
    }

    // Firmware wrapper erCWQR5eKiQ at 0x4ecf0 delegates to FUN_0004f3b0,
    // which rejects null and tears down both the primary and optional decoder.
    [SysAbiExport(
        Nid = "erCWQR5eKiQ",
        ExportName = "sceAudioOut2SpeakerArrayDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2",
        PreferLle = true)]
    public static int AudioOut2SpeakerArrayDestroy(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        if (handle == 0 || !SpeakerArrays.TryRemove(handle, out _))
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        TraceAudioOut2($"speaker-array-destroy handle=0x{handle:X}");
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "t5YrizufpQc",
        ExportName = "sceAudioOut2ContextResetParam",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextResetParam(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Layout matches libSceAudioOut2 SceAudioOut2ContextParam (no size prefix):
        // max_ports, max_object_ports, guarantee_object_ports, queue_depth,
        // num_grains, flags, reserved...
        Span<byte> param = stackalloc byte[AudioOut2ContextParamSize];
        param.Clear();
        // Firmware 12.70 t5YrizufpQc at 0x11050 copies the 16-byte default
        // block at 0x5e160, stores 0x100 at +0x10, and clears through +0x3f.
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x00..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x10..], 0x100);

        return ctx.Memory.TryWrite(paramAddress, param)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "pDmme7Bgm6E",
        ExportName = "sceAudioOut2ContextQueryMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextQueryMemory(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var memorySizeAddress = ctx[CpuRegister.Rsi];
        if (paramAddress == 0 || memorySizeAddress == 0)
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        Span<byte> param = stackalloc byte[AudioOut2ContextParamSize];
        if (!ctx.Memory.TryRead(paramAddress, param))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var bedCount = BinaryPrimitives.ReadUInt32LittleEndian(param[0x00..]);
        var objectCount = BinaryPrimitives.ReadUInt32LittleEndian(param[0x04..]);
        var reservedObjectCount = BinaryPrimitives.ReadUInt32LittleEndian(param[0x08..]);
        var busCount = BinaryPrimitives.ReadUInt32LittleEndian(param[0x0C..]);
        var mode = BinaryPrimitives.ReadUInt32LittleEndian(param[0x10..]);
        var objectMode = BinaryPrimitives.ReadUInt32LittleEndian(param[0x14..]);

        // Firmware 12.70 pDmme7Bgm6E at 0x2a6b0 validates this public
        // parameter block, normalizes it at 0x2a7a0, and calls +8fuZ1rh4PA
        // with RDX equal to the caller's single uint64_t output. The latter
        // clears RCX before entering the sizing routine at 0xe330, so there is
        // no alignment/secondary output. Writing a fabricated structure here
        // used to overwrite GTA V's adjacent stack canary.
        if (mode < 0x100 || (mode & 0xFF) != 0 ||
            bedCount > 0x20 || reservedObjectCount > objectCount || busCount == 0 ||
            (objectCount != 0 && reservedObjectCount != 0) ||
            (objectCount != 0 && objectMode is not (1 or 2)))
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        // The provider gates larger modes on hardware capability globals.
        // GTA uses mode 0x100; fail closed for modes that need those gates.
        if ((objectCount == 0 && mode > 0x800) ||
            (objectCount != 0 && mode > 0x400))
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        var normalizedObjectCount = Math.Min(objectCount, 0x80U);
        var normalizedBusCount = (ulong)(mode >> 8) * (busCount + 1UL) - 1UL;
        if (normalizedBusCount > 0x40)
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        const uint builtInVoiceCount = 0x15;
        var memorySize = ((ulong)bedCount + normalizedObjectCount + builtInVoiceCount) * 0xB60UL
            + GetAudioOut2DescriptorSize(bedCount)
            + GetAudioOut2DescriptorSize(normalizedObjectCount)
            + GetAudioOut2DescriptorSize(builtInVoiceCount)
            + AlignUp((ulong)normalizedObjectCount * 0x18UL, 0x80UL);

        Span<byte> sizeBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(sizeBytes, memorySize);
        TraceAudioOut2(
            $"context-query-memory beds={bedCount} objects={objectCount} buses={busCount} " +
            $"mode=0x{mode:X} object-mode={objectMode} size=0x{memorySize:X}");

        return ctx.Memory.TryWrite(memorySizeAddress, sizeBytes)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "0x6o1VVAYSY",
        ExportName = "sceAudioOut2ContextCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextCreate(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var memoryAddress = ctx[CpuRegister.Rsi];
        var memorySize = ctx[CpuRegister.Rdx];
        var outContextAddress = ctx[CpuRegister.Rcx];
        if (paramAddress == 0 || memoryAddress == 0 || memorySize == 0 || outContextAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Prospero AudioOut2 context params are port/queue config, not an AudioOut
        // open-style frequency/channel block. Sample rate is fixed at 48 kHz.
        uint frequency = 48000;
        uint grain = 256;
        uint queueDepth = 4;
        Span<byte> param = stackalloc byte[AudioOut2ContextParamSize];
        if (ctx.Memory.TryRead(paramAddress, param))
        {
            var qd = BinaryPrimitives.ReadUInt32LittleEndian(param[0x0C..]);
            var ng = BinaryPrimitives.ReadUInt32LittleEndian(param[0x10..]);
            if (qd is >= 1 and <= 32) queueDepth = qd;
            if (ng is >= 64 and <= 0x4000) grain = ng;
            TraceAudioOut2($"context-param address=0x{paramAddress:X} bytes={Convert.ToHexString(param)}");
        }

        var handle = (ulong)Interlocked.Increment(ref _nextContextHandle);
        // Backend is bound lazily on first real Push (primary vs secondary device).
        Contexts[handle] = new ContextState(handle, frequency, grain, queueDepth, backend: null);
        TraceAudioOut2(
            $"context-create handle=0x{handle:X} frequency={frequency} grain={grain} " +
            $"queue={queueDepth} memory=0x{memoryAddress:X} size=0x{memorySize:X} backend=pending");
        return TryWriteUInt64(ctx, outContextAddress, handle)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "on6ZH7Abo10",
        ExportName = "sceAudioOut2ContextDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextDestroy(CpuContext ctx)
    {
        // Shared backend lifetime is process-wide; just drop the context entry.
        Contexts.TryRemove(ctx[CpuRegister.Rdi], out _);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "DxGyV8dtOR8",
        ExportName = "sceAudioOut2ContextBedWrite",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextBedWrite(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "aII9h5nli9U",
        ExportName = "sceAudioOut2ContextPush",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextPush(CpuContext ctx)
    {
        // ABI: sceAudioOut2ContextPush(ctx, blocking). RSI is a blocking flag
        // (observed 1), not a PCM pointer. PCM is attached earlier via
        // PortSetAttributes(attribute_id=PCM) and flushed here.
        var handle = ctx[CpuRegister.Rdi];
        var blocking = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (Interlocked.Increment(ref _pushTraceCount) <= 8)
        {
            TraceAudioOut2($"context-push handle=0x{handle:X} blocking={blocking}");
        }

        if (!Contexts.TryGetValue(handle, out var context))
        {
            return SetReturn(ctx, 0);
        }

        // Host Submit already blocks on the waveOut queue; only fall back to
        // software pacing when nothing was queued (silence / non-primary ctx).
        if (!TrySubmitContextAudio(ctx, context))
        {
            context.PaceAdvance();
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "PE2zHMqLSHs",
        ExportName = "sceAudioOut2ContextAdvance",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextAdvance(CpuContext ctx)
    {
        if (Contexts.TryGetValue(ctx[CpuRegister.Rdi], out var state))
        {
            if (!TrySubmitContextAudio(ctx, state))
            {
                state.PaceAdvance();
            }
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "R7d0F1g2qsU",
        ExportName = "sceAudioOut2ContextGetQueueLevel",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextGetQueueLevel(CpuContext ctx)
    {
        // Firmware 12.70 libSceAudioOut.sprx SHA-256
        // 948dfdc30b9c974c5447d9078853beb1555a2e548de6093b05a114e99445ab33:
        // R7d0F1g2qsU at 0x2b3f0 enters the shared implementation at 0x2c260,
        // whose two optional outputs are uint32_t pointers. GTA V places the
        // first output four bytes before its stack canary, so an eight-byte
        // store here corrupts the canary.
        //
        // The advance path paces synchronously, so both queue values are zero.
        var levelAddress = ctx[CpuRegister.Rsi];
        var availableAddress = ctx[CpuRegister.Rdx];
        if (levelAddress == 0 && availableAddress == 0)
        {
            return SetReturn(ctx, AudioOut2ErrorInvalidArgument);
        }

        if ((levelAddress != 0 && !TryWriteUInt32(ctx, levelAddress, 0)) ||
            (availableAddress != 0 && !TryWriteUInt32(ctx, availableAddress, 0)))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "8XTArSPyWHk",
        ExportName = "sceAudioOut2PortSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortSetAttributes(CpuContext ctx)
    {
        // sceAudioOut2PortSetAttributes(port, attributes*, num).
        // Attribute id 0 = PCM; value points at { const void* data }.
        var portHandle = ctx[CpuRegister.Rdi];
        var attributesAddress = ctx[CpuRegister.Rsi];
        var attributeCount = unchecked((uint)ctx[CpuRegister.Rdx]);
        if (!Ports.TryGetValue(portHandle, out var port))
        {
            return SetReturn(ctx, 0);
        }

        if (attributeCount == 0 || attributesAddress == 0)
        {
            return SetReturn(ctx, 0);
        }

        if (attributeCount > 32)
        {
            attributeCount = 32;
        }

        Span<byte> entry = stackalloc byte[AttributeEntrySize];
        Span<byte> pcm = stackalloc byte[8];
        for (uint i = 0; i < attributeCount; i++)
        {
            if (!ctx.Memory.TryRead(attributesAddress + (i * AttributeEntrySize), entry))
            {
                break;
            }

            var attributeId = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var valueAddress = BinaryPrimitives.ReadUInt64LittleEndian(entry[0x08..]);
            var valueSize = BinaryPrimitives.ReadUInt64LittleEndian(entry[0x10..]);
            if (attributeId != PortAttributeIdPcm || valueAddress == 0 || valueSize < 8)
            {
                continue;
            }

            if (!ctx.Memory.TryRead(valueAddress, pcm))
            {
                continue;
            }

            port.PcmAddress = BinaryPrimitives.ReadUInt64LittleEndian(pcm);
            Volatile.Write(ref port.PcmPending, port.PcmAddress != 0 ? 1 : 0);
            var n = Interlocked.Increment(ref _attributePcmTraceCount);
            if (n <= 8 || n % 500 == 0)
            {
                TraceAudioOut2(
                    $"port-set-pcm#{n} port=0x{portHandle:X} pcm=0x{port.PcmAddress:X} " +
                    $"format=0x{port.DataFormat:X} grains={port.GrainSamples}");
            }
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "JK2wamZPzwM",
        ExportName = "sceAudioOut2PortCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortCreate(CpuContext ctx)
    {
        // sceAudioOut2PortCreate(ctx, PortParam*, outPort*).
        var contextHandle = ctx[CpuRegister.Rdi];
        var paramAddress = ctx[CpuRegister.Rsi];
        var outPortAddress = ResolveGuestOutBuffer(ctx[CpuRegister.Rdx], ctx[CpuRegister.Rcx]);
        if (outPortAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ushort portType = 0;
        uint dataFormat = 0x0000_0200; // float stereo default
        uint samplingFrequency = 48000;
        uint grainSamples = 256;
        if (Contexts.TryGetValue(contextHandle, out var context))
        {
            grainSamples = context.GrainSamples;
            samplingFrequency = context.Frequency;
        }

        if (paramAddress != 0 && IsPlausibleGuestObjectPointer(paramAddress))
        {
            Span<byte> param = stackalloc byte[PortParamSize];
            if (ctx.Memory.TryRead(paramAddress, param))
            {
                portType = BinaryPrimitives.ReadUInt16LittleEndian(param);
                dataFormat = BinaryPrimitives.ReadUInt32LittleEndian(param[0x04..]);
                var freq = BinaryPrimitives.ReadUInt32LittleEndian(param[0x08..]);
                if (freq is >= 8000 and <= 192000)
                {
                    samplingFrequency = freq;
                }
            }
        }

        var portId = (uint)Interlocked.Increment(ref _nextPortId);
        // Handle encodes only the low type byte; PortState keeps the full type
        // so object ports (0x01xx) can still be filtered at submit time.
        var handle = 0x2000_0000UL | ((ulong)(portType & 0xFF) << 16) | portId;
        var portState = new PortState(
            handle,
            contextHandle,
            portType,
            dataFormat,
            samplingFrequency,
            grainSamples);
        Ports[handle] = portState;
        if (!TryWriteUInt64(ctx, outPortAddress, handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAudioOut2(
            $"port-create handle=0x{handle:X} ctx=0x{contextHandle:X} type=0x{portType:X} " +
            $"format=0x{dataFormat:X} freq={samplingFrequency} out=0x{outPortAddress:X}");
        return SetReturn(ctx, 0);
    }

    // Fixed-size connected stereo state. Do not trust r8/r9 for byte counts.
    [SysAbiExport(
        Nid = "gatEUKG+Ea4",
        ExportName = "sceAudioOut2PortGetState",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortGetState(CpuContext ctx)
    {
        var portHandle = ctx[CpuRegister.Rdi];
        var stateAddress = ResolveGuestOutBuffer(ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]);
        if (stateAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Stack out-buffers with garbage handles were writing 0x20 bytes over
        // caller frames / canaries (state=0x7FFFDE1FF688 right before fail).
        // Heap outs still get a real state blob even when the handle wasn't
        // minted by PortCreate — this title synthesizes port ids itself.
        if (IsGuestStackAddress(stateAddress) &&
            !(AllowStackOut("portstate") && Ports.ContainsKey(portHandle)))
        {
            TraceAudioOut2(
                $"port-get-state skip-stack handle=0x{portHandle:X} state=0x{stateAddress:X}");
            return SetReturn(ctx, 0);
        }

        Span<byte> state = stackalloc byte[PortStateSize];
        state.Clear();
        //   +0x00 u16 output   = CONNECTED_PRIMARY (1)
        //   +0x02 u8  channels = from port format when known, else 2
        //   +0x04 s16 volume   = -1 (N/A for main)
        byte channels = 2;
        if (Ports.TryGetValue(portHandle, out var port) &&
            TryDecodeDataFormat(port.DataFormat, out var decodedChannels, out _, out _))
        {
            channels = (byte)Math.Clamp(decodedChannels, 1, 16);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(state[0x00..], PortStateOutputConnectedPrimary);
        state[0x02] = channels;
        BinaryPrimitives.WriteInt16LittleEndian(state[0x04..], -1);

        if (!ctx.Memory.TryWrite(stateAddress, state))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAudioOut2(
            $"port-get-state handle=0x{portHandle:X} state=0x{stateAddress:X} bytes=0x{PortStateSize:X}");
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "4dq2rblWlg0",
        ExportName = "sceAudioOut2ContextSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextSetAttributes(CpuContext ctx)
    {
        var attributeAddress = ctx[CpuRegister.Rsi];
        var count = unchecked((uint)ctx[CpuRegister.Rdx]);
        return SetReturn(
            ctx,
            count != 0 && attributeAddress == 0
                ? (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT
                : 0);
    }

    [SysAbiExport(
        Nid = "bkBN+CMLwRc",
        ExportName = "sceAudioOut2GetSystemState",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2GetSystemState(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rdi];
        if (stateAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (IsGuestStackAddress(stateAddress) && !AllowStackOut("systemstate"))
        {
            TraceAudioOut2($"get-system-state skip-stack out=0x{stateAddress:X}");
            return SetReturn(ctx, 0);
        }

        Span<byte> state = stackalloc byte[0x40];
        state.Clear();
        return ctx.Memory.TryWrite(stateAddress, state)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    // rdi=out buffer, rsi=type/flag (not a pointer). Fixed-size write only.
    [SysAbiExport(
        Nid = "DImz2Ft9E2g",
        ExportName = "sceAudioOut2GetSpeakerInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2GetSpeakerInfo(CpuContext ctx)
    {
        var infoAddress = ResolveGuestOutBuffer(ctx[CpuRegister.Rdi], ctx[CpuRegister.Rdx]);
        if (infoAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (IsGuestStackAddress(infoAddress) && !AllowStackOut("speaker"))
        {
            TraceAudioOut2($"get-speaker-info skip-stack out=0x{infoAddress:X}");
            return SetReturn(ctx, 0);
        }

        Span<byte> info = stackalloc byte[SpeakerInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x00..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x04..], 48000);
        BinaryPrimitives.WriteUInt16LittleEndian(info[0x08..], PortStateOutputConnectedPrimary);

        if (!ctx.Memory.TryWrite(infoAddress, info))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAudioOut2(
            $"get-speaker-info out=0x{infoAddress:X} type=0x{ctx[CpuRegister.Rsi]:X} bytes=0x{SpeakerInfoSize:X}");
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "4BlZurolOAo",
        ExportName = "sceAudioOut2GetSpeakerArrayCoefficients",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2GetSpeakerArrayCoefficients(CpuContext ctx) =>
        WriteZeroSpeakerArrayCoefficients(ctx, "coefficients");

    [SysAbiExport(
        Nid = "cd+Rtw+D1x8",
        ExportName = "sceAudioOut2PortDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortDestroy(CpuContext ctx)
    {
        Ports.TryRemove(ctx[CpuRegister.Rdi], out _);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "IaZXJ9M79uo",
        ExportName = "sceAudioOut2UserDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2UserDestroy(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "xywYcRB7nbQ",
        ExportName = "sceAudioOut2UserCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2UserCreate(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var outUserAddress = ctx[CpuRegister.Rsi];
        if ((userId != 0 && userId != 1 && userId != 1000 && userId != 0x10000000 && userId != 255) ||
            outUserAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = (ulong)Interlocked.Increment(ref _nextUserHandle);
        return TryWriteUInt64(ctx, outUserAddress, handle)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static IHostAudioStream? ResolveContextBackend(ContextState context, out string backendName)
    {
        lock (HostBackendGate)
        {
            if (PrimaryContextHandle == 0)
            {
                PrimaryContextHandle = context.Handle;
            }

            if (context.Handle == PrimaryContextHandle)
            {
                if (PrimaryBackend is null)
                {
                    try
                    {
                        var audio = HostPlatform.Current.Audio;
                        // Deeper host queue than classic AudioOut: FMOD's bursty
                        // AudioOut2 Push pattern underran a 32 KiB (~171 ms) bed.
                        PrimaryBackend = audio.OpenStereoPcm16Stream(
                            context.Frequency,
                            maxQueuedPcmBytes: 128 * 1024);
                        PrimaryBackendName = audio.BackendName + "-primary";
                    }
                    catch (Exception exception)
                    {
                        PrimaryBackendName = "silent";
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] AudioOut2 primary backend unavailable: {exception.Message}");
                    }
                }

                backendName = PrimaryBackendName;
                return PrimaryBackend;
            }

            if (SecondaryBackend is null)
            {
                try
                {
                    var audio = HostPlatform.Current.Audio;
                    SecondaryBackend = audio.OpenStereoPcm16Stream(
                        context.Frequency,
                        maxQueuedPcmBytes: 128 * 1024);
                    SecondaryBackendName = audio.BackendName + "-secondary";
                }
                catch (Exception exception)
                {
                    SecondaryBackendName = "silent";
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] AudioOut2 secondary backend unavailable: {exception.Message}");
                }
            }

            backendName = SecondaryBackendName;
            return SecondaryBackend;
        }
    }

    private static bool TrySubmitContextAudio(CpuContext ctx, ContextState context)
    {
        var frames = checked((int)context.GrainSamples);
        if (frames <= 0)
        {
            return false;
        }

        lock (HostSubmitGate)
        {
            var mix = ArrayPool<float>.Shared.Rent(frames * 2);
            var source = ArrayPool<byte>.Shared.Rent(frames * 16 * sizeof(float));
            var output = ArrayPool<byte>.Shared.Rent(frames * AudioPcmConversion.OutputFrameSize);
            try
            {
                mix.AsSpan(0, frames * 2).Clear();
                var mixedPorts = 0;
                foreach (var port in Ports.Values)
                {
                    if (port.ContextHandle != context.Handle ||
                        port.PcmAddress == 0 ||
                        Interlocked.Exchange(ref port.PcmPending, 0) == 0 ||
                        !TryDecodeDataFormat(port.DataFormat, out var ch, out var bps, out var isFloat))
                    {
                        continue;
                    }

                    var byteLength = checked(frames * ch * bps);
                    if (byteLength <= 0 || byteLength > source.Length)
                    {
                        continue;
                    }

                    var sourceSpan = source.AsSpan(0, byteLength);
                    if (!ctx.Memory.TryRead(port.PcmAddress, sourceSpan))
                    {
                        continue;
                    }

                    MixPortIntoStereo(
                        sourceSpan,
                        mix.AsSpan(0, frames * 2),
                        frames,
                        ch,
                        bps,
                        isFloat,
                        additive: mixedPorts > 0);
                    mixedPorts++;
                }

                if (mixedPorts == 0)
                {
                    TraceSubmitSkipped(context, frames, "no-ports");
                    return false;
                }

                var outputSpan = output.AsSpan(0, frames * AudioPcmConversion.OutputFrameSize);
                var peak = 0f;
                for (var frame = 0; frame < frames; frame++)
                {
                    var left = Math.Clamp(mix[frame * 2], -1f, 1f);
                    var right = Math.Clamp(mix[(frame * 2) + 1], -1f, 1f);
                    peak = Math.Max(peak, Math.Max(Math.Abs(left), Math.Abs(right)));
                    BinaryPrimitives.WriteInt16LittleEndian(
                        outputSpan[(frame * AudioPcmConversion.OutputFrameSize)..],
                        FloatToPcm16(left));
                    BinaryPrimitives.WriteInt16LittleEndian(
                        outputSpan[((frame * AudioPcmConversion.OutputFrameSize) + 2)..],
                        FloatToPcm16(right));
                }

                var backend = ResolveContextBackend(context, out var backendName);
                if (backend is null)
                {
                    TraceSubmitSkipped(context, frames, "no-backend");
                    return false;
                }

                var n = Interlocked.Increment(ref _submitTraceCount);
                if (n <= 8 || n % 500 == 0)
                {
                    TraceAudioOut2(
                        $"context-submit#{n} handle=0x{context.Handle:X} frames={frames} " +
                        $"ports={mixedPorts} peak={peak:F4} backend={backendName}");
                }

                return backend.Submit(outputSpan);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(mix);
                ArrayPool<byte>.Shared.Return(source);
                ArrayPool<byte>.Shared.Return(output);
            }
        }
    }

    private static bool IsMainOrBgmPort(ushort portType)
    {
        var kind = portType & 0xFF;
        return kind is 0 or 1;
    }

    private static void MixPortIntoStereo(
        ReadOnlySpan<byte> source,
        Span<float> mix,
        int frames,
        int channels,
        int bytesPerSample,
        bool isFloat,
        bool additive)
    {
        var frameSize = channels * bytesPerSample;
        for (var frame = 0; frame < frames; frame++)
        {
            var frameBytes = source.Slice(frame * frameSize, frameSize);
            float left;
            float right;
            if (channels >= 8)
            {
                var fl = ReadNormalizedSample(frameBytes, 0, bytesPerSample, isFloat);
                var fr = ReadNormalizedSample(frameBytes, 1, bytesPerSample, isFloat);
                var c = ReadNormalizedSample(frameBytes, 2, bytesPerSample, isFloat);
                var bl = ReadNormalizedSample(frameBytes, 4, bytesPerSample, isFloat);
                var br = ReadNormalizedSample(frameBytes, 5, bytesPerSample, isFloat);
                var sl = ReadNormalizedSample(frameBytes, 6, bytesPerSample, isFloat);
                var sr = ReadNormalizedSample(frameBytes, 7, bytesPerSample, isFloat);
                const float side = 0.70710678f;
                left = fl + (c * side) + (bl * side) + (sl * side);
                right = fr + (c * side) + (br * side) + (sr * side);
            }
            else
            {
                left = ReadNormalizedSample(frameBytes, 0, bytesPerSample, isFloat);
                right = channels == 1
                    ? left
                    : ReadNormalizedSample(frameBytes, 1, bytesPerSample, isFloat);
            }

            if (additive)
            {
                mix[frame * 2] += left;
                mix[(frame * 2) + 1] += right;
            }
            else
            {
                mix[frame * 2] = left;
                mix[(frame * 2) + 1] = right;
            }
        }
    }

    private static float ReadNormalizedSample(
        ReadOnlySpan<byte> frame,
        int channel,
        int bytesPerSample,
        bool isFloat)
    {
        var sample = frame.Slice(channel * bytesPerSample, bytesPerSample);
        if (isFloat)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(sample);
            var value = BitConverter.Int32BitsToSingle(bits);
            return float.IsFinite(value) ? value : 0f;
        }

        return BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f;
    }

    private static short FloatToPcm16(float value)
    {
        var scale = value < 0f ? 32768f : short.MaxValue;
        return (short)Math.Clamp(MathF.Round(value * scale), short.MinValue, short.MaxValue);
    }

    private static bool IsObjectPort(ushort portType) => (portType & 0xFF00) == 0x0100;

    private static bool TryDecodeDataFormat(
        uint dataFormat,
        out int channels,
        out int bytesPerSample,
        out bool isFloat)
    {
        channels = (int)((dataFormat >> 8) & 0xFF);
        if (channels == 0)
        {
            channels = 2;
        }

        if (channels is < 1 or > 16)
        {
            bytesPerSample = 0;
            isFloat = false;
            return false;
        }

        var dataType = dataFormat & 0x7Fu;
        isFloat = dataType == 0;
        bytesPerSample = isFloat ? 4 : dataType == 1 ? 2 : 0;
        return bytesPerSample != 0;
    }

    private static int ComputeSpeakerArrayBytes(uint channels) =>
        SpeakerArrayHeaderSize + (int)(channels * SpeakerArrayEntrySize) + SpeakerArrayScratchBytes;

    private static bool InitializeSpeakerArrayObject(CpuContext ctx, ulong memory, uint channels)
    {
        // Header only — never wipe the full GetSize slab (and never touch stack).
        Span<byte> body = stackalloc byte[SpeakerArrayHeaderSize];
        body.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(body[0x00..], (uint)SpeakerArrayHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(body[0x04..], channels);
        BinaryPrimitives.WriteUInt32LittleEndian(body[SpeakerArrayDivisorFieldOffset..], SpeakerArrayDefaultDivisor);
        BinaryPrimitives.WriteUInt32LittleEndian(body[SpeakerArrayResultFieldOffset..], 0);
        return ctx.Memory.TryWrite(memory, body);
    }

    // Prefer the high guest arena (0x6000_xxxx). TryAllocateHleData advances
    // _nextVirtualAddress into the title's direct-memory window (~0x1559_xxxx);
    // publishing an object there made sceKernelBatchMap(fixed, 0x1559C80000,
    // 0x20000) return NOT_FOUND and abort RenderThread with int 0x41.
    // Never mint the old 0x1559C0xxxx "cookie" pointers — they are unmapped and
    // collide with dmem VAs.
    private static bool TryAllocateSpeakerArrayMemory(CpuContext ctx, ulong bytes, out ulong memory)
    {
        memory = 0;
        var length = Math.Max(bytes, 0x1000UL);

        if (TryAllocateViaGuestAllocator(ctx, length, 0x1000, out memory) &&
            IsSafeSpeakerArrayAddress(memory))
        {
            return true;
        }

        if (Kernel.KernelMemoryCompatExports.TryAllocateHleData(ctx, length, 0x1000, out memory) &&
            IsSafeSpeakerArrayAddress(memory))
        {
            return true;
        }

        memory = 0;
        return false;
    }

    private static bool TryAllocateViaGuestAllocator(CpuContext ctx, ulong length, ulong alignment, out ulong memory)
    {
        memory = 0;
        var allocator = ctx.Memory as IGuestMemoryAllocator;
        if (allocator is null && ctx.Memory is ICpuMemoryWrapper { Inner: IGuestMemoryAllocator inner })
        {
            allocator = inner;
        }

        return allocator is not null && allocator.TryAllocateGuestMemory(length, alignment, out memory);
    }

    private static bool IsSafeSpeakerArrayAddress(ulong value) =>
        IsPlausibleGuestObjectPointer(value) &&
        !IsGuestStackAddress(value) &&
        !IsDirectMemoryWindowAddress(value);

    // GTA V Enhanced BatchMap fixed dmem VAs observed around 0x1559_xxxx_xxxx.
    // Keep HLE speaker-array objects out of that window.
    private static bool IsDirectMemoryWindowAddress(ulong value) =>
        value >= 0x0000_1400_0000_0000UL && value < 0x0000_1800_0000_0000UL;

    private static bool IsPlausibleGuestObjectPointer(ulong value) =>
        value >= 0x1000_0000UL &&
        value != 0x10000UL &&
        value < 0x0000_8000_0000_0000UL;

    // Windows user stacks sit in 0x00007FFFxxxxxxxx. Never treat those as
    // heap objects we can bulk-initialize.
    private static bool IsGuestStackAddress(ulong value) =>
        value >= 0x0000_7FF0_0000_0000UL && value <= 0x0000_7FFF_FFFF_FFFFUL;

    private static ulong ResolveGuestOutBuffer(ulong primary, ulong secondary)
    {
        // Accept heap or stack out-buffers (PortGetState legitimately uses both),
        // but never small integers / size constants.
        if (IsWritableOutBuffer(primary))
        {
            return primary;
        }

        if (IsWritableOutBuffer(secondary))
        {
            return secondary;
        }

        return 0;
    }

    private static bool IsWritableOutBuffer(ulong value) =>
        value != 0 &&
        value != 0x10000UL &&
        value >= 0x1000UL &&
        (IsPlausibleGuestObjectPointer(value) || IsGuestStackAddress(value));

    private static int WriteZeroSpeakerArrayCoefficients(CpuContext ctx, string label)
    {
        var destination = ctx[CpuRegister.Rsi];
        if (destination == 0)
        {
            destination = ctx[CpuRegister.Rdx];
        }

        // Coefficients are large — only wipe real heap objects, never stack.
        if (destination != 0 &&
            IsPlausibleGuestObjectPointer(destination) &&
            !IsGuestStackAddress(destination))
        {
            Span<byte> zeros = stackalloc byte[SpeakerArrayCoefficientBytes];
            zeros.Clear();
            if (!ctx.Memory.TryWrite(destination, zeros))
            {
                TraceAudioOut2($"{label} write-failed dest=0x{destination:X}");
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        TraceAudioOut2($"{label} ok dest=0x{destination:X}");
        return SetReturn(ctx, 0);
    }

    private static bool TryWriteUInt64(CpuContext ctx, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static ulong GetSpeakerArrayMemorySize(
        uint speakerCount,
        bool useObjectLayout,
        bool includeCoefficients)
    {
        // Ghidra 0x4ef40. The public export always supplies zero for the
        // private fourth parameter, selecting (3, 7) for the object layout.
        var size = useObjectLayout
            ? GetObjectSpeakerArrayBaseSize(speakerCount) + 0xA0UL
            : GetStandardSpeakerArrayBaseSize(speakerCount) + 0x80UL;

        if (!includeCoefficients)
        {
            return size + 0x100UL;
        }

        var coefficientBytes = GetAmbisonicsCoefficientBytes(speakerCount, 5, 0xF0);
        return size + 0x1A0UL + AlignUp32(coefficientBytes + 0x100UL);
    }

    private static ulong GetStandardSpeakerArrayBaseSize(uint speakerCount)
    {
        // Ghidra 0x3f790.
        var countPlusOne = (ulong)unchecked(speakerCount + 1U);
        return AlignUp32(countPlusOne * 8UL) +
               ((ulong)(unchecked(speakerCount + 8U) & ~7U) * 4UL) +
               AlignUp32(countPlusOne * 2UL) +
               AlignUp32(countPlusOne * 0x10UL);
    }

    private static ulong GetObjectSpeakerArrayBaseSize(uint speakerCount)
    {
        // Ghidra 0x41a00 with its public-export constants param2=3,param3=7.
        const uint objectCount = 3;
        const uint objectStrideSelector = 7;
        var totalCount = unchecked(speakerCount + objectCount);
        var lowCount = totalCount & 0xFFFFU;
        var expandedCount = lowCount < 3U ? lowCount : unchecked((lowCount * 2U) - 4U);

        var size = GetSpeakerMixWorkspaceSize(lowCount) + 0x60UL +
                   AlignUp32((ulong)totalCount * 0xCUL) +
                   ((ulong)(unchecked(speakerCount + objectCount + 7U) & ~7U) * 4UL) +
                   AlignUp32((ulong)expandedCount * 6UL) +
                   AlignUp32((ulong)expandedCount * 0x30UL);

        size += ((objectStrideSelector * 2UL) + 0x18UL) * objectCount;
        var lastIndex = unchecked(totalCount - 1U);
        if (lastIndex > 0x1FU)
        {
            size += ((lastIndex >> 3) & 0xFFFF_FFFCUL) + 4UL;
        }

        return AlignUp32(size);
    }

    private static ulong GetSpeakerMixWorkspaceSize(uint count)
    {
        // Ghidra 0x44110.
        var smallCount = count < 3U;
        var doubled = smallCount ? count : unchecked((count * 2U) - 4U);
        var tripled = smallCount ? count : unchecked((count * 3U) - 6U);
        return AlignUp32((ulong)count * 2UL) +
               AlignUp32((ulong)tripled * 4UL) +
               AlignUp32((ulong)doubled * 6UL);
    }

    private static ulong GetAmbisonicsCoefficientBytes(uint speakerCount, uint order, uint stride)
    {
        // Ghidra 0x45d40.
        var alignedSpeakers = unchecked(speakerCount + 7U) & ~7U;
        var alignedStride = unchecked(stride + 7U) & ~7U;
        var coefficientCount = unchecked((order + 1U) * (order + 1U));
        return ((ulong)stride * alignedSpeakers +
                ((ulong)alignedStride + alignedSpeakers) * coefficientCount) * 4UL;
    }

    private static bool IsValidAmbisonicsCoefficientIndex(int mode, uint coefficientIndex) =>
        mode switch
        {
            0 => coefficientIndex is >= 0x40 and <= 0x63,
            1 => coefficientIndex < 0x10,
            _ => coefficientIndex < 0x10 || coefficientIndex is >= 0x40 and <= 0x63,
        };

    internal static void ResetSpeakerArraysForTests() => SpeakerArrays.Clear();

    internal static int SpeakerArrayCountForTests => SpeakerArrays.Count;

    // Firmware 12.70 FUN_0000dc90 at 0xdc90.
    private static ulong GetAudioOut2DescriptorSize(uint count) =>
        (((ulong)count + 0x1FUL) >> 5) * 4UL + 0xCUL;

    private static ulong AlignUp(ulong value, ulong alignment) =>
        unchecked(value + alignment - 1UL) & ~(alignment - 1UL);

    private static ulong AlignUp32(ulong value) => unchecked(value + 0x1FUL) & ~0x1FUL;

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static void TraceAudioOut2(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AUDIO_OUT2"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] audio_out2.{message}");
        }
    }

    private static void TraceSubmitSkipped(ContextState context, int frames, string reason)
    {
        var n = Interlocked.Increment(ref _submitSkipTraceCount);
        if (n <= 8 || n % 500 == 0)
        {
            TraceAudioOut2(
                $"context-submit-skip#{n} handle=0x{context.Handle:X} frames={frames} reason={reason}");
        }
    }
}
