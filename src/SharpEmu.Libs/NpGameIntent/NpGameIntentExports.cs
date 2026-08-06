// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.NpGameIntent;

public static class NpGameIntentExports
{
    private const int NpGameIntentErrorNotInitialized = unchecked((int)0x80553802);
    private static int _initialized;

    [SysAbiExport(
        Nid = "m87BHxt-H60",
        ExportName = "sceNpGameIntentInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpGameIntent")]
    public static int NpGameIntentInitialize(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 1);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "0HBYxYAjmf0",
        ExportName = "sceNpGameIntentTerminate",
        Target = Generation.Gen5,
        LibraryName = "libSceNpGameIntent",
        PreferLle = true)]
    public static int NpGameIntentTerminate(CpuContext ctx)
    {
        if (Interlocked.CompareExchange(ref _initialized, 0, 1) == 0)
        {
            return ctx.SetReturn(NpGameIntentErrorNotInitialized);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    internal static void ResetForTests() => Volatile.Write(ref _initialized, 0);
}
