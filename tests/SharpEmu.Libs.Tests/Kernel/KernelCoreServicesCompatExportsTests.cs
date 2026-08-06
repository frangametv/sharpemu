// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection("KernelCoreServices")]
public sealed class KernelCoreServicesCompatExportsTests
{
    private const ulong BaseAddress = 0x4_1000_0000;
    private const ulong NameAddress = BaseAddress + 0x800;
    private const ulong SizeAddress = BaseAddress + 0x900;

    [Theory]
    [InlineData("mkgXxsoxWHg", "sceKernelClearVirtualRangeName")]
    [InlineData("n1-v6FgU7MQ", "sceKernelConfiguredFlexibleMemorySize")]
    public void CoreServicesExports_RegisterForGen5(string nid, string name)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libKernel", export.LibraryName);
    }

    [Fact]
    public void ClearVirtualRangeName_RemovesExistingMappedRangeName()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        KernelMemoryCompatExports.RegisterReservedVirtualRange(BaseAddress, 0x1000);
        memory.WriteCString(NameAddress, "streaming-textures");
        ctx[CpuRegister.Rdi] = BaseAddress + 0x100;
        ctx[CpuRegister.Rsi] = 0x400;
        ctx[CpuRegister.Rdx] = NameAddress;
        Assert.Equal(0, KernelMemoryCompatExports.KernelSetVirtualRangeName(ctx));
        Assert.True(KernelMemoryCompatExports.TryGetVirtualRangeNameForTests(BaseAddress, out var before));
        Assert.Equal("streaming-textures", before);

        ctx[CpuRegister.Rdx] = 0xDEAD_BEEF;
        Assert.Equal(0, KernelMemoryCompatExports.KernelClearVirtualRangeName(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.False(KernelMemoryCompatExports.TryGetVirtualRangeNameForTests(BaseAddress, out _));
    }

    [Fact]
    public void ClearVirtualRangeName_InvalidRangeDoesNotRemoveName()
    {
        const ulong regionAddress = BaseAddress + 0x1000;
        var memory = new FakeCpuMemory(BaseAddress, 0x3000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        KernelMemoryCompatExports.RegisterReservedVirtualRange(regionAddress, 0x800);
        memory.WriteCString(NameAddress, "persistent");
        ctx[CpuRegister.Rdi] = regionAddress;
        ctx[CpuRegister.Rsi] = 0x100;
        ctx[CpuRegister.Rdx] = NameAddress;
        Assert.Equal(0, KernelMemoryCompatExports.KernelSetVirtualRangeName(ctx));

        ctx[CpuRegister.Rsi] = 0x1000;
        Assert.Equal(
            unchecked((int)0x8002000C),
            KernelMemoryCompatExports.KernelClearVirtualRangeName(ctx));
        Assert.True(KernelMemoryCompatExports.TryGetVirtualRangeNameForTests(regionAddress, out var name));
        Assert.Equal("persistent", name);
    }

    [Theory]
    [InlineData(0UL, 0x100UL, 0x80020016U)]
    [InlineData(BaseAddress, 0UL, 0x80020016U)]
    [InlineData(0xFFFF_FFFF_FFFF_FFF0UL, 0x20UL, 0x80020054U)]
    [InlineData(0x5_0000_0000UL, 0x100UL, 0x8002000CU)]
    public void ClearVirtualRangeName_ConvertsModeledErrnoExactly(
        ulong address,
        ulong length,
        uint expectedResult)
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x2000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = address;
        ctx[CpuRegister.Rsi] = length;

        Assert.Equal(
            unchecked((int)expectedResult),
            KernelMemoryCompatExports.KernelClearVirtualRangeName(ctx));
        Assert.Equal(unchecked((ulong)(int)expectedResult), ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void ConfiguredFlexibleMemorySize_WritesDeterministicGuestBudget()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = SizeAddress;

        Assert.Equal(0, KernelMemoryCompatExports.KernelConfiguredFlexibleMemorySize(ctx));
        Assert.True(ctx.TryReadUInt64(SizeAddress, out var size));
        Assert.Equal(448UL * 1024 * 1024, size);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void ConfiguredFlexibleMemorySize_UnwritableOutputReturnsFaultWithoutOtherWrites()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        const ulong sentinel = 0x1122_3344_5566_7788;
        Assert.True(ctx.TryWriteUInt64(SizeAddress, sentinel));
        ctx[CpuRegister.Rdi] = 0;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.KernelConfiguredFlexibleMemorySize(ctx));
        Assert.True(ctx.TryReadUInt64(SizeAddress, out var unchanged));
        Assert.Equal(sentinel, unchanged);
    }
}
