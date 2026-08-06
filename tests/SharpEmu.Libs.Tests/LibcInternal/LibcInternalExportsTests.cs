// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Buffers.Binary;

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.LibcInternal;
using Xunit;

namespace SharpEmu.Libs.Tests.LibcInternal;

public sealed class LibcInternalExportsTests
{
    [Fact]
    public void SetJmp_RegistersAndReturnsZeroForInitialCall()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        const ulong jumpBufferAddress = 0x1_0000_0100;
        const ulong stackPointer = 0x1_0000_0800;
        const ulong returnRip = 0x8_0012_3456;
        Span<byte> returnSlot = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(returnSlot, returnRip);
        Assert.True(memory.TryWrite(stackPointer, returnSlot));

        context[CpuRegister.Rdi] = jumpBufferAddress;
        context[CpuRegister.Rsp] = stackPointer;
        context[CpuRegister.Rbx] = 0x11;
        context[CpuRegister.Rbp] = 0x22;
        context[CpuRegister.R12] = 0x33;
        context[CpuRegister.R13] = 0x44;
        context[CpuRegister.R14] = 0x55;
        context[CpuRegister.R15] = 0x66;
        context.FpuControlWord = 0x037F;
        context.Mxcsr = 0x1F80;
        context[CpuRegister.Rax] = ulong.MaxValue;

        Assert.True(manager.TryGetExport("gNQ1V2vfXDE", out var export));
        Assert.Equal("setjmp", export.Name);
        Assert.Equal("libSceLibcInternal", export.LibraryName);
        Assert.True(manager.TryDispatch("gNQ1V2vfXDE", context, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        Span<byte> saved = stackalloc byte[0x58];
        Assert.True(memory.TryRead(jumpBufferAddress, saved));
        Assert.Equal(returnRip, BinaryPrimitives.ReadUInt64LittleEndian(saved[0x00..]));
        Assert.Equal(0x11UL, BinaryPrimitives.ReadUInt64LittleEndian(saved[0x08..]));
        Assert.Equal(stackPointer, BinaryPrimitives.ReadUInt64LittleEndian(saved[0x10..]));
        Assert.Equal(0x22UL, BinaryPrimitives.ReadUInt64LittleEndian(saved[0x18..]));
        Assert.Equal(0x33UL, BinaryPrimitives.ReadUInt64LittleEndian(saved[0x20..]));
        Assert.Equal(0x44UL, BinaryPrimitives.ReadUInt64LittleEndian(saved[0x28..]));
        Assert.Equal(0x55UL, BinaryPrimitives.ReadUInt64LittleEndian(saved[0x30..]));
        Assert.Equal(0x66UL, BinaryPrimitives.ReadUInt64LittleEndian(saved[0x38..]));
        Assert.Equal((ushort)0x037F, BinaryPrimitives.ReadUInt16LittleEndian(saved[0x40..]));
        Assert.Equal(0x1F80U, BinaryPrimitives.ReadUInt32LittleEndian(saved[0x44..]));
        Assert.True(saved[0x48..].ToArray().All(value => value == 0));
    }

    [Fact]
    public void SetJmp_DirectCallReturnsInitialZero()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x1_0000_0100;
        context[CpuRegister.Rsp] = 0x1_0000_0800;
        Assert.True(context.TryWriteUInt64(context[CpuRegister.Rsp], 0x8_0000_1234));

        var result = LibcInternalExports.SetJmpInitialReturnCompat(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    private const ulong Base = 0x3_0000_0000;
    private const ulong InfoAddress = Base + 0x100;
    private const ulong ExpectedInfoSize = 32;

    [Fact]
    public void HeapGetTraceInfo_NullPointer_ReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = 0;

        var result = LibcInternalExports.LibcHeapGetTraceInfo(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void SetJmp_WritesNativeBackedJumpBufferForLleLongJmp()
    {
        var context = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        context[CpuRegister.Rsp] = 0x1_0000_0800;
        Assert.True(context.TryWriteUInt64(context[CpuRegister.Rsp], 0x8_0000_5678));

        context[CpuRegister.Rdi] = 0x58;
        Assert.Equal(0, KernelMemoryCompatExports.Malloc(context));
        var jumpBufferAddress = context[CpuRegister.Rax];
        try
        {
            context[CpuRegister.Rdi] = jumpBufferAddress;
            Assert.Equal(0, LibcInternalExports.SetJmpInitialReturnCompat(context));

            Assert.Equal(
                unchecked((long)0x8_0000_5678UL),
                Marshal.ReadInt64(unchecked((nint)jumpBufferAddress), 0x00));
            Assert.Equal(
                unchecked((long)context[CpuRegister.Rsp]),
                Marshal.ReadInt64(unchecked((nint)jumpBufferAddress), 0x10));
        }
        finally
        {
            context[CpuRegister.Rdi] = jumpBufferAddress;
            Assert.Equal(0, KernelMemoryCompatExports.Free(context));
        }
    }

    [Fact]
    public void AtomicFetchAddAndSub_UpdateNativeMappedGuestMemory()
    {
        var context = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = sizeof(uint);
        Assert.Equal(0, KernelMemoryCompatExports.Malloc(context));
        var valueAddress = context[CpuRegister.Rax];
        try
        {
            Marshal.WriteInt32(unchecked((nint)valueAddress), 7);
            context[CpuRegister.Rdi] = valueAddress;
            context[CpuRegister.Rsi] = 3;
            Assert.Equal(0, LibcInternalExports.AtomicFetchAdd32Compat1270(context));
            Assert.Equal(7UL, context[CpuRegister.Rax]);
            Assert.Equal(10, Marshal.ReadInt32(unchecked((nint)valueAddress)));

            context[CpuRegister.Rdi] = valueAddress;
            context[CpuRegister.Rsi] = 4;
            Assert.Equal(0, LibcInternalExports.AtomicFetchSub32Compat1270(context));
            Assert.Equal(10UL, context[CpuRegister.Rax]);
            Assert.Equal(6, Marshal.ReadInt32(unchecked((nint)valueAddress)));
        }
        finally
        {
            context[CpuRegister.Rdi] = valueAddress;
            Assert.Equal(0, KernelMemoryCompatExports.Free(context));
        }
    }

    [Fact]
    public void HeapGetTraceInfo_WrongSize_ReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        Span<byte> sizeBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(
            sizeBytes,
            ExpectedInfoSize - 1);

        Assert.True(memory.TryWrite(InfoAddress, sizeBytes));

        context[CpuRegister.Rdi] = InfoAddress;

        var result = LibcInternalExports.LibcHeapGetTraceInfo(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void HeapGetTraceInfo_ValidBuffer_WritesStablePointers()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        Span<byte> sizeBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(
            sizeBytes,
            ExpectedInfoSize);

        Assert.True(memory.TryWrite(InfoAddress, sizeBytes));

        context[CpuRegister.Rdi] = InfoAddress;

        var firstResult =
            LibcInternalExports.LibcHeapGetTraceInfo(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            firstResult);
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        Assert.True(
            context.TryReadUInt64(
                InfoAddress + 16,
                out var firstMaskAddress));

        Assert.True(
            context.TryReadUInt64(
                InfoAddress + 24,
                out var firstTableAddress));

        Assert.NotEqual(0UL, firstMaskAddress);
        Assert.Equal(firstMaskAddress + 8UL, firstTableAddress);

        var secondResult =
            LibcInternalExports.LibcHeapGetTraceInfo(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            secondResult);

        Assert.True(
            context.TryReadUInt64(
                InfoAddress + 16,
                out var secondMaskAddress));

        Assert.True(
            context.TryReadUInt64(
                InfoAddress + 24,
                out var secondTableAddress));

        Assert.Equal(firstMaskAddress, secondMaskAddress);
        Assert.Equal(firstTableAddress, secondTableAddress);
    }

    [Fact]
    public void HeapGetTraceInfo_TruncatedOutput_ReturnsMemoryFault()
    {
        var memory = new FakeCpuMemory(Base, 31);
        var context = new CpuContext(memory, Generation.Gen5);

        Span<byte> sizeBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(
            sizeBytes,
            ExpectedInfoSize);

        Assert.True(memory.TryWrite(Base, sizeBytes));

        context[CpuRegister.Rdi] = Base;

        var result = LibcInternalExports.LibcHeapGetTraceInfo(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }
}
