// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.VideoRecording;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoRecording;

public sealed class VideoRecordingExportsTests
{
    private const ulong MemoryBase = 0x2000;
    private const int NotInitialized = unchecked((int)0x80A80002);
    private const int InvalidArgument = unchecked((int)0x80A80003);

    [Theory]
    [InlineData(2u)]
    [InlineData(6u)]
    [InlineData(7u)]
    [InlineData(8u)]
    [InlineData(0xA01u)]
    public void SetInfo_AcceptsProviderMetadataTypesWithoutGuestWrites(uint infoType)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x100);
        var context = new CpuContext(memory, Generation.Gen5);
        var infoAddress = MemoryBase + 0x20;
        var metadata = new byte[] { 1, 2, 3, 4 };
        Assert.True(memory.TryWrite(infoAddress, metadata));
        context[CpuRegister.Rdi] = infoType;
        context[CpuRegister.Rsi] = infoAddress;
        context[CpuRegister.Rdx] = (ulong)metadata.Length;

        Assert.Equal(0, VideoRecordingExports.VideoRecordingSetInfo(context));
        Span<byte> preserved = stackalloc byte[4];
        Assert.True(memory.TryRead(infoAddress, preserved));
        Assert.True(preserved.SequenceEqual(metadata));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(2u, 0UL, 4UL)]
    [InlineData(6u, MemoryBase + 0x20, 0x801UL)]
    [InlineData(1u, MemoryBase + 0x20, 4UL)]
    public void SetInfo_RejectsInvalidMetadataArguments(uint infoType, ulong infoAddress, ulong infoSize)
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x100), Generation.Gen5);
        context[CpuRegister.Rdi] = infoType;
        context[CpuRegister.Rsi] = infoAddress;
        context[CpuRegister.Rdx] = infoSize;

        Assert.Equal(InvalidArgument, VideoRecordingExports.VideoRecordingSetInfo(context));
        Assert.Equal(unchecked((ulong)InvalidArgument), context[CpuRegister.Rax]);
    }

    [Fact]
    public void SetInfo_DoesNotPretendActiveRecordingDataWasAccepted()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x100), Generation.Gen5);
        context[CpuRegister.Rdi] = 0xA004;

        Assert.Equal(NotInitialized, VideoRecordingExports.VideoRecordingSetInfo(context));
        Assert.Equal(unchecked((ulong)NotInitialized), context[CpuRegister.Rax]);
    }
}
