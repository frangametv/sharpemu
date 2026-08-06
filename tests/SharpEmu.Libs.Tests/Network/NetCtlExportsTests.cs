// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Network;

public sealed class NetCtlExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong OutputAddress = MemoryBase + 0x100;
    private readonly CpuContext _ctx = new(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);

    [Fact]
    public void GetInfoReportsConnectedLinkForImmediatelyUsableNetwork()
    {
        _ctx[CpuRegister.Rdi] = 4;
        _ctx[CpuRegister.Rsi] = OutputAddress;

        Assert.Equal(0, NetCtlExports.NetCtlGetInfo(_ctx));
        Assert.Equal(1u, ReadUInt32());
    }

    [Fact]
    public void GetStateReportsIpObtainedForImmediatelyUsableNetwork()
    {
        _ctx[CpuRegister.Rdi] = OutputAddress;

        Assert.Equal(0, NetCtlExports.NetCtlGetState(_ctx));
        Assert.Equal(3u, ReadUInt32());
    }

    [Fact]
    public void GtaStartupPrerequisitesExposeEthernetAndParseableIpv4()
    {
        _ctx[CpuRegister.Rdi] = 2;
        _ctx[CpuRegister.Rsi] = OutputAddress;
        Assert.Equal(0, NetCtlExports.NetCtlGetInfo(_ctx));

        var etherAddress = new byte[6];
        Assert.True(_ctx.Memory.TryRead(OutputAddress, etherAddress));
        Assert.Equal(new byte[6], etherAddress);

        _ctx[CpuRegister.Rdi] = 14;
        _ctx[CpuRegister.Rsi] = OutputAddress;
        Assert.Equal(0, NetCtlExports.NetCtlGetInfo(_ctx));
        Assert.Equal("127.0.0.1", ReadCString(16));

        var packedAddress = OutputAddress + 0x100;
        _ctx[CpuRegister.Rdi] = 2;
        _ctx[CpuRegister.Rsi] = OutputAddress;
        _ctx[CpuRegister.Rdx] = packedAddress;
        Assert.Equal(0, NetExports.NetInetPton(_ctx));
        Assert.Equal(1UL, _ctx[CpuRegister.Rax]);

        var packed = new byte[4];
        Assert.True(_ctx.Memory.TryRead(packedAddress, packed));
        Assert.Equal(new byte[] { 127, 0, 0, 1 }, packed);
    }

    private uint ReadUInt32()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(_ctx.Memory.TryRead(OutputAddress, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private string ReadCString(int byteCount)
    {
        var bytes = new byte[byteCount];
        Assert.True(_ctx.Memory.TryRead(OutputAddress, bytes));
        var terminator = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, terminator >= 0 ? terminator : bytes.Length);
    }
}
