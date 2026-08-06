// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VideoOutSystemStatusTests
{
    private const ulong BaseAddress = 0x1_0000_0000;

    [Fact]
    public void SystemStatusExportsRegisterWithTwelveSeventyNids()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        AssertExport(manager, "O57F5ikhGxo", "sceVideoOutSysIsUserStatusSystemDefault");
        AssertExport(manager, "4XsQdhiOaAc", "sceVideoOutSysIsUserStatusVr");
        AssertExport(manager, "dFhciCfO31s", "sceVideoOutSysGetPipelineStatus");
        AssertExport(manager, "qLDCAl8ygCw", "sceVideoOutSysGetResolutionStatus2");
    }

    [Fact]
    public void ResolutionStatus2WritesPsmConsumedLayout()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var statusAddress = BaseAddress + 0x100;
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = statusAddress;

        var result = VideoOutExports.VideoOutSysGetResolutionStatus2(context);

        Span<byte> status = stackalloc byte[0x20];
        Assert.True(memory.TryRead(statusAddress, status));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(1920u, BinaryPrimitives.ReadUInt32LittleEndian(status[0x00..0x04]));
        Assert.Equal(1080u, BinaryPrimitives.ReadUInt32LittleEndian(status[0x04..0x08]));
        Assert.Equal(3UL, BinaryPrimitives.ReadUInt64LittleEndian(status[0x10..0x18]));
    }

    [Fact]
    public void ShellCoreUserAndPipelineQueriesReturnNormalDisplayState()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var pipelineAddress = BaseAddress + 0x100;

        Assert.Equal(1, VideoOutExports.VideoOutSysIsUserStatusSystemDefault(context));
        Assert.Equal(0, VideoOutExports.VideoOutSysIsUserStatusVr(context));

        context[CpuRegister.Rdi] = pipelineAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            VideoOutExports.VideoOutSysGetPipelineStatus(context));
        Span<byte> pipeline = stackalloc byte[0x20];
        Assert.True(memory.TryRead(pipelineAddress, pipeline));
        Assert.True(pipeline.SequenceEqual(new byte[0x20]));
    }

    private static void AssertExport(ModuleManager manager, string nid, string name)
    {
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceVideoOut", export.LibraryName);
    }
}
