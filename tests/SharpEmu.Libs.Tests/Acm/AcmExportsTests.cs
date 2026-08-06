// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Acm;
using Xunit;

namespace SharpEmu.Libs.Tests.Acm;

public sealed class AcmExportsTests : IDisposable
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int OpenFailed = unchecked((int)0x81940001);
    private const int OutOfMemory = unchecked((int)0x81940004);
    private const int TooManyOpenFiles = unchecked((int)0x81940005);
    private const int InvalidArgument = unchecked((int)0x81940006);

    public AcmExportsTests() => AcmExports.ResetForTests();

    public void Dispose() => AcmExports.ResetForTests();

    [Fact]
    public void ContextCreate_RejectsNullOutput()
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x100), Generation.Gen5);

        Assert.Equal(InvalidArgument, AcmExports.ContextCreate(ctx));
        Assert.Equal(unchecked((ulong)InvalidArgument), ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void ContextCreate_InitializesMinusOneBeforeUniqueDescriptors()
    {
        var memory = new RecordingMemory(BaseAddress, 0x100);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = BaseAddress + 0x20;

        Assert.Equal(0, AcmExports.ContextCreate(ctx));
        Assert.Equal(1, memory.ReadInt32(BaseAddress + 0x20));

        ctx[CpuRegister.Rdi] = BaseAddress + 0x24;
        Assert.Equal(0, AcmExports.ContextCreate(ctx));
        Assert.Equal(2, memory.ReadInt32(BaseAddress + 0x24));
        Assert.Equal(new[] { -1, 1, -1, 2 }, memory.Int32Writes);
    }

    [Theory]
    [InlineData(0x17, TooManyOpenFiles)]
    [InlineData(0x18, TooManyOpenFiles)]
    [InlineData(0x0C, OutOfMemory)]
    [InlineData(0x05, OpenFailed)]
    public void ContextCreate_MapsOpenErrnoAndLeavesMinusOne(
        int errno,
        int expected)
    {
        var memory = new RecordingMemory(BaseAddress, 0x100);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var contextAddress = BaseAddress + 0x20;
        ctx[CpuRegister.Rdi] = contextAddress;
        AcmExports.SetOpenDeviceForTests(() => (-1, errno));

        Assert.Equal(expected, AcmExports.ContextCreate(ctx));
        Assert.Equal(new[] { -1 }, memory.Int32Writes);
        Assert.Equal(-1, memory.ReadInt32(contextAddress));
    }

    [Fact]
    public void ContextCreateNid_RegistersWithAcmIdentity()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("ZIXln2K3XMk", out var export));
        Assert.Equal("sceAcmContextCreate", export.Name);
        Assert.Equal("libSceAcm", export.LibraryName);
    }

    private sealed class RecordingMemory : ICpuMemory
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _storage;

        public RecordingMemory(ulong baseAddress, int size)
        {
            _baseAddress = baseAddress;
            _storage = new byte[size];
        }

        public List<int> Int32Writes { get; } = [];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            if (source.Length == sizeof(int))
            {
                Int32Writes.Add(BinaryPrimitives.ReadInt32LittleEndian(source));
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        public int ReadInt32(ulong address)
        {
            Span<byte> value = stackalloc byte[sizeof(int)];
            Assert.True(TryRead(address, value));
            return BinaryPrimitives.ReadInt32LittleEndian(value);
        }

        private bool TryResolve(ulong address, int length, out int offset)
        {
            offset = 0;
            if (address < _baseAddress)
            {
                return false;
            }

            var relative = address - _baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
