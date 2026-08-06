// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class GnmTilingMipLayoutTests
{
    [Fact]
    public void Standard64K_Bc7AstroTitlePlacesMipZeroAfterTailAndSmallerLevels()
    {
        Assert.True(GnmTiling.TryGetStandard64KMipLayout(
            swizzleMode: 9,
            baseElementsWide: 400,
            baseElementsHigh: 64,
            bytesPerElement: 16,
            resourceMipLevels: 11,
            mipLevel: 0,
            out var layout));

        Assert.Equal(0x80000ul, layout.SourceOffset);
        Assert.Equal(0x70000ul, layout.SourceByteCount);
        Assert.Equal(0xF0000ul, layout.AllocationByteCount);
        Assert.Equal(400, layout.ElementsWide);
        Assert.Equal(64, layout.ElementsHigh);
        Assert.False(layout.IsMipTail);
    }

    [Theory]
    [InlineData(1, 0x40000ul, 0x40000ul)]
    [InlineData(2, 0x20000ul, 0x20000ul)]
    [InlineData(3, 0x10000ul, 0x10000ul)]
    public void Standard64K_NonTailLevelsUseReverseMipOrder(
        uint mipLevel,
        ulong expectedOffset,
        ulong expectedSize)
    {
        Assert.True(GnmTiling.TryGetStandard64KMipLayout(
            9,
            400,
            64,
            16,
            11,
            mipLevel,
            out var layout));

        Assert.Equal(expectedOffset, layout.SourceOffset);
        Assert.Equal(expectedSize, layout.SourceByteCount);
        Assert.Equal(0xF0000ul, layout.AllocationByteCount);
        Assert.False(layout.IsMipTail);
    }

    [Fact]
    public void Standard64K_FirstBc7TailLevelUsesPackedBlockOrigin()
    {
        Assert.True(GnmTiling.TryGetStandard64KMipLayout(
            9,
            400,
            64,
            16,
            11,
            4,
            out var layout));

        Assert.Equal(0ul, layout.SourceOffset);
        Assert.Equal(0x10000ul, layout.SourceByteCount);
        Assert.Equal(25, layout.ElementsWide);
        Assert.Equal(4, layout.ElementsHigh);
        Assert.Equal(32, layout.SourceX);
        Assert.Equal(0, layout.SourceY);
        Assert.True(layout.IsMipTail);
    }

    [Fact]
    public void Standard64K_DetileHonorsPackedMipTailOrigin()
    {
        if (!GnmTiling.Enabled)
        {
            return;
        }

        var tiled = new byte[0x10000];
        var expected = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        expected.CopyTo(tiled, 0x8000);
        var linear = new byte[16];

        Assert.True(GnmTiling.TryDetile(
            tiled,
            linear,
            swizzleMode: 9,
            elementsWide: 1,
            elementsHigh: 1,
            bytesPerElement: 16,
            sourceX: 32,
            sourceY: 0));
        Assert.Equal(expected, linear);
    }
}
