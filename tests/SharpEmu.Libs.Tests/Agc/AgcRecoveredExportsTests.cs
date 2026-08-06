// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Kernel;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcRecoveredExportsTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int IncompatiblePair = unchecked((int)0x8A6C0008);
    private const int DescriptorSize = 0x60;

    [Theory]
    [InlineData(0x8Bu, 0x8Cu, 8u)]
    [InlineData(0xCBu, 0xCCu, 0u)]
    [InlineData(0x4Bu, 0x4Cu, 0u)]
    public void DecodeExportUserDataLayout_UsesStageHardwareSgprBase(
        uint resource2Register,
        uint expectedUserDataRegister,
        uint expectedScalarRegisterBase)
    {
        var registers = new Dictionary<uint, uint>
        {
            [resource2Register] = 3u << 1,
        };

        var decoded = AgcExports.DecodeExportUserDataLayout(registers);

        Assert.Equal(expectedUserDataRegister, decoded.UserDataRegister);
        Assert.Equal(expectedScalarRegisterBase, decoded.ScalarRegisterBase);
    }

    [Fact]
    public void NggComputeRaster_RequiresExplicitOptIn()
    {
        Assert.False(AgcExports.IsNggComputeRasterEnabled(null));
        Assert.False(AgcExports.IsNggComputeRasterEnabled("0"));
        Assert.False(AgcExports.IsNggComputeRasterEnabled("true"));
        Assert.True(AgcExports.IsNggComputeRasterEnabled("1"));
    }

    [Fact]
    public void DecodeNggGraphicsSystemRegisters_UsesIndirectGsAddressPair()
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x82] = 0x89AB_CDEF,
            [0x83] = 0x0000_0005,
        };

        var decoded = AgcExports.DecodeNggGraphicsSystemRegisters(registers);

        Assert.True(decoded.HasValue);
        Assert.Equal(0x0000_0005_89AB_CDEFUL, decoded.Value.IndirectUserDataAddress);
        Assert.Null(
            AgcExports.DecodeNggGraphicsSystemRegisters(
                new Dictionary<uint, uint> { [0x82] = 0x89AB_CDEF }));
        Assert.Null(
            AgcExports.DecodeNggGraphicsSystemRegisters(
                new Dictionary<uint, uint> { [0x82] = 0, [0x83] = 0 }));

        var merged = AgcExports.DecodeNggGraphicsSystemRegisters(
            new Dictionary<uint, uint>(),
            mergedWaveInfo: 0x1000_0101);
        Assert.True(merged.HasValue);
        var scalarRegisters = new uint[8];
        merged.Value.Apply(scalarRegisters);
        Assert.Equal(0x1000_0101u, scalarRegisters[3]);
    }

    [Theory]
    [InlineData(1u, 1u, 0x1000_0101u)]
    [InlineData(4u, 3u, 0x1000_0103u)]
    public void EncodeNggMergedWaveInfo_PacksThreadCounts(
        uint primitiveType,
        uint vertexCount,
        uint expected)
    {
        Assert.Equal(
            expected,
            AgcExports.EncodeNggMergedWaveInfo(primitiveType, vertexCount));
    }

    [Fact]
    public void DriverRegisterOwnerReturnsExactProviderConstantWithoutTouchingGuestMemory()
    {
        var context = new CpuContext(new FakeCpuMemory(BaseAddress, 1), Generation.Gen5);
        context[CpuRegister.Rdi] = 0xDEAD_BEEF;
        context[CpuRegister.Rsi] = 0xFEED_FACE;

        Assert.Equal(unchecked((int)0x8A6C9018), AgcExports.DriverRegisterOwner(context));
        Assert.Equal(unchecked((ulong)unchecked((int)0x8A6C9018)), context[CpuRegister.Rax]);
    }

    [Fact]
    public void DcbJumpGetSize_MatchesFirmware1270AndRegistersAsLlePreferred()
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x100), Generation.Gen5);

        Assert.Equal(0x10, AgcExports.DcbJumpGetSize(ctx));
        Assert.Equal(0x10UL, ctx[CpuRegister.Rax]);

        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == "VEGu4dixjUg");
        Assert.Equal("sceAgcDcbJumpGetSize", export.Name);
        Assert.Equal("libSceAgc", export.LibraryName);
        Assert.True(export.PreferLle);
    }

    [Fact]
    public void RefillGetSizeHandlers_MatchFirmware1270()
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x100), Generation.Gen5);

        Assert.Equal(0x20, AgcExports.AcbAcquireMemGetSize(ctx));
        Assert.Equal(0x20UL, ctx[CpuRegister.Rax]);
        Assert.Equal(0x20, AgcExports.DcbAcquireMemGetSize(ctx));
        Assert.Equal(0x20UL, ctx[CpuRegister.Rax]);
        Assert.Equal(0x20, AgcExports.CbQueueEndOfPipeActionGetSize(ctx));
        Assert.Equal(0x20UL, ctx[CpuRegister.Rax]);
        Assert.Equal(8, AgcExports.DcbRewindGetSize(ctx));
        Assert.Equal(8UL, ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rdi] = 9;
        Assert.Equal(0x24, AgcExports.CbNopGetSize(ctx));
        Assert.Equal(0x24UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void GetIsTrinityMode_WritesOneZeroByteAndPreservesRax()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x100);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Span<byte> sentinel = stackalloc byte[] { 0xAA, 0xBB };
        Assert.True(memory.TryWrite(BaseAddress + 0x20, sentinel));
        ctx[CpuRegister.Rdi] = BaseAddress + 0x20;
        ctx[CpuRegister.Rax] = 0x1122_3344_5566_7788;
        ctx.ClearRaxWriteFlag();

        Assert.Equal(0, AgcExports.GetIsTrinityMode(ctx));

        Span<byte> actual = stackalloc byte[2];
        Assert.True(memory.TryRead(BaseAddress + 0x20, actual));
        Assert.Equal(new byte[] { 0, 0xBB }, actual.ToArray());
        Assert.True(ctx.WasRaxWritten);
        Assert.Equal(0x1122_3344_5566_7788UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void GetIsTrinityMode_ModuleDispatchPreservesIncomingRax()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x100);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        ctx[CpuRegister.Rdi] = BaseAddress + 0x20;
        ctx[CpuRegister.Rax] = 0x8877_6655_4433_2211;

        Assert.True(
            manager.TryDispatch("BfBDZGbti7A", ctx, out var result));

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0x8877_6655_4433_2211UL, ctx[CpuRegister.Rax]);
        Assert.True(ctx.WasRaxWritten);
        Assert.Equal(0, ReadByte(memory, BaseAddress + 0x20));
    }

    [Fact]
    public void CreatePrimState_MergesHullAndGeometrySpecialRegisters()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var cxRegisters = BaseAddress + 0x100;
        var ucRegisters = BaseAddress + 0x200;
        var hullShader = BaseAddress + 0x300;
        var geometryShader = BaseAddress + 0x400;
        var hullSpecials = BaseAddress + 0x600;
        var geometrySpecials = BaseAddress + 0x800;
        WriteUInt64(memory, hullShader + 0x28, hullSpecials);
        WriteUInt64(memory, geometryShader + 0x28, geometrySpecials);
        WriteRegisters(
            memory,
            geometrySpecials,
            (0x25B, 0x1111_1111),
            (0x2D5, 0x0000_0000),
            (0, 0),
            (0, 0),
            (0x2D6, 0xAAAA_AAAA),
            (0x262, 0xBBBB_BBBB));
        WriteRegisters(
            memory,
            hullSpecials,
            (0x25B, 0x2222_2222),
            (0x2D5, 0x0000_0010),
            (0, 0),
            (0, 0),
            (0x2D6, 0xCCCC_CCCC),
            (0x262, 0xDDDD_DDDD));
        ctx[CpuRegister.Rdi] = cxRegisters;
        ctx[CpuRegister.Rsi] = ucRegisters;
        ctx[CpuRegister.Rdx] = hullShader;
        ctx[CpuRegister.Rcx] = geometryShader;
        ctx[CpuRegister.R8] = 17;

        Assert.Equal(0, AgcExports.CreatePrimState(ctx));

        AssertRegister(memory, cxRegisters, 0, 0x2D5, 0x10);
        AssertRegister(memory, cxRegisters, 1, 0x2D6, 0xCCCC_CCCC);
        AssertRegister(memory, ucRegisters, 0, 0x25B, 0x1111_1111);
        AssertRegister(memory, ucRegisters, 1, 0x262, 0xDDDD_DDDD);
        AssertRegister(memory, ucRegisters, 2, 0x242, 17);
    }

    [Fact]
    public void CreatePrimState_HullMergeRetainsGeometryOutputPrimitiveWhenGsBitIsSet()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var cxRegisters = BaseAddress + 0x100;
        var hullShader = BaseAddress + 0x300;
        var geometryShader = BaseAddress + 0x400;
        var hullSpecials = BaseAddress + 0x600;
        var geometrySpecials = BaseAddress + 0x800;
        WriteUInt64(memory, hullShader + 0x28, hullSpecials);
        WriteUInt64(memory, geometryShader + 0x28, geometrySpecials);
        WriteRegisters(
            memory,
            geometrySpecials,
            (0x25B, 1),
            (0x2D5, 0x20),
            (0, 0),
            (0, 0),
            (0x2D6, 0x1234_5678),
            (0x262, 2));
        WriteRegisters(
            memory,
            hullSpecials,
            (0x25B, 3),
            (0x2D5, 0x04),
            (0, 0),
            (0, 0),
            (0x2D6, 0x8765_4321),
            (0x262, 4));
        ctx[CpuRegister.Rdi] = cxRegisters;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = hullShader;
        ctx[CpuRegister.Rcx] = geometryShader;
        ctx[CpuRegister.R8] = 7;

        Assert.Equal(0, AgcExports.CreatePrimState(ctx));

        AssertRegister(memory, cxRegisters, 0, 0x2D5, 0x24);
        AssertRegister(memory, cxRegisters, 1, 0x2D6, 0x1234_5678);
    }

    [Theory]
    [InlineData(0u, 2u)]
    [InlineData(1u, 0u)]
    [InlineData(2u, 1u)]
    [InlineData(7u, 3u)]
    [InlineData(17u, 4u)]
    [InlineData(18u, 1u)]
    [InlineData(19u, 2u)]
    public void CreatePrimState_MapsDefaultOutputPrimitiveLikeFirmware(
        uint primitiveType,
        uint expectedOutputPrimitive)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var cxRegisters = BaseAddress + 0x100;
        var geometryShader = BaseAddress + 0x200;
        var geometrySpecials = BaseAddress + 0x400;
        WriteUInt64(memory, geometryShader + 0x28, geometrySpecials);
        WriteRegisters(
            memory,
            geometrySpecials,
            (0x25B, 1),
            (0x2D5, 0),
            (0, 0),
            (0, 0),
            (0x2D6, 0xFFFF_FFFF),
            (0x262, 2));
        ctx[CpuRegister.Rdi] = cxRegisters;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = geometryShader;
        ctx[CpuRegister.R8] = primitiveType;

        Assert.Equal(0, AgcExports.CreatePrimState(ctx));

        AssertRegister(memory, cxRegisters, 1, 0x29B, expectedOutputPrimitive);
    }

    [Fact]
    public void CreatePrimState_NoOutputsReturnsSuccessWithoutReadingShaders()
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x100), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = ulong.MaxValue;
        ctx[CpuRegister.Rcx] = ulong.MaxValue;

        Assert.Equal(0, AgcExports.CreatePrimState(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void CreatePrimState_InaccessibleShaderReturnsMemoryFault()
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x100), Generation.Gen5);
        ctx[CpuRegister.Rdi] = BaseAddress;
        ctx[CpuRegister.Rcx] = ulong.MaxValue;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AgcExports.CreatePrimState(ctx));
    }

    [Fact]
    public void CreateShader_WritesDescriptorToLibcBackedDestination()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x4000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var header = BaseAddress + 0x100;
        var registers = BaseAddress + 0x400;
        var code = BaseAddress + 0x1000;
        Span<byte> descriptor = stackalloc byte[DescriptorSize];
        descriptor.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 0x3433_3231);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], 0x18);
        BinaryPrimitives.WriteUInt64LittleEndian(
            descriptor[0x20..],
            registers - (header + 0x20));
        descriptor[0x5A] = 0;
        descriptor[0x5C] = 2;
        Assert.True(memory.TryWrite(header, descriptor));
        WriteRegisters(memory, registers, (0x20C, 0), (0x20D, 0));

        var output = AllocateTracked(ctx, sizeof(ulong));
        try
        {
            Marshal.WriteInt64(unchecked((nint)output), 0);
            ctx[CpuRegister.Rdi] = output;
            ctx[CpuRegister.Rsi] = header;
            ctx[CpuRegister.Rdx] = code;

            Assert.Equal(0, AgcExports.CreateShader(ctx));
            Assert.Equal(
                header,
                unchecked((ulong)Marshal.ReadInt64(unchecked((nint)output))));
            Assert.Equal(code, ReadUInt64(memory, header + 0x10));
            Assert.Equal(registers, ReadUInt64(memory, header + 0x20));
            AssertRegisterValue(memory, registers, 0, unchecked((uint)(code >> 8)));
            AssertRegisterValue(memory, registers, 1, unchecked((uint)(code >> 40)));
        }
        finally
        {
            FreeTracked(ctx, output);
        }
    }

    [Fact]
    public void CreateShader_RegistersAdjacentGeometryPairForSetpcHandoff()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x4000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var entryHeader = BaseAddress + 0x100;
        var continuationHeader = BaseAddress + 0x300;
        var entryRegisters = BaseAddress + 0x600;
        var continuationRegisters = BaseAddress + 0x700;
        var entrySpecials = BaseAddress + 0x800;
        var continuationSpecials = BaseAddress + 0x900;
        var entryCode = BaseAddress + 0x1000;
        var continuationCode = BaseAddress + 0x1700;
        WriteCreateShaderDescriptor(
            memory,
            entryHeader,
            type: 4,
            entryRegisters,
            entrySpecials,
            shaderSize: 0x20);
        WriteCreateShaderDescriptor(
            memory,
            continuationHeader,
            type: 6,
            continuationRegisters,
            continuationSpecials,
            shaderSize: 0x20);
        WriteRegisters(memory, entryRegisters, (0x8A, 0), (0x8B, 0));
        WriteRegisters(memory, continuationRegisters, (0xC8, 0), (0xC9, 0));
        WriteUInt64(memory, entrySpecials + 8, 0);
        WriteUInt64(memory, continuationSpecials + 8, 0);
        WriteProgram(
            memory,
            entryCode,
            0xBF800000u,
            0xBE802006u,
            0x30306C73u,
            0x00000048u);
        WriteProgram(
            memory,
            continuationCode,
            0xBF800000u,
            0xBF810000u);

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = entryHeader;
        ctx[CpuRegister.Rdx] = entryCode;
        Assert.Equal(0, AgcExports.CreateShader(ctx));
        ctx[CpuRegister.Rsi] = continuationHeader;
        ctx[CpuRegister.Rdx] = continuationCode;
        Assert.Equal(0, AgcExports.CreateShader(ctx));

        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                entryCode,
                out var program,
                out var error),
            error);
        Assert.Equal(
            new[] { "SNop", "SNop", "SNop", "SEndpgm" },
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(
            new uint[] { 0, 4, 0x700, 0x704 },
            program.Instructions.Select(instruction => instruction.Pc));
    }

    [Theory]
    [InlineData(4, 0x8Au, 0x8Bu)]
    [InlineData(5, 0x10Au, 0x10Bu)]
    public void CreateShader_CombinedShaderFirstHalfSkipsProgramRelocation(
        byte shaderType,
        uint firstRegister,
        uint secondRegister)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x4000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var output = BaseAddress + 0x80;
        var header = BaseAddress + 0x100;
        var registers = BaseAddress + 0x400;
        const ulong code = 0x0000_12AB_CDEF_1200;
        Span<byte> descriptor = stackalloc byte[DescriptorSize];
        descriptor.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 0x3433_3231);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], 0x18);
        BinaryPrimitives.WriteUInt64LittleEndian(
            descriptor[0x20..],
            registers - (header + 0x20));
        descriptor[0x5A] = shaderType;
        descriptor[0x5C] = 2;
        Assert.True(memory.TryWrite(header, descriptor));
        WriteRegisters(
            memory,
            registers,
            (firstRegister, 0x1122_3344),
            (secondRegister, 0x5566_7788));
        ctx[CpuRegister.Rdi] = output;
        ctx[CpuRegister.Rsi] = header;
        ctx[CpuRegister.Rdx] = code;

        Assert.Equal(0, AgcExports.CreateShader(ctx));

        Assert.Equal(header, ReadUInt64(memory, output));
        Assert.Equal(code, ReadUInt64(memory, header + 0x10));
        Assert.Equal(registers, ReadUInt64(memory, header + 0x20));
        AssertRegisterValue(memory, registers, 0, 0x1122_3344);
        AssertRegisterValue(memory, registers, 1, 0x5566_7788);
    }

    [Fact]
    public void CreateShader_ScansRegisterTableAndAddsCodeBase()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x4000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var output = BaseAddress + 0x80;
        var header = BaseAddress + 0x100;
        var registers = BaseAddress + 0x400;
        const ulong code = 0x0000_12AB_CDEF_1200;
        const ulong relativeAddress = 0x2000;
        var relocatedAddress = code + relativeAddress;
        Span<byte> descriptor = stackalloc byte[DescriptorSize];
        descriptor.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 0x3433_3231);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], 0x18);
        BinaryPrimitives.WriteUInt64LittleEndian(
            descriptor[0x20..],
            registers - (header + 0x20));
        descriptor[0x5A] = 0;
        descriptor[0x5C] = 4;
        Assert.True(memory.TryWrite(header, descriptor));
        WriteRegisters(
            memory,
            registers,
            (0x210, 0x1111_1111),
            (0x211, 0x2222_2222),
            (0x20C, (uint)(relativeAddress >> 8)),
            (0x20D, 0xAABB_CC00));
        ctx[CpuRegister.Rdi] = output;
        ctx[CpuRegister.Rsi] = header;
        ctx[CpuRegister.Rdx] = code;

        Assert.Equal(0, AgcExports.CreateShader(ctx));

        Assert.Equal(header, ReadUInt64(memory, output));
        AssertRegisterValue(memory, registers, 0, 0x1111_1111);
        AssertRegisterValue(memory, registers, 1, 0x2222_2222);
        AssertRegisterValue(memory, registers, 2, (uint)(relocatedAddress >> 8));
        AssertRegisterValue(
            memory,
            registers,
            3,
            0xAABB_CC00u | (byte)(relocatedAddress >> 40));
    }

    [Theory]
    [InlineData(4, 6)]
    [InlineData(5, 7)]
    public void UnknownStorageSize_AcceptsBothRecoveredPairs(
        byte firstType,
        byte secondType)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var output = BaseAddress + 0x100;
        var first = BaseAddress + 0x200;
        var second = BaseAddress + 0x300;
        WriteByte(memory, first + 0x5A, firstType);
        WriteByte(memory, second + 0x5A, secondType);
        WriteByte(memory, second + 0x5C, 9);
        ctx[CpuRegister.Rdi] = output;
        ctx[CpuRegister.Rsi] = first;
        ctx[CpuRegister.Rdx] = second;

        Assert.Equal(
            0,
            AgcExports.UnknownGetCombinedShaderRegisterStorageSize(ctx));
        Assert.Equal(72UL, ReadUInt64(memory, output));
        Assert.Equal(4UL, ReadUInt64(memory, output + 8));
    }

    [Theory]
    [InlineData(3, 6)]
    [InlineData(4, 7)]
    [InlineData(5, 6)]
    public void UnknownStorageSize_InvalidPairLeavesOutputUntouched(
        byte firstType,
        byte secondType)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var output = BaseAddress + 0x100;
        var first = BaseAddress + 0x200;
        var second = BaseAddress + 0x300;
        Span<byte> sentinel = stackalloc byte[16];
        sentinel.Fill(0x5A);
        Assert.True(memory.TryWrite(output, sentinel));
        WriteByte(memory, first + 0x5A, firstType);
        WriteByte(memory, second + 0x5A, secondType);
        ctx[CpuRegister.Rdi] = output;
        ctx[CpuRegister.Rsi] = first;
        ctx[CpuRegister.Rdx] = second;

        Assert.Equal(
            IncompatiblePair,
            AgcExports.UnknownGetCombinedShaderRegisterStorageSize(ctx));
        AssertBytes(memory, output, sentinel);
    }

    [Fact]
    public void UnknownCreateCombinedShader_CompatibilityFailureIsNotAtomic()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x3000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var output = BaseAddress + 0x100;
        var first = BaseAddress + 0x300;
        var second = BaseAddress + 0x500;
        var firstSpecials = BaseAddress + 0x800;
        var secondSpecials = BaseAddress + 0x900;
        Span<byte> descriptor = stackalloc byte[DescriptorSize];
        descriptor.Fill(0x3C);
        descriptor[0x5A] = 6;
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[0x28..], secondSpecials);
        Assert.True(memory.TryWrite(second, descriptor));
        WriteByte(memory, first + 0x5A, 4);
        WriteUInt64(memory, first + 0x28, firstSpecials);
        WriteUInt64(memory, firstSpecials + 8, 0);
        WriteUInt64(memory, secondSpecials + 8, 1UL << 54);
        ctx[CpuRegister.Rdi] = output;
        ctx[CpuRegister.Rsi] = first;
        ctx[CpuRegister.Rdx] = second;

        Assert.Equal(
            IncompatiblePair,
            AgcExports.UnknownCreateCombinedShader(ctx));

        descriptor[0x5A] = 2;
        AssertBytes(memory, output, descriptor);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void UnknownCreateCombinedShader_ReconcilesBothRecoveredPairs(
        bool hullLocalPair,
        bool useOptionalRegisterBuffer)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x5000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var output = BaseAddress + 0x100;
        var first = BaseAddress + 0x300;
        var second = BaseAddress + 0x500;
        var firstRegisters = BaseAddress + 0x1000;
        var secondRegisters = BaseAddress + 0x1400;
        var optionalRegisters = useOptionalRegisterBuffer
            ? BaseAddress + 0x1800
            : 0;
        var firstSpecials = BaseAddress + 0x1C00;
        var secondSpecials = BaseAddress + 0x1D00;
        var internalRegister = hullLocalPair ? 0x100u : 0x80u;
        var resource1Register = hullLocalPair ? 0x10Au : 0x8Au;
        var resource2Register = hullLocalPair ? 0x10Bu : 0x8Bu;
        var programLoRegister = hullLocalPair ? 0x148u : 0xC8u;
        var programHiRegister = programLoRegister + 1;
        var firstResource1 = hullLocalPair ? 0x3000_0003u : 0x4000_0003u;
        const uint firstResource2 = 0xF002_003E;
        var secondResource1 = hullLocalPair ? 0x1000_0001u : 0x2000_0001u;
        const uint secondResource2 = 0x0001_0001;
        var expectedResource1 = hullLocalPair ? 0x3000_0003u : 0x4000_0003u;
        var expectedResource2 = hullLocalPair ? 0x2001_003Fu : 0x2002_003Fu;
        const ulong codeAddress = 0x0000_12AB_CDEF_1200;

        WriteDescriptor(
            memory,
            first,
            hullLocalPair ? (byte)5 : (byte)4,
            firstRegisters,
            4,
            firstSpecials,
            codeAddress,
            secondQword: 0x1111);
        WriteDescriptor(
            memory,
            second,
            hullLocalPair ? (byte)7 : (byte)6,
            secondRegisters,
            6,
            secondSpecials,
            codeAddress: 0x2000,
            secondQword: 0xFFFF_FFFF_FFFF_FFFF);
        WriteRegisters(
            memory,
            firstRegisters,
            (internalRegister, 0x1111_1111),
            (internalRegister, 0x2222_2222),
            (resource1Register, firstResource1),
            (resource2Register, firstResource2));
        WriteRegisters(
            memory,
            secondRegisters,
            (internalRegister, 0x3333_3333),
            (internalRegister, 0x4444_4444),
            (resource1Register, secondResource1),
            (resource2Register, secondResource2),
            (programLoRegister, 0),
            (programHiRegister, 0xAABB_CC00));
        WriteUInt64(memory, firstSpecials + 8, 0);
        WriteUInt64(memory, secondSpecials + 8, 0);
        ctx[CpuRegister.Rdi] = output;
        ctx[CpuRegister.Rsi] = first;
        ctx[CpuRegister.Rdx] = second;
        ctx[CpuRegister.Rcx] = optionalRegisters;

        Assert.Equal(0, AgcExports.UnknownCreateCombinedShader(ctx));

        Assert.Equal(0UL, ReadUInt64(memory, output + 8));
        Assert.Equal(
            hullLocalPair ? (byte)3 : (byte)2,
            ReadByte(memory, output + 0x5A));
        var targetRegisters = useOptionalRegisterBuffer
            ? optionalRegisters
            : secondRegisters;
        Assert.Equal(targetRegisters, ReadUInt64(memory, output + 0x20));
        AssertRegisterValue(memory, targetRegisters, 0, 0x1111_1111);
        AssertRegisterValue(memory, targetRegisters, 1, 0x2222_2222);
        AssertRegisterValue(memory, targetRegisters, 2, expectedResource1);
        AssertRegisterValue(memory, targetRegisters, 3, expectedResource2);
        AssertRegisterValue(memory, targetRegisters, 4, 0xABCD_EF12);
        AssertRegisterValue(memory, targetRegisters, 5, 0xAABB_CC12);
        if (useOptionalRegisterBuffer)
        {
            AssertRegisterValue(memory, secondRegisters, 0, 0x3333_3333);
            AssertRegisterValue(memory, secondRegisters, 1, 0x4444_4444);
        }
    }

    [Fact]
    public void RecoveredAgcNids_RegisterWithExpectedIdentity()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        AssertExport(
            manager,
            "BfBDZGbti7A",
            "sceAgcGetIsTrinityMode");
        AssertExport(
            manager,
            "dolOmWH+huQ",
            "unknown_dolOmWH_huQ");
        AssertExport(
            manager,
            "fd5Bp5tGTgo",
            "unknown_fd5Bp5tGTgo");
    }

    [Fact]
    public void ConfigureUnknownPatchDescriptor_MatchesFirmware1270BitfieldWrites()
    {
        const ulong descriptorAddress = BaseAddress + 0x100;
        const ulong targetAddress = 0x1_3DA4_0080;
        const uint originalControl = 0xC550_0000;
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Span<byte> descriptor = stackalloc byte[16];
        descriptor.Clear();
        descriptor[1] = 0x3F;
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], originalControl);
        Assert.True(memory.TryWrite(descriptorAddress, descriptor));

        ctx[CpuRegister.Rdi] = descriptorAddress;
        ctx[CpuRegister.Rsi] = 2;
        ctx[CpuRegister.Rdx] = targetAddress;
        ctx[CpuRegister.Rcx] = 0x2127;

        Assert.Equal(0, AgcExports.ConfigureUnknownPatchDescriptor(ctx));
        Assert.True(memory.TryRead(descriptorAddress, descriptor));
        Assert.Equal(0x3F, descriptor[1]);
        Assert.Equal(
            3U | (unchecked((uint)targetAddress) & 0xFFFF_FFFC),
            BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]));
        Assert.Equal(
            (uint)(targetAddress >> 32),
            BinaryPrimitives.ReadUInt32LittleEndian(descriptor[8..]));
        Assert.Equal(
            (2U << 28) | (originalControl & 0xCFF0_0000) | 0x2127U,
            BinaryPrimitives.ReadUInt32LittleEndian(descriptor[12..]));

        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == "Ikfdt-rIqCE");
        Assert.Equal("Ikfdt-rIqCE#G#A", export.Name);
        Assert.False(export.PreferLle);
    }

    private static void AssertExport(
        ModuleManager manager,
        string nid,
        string name)
    {
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceAgc", export.LibraryName);
    }

    private static ulong AllocateTracked(CpuContext context, int length)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)length);
        Assert.Equal(0, KernelMemoryCompatExports.Malloc(context));
        Assert.NotEqual(0UL, context[CpuRegister.Rax]);
        return context[CpuRegister.Rax];
    }

    private static void FreeTracked(CpuContext context, ulong address)
    {
        context[CpuRegister.Rdi] = address;
        Assert.Equal(0, KernelMemoryCompatExports.Free(context));
    }

    private static void WriteDescriptor(
        FakeCpuMemory memory,
        ulong address,
        byte type,
        ulong registers,
        byte registerCount,
        ulong specials,
        ulong codeAddress,
        ulong secondQword)
    {
        Span<byte> descriptor = stackalloc byte[DescriptorSize];
        descriptor.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[0x08..], secondQword);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[0x10..], codeAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[0x20..], registers);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[0x28..], specials);
        descriptor[0x5A] = type;
        descriptor[0x5C] = registerCount;
        Assert.True(memory.TryWrite(address, descriptor));
    }

    private static void WriteCreateShaderDescriptor(
        FakeCpuMemory memory,
        ulong headerAddress,
        byte type,
        ulong registersAddress,
        ulong specialsAddress,
        uint shaderSize)
    {
        Span<byte> descriptor = stackalloc byte[DescriptorSize];
        descriptor.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 0x3433_3231);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], 0x18);
        BinaryPrimitives.WriteUInt64LittleEndian(
            descriptor[0x20..],
            registersAddress - (headerAddress + 0x20));
        BinaryPrimitives.WriteUInt64LittleEndian(
            descriptor[0x28..],
            specialsAddress - (headerAddress + 0x28));
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[0x44..], shaderSize);
        descriptor[0x5A] = type;
        descriptor[0x5C] = 2;
        Assert.True(memory.TryWrite(headerAddress, descriptor));
    }

    private static void WriteProgram(
        FakeCpuMemory memory,
        ulong address,
        params uint[] words)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        foreach (var word in words)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, word);
            Assert.True(memory.TryWrite(address, bytes));
            address += sizeof(uint);
        }
    }

    private static void WriteRegisters(
        FakeCpuMemory memory,
        ulong address,
        params (uint Register, uint Value)[] registers)
    {
        Span<byte> record = stackalloc byte[8];
        for (var index = 0; index < registers.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                record,
                registers[index].Register);
            BinaryPrimitives.WriteUInt32LittleEndian(
                record[4..],
                registers[index].Value);
            Assert.True(memory.TryWrite(address + ((ulong)index * 8), record));
        }
    }

    private static void AssertRegisterValue(
        FakeCpuMemory memory,
        ulong address,
        int index,
        uint expected)
    {
        Span<byte> value = stackalloc byte[4];
        Assert.True(memory.TryRead(address + ((ulong)index * 8) + 4, value));
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(value));
    }

    private static void AssertRegister(
        FakeCpuMemory memory,
        ulong address,
        int index,
        uint expectedRegister,
        uint expectedValue)
    {
        Span<byte> record = stackalloc byte[8];
        Assert.True(memory.TryRead(address + ((ulong)index * 8), record));
        Assert.Equal(
            expectedRegister,
            BinaryPrimitives.ReadUInt32LittleEndian(record));
        Assert.Equal(
            expectedValue,
            BinaryPrimitives.ReadUInt32LittleEndian(record[4..]));
    }

    private static void WriteByte(FakeCpuMemory memory, ulong address, byte value)
    {
        Span<byte> bytes = stackalloc byte[] { value };
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static byte ReadByte(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[1];
        Assert.True(memory.TryRead(address, bytes));
        return bytes[0];
    }

    private static void WriteUInt64(
        FakeCpuMemory memory,
        ulong address,
        ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[8];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void AssertBytes(
        FakeCpuMemory memory,
        ulong address,
        ReadOnlySpan<byte> expected)
    {
        Span<byte> actual = stackalloc byte[expected.Length];
        Assert.True(memory.TryRead(address, actual));
        Assert.Equal(expected.ToArray(), actual.ToArray());
    }
}
