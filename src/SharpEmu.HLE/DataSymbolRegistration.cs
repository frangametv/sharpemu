// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Defines how an imported data object is resolved when both a loaded guest
/// definition and an HLE compatibility object are available.
/// </summary>
public enum DataSymbolResolutionPolicy : byte
{
    /// <summary>The loaded guest definition wins; HLE is only a fallback.</summary>
    GuestAuthoritative = 0,
}

public delegate bool DataSymbolAddressProvider(out ulong address);

/// <summary>
/// Metadata for an addressable ABI object. Data registrations are deliberately
/// separate from <see cref="ExportedFunction"/> and are never callable.
/// </summary>
public sealed class DataSymbolRegistration
{
    public DataSymbolRegistration(
        string logicalLibraryName,
        string nid,
        string name,
        Generation target,
        DataSymbolResolutionPolicy resolutionPolicy,
        DataSymbolAddressProvider? hleAddressProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalLibraryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(nid);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        LogicalLibraryName = logicalLibraryName;
        Nid = nid;
        Name = name;
        Target = target;
        ResolutionPolicy = resolutionPolicy;
        HleAddressProvider = hleAddressProvider;
    }

    public string LogicalLibraryName { get; }

    public string Nid { get; }

    public string Name { get; }

    public Generation Target { get; }

    public DataSymbolResolutionPolicy ResolutionPolicy { get; }

    public bool HasHleFallback => HleAddressProvider is not null;

    internal DataSymbolAddressProvider? HleAddressProvider { get; }

    public bool TryGetHleAddress(out ulong address)
    {
        if (HleAddressProvider is not null && HleAddressProvider(out address))
        {
            return true;
        }

        address = 0;
        return false;
    }
}
