// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;

namespace SharpEmu.Core.Runtime;

internal static class ImportedDataRebinder
{
    public static int Rebind(
        IVirtualMemory virtualMemory,
        SelfImage image,
        string imagePath,
        IReadOnlyDictionary<string, ulong> dataSymbols)
    {
        ArgumentNullException.ThrowIfNull(virtualMemory);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(dataSymbols);

        var rebound = 0;
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        foreach (var relocation in image.ImportedRelocations)
        {
            if (!relocation.IsData)
            {
                continue;
            }

            var resolved = dataSymbols.TryGetValue(relocation.Nid, out var symbolAddress) &&
                IsUsableAddress(symbolAddress);
            if (!resolved)
            {
                if (!relocation.IsWeak)
                {
                    throw new InvalidDataException(
                        $"Required imported data symbol '{relocation.Nid}' is unresolved " +
                        $"for '{Path.GetFileName(imagePath)}' at relocation 0x{relocation.TargetAddress:X16}.");
                }

                // ELF weak undefined symbols resolve with S=0. The relocation
                // still writes S+A, so a non-zero addend must not be discarded.
                symbolAddress = 0;
            }

            var reboundValue = AddSigned(symbolAddress, relocation.Addend);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, reboundValue);
            if (!virtualMemory.TryWrite(relocation.TargetAddress, bytes))
            {
                throw new InvalidDataException(
                    $"Failed to write imported data symbol '{relocation.Nid}' for " +
                    $"'{Path.GetFileName(imagePath)}' at relocation 0x{relocation.TargetAddress:X16}.");
            }

            if (resolved)
            {
                rebound++;
            }
        }

        return rebound;
    }

    internal static bool IsUsableAddress(ulong address) =>
        address >= 0x10000 && !IsUnresolvedSentinel(address);

    private static bool IsUnresolvedSentinel(ulong address) =>
        address == 0xFFFEUL ||
        address == 0xFFFF_FFFEUL ||
        address == 0xFFFF_FFFF_FFFF_FFFEUL;

    private static ulong AddSigned(ulong value, long addend)
    {
        if (addend >= 0)
        {
            return unchecked(value + (ulong)addend);
        }

        var magnitude = unchecked((ulong)(-(addend + 1))) + 1;
        return unchecked(value - magnitude);
    }
}
