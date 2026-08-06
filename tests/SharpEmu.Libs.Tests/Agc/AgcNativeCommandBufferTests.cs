// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed unsafe class AgcNativeCommandBufferTests
{
    private const int PageSize = 4096;

    [Fact]
    public void SetShRegisterRangeAndPayloadSupportNativeMappedCommandBuffers()
    {
        const ulong guestBase = 0x1_0000_0000;
        const ulong outputAddress = guestBase + 0x20;
        var memory = new FakeCpuMemory(guestBase, 0x100);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = PageSize;
        Assert.Equal(0, KernelMemoryCompatExports.Malloc(ctx));
        var commandBufferAddress = ctx[CpuRegister.Rax];
        Assert.NotEqual(0UL, commandBufferAddress);

        try
        {
            var native = new Span<byte>((void*)commandBufferAddress, PageSize);
            native.Clear();

            var cursorUp = commandBufferAddress + 0x100;
            var cursorDown = commandBufferAddress + 0x400;
            BinaryPrimitives.WriteUInt64LittleEndian(native[0x10..], cursorUp);
            BinaryPrimitives.WriteUInt64LittleEndian(native[0x18..], cursorDown);

            ctx[CpuRegister.Rdi] = commandBufferAddress;
            ctx[CpuRegister.Rsi] = 0x240;
            ctx[CpuRegister.Rdx] = 0;
            ctx[CpuRegister.Rcx] = 14;

            Assert.Equal(0, AgcExports.CbSetShRegisterRangeDirect(ctx));

            var commandAddress = ctx[CpuRegister.Rax];
            Assert.Equal(cursorUp + 8, commandAddress);
            Assert.Equal(commandAddress + (16UL * sizeof(uint)),
                BinaryPrimitives.ReadUInt64LittleEndian(native[0x10..]));

            ctx[CpuRegister.Rdi] = outputAddress;
            ctx[CpuRegister.Rsi] = commandAddress;
            ctx[CpuRegister.Rdx] = 1;

            Assert.Equal(0, AgcExports.GetDataPacketPayloadAddress(ctx));

            Span<byte> payloadBytes = stackalloc byte[sizeof(ulong)];
            Assert.True(memory.TryRead(outputAddress, payloadBytes));
            Assert.Equal(commandAddress + 8,
                BinaryPrimitives.ReadUInt64LittleEndian(payloadBytes));
        }
        finally
        {
            ctx[CpuRegister.Rdi] = commandBufferAddress;
            KernelMemoryCompatExports.Free(ctx);
        }
    }

    [Fact]
    public void DcbSetUcRegisterDirect_EmitsExactFirmwarePacket()
    {
        const ulong guestBase = 0x1_0000_0000;
        const ulong commandBufferAddress = guestBase + 0x100;
        const ulong cursorUp = guestBase + 0x200;
        const ulong cursorDown = guestBase + 0x400;
        const ulong packedRegisterAndValue = (0x20UL << 32) | 0x024AUL;
        var memory = new FakeCpuMemory(guestBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Span<byte> commandBuffer = stackalloc byte[0x38];
        commandBuffer.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(commandBuffer[0x10..], cursorUp);
        BinaryPrimitives.WriteUInt64LittleEndian(commandBuffer[0x18..], cursorDown);
        Assert.True(memory.TryWrite(commandBufferAddress, commandBuffer));

        ctx[CpuRegister.Rdi] = commandBufferAddress;
        ctx[CpuRegister.Rsi] = packedRegisterAndValue;

        Assert.Equal(0, AgcExports.DcbSetUcRegisterDirect(ctx));
        Assert.Equal(cursorUp, ctx[CpuRegister.Rax]);

        Span<byte> packet = stackalloc byte[3 * sizeof(uint)];
        Assert.True(memory.TryRead(cursorUp, packet));
        Assert.Equal(0xC0017900U, BinaryPrimitives.ReadUInt32LittleEndian(packet));
        Assert.Equal(0x024AU, BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]));
        Assert.Equal(0x20U, BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]));

        Assert.True(memory.TryRead(commandBufferAddress, commandBuffer));
        Assert.Equal(
            cursorUp + (3UL * sizeof(uint)),
            BinaryPrimitives.ReadUInt64LittleEndian(commandBuffer[0x10..]));
    }
}
