// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
//
// Ghidra 12.1.2_PUBLIC_20260605 program: libSceNpWebApi2.sprx
// Analyzed provider SHA-256: 9a7099ab99a65f1b818a8e61897ffcb31b02b74d6e0c2765688b4c3b71ed4ca5
// Provider registrations were recovered by Acelogic. Semantic HLE fallbacks
// maintained by Fran's fork are split out when a game exercises their ABI.

using SharpEmu.HLE;
using SharpEmu.Libs.Np;

namespace SharpEmu.Libs.Lle;

public static class NpWebApi2LleExports
{
    // Ghidra entry 00004be0; body addresses 73.
    [SysAbiExport(
        Nid = "3EI-OSJ65Xc",
        ExportName = "sceNpWebApi2CreateRequest",
        Target = Generation.Gen5,
        LibraryName = "libSceNpWebApi2",
        PreferLle = true)]
    public static int CreateRequestWithoutGuestProvider(CpuContext ctx) =>
        NpWebApi2Exports.NpWebApi2CreateRequest(ctx);

    // Ghidra entry 00004e80; body addresses 28.
    [SysAbiExport(
        Nid = "HwP3aM+c85c",
        ExportName = "sceNpWebApi2GetHttpResponseHeaderValueLength",
        Target = Generation.Gen5,
        LibraryName = "libSceNpWebApi2",
        PreferLle = true)]
    public static int GetHttpResponseHeaderValueLengthWithoutGuestProvider(CpuContext ctx) =>
        NpWebApi2Exports.NpWebApi2GetHttpResponseHeaderValueLength(ctx);

    // Ghidra entry 00004e60; body addresses 20.
    [SysAbiExport(
        Nid = "egOOvrnF6mI",
        ExportName = "sceNpWebApi2AddHttpRequestHeader",
        Target = Generation.Gen5,
        LibraryName = "libSceNpWebApi2",
        PreferLle = true)]
    public static int AddHttpRequestHeaderWithoutGuestProvider(CpuContext ctx) =>
        NpWebApi2Exports.NpWebApi2AddHttpRequestHeader(ctx);

    // Ghidra entry 00004b90; direct provider thunk.
    [SysAbiExport(
        Nid = "9X9+cneTGUU",
        ExportName = "sceNpWebApi2DeleteUserContext",
        Target = Generation.Gen5,
        LibraryName = "libSceNpWebApi2",
        PreferLle = true)]
    public static int DeleteUserContextWithoutGuestProvider(CpuContext ctx) =>
        NpWebApi2Exports.NpWebApi2DeleteUserContext(ctx);

    // Ghidra entry 00004e20; provider validates RSI and RDX.
    [SysAbiExport(
        Nid = "OOY9+ObfKec",
        ExportName = "sceNpWebApi2ReadData",
        Target = Generation.Gen5,
        LibraryName = "libSceNpWebApi2",
        PreferLle = true)]
    public static int ReadDataWithoutGuestProvider(CpuContext ctx) =>
        NpWebApi2Exports.NpWebApi2ReadData(ctx);

    // Ghidra entry 000080f0; body addresses 128.
    [SysAbiExport(
        Nid = "AAj9X+4aGYA",
        ExportName = "sceNpWebApi2PushEventStartPushContextCallback",
        Target = Generation.Gen5,
        LibraryName = "libSceNpWebApi2",
        PreferLle = true)]
    // Ghidra entry 00008000; body addresses 5.
    [SysAbiExport(
        Nid = "KJdPcOGmK58",
        ExportName = "sceNpWebApi2PushEventDeleteFilter",
        Target = Generation.Gen5,
        LibraryName = "libSceNpWebApi2",
        PreferLle = true)]
    // Ghidra entry 000080d0; body addresses 128.
    [SysAbiExport(
        Nid = "NNVf18SlbT8",
        ExportName = "sceNpWebApi2PushEventCreatePushContext",
        Target = Generation.Gen5,
        LibraryName = "libSceNpWebApi2",
        PreferLle = true)]
    // Ghidra entry 000080e0; body addresses 144.
    [SysAbiExport(
        Nid = "QafxeZM3WK4",
        ExportName = "sceNpWebApi2PushEventDeletePushContext",
        Target = Generation.Gen5,
        LibraryName = "libSceNpWebApi2",
        PreferLle = true)]
    // Ghidra entry 00007f70; body addresses 5.
    [SysAbiExport(
        Nid = "fIATVMo4Y1w",
        ExportName = "sceNpWebApi2PushEventDeleteHandle",
        Target = Generation.Gen5,
        LibraryName = "libSceNpWebApi2",
        PreferLle = true)]
    // Ghidra entry 00008010; body addresses 212.
    [SysAbiExport(
        Nid = "fY3QqeNkF8k",
        ExportName = "sceNpWebApi2PushEventRegisterCallback",
        Target = Generation.Gen5,
        LibraryName = "libSceNpWebApi2",
        PreferLle = true)]
    // Ghidra entry 00008020; body addresses 5.
    [SysAbiExport(
        Nid = "hOnIlcGrO6g",
        ExportName = "sceNpWebApi2PushEventUnregisterCallback",
        Target = Generation.Gen5,
        LibraryName = "libSceNpWebApi2",
        PreferLle = true)]
    public static int MissingGuestProvider(CpuContext ctx)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_IMPORTS"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("[LOADER][ERROR] NpWebApi2 LLE-preferred export reached its fail-closed HLE fallback");
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED);
    }

    // Ghidra entry 00004ea0; body addresses 29.
    [SysAbiExport(
        Nid = "hksbskNToEA",
        ExportName = "sceNpWebApi2GetHttpResponseHeaderValue",
        Target = Generation.Gen5,
        LibraryName = "libSceNpWebApi2",
        PreferLle = true)]
    public static int GetHttpResponseHeaderValueWithoutGuestProvider(CpuContext ctx) =>
        NpWebApi2Exports.NpWebApi2GetHttpResponseHeaderValue(ctx);

    // Ghidra entry 00004d70; body addresses 59.
    [SysAbiExport(
        Nid = "lQOCF84lvzw",
        ExportName = "sceNpWebApi2SendRequest",
        Target = Generation.Gen5,
        LibraryName = "libSceNpWebApi2",
        PreferLle = true)]
    public static int SendRequestWithoutGuestProvider(CpuContext ctx) =>
        NpWebApi2Exports.NpWebApi2SendRequest(ctx);

    // Ghidra entry 00004d60; body addresses 5.
    [SysAbiExport(
        Nid = "vvzWO-DvG1s",
        ExportName = "sceNpWebApi2DeleteRequest",
        Target = Generation.Gen5,
        LibraryName = "libSceNpWebApi2",
        PreferLle = true)]
    public static int DeleteRequestWithoutGuestProvider(CpuContext ctx) =>
        NpWebApi2Exports.NpWebApi2DeleteRequest(ctx);

    // Ghidra entry 00004ec0; body addresses 5.
    [SysAbiExport(
        Nid = "zpiPsH7dbFQ",
        ExportName = "sceNpWebApi2AbortRequest",
        Target = Generation.Gen5,
        LibraryName = "libSceNpWebApi2",
        PreferLle = true)]
    public static int AbortRequestWithoutGuestProvider(CpuContext ctx) =>
        NpWebApi2Exports.NpWebApi2AbortRequest(ctx);

}
