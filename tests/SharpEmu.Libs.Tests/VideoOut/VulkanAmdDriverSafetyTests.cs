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
    private const string SecondFaultingFragmentDigest =
        "C710C7055121A5C5E2821E6765EBEAE4E130AD17120D8986F14849534D57DC00";
    private const string AstroFaultingVertexDigest =
        "D46E13BF027B050D000000000000000000000000000000000000000000000000";
    private const string AstroFaultingFragmentDigest =
        "02B5BD96E82E762F000000000000000000000000000000000000000000000000";
    private const string GtaFaultingVertexDigest =
        "5CD1D345A0121A5E000000000000000000000000000000000000000000000000";
    private const string GtaFaultingFragmentDigest =
        "407058204AFB4080000000000000000000000000000000000000000000000000";

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
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldQuarantineAmdWindowsGraphicsPipeline(
                vendorId,
                isWindows,
                FaultingVertexDigest,
                SecondFaultingFragmentDigest,
                configuredValue));
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldQuarantineAmdWindowsGraphicsPipeline(
                vendorId,
                isWindows,
                AstroFaultingVertexDigest,
                AstroFaultingFragmentDigest,
                configuredValue));
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldQuarantineAmdWindowsGraphicsPipeline(
                vendorId,
                isWindows,
                GtaFaultingVertexDigest,
                GtaFaultingFragmentDigest,
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

    [Theory]
    [InlineData(AmdVendorId, true, null, true)]
    [InlineData(AmdVendorId, true, "1", true)]
    [InlineData(AmdVendorId, true, "0", false)]
    [InlineData(AmdVendorId, false, null, false)]
    [InlineData(NvidiaVendorId, true, null, false)]
    public void SolidFragmentFallbackIsLimitedToFieldConfirmedAmdWindowsPairs(
        uint vendorId,
        bool isWindows,
        string? configuredValue,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldUseAmdWindowsSolidFragmentFallback(
                vendorId,
                isWindows,
                AstroFaultingVertexDigest,
                AstroFaultingFragmentDigest,
                configuredValue));
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldUseAmdWindowsSolidFragmentFallback(
                vendorId,
                isWindows,
                GtaFaultingVertexDigest,
                GtaFaultingFragmentDigest,
                configuredValue));
        Assert.False(
            VulkanVideoPresenter.ShouldUseAmdWindowsSolidFragmentFallback(
                vendorId,
                isWindows,
                AstroFaultingVertexDigest,
                GtaFaultingFragmentDigest,
                configuredValue));
    }

    [Theory]
    [InlineData(AmdVendorId, true, null, true)]
    [InlineData(AmdVendorId, true, "1", true)]
    [InlineData(AmdVendorId, true, "0", false)]
    [InlineData(AmdVendorId, false, null, false)]
    [InlineData(NvidiaVendorId, true, null, false)]
    public void FullscreenVertexFallbackIsScopedToKnownShaderOnAmdWindows(
        uint vendorId,
        bool isWindows,
        string? configuredValue,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldUseAmdWindowsFullscreenVertexFallback(
                vendorId,
                isWindows,
                FaultingVertexDigest,
                configuredValue));
        Assert.False(
            VulkanVideoPresenter.ShouldUseAmdWindowsFullscreenVertexFallback(
                vendorId,
                isWindows,
                "2" + FaultingVertexDigest[1..],
                configuredValue));
    }

    [Theory]
    [InlineData(AmdVendorId, true, null, true)]
    [InlineData(AmdVendorId, true, "1", true)]
    [InlineData(AmdVendorId, true, "0", false)]
    [InlineData(AmdVendorId, false, null, false)]
    [InlineData(NvidiaVendorId, true, null, false)]
    public void GlobalFullscreenVertexFallbackReproducesRun10OnlyOnAmdWindows(
        uint vendorId,
        bool isWindows,
        string? configuredValue,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldUseAmdWindowsGlobalFullscreenVertexFallback(
                vendorId,
                isWindows,
                configuredValue));
    }
}
