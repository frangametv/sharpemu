// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Gen5 libc process-global pseudo-random number exports.
/// </summary>
public static class LibcRandomExports
{
    private const ulong Multiplier = 0x5851_F42D_4C95_7F2DUL;
    private static readonly object StateLock = new();
    private static ulong _state = 1;

    [SysAbiExport(
        Nid = "cpCOXWMgha0",
        ExportName = "rand",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcRand(CpuContext ctx)
    {
        ulong result;
        lock (StateLock)
        {
            _state = unchecked((_state * Multiplier) + 1UL);
            result = (_state >> 32) & 0x3FFF_FFFFUL;
        }

        ctx[CpuRegister.Rax] = result;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "VPbJwTCgME0",
        ExportName = "srand",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcSrand(CpuContext ctx)
    {
        lock (StateLock)
        {
            _state = (uint)ctx[CpuRegister.Rdi];
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
