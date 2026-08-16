// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

[Collection("AmprExports")]
public sealed class KernelAprCompatExportsTests
{
    private const ulong MemoryBase = 0x10_0000;
    private const ulong CommandBuffer = MemoryBase + 0x100;

    [Fact]
    public unsafe void SubmitAndGetResult_WritesNativeOutputPointers()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = CommandBuffer;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(context));

        var outputs = NativeMemory.AllocZeroed(2, (nuint)sizeof(ulong));
        try
        {
            var resultAddress = unchecked((ulong)(nuint)outputs);
            var submissionIdAddress = resultAddress + sizeof(ulong);
            context[CpuRegister.Rdi] = CommandBuffer;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = resultAddress;
            context[CpuRegister.Rcx] = submissionIdAddress;

            Assert.Equal(0, KernelAprCompatExports.KernelAprSubmitCommandBufferAndGetResult(context));
            Assert.Equal(0UL, *(ulong*)outputs);
            var submissionId = *(uint*)((byte*)outputs + sizeof(ulong));
            Assert.NotEqual(0U, submissionId);

            context[CpuRegister.Rdi] = submissionId;
            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = 0;
            Assert.Equal(0, KernelAprCompatExports.KernelAprWaitCommandBuffer(context));
        }
        finally
        {
            NativeMemory.Free(outputs);
        }
    }

    [Fact]
    public unsafe void SubmitAndGetId_WritesNativeOutputPointer()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = CommandBuffer;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(context));

        var output = NativeMemory.AllocZeroed((nuint)sizeof(uint));
        try
        {
            context[CpuRegister.Rdi] = CommandBuffer;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = unchecked((ulong)(nuint)output);

            Assert.Equal(0, KernelAprCompatExports.KernelAprSubmitCommandBufferAndGetId(context));
            var submissionId = *(uint*)output;
            Assert.NotEqual(0U, submissionId);

            context[CpuRegister.Rdi] = submissionId;
            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = 0;
            Assert.Equal(0, KernelAprCompatExports.KernelAprWaitCommandBuffer(context));
        }
        finally
        {
            NativeMemory.Free(output);
        }
    }

    [Fact]
    public unsafe void WaitCommandBuffer_UintMaxRetiresEverySubmission()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = CommandBuffer;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(context));

        var outputs = NativeMemory.AllocZeroed(2, (nuint)sizeof(uint));
        try
        {
            for (var index = 0; index < 2; index++)
            {
                context[CpuRegister.Rdi] = CommandBuffer;
                context[CpuRegister.Rsi] = 1;
                context[CpuRegister.Rdx] = unchecked((ulong)(nuint)((uint*)outputs + index));
                Assert.Equal(0, KernelAprCompatExports.KernelAprSubmitCommandBufferAndGetId(context));
            }

            context[CpuRegister.Rdi] = uint.MaxValue;
            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = 0;
            Assert.Equal(0, KernelAprCompatExports.KernelAprWaitCommandBuffer(context));

            for (var index = 0; index < 2; index++)
            {
                context[CpuRegister.Rdi] = *((uint*)outputs + index);
                Assert.Equal(
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
                    KernelAprCompatExports.KernelAprWaitCommandBuffer(context));
            }
        }
        finally
        {
            NativeMemory.Free(outputs);
        }
    }
}
