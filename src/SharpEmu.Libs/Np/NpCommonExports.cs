// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Np;

public static class NpCommonExports
{
    private const ulong HeapAlignment = 0x4000;
    private const ulong ObjectAlignment = 0x10;
    private const int HeapStateSize = 0x20;
    private const int AllocatorSize = 0x20;
    private const int AllocatorExSize = 0x28;
    private const int ObjectHeaderSize = 0x10;
    private const int NpCommonErrorAlreadyInitialized = unchecked((int)0x80559E03);
    private static readonly object HeapGate = new();
    private static readonly object Atomic32Gate = new();
    private static readonly Dictionary<ulong, NpHeapState> Heaps = new();

    [SysAbiExport(
        Nid = "pfJgSA4jO3M",
        ExportName = "sceNpAtomicInc32",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpAtomicIncrement32(CpuContext ctx) => NpAtomicFetchUpdate32(ctx, increment: true);

    [SysAbiExport(
        Nid = "Yohe0MMDfj0",
        ExportName = "sceNpAtomicDec32",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpAtomicDecrement32(CpuContext ctx) => NpAtomicFetchUpdate32(ctx, increment: false);

    [SysAbiExport(
        Nid = "kZizwrFvWZY",
        ExportName = "sceNpMemoryHeapInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpMemoryHeapInit(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rdi];
        var requestedSize = ctx[CpuRegister.Rsi];
        var nameAddress = ctx[CpuRegister.Rdx];
        if (stateAddress == 0 || requestedSize == 0 || requestedSize > ulong.MaxValue - (HeapAlignment - 1))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var alignedSize = (requestedSize + (HeapAlignment - 1)) & ~(HeapAlignment - 1);
        if (ctx.Memory is not IGuestMemoryAllocator allocator ||
            !allocator.TryAllocateGuestMemory(alignedSize, HeapAlignment, out var heapAddress))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Span<byte> state = stackalloc byte[HeapStateSize];
        state.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(state, alignedSize);
        BinaryPrimitives.WriteUInt64LittleEndian(state[0x10..], heapAddress);

        // The firmware stores an mspace handle in the last word. SharpEmu's
        // guest arena is monotonic, so use the allocation itself as an opaque,
        // non-null handle until the NP allocator entry points are exercised.
        BinaryPrimitives.WriteUInt64LittleEndian(state[0x18..], heapAddress);
        if (!ctx.Memory.TryWrite(stateAddress, state))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (HeapGate)
        {
            Heaps[stateAddress] = new NpHeapState(heapAddress, alignedSize);
        }

        var name = nameAddress != 0 && ctx.TryReadNullTerminatedUtf8(nameAddress, 128, out var heapName)
            ? heapName
            : string.Empty;
        TraceNp($"memory_heap_init state=0x{stateAddress:X} size=0x{alignedSize:X} heap=0x{heapAddress:X} name='{name}'");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "xHAiSVEEjSI",
        ExportName = "sceNpMemoryHeapGetAllocatorEx",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpMemoryHeapGetAllocatorEx(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rdi];
        var errorCode = unchecked((uint)ctx[CpuRegister.Rsi]);
        var allocatorAddress = ctx[CpuRegister.Rdx];
        if (stateAddress == 0 || allocatorAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> allocator = stackalloc byte[AllocatorExSize];
        allocator.Clear();
        // The first three words are native malloc/free/realloc callbacks in
        // firmware. Object allocation is bridged directly below; retain the
        // heap context and caller-selected error code in their real offsets.
        BinaryPrimitives.WriteUInt64LittleEndian(allocator[0x18..], stateAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(allocator[0x20..], errorCode);
        if (!ctx.Memory.TryWrite(allocatorAddress, allocator))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNp($"memory_heap_get_allocator_ex state=0x{stateAddress:X} allocator=0x{allocatorAddress:X} error=0x{errorCode:X8}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "FaMNvjMA6to",
        ExportName = "sceNpMemoryHeapGetAllocator",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpMemoryHeapGetAllocator(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rdi];
        var allocatorAddress = ctx[CpuRegister.Rsi];
        if (stateAddress == 0 || allocatorAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> allocator = stackalloc byte[AllocatorSize];
        allocator.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(allocator[0x18..], stateAddress);
        if (!ctx.Memory.TryWrite(allocatorAddress, allocator))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNp($"memory_heap_get_allocator state=0x{stateAddress:X} allocator=0x{allocatorAddress:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "orRb69nSo64",
        ExportName = "_ZN3sce2np6ObjectnwEmR16SceNpAllocatorEx",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpObjectNew(CpuContext ctx)
    {
        var objectSize = ctx[CpuRegister.Rdi];
        var allocatorAddress = ctx[CpuRegister.Rsi];
        if (objectSize > int.MaxValue - ObjectHeaderSize ||
            allocatorAddress == 0 ||
            !ctx.TryReadUInt64(allocatorAddress + 0x18, out var stateAddress) ||
            !TryAllocateFromHeap(stateAddress, objectSize + ObjectHeaderSize, out var rawAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var allocationSize = checked((int)(objectSize + ObjectHeaderSize));
        var allocation = new byte[allocationSize];
        BinaryPrimitives.WriteUInt64LittleEndian(allocation, allocatorAddress);
        if (!ctx.Memory.TryWrite(rawAddress, allocation))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var objectAddress = rawAddress + ObjectHeaderSize;
        ctx[CpuRegister.Rax] = objectAddress;
        TraceNp($"object_new size=0x{objectSize:X} allocator=0x{allocatorAddress:X} object=0x{objectAddress:X}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "0syNkhJANVw",
        ExportName = "_ZN3sce2np6ObjectnwEmR14SceNpAllocator",
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpObjectNewBasicAllocator(CpuContext ctx)
    {
        // The basic and extended allocator objects both keep their heap state
        // pointer at +0x18 and use the same 16-byte hidden object header.
        return NpObjectNew(ctx);
    }

    [SysAbiExport(
        Nid = "V75N47uYdQc",
        ExportName = "sceNpObjectNewCallbackAllocatorCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpObjectNewCallbackAllocatorCompat1270(CpuContext ctx)
    {
        // Firmware 12.70's libSceNpCommon +0x18940 performs the same hidden
        // 16-byte header allocation through a raw callback-table allocator.
        return NpObjectNew(ctx);
    }

    [SysAbiExport(
        Nid = "mzlILsFx0cU",
        ExportName = "sceNpAllocatorAllocateCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpAllocatorAllocateCompat1270(CpuContext ctx)
    {
        var allocatorAddress = ctx[CpuRegister.Rdi];
        var allocationSize = ctx[CpuRegister.Rsi];

        // Firmware 12.70's five-instruction wrapper loads the heap context
        // from allocator+0x18 and tail-calls its malloc callback as
        // malloc(size, context). Bridge that callback through the same
        // monotonic NP heap used by sceNpMemoryHeapInit.
        if (allocationSize == 0 ||
            allocatorAddress == 0 ||
            !ctx.TryReadUInt64(allocatorAddress + 0x18, out var stateAddress) ||
            !TryAllocateFromHeap(stateAddress, allocationSize, out var allocationAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        ctx[CpuRegister.Rax] = allocationAddress;
        TraceNp(
            $"allocator_allocate size=0x{allocationSize:X} allocator=0x{allocatorAddress:X} " +
            $"object=0x{allocationAddress:X}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "BztTl7QeYqE",
        ExportName = "sceNpAllocatorFreeCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpAllocatorFreeCompat1270(CpuContext ctx)
    {
        // The firmware wrapper tail-calls allocator->free(pointer, context).
        // The boot-time NP heap is monotonic, so individual frees are safe
        // no-ops and the whole arena is discarded by heap destruction.
        TraceNp(
            $"allocator_free allocator=0x{ctx[CpuRegister.Rdi]:X} " +
            $"object=0x{ctx[CpuRegister.Rsi]:X}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "kBON3bAtfGs",
        ExportName = "sceNpGetPlatformEnvironmentCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpGetPlatformEnvironmentCompat1270(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        var outputCapacity = ctx[CpuRegister.Rsi];
        if (outputAddress == 0 || outputCapacity < 3 || outputCapacity > 4096)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // The firmware implementation reads the 16-byte NP environment from
        // RegMgr key 0x6b976df7f847ea43. "np" is the production environment
        // and selects ShellCore's normal platform configuration table.
        var environment = new byte[(int)outputCapacity];
        environment[0] = (byte)'n';
        environment[1] = (byte)'p';
        if (!ctx.Memory.TryWrite(outputAddress, environment))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNp($"platform_environment output=0x{outputAddress:X} capacity=0x{outputCapacity:X} value=np");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "dV+zK-Ce-2E",
        ExportName = "sceNpIpcClientConstructorCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpIpcClientConstructorCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        if (objectAddress == 0 ||
            !ctx.Memory.TryWrite(objectAddress, new byte[0x38]) ||
            !KernelMemoryCompatExports.TryWriteDummyVtable(ctx, objectAddress))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // The 12.70 constructor installs its C++ vtable at offset zero and
        // clears the remaining IPC-client state. ShellCore invokes slot +8
        // while unwinding a failed initialization, so a zero vtable turns a
        // recoverable NP error into a guest jump through address 0x8.
        ctx[CpuRegister.Rax] = objectAddress;
        TraceNp($"ipc_client_ctor object=0x{objectAddress:X}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "oLpLfV2Ov9A",
        ExportName = "sceNpIpcClientInitializeCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpIpcClientInitializeCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (objectAddress == 0 ||
            parameterAddress == 0 ||
            !ctx.Memory.TryWrite(objectAddress + 0x30, new byte[] { 1 }))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // Service workers are intentionally quiescent during early boot. The
        // initialized flag is the state consumed by the platform bootstrap.
        TraceNp($"ipc_client_init object=0x{objectAddress:X} params=0x{parameterAddress:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "pJlGhXEt5CU",
        ExportName = "sceNpCommonInitializeGlobalsCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpCommonInitializeGlobalsCompat1270(CpuContext ctx)
    {
        // The 12.70 implementation only runs a process-global once routine.
        // The managed HLE state is initialized statically, so there is no
        // native callback to invoke here.
        TraceNp("common_globals_init");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "XEXFdmQj5oI",
        ExportName = "sceNpEventSubscriptionConstructorCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpEventSubscriptionConstructorCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        var ownerAddress = ctx[CpuRegister.Rsi];
        if (objectAddress == 0 ||
            !ctx.Memory.TryWrite(objectAddress, new byte[0x48]) ||
            !ctx.TryWriteUInt64(objectAddress + 8, ownerAddress))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = objectAddress;
        TraceNp($"event_subscription_ctor object=0x{objectAddress:X} owner=0x{ownerAddress:X}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "kUitiIVR43g",
        ExportName = "sceNpEventSubscriptionDestructorCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpEventSubscriptionDestructorCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        if (objectAddress != 0 && !ctx.TryWriteUInt64(objectAddress + 0x40, 0))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = objectAddress;
        TraceNp($"event_subscription_dtor object=0x{objectAddress:X}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "esiO4He2WTU",
        ExportName = "sceNpEventQueueConstructorCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpEventQueueConstructorCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        if (objectAddress == 0 || !ctx.Memory.TryWrite(objectAddress, new byte[0xA0]))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = objectAddress;
        TraceNp($"event_queue_ctor object=0x{objectAddress:X}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "8kUkQPQP7bA",
        ExportName = "sceNpEventQueueDestructorCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpEventQueueDestructorCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        if (objectAddress != 0 && !ctx.Memory.TryWrite(objectAddress + 0x48, new byte[0x52]))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = objectAddress;
        TraceNp($"event_queue_dtor object=0x{objectAddress:X}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "b20e017Ei94",
        ExportName = "sceNpEventQueueInitializeCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpEventQueueInitializeCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        var elementCount = ctx[CpuRegister.Rdx];
        var elementSize = ctx[CpuRegister.Rcx];
        if (objectAddress == 0 || elementCount == 0 || elementSize == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> initialized = stackalloc byte[2];
        if (!ctx.Memory.TryRead(objectAddress + 0x98, initialized))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(initialized) != 0)
        {
            return ctx.SetReturn(NpCommonErrorAlreadyInitialized);
        }

        var mutexAddress = objectAddress + 0x40;
        var mutexHandleAddress = mutexAddress + 8;
        if (!ctx.TryWriteUInt64(mutexHandleAddress, mutexHandleAddress) ||
            !ctx.Memory.TryWrite(mutexAddress + 0x10, new byte[] { 1 }) ||
            !ctx.TryWriteUInt64(objectAddress + 0x60, objectAddress + 0x60) ||
            !ctx.TryWriteUInt64(objectAddress + 0x68, mutexHandleAddress) ||
            !ctx.TryWriteUInt64(objectAddress + 0x78, objectAddress + 0x78) ||
            !ctx.TryWriteUInt64(objectAddress + 0x80, mutexHandleAddress) ||
            !ctx.TryWriteUInt64(objectAddress + 0x88, elementCount) ||
            !ctx.TryWriteUInt64(objectAddress + 0x90, elementSize) ||
            !ctx.TryWriteUInt32(objectAddress + 0x98, 1))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNp($"event_queue_init object=0x{objectAddress:X} count=0x{elementCount:X} size=0x{elementSize:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "jxPY-0x8e-M",
        ExportName = "sceNpEventQueueIsEmptyCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpEventQueueIsEmptyCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        if (objectAddress == 0)
        {
            // A quiesced or partially constructed service owns no queued
            // items. Treat its absent queue as empty so firmware teardown can
            // finish instead of spinning on a dormant worker forever.
            ctx[CpuRegister.Rax] = 1;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!ctx.TryReadUInt64(objectAddress + 0x28, out var queuedItemAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        // 12.70 adds eight bytes to the outer object and returns whether the
        // internal queue's +0x20 item pointer is null.
        var isEmpty = queuedItemAddress == 0;
        ctx[CpuRegister.Rax] = isEmpty ? 1UL : 0UL;
        TraceNp($"event_queue_is_empty object=0x{objectAddress:X} empty={isEmpty}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "slmKkuIoC28",
        ExportName = "sceNpEventQueueSignalCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpEventQueueSignalCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        var flags = unchecked((byte)ctx[CpuRegister.Rsi]);
        Span<byte> currentFlags = stackalloc byte[1];
        if (objectAddress == 0 || !ctx.Memory.TryRead(objectAddress + 0x9A, currentFlags))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        currentFlags[0] |= (byte)(flags & 3);
        if (!ctx.Memory.TryWrite(objectAddress + 0x9A, currentFlags))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNp($"event_queue_signal object=0x{objectAddress:X} flags=0x{flags & 3:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "CnDHI7sU+l0",
        ExportName = "_ZN3sce2np6ObjectdlEPv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpObjectDelete(CpuContext ctx)
    {
        // NP allocations live in a monotonic per-process heap for now. The
        // hidden allocator header remains intact so repeated cleanup is safe.
        TraceNp($"object_delete object=0x{ctx[CpuRegister.Rdi]:X}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "O1AvlQU33pI",
        ExportName = "_ZN3sce2np5MutexC1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpMutexConstructor(CpuContext ctx)
    {
        var mutexAddress = ctx[CpuRegister.Rdi];
        if (mutexAddress == 0 ||
            !ctx.Memory.TryWrite(mutexAddress, new byte[0x18]) ||
            !KernelMemoryCompatExports.TryWriteDummyVtable(ctx, mutexAddress))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // sce::np::Mutex is polymorphic: ShellCore invokes virtual slot +8 when
        // an NP service initializer rolls back. The real constructor installs
        // libSceNpCommon's vtable; the HLE constructor must provide a callable
        // substitute or a recoverable init error becomes a jump through 0x8.
        ctx[CpuRegister.Rax] = mutexAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "aTNOl9EB4V4",
        ExportName = "_ZN3sce2np5Mutex4InitEPKcj",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpMutexInit(CpuContext ctx)
    {
        var mutexAddress = ctx[CpuRegister.Rdi];
        if (mutexAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> initialized = stackalloc byte[1] { 1 };
        if (!ctx.TryWriteUInt64(mutexAddress + 8, mutexAddress + 8) ||
            !ctx.Memory.TryWrite(mutexAddress + 0x10, initialized))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "9zi9FTPol74",
        ExportName = "_ZN3sce2np5MutexD1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpMutexDestructor(CpuContext ctx)
    {
        var mutexAddress = ctx[CpuRegister.Rdi];
        if (mutexAddress != 0 && !ctx.Memory.TryWrite(mutexAddress + 8, new byte[9]))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = mutexAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "VM+CXTW4F-s",
        ExportName = "_ZN3sce2np5Mutex4LockEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpMutexLock(CpuContext ctx) => NpMutexOperation(ctx);

    [SysAbiExport(
        Nid = "eYgHIWx0Hco",
        ExportName = "_ZN3sce2np5Mutex6UnlockEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpMutexUnlock(CpuContext ctx) => NpMutexOperation(ctx);

    [SysAbiExport(
        Nid = "TJNrs69haak",
        ExportName = "_ZN3sce2np5Mutex7TryLockEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpMutexTryLock(CpuContext ctx) => NpMutexOperation(ctx);

    [SysAbiExport(
        Nid = "RgGW4f0ox1g",
        ExportName = "_ZN3sce2np5Mutex7DestroyEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpMutexDestroy(CpuContext ctx)
    {
        var mutexAddress = ctx[CpuRegister.Rdi];
        if (mutexAddress == 0 || !ctx.Memory.TryWrite(mutexAddress + 8, new byte[9]))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "Yc+qj4TIEY0",
        ExportName = "_ZNK3sce2np5Mutex6IsInitEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpMutexIsInit(CpuContext ctx)
    {
        var mutexAddress = ctx[CpuRegister.Rdi];
        Span<byte> initialized = stackalloc byte[1];
        ctx[CpuRegister.Rax] = mutexAddress != 0 &&
                               ctx.Memory.TryRead(mutexAddress + 0x10, initialized) &&
                               initialized[0] != 0
            ? 1UL
            : 0UL;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "cMOgkE2M2e8",
        ExportName = "sceNpHandleConstructorCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpHandleConstructorCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        if (objectAddress == 0 ||
            !ctx.Memory.TryWrite(objectAddress, new byte[0x18]) ||
            !KernelMemoryCompatExports.TryWriteDummyVtable(ctx, objectAddress))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // Firmware 12.70's implementation (libSceNpCommon +0x1DC80)
        // installs a two-slot vtable at +0 and clears its handle-kind field
        // at +0x10. ShellCore embeds this polymorphic handle wrapper in the
        // NP service singleton and subsequently dispatches through it.
        ctx[CpuRegister.Rax] = objectAddress;
        TraceNp($"handle_ctor object=0x{objectAddress:X}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "amFi-Av19hU",
        ExportName = "sceNpEventFlagInitializeCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpEventFlagInitializeCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        if (objectAddress == 0 ||
            !ctx.TryWriteUInt64(objectAddress + 8, objectAddress + 8) ||
            !ctx.TryWriteUInt32(objectAddress + 0x10, 1))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // libSceNpCommon +0x1DD60 initializes its embedded kernel event flag
        // at +8 and records the initialized state at +0x10.
        TraceNp($"event_flag_init object=0x{objectAddress:X} name=0x{ctx[CpuRegister.Rsi]:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "st-oQvV7HVI",
        ExportName = "sceNpEventFlagIsInitializedCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpEventFlagIsInitializedCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        uint state = 0;
        if (objectAddress != 0)
        {
            _ = ctx.TryReadUInt32(objectAddress + 0x10, out state);
        }

        ctx[CpuRegister.Rax] = objectAddress != 0 && state != 0 ? 1UL : 0UL;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "8i-vOVRVt5w",
        ExportName = "sceNpEventFlagSetCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpEventFlagSetCompat1270(CpuContext ctx) =>
        ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(
        Nid = "QlaBcxSFPZI",
        ExportName = "sceNpEventFlagDestroyCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpEventFlagDestroyCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        if (objectAddress != 0 && !ctx.Memory.TryWrite(objectAddress + 8, new byte[0x0C]))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "fhJ5uKzcn0w",
        ExportName = "sceNpCreateDispatchThreadCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpCreateDispatchThreadCompat1270(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        var entryAddress = ctx[CpuRegister.Rsi];
        if (outputAddress == 0 || entryAddress == 0 || !ctx.TryWriteUInt64(outputAddress, outputAddress))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // The firmware helper configures priority/stack attributes and creates
        // the permanent NP dispatch pthread. Bootstrap keeps it quiescent but
        // returns a stable handle so the matching join/cleanup path is valid.
        TraceNp($"dispatch_thread_create_quiescent handle=0x{outputAddress:X} entry=0x{entryAddress:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "EjMsfO3GCIA",
        ExportName = "sceNpJoinDispatchThreadCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpJoinDispatchThreadCompat1270(CpuContext ctx) =>
        ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(
        Nid = "X6NVkdpRnog",
        ExportName = "sceNpWorkerConstructorCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpWorkerConstructorCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        if (objectAddress == 0 ||
            !ctx.Memory.TryWrite(objectAddress, new byte[0x110]) ||
            !KernelMemoryCompatExports.TryWriteDummyVtable(ctx, objectAddress) ||
            !KernelMemoryCompatExports.TryWriteDummyVtable(ctx, objectAddress + 0x70) ||
            !ctx.TryWriteUInt64(objectAddress + 0xC0, objectAddress))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // libSceNpCommon +0x1F580 embeds Mutex (+0x10), Cond (+0x28), and
        // Thread (+0x70) instances, then installs a derived-thread vtable and
        // stores its self pointer at +0xC0. Keep its permanent worker dormant
        // while preserving every field ShellCore inspects or dispatches.
        ctx[CpuRegister.Rax] = objectAddress;
        TraceNp($"worker_ctor object=0x{objectAddress:X}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "1QFKnDJxk3A",
        ExportName = "sceNpWorkerDestructorCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpWorkerDestructorCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = objectAddress;
        TraceNp($"worker_dtor object=0x{objectAddress:X}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "+dGO+GS2ZXQ",
        ExportName = "sceNpWorkerInitializeCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpWorkerInitializeCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        if (objectAddress == 0 || nameAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> nameBuffer = stackalloc byte[0x20];
        nameBuffer.Clear();
        if (ctx.TryReadNullTerminatedUtf8(nameAddress, 0x1F, out var name))
        {
            var encodedName = System.Text.Encoding.UTF8.GetBytes(name);
            encodedName.AsSpan(0, Math.Min(encodedName.Length, 0x1F)).CopyTo(nameBuffer);
        }

        var mutexAddress = objectAddress + 0x10;
        var condAddress = objectAddress + 0x28;
        var threadAddress = objectAddress + 0x70;
        if (!ctx.TryWriteUInt64(mutexAddress + 8, mutexAddress + 8) ||
            !ctx.Memory.TryWrite(mutexAddress + 0x10, new byte[] { 1 }) ||
            !ctx.TryWriteUInt64(condAddress + 8, condAddress + 8) ||
            !ctx.TryWriteUInt64(condAddress + 0x10, mutexAddress + 8) ||
            !ctx.TryWriteUInt32(threadAddress + 0x10, unchecked((uint)ctx[CpuRegister.Rdx])) ||
            !ctx.TryWriteUInt64(threadAddress + 0x18, ctx[CpuRegister.Rcx]) ||
            !ctx.Memory.TryWrite(threadAddress + 0x20, nameBuffer) ||
            !ctx.TryWriteUInt64(threadAddress + 0x40, ctx[CpuRegister.R8]) ||
            !ctx.TryWriteUInt64(threadAddress + 0x48, 0x80559E00_00000001UL))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNp($"worker_init object=0x{objectAddress:X} name=0x{nameAddress:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "VnQolo6vTr4",
        ExportName = "sceNpWorkerStartCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpWorkerStartCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        if (objectAddress == 0 ||
            !ctx.Memory.TryWrite(objectAddress + 8, new byte[] { 0 }) ||
            !ctx.TryWriteUInt32(objectAddress + 0xB8, 2))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNp($"worker_start_quiescent object=0x{objectAddress:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "CznMfhTIvVY",
        ExportName = "sceNpWorkerIsInitializedCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpWorkerIsInitializedCompat1270(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = ctx[CpuRegister.Rdi] == 0 ? 0UL : 1UL;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "eTy3L1azX4E",
        ExportName = "sceNpWorkerIsRunningCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpWorkerIsRunningCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = objectAddress == 0 ? 0UL : 1UL;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "zmOmSLnqlBQ",
        ExportName = "sceNpWorkerNotifyCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpWorkerNotifyCompat1270(CpuContext ctx) =>
        ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(
        Nid = "4DE+nnCVRPA",
        ExportName = "sceNpWorkerStopCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpWorkerStopCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        if (objectAddress != 0)
        {
            _ = ctx.TryWriteUInt32(objectAddress + 0xB8, 3);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "NeopmYshD0U",
        ExportName = "sceNpWorkerDestroyCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpWorkerDestroyCompat1270(CpuContext ctx)
    {
        var objectAddress = ctx[CpuRegister.Rdi];
        if (objectAddress != 0)
        {
            _ = ctx.Memory.TryWrite(objectAddress + 8, new byte[0x68]);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "1x0jThSUr4w",
        ExportName = "sceNpObjectArrayDeleteCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpObjectArrayDeleteCompat1270(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "3z5EPY-ph14",
        ExportName = "_ZN3sce2np4CondC1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpCondConstructor(CpuContext ctx)
    {
        var condAddress = ctx[CpuRegister.Rdi];
        if (condAddress == 0 || !ctx.Memory.TryWrite(condAddress, new byte[0x18]))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = condAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "wWTqVcTnep8",
        ExportName = "_ZN3sce2np4Cond4InitEPKcPNS0_5MutexE",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpCondInit(CpuContext ctx)
    {
        var condAddress = ctx[CpuRegister.Rdi];
        var mutexAddress = ctx[CpuRegister.Rdx];
        if (condAddress == 0 || mutexAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryWriteUInt64(condAddress + 8, condAddress + 8) ||
            !ctx.TryWriteUInt64(condAddress + 0x10, mutexAddress + 8))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "SLPuaDLbeD4",
        ExportName = "_ZN3sce2np4Cond4WaitEj",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpCondWait(CpuContext ctx)
    {
        var condAddress = ctx[CpuRegister.Rdi];
        if (condAddress == 0 ||
            !ctx.TryReadUInt64(condAddress + 8, out var condHandle) ||
            condHandle == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // NP worker threads stay quiescent in the boot MVP because dispatching
        // another guest entry concurrently is not safe yet. CloudSave waits on
        // an intrusive completion list immediately after its embedded Cond;
        // drain that list cooperatively so its normal guest cleanup can run.
        // Restrict the compatibility path to the observed adjacent Mutex/Cond
        // layout instead of changing arbitrary memory after every condition.
        if (ctx.TryReadUInt64(condAddress + 0x10, out var mutexHandle) &&
            mutexHandle == condAddress - 0x10 &&
            ctx.TryReadUInt64(condAddress + 0x20, out var pendingHead) &&
            pendingHead != 0 &&
            !ctx.TryWriteUInt64(condAddress + 0x20, 0))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNp($"cond_wait object=0x{condAddress:X} timeout={ctx[CpuRegister.Rsi]} cooperative=yes");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "yX9ISVXv+0M",
        ExportName = "_ZN3sce2np4CondD1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpCondDestructor(CpuContext ctx)
    {
        var condAddress = ctx[CpuRegister.Rdi];
        if (condAddress != 0 && !ctx.Memory.TryWrite(condAddress + 8, new byte[0x10]))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = condAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "I5uzTXxbziU",
        ExportName = "_ZN3sce2np4Cond7DestroyEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpCondDestroy(CpuContext ctx)
    {
        var condAddress = ctx[CpuRegister.Rdi];
        if (condAddress == 0 || !ctx.Memory.TryWrite(condAddress + 8, new byte[0x10]))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "0f3ylOQJwqE",
        ExportName = "_ZN3sce2np6ThreadC2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpThreadConstructor(CpuContext ctx)
    {
        var threadAddress = ctx[CpuRegister.Rdi];
        if (threadAddress == 0 || !ctx.Memory.TryWrite(threadAddress, new byte[0x60]))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = threadAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "EqX45DhWUpo",
        ExportName = "_ZN3sce2np6Thread4InitEPKcimm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpThreadInit(CpuContext ctx)
    {
        var threadAddress = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        if (threadAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> nameBuffer = stackalloc byte[0x20];
        nameBuffer.Clear();
        if (nameAddress != 0 && ctx.TryReadNullTerminatedUtf8(nameAddress, 0x1F, out var name))
        {
            var encodedName = System.Text.Encoding.UTF8.GetBytes(name);
            encodedName.AsSpan(0, Math.Min(encodedName.Length, 0x1F)).CopyTo(nameBuffer);
        }

        if (!ctx.TryWriteUInt32(threadAddress + 0x10, unchecked((uint)ctx[CpuRegister.Rdx])) ||
            !ctx.TryWriteUInt64(threadAddress + 0x18, ctx[CpuRegister.Rcx]) ||
            !ctx.Memory.TryWrite(threadAddress + 0x20, nameBuffer) ||
            !ctx.TryWriteUInt64(threadAddress + 0x40, ctx[CpuRegister.R8]) ||
            !ctx.TryWriteUInt64(threadAddress + 0x48, 0x80559E00_00000001UL))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "OoK0Ah0l1ko",
        ExportName = "sceNpThreadInitializeWithParamCompat1270",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpThreadInitializeWithParamCompat1270(CpuContext ctx)
    {
        var threadAddress = ctx[CpuRegister.Rdi];
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (threadAddress == 0 ||
            parameterAddress == 0 ||
            !ctx.TryReadUInt32(threadAddress + 0x48, out var state) ||
            !ctx.TryReadUInt64(parameterAddress, out var nameAddress) ||
            !ctx.TryReadUInt64(parameterAddress + 8, out var stackSize) ||
            !ctx.TryReadUInt32(parameterAddress + 0x10, out var priority) ||
            !ctx.TryReadUInt64(parameterAddress + 0x18, out var entryArgument))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (state != 0)
        {
            return ctx.SetReturn(NpCommonErrorAlreadyInitialized);
        }

        Span<byte> nameBuffer = stackalloc byte[0x20];
        nameBuffer.Clear();
        if (nameAddress != 0 && ctx.TryReadNullTerminatedUtf8(nameAddress, 0x1F, out var name))
        {
            var encodedName = System.Text.Encoding.UTF8.GetBytes(name);
            encodedName.AsSpan(0, Math.Min(encodedName.Length, 0x1F)).CopyTo(nameBuffer);
        }

        if (!ctx.TryWriteUInt32(threadAddress + 0x10, priority) ||
            !ctx.TryWriteUInt64(threadAddress + 0x18, stackSize) ||
            !ctx.Memory.TryWrite(threadAddress + 0x20, nameBuffer) ||
            !ctx.TryWriteUInt64(threadAddress + 0x40, entryArgument) ||
            !ctx.TryWriteUInt64(threadAddress + 0x48, 0x80559E00_00000001UL))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNp($"thread_init_param object=0x{threadAddress:X} name=0x{nameAddress:X} priority=0x{priority:X} stack=0x{stackSize:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "6750DaF5Pas",
        ExportName = "_ZN3sce2np6ThreadD2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpThreadDestructor(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = ctx[CpuRegister.Rdi];
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "VNKdE2Dgp0Y",
        ExportName = "_ZN3sce2np6Thread5StartEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpThreadStart(CpuContext ctx)
    {
        var threadAddress = ctx[CpuRegister.Rdi];
        if (threadAddress == 0 || !ctx.TryWriteUInt32(threadAddress + 0x48, 2))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // Background NP workers are intentionally quiescent for the boot MVP;
        // marking the wrapper started preserves its lifecycle without racing
        // a second guest entry through the native dispatcher.
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "ne77q1GOlF8",
        ExportName = "_ZN3sce2np6Thread4JoinEPi",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpThreadJoin(CpuContext ctx)
    {
        var threadAddress = ctx[CpuRegister.Rdi];
        var resultAddress = ctx[CpuRegister.Rsi];
        if (threadAddress == 0 || !ctx.TryWriteUInt32(threadAddress + 0x48, 3))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (resultAddress != 0)
        {
            _ = ctx.TryReadUInt32(threadAddress + 0x4C, out var threadResult);
            if (!ctx.TryWriteUInt32(resultAddress, threadResult))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "-hchsElmzXY",
        ExportName = "_ZN3sce2np4Cond9SignalAllEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpCondSignalAll(CpuContext ctx)
    {
        return ctx[CpuRegister.Rdi] == 0
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "dfXSH2Tsjkw",
        ExportName = "sceNpMemoryHeapDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCommon")]
    public static int NpMemoryHeapDestroy(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rdi];
        if (stateAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (HeapGate)
        {
            Heaps.Remove(stateAddress);
        }

        if (!ctx.Memory.TryWrite(stateAddress, new byte[HeapStateSize]))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceNp($"memory_heap_destroy state=0x{stateAddress:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static bool TryAllocateFromHeap(ulong stateAddress, ulong size, out ulong address)
    {
        address = 0;
        lock (HeapGate)
        {
            if (!Heaps.TryGetValue(stateAddress, out var heap))
            {
                return false;
            }

            var alignedOffset = (heap.NextOffset + (ObjectAlignment - 1)) & ~(ObjectAlignment - 1);
            if (alignedOffset > heap.Size || size > heap.Size - alignedOffset)
            {
                return false;
            }

            address = heap.BaseAddress + alignedOffset;
            heap.NextOffset = alignedOffset + size;
            return true;
        }
    }

    internal static bool TryCreateHleAllocator(CpuContext ctx, ulong poolSize, out ulong allocatorAddress)
    {
        allocatorAddress = 0;
        if (poolSize == 0 || poolSize > int.MaxValue ||
            !KernelMemoryCompatExports.TryAllocateHleData(ctx, 0x1000, 0x1000, out var tableAddress) ||
            !KernelMemoryCompatExports.TryAllocateHleData(ctx, poolSize, 0x4000, out var poolAddress) ||
            !KernelMemoryCompatExports.TryGetDummyCallbackTable(ctx, out var dummyTableAddress) ||
            !ctx.TryReadUInt64(dummyTableAddress, out var noOpStubAddress))
        {
            return false;
        }

        var allocator = new byte[AllocatorSize];
        BinaryPrimitives.WriteUInt64LittleEndian(allocator.AsSpan(0x00), noOpStubAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(allocator.AsSpan(0x08), noOpStubAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(allocator.AsSpan(0x10), noOpStubAddress);
        // +0x18 is null in firmware's public callback table. Reserve it as
        // HLE-only heap context so the Object::new bridges can identify this
        // otherwise module-private allocator without changing its ABI slots.
        BinaryPrimitives.WriteUInt64LittleEndian(allocator.AsSpan(0x18), tableAddress);
        if (!ctx.Memory.TryWrite(tableAddress, allocator))
        {
            return false;
        }

        lock (HeapGate)
        {
            Heaps[tableAddress] = new NpHeapState(poolAddress, poolSize);
        }

        allocatorAddress = tableAddress;
        TraceNp($"hle_allocator_create allocator=0x{tableAddress:X} pool=0x{poolAddress:X} size=0x{poolSize:X}");
        return true;
    }

    internal static void ReleaseHleAllocator(ulong allocatorAddress)
    {
        if (allocatorAddress == 0)
        {
            return;
        }

        lock (HeapGate)
        {
            Heaps.Remove(allocatorAddress);
        }
    }

    private static int NpMutexOperation(CpuContext ctx)
    {
        var mutexAddress = ctx[CpuRegister.Rdi];
        Span<byte> initialized = stackalloc byte[1];
        if (mutexAddress == 0 ||
            !ctx.Memory.TryRead(mutexAddress + 0x10, initialized) ||
            initialized[0] == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static void TraceNp(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] np.{message}");
    }

    private static int NpAtomicFetchUpdate32(CpuContext ctx, bool increment)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        if (valueAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        uint previous;
        lock (Atomic32Gate)
        {
            if (!ctx.TryReadUInt32(valueAddress, out previous) ||
                !ctx.TryWriteUInt32(valueAddress, increment
                    ? unchecked(previous + 1)
                    : unchecked(previous - 1)))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        // NP callers add one to the returned ticket themselves, so these are
        // fetch-before-update operations rather than add-and-return helpers.
        ctx[CpuRegister.Rax] = previous;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private sealed class NpHeapState(ulong baseAddress, ulong size)
    {
        public ulong BaseAddress { get; } = baseAddress;

        public ulong Size { get; } = size;

        public ulong NextOffset { get; set; }
    }
}
