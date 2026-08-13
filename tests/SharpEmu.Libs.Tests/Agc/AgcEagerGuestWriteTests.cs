// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcEagerGuestWriteTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void PacketWalkWriteDoesNotReleaseAnOrderedWaitEarly(
        bool hasActiveWait,
        bool isWatchedLabelWrite,
        bool expected)
    {
        Assert.Equal(
            expected,
            AgcExports.ShouldEagerlyApplyGuestWrite(
                hasActiveWait,
                isWatchedLabelWrite));
    }

    [Fact]
    public void WriteBackSnapshotPreservesWaiterWidth()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        try
        {
            GpuWaitRegistry.Register(
                0x1004,
                new GpuWaitRegistry.WaitingDcb
                {
                    Memory = memory,
                    WaitAddress = 0x1004,
                    Is64Bit = false,
                });
            GpuWaitRegistry.Register(
                0x1010,
                new GpuWaitRegistry.WaitingDcb
                {
                    Memory = memory,
                    WaitAddress = 0x1010,
                    Is64Bit = true,
                });

            var waiters = GpuWaitRegistry.SnapshotWaitersInRange(
                memory,
                0x1000,
                0x20);

            Assert.Equal(2, waiters.Count);
            Assert.Contains(waiters, waiter =>
                waiter.WaitAddress == 0x1004 && !waiter.Is64Bit);
            Assert.Contains(waiters, waiter =>
                waiter.WaitAddress == 0x1010 && waiter.Is64Bit);
        }
        finally
        {
            GpuWaitRegistry.Clear();
        }
    }
}
