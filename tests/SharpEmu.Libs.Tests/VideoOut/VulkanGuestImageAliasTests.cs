// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestImageAliasTests
{
    [Fact]
    public void ExactExtentIsCompatibleForLinearImages()
    {
        Assert.True(VulkanVideoPresenter.IsSampledGuestImageExtentCompatible(
            textureWidth: 1920,
            textureHeight: 1080,
            tileMode: 0,
            imageWidth: 1920,
            imageHeight: 1080));
    }

    [Fact]
    public void LargerTiledDescriptorCanViewSmallerDynamicResolutionImage()
    {
        Assert.True(VulkanVideoPresenter.IsSampledGuestImageExtentCompatible(
            textureWidth: 2432,
            textureHeight: 1368,
            tileMode: 27,
            imageWidth: 1920,
            imageHeight: 1080));
    }

    [Fact]
    public void SmallerTiledDescriptorCanViewLargerDynamicResolutionImage()
    {
        Assert.True(VulkanVideoPresenter.IsSampledGuestImageExtentCompatible(
            textureWidth: 960,
            textureHeight: 540,
            tileMode: 27,
            imageWidth: 1920,
            imageHeight: 1080));
    }

    [Fact]
    public void MismatchedLinearExtentsAreNotAliases()
    {
        Assert.False(VulkanVideoPresenter.IsSampledGuestImageExtentCompatible(
            textureWidth: 960,
            textureHeight: 540,
            tileMode: 0,
            imageWidth: 1920,
            imageHeight: 1080));
    }

    [Fact]
    public void GpuDmaCopyRequiresMatchingHostFormats()
    {
        Assert.False(VulkanVideoPresenter.IsGuestImageCopyCompatible(
            sourceWidth: 960,
            sourceHeight: 540,
            sourceFormat: Format.R16G16B16A16Sfloat,
            destinationWidth: 960,
            destinationHeight: 540,
            destinationFormat: Format.B10G11R11UfloatPack32));
    }

    [Fact]
    public void GpuDmaCopyAcceptsMatchingImages()
    {
        Assert.True(VulkanVideoPresenter.IsGuestImageCopyCompatible(
            sourceWidth: 960,
            sourceHeight: 540,
            sourceFormat: Format.B10G11R11UfloatPack32,
            destinationWidth: 960,
            destinationHeight: 540,
            destinationFormat: Format.B10G11R11UfloatPack32));
    }

    [Fact]
    public void GpuDmaCopyAcceptsMatchingThreeDimensionalImages()
    {
        Assert.True(VulkanVideoPresenter.IsGuestImageCopyCompatible(
            sourceWidth: 32,
            sourceHeight: 16,
            sourceDepth: 8,
            sourceType: VulkanVideoPresenter.Gen5TextureType3D,
            sourceFormat: Format.R8G8B8A8Unorm,
            destinationWidth: 32,
            destinationHeight: 16,
            destinationDepth: 8,
            destinationType: VulkanVideoPresenter.Gen5TextureType3D,
            destinationFormat: Format.R8G8B8A8Unorm));
    }

    [Fact]
    public void GpuDmaCopyRejectsTwoDimensionalAliasForThreeDimensionalImage()
    {
        Assert.False(VulkanVideoPresenter.IsGuestImageCopyCompatible(
            sourceWidth: 32,
            sourceHeight: 16,
            sourceDepth: 1,
            sourceType: VulkanVideoPresenter.Gen5TextureType2D,
            sourceFormat: Format.R8G8B8A8Unorm,
            destinationWidth: 32,
            destinationHeight: 16,
            destinationDepth: 1,
            destinationType: VulkanVideoPresenter.Gen5TextureType3D,
            destinationFormat: Format.R8G8B8A8Unorm));
    }

    [Fact]
    public void ThreeDimensionalDescriptorsMapToVolumeImageAndViewTypes()
    {
        Assert.Equal(
            ImageType.Type3D,
            VulkanVideoPresenter.GetGuestTextureImageType(
                VulkanVideoPresenter.Gen5TextureType3D));
        Assert.Equal(
            ImageViewType.Type3D,
            VulkanVideoPresenter.GetGuestTextureViewType(
                VulkanVideoPresenter.Gen5TextureType3D,
                arrayedView: true));
        Assert.Equal(
            7u,
            VulkanVideoPresenter.GetGuestTextureDepth(
                VulkanVideoPresenter.Gen5TextureType3D,
                7));
    }

    [Fact]
    public void TwoDimensionalArraysKeepLayersSeparateFromImageDepth()
    {
        Assert.Equal(
            ImageType.Type2D,
            VulkanVideoPresenter.GetGuestTextureImageType(
                VulkanVideoPresenter.Gen5TextureType2D));
        Assert.Equal(
            ImageViewType.Type2DArray,
            VulkanVideoPresenter.GetGuestTextureViewType(
                VulkanVideoPresenter.Gen5TextureType2D,
                arrayedView: true));
        Assert.Equal(
            1u,
            VulkanVideoPresenter.GetGuestTextureDepth(
                VulkanVideoPresenter.Gen5TextureType2D,
                7));
    }

    [Fact]
    public void GpuDmaCopyAcceptsInitializedGpuAuthoredSource()
    {
        Assert.True(VulkanVideoPresenter.ShouldMirrorGuestImageCopyOnGpu(
            sourceInitialized: true,
            sourceIsCpuBacked: false));
    }

    [Fact]
    public void GpuDmaCopyRejectsCpuBackedSource()
    {
        Assert.False(VulkanVideoPresenter.ShouldMirrorGuestImageCopyOnGpu(
            sourceInitialized: true,
            sourceIsCpuBacked: true));
    }

    [Fact]
    public void GpuDmaCopyRejectsUninitializedSource()
    {
        Assert.False(VulkanVideoPresenter.ShouldMirrorGuestImageCopyOnGpu(
            sourceInitialized: false,
            sourceIsCpuBacked: false));
    }
}
