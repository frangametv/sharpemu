// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Media;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition("KernelFileCompatState", DisableParallelization = true)]
public sealed class KernelFileCompatStateCollection;

[Collection("KernelFileCompatState")]
public sealed class KernelFileCompatExportsTests : IDisposable
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong PathAddress = Base + 0x100;
    private const int OpenWriteCreateTruncate = 0x1 | 0x0200 | 0x0400;

    private readonly FakeCpuMemory _memory = new(Base, 0x20000);
    private readonly CpuContext _ctx;
    private readonly string _root;

    public KernelFileCompatExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        _root = Path.Combine(Path.GetTempPath(), $"sharpemu-kernel-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        HostMovieBridge.ResetForTests();
        KernelMemoryCompatExports.ClearGuestPathMounts();
        KernelMemoryCompatExports.RegisterGuestPathMount("/savedata0", _root);
    }

    public void Dispose()
    {
        HostMovieBridge.ResetForTests();
        KernelMemoryCompatExports.ClearGuestPathMounts();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void KernelPread_ObservesValidatedEmbeddedBinkRangeWithoutChangingIo()
    {
        const int movieOffset = 64;
        const int movieLength = 256;
        const ulong bufferAddress = Base + 0x1000;
        var previousMode = Environment.GetEnvironmentVariable("SHARPEMU_BINK_MODE");
        var archivePath = Path.Combine(_root, "prosperoa.rpf");
        var archive = new byte[512];
        CreateBinkHeader(movieLength).CopyTo(archive, movieOffset);
        File.WriteAllBytes(archivePath, archive);
        Assert.True(_memory.TryWrite(PathAddress, "/savedata0/prosperoa.rpf\0"u8));
        var fd = -1;

        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_BINK_MODE", "skip");
            _ctx[CpuRegister.Rdi] = PathAddress;
            _ctx[CpuRegister.Rsi] = 0;
            Assert.Equal(0, KernelMemoryCompatExports.KernelOpenUnderscore(_ctx));
            fd = unchecked((int)_ctx[CpuRegister.Rax]);

            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            _ctx[CpuRegister.Rsi] = bufferAddress;
            _ctx[CpuRegister.Rdx] = movieLength;
            _ctx[CpuRegister.Rcx] = movieOffset;
            _ctx.Rip = 0x80283A98C;
            const ulong returnRip = 0x802939E28;
            const ulong callerReturnRip = 0x8029325C4;
            _ctx[CpuRegister.Rsp] = Base + 0x8000;
            Assert.True(_ctx.TryWriteUInt64(_ctx[CpuRegister.Rsp], returnRip));
            Assert.True(_ctx.TryWriteUInt64(_ctx[CpuRegister.Rsp] + 0x50, callerReturnRip));
            Assert.Equal(0, KernelMemoryCompatExports.KernelPread(_ctx));
            Assert.Equal((ulong)movieLength, _ctx[CpuRegister.Rax]);

            var guestBytes = new byte[movieLength];
            Assert.True(_memory.TryRead(bufferAddress, guestBytes));
            Assert.Equal(archive.AsSpan(movieOffset, movieLength).ToArray(), guestBytes);

            var observed = HostMovieBridge.LastObservedMovieRange;
            Assert.True(observed.HasValue);
            Assert.Equal(BinkMovieMode.Skip, observed.Value.Mode);
            Assert.Equal(BinkMovieRangeAttachment.None, observed.Value.Attachment);
            Assert.Equal(fd, observed.Value.FileDescriptor);
            Assert.Equal(movieOffset, observed.Value.FileOffset);
            Assert.Equal(movieLength, observed.Value.Header.ByteLength);
            Assert.Equal(bufferAddress, observed.Value.GuestDestination);
            Assert.Equal(0x80283A98CUL, observed.Value.GuestRip);
            Assert.Equal(returnRip, observed.Value.GuestReturnRip);
            Assert.Equal(callerReturnRip, observed.Value.GuestCallerReturnRip);

            Assert.Equal(archive, File.ReadAllBytes(archivePath));
            Assert.Equal([archivePath], Directory.GetFiles(_root));
        }
        finally
        {
            if (fd > 2)
            {
                Close(fd);
            }

            Environment.SetEnvironmentVariable("SHARPEMU_BINK_MODE", previousMode);
            HostMovieBridge.ResetForTests();
        }
    }

    [Fact]
    public void KernelWrite_ReadsLibcBackedBuffer()
    {
        var guestPath = "/savedata0/metadata.bin\0"u8.ToArray();
        Assert.True(_memory.TryWrite(PathAddress, guestPath));
        _ctx[CpuRegister.Rdi] = PathAddress;
        _ctx[CpuRegister.Rsi] = OpenWriteCreateTruncate;
        Assert.Equal(0, KernelMemoryCompatExports.KernelOpenUnderscore(_ctx));
        var fd = unchecked((int)_ctx[CpuRegister.Rax]);
        Assert.True(fd > 2);

        var expected = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var payloadAddress = AllocateTracked(expected.Length);
        try
        {
            Marshal.Copy(expected, 0, unchecked((nint)payloadAddress), expected.Length);
            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            _ctx[CpuRegister.Rsi] = payloadAddress;
            _ctx[CpuRegister.Rdx] = unchecked((ulong)expected.Length);
            Assert.Equal(0, KernelMemoryCompatExports.KernelWrite(_ctx));
            Assert.Equal(unchecked((ulong)expected.Length), _ctx[CpuRegister.Rax]);
        }
        finally
        {
            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            Assert.Equal(0, KernelMemoryCompatExports.KernelClose(_ctx));
            FreeTracked(payloadAddress);
        }

        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(_root, "metadata.bin")));
    }

    [Fact]
    public void KernelGetdents_EmptyDirectoryReturnsDotEntriesBeforeEof()
    {
        const ulong bufferAddress = Base + 0x1000;
        var fd = OpenRootDirectory();

        try
        {
            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            _ctx[CpuRegister.Rsi] = bufferAddress;
            _ctx[CpuRegister.Rdx] = 0x10000;
            Assert.Equal(0, KernelMemoryCompatExports.KernelGetdents(_ctx));

            Assert.Equal(32UL, _ctx[CpuRegister.Rax]);
            Assert.Equal((ushort)16, ReadUInt16(bufferAddress + 4));
            Assert.Equal((byte)4, ReadByte(bufferAddress + 6));
            Assert.Equal(".", ReadCString(bufferAddress + 8));
            Assert.Equal((ushort)16, ReadUInt16(bufferAddress + 16 + 4));
            Assert.Equal((byte)4, ReadByte(bufferAddress + 16 + 6));
            Assert.Equal("..", ReadCString(bufferAddress + 16 + 8));

            var sentinel = Enumerable.Repeat((byte)0xA5, 32).ToArray();
            Assert.True(_memory.TryWrite(bufferAddress, sentinel));
            Assert.Equal(0, KernelMemoryCompatExports.KernelGetdents(_ctx));
            Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
            var afterEof = new byte[sentinel.Length];
            Assert.True(_memory.TryRead(bufferAddress, afterEof));
            Assert.Equal(sentinel, afterEof);
        }
        finally
        {
            Close(fd);
        }
    }

    [Fact]
    public void KernelGetdents_FailedGuestWriteDoesNotAdvanceDirectoryCursor()
    {
        const ulong validBufferAddress = Base + 0x1000;
        const ulong invalidBufferAddress = Base + 0x30000;
        var fd = OpenRootDirectory();

        try
        {
            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            _ctx[CpuRegister.Rsi] = invalidBufferAddress;
            _ctx[CpuRegister.Rdx] = 16;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                KernelMemoryCompatExports.KernelGetdents(_ctx));

            _ctx[CpuRegister.Rsi] = validBufferAddress;
            Assert.Equal(0, KernelMemoryCompatExports.KernelGetdents(_ctx));
            Assert.Equal(16UL, _ctx[CpuRegister.Rax]);
            Assert.Equal(".", ReadCString(validBufferAddress + 8));
        }
        finally
        {
            Close(fd);
        }
    }

    [Fact]
    public void KernelGetdirentries_ReportsByteOffsetForEachSplitRead()
    {
        const ulong bufferAddress = Base + 0x1000;
        const ulong basePointerAddress = Base + 0x800;
        var fd = OpenRootDirectory();

        try
        {
            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            _ctx[CpuRegister.Rsi] = bufferAddress;
            _ctx[CpuRegister.Rdx] = 16;
            _ctx[CpuRegister.Rcx] = basePointerAddress;

            Assert.Equal(0, KernelMemoryCompatExports.KernelGetdirentries(_ctx));
            Assert.Equal(16UL, _ctx[CpuRegister.Rax]);
            Assert.Equal(0UL, ReadUInt64(basePointerAddress));
            Assert.Equal(".", ReadCString(bufferAddress + 8));

            Assert.Equal(0, KernelMemoryCompatExports.KernelGetdirentries(_ctx));
            Assert.Equal(16UL, _ctx[CpuRegister.Rax]);
            Assert.Equal(16UL, ReadUInt64(basePointerAddress));
            Assert.Equal("..", ReadCString(bufferAddress + 8));

            Assert.Equal(0, KernelMemoryCompatExports.KernelGetdirentries(_ctx));
            Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
            Assert.Equal(32UL, ReadUInt64(basePointerAddress));
        }
        finally
        {
            Close(fd);
        }
    }

    [Fact]
    public void KernelGetdents_PacksHostEntriesUsingRecordLengths()
    {
        const ulong bufferAddress = Base + 0x1000;
        File.WriteAllText(Path.Combine(_root, "asset.bin"), "asset");
        Directory.CreateDirectory(Path.Combine(_root, "folder"));
        var fd = OpenRootDirectory();

        try
        {
            _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
            _ctx[CpuRegister.Rsi] = bufferAddress;
            _ctx[CpuRegister.Rdx] = 0x10000;
            Assert.Equal(0, KernelMemoryCompatExports.KernelGetdents(_ctx));

            var bytesWritten = checked((int)_ctx[CpuRegister.Rax]);
            var names = new List<string>();
            var types = new List<byte>();
            var offset = 0;
            while (offset < bytesWritten)
            {
                var recordLength = ReadUInt16(bufferAddress + unchecked((ulong)offset) + 4);
                Assert.True(recordLength >= 16);
                names.Add(ReadCString(bufferAddress + unchecked((ulong)offset) + 8));
                types.Add(ReadByte(bufferAddress + unchecked((ulong)offset) + 6));
                offset += recordLength;
            }

            Assert.Equal(bytesWritten, offset);
            Assert.Equal([".", "..", "asset.bin", "folder"], names);
            Assert.Equal([(byte)4, (byte)4, (byte)8, (byte)4], types);
        }
        finally
        {
            Close(fd);
        }
    }

    private ulong AllocateTracked(int length)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)length);
        Assert.Equal(0, KernelMemoryCompatExports.Malloc(_ctx));
        Assert.NotEqual(0UL, _ctx[CpuRegister.Rax]);
        return _ctx[CpuRegister.Rax];
    }

    private static byte[] CreateBinkHeader(int byteLength)
    {
        var header = new byte[0x24];
        "KB2j"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x04, 4), checked((uint)byteLength - 8));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x08, 4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x0C, 4), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x14, 4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x18, 4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x1C, 4), 30);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x20, 4), 1);
        return header;
    }

    private void FreeTracked(ulong address)
    {
        _ctx[CpuRegister.Rdi] = address;
        Assert.Equal(0, KernelMemoryCompatExports.Free(_ctx));
    }

    private int OpenRootDirectory()
    {
        Assert.True(_memory.TryWrite(PathAddress, "/savedata0\0"u8));
        _ctx[CpuRegister.Rdi] = PathAddress;
        _ctx[CpuRegister.Rsi] = 0x00020000;
        _ctx[CpuRegister.Rdx] = 0x1FF;
        Assert.Equal(0, KernelMemoryCompatExports.KernelOpenUnderscore(_ctx));
        var fd = unchecked((int)_ctx[CpuRegister.Rax]);
        Assert.True(fd > 2);
        return fd;
    }

    private void Close(int fd)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)fd);
        Assert.Equal(0, KernelMemoryCompatExports.KernelClose(_ctx));
    }

    private byte ReadByte(ulong address)
    {
        Span<byte> value = stackalloc byte[1];
        Assert.True(_memory.TryRead(address, value));
        return value[0];
    }

    private ushort ReadUInt16(ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(ushort)];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt16LittleEndian(value);
    }

    private ulong ReadUInt64(ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }

    private string ReadCString(ulong address)
    {
        Span<byte> value = stackalloc byte[256];
        Assert.True(_memory.TryRead(address, value));
        var length = value.IndexOf((byte)0);
        Assert.True(length >= 0);
        return Encoding.UTF8.GetString(value[..length]);
    }
}
