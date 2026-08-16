// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VideoOutEventCompatTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong EventAddress = MemoryBase + 0x100;

    [Fact]
    public void MissingVideoOutEventExportsRegisterByKnownNids()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("-Ozn0F1AFRg", out var deleteFlip));
        Assert.Equal("sceVideoOutDeleteFlipEvent", deleteFlip.Name);
        Assert.True(manager.TryGetExport("Mt4QHHkxkOc", out var getCount));
        Assert.Equal("sceVideoOutGetEventCount", getCount.Name);
    }

    [Fact]
    public void GetEventCountDecodesCoalescedKeventBits()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        Span<byte> kevent = stackalloc byte[0x20];
        BinaryPrimitives.WriteInt16LittleEndian(kevent[0x08..], -13);
        BinaryPrimitives.WriteUInt64LittleEndian(kevent[0x10..], 9UL << 12);
        Assert.True(memory.TryWrite(EventAddress, kevent));

        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = EventAddress;
        Assert.Equal(9, VideoOutExports.VideoOutGetEventCount(context));
        Assert.Equal(9UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void DeleteFlipEventRejectsUnknownHandle()
    {
        var context = new CpuContext(
            new FakeCpuMemory(MemoryBase, 0x1000),
            Generation.Gen5);
        context[CpuRegister.Rdi] = 0x1111;
        context[CpuRegister.Rsi] = uint.MaxValue;

        Assert.Equal(
            unchecked((int)0x8029000B),
            VideoOutExports.VideoOutDeleteFlipEvent(context));
    }
}
