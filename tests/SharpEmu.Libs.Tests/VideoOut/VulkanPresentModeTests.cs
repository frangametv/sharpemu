// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPresentModeTests
{
    [Fact]
    public void VSyncAlwaysUsesFifoEvenWhenMailboxIsAvailable()
    {
        var modes = new[]
        {
            PresentModeKHR.MailboxKhr,
            PresentModeKHR.ImmediateKhr,
            PresentModeKHR.FifoKhr,
        };

        Assert.Equal(
            PresentModeKHR.FifoKhr,
            VulkanVideoPresenter.SelectPresentMode(vsync: true, modes));
    }

    [Fact]
    public void VSyncOffPrefersImmediate()
    {
        var modes = new[]
        {
            PresentModeKHR.FifoKhr,
            PresentModeKHR.MailboxKhr,
            PresentModeKHR.ImmediateKhr,
        };

        Assert.Equal(
            PresentModeKHR.ImmediateKhr,
            VulkanVideoPresenter.SelectPresentMode(vsync: false, modes));
    }

    [Fact]
    public void VSyncOffFallsBackToMailboxThenFifo()
    {
        Assert.Equal(
            PresentModeKHR.MailboxKhr,
            VulkanVideoPresenter.SelectPresentMode(
                vsync: false,
                new[] { PresentModeKHR.FifoKhr, PresentModeKHR.MailboxKhr }));
        Assert.Equal(
            PresentModeKHR.FifoKhr,
            VulkanVideoPresenter.SelectPresentMode(
                vsync: false,
                new[] { PresentModeKHR.FifoKhr }));
    }
}
