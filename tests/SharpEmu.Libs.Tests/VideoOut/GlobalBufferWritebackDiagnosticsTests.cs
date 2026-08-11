// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class GlobalBufferWritebackDiagnosticsTests
{
    [Fact]
    public void AddressSamplesUseExactInteriorOffsetsAndClampAtTheEnd()
    {
        var bytes = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();

        var samples = GlobalBufferWritebackDiagnostics.SampleAddresses(
            bytes,
            0x1000,
            [0x0FFF, 0x1004, 0x100F, 0x1010],
            maximumBytes: 4);

        Assert.Collection(
            samples,
            sample =>
            {
                Assert.Equal(0x1004UL, sample.Address);
                Assert.Equal(4, sample.Offset);
                Assert.Equal([4, 5, 6, 7], sample.Bytes);
            },
            sample =>
            {
                Assert.Equal(0x100FUL, sample.Address);
                Assert.Equal(15, sample.Offset);
                Assert.Equal([15], sample.Bytes);
            });
    }

    [Fact]
    public void AddressSamplesRejectNonPositiveMaximumLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GlobalBufferWritebackDiagnostics.SampleAddresses(
                [1],
                0x1000,
                [0x1000],
                maximumBytes: 0));
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(4, 0, true)]
    [InlineData(5, 0, false)]
    [InlineData(64, 0, true)]
    [InlineData(65, 1, true)]
    public void AddressFilteredTraceKeepsEarlyPeriodicAndChangedSamples(
        int occurrence,
        long changedBytes,
        bool expected)
    {
        Assert.Equal(
            expected,
            GlobalBufferWritebackDiagnostics.ShouldEmitAddressFilteredTrace(
                occurrence,
                changedBytes));
    }

    [Fact]
    public void ContentSummaryReportsSparseExtentAndHash()
    {
        var bytes = new byte[] { 0, 3, 0, 0, 7, 0 };

        var summary = GlobalBufferWritebackDiagnostics.Summarize(bytes);

        Assert.Equal(2, summary.NonzeroBytes);
        Assert.Equal(1, summary.FirstNonzeroOffset);
        Assert.Equal(4, summary.LastNonzeroOffset);
        Assert.NotEqual(0UL, summary.Hash);
    }

    [Fact]
    public void SummarizesEntireCurrentRangeAgainstPreviousSnapshot()
    {
        var summary = GlobalBufferWritebackDiagnostics.Summarize(
            [0, 2, 0, 4],
            [0, 1, 0, 4]);

        Assert.Equal(2, summary.NonzeroBytes);
        Assert.Equal(1, summary.ChangedBytes);
        Assert.Equal(1, summary.FirstChangedOffset);
        Assert.Equal(0x3BD27C7F93FE0FD3UL, summary.Hash);
    }

    [Fact]
    public void IdenticalZeroRangesReportNoChanges()
    {
        var summary = GlobalBufferWritebackDiagnostics.Summarize(
            new byte[8],
            new byte[8]);

        Assert.Equal(0, summary.NonzeroBytes);
        Assert.Equal(0, summary.ChangedBytes);
        Assert.Equal(-1, summary.FirstChangedOffset);
        Assert.Equal(0xA8C7F832281A39C5UL, summary.Hash);
    }

    [Fact]
    public void DetectsAChangeBeyondTheOldHeadProbeWindow()
    {
        var current = new byte[1024];
        var previous = new byte[1024];
        current[^1] = 7;

        var summary = GlobalBufferWritebackDiagnostics.Summarize(current, previous);

        Assert.Equal(1, summary.NonzeroBytes);
        Assert.Equal(1, summary.ChangedBytes);
        Assert.Equal(1023, summary.FirstChangedOffset);
    }

    [Fact]
    public void RejectsSnapshotsWithDifferentLengths()
    {
        Assert.Throws<ArgumentException>(() =>
            GlobalBufferWritebackDiagnostics.Summarize([1], [1, 2]));
    }
}
