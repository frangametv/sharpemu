// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Gen5 libc byte-string exports used by Grand Theft Auto V.
/// </summary>
public static class LibcStringExports
{
    [SysAbiExport(
        Nid = "q0F6yS-rCms",
        ExportName = "strcspn",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcStrcspn(CpuContext ctx)
    {
        var sourceAddress = ctx[CpuRegister.Rdi];
        var rejectAddress = ctx[CpuRegister.Rsi];
        if (!TryReadByte(ctx, sourceAddress, out var firstSource))
        {
            return MemoryFault(ctx);
        }

        if (firstSource == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        Span<ulong> membership = stackalloc ulong[4];
        AddMember(membership, 0);
        if (!TryBuildMembership(ctx, rejectAddress, membership))
        {
            return MemoryFault(ctx);
        }

        return Scan(ctx, sourceAddress, firstSource, membership, stopWhenMember: true);
    }

    [SysAbiExport(
        Nid = "-kU6bB4M-+k",
        ExportName = "strspn",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcStrspn(CpuContext ctx)
    {
        var sourceAddress = ctx[CpuRegister.Rdi];
        var acceptAddress = ctx[CpuRegister.Rsi];
        if (!TryReadByte(ctx, sourceAddress, out var firstSource))
        {
            return MemoryFault(ctx);
        }

        if (firstSource == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        Span<ulong> membership = stackalloc ulong[4];
        if (!TryBuildMembership(ctx, acceptAddress, membership))
        {
            return MemoryFault(ctx);
        }

        return Scan(ctx, sourceAddress, firstSource, membership, stopWhenMember: false);
    }

    private static int Scan(
        CpuContext ctx,
        ulong sourceAddress,
        byte firstSource,
        ReadOnlySpan<ulong> membership,
        bool stopWhenMember)
    {
        ulong count = 0;
        var current = firstSource;
        while (Contains(membership, current) != stopWhenMember)
        {
            count++;
            if (!TryReadByte(ctx, sourceAddress + count, out current))
            {
                return MemoryFault(ctx);
            }
        }

        ctx[CpuRegister.Rax] = count;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryBuildMembership(
        CpuContext ctx,
        ulong stringAddress,
        Span<ulong> membership)
    {
        for (ulong index = 0; ; index++)
        {
            if (!TryReadByte(ctx, stringAddress + index, out var value))
            {
                return false;
            }

            if (value == 0)
            {
                return true;
            }

            AddMember(membership, value);
        }
    }

    private static bool TryReadByte(CpuContext ctx, ulong address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = buffer[0];
        return true;
    }

    private static void AddMember(Span<ulong> membership, byte value) =>
        membership[value >> 6] |= 1UL << (value & 63);

    private static bool Contains(ReadOnlySpan<ulong> membership, byte value) =>
        (membership[value >> 6] & (1UL << (value & 63))) != 0;

    private static int MemoryFault(CpuContext ctx) =>
        ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
}
