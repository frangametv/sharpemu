// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// sceKernelGetTscFrequency must describe the same clock that sceKernelReadTsc returns. ReadTsc
// only returns the CPU's RDTSC when the host RDTSC reader is available (64-bit Windows) and
// otherwise falls back to the QPC-based Stopwatch, so the frequency selection has to follow suit.
public sealed class KernelRuntimeCompatExportsTests
{
    [Fact]
    public void GetModuleInfoFromAddr_Fills1270UnwindLayoutWithoutCallerSize()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong infoAddress = memoryBase + 0x100;
        const ulong moduleBase = 0x8_0000_0000;
        const ulong moduleSize = 0x20_0000;
        const ulong entryPoint = moduleBase + 0x220;
        const ulong ehFrameHeader = moduleBase + 0x12000;
        const ulong ehFrame = moduleBase + 0x10000;
        const ulong ehFrameSize = 0x3456;

        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        Span<byte> callerBuffer = stackalloc byte[0x1A8];
        callerBuffer.Fill(0xCC);
        // Mono's 12.70 caller does not initialize the output structure. This
        // value is the exact first qword observed in the NPXS40001 boot trace.
        BinaryPrimitives.WriteUInt64LittleEndian(callerBuffer, 0x7AC4);
        Assert.True(memory.TryWrite(infoAddress, callerBuffer));

        KernelModuleRegistry.Reset();
        try
        {
            var handle = KernelModuleRegistry.RegisterModule(
                "mscorlib.dll.sprx",
                moduleBase,
                moduleSize,
                entryPoint,
                initEntryPoint: moduleBase + 0x100,
                ehFrameHeader,
                ehFrame,
                ehFrameSize,
                isMain: false);
            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rdi] = entryPoint;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = infoAddress;

            var result = KernelRuntimeCompatExports.KernelGetModuleInfoFromAddr(context);

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
            Assert.Equal(0UL, context[CpuRegister.Rax]);
            Span<byte> info = stackalloc byte[0x1A8];
            Assert.True(memory.TryRead(infoAddress, info));
            Assert.Equal(0x1A8UL, BinaryPrimitives.ReadUInt64LittleEndian(info));
            Assert.Equal(
                "mscorlib.dll.sprx",
                Encoding.UTF8.GetString(info.Slice(0x08, 256)).TrimEnd('\0'));
            Assert.Equal(handle, BinaryPrimitives.ReadInt32LittleEndian(info[0x108..]));
            Assert.Equal(ehFrameHeader, BinaryPrimitives.ReadUInt64LittleEndian(info[0x148..]));
            Assert.Equal(ehFrame, BinaryPrimitives.ReadUInt64LittleEndian(info[0x150..]));
            Assert.Equal((uint)ehFrameSize, BinaryPrimitives.ReadUInt32LittleEndian(info[0x15C..]));
            Assert.Equal(moduleBase, BinaryPrimitives.ReadUInt64LittleEndian(info[0x160..]));
            Assert.Equal((uint)moduleSize, BinaryPrimitives.ReadUInt32LittleEndian(info[0x168..]));
            Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(info[0x16C..]));
            Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(info[0x1A0..]));
        }
        finally
        {
            KernelModuleRegistry.Reset();
        }
    }

    private static KernelRuntimeCompatExports.TryGetFrequency Yields(ulong hz) =>
        (out ulong frequencyHz) =>
        {
            frequencyHz = hz;
            return true;
        };

    private static readonly KernelRuntimeCompatExports.TryGetFrequency Fails =
        (out ulong frequencyHz) =>
        {
            frequencyHz = 0;
            return false;
        };

    [Fact]
    public void WithoutHostRdtsc_ReportsStopwatchFrequency_NotHardwareTsc()
    {
        // Regression: on Linux/macOS ReadTsc returns the Stopwatch counter, so the reported
        // frequency must be the Stopwatch's, never the CPU's much larger hardware TSC frequency.
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: false,
            overrideHzText: null,
            tryCalibrate: Yields(2_400_000_000UL),
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(10_000_000UL, frequencyHz);
        Assert.Equal("qpc", source);
    }

    [Fact]
    public void WithHostRdtsc_PrefersCalibratedFrequency()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: true,
            overrideHzText: null,
            tryCalibrate: Yields(2_400_000_000UL),
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(2_400_000_000UL, frequencyHz);
        Assert.Equal("calibrated-rdtsc", source);
    }

    [Fact]
    public void WithHostRdtsc_FallsBackToCpuid_WhenCalibrationFails()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: true,
            overrideHzText: null,
            tryCalibrate: Fails,
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(3_000_000_000UL, frequencyHz);
        Assert.Equal("cpuid", source);
    }

    [Fact]
    public void WithHostRdtsc_UsesStopwatch_WhenRdtscFrequencyUnknown()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: true,
            overrideHzText: null,
            tryCalibrate: Fails,
            tryResolveCpuid: Fails,
            stopwatchFrequency: 10_000_000);

        Assert.Equal(10_000_000UL, frequencyHz);
        Assert.Equal("qpc", source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnvOverride_Wins_WhenSane(bool rdtscAvailable)
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable,
            overrideHzText: "1500000000",
            tryCalibrate: Yields(2_400_000_000UL),
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(1_500_000_000UL, frequencyHz);
        Assert.Equal("env", source);
    }

    [Fact]
    public void EnvOverride_BelowMinimum_IsIgnored()
    {
        // 500 kHz is below the sanity floor, so it is dropped; with rdtsc unavailable the
        // hardware-TSC path is gated off and the Stopwatch frequency is used.
        var (frequencyHz, _) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: false,
            overrideHzText: "500000",
            tryCalibrate: Fails,
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(10_000_000UL, frequencyHz);
    }

    [Fact]
    public void NonPositiveStopwatchFrequency_FallsBackToDefault()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: false,
            overrideHzText: null,
            tryCalibrate: Fails,
            tryResolveCpuid: Fails,
            stopwatchFrequency: 0);

        Assert.Equal(10_000_000UL, frequencyHz); // DefaultKernelTscFrequency
        Assert.Equal("qpc", source);
    }
}
