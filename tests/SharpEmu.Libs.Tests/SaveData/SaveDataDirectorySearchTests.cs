// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests.SaveData;

public sealed class SaveDataDirectorySearchTests
{
    [Fact]
    public void DirectorySearch_ReadsAndWritesLibcBackedResult()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong conditionAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Span<byte> condition = stackalloc byte[0x20];
        condition.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(condition, 0x1000_0000);
        Assert.True(memory.TryWrite(conditionAddress, condition));
        SaveDataExports.ConfigureApplicationInfo("PPSATEST01");

        var resultAddress = AllocateTracked(context, 0x30);
        try
        {
            var result = new byte[0x30];
            Marshal.Copy(result, 0, unchecked((nint)resultAddress), result.Length);
            context[CpuRegister.Rdi] = conditionAddress;
            context[CpuRegister.Rsi] = resultAddress;

            Assert.Equal(0, SaveDataExports.SaveDataDirNameSearch(context));
            Assert.Equal(0, Marshal.ReadInt32(unchecked((nint)resultAddress)));
            Assert.Equal(0, Marshal.ReadInt32(unchecked((nint)(resultAddress + 0x14))));
        }
        finally
        {
            FreeTracked(context, resultAddress);
            SaveDataExports.ConfigureApplicationInfo(null);
        }
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
