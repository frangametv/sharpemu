// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class LibcSearchAndConversionExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ErrnoAddress = MemoryBase + 0x40;
    private const ulong InputAddress = MemoryBase + 0x100;
    private const ulong EndPointerAddress = MemoryBase + 0x300;
    private const ulong KeyAddress = MemoryBase + 0x500;
    private const ulong ArrayAddress = MemoryBase + 0x600;
    private const ulong ComparatorAddress = 0x1234_5678;
    private const int InitialErrno = 0x1234;

    public static IEnumerable<object[]> ExportCases()
    {
        yield return new object[] { "NesIgTmfF0Q", "bsearch" };
        yield return new object[] { "5OqszGpy7Mg", "strtoull" };
    }

    [Theory]
    [MemberData(nameof(ExportCases))]
    public void Exports_RegisterAsGen5Libc(string nid, string name)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(nid, export.Nid);
        Assert.Equal(name, export.Name);
        Assert.Equal("libc", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    [Fact]
    public void Bsearch_EmptyRangeReturnsNullWithoutSchedulerOrPointerAccess()
    {
        var (_, context) = CreateContext();
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = ulong.MaxValue;
        context[CpuRegister.R8] = 0;

        var result = LibcSearchAndConversionExports.LibcBsearch(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Bsearch_FollowsFirmwareMidpointBranchesAndReturnsMatchingElement()
    {
        var (_, context) = CreateContext();
        Assert.True(context.TryWriteUInt64(KeyAddress, 40));
        var values = new ulong[] { 10, 20, 30, 40, 50 };
        for (var index = 0; index < values.Length; index++)
        {
            Assert.True(context.TryWriteUInt64(ArrayAddress + ((ulong)index * sizeof(ulong)), values[index]));
        }

        context[CpuRegister.Rdi] = KeyAddress;
        context[CpuRegister.Rsi] = ArrayAddress;
        context[CpuRegister.Rdx] = (ulong)values.Length;
        context[CpuRegister.Rcx] = sizeof(ulong);
        context[CpuRegister.R8] = ComparatorAddress;
        var scheduler = new BsearchTestScheduler(ComparatorAddress);
        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            var result = LibcSearchAndConversionExports.LibcBsearch(context);

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
            Assert.Equal(ArrayAddress + (3 * sizeof(ulong)), context[CpuRegister.Rax]);
            Assert.Equal(
                new[]
                {
                    ArrayAddress + (2 * sizeof(ulong)),
                    ArrayAddress + (4 * sizeof(ulong)),
                    ArrayAddress + (3 * sizeof(ulong)),
                },
                scheduler.CandidateAddresses);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    [Fact]
    public void Bsearch_CallbackFailureReportsCpuTrap()
    {
        var (_, context) = CreateContext();
        context[CpuRegister.Rdi] = KeyAddress;
        context[CpuRegister.Rsi] = ArrayAddress;
        context[CpuRegister.Rdx] = 1;
        context[CpuRegister.Rcx] = sizeof(ulong);
        context[CpuRegister.R8] = ComparatorAddress;
        var scheduler = new BsearchTestScheduler(ComparatorAddress, fail: true);
        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            var result = LibcSearchAndConversionExports.LibcBsearch(context);

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP, result);
            Assert.Equal(0UL, context[CpuRegister.Rax]);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    [Fact]
    public void Strtoull_AutodetectsSignedHexAndWritesFirstUnconsumedByte()
    {
        var (memory, context) = CreateContext();
        memory.WriteCString(InputAddress, " \t-0x2A!");
        ConfigureStrtoull(context, numberBase: 0);

        var result = LibcSearchAndConversionExports.LibcStrtoull(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(unchecked((ulong)-42L), context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(EndPointerAddress, out var endPointer));
        Assert.Equal(InputAddress + 7, endPointer);
        Assert.Equal(InitialErrno, ReadErrno(memory));
    }

    [Fact]
    public void Strtoull_BaseZeroUsesOctalAndStopsBeforeInvalidDigit()
    {
        var (memory, context) = CreateContext();
        memory.WriteCString(InputAddress, "0759");
        ConfigureStrtoull(context, numberBase: 0);

        var result = LibcSearchAndConversionExports.LibcStrtoull(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(61UL, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(EndPointerAddress, out var endPointer));
        Assert.Equal(InputAddress + 3, endPointer);
        Assert.Equal(InitialErrno, ReadErrno(memory));
    }

    [Fact]
    public void Strtoull_HexPrefixWithoutDigitPerformsNoConversion()
    {
        var (memory, context) = CreateContext();
        memory.WriteCString(InputAddress, "  +0xg");
        ConfigureStrtoull(context, numberBase: 0);

        var result = LibcSearchAndConversionExports.LibcStrtoull(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(EndPointerAddress, out var endPointer));
        Assert.Equal(InputAddress, endPointer);
        Assert.Equal(InitialErrno, ReadErrno(memory));
    }

    [Fact]
    public void Strtoull_InvalidBaseWritesOriginalEndPointerThenSetsEinval()
    {
        var (memory, context) = CreateContext();
        memory.WriteCString(InputAddress, "99");
        ConfigureStrtoull(context, numberBase: 1);

        var result = LibcSearchAndConversionExports.LibcStrtoull(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(EndPointerAddress, out var endPointer));
        Assert.Equal(InputAddress, endPointer);
        Assert.Equal(22, ReadErrno(memory));
    }

    [Fact]
    public void Strtoull_InvalidBaseDoesNotSetErrnoWhenEndPointerFaultsFirst()
    {
        var (memory, context) = CreateContext();
        memory.WriteCString(InputAddress, "99");
        ConfigureStrtoull(context, numberBase: 37);
        context[CpuRegister.Rsi] = MemoryBase + 0x3000;

        var result = LibcSearchAndConversionExports.LibcStrtoull(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.Equal(InitialErrno, ReadErrno(memory));
    }

    [Fact]
    public void Strtoull_InvalidBaseWritesEndPointerBeforeUnmappedErrnoFault()
    {
        var (memory, context) = CreateContext();
        memory.WriteCString(InputAddress, "99");
        ConfigureStrtoull(context, numberBase: 1);
        context.FsBase = MemoryBase + 0x3000;

        var result = LibcSearchAndConversionExports.LibcStrtoull(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.True(context.TryReadUInt64(EndPointerAddress, out var endPointer));
        Assert.Equal(InputAddress, endPointer);
        Assert.Equal(InitialErrno, ReadErrno(memory));
    }

    [Fact]
    public void Strtoull_OverflowSaturatesSetsErangeAndConsumesAllDigits()
    {
        var (memory, context) = CreateContext();
        const string digits = "184467440737095516160";
        memory.WriteCString(InputAddress, digits + "xyz");
        ConfigureStrtoull(context, numberBase: 10);

        var result = LibcSearchAndConversionExports.LibcStrtoull(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(EndPointerAddress, out var endPointer));
        Assert.Equal(InputAddress + (ulong)digits.Length, endPointer);
        Assert.Equal(34, ReadErrno(memory));
    }

    [Fact]
    public void Strtoull_OverflowSetsErangeBeforeFinalEndPointerFault()
    {
        var (memory, context) = CreateContext();
        memory.WriteCString(InputAddress, "18446744073709551616");
        ConfigureStrtoull(context, numberBase: 10);
        context[CpuRegister.Rsi] = MemoryBase + 0x3000;

        var result = LibcSearchAndConversionExports.LibcStrtoull(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.Equal(34, ReadErrno(memory));
    }

    [Fact]
    public void Strtoull_OverflowDoesNotWriteEndPointerWhenFirstErrnoWriteFaults()
    {
        var (memory, context) = CreateContext();
        const ulong sentinel = 0xCAFE_BABE_DEAD_BEEFUL;
        const string digits = "18446744073709551616";
        memory.WriteCString(InputAddress, digits);
        ConfigureStrtoull(context, numberBase: 10);
        Assert.True(context.TryWriteUInt64(EndPointerAddress, sentinel));
        context.FsBase = MemoryBase + 0x3000;

        var result = LibcSearchAndConversionExports.LibcStrtoull(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.True(context.TryReadUInt64(EndPointerAddress, out var endPointer));
        Assert.Equal(sentinel, endPointer);
        Assert.Equal(InitialErrno, ReadErrno(memory));
    }

    [Fact]
    public void Strtoull_OverflowChecksSecondErrnoWriteAfterEndPointerStore()
    {
        var backingMemory = new FakeCpuMemory(MemoryBase, 0x2000);
        WriteErrno(backingMemory, InitialErrno);
        const string digits = "18446744073709551616";
        backingMemory.WriteCString(InputAddress, digits + "!");
        var memory = new FailNthWriteMemory(backingMemory, ErrnoAddress, failOnWrite: 2);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            FsBase = MemoryBase,
        };
        ConfigureStrtoull(context, numberBase: 10);

        var result = LibcSearchAndConversionExports.LibcStrtoull(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.Equal(2, memory.TargetWriteCount);
        Assert.True(context.TryReadUInt64(EndPointerAddress, out var endPointer));
        Assert.Equal(InputAddress + (ulong)digits.Length, endPointer);
        Assert.Equal(34, ReadErrno(backingMemory));
    }

    [Fact]
    public void Strtoull_NegativeMaximumWrapsWithoutRangeError()
    {
        var (memory, context) = CreateContext();
        memory.WriteCString(InputAddress, "-18446744073709551615!");
        ConfigureStrtoull(context, numberBase: 10);

        var result = LibcSearchAndConversionExports.LibcStrtoull(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(1UL, context[CpuRegister.Rax]);
        Assert.Equal(InitialErrno, ReadErrno(memory));
    }

    [Fact]
    public void Strtoull_Base36AcceptsUpperAndLowerCaseDigits()
    {
        var (memory, context) = CreateContext();
        memory.WriteCString(InputAddress, "Zz!");
        ConfigureStrtoull(context, numberBase: 36);

        var result = LibcSearchAndConversionExports.LibcStrtoull(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(1295UL, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(EndPointerAddress, out var endPointer));
        Assert.Equal(InputAddress + 2, endPointer);
        Assert.Equal(InitialErrno, ReadErrno(memory));
    }

    [Fact]
    public void Strtoull_UnreadableInputReportsMemoryFault()
    {
        var (_, context) = CreateContext();
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = EndPointerAddress;
        context[CpuRegister.Rdx] = 10;

        var result = LibcSearchAndConversionExports.LibcStrtoull(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateContext()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x2000);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            FsBase = MemoryBase,
        };
        WriteErrno(memory, InitialErrno);
        return (memory, context);
    }

    private static void ConfigureStrtoull(CpuContext context, uint numberBase)
    {
        context[CpuRegister.Rdi] = InputAddress;
        context[CpuRegister.Rsi] = EndPointerAddress;
        context[CpuRegister.Rdx] = numberBase;
    }

    private static void WriteErrno(FakeCpuMemory memory, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(ErrnoAddress, bytes));
    }

    private static int ReadErrno(FakeCpuMemory memory)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        Assert.True(memory.TryRead(ErrnoAddress, bytes));
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private sealed class FailNthWriteMemory : ICpuMemory
    {
        private readonly ICpuMemory _inner;
        private readonly ulong _targetAddress;
        private readonly int _failOnWrite;

        public FailNthWriteMemory(ICpuMemory inner, ulong targetAddress, int failOnWrite)
        {
            _inner = inner;
            _targetAddress = targetAddress;
            _failOnWrite = failOnWrite;
        }

        public int TargetWriteCount { get; private set; }

        public bool TryRead(ulong virtualAddress, Span<byte> destination) =>
            _inner.TryRead(virtualAddress, destination);

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (virtualAddress == _targetAddress && ++TargetWriteCount == _failOnWrite)
            {
                return false;
            }

            return _inner.TryWrite(virtualAddress, source);
        }
    }

    private sealed class BsearchTestScheduler : IGuestThreadScheduler
    {
        private readonly ulong _comparatorAddress;
        private readonly bool _fail;

        public BsearchTestScheduler(ulong comparatorAddress, bool fail = false)
        {
            _comparatorAddress = comparatorAddress;
            _fail = fail;
        }

        public List<ulong> CandidateAddresses { get; } = new();

        public bool SupportsGuestContextTransfer => false;

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context)
        {
        }

        public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryJoinThread(
            CpuContext callerContext,
            ulong threadHandle,
            out ulong returnValue,
            out string? error)
        {
            returnValue = 0;
            error = "not supported";
            return false;
        }

        public void Pump(CpuContext callerContext, string reason)
        {
        }

        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => 0;

        public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority) => false;

        public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask) => false;

        public bool TrySuspendGuestThread(ulong guestThreadHandle, out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryResumeGuestThread(ulong guestThreadHandle, out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryGetSuspendedGuestThreadContext(
            ulong guestThreadHandle,
            out GuestCpuContinuation continuation,
            out string? error)
        {
            continuation = default;
            error = "not supported";
            return false;
        }

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => Array.Empty<GuestThreadSnapshot>();

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out string? error) =>
            TryCallGuestFunction(
                callerContext,
                entryPoint,
                arg0,
                arg1,
                0,
                stackAddress,
                stackSize,
                reason,
                out _,
                out error);

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error)
        {
            CandidateAddresses.Add(arg1);
            if (_fail || entryPoint != _comparatorAddress ||
                !callerContext.TryReadUInt64(arg0, out var key) ||
                !callerContext.TryReadUInt64(arg1, out var candidate))
            {
                returnValue = 0;
                error = "comparator failed";
                return false;
            }

            returnValue = unchecked((uint)key.CompareTo(candidate));
            error = null;
            return true;
        }

        public bool TryCallGuestContinuation(
            CpuContext callerContext,
            GuestCpuContinuation continuation,
            string reason,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryRaiseGuestException(
            CpuContext callerContext,
            ulong threadHandle,
            ulong handler,
            int exceptionType,
            out string? error)
        {
            error = "not supported";
            return false;
        }
    }
}
