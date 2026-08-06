// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.LibcInternal;

public static class LibcInternalExports
{
    // libSceLibcInternal's x86-64 jmp_buf layout, verified against the firmware
    // setjmp/longjmp implementations in Ghidra.  The final 16 bytes are the
    // saved signal mask populated by sceKernelSigprocmask.
    private const int JumpBufferSize = 0x58;
    private const int JumpReturnRipOffset = 0x00;
    private const int JumpRbxOffset = 0x08;
    private const int JumpRspOffset = 0x10;
    private const int JumpRbpOffset = 0x18;
    private const int JumpR12Offset = 0x20;
    private const int JumpR13Offset = 0x28;
    private const int JumpR14Offset = 0x30;
    private const int JumpR15Offset = 0x38;
    private const int JumpFpuControlOffset = 0x40;
    private const int JumpMxcsrOffset = 0x44;
    private const ulong HeapTraceInfoSize = 32;
    private const int HeapTraceTableEntryCount = 64;
    private const int HeapTraceMaskOffset = 0;
    private const int HeapTraceTableOffset = HeapTraceMaskOffset + sizeof(ulong);
    private const int HeapTraceStorageSize = HeapTraceTableOffset + (HeapTraceTableEntryCount * sizeof(ulong));

    private static readonly object _heapTraceGate = new();
    private static readonly object _atomic32Gate = new();
    private static nint _heapTraceStorage;

    [SysAbiExport(
        Nid = "gNQ1V2vfXDE",
        ExportName = "setjmp",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceLibcInternal")]
    public static int SetJmpInitialReturnCompat(CpuContext ctx)
    {
        var jumpBufferAddress = ctx[CpuRegister.Rdi];
        var stackPointer = ctx[CpuRegister.Rsp];
        if (jumpBufferAddress == 0 ||
            stackPointer == 0 ||
            !KernelMemoryCompatExports.TryReadUInt64Compat(ctx, stackPointer, out var returnRip))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        Span<byte> jumpBuffer = stackalloc byte[JumpBufferSize];
        BinaryPrimitives.WriteUInt64LittleEndian(jumpBuffer[JumpReturnRipOffset..], returnRip);
        BinaryPrimitives.WriteUInt64LittleEndian(jumpBuffer[JumpRbxOffset..], ctx[CpuRegister.Rbx]);
        BinaryPrimitives.WriteUInt64LittleEndian(jumpBuffer[JumpRspOffset..], stackPointer);
        BinaryPrimitives.WriteUInt64LittleEndian(jumpBuffer[JumpRbpOffset..], ctx[CpuRegister.Rbp]);
        BinaryPrimitives.WriteUInt64LittleEndian(jumpBuffer[JumpR12Offset..], ctx[CpuRegister.R12]);
        BinaryPrimitives.WriteUInt64LittleEndian(jumpBuffer[JumpR13Offset..], ctx[CpuRegister.R13]);
        BinaryPrimitives.WriteUInt64LittleEndian(jumpBuffer[JumpR14Offset..], ctx[CpuRegister.R14]);
        BinaryPrimitives.WriteUInt64LittleEndian(jumpBuffer[JumpR15Offset..], ctx[CpuRegister.R15]);
        BinaryPrimitives.WriteUInt16LittleEndian(jumpBuffer[JumpFpuControlOffset..], ctx.FpuControlWord);
        BinaryPrimitives.WriteUInt32LittleEndian(jumpBuffer[JumpMxcsrOffset..], ctx.Mxcsr);

        // The emulator does not currently virtualize a per-guest signal mask.
        // An empty mask is the safe equivalent for longjmp's restore step and
        // preserves the ABI layout without leaking host signal state.
        if (!KernelMemoryCompatExports.TryWriteCompat(ctx, jumpBufferAddress, jumpBuffer))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "iPBqs+YUUFw",
        ExportName = "__atomic_fetch_add_4_compat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceLibcInternal")]
    public static int AtomicFetchAdd32Compat1270(CpuContext ctx)
    {
        return AtomicFetchUpdate32(ctx, subtract: false);
    }

    [SysAbiExport(
        Nid = "2HnmKiLmV6s",
        ExportName = "__atomic_fetch_sub_4_compat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceLibcInternal")]
    public static int AtomicFetchSub32Compat1270(CpuContext ctx)
    {
        return AtomicFetchUpdate32(ctx, subtract: true);
    }

    [SysAbiExport(
        Nid = "NWtTN10cJzE",
        ExportName = "sceLibcHeapGetTraceInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "LibcInternalExt")]
    public static int LibcHeapGetTraceInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0 ||
            !KernelMemoryCompatExports.TryReadUInt64Compat(ctx, infoAddress, out var size) ||
            size != HeapTraceInfoSize)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var storage = EnsureHeapTraceStorage();
        if (storage == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var maskAddress = unchecked((ulong)(storage + HeapTraceMaskOffset));
        var tableAddress = unchecked((ulong)(storage + HeapTraceTableOffset));
        Span<byte> pointerBytes = stackalloc byte[sizeof(ulong)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(pointerBytes, maskAddress);
        if (!KernelMemoryCompatExports.TryWriteCompat(ctx, infoAddress + 16, pointerBytes))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(pointerBytes, tableAddress);
        if (!KernelMemoryCompatExports.TryWriteCompat(ctx, infoAddress + 24, pointerBytes))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static nint EnsureHeapTraceStorage()
    {
        lock (_heapTraceGate)
        {
            if (_heapTraceStorage != 0)
            {
                return _heapTraceStorage;
            }

            var storage = Marshal.AllocHGlobal(HeapTraceStorageSize);
            if (storage == 0)
            {
                return 0;
            }

            unsafe
            {
                NativeMemory.Clear((void*)storage, (nuint)HeapTraceStorageSize);
            }

            _heapTraceStorage = storage;
            return storage;
        }
    }

    private static int AtomicFetchUpdate32(CpuContext ctx, bool subtract)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var delta = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (valueAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        uint previous;
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        lock (_atomic32Gate)
        {
            if (!KernelMemoryCompatExports.TryReadCompat(ctx, valueAddress, bytes))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            previous = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            var next = subtract
                ? unchecked(previous - delta)
                : unchecked(previous + delta);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, next);
            if (!KernelMemoryCompatExports.TryWriteCompat(ctx, valueAddress, bytes))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        // The firmware helpers return the value observed before the update.
        ctx[CpuRegister.Rax] = previous;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
