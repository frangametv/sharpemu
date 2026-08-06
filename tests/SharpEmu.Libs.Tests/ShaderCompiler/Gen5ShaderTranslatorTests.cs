// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.ShaderCompiler;

public sealed class Gen5ShaderTranslatorTests
{
    private const ulong ProgramAddress = 0x1_0000_0000;

    [Fact]
    public void CombinedShader_FollowsSetpcHandoffWithoutDecodingTrailingMetadata()
    {
        const uint shaderSize = 0x20;
        var entryHeader = ProgramAddress + 0x100;
        var continuationHeader = ProgramAddress + 0x200;
        var combinedHeader = ProgramAddress + 0x300;
        var entryAddress = ProgramAddress + 0x1000;
        var continuationAddress = ProgramAddress + 0x1700;
        var memory = new FakeCpuMemory(ProgramAddress, 0x4000);
        WriteUInt32(memory, entryHeader + 0x44, shaderSize);
        WriteUInt32(memory, continuationHeader + 0x44, shaderSize);
        WriteUInt32(memory, combinedHeader + 0x44, shaderSize);

        WriteWords(
            memory,
            entryAddress,
            0xBF800000u, // s_nop 0
            0xBE802006u, // s_setpc_b64 s[6:7]
            0x30306C73u, // "sl00" metadata, not an instruction
            0x00000048u);
        WriteWords(
            memory,
            continuationAddress,
            0xBF800000u, // s_nop 0
            0xBF810000u); // s_endpgm

        var context = new CpuContext(memory, Generation.Gen5);
        Assert.False(Gen5ShaderTranslator.IsCombinedShader(context, entryAddress));
        Gen5ShaderTranslator.RegisterCombinedShader(
            context,
            entryAddress,
            entryHeader,
            continuationAddress,
            continuationHeader);
        Assert.True(Gen5ShaderTranslator.IsCombinedShader(context, entryAddress));

        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                context,
                entryAddress,
                combinedHeader,
                new Dictionary<uint, uint> { [0xCB] = 0 },
                0xCC,
                out var state,
                out var error),
            error);

        Assert.Equal(
            new[] { "SNop", "SNop", "SNop", "SEndpgm" },
            state.Program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            new uint[] { 0, 4, 0x700, 0x704 },
            state.Program.Instructions.Select(instruction => instruction.Pc));
        Assert.DoesNotContain(
            state.Program.Instructions,
            instruction => instruction.Words.Contains(0x30306C73u));
    }

    [Theory]
    [InlineData(0xD7600005u, 5u)]
    [InlineData(0xD7600065u, 101u)]
    public void VReadlaneB32DecodesScalarDestinationFromVdstByte(
        uint instructionWord,
        uint expectedDestination)
    {
        var memory = new FakeCpuMemory(ProgramAddress, 0x100);
        Span<byte> code = stackalloc byte[3 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(code, instructionWord);
        BinaryPrimitives.WriteUInt32LittleEndian(code[sizeof(uint)..], 0x02000501u);
        BinaryPrimitives.WriteUInt32LittleEndian(code[(2 * sizeof(uint))..], 0xBF810000u);
        Assert.True(memory.TryWrite(ProgramAddress, code));

        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ProgramAddress,
                out var program,
                out var error),
            error);

        var instruction = Assert.Single(
            program.Instructions,
            static item => item.Opcode == "VReadlaneB32");
        Assert.Equal(Gen5ShaderEncoding.Vop3, instruction.Encoding);

        var destination = Assert.Single(instruction.Destinations);
        Assert.Equal(Gen5OperandKind.ScalarRegister, destination.Kind);
        Assert.Equal(expectedDestination, destination.Value);

        Assert.Equal(Gen5OperandKind.VectorRegister, instruction.Sources[0].Kind);
        Assert.Equal(1u, instruction.Sources[0].Value);
        Assert.Equal(Gen5OperandKind.ScalarRegister, instruction.Sources[1].Kind);
        Assert.Equal(2u, instruction.Sources[1].Value);
    }

    [Fact]
    public void SBcnt1I32B64_DecodesScalarPairSourceAndScalarDestination()
    {
        var memory = new FakeCpuMemory(ProgramAddress, 0x100);
        WriteWords(memory, ProgramAddress, 0xBEEB106Au, 0xBF810000u);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ProgramAddress,
                out var program,
                out var error),
            error);

        var instruction = program.Instructions[0];
        Assert.Equal("SBcnt1I32B64", instruction.Opcode);
        Assert.Equal(new[] { Gen5Operand.Scalar(106) }, instruction.Sources);
        Assert.Equal(new[] { Gen5Operand.Scalar(107) }, instruction.Destinations);
    }

    [Fact]
    public void SWaitcntVscnt_DecodesNullAsSourceWithoutDestination()
    {
        var memory = new FakeCpuMemory(ProgramAddress, 0x100);
        WriteWords(memory, ProgramAddress, 0xBBFD0000u, 0xBF810000u);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ProgramAddress,
                out var program,
                out var error),
            error);

        var instruction = program.Instructions[0];
        Assert.Equal("SWaitcntVscnt", instruction.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Sopk, instruction.Encoding);
        Assert.Equal(
            new[]
            {
                Gen5Operand.Scalar(125),
                new Gen5Operand(Gen5OperandKind.EncodedConstant, 0),
            },
            instruction.Sources);
        Assert.Empty(instruction.Destinations);
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteWords(
        FakeCpuMemory memory,
        ulong address,
        params uint[] words)
    {
        foreach (var word in words)
        {
            WriteUInt32(memory, address, word);
            address += sizeof(uint);
        }
    }
}
