// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestExecutionProbeStateTests
{
    [Fact]
    public void UnhitProbeRemainsReusableAfterItsLastEntryScopeExits()
    {
        var state = new DirectExecutionBackend.GuestExecutionProbeState(0xC6);

        Assert.False(state.Release());
        Assert.Equal(0, state.Registrations);
        Assert.False(state.Restored);

        state.Register();

        Assert.Equal(1, state.Registrations);
        Assert.Equal(0xC6, state.OriginalByte);
    }

    [Fact]
    public void SharedProbeIsRemovedOnlyAfterHitAndFinalRegistrationRelease()
    {
        var state = new DirectExecutionBackend.GuestExecutionProbeState(0xFF);
        state.Register();

        Assert.False(state.Release());
        Assert.Equal(1, state.Registrations);
        Assert.True(state.TryMarkLogged());
        Assert.False(state.TryMarkLogged());

        state.MarkRestored();

        Assert.True(state.Release());
        Assert.Equal(0, state.Registrations);
        Assert.True(state.Restored);
    }
}
