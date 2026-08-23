// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class SessionLogFileNameTests
{
    [Theory]
    [InlineData("PPSA04263", 8, 11, "GTA5-Fran6-2026-08-23_16-08.log")]
    [InlineData("PPSA21567", 4, 51, "AstroBot-Fran6-2026-08-23_16-04.log")]
    [InlineData("PPSA99999", 8, 11, "PPSA99999-Fran6-2026-08-23_16-08.log")]
    public void BuildMapsKnownTitlesAndRetainsUnknownTitleIds(
        string titleId,
        int minute,
        int second,
        string expected)
    {
        var timestamp = new DateTime(2026, 8, 23, 16, minute, second);
        Assert.Equal(expected, SessionLogFileName.Build(titleId, "Fran6", timestamp));
    }

    [Fact]
    public void BuildUsesTheEmbeddedCustomVersion()
    {
        Assert.Equal(
            "AstroBot-Fran6-2026-08-23_16-08.log",
            SessionLogFileName.Build("PPSA21567", new DateTime(2026, 8, 23, 16, 8, 11)));
    }

    [Fact]
    public void BuildFallsBackCleanlyWhenMetadataIsMissing()
    {
        Assert.Equal(
            "UNKNOWN-2026-08-23_16-08.log",
            SessionLogFileName.Build(null, null, new DateTime(2026, 8, 23, 16, 8, 11)));
    }
}
