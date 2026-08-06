// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests.SaveData;

[CollectionDefinition("SaveDataDialogState", DisableParallelization = true)]
public sealed class SaveDataDialogStateCollection;

[Collection("SaveDataDialogState")]
public sealed class SaveDataDialogExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong ParamAddress = Base + 0x100;

    [Fact]
    public void FinishedCloseAndDuplicateTerminate_AreIdempotent()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(ParamAddress, new byte[0xD0]));

        Assert.Equal(0, SaveDataDialogExports.SaveDataDialogTerminate(ctx));
        Assert.Equal(0, SaveDataDialogExports.SaveDataDialogInitialize(ctx));

        ctx[CpuRegister.Rdi] = ParamAddress;
        Assert.Equal(0, SaveDataDialogExports.SaveDataDialogOpen(ctx));
        Assert.Equal(3, SaveDataDialogExports.SaveDataDialogUpdateStatus(ctx));
        Assert.Equal(0, SaveDataDialogExports.SaveDataDialogClose(ctx));
        Assert.Equal(0, SaveDataDialogExports.SaveDataDialogTerminate(ctx));
        Assert.Equal(0, SaveDataDialogExports.SaveDataDialogTerminate(ctx));
        Assert.Equal(0, SaveDataDialogExports.SaveDataDialogClose(ctx));
    }
}
