// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ComputeVgprCaptureTests
{
    private const ulong ShaderAddress = 0x1_0000_1234;
    private const ushort OpStore = 62;

    private static readonly string[] CaptureEnvironmentVariables =
    [
        "SHARPEMU_CAPTURE_COMPUTE_VGPR_ADDRESS",
        "SHARPEMU_CAPTURE_COMPUTE_VGPR_PC",
        "SHARPEMU_CAPTURE_COMPUTE_VGPR_SOURCES",
        "SHARPEMU_CAPTURE_COMPUTE_VGPR_DEST_BASE",
        "SHARPEMU_CAPTURE_COMPUTE_VGPR_IGNORE_EXEC",
    ];

    [Fact]
    public void ComputeVgprCaptureRequiresMatchingAddressAndPcAndCopiesFourSources()
    {
        var originalEnvironment = CaptureEnvironmentVariables.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable);

        try
        {
            ClearCaptureEnvironment();
            var baselineOpcodes = CompileAndReadSpirvOpcodes();

            Environment.SetEnvironmentVariable(
                "SHARPEMU_CAPTURE_COMPUTE_VGPR_ADDRESS",
                $"0x{ShaderAddress:X}");
            Environment.SetEnvironmentVariable(
                "SHARPEMU_CAPTURE_COMPUTE_VGPR_PC",
                "0x0");
            Environment.SetEnvironmentVariable(
                "SHARPEMU_CAPTURE_COMPUTE_VGPR_SOURCES",
                "v0,u1,v2,exec");
            Environment.SetEnvironmentVariable(
                "SHARPEMU_CAPTURE_COMPUTE_VGPR_DEST_BASE",
                "240");
            Environment.SetEnvironmentVariable(
                "SHARPEMU_CAPTURE_COMPUTE_VGPR_IGNORE_EXEC",
                "1");

            var capturedOpcodes = CompileAndReadSpirvOpcodes();
            Assert.Equal(
                baselineOpcodes.Count(opcode => opcode == OpStore) + 4,
                capturedOpcodes.Count(opcode => opcode == OpStore));

            Environment.SetEnvironmentVariable(
                "SHARPEMU_CAPTURE_COMPUTE_VGPR_PC",
                "0x4");
            var pcMismatchOpcodes = CompileAndReadSpirvOpcodes();
            Assert.Equal(
                baselineOpcodes.Count(opcode => opcode == OpStore),
                pcMismatchOpcodes.Count(opcode => opcode == OpStore));

            Environment.SetEnvironmentVariable(
                "SHARPEMU_CAPTURE_COMPUTE_VGPR_PC",
                "0x0");
            Environment.SetEnvironmentVariable(
                "SHARPEMU_CAPTURE_COMPUTE_VGPR_ADDRESS",
                "0x100001235");
            var addressMismatchOpcodes = CompileAndReadSpirvOpcodes();
            Assert.Equal(
                baselineOpcodes.Count(opcode => opcode == OpStore),
                addressMismatchOpcodes.Count(opcode => opcode == OpStore));
        }
        finally
        {
            foreach (var (name, value) in originalEnvironment)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private static IReadOnlyList<ushort> CompileAndReadSpirvOpcodes()
    {
        var program = new Gen5ShaderProgram(
            ShaderAddress,
            [
                new Gen5ShaderInstruction(
                    0,
                    Gen5ShaderEncoding.Sopp,
                    "SNop",
                    [],
                    [],
                    [],
                    null),
                new Gen5ShaderInstruction(
                    4,
                    Gen5ShaderEncoding.Sopp,
                    "SEndpgm",
                    [],
                    [],
                    [],
                    null),
            ]);
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

    private static void ClearCaptureEnvironment()
    {
        foreach (var name in CaptureEnvironmentVariables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
