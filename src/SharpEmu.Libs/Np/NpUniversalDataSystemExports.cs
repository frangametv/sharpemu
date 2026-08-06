// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace SharpEmu.Libs.Np;

public static class NpUniversalDataSystemExports
{
    private const int NpUniversalDataSystemErrorInvalidArgument = unchecked((int)0x80553102);
    private const int NpUniversalDataSystemErrorSetTargetInvalid = unchecked((int)0x8055311A);
    private const int NpUniversalDataSystemErrorInvalidProperty = unchecked((int)0x80553115);
    private const int NpUniversalDataSystemErrorNotInitialized = unchecked((int)0x80553117);
    private const int NpUniversalDataSystemErrorPropertyReplacement = unchecked((int)0x80553101);
    private const int NpUniversalDataSystemInternalErrorPropertyReplacement = unchecked((int)0x8055BB02);
    private const int NpUniversalDataSystemInternalErrorArrayFull = unchecked((int)0x8055BB09);
    private const int NpUniversalDataSystemInternalErrorMissingBacking = unchecked((int)0x8055BB0C);
    private const int MaximumEventPropertyStringLength = 16 * 1024;
    private const int MaximumEventPropertyDepth = 64;
    private const int MaximumEventPropertyNodes = 4096;
    private const int ValidPrimitivePropertyTypeMask = 0x799;
    private const ushort EventPropertyStringType = 0x2001;
    private const ushort EventPropertyArrayType = 0x2002;
    private const ushort EventPropertyObjectType = 0x2003;
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);
    private static readonly object _eventGate = new();
    private static readonly object _eventPropertyArrayGate = new();
    private static readonly HashSet<int> _createdEvents = [];
    private static readonly ConcurrentDictionary<ulong, EventPropertyArrayStringShadow[]> _eventPropertyArrayStrings = new();
    private static int _nextHandle = 1;
    private static int _nextEvent = 1;
    private static int _isInitialized;
    private static int _eventPropertyArrayAllocationFailureForTests;

    private sealed record EventPropertyArrayStringShadow(ushort TemporaryType, string Value);

    [SysAbiExport(
        Nid = "sjaobBgqeB4",
        ExportName = "sceNpUniversalDataSystemInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemInitialize(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        Span<byte> parameters = stackalloc byte[16];
        if (!KernelMemoryCompatExports.TryReadCompat(ctx, parameterAddress, parameters))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }

        Volatile.Write(ref _isInitialized, 1);
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "47UAEuQl+iI",
        ExportName = "sceNpUniversalDataSystemTerminate",
        Target = Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem",
        PreferLle = true)]
    public static int NpUniversalDataSystemTerminate(CpuContext ctx)
    {
        if (Interlocked.Exchange(ref _isInitialized, 0) == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorNotInitialized, typeof(long));
        }

        ClearRuntimeState();
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "5zBnau1uIEo",
        ExportName = "sceNpUniversalDataSystemCreateContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateContext(CpuContext ctx)
    {
        var contextAddress = ctx[CpuRegister.Rdi];
        if (contextAddress == 0)
        {
            return ctx.SetReturn(0, typeof(long));
        }

        Span<byte> context = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(context, 1);
        return KernelMemoryCompatExports.TryWriteCompat(ctx, contextAddress, context)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "hT0IAEvN+M0",
        ExportName = "sceNpUniversalDataSystemCreateHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateHandle(CpuContext ctx)
    {
        var handle = Interlocked.Increment(ref _nextHandle);
        if (TryWriteInt32(ctx, ctx[CpuRegister.Rdi], handle) ||
            TryWriteInt32(ctx, ctx[CpuRegister.Rsi], handle))
        {
            return ctx.SetReturn(0, typeof(long));
        }

        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "p+GcLqwpL9M",
        ExportName = "sceNpUniversalDataSystemCreateEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemCreateEvent(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        var eventId = Interlocked.Increment(ref _nextEvent);
        lock (_eventGate)
        {
            _createdEvents.Add(eventId);
        }

        if (TryWriteInt32(ctx, ctx[CpuRegister.Rdx], eventId) ||
            TryWriteInt32(ctx, ctx[CpuRegister.Rcx], eventId))
        {
            return ctx.SetReturn(0, typeof(long));
        }

        lock (_eventGate)
        {
            _createdEvents.Remove(eventId);
        }

        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "wG+84pnNIuo",
        ExportName = "sceNpUniversalDataSystemDestroyEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyEvent(CpuContext ctx)
    {
        var eventId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (_eventGate)
        {
            _createdEvents.Remove(eventId);
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "MfDb+4Nln64",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetString(CpuContext ctx)
    {
        var propertyObjectAddress = ctx[CpuRegister.Rsi];
        var valueAddress = ctx[CpuRegister.Rdx];
        if (propertyObjectAddress == 0 || valueAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        Span<byte> probe = stackalloc byte[1];
        return KernelMemoryCompatExports.TryReadCompat(ctx, propertyObjectAddress, probe) &&
               KernelMemoryCompatExports.TryReadCompat(ctx, valueAddress, probe)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "Wxbg5x3pTXA",
        ExportName = "sceNpUniversalDataSystemEventPropertyObjectSetArray",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyObjectSetArray(CpuContext ctx)
    {
        var propertyObjectAddress = ctx[CpuRegister.Rsi];
        var valueAddress = ctx[CpuRegister.Rdx];
        if (propertyObjectAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorInvalidArgument, typeof(long));
        }

        Span<byte> probe = stackalloc byte[1];
        if (!KernelMemoryCompatExports.TryReadCompat(ctx, propertyObjectAddress, probe))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }

        if (valueAddress != 0 && !KernelMemoryCompatExports.TryReadCompat(ctx, valueAddress, probe))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "4llLk7YJRTE",
        ExportName = "sceNpUniversalDataSystemEventPropertyArraySetString",
        Target = Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemEventPropertyArraySetString(CpuContext ctx)
    {
        if (Volatile.Read(ref _isInitialized) == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorNotInitialized, typeof(long));
        }

        var propertyArrayAddress = ctx[CpuRegister.Rdi];
        if (propertyArrayAddress == 0)
        {
            return ctx.SetReturn(NpUniversalDataSystemErrorSetTargetInvalid, typeof(long));
        }

        lock (_eventPropertyArrayGate)
        {
            if (!IsValidEventProperty(ctx, propertyArrayAddress))
            {
                return ctx.SetReturn(NpUniversalDataSystemErrorInvalidProperty, typeof(long));
            }

            if (!TryReadStrictUtf8CString(ctx, ctx[CpuRegister.Rsi], out var value))
            {
                return ctx.SetReturn(NpUniversalDataSystemErrorInvalidProperty, typeof(long));
            }

            var setterResult = ApplyEventPropertyArrayString(ctx, propertyArrayAddress, value);
            return ctx.SetReturn(setterResult, typeof(long));
        }
    }

    [SysAbiExport(
        Nid = "CzkKf7ahIyU",
        ExportName = "sceNpUniversalDataSystemPostEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemPostEvent(CpuContext ctx)
    {
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "tpFJ8LIKvPw",
        ExportName = "sceNpUniversalDataSystemRegisterContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemRegisterContext(CpuContext ctx)
    {
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "AUIHb7jUX3I",
        ExportName = "sceNpUniversalDataSystemDestroyHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpUniversalDataSystem")]
    public static int NpUniversalDataSystemDestroyHandle(CpuContext ctx)
    {
        return ctx.SetReturn(0, typeof(long));
    }

    internal static bool TryGetEventPropertyArrayStringForTests(ulong address, out string value)
    {
        if (_eventPropertyArrayStrings.TryGetValue(address, out var states) && states.Length != 0)
        {
            value = states[^1].Value;
            return true;
        }

        value = string.Empty;
        return false;
    }

    internal static bool TryGetEventPropertyArrayStringStateForTests(
        ulong address,
        out ushort temporaryType,
        out string value)
    {
        if (_eventPropertyArrayStrings.TryGetValue(address, out var states) && states.Length != 0)
        {
            var state = states[^1];
            temporaryType = state.TemporaryType;
            value = state.Value;
            return true;
        }

        temporaryType = 0;
        value = string.Empty;
        return false;
    }

    internal static bool TryGetEventPropertyArrayStringsForTests(
        ulong address,
        out IReadOnlyList<string> values)
    {
        if (_eventPropertyArrayStrings.TryGetValue(address, out var states))
        {
            values = states.Select(static state => state.Value).ToArray();
            return true;
        }

        values = Array.Empty<string>();
        return false;
    }

    internal static void SetEventPropertyArrayAllocationFailureForTests(bool fail) =>
        Volatile.Write(ref _eventPropertyArrayAllocationFailureForTests, fail ? 1 : 0);

    internal static void ResetForTests()
    {
        Volatile.Write(ref _isInitialized, 0);
        Volatile.Write(ref _eventPropertyArrayAllocationFailureForTests, 0);
        ClearRuntimeState();
    }

    private static void ClearRuntimeState()
    {
        _eventPropertyArrayStrings.Clear();
        lock (_eventGate)
        {
            _createdEvents.Clear();
        }

        _nextHandle = 1;
        _nextEvent = 1;
    }

    private static bool IsValidEventProperty(CpuContext ctx, ulong address)
    {
        var activeVariants = new HashSet<ulong>();
        var activeBackings = new HashSet<ulong>();
        var remainingNodes = MaximumEventPropertyNodes;
        return IsValidEventProperty(
            ctx,
            address,
            depth: 0,
            activeVariants,
            activeBackings,
            ref remainingNodes);
    }

    private static bool IsValidEventProperty(
        CpuContext ctx,
        ulong address,
        int depth,
        HashSet<ulong> activeVariants,
        HashSet<ulong> activeBackings,
        ref int remainingNodes)
    {
        if (address == 0 ||
            depth > MaximumEventPropertyDepth ||
            !activeVariants.Add(address))
        {
            return false;
        }

        try
        {
            if (!TryReadEventPropertyType(ctx, address, out var type))
            {
                return false;
            }

            if (IsValidPrimitiveEventPropertyType(type))
            {
                return true;
            }

            return type switch
            {
                EventPropertyStringType => IsValidEventPropertyString(
                    ctx,
                    address,
                    requireNonEmpty: false),
                EventPropertyArrayType => IsValidEventPropertyContainer(
                    ctx,
                    address,
                    isObject: false,
                    depth,
                    activeVariants,
                    activeBackings,
                    ref remainingNodes),
                EventPropertyObjectType => IsValidEventPropertyContainer(
                    ctx,
                    address,
                    isObject: true,
                    depth,
                    activeVariants,
                    activeBackings,
                    ref remainingNodes),
                0x2004 => true,
                _ => false,
            };
        }
        finally
        {
            activeVariants.Remove(address);
        }
    }

    private static bool IsValidPrimitiveEventPropertyType(ushort type)
    {
        if (type is >= 0x1001 and <= 0x100B)
        {
            var typeBit = type - 0x1001;
            return (ValidPrimitivePropertyTypeMask & (1 << typeBit)) != 0;
        }

        return false;
    }

    private static bool IsValidEventPropertyString(
        CpuContext ctx,
        ulong variantAddress,
        bool requireNonEmpty)
    {
        if (!TryReadEventPropertyType(ctx, variantAddress, out var type) ||
            type != EventPropertyStringType ||
            !TryReadPointer(ctx, variantAddress, 0x08, out var backingAddress) ||
            backingAddress == 0 ||
            !TryReadPointer(ctx, backingAddress, 0x18, out var stringAddress) ||
            !TryReadStrictUtf8CString(ctx, stringAddress, out var value))
        {
            return false;
        }

        return !requireNonEmpty || value.Length != 0;
    }

    private static bool IsValidEventPropertyContainer(
        CpuContext ctx,
        ulong variantAddress,
        bool isObject,
        int depth,
        HashSet<ulong> activeVariants,
        HashSet<ulong> activeBackings,
        ref int remainingNodes)
    {
        if (!TryReadPointer(ctx, variantAddress, 0x08, out var backingAddress))
        {
            return false;
        }

        // A null backing represents an empty/unmaterialized container. The
        // array setter reports its own 0x8055BB0C error for this state.
        if (backingAddress == 0)
        {
            return true;
        }

        if (!activeBackings.Add(backingAddress))
        {
            return false;
        }

        try
        {
            if (!TryReadPointer(ctx, backingAddress, 0x20, out var nodeAddress))
            {
                return false;
            }

            var visitedNodes = new HashSet<ulong>();
            while (nodeAddress != 0)
            {
                if (remainingNodes-- <= 0 || !visitedNodes.Add(nodeAddress))
                {
                    return false;
                }

                if (!TryReadPointer(ctx, nodeAddress, 0x08, out var nextNodeAddress))
                {
                    return false;
                }

                if (isObject)
                {
                    if (!TryAddAddress(nodeAddress, 0x18, out var keyVariantAddress) ||
                        !IsValidEventPropertyString(
                            ctx,
                            keyVariantAddress,
                            requireNonEmpty: true) ||
                        !TryAddAddress(nodeAddress, 0x28, out var valueVariantAddress) ||
                        !IsValidEventProperty(
                            ctx,
                            valueVariantAddress,
                            depth + 1,
                            activeVariants,
                            activeBackings,
                            ref remainingNodes))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!TryAddAddress(nodeAddress, 0x18, out var valueVariantAddress) ||
                        !IsValidEventProperty(
                            ctx,
                            valueVariantAddress,
                            depth + 1,
                            activeVariants,
                            activeBackings,
                            ref remainingNodes))
                    {
                        return false;
                    }
                }

                nodeAddress = nextNodeAddress;
            }

            return true;
        }
        finally
        {
            activeBackings.Remove(backingAddress);
        }
    }

    private static int ApplyEventPropertyArrayString(CpuContext ctx, ulong address, string value)
    {
        if (!TryReadPointer(ctx, address, 0x08, out var backingAddress) || backingAddress == 0)
        {
            return NpUniversalDataSystemInternalErrorMissingBacking;
        }

        if (!TryReadPointer(ctx, backingAddress, 0x20, out var oldHead) ||
            !TryReadPointer(ctx, backingAddress, 0x28, out var oldTail) ||
            !TryReadPointer(ctx, backingAddress, 0x30, out var count))
        {
            return NpUniversalDataSystemInternalErrorMissingBacking;
        }

        if (count == ulong.MaxValue)
        {
            return NpUniversalDataSystemInternalErrorArrayFull;
        }

        if (Volatile.Read(ref _eventPropertyArrayAllocationFailureForTests) != 0 ||
            ctx.Memory is not IGuestMemoryAllocator allocator)
        {
            return NormalizeEventPropertyArraySetterResult(
                NpUniversalDataSystemInternalErrorPropertyReplacement);
        }

        var oldTailNext = 0UL;
        if (oldTail != 0 && !TryReadPointer(ctx, oldTail, 0x08, out oldTailNext))
        {
            return NpUniversalDataSystemInternalErrorMissingBacking;
        }

        if (oldTail != 0 && oldTailNext != 0)
        {
            return NpUniversalDataSystemErrorInvalidProperty;
        }

        if ((oldHead == 0) != (oldTail == 0))
        {
            return NpUniversalDataSystemErrorInvalidProperty;
        }

        if (!TryMaterializeEventPropertyStringNode(
                ctx,
                allocator,
                oldTail,
                value,
                out var nodeAddress,
                out var stringBackingAddress,
                out var stringAddress))
        {
            return NormalizeEventPropertyArraySetterResult(
                NpUniversalDataSystemInternalErrorPropertyReplacement);
        }

        if (!TryCommitEventPropertyStringNode(
                ctx,
                backingAddress,
                oldTail,
                count,
                nodeAddress,
                out var canReleaseUnlinkedNode))
        {
            if (canReleaseUnlinkedNode)
            {
                ReleaseUnlinkedEventPropertyStringNode(
                    allocator,
                    nodeAddress,
                    stringBackingAddress,
                    stringAddress);
            }

            return NormalizeEventPropertyArraySetterResult(
                NpUniversalDataSystemInternalErrorPropertyReplacement);
        }

        // The guest list is now committed. Keep the typed diagnostic shadow in
        // the same append order as the materialized firmware list.
        var appendedValue = new EventPropertyArrayStringShadow(
            EventPropertyStringType,
            value);
        _eventPropertyArrayStrings.AddOrUpdate(
            address,
            [appendedValue],
            (_, existing) => [.. existing, appendedValue]);
        return 0;
    }

    private static bool TryMaterializeEventPropertyStringNode(
        CpuContext ctx,
        IGuestMemoryAllocator allocator,
        ulong previousNodeAddress,
        string value,
        out ulong nodeAddress,
        out ulong stringBackingAddress,
        out ulong stringAddress)
    {
        nodeAddress = 0;
        stringBackingAddress = 0;
        stringAddress = 0;
        var stringBytes = _strictUtf8.GetBytes(value);
        var terminatedString = new byte[stringBytes.Length + 1];
        stringBytes.CopyTo(terminatedString, 0);
        if (!allocator.TryAllocateGuestMemory(0x28, 0x10, out nodeAddress) ||
            !allocator.TryAllocateGuestMemory(0x20, 0x10, out stringBackingAddress) ||
            !allocator.TryAllocateGuestMemory((ulong)terminatedString.Length, 0x10, out stringAddress))
        {
            ReleaseUnlinkedEventPropertyStringNode(
                allocator,
                nodeAddress,
                stringBackingAddress,
                stringAddress);
            nodeAddress = 0;
            stringBackingAddress = 0;
            stringAddress = 0;
            return false;
        }

        var nodePayload = new byte[0x28];
        BinaryPrimitives.WriteUInt64LittleEndian(nodePayload.AsSpan(0x10), previousNodeAddress);
        BinaryPrimitives.WriteUInt16LittleEndian(nodePayload.AsSpan(0x18), EventPropertyStringType);
        BinaryPrimitives.WriteUInt64LittleEndian(nodePayload.AsSpan(0x20), stringBackingAddress);
        var backingPayload = new byte[0x20];
        BinaryPrimitives.WriteUInt64LittleEndian(backingPayload.AsSpan(0x18), stringAddress);
        if (!KernelMemoryCompatExports.TryWriteCompat(ctx, stringAddress, terminatedString) ||
            !KernelMemoryCompatExports.TryWriteCompat(ctx, stringBackingAddress, backingPayload) ||
            !KernelMemoryCompatExports.TryWriteCompat(ctx, nodeAddress, nodePayload))
        {
            ReleaseUnlinkedEventPropertyStringNode(
                allocator,
                nodeAddress,
                stringBackingAddress,
                stringAddress);
            nodeAddress = 0;
            stringBackingAddress = 0;
            stringAddress = 0;
            return false;
        }

        return true;
    }

    private static bool TryCommitEventPropertyStringNode(
        CpuContext ctx,
        ulong backingAddress,
        ulong oldTail,
        ulong count,
        ulong nodeAddress,
        out bool canReleaseUnlinkedNode)
    {
        canReleaseUnlinkedNode = true;
        if (!TryAddAddress(backingAddress, 0x20, out var headAddress) ||
            !TryAddAddress(backingAddress, 0x28, out var tailAddress))
        {
            return false;
        }

        if (oldTail == 0)
        {
            Span<byte> emptyListState = stackalloc byte[0x18];
            BinaryPrimitives.WriteUInt64LittleEndian(emptyListState, nodeAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(emptyListState[0x08..], nodeAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(emptyListState[0x10..], count + 1);
            return KernelMemoryCompatExports.TryWriteCompat(ctx, headAddress, emptyListState);
        }

        Span<byte> appendedListState = stackalloc byte[0x10];
        BinaryPrimitives.WriteUInt64LittleEndian(appendedListState, nodeAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(appendedListState[0x08..], count + 1);
        if (!KernelMemoryCompatExports.TryWriteCompat(ctx, tailAddress, appendedListState))
        {
            return false;
        }

        if (KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, oldTail + 0x08, nodeAddress))
        {
            return true;
        }

        Span<byte> originalListState = stackalloc byte[0x10];
        BinaryPrimitives.WriteUInt64LittleEndian(originalListState, oldTail);
        BinaryPrimitives.WriteUInt64LittleEndian(originalListState[0x08..], count);
        canReleaseUnlinkedNode = KernelMemoryCompatExports.TryWriteCompat(ctx, tailAddress, originalListState);
        return false;
    }

    private static void ReleaseUnlinkedEventPropertyStringNode(
        IGuestMemoryAllocator allocator,
        ulong nodeAddress,
        ulong stringBackingAddress,
        ulong stringAddress)
    {
        if (stringAddress != 0)
        {
            _ = allocator.TryFreeGuestMemory(stringAddress);
        }

        if (stringBackingAddress != 0)
        {
            _ = allocator.TryFreeGuestMemory(stringBackingAddress);
        }

        if (nodeAddress != 0)
        {
            _ = allocator.TryFreeGuestMemory(nodeAddress);
        }
    }

    private static bool TryReadEventPropertyType(CpuContext ctx, ulong address, out ushort type)
    {
        Span<byte> typeBytes = stackalloc byte[sizeof(ushort)];
        if (!KernelMemoryCompatExports.TryReadCompat(ctx, address, typeBytes))
        {
            type = 0;
            return false;
        }

        type = BinaryPrimitives.ReadUInt16LittleEndian(typeBytes);
        return true;
    }

    private static bool TryReadPointer(
        CpuContext ctx,
        ulong address,
        ulong offset,
        out ulong value)
    {
        if (!TryAddAddress(address, offset, out var fieldAddress))
        {
            value = 0;
            return false;
        }

        return KernelMemoryCompatExports.TryReadUInt64Compat(ctx, fieldAddress, out value);
    }

    private static bool TryWriteInt32(CpuContext ctx, ulong address, int value) =>
        address != 0 &&
        KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, address, unchecked((uint)value));

    private static bool TryAddAddress(ulong address, ulong offset, out ulong result)
    {
        if (address > ulong.MaxValue - offset)
        {
            result = 0;
            return false;
        }

        result = address + offset;
        return true;
    }

    private static int NormalizeEventPropertyArraySetterResult(int result)
    {
        if (result >= 0)
        {
            return 0;
        }

        return result == NpUniversalDataSystemInternalErrorPropertyReplacement
            ? NpUniversalDataSystemErrorPropertyReplacement
            : result;
    }

    private static bool TryReadStrictUtf8CString(CpuContext ctx, ulong address, out string value)
    {
        value = string.Empty;
        if (address == 0)
        {
            return false;
        }

        var bytes = new byte[MaximumEventPropertyStringLength];
        Span<byte> current = stackalloc byte[1];
        for (var index = 0; index < bytes.Length; index++)
        {
            if (!KernelMemoryCompatExports.TryReadCompat(ctx, address + (ulong)index, current))
            {
                return false;
            }

            if (current[0] == 0)
            {
                try
                {
                    value = _strictUtf8.GetString(bytes, 0, index);
                    return true;
                }
                catch (DecoderFallbackException)
                {
                    return false;
                }
            }

            bytes[index] = current[0];
        }

        return false;
    }
}
