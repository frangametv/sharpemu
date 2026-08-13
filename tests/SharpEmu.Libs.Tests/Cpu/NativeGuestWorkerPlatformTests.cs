// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class NativeGuestWorkerPlatformTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PosixTbbThreadsKeepTheInlineExecutionPath(bool workersDisabled)
    {
        Assert.False(
            DirectExecutionBackend.ShouldRequireNativeGuestWorker(
                requested: true,
                isWindows: false,
                nativeWorkersDisabled: workersDisabled));
    }

    [Fact]
    public void WindowsTbbThreadsRequireTheNativeWorkerByDefault()
    {
        Assert.True(
            DirectExecutionBackend.ShouldRequireNativeGuestWorker(
                requested: true,
                isWindows: true,
                nativeWorkersDisabled: false));
    }

    [Fact]
    public void ExplicitWorkerDisableAllowsTheDiagnosticInlinePathOnWindows()
    {
        Assert.False(
            DirectExecutionBackend.ShouldRequireNativeGuestWorker(
                requested: true,
                isWindows: true,
                nativeWorkersDisabled: true));
    }

    [Fact]
    public void OrdinaryGuestThreadsNeverRequireTheWorker()
    {
        Assert.False(
            DirectExecutionBackend.ShouldRequireNativeGuestWorker(
                requested: false,
                isWindows: true,
                nativeWorkersDisabled: false));
    }
}
