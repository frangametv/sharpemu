// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using System.Buffers.Binary;
using System.Threading;

namespace SharpEmu.Libs.SystemService;

public static class SystemServiceExports
{
    private const int OrbisSystemServiceErrorParameter = unchecked((int)0x80A10003);
    private const int SystemServiceStatusSize = 0x0C;
    private const int DisplaySafeAreaInfoSize = sizeof(float) + 128;
    private const int HdrToneMapLuminanceSize = sizeof(float) * 3;

    private const int TitleIdFieldSize = 0x10;

    private static string? _mainAppTitleId;
    private static int _noticeScreenSkipFlag;

    public static void ConfigureApplicationInfo(string? titleId)
    {
        _mainAppTitleId = string.IsNullOrWhiteSpace(titleId) ? null : titleId.Trim();
    }

    internal static void ResetForTests() => Volatile.Write(ref _noticeScreenSkipFlag, 0);

    [SysAbiExport(
        Nid = "3RQ5aQfnstU",
        ExportName = "sceSystemServiceGetNoticeScreenSkipFlag",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetNoticeScreenSkipFlag(CpuContext ctx)
    {
        var flagAddress = ctx[CpuRegister.Rdi];
        if (flagAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        Span<byte> flagBytes = stackalloc byte[1];
        flagBytes[0] = Volatile.Read(ref _noticeScreenSkipFlag) == 0 ? (byte)0 : (byte)1;
        return KernelMemoryCompatExports.TryWriteCompat(ctx, flagAddress, flagBytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "Q3utJvma4Mo",
        ExportName = "sceSystemServiceSetNoticeScreenSkipFlag",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService",
        PreferLle = true)]
    public static int SystemServiceSetNoticeScreenSkipFlag(CpuContext ctx)
    {
        // The Gen5 provider takes no arguments and sends operation 0x62 to the
        // system-service proxy. The flag is observable through the getter, so
        // retain that state locally when the guest provider is unavailable.
        Interlocked.Exchange(ref _noticeScreenSkipFlag, 1);
        return ctx.SetReturn(0);
    }

    // Ghidra 12.1.2_PUBLIC_20260605, libSceSystemService.sprx
    // SHA-256 6bcb5dd125d9d09b6053f383ba89b1613335bff5ab83870f0bd732b767f4aac9,
    // entry RVA 0x3A80. The provider takes no arguments and sends proxy
    // operation 0x61. SharpEmu has no automatic flag setter to disable, so the
    // faithful provider-free fallback succeeds without altering explicit state.
    [SysAbiExport(
        Nid = "8Lo6Zv94aho",
        ExportName = "sceSystemServiceDisableNoticeScreenSkipFlagAutoSet",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService",
        PreferLle = true)]
    public static int SystemServiceDisableNoticeScreenSkipFlagAutoSet(CpuContext ctx) =>
        ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "4veE0XiIugA",
        ExportName = "sceSystemServiceGetMainAppTitleId",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetMainAppTitleId(CpuContext ctx)
    {
        var titleIdAddress = ctx[CpuRegister.Rdi];
        if (titleIdAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        // Title IDs are a fixed 9-char format written into a 0x10-byte field;
        // bound the length so a malformed param.json cannot drive an unbounded
        // stack allocation or overrun the guest buffer.
        var titleId = _mainAppTitleId ?? "PPSA00000";
        var length = Math.Min(titleId.Length, TitleIdFieldSize - 1);
        Span<byte> titleIdBytes = stackalloc byte[TitleIdFieldSize];
        titleIdBytes.Clear();
        System.Text.Encoding.ASCII.GetBytes(titleId.AsSpan(0, length), titleIdBytes);
        return KernelMemoryCompatExports.TryWriteCompat(ctx, titleIdAddress, titleIdBytes[..(length + 1)])
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "fZo48un7LK4",
        ExportName = "sceSystemServiceParamGetInt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceParamGetInt(CpuContext ctx)
    {
        var parameterId = unchecked((int)ctx[CpuRegister.Rdi]);
        var valueAddress = ctx[CpuRegister.Rsi];
        if (valueAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        var value = parameterId switch
        {
            1 or 2 or 3 or 1000 => 1,
            4 => 180,
            _ => 0,
        };

        Span<byte> valueBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(valueBytes, value);
        return KernelMemoryCompatExports.TryWriteCompat(ctx, valueAddress, valueBytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "SsC-m-S9JTA",
        ExportName = "sceSystemServiceParamGetString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceParamGetString(CpuContext ctx)
    {
        _ = unchecked((int)ctx[CpuRegister.Rdi]); // parameter id (nickname, etc.)
        var bufferAddress = ctx[CpuRegister.Rsi];
        var bufferSize = unchecked((int)ctx[CpuRegister.Rdx]);
        if (bufferAddress == 0 || bufferSize <= 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        // String params are typically the user nickname. Callers that gate UI
        // or text setup on a successful read (and skip it on failure) stall on
        // a black screen when this returns NOT_FOUND, so return a neutral
        // non-empty default and success.
        var value = System.Text.Encoding.UTF8.GetBytes("SharpEmu");
        var writeLength = Math.Min(value.Length, bufferSize - 1);
        Span<byte> output = stackalloc byte[writeLength + 1];
        value.AsSpan(0, writeLength).CopyTo(output);
        output[writeLength] = 0;
        return KernelMemoryCompatExports.TryWriteCompat(ctx, bufferAddress, output)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "rPo6tV8D9bM",
        ExportName = "sceSystemServiceGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetStatus(CpuContext ctx)
    {
        var statusAddress = ctx[CpuRegister.Rdi];
        if (statusAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        Span<byte> status = stackalloc byte[SystemServiceStatusSize];
        status.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(status, 0);
        status[0x06] = 1;

        return KernelMemoryCompatExports.TryWriteCompat(ctx, statusAddress, status)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "1n37q1Bvc5Y",
        ExportName = "sceSystemServiceGetDisplaySafeAreaInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetDisplaySafeAreaInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        Span<byte> info = stackalloc byte[DisplaySafeAreaInfoSize];
        info.Clear();
        BinaryPrimitives.WriteSingleLittleEndian(info, 1.0f);

        return KernelMemoryCompatExports.TryWriteCompat(ctx, infoAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "mPpPxv5CZt4",
        ExportName = "sceSystemServiceGetHdrToneMapLuminance",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetHdrToneMapLuminance(CpuContext ctx)
    {
        var luminanceAddress = ctx[CpuRegister.Rdi];
        if (luminanceAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        Span<byte> luminance = stackalloc byte[HdrToneMapLuminanceSize];
        BinaryPrimitives.WriteSingleLittleEndian(luminance, 1000.0f);
        BinaryPrimitives.WriteSingleLittleEndian(luminance[sizeof(float)..], 1000.0f);
        BinaryPrimitives.WriteSingleLittleEndian(luminance[(sizeof(float) * 2)..], 0.01f);
        return KernelMemoryCompatExports.TryWriteCompat(ctx, luminanceAddress, luminance)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "Vo5V8KAwCmk",
        ExportName = "sceSystemServiceHideSplashScreen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceHideSplashScreen(CpuContext ctx)
    {
        GuestGpu.Current.HideSplashScreen();
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "3s8cHiCBKBE",
        ExportName = "sceSystemServiceReportAbnormalTermination",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceReportAbnormalTermination(CpuContext ctx) => ctx.SetReturn(0);
}
