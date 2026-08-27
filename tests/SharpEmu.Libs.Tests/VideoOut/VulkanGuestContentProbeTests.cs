// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestContentProbeTests
{
    [Fact]
    public void LargeSurfaceSamplesAcrossEntireAllocation()
    {
        const ulong size = 8UL * 1024 * 1024;

        Assert.Equal(0UL, VulkanVideoPresenter.GetSparseGuestContentProbeOffset(size, 0, 64));
        Assert.InRange(
            VulkanVideoPresenter.GetSparseGuestContentProbeOffset(size, 16, 64),
            size / 5,
            size / 3);
        Assert.Equal(
            size - 64,
            VulkanVideoPresenter.GetSparseGuestContentProbeOffset(size, 63, 64));
    }

    [Fact]
    public void SmallSurfaceUsesSingleSafeOffset()
    {
        Assert.Equal(0UL, VulkanVideoPresenter.GetSparseGuestContentProbeOffset(32, 0, 1));
    }
}
