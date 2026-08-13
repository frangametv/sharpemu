// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerAbiTests
{
    [Theory]
    [InlineData(Generation.Gen4, false, 108UL)]
    [InlineData(Generation.Gen5, false, 112UL)]
    [InlineData(Generation.Gen4, true, 164UL)]
    [InlineData(Generation.Gen5, true, 168UL)]
    public void InitAutoStartOffsetMatchesGeneration(
        Generation generation,
        bool extended,
        ulong expected)
    {
        Assert.Equal(expected, AvPlayerExports.GetAutoStartOffset(generation, extended));
    }

    [Theory]
    [InlineData(Generation.Gen4, 40)]
    [InlineData(Generation.Gen5, 32)]
    public void LegacyStreamInfoSizeMatchesGeneration(
        Generation generation,
        int expected)
    {
        Assert.Equal(expected, AvPlayerExports.GetLegacyStreamInfoSize(generation));
    }

    [Theory]
    [InlineData(Generation.Gen4, 0u, 0u)]
    [InlineData(Generation.Gen4, 1u, 1u)]
    [InlineData(Generation.Gen5, 0u, 1u)]
    [InlineData(Generation.Gen5, 1u, 2u)]
    public void StreamTypeMatchesGeneration(
        Generation generation,
        uint streamIndex,
        uint expected)
    {
        Assert.Equal(expected, AvPlayerExports.GetStreamType(generation, streamIndex));
    }

    [Fact]
    public void Gen5FrameInfoExCarriesPitchCropAndFrameRate()
    {
        var info = new byte[104];

        AvPlayerExports.WriteVideoFrameInfo(
            info,
            Generation.Gen5,
            extended: true,
            bufferAddress: 0x1234_5000,
            timestamp: 2_903,
            width: 512,
            visibleWidth: 378,
            height: 150,
            pitch: 512,
            framesPerSecond: 29.97);

        Assert.Equal(0x1234_5000UL, BinaryPrimitives.ReadUInt64LittleEndian(info));
        Assert.Equal(2_903UL, BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(16)));
        Assert.Equal(512u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(24)));
        Assert.Equal(150u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(28)));
        Assert.Equal(134u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(48)));
        Assert.Equal(512u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(60)));
        Assert.Equal(8, info[64]);
        Assert.Equal(8, info[65]);
        Assert.Equal(29.97, BinaryPrimitives.ReadDoubleLittleEndian(info.AsSpan(0x48)));
    }

    [Fact]
    public void FallbackFrameValidationUsesTheDecodedDimensions()
    {
        var fullHdFrame = new byte[1920 * 1080 * 4];

        Assert.True(AvPlayerExports.IsValidBgraFrame(fullHdFrame, 1920, 1080));
        Assert.False(AvPlayerExports.IsValidBgraFrame(fullHdFrame, 3840, 2160));
        Assert.False(AvPlayerExports.IsValidBgraFrame(fullHdFrame, 0, 1080));
    }
}
