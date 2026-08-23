// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class TlsScanWindowTests
{
    [Fact]
    public void Gen5BootstrapEntryStillIncludesMainImage()
    {
        Assert.Equal(
            0x0000000800000000UL,
            DirectExecutionBackend.SelectTlsScanStart(
                entryPoint: 0x0000000804000010UL,
                allocationBase: 0x0000000804000000UL));
    }

    [Fact]
    public void Gen4EntryStillIncludesMainImage()
    {
        Assert.Equal(
            0x0000000000400000UL,
            DirectExecutionBackend.SelectTlsScanStart(
                entryPoint: 0x0000000001400010UL,
                allocationBase: 0x0000000001400000UL));
    }

    [Fact]
    public void EarlierAllocationBaseIsPreserved()
    {
        Assert.Equal(
            0x0000000000200000UL,
            DirectExecutionBackend.SelectTlsScanStart(
                entryPoint: 0x0000000000300010UL,
                allocationBase: 0x0000000000200000UL));
    }

    [Theory]
    [InlineData(0, 0xEB, true)]
    [InlineData(1, 0x90, true)]
    [InlineData(1, 0xEB, false)]
    public void PatchCandidateRejectsShortJumpDisplacement(
        int regionOffset,
        byte precedingByte,
        bool expected)
    {
        Assert.Equal(
            expected,
            DirectExecutionBackend.IsTlsPatchCandidateBoundary(
                regionOffset,
                precedingByte));
    }
}
