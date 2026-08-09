// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class DirectExecutionBackendNullVirtualCallDiagnosticsTests
{
    private static readonly byte[] CodeBeforeRip =
    [
        0x48, 0x8B, 0x07,       // mov rax, [rdi]
        0xFF, 0x50, 0x48,       // call qword ptr [rax+0x48]
        0x49, 0x89, 0xC6        // mov r14, rax
    ];

    private static readonly byte[] CodeAtRip =
    [
        0x48, 0x89, 0x58, 0x08  // mov [rax+8], rbx
    ];

    [Fact]
    public void GtaCrashPattern_DecodesVirtualSlot()
    {
        Assert.True(DirectExecutionBackend.TryDecodeNullVirtualCallResultStore(
            CodeBeforeRip,
            CodeAtRip,
            rax: 0,
            accessType: 1,
            accessTarget: 8,
            out var virtualSlot));
        Assert.Equal(0x48, virtualSlot);
    }

    [Theory]
    [InlineData(1UL, 1UL, 8UL)]
    [InlineData(0UL, 0UL, 8UL)]
    [InlineData(0UL, 1UL, 0UL)]
    public void DifferentFaultState_IsNotClassified(
        ulong rax,
        ulong accessType,
        ulong accessTarget)
    {
        Assert.False(DirectExecutionBackend.TryDecodeNullVirtualCallResultStore(
            CodeBeforeRip,
            CodeAtRip,
            rax,
            accessType,
            accessTarget,
            out _));
    }

    [Fact]
    public void DifferentInstructionSequence_IsNotClassified()
    {
        var code = CodeBeforeRip.ToArray();
        code[4] = 0x51;

        Assert.False(DirectExecutionBackend.TryDecodeNullVirtualCallResultStore(
            code,
            CodeAtRip,
            rax: 0,
            accessType: 1,
            accessTarget: 8,
            out _));
    }
}