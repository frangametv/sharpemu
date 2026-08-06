// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5VertexInputCaptureTests
{
    private const ulong VertexAddress = 0x1_0000_1000;

    [Fact]
    public void InterleavedAttributesDoNotOverreadPastLastElement()
    {
        var memory = new ExactCpuMemory(VertexAddress, 64);
        Assert.True(memory.TryWrite(VertexAddress, Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));

        var program = new Gen5ShaderProgram(
            0x1_0000_0000,
            [
                CreateVertexFetch(pc: 0, scalarResource: 0, vectorData: 0),
                CreateVertexFetch(pc: 8, scalarResource: 4, vectorData: 2),
            ]);
        var state = new Gen5ShaderState(
            program,
            [
                .. CreateBufferDescriptor(VertexAddress),
                .. CreateBufferDescriptor(VertexAddress + 8),
            ],
            null);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var error,
                resolveVertexInputs: true,
                requiredVertexRecordCount: 4),
            error);

        Assert.NotNull(evaluation.VertexInputs);
        Assert.Equal(2, evaluation.VertexInputs.Count);
        Assert.All(evaluation.VertexInputs, input => Assert.Equal(64, input.DataLength));
        Assert.All(evaluation.VertexInputs, input => Assert.Equal(VertexAddress, input.BaseAddress));
        Assert.Equal(0u, evaluation.VertexInputs[0].OffsetBytes);
        Assert.Equal(8u, evaluation.VertexInputs[1].OffsetBytes);
        Assert.Same(evaluation.VertexInputs[0].Data, evaluation.VertexInputs[1].Data);
        Assert.Equal(
            Enumerable.Range(0, 64).Select(i => (byte)i),
            evaluation.VertexInputs[0].Data.AsSpan(0, 64).ToArray());
    }

    private static Gen5ShaderInstruction CreateVertexFetch(
        uint pc,
        uint scalarResource,
        uint vectorData) =>
        new(
            pc,
            Gen5ShaderEncoding.Mubuf,
            "BufferLoadFormatXy",
            [],
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Scalar(scalarResource),
                Gen5Operand.Source(128),
            ],
            [Gen5Operand.Vector(vectorData), Gen5Operand.Vector(vectorData + 1)],
            new Gen5BufferMemoryControl(
                DwordCount: 2,
                VectorAddress: 0,
                VectorData: vectorData,
                ScalarResource: scalarResource,
                OffsetBytes: 0,
                IndexEnabled: true,
                OffsetEnabled: false,
                Glc: false,
                Slc: false));

    private static uint[] CreateBufferDescriptor(ulong address) =>
    [
        unchecked((uint)address),
        unchecked((uint)(address >> 32)) | (16u << 16),
        4,
        64u << 12,
    ];

    private sealed class ExactCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

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

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < baseAddress)
            {
                return false;
            }

            var relative = virtualAddress - baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = checked((int)relative);
            return true;
        }
    }
}
