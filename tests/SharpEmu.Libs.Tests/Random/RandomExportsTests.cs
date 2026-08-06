// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Random;
using Xunit;

namespace SharpEmu.Libs.Tests.Random;

public sealed class RandomExportsTests
{
    private const string Nid = "PI7jIZj4pcE";
    private const ulong OutputAddress = 0x1_2345_6000;
    private const int RandomErrorInvalidArgument = unchecked((int)0x817C0016);

    [Fact]
    public void GetRandomNumber_RegistersOneExactGen5Export()
    {
        var gen5Exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5)
            .Where(export => export.Nid == Nid)
            .ToArray();
        var gen4Exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4)
            .Where(export => export.Nid == Nid)
            .ToArray();

        var export = Assert.Single(gen5Exports);
        Assert.Equal("sceRandomGetRandomNumber", export.Name);
        Assert.Equal("libSceRandom", export.LibraryName);
        Assert.True(export.PreferLle);
        Assert.Empty(gen4Exports);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(64)]
    public void GetRandomNumber_WritesExactlyRequestedPrefix(int requestedByteCount)
    {
        var memory = new RecordingMemory(acceptWrites: true);
        var context = CreateContext(memory, OutputAddress, (ulong)requestedByteCount);

        Assert.Equal(0, RandomExports.GetRandomNumber(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        var write = Assert.Single(memory.Writes);
        Assert.Equal(OutputAddress, write.Address);
        Assert.Equal(requestedByteCount, write.Bytes.Length);
    }

    [Fact]
    public void GetRandomNumber_NonNullZeroLengthSucceedsWithoutWritingGuestMemory()
    {
        var memory = new RecordingMemory(acceptWrites: false);
        var context = CreateContext(memory, OutputAddress, 0);

        Assert.Equal(0, RandomExports.GetRandomNumber(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Empty(memory.Writes);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 64)]
    [InlineData(OutputAddress, 65)]
    [InlineData(OutputAddress, ulong.MaxValue)]
    public void GetRandomNumber_RejectsNullOutputAndCountsAbove64(ulong outputAddress, ulong requestedByteCount)
    {
        var memory = new RecordingMemory(acceptWrites: true);
        var context = CreateContext(memory, outputAddress, requestedByteCount);

        Assert.Equal(RandomErrorInvalidArgument, RandomExports.GetRandomNumber(context));
        Assert.Equal(unchecked((ulong)RandomErrorInvalidArgument), context[CpuRegister.Rax]);
        Assert.Empty(memory.Writes);
    }

    [Fact]
    public void GetRandomNumber_UnmappedOutputReturnsSafeMemoryFault()
    {
        var memory = new RecordingMemory(acceptWrites: false);
        var context = CreateContext(memory, OutputAddress, 16);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            RandomExports.GetRandomNumber(context));
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT),
            context[CpuRegister.Rax]);
        Assert.Empty(memory.Writes);
        Assert.Equal(1, memory.RejectedWriteCount);
    }

    [Fact]
    public void GetRandomNumber_DispatchesSemanticFallback()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        var memory = new RecordingMemory(acceptWrites: true);
        var context = CreateContext(memory, OutputAddress, 8);

        Assert.True(manager.TryDispatch(Nid, context, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(8, Assert.Single(memory.Writes).Bytes.Length);
    }

    private static CpuContext CreateContext(
        ICpuMemory memory,
        ulong outputAddress,
        ulong requestedByteCount)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = outputAddress;
        context[CpuRegister.Rsi] = requestedByteCount;
        return context;
    }

    private sealed class RecordingMemory(bool acceptWrites) : ICpuMemory
    {
        public List<(ulong Address, byte[] Bytes)> Writes { get; } = [];

        public int RejectedWriteCount { get; private set; }

        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!acceptWrites)
            {
                RejectedWriteCount++;
                return false;
            }

            Writes.Add((virtualAddress, source.ToArray()));
            return true;
        }
    }
}
