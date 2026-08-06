// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Random;

public static class RandomExports
{
    private const int MaximumRandomBytes = 0x40;
    private const int RandomErrorInvalidArgument = unchecked((int)0x817C0016);
    private const int RandomErrorInternal = unchecked((int)0x817C00FF);

    // Ghidra 12.1.2, libSceRandom.sprx SHA-256
    // 4af0ac1f1dc40c45a267a13f623e87080bf11cdedc210c1062e474d59b98b8fd,
    // export 0xD0. RDI is the output buffer and RSI is its unsigned byte count.
    // The provider requires a non-null output, accepts 0..64 bytes, obtains a
    // 64-byte kernel RNG block, and copies exactly the requested prefix.
    [SysAbiExport(
        Nid = "PI7jIZj4pcE",
        ExportName = "sceRandomGetRandomNumber",
        Target = Generation.Gen5,
        LibraryName = "libSceRandom",
        PreferLle = true)]
    public static int GetRandomNumber(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        var requestedByteCount = ctx[CpuRegister.Rsi];

        if (outputAddress == 0 || requestedByteCount > MaximumRandomBytes)
        {
            return ctx.SetReturn(RandomErrorInvalidArgument);
        }

        Span<byte> randomBytes = stackalloc byte[MaximumRandomBytes];
        try
        {
            RandomNumberGenerator.Fill(randomBytes);
        }
        catch (CryptographicException)
        {
            return ctx.SetReturn(RandomErrorInternal);
        }

        var requestedBytes = randomBytes[..(int)requestedByteCount];
        if (!requestedBytes.IsEmpty && !ctx.Memory.TryWrite(outputAddress, requestedBytes))
        {
            // The native provider reaches memcpy here and would fault. Keep a
            // bad guest mapping from escaping into the host process.
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }
}
