// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanHostPresentationPacingTests
{
    [Fact]
    public void ImmediateModeWaitsForConfiguredRefreshDeadline()
    {
        Assert.False(VulkanVideoPresenter.IsHostPresentationDue(false, 60, 99, 100));
        Assert.True(VulkanVideoPresenter.IsHostPresentationDue(false, 60, 100, 100));
    }

    [Fact]
    public void VSyncAndAutomaticRefreshDoNotUseSoftwareLimiter()
    {
        Assert.True(VulkanVideoPresenter.IsHostPresentationDue(true, 60, 1, 100));
        Assert.True(VulkanVideoPresenter.IsHostPresentationDue(false, 0, 1, 100));
    }

    [Fact]
    public void NextDeadlineUsesConfiguredRefreshInterval()
    {
        var next = VulkanVideoPresenter.NextHostPresentationDeadline(60, 1_000, 0);

        Assert.Equal(1_000 + Stopwatch.Frequency / 60, next);
    }
}
