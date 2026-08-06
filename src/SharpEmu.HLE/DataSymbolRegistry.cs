// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// First-class registry for Gen5 ABI objects. These entries participate in
/// registration parity without entering the callable SysAbi dispatch table.
/// </summary>
public static class DataSymbolRegistry
{
    public const string ProgNameNid = "djxxOmW6-aw";
    public const string LibcNeedFlagNid = "P330P3dFF68";
    public const string LibcInternalNeedFlagNid = "ZT4ODD2Ts9o";
    public const string StderrNid = "H8AprKeZtNg";
    public const string StdoutNid = "2sWzhYqFH4E";

    private static readonly DataSymbolRegistration[] Registrations =
    [
        new(
            "libkernel",
            ProgNameNid,
            "__progname",
            Generation.Gen5,
            DataSymbolResolutionPolicy.GuestAuthoritative,
            ResolveProgName),
        new(
            "libc",
            LibcNeedFlagNid,
            "Need_sceLibc",
            Generation.Gen5,
            DataSymbolResolutionPolicy.GuestAuthoritative,
            ResolveLibcNeedFlag),
        new(
            "libSceLibcInternal",
            LibcInternalNeedFlagNid,
            "Need_sceLibcInternal",
            Generation.Gen5,
            DataSymbolResolutionPolicy.GuestAuthoritative,
            ResolveLibcInternalNeedFlag),
        new(
            "libc",
            StderrNid,
            "_Stderr",
            Generation.Gen5,
            DataSymbolResolutionPolicy.GuestAuthoritative),
        new(
            "libc",
            StdoutNid,
            "_Stdout",
            Generation.Gen5,
            DataSymbolResolutionPolicy.GuestAuthoritative),
    ];

    public static IReadOnlyList<DataSymbolRegistration> CreateRegistrations(Generation registrationGeneration)
    {
        if (registrationGeneration == Generation.None)
        {
            return Array.Empty<DataSymbolRegistration>();
        }

        return Registrations
            .Where(registration => (registration.Target & registrationGeneration) != 0)
            .ToArray();
    }

    private static bool ResolveProgName(out ulong address) =>
        HleDataSymbols.TryGetAddress(ProgNameNid, out address);

    private static bool ResolveLibcNeedFlag(out ulong address) =>
        HleDataSymbols.TryGetAddress(LibcNeedFlagNid, out address);

    private static bool ResolveLibcInternalNeedFlag(out ulong address) =>
        HleDataSymbols.TryGetAddress(LibcInternalNeedFlagNid, out address);
}
