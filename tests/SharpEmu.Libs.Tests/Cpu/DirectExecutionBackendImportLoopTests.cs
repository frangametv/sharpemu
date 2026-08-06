// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class DirectExecutionBackendImportLoopTests
{
    [Fact]
    public void Signature_ChangesWhenThirdArgumentMakesProgress()
    {
        var first = DirectExecutionBackend.BuildImportLoopSignature(
            nidHash: 0x1234,
            returnRip: 0x0000_0008_0029_C021,
            arg0: 0x0000_6FFF_F01F_EF60,
            arg1: 0x20,
            arg2: 0x0000_0002_2517_1F80);
        var next = DirectExecutionBackend.BuildImportLoopSignature(
            nidHash: 0x1234,
            returnRip: 0x0000_0008_0029_C021,
            arg0: 0x0000_6FFF_F01F_EF60,
            arg1: 0x20,
            arg2: 0x0000_0002_2517_1FA8);

        Assert.NotEqual(first, next);
    }

    [Fact]
    public void Signature_RemainsStableWhenAllObservedInputsAreStable()
    {
        var first = DirectExecutionBackend.BuildImportLoopSignature(1, 2, 3, 4, 5);
        var repeated = DirectExecutionBackend.BuildImportLoopSignature(1, 2, 3, 4, 5);

        Assert.Equal(first, repeated);
    }
}
