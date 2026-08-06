// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Text;

namespace SharpEmu.Libs.Kernel;

public static class KernelSystemServiceCompatExports
{
    private const int ProcessNameCapacity = 32;
    private const int MaxDebugTextBytes = 16 * 1024;

    [SysAbiExport(
        Nid = "fUJRLEbJOuQ",
        ExportName = "sceKernelGetProcessName",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetProcessName(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rsi];
        if (outputAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        Span<byte> processName = stackalloc byte[ProcessNameCapacity];
        Encoding.UTF8.GetBytes("eboot.bin", processName);
        return ctx.Memory.TryWrite(outputAddress, processName)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    [SysAbiExport(
        Nid = "-W4xI5aVI8w",
        ExportName = "sceKernelSetProcessProperty",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSetProcessProperty(CpuContext ctx)
    {
        _ = ctx;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "9JYNqN6jAKI",
        ExportName = "sceKernelDebugOutText",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDebugOutText(CpuContext ctx)
    {
        var textAddress = ctx[CpuRegister.Rsi];
        var requestedLength = ctx[CpuRegister.Rdx];
        var length = unchecked((int)Math.Min(requestedLength, MaxDebugTextBytes));
        if (textAddress == 0 || length <= 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var buffer = new byte[length];
        if (!ctx.Memory.TryRead(textAddress, buffer))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var text = Encoding.UTF8.GetString(buffer).TrimEnd('\0', '\r', '\n');
        if (text.Length != 0)
        {
            Console.Error.WriteLine($"[GUEST][DEBUG] {text}");
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "mPYKD12UDQI",
        ExportName = "sceRegMgrGetInt",
        Target = Generation.Gen5,
        LibraryName = "libSceRegMgr")]
    public static int RegMgrGetInt(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rsi];
        if (outputAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        return ctx.TryWriteUInt32(outputAddress, 0)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    [SysAbiExport(
        Nid = "COqIT6fJpzc",
        ExportName = "sceSystemTtsIsAccessibilityAvailable",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemTtsWrapper")]
    public static int SystemTtsIsAccessibilityAvailable(CpuContext ctx)
    {
        _ = ctx;
        return 0;
    }

    [SysAbiExport(
        Nid = "Rf0G+91hdUA",
        ExportName = "sceSystemTtsIsAccessibilityAvailableA",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemTtsWrapper")]
    public static int SystemTtsIsAccessibilityAvailableA(CpuContext ctx) =>
        SystemTtsIsAccessibilityAvailable(ctx);

    // The managed PUI speech wrapper resolves this entry point dynamically.
    // Firmware 12.70 forwards (callback, userData) to the SystemTts singleton's
    // virtual register method. Until the speech service is emulated, accepting
    // registration is sufficient because accessibility is reported unavailable.
    [SysAbiExport(
        Nid = "up9Z19akYXM",
        ExportName = "sceSystemTtsRegisterCallback",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemTtsWrapper")]
    public static int SystemTtsRegisterCallback(CpuContext ctx)
    {
        _ = ctx[CpuRegister.Rdi];
        _ = ctx[CpuRegister.Rsi];
        return 0;
    }

    [SysAbiExport(
        Nid = "a05rlp573ow",
        ExportName = "sceSystemTtsUnregisterCallback",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemTtsWrapper")]
    public static int SystemTtsUnregisterCallback(CpuContext ctx)
    {
        _ = ctx[CpuRegister.Rdi];
        return 0;
    }
}
