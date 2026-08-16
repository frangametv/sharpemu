// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Codec;
using Xunit;

namespace SharpEmu.Libs.Tests.Codec;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Videodec2StateCollection
{
    public const string Name = "Videodec2State";
}

[Collection(Videodec2StateCollection.Name)]
public sealed class Videodec2ExportsTests : IDisposable
{
    private const ulong MemoryBase = 0x2_0000_0000;
    private const ulong QueueAddress = MemoryBase + 0x100;
    private const ulong MemoryInfoAddress = MemoryBase + 0x200;
    private const ulong DecoderAddress = MemoryBase + 0x300;
    private const ulong OutputInfoAddress = MemoryBase + 0x400;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _context;

    public Videodec2ExportsTests()
    {
        Videodec2Exports.ResetForTests();
        _context = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void ComputeAndDecoderMemoryQueriesWriteSafeValues()
    {
        _context[CpuRegister.Rdi] = MemoryBase + 0x80;
        Assert.Equal(0, Videodec2Exports.Videodec2QueryComputeMemoryInfo(_context));

        _context[CpuRegister.Rdi] = QueueAddress;
        Assert.Equal(0, Videodec2Exports.Videodec2AllocateComputeQueue(_context));
        Assert.NotEqual(0UL, ReadUInt64(QueueAddress));

        Span<byte> initial = stackalloc byte[0x40];
        initial.Fill(0xCC);
        Assert.True(_memory.TryWrite(MemoryInfoAddress, initial));
        _context[CpuRegister.Rsi] = MemoryInfoAddress;
        Assert.Equal(0, Videodec2Exports.Videodec2QueryDecoderMemoryInfo(_context));
        Assert.Equal(0UL, ReadUInt64(MemoryInfoAddress + 0x08));
        Assert.Equal(0UL, ReadUInt64(MemoryInfoAddress + 0x28));
        Assert.Equal(0x1000UL, ReadUInt64(MemoryInfoAddress + 0x38));
    }

    [Fact]
    public void DecoderLifecycleUsesFreshOpaqueHandles()
    {
        _context[CpuRegister.Rdx] = DecoderAddress;
        Assert.Equal(0, Videodec2Exports.Videodec2CreateDecoder(_context));
        var firstHandle = ReadUInt64(DecoderAddress);
        Assert.NotEqual(0UL, firstHandle);

        _context[CpuRegister.Rdx] = DecoderAddress + sizeof(ulong);
        Assert.Equal(0, Videodec2Exports.Videodec2CreateDecoder(_context));
        var secondHandle = ReadUInt64(DecoderAddress + sizeof(ulong));
        Assert.NotEqual(firstHandle, secondHandle);

        foreach (var handle in new[] { firstHandle, secondHandle })
        {
            _context[CpuRegister.Rdi] = handle;
            Assert.Equal(0, Videodec2Exports.Videodec2Reset(_context));
            Assert.Equal(0, Videodec2Exports.Videodec2DeleteDecoder(_context));
        }
    }

    [Fact]
    public void DecodeStubClearsOnlyPictureReadyByte()
    {
        Span<byte> canary = stackalloc byte[0x20];
        canary.Fill(0xCC);
        Assert.True(_memory.TryWrite(OutputInfoAddress, canary));

        _context[CpuRegister.Rdi] = 0xDEAD_BEEF;
        _context[CpuRegister.Rcx] = OutputInfoAddress;
        Assert.Equal(0, Videodec2Exports.Videodec2Decode(_context));

        Span<byte> result = stackalloc byte[2];
        Assert.True(_memory.TryRead(OutputInfoAddress, result));
        Assert.Equal(0, result[0]);
        Assert.Equal(0xCC, result[1]);
    }

    [Fact]
    public void ExportsRegisterOnlyForGen5()
    {
        var gen5 = new ModuleManager();
        gen5.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(gen5.TryGetExport("RnDibcGCPKw", out _));
        Assert.True(gen5.TryGetExport("eD+X2SmxUt4", out _));
        Assert.True(gen5.TryGetExport("qqMCwlULR+E", out _));
        Assert.True(gen5.TryGetExport("CNNRoRYd8XI", out _));
        Assert.True(gen5.TryGetExport("l1hXwscLuCY", out _));
        Assert.True(gen5.TryGetExport("wJXikG6QFN8", out _));
        Assert.True(gen5.TryGetExport("jwImxXRGSKA", out _));
        Assert.True(gen5.TryGetExport("852F5+q6+iM", out _));

        var gen4 = new ModuleManager();
        gen4.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));
        Assert.False(gen4.TryGetExport("RnDibcGCPKw", out _));
    }

    public void Dispose() => Videodec2Exports.ResetForTests();

    private ulong ReadUInt64(ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }
}
