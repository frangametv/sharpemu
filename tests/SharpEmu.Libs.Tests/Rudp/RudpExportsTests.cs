// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Rudp;
using Xunit;

namespace SharpEmu.Libs.Tests.Rudp;

public sealed class RudpExportsTests : IDisposable
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int NotInitialized = unchecked((int)0x80770001);
    private const int AlreadyInitialized = unchecked((int)0x80770002);
    private const int InvalidArgument = unchecked((int)0x80770004);
    private const int OutOfMemory = unchecked((int)0x80770007);
    private const int InternalIoThreadAlreadyEnabled = unchecked((int)0x80770010);
    private const int InvalidEventHandler = unchecked((int)0x80770022);

    public RudpExportsTests() => RudpExports.ResetForTests();

    public void Dispose() => RudpExports.ResetForTests();

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 0x1000)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void Init_RejectsNullOrNonPositiveBuffer(long addressSelector, int size)
    {
        var ctx = CreateContext();
        ctx[CpuRegister.Rdi] = addressSelector == 0 ? 0 : BaseAddress;
        ctx[CpuRegister.Rsi] = unchecked((ulong)size);

        Assert.Equal(InvalidArgument, RudpExports.Init(ctx));
        Assert.False(RudpExports.GetStateForTests().Initialized);
    }

    [Fact]
    public void Init_ReportsAllocatorFailureForPositiveUndersizedBuffer()
    {
        var ctx = CreateContext();
        ctx[CpuRegister.Rdi] = BaseAddress;
        ctx[CpuRegister.Rsi] = 1;

        Assert.Equal(OutOfMemory, RudpExports.Init(ctx));
        Assert.False(RudpExports.GetStateForTests().Initialized);
    }

    [Fact]
    public void Init_RetainsCallerBufferAndChecksAlreadyInitializedFirst()
    {
        var ctx = CreateContext();
        ctx[CpuRegister.Rdi] = BaseAddress + 0x100;
        ctx[CpuRegister.Rsi] = 0x1000;

        Assert.Equal(0, RudpExports.Init(ctx));
        Assert.Equal(
            (true, BaseAddress + 0x100, 0x1000),
            RudpExports.GetStateForTests());

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(AlreadyInitialized, RudpExports.Init(ctx));
        Assert.Equal(
            (true, BaseAddress + 0x100, 0x1000),
            RudpExports.GetStateForTests());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Init_MiddleGapOrWriteFailureLeavesStateUnpublished(
        bool failWrite)
    {
        var faultAddress = BaseAddress + 0x1000;
        var memory = new FaultingMemory(
            BaseAddress,
            0x3000,
            failWrite ? null : faultAddress,
            failWrite ? faultAddress : null);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = BaseAddress;
        ctx[CpuRegister.Rsi] = 0x2000;

        Assert.Equal(OutOfMemory, RudpExports.Init(ctx));
        Assert.Equal((false, 0UL, 0), RudpExports.GetStateForTests());
    }

    [Fact]
    public void InitNid_RegistersWithRudpIdentity()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("amuBfI-AQc4", out var export));
        Assert.Equal("sceRudpInit", export.Name);
        Assert.Equal("libSceRudp", export.LibraryName);
    }

    [Fact]
    public void SetEventHandlerNid_RegistersForGen5WithRudpIdentity()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("SUEVes8gvmw", out var export));
        Assert.Equal("sceRudpSetEventHandler", export.Name);
        Assert.Equal("libSceRudp", export.LibraryName);
    }

    [Fact]
    public void SetEventHandler_ChecksInitializationBeforeNullHandler()
    {
        var ctx = CreateContext();
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0x1234;

        Assert.Equal(NotInitialized, RudpExports.SetEventHandler(ctx));
        Assert.Equal(0x0000_0000_8077_0001UL, ctx[CpuRegister.Rax]);
        Assert.Equal((0UL, 0UL), RudpExports.GetEventHandlerStateForTests());
    }

    [Fact]
    public void SetEventHandler_RejectsNullHandlerWithoutReplacingState()
    {
        var ctx = CreateInitializedContext();
        ctx[CpuRegister.Rdi] = 0x1234_5678;
        ctx[CpuRegister.Rsi] = 0x8765_4321;
        Assert.Equal(0, RudpExports.SetEventHandler(ctx));

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0xDEAD_BEEF;

        Assert.Equal(InvalidEventHandler, RudpExports.SetEventHandler(ctx));
        Assert.Equal(0x0000_0000_8077_0022UL, ctx[CpuRegister.Rax]);
        Assert.Equal(
            (0x1234_5678UL, 0x8765_4321UL),
            RudpExports.GetEventHandlerStateForTests());
    }

    [Fact]
    public void SetEventHandler_RetainsAndReplacesCallbackPairWithoutProbingGuestMemory()
    {
        var ctx = CreateInitializedContext();

        ctx[CpuRegister.Rdi] = 0xFFFF_0000_1234_5678;
        ctx[CpuRegister.Rsi] = 0x8765_4321;
        Assert.Equal(0, RudpExports.SetEventHandler(ctx));
        Assert.Equal(
            (0xFFFF_0000_1234_5678UL, 0x8765_4321UL),
            RudpExports.GetEventHandlerStateForTests());

        ctx[CpuRegister.Rdi] = 0xFFFF_0000_8765_4321;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, RudpExports.SetEventHandler(ctx));
        Assert.Equal(
            (0xFFFF_0000_8765_4321UL, 0UL),
            RudpExports.GetEventHandlerStateForTests());
    }

    [Fact]
    public void ResetForTests_ClearsRetainedEventHandlerPair()
    {
        var ctx = CreateInitializedContext();
        ctx[CpuRegister.Rdi] = 0x1234_5678;
        ctx[CpuRegister.Rsi] = 0x8765_4321;
        Assert.Equal(0, RudpExports.SetEventHandler(ctx));

        RudpExports.ResetForTests();

        Assert.Equal((0UL, 0UL), RudpExports.GetEventHandlerStateForTests());
        Assert.False(RudpExports.GetStateForTests().Initialized);
    }

    [Fact]
    public void EnableInternalIoThreadNid_RegistersForGen5WithRudpIdentity()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("6PBNpsgyaxw", out var export));
        Assert.Equal("sceRudpEnableInternalIOThread", export.Name);
        Assert.Equal("libSceRudp", export.LibraryName);
    }

    [Fact]
    public void EnableInternalIoThread_RequiresInitializationBeforeArguments()
    {
        var ctx = CreateContext();
        ctx[CpuRegister.Rdi] = uint.MaxValue;
        ctx[CpuRegister.Rsi] = uint.MaxValue;

        Assert.Equal(NotInitialized, RudpExports.EnableInternalIoThread(ctx));
        Assert.Equal(0x0000_0000_8077_0001UL, ctx[CpuRegister.Rax]);
        Assert.Equal(
            (false, 0U, 0),
            RudpExports.GetInternalIoThreadStateForTests());
    }

    [Theory]
    [InlineData(0U, 0x4000U)]
    [InlineData(1U, 0x4000U)]
    [InlineData(0x4000U, 0x4000U)]
    [InlineData(0x8000U, 0x8000U)]
    public void EnableInternalIoThread_NormalizesStackAndRetainsRawPriority(
        uint requestedStackSize,
        uint expectedStackSize)
    {
        var ctx = CreateInitializedContext();
        ctx[CpuRegister.Rdi] = requestedStackSize;
        ctx[CpuRegister.Rsi] = unchecked((ulong)-7L);

        Assert.Equal(0, RudpExports.EnableInternalIoThread(ctx));
        Assert.Equal(
            (true, expectedStackSize, -7),
            RudpExports.GetInternalIoThreadStateForTests());
    }

    [Fact]
    public void EnableInternalIoThread_DuplicateReturnsErrorWithoutReplacingConfiguration()
    {
        var ctx = CreateInitializedContext();
        ctx[CpuRegister.Rdi] = 0x5000;
        ctx[CpuRegister.Rsi] = 2;
        Assert.Equal(0, RudpExports.EnableInternalIoThread(ctx));

        ctx[CpuRegister.Rdi] = 0x9000;
        ctx[CpuRegister.Rsi] = 3;

        Assert.Equal(
            InternalIoThreadAlreadyEnabled,
            RudpExports.EnableInternalIoThread(ctx));
        Assert.Equal(0x0000_0000_8077_0010UL, ctx[CpuRegister.Rax]);
        Assert.Equal(
            (true, 0x5000U, 2),
            RudpExports.GetInternalIoThreadStateForTests());
    }

    [Fact]
    public void EnableInternalIoThread_RemainsIndependentFromEventHandlerReplacement()
    {
        var ctx = CreateInitializedContext();
        ctx[CpuRegister.Rdi] = 0x1234_5678;
        ctx[CpuRegister.Rsi] = 0x8765_4321;
        Assert.Equal(0, RudpExports.SetEventHandler(ctx));

        ctx[CpuRegister.Rdi] = 0x8000;
        ctx[CpuRegister.Rsi] = 4;
        Assert.Equal(0, RudpExports.EnableInternalIoThread(ctx));

        ctx[CpuRegister.Rdi] = 0xAAAA_BBBB;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, RudpExports.SetEventHandler(ctx));
        Assert.Equal(
            (0xAAAA_BBBBUL, 0UL),
            RudpExports.GetEventHandlerStateForTests());
        Assert.Equal(
            (true, 0x8000U, 4),
            RudpExports.GetInternalIoThreadStateForTests());
    }

    [Fact]
    public void ResetForTests_ClearsInternalIoThreadLifecycleState()
    {
        var ctx = CreateInitializedContext();
        ctx[CpuRegister.Rdi] = 0x8000;
        ctx[CpuRegister.Rsi] = 4;
        Assert.Equal(0, RudpExports.EnableInternalIoThread(ctx));

        RudpExports.ResetForTests();

        Assert.Equal(
            (false, 0U, 0),
            RudpExports.GetInternalIoThreadStateForTests());
        Assert.False(RudpExports.GetStateForTests().Initialized);
    }

    [Fact]
    public void EndNid_RegistersOnlyForGen5WithRudpIdentity()
    {
        var gen5Manager = new ModuleManager();
        gen5Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(gen5Manager.TryGetExport("3hBvwqEwqj8", out var export));
        Assert.Equal("sceRudpEnd", export.Name);
        Assert.Equal("libSceRudp", export.LibraryName);

        var gen4Manager = new ModuleManager();
        gen4Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));
        Assert.False(gen4Manager.TryGetExport("3hBvwqEwqj8", out _));
    }

    [Fact]
    public void End_UninitializedReturnsErrorWithoutMutation()
    {
        var ctx = CreateContext();
        ctx[CpuRegister.Rdi] = ulong.MaxValue;
        ctx[CpuRegister.Rsi] = ulong.MaxValue;

        Assert.Equal(NotInitialized, RudpExports.End(ctx));
        Assert.Equal(0x0000_0000_8077_0001UL, ctx[CpuRegister.Rax]);
        Assert.Equal((false, 0UL, 0), RudpExports.GetStateForTests());
        Assert.Equal((0UL, 0UL), RudpExports.GetEventHandlerStateForTests());
        Assert.Equal(
            (false, 0U, 0),
            RudpExports.GetInternalIoThreadStateForTests());
    }

    [Fact]
    public void End_TearsDownHandlerAndWorkerAndAllowsFreshReinitialization()
    {
        var ctx = CreateInitializedContext();
        ctx[CpuRegister.Rdi] = 0x1234_5678;
        ctx[CpuRegister.Rsi] = 0x8765_4321;
        Assert.Equal(0, RudpExports.SetEventHandler(ctx));
        ctx[CpuRegister.Rdi] = 0x8000;
        ctx[CpuRegister.Rsi] = 4;
        Assert.Equal(0, RudpExports.EnableInternalIoThread(ctx));

        Assert.Equal(0, RudpExports.End(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal((false, 0UL, 0), RudpExports.GetStateForTests());
        Assert.Equal((0UL, 0UL), RudpExports.GetEventHandlerStateForTests());
        Assert.Equal(
            (false, 0U, 0),
            RudpExports.GetInternalIoThreadStateForTests());

        Assert.Equal(NotInitialized, RudpExports.End(ctx));
        Assert.Equal((false, 0UL, 0), RudpExports.GetStateForTests());

        ctx[CpuRegister.Rdi] = BaseAddress + 0x100;
        ctx[CpuRegister.Rsi] = 0x1000;
        Assert.Equal(0, RudpExports.Init(ctx));
        Assert.Equal(
            (true, BaseAddress + 0x100, 0x1000),
            RudpExports.GetStateForTests());
        Assert.Equal((0UL, 0UL), RudpExports.GetEventHandlerStateForTests());
        Assert.Equal(
            (false, 0U, 0),
            RudpExports.GetInternalIoThreadStateForTests());

        ctx[CpuRegister.Rdi] = 0xAAAA_BBBB;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, RudpExports.SetEventHandler(ctx));
        ctx[CpuRegister.Rdi] = 0x4000;
        ctx[CpuRegister.Rsi] = 5;
        Assert.Equal(0, RudpExports.EnableInternalIoThread(ctx));
    }

    [Fact]
    public void RudpDispatch_ZeroExtendsFirmwareIntResultsIntoRax()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        var ctx = CreateContext();

        Assert.True(manager.TryDispatch("6PBNpsgyaxw", ctx, out _));
        Assert.Equal(0x0000_0000_8077_0001UL, ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rdi] = BaseAddress;
        ctx[CpuRegister.Rsi] = 0x1000;
        Assert.True(manager.TryDispatch("amuBfI-AQc4", ctx, out _));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0x1234;
        Assert.True(manager.TryDispatch("SUEVes8gvmw", ctx, out _));
        Assert.Equal(0x0000_0000_8077_0022UL, ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rdi] = 0x4000;
        ctx[CpuRegister.Rsi] = 4;
        Assert.True(manager.TryDispatch("6PBNpsgyaxw", ctx, out _));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

        Assert.True(manager.TryDispatch("6PBNpsgyaxw", ctx, out _));
        Assert.Equal(0x0000_0000_8077_0010UL, ctx[CpuRegister.Rax]);

        Assert.True(manager.TryDispatch("3hBvwqEwqj8", ctx, out _));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.True(manager.TryDispatch("3hBvwqEwqj8", ctx, out _));
        Assert.Equal(0x0000_0000_8077_0001UL, ctx[CpuRegister.Rax]);
    }

    private static CpuContext CreateContext() =>
        new(new FakeCpuMemory(BaseAddress, 0x4000), Generation.Gen5);

    private static CpuContext CreateInitializedContext()
    {
        var ctx = CreateContext();
        ctx[CpuRegister.Rdi] = BaseAddress;
        ctx[CpuRegister.Rsi] = 0x1000;
        Assert.Equal(0, RudpExports.Init(ctx));
        return ctx;
    }

    private sealed class FaultingMemory : ICpuMemory
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _storage;
        private readonly ulong? _readFaultAddress;
        private readonly ulong? _writeFaultAddress;

        public FaultingMemory(
            ulong baseAddress,
            int size,
            ulong? readFaultAddress,
            ulong? writeFaultAddress)
        {
            _baseAddress = baseAddress;
            _storage = new byte[size];
            _readFaultAddress = readFaultAddress;
            _writeFaultAddress = writeFaultAddress;
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset) ||
                IntersectsFault(
                    virtualAddress,
                    destination.Length,
                    _readFaultAddress))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(
            ulong virtualAddress,
            ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset) ||
                IntersectsFault(
                    virtualAddress,
                    source.Length,
                    _writeFaultAddress))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private static bool IntersectsFault(
            ulong address,
            int length,
            ulong? faultAddress) =>
            faultAddress is { } fault &&
            address <= fault &&
            fault - address < (ulong)length;

        private bool TryResolve(ulong address, int length, out int offset)
        {
            offset = 0;
            if (address < _baseAddress)
            {
                return false;
            }

            var relative = address - _baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
