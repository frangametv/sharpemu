// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Network;

// The libSceNet byte-order helpers take their operand in Rdi and return the converted value in
// Rax. They swap endianness unconditionally, which is correct on the little-endian hosts (and
// little-endian guest) the emulator targets, so network (big-endian) order is always a byte swap.
public sealed class NetExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong SourceAddress = MemoryBase + 0x100;
    private const ulong DestinationAddress = MemoryBase + 0x200;
    private readonly CpuContext _ctx = new(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);

    [Fact]
    public void Htonl_SwapsAllFourBytes()
    {
        _ctx[CpuRegister.Rdi] = 0x01020304;

        Assert.Equal(0, NetExports.NetHtonl(_ctx));
        Assert.Equal(0x04030201UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Ntohl_SwapsAllFourBytes()
    {
        _ctx[CpuRegister.Rdi] = 0x01020304;

        Assert.Equal(0, NetExports.NetNtohl(_ctx));
        Assert.Equal(0x04030201UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Htons_SwapsLowTwoBytesOnly()
    {
        // High bits above the 16-bit short must be ignored, not folded into the result.
        _ctx[CpuRegister.Rdi] = 0xFFFF_0102;

        Assert.Equal(0, NetExports.NetHtons(_ctx));
        Assert.Equal(0x0201UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Ntohs_SwapsLowTwoBytesOnly()
    {
        _ctx[CpuRegister.Rdi] = 0xFFFF_0102;

        Assert.Equal(0, NetExports.NetNtohs(_ctx));
        Assert.Equal(0x0201UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Htonl_IgnoresBitsAboveThe32BitWord()
    {
        _ctx[CpuRegister.Rdi] = 0xDEADBEEF_01020304;

        Assert.Equal(0, NetExports.NetHtonl(_ctx));
        Assert.Equal(0x04030201UL, _ctx[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(0xDEADBEEFUL)]
    [InlineData(0x00000000UL)]
    [InlineData(0xFFFFFFFFUL)]
    [InlineData(0x00000001UL)]
    public void HtonlThenNtohl_RoundTripsToOriginal(ulong value)
    {
        _ctx[CpuRegister.Rdi] = value;
        NetExports.NetHtonl(_ctx);

        _ctx[CpuRegister.Rdi] = _ctx[CpuRegister.Rax];
        NetExports.NetNtohl(_ctx);

        Assert.Equal(value, _ctx[CpuRegister.Rax]);
    }

    // Regression guard: a non-palindromic value must not come back as 0. The functions previously
    // computed the swap into Rax and then called SetReturn(0), which overwrote Rax, so every
    // sceNetHtonl/Htons/Ntohl/Ntohs call returned 0 regardless of input.
    [Fact]
    public void ByteOrderConversions_DoNotReturnZeroForNonZeroInput()
    {
        _ctx[CpuRegister.Rdi] = 0x01020304;
        NetExports.NetHtonl(_ctx);
        Assert.NotEqual(0UL, _ctx[CpuRegister.Rax]);

        _ctx[CpuRegister.Rax] = 0;
        _ctx[CpuRegister.Rdi] = 0x0102;
        NetExports.NetHtons(_ctx);
        Assert.NotEqual(0UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void NetInetPtonParsesStrictIpv4ForGtaNetCtlAddress()
    {
        WriteCString("127.0.0.1");
        _ctx[CpuRegister.Rdi] = 2;
        _ctx[CpuRegister.Rsi] = SourceAddress;
        _ctx[CpuRegister.Rdx] = DestinationAddress;

        Assert.Equal(0, NetExports.NetInetPton(_ctx));
        Assert.Equal(1UL, _ctx[CpuRegister.Rax]);
        AssertBytes(DestinationAddress, new byte[] { 127, 0, 0, 1 });
    }

    [Fact]
    public void NetInetPtonParsesIpv6Compression()
    {
        WriteCString("2001:db8::1");
        _ctx[CpuRegister.Rdi] = 28;
        _ctx[CpuRegister.Rsi] = SourceAddress;
        _ctx[CpuRegister.Rdx] = DestinationAddress;

        Assert.Equal(0, NetExports.NetInetPton(_ctx));
        AssertBytes(
            DestinationAddress,
            new byte[] { 0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 });
    }

    [Fact]
    public void NetInetPtonInvalidSyntaxReturnsZeroWithoutChangingDestinationOrErrno()
    {
        _ctx[CpuRegister.Rdi] = 123;
        Assert.Equal(unchecked((int)0x8041012F), NetExports.NetInetPton(_ctx));
        NetExports.NetErrnoLoc(_ctx);
        var errnoAddress = unchecked((nint)_ctx[CpuRegister.Rax]);
        Assert.Equal(47, Marshal.ReadInt32(errnoAddress));

        WriteCString("127.0.0");
        Assert.True(_ctx.Memory.TryWrite(DestinationAddress, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }));
        _ctx[CpuRegister.Rdi] = 2;
        _ctx[CpuRegister.Rsi] = SourceAddress;
        _ctx[CpuRegister.Rdx] = DestinationAddress;

        Assert.Equal(0, NetExports.NetInetPton(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        AssertBytes(DestinationAddress, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        Assert.Equal(47, Marshal.ReadInt32(errnoAddress));
    }

    [Fact]
    public void NetInetPtonNullSourceSetsProviderInvalidArgumentAndErrno()
    {
        _ctx[CpuRegister.Rdi] = 2;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = DestinationAddress;

        Assert.Equal(unchecked((int)0x80410116), NetExports.NetInetPton(_ctx));
        NetExports.NetErrnoLoc(_ctx);
        Assert.Equal(22, Marshal.ReadInt32(unchecked((nint)_ctx[CpuRegister.Rax])));
    }

    [Fact]
    public void NetInetPtonRegistersAsExactLlePreferredGen5Fallback()
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == "8Kcp5d-q1Uo");

        Assert.Equal("sceNetInetPton", export.Name);
        Assert.Equal("libSceNet", export.LibraryName);
        Assert.True(export.PreferLle);
        Assert.Equal(typeof(NetExports), export.Function.Method.DeclaringType);
    }

    [Fact]
    public void NetGetSockInfoWritesBoundLocalPortForGtaSocketBootstrap()
    {
        WriteCString("gta-sockinfo-test");
        _ctx[CpuRegister.Rdi] = SourceAddress;
        _ctx[CpuRegister.Rsi] = 2;
        _ctx[CpuRegister.Rdx] = 2;
        _ctx[CpuRegister.Rcx] = 17;
        Assert.Equal(0, NetExports.NetSocket(_ctx));
        var socketId = _ctx[CpuRegister.Rax];

        try
        {
            Span<byte> socketAddress = stackalloc byte[16];
            socketAddress[0] = 16;
            socketAddress[1] = 2;
            socketAddress[4] = 127;
            socketAddress[7] = 1;
            Assert.True(_ctx.Memory.TryWrite(SourceAddress, socketAddress));

            _ctx[CpuRegister.Rdi] = socketId;
            _ctx[CpuRegister.Rsi] = SourceAddress;
            _ctx[CpuRegister.Rdx] = 16;
            Assert.Equal(0, NetExports.NetBind(_ctx));

            _ctx[CpuRegister.Rdi] = socketId;
            _ctx[CpuRegister.Rsi] = DestinationAddress;
            _ctx[CpuRegister.Rdx] = 1;
            _ctx[CpuRegister.Rcx] = 0;
            Assert.Equal(0, NetExports.NetGetSockInfo(_ctx));

            Span<byte> port = stackalloc byte[sizeof(ushort)];
            Assert.True(_ctx.Memory.TryRead(DestinationAddress + 0x3C, port));
            Assert.NotEqual(0, BinaryPrimitives.ReadUInt16BigEndian(port));
        }
        finally
        {
            _ctx[CpuRegister.Rdi] = socketId;
            NetExports.NetSocketClose(_ctx);
        }
    }

    [Fact]
    public void NetGetSockInfoRegistersAsSemanticLlePreferredGen5Fallback()
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == "hLuXdjHnhiI");

        Assert.Equal("sceNetGetSockInfo", export.Name);
        Assert.Equal("libSceNet", export.LibraryName);
        Assert.True(export.PreferLle);
        Assert.Equal(typeof(NetExports), export.Function.Method.DeclaringType);
    }

    private void WriteCString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        Assert.True(_ctx.Memory.TryWrite(SourceAddress, bytes));
    }

    private void AssertBytes(ulong address, byte[] expected)
    {
        var actual = new byte[expected.Length];
        Assert.True(_ctx.Memory.TryRead(address, actual));
        Assert.Equal(expected, actual);
    }
}
