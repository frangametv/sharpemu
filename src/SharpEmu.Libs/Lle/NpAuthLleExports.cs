// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Lle;

public static class NpAuthLleExports
{
    private const int RequestLimit = 0x10;
    private const int RequestIdOffset = 0x1000_0000;
    private const ulong AsyncParameterSize = 0x18;
    private const ulong AuthorizationParameterSize = 0x20;
    private const int ErrorInvalidArgument = unchecked((int)0x80550301);
    private const int ErrorInvalidSize = unchecked((int)0x80550302);
    private const int ErrorAborted = unchecked((int)0x80550304);
    private const int ErrorRequestMax = unchecked((int)0x80550305);
    private const int ErrorRequestNotFound = unchecked((int)0x80550306);
    private const int ErrorInvalidId = unchecked((int)0x80550307);

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Request> Requests = [];

    private enum RequestState { Ready, Aborted, Complete }
    private sealed class Request
    {
        public RequestState State { get; set; } = RequestState.Ready;
        public int Result { get; set; }
    }

    [SysAbiExport(Nid = "N+mr7GjTvr8", ExportName = "sceNpAuthCreateAsyncRequest", Target = Generation.Gen5, LibraryName = "libSceNpAuth", PreferLle = true)]
    public static int CreateAsyncRequest(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0) return ctx.SetReturn(ErrorInvalidArgument);
        if (!ctx.TryReadUInt64(address, out var size)) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        if (size != AsyncParameterSize) return ctx.SetReturn(ErrorInvalidSize);
        if (!ctx.TryReadUInt64(address + 8, out _) || !ctx.TryReadUInt32(address + 0x10, out _))
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);

        lock (Gate)
        {
            if (Requests.Count >= RequestLimit) return ctx.SetReturn(ErrorRequestMax);
            for (var index = 1; index <= RequestLimit; index++)
            {
                var id = RequestIdOffset + index;
                if (Requests.TryAdd(id, new Request()))
                {
                    Trace($"create request=0x{id:X8}");
                    return ctx.SetReturn(id);
                }
            }
        }
        return ctx.SetReturn(ErrorRequestMax);
    }

    [SysAbiExport(Nid = "KI4dHLlTNl0", ExportName = "sceNpAuthGetAuthorizationCodeV3", Target = Generation.Gen5, LibraryName = "libSceNpAuth", PreferLle = true)]
    public static int GetAuthorizationCodeV3(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameter = ctx[CpuRegister.Rsi];
        var authorizationCodeAddress = ctx[CpuRegister.Rdx];
        var issuerIdAddress = ctx[CpuRegister.Rcx];
        if (parameter == 0 || authorizationCodeAddress == 0) return ctx.SetReturn(ErrorInvalidArgument);
        if (!ctx.TryReadUInt64(parameter, out var size)) return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        if (size != AuthorizationParameterSize) return ctx.SetReturn(ErrorInvalidSize);
        if (!ctx.TryReadUInt32(parameter + 8, out var userId) ||
            !ctx.TryReadUInt64(parameter + 0x10, out var clientId) ||
            !ctx.TryReadUInt64(parameter + 0x18, out var scope))
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        if (userId == uint.MaxValue || clientId == 0 || scope == 0) return ctx.SetReturn(ErrorInvalidArgument);

        lock (Gate)
        {
            if (!Requests.TryGetValue(id, out var request)) return ctx.SetReturn(ErrorRequestNotFound);
            if (request.State == RequestState.Complete)
            {
                request.Result = ErrorInvalidArgument;
                return ctx.SetReturn(ErrorInvalidArgument);
            }
            if (request.State == RequestState.Aborted)
            {
                request.Result = ErrorAborted;
                return ctx.SetReturn(ErrorAborted);
            }
            // Provide a deterministic local authorization result. This never
            // contacts PSN and is not usable as a real credential; it only
            // lets games select their signed-in local landing-page path.
            Span<byte> authorizationCode = stackalloc byte[136];
            authorizationCode.Clear();
            "AUTHEN"u8.CopyTo(authorizationCode);
            if (!ctx.Memory.TryWrite(authorizationCodeAddress, authorizationCode) ||
                (issuerIdAddress != 0 && !ctx.TryWriteUInt32(issuerIdAddress, 0x100)))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
            request.State = RequestState.Complete;
            request.Result = 0;
        }
        Trace($"authorization complete request=0x{id:X8} result=LOCAL_OK");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "gjSyfzSsDcE", ExportName = "sceNpAuthPollAsync", Target = Generation.Gen5, LibraryName = "libSceNpAuth", PreferLle = true)]
    public static int PollAsync(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var resultAddress = ctx[CpuRegister.Rsi];
        if (resultAddress == 0) return ctx.SetReturn(ErrorInvalidArgument);
        int result;
        lock (Gate)
        {
            if (!Requests.TryGetValue(id, out var request)) return ctx.SetReturn(ErrorRequestNotFound);
            if (request.State == RequestState.Ready) return ctx.SetReturn(ErrorInvalidId);
            result = request.Result;
        }
        if (!ctx.TryWriteUInt32(resultAddress, unchecked((uint)result)))
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        Trace($"poll request=0x{id:X8} result=0x{result:X8}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "cE7wIsqXdZ8", ExportName = "sceNpAuthAbortRequest", Target = Generation.Gen5, LibraryName = "libSceNpAuth", PreferLle = true)]
    public static int AbortRequest(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (Gate)
        {
            if (!Requests.TryGetValue(id, out var request)) return ctx.SetReturn(ErrorRequestNotFound);
            if (request.State != RequestState.Complete)
            {
                request.State = RequestState.Aborted;
                request.Result = ErrorAborted;
            }
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "H8wG9Bk-nPc", ExportName = "sceNpAuthDeleteRequest", Target = Generation.Gen5, LibraryName = "libSceNpAuth", PreferLle = true)]
    public static int DeleteRequest(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (Gate)
        {
            if (!Requests.Remove(id)) return ctx.SetReturn(ErrorRequestNotFound);
        }
        Trace($"delete request=0x{id:X8}");
        return ctx.SetReturn(0);
    }

    internal static void ResetForTests()
    {
        lock (Gate) Requests.Clear();
    }

    private static void Trace(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_IMPORTS"), "1", StringComparison.Ordinal))
            Console.Error.WriteLine($"[NP_AUTH][HLE] {message}");
    }
}
