// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class GuestBlendStateNormalizerTests
{
    [Fact]
    public void EmptyBlendListDefaultsEveryColorAttachment()
    {
        var normalized = GuestBlendStateNormalizer.NormalizeIntegerAttachments(
            [],
            [false, true],
            out var normalizedCount);

        Assert.Equal(2, normalizedCount);
        Assert.Equal(
            [GuestBlendState.Default, GuestBlendState.Default],
            normalized);
    }

    [Fact]
    public void NonEmptyMismatchedBlendListStillFailsClosed()
    {
        Assert.Throws<ArgumentException>(() =>
            GuestBlendStateNormalizer.NormalizeIntegerAttachments(
                [GuestBlendState.Default],
                [false, false],
                out _));
    }

    [Fact]
    public void IntegerAttachmentDisablesConfiguredBlending()
    {
        var configured = GuestBlendState.Default with { Enable = true };

        var normalized = GuestBlendStateNormalizer.NormalizeIntegerAttachments(
            [configured],
            [true],
            out var normalizedCount);

        Assert.Equal(1, normalizedCount);
        Assert.Equal(GuestBlendState.Default, normalized[0]);
    }
}
