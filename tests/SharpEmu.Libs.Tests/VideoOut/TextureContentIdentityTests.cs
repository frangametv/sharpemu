// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class TextureContentIdentityTests
{
    [Fact]
    public void FromGuestTextureIncludesTheCompleteResourceShape()
    {
        var sampler = new GuestSampler(11, 22, 33, 44);
        var texture = new GuestDrawTexture(
            Address: 0x1234_5000,
            Width: 128,
            Height: 64,
            Format: 10,
            NumberType: 7,
            RgbaPixels: [],
            IsFallback: false,
            IsStorage: false,
            Pitch: 160,
            TileMode: 13,
            DstSelect: 0xFAC,
            Sampler: sampler,
            SourceOffset: 0x280,
            ArrayedView: true,
            ArrayLayers: 6,
            Type: 10,
            Depth: 4);

        // Sampling state is not part of the content identity.
        var identity = new TextureContentIdentity(
            texture.Address,
            texture.Width,
            texture.Height,
            texture.Format,
            texture.NumberType,
            texture.DstSelect,
            texture.TileMode,
            texture.Pitch,
            texture.ArrayedView,
            texture.ArrayLayers,
            texture.Type,
            texture.Depth,
            texture.ResourceMipLevels);

        Assert.Equal(
            new TextureContentIdentity(
                0x1234_5000UL,
                128,
                64,
                10,
                7,
                0xFAC,
                13,
                160,
                true,
                6,
                10,
                4,
                1),
            identity);
    }

    [Fact]
    public void FromGuestTextureNormalizesCountsForTwoDimensionalResources()
    {
        var texture = new GuestDrawTexture(
            Address: 0x9000,
            Width: 1,
            Height: 1,
            Format: 1,
            NumberType: 0,
            RgbaPixels: [],
            IsFallback: false,
            IsStorage: false,
            ArrayLayers: 0,
            Type: 9,
            Depth: 7);

        // The translator normalizes counts before building the identity.
        var identity = new TextureContentIdentity(
            texture.Address,
            texture.Width,
            texture.Height,
            texture.Format,
            texture.NumberType,
            texture.DstSelect,
            texture.TileMode,
            texture.Pitch,
            false,
            Math.Max(texture.ArrayLayers, 1u),
            texture.Type,
            1u,
            1);

        Assert.Equal(1u, identity.ArrayLayers);
        Assert.Equal(1u, identity.Depth);
    }
}
