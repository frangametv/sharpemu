// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

public sealed class PthreadUserContextTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong ContextAddress = BaseAddress + 0x800;

    [Fact]
    public void UserContextExports_RegisterWithFirmwareNids()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        AssertExport(manager, "cfjAjVTFG6A", "pthread_suspend_user_context_np");
        AssertExport(manager, "QRdE7dBfNks", "pthread_resume_user_context_np");
        AssertExport(manager, "YkGOXpJEtO8", "pthread_get_user_context_np");
        AssertExport(manager, "el9stmu6290", "pthread_set_user_context_np");
    }

    [Fact]
    public void UserContextWriter_UsesTwelveSeventyOrbisUcontextOffsets()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var context = new CpuContext(memory, Generation.Gen5);
        var continuation = new GuestCpuContinuation(
            Rip: 0x101,
            Rsp: 0x102,
            ReturnSlotAddress: 0,
            Rflags: 0x103,
            FsBase: 0x104,
            GsBase: 0x105,
            Rax: 0x106,
            Rcx: 0x107,
            Rdx: 0x108,
            Rbx: 0x109,
            Rbp: 0x10A,
            Rsi: 0x10B,
            Rdi: 0x10C,
            R8: 0x10D,
            R9: 0x10E,
            R10: 0x10F,
            R11: 0x110,
            R12: 0x111,
            R13: 0x112,
            R14: 0x113,
            R15: 0x114,
            FpuControlWord: 0x115,
            Mxcsr: 0x116,
            RestoreFullFpuState: false);

        Assert.True(KernelPthreadExtendedCompatExports.TryWritePthreadUserContext(
            context,
            ContextAddress,
            continuation));

        Assert.Equal(continuation.Rdi, ReadUInt64(memory, ContextAddress + 0x48));
        Assert.Equal(continuation.Rsi, ReadUInt64(memory, ContextAddress + 0x50));
        Assert.Equal(continuation.Rdx, ReadUInt64(memory, ContextAddress + 0x58));
        Assert.Equal(continuation.Rcx, ReadUInt64(memory, ContextAddress + 0x60));
        Assert.Equal(continuation.R8, ReadUInt64(memory, ContextAddress + 0x68));
        Assert.Equal(continuation.R9, ReadUInt64(memory, ContextAddress + 0x70));
        Assert.Equal(continuation.Rax, ReadUInt64(memory, ContextAddress + 0x78));
        Assert.Equal(continuation.Rbx, ReadUInt64(memory, ContextAddress + 0x80));
        Assert.Equal(continuation.Rbp, ReadUInt64(memory, ContextAddress + 0x88));
        Assert.Equal(continuation.R10, ReadUInt64(memory, ContextAddress + 0x90));
        Assert.Equal(continuation.R11, ReadUInt64(memory, ContextAddress + 0x98));
        Assert.Equal(continuation.R12, ReadUInt64(memory, ContextAddress + 0xA0));
        Assert.Equal(continuation.R13, ReadUInt64(memory, ContextAddress + 0xA8));
        Assert.Equal(continuation.R14, ReadUInt64(memory, ContextAddress + 0xB0));
        Assert.Equal(continuation.R15, ReadUInt64(memory, ContextAddress + 0xB8));
        Assert.Equal(continuation.Rip, ReadUInt64(memory, ContextAddress + 0xE0));
        Assert.Equal(continuation.Rflags, ReadUInt64(memory, ContextAddress + 0xF0));
        Assert.Equal(continuation.Rsp, ReadUInt64(memory, ContextAddress + 0xF8));
        Assert.Equal(0x480UL, ReadUInt64(memory, ContextAddress + 0x108));
        Assert.Equal(continuation.FsBase, ReadUInt64(memory, ContextAddress + 0x480));
        Assert.Equal(continuation.GsBase, ReadUInt64(memory, ContextAddress + 0x488));
    }

    private static void AssertExport(ModuleManager manager, string nid, string name)
    {
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libKernel", export.LibraryName);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }
}
