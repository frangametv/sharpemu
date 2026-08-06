// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelStrtokCompatExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong InputAddress = MemoryBase + 0x100;
    private const ulong SavePointerAddress = MemoryBase + 0x200;
    private const ulong DelimiterAddress = MemoryBase + 0x3FE;

    [Fact]
    public void StrtokR_ReadsDelimiterThatEndsAtGuestMappingBoundary()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x400);
        memory.WriteCString(InputAddress, "char id=32");
        memory.WriteCString(DelimiterAddress, "=");
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = InputAddress;
        context[CpuRegister.Rsi] = DelimiterAddress;
        context[CpuRegister.Rdx] = SavePointerAddress;

        Assert.Equal(0, KernelRuntimeCompatExports.LibcStrtokReentrant(context));
        Assert.Equal(InputAddress, context[CpuRegister.Rax]);
        Assert.Equal("char id", ReadCString(memory, InputAddress, 16));
        Assert.True(context.TryReadUInt64(SavePointerAddress, out var nextToken));
        Assert.Equal(InputAddress + 8, nextToken);

        context[CpuRegister.Rdi] = 0;
        Assert.Equal(0, KernelRuntimeCompatExports.LibcStrtokReentrant(context));
        Assert.Equal(InputAddress + 8, context[CpuRegister.Rax]);
        Assert.Equal("32", ReadCString(memory, InputAddress + 8, 8));
        Assert.True(context.TryReadUInt64(SavePointerAddress, out var endCursor));
        Assert.Equal(InputAddress + 10, endCursor);

        context[CpuRegister.Rdi] = 0;
        Assert.Equal(0, KernelRuntimeCompatExports.LibcStrtokReentrant(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(SavePointerAddress, out endCursor));
        Assert.Equal(InputAddress + 10, endCursor);
    }

    private static string ReadCString(ICpuMemory memory, ulong address, int maxBytes)
    {
        Span<byte> bytes = stackalloc byte[maxBytes];
        Assert.True(memory.TryRead(address, bytes));
        var terminator = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(bytes[..(terminator < 0 ? bytes.Length : terminator)]);
    }
}
