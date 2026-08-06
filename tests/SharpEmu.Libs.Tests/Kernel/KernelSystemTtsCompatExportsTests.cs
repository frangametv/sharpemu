// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelSystemTtsCompatExportsTests
{
    [Fact]
    public void CallbackExports_RegisterByFirmwareNidAndReturnSuccess()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x1234;
        context[CpuRegister.Rsi] = 0x5678;

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("up9Z19akYXM", out var register));
        Assert.Equal("sceSystemTtsRegisterCallback", register.Name);
        Assert.True(manager.TryDispatch("up9Z19akYXM", context, out var registerResult));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, registerResult);
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        Assert.True(manager.TryGetExport("a05rlp573ow", out var unregister));
        Assert.Equal("sceSystemTtsUnregisterCallback", unregister.Name);
        Assert.True(manager.TryDispatch("a05rlp573ow", context, out var unregisterResult));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, unregisterResult);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }
}
