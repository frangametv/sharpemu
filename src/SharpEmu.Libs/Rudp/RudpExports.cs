// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Rudp;

public static class RudpExports
{
    private const int RudpErrorNotInitialized = unchecked((int)0x80770001);
    private const int RudpErrorAlreadyInitialized = unchecked((int)0x80770002);
    private const int RudpErrorInvalidArgument = unchecked((int)0x80770004);
    private const int RudpErrorOutOfMemory = unchecked((int)0x80770007);
    private const int RudpErrorInternalIoThreadAlreadyEnabled = unchecked((int)0x80770010);
    private const int RudpErrorInvalidEventHandler = unchecked((int)0x80770022);
    private const int MinimumAllocatorStorageSize = 0xF8 + 0x2D8;
    private const uint MinimumInternalIoThreadStackSize = 0x4000;
    private const int GuestBufferProbeSize = 4096;

    private static readonly object StateGate = new();
    private static bool _initialized;
    private static ulong _retainedBufferAddress;
    private static int _retainedBufferSize;
    private static ulong _eventHandlerAddress;
    private static ulong _eventHandlerUserData;
    private static bool _internalIoThreadEnabled;
    private static uint _internalIoThreadStackSize;
    private static int _internalIoThreadPriority;

    [SysAbiExport(
        Nid = "amuBfI-AQc4",
        ExportName = "sceRudpInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRudp")]
    public static int Init(CpuContext ctx)
    {
        var bufferAddress = ctx[CpuRegister.Rdi];
        var bufferSize = unchecked((int)ctx[CpuRegister.Rsi]);

        lock (StateGate)
        {
            if (_initialized)
            {
                return SetReturn(ctx, RudpErrorAlreadyInitialized);
            }

            ClearRetainedState();
            if (bufferAddress == 0 || bufferSize < 1)
            {
                return SetReturn(ctx, RudpErrorInvalidArgument);
            }

            if (bufferSize < MinimumAllocatorStorageSize ||
                !IsGuestBufferAvailable(ctx, bufferAddress, bufferSize))
            {
                return SetReturn(ctx, RudpErrorOutOfMemory);
            }

            // The firmware allocator and both RUDP objects are backed by this
            // caller-owned region. Retain the exact address/size for the entire
            // initialized lifetime rather than treating Init as a success stub.
            _retainedBufferAddress = bufferAddress;
            _retainedBufferSize = bufferSize;
            _initialized = true;
            return SetReturn(ctx, 0);
        }
    }

    [SysAbiExport(
        Nid = "SUEVes8gvmw",
        ExportName = "sceRudpSetEventHandler",
        Target = Generation.Gen5,
        LibraryName = "libSceRudp")]
    public static int SetEventHandler(CpuContext ctx)
    {
        var handlerAddress = ctx[CpuRegister.Rdi];
        var userData = ctx[CpuRegister.Rsi];

        lock (StateGate)
        {
            if (!_initialized)
            {
                return SetReturn(ctx, RudpErrorNotInitialized);
            }

            if (handlerAddress == 0)
            {
                return SetReturn(ctx, RudpErrorInvalidEventHandler);
            }

            _eventHandlerAddress = handlerAddress;
            _eventHandlerUserData = userData;
            return SetReturn(ctx, 0);
        }
    }

    [SysAbiExport(
        Nid = "6PBNpsgyaxw",
        ExportName = "sceRudpEnableInternalIOThread",
        Target = Generation.Gen5,
        LibraryName = "libSceRudp")]
    public static int EnableInternalIoThread(CpuContext ctx)
    {
        var requestedStackSize = unchecked((uint)ctx[CpuRegister.Rdi]);
        var priority = unchecked((int)ctx[CpuRegister.Rsi]);

        lock (StateGate)
        {
            if (!_initialized)
            {
                return SetReturn(ctx, RudpErrorNotInitialized);
            }

            if (_internalIoThreadEnabled)
            {
                return SetReturn(ctx, RudpErrorInternalIoThreadAlreadyEnabled);
            }

            // Firmware starts one module-owned worker after normalizing the
            // requested stack size. The HLE retains that lifecycle boundary;
            // it does not need a host socket, poll object, or background thread
            // until an implemented RUDP context can consume that machinery.
            _internalIoThreadStackSize = Math.Max(
                requestedStackSize,
                MinimumInternalIoThreadStackSize);
            _internalIoThreadPriority = priority;
            _internalIoThreadEnabled = true;
            return SetReturn(ctx, 0);
        }
    }

    [SysAbiExport(
        Nid = "3hBvwqEwqj8",
        ExportName = "sceRudpEnd",
        Target = Generation.Gen5,
        LibraryName = "libSceRudp")]
    public static int End(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!_initialized)
            {
                return SetReturn(ctx, RudpErrorNotInitialized);
            }

            // Firmware marks the module uninitialized before stopping and
            // destroying its owned worker. The HLE has no host worker to join,
            // so clearing the retained ownership graph is the whole supported
            // teardown boundary and makes a subsequent Init start fresh.
            ClearRetainedState();
            return SetReturn(ctx, 0);
        }
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        // These firmware wrappers return through EAX, which zero-extends the
        // 32-bit status into RAX even when its signed int representation is
        // negative. Writing RAX here also prevents ModuleManager dispatch from
        // replacing the result with a sign-extended managed int.
        ctx[CpuRegister.Rax] = unchecked((uint)result);
        return result;
    }

    private static bool IsGuestBufferAvailable(
        CpuContext ctx,
        ulong bufferAddress,
        int bufferSize)
    {
        var byteCount = (ulong)bufferSize;
        if (bufferAddress > ulong.MaxValue - (byteCount - 1))
        {
            return false;
        }

        Span<byte> probe = stackalloc byte[GuestBufferProbeSize];
        for (ulong offset = 0; offset < byteCount;)
        {
            var length = (int)Math.Min(
                (ulong)GuestBufferProbeSize,
                byteCount - offset);
            var chunk = probe[..length];
            var address = bufferAddress + offset;
            if (!ctx.Memory.TryRead(address, chunk) ||
                !ctx.Memory.TryWrite(address, chunk))
            {
                return false;
            }

            offset += (ulong)length;
        }

        return true;
    }

    internal static (bool Initialized, ulong BufferAddress, int BufferSize)
        GetStateForTests()
    {
        lock (StateGate)
        {
            return (_initialized, _retainedBufferAddress, _retainedBufferSize);
        }
    }

    internal static (ulong HandlerAddress, ulong UserData)
        GetEventHandlerStateForTests()
    {
        lock (StateGate)
        {
            return (_eventHandlerAddress, _eventHandlerUserData);
        }
    }

    internal static (bool Enabled, uint StackSize, int Priority)
        GetInternalIoThreadStateForTests()
    {
        lock (StateGate)
        {
            return (
                _internalIoThreadEnabled,
                _internalIoThreadStackSize,
                _internalIoThreadPriority);
        }
    }

    internal static void ResetForTests()
    {
        lock (StateGate)
        {
            ClearRetainedState();
        }
    }

    private static void ClearRetainedState()
    {
        _initialized = false;
        _retainedBufferAddress = 0;
        _retainedBufferSize = 0;
        _eventHandlerAddress = 0;
        _eventHandlerUserData = 0;
        _internalIoThreadEnabled = false;
        _internalIoThreadStackSize = 0;
        _internalIoThreadPriority = 0;
    }
}
