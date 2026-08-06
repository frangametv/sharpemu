// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Gen5 libc search and integer-conversion exports recovered from the pinned
/// libSceLibcInternal image used by Grand Theft Auto V.
/// </summary>
public static class LibcSearchAndConversionExports
{
    private const int Einval = 22;
    private const int Erange = 34;

    [SysAbiExport(
        Nid = "NesIgTmfF0Q",
        ExportName = "bsearch",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcBsearch(CpuContext ctx)
    {
        var keyAddress = ctx[CpuRegister.Rdi];
        var baseAddress = ctx[CpuRegister.Rsi];
        var elementCount = ctx[CpuRegister.Rdx];
        var elementSize = ctx[CpuRegister.Rcx];
        var comparator = ctx[CpuRegister.R8];
        ctx[CpuRegister.Rax] = 0;

        // libSceLibcInternal 0x67A20 performs no eager pointer validation: an
        // empty range returns null without touching base/key/comparator.
        while (elementCount != 0)
        {
            var midpoint = elementCount >> 1;
            var candidateAddress = unchecked(baseAddress + (midpoint * elementSize));
            var scheduler = GuestThreadExecution.Scheduler;
            if (scheduler is null ||
                !scheduler.TryCallGuestFunction(
                    ctx,
                    comparator,
                    keyAddress,
                    candidateAddress,
                    0,
                    0,
                    0,
                    "bsearch",
                    out var rawComparison,
                    out _))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP;
            }

            var comparison = unchecked((int)(uint)rawComparison);
            if (comparison < 0)
            {
                elementCount = midpoint;
                continue;
            }

            if (comparison == 0)
            {
                ctx[CpuRegister.Rax] = candidateAddress;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            baseAddress = unchecked(candidateAddress + elementSize);
            elementCount = unchecked(elementCount - midpoint - 1);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "5OqszGpy7Mg",
        ExportName = "strtoull",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcStrtoull(CpuContext ctx)
    {
        var inputAddress = ctx[CpuRegister.Rdi];
        var endPointerAddress = ctx[CpuRegister.Rsi];
        var requestedBase = unchecked((uint)ctx[CpuRegister.Rdx]);
        var cursor = inputAddress;

        if (!TryReadByte(ctx, cursor, out var current))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // The firmware parser uses the C-locale ctype table. Bytes with the
        // high bit set are not treated as whitespace.
        while (IsAsciiSpace(current))
        {
            cursor = unchecked(cursor + 1);
            if (!TryReadByte(ctx, cursor, out current))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        var negative = current == (byte)'-';
        if (negative || current == (byte)'+')
        {
            cursor = unchecked(cursor + 1);
            if (!TryReadByte(ctx, cursor, out current))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        // 0 and 2..36 are the only accepted bases. The analyzed wrapper writes
        // the original input to endptr before publishing EINVAL through errno.
        if (requestedBase == 1 || requestedBase > 36)
        {
            if (!TryWriteEndPointer(ctx, endPointerAddress, inputAddress))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (!KernelRuntimeCompatExports.TrySetErrno(ctx, Einval))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var numberBase = requestedBase;
        if (numberBase == 0)
        {
            numberBase = 10;
            if (current == (byte)'0')
            {
                if (!TryReadByte(ctx, unchecked(cursor + 1), out var next))
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                if ((next | 0x20) == (byte)'x')
                {
                    numberBase = 16;
                    cursor = unchecked(cursor + 2);
                    if (!TryReadByte(ctx, cursor, out current))
                    {
                        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                    }
                }
                else
                {
                    numberBase = 8;
                }
            }
        }
        else if (numberBase == 16 && current == (byte)'0')
        {
            if (!TryReadByte(ctx, unchecked(cursor + 1), out var next))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if ((next | 0x20) == (byte)'x')
            {
                cursor = unchecked(cursor + 2);
                if (!TryReadByte(ctx, cursor, out current))
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }
        }

        var converted = false;
        var overflow = false;
        ulong value = 0;
        while (TryGetDigit(current, out var digit) && digit < numberBase)
        {
            converted = true;
            if (overflow || value > (ulong.MaxValue - digit) / numberBase)
            {
                overflow = true;
            }
            else
            {
                value = (value * numberBase) + digit;
            }

            cursor = unchecked(cursor + 1);
            if (!TryReadByte(ctx, cursor, out current))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        if (overflow)
        {
            // The internal parser sets ERANGE before its final endptr store;
            // the public wrapper publishes the same value once more.
            if (!KernelRuntimeCompatExports.TrySetErrno(ctx, Erange))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        var endAddress = converted ? cursor : inputAddress;
        if (!TryWriteEndPointer(ctx, endPointerAddress, endAddress))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (overflow)
        {
            // The public strtoull wrapper publishes the parser's local ERANGE
            // once more after the parser's endptr store.
            if (!KernelRuntimeCompatExports.TrySetErrno(ctx, Erange))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            ctx[CpuRegister.Rax] = ulong.MaxValue;
        }
        else
        {
            ctx[CpuRegister.Rax] = negative ? unchecked(0UL - value) : value;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
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

    private static bool TryWriteEndPointer(CpuContext ctx, ulong endPointerAddress, ulong value) =>
        endPointerAddress == 0 || ctx.TryWriteUInt64(endPointerAddress, value);

    private static bool IsAsciiSpace(byte value) =>
        value == (byte)' ' || value is >= (byte)'\t' and <= (byte)'\r';

    private static bool TryGetDigit(byte value, out uint digit)
    {
        if (value is >= (byte)'0' and <= (byte)'9')
        {
            digit = (uint)(value - (byte)'0');
            return true;
        }

        var lower = (byte)(value | 0x20);
        if (lower is >= (byte)'a' and <= (byte)'z')
        {
            digit = (uint)(lower - (byte)'a' + 10);
            return true;
        }

        digit = 0;
        return false;
    }
}
