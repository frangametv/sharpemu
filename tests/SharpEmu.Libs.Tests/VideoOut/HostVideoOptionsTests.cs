// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class HostVideoOptionsTests
{
    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(2, 1, true)]
    [InlineData(2, 2, false)]
    [InlineData(0, -1, false)]
    public void AvPlayerFallbackSerialAdvancesIndependentlyOfGuestFlips(
        long serial,
        long presentedSerial,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.IsNewAvPlayerFallbackSerial(serial, presentedSerial));
    }

    [Fact]
    public void NormalizeClampsUnsafeHostValues()
    {
        var options = new HostVideoOptions
        {
            Width = 1,
            Height = 99_999,
            DisplayIndex = -2,
            RefreshRate = 5000,
            HdrMode = (HostHdrMode)99,
        }.Normalize();

        Assert.Equal(640, options.Width);
        Assert.Equal(16384, options.Height);
        Assert.Equal(0, options.DisplayIndex);
        Assert.Equal(1000, options.RefreshRate);
        Assert.Equal(HostHdrMode.Auto, options.HdrMode);
    }
}
