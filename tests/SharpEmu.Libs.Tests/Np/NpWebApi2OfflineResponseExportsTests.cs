// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Lle;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpWebApi2OfflineResponseExportsTests
{
    [Theory]
    [InlineData("lQOCF84lvzw", nameof(NpWebApi2LleExports.SendRequestWithoutGuestProvider))]
    [InlineData("HwP3aM+c85c", nameof(NpWebApi2LleExports.GetHttpResponseHeaderValueLengthWithoutGuestProvider))]
    [InlineData("hksbskNToEA", nameof(NpWebApi2LleExports.GetHttpResponseHeaderValueWithoutGuestProvider))]
    [InlineData("vvzWO-DvG1s", nameof(NpWebApi2LleExports.DeleteRequestWithoutGuestProvider))]
    [InlineData("zpiPsH7dbFQ", nameof(NpWebApi2LleExports.AbortRequestWithoutGuestProvider))]
    [InlineData("OOY9+ObfKec", nameof(NpWebApi2LleExports.ReadDataWithoutGuestProvider))]
    [InlineData("9X9+cneTGUU", nameof(NpWebApi2LleExports.DeleteUserContextWithoutGuestProvider))]
    public void RegistryUsesSemanticOfflineFallback(string nid, string methodName)
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == nid);

        Assert.Equal(methodName, export.Function.Method.Name);
        Assert.True(export.PreferLle);
    }

    [Fact]
    public void EmptyOfflineHeaderHasNulOnlyValue()
    {
        var memory = new FakeCpuMemory(0x1000, 0x3000);
        var context = new CpuContext(memory, Generation.Gen5);
        const ulong nameAddress = 0x1000;
        const ulong lengthAddress = 0x2000;
        const ulong valueAddress = 0x3000;
        memory.WriteCString(nameAddress, "Retry-After");

        context[CpuRegister.Rsi] = nameAddress;
        context[CpuRegister.Rdx] = lengthAddress;
        Assert.Equal(0, NpWebApi2LleExports.GetHttpResponseHeaderValueLengthWithoutGuestProvider(context));
        Assert.True(context.TryReadUInt64(lengthAddress, out var length));
        Assert.Equal(1UL, length);

        context[CpuRegister.Rdx] = valueAddress;
        context[CpuRegister.Rcx] = length;
        Assert.Equal(0, NpWebApi2LleExports.GetHttpResponseHeaderValueWithoutGuestProvider(context));
        Span<byte> value = stackalloc byte[1];
        Assert.True(memory.TryRead(valueAddress, value));
        Assert.Equal(0, value[0]);
    }

    [Fact]
    public void SendAbortAndDeleteAcceptProviderCompatibleZeroHandle()
    {
        var context = new CpuContext(new FakeCpuMemory(0x1000, 0x1000), Generation.Gen5);
        Assert.Equal(0, NpWebApi2LleExports.SendRequestWithoutGuestProvider(context));
        Assert.Equal(0, NpWebApi2LleExports.AbortRequestWithoutGuestProvider(context));
        Assert.Equal(0, NpWebApi2LleExports.DeleteRequestWithoutGuestProvider(context));
        Assert.Equal(0, NpWebApi2LleExports.DeleteUserContextWithoutGuestProvider(context));
    }

    [Fact]
    public void ReadDataReturnsImmediateOfflineEofWithoutChangingBuffer()
    {
        var memory = new FakeCpuMemory(0x1000, 0x1000);
        Assert.True(memory.TryWrite(0x1800, stackalloc byte[] { 0xA5 }));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rsi] = 0x1800;
        context[CpuRegister.Rdx] = 1;

        Assert.Equal(0, NpWebApi2LleExports.ReadDataWithoutGuestProvider(context));
        Span<byte> value = stackalloc byte[1];
        Assert.True(memory.TryRead(0x1800, value));
        Assert.Equal(0xA5, value[0]);
    }
}
