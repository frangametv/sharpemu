// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.WebBrowserDialog;

public static class WebBrowserDialogExports
{
    private const int WebBrowserDialogErrorNotInitialized = unchecked((int)0x80B80003);

    [SysAbiExport(
        Nid = "ocHtyBwHfys",
        ExportName = "sceWebBrowserDialogTerminate",
        Target = Generation.Gen5,
        LibraryName = "libSceWebBrowserDialog",
        PreferLle = true)]
    public static int WebBrowserDialogTerminate(CpuContext ctx)
    {
        // The provider takes no arguments and returns NOT_INITIALIZED before
        // touching any dialog resources when its global service object is null.
        // SharpEmu has no HLE browser-dialog initializer, so that is the exact
        // safe fallback state whenever the guest provider cannot be used.
        return ctx.SetReturn(WebBrowserDialogErrorNotInitialized);
    }
}
