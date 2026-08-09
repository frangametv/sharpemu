// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Lle;
using Xunit;

namespace SharpEmu.Libs.Tests.Lle;

public sealed class HttpLleExportsTests
{
    private const ulong MemoryBase = 0x100_000;
    private const ulong InputAddress = MemoryBase + 0x100;
    private const ulong RequiredAddress = MemoryBase + 0x300;
    private const ulong OutputAddress = MemoryBase + 0x400;

    [Fact]
    public void RegistryUsesSemanticUriEscapeFallback()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5);
        var export = Assert.Single(exports, candidate => candidate.Nid == "YuOW3dDAKYc");

        Assert.Equal((SysAbiFunction)HttpLleExports.UriEscapeWithoutGuestProvider, export.Function);
        Assert.True(export.PreferLle);
    }

    [Fact]
    public void NullOutputQueriesRequiredUInt64Size()
    {
        var (memory, context) = CreateContext("Astro Bot/Fran~1");
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = RequiredAddress;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = InputAddress;

        AssertResult(0, context);
        Assert.Equal((ulong)"Astro%20Bot%2FFran~1".Length + 1, ReadUInt64(memory, RequiredAddress));
    }

    [Fact]
    public void SizeQueryWritesHostBackedRequiredPointer()
    {
        var (_, context) = CreateContext("Astro Bot");
        var requiredPointer = Marshal.AllocHGlobal(sizeof(long));
        try
        {
            Marshal.WriteInt64(requiredPointer, 0);
            context[CpuRegister.Rsi] = unchecked((ulong)requiredPointer.ToInt64());
            context[CpuRegister.Rcx] = InputAddress;

            AssertResult(0, context);
            Assert.Equal(12, Marshal.ReadInt64(requiredPointer));
        }
        finally
        {
            Marshal.FreeHGlobal(requiredPointer);
        }
    }

    [Fact]
    public void EscapesUtf8BytesAndTerminatesOutput()
    {
        var (memory, context) = CreateContext("caffè /~");
        var expected = Encoding.ASCII.GetBytes("caff%C3%A8%20%2F~\0");
        context[CpuRegister.Rdi] = OutputAddress;
        context[CpuRegister.Rsi] = RequiredAddress;
        context[CpuRegister.Rdx] = (ulong)expected.Length;
        context[CpuRegister.Rcx] = InputAddress;

        AssertResult(0, context);
        Assert.Equal((ulong)expected.Length, ReadUInt64(memory, RequiredAddress));
        var actual = new byte[expected.Length];
        Assert.True(memory.TryRead(OutputAddress, actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TooSmallOutputReportsProviderErrorAndRequiredSize()
    {
        var (memory, context) = CreateContext("a b");
        context[CpuRegister.Rdi] = OutputAddress;
        context[CpuRegister.Rsi] = RequiredAddress;
        context[CpuRegister.Rdx] = 2;
        context[CpuRegister.Rcx] = InputAddress;

        AssertResult(0x80431022, context);
        Assert.Equal(6UL, ReadUInt64(memory, RequiredAddress));
    }

    [Fact]
    public void NullInputReportsProviderInvalidValue()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);

        AssertResult(0x804311FE, context);
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateContext(string input)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var bytes = Encoding.UTF8.GetBytes(input + '\0');
        Assert.True(memory.TryWrite(InputAddress, bytes));
        return (memory, new CpuContext(memory, Generation.Gen5));
    }

    private static void AssertResult(uint expected, CpuContext context)
    {
        var result = HttpLleExports.UriEscapeWithoutGuestProvider(context);
        Assert.Equal(unchecked((int)expected), result);
        Assert.Equal(unchecked((ulong)unchecked((int)expected)), context[CpuRegister.Rax]);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
