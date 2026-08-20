// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Gen5ShaderScalarEvaluator.FallbackMemoryReader is a process-global static. This
// test swaps it, but the SharpEmu.Libs [ModuleInitializer] (AgcShaderCompilerHooks)
// reassigns the same static the first time any Libs type is touched. Under xUnit's
// default cross-class parallelism a Libs test running concurrently can fire that
// initializer mid-test and clobber the swapped-in reader (observed as all-zero
// reads on CI). A DisableParallelization collection runs alone in the non-parallel
// phase, so nothing else can mutate the static while this test holds it.
[CollectionDefinition(Gen5ScalarEvaluatorStateCollection.Name, DisableParallelization = true)]
public sealed class Gen5ScalarEvaluatorStateCollection
{
    public const string Name = "Gen5ScalarEvaluatorState";
}

[Collection(Gen5ScalarEvaluatorStateCollection.Name)]
public sealed class Gen5ScalarMemoryFallbackTests
{
    private const ulong ScalarTableAddress = 0x4_4665_4FD0;
    private static readonly object FallbackReaderGate = new();

    [Fact]
    public void ScalarLoadReadsTrackedFallbackMemory()
    {
        var expected = new uint[]
        {
            0x4665_4F70,
            0x0000_0004,
            0x4EA7_FCE0,
            0x0000_0004,
        };
        var table = new byte[expected.Length * sizeof(uint)];
        for (var index = 0; index < expected.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                table.AsSpan(index * sizeof(uint), sizeof(uint)),
                expected[index]);
        }

        var load = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Smem,
            "SLoadDwordx4",
            [],
            [Gen5Operand.Scalar(0)],
            [
                Gen5Operand.Scalar(16),
                Gen5Operand.Scalar(17),
                Gen5Operand.Scalar(18),
                Gen5Operand.Scalar(19),
            ],
            new Gen5ScalarMemoryControl(4, 0, null));
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0, [load, end]),
            [unchecked((uint)ScalarTableAddress), (uint)(ScalarTableAddress >> 32)],
            null);
        var ctx = new CpuContext(new FakeCpuMemory(0x1000, 0x100), Generation.Gen5);

        lock (FallbackReaderGate)
        {
            var previousReader = Gen5ShaderScalarEvaluator.FallbackMemoryReader;
            try
            {
                Gen5ShaderScalarEvaluator.FallbackMemoryReader = ReadFallback;
                Assert.True(
                    Gen5ShaderScalarEvaluator.TryEvaluate(
                        ctx,
                        state,
                        out var evaluation,
                        out var error),
                    error);
                Assert.Equal(expected, evaluation.ScalarRegisters.Skip(16).Take(4));
            }
            finally
            {
                Gen5ShaderScalarEvaluator.FallbackMemoryReader = previousReader;
            }
        }

        bool ReadFallback(ulong address, Span<byte> destination)
        {
            if (address < ScalarTableAddress)
            {
                return false;
            }

            var offset = address - ScalarTableAddress;
            if (offset + (ulong)destination.Length > (ulong)table.Length)
            {
                return false;
            }

            table.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return true;
        }
    }

    [Fact]
    public void ScalarLoadReadsMergedGraphicsIndirectUserDataPointer()
    {
        var expected = new uint[]
        {
            0x0000_1000,
            0x0000_2000,
            0x0000_3000,
            0x0000_4000,
        };
        var table = new byte[expected.Length * sizeof(uint)];
        for (var index = 0; index < expected.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                table.AsSpan(index * sizeof(uint), sizeof(uint)),
                expected[index]);
        }

        var load = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Smem,
            "SLoadDwordx4",
            [],
            [Gen5Operand.Scalar(0)],
            [
                Gen5Operand.Scalar(16),
                Gen5Operand.Scalar(17),
                Gen5Operand.Scalar(18),
                Gen5Operand.Scalar(19),
            ],
            new Gen5ScalarMemoryControl(4, 0, null));
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0, [load, end]),
            [0xCAFE_BABEu],
            Metadata: null,
            UserDataScalarRegisterBase: 8,
            GraphicsSystemRegisters:
                new Gen5GraphicsSystemRegisters(ScalarTableAddress));
        var ctx = new CpuContext(new FakeCpuMemory(0x1000, 0x100), Generation.Gen5);

        lock (FallbackReaderGate)
        {
            var previousReader = Gen5ShaderScalarEvaluator.FallbackMemoryReader;
            try
            {
                Gen5ShaderScalarEvaluator.FallbackMemoryReader = ReadFallback;
                Assert.True(
                    Gen5ShaderScalarEvaluator.TryEvaluate(
                        ctx,
                        state,
                        out var evaluation,
                        out var error),
                    error);
                Assert.Equal(
                    unchecked((uint)ScalarTableAddress),
                    evaluation.InitialScalarRegisters[0]);
                Assert.Equal(
                    (uint)(ScalarTableAddress >> 32),
                    evaluation.InitialScalarRegisters[1]);
                Assert.Equal(0xCAFE_BABEu, evaluation.InitialScalarRegisters[8]);
                Assert.Equal(expected, evaluation.ScalarRegisters.Skip(16).Take(4));
            }
            finally
            {
                Gen5ShaderScalarEvaluator.FallbackMemoryReader = previousReader;
            }
        }

        bool ReadFallback(ulong address, Span<byte> destination)
        {
            if (address < ScalarTableAddress)
            {
                return false;
            }

            var offset = address - ScalarTableAddress;
            if (offset + (ulong)destination.Length > (ulong)table.Length)
            {
                return false;
            }

            table.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return true;
        }
    }

    [Fact]
    public void ScalarLoadFollowsKnownSccBranchInsteadOfLinearWrongArm()
    {
        var expected = new uint[]
        {
            0x1111_2222,
            0x3333_4444,
            0x5555_6666,
            0x7777_8888,
        };
        var table = new byte[expected.Length * sizeof(uint)];
        for (var index = 0; index < expected.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                table.AsSpan(index * sizeof(uint), sizeof(uint)),
                expected[index]);
        }

        // SCC is false, so the branch skips the mutually-exclusive write of
        // 0x200 and the scalar load must retain the valid incoming pointer.
        // A linear walk used to execute that write anyway, producing the same
        // bogus descriptor observed in Astro Bot's scene compute shaders.
        var compare = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Sopc,
            "SCmpEqU32",
            [],
            [
                new Gen5Operand(Gen5OperandKind.LiteralConstant, 1),
                new Gen5Operand(Gen5OperandKind.LiteralConstant, 2),
            ],
            [],
            null);
        var branch = new Gen5ShaderInstruction(
            4,
            Gen5ShaderEncoding.Sopp,
            "SCbranchScc0",
            [1u],
            [],
            [],
            null);
        var wrongArm = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sop1,
            "SMovB32",
            [],
            [new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x200)],
            [Gen5Operand.Scalar(0)],
            null);
        var load = new Gen5ShaderInstruction(
            12,
            Gen5ShaderEncoding.Smem,
            "SLoadDwordx4",
            [],
            [Gen5Operand.Scalar(0)],
            [
                Gen5Operand.Scalar(16),
                Gen5Operand.Scalar(17),
                Gen5Operand.Scalar(18),
                Gen5Operand.Scalar(19),
            ],
            new Gen5ScalarMemoryControl(4, 0, null));
        var end = new Gen5ShaderInstruction(
            20,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0, [compare, branch, wrongArm, load, end]),
            [
                unchecked((uint)ScalarTableAddress),
                (uint)(ScalarTableAddress >> 32),
            ],
            null);
        var ctx = new CpuContext(new FakeCpuMemory(0x1000, 0x100), Generation.Gen5);

        lock (FallbackReaderGate)
        {
            var previousReader = Gen5ShaderScalarEvaluator.FallbackMemoryReader;
            try
            {
                Gen5ShaderScalarEvaluator.FallbackMemoryReader = ReadFallback;
                Assert.True(
                    Gen5ShaderScalarEvaluator.TryEvaluate(
                        ctx,
                        state,
                        out var evaluation,
                        out var error),
                    error);
                Assert.Equal(expected, evaluation.ScalarRegisters.Skip(16).Take(4));
                Assert.Contains(
                    evaluation.GlobalMemoryBindings,
                    binding => binding.BaseAddress == ScalarTableAddress);
            }
            finally
            {
                Gen5ShaderScalarEvaluator.FallbackMemoryReader = previousReader;
            }
        }

        bool ReadFallback(ulong address, Span<byte> destination)
        {
            if (address < ScalarTableAddress)
            {
                return false;
            }

            var offset = address - ScalarTableAddress;
            if (offset + (ulong)destination.Length > (ulong)table.Length)
            {
                return false;
            }

            table.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return true;
        }
    }

    [Fact]
    public void KnownSccFallthroughAlsoDiscoversTheUnselectedTargetResources()
    {
        var compare = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Sopc,
            "SCmpEqU32",
            [],
            [
                new Gen5Operand(Gen5OperandKind.LiteralConstant, 1),
                new Gen5Operand(Gen5OperandKind.LiteralConstant, 1),
            ],
            [],
            null);
        var branch = new Gen5ShaderInstruction(
            4,
            Gen5ShaderEncoding.Sopp,
            "SCbranchScc0",
            [2u],
            [],
            [],
            null);
        var selectedImage = CreateImageSample(8, scalarResource: 0, scalarSampler: 8);
        var selectedEnd = new Gen5ShaderInstruction(
            12,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var unselectedImage = CreateImageSample(16, scalarResource: 16, scalarSampler: 24);
        var unselectedEnd = new Gen5ShaderInstruction(
            20,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(
                0,
                [compare, branch, selectedImage, selectedEnd, unselectedImage, unselectedEnd]),
            Enumerable.Range(1, 32).Select(static value => (uint)value).ToArray(),
            null);
        var ctx = new CpuContext(new FakeCpuMemory(0x1000, 0x100), Generation.Gen5);

        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var error),
            error);
        Assert.Contains(evaluation.ImageBindings, binding => binding.Pc == 8);
        Assert.Contains(evaluation.ImageBindings, binding => binding.Pc == 16);

        static Gen5ShaderInstruction CreateImageSample(
            uint pc,
            uint scalarResource,
            uint scalarSampler) =>
            new(
                pc,
                Gen5ShaderEncoding.Mimg,
                "ImageSample",
                [],
                [],
                [Gen5Operand.Vector(0)],
                new Gen5ImageControl(
                    Dmask: 1,
                    VectorAddress: 0,
                    AddressRegisters: [0, 1],
                    VectorData: 0,
                    ScalarResource: scalarResource,
                    ScalarSampler: scalarSampler,
                    Dimension: 1,
                    IsArray: false,
                    Glc: false,
                    Slc: false,
                    A16: false,
                    D16: false));
    }

    [Fact]
    public void ExecConditionalBranchDiscoversItsForwardTargetResources()
    {
        var branch = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Sopp,
            "SCbranchExecz",
            [2u],
            [],
            [],
            null);
        var selectedImage = CreateImageSample(4, scalarResource: 0, scalarSampler: 8);
        var selectedEnd = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var targetImage = CreateImageSample(12, scalarResource: 16, scalarSampler: 24);
        var targetEnd = new Gen5ShaderInstruction(
            16,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(
                0,
                [branch, selectedImage, selectedEnd, targetImage, targetEnd]),
            Enumerable.Range(1, 32).Select(static value => (uint)value).ToArray(),
            null);
        var ctx = new CpuContext(new FakeCpuMemory(0x1000, 0x100), Generation.Gen5);

        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var error),
            error);
        Assert.Contains(evaluation.ImageBindings, binding => binding.Pc == 4);
        Assert.Contains(evaluation.ImageBindings, binding => binding.Pc == 12);

        static Gen5ShaderInstruction CreateImageSample(
            uint pc,
            uint scalarResource,
            uint scalarSampler) =>
            new(
                pc,
                Gen5ShaderEncoding.Mimg,
                "ImageSample",
                [],
                [],
                [Gen5Operand.Vector(0)],
                new Gen5ImageControl(
                    Dmask: 1,
                    VectorAddress: 0,
                    AddressRegisters: [0, 1],
                    VectorData: 0,
                    ScalarResource: scalarResource,
                    ScalarSampler: scalarSampler,
                    Dimension: 1,
                    IsArray: false,
                    Glc: false,
                    Slc: false,
                    A16: false,
                    D16: false));
    }

    [Fact]
    public void IndirectlyReachedImageGetsAPcLocalNeutralBinding()
    {
        var indirectControlTransfer = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Sop1,
            "SSetpcB64",
            [],
            [Gen5Operand.Scalar(0)],
            [],
            null);
        var image = new Gen5ShaderInstruction(
            4,
            Gen5ShaderEncoding.Mimg,
            "ImageSample",
            [],
            [],
            [Gen5Operand.Vector(0)],
            new Gen5ImageControl(
                Dmask: 1,
                VectorAddress: 0,
                AddressRegisters: [0, 1],
                VectorData: 0,
                ScalarResource: 16,
                ScalarSampler: 24,
                Dimension: 1,
                IsArray: false,
                Glc: false,
                Slc: false,
                A16: false,
                D16: false));
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0x1234, [indirectControlTransfer, image, end]),
            new uint[32],
            null);
        var ctx = new CpuContext(new FakeCpuMemory(0x1000, 0x100), Generation.Gen5);

        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var error),
            error);
        var binding = Assert.Single(evaluation.ImageBindings);
        Assert.Equal(4u, binding.Pc);
        Assert.All(binding.ResourceDescriptor, word => Assert.Equal(0u, word));
        Assert.All(binding.SamplerDescriptor, word => Assert.Equal(0u, word));
    }
}
