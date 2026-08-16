// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class DirectExecutionBackendLlePreferenceTests
{
    [Theory]
    [InlineData("ASTRO BOT", "PPSA21567")]
    [InlineData("astro bot", null)]
    [InlineData(null, "PPSA21564")]
    [InlineData(null, "PPSA21567")]
    public void AstroBotAutomaticallyUsesTheValidatedFullLleLibcPolicy(
        string? title,
        string? titleId)
    {
        Assert.True(CpuDispatcher.ShouldPreferAllLleLibcForApplication(title, titleId));
    }

    [Theory]
    [InlineData("Astro's Playroom", "PPSA01325")]
    [InlineData("Another Game", "PPSA00000")]
    [InlineData(null, null)]
    public void OtherApplicationsKeepTheDefaultMixedLibcPolicy(string? title, string? titleId)
    {
        Assert.False(CpuDispatcher.ShouldPreferAllLleLibcForApplication(
            title,
            titleId));
    }

    [Fact]
    public void ExplicitLlePreference_AllowsNonKernelRegisteredExport()
    {
        var export = Export("libSceNpCppWebApi", preferLle: true);

        Assert.True(DirectExecutionBackend.ShouldResolveRegisteredExportViaLle(
            export,
            preferLleForLibc: false));
    }

    [Fact]
    public void ExplicitLlePreference_CannotOverrideKernelHleBoundary()
    {
        var export = Export("libKernel", preferLle: true);

        Assert.False(DirectExecutionBackend.ShouldResolveRegisteredExportViaLle(
            export,
            preferLleForLibc: true));
    }

    [Fact]
    public void RegisteredExportWithoutLlePreference_RemainsHle()
    {
        var export = Export("libSceNpCppWebApi", preferLle: false);

        Assert.False(DirectExecutionBackend.ShouldResolveRegisteredExportViaLle(
            export,
            preferLleForLibc: false));
    }

    [Fact]
    public void ExistingLibcPolicy_CanStillSelectRegisteredFirmwareExport()
    {
        var export = Export("libSceLibcInternal", preferLle: false);

        Assert.True(DirectExecutionBackend.ShouldResolveRegisteredExportViaLle(
            export,
            preferLleForLibc: true));
    }

    [Theory]
    [InlineData("_Getptolower")]
    [InlineData("_ZNSt6locale5_InitEv")]
    [InlineData("_ZNSt6locale16_GetgloballocaleEv")]
    [InlineData("sceLibcMspaceCreate")]
    [InlineData("sceLibcMspaceDestroy")]
    [InlineData("sceLibcMspaceMalloc")]
    [InlineData("sceLibcMspaceMemalign")]
    [InlineData("sceLibcMspaceMallocStatsFast")]
    [InlineData("qsort")]
    public void FirmwareOwnedLibcStateAndCallbacks_PreferMappedLle(string exportName)
    {
        Assert.True(DirectExecutionBackend.IsSafeLleLibcExport(exportName));
    }

    [Theory]
    [InlineData("_ZNSt6locale5_InitEb")]
    [InlineData("fputs")]
    [InlineData("malloc")]
    [InlineData("free")]
    [InlineData("calloc")]
    [InlineData("realloc")]
    [InlineData("memalign")]
    [InlineData("aligned_alloc")]
    [InlineData("posix_memalign")]
    [InlineData("malloc_usable_size")]
    [InlineData("setlocale")]
    public void StatefulOrUnavailableLibcExports_DoNotEnterSafeLleSet(string exportName)
    {
        Assert.False(DirectExecutionBackend.IsSafeLleLibcExport(exportName));
    }

    private static ExportedFunction Export(string libraryName, bool preferLle) =>
        new(
            libraryName,
            "Zxa0VhQVTsk",
            "sceKernelWaitSema",
            Generation.Gen5,
            static _ => 0,
            preferLle);
}
