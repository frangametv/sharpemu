// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

[CollectionDefinition("AudioOut2State", DisableParallelization = true)]
public sealed class AudioOut2StateCollection
{
    public const string Name = "AudioOut2State";
}

[Collection(AudioOut2StateCollection.Name)]
public sealed class AudioOut2ExportsTests : IDisposable
{
    private const int AudioOut2InvalidArgument = unchecked((int)0x80268001);
    private const ulong MemoryBase = 0x1000;
    private const ulong OutHandleAddress = 0x1100;
    private const ulong DescriptorAddress = 0x1200;
    private const ulong AuxiliaryAddress = 0x1300;
    private const ulong PositionsAddress = 0x1400;
    private const ulong CoefficientsAddress = 0x1800;
    private const ulong WorkspaceAddress = 0x2000;
    private const ulong GtaWorkspaceSize = 0xAD40;

    public AudioOut2ExportsTests() => AudioOut2Exports.ResetSpeakerArraysForTests();

    public void Dispose() => AudioOut2Exports.ResetSpeakerArraysForTests();

    [Theory]
    [InlineData(8U, 0U, 0U, 0x2E0UL)]
    [InlineData(8U, 0U, 1U, 0xAE00UL)]
    [InlineData(8U, 1U, 0U, 0x860UL)]
    [InlineData(8U, 1U, 1U, 0xB380UL)]
    [InlineData(2U, 0U, 1U, GtaWorkspaceSize)]
    public void GetSpeakerArrayMemorySize_MatchesFirmware1270(
        uint speakerCount,
        uint useObjectLayout,
        uint includeCoefficients,
        ulong expectedSize)
    {
        var ctx = new CpuContext(new NullMemory(), Generation.Gen5);
        ctx[CpuRegister.Rdi] = speakerCount;
        ctx[CpuRegister.Rsi] = useObjectLayout;
        ctx[CpuRegister.Rdx] = includeCoefficients;

        Assert.Equal(0, AudioOut2Exports.AudioOut2GetSpeakerArrayMemorySize(ctx));
        Assert.Equal(expectedSize, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void GetSpeakerArrayMemorySize_RegistersExactGen5NidAsLlePreferred()
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == "G1YOKDJYX2Y");

        Assert.Equal("sceAudioOut2GetSpeakerArrayMemorySize", export.Name);
        Assert.Equal("libSceAudioOut2", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
        Assert.True(export.PreferLle);
    }

    [Theory]
    [InlineData("+k91hoTuoA8", "sceAudioOut2SpeakerArrayCreate")]
    [InlineData("28QqMnuuJ9Y", "sceAudioOut2GetSpeakerArrayAmbisonicsCoefficients")]
    [InlineData("erCWQR5eKiQ", "sceAudioOut2SpeakerArrayDestroy")]
    public void SpeakerArrayLifecycle_RegistersExactGen5NidsAsSemanticLleFallbacks(
        string nid,
        string name)
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == nid);

        Assert.Equal(name, export.Name);
        Assert.Equal("libSceAudioOut2", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
        Assert.True(export.PreferLle);
        Assert.Equal(typeof(AudioOut2Exports), export.Function.Method.DeclaringType);
    }

    [Fact]
    public void SpeakerArrayCreate_GtaDescriptorWritesWorkspaceTailHandleAndIgnoresRcx()
    {
        var memory = CreateSpeakerArrayMemory();
        WriteSpeakerArrayDescriptor(memory);
        var ctx = CreateSpeakerArrayContext(memory);
        ctx[CpuRegister.Rcx] = 0xDEAD_BEEF_CAFE_BABE;

        Assert.Equal(0, AudioOut2Exports.AudioOut2SpeakerArrayCreate(ctx));

        var expectedHandle = WorkspaceAddress + GtaWorkspaceSize - 0x18;
        Span<byte> outputAndCanary = stackalloc byte[0x10];
        Assert.True(memory.TryRead(OutHandleAddress, outputAndCanary));
        Assert.Equal(expectedHandle, BinaryPrimitives.ReadUInt64LittleEndian(outputAndCanary));
        Assert.Equal(0xC0DE_C0DE_CAFE_BA00UL, BinaryPrimitives.ReadUInt64LittleEndian(outputAndCanary[8..]));

        Span<byte> footer = stackalloc byte[0x18];
        Assert.True(memory.TryRead(expectedHandle, footer));
        Assert.Equal(new byte[0x18], footer.ToArray());
        Assert.Equal(1, AudioOut2Exports.SpeakerArrayCountForTests);
    }

    [Fact]
    public void SpeakerArrayLifecycle_InitializesAllGtaCoefficientRowsAndDestroysState()
    {
        var memory = CreateSpeakerArrayMemory();
        WriteSpeakerArrayDescriptor(memory);
        var create = CreateSpeakerArrayContext(memory);
        Assert.Equal(0, AudioOut2Exports.AudioOut2SpeakerArrayCreate(create));
        var handle = ReadUInt64(memory, OutHandleAddress);

        for (uint index = 0x40; index <= 0x63; index++)
        {
            var coefficientsAndCanary = new byte[0x10];
            Array.Fill(coefficientsAndCanary, (byte)0xA5);
            BinaryPrimitives.WriteUInt64LittleEndian(coefficientsAndCanary.AsSpan(8), 0xC0DE_C0DE_CAFE_BA00UL);
            Assert.True(memory.TryWrite(CoefficientsAddress, coefficientsAndCanary));

            var get = new CpuContext(memory, Generation.Gen5);
            get[CpuRegister.Rdi] = handle;
            get[CpuRegister.Rsi] = index;
            get[CpuRegister.Rdx] = CoefficientsAddress;
            get[CpuRegister.Rcx] = 2;
            Assert.Equal(0, AudioOut2Exports.AudioOut2GetSpeakerArrayAmbisonicsCoefficients(get));

            Assert.True(memory.TryRead(CoefficientsAddress, coefficientsAndCanary));
            Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(coefficientsAndCanary));
            Assert.Equal(
                0xC0DE_C0DE_CAFE_BA00UL,
                BinaryPrimitives.ReadUInt64LittleEndian(coefficientsAndCanary.AsSpan(8)));
        }

        var destroy = new CpuContext(memory, Generation.Gen5);
        destroy[CpuRegister.Rdi] = handle;
        Assert.Equal(0, AudioOut2Exports.AudioOut2SpeakerArrayDestroy(destroy));
        Assert.Equal(0, AudioOut2Exports.SpeakerArrayCountForTests);

        Assert.Equal(AudioOut2InvalidArgument, AudioOut2Exports.AudioOut2SpeakerArrayDestroy(destroy));

        var afterDestroy = new CpuContext(memory, Generation.Gen5);
        afterDestroy[CpuRegister.Rdi] = handle;
        afterDestroy[CpuRegister.Rsi] = 0x40;
        afterDestroy[CpuRegister.Rdx] = CoefficientsAddress;
        afterDestroy[CpuRegister.Rcx] = 2;
        Assert.Equal(
            AudioOut2InvalidArgument,
            AudioOut2Exports.AudioOut2GetSpeakerArrayAmbisonicsCoefficients(afterDestroy));
    }

    [Theory]
    [InlineData(33U, GtaWorkspaceSize, 0, 0U)]
    [InlineData(2U, GtaWorkspaceSize - 1, 0, 0U)]
    [InlineData(2U, GtaWorkspaceSize, 1, 0xBF800000U)]
    [InlineData(2U, GtaWorkspaceSize, 1, 0x7FC00000U)]
    public void SpeakerArrayCreate_RejectsInvalidProviderDescriptorWithoutWritingOutput(
        uint speakerCount,
        ulong workspaceSize,
        int mode,
        uint modeParameterBits)
    {
        var memory = CreateSpeakerArrayMemory();
        WriteSpeakerArrayDescriptor(memory, speakerCount, workspaceSize, mode, modeParameterBits);
        var ctx = CreateSpeakerArrayContext(memory);

        Assert.Equal(AudioOut2InvalidArgument, AudioOut2Exports.AudioOut2SpeakerArrayCreate(ctx));
        Assert.Equal(0xA5A5_A5A5_A5A5_A5A5UL, ReadUInt64(memory, OutHandleAddress));
        Assert.Equal(0, AudioOut2Exports.SpeakerArrayCountForTests);
    }

    [Fact]
    public void SpeakerArrayCreate_FaultingOutputDoesNotRetainLifecycleState()
    {
        var memory = CreateSpeakerArrayMemory();
        WriteSpeakerArrayDescriptor(memory);
        var ctx = CreateSpeakerArrayContext(memory);
        ctx[CpuRegister.Rdi] = MemoryBase - 8;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AudioOut2Exports.AudioOut2SpeakerArrayCreate(ctx));
        Assert.Equal(0, AudioOut2Exports.SpeakerArrayCountForTests);
    }

    [Fact]
    public void ContextResetParam_MatchesFirmware1270DefaultBlock()
    {
        const ulong paramAddress = 0x1020;
        var memory = new global::SharpEmu.Libs.Tests.FakeCpuMemory(0x1000, 0x200);
        Span<byte> dirty = stackalloc byte[0x40];
        dirty.Fill(0xCC);
        Assert.True(memory.TryWrite(paramAddress, dirty));

        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = paramAddress;

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextResetParam(ctx));

        Span<byte> actual = stackalloc byte[0x40];
        Assert.True(memory.TryRead(paramAddress, actual));
        Span<byte> expected = stackalloc byte[0x40];
        expected.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(expected[0x00..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(expected[0x0C..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(expected[0x10..], 0x100);
        Assert.Equal(expected.ToArray(), actual.ToArray());
    }

    [Fact]
    public void ContextQueryMemory_WritesSingleQwordWithoutClobberingGtaCanary()
    {
        const ulong paramAddress = 0x1020;
        const ulong sizeAddress = 0x1100;
        const ulong canary = 0xC0DEC0DECAFEBA00;
        var memory = new global::SharpEmu.Libs.Tests.FakeCpuMemory(0x1000, 0x200);

        Span<byte> param = stackalloc byte[0x40];
        param.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x00..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x10..], 0x100);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x14..], 1);
        Assert.True(memory.TryWrite(paramAddress, param));

        Span<byte> outputAndCanary = stackalloc byte[0x10];
        outputAndCanary.Fill(0xA5);
        BinaryPrimitives.WriteUInt64LittleEndian(outputAndCanary[0x08..], canary);
        Assert.True(memory.TryWrite(sizeAddress, outputAndCanary));

        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = paramAddress;
        ctx[CpuRegister.Rsi] = sizeAddress;

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextQueryMemory(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

        Assert.True(memory.TryRead(sizeAddress, outputAndCanary));
        Assert.Equal(0xFA6CUL, BinaryPrimitives.ReadUInt64LittleEndian(outputAndCanary));
        Assert.Equal(canary, BinaryPrimitives.ReadUInt64LittleEndian(outputAndCanary[0x08..]));
    }

    [Fact]
    public void ContextGetQueueLevel_WritesTwoDwordsWithoutClobberingGtaCanary()
    {
        const ulong firstOutputAddress = 0x1100;
        const ulong secondOutputAddress = 0x1140;
        const ulong canary = 0xC0DEC0DECAFEBA00;
        var memory = new global::SharpEmu.Libs.Tests.FakeCpuMemory(0x1000, 0x200);

        Span<byte> firstOutputAndCanary = stackalloc byte[0x0C];
        firstOutputAndCanary.Fill(0xA5);
        BinaryPrimitives.WriteUInt64LittleEndian(firstOutputAndCanary[0x04..], canary);
        Assert.True(memory.TryWrite(firstOutputAddress, firstOutputAndCanary));

        Span<byte> secondOutput = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(secondOutput, 0xA5A5A5A5);
        Assert.True(memory.TryWrite(secondOutputAddress, secondOutput));

        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 3;
        ctx[CpuRegister.Rsi] = firstOutputAddress;
        ctx[CpuRegister.Rdx] = secondOutputAddress;

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

        Assert.True(memory.TryRead(firstOutputAddress, firstOutputAndCanary));
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(firstOutputAndCanary));
        Assert.Equal(canary, BinaryPrimitives.ReadUInt64LittleEndian(firstOutputAndCanary[0x04..]));
        Assert.True(memory.TryRead(secondOutputAddress, secondOutput));
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(secondOutput));
    }

    private static global::SharpEmu.Libs.Tests.FakeCpuMemory CreateSpeakerArrayMemory()
    {
        var memory = new global::SharpEmu.Libs.Tests.FakeCpuMemory(MemoryBase, 0xE000);

        Span<byte> outputAndCanary = stackalloc byte[0x10];
        outputAndCanary.Fill(0xA5);
        BinaryPrimitives.WriteUInt64LittleEndian(outputAndCanary[8..], 0xC0DE_C0DE_CAFE_BA00UL);
        Assert.True(memory.TryWrite(OutHandleAddress, outputAndCanary));

        Span<byte> auxiliary = stackalloc byte[8];
        auxiliary.Clear();
        Assert.True(memory.TryWrite(AuxiliaryAddress, auxiliary));

        Span<byte> positions = stackalloc byte[2 * 3 * sizeof(float)];
        positions.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(positions[0x00..], BitConverter.SingleToInt32Bits(-1.0f));
        BinaryPrimitives.WriteInt32LittleEndian(positions[0x0C..], BitConverter.SingleToInt32Bits(1.0f));
        Assert.True(memory.TryWrite(PositionsAddress, positions));
        return memory;
    }

    private static void WriteSpeakerArrayDescriptor(
        global::SharpEmu.Libs.Tests.FakeCpuMemory memory,
        uint speakerCount = 2,
        ulong workspaceSize = GtaWorkspaceSize,
        int mode = 0,
        uint modeParameterBits = 0)
    {
        Span<byte> descriptor = stackalloc byte[0x28];
        descriptor.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[0x00..], PositionsAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[0x08..], speakerCount);
        descriptor[0x0C] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[0x10..], WorkspaceAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[0x18..], workspaceSize);
        BinaryPrimitives.WriteInt32LittleEndian(descriptor[0x20..], mode);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[0x24..], modeParameterBits);
        Assert.True(memory.TryWrite(DescriptorAddress, descriptor));
    }

    private static CpuContext CreateSpeakerArrayContext(ICpuMemory memory)
    {
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = OutHandleAddress;
        ctx[CpuRegister.Rsi] = DescriptorAddress;
        ctx[CpuRegister.Rdx] = AuxiliaryAddress;
        return ctx;
    }

    private static ulong ReadUInt64(ICpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private sealed class NullMemory : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }
}
