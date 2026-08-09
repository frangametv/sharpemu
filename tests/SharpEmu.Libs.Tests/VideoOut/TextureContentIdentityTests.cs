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

        Assert.Equal(
            new TextureContentIdentity(
                Address: 0x1234_5000,
                Width: 128,
                Height: 64,
                Format: 10,
                NumberType: 7,
                DstSelect: 0xFAC,
                TileMode: 13,
                Pitch: 160,
                SourceOffset: 0x280,
                Sampler: sampler,
                Arrayed: true,
                ArrayLayers: 6,
                Type: 10,
                Depth: 4),
            TextureContentIdentity.FromGuestTexture(texture));
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

        var identity = TextureContentIdentity.FromGuestTexture(texture);

        Assert.Equal(1u, identity.ArrayLayers);
        Assert.Equal(1u, identity.Depth);
    }
}
