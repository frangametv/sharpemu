// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class Gen5KernelContractExportsTests
{
    private const ulong MemoryBase = 0x0000_7FFF_5200_0000;

    private static readonly (string Nid, string Name, string Library)[] ExpectedRegistrations =
    [
        ("crb5j7mkk1c", "_is_signal_return", "libkernel"),
        ("NhpspxdjEKU", "_nanosleep", "libkernel"),
        ("hHlZQUnlxSM", "getrusage", "libkernel"),
        ("c7ZnT7V1B98", "rmdir", "libkernel"),
        ("QzB4O+bJQyA", "sceKernelAprResolveFilepathsToIdsAndFileSizesForEach", "libkernel"),
        ("eYAh2vlCY-U", "sceKernelAprResolveFilepathsToIdsForEach", "libkernel"),
        ("i3HWvW35jao", "sceKernelAprResolveFilepathsWithPrefixToIds", "libkernel"),
        ("w5fcCG+t31g", "sceKernelAprResolveFilepathsWithPrefixToIdsAndFileSizes", "libkernel"),
        ("C+Khtbbx2g8", "sceKernelAprResolveFilepathsWithPrefixToIdsAndFileSizesForEach", "libkernel"),
        ("VB-BtuIW8Xc", "sceKernelAprResolveFilepathsWithPrefixToIdsForEach", "libkernel"),
        ("uWyW3v98sU4", "sceKernelCheckReachability", "libKernel"),
        ("cfwBSQyr5Ys", "sceKernelDebugWriteCppExceptionInfo", "libkernel"),
        ("-YTW+qXc3CQ", "sceKernelInternalMemoryGetModuleSegmentInfo", "libkernel"),
        ("3k6kx-zOOSQ", "sceKernelMlock", "libkernel"),
        ("0Cq8ipKr9n0", "sceKernelUtimes", "libkernel"),
        ("IafI2PxcPnQ", "scePthreadMutexTimedlock", "libkernel"),
        ("VADc3MNQ3cM", "signal", "libkernel"),
        ("VAzswvTOCzI", "unlink", "libkernel"),
        ("TXFFFiNldU8", "getpeername", "libScePosix"),
        ("6O8EwYOgH9Y", "getsockopt", "libScePosix"),
        ("5jRCs2axtr4", "inet_ntop", "libScePosix"),
        ("Ez8xjo9UF4E", "recv", "libScePosix"),
        ("lUk6wrGXyMw", "recvfrom", "libScePosix"),
        ("fZOeZIOEmLw", "send", "libScePosix"),
        ("oBr313PppNE", "sendto", "libScePosix"),
        ("fFxGkxF2bVo", "setsockopt", "libScePosix"),
        ("TUuiYS2kE8s", "shutdown", "libScePosix"),
    ];

    private static readonly HashSet<string> SharedRegistrationNids =
    [
        "NhpspxdjEKU",
        "6O8EwYOgH9Y",
        "5jRCs2axtr4",
        "fZOeZIOEmLw",
        "fFxGkxF2bVo",
        "uWyW3v98sU4",
    ];

    public static TheoryData<string> DeferredNids => new()
    {
        "crb5j7mkk1c",
        "hHlZQUnlxSM",
        "QzB4O+bJQyA",
        "eYAh2vlCY-U",
        "i3HWvW35jao",
        "C+Khtbbx2g8",
        "VB-BtuIW8Xc",
        "-YTW+qXc3CQ",
        "3k6kx-zOOSQ",
        "0Cq8ipKr9n0",
        "IafI2PxcPnQ",
        "VADc3MNQ3cM",
        "lUk6wrGXyMw",
        "oBr313PppNE",
    };

    [Fact]
    public void ContractClassAndSharedProvidersCoverAll27RecoveredGen5Registrations()
    {
        var attributes = typeof(Gen5KernelContractExports)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(method => method.GetCustomAttributes<SysAbiExportAttribute>())
            .ToArray();
        var expectedNids = ExpectedRegistrations.Select(item => item.Nid).ToHashSet(StringComparer.Ordinal);

        var classExpectedNids = expectedNids
            .Except(SharedRegistrationNids, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(classExpectedNids.Count, attributes.Length);
        Assert.Equal(classExpectedNids.Count, attributes.Select(attribute => attribute.Nid).Distinct(StringComparer.Ordinal).Count());
        Assert.True(classExpectedNids.SetEquals(attributes.Select(attribute => attribute.Nid)));
        Assert.All(attributes, attribute =>
        {
            Assert.Equal(Generation.Gen5, attribute.Target);
            Assert.False(attribute.PreferLle);
        });

        var gen5 = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5)
            .Where(export => expectedNids.Contains(export.Nid))
            .ToArray();
        Assert.Equal(27, gen5.Length);
        Assert.Equal(27, gen5.Select(export => export.Nid).Distinct(StringComparer.Ordinal).Count());

        foreach (var expected in ExpectedRegistrations)
        {
            if (SharedRegistrationNids.Contains(expected.Nid))
            {
                Assert.DoesNotContain(attributes, candidate => candidate.Nid == expected.Nid);
            }
            else
            {
                var attribute = Assert.Single(attributes, candidate => candidate.Nid == expected.Nid);
                Assert.Equal(expected.Name, attribute.ExportName);
                Assert.Equal(expected.Library, attribute.LibraryName);
            }

            var export = Assert.Single(gen5, candidate => candidate.Nid == expected.Nid);
            Assert.Equal(expected.Name, export.Name);
            Assert.Equal(expected.Library, export.LibraryName);
            Assert.NotEqual((Generation)0, export.Target & Generation.Gen5);
            Assert.False(export.PreferLle);
        }

        var gen4Nids = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4)
            .Select(export => export.Nid)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(classExpectedNids, gen4Nids.Contains);
        Assert.All(SharedRegistrationNids, nid => Assert.Contains(nid, gen4Nids));

        Assert.DoesNotContain(ExpectedRegistrations, item =>
            item.Name is "__progname" or "_Stderr" or "_Stdout" or
            "Need_sceLibc" or "Need_sceLibcInternal" or
            "sceLibcInternalBacktraceForGame");
    }

    [Theory]
    [MemberData(nameof(DeferredNids))]
    public void DeferredContractsReturnNotImplementedWithoutTouchingGuestOutputs(string nid)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var outputAddress = MemoryBase + 0x200;
        var before = Enumerable.Repeat((byte)0xA5, 128).ToArray();
        Assert.True(memory.TryWrite(outputAddress, before));
        context[CpuRegister.Rdi] = outputAddress;
        context[CpuRegister.Rsi] = outputAddress + 16;
        context[CpuRegister.Rdx] = outputAddress + 32;
        context[CpuRegister.Rcx] = outputAddress + 48;
        context[CpuRegister.R8] = outputAddress + 64;
        context[CpuRegister.R9] = outputAddress + 80;

        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == nid);
        var result = export.Function(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED, result);
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED),
            context[CpuRegister.Rax]);
        var after = new byte[before.Length];
        Assert.True(memory.TryRead(outputAddress, after));
        Assert.Equal(before, after);
    }

    [Fact]
    public void NanosleepPreservesThePosixTimespecContractOnSuccessAndFailure()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var requestAddress = MemoryBase + 0x100;
        var remainAddress = MemoryBase + 0x120;
        Span<byte> zeroTimespec = stackalloc byte[16];
        Assert.True(memory.TryWrite(requestAddress, zeroTimespec));
        Assert.True(memory.TryWrite(remainAddress, Enumerable.Repeat((byte)0xCC, 16).ToArray()));
        context[CpuRegister.Rdi] = requestAddress;
        context[CpuRegister.Rsi] = remainAddress;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Gen5KernelContractExports.Nanosleep(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Span<byte> remaining = stackalloc byte[16];
        Assert.True(memory.TryRead(remainAddress, remaining));
        Assert.True(remaining.SequenceEqual(zeroTimespec));

        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = remainAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            Gen5KernelContractExports.Nanosleep(context));
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void RmdirRemovesAnEmptyDirectoryAndMapsMissingPathToPosixFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sharpemu-kernel27-rmdir-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, "empty");
        var mountPoint = $"/sharpemu_kernel_rmdir_{Guid.NewGuid():N}";
        Directory.CreateDirectory(directory);
        try
        {
            KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, root);
            var memory = new FakeCpuMemory(MemoryBase, 0x2000);
            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rdi] = memory.WriteCString(MemoryBase + 0x100, $"{mountPoint}/empty");

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Gen5KernelContractExports.Rmdir(context));
            Assert.Equal(0UL, context[CpuRegister.Rax]);
            Assert.False(Directory.Exists(directory));

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
                Gen5KernelContractExports.Rmdir(context));
            Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
        }
        finally
        {
            KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void UnlinkRemovesAFileAndMapsMissingPathToPosixFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sharpemu-kernel27-unlink-{Guid.NewGuid():N}");
        var mountPoint = $"/sharpemu_kernel_unlink_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "payload.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        try
        {
            KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, root);
            var memory = new FakeCpuMemory(MemoryBase, 0x2000);
            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rdi] = memory.WriteCString(MemoryBase + 0x100, $"{mountPoint}/payload.bin");

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Gen5KernelContractExports.Unlink(context));
            Assert.Equal(0UL, context[CpuRegister.Rax]);
            Assert.False(File.Exists(file));

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
                Gen5KernelContractExports.Unlink(context));
            Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
        }
        finally
        {
            KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void DebugExceptionInfoIsAnIntentionalNoOutputDiagnosticSink()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var outputAddress = MemoryBase + 0x200;
        var before = Enumerable.Repeat((byte)0x5A, 64).ToArray();
        Assert.True(memory.TryWrite(outputAddress, before));
        context[CpuRegister.Rax] = 0x1122_3344_5566_7788;
        context[CpuRegister.Rdi] = outputAddress;
        context[CpuRegister.Rsi] = outputAddress + 8;
        context[CpuRegister.Rdx] = outputAddress + 16;
        context[CpuRegister.Rcx] = outputAddress + 24;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            Gen5KernelContractExports.DebugWriteCppExceptionInfo(context));
        Assert.Equal(0x1122_3344_5566_7788UL, context[CpuRegister.Rax]);
        var after = new byte[before.Length];
        Assert.True(memory.TryRead(outputAddress, after));
        Assert.Equal(before, after);

        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            Gen5KernelContractExports.DebugWriteCppExceptionInfo(context));
    }

    [Theory]
    [InlineData("127.0.0.1", 2)]
    [InlineData("2001:db8::1", 28)]
    public void InetNtopWritesCanonicalIpv4AndIpv6Text(string addressText, int addressFamily)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var sourceAddress = MemoryBase + 0x100;
        var destinationAddress = MemoryBase + 0x180;
        Assert.True(memory.TryWrite(sourceAddress, IPAddress.Parse(addressText).GetAddressBytes()));
        context[CpuRegister.Rdi] = unchecked((ulong)addressFamily);
        context[CpuRegister.Rsi] = sourceAddress;
        context[CpuRegister.Rdx] = destinationAddress;
        context[CpuRegister.Rcx] = 64;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Gen5KernelContractExports.InetNtop(context));
        Assert.Equal(destinationAddress, context[CpuRegister.Rax]);
        Assert.Equal(addressText, ReadCString(memory, destinationAddress, 64));
    }

    [Fact]
    public void InetNtopReturnsNullAndLeavesDestinationUntouchedWhenCapacityIsTooSmall()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var sourceAddress = MemoryBase + 0x100;
        var destinationAddress = MemoryBase + 0x180;
        var sentinel = Enumerable.Repeat((byte)0xCC, 16).ToArray();
        Assert.True(memory.TryWrite(sourceAddress, IPAddress.Loopback.GetAddressBytes()));
        Assert.True(memory.TryWrite(destinationAddress, sentinel));
        context[CpuRegister.Rdi] = 2;
        context[CpuRegister.Rsi] = sourceAddress;
        context[CpuRegister.Rdx] = destinationAddress;
        context[CpuRegister.Rcx] = 4;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            Gen5KernelContractExports.InetNtop(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        var after = new byte[sentinel.Length];
        Assert.True(memory.TryRead(destinationAddress, after));
        Assert.Equal(sentinel, after);
    }

    [Fact]
    public async Task GetpeernameWritesTheConnectedPeerAndInvalidFdDoesNotWrite()
    {
        await using var socket = await ConnectedGuestSocket.CreateAsync();
        var sockaddrAddress = MemoryBase + 0x300;
        var lengthAddress = MemoryBase + 0x340;
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, 16);
        Assert.True(socket.Memory.TryWrite(lengthAddress, length));
        socket.Context[CpuRegister.Rdi] = unchecked((ulong)socket.GuestFd);
        socket.Context[CpuRegister.Rsi] = sockaddrAddress;
        socket.Context[CpuRegister.Rdx] = lengthAddress;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Gen5KernelContractExports.Getpeername(socket.Context));
        Assert.Equal(0UL, socket.Context[CpuRegister.Rax]);
        Span<byte> sockaddr = stackalloc byte[16];
        Assert.True(socket.Memory.TryRead(sockaddrAddress, sockaddr));
        Assert.Equal(16, sockaddr[0]);
        Assert.Equal(2, sockaddr[1]);
        Assert.Equal(socket.Port, BinaryPrimitives.ReadUInt16BigEndian(sockaddr[2..4]));
        Assert.True(sockaddr[4..8].SequenceEqual(IPAddress.Loopback.GetAddressBytes()));

        var sentinel = Enumerable.Repeat((byte)0x6D, 16).ToArray();
        Assert.True(socket.Memory.TryWrite(sockaddrAddress, sentinel));
        socket.Context[CpuRegister.Rdi] = unchecked((ulong)(int.MaxValue - 1));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            Gen5KernelContractExports.Getpeername(socket.Context));
        Assert.Equal(ulong.MaxValue, socket.Context[CpuRegister.Rax]);
        var after = new byte[sentinel.Length];
        Assert.True(socket.Memory.TryRead(sockaddrAddress, after));
        Assert.Equal(sentinel, after);
    }

    [Fact]
    public async Task RecvCopiesConnectedTcpBytesAndInvalidFdDoesNotWrite()
    {
        await using var socket = await ConnectedGuestSocket.CreateAsync();
        var bufferAddress = MemoryBase + 0x400;
        await socket.HostClient.GetStream().WriteAsync("GTA5"u8.ToArray());
        socket.Context[CpuRegister.Rdi] = unchecked((ulong)socket.GuestFd);
        socket.Context[CpuRegister.Rsi] = bufferAddress;
        socket.Context[CpuRegister.Rdx] = 4;
        socket.Context[CpuRegister.Rcx] = 0;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Gen5KernelContractExports.Recv(socket.Context));
        Assert.Equal(4UL, socket.Context[CpuRegister.Rax]);
        Span<byte> received = stackalloc byte[4];
        Assert.True(socket.Memory.TryRead(bufferAddress, received));
        Assert.True(received.SequenceEqual("GTA5"u8));

        var sentinel = Enumerable.Repeat((byte)0x44, 4).ToArray();
        Assert.True(socket.Memory.TryWrite(bufferAddress, sentinel));
        socket.Context[CpuRegister.Rdi] = unchecked((ulong)(int.MaxValue - 2));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            Gen5KernelContractExports.Recv(socket.Context));
        Assert.Equal(ulong.MaxValue, socket.Context[CpuRegister.Rax]);
        var after = new byte[4];
        Assert.True(socket.Memory.TryRead(bufferAddress, after));
        Assert.Equal(sentinel, after);
    }

    [Fact]
    public async Task SendCopiesGuestBytesToConnectedTcpAndInvalidFdFailsClosed()
    {
        await using var socket = await ConnectedGuestSocket.CreateAsync();
        var bufferAddress = MemoryBase + 0x500;
        Assert.True(socket.Memory.TryWrite(bufferAddress, "SHARP"u8));
        socket.Context[CpuRegister.Rdi] = unchecked((ulong)socket.GuestFd);
        socket.Context[CpuRegister.Rsi] = bufferAddress;
        socket.Context[CpuRegister.Rdx] = 5;
        socket.Context[CpuRegister.Rcx] = 0;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Gen5KernelContractExports.Send(socket.Context));
        Assert.Equal(5UL, socket.Context[CpuRegister.Rax]);
        var received = new byte[5];
        await socket.HostClient.GetStream().ReadExactlyAsync(received);
        Assert.Equal("SHARP"u8.ToArray(), received);

        socket.Context[CpuRegister.Rdi] = unchecked((ulong)(int.MaxValue - 3));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            Gen5KernelContractExports.Send(socket.Context));
        Assert.Equal(ulong.MaxValue, socket.Context[CpuRegister.Rax]);
    }

    [Fact]
    public void RecvAndSendWithUnmodeledFlagsReturnNotImplementedWithoutTouchingTheBuffer()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var bufferAddress = MemoryBase + 0x500;
        var sentinel = Enumerable.Repeat((byte)0xB7, 16).ToArray();
        Assert.True(memory.TryWrite(bufferAddress, sentinel));
        context[CpuRegister.Rdi] = 123;
        context[CpuRegister.Rsi] = bufferAddress;
        context[CpuRegister.Rdx] = 16;
        context[CpuRegister.Rcx] = 1;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED,
            Gen5KernelContractExports.Recv(context));
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED),
            context[CpuRegister.Rax]);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED,
            Gen5KernelContractExports.Send(context));
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED),
            context[CpuRegister.Rax]);

        var after = new byte[sentinel.Length];
        Assert.True(memory.TryRead(bufferAddress, after));
        Assert.Equal(sentinel, after);
    }

    [Fact]
    public async Task ShutdownRejectsInvalidHowWithoutStateChangeThenShutsDownBothDirections()
    {
        await using var socket = await ConnectedGuestSocket.CreateAsync();
        socket.Context[CpuRegister.Rdi] = unchecked((ulong)socket.GuestFd);
        socket.Context[CpuRegister.Rsi] = 3;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            Gen5KernelContractExports.Shutdown(socket.Context));
        Assert.Equal(ulong.MaxValue, socket.Context[CpuRegister.Rax]);

        socket.Context[CpuRegister.Rsi] = 2;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Gen5KernelContractExports.Shutdown(socket.Context));
        Assert.Equal(0UL, socket.Context[CpuRegister.Rax]);
    }

    private static string ReadCString(FakeCpuMemory memory, ulong address, int capacity)
    {
        var bytes = new byte[capacity];
        Assert.True(memory.TryRead(address, bytes));
        var terminator = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, terminator >= 0 ? terminator : bytes.Length);
    }

    private sealed class ConnectedGuestSocket : IAsyncDisposable
    {
        private ConnectedGuestSocket(
            FakeCpuMemory memory,
            CpuContext context,
            int guestFd,
            int port,
            TcpClient hostClient,
            TcpListener listener)
        {
            Memory = memory;
            Context = context;
            GuestFd = guestFd;
            Port = port;
            HostClient = hostClient;
            _listener = listener;
        }

        private readonly TcpListener _listener;

        public FakeCpuMemory Memory { get; }

        public CpuContext Context { get; }

        public int GuestFd { get; }

        public int Port { get; }

        public TcpClient HostClient { get; }

        public static async Task<ConnectedGuestSocket> CreateAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var memory = new FakeCpuMemory(MemoryBase, 0x2000);
                var context = new CpuContext(memory, Generation.Gen5);
                context[CpuRegister.Rdi] = 2;
                context[CpuRegister.Rsi] = 1;
                context[CpuRegister.Rdx] = 6;
                Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelSocketCompatExports.Socket(context));
                var guestFd = checked((int)context[CpuRegister.Rax]);

                var sockaddrAddress = MemoryBase + 0x100;
                Span<byte> sockaddr = stackalloc byte[16];
                sockaddr[0] = 16;
                sockaddr[1] = 2;
                BinaryPrimitives.WriteUInt16BigEndian(sockaddr[2..4], checked((ushort)port));
                IPAddress.Loopback.GetAddressBytes().CopyTo(sockaddr[4..8]);
                Assert.True(memory.TryWrite(sockaddrAddress, sockaddr));

                var accept = listener.AcceptTcpClientAsync();
                context[CpuRegister.Rdi] = unchecked((ulong)guestFd);
                context[CpuRegister.Rsi] = sockaddrAddress;
                context[CpuRegister.Rdx] = 16;
                Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelSocketCompatExports.Connect(context));
                Assert.Equal(0UL, context[CpuRegister.Rax]);
                var hostClient = await accept.WaitAsync(TimeSpan.FromSeconds(2));
                return new ConnectedGuestSocket(memory, context, guestFd, port, hostClient, listener);
            }
            catch
            {
                listener.Stop();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            KernelSocketCompatExports.TryCloseSocketFd(GuestFd);
            HostClient.Dispose();
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }
}
