// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcComputeShaderCompatibilityDiagnosticsTests
{
    [Fact]
    public void ReportsFirstFailureForShaderWithStableFormat()
    {
        var tracedShaders = new HashSet<ulong>();

        var reported = AgcExports.TryCreateComputeShaderCompatibilityDiagnostic(
            tracedShaders,
            0x0000000800123456,
            "unsupported vector opcode Vop3Raw001",
            out var diagnostic);

        Assert.True(reported);
        Assert.Equal(
            "[COMPAT][SHADER] cs=0x0000000800123456 error=unsupported vector opcode Vop3Raw001",
            diagnostic);
    }

    [Fact]
    public void DeduplicatesFailuresByShaderAddress()
    {
        var tracedShaders = new HashSet<ulong>();

        Assert.True(AgcExports.TryCreateComputeShaderCompatibilityDiagnostic(
            tracedShaders,
            0x8000,
            "first failure",
            out _));

        Assert.False(AgcExports.TryCreateComputeShaderCompatibilityDiagnostic(
            tracedShaders,
            0x8000,
            "later failure",
            out var duplicateDiagnostic));
        Assert.Equal(string.Empty, duplicateDiagnostic);

        Assert.True(AgcExports.TryCreateComputeShaderCompatibilityDiagnostic(
            tracedShaders,
            0x9000,
            "independent shader failure",
            out _));
    }
}
