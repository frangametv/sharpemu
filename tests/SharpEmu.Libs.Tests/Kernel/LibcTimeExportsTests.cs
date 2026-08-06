// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class LibcTimeExportsTests
{
    private const ulong MemoryBase = 0x4_0000_0000;

    [Fact]
    public void TimeRegistersAsGen5Libc()
    {
        var manager = CreateManager();

        Assert.True(manager.TryGetExport("wLlFkwG9UcQ", out var export));
        Assert.Equal("wLlFkwG9UcQ", export.Nid);
        Assert.Equal("time", export.Name);
        Assert.Equal("libc", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    [Fact]
    public void TimeWithNullOutputReturnsCurrentUnixSeconds()
    {
        var (_, context) = CreateContext();
        context[CpuRegister.Rdi] = 0;
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Dispatch(context));

        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var actual = unchecked((long)context[CpuRegister.Rax]);
        Assert.InRange(actual, before, after);
    }

    [Fact]
    public void TimeWritesTheIdenticalSigned64BitResult()
    {
        var (memory, context) = CreateContext();
        var outputAddress = MemoryBase + 0x40;
        context[CpuRegister.Rdi] = outputAddress;
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, Dispatch(context));

        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Span<byte> output = stackalloc byte[sizeof(long)];
        Assert.True(memory.TryRead(outputAddress, output));
        var written = BinaryPrimitives.ReadInt64LittleEndian(output);
        var returned = unchecked((long)context[CpuRegister.Rax]);
        Assert.Equal(returned, written);
        Assert.InRange(returned, before, after);
    }

    [Fact]
    public void TimeWithUnmappedOutputIsMemoryFault()
    {
        var (_, context) = CreateContext();
        context[CpuRegister.Rdi] = 0xDEAD_BEEF;

        Assert.Equal(
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            Dispatch(context));
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

    private static OrbisGen2Result Dispatch(CpuContext context)
    {
        var manager = CreateManager();
        Assert.True(manager.TryGetExport("wLlFkwG9UcQ", out var export));
        Assert.Equal("time", export.Name);
        Assert.Equal("libc", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
        Assert.True(manager.TryDispatch("wLlFkwG9UcQ", context, out var result));
        return result;
    }
}
