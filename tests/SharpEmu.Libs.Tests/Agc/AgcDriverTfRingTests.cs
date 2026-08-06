// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcDriverTfRingTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong GtaRingAddress = 0x3_11F0_0200;
    private const int InvalidDriverArgument = unchecked((int)0x8A6DFFFF);

    [Fact]
    public void SetTfRing_RegistersExactGen5AgcDriverIdentity()
    {
        var gen5 = new ModuleManager();
        gen5.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        var gen4 = new ModuleManager();
        gen4.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));

        Assert.True(gen5.TryGetExport("XlNp7jzGiPo", out var export));
        Assert.Equal("sceAgcDriverSetTFRing", export.Name);
        Assert.Equal("libSceAgcDriver", export.LibraryName);
        Assert.False(gen4.TryGetExport("XlNp7jzGiPo", out _));
    }

    [Fact]
    public void SetTfRing_GtaArgumentsClampStoreAndDoNotTouchGuestMemory()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x400);
        var before = new byte[0x400];
        Array.Fill(before, (byte)0xA5);
        Assert.True(memory.TryWrite(BaseAddress, before));
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = GtaRingAddress;
        ctx[CpuRegister.Rsi] = 0x3FFF8;
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryDispatch("XlNp7jzGiPo", ctx, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

        var after = new byte[before.Length];
        Assert.True(memory.TryRead(BaseAddress, after));
        Assert.Equal(before, after);
        Assert.True(AgcExports.TryGetDriverTfRingState(memory, out var address, out var size));
        Assert.Equal(GtaRingAddress, address);
        Assert.Equal(0x4000U, size);
    }

    [Theory]
    [InlineData(0x1234U, 0x1234U)]
    [InlineData(0x4001U, 0x4000U)]
    public void SetTfRing_ClampsBeforeAlignmentAndStoresEffectiveSize(
        uint requestedSize,
        uint expectedSize)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x100);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = GtaRingAddress;
        ctx[CpuRegister.Rsi] = requestedSize;

        Assert.Equal(0, AgcExports.DriverSetTfRing(ctx));
        Assert.True(AgcExports.TryGetDriverTfRingState(memory, out var address, out var size));
        Assert.Equal(GtaRingAddress, address);
        Assert.Equal(expectedSize, size);
    }

    [Theory]
    [InlineData(0UL, 0x4000U)]
    [InlineData(0x3_11F0_0280UL, 0x4000U)]
    [InlineData(GtaRingAddress, 0x3FFFU)]
    public void SetTfRing_InvalidNullOrAlignmentReturnsFirmwareErrorWithoutState(
        ulong ringAddress,
        uint requestedSize)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x100);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = ringAddress;
        ctx[CpuRegister.Rsi] = requestedSize;

        Assert.Equal(InvalidDriverArgument, AgcExports.DriverSetTfRing(ctx));
        Assert.Equal(unchecked((ulong)InvalidDriverArgument), ctx[CpuRegister.Rax]);
        Assert.False(AgcExports.TryGetDriverTfRingState(memory, out _, out _));
    }

    [Fact]
    public void SetTfRing_InvalidUpdatePreservesPriorSubmittedGpuState()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x100);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = GtaRingAddress;
        ctx[CpuRegister.Rsi] = 0x2000;
        Assert.Equal(0, AgcExports.DriverSetTfRing(ctx));

        ctx[CpuRegister.Rdi] = GtaRingAddress + 0x80;
        ctx[CpuRegister.Rsi] = 0x1000;
        Assert.Equal(InvalidDriverArgument, AgcExports.DriverSetTfRing(ctx));

        Assert.True(AgcExports.TryGetDriverTfRingState(memory, out var address, out var size));
        Assert.Equal(GtaRingAddress, address);
        Assert.Equal(0x2000U, size);
    }
}
