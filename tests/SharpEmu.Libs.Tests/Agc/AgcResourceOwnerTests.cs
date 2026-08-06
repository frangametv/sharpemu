// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcResourceOwnerTests
{
    private const int ProviderError = unchecked((int)0x8A6C9018);
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong OwnerAddress = BaseAddress + 0x100;
    private const ulong NameAddress = BaseAddress + 0x200;
    private const ulong RegistrationMemoryAddress = BaseAddress + 0x400;

    [Fact]
    public void RegisterOwner_ReturnsRecoveredProviderErrorWithoutWritingOwner()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, OwnerAddress, 0xA5A5_A5A5);
        memory.WriteCString(NameAddress, "GIRender");
        ctx[CpuRegister.Rdi] = OwnerAddress;
        ctx[CpuRegister.Rsi] = NameAddress;

        Assert.Equal(ProviderError, AgcExports.DriverRegisterOwner(ctx));
        Assert.Equal(unchecked((ulong)ProviderError), ctx[CpuRegister.Rax]);
        Assert.Equal(0xA5A5_A5A5u, ReadUInt32(memory, OwnerAddress));
    }

    [Fact]
    public void RegisterOwner_DoesNotConsultInitializedResourceRegistry()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = RegistrationMemoryAddress;
        ctx[CpuRegister.Rsi] = 0x1000;
        ctx[CpuRegister.Rdx] = 1;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.DriverInitResourceRegistration(ctx));

        WriteUInt32(memory, OwnerAddress, 0x5A5A_5A5A);
        memory.WriteCString(NameAddress, "Owner");
        ctx[CpuRegister.Rdi] = OwnerAddress;
        ctx[CpuRegister.Rsi] = NameAddress;

        Assert.Equal(ProviderError, AgcExports.DriverRegisterOwner(ctx));
        Assert.Equal(0x5A5A_5A5Au, ReadUInt32(memory, OwnerAddress));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[4];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}
