// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Compositor;

public static class CompositorExports
{
    private static readonly object _stateGate = new();
    private static ulong _systemAddress;
    private static ulong _systemSize;
    private static ulong _videoAddress;
    private static ulong _videoSize;
    private static uint _nextCompositorIndex;

    [SysAbiExport(
        Nid = "IUlpGnuoR1c",
        ExportName = "sceCompositorInitWithProcessOrder",
        Target = Generation.Gen5,
        LibraryName = "libSceComposite")]
    public static int InitWithProcessOrder(CpuContext ctx)
    {
        var requestedSystemSize = ctx[CpuRegister.Rdi];
        var requestedVideoSize = ctx[CpuRegister.Rsi];
        if (requestedSystemSize == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_stateGate)
        {
            if (_systemAddress == 0 &&
                !KernelMemoryCompatExports.TryAllocateHleData(
                    ctx,
                    requestedSystemSize,
                    0x4000,
                    out _systemAddress))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            if (requestedVideoSize != 0 &&
                _videoAddress == 0 &&
                !KernelMemoryCompatExports.TryAllocateHleData(
                    ctx,
                    requestedVideoSize,
                    0x4000,
                    out _videoAddress))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            _systemSize = requestedSystemSize;
            _videoSize = requestedVideoSize;
        }

        Console.Error.WriteLine(
            $"[LOADER][INFO] compositor.init system=0x{_systemAddress:X16}+0x{_systemSize:X} " +
            $"video=0x{_videoAddress:X16}+0x{_videoSize:X} order={ctx[CpuRegister.Rcx]}");

        // Shell/PUI applications present through libSceComposite rather than
        // sceVideoOut, so they otherwise never trigger the existing host
        // presenter. Bring up the same Vulkan surface as soon as the guest OS
        // compositor is initialized; later AGC submissions can replace this
        // initial frame without a second window or a special shell-only path.
        VulkanVideoPresenter.EnsureStarted(1280, 720);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "T6CVkdCDO7o",
        ExportName = "sceCompositorGetSystemAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceComposite")]
    public static int GetSystemAddress(CpuContext ctx) => ReturnValue(ctx, _systemAddress);

    [SysAbiExport(
        Nid = "N6ID0KNnzY8",
        ExportName = "sceCompositorGetSystemSize",
        Target = Generation.Gen5,
        LibraryName = "libSceComposite")]
    public static int GetSystemSize(CpuContext ctx) => ReturnValue(ctx, _systemSize);

    [SysAbiExport(
        Nid = "bxt+muwit0w",
        ExportName = "sceCompositorGetVideoAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceComposite")]
    public static int GetVideoAddress(CpuContext ctx) => ReturnValue(ctx, _videoAddress);

    [SysAbiExport(
        Nid = "FTQCTDU0b4g",
        ExportName = "sceCompositorGetVideoSize",
        Target = Generation.Gen5,
        LibraryName = "libSceComposite")]
    public static int GetVideoSize(CpuContext ctx) => ReturnValue(ctx, _videoSize);

    [SysAbiExport(
        Nid = "G4Q8KNkb5XE",
        ExportName = "sceCompositorAllocateIndex",
        Target = Generation.Gen5,
        LibraryName = "libSceComposite")]
    public static int AllocateIndex(CpuContext ctx)
    {
        uint index;
        lock (_stateGate)
        {
            index = _nextCompositorIndex++;
        }

        ctx[CpuRegister.Rax] = index;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "N7PrM+lPMW0",
        ExportName = "sceCompositorGetCanvasHandle",
        Target = Generation.Gen5,
        LibraryName = "libSceComposite")]
    public static int GetCanvasHandle(CpuContext ctx)
    {
        var compositorIndex = unchecked((uint)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rsi];
        var canvasHandle = unchecked((ulong)compositorIndex + 1);
        if (outputAddress == 0 || !ctx.TryWriteUInt64(outputAddress, canvasHandle))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "wXnNof3LrrU",
        ExportName = "sceCompositorConfigureCanvasCompat",
        Target = Generation.Gen5,
        LibraryName = "libSceComposite")]
    public static int ConfigureCanvasCompat(CpuContext ctx)
    {
        _ = ctx;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int ReturnValue(CpuContext ctx, ulong value)
    {
        ctx[CpuRegister.Rax] = value;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
