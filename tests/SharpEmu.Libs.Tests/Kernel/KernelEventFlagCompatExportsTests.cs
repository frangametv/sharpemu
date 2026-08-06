// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelEventFlagCompatExportsTests
{
    [Fact]
    public void KernelCreateEventFlag_WritesHandleToLibcBackedPointer()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong nameAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(nameAddress, "VoidWorkerEvent");
        var handleAddress = AllocateTracked(context, sizeof(ulong));
        ulong handle = 0;
        try
        {
            Marshal.WriteInt64(unchecked((nint)handleAddress), 0);
            context[CpuRegister.Rdi] = handleAddress;
            context[CpuRegister.Rsi] = nameAddress;
            context[CpuRegister.Rdx] = 0x11;
            context[CpuRegister.Rcx] = 0;
            context[CpuRegister.R8] = 0;

            Assert.Equal(0, KernelEventFlagCompatExports.KernelCreateEventFlag(context));
            handle = unchecked((ulong)Marshal.ReadInt64(unchecked((nint)handleAddress)));
            Assert.NotEqual(0UL, handle);
        }
        finally
        {
            if (handle != 0)
            {
                context[CpuRegister.Rdi] = handle;
                Assert.Equal(0, KernelEventFlagCompatExports.KernelDeleteEventFlag(context));
            }

            FreeTracked(context, handleAddress);
        }
    }

    [Fact]
    public void KernelCreateEventFlag_RejectsUnmappedOutputPointer()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong nameAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(nameAddress, "InvalidOutput");
        context[CpuRegister.Rdi] = 8;
        context[CpuRegister.Rsi] = nameAddress;
        context[CpuRegister.Rdx] = 0x11;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelEventFlagCompatExports.KernelCreateEventFlag(context));
    }

    [Fact]
    public void KernelWaitEventFlag_ZeroTimeoutReturnsTimedOutImmediately()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong nameAddress = memoryBase + 0x100;
        const ulong handleAddress = memoryBase + 0x180;
        const ulong resultAddress = memoryBase + 0x200;
        const ulong timeoutAddress = memoryBase + 0x208;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(nameAddress, "InstantPoll");
        Assert.True(context.TryWriteUInt32(timeoutAddress, 0));
        context[CpuRegister.Rdi] = handleAddress;
        context[CpuRegister.Rsi] = nameAddress;
        context[CpuRegister.Rdx] = 0x11;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        Assert.Equal(0, KernelEventFlagCompatExports.KernelCreateEventFlag(context));
        Assert.True(context.TryReadUInt64(handleAddress, out var handle));

        try
        {
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = 1;
            context[CpuRegister.Rcx] = resultAddress;
            context[CpuRegister.R8] = timeoutAddress;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
                KernelEventFlagCompatExports.KernelWaitEventFlag(context));
            Assert.True(context.TryReadUInt32(timeoutAddress, out var remainingTimeout));
            Assert.Equal(0U, remainingTimeout);
        }
        finally
        {
            context[CpuRegister.Rdi] = handle;
            Assert.Equal(0, KernelEventFlagCompatExports.KernelDeleteEventFlag(context));
        }
    }

    [Fact]
    public void KernelWaitEventFlag_PositiveTimeoutClearsOutputsWithoutPumpingScheduler()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong nameAddress = memoryBase + 0x100;
        const ulong handleAddress = memoryBase + 0x180;
        const ulong resultAddress = memoryBase + 0x200;
        const ulong timeoutAddress = memoryBase + 0x208;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(nameAddress, "TimedFallback");
        // A positive timeout must park the current host thread in place rather
        // than re-entering guest execution through the scheduler.
        Assert.True(context.TryWriteUInt32(timeoutAddress, 100_000));
        context[CpuRegister.Rdi] = handleAddress;
        context[CpuRegister.Rsi] = nameAddress;
        context[CpuRegister.Rdx] = 0x11;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        Assert.Equal(0, KernelEventFlagCompatExports.KernelCreateEventFlag(context));
        Assert.True(context.TryReadUInt64(handleAddress, out var handle));

        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 1;
        context[CpuRegister.Rcx] = resultAddress;
        context[CpuRegister.R8] = timeoutAddress;

        var previousScheduler = GuestThreadExecution.Scheduler;
        var scheduler = new CountingScheduler();
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
                KernelEventFlagCompatExports.KernelWaitEventFlag(context));
            Assert.Equal(0, scheduler.PumpCount);
            Assert.Equal(0, scheduler.MaxPumpDepth);
            Assert.True(context.TryReadUInt32(timeoutAddress, out var remainingTimeout));
            Assert.Equal(0U, remainingTimeout);
            Assert.True(context.TryReadUInt64(resultAddress, out var resultPattern));
            Assert.Equal(0UL, resultPattern);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
            context[CpuRegister.Rdi] = handle;
            Assert.Equal(0, KernelEventFlagCompatExports.KernelDeleteEventFlag(context));
        }
    }

    private static ulong AllocateTracked(CpuContext context, int length)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)length);
        Assert.Equal(0, KernelMemoryCompatExports.Malloc(context));
        Assert.NotEqual(0UL, context[CpuRegister.Rax]);
        return context[CpuRegister.Rax];
    }

    private static void FreeTracked(CpuContext context, ulong address)
    {
        context[CpuRegister.Rdi] = address;
        Assert.Equal(0, KernelMemoryCompatExports.Free(context));
    }

    private sealed class CountingScheduler : IGuestThreadScheduler
    {
        private readonly Action<CpuContext>? _onPump;
        private int _pumpDepth;

        public CountingScheduler(Action<CpuContext>? onPump = null)
        {
            _onPump = onPump;
        }

        public int PumpCount { get; private set; }

        public int MaxPumpDepth { get; private set; }

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
            PumpCount++;
            _pumpDepth++;
            MaxPumpDepth = Math.Max(MaxPumpDepth, _pumpDepth);
            try
            {
                _onPump?.Invoke(callerContext);
            }
            finally
            {
                _pumpDepth--;
            }
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

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => [];

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out string? error)
        {
            error = "not supported";
            return false;
        }

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
            returnValue = 0;
            error = "not supported";
            return false;
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
