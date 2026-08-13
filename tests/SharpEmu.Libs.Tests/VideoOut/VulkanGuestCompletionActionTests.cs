// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestCompletionActionTests
{
    [Fact]
    public void FenceCallbacksRunInOriginalQueueOrder()
    {
        List<int> observed = [];
        VulkanGuestCompletionAction[] actions =
        [
            new(() => observed.Add(1), "first"),
            new(() => observed.Add(2), "second"),
            new(() => observed.Add(3), "third"),
        ];

        VulkanVideoPresenter.ExecuteGuestCompletionActions(actions);

        Assert.Equal([1, 2, 3], observed);
    }

    [Fact]
    public void FailingCallbackDoesNotSuppressLaterCompletions()
    {
        var laterCallbackRan = false;
        VulkanGuestCompletionAction[] actions =
        [
            new(() => throw new InvalidOperationException("expected test failure"), "broken"),
            new(() => laterCallbackRan = true, "later"),
        ];

        VulkanVideoPresenter.ExecuteGuestCompletionActions(actions);

        Assert.True(laterCallbackRan);
    }
}
