// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.LibcStdio;
using Xunit;

namespace SharpEmu.Libs.Tests.LibcStdio;

public sealed class LibcStdioExportsTests
{
    private const ulong GuestBase = 0x1_0000_0000;
    private const ulong TestFileHandle = 0xF000_0000_0000_0001;
    private const ulong InvalidPointerTestFileHandle = 0xF000_0000_0000_0002;

    private static readonly ConcurrentDictionary<ulong, FileStream> FileHandles =
        (ConcurrentDictionary<ulong, FileStream>)typeof(LibcStdioExports)
            .GetField("_fileHandles", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    [Fact]
    public void FreadWritesIntoTrackedNativeLibcAllocation()
    {
        var payload = Encoding.UTF8.GetBytes("dreaming-sarah-menu");
        var hostPath = Path.Combine(Path.GetTempPath(), $"sharpemu-fread-{Guid.NewGuid():N}.bin");
        var memory = new FakeCpuMemory(GuestBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        ulong handle = 0;
        ulong destination = 0;

        File.WriteAllBytes(hostPath, payload);
        try
        {
            Assert.True(FileHandles.TryAdd(
                TestFileHandle,
                new FileStream(hostPath, FileMode.Open, FileAccess.Read, FileShare.Read)));
            handle = TestFileHandle;

            context[CpuRegister.Rdi] = (ulong)payload.Length;
            Assert.Equal(0, KernelMemoryCompatExports.Malloc(context));
            destination = context[CpuRegister.Rax];
            Assert.NotEqual(0UL, destination);

            context[CpuRegister.Rdi] = destination;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = (ulong)payload.Length;
            context[CpuRegister.Rcx] = handle;

            Assert.Equal(0, LibcStdioExports.Fread(context));
            Assert.Equal((ulong)payload.Length, context[CpuRegister.Rax]);

            var actual = new byte[payload.Length];
            Marshal.Copy(unchecked((nint)destination), actual, 0, actual.Length);
            Assert.Equal(payload, actual);
        }
        finally
        {
            if (handle != 0)
            {
                context[CpuRegister.Rdi] = handle;
                LibcStdioExports.Fclose(context);
            }

            if (destination != 0)
            {
                context[CpuRegister.Rdi] = destination;
                KernelMemoryCompatExports.Free(context);
            }

            File.Delete(hostPath);
        }
    }

    [Fact]
    public void FreadRejectsUnmappedDestination()
    {
        var payload = Encoding.UTF8.GetBytes("invalid-destination");
        var hostPath = Path.Combine(Path.GetTempPath(), $"sharpemu-fread-invalid-{Guid.NewGuid():N}.bin");
        var context = new CpuContext(new FakeCpuMemory(GuestBase, 0x1000), Generation.Gen5);

        File.WriteAllBytes(hostPath, payload);
        try
        {
            Assert.True(FileHandles.TryAdd(
                InvalidPointerTestFileHandle,
                new FileStream(hostPath, FileMode.Open, FileAccess.Read, FileShare.Read)));

            context[CpuRegister.Rdi] = 0xDEAD_BEEF;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = (ulong)payload.Length;
            context[CpuRegister.Rcx] = InvalidPointerTestFileHandle;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                LibcStdioExports.Fread(context));
            Assert.Equal(0UL, context[CpuRegister.Rax]);
        }
        finally
        {
            context[CpuRegister.Rdi] = InvalidPointerTestFileHandle;
            LibcStdioExports.Fclose(context);
            File.Delete(hostPath);
        }
    }

}
