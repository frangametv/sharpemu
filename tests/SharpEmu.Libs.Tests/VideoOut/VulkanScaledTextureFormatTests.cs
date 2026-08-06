// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanScaledTextureFormatTests
{
    [Theory]
    [InlineData(1u, 2u, Format.R8Uint)]
    [InlineData(1u, 3u, Format.R8Sint)]
    [InlineData(3u, 2u, Format.R8G8Uint)]
    [InlineData(3u, 3u, Format.R8G8Sint)]
    [InlineData(5u, 2u, Format.R16G16Uint)]
    [InlineData(5u, 3u, Format.R16G16Sint)]
    [InlineData(10u, 2u, Format.R8G8B8A8Uint)]
    [InlineData(10u, 3u, Format.R8G8B8A8Sint)]
    [InlineData(12u, 2u, Format.R16G16B16A16Uint)]
    [InlineData(12u, 3u, Format.R16G16B16A16Sint)]
    public void ScaledTexturesUseBitCompatibleIntegerHostFormats(
        uint dataFormat,
        uint numberType,
        Format expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.GetTextureFormatForTests(dataFormat, numberType));
    }
}
