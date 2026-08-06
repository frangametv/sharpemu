// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcVshCompatExportsTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong DcbAddress = BaseAddress + 0x100;
    private const ulong CommandAddress = BaseAddress + 0x1000;
    private const ulong StackAddress = BaseAddress + 0x3000;

    [Fact]
    public void VshCompatExports_RegisterWithFirmwareNids()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("BfBDZGbti7A", out var trinity));
        Assert.Equal("sceAgcGetIsTrinityMode", trinity.Name);
        Assert.Equal("libSceAgc", trinity.LibraryName);

        Assert.True(manager.TryGetExport("1rZSWUv1IRc", out var copyData));
        Assert.Equal("sceAgcDcbCopyData", copyData.Name);
        Assert.Equal("libSceAgc", copyData.LibraryName);
    }

    [Fact]
    public void GetIsTrinityMode_WritesBaseHardwareMode()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x10000);
        var context = new CpuContext(memory, Generation.Gen5);
        var outputAddress = BaseAddress + 0x80;
        Assert.True(memory.TryWrite(outputAddress, stackalloc byte[] { 0xFF }));
        context[CpuRegister.Rdi] = outputAddress;

        var result = AgcExports.GetIsTrinityMode(context);

        Span<byte> output = stackalloc byte[1];
        Assert.True(memory.TryRead(outputAddress, output));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal((byte)0, output[0]);
    }

    [Fact]
    public void DcbCopyData_EmitsExactTwelveSeventyPacket()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x10000);
        var context = new CpuContext(memory, Generation.Gen5);
        InitializeDcb(memory);

        const ulong destinationAddress = 0x3_1234_5678;
        const ulong sourceAddress = 0x2_ABCD_EF00;
        context[CpuRegister.Rdi] = DcbAddress;
        context[CpuRegister.Rsi] = 0x1A; // DST_SEL is (value >> 1) & 0xf.
        context[CpuRegister.Rdx] = 2;    // Destination cache policy.
        context[CpuRegister.Rcx] = destinationAddress;
        context[CpuRegister.R8] = 0x13; // Engine bit 0 plus SRC_SEL in bits 4:1.
        context[CpuRegister.R9] = 3;    // Source cache policy.
        context[CpuRegister.Rsp] = StackAddress;
        WriteUInt64(memory, StackAddress + 8, sourceAddress);
        WriteUInt64(memory, StackAddress + 16, 1); // 64-bit count select.
        WriteUInt64(memory, StackAddress + 24, 1); // Write confirmation.

        var result = AgcExports.DcbCopyData(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(CommandAddress, context[CpuRegister.Rax]);
        Assert.Equal(CommandAddress + 24, ReadUInt64(memory, DcbAddress + 0x10));
        Assert.Equal(0xC004_4000u, ReadUInt32(memory, CommandAddress));
        Assert.Equal(0x4411_6D09u, ReadUInt32(memory, CommandAddress + 4));
        Assert.Equal(unchecked((uint)sourceAddress), ReadUInt32(memory, CommandAddress + 8));
        Assert.Equal((uint)(sourceAddress >> 32), ReadUInt32(memory, CommandAddress + 12));
        Assert.Equal(unchecked((uint)destinationAddress), ReadUInt32(memory, CommandAddress + 16));
        Assert.Equal((uint)(destinationAddress >> 32), ReadUInt32(memory, CommandAddress + 20));
    }

    private static void InitializeDcb(FakeCpuMemory memory)
    {
        WriteUInt64(memory, DcbAddress + 0x10, CommandAddress);
        WriteUInt64(memory, DcbAddress + 0x18, BaseAddress + 0x2000);
        WriteUInt64(memory, DcbAddress + 0x20, 0);
        WriteUInt64(memory, DcbAddress + 0x28, 0);
        WriteUInt32(memory, DcbAddress + 0x30, 0);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}
