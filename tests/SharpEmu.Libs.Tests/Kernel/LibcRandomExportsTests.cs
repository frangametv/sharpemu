// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class LibcRandomExportsTests
{
    private const ulong MemoryBase = 0x2_0000_0000;
    private const ulong Multiplier = 0x5851_F42D_4C95_7F2DUL;

    public static IEnumerable<object[]> ExportCases()
    {
        yield return new object[] { "cpCOXWMgha0", "rand" };
        yield return new object[] { "VPbJwTCgME0", "srand" };
    }

    [Theory]
    [MemberData(nameof(ExportCases))]
    public void Exports_RegisterAsGen5Libc(string nid, string name)
    {
        var manager = CreateManager();

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(nid, export.Nid);
        Assert.Equal(name, export.Name);
        Assert.Equal("libc", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    [Fact]
    public void Rand_AfterSrandOneMatchesRecoveredVector()
    {
        var seedContext = CreateContext();
        seedContext[CpuRegister.Rdi] = 1;
        Dispatch("VPbJwTCgME0", "srand", seedContext);

        var expected = new ulong[]
        {
            0x1851_F42D,
            0x00B1_8CCF,
            0x0BB5_F646,
            0x0703_3129,
            0x3070_5B04,
        };
        foreach (var value in expected)
        {
            var context = CreateContext();
            Dispatch("cpCOXWMgha0", "rand", context);
            Assert.Equal(value, context[CpuRegister.Rax]);
        }
    }

    [Fact]
    public void Srand_ZeroExtendsTheLow32BitSeed()
    {
        var seedContext = CreateContext();
        seedContext[CpuRegister.Rdi] = 0xFFFF_FFFF_8000_0001UL;
        Dispatch("VPbJwTCgME0", "srand", seedContext);

        var context = CreateContext();
        Dispatch("cpCOXWMgha0", "rand", context);

        var state = unchecked((0x8000_0001UL * Multiplier) + 1UL);
        Assert.Equal((state >> 32) & 0x3FFF_FFFFUL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Rand_ConcurrentCallsDoNotLoseStateUpdates()
    {
        const uint seed = 0x1357_9BDF;
        const int concurrentCalls = 20_000;
        var seedContext = CreateContext();
        seedContext[CpuRegister.Rdi] = seed;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            LibcRandomExports.LibcSrand(seedContext));

        Parallel.For(
            0,
            concurrentCalls,
            _ =>
            {
                var context = CreateContext();
                Assert.Equal(
                    (int)OrbisGen2Result.ORBIS_GEN2_OK,
                    LibcRandomExports.LibcRand(context));
            });

        var finalContext = CreateContext();
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            LibcRandomExports.LibcRand(finalContext));

        ulong expectedState = seed;
        for (var index = 0; index <= concurrentCalls; index++)
        {
            expectedState = unchecked((expectedState * Multiplier) + 1UL);
        }

        Assert.Equal(
            (expectedState >> 32) & 0x3FFF_FFFFUL,
            finalContext[CpuRegister.Rax]);
    }

    private static ModuleManager CreateManager()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        return manager;
    }

    private static CpuContext CreateContext() =>
        new(new FakeCpuMemory(MemoryBase, 0x100), Generation.Gen5);

    private static void Dispatch(string nid, string name, CpuContext context)
    {
        var manager = CreateManager();
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libc", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
        Assert.True(manager.TryDispatch(nid, context, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
    }
}
