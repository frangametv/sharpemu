// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Lle;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpWebApi2CreateRequestExportsTests
{
    [Fact]
    public void Gen5RegistryUsesSemanticCreateRequestFallback()
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == "3EI-OSJ65Xc");

        Assert.Equal((SysAbiFunction)NpWebApi2LleExports.CreateRequestWithoutGuestProvider, export.Function);
        Assert.True(export.PreferLle);
    }

    [Fact]
    public void Gen5RegistryUsesSemanticAddHeaderFallback()
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == "egOOvrnF6mI");

        Assert.Equal(
            (SysAbiFunction)NpWebApi2LleExports.AddHttpRequestHeaderWithoutGuestProvider,
            export.Function);
        Assert.True(export.PreferLle);
    }

    [Fact]
    public void AddHeaderAcceptsZeroRequestHandleWhenStringsAreValid()
    {
        var context = new CpuContext(new NullMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x810D_604A5;
        context[CpuRegister.Rdx] = 0x7FFF_C61F_F1D0;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            NpWebApi2LleExports.AddHttpRequestHeaderWithoutGuestProvider(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(0UL, 1UL)]
    [InlineData(1UL, 0UL)]
    public void AddHeaderRejectsMissingStringPointers(ulong name, ulong value)
    {
        var context = new CpuContext(new NullMemory(), Generation.Gen5);
        context[CpuRegister.Rsi] = name;
        context[CpuRegister.Rdx] = value;

        Assert.Equal(
            unchecked((int)0x80553402),
            NpWebApi2LleExports.AddHttpRequestHeaderWithoutGuestProvider(context));
    }

    [Fact]
    public void CreateRequestReturnsPositiveHandleForCreatedUserContext()
    {
        var context = new CpuContext(new NullMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = 0x1000;
        Assert.True(NpWebApi2Exports.NpWebApi2Initialize(context) > 0);
        var libraryContextId = unchecked((int)context[CpuRegister.Rax]);

        context[CpuRegister.Rdi] = unchecked((ulong)libraryContextId);
        context[CpuRegister.Rsi] = 0;
        Assert.True(NpWebApi2Exports.NpWebApi2CreateUserContext(context) > 0);
        var userContextId = unchecked((int)context[CpuRegister.Rax]);

        context[CpuRegister.Rdi] = unchecked((ulong)userContextId);
        context[CpuRegister.Rsi] = 0x2000;
        context[CpuRegister.Rdx] = 0x3000;
        var requestId = NpWebApi2LleExports.CreateRequestWithoutGuestProvider(context);

        Assert.True(requestId > 0);
        Assert.Equal(unchecked((ulong)requestId), context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(999, 0x2000UL, 0x3000UL)]
    [InlineData(1001, 0UL, 0x3000UL)]
    [InlineData(1001, 0x2000UL, 0UL)]
    public void CreateRequestRejectsInvalidArguments(
        int userContextId,
        ulong serviceNameAddress,
        ulong requestDataAddress)
    {
        var context = new CpuContext(new NullMemory(), Generation.Gen5);
        context[CpuRegister.Rdi] = unchecked((ulong)userContextId);
        context[CpuRegister.Rsi] = serviceNameAddress;
        context[CpuRegister.Rdx] = requestDataAddress;

        Assert.Equal(
            unchecked((int)0x80553402),
            NpWebApi2LleExports.CreateRequestWithoutGuestProvider(context));
    }

    private sealed class NullMemory : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination) => false;
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
