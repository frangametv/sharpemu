// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcDriverHsOffchipParamTests
{
    private const string Nid = "MM4IZSEYytQ";
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong GtaRingAddress = 0x3_11F0_0200;
    private const int InvalidDriverArgument = unchecked((int)0x8A6DFFFF);

    [Fact]
    public void SetHsOffchipParam_RegistersExactGen5AgcDriverIdentity()
    {
        var gen5 = CreateManager(Generation.Gen5);
        var gen4 = CreateManager(Generation.Gen4);

        Assert.True(gen5.TryGetExport(Nid, out var export));
        Assert.Equal("sceAgcDriverSetHsOffchipParam", export.Name);
        Assert.Equal("libSceAgcDriver", export.LibraryName);
        Assert.False(gen4.TryGetExport(Nid, out _));
    }

    [Fact]
    public void SetHsOffchipParam_GtaArgumentsRecordStateAndDoNotTouchGuestMemory()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x400);
        ConfigureTfRing(memory);
        var before = new byte[0x400];
        Array.Fill(before, (byte)0xA5);
        Assert.True(memory.TryWrite(BaseAddress, before));
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0x1FF;
        var manager = CreateManager(Generation.Gen5);

        Assert.True(manager.TryDispatch(Nid, ctx, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

        var after = new byte[before.Length];
        Assert.True(memory.TryRead(BaseAddress, after));
        Assert.Equal(before, after);
        Assert.True(AgcExports.TryGetDriverHsOffchipParamState(
            memory,
            out var first,
            out var second,
            out var payload));
        Assert.Equal((ushort)0, first);
        Assert.Equal((ushort)0x1FF, second);
        Assert.Equal(0x0000_01FFU, payload);
    }

    [Fact]
    public void SetHsOffchipParam_TruncatesBothScalarsAndPreservesPayloadOrder()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x100);
        ConfigureTfRing(memory);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0xABCD_1234;
        ctx[CpuRegister.Rsi] = 0x5678_FEDC;

        Assert.Equal(0, AgcExports.DriverSetHsOffchipParam(ctx));
        Assert.True(AgcExports.TryGetDriverHsOffchipParamState(
            memory,
            out var first,
            out var second,
            out var payload));
        Assert.Equal((ushort)0x1234, first);
        Assert.Equal((ushort)0xFEDC, second);
        Assert.Equal(0x1234_FEDCU, payload);
    }

    [Fact]
    public void SetHsOffchipParam_MissingSubmittedStateReturnsFirmwareErrorWithoutCreatingState()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x100);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0x1FF;

        Assert.Equal(InvalidDriverArgument, AgcExports.DriverSetHsOffchipParam(ctx));
        Assert.Equal(unchecked((ulong)InvalidDriverArgument), ctx[CpuRegister.Rax]);
        Assert.False(AgcExports.TryGetDriverHsOffchipParamState(
            memory,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void SetHsOffchipParam_ReplacesPairAndFailedOtherProcessPreservesPriorState()
    {
        var configuredMemory = new FakeCpuMemory(BaseAddress, 0x100);
        ConfigureTfRing(configuredMemory);
        var configuredContext = new CpuContext(configuredMemory, Generation.Gen5);
        configuredContext[CpuRegister.Rdi] = 0x1111;
        configuredContext[CpuRegister.Rsi] = 0x2222;
        Assert.Equal(0, AgcExports.DriverSetHsOffchipParam(configuredContext));

        configuredContext[CpuRegister.Rdi] = 0x3333;
        configuredContext[CpuRegister.Rsi] = 0x4444;
        Assert.Equal(0, AgcExports.DriverSetHsOffchipParam(configuredContext));

        var unavailableMemory = new FakeCpuMemory(BaseAddress, 0x100);
        var unavailableContext = new CpuContext(unavailableMemory, Generation.Gen5);
        unavailableContext[CpuRegister.Rdi] = 0xAAAA;
        unavailableContext[CpuRegister.Rsi] = 0xBBBB;
        Assert.Equal(InvalidDriverArgument, AgcExports.DriverSetHsOffchipParam(unavailableContext));

        Assert.True(AgcExports.TryGetDriverHsOffchipParamState(
            configuredMemory,
            out var first,
            out var second,
            out var payload));
        Assert.Equal((ushort)0x3333, first);
        Assert.Equal((ushort)0x4444, second);
        Assert.Equal(0x3333_4444U, payload);
        Assert.False(AgcExports.TryGetDriverHsOffchipParamState(
            unavailableMemory,
            out _,
            out _,
            out _));
    }

    [Fact]
    public async Task SetHsOffchipParam_ConcurrentUpdatesNeverExposeTornPairs()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x100);
        ConfigureTfRing(memory);
        var violations = new ConcurrentQueue<(ushort First, ushort Second, uint Payload)>();
        var writersDone = false;

        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref writersDone))
            {
                if (AgcExports.TryGetDriverHsOffchipParamState(
                        memory,
                        out var first,
                        out var second,
                        out var payload) &&
                    (ushort)~first != second)
                {
                    violations.Enqueue((first, second, payload));
                }
            }
        });

        var writers = Enumerable.Range(1, 8).Select(worker => Task.Run(() =>
        {
            var first = (ushort)(worker * 0x1111);
            var second = (ushort)~first;
            var ctx = new CpuContext(memory, Generation.Gen5);
            for (var iteration = 0; iteration < 2_000; iteration++)
            {
                ctx[CpuRegister.Rdi] = first;
                ctx[CpuRegister.Rsi] = second;
                Assert.Equal(0, AgcExports.DriverSetHsOffchipParam(ctx));
            }
        })).ToArray();

        try
        {
            await Task.WhenAll(writers);
        }
        finally
        {
            Volatile.Write(ref writersDone, true);
            await reader;
        }

        Assert.Empty(violations);
        Assert.True(AgcExports.TryGetDriverHsOffchipParamState(
            memory,
            out var finalFirst,
            out var finalSecond,
            out var finalPayload));
        Assert.Equal((ushort)~finalFirst, finalSecond);
        Assert.Equal((uint)finalSecond | ((uint)finalFirst << 16), finalPayload);
    }

    private static ModuleManager CreateManager(Generation generation)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));
        return manager;
    }

    private static void ConfigureTfRing(FakeCpuMemory memory)
    {
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = GtaRingAddress;
        ctx[CpuRegister.Rsi] = 0x4000;
        Assert.Equal(0, AgcExports.DriverSetTfRing(ctx));
    }
}
