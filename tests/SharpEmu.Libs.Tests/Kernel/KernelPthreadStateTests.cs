// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelPthreadStateTests
{
    [Fact]
    public void CurrentGuestIdentityCacheTracksGuestThreadSwitches()
    {
        var first = KernelPthreadState.CreateThreadHandle("identity-cache-first");
        var second = KernelPthreadState.CreateThreadHandle("identity-cache-second");
        Assert.True(KernelPthreadState.TryGetThreadIdentity(first, out var firstIdentity));
        Assert.True(KernelPthreadState.TryGetThreadIdentity(second, out var secondIdentity));

        var previous = GuestThreadExecution.EnterGuestThread(first);
        try
        {
            Assert.Equal(first, KernelPthreadState.GetCurrentThreadHandle());
            Assert.Equal(firstIdentity.UniqueId, KernelPthreadState.GetCurrentThreadUniqueId());

            var previousNested = GuestThreadExecution.EnterGuestThread(second);
            try
            {
                Assert.Equal(second, KernelPthreadState.GetCurrentThreadHandle());
                Assert.Equal(secondIdentity.UniqueId, KernelPthreadState.GetCurrentThreadUniqueId());
            }
            finally
            {
                GuestThreadExecution.RestoreGuestThread(previousNested);
            }

            Assert.Equal(first, KernelPthreadState.GetCurrentThreadHandle());
            Assert.Equal(firstIdentity.UniqueId, KernelPthreadState.GetCurrentThreadUniqueId());
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previous);
        }
    }
}
