// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.VideoRecording;

public static class VideoRecordingExports
{
    private const int ErrorNotInitialized = unchecked((int)0x80A80002);
    private const int ErrorInvalidArgument = unchecked((int)0x80A80003);
    private const uint MaximumMetadataSize = 0x800;

    // Game-facing dev provider libSceVideoRecording.sprx SHA-256
    // ab1a4c51eb868db75dae2e11ace9b67ff2c638472e5ffcc514416155204da560,
    // RVA 0x7f20. Types 2, 6-8, and 0xa01 forward metadata to the native provider
    // without writing guest memory. GTA ignores the three initialization returns.
    [SysAbiExport(
        Nid = "Fc8qxlKINYQ",
        ExportName = "sceVideoRecordingSetInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceVideoRecordingP",
        PreferLle = true)]
    public static int VideoRecordingSetInfo(CpuContext ctx)
    {
        var infoType = (uint)ctx[CpuRegister.Rdi];
        if (!IsMetadataInfoType(infoType))
        {
            return ctx.SetReturn(IsKnownRecordingDataType(infoType)
                ? ErrorNotInitialized
                : ErrorInvalidArgument);
        }

        var infoAddress = ctx[CpuRegister.Rsi];
        var infoSize = ctx[CpuRegister.Rdx];
        if (infoAddress == 0 || infoSize > MaximumMetadataSize)
        {
            return ctx.SetReturn(ErrorInvalidArgument);
        }

        if (infoSize != 0)
        {
            var metadata = new byte[checked((int)infoSize)];
            if (!ctx.Memory.TryRead(infoAddress, metadata))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static bool IsMetadataInfoType(uint infoType) =>
        infoType is 2 or 6 or 7 or 8 or 0xA01;

    private static bool IsKnownRecordingDataType(uint infoType) =>
        infoType is 0xA004 or 0xA005 or 0xA006 or 0xA007 or 0xA008 or 0xA009;
}
