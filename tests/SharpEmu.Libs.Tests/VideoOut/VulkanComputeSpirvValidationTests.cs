// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanComputeSpirvValidationTests
{
    [Fact]
    public void ComputeResourceGateAcceptsReadOnlyTextureWithRealGuestAddress()
    {
        var texture = new GuestDrawTexture(
            0x1_0000_4000,
            16,
            16,
            10,
            0,
            [],
            IsFallback: false,
            IsStorage: false);

        Assert.True(VulkanVideoPresenter.HasUsableComputeResource([texture], []));
    }

    [Fact]
    public void ComputeResourceGateRejectsOnlyAddressZeroFallbacks()
    {
        var texture = new GuestDrawTexture(
            0,
            1,
            1,
            10,
            0,
            [],
            IsFallback: true,
            IsStorage: true);
        var buffer = new GuestMemoryBuffer(0, [], 0, Pooled: false);

        Assert.False(VulkanVideoPresenter.HasUsableComputeResource([texture], [buffer]));
    }

    [Fact]
    public void ComputeResourceGateAcceptsRealGlobalBufferWithoutTextures()
    {
        var buffer = new GuestMemoryBuffer(
            0x1_0000_8000,
            new byte[4],
            4,
            Pooled: false);

        Assert.True(VulkanVideoPresenter.HasUsableComputeResource([], [buffer]));
    }

    [Fact]
    public void ComputePreflightAcceptsWellFormedModuleWithMatchingLocalSize()
    {
        var spirv = SpirvFixedShaders.CreateDetileCompute();

        Assert.True(
            VulkanVideoPresenter.TryValidateComputeSpirv(
                spirv,
                8,
                8,
                1,
                out var error),
            error);
    }

    [Fact]
    public void ComputePreflightRejectsMismatchedLocalSizeBeforeTheDriver()
    {
        var spirv = SpirvFixedShaders.CreateDetileCompute();

        Assert.False(
            VulkanVideoPresenter.TryValidateComputeSpirv(
                spirv,
                64,
                1,
                1,
                out var error));
        Assert.Contains("mismatched-local-size", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputePreflightRejectsMalformedInstructionStreamBeforeTheDriver()
    {
        var spirv = SpirvFixedShaders.CreateDetileCompute();
        BinaryPrimitives.WriteUInt32LittleEndian(spirv.AsSpan(5 * sizeof(uint)), 0);

        Assert.False(
            VulkanVideoPresenter.TryValidateComputeSpirv(
                spirv,
                8,
                8,
                1,
                out var error));
        Assert.Contains("instruction-size", error, StringComparison.Ordinal);
    }
}
