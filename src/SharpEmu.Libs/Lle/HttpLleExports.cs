// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
//
// Ghidra 12.1.2_PUBLIC_20260605 program: libSceHttp.sprx
// Analyzed provider SHA-256: fdb6ddc34e9d8c566421511c693294ca93ab1636f797f8fc705588b678fb1f6a
// The provider registration was recovered by Acelogic. The semantic HLE
// fallback below is maintained by Fran's fork for games that do not load the
// guest libSceHttp provider.

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Lle;

public static class HttpLleExports
{
    private const int HttpErrorOutOfMemory = unchecked((int)0x80431022);
    private const int HttpErrorInvalidValue = unchecked((int)0x804311FE);
    private const int MaxUriInputLength = 1024 * 1024;

    // Ghidra entry 00020f90; body addresses 298.
    [SysAbiExport(
        Nid = "YuOW3dDAKYc",
        ExportName = "sceHttpUriEscape",
        Target = Generation.Gen5,
        LibraryName = "libSceHttp",
        PreferLle = true)]
    public static int UriEscapeWithoutGuestProvider(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        var requiredAddress = ctx[CpuRegister.Rsi];
        var outputCapacity = ctx[CpuRegister.Rdx];
        var inputAddress = ctx[CpuRegister.Rcx];

        if (inputAddress == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (!TryReadNullTerminatedBytes(ctx, inputAddress, out var input))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var output = EscapeUri(input);
        var required = checked((ulong)output.Length + 1);

        if (requiredAddress != 0 &&
            !KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, requiredAddress, required))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // A null output pointer is the documented size-query form used by
        // Astro Bot's WebApiJobWorker before allocating the destination.
        if (outputAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }

        if (outputCapacity < required)
        {
            return ctx.SetReturn(HttpErrorOutOfMemory);
        }

        var terminatedOutput = new byte[output.Length + 1];
        output.CopyTo(terminatedOutput, 0);
        return KernelMemoryCompatExports.TryWriteCompat(ctx, outputAddress, terminatedOutput)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool TryReadNullTerminatedBytes(
        CpuContext ctx,
        ulong address,
        out byte[] value)
    {
        var bytes = new List<byte>();
        Span<byte> current = stackalloc byte[1];
        for (var index = 0; index < MaxUriInputLength; index++)
        {
            if (!KernelMemoryCompatExports.TryReadCompat(ctx, address + (ulong)index, current))
            {
                value = [];
                return false;
            }

            if (current[0] == 0)
            {
                value = bytes.ToArray();
                return true;
            }

            bytes.Add(current[0]);
        }

        value = [];
        return false;
    }

    private static byte[] EscapeUri(ReadOnlySpan<byte> input)
    {
        const string hex = "0123456789ABCDEF";
        var output = new List<byte>(checked(input.Length * 3));
        foreach (var value in input)
        {
            if (IsUnreserved(value))
            {
                output.Add(value);
                continue;
            }

            output.Add((byte)'%');
            output.Add((byte)hex[value >> 4]);
            output.Add((byte)hex[value & 0x0F]);
        }

        return output.ToArray();
    }

    private static bool IsUnreserved(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' or
        >= (byte)'a' and <= (byte)'z' or
        >= (byte)'0' and <= (byte)'9' or
        (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~';
}
