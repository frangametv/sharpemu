// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanLiveDisplayTargetTests
{
    [Fact]
    public void PublishesWhenNoOrderedFlipHasBeenObserved()
    {
        Assert.True(VulkanVideoPresenter.ShouldPublishLiveDisplayTarget(0));
    }

    [Fact]
    public void SuppressesIntermediateDrawDuringActiveOrderedFlipStream()
    {
        Assert.False(VulkanVideoPresenter.ShouldPublishLiveDisplayTarget(2));
    }

    [Fact]
    public void RetainsFallbackForSingleInitialOrderedFlip()
    {
        Assert.True(VulkanVideoPresenter.ShouldPublishLiveDisplayTarget(1));
    }
}
