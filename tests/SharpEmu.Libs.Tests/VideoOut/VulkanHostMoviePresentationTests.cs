// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanHostMoviePresentationTests
{
    [Fact]
    public void PresentsMovieOnlyFrameWhenPlaybackAdvances()
    {
        Assert.True(VulkanVideoPresenter.ShouldPresentHostMovieOnlyFrame(true));
    }

    [Fact]
    public void DoesNotResubmitSameMovieFrameOnEveryRenderTick()
    {
        Assert.False(VulkanVideoPresenter.ShouldPresentHostMovieOnlyFrame(false));
    }
}
