// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.AppContent;
using Xunit;

namespace SharpEmu.Libs.Tests.AppContent;

public sealed class AppContentExportsTests
{
    private const ulong MemoryBase = 0x1000;
    private const int InvalidArgument = unchecked((int)0x80D90002);

    [Fact]
    public void DownloadDataGetAvailableSpaceKb_WritesExactEightByteResult()
    {
        const ulong expectedSpaceKb = 0x1122334455667788;
        var memory = new FakeCpuMemory(MemoryBase, 0x100);
        var context = new CpuContext(memory, Generation.Gen5);
        var mountAddress = MemoryBase + 0x10;
        var outputAddress = MemoryBase + 0x40;
        Span<byte> mountName = stackalloc byte[0x10];
        Encoding.ASCII.GetBytes("/download0\0", mountName);
        Assert.True(memory.TryWrite(mountAddress, mountName));
        context[CpuRegister.Rdi] = mountAddress;
        context[CpuRegister.Rsi] = outputAddress;
        AppContentExports.SetAvailableSpaceKbProviderForTests(_ => expectedSpaceKb);

        try
        {
            Assert.Equal(0, AppContentExports.AppContentDownloadDataGetAvailableSpaceKb(context));
            Span<byte> output = stackalloc byte[sizeof(ulong)];
            Assert.True(memory.TryRead(outputAddress, output));
            Assert.Equal(expectedSpaceKb, BinaryPrimitives.ReadUInt64LittleEndian(output));
            Assert.Equal(0UL, context[CpuRegister.Rax]);
        }
        finally
        {
            AppContentExports.SetAvailableSpaceKbProviderForTests(null);
        }
    }

    [Theory]
    [InlineData(0, MemoryBase + 0x40)]
    [InlineData(MemoryBase + 0x10, 0)]
    public void DownloadDataGetAvailableSpaceKb_RejectsNullPointers(
        ulong mountAddress,
        ulong outputAddress)
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x100), Generation.Gen5);
        context[CpuRegister.Rdi] = mountAddress;
        context[CpuRegister.Rsi] = outputAddress;

        Assert.Equal(InvalidArgument, AppContentExports.AppContentDownloadDataGetAvailableSpaceKb(context));
        Assert.Equal(unchecked((ulong)InvalidArgument), context[CpuRegister.Rax]);
    }
}
