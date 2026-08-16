// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5Vop3Tests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SEndpgm = 0xBF810000;
    private const ushort OpBitFieldSExtract = 202;
    private const ushort OpBitFieldUExtract = 203;
    private const ushort OpArrayLength = 68;
    private const ushort OpISub = 130;
    private const ushort OpULessThan = 176;
    private const ushort OpUGreaterThan = 172;

    [Fact]
    public void VBfeI32DecodesFromVop3Opcode149()
    {
        var program = DecodeProgram(0x149);

        var instruction = Assert.Single(program.Instructions, item => item.Opcode == "VBfeI32");
        Assert.Equal(Gen5ShaderEncoding.Vop3, instruction.Encoding);
        Assert.Equal(Gen5Operand.Vector(0), instruction.Sources[0]);
        Assert.Equal(Gen5Operand.Scalar(0), instruction.Sources[1]);
        Assert.Equal(Gen5Operand.Scalar(1), instruction.Sources[2]);
        Assert.Equal(Gen5Operand.Vector(3), Assert.Single(instruction.Destinations));
    }

    [Fact]
    public void VBfeI32LowersToSignedBitFieldExtract()
    {
        var signedOpcodes = CompileAndReadSpirvOpcodes(0x149);
        var unsignedOpcodes = CompileAndReadSpirvOpcodes(0x148);

        Assert.Equal(
            unsignedOpcodes.Count(opcode => opcode == OpBitFieldSExtract) + 1,
            signedOpcodes.Count(opcode => opcode == OpBitFieldSExtract));
        Assert.Equal(
            unsignedOpcodes.Count(opcode => opcode == OpBitFieldUExtract),
            signedOpcodes.Count(opcode => opcode == OpBitFieldUExtract) + 1);
    }

    [Theory]
    [InlineData(0x129u, "VSubbU32")]
    [InlineData(0x12Au, "VSubbrevU32")]
    public void Vop3SubtractWithBorrowDecodesAndLowers(uint opcode, string expectedName)
    {
        var program = DecodeProgram(opcode);
        var instruction = Assert.Single(program.Instructions, item => item.Opcode == expectedName);

        Assert.Equal(Gen5ShaderEncoding.Vop3, instruction.Encoding);
        Assert.Equal(Gen5Operand.Vector(0), instruction.Sources[0]);
        Assert.Equal(Gen5Operand.Scalar(0), instruction.Sources[1]);
        Assert.Equal(Gen5Operand.Scalar(1), instruction.Sources[2]);
        Assert.Equal(Gen5Operand.Vector(3), Assert.Single(instruction.Destinations));
        Assert.Equal(
            0u,
            Assert.IsType<Gen5Vop3Control>(instruction.Control).ScalarDestination);

        var spirvOpcodes = CompileAndReadSpirvOpcodes(opcode);
        Assert.Contains(OpISub, spirvOpcodes);
        Assert.Contains(OpULessThan, spirvOpcodes);
    }

    [Theory]
    [InlineData(0x0E0u, "VCmpFU64")]
    [InlineData(0x0E1u, "VCmpLtU64")]
    [InlineData(0x0E2u, "VCmpEqU64")]
    [InlineData(0x0E3u, "VCmpLeU64")]
    [InlineData(0x0E4u, "VCmpGtU64")]
    [InlineData(0x0E5u, "VCmpNeU64")]
    [InlineData(0x0E6u, "VCmpGeU64")]
    [InlineData(0x0E7u, "VCmpTU64")]
    [InlineData(0x0F0u, "VCmpxFU64")]
    [InlineData(0x0F1u, "VCmpxLtU64")]
    [InlineData(0x0F2u, "VCmpxEqU64")]
    [InlineData(0x0F3u, "VCmpxLeU64")]
    [InlineData(0x0F4u, "VCmpxGtU64")]
    [InlineData(0x0F5u, "VCmpxNeU64")]
    [InlineData(0x0F6u, "VCmpxGeU64")]
    [InlineData(0x0F7u, "VCmpxTU64")]
    public void Unsigned64CompareFamilyDecodesAndLowers(uint opcode, string expectedName)
    {
        var program = DecodeProgram(opcode);
        var instruction = Assert.Single(program.Instructions, item => item.Opcode == expectedName);

        Assert.Equal(Gen5ShaderEncoding.Vop3, instruction.Encoding);
        Assert.Equal(Gen5Operand.Vector(0), instruction.Sources[0]);
        Assert.Equal(Gen5Operand.Scalar(0), instruction.Sources[1]);
        Assert.Equal(Gen5Operand.Scalar(3), Assert.Single(instruction.Destinations));
        Assert.Equal(
            3u,
            Assert.IsType<Gen5Vop3Control>(instruction.Control).ScalarDestination);

        Assert.NotEmpty(CompileAndReadSpirvOpcodes(opcode));
    }

    [Theory]
    [InlineData(0x178u, "VXor3B32")]
    [InlineData(0x345u, "VXadU32")]
    public void ThreeSourceIntegerOperationsDecodeAndLower(uint opcode, string expectedName)
    {
        var program = DecodeProgram(opcode);
        var instruction = Assert.Single(program.Instructions, item => item.Opcode == expectedName);
        Assert.Equal(3, instruction.Sources.Count);
        Assert.NotEmpty(CompileAndReadSpirvOpcodes(opcode));
    }

    [Fact]
    public void VFmaMixF32DecodesSelectorsAndModifiers()
    {
        var program = DecodeVop3pProgram(
            opSelMask: 0b101,
            opSelHiMask: 0b110,
            negateMask: 0b011,
            absoluteMask: 0b100,
            clamp: true);

        var instruction = Assert.Single(program.Instructions, item => item.Opcode == "VFmaMixF32");
        Assert.Equal(Gen5ShaderEncoding.Vop3p, instruction.Encoding);
        Assert.Equal(Gen5Operand.Vector(0), instruction.Sources[0]);
        Assert.Equal(Gen5Operand.Vector(1), instruction.Sources[1]);
        Assert.Equal(Gen5Operand.Vector(2), instruction.Sources[2]);
        Assert.Equal(Gen5Operand.Vector(3), Assert.Single(instruction.Destinations));
        Assert.Equal(
            new Gen5Vop3pControl(0b101, 0b110, 0b011, 0b100, true),
            Assert.IsType<Gen5Vop3pControl>(instruction.Control));
    }

    [Fact]
    public void VFmaMixF32LowersMixedPrecisionFusedModifiersAndClamp()
    {
        var pureF32 = CompileVop3pAndReadSpirv(
            opSelMask: 0,
            opSelHiMask: 0,
            negateMask: 0,
            absoluteMask: 0,
            clamp: false);
        var mixed = CompileVop3pAndReadSpirv(
            opSelMask: 0b001,
            opSelHiMask: 0b001,
            negateMask: 0b010,
            absoluteMask: 0b100,
            clamp: true);

        var pureExtInsts = ReadGlslExtInstNumbers(pureF32);
        var mixedExtInsts = ReadGlslExtInstNumbers(mixed);
        Assert.Contains(50u, pureExtInsts); // Fma
        Assert.DoesNotContain(75u, pureExtInsts); // FindUMsb, used by f16 widening
        Assert.Contains(50u, mixedExtInsts);
        Assert.Contains(75u, mixedExtInsts);
        Assert.Contains(4u, mixedExtInsts); // FAbs
        var mixedOpcodes = ReadSpirvOpcodes(mixed);
        Assert.Contains((ushort)SpirvOp.FOrdGreaterThan, mixedOpcodes);
        Assert.Contains((ushort)SpirvOp.FOrdLessThan, mixedOpcodes);
        Assert.True(mixedOpcodes.Count(opcode => opcode == (ushort)SpirvOp.Select) >= 2);
        Assert.Contains((ushort)SpirvOp.FNegate, mixedOpcodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BufferBoundsDoNotUseRuntimeArrayLength(bool runtimeScalars)
    {
        var instruction = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Smem,
            "SBufferLoadDword",
            [],
            [Gen5Operand.Scalar(0)],
            [Gen5Operand.Scalar(4)],
            new Gen5ScalarMemoryControl(1, 0, null));
        var program = new Gen5ShaderProgram(ShaderAddress, [instruction]);
        var state = new Gen5ShaderState(program, [], null);
        var scalarRegisters = new uint[256];
        var binding = new Gen5GlobalMemoryBinding(
            0,
            0x1003,
            [0],
            new byte[17],
            17,
            false);
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [],
            [binding]);
        var initialScalarBufferIndex = runtimeScalars ? 1 : -1;
        var totalGlobalBufferCount = runtimeScalars ? 2 : 1;

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out var error,
                totalGlobalBufferCount,
                initialScalarBufferIndex,
                storageBufferOffsetAlignment: 256),
            error);

        var opcodes = ReadSpirvOpcodes(shader.Spirv);
        Assert.DoesNotContain(OpArrayLength, opcodes);
        Assert.Contains(OpULessThan, opcodes);
    }

    [Fact]
    public void RuntimeScalarLayoutInterleavesMetadataByBinding()
    {
        Assert.Equal(256, Gen5RuntimeScalarLayout.GetByteBiasDwordIndex(0));
        Assert.Equal(257, Gen5RuntimeScalarLayout.GetBufferDwordCountDwordIndex(0));
        Assert.Equal(258, Gen5RuntimeScalarLayout.GetByteBiasDwordIndex(1));
        Assert.Equal(259, Gen5RuntimeScalarLayout.GetBufferDwordCountDwordIndex(1));
        Assert.Equal(266, Gen5RuntimeScalarLayout.GetDwordLength(5));
    }

    private static Gen5ShaderProgram DecodeProgram(uint opcode)
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[3 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(shader, 0xD0000003u | (opcode << 16));
        BinaryPrimitives.WriteUInt32LittleEndian(
            shader[sizeof(uint)..],
            0x100u | (1u << 18)); // v0, s0, s1
        BinaryPrimitives.WriteUInt32LittleEndian(shader[(2 * sizeof(uint))..], SEndpgm);
        Assert.True(memory.TryWrite(ShaderAddress, shader));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(ctx, ShaderAddress, out var program, out var error),
            error);
        return program;
    }

    private static Gen5ShaderProgram DecodeVop3pProgram(
        uint opSelMask,
        uint opSelHiMask,
        uint negateMask,
        uint absoluteMask,
        bool clamp)
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[3 * sizeof(uint)];
        var word =
            0xCC000003u |
            (0x20u << 16) |
            ((absoluteMask & 0x7) << 8) |
            ((opSelMask & 0x7) << 11) |
            (((opSelHiMask >> 2) & 1) << 14) |
            (clamp ? 1u << 15 : 0);
        var extra =
            0x100u |
            (0x101u << 9) |
            (0x102u << 18) |
            ((opSelHiMask & 0x3) << 27) |
            ((negateMask & 0x7) << 29);
        BinaryPrimitives.WriteUInt32LittleEndian(shader, word);
        BinaryPrimitives.WriteUInt32LittleEndian(shader[sizeof(uint)..], extra);
        BinaryPrimitives.WriteUInt32LittleEndian(shader[(2 * sizeof(uint))..], SEndpgm);
        Assert.True(memory.TryWrite(ShaderAddress, shader));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(ctx, ShaderAddress, out var program, out var error),
            error);
        return program;
    }

    private static byte[] CompileVop3pAndReadSpirv(
        uint opSelMask,
        uint opSelHiMask,
        uint negateMask,
        uint absoluteMask,
        bool clamp)
    {
        var program = DecodeVop3pProgram(
            opSelMask,
            opSelHiMask,
            negateMask,
            absoluteMask,
            clamp);
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
        return shader.Spirv;
    }

    private static IReadOnlyList<uint> ReadGlslExtInstNumbers(byte[] spirv)
    {
        var operations = new List<uint>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var wordCount = checked((int)(header >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            if ((ushort)header == (ushort)SpirvOp.ExtInst)
            {
                Assert.True(wordCount >= 5);
                operations.Add(BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset + (4 * sizeof(uint)))));
            }

            offset += wordCount * sizeof(uint);
        }

        return operations;
    }

    private static IReadOnlyList<ushort> CompileAndReadSpirvOpcodes(uint opcode)
    {
        var program = DecodeProgram(opcode);
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
        Assert.Equal(0, spirv.Length % sizeof(uint));
        Assert.True(spirv.Length >= 5 * sizeof(uint));
        Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(spirv));

        var opcodes = new List<ushort>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            opcodes.Add((ushort)instruction);
            offset += wordCount * sizeof(uint);
        }

        return opcodes;
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
