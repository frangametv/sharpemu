// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpCppWebApiLleExportsTests
{
    private const string LegacyInitializeNid = "UYPxv8MIzGo";

    [Fact]
    public void GtaProviderCatalog_RegistersAll436ExactGen5NidsAsLlePreferred()
    {
        var gen5 = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5)
            .Where(export => export.LibraryName == "libSceNpCppWebApi")
            .ToArray();
        var catalog = gen5.Where(export => export.Nid != LegacyInitializeNid).ToArray();

        Assert.Equal(437, gen5.Length);
        Assert.Equal(436, catalog.Length);
        Assert.Equal(436, catalog.Select(export => export.Nid).Distinct().Count());
        Assert.All(catalog, export =>
        {
            Assert.Equal(Generation.Gen5, export.Target);
            Assert.True(export.PreferLle);
        });

        AssertExport(
            catalog,
            "+6Xo+7GdUGM",
            "_ZNK3sce2Np9CppWebApi30CommunicationRestrictionStatus2V338CommunicationRestrictionStatusResponse13getRestrictedEv");
        AssertExport(
            catalog,
            "PzLUwQXc7VM",
            "_ZNK3sce2Np9CppWebApi6Common12IntrusivePtrINS1_14SessionManager2V154GetUsersAccountIdPlayerSessionsInvitationsResponseBodyEEptEv");
        AssertExport(
            catalog,
            "zy9ivTre1ko",
            "_ZN3sce2Np9CppWebApi6Common12IntrusivePtrINS1_14SessionManager2V113PlayerSessionEEC1ERS7_");
    }

    [Fact]
    public void GtaProviderCatalog_DoesNotProjectThe436NewRegistrationsToGen4()
    {
        var gen4 = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4)
            .Where(export => export.LibraryName == "libSceNpCppWebApi")
            .ToArray();

        var legacy = Assert.Single(gen4);
        Assert.Equal(LegacyInitializeNid, legacy.Nid);
        Assert.True(legacy.PreferLle);
    }

    [Fact]
    public void MissingGuestProviderFallback_IsExplicitlyFailClosed()
    {
        var context = new CpuContext(new NullMemory(), Generation.Gen5);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED,
            NpCppWebApiLleExports.MissingGuestProvider(context));
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED),
            context[CpuRegister.Rax]);
    }

    private static void AssertExport(
        IEnumerable<ExportedFunction> exports,
        string nid,
        string name)
    {
        var export = Assert.Single(exports, candidate => candidate.Nid == nid);
        Assert.Equal(name, export.Name);
    }

    private sealed class NullMemory : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }
}
