// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NpManagerStateCollection
{
    public const string Name = "NpManagerState";
}

[Collection(NpManagerStateCollection.Name)]
public sealed class NpManagerExportsTests : IDisposable
{
    private const int NpErrorNotInitialized = unchecked((int)0x80550002);
    private const int NpErrorInvalidArgument = unchecked((int)0x80550003);
    private const int NpErrorCallbackAlreadyRegistered = unchecked((int)0x80550008);
    private const int NpErrorCallbackNotRegistered = unchecked((int)0x80550009);

    private const ulong BaseAddress = 0x5_1000_0000;
    private const ulong NameAddress = BaseAddress + 0x100;
    private const ulong StateAddress = BaseAddress + 0x200;
    private const ulong Callback = 0x8_0012_3456;
    private const ulong OtherCallback = 0x8_0065_4321;
    private const ulong UserData = 0x6_0000_0800;

    private readonly SparseGuestAddressSpace _memory = new(BaseAddress, 0x4000);
    private readonly IGuestThreadScheduler? _previousScheduler;
    private readonly CpuContext _ctx;

    public NpManagerExportsTests()
    {
        _previousScheduler = GuestThreadExecution.Scheduler;
        NpManagerExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
        _memory.WriteCString(NameAddress, "gta-v-np-manager-tests");
    }

    public void Dispose()
    {
        GuestThreadExecution.Scheduler = _previousScheduler;
        NpManagerExports.ResetForTests();
    }

    [Fact]
    public void PremiumEventCallbacks_RegisterExactGen5DispatchIdentities()
    {
        var gen5Manager = new ModuleManager();
        gen5Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        var gen4Manager = new ModuleManager();
        gen4Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));

        Assert.True(gen5Manager.TryGetExport("+yqjab2fUJA", out var register));
        Assert.Equal("sceNpRegisterPremiumEventCallback", register.Name);
        Assert.Equal("libSceNpManager", register.LibraryName);
        Assert.False(gen4Manager.TryGetExport("+yqjab2fUJA", out _));

        Assert.True(gen5Manager.TryGetExport("-Rjp3-YViXc", out var unregister));
        Assert.Equal("sceNpUnregisterPremiumEventCallback", unregister.Name);
        Assert.Equal("libSceNpManager", unregister.LibraryName);
        Assert.False(gen4Manager.TryGetExport("-Rjp3-YViXc", out _));

        Initialize();
        _ctx[CpuRegister.Rdi] = Callback;
        _ctx[CpuRegister.Rsi] = UserData;
        Assert.True(gen5Manager.TryDispatch("+yqjab2fUJA", _ctx, out var registerResult));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, registerResult);
        Assert.True(NpManagerExports.TryGetPremiumEventCallbackForTests(out var callback, out var userData));
        Assert.Equal(Callback, callback);
        Assert.Equal(UserData, userData);

        Assert.True(gen5Manager.TryDispatch("-Rjp3-YViXc", _ctx, out var unregisterResult));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, unregisterResult);
        Assert.False(NpManagerExports.TryGetPremiumEventCallbackForTests(out _, out _));
    }

    [Fact]
    public void ReachabilityStateCallback_RegistersExactGen5DispatchIdentity()
    {
        var gen5Manager = new ModuleManager();
        gen5Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        var gen4Manager = new ModuleManager();
        gen4Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));

        Assert.True(gen5Manager.TryGetExport("hw5KNqAAels", out var export));
        Assert.Equal("sceNpRegisterNpReachabilityStateCallback", export.Name);
        Assert.Equal("libSceNpManager", export.LibraryName);
        Assert.True(export.PreferLle);
        Assert.Equal(typeof(NpManagerExports), export.Function.Method.DeclaringType);
        Assert.False(gen4Manager.TryGetExport("hw5KNqAAels", out _));
    }

    [Fact]
    public void GetState_WritesFirmwareSignedInValue()
    {
        _ctx[CpuRegister.Rdi] = 0x1000_0000;
        _ctx[CpuRegister.Rsi] = StateAddress;

        AssertResult(0, NpManagerExports.NpGetState);

        Span<byte> state = stackalloc byte[sizeof(uint)];
        Assert.True(_memory.TryRead(StateAddress, state));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(state));
    }

    [Fact]
    public void RegisterReachabilityStateCallback_PreservesValidationOrderAndOriginalPair()
    {
        NpManagerAsyncRequests.ShutdownForTests();
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = UserData;
        AssertResult(NpErrorNotInitialized, NpManagerExports.NpRegisterNpReachabilityStateCallback);

        NpManagerAsyncRequests.ResetForTests();
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = UserData;
        AssertResult(NpErrorInvalidArgument, NpManagerExports.NpRegisterNpReachabilityStateCallback);

        RegisterReachability(Callback, UserData);
        Assert.True(NpManagerExports.TryGetReachabilityStateCallbackForTests(out var callback, out var userData));
        Assert.Equal(Callback, callback);
        Assert.Equal(UserData, userData);

        _ctx[CpuRegister.Rdi] = OtherCallback;
        _ctx[CpuRegister.Rsi] = UserData + 0x100;
        AssertResult(
            NpErrorCallbackAlreadyRegistered,
            NpManagerExports.NpRegisterNpReachabilityStateCallback);
        Assert.True(NpManagerExports.TryGetReachabilityStateCallbackForTests(out callback, out userData));
        Assert.Equal(Callback, callback);
        Assert.Equal(UserData, userData);
    }

    [Fact]
    public void ManagerTerminate_DoesNotClearPrxReachabilityStateCallback()
    {
        RegisterReachability(Callback, UserData);

        AssertResult(0, NpManagerExports.NpManagerGlobalTerminateCompat1270);
        Assert.True(NpManagerExports.TryGetReachabilityStateCallbackForTests(out var callback, out var userData));
        Assert.Equal(Callback, callback);
        Assert.Equal(UserData, userData);

        _ctx[CpuRegister.Rdi] = OtherCallback;
        _ctx[CpuRegister.Rsi] = UserData + 0x100;
        AssertResult(
            NpErrorCallbackAlreadyRegistered,
            NpManagerExports.NpRegisterNpReachabilityStateCallback);
    }

    [Fact]
    public void DispatchReachabilityState_CopiesPairUnderLockAndInvokesGuestAfterUnlock()
    {
        RegisterReachability(Callback, UserData);
        var scheduler = new RecordingScheduler();
        GuestThreadExecution.Scheduler = scheduler;

        Assert.True(NpManagerExports.TryDispatchNpReachabilityState(_ctx, 11, 0, out var error));
        Assert.Null(error);
        Assert.Equal(1, scheduler.CallCount);
        Assert.Equal(Callback, scheduler.EntryPoint);
        Assert.Equal(11UL, scheduler.EventType);
        Assert.Equal(0UL, scheduler.EventValue);
        Assert.Equal(UserData, scheduler.UserData);
        Assert.Equal("np_reachability_state_11_0", scheduler.Reason);
        Assert.False(scheduler.ManagerGateHeldDuringCall);
    }

    [Fact]
    public void DispatchReachabilityState_UsesPrxSdkLifecycle()
    {
        RegisterReachability(Callback, UserData);
        NpManagerAsyncRequests.ShutdownForTests();

        Assert.False(NpManagerExports.TryDispatchNpReachabilityState(_ctx, 11, 0, out var error));
        Assert.Equal("NP SDK request subsystem is not initialized", error);
    }

    [Fact]
    public void RegisterPremiumEventCallback_ChecksInitializationBeforeNullCallback()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = UserData;

        AssertResult(NpErrorNotInitialized, NpManagerExports.NpRegisterPremiumEventCallback);
        Assert.False(NpManagerExports.TryGetPremiumEventCallbackForTests(out _, out _));

        Initialize();
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = UserData;
        AssertResult(NpErrorInvalidArgument, NpManagerExports.NpRegisterPremiumEventCallback);
        Assert.False(NpManagerExports.TryGetPremiumEventCallbackForTests(out _, out _));
    }

    [Fact]
    public void UnregisterPremiumEventCallback_RequiresInitialization()
    {
        AssertResult(NpErrorNotInitialized, NpManagerExports.NpUnregisterPremiumEventCallback);
    }

    [Fact]
    public void RegisterPremiumEventCallback_RejectsDuplicateAndRetainsOriginalPair()
    {
        Initialize();
        Register(Callback, UserData);

        _ctx[CpuRegister.Rdi] = OtherCallback;
        _ctx[CpuRegister.Rsi] = UserData + 0x100;
        AssertResult(
            NpErrorCallbackAlreadyRegistered,
            NpManagerExports.NpRegisterPremiumEventCallback);

        Assert.True(NpManagerExports.TryGetPremiumEventCallbackForTests(out var callback, out var userData));
        Assert.Equal(Callback, callback);
        Assert.Equal(UserData, userData);
    }

    [Fact]
    public void UnregisterPremiumEventCallback_ReportsMissingSlotAndClearsRegistration()
    {
        Initialize();
        AssertResult(
            NpErrorCallbackNotRegistered,
            NpManagerExports.NpUnregisterPremiumEventCallback);

        Register(Callback, UserData);
        AssertResult(0, NpManagerExports.NpUnregisterPremiumEventCallback);
        Assert.False(NpManagerExports.TryGetPremiumEventCallbackForTests(out _, out _));
        AssertResult(
            NpErrorCallbackNotRegistered,
            NpManagerExports.NpUnregisterPremiumEventCallback);
    }

    [Fact]
    public void ManagerTerminate_ClearsPremiumCallbackBeforeReinitialize()
    {
        Initialize();
        Register(Callback, UserData);

        AssertResult(0, NpManagerExports.NpManagerGlobalTerminateCompat1270);
        Assert.False(NpManagerExports.TryGetPremiumEventCallbackForTests(out _, out _));

        _ctx[CpuRegister.Rdi] = OtherCallback;
        _ctx[CpuRegister.Rsi] = UserData + 0x100;
        AssertResult(NpErrorNotInitialized, NpManagerExports.NpRegisterPremiumEventCallback);

        Initialize();
        AssertResult(
            NpErrorCallbackNotRegistered,
            NpManagerExports.NpUnregisterPremiumEventCallback);
        Register(OtherCallback, UserData + 0x100);
    }

    [Fact]
    public void DispatchPremiumEvent_CopiesPairUnderLockAndInvokesGuestAfterUnlock()
    {
        Initialize();
        Register(Callback, UserData);
        var scheduler = new RecordingScheduler();
        GuestThreadExecution.Scheduler = scheduler;

        Assert.True(NpManagerExports.TryDispatchPremiumEvent(_ctx, 7, 2, out var error));
        Assert.Null(error);
        Assert.Equal(1, scheduler.CallCount);
        Assert.Equal(Callback, scheduler.EntryPoint);
        Assert.Equal(7UL, scheduler.EventType);
        Assert.Equal(2UL, scheduler.EventValue);
        Assert.Equal(UserData, scheduler.UserData);
        Assert.Equal("np_premium_event_7", scheduler.Reason);
        Assert.False(scheduler.ManagerGateHeldDuringCall);

        // A guest callback is allowed to unregister itself because invocation
        // happens after the firmware-modeled manager mutex has been released.
        AssertResult(0, NpManagerExports.NpUnregisterPremiumEventCallback);
    }

    private void Initialize()
    {
        _ctx[CpuRegister.Rdi] = 0x4000;
        _ctx[CpuRegister.Rsi] = NameAddress;
        AssertResult(0, NpManagerExports.NpManagerGlobalInitializeCompat1270);
    }

    private void Register(ulong callback, ulong userData)
    {
        _ctx[CpuRegister.Rdi] = callback;
        _ctx[CpuRegister.Rsi] = userData;
        AssertResult(0, NpManagerExports.NpRegisterPremiumEventCallback);
    }

    private void RegisterReachability(ulong callback, ulong userData)
    {
        _ctx[CpuRegister.Rdi] = callback;
        _ctx[CpuRegister.Rsi] = userData;
        AssertResult(0, NpManagerExports.NpRegisterNpReachabilityStateCallback);
    }

    private void AssertResult(int expected, Func<CpuContext, int> export)
    {
        Assert.Equal(expected, export(_ctx));
        Assert.Equal(unchecked((ulong)expected), _ctx[CpuRegister.Rax]);
    }

    private sealed class SparseGuestAddressSpace : ICpuMemory, IGuestAddressSpace
    {
        private sealed record Region(ulong Address, byte[] Storage)
        {
            public ulong End => Address + (ulong)Storage.Length;
        }

        private readonly object _gate = new();
        private readonly List<Region> _regions = [];

        public SparseGuestAddressSpace(ulong baseAddress, int size)
        {
            _regions.Add(new Region(baseAddress, new byte[size]));
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            lock (_gate)
            {
                if (!TryResolve(virtualAddress, destination.Length, out var region, out var offset))
                {
                    return false;
                }

                region.Storage.AsSpan(offset, destination.Length).CopyTo(destination);
                return true;
            }
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            lock (_gate)
            {
                if (!TryResolve(virtualAddress, source.Length, out var region, out var offset))
                {
                    return false;
                }

                source.CopyTo(region.Storage.AsSpan(offset, source.Length));
                return true;
            }
        }

        public void WriteCString(ulong address, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Assert.True(TryWrite(address, bytes));
            Assert.True(TryWrite(address + (ulong)bytes.Length, stackalloc byte[] { 0 }));
        }

        public ulong AllocateAt(
            ulong desiredAddress,
            ulong size,
            bool executable = true,
            bool allowAlternative = true)
        {
            lock (_gate)
            {
                if (TryAddRegion(desiredAddress, size, out var address))
                {
                    return address;
                }

                if (!allowAlternative)
                {
                    return 0;
                }

                var alternative = _regions.Count == 0
                    ? desiredAddress
                    : _regions.Max(static region => region.End);
                alternative = AlignUp(alternative, 0x1000);
                return TryAddRegion(alternative, size, out address) ? address : 0;
            }
        }

        public bool TryAllocateAtOrAbove(
            ulong desiredAddress,
            ulong size,
            bool executable,
            ulong alignment,
            out ulong actualAddress)
        {
            lock (_gate)
            {
                actualAddress = 0;
                if (alignment == 0 || (alignment & (alignment - 1)) != 0)
                {
                    return false;
                }

                var candidate = AlignUp(desiredAddress, alignment);
                for (var attempt = 0; attempt < 1024; attempt++)
                {
                    var overlap = _regions.FirstOrDefault(region =>
                        candidate < region.End &&
                        region.Address < candidate + size);
                    if (overlap is null)
                    {
                        return TryAddRegion(candidate, size, out actualAddress);
                    }

                    candidate = AlignUp(overlap.End, alignment);
                }

                return false;
            }
        }

        public bool TryBackFixedRange(ulong address, ulong size, bool executable)
        {
            lock (_gate)
            {
                if (size == 0 || size > int.MaxValue || ulong.MaxValue - address < size)
                {
                    return false;
                }

                if (TryResolve(address, (int)size, out _, out _))
                {
                    return true;
                }

                return TryAddRegion(address, size, out _);
            }
        }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address) =>
            TryAllocateAtOrAbove(BaseAddress + 0x10_0000, size, false, alignment, out address);

        public bool TryFreeGuestMemory(ulong address)
        {
            lock (_gate)
            {
                var index = _regions.FindIndex(region => region.Address == address);
                if (index < 0)
                {
                    return false;
                }

                _regions.RemoveAt(index);
                return true;
            }
        }

        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection)
        {
            lock (_gate)
            {
                return TryResolve(address, checked((int)size), out _, out _);
            }
        }

        private bool TryAddRegion(ulong address, ulong size, out ulong allocatedAddress)
        {
            allocatedAddress = 0;
            if (address == 0 || size == 0 || size > int.MaxValue ||
                ulong.MaxValue - address < size)
            {
                return false;
            }

            var end = address + size;
            if (_regions.Any(region => address < region.End && region.Address < end))
            {
                return false;
            }

            _regions.Add(new Region(address, new byte[(int)size]));
            allocatedAddress = address;
            return true;
        }

        private bool TryResolve(
            ulong address,
            int length,
            out Region region,
            out int offset)
        {
            region = null!;
            offset = 0;
            if (length < 0 || ulong.MaxValue - address < (ulong)length)
            {
                return false;
            }

            var end = address + (ulong)length;
            region = _regions.FirstOrDefault(candidate =>
                candidate.Address <= address && end <= candidate.End)!;
            if (region is null)
            {
                return false;
            }

            offset = checked((int)(address - region.Address));
            return true;
        }

        private static ulong AlignUp(ulong value, ulong alignment) =>
            checked((value + alignment - 1) & ~(alignment - 1));
    }

    private sealed class RecordingScheduler : IGuestThreadScheduler
    {
        public int CallCount { get; private set; }
        public ulong EntryPoint { get; private set; }
        public ulong EventType { get; private set; }
        public ulong EventValue { get; private set; }
        public ulong UserData { get; private set; }
        public string? Reason { get; private set; }
        public bool ManagerGateHeldDuringCall { get; private set; }

        public bool SupportsGuestContextTransfer => false;

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context)
        {
        }

        public bool TryStartThread(
            CpuContext creatorContext,
            GuestThreadStartRequest request,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryJoinThread(
            CpuContext callerContext,
            ulong threadHandle,
            out ulong returnValue,
            out string? error)
        {
            returnValue = 0;
            error = "not supported";
            return false;
        }

        public void Pump(CpuContext callerContext, string reason)
        {
        }

        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => 0;

        public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority) => false;

        public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask) => false;

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => [];

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out string? error)
        {
            error = "two-argument callback overload was not expected";
            return false;
        }

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error)
        {
            CallCount++;
            EntryPoint = entryPoint;
            EventType = arg0;
            EventValue = arg1;
            UserData = arg2;
            Reason = reason;
            ManagerGateHeldDuringCall = NpManagerExports.IsPremiumEventGateHeldByCurrentThreadForTests;
            returnValue = 0;
            error = null;
            return true;
        }

        public bool TryCallGuestContinuation(
            CpuContext callerContext,
            GuestCpuContinuation continuation,
            string reason,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryRaiseGuestException(
            CpuContext callerContext,
            ulong threadHandle,
            ulong handler,
            int exceptionType,
            out string? error)
        {
            error = "not supported";
            return false;
        }
    }
}
