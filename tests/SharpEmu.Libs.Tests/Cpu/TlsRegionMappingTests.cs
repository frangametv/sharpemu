// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class TlsRegionMappingTests
{
    private const ulong RuntimeTlsSize = 0x10000UL;
    private const ulong GtaVStaticTlsSpan = 0x13570UL;

    [Fact]
    public void MappingConsumersUseSharedStaticTlsReservation()
    {
        var dispatcherPrefix = typeof(CpuDispatcher).GetField(
            "TlsPrefixSize",
            BindingFlags.Static | BindingFlags.NonPublic);
        var directPrefix = typeof(DirectExecutionBackend).GetField(
            "GuestThreadTlsPrefixSize",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(dispatcherPrefix);
        Assert.NotNull(directPrefix);

        Assert.Equal(
            GuestTlsTemplate.StartupStaticTlsReservation,
            Assert.IsType<ulong>(dispatcherPrefix.GetRawConstantValue()));
        Assert.Equal(
            GuestTlsTemplate.StartupStaticTlsReservation,
            Assert.IsType<ulong>(directPrefix.GetRawConstantValue()));
    }

    [Fact]
    public void CpuDispatcherMapsSharedStaticTlsReservation()
    {
        var memory = new VirtualMemory();
        using var dispatcher = new CpuDispatcher(memory, new ModuleManager());
        var mapTls = typeof(CpuDispatcher).GetMethod(
            "TryMapTlsRegion",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(mapTls);

        var tlsBase = Assert.IsType<ulong>(mapTls.Invoke(dispatcher, null));

        Assert.NotEqual(0UL, tlsBase);
        AssertMappedTlsRegion(memory, tlsBase);
    }

    [Fact]
    public void DirectExecutionBackendMapsSharedStaticTlsReservation()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            // DirectExecutionBackend's production callback thunk is x86-64;
            // its shared mapping constant is still verified above on all hosts.
            return;
        }

        var memory = new VirtualMemory();
        var mapTls = typeof(DirectExecutionBackend).GetMethod(
            "TryMapGuestThreadTlsRegion",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(mapTls);
        object?[] arguments = [memory, 0UL, null];

        var mapped = Assert.IsType<bool>(mapTls.Invoke(null, arguments));
        var tlsBase = Assert.IsType<ulong>(arguments[1]);

        Assert.True(mapped);
        Assert.NotEqual(0UL, tlsBase);
        Assert.Null(arguments[2]);
        AssertMappedTlsRegion(memory, tlsBase);
    }

    private static void AssertMappedTlsRegion(VirtualMemory memory, ulong tlsBase)
    {
        var region = Assert.Single(memory.SnapshotRegions());
        Assert.Equal(
            tlsBase - GuestTlsTemplate.StartupStaticTlsReservation,
            region.VirtualAddress);
        Assert.Equal(
            RuntimeTlsSize + GuestTlsTemplate.StartupStaticTlsReservation,
            region.MemorySize);
        Assert.True(tlsBase - GtaVStaticTlsSpan >= region.VirtualAddress);
        Assert.True(tlsBase - GtaVStaticTlsSpan < region.VirtualAddress + region.MemorySize);
        Assert.Equal(0UL, region.VirtualAddress & 0xFFFFUL);
    }
}
