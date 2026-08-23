// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanAmdDriverSafetyTests
{
    private const uint NvidiaVendorId = 0x10DE;
    private const uint AmdVendorId = 0x1002;
    private const string FaultingComputeDigest =
        "1A5205C396F8192DF173E537C480766DBE03024C9D0CE4502E39FE42B13464D8";
    private const string FaultingVertexDigest =
        "346E080C9952918F2BB22A1D9E2FD73D8381F2B43A21DD3B60950BA75D1180EA";
    private const string FaultingFragmentDigest =
        "D17904BBF37B1B9C6E7CF6C8222AF540340A2BB1B4B5DB66EE477934DAB3C3AA";

    [Theory]
    [InlineData(AmdVendorId, true, null, true)]
    [InlineData(AmdVendorId, true, "1", true)]
    [InlineData(AmdVendorId, true, "0", false)]
    [InlineData(AmdVendorId, false, null, false)]
    [InlineData(NvidiaVendorId, true, null, false)]
    public void ComputeNoOptimizationIsScopedToAmdWindows(
        uint vendorId,
        bool isWindows,
        string? configuredValue,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldDisableAmdComputePipelineOptimization(
                vendorId,
                isWindows,
                configuredValue));
    }

    [Theory]
    [InlineData(AmdVendorId, true, null, true)]
    [InlineData(AmdVendorId, true, "1", true)]
    [InlineData(AmdVendorId, true, "0", false)]
    [InlineData(AmdVendorId, false, null, false)]
    [InlineData(NvidiaVendorId, true, null, false)]
    public void GraphicsNoOptimizationIsScopedToAmdWindows(
        uint vendorId,
        bool isWindows,
        string? configuredValue,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldDisableAmdGraphicsPipelineOptimization(
                vendorId,
                isWindows,
                configuredValue));
    }

    [Theory]
    [InlineData(AmdVendorId, true, null, true)]
    [InlineData(AmdVendorId, true, "0", true)]
    [InlineData(AmdVendorId, true, "1", false)]
    [InlineData(AmdVendorId, false, null, false)]
    [InlineData(NvidiaVendorId, true, null, false)]
    public void NativeComputeSubgroupsAreDisabledOnlyForAmdWindows(
        uint vendorId,
        bool isWindows,
        string? configuredValue,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldDisableAmdNativeComputeSubgroups(
                vendorId,
                isWindows,
                configuredValue));
    }

    [Theory]
    [InlineData(AmdVendorId, true, null, true)]
    [InlineData(AmdVendorId, true, "1", true)]
    [InlineData(AmdVendorId, true, "0", false)]
    [InlineData(AmdVendorId, false, null, false)]
    [InlineData(NvidiaVendorId, true, null, false)]
    public void FaultingComputeShaderIsQuarantinedOnlyForAmdWindows(
        uint vendorId,
        bool isWindows,
        string? configuredValue,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldQuarantineAmdWindowsComputeShader(
                vendorId,
                isWindows,
                FaultingComputeDigest,
                configuredValue));
        Assert.False(
            VulkanVideoPresenter.ShouldQuarantineAmdWindowsComputeShader(
                vendorId,
                isWindows,
                "2" + FaultingComputeDigest[1..],
                configuredValue));
    }

    [Theory]
    [InlineData(AmdVendorId, true, null, true)]
    [InlineData(AmdVendorId, true, "1", true)]
    [InlineData(AmdVendorId, true, "0", false)]
    [InlineData(AmdVendorId, false, null, false)]
    [InlineData(NvidiaVendorId, true, null, false)]
    public void FaultingGraphicsPairIsQuarantinedOnlyForAmdWindows(
        uint vendorId,
        bool isWindows,
        string? configuredValue,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldQuarantineAmdWindowsGraphicsPipeline(
                vendorId,
                isWindows,
                FaultingVertexDigest,
                FaultingFragmentDigest,
                configuredValue));
        Assert.False(
            VulkanVideoPresenter.ShouldQuarantineAmdWindowsGraphicsPipeline(
                vendorId,
                isWindows,
                "2" + FaultingVertexDigest[1..],
                FaultingFragmentDigest,
                configuredValue));
        Assert.False(
            VulkanVideoPresenter.ShouldQuarantineAmdWindowsGraphicsPipeline(
                vendorId,
                isWindows,
                FaultingVertexDigest,
                "2" + FaultingFragmentDigest[1..],
                configuredValue));
    }
}
