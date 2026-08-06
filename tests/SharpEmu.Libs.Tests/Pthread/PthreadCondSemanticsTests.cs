// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

// POSIX condition variables are edges, not semaphore credits. A signal with no waiter
// must have no effect. This was violated by the previous implementation which persisted
// signals via PendingSignals, causing lock inversions and predicate bypasses.
// See issue #113.
public sealed class PthreadCondSemanticsTests
{
    private const ulong MemoryBase = 0x2_0000_0000;
    private const ulong MutexAddress = MemoryBase + 0x100;
    private const ulong CondAddress = MemoryBase + 0x200;

    [Fact]
    public void PthreadCondState_DoesNotHavePendingSignals()
    {
        // Verify that PthreadCondState no longer has the PendingSignals property.
        // This is a regression test to ensure the POSIX-correct behavior is maintained.
        var stateType = typeof(KernelPthreadCompatExports).GetNestedType("PthreadCondState", BindingFlags.NonPublic);
        Assert.NotNull(stateType);

        var pendingSignalsProp = stateType.GetProperty("PendingSignals");
        Assert.Null(pendingSignalsProp);

        var signalsPendingProp = stateType.GetProperty("SignalsPending");
        Assert.Null(signalsPendingProp);

        var tryConsumeMethod = stateType.GetMethod("TryConsumePendingSignal");
        Assert.Null(tryConsumeMethod);
    }

    [Fact]
    public void PthreadCondSignal_WithNoWaiter_DoesNotPersist()
    {
        var state = new KernelPthreadCompatExports.PthreadCondState();
        Assert.Equal(0UL, state.SignalEpoch);
        Assert.Equal(0, state.Waiters);

        // Simulate signal with no waiter (this would have incremented PendingSignals before)
        lock (state.SyncRoot)
        {
            state.SignalEpoch++;
            Assert.Equal(0, state.AssignSignals(broadcast: false));
        }

        // Verify epoch advanced but no persistent signal state
        Assert.Equal(1UL, state.SignalEpoch);

        // A waiter arriving later must not inherit that signal.
        var lateWaiter = new KernelPthreadCompatExports.PthreadCondWaiter { ThreadId = 1 };
        lock (state.SyncRoot)
        {
            state.Enqueue(lateWaiter);
            Assert.False(lateWaiter.Signaled);
            Assert.Equal(1, state.Waiters);
            state.Remove(lateWaiter);
        }
    }

    [Fact]
    public void PthreadCondSignal_IsAssignedToAnExistingWaiter()
    {
        var state = new KernelPthreadCompatExports.PthreadCondState();
        var first = new KernelPthreadCompatExports.PthreadCondWaiter { ThreadId = 1 };
        var second = new KernelPthreadCompatExports.PthreadCondWaiter { ThreadId = 2 };
        var late = new KernelPthreadCompatExports.PthreadCondWaiter { ThreadId = 3 };

        lock (state.SyncRoot)
        {
            state.Enqueue(first);
            state.Enqueue(second);

            Assert.Equal(1, state.AssignSignals(broadcast: false));
            Assert.True(first.Signaled);
            Assert.False(second.Signaled);

            // A waiter that arrives after the signal cannot steal the assigned
            // wake before the selected host thread gets scheduled.
            state.Enqueue(late);
            Assert.False(late.Signaled);

            Assert.Equal(1, state.AssignSignals(broadcast: false));
            Assert.True(second.Signaled);
            Assert.False(late.Signaled);

            Assert.Equal(1, state.AssignSignals(broadcast: true));
            Assert.True(late.Signaled);
        }
    }

    [Fact]
    public void PthreadCondRelativeTimedwait_RegistersAndReturnsPosixTimeout()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(manager.TryGetExport("K953PF5u6Pc", out var export));
        Assert.Equal("pthread_cond_reltimedwait_np", export.Name);

        var context = new CpuContext(new AllocatingPthreadMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = MutexAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexInit(context));

        context[CpuRegister.Rdi] = CondAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondInit(context));

        context[CpuRegister.Rdi] = MutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexLock(context));

        context[CpuRegister.Rdi] = CondAddress;
        context[CpuRegister.Rsi] = MutexAddress;
        context[CpuRegister.Rdx] = 0;
        Assert.Equal(60, KernelPthreadCompatExports.PosixPthreadCondRelativeTimedwait(context));

        context[CpuRegister.Rdi] = MutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexUnlock(context));

        context[CpuRegister.Rdi] = CondAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondDestroy(context));
        context[CpuRegister.Rdi] = MutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexDestroy(context));
    }

    private sealed class AllocatingPthreadMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _storage;
        private ulong _nextAllocation;

        public AllocatingPthreadMemory(ulong baseAddress, int size)
        {
            _baseAddress = baseAddress;
            _storage = new byte[size];
            _nextAllocation = baseAddress + 0x400;
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = 0;
            if (size == 0 || alignment == 0 || (alignment & (alignment - 1)) != 0)
            {
                return false;
            }

            var aligned = (_nextAllocation + alignment - 1) & ~(alignment - 1);
            if (size > int.MaxValue || !TryResolve(aligned, (int)size, out _))
            {
                return false;
            }

            address = aligned;
            _nextAllocation = aligned + size;
            return true;
        }

        public bool TryFreeGuestMemory(ulong address) => true;

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < _baseAddress || length < 0)
            {
                return false;
            }

            var relative = virtualAddress - _baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
