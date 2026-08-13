// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class GpuWaitRegistryProducedRetentionTests
{
    private const ulong WatchedLabel = 0x7020_0000_1000UL;

    // A suspended DCB whose label the guest has recycled can only be released by
    // replaying the value a real producer wrote to that label. Recording enough
    // unrelated producers to cross the table's soft bound must not discard that
    // value, or the waiter is stranded and the graphics queue never resumes.
    [Fact]
    public void ProducedValueSurvivesBoundCrossingWhileAWaiterWatchesIt()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();

        GpuWaitRegistry.Register(WatchedLabel, NewWaiter(memory, WatchedLabel));
        Assert.True(GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 1));

        // Cross the soft bound with labels nobody is waiting on.
        for (var i = 0; i < 9000; i++)
        {
            GpuWaitRegistry.RecordProduced(memory, 0x7030_0000_0000UL + ((ulong)i * 8), 1);
        }

        // The guest has since recycled the label, so its memory no longer holds
        // the produced value — the registry's record is the only way back.
        var broken = GpuWaitRegistry.CollectDeadlockBroken(memory, nowTicks: 1_000_000, minAgeTicks: 1);

        Assert.NotNull(broken);
        Assert.Contains(broken!, waiter => waiter.WaitAddress == WatchedLabel);
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void UnwatchedProducedValuesArePrunedAtTheBound()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();

        for (var i = 0; i < 9000; i++)
        {
            GpuWaitRegistry.RecordProduced(memory, 0x7030_0000_0000UL + ((ulong)i * 8), 1);
        }

        // Nothing was watching any of them, so a waiter registered afterwards on
        // a pruned label has no produced value to replay and stays suspended.
        GpuWaitRegistry.Register(WatchedLabel, NewWaiter(memory, WatchedLabel));
        var broken = GpuWaitRegistry.CollectDeadlockBroken(memory, nowTicks: 1_000_000, minAgeTicks: 1);

        Assert.Null(broken);
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void ProducedEdgeCanSatisfyExactlyOneLaterWait()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        var waiter = NewWaiter(memory, WatchedLabel);

        Assert.False(GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 1));
        Assert.True(GpuWaitRegistry.TryConsumeProducedSatisfaction(waiter));
        Assert.False(GpuWaitRegistry.TryConsumeProducedSatisfaction(waiter));

        // A new real write creates a fresh one-shot edge.
        Assert.False(GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 1));
        Assert.True(GpuWaitRegistry.TryConsumeProducedSatisfaction(waiter));
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void ProducerThatLatchedLiveWaitHasNoFutureCredit()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        var waiter = NewWaiter(memory, WatchedLabel);
        GpuWaitRegistry.Register(WatchedLabel, waiter);

        Assert.True(GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 1));
        Assert.False(GpuWaitRegistry.TryConsumeProducedSatisfaction(waiter));
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void RecurrentAgcDeadlockUsesShortThresholdOnlyAfterFirstBreak()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        var waiter = NewWaiter(memory, WatchedLabel);
        waiter.Is64Bit = true;

        GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 1);
        GpuWaitRegistry.Register(WatchedLabel, waiter);

        // The first encounter cannot use the learned 10-tick threshold.
        Assert.Null(GpuWaitRegistry.CollectDeadlockBroken(
            memory, nowTicks: 50, minAgeTicks: 100, learnedMinAgeTicks: 10));

        var first = GpuWaitRegistry.CollectDeadlockBroken(
            memory, nowTicks: 100, minAgeTicks: 100, learnedMinAgeTicks: 10);
        Assert.NotNull(first);
        Assert.False(Assert.Single(first!).UsedLearnedDeadlockRecovery);

        waiter.RegisteredTicks = 100;
        GpuWaitRegistry.Register(WatchedLabel, waiter);
        var recurrent = GpuWaitRegistry.CollectDeadlockBroken(
            memory, nowTicks: 110, minAgeTicks: 100, learnedMinAgeTicks: 10);

        Assert.NotNull(recurrent);
        Assert.True(Assert.Single(recurrent!).UsedLearnedDeadlockRecovery);
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void StandardWaitNeverLearnsShortDeadlockThreshold()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        var waiter = NewWaiter(memory, WatchedLabel);
        waiter.Is64Bit = true;
        waiter.IsStandard = true;

        GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 1);
        GpuWaitRegistry.Register(WatchedLabel, waiter);
        Assert.NotNull(GpuWaitRegistry.CollectDeadlockBroken(
            memory, nowTicks: 100, minAgeTicks: 100, learnedMinAgeTicks: 10));

        waiter.RegisteredTicks = 100;
        GpuWaitRegistry.Register(WatchedLabel, waiter);
        Assert.Null(GpuWaitRegistry.CollectDeadlockBroken(
            memory, nowTicks: 110, minAgeTicks: 100, learnedMinAgeTicks: 10));
        GpuWaitRegistry.Clear();
    }

    private static GpuWaitRegistry.WaitingDcb NewWaiter(object memory, ulong address) => new()
    {
        WaitAddress = address,
        ReferenceValue = 1,
        Mask = 0xFFFF_FFFFUL,
        CompareFunction = 3, // equal
        Memory = memory,
        QueueName = "dcb.graphics",
        RegisteredTicks = 0,
    };
}
