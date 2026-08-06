// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanSamplerTests
{
    [Fact]
    public void OrdinaryColorSamplerDoesNotEnableDescriptorCompareFunction()
    {
        // ASTRO BOT's ordinary IMAGE_SAMPLE_LZ sampler. DEPTH_COMPARE_FUNC is
        // ALWAYS even though the opcode is not a comparison sample.
        var sampler = new GuestSampler(
            Word0: 0x00007092,
            Word1: 0x00FFF000,
            Word2: 0x06500000,
            Word3: 0x00000000);

        var info = VulkanVideoPresenter.DecodeSamplerCreateInfo(sampler);

        Assert.False(info.CompareEnable);
        Assert.Equal(CompareOp.Always, info.CompareOp);
        Assert.Equal(SamplerAddressMode.ClampToEdge, info.AddressModeU);
        Assert.Equal(SamplerAddressMode.ClampToEdge, info.AddressModeV);
        Assert.Equal(BorderColor.FloatTransparentBlack, info.BorderColor);
    }

    [Theory]
    [InlineData(0u, BorderColor.FloatTransparentBlack)]
    [InlineData(1u, BorderColor.FloatOpaqueBlack)]
    [InlineData(2u, BorderColor.FloatOpaqueWhite)]
    [InlineData(3u, BorderColor.FloatOpaqueBlack)]
    public void BorderColorTypeUsesAmdEncoding(uint type, BorderColor expected)
    {
        var sampler = new GuestSampler(
            Word0: 0,
            Word1: 0,
            Word2: 0,
            Word3: type << 30);

        var info = VulkanVideoPresenter.DecodeSamplerCreateInfo(sampler);

        Assert.Equal(expected, info.BorderColor);
    }
}
