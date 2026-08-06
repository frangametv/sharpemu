// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.WebBrowserDialog;
using Xunit;

namespace SharpEmu.Libs.Tests.WebBrowserDialog;

public sealed class WebBrowserDialogExportsTests
{
    private const int WebBrowserDialogErrorNotInitialized = unchecked((int)0x80B80003);

    [Fact]
    public void TerminateWithoutHleInitializationReturnsProviderNotInitializedError()
    {
        var context = new CpuContext(new FakeCpuMemory(0x1000, 1), Generation.Gen5);
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        context[CpuRegister.Rdi] = 0xDEAD_BEEF;

        Assert.True(manager.TryDispatch("ocHtyBwHfys", context, out var result));
        Assert.Equal(unchecked((OrbisGen2Result)WebBrowserDialogErrorNotInitialized), result);
        Assert.Equal(unchecked((ulong)WebBrowserDialogErrorNotInitialized), context[CpuRegister.Rax]);
    }

    [Fact]
    public void TerminateRegistersExactGen5SemanticFallback()
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == "ocHtyBwHfys");

        Assert.Equal("sceWebBrowserDialogTerminate", export.Name);
        Assert.Equal("libSceWebBrowserDialog", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
        Assert.True(export.PreferLle);
        Assert.Equal(typeof(WebBrowserDialogExports), export.Function.Method.DeclaringType);
    }
}
