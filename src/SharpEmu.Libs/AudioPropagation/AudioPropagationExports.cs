// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.AudioPropagation;

public static class AudioPropagationExports
{
    private const int ErrorInvalidValue = unchecked((int)0x8A700001);
    private const int ErrorInvalidHandle = unchecked((int)0x8A700002);
    private const int ErrorInvalidPointer = unchecked((int)0x8A700003);
    private const int ErrorInsufficientMemory = unchecked((int)0x8A700004);
    private const int ErrorResourceExhausted = unchecked((int)0x8A700006);
    private const int ErrorInvalidStructure = unchecked((int)0x8A700007);

    private const uint ConfigTag = 0x010107D5;
    private const ulong ConfigSize = 0x38;
    private const uint MemoryInfoTag = 0x010107D4;
    private const ulong MemoryInfoSize = 0x30;
    private const uint MaterialTag = 0x010107D1;
    private const ulong MaterialSize = 0x40;
    private const uint RayTag = 0x010107D7;
    private const ulong RaySize = 0x58;

    private const uint ReferencedObjectAttribute = 0x00020000;
    private const uint PropagationGainAttribute = 0x00020001;
    private const int AttributeSize = 0x20;
    private const int RayCapacity = 64;
    private const int RayBufferSize = RayCapacity * (int)RaySize;
    private const int MaxSystems = 256;
    private const int MaxRooms = 32;
    private const float MaximumFrequency = 1372.0f;
    private const float FrequencyEpsilon = 1.1920929E-07f;

    private const ulong PrimaryBackingMagic = 0x5348_4150_5359_5331; // "SHAPSYS1"
    private const ulong SecondaryBackingMagic = 0x5348_4150_5359_5332; // "SHAPSYS2"
    private const ulong HandlePrefix = 0xA700_0000_0000_0000;

    private static readonly object RegistryGate = new();
    private static readonly Dictionary<ulong, AudioPropagationSystemState> Systems = new();
    private static long _nextHandleSequence;

    [SysAbiExport(
        Nid = "7xyAxrusLko",
        ExportName = "sceAudioPropagationSystemQueryMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SystemQueryMemory(CpuContext ctx)
    {
        var configAddress = ctx[CpuRegister.Rdi];
        var memoryInfoAddress = ctx[CpuRegister.Rsi];
        if (configAddress == 0 || memoryInfoAddress == 0)
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        Span<byte> memoryInfoBytes = stackalloc byte[(int)MemoryInfoSize];
        if (!ctx.Memory.TryRead(memoryInfoAddress, memoryInfoBytes))
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        var memoryInfo = ParseMemoryInfo(memoryInfoBytes);
        if (memoryInfo.Tag != MemoryInfoTag || memoryInfo.Size != MemoryInfoSize)
        {
            return SetReturn(ctx, ErrorInvalidStructure);
        }

        Span<byte> output = stackalloc byte[0x20];
        output.Clear();
        if (!ctx.Memory.TryWrite(memoryInfoAddress + 0x10, output))
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        var validation = TryReadAndValidateConfig(ctx, configAddress, out _, out var requirements);
        if (validation != 0)
        {
            return SetReturn(ctx, validation);
        }

        if (!TryWriteUInt64(ctx, memoryInfoAddress + 0x18, requirements.PrimarySize) ||
            !TryWriteUInt64(ctx, memoryInfoAddress + 0x28, requirements.SecondarySize))
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "aNEqtSHdUSo",
        ExportName = "sceAudioPropagationSystemCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SystemCreate(CpuContext ctx)
    {
        var configAddress = ctx[CpuRegister.Rdi];
        var memoryInfoAddress = ctx[CpuRegister.Rsi];
        var outputAddress = ctx[CpuRegister.Rdx];
        if (configAddress == 0 || memoryInfoAddress == 0 || outputAddress == 0)
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        Span<byte> memoryInfoBytes = stackalloc byte[(int)MemoryInfoSize];
        if (!ctx.Memory.TryRead(memoryInfoAddress, memoryInfoBytes))
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        var memoryInfo = ParseMemoryInfo(memoryInfoBytes);
        // Preserve the firmware's pointer-first validation for malformed
        // descriptors, but accept a valid descriptor without guest backing.
        // The emulated service keeps its authoritative state in managed
        // memory, and some titles only use QueryMemory to size an optional
        // caller-owned allocation.
        if (memoryInfo.PrimaryAddress == 0 &&
            (memoryInfo.Tag != MemoryInfoTag || memoryInfo.Size != MemoryInfoSize))
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        if (memoryInfo.Tag != MemoryInfoTag || memoryInfo.Size != MemoryInfoSize)
        {
            return SetReturn(ctx, ErrorInvalidStructure);
        }

        if (memoryInfo.PrimarySize == 0)
        {
            return SetReturn(ctx, ErrorInvalidValue);
        }

        Span<byte> configBytes = stackalloc byte[(int)ConfigSize];
        if (!ctx.Memory.TryRead(configAddress, configBytes))
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        var unvalidatedConfig = ParseConfig(configBytes);
        if ((unvalidatedConfig.Flags & 1U) != 0 && memoryInfo.SecondarySize == 0)
        {
            return SetReturn(ctx, ErrorInvalidValue);
        }

        var validation = ValidateConfig(configBytes, unvalidatedConfig, out var requirements);
        if (validation != 0)
        {
            return SetReturn(ctx, validation);
        }

        if (memoryInfo.PrimarySize < requirements.PrimarySize ||
            memoryInfo.SecondarySize < requirements.SecondarySize)
        {
            return SetReturn(ctx, ErrorInsufficientMemory);
        }

        lock (RegistryGate)
        {
            if (Systems.Count >= MaxSystems)
            {
                return SetReturn(ctx, ErrorResourceExhausted);
            }

            var handle = AllocateHandle(kind: 1);
            var state = new AudioPropagationSystemState(handle, ctx.Memory, unvalidatedConfig, memoryInfo);
            var primaryHeader = BuildPrimaryBackingHeader(handle, memoryInfo, unvalidatedConfig);
            var secondaryHeader = memoryInfo.SecondarySize == 0
                ? null
                : BuildSecondaryBackingHeader(handle);

            // The backing returned by some PS5 allocators is writable but not
            // readable until its first store. Requiring a rollback snapshot
            // rejected Astro Bot immediately after a successful QueryMemory.
            // The emulated service owns its actual state; only the caller's
            // output handle is mandatory, while backing headers are advisory.
            if (!TryWriteUInt64(ctx, outputAddress, handle))
            {
                return SetReturn(ctx, ErrorInvalidPointer);
            }

            Systems.Add(handle, state);
            if (memoryInfo.PrimaryAddress != 0)
            {
                _ = ctx.Memory.TryWrite(memoryInfo.PrimaryAddress, primaryHeader);
            }

            if (secondaryHeader is not null && memoryInfo.SecondaryAddress != 0)
            {
                _ = ctx.Memory.TryWrite(memoryInfo.SecondaryAddress, secondaryHeader);
            }
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "ht-QXT3zGxo",
        ExportName = "sceAudioPropagationSystemGetRays",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SystemGetRays(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        if (!TryGetSystem(systemHandle, out var system))
        {
            return SetReturn(ctx, ErrorInvalidHandle);
        }

        var raysAddress = ctx[CpuRegister.Rsi];
        var countAddress = ctx[CpuRegister.Rdx];
        if (raysAddress == 0 || countAddress == 0)
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        if (!TryReadUInt32(ctx, countAddress, out var capacity))
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        if (capacity != RayCapacity)
        {
            return SetReturn(ctx, ErrorInvalidValue);
        }

        var rays = new byte[RayBufferSize];
        if (!ctx.Memory.TryRead(raysAddress, rays))
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        for (var index = 0; index < RayCapacity; index++)
        {
            var record = rays.AsSpan(index * (int)RaySize, (int)RaySize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != RayTag ||
                BinaryPrimitives.ReadUInt64LittleEndian(record[0x08..]) != RaySize)
            {
                return SetReturn(ctx, ErrorInvalidStructure);
            }
        }

        lock (system.Gate)
        {
            var rooms = system.Rooms.Values.OrderBy(static room => room.Slot).ToArray();
            if (rooms.Length > RayCapacity)
            {
                return SetReturn(ctx, ErrorInvalidValue);
            }

            var firstMaterialHandle = system.Materials.Keys.Order().FirstOrDefault();
            for (var index = 0; index < rooms.Length; index++)
            {
                var record = rays.AsSpan(index * (int)RaySize, (int)RaySize);
                record[0x10..].Clear();
                BinaryPrimitives.WriteUInt64LittleEndian(record[0x10..], rooms[index].Handle);
                BinaryPrimitives.WriteUInt64LittleEndian(record[0x18..], firstMaterialHandle);

                // Full acoustic tracing is outside this cluster. Emit stable rays
                // derived from room/material state while preserving the recovered
                // D7/0x58 ABI and the caller-visible nonzero direction vector.
                var x = (float)(index + 1) / (rooms.Length + 1);
                var y = system.Config.MaterialCapacity == 0
                    ? 0.0f
                    : (float)system.Materials.Count / system.Config.MaterialCapacity;
                WriteSingle(record[0x20..], x);
                WriteSingle(record[0x24..], y);
                WriteSingle(record[0x28..], 1.0f);
                WriteSingle(record[0x2C..], index + 1.0f);
                BinaryPrimitives.WriteUInt32LittleEndian(record[0x30..], (uint)rooms[index].Slot);
                BinaryPrimitives.WriteUInt32LittleEndian(record[0x34..], (uint)system.Materials.Count);
            }

            if (!ctx.Memory.TryWrite(raysAddress, rays) ||
                !TryWriteUInt32(ctx, countAddress, (uint)rooms.Length))
            {
                return SetReturn(ctx, ErrorInvalidPointer);
            }

            system.LastRayBufferAddress = raysAddress;
            for (var index = 0; index < rooms.Length; index++)
            {
                rooms[index].LastRayRecordAddress = raysAddress + (ulong)(index * (int)RaySize);
            }
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "VlBT16890mA",
        ExportName = "sceAudioPropagationSystemSetRays",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SystemSetRays(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        if (!TryGetSystem(systemHandle, out _))
        {
            return SetReturn(ctx, ErrorInvalidHandle);
        }

        var raysAddress = ctx[CpuRegister.Rsi];
        if (raysAddress == 0)
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        var count = unchecked((uint)ctx[CpuRegister.Rdx]);
        if (count == 0)
        {
            return SetReturn(ctx, ErrorInvalidValue);
        }

        ulong byteCount;
        try
        {
            byteCount = checked((ulong)count * RaySize);
        }
        catch (OverflowException)
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        if (raysAddress > ulong.MaxValue - (byteCount - 1))
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        for (var index = 0U; index < count; index++)
        {
            var recordAddress = raysAddress + ((ulong)index * RaySize);
            if (!TryReadUInt32(ctx, recordAddress, out var tag))
            {
                return SetReturn(ctx, ErrorInvalidPointer);
            }

            if (tag != RayTag)
            {
                return SetReturn(ctx, ErrorInvalidStructure);
            }

            if (!TryReadUInt64(ctx, recordAddress + 0x08, out var size))
            {
                return SetReturn(ctx, ErrorInvalidPointer);
            }

            if (size != RaySize)
            {
                return SetReturn(ctx, ErrorInvalidStructure);
            }
        }

        // Firmware consumes only records still associated with an outstanding
        // GetRays source slot. The current HLE has no acoustic tracing backend,
        // so validated matched and unmatched records are both an internal no-op.
        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "kIdb+iQUzCs",
        ExportName = "sceAudioPropagationSystemSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SystemSetAttributes(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        if (!TryGetSystem(systemHandle, out var system))
        {
            return SetReturn(ctx, ErrorInvalidHandle);
        }

        var attributesAddress = ctx[CpuRegister.Rsi];
        var count = (uint)ctx[CpuRegister.Rdx];
        if (attributesAddress == 0)
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        if (count == 0 || count > int.MaxValue)
        {
            return SetReturn(ctx, ErrorInvalidValue);
        }

        var attributes = new List<AudioPropagationAttribute>((int)Math.Min(count, 64U));
        Span<byte> descriptor = stackalloc byte[AttributeSize];
        for (var index = 0U; index < count; index++)
        {
            var offset = (ulong)index * AttributeSize;
            if (attributesAddress > ulong.MaxValue - offset)
            {
                return SetReturn(ctx, ErrorInvalidPointer);
            }

            if (!ctx.Memory.TryRead(attributesAddress + offset, descriptor))
            {
                return SetReturn(ctx, ErrorInvalidPointer);
            }

            var type = BinaryPrimitives.ReadUInt32LittleEndian(descriptor);
            var dataAddress = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[0x08..]);
            var dataSize = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[0x10..]);
            if (dataAddress == 0)
            {
                return SetReturn(ctx, ErrorInvalidPointer);
            }

            if (!HasValidAttributeSize(type, dataSize))
            {
                return SetReturn(ctx, ErrorInvalidValue);
            }

            attributes.Add(new AudioPropagationAttribute(type, dataAddress, dataSize, 0, 0.0f));
        }

        lock (system.Gate)
        {
            foreach (var attribute in attributes)
            {
                if (attribute.Type == ReferencedObjectAttribute)
                {
                    if (!TryReadUInt64(ctx, attribute.DataAddress, out var referencedHandle))
                    {
                        return SetReturn(ctx, ErrorInvalidPointer);
                    }

                    if (referencedHandle != 0 &&
                        !system.Materials.ContainsKey(referencedHandle) &&
                        !system.Rooms.ContainsKey(referencedHandle))
                    {
                        return SetReturn(ctx, ErrorInvalidHandle);
                    }

                    system.ReferencedObjectHandle = referencedHandle;
                }
                else if (attribute.Type == PropagationGainAttribute)
                {
                    if (!TryReadSingle(ctx, attribute.DataAddress, out var value))
                    {
                        return SetReturn(ctx, ErrorInvalidPointer);
                    }

                    if ((system.Config.Flags & 4U) == 0 ||
                        !float.IsFinite(value) || value < 0.0f || value > 1.0f)
                    {
                        return SetReturn(ctx, ErrorInvalidValue);
                    }

                    system.PropagationGain = value;
                }
                else
                {
                    return SetReturn(ctx, ErrorInvalidValue);
                }
            }
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "CPLV6G-eXmk",
        ExportName = "sceAudioPropagationSystemRegisterMaterial",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int SystemRegisterMaterial(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        if (!TryGetSystem(systemHandle, out var system))
        {
            return SetReturn(ctx, ErrorInvalidHandle);
        }

        var materialAddress = ctx[CpuRegister.Rsi];
        var outputAddress = ctx[CpuRegister.Rdx];
        if (materialAddress == 0 || outputAddress == 0)
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        var material = new byte[(int)MaterialSize];
        Span<byte> originalOutput = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(materialAddress, material) ||
            !ctx.Memory.TryRead(outputAddress, originalOutput))
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        if (BinaryPrimitives.ReadUInt64LittleEndian(material.AsSpan(0x08)) != MaterialSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(material) != MaterialTag)
        {
            return SetReturn(ctx, ErrorInvalidStructure);
        }

        lock (system.Gate)
        {
            if (system.Config.MaterialCapacity == 0)
            {
                return SetReturn(ctx, ErrorInvalidValue);
            }

            if ((uint)system.Materials.Count >= system.Config.MaterialCapacity)
            {
                return SetReturn(ctx, ErrorResourceExhausted);
            }

            var handle = AllocateHandle(kind: 4);
            if (!TryWriteUInt64(ctx, outputAddress, handle))
            {
                _ = ctx.Memory.TryWrite(outputAddress, originalOutput);
                return SetReturn(ctx, ErrorInvalidPointer);
            }

            system.Materials.Add(handle, new AudioPropagationMaterialState(handle, material));
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "8bI5h8req30",
        ExportName = "sceAudioPropagationRoomCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioPropagation")]
    public static int RoomCreate(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        if (!TryGetSystem(systemHandle, out var system))
        {
            return SetReturn(ctx, ErrorInvalidHandle);
        }

        var outputAddress = ctx[CpuRegister.Rsi];
        if (outputAddress == 0)
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        Span<byte> originalOutput = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(outputAddress, originalOutput))
        {
            return SetReturn(ctx, ErrorInvalidPointer);
        }

        lock (system.Gate)
        {
            if (system.Rooms.Count >= MaxRooms)
            {
                return SetReturn(ctx, ErrorResourceExhausted);
            }

            var slot = system.Rooms.Count;
            var handle = AllocateHandle(kind: 6);
            if (!TryWriteUInt64(ctx, outputAddress, handle))
            {
                _ = ctx.Memory.TryWrite(outputAddress, originalOutput);
                return SetReturn(ctx, ErrorInvalidPointer);
            }

            system.Rooms.Add(handle, new AudioPropagationRoomState(handle, slot));
        }

        return SetSuccess(ctx);
    }

    internal static void ResetForTests()
    {
        lock (RegistryGate)
        {
            Systems.Clear();
            _nextHandleSequence = 0;
        }
    }

    internal static bool TryGetDebugSnapshot(
        ulong handle,
        out AudioPropagationDebugSnapshot snapshot)
    {
        AudioPropagationSystemState? system;
        lock (RegistryGate)
        {
            Systems.TryGetValue(handle, out system);
        }

        if (system is null)
        {
            snapshot = default;
            return false;
        }

        lock (system.Gate)
        {
            snapshot = new AudioPropagationDebugSnapshot(
                system.Materials.Count,
                system.Rooms.Count,
                system.ReferencedObjectHandle,
                system.PropagationGain,
                system.LastRayBufferAddress);
            return true;
        }
    }

    private static int TryReadAndValidateConfig(
        CpuContext ctx,
        ulong address,
        out AudioPropagationConfig config,
        out AudioPropagationMemoryRequirements requirements)
    {
        Span<byte> bytes = stackalloc byte[(int)ConfigSize];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            config = default;
            requirements = default;
            return ErrorInvalidPointer;
        }

        config = ParseConfig(bytes);
        return ValidateConfig(bytes, config, out requirements);
    }

    private static int ValidateConfig(
        ReadOnlySpan<byte> bytes,
        AudioPropagationConfig config,
        out AudioPropagationMemoryRequirements requirements)
    {
        requirements = default;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != ConfigTag ||
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[0x08..]) != ConfigSize)
        {
            return ErrorInvalidStructure;
        }

        if (config.SourceCapacity is 0 or > 64 ||
            config.MaterialCapacity is 0 or > 64 ||
            config.WorkerWidth != 6 ||
            config.PropagationMode is < 1 or > 2 ||
            config.TransformSize is not (0x100 or 0x200 or 0x400 or 0x800 or 0x1000) ||
            !float.IsFinite(config.MinimumFrequency) ||
            config.MinimumFrequency <= FrequencyEpsilon ||
            config.MinimumFrequency > MaximumFrequency ||
            !float.IsFinite(config.MaximumFrequency) ||
            config.MaximumFrequency <= FrequencyEpsilon ||
            config.MaximumFrequency > MaximumFrequency ||
            config.Flags >= 8 ||
            (config.TransformSize != 0x200 && (config.Flags & 1U) != 0) ||
            !TryComputeMemoryRequirements(config, out requirements) ||
            requirements.PrimarySize == 0)
        {
            return ErrorInvalidValue;
        }

        return 0;
    }

    private static AudioPropagationConfig ParseConfig(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x10..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x14..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x18..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x1C..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x20..]),
        ReadSingle(bytes[0x24..]),
        ReadSingle(bytes[0x28..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x2C..]));

    private static AudioPropagationMemoryInfo ParseMemoryInfo(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(bytes),
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[0x08..]),
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[0x10..]),
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[0x18..]),
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[0x20..]),
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[0x28..]));

    private static bool TryComputeMemoryRequirements(
        AudioPropagationConfig config,
        out AudioPropagationMemoryRequirements requirements)
    {
        try
        {
            checked
            {
                var sources = (ulong)config.SourceCapacity;
                var materialCount = (ulong)config.MaterialCapacity;
                var mode = (ulong)config.PropagationMode;
                var coreSize = sources * 0x400UL;
                if (config.PropagationMode != 0)
                {
                    var propagationBlock = ((mode + 2UL) * 0x20UL) + 0x22E0UL;
                    coreSize += propagationBlock * sources * 6UL;
                    if (config.PropagationMode >= 2)
                    {
                        coreSize += propagationBlock * sources * 0x1EUL;
                    }
                }

                // VROUNDSS immediate 0x0A selects round toward +infinity.
                var bandFloat = MathF.Ceiling(
                    (config.MaximumFrequency / config.MinimumFrequency) * 48000.0f);
                if (!float.IsFinite(bandFloat) || bandFloat < 0.0f || bandFloat > ulong.MaxValue)
                {
                    requirements = default;
                    return false;
                }

                var bandCount = (ulong)bandFloat;
                var transformBytes = config.TransformSize == 0x100
                    ? 0x800UL
                    : (ulong)config.TransformSize * 4UL;
                coreSize += transformBytes + (sources * bandCount * 4UL);

                // The developer firmware adds the low-nibble remainder here;
                // retain that nonstandard alignment behavior verbatim.
                coreSize += coreSize & 0xFUL;
                coreSize += sources * (config.PropagationMode == 1 ? 0x3C0UL : 0x1680UL);
                if (config.TransformSize == 0x100)
                {
                    coreSize += sources * ((config.Flags & 2U) == 0 ? 0x4020UL : 0x9020UL);
                }

                coreSize += materialCount * 0x1070UL;
                if ((config.Flags & 4U) != 0)
                {
                    coreSize += 0x1E8710UL;
                }

                coreSize += 0x3040UL;
                var primarySize = coreSize - ((coreSize + 8UL) & 0xFUL) + 0x14F8UL;

                // Secondary sizing depends on a firmware/SDK feature query not
                // represented in HLE. The observed non-accelerated floor is stable
                // and conservative; QueryMemory and SystemCreate use the same value.
                var secondarySize = (config.Flags & 1U) != 0
                    ? sources * 0x90000UL
                    : 0UL;
                requirements = new AudioPropagationMemoryRequirements(primarySize, secondarySize);
                return true;
            }
        }
        catch (OverflowException)
        {
            requirements = default;
            return false;
        }
    }

    private static bool TryGetSystem(
        ulong handle,
        out AudioPropagationSystemState system)
    {
        lock (RegistryGate)
        {
            return Systems.TryGetValue(handle, out system!);
        }
    }

    private static byte[] BuildPrimaryBackingHeader(
        ulong handle,
        AudioPropagationMemoryInfo memoryInfo,
        AudioPropagationConfig config)
    {
        var header = new byte[0x30];
        BinaryPrimitives.WriteUInt64LittleEndian(header, PrimaryBackingMagic);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x08), handle);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x10), memoryInfo.PrimarySize);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x18), memoryInfo.SecondaryAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x20), memoryInfo.SecondarySize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x28), config.SourceCapacity);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x2C), config.MaterialCapacity);
        return header;
    }

    private static byte[] BuildSecondaryBackingHeader(ulong handle)
    {
        var header = new byte[0x10];
        BinaryPrimitives.WriteUInt64LittleEndian(header, SecondaryBackingMagic);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x08), handle);
        return header;
    }

    private static bool HasValidAttributeSize(uint type, ulong size) => type switch
    {
        0 or 1 or 2 or 3 or 6 or 8 or
            0x10000 or 0x10001 or 0x10002 or 0x10003 or 0x10004 => size == 0x10,
        4 => size == 0x18,
        5 or 7 or 9 or ReferencedObjectAttribute => size == 0x08,
        0x0B => size == 0x6D0,
        0x10005 => size == 0x01,
        PropagationGainAttribute => size == 0x04,
        _ => false,
    };

    private static ulong AllocateHandle(byte kind)
    {
        var sequence = unchecked((ulong)Interlocked.Increment(ref _nextHandleSequence)) &
            0x0000_FFFF_FFFF_FFFFUL;
        return HandlePrefix | ((ulong)kind << 48) | sequence;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    private static bool TryReadUInt64(CpuContext ctx, ulong address, out ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return true;
    }

    private static bool TryReadSingle(CpuContext ctx, ulong address, out float value)
    {
        if (!TryReadUInt32(ctx, address, out var bits))
        {
            value = 0.0f;
            return false;
        }

        value = BitConverter.UInt32BitsToSingle(bits);
        return true;
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static bool TryWriteUInt64(CpuContext ctx, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static float ReadSingle(ReadOnlySpan<byte> bytes) =>
        BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(bytes));

    private static void WriteSingle(Span<byte> bytes, float value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, BitConverter.SingleToUInt32Bits(value));

    private static int SetSuccess(CpuContext ctx) => SetReturn(ctx, 0);

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((uint)result);
        return result;
    }
}
