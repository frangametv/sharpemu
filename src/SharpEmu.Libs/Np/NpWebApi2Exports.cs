// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpWebApi2Exports
{
    private const int NpWebApi2ErrorInvalidArgument = unchecked((int)0x80553402);

    private static int _initialized;
    private static int _nextLibraryContextHandle;
    private static int _nextPushEventHandle;
    private static int _nextUserContextHandle = 1000;
    private static int _nextRequestHandle;
    private static readonly object _contextGate = new();
    private static readonly HashSet<int> _libraryContexts = [];
    private static readonly HashSet<int> _userContexts = [];
    private static readonly HashSet<int> _requests = [];

    [SysAbiExport(
        Nid = "+o9816YQhqQ",
        ExportName = "sceNpWebApi2Initialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Initialize(CpuContext ctx)
    {
        var httpContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var poolSize = ctx[CpuRegister.Rsi];

        if (httpContextId <= 0 || poolSize == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var libraryContextId = CreateLibraryContextId();
        Interlocked.Exchange(ref _initialized, 1);
        TraceNpWebApi2("init", httpContextId, poolSize);
        return ctx.SetReturn(libraryContextId);
    }

    [SysAbiExport(
        Nid = "MsaFhR+lPE4",
        ExportName = "sceNpWebApi2PushEventCreateFilter",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2",
        PreferLle = true)]
    public static int NpWebApi2PushEventCreateFilter(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsValidLibraryContextId(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var filterHandle = Interlocked.Increment(ref _nextPushEventHandle);
        TraceNpWebApi2("push-event-create-filter", libraryContextId, (ulong)filterHandle);
        return ctx.SetReturn(filterHandle);
    }

    [SysAbiExport(
        Nid = "WV1GwM32NgY",
        ExportName = "sceNpWebApi2PushEventCreateHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2InitializeAlt(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsValidLibraryContextId(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var handle = CreatePushEventHandle();
        Interlocked.Exchange(ref _initialized, 1);
        TraceNpWebApi2("init-alt", libraryContextId, 0);
        return ctx.SetReturn(handle);
    }

    [SysAbiExport(
        Nid = "sk54bi6FtYM",
        ExportName = "sceNpWebApi2CreateUserContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2CreateUserContext(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var userId = unchecked((int)ctx[CpuRegister.Rsi]);

        TraceNpWebApi2(
            "create-user-context",
            libraryContextId,
            unchecked((uint)userId));

        if (Volatile.Read(ref _initialized) == 0 ||
            !IsValidLibraryContextId(libraryContextId) ||
            userId == -1)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var userContextId = Interlocked.Increment(ref _nextUserContextHandle);
        lock (_contextGate)
        {
            _userContexts.Add(userContextId);
        }

        return ctx.SetReturn(userContextId);
    }

    public static int NpWebApi2CreateRequest(CpuContext ctx)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var serviceNameAddress = ctx[CpuRegister.Rsi];
        var requestDataAddress = ctx[CpuRegister.Rdx];

        lock (_contextGate)
        {
            if (!_userContexts.Contains(userContextId) ||
                serviceNameAddress == 0 ||
                requestDataAddress == 0)
            {
                return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
            }

            var requestId = Interlocked.Increment(ref _nextRequestHandle);
            _requests.Add(requestId);
            TraceNpWebApi2("create-request", requestId, (ulong)userContextId);
            return ctx.SetReturn(requestId);
        }
    }

    public static int NpWebApi2AddHttpRequestHeader(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        var headerNameAddress = ctx[CpuRegister.Rsi];
        var headerValueAddress = ctx[CpuRegister.Rdx];

        // Matches the provider's observed entry checks: request handle zero is
        // accepted, while both string pointers are mandatory. The offline HLE
        // records no network state; it only preserves the successful request
        // construction path used by libSceNpCppWebApi.
        if (headerNameAddress == 0 || headerValueAddress == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        TraceNpWebApi2("add-request-header", requestId, headerNameAddress);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    public static int NpWebApi2SendRequest(CpuContext ctx)
    {
        // The provider accepts request handle zero and forwards all four
        // arguments to its internal dispatcher.  In the no-provider path we
        // deliberately complete an empty offline response: no host network
        // access and no fabricated PSN identity or payload.
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        TraceNpWebApi2("send-request-offline", requestId, ctx[CpuRegister.Rcx]);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    public static int NpWebApi2GetHttpResponseHeaderValueLength(CpuContext ctx)
    {
        var headerNameAddress = ctx[CpuRegister.Rsi];
        var outputLengthAddress = ctx[CpuRegister.Rdx];
        if (headerNameAddress == 0 ||
            outputLengthAddress == 0 ||
            !ctx.TryWriteUInt64(outputLengthAddress, 1))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        // One byte is sufficient for the empty response's trailing NUL.
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    public static int NpWebApi2GetHttpResponseHeaderValue(CpuContext ctx)
    {
        var headerNameAddress = ctx[CpuRegister.Rsi];
        var outputAddress = ctx[CpuRegister.Rdx];
        var outputCapacity = ctx[CpuRegister.Rcx];
        if (headerNameAddress == 0 ||
            outputAddress == 0 ||
            outputCapacity == 0 ||
            !ctx.Memory.TryWrite(outputAddress, stackalloc byte[] { 0 }))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    public static int NpWebApi2DeleteRequest(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (_contextGate)
        {
            _requests.Remove(requestId);
        }

        TraceNpWebApi2("delete-request", requestId, 0);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    public static int NpWebApi2ReadData(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rsi];
        var outputCapacity = ctx[CpuRegister.Rdx];
        // Matches the provider entry checks for RSI/RDX. The deterministic
        // offline response has no entity body, so zero is an immediate EOF.
        if (outputAddress == 0 || outputCapacity == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        TraceNpWebApi2("read-data-eof", requestId, outputCapacity);
        return ctx.SetReturn(0);
    }

    public static int NpWebApi2DeleteUserContext(CpuContext ctx)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (_contextGate)
        {
            _userContexts.Remove(userContextId);
        }

        TraceNpWebApi2("delete-user-context", userContextId, 0);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    public static int NpWebApi2AbortRequest(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        TraceNpWebApi2("abort-request", requestId, 0);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "bEvXpcEk200",
        ExportName = "sceNpWebApi2Terminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Terminate(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsValidLibraryContextId(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        RemoveLibraryContextId(libraryContextId);
        TraceNpWebApi2("term", libraryContextId, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "zXaFo7euxsQ",
        ExportName = "sceNpWebApi2IntInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2IntInitialize(CpuContext ctx)
    {
        var argsAddress = ctx[CpuRegister.Rdi];
        if (argsAddress == 0 ||
            !ctx.TryReadInt32(argsAddress, out var httpContextId) ||
            !ctx.TryReadUInt64(argsAddress + 8, out var poolSize) ||
            !ctx.TryReadUInt64(argsAddress + 0x18, out var structSize) ||
            structSize < 0x20)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var libraryContextId = CreateLibraryContextId();
        Interlocked.Exchange(ref _initialized, 1);
        TraceNpWebApi2("int_init", httpContextId, poolSize);
        ctx[CpuRegister.Rax] = unchecked((ulong)libraryContextId);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int CreateLibraryContextId()
    {
        var handle = Interlocked.Increment(ref _nextLibraryContextHandle);
        lock (_contextGate)
        {
            _libraryContexts.Add(handle);
        }

        return handle;
    }

    private static int CreatePushEventHandle()
    {
        return Interlocked.Increment(ref _nextPushEventHandle);
    }

    private static bool IsValidLibraryContextId(int libraryContextId)
    {
        if (libraryContextId <= 0 || libraryContextId >= 0x8000)
        {
            return false;
        }

        lock (_contextGate)
        {
            return _libraryContexts.Contains(libraryContextId);
        }
    }

    private static void RemoveLibraryContextId(int libraryContextId)
    {
        lock (_contextGate)
        {
            _libraryContexts.Remove(libraryContextId);
            if (_libraryContexts.Count == 0)
            {
                Interlocked.Exchange(ref _initialized, 0);
            }
        }
    }

    private static void TraceNpWebApi2(string operation, int id, ulong arg0)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP_WEB_API2"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] npwebapi2.{operation} id={id} arg0=0x{arg0:X16} initialized={Volatile.Read(ref _initialized)}");
    }
}
