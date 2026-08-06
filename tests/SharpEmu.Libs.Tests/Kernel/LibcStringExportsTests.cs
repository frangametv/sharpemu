// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class LibcStringExportsTests
{
    private const ulong MemoryBase = 0x3_0000_0000;

    public static IEnumerable<object[]> ExportCases()
    {
        yield return new object[] { "q0F6yS-rCms", "strcspn" };
        yield return new object[] { "-kU6bB4M-+k", "strspn" };
    }

    [Theory]
    [MemberData(nameof(ExportCases))]
    public void Exports_RegisterAsGen5Libc(string nid, string name)
    {
        var manager = CreateManager();

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(nid, export.Nid);
        Assert.Equal(name, export.Name);
        Assert.Equal("libc", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    [Fact]
    public void StringSpansUseRawHighByteMembership()
    {
        var (memory, context) = CreateContext();
        var source = MemoryBase + 0x20;
        var membership = MemoryBase + 0x40;
        Write(memory, source, 0x41, 0xE9, 0x42, 0x00);
        Write(memory, membership, 0xE9, 0x00);
        context[CpuRegister.Rdi] = source;
        context[CpuRegister.Rsi] = membership;

        Assert.Equal(
            OrbisGen2Result.ORBIS_GEN2_OK,
            Dispatch("q0F6yS-rCms", "strcspn", context));
        Assert.Equal(1UL, context[CpuRegister.Rax]);

        (memory, context) = CreateContext();
        source = MemoryBase + 0x20;
        membership = MemoryBase + 0x40;
        Write(memory, source, 0xFF, 0xFF, 0x78, 0x00);
        Write(memory, membership, 0xFF, 0x00);
        context[CpuRegister.Rdi] = source;
        context[CpuRegister.Rsi] = membership;

        Assert.Equal(
            OrbisGen2Result.ORBIS_GEN2_OK,
            Dispatch("-kU6bB4M-+k", "strspn", context));
        Assert.Equal(2UL, context[CpuRegister.Rax]);
    }

    [Theory]
    [MemberData(nameof(ExportCases))]
    public void EmptySourceReturnsZeroBeforeSecondPointerRead(string nid, string name)
    {
        var (memory, context) = CreateContext();
        var source = MemoryBase + 0x20;
        Write(memory, source, 0x00);
        context[CpuRegister.Rdi] = source;
        context[CpuRegister.Rsi] = 0xDEAD_BEEF;

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Dispatch(nid, name, context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void UnterminatedSourceIsMemoryFaultNotTerminator()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x40);
        var context = new CpuContext(memory, Generation.Gen5);
        var source = MemoryBase + 0x3E;
        var reject = MemoryBase + 0x10;
        Write(memory, source, 0x61, 0x62);
        Write(memory, reject, 0x78, 0x00);
        context[CpuRegister.Rdi] = source;
        context[CpuRegister.Rsi] = reject;

        Assert.Equal(
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            Dispatch("q0F6yS-rCms", "strcspn", context));
    }

    [Fact]
    public void UnterminatedMembershipStringIsMemoryFault()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x40);
        var context = new CpuContext(memory, Generation.Gen5);
        var source = MemoryBase + 0x10;
        var accept = MemoryBase + 0x3E;
        Write(memory, source, 0x61, 0x62, 0x00);
        Write(memory, accept, 0x61, 0x62);
        context[CpuRegister.Rdi] = source;
        context[CpuRegister.Rsi] = accept;

        Assert.Equal(
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            Dispatch("-kU6bB4M-+k", "strspn", context));
    }

    [Theory]
    [MemberData(nameof(ExportCases))]
    public void UnmappedSourceIsMemoryFault(string nid, string name)
    {
        var (_, context) = CreateContext();
        context[CpuRegister.Rdi] = 0xDEAD_BEEF;
        context[CpuRegister.Rsi] = MemoryBase;

        Assert.Equal(
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            Dispatch(nid, name, context));
    }

    private static ModuleManager CreateManager()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        return manager;
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateContext()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x100);
        return (memory, new CpuContext(memory, Generation.Gen5));
    }

    private static OrbisGen2Result Dispatch(string nid, string name, CpuContext context)
    {
        var manager = CreateManager();
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libc", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
        Assert.True(manager.TryDispatch(nid, context, out var result));
        return result;
    }

    private static void Write(FakeCpuMemory memory, ulong address, params byte[] bytes) =>
        Assert.True(memory.TryWrite(address, bytes));
}
