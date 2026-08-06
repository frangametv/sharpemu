// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelStrtoCompatExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong InputAddress = MemoryBase + 0x100;
    private const ulong EndPointerAddress = MemoryBase + 0x200;

    [Fact]
    public void Strtoll_RegistersAndStopsAtFirstNonDigit()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        memory.WriteCString(InputAddress, "123.75");
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = InputAddress;
        context[CpuRegister.Rsi] = EndPointerAddress;
        context[CpuRegister.Rdx] = 10;

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("VOBg+iNwB-4", out var export));
        Assert.Equal("strtoll", export.Name);
        Assert.True(manager.TryDispatch("VOBg+iNwB-4", context, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(123UL, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(EndPointerAddress, out var endPointer));
        Assert.Equal(InputAddress + 3, endPointer);
    }

    [Fact]
    public void Strtoll_NegativeValueUsesSigned64BitResult()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        memory.WriteCString(InputAddress, "-42e2");
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = InputAddress;
        context[CpuRegister.Rsi] = EndPointerAddress;
        context[CpuRegister.Rdx] = 10;

        var result = KernelRuntimeCompatExports.LibcStrtoll(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(unchecked((ulong)-42L), context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(EndPointerAddress, out var endPointer));
        Assert.Equal(InputAddress + 3, endPointer);
    }
}
