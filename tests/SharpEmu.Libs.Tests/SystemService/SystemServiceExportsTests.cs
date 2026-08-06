// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.SystemService;
using Xunit;

namespace SharpEmu.Libs.Tests.SystemService;

public sealed class SystemServiceExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    public SystemServiceExportsTests() => SystemServiceExports.ResetForTests();

    [Fact]
    public void GetNoticeScreenSkipFlagWritesOneByteAtMemoryBoundary()
    {
        var memory = new FakeCpuMemory(MemoryBase, 1);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(MemoryBase, new byte[] { 0xA5 }));
        context[CpuRegister.Rdi] = MemoryBase;

        Assert.Equal(0, SystemServiceExports.SystemServiceGetNoticeScreenSkipFlag(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        Span<byte> flag = stackalloc byte[1];
        Assert.True(memory.TryRead(MemoryBase, flag));
        Assert.Equal(0, flag[0]);
    }

    [Fact]
    public void SetNoticeScreenSkipFlagTakesNoArgumentsAndUpdatesGetterState()
    {
        var memory = new FakeCpuMemory(MemoryBase, 1);
        var context = new CpuContext(memory, Generation.Gen5);
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        context[CpuRegister.Rdi] = 0xDEAD_BEEF;

        Assert.True(manager.TryDispatch("Q3utJvma4Mo", context, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        context[CpuRegister.Rdi] = MemoryBase;
        Assert.Equal(0, SystemServiceExports.SystemServiceGetNoticeScreenSkipFlag(context));
        Span<byte> flag = stackalloc byte[1];
        Assert.True(memory.TryRead(MemoryBase, flag));
        Assert.Equal(1, flag[0]);
    }

    [Fact]
    public void SetNoticeScreenSkipFlagRegistersExactGen5SemanticFallback()
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == "Q3utJvma4Mo");

        Assert.Equal("sceSystemServiceSetNoticeScreenSkipFlag", export.Name);
        Assert.Equal("libSceSystemService", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
        Assert.True(export.PreferLle);
        Assert.Equal(typeof(SystemServiceExports), export.Function.Method.DeclaringType);
    }

    [Fact]
    public void DisableNoticeScreenSkipFlagAutoSetIsNoArgumentNoOpSuccess()
    {
        var memory = new FakeCpuMemory(MemoryBase, 1);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.Equal(0, SystemServiceExports.SystemServiceSetNoticeScreenSkipFlag(context));
        context[CpuRegister.Rdi] = 0xDEAD_BEEF;

        Assert.Equal(
            0,
            SystemServiceExports.SystemServiceDisableNoticeScreenSkipFlagAutoSet(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        context[CpuRegister.Rdi] = MemoryBase;
        Assert.Equal(0, SystemServiceExports.SystemServiceGetNoticeScreenSkipFlag(context));
        Span<byte> flag = stackalloc byte[1];
        Assert.True(memory.TryRead(MemoryBase, flag));
        Assert.Equal(1, flag[0]);
    }

    [Fact]
    public void DisableNoticeScreenSkipFlagAutoSetRegistersExactGen5Fallback()
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == "8Lo6Zv94aho");

        Assert.Equal("sceSystemServiceDisableNoticeScreenSkipFlagAutoSet", export.Name);
        Assert.Equal("libSceSystemService", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
        Assert.True(export.PreferLle);
        Assert.Equal(typeof(SystemServiceExports), export.Function.Method.DeclaringType);
    }
}
