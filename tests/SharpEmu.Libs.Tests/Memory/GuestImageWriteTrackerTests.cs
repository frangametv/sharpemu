// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

[CollectionDefinition("GuestImageWriteTracker", DisableParallelization = true)]
public sealed class GuestImageWriteTrackerTestCollection
{
}

[Collection("GuestImageWriteTracker")]
public sealed unsafe class GuestImageWriteTrackerTests
{
    private const nuint PageSize = 4096;
    private const nuint HostPageAlignment = 16384;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(
        nint lpAddress,
        nuint dwSize,
        uint flAllocationType,
        uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualFree(nint lpAddress, nuint dwSize, uint dwFreeType);

    private static byte* AllocateTrackedPages()
    {
        if (OperatingSystem.IsWindows())
        {
            var windowsAllocation = VirtualAlloc(
                0,
                2 * HostPageAlignment,
                MemCommit | MemReserve,
                PageReadWrite);
            Assert.NotEqual(nint.Zero, windowsAllocation);
            return (byte*)windowsAllocation;
        }

        // The tracker uses 4 KiB guest pages, while mprotect uses the host page
        // size (16 KiB on Apple Silicon). Keep kernel rounding inside memory
        // owned by this test process.
        return (byte*)NativeMemory.AlignedAlloc(2 * HostPageAlignment, HostPageAlignment);
    }

    [Fact]
    public void TrackedCpuMemoryManagedWriteMarksTrackedImageDirty()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var page = AllocateTrackedPages();
        Assert.NotEqual(nint.Zero, (nint)page);
        new Span<byte>(page, checked((int)PageSize)).Clear();
        var address = (ulong)page;
        var memory = new TrackedCpuMemory(
            new PointerCpuMemory(address, checked((ulong)PageSize)));

        GuestImageWriteTracker.Track(address, checked((ulong)PageSize));
        try
        {
            Assert.True(memory.TryWrite(address + 128, [1, 2, 3, 4]));
            Assert.True(GuestImageWriteTracker.ConsumeDirty(address));
            Assert.False(GuestImageWriteTracker.ConsumeDirty(address));

            Span<byte> actual = stackalloc byte[4];
            Assert.True(memory.TryRead(address + 128, actual));
            Assert.Equal([1, 2, 3, 4], actual.ToArray());

            GuestImageWriteTracker.Rearm(address);
            Assert.True(memory.TryWrite(address + 256, [5]));
            Assert.True(GuestImageWriteTracker.ConsumeDirty(address));
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(page);
        }
    }

    [Fact]
    public void ManagedWriteMarksEveryTrackedOwnerOfSharedPageDirty()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var page = AllocateTrackedPages();
        Assert.NotEqual(nint.Zero, (nint)page);
        new Span<byte>(page, checked((int)PageSize)).Clear();
        var firstAddress = (ulong)page + 64;
        var secondAddress = (ulong)page + 2048;

        GuestImageWriteTracker.Track(firstAddress, 512);
        GuestImageWriteTracker.Track(secondAddress, 512);
        try
        {
            GuestImageWriteTracker.NotifyManagedWrite(secondAddress + 32, 1);

            Assert.True(GuestImageWriteTracker.ConsumeDirty(firstAddress));
            Assert.True(GuestImageWriteTracker.ConsumeDirty(secondAddress));
        }
        finally
        {
            GuestImageWriteTracker.Untrack(firstAddress);
            GuestImageWriteTracker.Untrack(secondAddress);
            FreeTrackedPages(page);
        }
    }

    [Fact]
    public void FirstWriteContextIsPreservedUntilTheRangeIsRearmed()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var page = AllocateTrackedPages();
        Assert.NotEqual(nint.Zero, (nint)page);
        new Span<byte>(page, checked((int)PageSize)).Clear();
        var address = (ulong)page;
        var expected = new GuestWriteFaultContext(
            InstructionAddress: 0x800253858,
            Rax: 0x3000,
            Rcx: 1,
            Rdx: 0,
            R12: 0x401234000,
            R13: 2,
            R14: 0x400ABC000,
            R15: address);

        GuestImageWriteTracker.Track(address, checked((ulong)PageSize));
        try
        {
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address, expected));
            Assert.True(GuestImageWriteTracker.TryGetFirstCpuWriteContext(address, out var actual));
            Assert.Equal(expected, actual);
            Assert.True(GuestImageWriteTracker.TryGetFirstCpuWriteInfo(address, out var info));
            Assert.Equal(address, info.Address);
            Assert.Equal(address & ~0xFFFUL, info.Page);
            Assert.Equal(expected, info.Context);

            Assert.True(GuestImageWriteTracker.ConsumeDirty(address));
            Assert.True(GuestImageWriteTracker.TryGetFirstCpuWriteContext(address, out actual));
            Assert.Equal(expected, actual);

            GuestImageWriteTracker.Rearm(address);
            Assert.False(GuestImageWriteTracker.TryGetFirstCpuWriteContext(address, out _));
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(page);
        }
    }

    private static void FreeTrackedPages(void* allocation)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = VirtualFree((nint)allocation, 0, MemRelease);
            return;
        }

        NativeMemory.Free(allocation);
    }

    [Fact]
    public void GenerationSurvivesDirtyConsume()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var allocation = AllocateTrackedPages();
        Assert.NotEqual(nint.Zero, (nint)allocation);
        var address = (ulong)allocation;
        try
        {
            GuestImageWriteTracker.Track(address, PageSize);
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation));
            Assert.Equal(0, generation);

            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            Assert.True(GuestImageWriteTracker.ConsumeDirty(address));
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out generation));
            Assert.Equal(1, generation);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void GenerationIncrementsOncePerArmedLifetime()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var allocation = AllocateTrackedPages();
        Assert.NotEqual(nint.Zero, (nint)allocation);
        var address = (ulong)allocation;
        try
        {
            GuestImageWriteTracker.Track(address, PageSize);
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation));
            Assert.Equal(1, generation);

            GuestImageWriteTracker.Rearm(address);
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out generation));
            Assert.Equal(2, generation);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void GenerationCarriesAcrossRangeReplacement()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var allocation = AllocateTrackedPages();
        Assert.NotEqual(nint.Zero, (nint)allocation);
        var address = (ulong)allocation;
        try
        {
            GuestImageWriteTracker.Track(address, PageSize);
            Assert.True(GuestImageWriteTracker.TryHandleWriteFault(address));

            GuestImageWriteTracker.Track(address, 2 * PageSize);
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation));
            Assert.Equal(1, generation);
        }
        finally
        {
            GuestImageWriteTracker.Untrack(address);
            FreeTrackedPages(allocation);
        }
    }

    [Fact]
    public void UntrackedAddressHasNoGeneration()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        Assert.False(GuestImageWriteTracker.TryGetWriteGeneration(0xDEAD_0000_0000UL, out _));
    }

    private sealed class PointerCpuMemory(ulong baseAddress, ulong length) : ICpuMemory
    {
        private readonly ulong _baseAddress = baseAddress;
        private readonly ulong _length = length;

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!Contains(virtualAddress, destination.Length))
            {
                return false;
            }

            new ReadOnlySpan<byte>((void*)virtualAddress, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!Contains(virtualAddress, source.Length))
            {
                return false;
            }

            source.CopyTo(new Span<byte>((void*)virtualAddress, source.Length));
            return true;
        }

        private bool Contains(ulong virtualAddress, int byteCount)
        {
            var length = checked((ulong)byteCount);
            return virtualAddress >= _baseAddress &&
                length <= _length &&
                virtualAddress - _baseAddress <= _length - length;
        }
    }
}
