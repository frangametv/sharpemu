// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

[CollectionDefinition("AjmState", DisableParallelization = true)]
public sealed class AjmStateCollection
{
    public const string Name = "AjmState";
}

[Collection(AjmStateCollection.Name)]
public sealed class AjmExportsTests : IDisposable
{
    private const int InvalidContext = unchecked((int)0x80930002);
    private const int InvalidInstance = unchecked((int)0x80930003);
    private const int InvalidParameter = unchecked((int)0x80930005);
    private const int CodecAlreadyRegistered = unchecked((int)0x80930009);
    private const int CodecNotRegistered = unchecked((int)0x8093000A);
    private const int MalformedBatch = unchecked((int)0x80930011);
    private const int JobCreationError = unchecked((int)0x80930012);
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ContextAddress = MemoryBase + 0x100;
    private const ulong InstanceAddress = MemoryBase + 0x200;
    private const ulong BatchInfoAddress = MemoryBase + 0x300;
    private const ulong StatisticsAddress = MemoryBase + 0x400;
    private const ulong BatchBufferAddress = MemoryBase + 0x500;
    private const ulong BatchAddress = MemoryBase + 0x300;
    private const ulong DescriptorBase = MemoryBase + 0x500;
    private const ulong StackAddress = MemoryBase + 0xE00;
    private const ulong BatchErrorAddress = MemoryBase + 0xE40;
    private const ulong BatchIdAddress = MemoryBase + 0xE80;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _ctx;

    public AjmExportsTests()
    {
        AjmExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void InstanceLifecycle_RegisteredCodecCreatesAndDestroysInstance()
    {
        var contextId = Initialize();

        Assert.Equal(0, RegisterCodec(contextId, 1));
        Assert.Equal(0, CreateInstance(contextId, 1, 0x401, InstanceAddress));
        Assert.Equal(0x4001u, ReadUInt32(InstanceAddress));

        Assert.Equal(0, DestroyInstance(contextId, 0x4001));
        Assert.Equal(InvalidInstance, DestroyInstance(contextId, 0x4001));
    }

    [Fact]
    public void InstanceCreate_UnregisteredCodecDoesNotWriteOutput()
    {
        var contextId = Initialize();
        WriteUInt32(InstanceAddress, 0xCCCCCCCC);

        Assert.Equal(CodecNotRegistered, CreateInstance(contextId, 1, 0x401, InstanceAddress));
        Assert.Equal(0xCCCCCCCCu, ReadUInt32(InstanceAddress));
    }

    [Fact]
    public void InstanceCreate_FaultingOutputDoesNotAdvanceInstanceId()
    {
        var contextId = Initialize();
        Assert.Equal(0, RegisterCodec(contextId, 1));

        Assert.Equal(InvalidParameter, CreateInstance(contextId, 1, 0x401, MemoryBase + 0x1000));
        Assert.Equal(0, CreateInstance(contextId, 1, 0x401, InstanceAddress));
        Assert.Equal(0x4001u, ReadUInt32(InstanceAddress));
        Assert.Equal(0, DestroyInstance(contextId, 0x4001));
    }

    [Fact]
    public void ModuleRegister_RejectsDuplicateAndUnknownContext()
    {
        var contextId = Initialize();

        Assert.Equal(0, RegisterCodec(contextId, 1));
        Assert.Equal(CodecAlreadyRegistered, RegisterCodec(contextId, 1));
        Assert.Equal(InvalidContext, RegisterCodec(contextId + 1, 1));
    }

    [Fact]
    public void MemoryRegistration_TracksValidContextAndToleratesRepeatedUnregister()
    {
        var contextId = Initialize();
        const ulong address = 0x4_D4E0_0000;

        Assert.Equal(0, RegisterMemory(contextId, address, 4));
        Assert.Equal(0, UnregisterMemory(contextId, address));
        Assert.Equal(0, UnregisterMemory(contextId, address));
        Assert.Equal(InvalidContext, RegisterMemory(contextId + 1, address, 4));
        Assert.Equal(InvalidContext, UnregisterMemory(contextId + 1, address));
        Assert.Equal(InvalidParameter, RegisterMemory(contextId, 0, 4));
        Assert.Equal(InvalidParameter, RegisterMemory(contextId, address, 0));
        Assert.Equal(InvalidParameter, UnregisterMemory(contextId, 0));
    }

    [Fact]
    public void BatchInitializeAndStatistics_WriteExpectedAbiStructures()
    {
        Span<byte> sentinel = stackalloc byte[48];
        sentinel.Fill(0xCC);
        Assert.True(_memory.TryWrite(StatisticsAddress, sentinel));

        _ctx[CpuRegister.Rdi] = BatchBufferAddress;
        _ctx[CpuRegister.Rsi] = 0x200;
        _ctx[CpuRegister.Rdx] = BatchInfoAddress;
        Assert.Equal(0, AjmExports.AjmBatchInitialize(_ctx));

        Assert.Equal(BatchBufferAddress, ReadUInt64(BatchInfoAddress));
        Assert.Equal(0ul, ReadUInt64(BatchInfoAddress + 8));
        Assert.Equal(0x200ul, ReadUInt64(BatchInfoAddress + 16));
        Assert.Equal(0ul, ReadUInt64(BatchInfoAddress + 24));
        Assert.Equal(0ul, ReadUInt64(BatchInfoAddress + 32));

        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = StatisticsAddress;
        _ctx.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(0.25f), 0);
        Assert.Equal(0, AjmExports.AjmBatchJobGetStatistics(_ctx));

        Assert.Equal(88ul, ReadUInt64(BatchInfoAddress + 8));
        Assert.Equal(BatchBufferAddress, ReadUInt64(BatchInfoAddress + 24));
        Assert.Equal(0ul, ReadUInt64(BatchInfoAddress + 32));
        Assert.All(ReadBytes(StatisticsAddress, 48), value => Assert.Equal(0, value));
        Assert.All(ReadBytes(BatchBufferAddress, 88), value => Assert.Equal(0, value));
    }

    /// <summary>
    /// Demon's Souls creates ATRAC9 voices with low flag words 1, 2, 4 and 8 —
    /// the channel counts of the streams they carry. Rejecting any of them left
    /// the title holding SCE_AJM_INSTANCE_INVALID for that voice, so its 4- and
    /// 8-channel movie stems played silence.
    /// </summary>
    [Theory]
    [InlineData(0x1_0000_0001ul)]
    [InlineData(0x1_0000_0002ul)]
    [InlineData(0x1_0000_0004ul)]
    [InlineData(0x1_0000_0008ul)]
    public void InstanceCreate_AcceptsEveryChannelCountFlagWord(ulong flags)
    {
        var contextId = Initialize();
        Assert.Equal(0, RegisterCodec(contextId, 1));

        Assert.Equal(0, CreateInstance(contextId, 1, flags, InstanceAddress));
        Assert.Equal(0x4001u, ReadUInt32(InstanceAddress));
    }

    /// <summary>
    /// sceAjmBatchJobRunSplit gathers arrays of AjmBuffer descriptors rather than
    /// a single pointer/size pair, and its sideband layout is positional: result,
    /// then only the blocks the job flags asked for.
    /// </summary>
    [Fact]
    public void BatchJobRunSplit_GathersDescriptorsAndWritesFlaggedSideband()
    {
        const ulong inputDescriptors = MemoryBase + 0x600;
        const ulong outputDescriptors = MemoryBase + 0x640;
        const ulong inputData = MemoryBase + 0x700;
        const ulong outputData = MemoryBase + 0x800;
        const ulong sideband = MemoryBase + 0x900;
        const ulong streamSideband = 1ul << 47;
        const ulong multipleFrames = 1ul << 12;

        var contextId = Initialize();
        Assert.Equal(0, RegisterCodec(contextId, 2));
        Assert.Equal(0, CreateInstance(contextId, 2, 0x2_0000_0002, InstanceAddress));
        var instanceId = ReadUInt32(InstanceAddress);

        InitializeBatch(BatchBufferAddress, 0x200, BatchInfoAddress);

        // Two input descriptors totalling 0x30 bytes, one output of 0x40.
        WriteUInt64(inputDescriptors, inputData);
        WriteUInt64(inputDescriptors + 8, 0x20);
        WriteUInt64(inputDescriptors + 16, inputData + 0x20);
        WriteUInt64(inputDescriptors + 24, 0x10);
        WriteUInt64(outputDescriptors, outputData);
        WriteUInt64(outputDescriptors + 8, 0x40);

        Span<byte> dirty = stackalloc byte[0x40];
        dirty.Fill(0xAB);
        Assert.True(_memory.TryWrite(outputData, dirty));

        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = streamSideband | multipleFrames;
        _ctx[CpuRegister.Rcx] = inputDescriptors;
        _ctx[CpuRegister.R8] = 2;
        _ctx[CpuRegister.R9] = outputDescriptors;
        WriteStackArgs(outputCount: 1, sidebandAddress: sideband, sidebandSize: 0x20);

        Assert.Equal(0, AjmExports.AjmBatchJobRunSplit(_ctx));

        // A non-ATRAC9 instance stays a silence stub: the output is cleared and
        // the whole gathered input is reported consumed.
        Assert.All(ReadBytes(outputData, 0x40), value => Assert.Equal(0, value));

        var result = ReadBytes(sideband, 0x20);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(result));            // AjmSidebandResult
        Assert.Equal(0x30, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(8)));  // stream.input_consumed
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(12)));    // stream.output_written
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(24)));  // mframe.num_frames

        // The batch cursor advanced past the job plus its descriptors.
        Assert.True(ReadUInt64(BatchInfoAddress + 8) > 0);
    }

    [Fact]
    public void BatchJobRunSplit_OmitsSidebandBlocksTheJobFlagsDidNotRequest()
    {
        const ulong inputDescriptors = MemoryBase + 0x600;
        const ulong outputDescriptors = MemoryBase + 0x640;
        const ulong inputData = MemoryBase + 0x700;
        const ulong outputData = MemoryBase + 0x800;
        const ulong sideband = MemoryBase + 0x900;

        var contextId = Initialize();
        Assert.Equal(0, RegisterCodec(contextId, 2));
        Assert.Equal(0, CreateInstance(contextId, 2, 0x2_0000_0002, InstanceAddress));
        var instanceId = ReadUInt32(InstanceAddress);

        InitializeBatch(BatchBufferAddress, 0x200, BatchInfoAddress);
        WriteUInt64(inputDescriptors, inputData);
        WriteUInt64(inputDescriptors + 8, 0x20);
        WriteUInt64(outputDescriptors, outputData);
        WriteUInt64(outputDescriptors + 8, 0x40);

        Span<byte> sentinel = stackalloc byte[0x20];
        sentinel.Fill(0x5A);
        Assert.True(_memory.TryWrite(sideband, sentinel));

        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = 0; // No stream sideband, no multiple-frames.
        _ctx[CpuRegister.Rcx] = inputDescriptors;
        _ctx[CpuRegister.R8] = 1;
        _ctx[CpuRegister.R9] = outputDescriptors;
        WriteStackArgs(outputCount: 1, sidebandAddress: sideband, sidebandSize: 0x20);

        Assert.Equal(0, AjmExports.AjmBatchJobRunSplit(_ctx));

        // Only the 8-byte result block is written; the rest keeps its sentinel.
        var written = ReadBytes(sideband, 0x20);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(written));
        Assert.All(written.AsSpan(8).ToArray(), value => Assert.Equal(0x5A, value));
    }

    [Theory]
    [InlineData(23u)]
    [InlineData(24u)]
    public void Gen5CodecTypesCanRegisterAndCreateInstances(uint codecType)
    {
        var contextId = Initialize();

        Assert.Equal(0, RegisterCodec(contextId, codecType));
        Assert.Equal(
            0,
            CreateInstance(contextId, codecType, 0x401, InstanceAddress));
    }

    [Fact]
    public void InstanceDestroy_RejectsUnknownContextAndSlot()
    {
        var contextId = Initialize();

        Assert.Equal(InvalidContext, DestroyInstance(contextId + 1, 1));
        Assert.Equal(InvalidInstance, DestroyInstance(contextId, 0));
        Assert.Equal(InvalidInstance, DestroyInstance(contextId, 1));
    }

    [Fact]
    public void InstanceDestroy_ResolvesInstanceByMaskedSlot()
    {
        var contextId = Initialize();
        Assert.Equal(0, RegisterCodec(contextId, 1));
        Assert.Equal(0, CreateInstance(contextId, 1, 0x401, InstanceAddress));

        Assert.Equal(0, DestroyInstance(contextId, 0x8001));
        Assert.Equal(InvalidInstance, DestroyInstance(contextId, 0x4001));
    }

    [Fact]
    public void ConcurrentInstanceCreates_ProduceUniqueLiveIds()
    {
        const int count = 32;
        var contextId = Initialize();
        Assert.Equal(0, RegisterCodec(contextId, 1));

        var results = Enumerable.Range(0, count)
            .AsParallel()
            .Select(index =>
            {
                var outputAddress = MemoryBase + 0x300 + unchecked((ulong)(index * sizeof(uint)));
                var context = new CpuContext(_memory, Generation.Gen5)
                {
                    [CpuRegister.Rdi] = contextId,
                    [CpuRegister.Rsi] = 1,
                    [CpuRegister.Rdx] = 0x401,
                    [CpuRegister.Rcx] = outputAddress,
                };
                var result = AjmExports.AjmInstanceCreate(context);
                return (result, instanceId: ReadUInt32(outputAddress));
            })
            .ToArray();

        Assert.All(results, result => Assert.Equal(0, result.result));
        Assert.Equal(count, results.Select(result => result.instanceId).Distinct().Count());
        Assert.All(results, result => Assert.Equal(0, DestroyInstance(contextId, result.instanceId)));
    }

    [Fact]
    public void BatchJobDecode_AppendsExactNativeDescriptor()
    {
        const ulong cursor = 0x20;
        const uint instanceId = 0xABCDE;
        const uint inputSize = 0x3C0;
        const uint outputSize = 0x780;
        var descriptorAddress = DescriptorBase + cursor;
        var inputAddress = MemoryBase + 0x800;
        var outputAddress = MemoryBase + 0xA00;
        var sidebandAddress = MemoryBase + 0xC00;

        WriteUInt64(BatchAddress, DescriptorBase);
        WriteUInt64(BatchAddress + 0x08, cursor);
        WriteUInt64(BatchAddress + 0x10, 0x100);
        WriteUInt64(BatchAddress + 0x18, 0xAAAAAAAAAAAAAAAA);
        WriteUInt64(BatchAddress + 0x20, 0xBBBBBBBBBBBBBBBB);

        Span<byte> originalDescriptor = stackalloc byte[0x40];
        originalDescriptor.Fill(0xCC);
        Assert.True(_memory.TryWrite(descriptorAddress, originalDescriptor));

        _ctx[CpuRegister.Rdi] = BatchAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = inputAddress;
        _ctx[CpuRegister.Rcx] = inputSize;
        _ctx[CpuRegister.R8] = outputAddress;
        _ctx[CpuRegister.R9] = outputSize;
        _ctx[CpuRegister.Rsp] = StackAddress;
        WriteUInt64(StackAddress, 0x8000000012345678);
        WriteUInt64(StackAddress + sizeof(ulong), sidebandAddress);

        Assert.Equal(0, AjmExports.AjmBatchJobDecode(_ctx));
        Assert.Equal(cursor + 0x40, ReadUInt64(BatchAddress + 0x08));
        Assert.Equal(descriptorAddress, ReadUInt64(BatchAddress + 0x18));
        Assert.Equal(0UL, ReadUInt64(BatchAddress + 0x20));

        Assert.Equal((0xCCCCCCCCu & 0xFC000030u) | (instanceId << 6), ReadUInt32(descriptorAddress));
        Assert.Equal(0x38u, ReadUInt32(descriptorAddress + 0x04));
        Assert.Equal((0xCCCCCCCCu & 0xFFFFFFE0u) | 0x01u, ReadUInt32(descriptorAddress + 0x08));
        Assert.Equal(inputSize, ReadUInt32(descriptorAddress + 0x0C));
        Assert.Equal(inputAddress, ReadUInt64(descriptorAddress + 0x10));
        Assert.Equal((0xCCCCCCCCu & 0xFC000030u) | 0x00200004u, ReadUInt32(descriptorAddress + 0x18));
        Assert.Equal(0x1000u, ReadUInt32(descriptorAddress + 0x1C));
        Assert.Equal((0xCCCCCCCCu & 0xFFFFFFE0u) | 0x11u, ReadUInt32(descriptorAddress + 0x20));
        Assert.Equal(outputSize, ReadUInt32(descriptorAddress + 0x24));
        Assert.Equal(outputAddress, ReadUInt64(descriptorAddress + 0x28));
        Assert.Equal((0xCCCCCCCCu & 0xFFFFFFE0u) | 0x12u, ReadUInt32(descriptorAddress + 0x30));
        Assert.Equal(0x20u, ReadUInt32(descriptorAddress + 0x34));
        Assert.Equal(sidebandAddress, ReadUInt64(descriptorAddress + 0x38));
    }

    [Fact]
    public void BatchInitialize_WritesAndResetsExactNativeBuilder()
    {
        Fill(BatchAddress, 0x28, 0xCC);
        _ctx[CpuRegister.Rdi] = DescriptorBase;
        _ctx[CpuRegister.Rsi] = 0x100;
        _ctx[CpuRegister.Rdx] = BatchAddress;

        Assert.Equal(0, AjmExports.AjmBatchInitialize(_ctx));
        Assert.Equal(DescriptorBase, ReadUInt64(BatchAddress));
        Assert.Equal(0UL, ReadUInt64(BatchAddress + 0x08));
        Assert.Equal(0x100UL, ReadUInt64(BatchAddress + 0x10));
        Assert.Equal(0UL, ReadUInt64(BatchAddress + 0x18));
        Assert.Equal(0UL, ReadUInt64(BatchAddress + 0x20));

        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == "MmpF1XsQiHw");
        Assert.True(export.PreferLle);
    }

    [Fact]
    public void BatchInitialize_AllowsZeroCapacityAndRejectsNullPointersWithoutWrites()
    {
        Fill(BatchAddress, 0x28, 0xCC);
        _ctx[CpuRegister.Rdi] = DescriptorBase;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = BatchAddress;
        Assert.Equal(0, AjmExports.AjmBatchInitialize(_ctx));
        Assert.Equal(DescriptorBase, ReadUInt64(BatchAddress));
        Assert.Equal(0UL, ReadUInt64(BatchAddress + 0x10));

        Fill(BatchAddress, 0x28, 0xCC);
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rdx] = BatchAddress;
        Assert.Equal(InvalidParameter, AjmExports.AjmBatchInitialize(_ctx));
        Assert.All(ReadBytes(BatchAddress, 0x28), value => Assert.Equal(0xCC, value));

        _ctx[CpuRegister.Rdi] = DescriptorBase;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(InvalidParameter, AjmExports.AjmBatchInitialize(_ctx));
    }

    [Fact]
    public void BatchJobDecode_CapacityFailureAdvancesCursorWithoutRollback()
    {
        WriteUInt64(BatchAddress, DescriptorBase);
        WriteUInt64(BatchAddress + 0x08, 0xE0);
        WriteUInt64(BatchAddress + 0x10, 0x100);
        WriteUInt64(BatchAddress + 0x18, 0xAAAAAAAAAAAAAAAA);
        WriteUInt64(BatchAddress + 0x20, 0xBBBBBBBBBBBBBBBB);
        _ctx[CpuRegister.Rdi] = BatchAddress;

        Assert.Equal(unchecked((int)0x80930012), AjmExports.AjmBatchJobDecode(_ctx));
        Assert.Equal(0x120UL, ReadUInt64(BatchAddress + 0x08));
        Assert.Equal(0xAAAAAAAAAAAAAAAAUL, ReadUInt64(BatchAddress + 0x18));
        Assert.Equal(0xBBBBBBBBBBBBBBBBUL, ReadUInt64(BatchAddress + 0x20));
    }

    [Fact]
    public void BatchJobDecode_NullBuilderReturnsInvalidParameter()
    {
        _ctx[CpuRegister.Rdi] = 0;

        Assert.Equal(InvalidParameter, AjmExports.AjmBatchJobDecode(_ctx));
    }

    [Fact]
    public void BatchStart_OverCapacityReportsExactNativeCreationError()
    {
        WriteUInt64(BatchAddress, DescriptorBase);
        WriteUInt64(BatchAddress + 0x08, 0x101);
        WriteUInt64(BatchAddress + 0x10, 0x100);
        WriteUInt64(BatchAddress + 0x18, 0xAAAAAAAAAAAAAAAA);
        WriteUInt64(BatchAddress + 0x20, 0xBBBBBBBBBBBBBBBB);
        Fill(BatchErrorAddress, 0x20, 0xCC);
        WriteUInt32(BatchIdAddress, 0xDEADBEEF);

        _ctx[CpuRegister.Rdi] = uint.MaxValue;
        _ctx[CpuRegister.Rsi] = BatchAddress;
        _ctx[CpuRegister.Rdx] = 7;
        _ctx[CpuRegister.Rcx] = BatchErrorAddress;
        _ctx[CpuRegister.R8] = BatchIdAddress;

        Assert.Equal(MalformedBatch, AjmExports.AjmBatchStart(_ctx));
        Assert.Equal(unchecked((uint)JobCreationError), ReadUInt32(BatchErrorAddress));
        Assert.Equal(0xCCCCCCCCu, ReadUInt32(BatchErrorAddress + 0x04));
        Assert.Equal(0xAAAAAAAAAAAAAAAAUL, ReadUInt64(BatchErrorAddress + 0x08));
        Assert.Equal(0u, ReadUInt32(BatchErrorAddress + 0x10));
        Assert.Equal(0xCCCCCCCCu, ReadUInt32(BatchErrorAddress + 0x14));
        Assert.Equal(0xBBBBBBBBBBBBBBBBUL, ReadUInt64(BatchErrorAddress + 0x18));
        Assert.Equal(0xDEADBEEFu, ReadUInt32(BatchIdAddress));
    }

    [Fact]
    public void BatchStartAndWait_CompleteDecodeOnceAndEmitSilence()
    {
        const uint inputSize = 0x40;
        const uint outputSize = 0x80;
        const uint sidebandSize = 0x20;
        var inputAddress = MemoryBase + 0x800;
        var outputAddress = MemoryBase + 0x900;
        var sidebandAddress = MemoryBase + 0xA00;
        var contextId = Initialize();

        _ctx[CpuRegister.Rdi] = DescriptorBase;
        _ctx[CpuRegister.Rsi] = 0x100;
        _ctx[CpuRegister.Rdx] = BatchAddress;
        Assert.Equal(0, AjmExports.AjmBatchInitialize(_ctx));
        Fill(DescriptorBase, 0x40, 0);
        Fill(outputAddress, unchecked((int)outputSize), 0xCC);
        Fill(sidebandAddress, unchecked((int)sidebandSize), 0xCC);

        _ctx[CpuRegister.Rdi] = BatchAddress;
        _ctx[CpuRegister.Rsi] = 1;
        _ctx[CpuRegister.Rdx] = inputAddress;
        _ctx[CpuRegister.Rcx] = inputSize;
        _ctx[CpuRegister.R8] = outputAddress;
        _ctx[CpuRegister.R9] = outputSize;
        _ctx[CpuRegister.Rsp] = StackAddress;
        WriteUInt64(StackAddress + sizeof(ulong), sidebandAddress);
        Assert.Equal(0, AjmExports.AjmBatchJobDecode(_ctx));
        Assert.Equal(sidebandSize, ReadUInt32(DescriptorBase + 0x34));

        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = BatchAddress;
        _ctx[CpuRegister.Rdx] = 3;
        _ctx[CpuRegister.Rcx] = BatchErrorAddress;
        _ctx[CpuRegister.R8] = BatchIdAddress;
        Assert.Equal(0, AjmExports.AjmBatchStart(_ctx));

        var batchId = ReadUInt32(BatchIdAddress);
        Assert.NotEqual(0u, batchId);
        Assert.All(ReadBytes(outputAddress, unchecked((int)outputSize)), value => Assert.Equal(0, value));
        Assert.All(ReadBytes(sidebandAddress, unchecked((int)sidebandSize)), value => Assert.Equal(0, value));

        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = batchId;
        _ctx[CpuRegister.Rdx] = 1000;
        _ctx[CpuRegister.Rcx] = BatchErrorAddress;
        Assert.Equal(0, AjmExports.AjmBatchWait(_ctx));
        Assert.Equal(InvalidParameter, AjmExports.AjmBatchWait(_ctx));
    }

    [Fact]
    public void InstanceLifecycleExports_RegisterForBothGenerations()
    {
        foreach (var generation in new[] { Generation.Gen4, Generation.Gen5 })
        {
            var manager = new ModuleManager();
            manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));

            Assert.True(manager.TryGetExport("AxoDrINp4J8", out var create));
            Assert.Equal("sceAjmInstanceCreate", create.Name);
            Assert.True(manager.TryGetExport("RbLbuKv8zho", out var destroy));
            Assert.Equal("sceAjmInstanceDestroy", destroy.Name);
            Assert.True(manager.TryGetExport("bkRHEYG6lEM", out var memoryRegister));
            Assert.Equal("sceAjmMemoryRegister", memoryRegister.Name);
            Assert.True(manager.TryGetExport("pIpGiaYkHkM", out var memoryUnregister));
            Assert.Equal("sceAjmMemoryUnregister", memoryUnregister.Name);
            Assert.True(manager.TryGetExport("3cAg7xN995U", out var statistics));
            Assert.Equal("sceAjmBatchJobGetStatistics", statistics.Name);
        }
    }

    public void Dispose()
    {
        AjmExports.ResetForTests();
    }

    private uint Initialize()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = ContextAddress;
        Assert.Equal(0, AjmExports.AjmInitialize(_ctx));
        return ReadUInt32(ContextAddress);
    }

    private int RegisterCodec(uint contextId, uint codecType)
    {
        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = codecType;
        _ctx[CpuRegister.Rdx] = 0;
        return AjmExports.AjmModuleRegister(_ctx);
    }

    private int RegisterMemory(uint contextId, ulong address, ulong pages)
    {
        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = address;
        _ctx[CpuRegister.Rdx] = pages;
        return AjmExports.AjmMemoryRegister(_ctx);
    }

    private int UnregisterMemory(uint contextId, ulong address)
    {
        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = address;
        return AjmExports.AjmMemoryUnregister(_ctx);
    }

    private int CreateInstance(uint contextId, uint codecType, ulong flags, ulong outputAddress)
    {
        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = codecType;
        _ctx[CpuRegister.Rdx] = flags;
        _ctx[CpuRegister.Rcx] = outputAddress;
        return AjmExports.AjmInstanceCreate(_ctx);
    }

    private int DestroyInstance(uint contextId, uint instanceId)
    {
        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = instanceId;
        return AjmExports.AjmInstanceDestroy(_ctx);
    }

    private void InitializeBatch(ulong bufferAddress, ulong bufferSize, ulong infoAddress)
    {
        _ctx[CpuRegister.Rdi] = bufferAddress;
        _ctx[CpuRegister.Rsi] = bufferSize;
        _ctx[CpuRegister.Rdx] = infoAddress;
        Assert.Equal(0, AjmExports.AjmBatchInitialize(_ctx));
    }

    private void WriteStackArgs(ulong outputCount, ulong sidebandAddress, ulong sidebandSize)
    {
        _ctx[CpuRegister.Rsp] = StackAddress;
        WriteUInt64(StackAddress + 8, outputCount);
        WriteUInt64(StackAddress + 16, sidebandAddress);
        WriteUInt64(StackAddress + 24, sidebandSize);
    }

    private uint ReadUInt32(ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    private ulong ReadUInt64(ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }

    private byte[] ReadBytes(ulong address, int size)
    {
        var bytes = new byte[size];
        Assert.True(_memory.TryRead(address, bytes));
        return bytes;
    }

    private void Fill(ulong address, int size, byte value)
    {
        var bytes = new byte[size];
        Array.Fill(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private void WriteUInt32(ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private void WriteUInt64(ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }
}
