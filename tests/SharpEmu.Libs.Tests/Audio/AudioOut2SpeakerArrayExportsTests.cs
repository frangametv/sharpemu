// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

[Collection(AudioOut2StateCollection.Name)]
public sealed class AudioOut2SpeakerArrayExportsTests : IDisposable
{
    private const int AudioOut2InvalidArgument = unchecked((int)0x80268001);
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong OutHandleAddress = MemoryBase + 0x100;
    private const ulong ReservedAddress = MemoryBase + 0x120;
    private const ulong ParamAddress = MemoryBase + 0x200;
    private const ulong SpeakerMemoryAddress = MemoryBase + 0x400;

    public AudioOut2SpeakerArrayExportsTests() => AudioOut2Exports.ResetSpeakerArraysForTests();

    public void Dispose() => AudioOut2Exports.ResetSpeakerArraysForTests();

    private static CpuContext CreateContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(MemoryBase, 0x2000);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static void WriteU64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteU32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteValidDescriptor(FakeCpuMemory memory)
    {
        WriteU64(memory, ParamAddress, ParamAddress + 0x100);
        WriteU32(memory, ParamAddress + 0x08, 2);
        WriteU64(memory, ParamAddress + 0x10, SpeakerMemoryAddress);
        WriteU64(memory, ParamAddress + 0x18, 0x1000);
    }

    private static ulong ReadU64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[8];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static uint ReadU32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[4];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    [Fact]
    public void GetSpeakerArrayMemorySize_NeverReturnsTheNotFoundSentinel()
    {
        var ctx = CreateContext(out _);
        ctx[CpuRegister.Rdi] = 8;

        var result = AudioOut2Exports.AudioOut2GetSpeakerArrayMemorySize(ctx);

        Assert.NotEqual((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
        Assert.Equal(0, result);
        Assert.Equal(0x2E0UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void GetSpeakerArrayMemorySize_TwoChannelsIsExactChannelScaledSize()
    {
        var ctx = CreateContext(out _);
        ctx[CpuRegister.Rdi] = 2;

        var result = AudioOut2Exports.AudioOut2GetSpeakerArrayMemorySize(ctx);

        Assert.Equal(0, result);
        Assert.Equal(0x220UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void SpeakerArrayCreate_PublishesObjectPointerAndLeavesReservedSizeAlone()
    {
        var ctx = CreateContext(out var memory);
        WriteValidDescriptor(memory);
        // Stage a size in the reserved slot the way callers do before Create.
        WriteU64(memory, ReservedAddress, 0x100);
        ctx[CpuRegister.Rdi] = OutHandleAddress;
        ctx[CpuRegister.Rsi] = ParamAddress;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = 0xDEAD_BEEF;

        var result = AudioOut2Exports.AudioOut2SpeakerArrayCreate(ctx);

        Assert.Equal(0, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(SpeakerMemoryAddress + 0x1000 - 0x18, ReadU64(memory, OutHandleAddress));
        // Reserved/size slot must remain untouched — writing it corrupted canaries.
        Assert.Equal(0x100UL, ReadU64(memory, ReservedAddress));
    }

    [Fact]
    public void SpeakerArrayCreate_PublishesHandleForTypicalCallShape()
    {
        var ctx = CreateContext(out var memory);
        WriteValidDescriptor(memory);
        ctx[CpuRegister.Rdi] = OutHandleAddress;
        ctx[CpuRegister.Rsi] = ParamAddress;

        var result = AudioOut2Exports.AudioOut2SpeakerArrayCreate(ctx);

        Assert.Equal(0, result);
        Assert.NotEqual(0UL, ReadU64(memory, OutHandleAddress));
        Assert.NotEqual((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
    }

    [Fact]
    public void SpeakerArrayCreate_RejectsCorruptedDescriptorFields()
    {
        var ctx = CreateContext(out var memory);
        // Simulate PortGetState having overwritten param+0x18 (size) with a
        // state blob — Create must NOT adopt that as an in-place buffer.
        WriteU64(memory, ParamAddress + 0x10, SpeakerMemoryAddress);
        WriteU64(memory, ParamAddress + 0x18, 0x100);
        ctx[CpuRegister.Rdi] = OutHandleAddress;
        ctx[CpuRegister.Rsi] = ParamAddress;

        var result = AudioOut2Exports.AudioOut2SpeakerArrayCreate(ctx);

        Assert.Equal(AudioOut2InvalidArgument, result);
        Assert.Equal(unchecked((ulong)AudioOut2InvalidArgument), ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void SpeakerArrayDestroy_UnknownHandleIsRejected()
    {
        var ctx = CreateContext(out _);
        ctx[CpuRegister.Rdi] = 0xDEAD_BEEF;

        var result = AudioOut2Exports.AudioOut2SpeakerArrayDestroy(ctx);

        Assert.Equal(AudioOut2InvalidArgument, result);
    }
}
