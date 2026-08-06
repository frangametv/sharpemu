// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.AudioPropagation;

internal readonly record struct AudioPropagationConfig(
    uint SourceCapacity,
    uint MaterialCapacity,
    uint WorkerWidth,
    uint PropagationMode,
    uint TransformSize,
    float MinimumFrequency,
    float MaximumFrequency,
    uint Flags);

internal readonly record struct AudioPropagationMemoryRequirements(
    ulong PrimarySize,
    ulong SecondarySize);

internal readonly record struct AudioPropagationMemoryInfo(
    uint Tag,
    ulong Size,
    ulong PrimaryAddress,
    ulong PrimarySize,
    ulong SecondaryAddress,
    ulong SecondarySize);

internal sealed class AudioPropagationSystemState
{
    public AudioPropagationSystemState(
        ulong handle,
        ICpuMemory memory,
        AudioPropagationConfig config,
        AudioPropagationMemoryInfo memoryInfo)
    {
        Handle = handle;
        Memory = memory;
        Config = config;
        MemoryInfo = memoryInfo;
    }

    public object Gate { get; } = new();

    public ulong Handle { get; }

    public ICpuMemory Memory { get; }

    public AudioPropagationConfig Config { get; }

    public AudioPropagationMemoryInfo MemoryInfo { get; }

    public Dictionary<ulong, AudioPropagationMaterialState> Materials { get; } = new();

    public Dictionary<ulong, AudioPropagationRoomState> Rooms { get; } = new();

    public ulong ReferencedObjectHandle { get; set; }

    public float PropagationGain { get; set; } = 1.0f;

    public ulong LastRayBufferAddress { get; set; }
}

internal sealed class AudioPropagationMaterialState
{
    public AudioPropagationMaterialState(ulong handle, byte[] descriptor)
    {
        Handle = handle;
        Descriptor = descriptor;
    }

    public ulong Handle { get; }

    public byte[] Descriptor { get; }
}

internal sealed class AudioPropagationRoomState
{
    public AudioPropagationRoomState(ulong handle, int slot)
    {
        Handle = handle;
        Slot = slot;
    }

    public ulong Handle { get; }

    public int Slot { get; }

    public ulong LastRayRecordAddress { get; set; }
}

internal readonly record struct AudioPropagationAttribute(
    uint Type,
    ulong DataAddress,
    ulong DataSize,
    ulong ReferencedHandle,
    float ScalarValue);

internal readonly record struct AudioPropagationDebugSnapshot(
    int MaterialCount,
    int RoomCount,
    ulong ReferencedObjectHandle,
    float PropagationGain,
    ulong LastRayBufferAddress);
