// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

[CollectionDefinition("AmprExports", DisableParallelization = true)]
public sealed class AmprExportsCollection;

[Collection("AmprExports")]
public sealed class AmprExportsTests
{
    private const ulong CommandBuffer = 0x10_0100;
    private const ulong BackingBuffer = 0x10_1000;

    [Fact]
    public void ConstructorAndSetBuffer_UseFirmware1270ObjectLayout()
    {
        var memory = new FixedMemory();
        memory.Fill(CommandBuffer, 0x18, 0xA5);
        var ctx = new CpuContext(memory, Generation.Gen5);

        ctx[CpuRegister.Rdi] = CommandBuffer;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(ctx));
        Assert.Equal(new byte[0x18], memory.Read(CommandBuffer, 0x18));

        ctx[CpuRegister.Rdi] = CommandBuffer;
        ctx[CpuRegister.Rsi] = BackingBuffer;
        ctx[CpuRegister.Rdx] = 0x4000;
        Assert.Equal(0, AmprExports.CommandBufferSetBuffer(ctx));
        Assert.Equal(0x4000U, memory.ReadUInt32(CommandBuffer + 0x0C));
        Assert.Equal(BackingBuffer, memory.ReadUInt64(CommandBuffer + 0x10));

        ctx[CpuRegister.Rdi] = CommandBuffer;
        Assert.Equal(0, AmprExports.CommandBufferGetSize(ctx));
        Assert.Equal(0x4000UL, ctx[CpuRegister.Rax]);
        Assert.Equal(0, AmprExports.CommandBufferGetCurrentOffset(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(0, AmprExports.CommandBufferGetNumCommands(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rsi] = BackingBuffer + 0x1000;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY,
            AmprExports.CommandBufferSetBuffer(ctx));
    }

    [Fact]
    public void AprConstructor_ZerosItsTwoOutputPointersWithoutRewritingCommandBuffer()
    {
        var memory = new FixedMemory();
        memory.Fill(CommandBuffer, 0x18, 0xA5);
        var expectedHeader = memory.Read(CommandBuffer, 0x18);
        var outAprContext = CommandBuffer + 0x40;
        var outAprAux = CommandBuffer + 0x48;
        memory.WriteUInt64(outAprContext, ulong.MaxValue);
        memory.WriteUInt64(outAprAux, ulong.MaxValue);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = CommandBuffer;
        ctx[CpuRegister.Rsi] = outAprContext;
        ctx[CpuRegister.Rdx] = outAprAux;

        Assert.Equal(0, AmprExports.AprCommandBufferConstructor(ctx));

        Assert.Equal(expectedHeader, memory.Read(CommandBuffer, 0x18));
        Assert.Equal(0UL, memory.ReadUInt64(outAprContext));
        Assert.Equal(0UL, memory.ReadUInt64(outAprAux));
    }

    [Fact]
    public void ResetAndClearBuffer_MatchFirmwareCountersAndBindingSemantics()
    {
        var memory = new FixedMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = CommandBuffer;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(ctx));
        ctx[CpuRegister.Rsi] = BackingBuffer;
        ctx[CpuRegister.Rdx] = 0x4000;
        Assert.Equal(0, AmprExports.CommandBufferSetBuffer(ctx));
        memory.WriteUInt32(CommandBuffer + 0x04, 0x80);
        memory.WriteUInt32(CommandBuffer + 0x08, 3);

        Assert.Equal(0, AmprExports.CommandBufferReset(ctx));
        Assert.Equal(0U, memory.ReadUInt32(CommandBuffer + 0x04));
        Assert.Equal(0U, memory.ReadUInt32(CommandBuffer + 0x08));
        Assert.Equal(0x4000U, memory.ReadUInt32(CommandBuffer + 0x0C));
        Assert.Equal(BackingBuffer, memory.ReadUInt64(CommandBuffer + 0x10));

        Assert.Equal(0, AmprExports.CommandBufferClearBuffer(ctx));
        Assert.Equal(BackingBuffer, ctx[CpuRegister.Rax]);
        Assert.Equal(0U, memory.ReadUInt32(CommandBuffer + 0x0C));
        Assert.Equal(0UL, memory.ReadUInt64(CommandBuffer + 0x10));
    }

    [Fact]
    public void CompleteCommandBuffer_HeaderTemporarilyUnreadable_UsesRegisteredState()
    {
        var memory = new FixedMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        const ulong watcherAddress = BackingBuffer + 0x8000;
        const ulong watcherValue = 0x1234_5678UL;

        ctx[CpuRegister.Rdi] = CommandBuffer;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(ctx));
        ctx[CpuRegister.Rsi] = BackingBuffer;
        ctx[CpuRegister.Rdx] = 0x4000;
        Assert.Equal(0, AmprExports.CommandBufferSetBuffer(ctx));

        ctx[CpuRegister.Rdi] = CommandBuffer;
        ctx[CpuRegister.Rsi] = watcherAddress;
        ctx[CpuRegister.Rdx] = watcherValue;
        Assert.Equal(0, AmprExports.CommandBufferWriteAddressOnCompletion(ctx));

        memory.DenyCommandBufferReads = true;
        Assert.Equal(0, AmprExports.CompleteCommandBuffer(ctx, CommandBuffer));
        Assert.Equal(watcherValue, memory.ReadUInt64(watcherAddress));
    }

    private sealed class FixedMemory : ICpuMemory
    {
        private const ulong BaseAddress = 0x10_0000;
        private readonly byte[] _bytes = new byte[0x20_000];

        public bool DenyCommandBufferReads { get; set; }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (DenyCommandBufferReads &&
                virtualAddress < CommandBuffer + 0x18 &&
                virtualAddress + (ulong)destination.Length > CommandBuffer)
            {
                return false;
            }

            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _bytes.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_bytes.AsSpan(offset, source.Length));
            return true;
        }

        public void Fill(ulong address, int length, byte value)
        {
            Assert.True(TryResolve(address, length, out var offset));
            _bytes.AsSpan(offset, length).Fill(value);
        }

        public byte[] Read(ulong address, int length)
        {
            var result = new byte[length];
            Assert.True(TryRead(address, result));
            return result;
        }

        public uint ReadUInt32(ulong address)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            Assert.True(TryRead(address, bytes));
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        public ulong ReadUInt64(ulong address)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            Assert.True(TryRead(address, bytes));
            return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }

        public void WriteUInt32(ulong address, uint value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            Assert.True(TryWrite(address, bytes));
        }

        public void WriteUInt64(ulong address, ulong value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            Assert.True(TryWrite(address, bytes));
        }

        private bool TryResolve(ulong address, int length, out int offset)
        {
            if (address < BaseAddress || length < 0)
            {
                offset = 0;
                return false;
            }

            var relative = address - BaseAddress;
            if (relative > (ulong)_bytes.Length || (ulong)length > (ulong)_bytes.Length - relative)
            {
                offset = 0;
                return false;
            }

            offset = checked((int)relative);
            return true;
        }
    }
}
