// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class ModuleManagerWarmupTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(16, 8)]
    public void AutomaticWarmupWorkersCapAtEight(int processorCount, int expected)
    {
        Assert.Equal(
            expected,
            ModuleManager.SelectWarmupWorkerCount(processorCount, configuredValue: null));
    }

    [Theory]
    [InlineData(16, "1", 1)]
    [InlineData(16, "4", 4)]
    [InlineData(8, "16", 8)]
    [InlineData(8, "0", 1)]
    [InlineData(12, "invalid", 8)]
    public void ConfiguredWarmupWorkersAreClampedToTheHost(
        int processorCount,
        string configuredValue,
        int expected)
    {
        Assert.Equal(
            expected,
            ModuleManager.SelectWarmupWorkerCount(processorCount, configuredValue));
    }
}
