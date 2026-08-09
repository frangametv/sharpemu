// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class GpuWaitRegistryProducerlessRecoveryTests : IDisposable
{
    private readonly object _memory = new();

    [Fact]
    public void RecoversOnlyExpiredGen5AgcEqualityWaitWithoutProducer()
    {
        Register(isStandard: false, is64Bit: true, mask: uint.MaxValue, reference: 1);

        var recovered = GpuWaitRegistry.SnapshotExpiredProducerlessAgcWaitCandidates(
            _memory,
            nowTicks: 10_000,
            minAgeTicks: 5_000);

        var waiter = Assert.Single(recovered!);
        Assert.Equal(0x4020_2D00UL, waiter.WaitAddress);
        Assert.True(GpuWaitRegistry.TryRemove(waiter));
        Assert.Equal(0, GpuWaitRegistry.CountForMemory(_memory));
    }

    [Theory]
    [InlineData(true, true, 0xFFFF_FFFFUL, 1UL)]
    [InlineData(false, false, 0xFFFF_FFFFUL, 1UL)]
    [InlineData(false, true, ulong.MaxValue, 1UL)]
    [InlineData(false, true, 0xFFFF_FFFFUL, 2UL)]
    public void KeepsWaitsOutsideNarrowRecoveryShape(
        bool isStandard,
        bool is64Bit,
        ulong mask,
        ulong reference)
    {
        Register(isStandard, is64Bit, mask, reference);

        var recovered = GpuWaitRegistry.SnapshotExpiredProducerlessAgcWaitCandidates(
            _memory,
            nowTicks: 10_000,
            minAgeTicks: 5_000);

        Assert.Null(recovered);
        Assert.Equal(1, GpuWaitRegistry.CountForMemory(_memory));
    }

    [Fact]
    public void KeepsMatchingWaitWhenTimeoutHasNotElapsed()
    {
        Register(isStandard: false, is64Bit: true, mask: uint.MaxValue, reference: 1);

        Assert.Null(GpuWaitRegistry.SnapshotExpiredProducerlessAgcWaitCandidates(
            _memory,
            nowTicks: 4_999,
            minAgeTicks: 5_000));
        Assert.Equal(1, GpuWaitRegistry.CountForMemory(_memory));
    }

    private void Register(bool isStandard, bool is64Bit, ulong mask, ulong reference)
    {
        GpuWaitRegistry.Register(0x4020_2D00, new GpuWaitRegistry.WaitingDcb
        {
            Memory = _memory,
            WaitAddress = 0x4020_2D00,
            IsStandard = isStandard,
            Is64Bit = is64Bit,
            CompareFunction = 3,
            Mask = mask,
            ReferenceValue = reference,
            RegisteredTicks = 0,
        });
    }

    public void Dispose()
    {
        GpuWaitRegistry.CollectAllForMemory(_memory);
    }
}
