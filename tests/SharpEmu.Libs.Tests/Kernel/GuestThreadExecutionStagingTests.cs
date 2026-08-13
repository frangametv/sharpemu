// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class GuestThreadExecutionStagingTests
{
    [Fact]
    public void NestedGuestExecutionCannotConsumeInterruptedImportState()
    {
        var previousThread = GuestThreadExecution.EnterGuestThread(0x1234);
        try
        {
            GuestThreadExecution.RequestCurrentEntryExit("outer", 42UL);
            GuestThreadExecution.RequestCurrentContextTransfer(default);

            var outer = GuestThreadExecution.SaveAndResetStagedState();
            Assert.False(GuestThreadExecution.TryConsumeCurrentEntryExit(out _, out _));
            Assert.False(GuestThreadExecution.TryConsumeCurrentContextTransfer(out _));

            GuestThreadExecution.RequestCurrentEntryExit("nested", 99UL);
            GuestThreadExecution.RestoreStagedState(outer);

            Assert.True(GuestThreadExecution.TryConsumeCurrentEntryExit(
                out var value,
                out var reason));
            Assert.Equal(42UL, value);
            Assert.Equal("outer", reason);
            Assert.True(GuestThreadExecution.TryConsumeCurrentContextTransfer(out _));
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }
}
