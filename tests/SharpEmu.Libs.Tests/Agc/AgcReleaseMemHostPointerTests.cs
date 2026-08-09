// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class AgcReleaseMemHostPointerTests
{
    private const ulong BaseAddress = 0x1_1000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong SubmitPacketAddress = BaseAddress + 0x200;
    private const ulong StackAddress = BaseAddress + 0x300;
    private const ulong PacketAddress = BaseAddress + 0x800;
    private const ulong ExpectedFenceValue = 0x1122_3344_5566_7788;

    [Fact]
    public void SubmittedReleaseMem64_WritesValidatedHostPointer()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var hostFence = Marshal.AllocHGlobal(sizeof(long));

        try
        {
            Marshal.WriteInt64(hostFence, 0);
            WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
            WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x100);

            ctx[CpuRegister.Rdi] = CommandBufferAddress;
            ctx[CpuRegister.Rsi] = 0; // cache action
            ctx[CpuRegister.Rdx] = 0; // GCR control
            ctx[CpuRegister.Rcx] = 0; // memory destination
            ctx[CpuRegister.R8] = 0;  // cache policy
            ctx[CpuRegister.R9] = unchecked((ulong)hostFence.ToInt64());
            ctx[CpuRegister.Rsp] = StackAddress;
            WriteUInt64(memory, StackAddress + 8, 2); // 64-bit immediate data
            WriteUInt64(memory, StackAddress + 16, ExpectedFenceValue);
            WriteUInt64(memory, StackAddress + 24, 0); // GDS offset
            WriteUInt64(memory, StackAddress + 32, 0); // GDS size
            WriteUInt64(memory, StackAddress + 40, 0); // interrupt
            WriteUInt64(memory, StackAddress + 48, 0); // interrupt context

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.CbReleaseMem(ctx));
            Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);

            WriteUInt64(memory, SubmitPacketAddress, PacketAddress);
            WriteUInt32(memory, SubmitPacketAddress + 8, 8);
            ctx[CpuRegister.Rdi] = SubmitPacketAddress;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverSubmitDcb(ctx));

            Assert.Equal(
                ExpectedFenceValue,
                unchecked((ulong)Marshal.ReadInt64(hostFence)));
        }
        finally
        {
            Marshal.FreeHGlobal(hostFence);
        }
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}
