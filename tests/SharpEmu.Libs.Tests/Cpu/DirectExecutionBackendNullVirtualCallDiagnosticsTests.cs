// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
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

    [Fact]
    public void GtaNullStoryProgress_MatchesExactNullReadSequence()
    {
        byte[] before =
        {
            0x5E, 0xC8,
            0xC4, 0xE3, 0x71, 0x0A, 0xC9, 0x09,
            0xC5, 0xFA, 0x5B, 0xC9,
            0xC5, 0xF8, 0x5B, 0xD1,
        };
        byte[] current =
        {
            0xC5, 0xD2, 0x2A, 0x88, 0xC8, 0x00, 0x00, 0x00,
            0xC5, 0xEA, 0x5E, 0xD1,
            0xC5, 0xFA, 0x2C, 0xD2,
            0x85, 0xD2,
        };

        Assert.True(DirectExecutionBackend.IsGtaNullStoryProgressPattern(
            before,
            current,
            rax: 0,
            accessType: 0,
            accessTarget: 0xC8));

        Assert.False(DirectExecutionBackend.IsGtaNullStoryProgressPattern(
            before,
            current,
            rax: 1,
            accessType: 0,
            accessTarget: 0xC8));
    }

    [Fact]
    public void GtaNullStoryStateCompare_MatchesExactNullReadSequence()
    {
        byte[] before =
        {
            0x70, 0x86, 0xFF, 0xFF,
            0xB8, 0xFF, 0x3F, 0x00, 0x00,
            0x48, 0x8B, 0x0D, 0x5E, 0xF8, 0x80, 0x04,
        };
        byte[] current =
        {
            0x39, 0x81, 0x9C, 0x00, 0x00, 0x00,
            0x76, 0x5E,
            0xC5, 0xF8, 0x28, 0x9D, 0x00, 0x87, 0xFF, 0xFF,
        };

        Assert.True(DirectExecutionBackend.IsGtaNullStoryStateComparePattern(
            before, current, rcx: 0, accessType: 0, accessTarget: 0x9C));
        Assert.False(DirectExecutionBackend.IsGtaNullStoryStateComparePattern(
            before, current, rcx: 1, accessType: 0, accessTarget: 0x9C));
    }

    [Fact]
    public void GtaNullStoryStateCompare_MatchesLaterStoryLoadVariant()
    {
        byte[] before =
        {
            0x58, 0xC1, 0x75, 0xD1, 0xE9, 0x7C, 0xEE, 0xFF,
            0xFF, 0x48, 0x8B, 0x0D, 0x4D, 0xEB, 0x80, 0x04,
        };
        byte[] current =
        {
            0x39, 0x81, 0x9C, 0x00, 0x00, 0x00,
            0x76, 0x49,
            0x48, 0x8B, 0x91, 0xA8, 0x00, 0x00, 0x00, 0x48,
        };

        Assert.True(DirectExecutionBackend.IsGtaNullStoryStateComparePattern(
            before, current, rcx: 0, accessType: 0, accessTarget: 0x9C));
    }

    [Fact]
    public void GtaNullStoryServiceCall_MatchesExactNullVirtualCall()
    {
        byte[] before =
        {
            0xD8, 0x04, 0x48, 0x8D, 0x35, 0xAD, 0x8E, 0x13,
            0x03, 0x48, 0x8D, 0x15, 0x6E, 0xFD, 0xFF, 0xFF,
        };
        byte[] current =
        {
            0x48, 0x8B, 0x07,
            0xFF, 0x90, 0x88, 0x01, 0x00, 0x00,
            0x80, 0x3D, 0x36, 0x96, 0xD8, 0x04, 0x00,
        };

        Assert.True(DirectExecutionBackend.IsGtaNullStoryServiceCallPattern(
            before, current, rdi: 0, accessType: 0, accessTarget: 0));
        Assert.False(DirectExecutionBackend.IsGtaNullStoryServiceCallPattern(
            before, current, rdi: 1, accessType: 0, accessTarget: 0));
    }

    [Fact]
    public void GtaFallbackStoryServiceCall_MatchesNullVtableSlot()
    {
        byte[] before =
        {
            0x8D, 0x35, 0xAD, 0x8E, 0x13, 0x03, 0x48, 0x8D,
            0x15, 0x6E, 0xFD, 0xFF, 0xFF, 0x48, 0x8B, 0x07,
        };
        byte[] current =
        {
            0xFF, 0x90, 0x88, 0x01, 0x00, 0x00,
            0x80, 0x3D, 0x36, 0x96, 0xD8, 0x04, 0x00, 0x75, 0x1E, 0x48,
        };

        Assert.True(DirectExecutionBackend.IsGtaFallbackStoryServiceCallPattern(
            before, current, accessType: 0, accessTarget: 0x188));
        Assert.False(DirectExecutionBackend.IsGtaFallbackStoryServiceCallPattern(
            before, current, accessType: 0, accessTarget: 0));
    }

    [Fact]
    public void GtaNullStoryFallbackState_ClearsPrologueGateAndProvidesSafeFields()
    {
        const ulong stateAddress = 0x1234_5000;
        var state = new byte[0x10000];
        Array.Fill(state, (byte)0xCC);

        DirectExecutionBackend.InitializeGtaStoryFallbackState(state, stateAddress);

        Assert.Equal(stateAddress, BinaryPrimitives.ReadUInt64LittleEndian(state.AsSpan(0x40, 8)));
        Assert.Equal(stateAddress, BinaryPrimitives.ReadUInt64LittleEndian(state.AsSpan(0x48, 8)));
        Assert.Equal(0x4000u, BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(0x9C, 4)));
        Assert.Equal(stateAddress, BinaryPrimitives.ReadUInt64LittleEndian(state.AsSpan(0xA8, 8)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(0xC8, 4)));
        Assert.Equal(0, state[0]);
        // The progress routine indexes a signed 16-bit table with RCX=0x3fff,
        // reaching state+0x7ffe after the initial null-pointer recovery.
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(state.AsSpan(0x7FFE, 2)));
    }

}
