// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Share;

public static class ShareExports
{
    private const int MaxContentParamBytes = 4096;
    private const int ShareErrorServiceUnavailable = unchecked((int)0x81960001);
    private const int ShareErrorInvalidArgument = unchecked((int)0x81960002);
    private const int ShareErrorNotInitialized = unchecked((int)0x8196000C);

    private static readonly object _contentEventGate = new();
    private static readonly List<ContentEventRegistration> _contentEventRegistrations = [];
    private static readonly HashSet<int> _permittedFeatures = [];
    private static int _initialized;
    private static int _contentEventServiceAvailable;
    private static string _contentParam = string.Empty;
    private static readonly object _callbackGate = new();
    private static ulong _contentEventCallback;

    private sealed record ContentEventRegistration(ulong Callback, ulong UserData);

    [SysAbiExport(
        Nid = "nBDD66kiFW8",
        ExportName = "sceShareInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceShareUtility")]
    public static int ShareInitialize(CpuContext ctx)
    {
        var memorySize = ctx[CpuRegister.Rdi];
        var priority = unchecked((int)ctx[CpuRegister.Rsi]);
        var affinityMask = ctx[CpuRegister.Rdx];
        if (memorySize == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (_contentEventGate)
        {
            Volatile.Write(ref _initialized, 1);
            Volatile.Write(ref _contentEventServiceAvailable, 1);
        }

        TraceShare($"initialize memory=0x{memorySize:X} priority={priority} affinity=0x{affinityMask:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "0IL1keINExQ",
        ExportName = "sceShareTerminate",
        Target = Generation.Gen5,
        LibraryName = "libSceShare")]
    public static int ShareTerminate(CpuContext ctx)
    {
        lock (_contentEventGate)
        {
            if (Volatile.Read(ref _initialized) == 0)
            {
                return SetShareReturn(ctx, ShareErrorNotInitialized);
            }

            ClearShareLifecycleUnderLock();
        }

        TraceShare("terminate");
        return SetShareReturn(ctx, 0);
    }

    // Ghidra entry 00006d00 in libSceShare.native.sprx (SHA-256
    // 02b41c8d10cc86418a7b3182a972d0a24163791eaf67a187ac6d1df531e4560d)
    // forwards the signed feature selector with command mask 0x200000000.
    // The provider returns NOT_INITIALIZED before the service call when its
    // global Share service is absent. SharpEmu's initialized local service
    // records the permit operation and completes it synchronously.
    [SysAbiExport(
        Nid = "YBiIdcDPrxs",
        ExportName = "sceShareFeaturePermit",
        Target = Generation.Gen5,
        LibraryName = "libSceShare",
        PreferLle = true)]
    public static int ShareFeaturePermit(CpuContext ctx)
    {
        var feature = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (_contentEventGate)
        {
            if (Volatile.Read(ref _initialized) == 0)
            {
                return SetShareReturn(ctx, ShareErrorNotInitialized);
            }

            _permittedFeatures.Add(feature);
        }

        TraceShare($"feature_permit feature=0x{unchecked((uint)feature):X8}");
        return SetShareReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "Sygnk9dr5WQ",
        ExportName = "sceShareRegisterContentEventCallback",
        Target = Generation.Gen5,
        LibraryName = "libSceShare")]
    public static int ShareRegisterContentEventCallback(CpuContext ctx)
    {
        var callback = ctx[CpuRegister.Rdi];
        var userData = ctx[CpuRegister.Rsi];
        if (callback == 0)
        {
            return SetShareReturn(ctx, ShareErrorInvalidArgument);
        }

        lock (_contentEventGate)
        {
            if (Volatile.Read(ref _initialized) == 0)
            {
                return SetShareReturn(ctx, ShareErrorNotInitialized);
            }

            if (Volatile.Read(ref _contentEventServiceAvailable) == 0)
            {
                return SetShareReturn(ctx, ShareErrorServiceUnavailable);
            }

            if (_contentEventRegistrations.Exists(
                    registration => registration.Callback == callback))
            {
                return SetShareReturn(ctx, ShareErrorInvalidArgument);
            }

            // Firmware owns only its 0x20 list node. The callback and userdata
            // remain unowned guest targets; invoking the guest callback requires
            // a guest-execution bridge and is intentionally outside this HLE.
            _contentEventRegistrations.Add(new ContentEventRegistration(callback, userData));
        }

        TraceShare($"register_content_event_callback callback=0x{callback:X} userdata=0x{userData:X}");
        return SetShareReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "7QZtURYnXG4",
        ExportName = "sceShareSetContentParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceShareUtility")]
    public static int ShareSetContentParam(CpuContext ctx)
    {
        var contentParamAddress = ctx[CpuRegister.Rdi];
        if (contentParamAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadNullTerminatedUtf8(ctx, contentParamAddress, MaxContentParamBytes, out var contentParam))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        _contentParam = contentParam;
        if (Volatile.Read(ref _initialized) == 0)
        {
            TraceShare("set_content_param before initialize");
        }

        TraceShare($"set_content_param len={contentParam.Length} preview='{FormatTraceString(contentParam)}'");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "KnsfHKmZqFA",
        ExportName = "sceShareUnregisterContentEventCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceShareUtility")]
    public static int ShareUnregisterContentEventCallback(CpuContext ctx)
    {
        var callback = ctx[CpuRegister.Rdi];
        if (callback == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (_callbackGate)
        {
            if (_contentEventCallback == callback)
            {
                _contentEventCallback = 0;
            }
        }

        TraceShare($"unregister_content_event_callback fn=0x{callback:X16}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static bool TryReadNullTerminatedUtf8(CpuContext ctx, ulong address, int maxLength, out string value)
    {
        Span<byte> bytes = stackalloc byte[maxLength];
        Span<byte> one = stackalloc byte[1];
        for (var index = 0; index < maxLength; index++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)index, one))
            {
                value = string.Empty;
                return false;
            }

            if (one[0] == 0)
            {
                value = Encoding.UTF8.GetString(bytes[..index]);
                return true;
            }

            bytes[index] = one[0];
        }

        value = string.Empty;
        return false;
    }

    private static string FormatTraceString(string value)
    {
        var normalized = value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= 120 ? normalized : string.Concat(normalized.AsSpan(0, 120), "...");
    }

    private static void TraceShare(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SHARE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] share.{message}");
    }

    internal static bool TryGetContentEventCallbackForTests(ulong callback, out ulong userData)
    {
        lock (_contentEventGate)
        {
            var registration = _contentEventRegistrations.Find(
                candidate => candidate.Callback == callback);
            if (registration is not null)
            {
                userData = registration.UserData;
                return true;
            }
        }

        userData = 0;
        return false;
    }

    internal static int ContentEventCallbackCountForTests
    {
        get
        {
            lock (_contentEventGate)
            {
                return _contentEventRegistrations.Count;
            }
        }
    }

    internal static bool IsFeaturePermittedForTests(int feature)
    {
        lock (_contentEventGate)
        {
            return _permittedFeatures.Contains(feature);
        }
    }

    internal static void SetContentEventServiceAvailableForTests(bool available)
    {
        lock (_contentEventGate)
        {
            Volatile.Write(ref _contentEventServiceAvailable, available ? 1 : 0);
        }
    }

    internal static void ResetForTests()
    {
        lock (_contentEventGate)
        {
            ClearShareLifecycleUnderLock();
        }
    }

    private static void ClearShareLifecycleUnderLock()
    {
        _contentEventRegistrations.Clear();
        _permittedFeatures.Clear();
        Volatile.Write(ref _contentEventServiceAvailable, 0);
        Volatile.Write(ref _initialized, 0);
        _contentParam = string.Empty;
    }

    private static int SetShareReturn(CpuContext ctx, int result)
    {
        // The firmware functions return through EAX, which zero-extends RAX.
        ctx[CpuRegister.Rax] = unchecked((uint)result);
        return result;
    }
}
