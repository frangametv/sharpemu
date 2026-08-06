// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5NggSpirvTests
{
    [Fact]
    public void NggComputeAndRasterShadersBuildWithSharedOutputLayout()
    {
        var program = new Gen5ShaderProgram(
            0x5007_04F00,
            [
                new Gen5ShaderInstruction(
                    0,
                    Gen5ShaderEncoding.Sopp,
                    "SBarrier",
                    [],
                    [],
                    [],
                    null),
                Export(4, 20, 12, 0x1),
                Export(8, 12, 0, 0xF),
                Export(12, 32, 4, 0xF),
                Export(16, 33, 8, 0xF),
                new Gen5ShaderInstruction(
                    20,
                    Gen5ShaderEncoding.Sopp,
                    "SEndpgm",
                    [],
                    [],
                    [],
                    null),
            ]);
        var registers = new uint[256];
        registers[3] = 0x1000_0101;
        var state = new Gen5ShaderState(program, [], null);
        var evaluation = new Gen5ShaderEvaluation(
            registers,
            registers,
            [],
            []);
        var layout = new Gen5NggOutputLayout(
            BufferIndex: 0,
            MaximumPrimitiveCount: 64,
            MaximumVertexCount: 64,
            ParameterCount: 2);

        Assert.Equal(4864, layout.ByteLength);
        Assert.True(
            Gen5SpirvTranslator.TryCompileNggComputeShader(
                state,
                evaluation,
                layout,
                out var compute,
                out var error,
                globalBufferBase: 0,
                totalGlobalBufferCount: 1,
                imageBindingBase: 0),
            error);
        var raster = SpirvFixedShaders.CreateNggRasterVertex(
            layout,
            totalGlobalBufferCount: 1,
            pixelInputControls: [0, 1]);

        Assert.Equal(0x0723_0203u, BinaryPrimitives.ReadUInt32LittleEndian(compute.Spirv));
        Assert.Equal(0x0723_0203u, BinaryPrimitives.ReadUInt32LittleEndian(raster));
        Assert.Contains((ushort)SpirvOp.ControlBarrier, CollectOpcodes(compute.Spirv));
        Assert.Contains((ushort)SpirvOp.AccessChain, CollectOpcodes(raster));
    }

    [Fact]
    public void NggComputeCombinesSingleDataShareOffsetBytes()
    {
        var program = new Gen5ShaderProgram(
            0x5007_04F00,
            [
                new Gen5ShaderInstruction(
                    0,
                    Gen5ShaderEncoding.Ds,
                    "DsWriteB32",
                    [],
                    [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
                    [],
                    new Gen5DataShareControl(0, 0x29, Gds: false)),
                new Gen5ShaderInstruction(
                    8,
                    Gen5ShaderEncoding.Sopp,
                    "SEndpgm",
                    [],
                    [],
                    [],
                    null),
            ]);
        var registers = new uint[256];
        var layout = new Gen5NggOutputLayout(0, 64, 64, 0);

        Assert.True(
            Gen5SpirvTranslator.TryCompileNggComputeShader(
                new Gen5ShaderState(program, [], null),
                new Gen5ShaderEvaluation(registers, registers, [], []),
                layout,
                out var compute,
                out var error,
                globalBufferBase: 0,
                totalGlobalBufferCount: 1,
                imageBindingBase: 0),
            error);

        Assert.True(ContainsConstant(compute.Spirv, 0x2900));
    }

    private static Gen5ShaderInstruction Export(
        uint pc,
        uint target,
        uint sourceBase,
        uint enableMask) => new(
        pc,
        Gen5ShaderEncoding.Exp,
        "Exp",
        [],
        [
            Gen5Operand.Vector(sourceBase),
            Gen5Operand.Vector(sourceBase + 1),
            Gen5Operand.Vector(sourceBase + 2),
            Gen5Operand.Vector(sourceBase + 3),
        ],
        [],
        new Gen5ExportControl(
            target,
            enableMask,
            Compressed: false,
            Done: false,
            ValidMask: true));

    private static HashSet<ushort> CollectOpcodes(byte[] spirv)
    {
        var opcodes = new HashSet<ushort>();
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            opcodes.Add((ushort)word);
            offset += Math.Max((int)(word >> 16), 1) * sizeof(uint);
        }

        return opcodes;
    }

    private static bool ContainsConstant(byte[] spirv, uint expected)
    {
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            var wordCount = Math.Max((int)(word >> 16), 1);
            if ((ushort)word == (ushort)SpirvOp.Constant && wordCount >= 4 &&
                BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + (3 * sizeof(uint)), sizeof(uint))) == expected)
            {
                return true;
            }

            offset += wordCount * sizeof(uint);
        }

        return false;
    }
}
