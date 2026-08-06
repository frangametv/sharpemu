// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class ImportTrampolineAbiTests
{
	[Fact]
	public void StackCheckRecovery_FindsColdFailureBranchAfterLongEpilogue()
	{
		var code = new byte[128];
		const int returnOffset = 100;
		const int failureCallOffset = returnOffset - 5;
		const int branchOffset = returnOffset - 81;
		const int epilogueOffset = branchOffset + 2;

		code[branchOffset] = 0x75; // jne __stack_chk_fail
		code[branchOffset + 1] = checked((byte)(failureCallOffset - epilogueOffset));
		code[epilogueOffset] = 0x48; // add rsp,0x28
		code[epilogueOffset + 1] = 0x83;
		code[epilogueOffset + 2] = 0xC4;
		code[epilogueOffset + 3] = 0x28;
		code[epilogueOffset + 12] = 0xC3; // ret
		code[failureCallOffset] = 0xE8; // call __stack_chk_fail
		code[returnOffset] = 0x0F;
		code[returnOffset + 1] = 0x0B; // ud2

		Assert.True(DirectExecutionBackend.TryFindStackCheckRecovery(
			code,
			returnOffset,
			out var recoveryOffset));
		Assert.Equal(epilogueOffset, recoveryOffset);
	}

	[Fact]
	public void StackCheckRecovery_RejectsBranchWithoutNormalReturn()
	{
		var code = new byte[64];
		const int returnOffset = 56;
		const int failureCallOffset = returnOffset - 5;
		const int branchOffset = 12;

		code[branchOffset] = 0x75;
		code[branchOffset + 1] = checked((byte)(failureCallOffset - (branchOffset + 2)));
		code[failureCallOffset] = 0xE8;
		code[returnOffset] = 0x0F;
		code[returnOffset + 1] = 0x0B;

		Assert.False(DirectExecutionBackend.TryFindStackCheckRecovery(
			code,
			returnOffset,
			out _));
	}

    [Fact]
    public unsafe void GeneratedTrampoline_PreservesVolatileGuestState()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            // The production backend emits and initializes x86-64 callback
            // thunks, so inspect it only in an x64 test process. Apple Silicon
            // runs this path through the same Rosetta environment as SharpEmu.
            return;
        }

        var code = CreateTrampolineBytes();

        AssertContains(code, [0x48, 0x81, 0xEC, 0xB0, 0x00, 0x00, 0x00]); // sub rsp,0xB0
        AssertContains(code, [0x48, 0x89, 0x04, 0x24]);                   // mov [rsp],rax
        AssertContains(code, [0x4C, 0x89, 0x54, 0x24, 0x08]);             // mov [rsp+8],r10
        AssertContains(code, [0x4C, 0x89, 0x5C, 0x24, 0x10]);             // mov [rsp+16],r11
        AssertContains(code, [0x0F, 0xAE, 0x5C, 0x24, 0x18]);             // stmxcsr [rsp+24]
        AssertContains(code, [0xD9, 0x7C, 0x24, 0x1C]);                   // fnstcw [rsp+28]
        AssertContains(code, [0x4C, 0x8D, 0xA4, 0x24, 0xB0, 0, 0, 0]);   // lea r12,[rsp+0xB0]

        for (var xmm = 0; xmm < 8; xmm++)
        {
            Span<byte> save = new byte[9];
            save[0] = 0xF3;
            save[1] = 0x0F;
            save[2] = 0x7F;
            save[3] = (byte)(0x84 | (xmm << 3));
            save[4] = 0x24;
            BinaryPrimitives.WriteInt32LittleEndian(save[5..], 0x30 + (xmm * 0x10));
            AssertContains(code, save);
        }

        for (var xmm = 0; xmm < 2; xmm++)
        {
            Span<byte> restore = new byte[10];
            restore[0] = 0xF3;
            restore[1] = 0x41;
            restore[2] = 0x0F;
            restore[3] = 0x6F;
            restore[4] = (byte)(0x84 | (xmm << 3));
            restore[5] = 0x24;
            BinaryPrimitives.WriteInt32LittleEndian(restore[6..], -0x80 + (xmm * 0x10));
            AssertContains(code, restore);
        }
    }

    private static unsafe byte[] CreateTrampolineBytes()
    {
        var backend = (DirectExecutionBackend)RuntimeHelpers.GetUninitializedObject(
            typeof(DirectExecutionBackend));
        var trampolineList = typeof(DirectExecutionBackend).GetField(
            "_importHandlerTrampolines",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(trampolineList);
        trampolineList.SetValue(backend, new List<nint>());

        var createTrampoline = typeof(DirectExecutionBackend).GetMethod(
            "CreateImportHandlerTrampoline",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(createTrampoline);
        var trampoline = (nint)createTrampoline.Invoke(backend, [0])!;
        Assert.NotEqual(0, trampoline);

        try
        {
            return new ReadOnlySpan<byte>((void*)trampoline, 512).ToArray();
        }
        finally
        {
            Assert.True(HostMemory.Free((void*)trampoline, 0, HostMemory.MEM_RELEASE));
        }
    }

    private static void AssertContains(ReadOnlySpan<byte> code, ReadOnlySpan<byte> expected)
    {
        Assert.True(
            code.IndexOf(expected) >= 0,
            $"Generated trampoline did not contain {Convert.ToHexString(expected)}.");
    }
}
