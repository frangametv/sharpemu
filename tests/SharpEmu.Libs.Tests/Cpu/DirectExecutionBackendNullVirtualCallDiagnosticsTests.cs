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

    [Fact]
    public void GtaNullVirtualAllocatorResult_MatchesExactAllocationSequence()
    {
        byte[] before =
        {
            0x48, 0x8B, 0x3A,
            0xBE, 0x10, 0x00, 0x00, 0x00,
            0xBA, 0x10, 0x00, 0x00, 0x00,
            0x31, 0xC9,
            0x48, 0x8B, 0x07,
            0xFF, 0x50, 0x48,
            0x49, 0x89, 0xC6,
        };
        byte[] current = { 0x48, 0x89, 0x58, 0x08 };

        Assert.True(DirectExecutionBackend.IsNullVirtualAllocatorResultPattern(before, current));
    }

    [Fact]
    public void NullVirtualAllocatorResult_RejectsDifferentSizeOrFaultingStore()
    {
        byte[] before =
        {
            0x48, 0x8B, 0x3A,
            0xBE, 0x20, 0x00, 0x00, 0x00,
            0xBA, 0x10, 0x00, 0x00, 0x00,
            0x31, 0xC9,
            0x48, 0x8B, 0x07,
            0xFF, 0x50, 0x48,
            0x49, 0x89, 0xC6,
        };

        Assert.False(DirectExecutionBackend.IsNullVirtualAllocatorResultPattern(
            before,
            new byte[] { 0x48, 0x89, 0x58, 0x08 }));

        before[4] = 0x10;
        Assert.False(DirectExecutionBackend.IsNullVirtualAllocatorResultPattern(
            before,
            new byte[] { 0x48, 0x89, 0x48, 0x08 }));
    }

    [Fact]
    public void GtaNullAssetTable_MatchesExactPersistentTableInitialization()
    {
        byte[] before =
        {
            0x4D, 0x8B, 0x75, 0x60,
            0x48, 0xC1, 0xE3, 0x04,
            0x45, 0x31, 0xE4,
            0x4C, 0x8D, 0xB8, 0xE0, 0xFF, 0xFF, 0xFF,
            0xEB, 0x1D,
            0x0F, 0x1F, 0x00,
        };
        byte[] current =
        {
            0x43, 0xC7, 0x44, 0x26, 0x08, 0x00, 0x00, 0x00, 0x00,
            0x4B, 0xC7, 0x04, 0x26, 0x00, 0x00, 0x00, 0x00,
        };

        Assert.True(DirectExecutionBackend.IsGtaNullAssetTablePattern(
            before,
            current,
			rbx: 0,
            r12: 0,
            r14: 0,
            accessType: 1,
            accessTarget: 8));
    }

    [Theory]
	[InlineData(1UL, 0UL, 0UL, 1UL, 8UL)]
	[InlineData(0UL, 1UL, 0UL, 1UL, 8UL)]
	[InlineData(0UL, 0UL, 1UL, 1UL, 8UL)]
	[InlineData(0UL, 0UL, 0UL, 0UL, 8UL)]
	[InlineData(0UL, 0UL, 0UL, 1UL, 0UL)]
    public void GtaNullAssetTable_RejectsDifferentFaultState(
		ulong rbx,
        ulong r12,
        ulong r14,
        ulong accessType,
        ulong accessTarget)
    {
        byte[] before =
        {
            0x4D, 0x8B, 0x75, 0x60,
            0x48, 0xC1, 0xE3, 0x04,
            0x45, 0x31, 0xE4,
            0x4C, 0x8D, 0xB8, 0xE0, 0xFF, 0xFF, 0xFF,
            0xEB, 0x1D,
            0x0F, 0x1F, 0x00,
        };
        byte[] current =
        {
            0x43, 0xC7, 0x44, 0x26, 0x08, 0x00, 0x00, 0x00, 0x00,
            0x4B, 0xC7, 0x04, 0x26, 0x00, 0x00, 0x00, 0x00,
        };

        Assert.False(DirectExecutionBackend.IsGtaNullAssetTablePattern(
            before,
            current,
			rbx,
            r12,
            r14,
            accessType,
            accessTarget));
    }

    [Fact]
    public void GtaNullStorySingleton_MatchesExactNullWriteSequence()
    {
        byte[] before =
        {
            0x84, 0xDB,
            0x0F, 0x84, 0x44, 0x02, 0x00, 0x00,
            0x48, 0x8B, 0x1D, 0xA2, 0x30, 0xDB, 0x04,
        };
        byte[] current =
        {
            0xC7, 0x83, 0xFC, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        };

        Assert.True(DirectExecutionBackend.IsGtaNullStorySingletonPattern(
            before,
            current,
            rbx: 0,
            accessType: 1,
            accessTarget: 0xFC));

        Assert.False(DirectExecutionBackend.IsGtaNullStorySingletonPattern(
            before,
            current,
            rbx: 1,
            accessType: 1,
            accessTarget: 0xFC));
    }

}
