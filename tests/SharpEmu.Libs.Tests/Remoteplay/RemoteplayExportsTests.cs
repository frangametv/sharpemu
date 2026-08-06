// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Remoteplay;
using Xunit;

namespace SharpEmu.Libs.Tests.Remoteplay;

public sealed class RemoteplayExportsTests
{
    private const ulong OutputAddress = 0x1000;

    [Fact]
    public void GetConnectionStatus_ReturnsDisconnectedUint32()
    {
        var memory = new FixedMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0x1000_0000;
        ctx[CpuRegister.Rsi] = OutputAddress;

        Assert.Equal(0, RemoteplayExports.GetConnectionStatus(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(0U, memory.ReadUInt32(OutputAddress));
    }

    [Theory]
    [InlineData("k1SwgkMSOM8", "sceRemoteplayInitialize")]
    [InlineData("BOwybKVa3Do", "sceRemoteplayTerminate")]
    [InlineData("g3PNjYKWqnQ", "sceRemoteplayGetConnectionStatus")]
    public void Registrations_AreExactGen5LlePreferred(string nid, string name)
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == nid);

        Assert.Equal(name, export.Name);
        Assert.Equal("libSceRemoteplay", export.LibraryName);
        Assert.True(export.PreferLle);
    }

    private sealed class FixedMemory : ICpuMemory
    {
        private readonly byte[] _bytes = new byte[sizeof(uint)];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (virtualAddress != OutputAddress || destination.Length > _bytes.Length)
            {
                return false;
            }

            _bytes.AsSpan(0, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (virtualAddress != OutputAddress || source.Length > _bytes.Length)
            {
                return false;
            }

            source.CopyTo(_bytes);
            return true;
        }

        public uint ReadUInt32(ulong virtualAddress)
        {
            Assert.Equal(OutputAddress, virtualAddress);
            return BinaryPrimitives.ReadUInt32LittleEndian(_bytes);
        }
    }
}
