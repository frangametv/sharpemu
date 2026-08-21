// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5DataShareTests
{
    private const ulong ShaderAddress = 0x1_0000_4000;
    private const uint SEndpgm = 0xBF810000;
    private const ushort OpStore = 62;
    private const ushort OpAtomicIAdd = 234;
    private const ushort OpAtomicISub = 235;
    private const ushort OpSelectionMerge = 247;
    private const ushort OpGroupNonUniformBroadcast = 337;
    private const ushort OpGroupNonUniformBallot = 339;

    [Fact]
    public void DsAddRtnU32DecodesOpcode20WithDestination()
    {
        var program = DecodeProgram(0x20, offset: 128);

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "DsAddRtnU32");
        Assert.Equal(Gen5ShaderEncoding.Ds, instruction.Encoding);
        Assert.Equal(Gen5Operand.Vector(2), instruction.Sources[0]);
        Assert.Equal(Gen5Operand.Vector(3), instruction.Sources[1]);
        Assert.Equal(Gen5Operand.Vector(7), Assert.Single(instruction.Destinations));
        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.Equal(128U, control.Offset0);
        Assert.False(control.Gds);
    }

    [Fact]
    public void DsAddRtnU32PreservesFullUnsignedOffset16()
    {
        var program = DecodeProgram(0x20, offset: 0xAB80);

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "DsAddRtnU32");
        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.Equal(0x80U, control.Offset0);
        Assert.Equal(0xABU, control.Offset1);
    }

    [Fact]
    public void DsAppendDecodesOpcode3EWithGdsDestination()
    {
        var program = DecodeProgram(0x3E, offset: 0x14, gds: true);

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "DsAppend");
        Assert.Empty(instruction.Sources);
        Assert.Equal(Gen5Operand.Vector(7), Assert.Single(instruction.Destinations));
        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.Equal(0x14U, control.Offset0);
        Assert.True(control.Gds);
    }

    [Fact]
    public void DsConsumeDecodesOpcode3DWithGdsDestination()
    {
        var program = DecodeProgram(0x3D, offset: 4, gds: true);

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "DsConsume");
        Assert.Empty(instruction.Sources);
        Assert.Equal(Gen5Operand.Vector(7), Assert.Single(instruction.Destinations));
        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.Equal(4U, control.Offset0);
        Assert.True(control.Gds);
    }

    [Fact]
    public void DsPermuteB32DecodesOpcodeB2()
    {
        var program = DecodeProgram(0xB2);

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "DsPermuteB32");
        Assert.Equal(
            [Gen5Operand.Vector(2), Gen5Operand.Vector(3)],
            instruction.Sources);
        Assert.Equal(Gen5Operand.Vector(7), Assert.Single(instruction.Destinations));
    }

    [Theory]
    [InlineData(0x80E4)] // QUAD_PERM identity
    [InlineData(0x041F)] // BITMASK_PERM lane xor 1
    public void DsSwizzleB32DecodesAndLowersToSubgroupShuffle(uint pattern)
    {
        var program = DecodeProgram(0x35, offset: pattern);
        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "DsSwizzleB32");

        Assert.Equal([Gen5Operand.Vector(3)], instruction.Sources);
        Assert.Equal(Gen5Operand.Vector(7), Assert.Single(instruction.Destinations));
        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.Equal(pattern & 0xFF, control.Offset0);
        Assert.Equal(pattern >> 8, control.Offset1);

        var opcodes = CompileAndReadSpirvOpcodes(0x35, offset: pattern);
        Assert.Contains((ushort)SpirvOp.GroupNonUniformShuffle, opcodes);
    }

    [Fact]
    public void DsWriteAddtidB32DecodesRealTitleShaderWord()
    {
        var program = DecodeProgram(0xB0, offset: 0x700);

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "DsWriteAddtidB32");
        Assert.Equal([Gen5Operand.Vector(3)], instruction.Sources);
        Assert.Empty(instruction.Destinations);
        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.Equal(0U, control.Offset0);
        Assert.Equal(7U, control.Offset1);

        var opcodes = CompileAndReadSpirvOpcodes(0xB0, offset: 0x700);
        Assert.Contains((ushort)SpirvOp.IMul, opcodes);
        Assert.Contains(OpStore, opcodes);
    }

    [Fact]
    public void DsReadAddtidB32DecodesAndLowersWithoutAddressVgpr()
    {
        var program = DecodeProgram(0xB1, offset: 0x234);

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "DsReadAddtidB32");
        Assert.Empty(instruction.Sources);
        Assert.Equal(Gen5Operand.Vector(7), Assert.Single(instruction.Destinations));
        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.Equal(0x34U, control.Offset0);
        Assert.Equal(0x02U, control.Offset1);

        var opcodes = CompileAndReadSpirvOpcodes(0xB1, offset: 0x234);
        Assert.Contains((ushort)SpirvOp.BitwiseAnd, opcodes);
        Assert.Contains((ushort)SpirvOp.IMul, opcodes);
        Assert.Contains((ushort)SpirvOp.Load, opcodes);
        Assert.Contains(OpStore, opcodes);
    }

    [Fact]
    public void DsRead2B64DecodesFourDestinationsAndLowersFourLoads()
    {
        var program = DecodeProgram(0x77, offset: 0x0201);
        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "DsRead2B64");

        Assert.Equal([Gen5Operand.Vector(2)], instruction.Sources);
        Assert.Equal(
            [
                Gen5Operand.Vector(7),
                Gen5Operand.Vector(8),
                Gen5Operand.Vector(9),
                Gen5Operand.Vector(10),
            ],
            instruction.Destinations);

        var opcodes = CompileAndReadSpirvOpcodes(0x77, offset: 0x0201);
        Assert.True(opcodes.Count(opcode => opcode == (ushort)SpirvOp.Load) >= 4);
    }

    [Fact]
    public void GdsAppendLowersToOneDeviceAtomicAndWaveBroadcast()
    {
        var program = DecodeProgram(0x3E, offset: 0x14, gds: true);
        var state = new Gen5ShaderState(program, [], null);
        var scalarRegisters = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [],
            []);

        Assert.True(
            Gen5SpirvTranslator.TryCompilePixelShader(
                state,
                evaluation,
                Gen5PixelOutputKind.Float,
                out var shader,
                out var error,
                totalGlobalBufferCount: 1,
                gdsBufferIndex: 0),
            error);

        var opcodes = ReadSpirvOpcodes(shader.Spirv);
        Assert.Equal(1, opcodes.Count(opcode => opcode == OpAtomicIAdd));
        Assert.Contains(OpGroupNonUniformBallot, opcodes);
        Assert.Contains(OpGroupNonUniformBroadcast, opcodes);
    }

    [Fact]
    public void GdsConsumeLowersToOneDeviceAtomicSubtractAndWaveBroadcast()
    {
        var program = DecodeProgram(0x3D, offset: 4, gds: true);
        var state = new Gen5ShaderState(program, [], null);
        var scalarRegisters = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [],
            []);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out var error,
                totalGlobalBufferCount: 1,
                gdsBufferIndex: 0),
            error);

        var opcodes = ReadSpirvOpcodes(shader.Spirv);
        Assert.Equal(1, opcodes.Count(opcode => opcode == OpAtomicISub));
        Assert.Contains(OpGroupNonUniformBallot, opcodes);
        Assert.Contains(OpGroupNonUniformBroadcast, opcodes);
    }

    [Fact]
    public void GdsWaveCountUsesOnlyVulkanLegal32BitBitCounts()
    {
        var program = DecodeProgram(0x3E, offset: 0x14, gds: true);
        var state = new Gen5ShaderState(program, [], null);
        var scalarRegisters = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [],
            []);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out var error,
                totalGlobalBufferCount: 1,
                gdsBufferIndex: 0),
            error);

        AssertBitCountResultTypesAre32Bit(shader.Spirv);
    }

    [Fact]
    public void DsAddRtnU32LowersToAtomicAddAndReturnedValueStore()
    {
        var noReturnOpcodes = CompileAndReadSpirvOpcodes(0x00);
        var returnOpcodes = CompileAndReadSpirvOpcodes(0x20);

        Assert.Equal(
            noReturnOpcodes.Count(opcode => opcode == OpAtomicIAdd),
            returnOpcodes.Count(opcode => opcode == OpAtomicIAdd));
        Assert.Equal(
            noReturnOpcodes.Count(opcode => opcode == OpStore) + 1,
            returnOpcodes.Count(opcode => opcode == OpStore));
        var returnOpcodeList = returnOpcodes.ToList();
        Assert.True(
            returnOpcodeList.IndexOf(OpSelectionMerge) <
            returnOpcodeList.IndexOf(OpAtomicIAdd));
    }

    private static Gen5ShaderProgram DecodeProgram(
        uint opcode,
        uint offset = 0,
        bool gds = false)
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[3 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            shader,
            0xD8000000u | (opcode << 18) | offset | (gds ? 1u << 17 : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(
            shader[sizeof(uint)..],
            2u | (3u << 8) | (7u << 24));
        BinaryPrimitives.WriteUInt32LittleEndian(
            shader[(2 * sizeof(uint))..],
            SEndpgm);
        Assert.True(memory.TryWrite(ShaderAddress, shader));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var error),
            error);
        return program;
    }

    private static IReadOnlyList<ushort> CompileAndReadSpirvOpcodes(
        uint opcode,
        uint offset = 0)
    {
        var program = DecodeProgram(opcode, offset);
        var state = new Gen5ShaderState(program, [], null);
        var scalarRegisters = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [],
            []);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out var error),
            error);

        return ReadSpirvOpcodes(shader.Spirv);
    }

    private static IReadOnlyList<ushort> ReadSpirvOpcodes(byte[] spirv)
    {
        var opcodes = new List<ushort>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            opcodes.Add((ushort)instruction);
            offset += wordCount * sizeof(uint);
        }

        return opcodes;
    }

    private static void AssertBitCountResultTypesAre32Bit(byte[] spirv)
    {
        const ushort opTypeInt = 21;
        var integerWidths = new Dictionary<uint, uint>();
        var bitCountResultTypes = new List<uint>();

        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            var opcode = (ushort)instruction;
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));

            if (opcode == opTypeInt && wordCount >= 4)
            {
                var resultId = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + sizeof(uint)));
                var width = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + (2 * sizeof(uint))));
                integerWidths[resultId] = width;
            }
            else if (opcode == (ushort)SpirvOp.BitCount && wordCount >= 4)
            {
                bitCountResultTypes.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + sizeof(uint))));
            }

            offset += wordCount * sizeof(uint);
        }

        Assert.NotEmpty(bitCountResultTypes);
        Assert.All(
            bitCountResultTypes,
            typeId => Assert.Equal(32u, integerWidths[typeId]));
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
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

            offset = (int)relative;
            return true;
        }
    }
}
