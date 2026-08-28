// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ImageTests
{
    [Theory]
    [InlineData(0u, 4u, 0xE4)]
    [InlineData(1u, 4u, 0xC6)]
    [InlineData(2u, 4u, 0x1B)]
    [InlineData(3u, 4u, 0x93)]
    public void RenderTargetComponentSwapResolvesPhysicalOrder(
        uint componentSwap,
        uint componentCount,
        byte expectedPacked)
    {
        Assert.True(Gen5ColorComponentMapping.TryResolveRenderTarget(
            componentSwap,
            componentCount,
            out var mapping));
        Assert.Equal(expectedPacked, mapping.Packed);
    }

    [Theory]
    [InlineData(0xFACu, 0xFu, 0, 0)]
    [InlineData(0xFACu, 0xFu, 3, 3)]
    [InlineData(0x9F5u, 0xFu, 0, 3)]
    [InlineData(0x9F5u, 0xFu, 1, 0)]
    [InlineData(0xFA4u, 0xFu, 1, -1)]
    [InlineData(0xFACu, 0x0u, 0, 0)]
    [InlineData(0xFACu, 0x0u, 1, -1)]
    public void ImageStoreMapsLogicalSourcesToPhysicalChannels(
        uint dstSelect,
        uint dmask,
        int destinationComponent,
        int expectedSourceIndex)
    {
        Assert.Equal(
            expectedSourceIndex,
            Gen5ShaderTranslator.GetImageStoreSourceIndex(
                dstSelect,
                dmask,
                destinationComponent));
    }

    private const ulong ShaderAddress = 0x1_0000_C000;
    private const uint SEndpgm = 0xBF810000;

    [Fact]
    public void BvhIntersectRayUsesSplitMimgOpcodeHighBit()
    {
        var program = DecodeBvhProgram();

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "ImageBvhIntersectRay");
        Assert.Equal(Gen5ShaderEncoding.Mimg, instruction.Encoding);
        Assert.Equal(5, instruction.Words.Count);
        Assert.Equal(
            new[]
            {
                Gen5Operand.Vector(3),
                Gen5Operand.Vector(4),
                Gen5Operand.Vector(5),
                Gen5Operand.Vector(6),
            },
            instruction.Destinations);
        var control = Assert.IsType<Gen5ImageControl>(instruction.Control);
        Assert.Equal(
            new uint[] { 3, 63, 13, 68, 67, 64, 65, 66, 70, 71, 72 },
            control.AddressRegisters);
        Assert.Equal(16U, control.ScalarResource);
        Assert.Equal(0U, control.ScalarSampler);
        Assert.Equal(0xFU, control.Dmask);
        Assert.Equal(12, instruction.Sources.Count);
    }

    [Fact]
    public void NullBvhDescriptorCompilesAsNoHitSentinel()
    {
        var program = DecodeBvhProgram();
        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "ImageBvhIntersectRay");
        var control = Assert.IsType<Gen5ImageControl>(instruction.Control);
        var state = new Gen5ShaderState(program, [], null);
        var scalarRegisters = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [
                new Gen5ImageBinding(
                    instruction.Pc,
                    instruction.Opcode,
                    control,
                    new uint[8],
                    [],
                    null),
            ],
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
        Assert.True(ContainsSpirvConstant(shader.Spirv, uint.MaxValue));
    }

    [Fact]
    public void NonNullBvhDescriptorAlsoCompilesAsNoHitSentinel()
    {
        var program = DecodeBvhProgram();
        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "ImageBvhIntersectRay");
        var control = Assert.IsType<Gen5ImageControl>(instruction.Control);
        var state = new Gen5ShaderState(program, [], null);
        var scalarRegisters = new uint[256];
        var descriptor = new uint[8];
        descriptor[0] = 1;
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [
                new Gen5ImageBinding(
                    instruction.Pc,
                    instruction.Opcode,
                    control,
                    descriptor,
                    [],
                    null),
            ],
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
        Assert.True(ContainsSpirvConstant(shader.Spirv, uint.MaxValue));
    }

    [Fact]
    public void ImageStoreDmaskPreservesMaskedChannels()
    {
        var maskedOpcodes = CompileImageStoreAndReadSpirvOpcodes(0x7);
        var fullOpcodes = CompileImageStoreAndReadSpirvOpcodes(0xF);

        Assert.Equal(
            fullOpcodes.Count(opcode => opcode == (ushort)SpirvOp.ImageRead) + 1,
            maskedOpcodes.Count(opcode => opcode == (ushort)SpirvOp.ImageRead));
        Assert.Equal(
            fullOpcodes.Count(opcode => opcode == (ushort)SpirvOp.CompositeInsert) + 3,
            maskedOpcodes.Count(opcode => opcode == (ushort)SpirvOp.CompositeInsert));
        Assert.Equal(
            fullOpcodes.Count(opcode => opcode == (ushort)SpirvOp.ImageWrite),
            maskedOpcodes.Count(opcode => opcode == (ushort)SpirvOp.ImageWrite));
    }

    [Theory]
    [InlineData(1u, SpirvImageDim.Dim2D, 2u)]
    [InlineData(2u, SpirvImageDim.Dim3D, 3u)]
    public void ImageStoreDimensionControlsImageAndCoordinateTypes(
        uint dimension,
        SpirvImageDim expectedImageDimension,
        uint expectedCoordinateComponents)
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation("ImageStore", dimension));
        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);

        Assert.Equal((uint)expectedImageDimension, imageType.Operands[2]);
        Assert.Equal(2u, imageType.Operands[6]);
        AssertCoordinateVectorWidth(
            instructions,
            SpirvOp.ImageWrite,
            coordinateOperand: 1,
            expectedComponents: expectedCoordinateComponents);

        var sizeQuery = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ImageQuerySize);
        AssertVectorTypeWidth(
            instructions,
            sizeQuery.Operands[0],
            expectedCoordinateComponents);
    }

    [Fact]
    public void ImageSampleDim3DUsesThreeComponentSampleCoordinates()
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation("ImageSampleLz", dimension: 2));
        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);

        Assert.Equal((uint)SpirvImageDim.Dim3D, imageType.Operands[2]);
        Assert.Equal(1u, imageType.Operands[6]);
        AssertCoordinateVectorWidth(
            instructions,
            SpirvOp.ImageSampleExplicitLod,
            coordinateOperand: 3,
            expectedComponents: 3);
    }

    [Fact]
    public void ImageLoadSharesStorageImageWhenOnlyWritePolicyBitsDiffer()
    {
        var control = new Gen5ImageControl(
            Dmask: 0xF,
            VectorAddress: 0,
            AddressRegisters: [0, 1],
            VectorData: 4,
            ScalarResource: 8,
            ScalarSampler: 0,
            Dimension: 1,
            IsArray: false,
            Glc: false,
            Slc: false,
            A16: false,
            D16: false);
        uint[] loadDescriptor =
        [
            0x053C4200,
            0xC4700000,
            0x0155C25F,
            0x91B00FAC,
            0,
            0,
            0xC07B0000,
            0x00057056,
        ];
        var storeDescriptor = (uint[])loadDescriptor.Clone();
        storeDescriptor[5] = 0x0070_0000;
        var load = new Gen5ImageBinding(
            0x80,
            "ImageLoad",
            control,
            loadDescriptor,
            [],
            null);
        var store = new Gen5ImageBinding(
            0x198,
            "ImageStore",
            control,
            storeDescriptor,
            [],
            null);

        Assert.True(Gen5ShaderTranslator.RequiresStorageImage(load, [load, store]));

        var otherDescriptor = (uint[])storeDescriptor.Clone();
        otherDescriptor[0]++;
        var otherStore = store with { ResourceDescriptor = otherDescriptor };
        Assert.True(
            Gen5ShaderTranslator.RequiresStorageImage(load, [load, otherStore]));

        var compressedDescriptor = (uint[])loadDescriptor.Clone();
        compressedDescriptor[1] = 169u << 20; // BC1_UNORM
        var compressedLoad = load with
        {
            ResourceDescriptor = compressedDescriptor,
        };
        Assert.False(
            Gen5ShaderTranslator.RequiresStorageImage(
                compressedLoad,
                [compressedLoad, otherStore]));
    }

    [Theory]
    [InlineData(3u, SpirvOp.ConvertSToF)]
    [InlineData(2u, SpirvOp.ConvertUToF)]
    public void ScaledImageLoadUsesIntegerStorageAndReturnsFloatBits(
        uint numberType,
        SpirvOp expectedConversion)
    {
        var unifiedFormat = numberType == 3 ? 4u : 3u;
        var opcodes = CompileImageLoadAndReadSpirvOpcodes(unifiedFormat);

        Assert.Contains((ushort)SpirvOp.ImageRead, opcodes);
        Assert.Contains((ushort)expectedConversion, opcodes);
    }

    private static IReadOnlyList<ushort> CompileImageStoreAndReadSpirvOpcodes(
        uint dmask)
    {
        var control = new Gen5ImageControl(
            dmask,
            VectorAddress: 0,
            AddressRegisters: [0, 1],
            VectorData: 4,
            ScalarResource: 8,
            ScalarSampler: 0,
            Dimension: 1,
            IsArray: false,
            Glc: false,
            Slc: false,
            A16: false,
            D16: false);
        var store = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mimg,
            "ImageStore",
            [],
            [],
            [],
            control);
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [SEndpgm],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(ShaderAddress, [store, end]),
            [],
            null);
        var scalarRegisters = new uint[256];
        var descriptor = new uint[8];
        descriptor[1] = 71u << 20; // FORMAT_16_16_16_16_FLOAT
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [
                new Gen5ImageBinding(
                    store.Pc,
                    store.Opcode,
                    control,
                    descriptor,
                    [],
                    null),
            ],
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

    private static IReadOnlyList<ushort> CompileImageLoadAndReadSpirvOpcodes(
        uint unifiedFormat)
    {
        var control = new Gen5ImageControl(
            Dmask: 0x1,
            VectorAddress: 0,
            AddressRegisters: [0, 1],
            VectorData: 4,
            ScalarResource: 8,
            ScalarSampler: 0,
            Dimension: 1,
            IsArray: false,
            Glc: false,
            Slc: false,
            A16: false,
            D16: false);
        var load = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mimg,
            "ImageLoad",
            [],
            [],
            [],
            control);
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [SEndpgm],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(ShaderAddress, [load, end]),
            [],
            null);
        var scalarRegisters = new uint[256];
        var descriptor = new uint[8];
        descriptor[1] = unifiedFormat << 20;
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [
                new Gen5ImageBinding(
                    load.Pc,
                    load.Opcode,
                    control,
                    descriptor,
                    [],
                    null),
            ],
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

    private static byte[] CompileImageOperation(string opcode, uint dimension)
    {
        var addressRegisters = dimension == 2
            ? new uint[] { 0, 1, 2 }
            : [0, 1];
        var control = new Gen5ImageControl(
            Dmask: 0xF,
            VectorAddress: 0,
            AddressRegisters: addressRegisters,
            VectorData: 4,
            ScalarResource: 8,
            ScalarSampler: 16,
            Dimension: dimension,
            IsArray: false,
            Glc: false,
            Slc: false,
            A16: false,
            D16: false);
        var imageInstruction = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mimg,
            opcode,
            [],
            [],
            [],
            control);
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [SEndpgm],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(ShaderAddress, [imageInstruction, end]),
            [],
            null);
        var scalarRegisters = new uint[256];
        var descriptor = new uint[8];
        descriptor[1] = 71u << 20; // FORMAT_16_16_16_16_FLOAT
        descriptor[3] = (dimension == 2 ? 10u : 9u) << 28;
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [
                new Gen5ImageBinding(
                    imageInstruction.Pc,
                    imageInstruction.Opcode,
                    control,
                    descriptor,
                    new uint[4],
                    null),
            ],
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

    private static IReadOnlyList<ParsedSpirvInstruction> ReadSpirvInstructions(
        byte[] spirv)
    {
        var instructions = new List<ParsedSpirvInstruction>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            var operands = new uint[wordCount - 1];
            for (var operand = 0; operand < operands.Length; operand++)
            {
                operands[operand] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + (operand + 1) * sizeof(uint)));
            }

            instructions.Add(
                new ParsedSpirvInstruction((SpirvOp)(ushort)instruction, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private static void AssertCoordinateVectorWidth(
        IReadOnlyList<ParsedSpirvInstruction> instructions,
        SpirvOp operation,
        int coordinateOperand,
        uint expectedComponents)
    {
        var imageOperation = Assert.Single(
            instructions,
            item => item.Opcode == operation);
        var coordinateId = imageOperation.Operands[coordinateOperand];
        var coordinate = Assert.Single(
            instructions,
            item =>
                item.Opcode == SpirvOp.CompositeConstruct &&
                item.Operands.Length >= 2 &&
                item.Operands[1] == coordinateId);
        AssertVectorTypeWidth(
            instructions,
            coordinate.Operands[0],
            expectedComponents);
    }

    private static void AssertVectorTypeWidth(
        IReadOnlyList<ParsedSpirvInstruction> instructions,
        uint vectorTypeId,
        uint expectedComponents)
    {
        var vectorType = Assert.Single(
            instructions,
            item =>
                item.Opcode == SpirvOp.TypeVector &&
                item.Operands[0] == vectorTypeId);
        Assert.Equal(expectedComponents, vectorType.Operands[2]);
    }

    private static bool ContainsSpirvConstant(byte[] spirv, uint value)
    {
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            if ((ushort)instruction == (ushort)SpirvOp.Constant &&
                wordCount >= 4 &&
                BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + 3 * sizeof(uint))) == value)
            {
                return true;
            }

            offset += wordCount * sizeof(uint);
        }

        return false;
    }

    private static Gen5ShaderProgram DecodeBvhProgram()
    {
        uint[] words =
        [
            0xF1989F07,
            0x00040303,
            0x43440D3F,
            0x46424140,
            0x00004847,
            SEndpgm,
        ];
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                shader[(index * sizeof(uint))..],
                words[index]);
        }

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

    private readonly record struct ParsedSpirvInstruction(
        SpirvOp Opcode,
        uint[] Operands);

    [Fact]
    public void Ps5OpmImageSampleUsesTheStandardSampleOperandLayout()
    {
        // GTA V pixel shader 0x142583000, PC 0x34:
        // IMAGE_SAMPLE_OPM v6, v[2:3], s[0:7], s[24:27], dmask:0x1, 2D.
        uint[] words =
        [
            0xF0800109,
            0x00C00602,
            SEndpgm,
        ];
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                shader[(index * sizeof(uint))..],
                words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, shader));
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var error),
            error);

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "ImageSampleOpm");
        Assert.Equal(Gen5ShaderEncoding.Mimg, instruction.Encoding);
        Assert.Equal(2, instruction.Words.Count);
        Assert.Equal(new[] { Gen5Operand.Vector(6) }, instruction.Destinations);
        Assert.Equal(
            new[]
            {
                Gen5Operand.Vector(2),
                Gen5Operand.Scalar(0),
                Gen5Operand.Scalar(24),
            },
            instruction.Sources);

        var control = Assert.IsType<Gen5ImageControl>(instruction.Control);
        Assert.Equal(1U, control.Dmask);
        Assert.Equal(1U, control.Dimension);
        Assert.Equal(2U, control.GetAddressRegister(0));
        Assert.Equal(3U, control.GetAddressRegister(1));
        Assert.Equal(6U, control.VectorData);
        Assert.Equal(0U, control.ScalarResource);
        Assert.Equal(24U, control.ScalarSampler);
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < baseAddress ||
                address - baseAddress > (ulong)_storage.Length ||
                destination.Length > _storage.Length - (int)(address - baseAddress))
            {
                return false;
            }

            _storage.AsSpan((int)(address - baseAddress), destination.Length)
                .CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            if (address < baseAddress ||
                address - baseAddress > (ulong)_storage.Length ||
                source.Length > _storage.Length - (int)(address - baseAddress))
            {
                return false;
            }

            source.CopyTo(
                _storage.AsSpan((int)(address - baseAddress), source.Length));
            return true;
        }
    }
}
