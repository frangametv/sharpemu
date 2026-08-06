// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Gen5 libc wall-clock exports used by Grand Theft Auto V.
/// </summary>
public static class LibcTimeExports
{
    [SysAbiExport(
        Nid = "wLlFkwG9UcQ",
        ExportName = "time",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcTime(CpuContext ctx)
    {
        long seconds = -1;
        try
        {
            seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        catch (ArgumentOutOfRangeException)
        {
            ctx[CpuRegister.Rax] = ulong.MaxValue;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var outputAddress = ctx[CpuRegister.Rdi];
        if (outputAddress != 0)
        {
            Span<byte> output = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(output, seconds);
            if (!ctx.Memory.TryWrite(outputAddress, output))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)seconds);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
