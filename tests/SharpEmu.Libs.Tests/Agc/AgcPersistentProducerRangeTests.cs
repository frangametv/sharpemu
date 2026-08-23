// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcPersistentProducerRangeTests
{
    [Theory]
    [InlineData(0x1000UL, 16u, 0x1010UL, 4u, true)]
    [InlineData(0x1000UL, 16u, 0x1000UL, 16u, true)]
    [InlineData(0x1000UL, 16u, 0x103CUL, 2u, false)]
    [InlineData(0x1000UL, 16u, 0x0FFCUL, 1u, false)]
    public void SubmissionContainmentUsesCompleteByteRanges(
        ulong submissionAddress,
        uint submissionDwords,
        ulong candidateAddress,
        uint candidateDwords,
        bool expected)
    {
        Assert.Equal(
            expected,
            AgcExports.SubmissionContainsRange(
                submissionAddress,
                submissionDwords,
                candidateAddress,
                candidateDwords));
    }

    [Fact]
    public void SubmissionContainmentRejectsOverflow()
    {
        Assert.False(
            AgcExports.SubmissionContainsRange(
                ulong.MaxValue - 3,
                2,
                ulong.MaxValue - 3,
                1));
    }
}
