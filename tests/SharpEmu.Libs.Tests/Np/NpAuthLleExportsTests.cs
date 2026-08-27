// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Lle;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpAuthLleExportsTests : IDisposable
{
    private const ulong Base = 0x6_0000_0000;
    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public NpAuthLleExportsTests()
    {
        NpAuthLleExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
        Span<byte> create = stackalloc byte[0x18];
        BinaryPrimitives.WriteUInt64LittleEndian(create, 0x18);
        Assert.True(_memory.TryWrite(Base + 0x100, create));
        Span<byte> auth = stackalloc byte[0x20];
        BinaryPrimitives.WriteUInt64LittleEndian(auth, 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(auth[8..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(auth[0x10..], Base + 0x500);
        BinaryPrimitives.WriteUInt64LittleEndian(auth[0x18..], Base + 0x600);
        Assert.True(_memory.TryWrite(Base + 0x200, auth));
    }

    public void Dispose() => NpAuthLleExports.ResetForTests();

    [Fact]
    public void OfflineLifecycle_ReturnsLocalAuthorizationAndCompletesSuccessfully()
    {
        _ctx[CpuRegister.Rdi] = Base + 0x100;
        var id = NpAuthLleExports.CreateAsyncRequest(_ctx);
        Assert.Equal(0x1000_0001, id);
        _ctx[CpuRegister.Rdi] = (ulong)id;
        _ctx[CpuRegister.Rsi] = Base + 0x200;
        _ctx[CpuRegister.Rdx] = Base + 0x300;
        _ctx[CpuRegister.Rcx] = Base + 0x500;
        Assert.Equal(0, NpAuthLleExports.GetAuthorizationCodeV3(_ctx));
        Span<byte> authorizationCode = stackalloc byte[7];
        Assert.True(_memory.TryRead(Base + 0x300, authorizationCode));
        Assert.Equal("AUTHEN\0"u8.ToArray(), authorizationCode.ToArray());
        Span<byte> issuerId = stackalloc byte[4];
        Assert.True(_memory.TryRead(Base + 0x500, issuerId));
        Assert.Equal(0x100, BinaryPrimitives.ReadInt32LittleEndian(issuerId));
        _ctx[CpuRegister.Rsi] = Base + 0x400;
        Assert.Equal(0, NpAuthLleExports.PollAsync(_ctx));
        Span<byte> result = stackalloc byte[4];
        Assert.True(_memory.TryRead(Base + 0x400, result));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(result));
        Assert.Equal(0, NpAuthLleExports.DeleteRequest(_ctx));
    }

    [Fact]
    public void AllFiveExportsRemainLlePreferred()
    {
        var exports = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5);
        foreach (var nid in new[] { "N+mr7GjTvr8", "KI4dHLlTNl0", "gjSyfzSsDcE", "cE7wIsqXdZ8", "H8wG9Bk-nPc" })
        {
            var export = Assert.Single(exports, item => item.Nid == nid);
            Assert.True(export.PreferLle);
            Assert.Equal(typeof(NpAuthLleExports), export.Function.Method.DeclaringType);
        }
    }
}
