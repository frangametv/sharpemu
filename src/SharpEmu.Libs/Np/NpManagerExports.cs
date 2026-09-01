// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Np;

public static class NpManagerExports
{
    private const int NpTitleIdSize = 16;
    private const int NpTitleSecretSize = 128;
    private const int NpErrorNotInitialized = unchecked((int)0x80550002);
    private const int NpErrorInvalidArgument = unchecked((int)0x80550003);
    private const int NpErrorCallbackAlreadyRegistered = unchecked((int)0x80550008);
    private const int NpErrorCallbackNotRegistered = unchecked((int)0x80550009);
    private const int NpErrorInvalidAsyncParameterSize = unchecked((int)0x80550011);
    private const uint NpStateSignedOut = 1;
    private const uint NpStateSignedIn = 2;
    private const ulong NpAsyncParameterSize = 0x18;

    private static readonly object ManagerGate = new();
    private static ulong _managerAllocatorAddress;
    private static ulong _premiumEventCallback;
    private static ulong _premiumEventCallbackUserData;
    private static ulong _reachabilityStateCallback;
    private static ulong _reachabilityStateCallbackUserData;

    [SysAbiExport(
        Nid = "fHGhS3uP52k",
        ExportName = "sceNpManagerGlobalInitializeCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpManagerGlobalInitializeCompat1270(CpuContext ctx)
    {
        var poolSize = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        if (poolSize == 0 || nameAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (ManagerGate)
        {
            if (_managerAllocatorAddress == 0)
            {
                if (!NpCommonExports.TryCreateHleAllocator(ctx, poolSize, out _managerAllocatorAddress))
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                ClearPremiumEventCallbackUnderLock();
            }
        }

        // Firmware 12.70's implementation (libSceNpManager +0x14950)
        // creates the module-private NP manager pool and callback table. The
        // pool never crosses the ABI boundary, so boot only needs the observed
        // validation and successful initialized result.
        TraceNp($"manager_global_init pool=0x{poolSize:X} name=0x{nameAddress:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "ukEeOizCkIU",
        ExportName = "sceNpManagerGetAllocatorCallbacksCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpManagerGetAllocatorCallbacksCompat1270(CpuContext ctx)
    {
        lock (ManagerGate)
        {
            if (_managerAllocatorAddress == 0)
            {
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            ctx[CpuRegister.Rax] = _managerAllocatorAddress;
        }

        // The firmware table contains malloc, realloc, free, and a null user
        // pointer. Its address is consumed throughout ShellCore as allocator
        // identity; the shared executable no-op entries keep indirect cleanup
        // calls safe while the module-private pool remains HLE-owned.
        TraceNp($"manager_allocator_callbacks table=0x{ctx[CpuRegister.Rax]:X}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "4uhgVNAqiag",
        ExportName = "sceNpManagerGlobalTerminateCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpManagerGlobalTerminateCompat1270(CpuContext ctx)
    {
        ulong allocatorAddress;
        lock (ManagerGate)
        {
            allocatorAddress = _managerAllocatorAddress;
            _managerAllocatorAddress = 0;
            ClearPremiumEventCallbackUnderLock();
        }

        NpCommonExports.ReleaseHleAllocator(allocatorAddress);
        TraceNp("manager_global_term");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "QvqOkNK5ThU",
        ExportName = "sceNpExtNpHttpClientConstructorCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpExtNpHttpClientConstructorCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        var allocatorAddress = ctx[CpuRegister.Rsi];
        if (objectAddress == 0 || allocatorAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var objectBytes = new byte[0x50];
        BinaryPrimitives.WriteUInt64LittleEndian(objectBytes.AsSpan(0x08), allocatorAddress);
        if (!ctx.Memory.TryWrite(objectAddress, objectBytes) ||
            !KernelMemoryCompatExports.TryWriteDummyVtable(ctx, objectAddress))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // Firmware installs the ExtNpHttpClient vtable before initializing its
        // embedded synchronization state. ShellCore calls virtual slot +8 when
        // rolling this stage back, even when Initialize returned an error.
        ctx[CpuRegister.Rax] = objectAddress;
        TraceNp($"ext_http_client_ctor object=0x{objectAddress:X} allocator=0x{allocatorAddress:X}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "CvGog64+vCk",
        ExportName = "sceNpExtNpHttpClientInitializeCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpExtNpHttpClientInitializeCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        var mode = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (objectAddress == 0 ||
            !ctx.TryWriteUInt64(objectAddress + 0x18, objectAddress + 0x18) ||
            !ctx.Memory.TryWrite(objectAddress + 0x20, new byte[] { 1 }) ||
            !ctx.Memory.TryWrite(objectAddress + 0x28, BitConverter.GetBytes(mode)))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // The real object creates asynchronous HTTP workers here. Keep those
        // workers quiescent for the boot path while preserving initialized
        // mutex and mode fields used by ShellCore's state checks.
        TraceNp($"ext_http_client_init object=0x{objectAddress:X} mode={mode}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "S7Afe0llsL8",
        ExportName = "sceNpCallbackSlotConstructorCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCallbackSlotConstructorCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        if (objectAddress == 0 ||
            !ctx.Memory.TryWrite(objectAddress, new byte[0x10]) ||
            !KernelMemoryCompatExports.TryWriteDummyVtable(ctx, objectAddress) ||
            !ctx.TryWriteUInt32(objectAddress + 8, uint.MaxValue))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = objectAddress;
        TraceNp($"callback_slot_ctor object=0x{objectAddress:X} id=-1");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "gQFyT9aIsOk",
        ExportName = "sceNpCallbackSlotDestructorCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCallbackSlotDestructorCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = objectAddress;
        TraceNp($"callback_slot_dtor object=0x{objectAddress:X}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "+yqjab2fUJA",
        ExportName = "sceNpRegisterPremiumEventCallback",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpRegisterPremiumEventCallback(CpuContext ctx)
    {
        var callback = ctx[CpuRegister.Rdi];
        var userData = ctx[CpuRegister.Rsi];

        // Firmware 12.70 first checks the manager lifecycle, then protects the
        // single callback/user-data slot with its manager mutex. Preserve that
        // ordering: an uninitialized manager wins even when callback is null.
        lock (ManagerGate)
        {
            if (_managerAllocatorAddress == 0)
            {
                return SetReturn(ctx, NpErrorNotInitialized);
            }

            if (callback == 0)
            {
                return SetReturn(ctx, NpErrorInvalidArgument);
            }

            if (_premiumEventCallback != 0)
            {
                return SetReturn(ctx, NpErrorCallbackAlreadyRegistered);
            }

            // The firmware subscribes its stored slot to a platform backend
            // and rolls the slot back if that subscription fails. SharpEmu has
            // no online premium backend: this synchronized slot is instead a
            // real local subscription which receives only explicitly supplied
            // emulator events through TryDispatchPremiumEvent. It fabricates
            // neither premium state nor online events.
            _premiumEventCallback = callback;
            _premiumEventCallbackUserData = userData;
        }

        TraceNp($"register_premium_event_callback callback=0x{callback:X} userdata=0x{userData:X}");
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "-Rjp3-YViXc",
        ExportName = "sceNpUnregisterPremiumEventCallback",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpUnregisterPremiumEventCallback(CpuContext ctx)
    {
        lock (ManagerGate)
        {
            if (_managerAllocatorAddress == 0)
            {
                return SetReturn(ctx, NpErrorNotInitialized);
            }

            if (_premiumEventCallback == 0)
            {
                return SetReturn(ctx, NpErrorCallbackNotRegistered);
            }

            // Firmware ignores backend-unsubscribe failure and always clears
            // its local slot. There is no online backend to contact here, so
            // clearing the HLE subscription is the complete local operation.
            ClearPremiumEventCallbackUnderLock();
        }

        TraceNp("unregister_premium_event_callback");
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "hw5KNqAAels",
        ExportName = "sceNpRegisterNpReachabilityStateCallback",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager",
        PreferLle = true)]
    public static int NpRegisterNpReachabilityStateCallback(CpuContext ctx)
    {
        var callback = ctx[CpuRegister.Rdi];
        var userData = ctx[CpuRegister.Rsi];
        lock (ManagerGate)
        {
            // Provider entry 0x163a0 gates this callback on the PRX-owned NP SDK
            // state queried by 0x1b390. That is the same lifecycle used by the
            // async request exports, not fHGhS3uP52k's separate allocator.
            if (!NpManagerAsyncRequests.IsInitialized)
            {
                return SetReturn(ctx, NpErrorNotInitialized);
            }

            if (callback == 0)
            {
                return SetReturn(ctx, NpErrorInvalidArgument);
            }

            if (_reachabilityStateCallback != 0)
            {
                return SetReturn(ctx, NpErrorCallbackAlreadyRegistered);
            }

            _reachabilityStateCallback = callback;
            _reachabilityStateCallbackUserData = userData;
        }

        // The provider returns the event backend's current reachability state
        // after installing its slot. SharpEmu models NP as offline/unavailable,
        // whose provider value is zero.
        TraceNp($"register_reachability_state_callback callback=0x{callback:X} userdata=0x{userData:X}");
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "3Zl8BePTh9Y",
        ExportName = "sceNpCheckCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCheckCallback(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "eiqMCt9UshI",
        ExportName = "sceNpCreateAsyncRequest",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCreateAsyncRequest(CpuContext ctx)
    {
        // Firmware checks the PRX-owned request subsystem before touching the
        // parameter pointer. This lifecycle is intentionally independent from
        // the manager allocator used by fHGhS3uP52k.
        if (!NpManagerAsyncRequests.IsInitialized)
        {
            return SetReturn(ctx, NpManagerAsyncRequests.ErrorNotInitialized);
        }

        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0)
        {
            return SetReturn(ctx, NpManagerAsyncRequests.ErrorInvalidArgument);
        }

        if (!ctx.TryReadUInt64(parameterAddress, out var size))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (size != NpAsyncParameterSize)
        {
            return SetReturn(ctx, NpErrorInvalidAsyncParameterSize);
        }

        if (!ctx.TryReadUInt64(parameterAddress + 0x08, out var affinity) ||
            !ctx.TryReadUInt32(parameterAddress + 0x10, out var priority))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetReturn(ctx, NpManagerAsyncRequests.Create(priority, affinity));
    }

    [SysAbiExport(
        Nid = "KfGZg2y73oM",
        ExportName = "sceNpCheckNpReachability",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCheckNpReachability(CpuContext ctx)
    {
        // Firmware checks the PRX-owned async registry before validating either
        // argument. This operation must share the same registry as
        // sceNpCreateAsyncRequest and sceNpPollAsync; sending only this call to
        // the guest provider gives it an HLE-owned request ID it cannot resolve.
        if (!NpManagerAsyncRequests.IsInitialized)
        {
            return SetReturn(ctx, NpManagerAsyncRequests.ErrorNotInitialized);
        }

        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        var userId = unchecked((int)ctx[CpuRegister.Rsi]);
        if (requestId <= 0 || userId == -1)
        {
            return SetReturn(ctx, NpManagerAsyncRequests.ErrorInvalidArgument);
        }

        // The public call only schedules the check. Its worker completes the
        // request asynchronously; a zero operation result means the local check
        // itself completed, while sceNpGetNpReachabilityState remains the source
        // of the actual offline reachability value.
        var result = NpManagerAsyncRequests.StartLocalOperation(requestId, _ => 0);
        TraceNp($"check_np_reachability request={requestId} user={userId} result=0x{result:X8}");
        return SetReturn(ctx, result);
    }

    [SysAbiExport(
        Nid = "S7QTn72PrDw",
        ExportName = "sceNpDeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpDeleteRequest(CpuContext ctx)
    {
        // The Ghidra evidence packet establishes only Gen5 behavior. Preserve
        // the pre-existing Gen4 compatibility behavior rather than projecting
        // the Gen5 request registry onto an unevidenced ABI generation.
        if (ctx.TargetGeneration == Generation.Gen4)
        {
            return SetReturn(ctx, 0);
        }

        return SetReturn(
            ctx,
            NpManagerAsyncRequests.Delete(unchecked((int)ctx[CpuRegister.Rdi])));
    }

    [SysAbiExport(
        Nid = "OzKvTvg3ZYU",
        ExportName = "sceNpAbortRequest",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpAbortRequest(CpuContext ctx)
    {
        return SetReturn(
            ctx,
            NpManagerAsyncRequests.Abort(unchecked((int)ctx[CpuRegister.Rdi])));
    }

    [SysAbiExport(
        Nid = "uqcPJLWL08M",
        ExportName = "sceNpPollAsync",
        Target = Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpPollAsync(CpuContext ctx)
    {
        // Initialization wins over a null result pointer in firmware.
        if (!NpManagerAsyncRequests.IsInitialized)
        {
            return SetReturn(ctx, NpManagerAsyncRequests.ErrorNotInitialized);
        }

        var resultAddress = ctx[CpuRegister.Rsi];
        if (resultAddress == 0)
        {
            return SetReturn(ctx, NpManagerAsyncRequests.ErrorInvalidArgument);
        }

        var pollResult = NpManagerAsyncRequests.Poll(
            unchecked((int)ctx[CpuRegister.Rdi]),
            out var completed,
            out var operationResult);
        if (pollResult != 0 || !completed)
        {
            return SetReturn(ctx, pollResult);
        }

        // Firmware writes exactly one 32-bit result, and only on completion.
        return ctx.TryWriteInt32(resultAddress, operationResult)
            ? SetReturn(ctx, 0)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "JELHf4xPufo",
        ExportName = "sceNpCheckCallbackForLib",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpCheckCallbackForLib(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Offline profile: the online id payload is left untouched and the call
    // reports success, matching the other offline NpManager stubs here.
    [SysAbiExport(
        Nid = "XDncXQIJUSk",
        ExportName = "sceNpGetOnlineId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetOnlineId(CpuContext ctx)
    {
        // Gen5 ABI: user ID, then output structure.
        return WriteOfflineOnlineId(ctx, ctx[CpuRegister.Rsi]);
    }

    [SysAbiExport(
        Nid = "VfRSmPmj8Q8",
        ExportName = "sceNpRegisterStateCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpRegisterStateCallback(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    /// <summary>
    /// Accepts the reachability callback and never invokes it. Reachability
    /// transitions only ever fire on a real PSN connection, which an offline
    /// session does not have, so registering successfully and staying silent is
    /// the faithful offline behavior.
    /// </summary>
    [SysAbiExport(
        Nid = "qQJfO8HAiaY",
        ExportName = "sceNpRegisterStateCallbackA",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpRegisterStateCallbackA(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "0c7HbXRKUt4",
        ExportName = "sceNpRegisterStateCallbackForToolkit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManagerForToolkit")]
    public static int NpRegisterStateCallbackForToolkit(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "eQH7nWPcAgc",
        ExportName = "sceNpGetState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetState(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rsi];
        if (stateAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> stateBytes = stackalloc byte[sizeof(uint)];
        // SceNpState assigns SIGNED_OUT value 1 and SIGNED_IN value 2. The
        // default local profile stays signed in for compatibility. A title
        // can opt into a fully unavailable NP profile to avoid entering PSN
        // account/service paths which an offline emulator cannot satisfy.
        var state = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_NP_UNAVAILABLE"),
            "1",
            StringComparison.Ordinal)
            ? NpStateSignedOut
            : NpStateSignedIn;
        BinaryPrimitives.WriteUInt32LittleEndian(stateBytes, state);
        return ctx.Memory.TryWrite(stateAddress, stateBytes)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "rbknaUjpqWo",
        ExportName = "sceNpGetAccountIdA",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetAccountIdA(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var accountIdAddress = ctx[CpuRegister.Rsi];
        if (userId == -1 || accountIdAddress == 0)
        {
            return SetReturn(ctx, NpErrorInvalidArgument);
        }

        // The offline profile exposed by sceNpGetState is signed in. Keep the
        // account query consistent with that state: Unity's PSN integration
        // treats SIGNED_OUT as an exceptional state and retries it every frame.
        // A stable local-only id is sufficient for titles which only use the
        // value as a profile key.
        Span<byte> accountId = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(accountId, 1);
        return ctx.Memory.TryWrite(accountIdAddress, accountId)
            ? SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "JT+t00a3TxA",
        ExportName = "sceNpGetAccountCountryA",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetAccountCountryA(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var countryAddress = ctx[CpuRegister.Rsi];
        if (userId == -1 || countryAddress == 0)
        {
            return SetReturn(ctx, NpErrorInvalidArgument);
        }

        Span<byte> country = stackalloc byte[4];
        country[0] = (byte)'U';
        country[1] = (byte)'S';
        country[2] = 0;
        country[3] = 0;
        return ctx.Memory.TryWrite(countryAddress, country)
            ? SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "e-ZuhGEoeC4",
        ExportName = "sceNpGetNpReachabilityState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpGetNpReachabilityState(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var stateAddress = ctx[CpuRegister.Rsi];
        if (userId == -1 || stateAddress == 0)
        {
            return SetReturn(ctx, NpErrorInvalidArgument);
        }

        Span<byte> state = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(state, 0); // Unavailable while offline.
        return ctx.Memory.TryWrite(stateAddress, state)
            ? SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "Ec63y59l9tw",
        ExportName = "sceNpSetNpTitleId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpSetNpTitleId(CpuContext ctx)
    {
        var titleIdAddress = ctx[CpuRegister.Rdi];
        var titleSecretAddress = ctx[CpuRegister.Rsi];
        if (titleIdAddress == 0 || titleSecretAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> titleId = stackalloc byte[NpTitleIdSize];
        Span<byte> titleSecret = stackalloc byte[NpTitleSecretSize];
        if (!ctx.Memory.TryRead(titleIdAddress, titleId) ||
            !ctx.Memory.TryRead(titleSecretAddress, titleSecret))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNp($"set_np_title_id title='{ReadTitleId(titleId)}'");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    internal static bool TryDispatchPremiumEvent(
        CpuContext ctx,
        ulong eventType,
        ulong eventValue,
        out string? error)
    {
        ulong callback;
        ulong userData;
        lock (ManagerGate)
        {
            if (_managerAllocatorAddress == 0)
            {
                error = "NP manager is not initialized";
                return false;
            }

            callback = _premiumEventCallback;
            userData = _premiumEventCallbackUserData;
            if (callback == 0)
            {
                error = "premium event callback is not registered";
                return false;
            }
        }

        // Firmware copies the callback pair while holding the manager mutex,
        // then invokes callback(eventType, eventValue, userData) only after it
        // has unlocked. Guest code may therefore unregister itself safely.
        var scheduler = GuestThreadExecution.Scheduler;
        if (scheduler is null)
        {
            error = "guest scheduler is unavailable";
            return false;
        }

        var invoked = scheduler.TryCallGuestFunction(
            ctx,
            callback,
            eventType,
            eventValue,
            userData,
            0,
            0,
            $"np_premium_event_{eventType}",
            out _,
            out error);
        if (invoked)
        {
            TraceNp(
                $"premium_event callback=0x{callback:X} type={eventType} value={eventValue} userdata=0x{userData:X}");
        }

        return invoked;
    }

    internal static bool TryDispatchNpReachabilityState(
        CpuContext ctx,
        ulong userId,
        ulong state,
        out string? error)
    {
        ulong callback;
        ulong userData;
        lock (ManagerGate)
        {
            if (!NpManagerAsyncRequests.IsInitialized)
            {
                error = "NP SDK request subsystem is not initialized";
                return false;
            }

            callback = _reachabilityStateCallback;
            userData = _reachabilityStateCallbackUserData;
            if (callback == 0)
            {
                error = "NP reachability-state callback is not registered";
                return false;
            }
        }

        // Provider callback 0x16490 copies the pair under its mutex, unlocks,
        // then calls callback(userId, state, userData).
        var scheduler = GuestThreadExecution.Scheduler;
        if (scheduler is null)
        {
            error = "guest scheduler is unavailable";
            return false;
        }

        var invoked = scheduler.TryCallGuestFunction(
            ctx,
            callback,
            userId,
            state,
            userData,
            0,
            0,
            $"np_reachability_state_{userId}_{state}",
            out _,
            out error);
        if (invoked)
        {
            TraceNp(
                $"reachability_state callback=0x{callback:X} user={userId} state={state} userdata=0x{userData:X}");
        }

        return invoked;
    }

    internal static bool TryGetPremiumEventCallbackForTests(out ulong callback, out ulong userData)
    {
        lock (ManagerGate)
        {
            callback = _premiumEventCallback;
            userData = _premiumEventCallbackUserData;
            return callback != 0;
        }
    }

    internal static bool TryGetReachabilityStateCallbackForTests(out ulong callback, out ulong userData)
    {
        lock (ManagerGate)
        {
            callback = _reachabilityStateCallback;
            userData = _reachabilityStateCallbackUserData;
            return callback != 0;
        }
    }

    internal static bool IsPremiumEventGateHeldByCurrentThreadForTests => Monitor.IsEntered(ManagerGate);

    internal static void ResetForTests()
    {
        ulong allocatorAddress;
        lock (ManagerGate)
        {
            allocatorAddress = _managerAllocatorAddress;
            _managerAllocatorAddress = 0;
            ClearPremiumEventCallbackUnderLock();
            ClearReachabilityStateCallbackUnderLock();
        }

        NpCommonExports.ReleaseHleAllocator(allocatorAddress);
        NpManagerAsyncRequests.ResetForTests();
    }

    private static void ClearPremiumEventCallbackUnderLock()
    {
        _premiumEventCallback = 0;
        _premiumEventCallbackUserData = 0;
    }

    private static void ClearReachabilityStateCallbackUnderLock()
    {
        _reachabilityStateCallback = 0;
        _reachabilityStateCallbackUserData = 0;
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static string ReadTitleId(ReadOnlySpan<byte> bytes)
    {
        var length = 0;
        while (length < 12 && length < bytes.Length && bytes[length] != 0)
        {
            length++;
        }

        return length == 0
            ? string.Empty
            : System.Text.Encoding.ASCII.GetString(bytes[..length]);
    }

    private static void TraceNp(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] np.{message}");
    }

    private static int WriteOfflineOnlineId(CpuContext ctx, ulong address)
    {
        if (address == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // SceNpOnlineId is a 16-byte handle plus four trailing bytes.
        Span<byte> onlineId = stackalloc byte[20];
        "Player"u8.CopyTo(onlineId);
        return ctx.Memory.TryWrite(address, onlineId)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
