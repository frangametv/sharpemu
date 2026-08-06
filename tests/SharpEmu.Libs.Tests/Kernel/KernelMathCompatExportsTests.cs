// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelMathCompatExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Fact]
    public void Acosf_RegistersAndReturnsSingleInXmm0()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context.SetXmmRegister(
            0,
            unchecked((uint)BitConverter.SingleToInt32Bits(0.5f)),
            ulong.MaxValue);

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("QI-x0SL8jhw", out var export));
        Assert.Equal("acosf", export.Name);
        Assert.True(manager.TryDispatch("QI-x0SL8jhw", context, out var dispatchResult));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, dispatchResult);

        context.GetXmmRegister(0, out var low, out var high);
        var result = BitConverter.Int32BitsToSingle(unchecked((int)(uint)low));
        Assert.InRange(result, (MathF.PI / 3f) - 0.00001f, (MathF.PI / 3f) + 0.00001f);
        Assert.Equal(0UL, high);
    }

    [Fact]
    public void Sincosf_RegistersAndWritesBothSingleResults()
    {
        const ulong sinAddress = MemoryBase + 0x100;
        const ulong cosAddress = MemoryBase + 0x110;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context.SetXmmRegister(
            0,
            unchecked((uint)BitConverter.SingleToInt32Bits(MathF.PI / 2f)),
            0);
        context[CpuRegister.Rdi] = sinAddress;
        context[CpuRegister.Rsi] = cosAddress;

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("pztV4AF18iI", out var export));
        Assert.Equal("sincosf", export.Name);
        Assert.True(manager.TryDispatch("pztV4AF18iI", context, out var dispatchResult));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, dispatchResult);

        Assert.InRange(ReadSingle(memory, sinAddress), 0.99999f, 1.00001f);
        Assert.InRange(ReadSingle(memory, cosAddress), -0.00001f, 0.00001f);
    }

    private static float ReadSingle(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float)];
        Assert.True(memory.TryRead(address, bytes));
        return BitConverter.ToSingle(bytes);
    }
}
