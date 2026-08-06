// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Remoteplay;

public static class RemoteplayExports
{
    private static int _initialized;

    // Firmware 12.70 libSceRemoteplay.sprx SHA-256
    // 7c33fa5c41b065bf7a3577dbb968af192dbc7768a84e706e2a98dcfa0d501d59.
    // Ghidra entry 0x19b0 delegates initialization to the singleton at 0x29d0.
    [SysAbiExport(
        Nid = "k1SwgkMSOM8",
        ExportName = "sceRemoteplayInitialize",
        Target = Generation.Gen5,
        LibraryName = "libSceRemoteplay",
        PreferLle = true)]
    public static int Initialize(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 1);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // Ghidra entry 0x19e0 delegates teardown to singleton routine 0x3390.
    [SysAbiExport(
        Nid = "BOwybKVa3Do",
        ExportName = "sceRemoteplayTerminate",
        Target = Generation.Gen5,
        LibraryName = "libSceRemoteplay",
        PreferLle = true)]
    public static int Terminate(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 0);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // Ghidra entry 0x1b30 delegates to 0x35e0. That routine writes its
    // one-byte connection state to a uint32 output. With no host Remote Play
    // transport attached, state zero is the firmware's disconnected result.
    [SysAbiExport(
        Nid = "g3PNjYKWqnQ",
        ExportName = "sceRemoteplayGetConnectionStatus",
        Target = Generation.Gen5,
        LibraryName = "libSceRemoteplay",
        PreferLle = true)]
    public static int GetConnectionStatus(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rsi];
        if (outputAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // The provider wrapper retries after initialization on a cold query.
        Interlocked.CompareExchange(ref _initialized, 1, 0);
        Span<byte> status = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(status, 0);
        return ctx.Memory.TryWrite(outputAddress, status)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
