// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class DirectExecutionBackendImportClassificationTests
{
	[Theory]
	[InlineData("eV9wAD2riIA", unchecked((int)0x80020002))]
	[InlineData("1G3lF1Gg1k8", unchecked((int)0x80020002))]
	[InlineData("gEpBkcwxUjw", unchecked((int)0x80020002))]
	[InlineData("27bAgiJmOh0", 60)]
	[InlineData("BmMjYxmew1w", unchecked((int)0x8002003C))]
	[InlineData("Zxa0VhQVTsk", unchecked((int)0x8002003C))]
	[InlineData("fzyMKs9kim0", unchecked((int)0x8002003C))]
	[InlineData("K-jXhbt2gn4", unchecked((int)0x80020010))]
	[InlineData("12wOHk8ywb0", unchecked((int)0x80020010))]
	[InlineData("H2a+IN9TP0E", unchecked((int)0x80020023))]
	[InlineData("PIWqhn9oSxc", unchecked((int)0x80410123))]
	[InlineData("yH17Q6NWtVg", unchecked((int)0x80960007))]
	[InlineData("D-CzAxQL0XI", unchecked((int)0x80960009))]
	public void KnownPollingResults_AreExpected(string nid, int result)
	{
		Assert.True(DirectExecutionBackend.IsExpectedImportResult(
			nid,
			(OrbisGen2Result)result));
		Assert.False(DirectExecutionBackend.IsExpectedImportResult(
			"unrelated-nid",
			(OrbisGen2Result)result));
	}

    [Theory]
    [InlineData("BmMjYxmew1w")]
    [InlineData("Zxa0VhQVTsk")]
    public void TimedBlockingImportTimeout_IsExpectedPollingResult(string nid)
    {
        Assert.True(DirectExecutionBackend.IsExpectedImportResult(
            nid,
            OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT));
        Assert.False(DirectExecutionBackend.IsExpectedImportResult(
            nid,
            OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED));
    }

    [Theory]
    [InlineData("Zxa0VhQVTsk")]
    [InlineData("WKAXJ4XBPQ4")]
    [InlineData("BmMjYxmew1w")]
    [InlineData("Op8TBGY5KHg")]
    [InlineData("27bAgiJmOh0")]
    [InlineData("fzyMKs9kim0")]
    public void BlockingWaitImports_AreExcludedFromStallTermination(string nid)
    {
        Assert.True(DirectExecutionBackend.IsExpectedBlockingImportNid(nid));
    }

    [Fact]
    public void UnrelatedImport_IsNotClassifiedAsExpectedBlockingWait()
    {
        Assert.False(DirectExecutionBackend.IsExpectedBlockingImportNid("tn3VlD0hG60"));
    }

    [Fact]
    public void SceKernelWaitSema_IsImportLoopGuardBoundary()
    {
        Assert.True(DirectExecutionBackend.IsImportLoopGuardBoundary("Zxa0VhQVTsk"));
    }
}
