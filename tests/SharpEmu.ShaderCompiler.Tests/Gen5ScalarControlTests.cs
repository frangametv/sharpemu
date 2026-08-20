// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ScalarControlTests
{
    private const ulong ShaderAddress = 0x1_0000_8000;
    private const uint SEndpgm = 0xBF810000;

    [Theory]
    [InlineData(0x0000_0000u, 0xFFFF_FFFFu)]
    [InlineData(0x0000_0001u, 31u)]
    [InlineData(0x8000_0000u, 0u)]
    [InlineData(0x00F0_0000u, 8u)]
    public void SFlbitI32B32DecodesEvaluatesAndCompiles(uint input, uint expected)
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(shader, 0xBE821501u);
        BinaryPrimitives.WriteUInt32LittleEndian(shader[sizeof(uint)..], SEndpgm);
        Assert.True(memory.TryWrite(ShaderAddress, shader));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(ctx, ShaderAddress, out var program, out var error),
            error);
        var instruction = Assert.Single(program.Instructions, item => item.Opcode == "SFlbitI32B32");
        Assert.Equal([Gen5Operand.Scalar(1)], instruction.Sources);
        Assert.Equal([Gen5Operand.Scalar(2)], instruction.Destinations);

        var scalarRegisters = new uint[256];
        scalarRegisters[1] = input;
        var state = new Gen5ShaderState(program, scalarRegisters, null);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out error),
            error);
        Assert.Equal(expected, evaluation.ScalarRegisters[2]);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state, evaluation, 1, 1, 1, out _, out error),
            error);
    }

    [Theory]
    [InlineData(0x0000_0000u, 0x0000_0000u)]
    [InlineData(0x0000_002Au, 0x0000_002Au)]
    [InlineData(0xFFFF_FFD6u, 0x0000_002Au)]
    [InlineData(0x8000_0000u, 0x8000_0000u)]
    public void SAbsI32DecodesEvaluatesAndCompiles(uint input, uint expected)
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[2 * sizeof(uint)];
        // s_abs_i32 s2, s1 (SOP1 opcode 0x34).
        BinaryPrimitives.WriteUInt32LittleEndian(shader, 0xBE823401u);
        BinaryPrimitives.WriteUInt32LittleEndian(shader[sizeof(uint)..], SEndpgm);
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
            item => item.Opcode == "SAbsI32");
        Assert.Equal([Gen5Operand.Scalar(1)], instruction.Sources);
        Assert.Equal([Gen5Operand.Scalar(2)], instruction.Destinations);

        var scalarRegisters = new uint[256];
        scalarRegisters[1] = input;
        var state = new Gen5ShaderState(program, scalarRegisters, null);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out error),
            error);
        Assert.Equal(expected, evaluation.ScalarRegisters[2]);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out _,
                out error),
            error);
    }

    [Fact]
    public void STrapDecodesAndCompilesAsNoOpWithoutGuestGpuDebugger()
    {
        const uint trapId = 0x34;
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            shader,
            0xBF920000u | trapId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            shader[sizeof(uint)..],
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

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "STrap");
        Assert.Equal(Gen5ShaderEncoding.Sopp, instruction.Encoding);
        Assert.Equal(trapId, instruction.Words[0] & 0xFFFFu);

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
                out _,
                out error),
            error);
    }

    [Fact]
    public void SCbranchCdbgsysCompilesAsDebuggerDetachedFallthrough()
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[3 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            shader,
            0xBF970001u);
        BinaryPrimitives.WriteUInt32LittleEndian(
            shader[sizeof(uint)..],
            0xBF800000u);
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
        Assert.Equal("SCbranchCdbgsys", program.Instructions[0].Opcode);

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
                out _,
                out error),
            error);
    }

    [Fact]
    public void DeclaredShaderUsesConsistentBulkSnapshot()
    {
        const ulong headerAddress = ShaderAddress + 0x100;
        const uint computePgmRsrc2 = 0x213;
        const uint computeUserData = 0x240;
        const uint sNop = 0xBF800000;
        var backing = new TestCpuMemory(ShaderAddress, 0x200);
        Span<byte> shader = stackalloc byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(shader, sNop);
        BinaryPrimitives.WriteUInt32LittleEndian(shader[sizeof(uint)..], SEndpgm);
        Assert.True(backing.TryWrite(ShaderAddress, shader));

        Span<byte> size = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)shader.Length);
        Assert.True(backing.TryWrite(headerAddress + 0x44, size));

        var memory = new StaleSingleWordCpuMemory(
            backing,
            ShaderAddress + sizeof(uint),
            0x00000048);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var registers = new Dictionary<uint, uint>
        {
            [computePgmRsrc2] = 0,
        };
        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                headerAddress,
                registers,
                computeUserData,
                out var state,
                out var error),
            error);
        Assert.Equal(["SNop", "SEndpgm"], state.Program.Instructions.Select(
            static instruction => instruction.Opcode));
    }

    [Fact]
    public void DeclaredShaderRetriesTransientPartialSnapshot()
    {
        const ulong headerAddress = ShaderAddress + 0x100;
        const uint computePgmRsrc2 = 0x213;
        const uint computeUserData = 0x240;
        const uint sNop = 0xBF800000;
        var backing = new TestCpuMemory(ShaderAddress, 0x200);
        Span<byte> shader = stackalloc byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(shader, sNop);
        BinaryPrimitives.WriteUInt32LittleEndian(shader[sizeof(uint)..], SEndpgm);
        Assert.True(backing.TryWrite(ShaderAddress, shader));

        Span<byte> size = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)shader.Length);
        Assert.True(backing.TryWrite(headerAddress + 0x44, size));

        var memory = new TransientPartialSnapshotCpuMemory(
            backing,
            ShaderAddress,
            shader.Length,
            sizeof(uint),
            0x00000048,
            transientReadCount: 4);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var registers = new Dictionary<uint, uint>
        {
            [computePgmRsrc2] = 0,
        };
        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                headerAddress,
                registers,
                computeUserData,
                out var state,
                out var error),
            error);
        Assert.Equal(5, memory.ProgramReadCount);
        Assert.Equal(["SNop", "SEndpgm"], state.Program.Instructions.Select(
            static instruction => instruction.Opcode));
    }

    [Fact]
    public void ProgramLongerThan4096InstructionsReachesEndProgram()
    {
        const int nopCount = 4096;
        const uint sNop = 0xBF800000;
        var shader = new byte[(nopCount + 1) * sizeof(uint)];
        for (var index = 0; index < nopCount; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                shader.AsSpan(index * sizeof(uint), sizeof(uint)),
                sNop);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            shader.AsSpan(nopCount * sizeof(uint), sizeof(uint)),
            SEndpgm);
        var memory = new TestCpuMemory(ShaderAddress, shader.Length);
        Assert.True(memory.TryWrite(ShaderAddress, shader));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var error),
            error);
        Assert.Equal(nopCount + 1, program.Instructions.Count);
        Assert.Equal("SEndpgm", program.Instructions[^1].Opcode);
    }

    [Fact]
    public void ProgramCacheIncludesDeclaredShaderSize()
    {
        const ulong headerAddress = ShaderAddress + 0x100;
        const uint computePgmRsrc2 = 0x213;
        const uint computeUserData = 0x240;
        const uint sNop = 0xBF800000;
        var memory = new TestCpuMemory(ShaderAddress, 0x200);
        Span<byte> shader = stackalloc byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(shader, sNop);
        BinaryPrimitives.WriteUInt32LittleEndian(shader[sizeof(uint)..], SEndpgm);
        Assert.True(memory.TryWrite(ShaderAddress, shader));

        Span<byte> size = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)shader.Length);
        Assert.True(memory.TryWrite(headerAddress + 0x44, size));

        var ctx = new CpuContext(memory, Generation.Gen5);
        var registers = new Dictionary<uint, uint>
        {
            [computePgmRsrc2] = 0,
        };
        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                headerAddress,
                registers,
                computeUserData,
                out _,
                out var error),
            error);

        BinaryPrimitives.WriteUInt32LittleEndian(size, sizeof(uint));
        Assert.True(memory.TryWrite(headerAddress + 0x44, size));
        Assert.False(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                headerAddress,
                registers,
                computeUserData,
                out _,
                out error));
        Assert.Contains("unterminated", error);
        Assert.Contains("size=0x4", error);
    }

    [Fact]
    public void SetpcB64EndsStandaloneProgramBeforeTrailingDataAndCompiles()
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[4 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(shader, 0xBE802006u); // s_setpc_b64 s[6:7]
        BinaryPrimitives.WriteUInt32LittleEndian(shader[sizeof(uint)..], 0x30306C73u); // trailing data
        BinaryPrimitives.WriteUInt32LittleEndian(shader[(2 * sizeof(uint))..], 0x00000048u);
        BinaryPrimitives.WriteUInt32LittleEndian(shader[(3 * sizeof(uint))..], 0x00000061u);
        Assert.True(memory.TryWrite(ShaderAddress, shader));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var error),
            error);
        Assert.Equal("SSetpcB64", Assert.Single(program.Instructions).Opcode);

        var state = new Gen5ShaderState(program, new uint[256], null);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out error),
            error);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out _,
                out error),
            error);
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

    private sealed class StaleSingleWordCpuMemory(
        ICpuMemory inner,
        ulong staleAddress,
        uint staleWord) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address == staleAddress && destination.Length == sizeof(uint))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(destination, staleWord);
                return true;
            }

            return inner.TryRead(address, destination);
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) =>
            inner.TryWrite(address, source);
    }

    private sealed class TransientPartialSnapshotCpuMemory(
        ICpuMemory inner,
        ulong programAddress,
        int programLength,
        int staleOffset,
        uint staleWord,
        int transientReadCount) : ICpuMemory
    {
        public int ProgramReadCount { get; private set; }

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (!inner.TryRead(address, destination))
            {
                return false;
            }

            if (address == programAddress && destination.Length == programLength)
            {
                ProgramReadCount++;
                if (ProgramReadCount <= transientReadCount)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        destination[staleOffset..],
                        staleWord);
                }
            }

            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) =>
            inner.TryWrite(address, source);
    }
}
