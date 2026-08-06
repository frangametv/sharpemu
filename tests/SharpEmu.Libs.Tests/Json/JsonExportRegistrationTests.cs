// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Json;
using Xunit;

namespace SharpEmu.Libs.Tests.Json;

// These NIDs came back "unresolved" in the Quake (PPSA01880) import log right before its
// access violation. This asserts they now resolve to the Json handlers and dispatch cleanly,
// which is the plumbing the direct-call tests cannot cover.
[Collection("JsonObjectHeap")]
public sealed class JsonExportRegistrationTests
{
    private static readonly (string Nid, string Name)[] ExpectedExports =
    {
        ("qBMjqyBn3OM", "_ZN3sce4Json5ValueC1Ev"),
        ("5yHuiWXo2gg", "_ZN3sce4Json5Value3setEb"),
        ("QxVVYhP-mvg", "_ZN3sce4Json5Value3setEl"),
        ("SIe1ZmW7e7s", "_ZN3sce4Json5Value3setEm"),
        ("BSmWDIkV4w4", "_ZN3sce4Json5Value3setEd"),
        ("IKQimvG9Wqs", "_ZN3sce4Json5Value3setENS0_9ValueTypeE"),
        ("6l3Bv2gysNc", "_ZN3sce4Json5Value3setERKNS0_6StringE"),
        ("wLsJlmgEIaI", "_ZN3sce4Json5Value10referValueERKNS0_6StringE"),
        ("9KUZFjI1IxA", "_ZN3sce4Json6StringC1EPKc"),
        ("cG1VE2HMl6c", "_ZN3sce4Json6StringD1Ev"),
        ("+drDFyAS6u4", "_ZN3sce4Json11Initializer27setGlobalNullAccessCallbackEPFRKNS0_5ValueENS0_9ValueTypeEPS3_PvES7_"),
        ("00oCq0RwSAY", "_ZN3sce4Json11Initializer27setGlobalNullAccessCallBackEPFRKNS0_5ValueENS0_9ValueTypeEPS3_PvES7_"),
        ("IXW-z8pggfg", "_ZN3sce4Json11Initializer10initializeEPKNS0_14InitParameter2E"),
        ("fSb2oQTNrgA", "_ZN3sce4Json5ValueC1ERKS1_"),
        ("ONT8As5R1ug", "_ZNK3sce4Json5Value8getArrayEv"),
        ("MsMOdxWfbwQ", "_ZNK3sce4Json5Value8getValueERKNS0_6StringE"),
        ("epJ6x2LV0kU", "_ZNK3sce4Json5Value9getStringEv"),
        ("bI5AGFMydrA", "_ZN3sce4Json5ArrayC1ERKS1_"),
        ("bcH5EnFE2xY", "_ZNK3sce4Json5Array5beginEv"),
        ("WXF2ihRF+B8", "_ZNK3sce4Json5Array3endEv"),
        ("5AZPp99ogrc", "_ZNK3sce4Json5Array8iteratorneERKS2_"),
        ("wcgr5mte7T8", "_ZNK3sce4Json5Array8iteratordeEv"),
        ("iAIYn4oAWvI", "_ZNK3sce4Json5Array8iteratorptEv"),
        ("w5+VCznos5E", "_ZN3sce4Json5Array8iteratorppEv"),
        ("9yLjn46Ypfs", "_ZN3sce4Json5Array8iteratorD1Ev"),
        ("HJ8GpRT1aiw", "_ZN3sce4Json5ArrayD1Ev"),
    };

    private static ModuleManager CreateRegisteredManager()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        return manager;
    }

    [Fact]
    public void QuakeUnresolvedJsonNids_ResolveToJsonExports()
    {
        var manager = CreateRegisteredManager();

        foreach (var (nid, name) in ExpectedExports)
        {
            Assert.True(manager.TryGetExport(nid, out var export), $"NID {nid} did not register.");
            Assert.Equal(name, export.Name);
            Assert.Equal(
                nid is "00oCq0RwSAY" or "IXW-z8pggfg" ? "libSceJson2" : "libSceJson",
                export.LibraryName);
        }
    }

    [Fact]
    public void SetGlobalNullAccessCallback_StoresHookAndReturnsOk()
    {
        JsonObjectHeap.ResetForTests();
        var manager = CreateRegisteredManager();
        var ctx = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0x1_0000_0000; // Initializer instance
        ctx[CpuRegister.Rsi] = 0x8_0012_3456; // guest callback
        ctx[CpuRegister.Rdx] = 0x1_0000_0800; // user context

        Assert.True(manager.TryDispatch("+drDFyAS6u4", ctx, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(0x8_0012_3456UL, JsonObjectHeap.GlobalNullAccessCallback);
        Assert.Equal(0x1_0000_0800UL, JsonObjectHeap.GlobalNullAccessCallbackContext);
    }

    [Fact]
    public void SetGlobalNullAccessCallBack_StoresOnlyTheFirstValidHook()
    {
        const ulong initializerAddress = 0x1_0000_0000;
        JsonObjectHeap.ResetForTests();
        var memory = new FakeCpuMemory(initializerAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        InitializeJson2(ctx, initializerAddress, initializerAddress + 0x100);
        ctx[CpuRegister.Rdi] = initializerAddress;
        ctx[CpuRegister.Rsi] = 0x8_0012_3456;
        ctx[CpuRegister.Rdx] = initializerAddress + 0x800;

        Assert.Equal(0, JsonExports.InitializerSetGlobalNullAccessCallBack(ctx));
        Assert.Equal(0x8_0012_3456UL, JsonObjectHeap.GlobalNullAccessCallback);
        Assert.Equal(initializerAddress + 0x800, JsonObjectHeap.GlobalNullAccessCallbackContext);

        ctx[CpuRegister.Rsi] = 0x8_0065_4321;
        ctx[CpuRegister.Rdx] = initializerAddress + 0x900;
        Assert.Equal(unchecked((int)0x80848112), JsonExports.InitializerSetGlobalNullAccessCallBack(ctx));
        Assert.Equal(0x8_0012_3456UL, JsonObjectHeap.GlobalNullAccessCallback);
        Assert.Equal(initializerAddress + 0x800, JsonObjectHeap.GlobalNullAccessCallbackContext);
    }

    [Fact]
    public void SetGlobalNullAccessCallBack_RejectsForgedSelfWhenGlobalStateIsNotReady()
    {
        const ulong initializerAddress = 0x1_0000_0000;
        JsonObjectHeap.ResetForTests();
        var memory = new FakeCpuMemory(initializerAddress, 0x1000);
        Assert.True(memory.TryWrite(initializerAddress, new byte[] { 1 }));
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = initializerAddress;
        ctx[CpuRegister.Rsi] = 0x8_0012_3456;
        ctx[CpuRegister.Rdx] = initializerAddress + 0x800;

        Assert.Equal(unchecked((int)0x80848110), JsonExports.InitializerSetGlobalNullAccessCallBack(ctx));
        Assert.Equal(0UL, JsonObjectHeap.GlobalNullAccessCallback);
        Assert.Equal(0UL, JsonObjectHeap.GlobalNullAccessCallbackContext);
    }

    [Fact]
    public void SetGlobalNullAccessCallBack_RejectsUninitializedSelfWhenGlobalStateIsReady()
    {
        const ulong initializedAddress = 0x1_0000_0000;
        const ulong uninitializedAddress = initializedAddress + 0x40;
        JsonObjectHeap.ResetForTests();
        var memory = new FakeCpuMemory(initializedAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        InitializeJson2(ctx, initializedAddress, initializedAddress + 0x100);
        ctx[CpuRegister.Rdi] = uninitializedAddress;
        Assert.Equal(0, JsonExports.InitializerConstructor(ctx));
        ctx[CpuRegister.Rsi] = 0x8_0012_3456;
        ctx[CpuRegister.Rdx] = initializedAddress + 0x800;

        Assert.Equal(unchecked((int)0x80848110), JsonExports.InitializerSetGlobalNullAccessCallBack(ctx));
        Assert.Equal(0UL, JsonObjectHeap.GlobalNullAccessCallback);
    }

    [Fact]
    public void SetGlobalNullAccessCallBack_RejectsNullCallbackAfterRealInitialization()
    {
        const ulong initializerAddress = 0x1_0000_0000;
        JsonObjectHeap.ResetForTests();
        var memory = new FakeCpuMemory(initializerAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        InitializeJson2(ctx, initializerAddress, initializerAddress + 0x100);
        ctx[CpuRegister.Rdi] = initializerAddress;
        ctx[CpuRegister.Rsi] = 0;

        Assert.Equal(unchecked((int)0x80848120), JsonExports.InitializerSetGlobalNullAccessCallBack(ctx));
        Assert.Equal(0UL, JsonObjectHeap.GlobalNullAccessCallback);
    }

    [Fact]
    public void InitializerInitialize2_ConstructorToCallbackFlowSetsSharedLifecycle()
    {
        const ulong initializerAddress = 0x1_0000_0000;
        const ulong parameterAddress = initializerAddress + 0x100;
        const ulong callback = 0x8_0012_3456;
        JsonObjectHeap.ResetForTests();
        var memory = new FakeCpuMemory(initializerAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        ctx[CpuRegister.Rdi] = initializerAddress;
        Assert.Equal(0, JsonExports.InitializerConstructor(ctx));
        ctx[CpuRegister.Rdi] = parameterAddress;
        Assert.Equal(0, JsonExports.InitParameter2Constructor(ctx));
        ctx[CpuRegister.Rdi] = parameterAddress;
        ctx[CpuRegister.Rsi] = initializerAddress + 0x300;
        ctx[CpuRegister.Rdx] = initializerAddress + 0x400;
        Assert.Equal(0, JsonExports.InitParameter2SetAllocator(ctx));
        ctx[CpuRegister.Rdi] = parameterAddress;
        ctx[CpuRegister.Rsi] = 0x5678;
        Assert.Equal(0, JsonExports.InitParameter2SetFileBufferSize(ctx));
        Assert.True(ctx.TryWriteUInt32(parameterAddress + 0x18, 2));
        ctx[CpuRegister.Rdi] = initializerAddress;
        ctx[CpuRegister.Rsi] = parameterAddress;
        Assert.Equal(0, JsonExports.InitializerInitialize2(ctx));

        Span<byte> initialized = stackalloc byte[1];
        Assert.True(memory.TryRead(initializerAddress, initialized));
        Assert.Equal(1, initialized[0]);
        Assert.True(JsonExports.TryGetJson2InitializationStateForTests(
            out var allocator,
            out var allocatorContext,
            out var fileBufferSize,
            out var mode));
        Assert.Equal(initializerAddress + 0x300, allocator);
        Assert.Equal(initializerAddress + 0x400, allocatorContext);
        Assert.Equal(0x5678UL, fileBufferSize);
        Assert.Equal(2U, mode);
        Assert.Equal(unchecked((int)0x80848111), JsonExports.InitializerInitialize2(ctx));

        ctx[CpuRegister.Rdi] = initializerAddress;
        ctx[CpuRegister.Rsi] = callback;
        ctx[CpuRegister.Rdx] = initializerAddress + 0x800;
        Assert.Equal(0, JsonExports.InitializerSetGlobalNullAccessCallBack(ctx));
        Assert.Equal(callback, JsonObjectHeap.GlobalNullAccessCallback);
    }

    [Fact]
    public void InitializerInitialize2_RejectsInvalidModeAndAllocationFailureWithoutInitializing()
    {
        const ulong initializerAddress = 0x1_0000_0000;
        const ulong parameterAddress = initializerAddress + 0x100;
        JsonObjectHeap.ResetForTests();
        var memory = new FakeCpuMemory(initializerAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = initializerAddress;
        Assert.Equal(0, JsonExports.InitializerConstructor(ctx));
        ctx[CpuRegister.Rdi] = parameterAddress;
        Assert.Equal(0, JsonExports.InitParameter2Constructor(ctx));

        ctx[CpuRegister.Rdi] = initializerAddress;
        ctx[CpuRegister.Rsi] = parameterAddress;
        Assert.Equal(unchecked((int)0x80848120), JsonExports.InitializerInitialize2(ctx));

        ctx[CpuRegister.Rdi] = parameterAddress;
        ctx[CpuRegister.Rsi] = initializerAddress + 0x300;
        ctx[CpuRegister.Rdx] = initializerAddress + 0x400;
        Assert.Equal(0, JsonExports.InitParameter2SetAllocator(ctx));
        Assert.True(ctx.TryWriteUInt32(parameterAddress + 0x18, 3));
        ctx[CpuRegister.Rdi] = initializerAddress;
        ctx[CpuRegister.Rsi] = parameterAddress;
        Assert.Equal(unchecked((int)0x80848120), JsonExports.InitializerInitialize2(ctx));

        Assert.True(ctx.TryWriteUInt32(parameterAddress + 0x18, 2));
        JsonExports.SetInitializerInitialize2AllocationFailureForTests(true);
        Assert.Equal(unchecked((int)0x80848102), JsonExports.InitializerInitialize2(ctx));

        Span<byte> initialized = stackalloc byte[1];
        Assert.True(memory.TryRead(initializerAddress, initialized));
        Assert.Equal(0, initialized[0]);
        Assert.False(JsonExports.TryGetJson2InitializationStateForTests(
            out _,
            out _,
            out _,
            out _));
    }

    [Fact]
    public void InitializerTerminate_ClearsJson2LifecycleAndRejectsSecondTermination()
    {
        const ulong initializerAddress = 0x1_0000_0000;
        const ulong parameterAddress = initializerAddress + 0x100;
        JsonObjectHeap.ResetForTests();
        var memory = new FakeCpuMemory(initializerAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var manager = CreateRegisteredManager();

        ctx[CpuRegister.Rdi] = initializerAddress;
        Assert.Equal(0, JsonExports.InitializerConstructor(ctx));
        ctx[CpuRegister.Rdi] = parameterAddress;
        Assert.Equal(0, JsonExports.InitParameter2Constructor(ctx));
        ctx[CpuRegister.Rdi] = parameterAddress;
        ctx[CpuRegister.Rsi] = initializerAddress + 0x300;
        ctx[CpuRegister.Rdx] = initializerAddress + 0x400;
        Assert.Equal(0, JsonExports.InitParameter2SetAllocator(ctx));
        ctx[CpuRegister.Rdi] = initializerAddress;
        ctx[CpuRegister.Rsi] = parameterAddress;
        Assert.Equal(0, JsonExports.InitializerInitialize2(ctx));
        ctx[CpuRegister.Rdi] = initializerAddress;
        ctx[CpuRegister.Rsi] = 0x8_0012_3456;
        ctx[CpuRegister.Rdx] = initializerAddress + 0x800;
        Assert.Equal(0, JsonExports.InitializerSetGlobalNullAccessCallBack(ctx));

        ctx[CpuRegister.Rdi] = initializerAddress;
        Assert.True(manager.TryDispatch("PR5k1penBLM", ctx, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

        Span<byte> initialized = stackalloc byte[1];
        Assert.True(memory.TryRead(initializerAddress, initialized));
        Assert.Equal(0, initialized[0]);
        Assert.False(JsonExports.TryGetJson2InitializationStateForTests(out _, out _, out _, out _));
        Assert.Equal(0UL, JsonObjectHeap.GlobalNullAccessCallback);
        Assert.Equal(0UL, JsonObjectHeap.GlobalNullAccessCallbackContext);

        Assert.True(manager.TryDispatch("PR5k1penBLM", ctx, out result));
        Assert.Equal(unchecked((OrbisGen2Result)(int)0x80848110), result);
        Assert.Equal(unchecked((ulong)(int)0x80848110), ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void InitializerTerminate_RegistersExactGen5SemanticFallback()
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == "PR5k1penBLM");

        Assert.Equal("_ZN3sce4Json11Initializer9terminateEv", export.Name);
        Assert.Equal("libSceJson2", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
        Assert.True(export.PreferLle);
        Assert.Equal(typeof(JsonExports), export.Function.Method.DeclaringType);
    }

    [Fact]
    public void DispatchValueConstructor_RunsHandlerAndReturnsThis()
    {
        JsonObjectHeap.ResetForTests();
        var manager = CreateRegisteredManager();
        var ctx = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0x1_0000_0000;

        Assert.True(manager.TryDispatch("qBMjqyBn3OM", ctx, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0x1_0000_0000UL, ctx[CpuRegister.Rax]);
        Assert.Equal(JsonValueKind.Null, JsonObjectHeap.Values[0x1_0000_0000].Kind);
    }

    private static void InitializeJson2(CpuContext ctx, ulong initializerAddress, ulong parameterAddress)
    {
        ctx[CpuRegister.Rdi] = initializerAddress;
        Assert.Equal(0, JsonExports.InitializerConstructor(ctx));
        Span<byte> state = stackalloc byte[1];
        Assert.True(ctx.Memory.TryRead(initializerAddress, state));
        Assert.Equal(0, state[0]);
        Assert.True(ctx.TryWriteUInt64(parameterAddress, parameterAddress + 0x100));
        Assert.True(ctx.TryWriteUInt64(parameterAddress + 8, parameterAddress + 0x200));
        Assert.True(ctx.TryWriteUInt64(parameterAddress + 16, 0));
        ctx[CpuRegister.Rsi] = parameterAddress;
        Assert.Equal(0, JsonExports.InitializerInitialize(ctx));
        Assert.True(ctx.Memory.TryRead(initializerAddress, state));
        Assert.Equal(1, state[0]);
        Assert.True(JsonExports.TryGetJson2InitializationStateForTests(
            out var allocator,
            out var allocatorContext,
            out var fileBufferSize,
            out var mode));
        Assert.Equal(parameterAddress + 0x100, allocator);
        Assert.Equal(parameterAddress + 0x200, allocatorContext);
        Assert.Equal(0UL, fileBufferSize);
        Assert.Equal(0U, mode);
    }

    [Fact]
    public void ParsedArray_CanBeCopiedIteratedAndReadByJsonStringKey()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong rootAddress = memoryBase + 0x100;
        const ulong jsonAddress = memoryBase + 0x1000;
        const ulong arrayKeyAddress = memoryBase + 0x2000;
        const ulong idKeyAddress = memoryBase + 0x2100;
        const ulong nameKeyAddress = memoryBase + 0x2200;
        const ulong idStringAddress = memoryBase + 0x2300;
        const ulong nameStringAddress = memoryBase + 0x2310;
        const ulong arrayCopyAddress = memoryBase + 0x2400;
        const ulong beginAddress = memoryBase + 0x2410;
        const ulong endAddress = memoryBase + 0x2420;

        JsonExports.ResetForTests();
        JsonObjectHeap.ResetForTests();
        var memory = new AllocatingTestMemory(memoryBase, 0x20000, allocationOffset: 0x10000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var json = Encoding.UTF8.GetBytes(
            "{\"clientComponents\":[{\"id\":42,\"name\":\"alpha\"},{\"id\":43,\"name\":\"beta\"}]}");
        Assert.True(memory.TryWrite(jsonAddress, json));
        memory.WriteCString(arrayKeyAddress, "clientComponents");
        memory.WriteCString(idKeyAddress, "id");
        memory.WriteCString(nameKeyAddress, "name");

        ctx[CpuRegister.Rdi] = rootAddress;
        ctx[CpuRegister.Rsi] = jsonAddress;
        ctx[CpuRegister.Rdx] = (ulong)json.Length;
        Assert.Equal(0, JsonExports.ParserParseBuffer(ctx));

        ctx[CpuRegister.Rdi] = rootAddress;
        ctx[CpuRegister.Rsi] = arrayKeyAddress;
        Assert.Equal(0, JsonExports.ValueIndexCString(ctx));
        var arrayValueAddress = ctx[CpuRegister.Rax];
        Assert.NotEqual(0UL, arrayValueAddress);

        ctx[CpuRegister.Rdi] = arrayValueAddress;
        Assert.Equal(0, JsonExports.ValueGetArray(ctx));
        ctx[CpuRegister.Rsi] = ctx[CpuRegister.Rax];
        ctx[CpuRegister.Rdi] = arrayCopyAddress;
        Assert.Equal(0, JsonExports.ArrayCopyConstructor(ctx));

        ctx[CpuRegister.Rdi] = beginAddress;
        ctx[CpuRegister.Rsi] = arrayCopyAddress;
        Assert.Equal(0, JsonExports.ArrayBegin(ctx));
        ctx[CpuRegister.Rdi] = endAddress;
        ctx[CpuRegister.Rsi] = arrayCopyAddress;
        Assert.Equal(0, JsonExports.ArrayEnd(ctx));

        ctx[CpuRegister.Rdi] = idStringAddress;
        ctx[CpuRegister.Rsi] = idKeyAddress;
        Assert.Equal(0, JsonValueExports.StringCStringConstructor(ctx));
        ctx[CpuRegister.Rdi] = nameStringAddress;
        ctx[CpuRegister.Rsi] = nameKeyAddress;
        Assert.Equal(0, JsonValueExports.StringCStringConstructor(ctx));

        Assert.True(IteratorsDiffer(ctx, beginAddress, endAddress));
        Assert.Equal(42L, ReadCurrentId(ctx, memory, beginAddress, idStringAddress));
        Assert.Equal("alpha", ReadCurrentName(ctx, beginAddress, nameStringAddress));

        ctx[CpuRegister.Rdi] = beginAddress;
        Assert.Equal(0, JsonExports.ArrayIteratorIncrement(ctx));
        Assert.True(IteratorsDiffer(ctx, beginAddress, endAddress));
        Assert.Equal(43L, ReadCurrentId(ctx, memory, beginAddress, idStringAddress));
        Assert.Equal("beta", ReadCurrentName(ctx, beginAddress, nameStringAddress));

        ctx[CpuRegister.Rdi] = beginAddress;
        Assert.Equal(0, JsonExports.ArrayIteratorIncrement(ctx));
        Assert.False(IteratorsDiffer(ctx, beginAddress, endAddress));

        ctx[CpuRegister.Rdi] = endAddress;
        Assert.Equal(0, JsonExports.ArrayIteratorDestructor(ctx));
        ctx[CpuRegister.Rdi] = beginAddress;
        Assert.Equal(0, JsonExports.ArrayIteratorDestructor(ctx));
        ctx[CpuRegister.Rdi] = arrayCopyAddress;
        Assert.Equal(0, JsonExports.ArrayDestructor(ctx));
    }

    private static bool IteratorsDiffer(CpuContext ctx, ulong left, ulong right)
    {
        ctx[CpuRegister.Rdi] = left;
        ctx[CpuRegister.Rsi] = right;
        Assert.Equal(0, JsonExports.ArrayIteratorNotEqual(ctx));
        return ctx[CpuRegister.Rax] != 0;
    }

    private static long ReadCurrentId(
        CpuContext ctx,
        AllocatingTestMemory memory,
        ulong iteratorAddress,
        ulong idStringAddress)
    {
        ctx[CpuRegister.Rdi] = iteratorAddress;
        Assert.Equal(0, JsonExports.ArrayIteratorDereference(ctx));
        var valueAddress = ctx[CpuRegister.Rax];
        Assert.NotEqual(0UL, valueAddress);

        ctx[CpuRegister.Rdi] = valueAddress;
        ctx[CpuRegister.Rsi] = idStringAddress;
        Assert.Equal(0, JsonExports.ValueGetStringKey(ctx));
        ctx[CpuRegister.Rdi] = ctx[CpuRegister.Rax];
        Assert.Equal(0, JsonExports.ValueGetInteger(ctx));

        Span<byte> integer = stackalloc byte[sizeof(long)];
        Assert.True(memory.TryRead(ctx[CpuRegister.Rax], integer));
        return BinaryPrimitives.ReadInt64LittleEndian(integer);
    }

    private static string ReadCurrentName(
        CpuContext ctx,
        ulong iteratorAddress,
        ulong nameStringAddress)
    {
        ctx[CpuRegister.Rdi] = iteratorAddress;
        Assert.Equal(0, JsonExports.ArrayIteratorDereference(ctx));
        ctx[CpuRegister.Rdi] = ctx[CpuRegister.Rax];
        ctx[CpuRegister.Rsi] = nameStringAddress;
        Assert.Equal(0, JsonExports.ValueGetStringKey(ctx));
        ctx[CpuRegister.Rdi] = ctx[CpuRegister.Rax];
        Assert.Equal(0, JsonExports.ValueGetString(ctx));
        ctx[CpuRegister.Rdi] = ctx[CpuRegister.Rax];
        Assert.Equal(0, JsonExports.StringCStr(ctx));
        Assert.True(ctx.TryReadNullTerminatedUtf8(ctx[CpuRegister.Rax], 256, out var text));
        return text;
    }

    private sealed class AllocatingTestMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _storage;
        private ulong _nextAllocation;

        public AllocatingTestMemory(ulong baseAddress, int size, ulong allocationOffset)
        {
            _baseAddress = baseAddress;
            _storage = new byte[size];
            _nextAllocation = baseAddress + allocationOffset;
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        public void WriteCString(ulong virtualAddress, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Assert.True(TryWrite(virtualAddress, bytes));
            Assert.True(TryWrite(virtualAddress + (ulong)bytes.Length, stackalloc byte[] { 0 }));
        }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = 0;
            if (size == 0 || alignment == 0 || (alignment & (alignment - 1)) != 0)
            {
                return false;
            }

            var aligned = (_nextAllocation + alignment - 1) & ~(alignment - 1);
            if (!TryResolve(aligned, checked((int)size), out _))
            {
                return false;
            }

            address = aligned;
            _nextAllocation = aligned + size;
            return true;
        }

        public bool TryFreeGuestMemory(ulong address) => true;

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < _baseAddress || length < 0)
            {
                return false;
            }

            var relative = virtualAddress - _baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
