// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class NetExports
{
    private const int NetErrorBadFileDescriptor = unchecked((int)0x80410109);
    private const int NetErrorInvalidArgument = unchecked((int)0x80410116);
    private const int NetErrorAddressFamilyNotSupported = unchecked((int)0x8041012F);
    private const int NetErrorWouldBlock = unchecked((int)0x80410123);
    private const int NetErrorAddressInUse = unchecked((int)0x80410130);
    private const int NetErrorNotInitialized = unchecked((int)0x804101C8);
    private const int NetErrnoBadFileDescriptor = 9;
    private const int NetErrnoInvalidArgument = 22;
    private const int NetErrnoAddressFamilyNotSupported = 47;
    private const int NetErrnoWouldBlock = 35;
    private const int NetErrnoAddressInUse = 48;
    private const int NetErrnoNotInitialized = 200;
    private const int MaxNameLength = 256;
    private const int NetSockInfoSize = 0xA0;
    private const int NetSockInfoLocalPortOffset = 0x3C;
    private const int NetGetSockInfoInvalidFlagsMask = 0x31000;

    private static readonly ConcurrentDictionary<int, NetPool> _pools = new();
    private static readonly ConcurrentDictionary<int, ResolverContext> _resolvers = new();
    private static readonly ConcurrentDictionary<int, Socket> _sockets = new();
    private static int _nextPoolId;
    private static int _nextResolverId = 0x2000;
    private static int _nextSocketId = 0x4000;
    // The platform networking module is usable immediately after it is loaded.
    // Games and middleware (notably FMOD) can create internal sockets before an
    // explicit sceNetInit call reaches application code.
    private static bool _initialized = true;

    [ThreadStatic]
    private static nint _errnoAddress;

    private sealed record NetPool(string Name, int Size, int Flags);

    private sealed record ResolverContext(string Name, int PoolId, int Flags, int LastError);

    [SysAbiExport(
        Nid = "Nlev7Lg8k3A",
        ExportName = "sceNetInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetInit(CpuContext ctx)
    {
        _initialized = true;
        TraceNet("init", 0, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "cTGkc6-TBlI",
        ExportName = "sceNetTerm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetTerm(CpuContext ctx)
    {
        _initialized = false;
        _pools.Clear();
        _resolvers.Clear();
        foreach (var socket in _sockets.Values)
        {
            socket.Dispose();
        }
        _sockets.Clear();
        TraceNet("term", 0, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    // Ghidra 12.1.2_PUBLIC_20260605, libSceNet.sprx
    // SHA-256 c04e1735a3f80a502c120610a43d0d37741f7ef90040d6b9d3346cc43988c64d,
    // entry RVA 0x34A0. The provider accepts the Orbis AF_INET (2) and
    // AF_INET6 (28) values, returns 1/0 for valid/invalid text, and only sets
    // net errno for an invalid source pointer or unsupported address family.
    [SysAbiExport(
        Nid = "8Kcp5d-q1Uo",
        ExportName = "sceNetInetPton",
        Target = Generation.Gen5,
        LibraryName = "libSceNet",
        PreferLle = true)]
    public static int NetInetPton(CpuContext ctx)
    {
        var family = unchecked((int)ctx[CpuRegister.Rdi]);
        var sourceAddress = ctx[CpuRegister.Rsi];
        var destinationAddress = ctx[CpuRegister.Rdx];

        if (family is not 2 and not 28)
        {
            return SetNetError(
                ctx,
                NetErrorAddressFamilyNotSupported,
                NetErrnoAddressFamilyNotSupported);
        }

        if (sourceAddress == 0)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        if (!TryReadUtf8Z(ctx, sourceAddress, MaxNameLength, out var text))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        byte[] packed;
        if (family == 2)
        {
            if (!TryParseStrictIpv4(text, out packed))
            {
                return ctx.SetReturn(0);
            }
        }
        else
        {
            if (text.Contains('%', StringComparison.Ordinal) ||
                !IPAddress.TryParse(text, out var address) ||
                address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return ctx.SetReturn(0);
            }

            packed = address.GetAddressBytes();
        }

        if (destinationAddress == 0 || !ctx.Memory.TryWrite(destinationAddress, packed))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = 1;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Q4qBuN-c0ZM",
        ExportName = "sceNetSocket",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetSocket(CpuContext ctx)
    {
        if (!_initialized)
        {
            return SetNetError(ctx, NetErrorNotInitialized, NetErrnoNotInitialized);
        }

        var nameAddress = ctx[CpuRegister.Rdi];
        var family = unchecked((int)ctx[CpuRegister.Rsi]);
        var type = unchecked((int)ctx[CpuRegister.Rdx]);
        var protocol = unchecked((int)ctx[CpuRegister.Rcx]);
        var name = TryReadUtf8Z(ctx, nameAddress, MaxNameLength, out var value)
            ? value
            : string.Empty;

        if (!TryTranslateSocketParameters(family, type, protocol, out var addressFamily, out var socketType, out var protocolType))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        try
        {
            var socket = new Socket(addressFamily, socketType, protocolType);
            var id = Interlocked.Increment(ref _nextSocketId);
            _sockets[id] = socket;
            TraceNet("socket.create", id, unchecked((ulong)family), unchecked((ulong)type), unchecked((ulong)protocol));
            ctx[CpuRegister.Rax] = unchecked((ulong)id);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
    }

    [SysAbiExport(
        Nid = "45ggEzakPJQ",
        ExportName = "sceNetSocketClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetSocketClose(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_sockets.TryRemove(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        socket.Dispose();
        TraceNet("socket.close", id, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "2mKX2Spso7I",
        ExportName = "sceNetSetsockopt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetSetsockopt(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var level = unchecked((int)ctx[CpuRegister.Rsi]);
        var option = unchecked((int)ctx[CpuRegister.Rdx]);
        var valueAddress = ctx[CpuRegister.Rcx];
        var valueLength = unchecked((int)ctx[CpuRegister.R8]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        // ORBIS_NET_SOL_SOCKET / ORBIS_NET_SO_NBIO. This is the first option
        // used by FMOD's discovery socket and maps directly to host blocking.
        if (level == 0xFFFF && option == 0x1200)
        {
            Span<byte> value = stackalloc byte[sizeof(int)];
            if (valueLength < value.Length || valueAddress == 0 || !ctx.Memory.TryRead(valueAddress, value))
            {
                return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
            }

            socket.Blocking = BinaryPrimitives.ReadInt32LittleEndian(value) == 0;
            TraceNet("socket.nonblocking", id, socket.Blocking ? 0UL : 1UL, 0, 0);
            return ctx.SetReturn(0);
        }

        // ORBIS_NET_SO_REUSEADDR uses the BSD value 0x0004.
        if (level == 0xFFFF && option == 0x0004)
        {
            Span<byte> value = stackalloc byte[sizeof(int)];
            if (valueLength < value.Length || valueAddress == 0 || !ctx.Memory.TryRead(valueAddress, value))
            {
                return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
            }

            socket.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                BinaryPrimitives.ReadInt32LittleEndian(value) != 0);
            TraceNet("socket.reuseaddr", id, BinaryPrimitives.ReadUInt32LittleEndian(value), 0, 0);
            return ctx.SetReturn(0);
        }

        return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
    }

    /// <summary>
    /// POSIX alias of <see cref="NetSetsockopt"/>; identical
    /// (fd, level, option, value, length) argument order.
    /// </summary>
    [SysAbiExport(
        Nid = "fFxGkxF2bVo",
        ExportName = "setsockopt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePosix")]
    public static int PosixSetsockopt(CpuContext ctx) => NetSetsockopt(ctx);

    /// <summary>
    /// Reads back the socket options this backend actually tracks: SO_NBIO,
    /// SO_REUSEADDR and SO_ERROR.
    /// </summary>
    /// <remarks>
    /// Anything else returns EINVAL rather than a zero-filled buffer. A caller
    /// that receives success for an option nobody stored would treat whatever
    /// happens to be in its output buffer as the real setting, which is a harder
    /// failure to trace than an explicit rejection.
    /// </remarks>
    [SysAbiExport(
        Nid = "6O8EwYOgH9Y",
        ExportName = "getsockopt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePosix")]
    public static int PosixGetsockopt(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var level = unchecked((int)ctx[CpuRegister.Rsi]);
        var option = unchecked((int)ctx[CpuRegister.Rdx]);
        var valueAddress = ctx[CpuRegister.Rcx];
        var lengthAddress = ctx[CpuRegister.R8];
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        if (valueAddress == 0 || lengthAddress == 0 || level != 0xFFFF)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        if (!ctx.Memory.TryRead(lengthAddress, lengthBytes))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        if (BinaryPrimitives.ReadInt32LittleEndian(lengthBytes) < sizeof(int))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        int value;
        switch (option)
        {
            // ORBIS_NET_SO_NBIO: mirrors what sceNetSetsockopt stored.
            case 0x1200:
                value = socket.Blocking ? 0 : 1;
                break;
            case 0x0004:
                value = (int)socket.GetSocketOption(
                    SocketOptionLevel.Socket,
                    SocketOptionName.ReuseAddress)! != 0 ? 1 : 0;
                break;
            // ORBIS_NET_SO_ERROR: nothing here records per-socket async errors,
            // so report "no pending error" rather than inventing one.
            case 0x1007:
                value = 0;
                break;
            default:
                return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        Span<byte> valueBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(valueBytes, value);
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, sizeof(int));
        if (!ctx.Memory.TryWrite(valueAddress, valueBytes) ||
            !ctx.Memory.TryWrite(lengthAddress, lengthBytes))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        TraceNet("socket.getsockopt", id, unchecked((uint)option), unchecked((uint)value), 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "fZOeZIOEmLw",
        ExportName = "send",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePosix")]
    public static int PosixSend(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var length = unchecked((int)ctx[CpuRegister.Rdx]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        if (length < 0 || (length != 0 && bufferAddress == 0))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        if (length == 0)
        {
            return ctx.SetReturn(0);
        }

        var payload = new byte[length];
        if (!ctx.Memory.TryRead(bufferAddress, payload))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        try
        {
            var sent = socket.Send(payload, SocketFlags.None);
            TraceNet("socket.send", id, unchecked((uint)length), unchecked((uint)sent), 0);
            return ctx.SetReturn(sent);
        }
        catch (SocketException exception)
            when (exception.SocketErrorCode == SocketError.WouldBlock)
        {
            return SetNetError(ctx, NetErrorWouldBlock, NetErrnoWouldBlock);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
        catch (ObjectDisposedException)
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }
    }

    /// <summary>
    /// Formats a binary address as text. Pure conversion with no socket state,
    /// so it behaves identically to the console version for AF_INET/AF_INET6.
    /// </summary>
    [SysAbiExport(
        Nid = "5jRCs2axtr4",
        ExportName = "inet_ntop",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePosix")]
    public static int PosixInetNtop(CpuContext ctx)
    {
        var family = unchecked((int)ctx[CpuRegister.Rdi]);
        var sourceAddress = ctx[CpuRegister.Rsi];
        var destinationAddress = ctx[CpuRegister.Rdx];
        var destinationSize = unchecked((int)ctx[CpuRegister.Rcx]);
        if (sourceAddress == 0 || destinationAddress == 0 || destinationSize <= 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        // ORBIS_NET_AF_INET / ORBIS_NET_AF_INET6, matching TryMapAddressFamily.
        var addressLength = family switch
        {
            2 => 4,
            28 => 16,
            _ => 0,
        };

        if (addressLength == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var rawAddress = new byte[addressLength];
        if (!ctx.Memory.TryRead(sourceAddress, rawAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var text = new IPAddress(rawAddress).ToString();
        var encoded = Encoding.ASCII.GetBytes(text);

        // POSIX requires the terminator to fit as well; a truncated address string
        // is worse than a reported failure because the caller cannot detect it.
        if (encoded.Length + 1 > destinationSize)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var buffer = new byte[encoded.Length + 1];
        encoded.CopyTo(buffer, 0);
        if (!ctx.Memory.TryWrite(destinationAddress, buffer))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        // inet_ntop returns the destination pointer on success.
        ctx[CpuRegister.Rax] = destinationAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "bErx49PgxyY",
        ExportName = "sceNetBind",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetBind(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }
        if (!TryReadSocketAddress(ctx, ctx[CpuRegister.Rsi], unchecked((int)ctx[CpuRegister.Rdx]), out var endpoint))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        try
        {
            socket.Bind(endpoint);
            TraceNet("socket.bind", id, unchecked((ulong)endpoint.Port), 0, 0);
            return ctx.SetReturn(0);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return SetNetError(ctx, NetErrorAddressInUse, NetErrnoAddressInUse);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
    }

    // Ghidra 12.1.2_PUBLIC_20260605, libSceNet.sprx SHA-256
    // c04e1735a3f80a502c120610a43d0d37741f7ef90040d6b9d3346cc43988c64d,
    // entry RVA 0x4180. GTA V caller FUN_02AE1E70 passes one 0xA0-byte
    // record and consumes the big-endian local port at record offset 0x3C.
    [SysAbiExport(
        Nid = "hLuXdjHnhiI",
        ExportName = "sceNetGetSockInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceNet",
        PreferLle = true)]
    public static int NetGetSockInfo(CpuContext ctx)
    {
        if (!_initialized)
        {
            return SetNetError(ctx, NetErrorNotInitialized, NetErrnoNotInitialized);
        }

        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var infoAddress = ctx[CpuRegister.Rsi];
        var recordCount = unchecked((int)ctx[CpuRegister.Rdx]);
        var flags = unchecked((int)ctx[CpuRegister.Rcx]);
        if (infoAddress == 0 || recordCount < 1 || (flags & NetGetSockInfoInvalidFlagsMask) != 0)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        if (socket.LocalEndPoint is not IPEndPoint localEndpoint ||
            localEndpoint.Port is < 0 or > ushort.MaxValue)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        Span<byte> info = stackalloc byte[NetSockInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(
            info.Slice(NetSockInfoLocalPortOffset, sizeof(ushort)),
            unchecked((ushort)localEndpoint.Port));
        if (!ctx.Memory.TryWrite(infoAddress, info))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNet("socket.info", id, unchecked((ulong)localEndpoint.Port), unchecked((ulong)recordCount), unchecked((ulong)flags));
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "kOj1HiAGE54",
        ExportName = "sceNetListen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetListen(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        try
        {
            socket.Listen(Math.Max(0, unchecked((int)ctx[CpuRegister.Rsi])));
            TraceNet("socket.listen", id, ctx[CpuRegister.Rsi], 0, 0);
            return ctx.SetReturn(0);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
    }

    [SysAbiExport(
        Nid = "PIWqhn9oSxc",
        ExportName = "sceNetAccept",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetAccept(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        try
        {
            var accepted = socket.Accept();
            var acceptedId = Interlocked.Increment(ref _nextSocketId);
            _sockets[acceptedId] = accepted;
            TraceNet("socket.accept", acceptedId, unchecked((ulong)id), 0, 0);
            ctx[CpuRegister.Rax] = unchecked((ulong)acceptedId);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        {
            return SetNetError(ctx, NetErrorWouldBlock, NetErrnoWouldBlock);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
    }

    [SysAbiExport(
        Nid = "HQOwnfMGipQ",
        ExportName = "sceNetErrnoLoc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetErrnoLoc(CpuContext ctx)
    {
        if (_errnoAddress == 0)
        {
            _errnoAddress = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(_errnoAddress, 0);
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)_errnoAddress);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "dgJBaeJnGpo",
        ExportName = "sceNetPoolCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetPoolCreate(CpuContext ctx)
    {
        var nameAddress = ctx[CpuRegister.Rdi];
        var size = unchecked((int)ctx[CpuRegister.Rsi]);
        var flags = unchecked((int)ctx[CpuRegister.Rdx]);

        if (size <= 0)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        var name = TryReadUtf8Z(ctx, nameAddress, MaxNameLength, out var value)
            ? value
            : string.Empty;

        var id = Interlocked.Increment(ref _nextPoolId);
        _pools[id] = new NetPool(name, size, flags);

        TraceNet("pool.create", id, unchecked((ulong)size), unchecked((ulong)flags), _initialized ? 1UL : 0UL);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "K7RlrTkI-mw",
        ExportName = "sceNetPoolDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetPoolDestroy(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_pools.TryRemove(id, out _))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        TraceNet("pool.destroy", id, 0, 0, _initialized ? 1UL : 0UL);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "9T2pDF2Ryqg",
        ExportName = "sceNetHtonl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetHtonl(CpuContext ctx)
    {
        var value = unchecked((uint)ctx[CpuRegister.Rdi]);
        // The byte-swapped result is the return value and already lives in Rax; return OK as the
        // dispatch status without going through SetReturn, which would overwrite Rax with 0.
        ctx[CpuRegister.Rax] = BinaryPrimitives.ReverseEndianness(value);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "iWQWrwiSt8A",
        ExportName = "sceNetHtons",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetHtons(CpuContext ctx)
    {
        var value = unchecked((ushort)ctx[CpuRegister.Rdi]);
        // The byte-swapped result is the return value and already lives in Rax; return OK as the
        // dispatch status without going through SetReturn, which would overwrite Rax with 0.
        ctx[CpuRegister.Rax] = BinaryPrimitives.ReverseEndianness(value);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "pQGpHYopAIY",
        ExportName = "sceNetNtohl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetNtohl(CpuContext ctx)
    {
        var value = unchecked((uint)ctx[CpuRegister.Rdi]);
        // The byte-swapped result is the return value and already lives in Rax; return OK as the
        // dispatch status without going through SetReturn, which would overwrite Rax with 0.
        ctx[CpuRegister.Rax] = BinaryPrimitives.ReverseEndianness(value);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Rbvt+5Y2iEw",
        ExportName = "sceNetNtohs",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetNtohs(CpuContext ctx)
    {
        var value = unchecked((ushort)ctx[CpuRegister.Rdi]);
        // The byte-swapped result is the return value and already lives in Rax; return OK as the
        // dispatch status without going through SetReturn, which would overwrite Rax with 0.
        ctx[CpuRegister.Rax] = BinaryPrimitives.ReverseEndianness(value);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "C4UgDHHPvdw",
        ExportName = "sceNetResolverCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetResolverCreate(CpuContext ctx)
    {
        var nameAddress = ctx[CpuRegister.Rdi];
        var poolId = unchecked((int)ctx[CpuRegister.Rsi]);
        var flags = unchecked((int)ctx[CpuRegister.Rdx]);
        if (flags != 0)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        var name = TryReadUtf8Z(ctx, nameAddress, MaxNameLength, out var value)
            ? value
            : string.Empty;
        var id = Interlocked.Increment(ref _nextResolverId);
        _resolvers[id] = new ResolverContext(name, poolId, flags, 0);
        TraceNet("resolver.create", id, unchecked((ulong)poolId), unchecked((ulong)flags), _initialized ? 1UL : 0UL);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "kJlYH5uMAWI",
        ExportName = "sceNetResolverDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetResolverDestroy(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        return _resolvers.TryRemove(id, out _)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorBadFileDescriptor);
    }

    [SysAbiExport(
        Nid = "J5i3hiLJMPk",
        ExportName = "sceNetResolverGetError",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetResolverGetError(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var statusAddress = ctx[CpuRegister.Rsi];
        if (statusAddress == 0)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        if (!_resolvers.TryGetValue(id, out var resolver))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        Span<byte> status = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(status, resolver.LastError);
        return ctx.Memory.TryWrite(statusAddress, status)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int SetNetError(CpuContext ctx, int result, int errno)
    {
        if (_errnoAddress == 0)
        {
            _errnoAddress = Marshal.AllocHGlobal(sizeof(int));
        }
        Marshal.WriteInt32(_errnoAddress, errno);
        return ctx.SetReturn(result);
    }

    private static bool TryParseStrictIpv4(string text, out byte[] octets)
    {
        octets = new byte[4];
        var parts = text.Split('.');
        if (parts.Length != octets.Length)
        {
            return false;
        }

        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length == 0 ||
                !uint.TryParse(
                    parts[index],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var value) ||
                value > byte.MaxValue)
            {
                return false;
            }

            octets[index] = (byte)value;
        }

        return true;
    }

    private static bool TryTranslateSocketParameters(
        int family,
        int type,
        int protocol,
        out AddressFamily addressFamily,
        out SocketType socketType,
        out ProtocolType protocolType)
    {
        addressFamily = family switch
        {
            2 => AddressFamily.InterNetwork,
            28 => AddressFamily.InterNetworkV6,
            _ => AddressFamily.Unspecified,
        };
        socketType = type switch
        {
            1 => SocketType.Stream,
            2 => SocketType.Dgram,
            _ => SocketType.Unknown,
        };
        protocolType = protocol switch
        {
            0 when socketType == SocketType.Stream => ProtocolType.Tcp,
            0 when socketType == SocketType.Dgram => ProtocolType.Udp,
            6 => ProtocolType.Tcp,
            17 => ProtocolType.Udp,
            _ => ProtocolType.Unknown,
        };

        return addressFamily != AddressFamily.Unspecified &&
            socketType != SocketType.Unknown &&
            protocolType != ProtocolType.Unknown;
    }

    private static bool TryReadSocketAddress(CpuContext ctx, ulong address, int length, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.Any, 0);
        if (address == 0 || length < 16)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!ctx.Memory.TryRead(address, bytes) || bytes[1] != 2)
        {
            return false;
        }

        var port = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..4]);
        endpoint = new IPEndPoint(new IPAddress(bytes[4..8]), port);
        return true;
    }

    private static bool TryReadUtf8Z(CpuContext ctx, ulong address, int maxLength, out string value)
    {
        value = string.Empty;
        if (address == 0)
        {
            return true;
        }

        Span<byte> one = stackalloc byte[1];
        var bytes = new byte[maxLength];
        var count = 0;
        for (; count < maxLength; count++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)count, one))
            {
                return false;
            }

            if (one[0] == 0)
            {
                break;
            }

            bytes[count] = one[0];
        }

        value = Encoding.UTF8.GetString(bytes, 0, count);
        return true;
    }

    private static void TraceNet(string operation, int id, ulong arg0, ulong arg1, ulong arg2)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NET"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] net.{operation} id={id} arg0=0x{arg0:X16} arg1=0x{arg1:X16} arg2=0x{arg2:X16}");
    }
}
