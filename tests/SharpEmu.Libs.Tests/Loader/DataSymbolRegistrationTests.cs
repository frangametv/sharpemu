// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.Core.Runtime;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class DataSymbolRegistrationTests
{
    private static readonly IReadOnlyDictionary<string, (string Name, string Library, bool HasFallback)> Expected =
        new Dictionary<string, (string, string, bool)>(StringComparer.Ordinal)
        {
            ["djxxOmW6-aw"] = ("__progname", "libkernel", true),
            ["P330P3dFF68"] = ("Need_sceLibc", "libc", true),
            ["ZT4ODD2Ts9o"] = ("Need_sceLibcInternal", "libSceLibcInternal", true),
            ["H8AprKeZtNg"] = ("_Stderr", "libc", false),
            ["2sWzhYqFH4E"] = ("_Stdout", "libc", false),
        };

    [Fact]
    public void Gen5Registry_ContainsExactlyFiveExpectedDataSymbols()
    {
        var registrations = DataSymbolRegistry.CreateRegistrations(Generation.Gen5);

        Assert.Equal(Expected.Count, registrations.Count);
        Assert.Equal(Expected.Count, registrations.Select(registration => registration.Nid).Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(DataSymbolRegistry.CreateRegistrations(Generation.Gen4));
        foreach (var registration in registrations)
        {
            Assert.True(Expected.TryGetValue(registration.Nid, out var expected));
            Assert.Equal(expected.Name, registration.Name);
            Assert.Equal(expected.Library, registration.LogicalLibraryName);
            Assert.Equal(expected.HasFallback, registration.HasHleFallback);
            Assert.Equal(Generation.Gen5, registration.Target);
            Assert.Equal(DataSymbolResolutionPolicy.GuestAuthoritative, registration.ResolutionPolicy);
        }
    }

    [Fact]
    public void DataSymbols_AreAbsentFromCallableRegistryAndDispatchTables()
    {
        var callable = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5);
        var manager = new ModuleManager();
        manager.RegisterExports(callable);
        manager.RegisterDataSymbols(DataSymbolRegistry.CreateRegistrations(Generation.Gen5));

        foreach (var nid in Expected.Keys)
        {
            Assert.DoesNotContain(callable, export => string.Equals(export.Nid, nid, StringComparison.Ordinal));
            Assert.True(manager.TryGetDataSymbol(nid, out var registration));
            Assert.Equal(nid, registration.Nid);
            Assert.False(manager.TryGetExport(nid, out _));
            Assert.False(manager.TryGetFunction(nid, out _));
            Assert.False(DirectExecutionBackend.IsCallableImportNid(manager, nid));
        }
    }

    [Fact]
    public void RuntimeObjectDefinitions_AreExcludedFromCallableSymbols()
    {
        var callableSymbols = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var dataSymbols = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var importStubs = new Dictionary<ulong, string>();

        Assert.True(SelfLoader.RegisterRuntimeSymbol(
            callableSymbols,
            dataSymbols,
            importStubs,
            "kernel_dynlib_dlsym",
            0x2000_0000,
            isData: true));
        Assert.True(SelfLoader.RegisterRuntimeSymbol(
            callableSymbols,
            dataSymbols,
            importStubs,
            "function_definition",
            0x3000_0000,
            isData: false));

        Assert.DoesNotContain("kernel_dynlib_dlsym", callableSymbols.Keys);
        Assert.Equal(0x2000_0000UL, dataSymbols["kernel_dynlib_dlsym"]);
        Assert.Equal(0x3000_0000UL, callableSymbols["function_definition"]);
        Assert.DoesNotContain("function_definition", dataSymbols.Keys);
        Assert.Empty(importStubs);
    }

    [Fact]
    public void ModuleManager_RejectsFunctionDataKindConflictsInEitherOrder()
    {
        var data = Assert.Single(
            DataSymbolRegistry.CreateRegistrations(Generation.Gen5),
            registration => registration.Nid == DataSymbolRegistry.ProgNameNid);
        var function = new ExportedFunction(
            data.LogicalLibraryName,
            data.Nid,
            data.Name,
            Generation.Gen5,
            static _ => 0);

        var dataFirst = new ModuleManager();
        dataFirst.RegisterDataSymbols([data]);
        Assert.Throws<InvalidOperationException>(() => dataFirst.RegisterExports([function]));

        var functionFirst = new ModuleManager();
        functionFirst.RegisterExports([function]);
        Assert.Throws<InvalidOperationException>(() => functionFirst.RegisterDataSymbols([data]));
    }

    [Fact]
    public void ImportStubPolicy_NeverCreatesDataStubAndRejectsMixedKinds()
    {
        Assert.False(SelfLoader.EvaluateImportStubPolicy(
            "data", hasDataImport: true, hasFunctionImport: false,
            hasRequiredFunctionImport: true, hasRegisteredFunction: true));
        Assert.True(SelfLoader.EvaluateImportStubPolicy(
            "function", hasDataImport: false, hasFunctionImport: true,
            hasRequiredFunctionImport: true, hasRegisteredFunction: false));
        Assert.False(SelfLoader.EvaluateImportStubPolicy(
            "weak-function", hasDataImport: false, hasFunctionImport: true,
            hasRequiredFunctionImport: false, hasRegisteredFunction: false));
        Assert.True(SelfLoader.EvaluateImportStubPolicy(
            "weak-hle-function", hasDataImport: false, hasFunctionImport: true,
            hasRequiredFunctionImport: false, hasRegisteredFunction: true));
        Assert.Throws<InvalidDataException>(() => SelfLoader.EvaluateImportStubPolicy(
            "conflict", hasDataImport: true, hasFunctionImport: true,
            hasRequiredFunctionImport: true, hasRegisteredFunction: false));
    }

    [Fact]
    public void HleFallbackMerge_PreservesGuestDataAndDoesNotFabricateStreams()
    {
        const ulong guestNeedLibcAddress = 0x2345_6000;
        var symbols = new Dictionary<string, ulong>(StringComparer.Ordinal)
        {
            [DataSymbolRegistry.LibcNeedFlagNid] = guestNeedLibcAddress,
        };

        var added = SharpEmuRuntime.MergeRegisteredHleDataSymbols(symbols, Generation.Gen5);

        Assert.Equal(2, added);
        Assert.Equal(guestNeedLibcAddress, symbols[DataSymbolRegistry.LibcNeedFlagNid]);
        Assert.True(symbols.ContainsKey(DataSymbolRegistry.ProgNameNid));
        Assert.True(symbols.ContainsKey(DataSymbolRegistry.LibcInternalNeedFlagNid));
        Assert.False(symbols.ContainsKey(DataSymbolRegistry.StderrNid));
        Assert.False(symbols.ContainsKey(DataSymbolRegistry.StdoutNid));
    }

    [Fact]
    public void ModuleDlsymSymbols_AreUnionOfCallableAndDataDefinitions()
    {
        var image = new SelfImage(
            isSelf: false,
            elfHeader: default,
            programHeaders: Array.Empty<ProgramHeader>(),
            mappedRegions: Array.Empty<VirtualMemoryRegion>(),
            runtimeSymbols: new Dictionary<string, ulong>(StringComparer.Ordinal)
            {
                ["callable"] = 0x2000_0000,
            },
            runtimeDataSymbols: new Dictionary<string, ulong>(StringComparer.Ordinal)
            {
                ["object"] = 0x3000_0000,
            });

        var dlsymSymbols = SharpEmuRuntime.CreateModuleDlsymSymbols(image);

        Assert.Equal(0x2000_0000UL, dlsymSymbols["callable"]);
        Assert.Equal(0x3000_0000UL, dlsymSymbols["object"]);
        Assert.DoesNotContain("object", image.RuntimeSymbols.Keys);
    }

    [Theory]
    [InlineData("__progname", DataSymbolRegistry.ProgNameNid)]
    [InlineData("Need_sceLibc", DataSymbolRegistry.LibcNeedFlagNid)]
    [InlineData("Need_sceLibcInternal", DataSymbolRegistry.LibcInternalNeedFlagNid)]
    public void HleDataFallbacks_AreDlsymVisibleButNotCallable(string symbolName, string nid)
    {
        var callableSymbols = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var dataSymbols = new Dictionary<string, ulong>(StringComparer.Ordinal);
        Assert.Equal(3, SharpEmuRuntime.MergeRegisteredHleDataSymbols(dataSymbols, Generation.Gen5));

        Assert.True(DirectExecutionBackend.TryResolveGlobalDlsymSymbolAddress(
            callableSymbols,
            dataSymbols,
            symbolName,
            out var dlsymAddress));
        Assert.Equal(dataSymbols[nid], dlsymAddress);
        Assert.False(DirectExecutionBackend.TryResolveCallableRuntimeSymbolAddress(
            callableSymbols,
            nid,
            out _));
    }

    [Fact]
    public void HleFallbacks_HaveExpectedPointerAndFlagTopology()
    {
        const string imageName = "gta-v-data5-test-eboot.bin";
        HleDataSymbols.ConfigureProcessImageName(imageName);
        try
        {
            var registrations = DataSymbolRegistry.CreateRegistrations(Generation.Gen5);
            var progName = Assert.Single(registrations, registration => registration.Nid == DataSymbolRegistry.ProgNameNid);
            var libcNeed = Assert.Single(registrations, registration => registration.Nid == DataSymbolRegistry.LibcNeedFlagNid);
            var libcInternalNeed = Assert.Single(registrations, registration => registration.Nid == DataSymbolRegistry.LibcInternalNeedFlagNid);
            var stderr = Assert.Single(registrations, registration => registration.Nid == DataSymbolRegistry.StderrNid);
            var stdout = Assert.Single(registrations, registration => registration.Nid == DataSymbolRegistry.StdoutNid);

            Assert.True(progName.TryGetHleAddress(out var progNameCell));
            var bufferAddress = unchecked((ulong)Marshal.ReadInt64((nint)progNameCell));
            Assert.NotEqual(0UL, bufferAddress);
            Assert.Equal(imageName, Marshal.PtrToStringUTF8((nint)bufferAddress));

            Assert.True(libcNeed.TryGetHleAddress(out var libcNeedAddress));
            Assert.True(libcInternalNeed.TryGetHleAddress(out var libcInternalNeedAddress));
            Assert.Equal(1, Marshal.ReadInt32((nint)libcNeedAddress));
            Assert.Equal(1, Marshal.ReadInt32((nint)libcInternalNeedAddress));
            Assert.False(stderr.TryGetHleAddress(out var stderrAddress));
            Assert.False(stdout.TryGetHleAddress(out var stdoutAddress));
            Assert.Equal(0UL, stderrAddress);
            Assert.Equal(0UL, stdoutAddress);
        }
        finally
        {
            HleDataSymbols.ConfigureProcessImageName("eboot.bin");
        }
    }

    [Fact]
    public void Rebinder_AppliesPositiveAndNegativeAddends()
    {
        var memory = CreateWritableMemory();
        var image = CreateImage(
            new ImportedSymbolRelocation(0x10010, 0x28, "positive", IsData: true),
            new ImportedSymbolRelocation(0x10018, -0x18, "negative", IsData: true));
        var symbols = new Dictionary<string, ulong>(StringComparer.Ordinal)
        {
            ["positive"] = 0x3000_0000,
            ["negative"] = 0x4000_0000,
        };

        Assert.Equal(2, ImportedDataRebinder.Rebind(memory, image, "eboot.bin", symbols));
        Assert.Equal(0x3000_0028UL, ReadUInt64(memory, 0x10010));
        Assert.Equal(0x3FFF_FFE8UL, ReadUInt64(memory, 0x10018));
    }

    [Fact]
    public void RebindMissing_WeakUsesZeroSymbolValueAndStrongFailsClosed()
    {
        var memory = CreateWritableMemory();
        var weak = CreateImage(
            new ImportedSymbolRelocation(
                0x10020,
                0x28,
                "weak-positive",
                IsData: true,
                IsWeak: true),
            new ImportedSymbolRelocation(
                0x10028,
                -0x18,
                "weak-negative",
                IsData: true,
                IsWeak: true));
        var strong = CreateImage(new ImportedSymbolRelocation(
            0x10030,
            0,
            DataSymbolRegistry.StdoutNid,
            IsData: true,
            IsWeak: false));

        Assert.Equal(0, ImportedDataRebinder.Rebind(
            memory,
            weak,
            "weak.prx",
            new Dictionary<string, ulong>()));
        Assert.Equal(0x28UL, ReadUInt64(memory, 0x10020));
        Assert.Equal(0xFFFF_FFFF_FFFF_FFE8UL, ReadUInt64(memory, 0x10028));
        var exception = Assert.Throws<InvalidDataException>(() => ImportedDataRebinder.Rebind(
            memory,
            strong,
            "eboot.bin",
            new Dictionary<string, ulong>()));
        Assert.Contains(DataSymbolRegistry.StdoutNid, exception.Message, StringComparison.Ordinal);
        Assert.Contains("eboot.bin", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RebindWriteFailure_IsFatal()
    {
        var image = CreateImage(new ImportedSymbolRelocation(
            0x5000_0000,
            0,
            DataSymbolRegistry.StderrNid,
            IsData: true));
        var symbols = new Dictionary<string, ulong>(StringComparer.Ordinal)
        {
            [DataSymbolRegistry.StderrNid] = 0x3000_0000,
        };

        var exception = Assert.Throws<InvalidDataException>(() => ImportedDataRebinder.Rebind(
            CreateWritableMemory(),
            image,
            "eboot.bin",
            symbols));
        Assert.Contains("Failed to write", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0xFFFEUL)]
    [InlineData(0xFFFF_FFFEUL)]
    [InlineData(0xFFFF_FFFF_FFFF_FFFEUL)]
    public void RebindUnresolvedSentinel_StrongImportFailsClosed(ulong sentinel)
    {
        var image = CreateImage(new ImportedSymbolRelocation(
            0x10030,
            0,
            DataSymbolRegistry.StderrNid,
            IsData: true));
        var symbols = new Dictionary<string, ulong>(StringComparer.Ordinal)
        {
            [DataSymbolRegistry.StderrNid] = sentinel,
        };

        Assert.Throws<InvalidDataException>(() => ImportedDataRebinder.Rebind(
            CreateWritableMemory(),
            image,
            "eboot.bin",
            symbols));
    }

    private static VirtualMemory CreateWritableMemory()
    {
        var memory = new VirtualMemory();
        memory.Map(
            0x10000,
            0x100,
            0,
            ReadOnlySpan<byte>.Empty,
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        return memory;
    }

    private static SelfImage CreateImage(params ImportedSymbolRelocation[] relocations) =>
        new(
            isSelf: false,
            elfHeader: default,
            programHeaders: Array.Empty<ProgramHeader>(),
            mappedRegions: Array.Empty<VirtualMemoryRegion>(),
            importedRelocations: relocations);

    private static ulong ReadUInt64(VirtualMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
