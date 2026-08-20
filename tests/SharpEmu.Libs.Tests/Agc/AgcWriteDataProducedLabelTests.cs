// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class AgcWriteDataProducedLabelTests
{
    private const ulong BaseAddress = 0x1_1200_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong SubmitPacketAddress = BaseAddress + 0x200;
    private const ulong StackAddress = BaseAddress + 0x300;
    private const ulong DataAddress = BaseAddress + 0x400;
    private const ulong PacketAddress = BaseAddress + 0x800;
    private const ulong LabelAddress = BaseAddress + 0x1000;
    private const ulong LabelValue = 0x1122_3344_5566_7788;

    [Fact]
    public void SubmittedWriteDataRecordsDwordAndCombined64BitProducerEdges()
    {
        GpuWaitRegistry.Clear();
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        try
        {
            WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
            WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x100);
            WriteUInt64(memory, DataAddress, LabelValue);
            WriteUInt64(memory, StackAddress + 8, 0); // increment addresses
            WriteUInt64(memory, StackAddress + 16, 1); // write confirmation

            ctx[CpuRegister.Rdi] = CommandBufferAddress;
            ctx[CpuRegister.Rsi] = 4; // memory destination
            ctx[CpuRegister.Rdx] = 0;
            ctx[CpuRegister.Rcx] = LabelAddress;
            ctx[CpuRegister.R8] = DataAddress;
            ctx[CpuRegister.R9] = 2;
            ctx[CpuRegister.Rsp] = StackAddress;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DcbWriteData(ctx));
            Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);

            WriteUInt64(memory, SubmitPacketAddress, PacketAddress);
            WriteUInt32(memory, SubmitPacketAddress + 8, 6);
            ctx[CpuRegister.Rdi] = SubmitPacketAddress;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverSubmitDcb(ctx));

            Assert.True(GpuWaitRegistry.TryConsumeProducedSatisfaction(NewWaiter(
                memory,
                LabelAddress,
                LabelValue,
                ulong.MaxValue,
                is64Bit: true)));
            Assert.True(GpuWaitRegistry.TryConsumeProducedSatisfaction(NewWaiter(
                memory,
                LabelAddress + sizeof(uint),
                (uint)(LabelValue >> 32),
                uint.MaxValue,
                is64Bit: false)));
        }
        finally
        {
            GpuWaitRegistry.Clear();
        }
    }

    private static GpuWaitRegistry.WaitingDcb NewWaiter(
        object memory,
        ulong address,
        ulong reference,
        ulong mask,
        bool is64Bit) => new()
    {
        WaitAddress = address,
        ReferenceValue = reference,
        Mask = mask,
        CompareFunction = 3,
        Is64Bit = is64Bit,
        Memory = memory,
    };

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
