// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelSemaphoreCompatExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Fact]
    public void KernelCreateSema_WritesHandleToNativeMappedGuestMemory()
    {
        var nameBytes = Encoding.UTF8.GetBytes("AstroContentExport\0");
        var context = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        var handleAddress = AllocateTracked(context, sizeof(uint));
        var nameAddress = AllocateTracked(context, nameBytes.Length);
        try
        {
            Marshal.WriteInt32(unchecked((nint)handleAddress), 0);
            Marshal.Copy(nameBytes, 0, unchecked((nint)nameAddress), nameBytes.Length);

            context[CpuRegister.Rdi] = handleAddress;
            context[CpuRegister.Rsi] = nameAddress;
            context[CpuRegister.Rdx] = 1;
            context[CpuRegister.Rcx] = 0;
            context[CpuRegister.R8] = 1;
            context[CpuRegister.R9] = 0;

            Assert.Equal(0, KernelSemaphoreCompatExports.KernelCreateSema(context));
            var handle = unchecked((uint)Marshal.ReadInt32(unchecked((nint)handleAddress)));
            Assert.NotEqual(0U, handle);

            context[CpuRegister.Rdi] = handle;
            Assert.Equal(0, KernelSemaphoreCompatExports.KernelDeleteSema(context));
        }
        finally
        {
            FreeTracked(context, nameAddress);
            FreeTracked(context, handleAddress);
        }
    }

    [Fact]
    public void KernelWaitSema_GtaOneMillisecondPollTimesOutAndClearsTimeout()
    {
        const ulong timeoutAddress = MemoryBase + 0x200;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var handle = CreateSemaphore(context, memory, "GtaOneMillisecondPoll", initialCount: 0, maxCount: 1);
        Assert.True(context.TryWriteUInt32(timeoutAddress, 1_000));

        try
        {
            context[CpuRegister.Rdi] = handle;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = timeoutAddress;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
                KernelSemaphoreCompatExports.KernelWaitSema(context));
            Assert.True(context.TryReadUInt32(timeoutAddress, out var remainingTimeout));
            Assert.Equal(0U, remainingTimeout);
        }
        finally
        {
            DeleteSemaphore(context, handle);
        }
    }

    [Fact]
    public void KernelWaitSema_Gen5TaggedHandleUsesLow32Bits()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var handle = CreateSemaphore(context, memory, "GtaTaggedHandle", initialCount: 1, maxCount: 1);

        try
        {
            context[CpuRegister.Rdi] = 0x8_0000_0000UL | handle;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = 0;

            Assert.Equal(0, KernelSemaphoreCompatExports.KernelWaitSema(context));
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY,
                KernelSemaphoreCompatExports.KernelPollSema(context, handle, 1));
        }
        finally
        {
            DeleteSemaphore(context, handle);
        }
    }

    [Fact]
    public async Task KernelWaitSema_SignalBeforeGtaDeadlineSucceedsAndConsumesCount()
    {
        const ulong timeoutAddress = MemoryBase + 0x200;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var waitContext = new CpuContext(memory, Generation.Gen5);
        var signalContext = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        var handle = CreateSemaphore(waitContext, memory, "GtaSignalBeforeDeadline", initialCount: 0, maxCount: 1);
        Assert.True(waitContext.TryWriteUInt32(timeoutAddress, 250_000));

        try
        {
            waitContext[CpuRegister.Rdi] = handle;
            waitContext[CpuRegister.Rsi] = 1;
            waitContext[CpuRegister.Rdx] = timeoutAddress;
            var waitTask = Task.Run(() => KernelSemaphoreCompatExports.KernelWaitSema(waitContext));

            await Task.Delay(20);
            Assert.Equal(0, KernelSemaphoreCompatExports.KernelSignalSema(signalContext, handle, 1));
            Assert.Equal(0, await waitTask.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(waitContext.TryReadUInt32(timeoutAddress, out var remainingTimeout));
            Assert.Equal(0U, remainingTimeout);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY,
                KernelSemaphoreCompatExports.KernelPollSema(waitContext, handle, 1));
        }
        finally
        {
            DeleteSemaphore(waitContext, handle);
        }
    }

    private static uint CreateSemaphore(
        CpuContext context,
        FakeCpuMemory memory,
        string name,
        int initialCount,
        int maxCount)
    {
        const ulong nameAddress = MemoryBase + 0x100;
        const ulong handleAddress = MemoryBase + 0x180;
        memory.WriteCString(nameAddress, name);
        Assert.True(context.TryWriteUInt32(handleAddress, 0));
        context[CpuRegister.Rdi] = handleAddress;
        context[CpuRegister.Rsi] = nameAddress;
        context[CpuRegister.Rdx] = 1;
        context[CpuRegister.Rcx] = unchecked((ulong)initialCount);
        context[CpuRegister.R8] = unchecked((ulong)maxCount);
        context[CpuRegister.R9] = 0;

        Assert.Equal(0, KernelSemaphoreCompatExports.KernelCreateSema(context));
        Assert.True(context.TryReadUInt32(handleAddress, out var handle));
        Assert.NotEqual(0U, handle);
        return handle;
    }

    private static void DeleteSemaphore(CpuContext context, uint handle)
    {
        context[CpuRegister.Rdi] = handle;
        Assert.Equal(0, KernelSemaphoreCompatExports.KernelDeleteSema(context));
    }

    private static ulong AllocateTracked(CpuContext context, int length)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)length);
        Assert.Equal(0, KernelMemoryCompatExports.Malloc(context));
        Assert.NotEqual(0UL, context[CpuRegister.Rax]);
        return context[CpuRegister.Rax];
    }

    private static void FreeTracked(CpuContext context, ulong address)
    {
        context[CpuRegister.Rdi] = address;
        Assert.Equal(0, KernelMemoryCompatExports.Free(context));
    }
}
