// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class DirectExecutionBackendImportClassificationTests
{
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
