// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Net;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Gen5-only libkernel/libScePosix registrations recovered from PS5 corpus traces.
/// Contracts whose observable behavior is not yet complete stay registered but fail
/// closed. None of these kernel-facing registrations may be routed through LLE.
/// </summary>
public static class Gen5KernelContractExports
{
    private const int EBadF = 9;
    private const int EFault = 14;
    private const int EInvalid = 22;
    private const int ENoSpace = 28;
    private const int EAddressFamilyNotSupported = 47;
    private const int ENotConnected = 57;

    [SysAbiExport(
        Nid = "crb5j7mkk1c",
        ExportName = "_is_signal_return",
        Target = Generation.Gen5,
        LibraryName = "libkernel",
        PreferLle = false)]
    public static int IsSignalReturn(CpuContext ctx) => Deferred(ctx);

    // Compatibility entry point retained for branch-local callers. The NID is
    // registered by KernelRuntimeCompatExports after the upstream merge.
    public static int Nanosleep(CpuContext ctx) => KernelRuntimeCompatExports.PosixNanosleep(ctx);

    [SysAbiExport(
        Nid = "hHlZQUnlxSM",
        ExportName = "getrusage",
        Target = Generation.Gen5,
        LibraryName = "libkernel",
        PreferLle = false)]
    public static int Getrusage(CpuContext ctx) => Deferred(ctx);

    [SysAbiExport(
        Nid = "c7ZnT7V1B98",
        ExportName = "rmdir",
        Target = Generation.Gen5,
        LibraryName = "libkernel",
        PreferLle = false)]
    public static int Rmdir(CpuContext ctx) =>
        PosixPathMutation(ctx, KernelMemoryCompatExports.KernelRmdir);

    [SysAbiExport(
        Nid = "QzB4O+bJQyA",
        ExportName = "sceKernelAprResolveFilepathsToIdsAndFileSizesForEach",
        Target = Generation.Gen5,
        LibraryName = "libkernel",
        PreferLle = false)]
    public static int AprResolveFilepathsToIdsAndFileSizesForEach(CpuContext ctx) => Deferred(ctx);

    [SysAbiExport(
        Nid = "eYAh2vlCY-U",
        ExportName = "sceKernelAprResolveFilepathsToIdsForEach",
        Target = Generation.Gen5,
        LibraryName = "libkernel",
        PreferLle = false)]
    public static int AprResolveFilepathsToIdsForEach(CpuContext ctx) => Deferred(ctx);

    [SysAbiExport(
        Nid = "i3HWvW35jao",
        ExportName = "sceKernelAprResolveFilepathsWithPrefixToIds",
        Target = Generation.Gen5,
        LibraryName = "libkernel",
        PreferLle = false)]
    public static int AprResolveFilepathsWithPrefixToIds(CpuContext ctx) => Deferred(ctx);

    [SysAbiExport(
        Nid = "w5fcCG+t31g",
        ExportName = "sceKernelAprResolveFilepathsWithPrefixToIdsAndFileSizes",
        Target = Generation.Gen5,
        LibraryName = "libkernel",
        PreferLle = false)]
    public static int AprResolveFilepathsWithPrefixToIdsAndFileSizes(CpuContext ctx) =>
        KernelMemoryCompatExports.KernelAprResolveFilepathsWithPrefixToIdsAndFileSizes(ctx);

    [SysAbiExport(
        Nid = "C+Khtbbx2g8",
        ExportName = "sceKernelAprResolveFilepathsWithPrefixToIdsAndFileSizesForEach",
        Target = Generation.Gen5,
        LibraryName = "libkernel",
        PreferLle = false)]
    public static int AprResolveFilepathsWithPrefixToIdsAndFileSizesForEach(CpuContext ctx) => Deferred(ctx);

    [SysAbiExport(
        Nid = "VB-BtuIW8Xc",
        ExportName = "sceKernelAprResolveFilepathsWithPrefixToIdsForEach",
        Target = Generation.Gen5,
        LibraryName = "libkernel",
        PreferLle = false)]
    public static int AprResolveFilepathsWithPrefixToIdsForEach(CpuContext ctx) => Deferred(ctx);

    [SysAbiExport(
        Nid = "cfwBSQyr5Ys",
        ExportName = "sceKernelDebugWriteCppExceptionInfo",
        Target = Generation.Gen5,
        LibraryName = "libkernel",
        PreferLle = false)]
    public static int DebugWriteCppExceptionInfo(CpuContext ctx)
    {
        // Ghidra proves a void diagnostic-only command-0x23 sink. SharpEmu does not
        // expose that firmware diagnostic sink, so intentionally discard the record.
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "-YTW+qXc3CQ",
        ExportName = "sceKernelInternalMemoryGetModuleSegmentInfo",
        Target = Generation.Gen5,
        LibraryName = "libkernel",
        PreferLle = false)]
    public static int InternalMemoryGetModuleSegmentInfo(CpuContext ctx) => Deferred(ctx);

    [SysAbiExport(
        Nid = "3k6kx-zOOSQ",
        ExportName = "sceKernelMlock",
        Target = Generation.Gen5,
        LibraryName = "libkernel",
        PreferLle = false)]
    public static int Mlock(CpuContext ctx) => Deferred(ctx);

    [SysAbiExport(
        Nid = "0Cq8ipKr9n0",
        ExportName = "sceKernelUtimes",
        Target = Generation.Gen5,
        LibraryName = "libkernel",
        PreferLle = false)]
    public static int Utimes(CpuContext ctx) => Deferred(ctx);

    [SysAbiExport(
        Nid = "IafI2PxcPnQ",
        ExportName = "scePthreadMutexTimedlock",
        Target = Generation.Gen5,
        LibraryName = "libkernel",
        PreferLle = false)]
    public static int PthreadMutexTimedlock(CpuContext ctx) => Deferred(ctx);

    [SysAbiExport(
        Nid = "VADc3MNQ3cM",
        ExportName = "signal",
        Target = Generation.Gen5,
        LibraryName = "libkernel",
        PreferLle = false)]
    public static int Signal(CpuContext ctx) => Deferred(ctx);

    [SysAbiExport(
        Nid = "VAzswvTOCzI",
        ExportName = "unlink",
        Target = Generation.Gen5,
        LibraryName = "libkernel",
        PreferLle = false)]
    public static int Unlink(CpuContext ctx) =>
        PosixPathMutation(ctx, KernelMemoryCompatExports.KernelUnlink);

    [SysAbiExport(
        Nid = "TXFFFiNldU8",
        ExportName = "getpeername",
        Target = Generation.Gen5,
        LibraryName = "libScePosix",
        PreferLle = false)]
    public static int Getpeername(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var sockaddrAddress = ctx[CpuRegister.Rsi];
        var addrlenAddress = ctx[CpuRegister.Rdx];

        if (!KernelSocketCompatExports.TryGetPeerEndpoint(fd, out var endpoint, out var descriptorExists))
        {
            return PosixFailure(
                ctx,
                descriptorExists ? ENotConnected : EBadF,
                descriptorExists
                    ? OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT
                    : OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (endpoint is null || addrlenAddress == 0)
        {
            return PosixFailure(ctx, EFault, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Span<byte> lengthBytes = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(addrlenAddress, lengthBytes))
        {
            return PosixFailure(ctx, EFault, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var suppliedLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        Span<byte> sockaddr = stackalloc byte[28];
        var actualLength = WriteSockaddr(endpoint, sockaddr);
        var writeLength = checked((int)Math.Min(suppliedLength, (uint)actualLength));
        if (writeLength > 0 &&
            (sockaddrAddress == 0 || !ctx.Memory.TryWrite(sockaddrAddress, sockaddr[..writeLength])))
        {
            return PosixFailure(ctx, EFault, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)actualLength);
        if (!ctx.Memory.TryWrite(addrlenAddress, lengthBytes))
        {
            return PosixFailure(ctx, EFault, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Compatibility entry point retained for branch-local callers. The NID is
    // registered by Network.NetExports after the upstream merge.
    public static int Getsockopt(CpuContext ctx) => Deferred(ctx);

    // Compatibility entry point retained for branch-local callers. The NID is
    // registered by Network.NetExports after the upstream merge.
    public static int InetNtop(CpuContext ctx)
    {
        var addressFamily = unchecked((int)ctx[CpuRegister.Rdi]);
        var sourceAddress = ctx[CpuRegister.Rsi];
        var destinationAddress = ctx[CpuRegister.Rdx];
        var destinationSize = ctx[CpuRegister.Rcx];
        var addressSize = addressFamily switch
        {
            2 => 4,
            28 => 16,
            _ => 0,
        };

        if (addressSize == 0)
        {
            return PosixNullFailure(ctx, EAddressFamilyNotSupported, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (sourceAddress == 0 || destinationAddress == 0)
        {
            return PosixNullFailure(ctx, EFault, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Span<byte> packedAddress = stackalloc byte[addressSize];
        if (!ctx.Memory.TryRead(sourceAddress, packedAddress))
        {
            return PosixNullFailure(ctx, EFault, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var textBytes = Encoding.ASCII.GetBytes(new IPAddress(packedAddress).ToString());
        if (destinationSize < (ulong)textBytes.Length + 1)
        {
            return PosixNullFailure(ctx, ENoSpace, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var output = GC.AllocateUninitializedArray<byte>(textBytes.Length + 1);
        textBytes.CopyTo(output, 0);
        output[^1] = 0;
        if (!ctx.Memory.TryWrite(destinationAddress, output))
        {
            return PosixNullFailure(ctx, EFault, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = destinationAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Ez8xjo9UF4E",
        ExportName = "recv",
        Target = Generation.Gen5,
        LibraryName = "libScePosix",
        PreferLle = false)]
    public static int Recv(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var requestedLength = ctx[CpuRegister.Rdx];
        var flags = unchecked((int)ctx[CpuRegister.Rcx]);

        if (flags != 0)
        {
            return Deferred(ctx);
        }

        if (requestedLength > int.MaxValue)
        {
            return PosixFailure(ctx, EInvalid, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!KernelSocketCompatExports.IsEmulatedSocketFd(fd))
        {
            return PosixFailure(ctx, EBadF, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (!KernelSocketCompatExports.TryReadSocketFd(
                ctx,
                fd,
                bufferAddress,
                (int)requestedLength,
                out var bytesRead,
                out var error))
        {
            return PosixFailure(ctx, ENotConnected, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (error != OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return PosixFailure(ctx, EFault, error);
        }

        ctx[CpuRegister.Rax] = bytesRead;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "lUk6wrGXyMw",
        ExportName = "recvfrom",
        Target = Generation.Gen5,
        LibraryName = "libScePosix",
        PreferLle = false)]
    public static int Recvfrom(CpuContext ctx) => Deferred(ctx);

    // Compatibility entry point retained for branch-local callers. The NID is
    // registered by Network.NetExports after the upstream merge.
    public static int Send(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var requestedLength = ctx[CpuRegister.Rdx];
        var flags = unchecked((int)ctx[CpuRegister.Rcx]);

        if (flags != 0)
        {
            return Deferred(ctx);
        }

        if (requestedLength > int.MaxValue)
        {
            return PosixFailure(ctx, EInvalid, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!KernelSocketCompatExports.IsEmulatedSocketFd(fd))
        {
            return PosixFailure(ctx, EBadF, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        var payload = GC.AllocateUninitializedArray<byte>((int)requestedLength);
        if (requestedLength > 0 && !ctx.Memory.TryRead(bufferAddress, payload))
        {
            return PosixFailure(ctx, EFault, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!KernelSocketCompatExports.TryWriteSocketFd(ctx, fd, payload, out var error))
        {
            return PosixFailure(ctx, ENotConnected, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (error != OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return PosixFailure(ctx, EFault, error);
        }

        ctx[CpuRegister.Rax] = requestedLength;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "oBr313PppNE",
        ExportName = "sendto",
        Target = Generation.Gen5,
        LibraryName = "libScePosix",
        PreferLle = false)]
    public static int Sendto(CpuContext ctx) => Deferred(ctx);

    // Compatibility entry point retained for branch-local callers. The NID is
    // registered by Network.NetExports after the upstream merge.
    public static int Setsockopt(CpuContext ctx) => Deferred(ctx);

    [SysAbiExport(
        Nid = "TUuiYS2kE8s",
        ExportName = "shutdown",
        Target = Generation.Gen5,
        LibraryName = "libScePosix",
        PreferLle = false)]
    public static int Shutdown(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var how = unchecked((int)ctx[CpuRegister.Rsi]);
        if ((uint)how > 2)
        {
            return PosixFailure(ctx, EInvalid, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!KernelSocketCompatExports.TryShutdownSocket(fd, how, out var descriptorExists))
        {
            return PosixFailure(
                ctx,
                descriptorExists ? ENotConnected : EBadF,
                descriptorExists
                    ? OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT
                    : OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int Deferred(CpuContext ctx) =>
        ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED);

    private static int PosixPathMutation(CpuContext ctx, SysAbiFunction operation)
    {
        var result = operation(ctx);
        if (result == (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            ctx[CpuRegister.Rax] = 0;
            return result;
        }

        var errno = (OrbisGen2Result)result switch
        {
            OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND => 2,
            OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED => 13,
            OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT => EFault,
            OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY => 16,
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT => EInvalid,
            _ => 38,
        };
        return PosixFailure(ctx, errno, (OrbisGen2Result)result);
    }

    private static int PosixFailure(CpuContext ctx, int errno, OrbisGen2Result result)
    {
        KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
        ctx[CpuRegister.Rax] = ulong.MaxValue;
        return (int)result;
    }

    private static int PosixNullFailure(CpuContext ctx, int errno, OrbisGen2Result result)
    {
        KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
        ctx[CpuRegister.Rax] = 0;
        return (int)result;
    }

    private static int WriteSockaddr(IPEndPoint endpoint, Span<byte> destination)
    {
        var addressBytes = endpoint.Address.GetAddressBytes();
        if (addressBytes.Length == 4)
        {
            destination[..16].Clear();
            destination[0] = 16;
            destination[1] = 2;
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), checked((ushort)endpoint.Port));
            addressBytes.CopyTo(destination.Slice(4, 4));
            return 16;
        }

        destination[..28].Clear();
        destination[0] = 28;
        destination[1] = 28;
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), checked((ushort)endpoint.Port));
        addressBytes.CopyTo(destination.Slice(8, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(24, 4), unchecked((uint)endpoint.Address.ScopeId));
        return 28;
    }
}
