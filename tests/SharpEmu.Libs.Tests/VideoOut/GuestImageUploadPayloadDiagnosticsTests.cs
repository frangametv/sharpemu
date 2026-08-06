// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class GuestImageUploadPayloadDiagnosticsTests
{
    [Fact]
    public void SummarizesNonzeroBytesAndStableFnvHash()
    {
        var summary = GuestImageUploadPayloadDiagnostics.Summarize([0, 1, 0, 0xFF]);

        Assert.Equal(2, summary.NonzeroBytes);
        Assert.Equal(0x447BDE7F98E5E403UL, summary.Hash);
    }

    [Fact]
    public void EmptyPayloadUsesFnvOffsetBasis()
    {
        var summary = GuestImageUploadPayloadDiagnostics.Summarize([]);

        Assert.Equal(0, summary.NonzeroBytes);
        Assert.Equal(14695981039346656037UL, summary.Hash);
    }
}
