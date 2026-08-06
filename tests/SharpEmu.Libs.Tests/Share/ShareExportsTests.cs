// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Share;
using Xunit;

namespace SharpEmu.Libs.Tests.Share;

[CollectionDefinition("ShareState", DisableParallelization = true)]
public sealed class ShareStateCollection;

[Collection("ShareState")]
public sealed class ShareExportsTests
{
    private const ulong BaseAddress = 0x5_0000_0000;
    private const ulong Callback = 0x8_0012_3456;
    private const ulong UserData = BaseAddress + 0x800;

    private readonly CpuContext _ctx = new(
        new FakeCpuMemory(BaseAddress, 0x1000),
        Generation.Gen5);

    public ShareExportsTests()
    {
        ShareExports.ResetForTests();
    }

    [Fact]
    public void RegisterContentEventCallback_RegistersOnlyForGen5LibSceShare()
    {
        var gen5Manager = new ModuleManager();
        gen5Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        var gen4Manager = new ModuleManager();
        gen4Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));

        Assert.True(gen5Manager.TryGetExport("Sygnk9dr5WQ", out var export));
        Assert.Equal("sceShareRegisterContentEventCallback", export.Name);
        Assert.Equal("libSceShare", export.LibraryName);
        Assert.False(gen4Manager.TryGetExport("Sygnk9dr5WQ", out _));
        Assert.True(gen5Manager.TryGetExport("0IL1keINExQ", out var terminate));
        Assert.Equal("sceShareTerminate", terminate.Name);
        Assert.Equal("libSceShare", terminate.LibraryName);
        Assert.False(gen4Manager.TryGetExport("0IL1keINExQ", out _));
        Assert.True(gen5Manager.TryGetExport("YBiIdcDPrxs", out var permit));
        Assert.Equal("sceShareFeaturePermit", permit.Name);
        Assert.Equal("libSceShare", permit.LibraryName);
        Assert.False(gen4Manager.TryGetExport("YBiIdcDPrxs", out _));
    }

    [Fact]
    public void FeaturePermit_RequiresInitializationAndTracksProviderSelector()
    {
        const int feature = 0x10;
        _ctx[CpuRegister.Rdi] = feature;

        Assert.Equal(unchecked((int)0x8196000C), ShareExports.ShareFeaturePermit(_ctx));
        Assert.Equal(0x8196000CUL, _ctx[CpuRegister.Rax]);
        Assert.False(ShareExports.IsFeaturePermittedForTests(feature));

        Initialize();
        _ctx[CpuRegister.Rdi] = feature;
        Assert.Equal(0, ShareExports.ShareFeaturePermit(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.True(ShareExports.IsFeaturePermittedForTests(feature));

        Assert.Equal(0, ShareExports.ShareTerminate(_ctx));
        Assert.False(ShareExports.IsFeaturePermittedForTests(feature));
    }

    [Fact]
    public void RegisterContentEventCallback_NullCallbackWinsBeforeLifecycleChecks()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = UserData;

        AssertShareResult(unchecked((int)0x81960002));
        Assert.Equal(0, ShareExports.ContentEventCallbackCountForTests);
    }

    [Fact]
    public void RegisterContentEventCallback_RequiresInitialization()
    {
        _ctx[CpuRegister.Rdi] = Callback;
        _ctx[CpuRegister.Rsi] = UserData;

        AssertShareResult(unchecked((int)0x8196000C));
        Assert.Equal(0, ShareExports.ContentEventCallbackCountForTests);
    }

    [Fact]
    public void RegisterContentEventCallback_RequiresAvailableContentEventService()
    {
        Initialize();
        ShareExports.SetContentEventServiceAvailableForTests(false);
        _ctx[CpuRegister.Rdi] = Callback;
        _ctx[CpuRegister.Rsi] = UserData;

        AssertShareResult(unchecked((int)0x81960001));
        Assert.Equal(0, ShareExports.ContentEventCallbackCountForTests);
    }

    [Fact]
    public void RegisterContentEventCallback_DispatchesAndStoresUnownedTargets()
    {
        Initialize();
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        _ctx[CpuRegister.Rdi] = Callback;
        _ctx[CpuRegister.Rsi] = 0;

        Assert.True(manager.TryDispatch("Sygnk9dr5WQ", _ctx, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.True(ShareExports.TryGetContentEventCallbackForTests(Callback, out var userData));
        Assert.Equal(0UL, userData);
        Assert.Equal(1, ShareExports.ContentEventCallbackCountForTests);
    }

    [Fact]
    public void RegisterContentEventCallback_DuplicateIsKeyedOnlyByCallback()
    {
        Initialize();
        _ctx[CpuRegister.Rdi] = Callback;
        _ctx[CpuRegister.Rsi] = UserData;
        AssertShareResult(0);

        _ctx[CpuRegister.Rsi] = UserData + 0x100;
        AssertShareResult(unchecked((int)0x81960002));
        Assert.True(ShareExports.TryGetContentEventCallbackForTests(Callback, out var retainedUserData));
        Assert.Equal(UserData, retainedUserData);
        Assert.Equal(1, ShareExports.ContentEventCallbackCountForTests);
    }

    [Fact]
    public void Terminate_WhenUninitializedReturnsDirectLifecycleError()
    {
        Assert.Equal(unchecked((int)0x8196000C), ShareExports.ShareTerminate(_ctx));
        Assert.Equal(0x8196000CUL, _ctx[CpuRegister.Rax]);
        Assert.Equal(0, ShareExports.ContentEventCallbackCountForTests);
    }

    [Fact]
    public void Terminate_ClearsRegistrationAndAllowsCleanReinitialize()
    {
        Initialize();
        _ctx[CpuRegister.Rdi] = Callback;
        _ctx[CpuRegister.Rsi] = UserData;
        AssertShareResult(0);

        Assert.Equal(0, ShareExports.ShareTerminate(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(0, ShareExports.ContentEventCallbackCountForTests);
        Assert.False(ShareExports.TryGetContentEventCallbackForTests(Callback, out _));

        AssertShareResult(unchecked((int)0x8196000C));
        Initialize();
        Assert.Equal(0, ShareExports.ContentEventCallbackCountForTests);
        _ctx[CpuRegister.Rdi] = Callback;
        _ctx[CpuRegister.Rsi] = UserData + 0x100;
        AssertShareResult(0);
        Assert.True(ShareExports.TryGetContentEventCallbackForTests(Callback, out var userData));
        Assert.Equal(UserData + 0x100, userData);
        Assert.Equal(1, ShareExports.ContentEventCallbackCountForTests);
    }

    [Fact]
    public void ResetForTests_ModelsShutdownListTeardown()
    {
        Initialize();
        _ctx[CpuRegister.Rdi] = Callback;
        _ctx[CpuRegister.Rsi] = UserData;
        AssertShareResult(0);

        ShareExports.ResetForTests();

        Assert.Equal(0, ShareExports.ContentEventCallbackCountForTests);
        Assert.False(ShareExports.TryGetContentEventCallbackForTests(Callback, out _));
        AssertShareResult(unchecked((int)0x8196000C));
    }

    private void Initialize()
    {
        _ctx[CpuRegister.Rdi] = 0x2_0000;
        _ctx[CpuRegister.Rsi] = unchecked((ulong)-1L);
        _ctx[CpuRegister.Rdx] = 1;
        Assert.Equal(0, ShareExports.ShareInitialize(_ctx));
    }

    private void AssertShareResult(int expected)
    {
        Assert.Equal(expected, ShareExports.ShareRegisterContentEventCallback(_ctx));
        Assert.Equal(unchecked((uint)expected), _ctx[CpuRegister.Rax]);
    }
}
