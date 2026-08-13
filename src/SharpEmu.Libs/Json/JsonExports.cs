// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Json;

public static class JsonExports
{
    private const int ValueObjectSize = 0x20;
    private const int StringObjectSize = 0x08;
    private const ulong MaximumJsonBufferSize = 16 * 1024 * 1024;
    private const int SceJsonParserErrorInvalidToken = unchecked((int)0x80920101);
    private const int SceJsonParserErrorEmptyBuffer = unchecked((int)0x80920105);

    private sealed record JsonValueState(JsonElement Element);

    private sealed record JsonStringState(
        string Value,
        ulong GuestBufferAddress = 0,
        int GuestBufferCapacity = 0);

    private readonly record struct JsonReferenceKey(ulong ValueAddress, string Key);

    private sealed record Json2InitializationState(
        ulong Allocator,
        ulong AllocatorContext,
        ulong FileBufferSize,
        uint Mode);

    private static readonly ConcurrentDictionary<ulong, JsonValueState> _values = new();
    private static readonly ConcurrentDictionary<ulong, JsonStringState> _strings = new();
    private static readonly ConcurrentDictionary<JsonReferenceKey, ulong> _valueReferences = new();
    private static readonly JsonElement _nullElement = CreateNullElement();
    private static readonly object _globalNullAccessCallbackGate = new();
    private static Json2InitializationState? _json2InitializationState;
    private static bool _initializerInitialize2AllocationFailureForTests;

    private const int SceJsonErrorInitializationFailed = unchecked((int)0x80848102);
    private const int SceJsonErrorNotInitialized = unchecked((int)0x80848110);
    private const int SceJsonErrorAlreadyInitialized = unchecked((int)0x80848111);
    private const int SceJsonErrorCallbackAlreadySet = unchecked((int)0x80848112);
    private const int SceJsonErrorInvalidCallback = unchecked((int)0x80848120);

    private sealed record JsonArrayState(JsonElement Element, long Identity);

    private sealed record JsonArrayIteratorState(
        JsonArrayState Array,
        int Position,
        ulong ValueAddress = 0);

    private static readonly ConcurrentDictionary<ulong, ulong> _valueStrings = new();
    private static readonly ConcurrentDictionary<ulong, JsonArrayState> _arrays = new();
    private static readonly ConcurrentDictionary<ulong, JsonElement> _objects = new();
    private static readonly ConcurrentDictionary<ulong, JsonArrayIteratorState> _arrayIterators = new();
    private static readonly JsonElement _emptyArrayElement = CreateEmptyArrayElement();
    private static readonly JsonElement _emptyObjectElement = CreateEmptyObjectElement();
    private static long _nextArrayIdentity;

    [SysAbiExport(
        Nid = "-hJRce8wn1U",
        ExportName = "_ZN3sce4Json12MemAllocatorC2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int MemAllocatorConstructor(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        TraceJson("MemAllocator.ctor", thisAddress, 0);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "OcAgPxcq5Vk",
        ExportName = "_ZN3sce4Json12MemAllocatorD2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int MemAllocatorDestructor(CpuContext ctx)
    {
        TraceJson("MemAllocator.dtor", ctx[CpuRegister.Rdi], 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "cK6bYHf-Q5E",
        ExportName = "_ZN3sce4Json11InitializerC1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerConstructor(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // Some native C++ heaps are host-backed and are not represented by the
        // primary guest-memory facade. The constructor has no state beyond its
        // zero marker, so keep that store advisory like the pre-#770 baseline.
        _ = KernelMemoryCompatExports.TryWriteCompat(ctx, thisAddress, new byte[] { 0 });

        TraceJson("Initializer.ctor", thisAddress, 0);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "RujUxbr3haM",
        ExportName = "_ZN3sce4Json11InitializerD1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerDestructor(CpuContext ctx)
    {
        TraceJson("Initializer.dtor", ctx[CpuRegister.Rdi], 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Cxwy7wHq4J0",
        ExportName = "_ZN3sce4Json11Initializer10initializeEPKNS0_13InitParameterE",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerInitialize(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        var initParameterAddress = ctx[CpuRegister.Rsi];
        Span<byte> initialized = stackalloc byte[1];
        if (thisAddress == 0 || !ctx.Memory.TryRead(thisAddress, initialized))
        {
            return SetReturn(ctx, SceJsonErrorAlreadyInitialized);
        }

        lock (_globalNullAccessCallbackGate)
        {
            if (_json2InitializationState is not null || initialized[0] != 0)
            {
                return SetReturn(ctx, SceJsonErrorAlreadyInitialized);
            }

            if (initParameterAddress == 0)
            {
                return SetReturn(ctx, SceJsonErrorInvalidCallback);
            }

            Span<byte> initParameters = stackalloc byte[0x18];
            if (!ctx.Memory.TryRead(initParameterAddress, initParameters))
            {
                return SetReturn(ctx, SceJsonErrorInitializationFailed);
            }

            var allocator = BinaryPrimitives.ReadUInt64LittleEndian(initParameters);
            if (allocator == 0)
            {
                return SetReturn(ctx, SceJsonErrorInitializationFailed);
            }

            if (!ctx.Memory.TryWrite(thisAddress, new byte[] { 1 }))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            // Retain the guest allocator contract for lifecycle fidelity. Calling
            // its guest vtable remains an explicit HLE boundary.
            _json2InitializationState = new Json2InitializationState(
                allocator,
                BinaryPrimitives.ReadUInt64LittleEndian(initParameters[0x08..]),
                BinaryPrimitives.ReadUInt64LittleEndian(initParameters[0x10..]),
                Mode: 0);
        }

        TraceJson("Initializer.initialize", thisAddress, initParameterAddress);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "+drDFyAS6u4",
        ExportName = "_ZN3sce4Json11Initializer27setGlobalNullAccessCallbackEPFRKNS0_5ValueENS0_9ValueTypeEPS3_PvES7_",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitializerSetGlobalNullAccessCallback(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (_globalNullAccessCallbackGate)
        {
            JsonObjectHeap.GlobalNullAccessCallback = ctx[CpuRegister.Rsi];
            JsonObjectHeap.GlobalNullAccessCallbackContext = ctx[CpuRegister.Rdx];
        }
        TraceJson("Initializer.setGlobalNullAccessCallback", thisAddress, ctx[CpuRegister.Rsi]);
        return SetReturn(ctx, 0);
    }

    // Catalog alias NID for the same callback setter.
    #pragma warning disable SHEM004
    #pragma warning restore SHEM004

    // Kept as a direct helper for tests and JSON2 lifecycle emulation. The
    // shared NID is exported once by the catalog-backed libSceJson alias above.
    [SysAbiExport(
        Nid = "00oCq0RwSAY",
        ExportName = "_ZN3sce4Json11Initializer27setGlobalNullAccessCallBackEPFRKNS0_5ValueENS0_9ValueTypeEPS3_PvES7_",
        Target = Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int InitializerSetGlobalNullAccessCallBack(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        var callback = ctx[CpuRegister.Rsi];
        lock (_globalNullAccessCallbackGate)
        {
            Span<byte> initialized = stackalloc byte[1];
            if (_json2InitializationState is null ||
                thisAddress == 0 ||
                !ctx.Memory.TryRead(thisAddress, initialized) ||
                initialized[0] == 0)
            {
                return SetReturn(ctx, SceJsonErrorNotInitialized);
            }

            if (callback == 0)
            {
                return SetReturn(ctx, SceJsonErrorInvalidCallback);
            }

            if (JsonObjectHeap.GlobalNullAccessCallback != 0)
            {
                return SetReturn(ctx, SceJsonErrorCallbackAlreadySet);
            }

            JsonObjectHeap.GlobalNullAccessCallback = callback;
            JsonObjectHeap.GlobalNullAccessCallbackContext = ctx[CpuRegister.Rdx];
        }

        TraceJson("Initializer.setGlobalNullAccessCallBack", thisAddress, callback);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "WSOuge5IsCg",
        ExportName = "_ZN3sce4Json14InitParameter2C1Ev",
        Target = Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitParameter2Constructor(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // The PS5 ABI object occupies 0x28 bytes in the caller's frame. Its
        // setters below replace the allocator and file-buffer fields.
        Span<byte> parameter = stackalloc byte[0x28];
        parameter.Clear();
        if (!ctx.Memory.TryWrite(thisAddress, parameter))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceJson("InitParameter2.ctor", thisAddress, 0);
        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "I2QC8PYhJWY",
        ExportName = "_ZN3sce4Json14InitParameter212setAllocatorEPNS0_12MemAllocatorEPv",
        Target = Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitParameter2SetAllocator(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> fields = stackalloc byte[sizeof(ulong) * 2];
        BinaryPrimitives.WriteUInt64LittleEndian(fields, ctx[CpuRegister.Rsi]);
        BinaryPrimitives.WriteUInt64LittleEndian(fields[sizeof(ulong)..], ctx[CpuRegister.Rdx]);
        if (!ctx.Memory.TryWrite(thisAddress, fields))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Eu95jmqn5Rw",
        ExportName = "_ZN3sce4Json14InitParameter217setFileBufferSizeEm",
        Target = Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int InitParameter2SetFileBufferSize(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress == 0 || !ctx.TryWriteUInt64(thisAddress + 0x10, ctx[CpuRegister.Rsi]))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rax] = thisAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "IXW-z8pggfg",
        ExportName = "_ZN3sce4Json11Initializer10initializeEPKNS0_14InitParameter2E",
        Target = Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int InitializerInitialize2(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        var initParameterAddress = ctx[CpuRegister.Rsi];
        Span<byte> initialized = stackalloc byte[1];
        if (thisAddress == 0 || !ctx.Memory.TryRead(thisAddress, initialized))
        {
            return SetReturn(ctx, SceJsonErrorAlreadyInitialized);
        }

        lock (_globalNullAccessCallbackGate)
        {
            if (_json2InitializationState is not null || initialized[0] != 0)
            {
                return SetReturn(ctx, SceJsonErrorAlreadyInitialized);
            }

            Span<byte> initParameters = stackalloc byte[0x28];
            if (initParameterAddress == 0 ||
                !ctx.Memory.TryRead(initParameterAddress, initParameters))
            {
                return SetReturn(ctx, SceJsonErrorInvalidCallback);
            }

            var allocator = BinaryPrimitives.ReadUInt64LittleEndian(initParameters);
            var mode = BinaryPrimitives.ReadUInt32LittleEndian(initParameters[0x18..]);
            if (allocator == 0 || mode >= 3)
            {
                return SetReturn(ctx, SceJsonErrorInvalidCallback);
            }

            if (_initializerInitialize2AllocationFailureForTests)
            {
                return SetReturn(ctx, SceJsonErrorInitializationFailed);
            }

            if (!ctx.Memory.TryWrite(thisAddress, new byte[] { 1 }))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            // Retain the guest allocator contract for lifecycle fidelity. Calling
            // its guest vtable remains an explicit HLE boundary.
            _json2InitializationState = new Json2InitializationState(
                allocator,
                BinaryPrimitives.ReadUInt64LittleEndian(initParameters[0x08..]),
                BinaryPrimitives.ReadUInt64LittleEndian(initParameters[0x10..]),
                mode);
        }

        TraceJson("Initializer.initialize2", thisAddress, initParameterAddress);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "PR5k1penBLM",
        ExportName = "_ZN3sce4Json11Initializer9terminateEv",
        Target = Generation.Gen5,
        LibraryName = "libSceJson2",
        PreferLle = true)]
    public static int InitializerTerminate(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        Span<byte> initialized = stackalloc byte[1];

        lock (_globalNullAccessCallbackGate)
        {
            // The Gen5 provider requires both its global allocator state and
            // the one-byte Initializer state before it tears down the heaps.
            if (_json2InitializationState is null ||
                thisAddress == 0 ||
                !ctx.Memory.TryRead(thisAddress, initialized) ||
                initialized[0] == 0)
            {
                return SetReturn(ctx, SceJsonErrorNotInitialized);
            }

            if (!ctx.Memory.TryWrite(thisAddress, new byte[] { 0 }))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            _json2InitializationState = null;
            JsonObjectHeap.GlobalNullAccessCallback = 0;
            JsonObjectHeap.GlobalNullAccessCallbackContext = 0;
            JsonObjectHeap.Values.Clear();
            JsonObjectHeap.Strings.Clear();
            _values.Clear();
            _strings.Clear();
            _valueStrings.Clear();
            _arrays.Clear();
            _objects.Clear();
            _arrayIterators.Clear();
            _valueReferences.Clear();
            Interlocked.Exchange(ref _nextArrayIdentity, 0);
        }

        TraceJson("Initializer.terminate", thisAddress, 0);
        return SetReturn(ctx, 0);
    }

    public static int ValueConstructor(CpuContext ctx)
    {
        _ = ConstructValue(ctx);
        return JsonValueExports.ValueDefaultConstructor(ctx);
    }

    [SysAbiExport(
        Nid = "-wa17B7TGnw",
        ExportName = "_ZN3sce4Json5ValueC2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueBaseConstructor(CpuContext ctx) => ConstructValue(ctx);
    public static int ValueDestructor(CpuContext ctx)
    {
        _ = DestroyValue(ctx);
        return JsonValueExports.ValueDestructor(ctx);
    }

    [SysAbiExport(
        Nid = "0eUrW9JAxM0",
        ExportName = "_ZN3sce4Json5ValueD2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueBaseDestructor(CpuContext ctx) => DestroyValue(ctx);

    [SysAbiExport(
        Nid = "S5JxQnoGF3E",
        ExportName = "_ZN3sce4Json6Parser5parseERNS0_5ValueEPKcm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ParserParseBuffer(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var bufferAddress = ctx[CpuRegister.Rsi];
        var bufferSize = ctx[CpuRegister.Rdx];
        if (valueAddress == 0 || bufferAddress == 0 || bufferSize == 0)
        {
            return SetReturn(ctx, SceJsonParserErrorEmptyBuffer);
        }

        if (bufferSize > MaximumJsonBufferSize || bufferSize > int.MaxValue)
        {
            return SetReturn(ctx, SceJsonParserErrorInvalidToken);
        }

        var buffer = new byte[(int)bufferSize];
        if (!ctx.Memory.TryRead(bufferAddress, buffer))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        try
        {
            using var document = JsonDocument.Parse(buffer);
            var element = document.RootElement.Clone();
            StoreValue(ctx, valueAddress, element);
            TraceJsonText("Parser.parse", valueAddress, Encoding.UTF8.GetString(buffer));
            return SetReturn(ctx, 0);
        }
        catch (JsonException)
        {
            return SetReturn(ctx, SceJsonParserErrorInvalidToken);
        }
    }

    [SysAbiExport(
        Nid = "SHtAad20YYM",
        ExportName = "_ZNK3sce4Json5Value7getTypeEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetType(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var element = GetValue(valueAddress);
        ctx[CpuRegister.Rax] = (ulong)GetValueType(element);
        return 0;
    }

    [SysAbiExport(
        Nid = "RBw+4NukeGQ",
        ExportName = "_ZNK3sce4Json5Value5countEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueCount(CpuContext ctx)
    {
        var element = GetValue(ctx[CpuRegister.Rdi]);
        ctx[CpuRegister.Rax] = element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Array => (ulong)element.GetArrayLength(),
            System.Text.Json.JsonValueKind.Object => (ulong)element.EnumerateObject().Count(),
            _ => 0,
        };
        return 0;
    }

    [SysAbiExport(
        Nid = "zTwZdI8AZ5Y",
        ExportName = "_ZNK3sce4Json5Value10getBooleanEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetBoolean(CpuContext ctx) => ReturnValueStorage(ctx);

    [SysAbiExport(
        Nid = "DIxvoy7Ngvk",
        ExportName = "_ZNK3sce4Json5Value10getIntegerEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetInteger(CpuContext ctx) => ReturnValueStorage(ctx);

    [SysAbiExport(
        Nid = "sn4HNCtNRzY",
        ExportName = "_ZNK3sce4Json5Value11getUIntegerEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetUnsignedInteger(CpuContext ctx) => ReturnValueStorage(ctx);

    [SysAbiExport(
        Nid = "3qrge7L-AU4",
        ExportName = "_ZNK3sce4Json5Value7getRealEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetReal(CpuContext ctx) => ReturnValueStorage(ctx);

    [SysAbiExport(
        Nid = "HwDt5lD9Bfo",
        ExportName = "_ZNK3sce4Json5ValueixEPKc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueIndexCString(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var keyAddress = ctx[CpuRegister.Rsi];
        if (!TryReadUtf8CString(ctx, keyAddress, 4096, out var key))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        return ReturnNamedValue(ctx, key);
    }

    [SysAbiExport(
        Nid = "wLsJlmgEIaI",
        ExportName = "_ZN3sce4Json5Value10referValueERKNS0_6StringE",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueReferValueByString(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var stringAddress = ctx[CpuRegister.Rsi];
        if (!TryGetStringValue(ctx, stringAddress, out var key))
        {
            ctx[CpuRegister.Rax] = 0;
            TraceJsonReference(valueAddress, string.Empty, 0, 0);
            return 0;
        }

        var parent = GetValue(valueAddress);
        if (parent.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !parent.TryGetProperty(key, out var property) ||
            !TryGetOrAllocateValueReference(ctx, new JsonReferenceKey(valueAddress, key), out var childAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            TraceJsonReference(valueAddress, key, 0, 0);
            return 0;
        }

        var child = property.Clone();
        StoreValue(ctx, childAddress, child);
        ctx[CpuRegister.Rax] = childAddress;
        TraceJsonReference(valueAddress, key, childAddress, GetValueType(child));
        return 0;
    }

    [SysAbiExport(
        Nid = "XlWbvieLj2M",
        ExportName = "_ZNK3sce4Json5ValueixEm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueIndexPosition(CpuContext ctx) => ReturnIndexedValue(ctx);

    [SysAbiExport(
        Nid = "0YqYAoO-+Uo",
        ExportName = "_ZNK3sce4Json5Value8getValueEm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetPosition(CpuContext ctx) => ReturnIndexedValue(ctx);

    [SysAbiExport(
        Nid = "fSb2oQTNrgA",
        ExportName = "_ZN3sce4Json5ValueC1ERKS1_",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueCopyConstructor(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        if (destinationAddress != 0)
        {
            StoreValue(ctx, destinationAddress, GetValue(ctx[CpuRegister.Rsi]));
        }

        ctx[CpuRegister.Rax] = destinationAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "MsMOdxWfbwQ",
        ExportName = "_ZNK3sce4Json5Value8getValueERKNS0_6StringE",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetStringKey(CpuContext ctx)
    {
        var key = JsonObjectHeap.GetStringOrEmpty(ctx[CpuRegister.Rsi]);
        return ReturnNamedValue(ctx, key);
    }

    [SysAbiExport(
        Nid = "epJ6x2LV0kU",
        ExportName = "_ZNK3sce4Json5Value9getStringEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetString(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var element = GetValue(valueAddress);
        var text = element.ValueKind == System.Text.Json.JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

        if (!_valueStrings.TryGetValue(valueAddress, out var stringAddress))
        {
            if (!TryAllocateGuestObject(ctx, StringObjectSize, out stringAddress))
            {
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            _valueStrings[valueAddress] = stringAddress;
            ctx.TryWriteUInt64(stringAddress, 0);
        }

        _strings.AddOrUpdate(
            stringAddress,
            _ => new JsonStringState(text),
            (_, state) => state with { Value = text });
        ctx[CpuRegister.Rax] = stringAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "ONT8As5R1ug",
        ExportName = "_ZNK3sce4Json5Value8getArrayEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueGetArray(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        if (valueAddress == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        _arrays[valueAddress] = CreateArrayState(GetValue(valueAddress));
        ctx[CpuRegister.Rax] = valueAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    public static int ValueObjectConstructor(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        StoreValue(ctx, destinationAddress, GetObjectElement(ctx[CpuRegister.Rsi]));
        ctx[CpuRegister.Rax] = destinationAddress;
        return 0;
    }

    [SysAbiExport(
        Nid = "dFCphqnd+a4",
        ExportName = "_ZN3sce4Json5Value3setERKNS0_6ObjectE",
        Target = Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int ValueSetObject(CpuContext ctx) => ValueObjectConstructor(ctx);

    [SysAbiExport(
        Nid = "iZeYfOxtMRg",
        ExportName = "_ZN3sce4Json5ValueC1ERKNS0_5ArrayE",
        Target = Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int ValueArrayConstructor(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        StoreValue(ctx, destinationAddress, GetArrayElement(ctx[CpuRegister.Rsi]));
        ctx[CpuRegister.Rax] = destinationAddress;
        return 0;
    }

    public static int ValueGetObject(CpuContext ctx)
    {
        // Object is returned by value. Itanium passes hidden result storage in
        // RDI and the Value `this` pointer in RSI.
        var destinationAddress = ctx[CpuRegister.Rdi];
        var value = GetValue(ctx[CpuRegister.Rsi]);
        if (destinationAddress != 0)
        {
            _objects[destinationAddress] = value.ValueKind == System.Text.Json.JsonValueKind.Object
                ? value.Clone()
                : _emptyObjectElement;
        }
        ctx[CpuRegister.Rax] = destinationAddress;
        return 0;
    }

    public static int ObjectDefaultConstructor(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress != 0)
        {
            _objects[thisAddress] = _emptyObjectElement;
        }

        ctx[CpuRegister.Rax] = thisAddress;
        return 0;
    }

    public static int ObjectCopyConstructor(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        if (destinationAddress != 0)
        {
            _objects[destinationAddress] = GetObjectElement(ctx[CpuRegister.Rsi]).Clone();
        }

        ctx[CpuRegister.Rax] = destinationAddress;
        return 0;
    }

    public static int ObjectAssignment(CpuContext ctx) => ObjectCopyConstructor(ctx);

    public static int ObjectClear(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress != 0)
        {
            _objects[thisAddress] = _emptyObjectElement;
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    public static int ObjectDestructor(CpuContext ctx)
    {
        _objects.TryRemove(ctx[CpuRegister.Rdi], out _);
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "JP-PtKMiI1E",
        ExportName = "_ZN3sce4Json5ArrayC1Ev",
        Target = Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int ArrayDefaultConstructor(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress != 0)
        {
            _arrays[thisAddress] = CreateArrayState(_emptyArrayElement);
        }

        ctx[CpuRegister.Rax] = thisAddress;
        return 0;
    }

    public static int ArraySize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = (ulong)GetArrayElement(ctx[CpuRegister.Rdi]).GetArrayLength();
        return 0;
    }

    public static int StringLength(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = TryGetStringValue(ctx, ctx[CpuRegister.Rdi], out var value)
            ? (ulong)Encoding.UTF8.GetByteCount(value)
            : 0;
        return 0;
    }

    public static int StringAssignment(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        _ = TryGetStringValue(ctx, ctx[CpuRegister.Rsi], out var value);
        if (_strings.TryGetValue(destinationAddress, out var existing))
        {
            _strings[destinationAddress] = existing with { Value = value };
        }
        else if (destinationAddress != 0)
        {
            _strings[destinationAddress] = new JsonStringState(value);
        }

        if (destinationAddress != 0)
        {
            JsonObjectHeap.Strings[destinationAddress] = value;
        }

        ctx[CpuRegister.Rax] = destinationAddress;
        return 0;
    }

    [SysAbiExport(
        Nid = "-NxEk7XLkDY",
        ExportName = "_ZN3sce4Json5Value11referObjectEv",
        Target = Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int ValueReferObject(CpuContext ctx) => ReturnAggregateStorage(ctx, expectedType: 7);

    [SysAbiExport(
        Nid = "nM5XqdeXFPw",
        ExportName = "_ZN3sce4Json5Value10referArrayEv",
        Target = Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int ValueReferArray(CpuContext ctx) => ReturnAggregateStorage(ctx, expectedType: 6);

    [SysAbiExport(
        Nid = "ERuf9y0DY84",
        ExportName = "_ZN3sce4Json6ObjectixERKNS0_6StringE",
        Target = Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int ObjectIndexString(CpuContext ctx)
    {
        var objectStorage = ctx[CpuRegister.Rdi];
        _ = TryGetStringValue(ctx, ctx[CpuRegister.Rsi], out var key);
        return ReturnNamedElement(
            ctx,
            objectStorage,
            GetObjectElement(objectStorage),
            key);
    }

    [SysAbiExport(
        Nid = "zQtLRTqceMY",
        ExportName = "_ZN3sce4Json5Array9push_backERKNS0_5ValueE",
        Target = Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int ArrayPushBack(CpuContext ctx)
    {
        var arrayStorage = ctx[CpuRegister.Rdi];
        var valueAddress = arrayStorage >= 0x10 ? arrayStorage - 0x10 : 0;
        var current = GetValue(valueAddress);
        var items = current.ValueKind == System.Text.Json.JsonValueKind.Array
            ? current.EnumerateArray().Select(static item => item.GetRawText()).ToList()
            : new List<string>();
        items.Add(GetValue(ctx[CpuRegister.Rsi]).GetRawText());
        using var document = JsonDocument.Parse($"[{string.Join(',', items)}]");
        StoreValue(ctx, valueAddress, document.RootElement);
        ctx[CpuRegister.Rax] = arrayStorage;
        return 0;
    }

    [SysAbiExport(
        Nid = "bAM9Qwofus0",
        ExportName = "_ZNK3sce4Json5Array4backEv",
        Target = Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int ArrayBack(CpuContext ctx)
    {
        var arrayStorage = ctx[CpuRegister.Rdi];
        var valueAddress = arrayStorage >= 0x10 ? arrayStorage - 0x10 : 0;
        var array = GetValue(valueAddress);
        if (array.ValueKind != System.Text.Json.JsonValueKind.Array || array.GetArrayLength() == 0 ||
            !TryGetOrAllocateValueReference(ctx, new JsonReferenceKey(valueAddress, "#back"), out var result))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        StoreValue(ctx, result, array[array.GetArrayLength() - 1]);
        ctx[CpuRegister.Rax] = result;
        return 0;
    }

    [SysAbiExport(
        Nid = "R7FDWtcN6f8",
        ExportName = "_ZN3sce4Json5Value9serializeERNS0_6StringE",
        Target = Generation.Gen5,
        LibraryName = "libSceJson2")]
    public static int ValueSerialize(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rsi];
        if (destination == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        _strings[destination] = new JsonStringState(GetValue(ctx[CpuRegister.Rdi]).GetRawText());
        ctx.TryWriteUInt64(destination, 0);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "bI5AGFMydrA",
        ExportName = "_ZN3sce4Json5ArrayC1ERKS1_",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ArrayCopyConstructor(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        if (destinationAddress != 0)
        {
            _arrays[destinationAddress] = GetArrayState(ctx[CpuRegister.Rsi]);
        }

        ctx[CpuRegister.Rax] = destinationAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "HJ8GpRT1aiw",
        ExportName = "_ZN3sce4Json5ArrayD1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ArrayDestructor(CpuContext ctx)
    {
        _arrays.TryRemove(ctx[CpuRegister.Rdi], out _);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "bcH5EnFE2xY",
        ExportName = "_ZNK3sce4Json5Array5beginEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ArrayBegin(CpuContext ctx) => ConstructArrayIterator(ctx, atEnd: false);

    [SysAbiExport(
        Nid = "WXF2ihRF+B8",
        ExportName = "_ZNK3sce4Json5Array3endEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ArrayEnd(CpuContext ctx) => ConstructArrayIterator(ctx, atEnd: true);

    [SysAbiExport(
        Nid = "5AZPp99ogrc",
        ExportName = "_ZNK3sce4Json5Array8iteratorneERKS2_",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ArrayIteratorNotEqual(CpuContext ctx)
    {
        var different =
            _arrayIterators.TryGetValue(ctx[CpuRegister.Rdi], out var left) &&
            _arrayIterators.TryGetValue(ctx[CpuRegister.Rsi], out var right) &&
            (left.Array.Identity != right.Array.Identity || left.Position != right.Position);
        ctx[CpuRegister.Rax] = different ? 1UL : 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "wcgr5mte7T8",
        ExportName = "_ZNK3sce4Json5Array8iteratordeEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ArrayIteratorDereference(CpuContext ctx) => ReturnArrayIteratorValue(ctx);

    [SysAbiExport(
        Nid = "iAIYn4oAWvI",
        ExportName = "_ZNK3sce4Json5Array8iteratorptEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ArrayIteratorPointer(CpuContext ctx) => ReturnArrayIteratorValue(ctx);

    [SysAbiExport(
        Nid = "w5+VCznos5E",
        ExportName = "_ZN3sce4Json5Array8iteratorppEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ArrayIteratorIncrement(CpuContext ctx)
    {
        var iteratorAddress = ctx[CpuRegister.Rdi];
        if (_arrayIterators.TryGetValue(iteratorAddress, out var iterator))
        {
            var length = iterator.Array.Element.GetArrayLength();
            _arrayIterators[iteratorAddress] = iterator with
            {
                Position = Math.Min(iterator.Position + 1, length),
                ValueAddress = 0,
            };
        }

        ctx[CpuRegister.Rax] = iteratorAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "9yLjn46Ypfs",
        ExportName = "_ZN3sce4Json5Array8iteratorD1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ArrayIteratorDestructor(CpuContext ctx)
    {
        _arrayIterators.TryRemove(ctx[CpuRegister.Rdi], out _);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "4zrm6VrgIAw",
        ExportName = "_ZN3sce4Json5ValueaSERKS1_",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueAssignment(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        var sourceAddress = ctx[CpuRegister.Rsi];
        if (destinationAddress != 0)
        {
            StoreValue(ctx, destinationAddress, GetValue(sourceAddress));
        }

        ctx[CpuRegister.Rax] = destinationAddress;
        return 0;
    }
    public static int StringConstructor(CpuContext ctx)
    {
        _ = ConstructString(ctx);
        return JsonValueExports.StringDefaultConstructor(ctx);
    }

    [SysAbiExport(
        Nid = "eG9E9M6XvTM",
        ExportName = "_ZN3sce4Json6StringC2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int StringBaseConstructor(CpuContext ctx) => ConstructString(ctx);
    public static int StringDestructor(CpuContext ctx)
    {
        _ = DestroyString(ctx);
        return JsonValueExports.StringDestructor(ctx);
    }

    [SysAbiExport(
        Nid = "Ui7YFnSTCBw",
        ExportName = "_ZN3sce4Json6StringD2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int StringBaseDestructor(CpuContext ctx) => DestroyString(ctx);

    [SysAbiExport(
        Nid = "Ncel8t2Rrpc",
        ExportName = "_ZNK3sce4Json5Value8toStringERNS0_6StringE",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int ValueToString(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var stringAddress = ctx[CpuRegister.Rsi];
        if (stringAddress != 0)
        {
            var element = GetValue(valueAddress);
            var value = element.ValueKind == System.Text.Json.JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : element.GetRawText();
            _strings[stringAddress] = new JsonStringState(value);
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "L1KAkYWml-M",
        ExportName = "_ZNK3sce4Json6String5c_strEv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceJson")]
    public static int StringCStr(CpuContext ctx)
    {
        var stringAddress = ctx[CpuRegister.Rdi];
        if (!_strings.TryGetValue(stringAddress, out var state))
        {
            state = new JsonStringState(string.Empty);
        }

        var bytes = Encoding.UTF8.GetBytes(state.Value + '\0');
        var guestBufferAddress = state.GuestBufferAddress;
        if (guestBufferAddress == 0 || state.GuestBufferCapacity < bytes.Length)
        {
            if (!TryAllocateGuestObject(ctx, bytes.Length, out guestBufferAddress))
            {
                ctx[CpuRegister.Rax] = 0;
                return 0;
            }
        }

        if (!ctx.Memory.TryWrite(guestBufferAddress, bytes))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        _strings[stringAddress] = state with
        {
            GuestBufferAddress = guestBufferAddress,
            GuestBufferCapacity = bytes.Length,
        };
        ctx.TryWriteUInt64(stringAddress, guestBufferAddress);
        ctx[CpuRegister.Rax] = guestBufferAddress;
        TraceJsonText("String.c_str", stringAddress, state.Value);
        return 0;
    }

    private static int ConstructValue(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        StoreValue(ctx, thisAddress, _nullElement);
        ctx[CpuRegister.Rax] = thisAddress;
        return 0;
    }

    private static int ReturnIndexedValue(CpuContext ctx)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        var position = ctx[CpuRegister.Rsi];
        if (!TryAllocateGuestObject(ctx, ValueObjectSize, out var childAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        var parent = GetValue(valueAddress);
        var child = parent.ValueKind == System.Text.Json.JsonValueKind.Array &&
            position < (ulong)parent.GetArrayLength()
            ? parent[(int)position].Clone()
            : _nullElement;
        StoreValue(ctx, childAddress, child);
        ctx[CpuRegister.Rax] = childAddress;
        return 0;
    }

    private static int ReturnNamedValue(CpuContext ctx, string key)
    {
        var valueAddress = ctx[CpuRegister.Rdi];
        return ReturnNamedElement(ctx, valueAddress, GetValue(valueAddress), key);
    }

    private static int ReturnNamedElement(
        CpuContext ctx,
        ulong parentAddress,
        JsonElement parent,
        string key)
    {
        if (!TryAllocateGuestObject(ctx, ValueObjectSize, out var childAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var child = parent.ValueKind == System.Text.Json.JsonValueKind.Object &&
            parent.TryGetProperty(key, out var property)
            ? property.Clone()
            : _nullElement;
        StoreValue(ctx, childAddress, child);
        ctx[CpuRegister.Rax] = childAddress;
        TraceJsonText("Value.get", parentAddress, key);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int ConstructArrayIterator(CpuContext ctx, bool atEnd)
    {
        // Array::begin/end return an eight-byte iterator by value. The Itanium C++ ABI passes
        // its hidden return-storage pointer in RDI and the Array `this` pointer in RSI.
        var iteratorAddress = ctx[CpuRegister.Rdi];
        var array = GetArrayState(ctx[CpuRegister.Rsi]);
        if (iteratorAddress != 0)
        {
            _arrayIterators[iteratorAddress] = new JsonArrayIteratorState(
                array,
                atEnd ? array.Element.GetArrayLength() : 0);
        }

        ctx[CpuRegister.Rax] = iteratorAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int ReturnArrayIteratorValue(CpuContext ctx)
    {
        var iteratorAddress = ctx[CpuRegister.Rdi];
        if (!_arrayIterators.TryGetValue(iteratorAddress, out var iterator) ||
            iterator.Position < 0 ||
            iterator.Position >= iterator.Array.Element.GetArrayLength())
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var valueAddress = iterator.ValueAddress;
        if (valueAddress == 0)
        {
            if (!TryAllocateGuestObject(ctx, ValueObjectSize, out valueAddress))
            {
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            StoreValue(ctx, valueAddress, iterator.Array.Element[iterator.Position]);
            _arrayIterators[iteratorAddress] = iterator with { ValueAddress = valueAddress };
        }

        ctx[CpuRegister.Rax] = valueAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static JsonArrayState GetArrayState(ulong address)
    {
        if (address != 0 && _arrays.TryGetValue(address, out var state))
        {
            return state;
        }

        return CreateArrayState(GetValue(address));
    }

    private static JsonElement GetArrayElement(ulong address)
    {
        if (address != 0 && _arrays.TryGetValue(address, out var state))
        {
            return state.Element;
        }

        if (address >= 0x10)
        {
            var value = GetValue(address - 0x10);
            if (value.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                return value;
            }
        }

        var directValue = GetValue(address);
        return directValue.ValueKind == System.Text.Json.JsonValueKind.Array
            ? directValue
            : _emptyArrayElement;
    }

    private static JsonElement GetObjectElement(ulong address)
    {
        if (address != 0 && _objects.TryGetValue(address, out var state))
        {
            return state;
        }

        if (address >= 0x10)
        {
            var value = GetValue(address - 0x10);
            if (value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                return value;
            }
        }

        var directValue = GetValue(address);
        return directValue.ValueKind == System.Text.Json.JsonValueKind.Object
            ? directValue
            : _emptyObjectElement;
    }

    private static JsonArrayState CreateArrayState(JsonElement element)
    {
        var array = element.ValueKind == System.Text.Json.JsonValueKind.Array
            ? element.Clone()
            : _emptyArrayElement;
        return new JsonArrayState(array, Interlocked.Increment(ref _nextArrayIdentity));
    }

    private static int ReturnValueStorage(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = thisAddress == 0 ? 0 : thisAddress + 0x10;
        return 0;
    }

    private static int ReturnAggregateStorage(CpuContext ctx, int expectedType)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = thisAddress != 0 && GetValueType(GetValue(thisAddress)) == expectedType
            ? thisAddress + 0x10
            : 0;
        return 0;
    }

    private static int DestroyValue(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        RemoveCompleteValueShadow(ctx, thisAddress);
        _arrays.TryRemove(thisAddress, out _);
        _valueStrings.TryRemove(thisAddress, out _);
        if (thisAddress != 0)
        {
            Span<byte> empty = stackalloc byte[ValueObjectSize];
            empty.Clear();
            ctx.Memory.TryWrite(thisAddress, empty);
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static int ConstructString(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        if (thisAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        _strings[thisAddress] = new JsonStringState(string.Empty);
        ctx.TryWriteUInt64(thisAddress, 0);
        ctx[CpuRegister.Rax] = thisAddress;
        return 0;
    }

    private static int DestroyString(CpuContext ctx)
    {
        var thisAddress = ctx[CpuRegister.Rdi];
        RemoveCompleteStringShadow(ctx, thisAddress);
        if (thisAddress != 0)
        {
            ctx.TryWriteUInt64(thisAddress, 0);
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static JsonElement CreateNullElement()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    private static JsonElement CreateEmptyArrayElement()
    {
        using var document = JsonDocument.Parse("[]");
        return document.RootElement.Clone();
    }

    private static JsonElement CreateEmptyObjectElement()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static JsonElement GetValue(ulong address) =>
        address != 0 && _values.TryGetValue(address, out var state)
            ? state.Element
            : _nullElement;

    private static void StoreValue(CpuContext ctx, ulong address, JsonElement element)
    {
        if (address == 0)
        {
            return;
        }

        var clone = element.Clone();
        _values[address] = new JsonValueState(clone);
        _arrays.TryRemove(address, out _);
        _objects.TryRemove(address, out _);
        _valueStrings.TryRemove(address, out _);

        Span<byte> mirror = stackalloc byte[ValueObjectSize];
        mirror.Clear();
        var type = GetValueType(clone);
        var typeOffset = ctx.TargetGeneration == Generation.Gen4 ? 0x18 : 0x1C;
        BinaryPrimitives.WriteInt32LittleEndian(mirror[typeOffset..], type);
        switch (clone.ValueKind)
        {
            case System.Text.Json.JsonValueKind.True:
                mirror[0x10] = 1;
                break;
            case System.Text.Json.JsonValueKind.Number when clone.TryGetInt64(out var integer):
                BinaryPrimitives.WriteInt64LittleEndian(mirror[0x10..], integer);
                break;
            case System.Text.Json.JsonValueKind.Number when clone.TryGetUInt64(out var unsignedInteger):
                BinaryPrimitives.WriteUInt64LittleEndian(mirror[0x10..], unsignedInteger);
                break;
            case System.Text.Json.JsonValueKind.Number:
                BinaryPrimitives.WriteInt64LittleEndian(
                    mirror[0x10..],
                    BitConverter.DoubleToInt64Bits(clone.GetDouble()));
                break;
        }

        ctx.Memory.TryWrite(address, mirror);
    }

    private static int GetValueType(JsonElement element) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => 1,
        System.Text.Json.JsonValueKind.Number when element.TryGetInt64(out _) => 2,
        System.Text.Json.JsonValueKind.Number when element.TryGetUInt64(out _) => 3,
        System.Text.Json.JsonValueKind.Number => 4,
        System.Text.Json.JsonValueKind.String => 5,
        System.Text.Json.JsonValueKind.Array => 6,
        System.Text.Json.JsonValueKind.Object => 7,
        _ => 0,
    };

    private static bool TryAllocateGuestObject(CpuContext ctx, int size, out ulong address)
    {
        address = 0;
        return size > 0 &&
            ctx.Memory is IGuestMemoryAllocator allocator &&
            allocator.TryAllocateGuestMemory((ulong)size, 0x10, out address);
    }

    private static bool TryGetOrAllocateValueReference(
        CpuContext ctx,
        JsonReferenceKey key,
        out ulong address)
    {
        if (_valueReferences.TryGetValue(key, out address))
        {
            return true;
        }

        if (!TryAllocateGuestObject(ctx, ValueObjectSize, out var allocatedAddress))
        {
            address = 0;
            return false;
        }

        address = _valueReferences.GetOrAdd(key, allocatedAddress);
        if (address != allocatedAddress && ctx.Memory is IGuestMemoryAllocator allocator)
        {
            allocator.TryFreeGuestMemory(allocatedAddress);
        }

        return true;
    }

    internal static void RemoveCompleteValueShadow(CpuContext ctx, ulong address)
    {
        _values.TryRemove(address, out _);
        foreach (var reference in _valueReferences.Where(entry => entry.Key.ValueAddress == address).ToArray())
        {
            if (!_valueReferences.TryRemove(reference.Key, out var childAddress))
            {
                continue;
            }

            _values.TryRemove(childAddress, out _);
            if (ctx.Memory is IGuestMemoryAllocator allocator)
            {
                allocator.TryFreeGuestMemory(childAddress);
            }
        }
    }

    internal static void RemoveCompleteStringShadow(CpuContext ctx, ulong address)
    {
        if (!_strings.TryRemove(address, out var state) ||
            state.GuestBufferAddress == 0 ||
            ctx.Memory is not IGuestMemoryAllocator allocator)
        {
            return;
        }

        allocator.TryFreeGuestMemory(state.GuestBufferAddress);
    }

    internal static void ResetForTests()
    {
        _values.Clear();
        _strings.Clear();
        _valueStrings.Clear();
        _arrays.Clear();
        _objects.Clear();
        _arrayIterators.Clear();
        _valueReferences.Clear();
        Interlocked.Exchange(ref _nextArrayIdentity, 0);
        lock (_globalNullAccessCallbackGate)
        {
            _json2InitializationState = null;
            _initializerInitialize2AllocationFailureForTests = false;
        }
    }

    internal static bool TryGetJson2InitializationStateForTests(
        out ulong allocator,
        out ulong allocatorContext,
        out ulong fileBufferSize,
        out uint mode)
    {
        lock (_globalNullAccessCallbackGate)
        {
            if (_json2InitializationState is { } state)
            {
                allocator = state.Allocator;
                allocatorContext = state.AllocatorContext;
                fileBufferSize = state.FileBufferSize;
                mode = state.Mode;
                return true;
            }
        }

        allocator = 0;
        allocatorContext = 0;
        fileBufferSize = 0;
        mode = 0;
        return false;
    }

    internal static void SetInitializerInitialize2AllocationFailureForTests(bool fail)
    {
        lock (_globalNullAccessCallbackGate)
        {
            _initializerInitialize2AllocationFailureForTests = fail;
        }
    }

    private static bool TryReadUtf8CString(
        CpuContext ctx,
        ulong address,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (address == 0 || maximumLength <= 0)
        {
            return false;
        }

        var bytes = new byte[maximumLength];
        Span<byte> current = stackalloc byte[1];
        for (var index = 0; index < bytes.Length; index++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)index, current))
            {
                return false;
            }

            if (current[0] == 0)
            {
                value = Encoding.UTF8.GetString(bytes, 0, index);
                return true;
            }

            bytes[index] = current[0];
        }

        return false;
    }

    private static bool TryGetStringValue(CpuContext ctx, ulong address, out string value)
    {
        value = string.Empty;
        if (address == 0)
        {
            return false;
        }

        if (_strings.TryGetValue(address, out var stringState))
        {
            value = stringState.Value;
            return true;
        }

        if (JsonObjectHeap.Strings.TryGetValue(address, out var heapValue))
        {
            value = heapValue;
            return true;
        }

        return ctx.TryReadUInt64(address, out var bufferAddress) &&
            TryReadUtf8CString(ctx, bufferAddress, 4096, out value);
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static void TraceJson(string operation, ulong thisAddress, ulong argument)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_JSON"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] json.{operation} this=0x{thisAddress:X16} arg=0x{argument:X16}");
    }

    private static void TraceJsonText(string operation, ulong thisAddress, string value)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_JSON"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var preview = value.Length <= 128 ? value : value[..128];
        Console.Error.WriteLine(
            $"[LOADER][TRACE] json.{operation} this=0x{thisAddress:X16} value={preview}");
    }

    private static void TraceJsonReference(
        ulong valueAddress,
        string key,
        ulong childAddress,
        int childType)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_JSON"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var preview = key.Length <= 128 ? key : key[..128];
        Console.Error.WriteLine(
            $"[LOADER][TRACE] json.Value.refer this=0x{valueAddress:X16} key={preview} " +
            $"child=0x{childAddress:X16} type={childType}");
    }
}
