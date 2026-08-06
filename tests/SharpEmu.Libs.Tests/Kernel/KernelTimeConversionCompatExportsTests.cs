// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelTimeConversionCompatExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong TimeAddress = MemoryBase + 0x100;
    private const ulong TmAddress = MemoryBase + 0x200;
    private const ulong FormatAddress = MemoryBase + 0x300;
    private const ulong OutputAddress = MemoryBase + 0x400;

    [Fact]
    public void Gmtime_RegistersAndReturnsOrbisTmLayout()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(context.TryWriteUInt64(TimeAddress, 0));
        context[CpuRegister.Rdi] = TimeAddress;

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("1mecP7RgI2A", out var export));
        Assert.Equal("gmtime", export.Name);
        Assert.True(manager.TryDispatch("1mecP7RgI2A", context, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);

        var tm = unchecked((nint)context[CpuRegister.Rax]);
        Assert.NotEqual(0, tm);
        Assert.Equal(0, Marshal.ReadInt32(tm, 0));
        Assert.Equal(0, Marshal.ReadInt32(tm, 4));
        Assert.Equal(0, Marshal.ReadInt32(tm, 8));
        Assert.Equal(1, Marshal.ReadInt32(tm, 12));
        Assert.Equal(0, Marshal.ReadInt32(tm, 16));
        Assert.Equal(70, Marshal.ReadInt32(tm, 20));
        Assert.Equal(4, Marshal.ReadInt32(tm, 24));
        Assert.Equal(0, Marshal.ReadInt32(tm, 28));
        Assert.Equal(0, Marshal.ReadInt32(tm, 32));
    }

    [Fact]
    public void Mktime_UsesHostLocalOffsetAndNormalizesTm()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        Span<byte> tm = stackalloc byte[48];
        tm[36..].Fill(0xA5);
        BinaryPrimitives.WriteInt32LittleEndian(tm[12..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(tm[20..], 126);
        BinaryPrimitives.WriteInt32LittleEndian(tm[32..], 0);
        Assert.True(memory.TryWrite(TmAddress, tm));

        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = TmAddress;

        var result = KernelRuntimeCompatExports.LibcMktime(context);

        var localTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var expected = new DateTimeOffset(localTime, TimeZoneInfo.Local.GetUtcOffset(localTime))
            .ToUnixTimeSeconds();
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(unchecked((ulong)expected), context[CpuRegister.Rax]);

        Span<byte> normalized = stackalloc byte[48];
        Assert.True(memory.TryRead(TmAddress, normalized));
        Assert.Equal((int)localTime.DayOfWeek, BinaryPrimitives.ReadInt32LittleEndian(normalized[24..]));
        Assert.Equal(localTime.DayOfYear - 1, BinaryPrimitives.ReadInt32LittleEndian(normalized[28..]));
        Assert.All(normalized[36..].ToArray(), value => Assert.Equal(0xA5, value));
    }

    [Fact]
    public void Localtime_RegistersAndReturnsLocalOrbisTmLayout()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(context.TryWriteUInt64(TimeAddress, 0));
        context[CpuRegister.Rdi] = TimeAddress;

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("efhK-YSUYYQ", out var export));
        Assert.Equal("localtime", export.Name);
        Assert.True(manager.TryDispatch("efhK-YSUYYQ", context, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);

        var expected = TimeZoneInfo.ConvertTime(DateTimeOffset.UnixEpoch, TimeZoneInfo.Local);
        var tm = unchecked((nint)context[CpuRegister.Rax]);
        Assert.NotEqual(0, tm);
        Assert.Equal(expected.Second, Marshal.ReadInt32(tm, 0));
        Assert.Equal(expected.Minute, Marshal.ReadInt32(tm, 4));
        Assert.Equal(expected.Hour, Marshal.ReadInt32(tm, 8));
        Assert.Equal(expected.Day, Marshal.ReadInt32(tm, 12));
        Assert.Equal(expected.Month - 1, Marshal.ReadInt32(tm, 16));
        Assert.Equal(expected.Year - 1900, Marshal.ReadInt32(tm, 20));
        Assert.Equal((int)expected.DayOfWeek, Marshal.ReadInt32(tm, 24));
        Assert.Equal(expected.DayOfYear - 1, Marshal.ReadInt32(tm, 28));
        Assert.Equal(
            TimeZoneInfo.Local.IsDaylightSavingTime(expected.DateTime) ? 1 : 0,
            Marshal.ReadInt32(tm, 32));
    }

    [Fact]
    public void Difftime_RegistersAndReturnsDoubleInXmm0()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unchecked((ulong)-10L);
        context[CpuRegister.Rsi] = unchecked((ulong)-75L);
        context.SetXmmRegister(0, ulong.MaxValue, ulong.MaxValue);

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("-VVn74ZyhEs", out var export));
        Assert.Equal("difftime", export.Name);
        Assert.True(manager.TryDispatch("-VVn74ZyhEs", context, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);

        context.GetXmmRegister(0, out var low, out var high);
        Assert.Equal(65d, BitConverter.Int64BitsToDouble(unchecked((long)low)));
        Assert.Equal(0UL, high);
    }

    [Fact]
    public void Pow_RegistersAndReturnsDoubleInXmm0()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context.SetXmmRegister(0, unchecked((ulong)BitConverter.DoubleToInt64Bits(2d)), 0);
        context.SetXmmRegister(1, unchecked((ulong)BitConverter.DoubleToInt64Bits(8d)), 0);

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("9LCjpWyQ5Zc", out var export));
        Assert.Equal("pow", export.Name);
        Assert.True(manager.TryDispatch("9LCjpWyQ5Zc", context, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);

        context.GetXmmRegister(0, out var low, out var high);
        Assert.Equal(256d, BitConverter.Int64BitsToDouble(unchecked((long)low)));
        Assert.Equal(0UL, high);
    }

    [Fact]
    public void XtimeGetTicks_RegistersAndReturnsUnixEpochHundredNanosecondTicks()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var before = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("Cj+Fw5q1tUo", out var export));
        Assert.Equal("_Xtime_get_ticks", export.Name);
        Assert.True(manager.TryDispatch("Cj+Fw5q1tUo", context, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);

        var after = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
        Assert.InRange(unchecked((long)context[CpuRegister.Rax]), before, after);
    }

    [Fact]
    public void Strftime_RegistersAndFormatsOrbisTm()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        Span<byte> tm = stackalloc byte[36];
        BinaryPrimitives.WriteInt32LittleEndian(tm[0..], 6);
        BinaryPrimitives.WriteInt32LittleEndian(tm[4..], 5);
        BinaryPrimitives.WriteInt32LittleEndian(tm[8..], 4);
        BinaryPrimitives.WriteInt32LittleEndian(tm[12..], 17);
        BinaryPrimitives.WriteInt32LittleEndian(tm[16..], 6);
        BinaryPrimitives.WriteInt32LittleEndian(tm[20..], 126);
        BinaryPrimitives.WriteInt32LittleEndian(tm[24..], 5);
        BinaryPrimitives.WriteInt32LittleEndian(tm[28..], 197);
        BinaryPrimitives.WriteInt32LittleEndian(tm[32..], 1);
        Assert.True(memory.TryWrite(TmAddress, tm));
        Assert.True(memory.TryWrite(FormatAddress, Encoding.UTF8.GetBytes("%Y-%m-%d %H:%M:%S %z [%Z]\0")));

        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = OutputAddress;
        context[CpuRegister.Rsi] = 128;
        context[CpuRegister.Rdx] = FormatAddress;
        context[CpuRegister.Rcx] = TmAddress;

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("Av3zjWi64Kw", out var export));
        Assert.Equal("strftime", export.Name);
        Assert.True(manager.TryDispatch("Av3zjWi64Kw", context, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);

        var length = checked((int)context[CpuRegister.Rax]);
        Assert.InRange(length, 29, 96);
        var rendered = new byte[length];
        Assert.True(memory.TryRead(OutputAddress, rendered));
        var text = Encoding.UTF8.GetString(rendered);
        var localDate = new DateTime(2026, 7, 17, 4, 5, 6, DateTimeKind.Unspecified);
        var offset = TimeZoneInfo.Local.GetUtcOffset(localDate);
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var absoluteOffset = offset.Duration();
        var expectedPrefix = $"2026-07-17 04:05:06 {sign}{absoluteOffset.Hours:D2}{absoluteOffset.Minutes:D2} [";
        Assert.StartsWith(expectedPrefix, text, StringComparison.Ordinal);
        Assert.EndsWith("]", text, StringComparison.Ordinal);
    }
}
