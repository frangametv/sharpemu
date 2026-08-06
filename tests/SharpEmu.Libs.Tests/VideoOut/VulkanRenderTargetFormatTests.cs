// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanRenderTargetFormatTests
{
    [Theory]
    [InlineData(2u, 4u, Format.R16Uint, Gen5PixelOutputKind.Uint)]
    [InlineData(2u, 5u, Format.R16Sint, Gen5PixelOutputKind.Sint)]
    [InlineData(2u, 7u, Format.R16Sfloat, Gen5PixelOutputKind.Float)]
    [InlineData(11u, 4u, Format.R32G32Uint, Gen5PixelOutputKind.Uint)]
    [InlineData(11u, 5u, Format.R32G32Sint, Gen5PixelOutputKind.Sint)]
    [InlineData(11u, 7u, Format.R32G32Sfloat, Gen5PixelOutputKind.Float)]
    public void ScalarAndRgTargetsPreserveTheirNumericType(
        uint dataFormat,
        uint numberType,
        Format expectedFormat,
        Gen5PixelOutputKind expectedOutputKind)
    {
        Assert.True(VulkanVideoPresenter.TryDecodeRenderTargetFormat(
            dataFormat,
            numberType,
            out var result));
        Assert.Equal(expectedFormat, result.Format);
        Assert.Equal(expectedOutputKind, result.OutputKind);
    }

    [Theory]
    [InlineData(0u, Format.A2B10G10R10UnormPack32)]
    [InlineData(1u, Format.A2R10G10B10UnormPack32)]
    public void Color2101010HonorsComponentSwap(uint componentSwap, Format expected)
    {
        Assert.True(VulkanVideoPresenter.TryDecodeRenderTargetFormat(
            dataFormat: 9,
            numberType: 0,
            componentSwap,
            out var result));
        Assert.Equal(expected, result.Format);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    public void Color2101010RejectsUnsupportedComponentSwap(uint componentSwap)
    {
        Assert.False(VulkanVideoPresenter.TryDecodeRenderTargetFormat(
            dataFormat: 9,
            numberType: 0,
            componentSwap,
            out _));
    }
}
