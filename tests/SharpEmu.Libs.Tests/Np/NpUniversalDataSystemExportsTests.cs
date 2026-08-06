// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

[Collection("NpUniversalDataSystem")]
public sealed class NpUniversalDataSystemExportsTests
{
    private const ulong BaseAddress = 0x3_0000_0000;
    private const ulong ParametersAddress = BaseAddress + 0x100;
    private const ulong ArrayAddress = BaseAddress + 0x200;
    private const ulong StringAddress = BaseAddress + 0x400;
    private const ulong ArrayBackingAddress = BaseAddress + 0x800;
    private const ulong ArrayNodeAddress = BaseAddress + 0x900;
    private const ulong NestedBackingAddress = BaseAddress + 0xA00;
    private const ulong NestedNodeAddress = BaseAddress + 0xB00;
    private const ulong KeyStringBackingAddress = BaseAddress + 0xC00;
    private const ulong KeyStringAddress = BaseAddress + 0xD00;

    private readonly AllocatingCpuMemory _memory = new(
        BaseAddress,
        size: 0x4000,
        allocationStart: BaseAddress + 0x2000);
    private readonly CpuContext _ctx;

    public NpUniversalDataSystemExportsTests()
    {
        NpUniversalDataSystemExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void EventPropertyArraySetString_RegistersForGen5()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("4llLk7YJRTE", out var export));
        Assert.Equal("sceNpUniversalDataSystemEventPropertyArraySetString", export.Name);
        Assert.Equal("libSceNpUniversalDataSystem", export.LibraryName);
    }

    [Fact]
    public void Terminate_RegistersExactGen5DispatchIdentity()
    {
        var gen5Manager = new ModuleManager();
        gen5Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        var gen4Manager = new ModuleManager();
        gen4Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));

        Assert.True(gen5Manager.TryGetExport("47UAEuQl+iI", out var export));
        Assert.Equal("sceNpUniversalDataSystemTerminate", export.Name);
        Assert.Equal("libSceNpUniversalDataSystem", export.LibraryName);
        Assert.True(export.PreferLle);
        Assert.Equal(typeof(NpUniversalDataSystemExports), export.Function.Method.DeclaringType);
        Assert.False(gen4Manager.TryGetExport("47UAEuQl+iI", out _));
    }

    [Fact]
    public void Terminate_RequiresInitializationAndClearsRuntimeState()
    {
        const int notInitialized = unchecked((int)0x80553117);

        Assert.Equal(notInitialized, NpUniversalDataSystemExports.NpUniversalDataSystemTerminate(_ctx));
        Assert.Equal(unchecked((ulong)(long)notInitialized), _ctx[CpuRegister.Rax]);

        Initialize();
        WriteEmptyArray();
        _memory.WriteCString(StringAddress, "transient");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.True(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringsForTests(ArrayAddress, out _));

        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemTerminate(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.False(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringsForTests(ArrayAddress, out _));

        Assert.Equal(notInitialized, NpUniversalDataSystemExports.NpUniversalDataSystemTerminate(_ctx));
        Assert.Equal(unchecked((ulong)(long)notInitialized), _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void CreateEvent_WritesIdToLibcBackedPointer()
    {
        var eventIdAddress = AllocateTracked(_ctx, sizeof(int));
        try
        {
            Marshal.WriteInt32(unchecked((nint)eventIdAddress), 0);
            _ctx[CpuRegister.Rdi] = ParametersAddress;
            _ctx[CpuRegister.Rsi] = 0;
            _ctx[CpuRegister.Rdx] = eventIdAddress;
            _ctx[CpuRegister.Rcx] = 0;

            Assert.Equal(
                0,
                NpUniversalDataSystemExports.NpUniversalDataSystemCreateEvent(_ctx));
            var eventId = Marshal.ReadInt32(unchecked((nint)eventIdAddress));
            Assert.True(eventId > 0);

            _ctx[CpuRegister.Rdi] = unchecked((ulong)eventId);
            Assert.Equal(
                0,
                NpUniversalDataSystemExports.NpUniversalDataSystemDestroyEvent(_ctx));
        }
        finally
        {
            FreeTracked(_ctx, eventIdAddress);
        }
    }

    [Fact]
    public void EventPropertyArraySetString_ChecksInitializationBeforeArguments()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0;

        Assert.Equal(
            unchecked((int)0x80553117),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
    }

    [Fact]
    public void EventPropertyArraySetString_RejectsNullArrayAfterInitialization()
    {
        Initialize();
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x8055311A),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
    }

    [Fact]
    public void EventPropertyArraySetString_MaterializesAndRecursivelyRevalidatesAppendedNodes()
    {
        Initialize();
        WriteEmptyArray();
        _memory.WriteCString(StringAddress, "astro-🌟");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x20, out var firstNode));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x28, out var firstTail));
        Assert.Equal(firstNode, firstTail);
        Assert.NotEqual(0UL, firstNode);
        Assert.True(_ctx.TryReadUInt64(firstNode, out var firstReserved));
        Assert.True(_ctx.TryReadUInt64(firstNode + 0x08, out var firstNext));
        Assert.True(_ctx.TryReadUInt64(firstNode + 0x10, out var firstPrevious));
        Assert.Equal(0UL, firstReserved);
        Assert.Equal(0UL, firstNext);
        Assert.Equal(0UL, firstPrevious);
        Assert.Equal(0x2001, ReadPropertyType(firstNode + 0x18));
        Assert.True(_ctx.TryReadUInt64(firstNode + 0x20, out var firstStringBacking));
        Assert.True(_ctx.TryReadUInt64(firstStringBacking + 0x18, out var firstString));
        Assert.Equal(0x28UL, _memory.GetAllocationSize(firstNode));
        Assert.Equal(0x20UL, _memory.GetAllocationSize(firstStringBacking));
        Assert.Equal(
            (ulong)System.Text.Encoding.UTF8.GetByteCount("astro-🌟") + 1,
            _memory.GetAllocationSize(firstString));
        Assert.Equal("astro-🌟", ReadMaterializedString(firstNode + 0x18));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x30, out var firstCount));
        Assert.Equal(1UL, firstCount);

        _memory.WriteCString(StringAddress, "second");
        // This call first recursively validates the node materialized above.
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x20, out var head));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x28, out var secondNode));
        Assert.Equal(firstNode, head);
        Assert.NotEqual(firstNode, secondNode);
        Assert.True(_ctx.TryReadUInt64(firstNode + 0x08, out firstNext));
        Assert.True(_ctx.TryReadUInt64(secondNode, out var secondReserved));
        Assert.True(_ctx.TryReadUInt64(secondNode + 0x08, out var secondNext));
        Assert.True(_ctx.TryReadUInt64(secondNode + 0x10, out var secondPrevious));
        Assert.Equal(secondNode, firstNext);
        Assert.Equal(0UL, secondReserved);
        Assert.Equal(0UL, secondNext);
        Assert.Equal(firstNode, secondPrevious);
        Assert.Equal(0x2001, ReadPropertyType(secondNode + 0x18));
        Assert.True(_ctx.TryReadUInt64(secondNode + 0x20, out var secondStringBacking));
        Assert.True(_ctx.TryReadUInt64(secondStringBacking + 0x18, out var secondString));
        Assert.Equal(0x28UL, _memory.GetAllocationSize(secondNode));
        Assert.Equal(0x20UL, _memory.GetAllocationSize(secondStringBacking));
        Assert.Equal(7UL, _memory.GetAllocationSize(secondString));
        Assert.Equal("second", ReadMaterializedString(secondNode + 0x18));
        Assert.True(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringStateForTests(
            ArrayAddress,
            out var temporaryType,
            out var value));
        Assert.Equal(0x2001, temporaryType);
        Assert.Equal("second", value);
        Assert.True(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringsForTests(
            ArrayAddress,
            out var values));
        Assert.Equal(["astro-🌟", "second"], values);
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x30, out var count));
        Assert.Equal(2UL, count);
    }

    [Fact]
    public void EventPropertyArraySetString_InvalidUtf8DoesNotReplaceExistingState()
    {
        Initialize();
        WriteEmptyArray();
        _memory.WriteCString(StringAddress, "existing");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));

        Assert.True(_memory.TryWrite(StringAddress, new byte[] { 0xC0, 0x80, 0 }));
        Assert.Equal(
            unchecked((int)0x80553115),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.True(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out var value));
        Assert.Equal("existing", value);
    }

    [Fact]
    public void EventPropertyArraySetString_InvalidPropertyTypeDoesNotWriteState()
    {
        Initialize();
        WritePropertyType(0x7777);
        _memory.WriteCString(StringAddress, "ignored");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x80553115),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.False(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out _));
    }

    [Fact]
    public void EventPropertyArraySetString_TagValidPrimitiveReachesMissingBackingError()
    {
        Initialize();
        WritePropertyType(0x1001);
        _memory.WriteCString(StringAddress, "not-an-array");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x8055BB0C),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.False(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out _));
    }

    [Fact]
    public void EventPropertyArraySetString_NormalizesInternalSetterFailureWithoutMutation()
    {
        Initialize();
        WriteEmptyArray();
        _memory.WriteCString(StringAddress, "existing");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x20, out var originalHead));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x28, out var originalTail));
        var originalAllocationCount = _memory.AllocationCount;

        _memory.WriteCString(StringAddress, "replacement");
        NpUniversalDataSystemExports.SetEventPropertyArrayAllocationFailureForTests(true);
        Assert.Equal(
            unchecked((int)0x80553101),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.True(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out var value));
        Assert.Equal("existing", value);
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x30, out var count));
        Assert.Equal(1UL, count);
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x20, out var head));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x28, out var tail));
        Assert.Equal(originalHead, head);
        Assert.Equal(originalTail, tail);
        Assert.Equal(originalAllocationCount, _memory.AllocationCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EventPropertyArraySetString_AllocationFailureLeavesGuestAndShadowUnmodified(
        int successfulAllocationsBeforeFailure)
    {
        Initialize();
        WriteEmptyArray();
        _memory.WriteCString(StringAddress, "not-stored");
        _memory.SuccessfulAllocationsBeforeFailure = successfulAllocationsBeforeFailure;
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x80553101),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x20, out var head));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x28, out var tail));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x30, out var count));
        Assert.Equal(0UL, head);
        Assert.Equal(0UL, tail);
        Assert.Equal(0UL, count);
        Assert.Equal(0, _memory.AllocationCount);
        Assert.False(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out _));
    }

    [Fact]
    public void EventPropertyArraySetString_PayloadWriteFailureFreesAllUnpublishedAllocations()
    {
        Initialize();
        WriteEmptyArray();
        _memory.WriteCString(StringAddress, "not-stored");
        _memory.SuccessfulAllocationPayloadWritesBeforeFailure = 1;
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x80553101),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x20, out var head));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x28, out var tail));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x30, out var count));
        Assert.Equal(0UL, head);
        Assert.Equal(0UL, tail);
        Assert.Equal(0UL, count);
        Assert.Equal(0, _memory.AllocationCount);
        Assert.False(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out _));
    }

    [Fact]
    public void EventPropertyArraySetString_FinalLinkFailureRestoresTailCountAndShadow()
    {
        Initialize();
        WriteEmptyArray();
        _memory.WriteCString(StringAddress, "existing");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x20, out var originalHead));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x28, out var originalTail));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x30, out var originalCount));
        var originalAllocationCount = _memory.AllocationCount;

        _memory.WriteCString(StringAddress, "not-linked");
        _memory.FailNextWriteAddress = originalTail + 0x08;
        Assert.Equal(
            unchecked((int)0x80553101),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));

        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x20, out var head));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x28, out var tail));
        Assert.True(_ctx.TryReadUInt64(ArrayBackingAddress + 0x30, out var count));
        Assert.True(_ctx.TryReadUInt64(originalTail + 0x08, out var next));
        Assert.Equal(originalHead, head);
        Assert.Equal(originalTail, tail);
        Assert.Equal(originalCount, count);
        Assert.Equal(0UL, next);
        Assert.Equal(originalAllocationCount, _memory.AllocationCount);
        Assert.True(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringsForTests(
            ArrayAddress,
            out var values));
        Assert.Equal(["existing"], values);
    }

    [Fact]
    public void EventPropertyArraySetString_CountMinusOneReturnsArrayFullWithoutMutation()
    {
        Initialize();
        WriteEmptyArray();
        Assert.True(_ctx.TryWriteUInt64(ArrayBackingAddress + 0x30, ulong.MaxValue));
        _memory.WriteCString(StringAddress, "not-stored");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x8055BB09),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.False(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out _));
    }

    [Fact]
    public void EventPropertyArraySetString_ArrayWithoutBackingReturnsDirectSetterError()
    {
        Initialize();
        WritePropertyType(0x2002);
        _memory.WriteCString(StringAddress, "not-stored");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x8055BB0C),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.False(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out _));
    }

    [Fact]
    public void EventPropertyArraySetString_RejectsInvalidNestedArrayValue()
    {
        Initialize();
        WriteEmptyArray();
        Assert.True(_ctx.TryWriteUInt64(ArrayBackingAddress + 0x20, ArrayNodeAddress));
        Assert.True(_ctx.TryWriteUInt64(ArrayBackingAddress + 0x30, 1));
        Assert.True(_ctx.TryWriteUInt64(ArrayNodeAddress + 0x08, 0));
        WritePropertyType(ArrayNodeAddress + 0x18, 0x7777);
        _memory.WriteCString(StringAddress, "not-stored");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x80553115),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.False(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out _));
    }

    [Fact]
    public void EventPropertyArraySetString_RejectsNestedObjectWithEmptyStringKey()
    {
        Initialize();
        WriteEmptyArray();
        Assert.True(_ctx.TryWriteUInt64(ArrayBackingAddress + 0x20, ArrayNodeAddress));
        Assert.True(_ctx.TryWriteUInt64(ArrayNodeAddress + 0x08, 0));
        WritePropertyType(ArrayNodeAddress + 0x18, 0x2003);
        Assert.True(_ctx.TryWriteUInt64(ArrayNodeAddress + 0x20, NestedBackingAddress));
        Assert.True(_ctx.TryWriteUInt64(NestedBackingAddress + 0x20, NestedNodeAddress));
        Assert.True(_ctx.TryWriteUInt64(NestedNodeAddress + 0x08, 0));
        WritePropertyType(NestedNodeAddress + 0x18, 0x2001);
        Assert.True(_ctx.TryWriteUInt64(NestedNodeAddress + 0x20, KeyStringBackingAddress));
        Assert.True(_ctx.TryWriteUInt64(KeyStringBackingAddress + 0x18, KeyStringAddress));
        _memory.WriteCString(KeyStringAddress, string.Empty);
        WritePropertyType(NestedNodeAddress + 0x28, 0x1001);
        _memory.WriteCString(StringAddress, "not-stored");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x80553115),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.False(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out _));
    }

    [Fact]
    public void EventPropertyArraySetString_RejectsCyclicNestedArrayBacking()
    {
        Initialize();
        WriteEmptyArray();
        Assert.True(_ctx.TryWriteUInt64(ArrayBackingAddress + 0x20, ArrayNodeAddress));
        Assert.True(_ctx.TryWriteUInt64(ArrayNodeAddress + 0x08, 0));
        WritePropertyType(ArrayNodeAddress + 0x18, 0x2002);
        Assert.True(_ctx.TryWriteUInt64(ArrayNodeAddress + 0x20, ArrayBackingAddress));
        _memory.WriteCString(StringAddress, "not-stored");
        _ctx[CpuRegister.Rdi] = ArrayAddress;
        _ctx[CpuRegister.Rsi] = StringAddress;

        Assert.Equal(
            unchecked((int)0x80553115),
            NpUniversalDataSystemExports.NpUniversalDataSystemEventPropertyArraySetString(_ctx));
        Assert.False(NpUniversalDataSystemExports.TryGetEventPropertyArrayStringForTests(ArrayAddress, out _));
    }

    private void Initialize()
    {
        Assert.True(_memory.TryWrite(ParametersAddress, new byte[16]));
        _ctx[CpuRegister.Rdi] = ParametersAddress;
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemInitialize(_ctx));
    }

    private void WritePropertyType(ushort type)
    {
        WritePropertyType(ArrayAddress, type);
    }

    private void WriteEmptyArray()
    {
        WritePropertyType(0x2002);
        Assert.True(_ctx.TryWriteUInt64(ArrayAddress + 0x08, ArrayBackingAddress));
        Assert.True(_memory.TryWrite(ArrayBackingAddress, new byte[0x38]));
    }

    private void WritePropertyType(ulong address, ushort type)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, type);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private ushort ReadPropertyType(ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        Assert.True(_memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    }

    private string ReadMaterializedString(ulong variantAddress)
    {
        Assert.True(_ctx.TryReadUInt64(variantAddress + 0x08, out var backingAddress));
        Assert.True(_ctx.TryReadUInt64(backingAddress + 0x18, out var stringAddress));
        return _memory.ReadCString(stringAddress);
    }

    private static ulong AllocateTracked(CpuContext context, int length)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)length);
        Assert.Equal(0, KernelMemoryCompatExports.Malloc(context));
        Assert.NotEqual(0UL, context[CpuRegister.Rax]);
        return context[CpuRegister.Rax];
    }

    private static void FreeTracked(CpuContext context, ulong address)
    {
        context[CpuRegister.Rdi] = address;
        Assert.Equal(0, KernelMemoryCompatExports.Free(context));
    }

    private sealed class AllocatingCpuMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly FakeCpuMemory _memory;
        private readonly HashSet<ulong> _allocations = [];
        private readonly Dictionary<ulong, ulong> _allocationSizes = [];
        private readonly ulong _endAddress;
        private ulong _nextAddress;

        public AllocatingCpuMemory(ulong baseAddress, int size, ulong allocationStart)
        {
            _memory = new FakeCpuMemory(baseAddress, size);
            _nextAddress = allocationStart;
            _endAddress = baseAddress + (ulong)size;
        }

        public int SuccessfulAllocationsBeforeFailure { get; set; } = -1;

        public int SuccessfulAllocationPayloadWritesBeforeFailure { get; set; } = -1;

        public ulong? FailNextWriteAddress { get; set; }

        public int AllocationCount => _allocations.Count;

        public bool TryRead(ulong virtualAddress, Span<byte> destination) =>
            _memory.TryRead(virtualAddress, destination);

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (_allocations.Contains(virtualAddress) &&
                SuccessfulAllocationPayloadWritesBeforeFailure == 0)
            {
                SuccessfulAllocationPayloadWritesBeforeFailure = -1;
                return false;
            }

            if (_allocations.Contains(virtualAddress) &&
                SuccessfulAllocationPayloadWritesBeforeFailure > 0)
            {
                SuccessfulAllocationPayloadWritesBeforeFailure--;
            }

            if (FailNextWriteAddress == virtualAddress)
            {
                FailNextWriteAddress = null;
                return false;
            }

            return _memory.TryWrite(virtualAddress, source);
        }

        public ulong WriteCString(ulong virtualAddress, string text) =>
            _memory.WriteCString(virtualAddress, text);

        public string ReadCString(ulong virtualAddress)
        {
            var bytes = new byte[16 * 1024];
            Span<byte> current = stackalloc byte[1];
            for (var index = 0; index < bytes.Length; index++)
            {
                Assert.True(TryRead(virtualAddress + (ulong)index, current));
                if (current[0] == 0)
                {
                    return System.Text.Encoding.UTF8.GetString(bytes, 0, index);
                }

                bytes[index] = current[0];
            }

            throw new Xunit.Sdk.XunitException("Guest string was not null terminated.");
        }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = 0;
            if (SuccessfulAllocationsBeforeFailure == 0)
            {
                SuccessfulAllocationsBeforeFailure = -1;
                return false;
            }

            if (SuccessfulAllocationsBeforeFailure > 0)
            {
                SuccessfulAllocationsBeforeFailure--;
            }

            if (size == 0 || alignment == 0 || (alignment & (alignment - 1)) != 0)
            {
                return false;
            }

            var aligned = (_nextAddress + alignment - 1) & ~(alignment - 1);
            if (aligned > _endAddress || size > _endAddress - aligned)
            {
                return false;
            }

            address = aligned;
            _nextAddress = aligned + size;
            _allocations.Add(address);
            _allocationSizes[address] = size;
            return true;
        }

        public bool TryFreeGuestMemory(ulong address)
        {
            _allocationSizes.Remove(address);
            return _allocations.Remove(address);
        }

        public ulong GetAllocationSize(ulong address) => _allocationSizes[address];
    }
}
