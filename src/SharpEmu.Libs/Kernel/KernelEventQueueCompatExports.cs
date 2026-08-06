// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SharpEmu.Libs.Kernel;

public static class KernelEventQueueCompatExports
{
    private const int KernelEventSize = 0x20;
    public const short KernelEventFilterGraphics = -14;
    public const short KernelEventFilterUser = -11;
    public const short KernelEventFilterAmpr = -16;
    public const short KernelEventFilterAmprSystem = -17;
    public const ushort KernelEventFlagClear = 0x20;

    private static readonly object _eventQueueGate = new();
    private static readonly Dictionary<ulong, EventQueueState> _eventQueues = new();
    private static readonly ConditionalWeakTable<object, EventQueueRuntimeIdentity>
        _eventQueueRuntimeIdentities = new();
    private static readonly Dictionary<ulong, KernelEventDeque> _pendingEvents = new();
    private static readonly Dictionary<ulong, Dictionary<(ulong Ident, short Filter), KernelEventRegistration>> _registeredEvents = new();
    private static long _nextEventQueueHandle = 1;
    private static long _nextEventQueueWaiterId;
    private static long _nextEventRegistrationGeneration;
    private static long _nextEventQueueGeneration;
    private static long _nextEventQueueRuntimeId;

    private sealed record EventQueueRuntimeIdentity(ulong Id);

    private sealed class EventQueueState
    {
        public required ulong Handle { get; init; }
        public required ulong RuntimeId { get; init; }
        public required ulong Generation { get; init; }
        public required string WakeKey { get; init; }
        public bool Deleted { get; set; }
    }

    public readonly record struct KernelQueuedEvent(
        ulong Ident,
        short Filter,
        ushort Flags,
        uint Fflags,
        ulong Data,
        ulong UserData);

    private readonly record struct KernelEventRegistration(
        ulong Ident,
        short Filter,
        ulong UserData,
        ushort Flags,
        ulong Generation);

    internal readonly record struct KernelEventRegistrationToken(
        ulong EqueueHandle,
        ulong EqueueGeneration,
        ulong Ident,
        short Filter,
        ulong Generation);

    internal sealed record KernelEventRegistrationSnapshot(
        ulong RuntimeId,
        ulong Ident,
        short Filter,
        KernelEventRegistrationToken[] Targets);

    internal readonly record struct CapturedEventDeliveryResult(
        int TriggeredCount,
        int StaleCount);

    // Grow-only ring buffer standing in for LinkedList<KernelQueuedEvent>, which
    // allocated a node per enqueue — steady churn at one enqueue per vblank/flip edge
    // per registered queue. Mutated only under _eventQueueGate.
    private sealed class KernelEventDeque
    {
        private KernelQueuedEvent[] _items = new KernelQueuedEvent[4];
        private int _head;

        public int Count { get; private set; }

        public KernelQueuedEvent this[int index]
        {
            get => _items[(_head + index) % _items.Length];
            set => _items[(_head + index) % _items.Length] = value;
        }

        public void AddLast(in KernelQueuedEvent item)
        {
            if (Count == _items.Length)
            {
                var grown = new KernelQueuedEvent[_items.Length * 2];
                for (var i = 0; i < Count; i++)
                {
                    grown[i] = this[i];
                }

                _items = grown;
                _head = 0;
            }

            _items[(_head + Count) % _items.Length] = item;
            Count++;
        }

        public KernelQueuedEvent RemoveFirst()
        {
            var value = _items[_head];
            _head = (_head + 1) % _items.Length;
            Count--;
            return value;
        }

        public int FindIndex(ulong ident, short filter)
        {
            for (var i = 0; i < Count; i++)
            {
                var candidate = this[i];
                if (candidate.Ident == ident && candidate.Filter == filter)
                {
                    return i;
                }
            }

            return -1;
        }

        public bool Remove(ulong ident, short filter)
        {
            var index = FindIndex(ident, filter);
            if (index < 0)
            {
                return false;
            }

            for (var i = index; i < Count - 1; i++)
            {
                this[i] = this[i + 1];
            }

            Count--;
            return true;
        }

    }

    [SysAbiExport(
        Nid = "D0OdFMjp46I",
        ExportName = "sceKernelCreateEqueue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCreateEqueue(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        if (outAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var handle = unchecked((ulong)Interlocked.Increment(ref _nextEventQueueHandle));
        var generation = unchecked((ulong)Interlocked.Increment(
            ref _nextEventQueueGeneration));
        var state = new EventQueueState
        {
            Handle = handle,
            RuntimeId = GetEventQueueRuntimeId(ctx.Memory),
            Generation = generation,
            WakeKey = $"sceKernelWaitEqueue:{handle:X16}:{generation:X16}",
        };
        lock (_eventQueueGate)
        {
            _eventQueues.Add(handle, state);
            _pendingEvents[handle] = new KernelEventDeque();
            _registeredEvents[handle] = new Dictionary<(ulong Ident, short Filter), KernelEventRegistration>();
        }

        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, outAddress, handle))
        {
            lock (_eventQueueGate)
            {
                _eventQueues.Remove(handle);
                _pendingEvents.Remove(handle);
                _registeredEvents.Remove(handle);
            }

            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceEventQueue(ctx, "create", handle);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "jpFjmgAC5AE",
        ExportName = "sceKernelDeleteEqueue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteEqueue(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        EventQueueState state;
        lock (_eventQueueGate)
        {
            if (!_eventQueues.Remove(handle, out state!) || state.Deleted)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            state.Deleted = true;
            _pendingEvents.Remove(handle);
            _registeredEvents.Remove(handle);
            // Wake any thread parked on this queue so it observes the deletion.
            Monitor.PulseAll(_eventQueueGate);
        }

        TraceEventQueue(ctx, "delete", handle);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "WDszmSbWuDk",
        ExportName = "sceKernelAddUserEventEdge",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAddUserEventEdge(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var registered = RegisterEvent(
            handle,
            ctx[CpuRegister.Rsi],
            KernelEventFilterUser,
            0,
            KernelEventFlagClear);
        TraceEventQueue(ctx, "add_user_edge", handle);
        return registered
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "4R6-OvI2cEA",
        ExportName = "sceKernelAddUserEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAddUserEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var registered = RegisterEvent(
            handle,
            ctx[CpuRegister.Rsi],
            KernelEventFilterUser,
            0,
            flags: 0);
        TraceEventQueue(ctx, "add_user", handle);
        return registered
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "LJDwdSNTnDg",
        ExportName = "sceKernelDeleteUserEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteUserEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var deleted = DeleteRegisteredEvent(
            handle,
            ctx[CpuRegister.Rsi],
            KernelEventFilterUser);
        TraceEventQueue(ctx, "delete_user", handle);
        return deleted
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "F6e0kwo4cnk",
        ExportName = "sceKernelTriggerUserEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelTriggerUserEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var triggered = TriggerRegisteredEvent(
            handle,
            ctx[CpuRegister.Rsi],
            KernelEventFilterUser,
            userData: ctx[CpuRegister.Rdx]);
        TraceEventQueue(ctx, "trigger_user", handle);
        return triggered
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "bBfz7kMF2Ho",
        ExportName = "sceKernelAddAmprEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAddAmprEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var registered = RegisterEvent(
            handle,
            unchecked((uint)ctx[CpuRegister.Rsi]),
            KernelEventFilterAmpr,
            ctx[CpuRegister.Rdx]);
        TraceEventQueue(ctx, "add_ampr", handle);
        return registered
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "vuae5JPNt9A",
        ExportName = "sceKernelAddAmprSystemEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAddAmprSystemEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var registered = RegisterEvent(
            handle,
            unchecked((uint)ctx[CpuRegister.Rsi]),
            KernelEventFilterAmprSystem,
            ctx[CpuRegister.Rdx]);
        TraceEventQueue(ctx, "add_ampr_system", handle);
        return registered
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "bMmid3pfyjo",
        ExportName = "sceKernelDeleteAmprEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteAmprEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var deleted = DeleteRegisteredEvent(
            handle,
            unchecked((uint)ctx[CpuRegister.Rsi]),
            KernelEventFilterAmpr);
        TraceEventQueue(ctx, "delete_ampr", handle);
        return deleted
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "Ij+ryuEClXQ",
        ExportName = "sceKernelDeleteAmprSystemEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteAmprSystemEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var deleted = DeleteRegisteredEvent(
            handle,
            unchecked((uint)ctx[CpuRegister.Rsi]),
            KernelEventFilterAmprSystem);
        TraceEventQueue(ctx, "delete_ampr_system", handle);
        return deleted
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "QyrxcdBrb0M",
        ExportName = "sceKernelGetKqueueFromEqueue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetKqueueFromEqueue(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = ctx[CpuRegister.Rdi];
        TraceEventQueue(ctx, "get_kqueue", ctx[CpuRegister.Rdi]);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "vz+pg2zdopI",
        ExportName = "sceKernelGetEventUserData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetEventUserData(CpuContext ctx)
    {
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rdi] + 0x18, out var userData);
        ctx[CpuRegister.Rax] = userData;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "mJ7aghmgvfc",
        ExportName = "sceKernelGetEventId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetEventId(CpuContext ctx)
    {
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rdi], out var ident);
        ctx[CpuRegister.Rax] = ident;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "23CPPI1tyBY",
        ExportName = "sceKernelGetEventFilter",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetEventFilter(CpuContext ctx)
    {
        Span<byte> filterBytes = stackalloc byte[sizeof(short)];
        var filter = ctx.Memory.TryRead(ctx[CpuRegister.Rdi] + 0x08, filterBytes)
            ? BinaryPrimitives.ReadInt16LittleEndian(filterBytes)
            : (short)0;
        ctx[CpuRegister.Rax] = unchecked((uint)filter);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "kwGyyjohI50",
        ExportName = "sceKernelGetEventData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetEventData(CpuContext ctx)
    {
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rdi] + 0x10, out var data);
        ctx[CpuRegister.Rax] = data;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "fzyMKs9kim0",
        ExportName = "sceKernelWaitEqueue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelWaitEqueue(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var eventsAddress = ctx[CpuRegister.Rsi];
        var eventCapacity = (int)Math.Min(ctx[CpuRegister.Rdx], int.MaxValue);
        var outCountAddress = ctx[CpuRegister.Rcx];
        var timeoutAddress = ctx[CpuRegister.R8];

        if (!TryGetLiveEventQueue(handle, out var state))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (eventsAddress == 0 || eventCapacity < 1)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        uint timeoutUsec = 0;
        if (timeoutAddress != 0 && !TryReadUInt32(ctx, timeoutAddress, out timeoutUsec))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var deliveredCount = DequeueEvents(
            ctx,
            state,
            eventsAddress,
            eventCapacity);
        if (outCountAddress != 0 && !TryWriteUInt32(ctx, outCountAddress, (uint)deliveredCount))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (deliveredCount > 0)
        {
            if (_logEqueue)
            {
                TraceEventQueue(
                    ctx,
                    "wait-deliver",
                    handle,
                    $"delivered={deliveredCount} capacity={eventCapacity}");
            }
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var waiterId = Interlocked.Increment(ref _nextEventQueueWaiterId);

        // Host wait primitives only provide millisecond scheduling granularity.
        // Treat shorter guest deadlines as cooperative polls once the queue has
        // been checked: rounding (for example) a 1-us render-queue wait up to
        // Monitor.Wait(1) slows it by three orders of magnitude and serializes
        // the guest frame. A single yield for a non-zero deadline prevents a
        // render thread from hot-polling millions of imports while allowing an
        // event producer to run before the final check.
        if (timeoutAddress != 0 && IsSubMillisecondPoll(timeoutUsec))
        {
            if (timeoutUsec != 0)
            {
                Thread.Yield();
                deliveredCount = DequeueEvents(ctx, state, eventsAddress, eventCapacity);
                if (outCountAddress != 0 && !TryWriteUInt32(ctx, outCountAddress, (uint)deliveredCount))
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }
                if (deliveredCount > 0)
                {
                    TraceEventQueue(ctx, "wait-deliver", handle);
                    return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                }
            }

            if (_logEqueue)
            {
                TraceEventQueue(
                    ctx,
                    "wait-timeout",
                    handle,
                    $"waiter={waiterId} capacity={eventCapacity} timeout_usec={timeoutUsec}");
            }
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
        }

        // No events ready: block this host thread in place on the queue gate.
        // Monitor.Wait releases the gate and parks atomically, so an
        // EnqueueEvent/TriggerDisplayEvent PulseAll issued the instant after
        // the emptiness check cannot be lost. kqueue/kevent semantics: sleep
        // until an event matching a registration is delivered or the timeout
        // (usec, infinite when the arg pointer is null) lapses; a zero timeout
        // degrades to an instant poll.
        long deadline;
        if (timeoutAddress == 0)
        {
            deadline = long.MaxValue;
        }
        else if (timeoutUsec == 0)
        {
            deadline = 0;
        }
        else
        {
            deadline = Environment.TickCount64 + Math.Max(1L, timeoutUsec / 1000L);
        }

        TraceEventQueue(ctx, "wait-block", handle);
        var guestThreadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
        GuestThreadBlocking.NoteBlocked(
            guestThreadHandle,
            $"sceKernelWaitEqueue handle=0x{handle:X16} capacity={eventCapacity} " +
            $"timeout={(timeoutAddress == 0 ? "infinite" : $"{timeoutUsec}us")}");
        try
        {
            lock (_eventQueueGate)
            {
                while (true)
                {
                    if ((_pendingEvents.TryGetValue(handle, out var queue) && queue.Count != 0) ||
                        !_eventQueues.ContainsKey(handle) ||
                        GuestThreadBlocking.ShutdownRequested)
                    {
                        break;
                    }

                    var remaining = deadline - Environment.TickCount64;
                    if (timeoutAddress != 0 && remaining <= 0)
                    {
                        break;
                    }

                    var slice = timeoutAddress == 0
                        ? GuestThreadBlocking.WaitSliceMilliseconds
                        : (int)Math.Min(remaining, GuestThreadBlocking.WaitSliceMilliseconds);
                    GuestThreadBlocking.Checkpoint(guestThreadHandle, _eventQueueGate);
                    _ = Monitor.Wait(_eventQueueGate, slice);
                }
            }
        }
        finally
        {
            GuestThreadBlocking.NoteUnblocked(guestThreadHandle);
        }

        deliveredCount = DequeueEvents(ctx, state, eventsAddress, eventCapacity);
        if (outCountAddress != 0 && !TryWriteUInt32(ctx, outCountAddress, (uint)deliveredCount))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (deliveredCount > 0)
        {
            TraceEventQueue(ctx, "wait-deliver", handle);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (state.Deleted)
        {
            TraceEventQueue(ctx, "wait-deleted", handle);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED;
        }

        if (timeoutAddress != 0)
        {
            TraceEventQueue(ctx, "wait-timeout", handle);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
        }

        // Reached only on queue deletion or teardown; the guest sees zero events.
        TraceEventQueue(ctx, "wait", handle);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    internal static bool IsSubMillisecondPoll(uint timeoutUsec) => timeoutUsec < 1_000;

    public static bool IsValidEqueue(ulong handle)
    {
        return TryGetLiveEventQueue(handle, out _);
    }

    private static bool TryGetLiveEventQueue(
        ulong handle,
        out EventQueueState state)
    {
        lock (_eventQueueGate)
        {
            return _eventQueues.TryGetValue(handle, out state!) &&
                !state.Deleted;
        }
    }

    private static bool IsLiveEventQueueLocked(EventQueueState state) =>
        !state.Deleted &&
        _eventQueues.TryGetValue(state.Handle, out var current) &&
        ReferenceEquals(current, state);

    private static ulong GetEventQueueRuntimeId(ICpuMemory memory)
    {
        object key = memory;
        while (key is ICpuMemoryWrapper wrapper &&
               !ReferenceEquals(wrapper.Inner, key))
        {
            key = wrapper.Inner;
        }

        return _eventQueueRuntimeIdentities.GetValue(
            key,
            static _ => new EventQueueRuntimeIdentity(
                unchecked((ulong)Interlocked.Increment(
                    ref _nextEventQueueRuntimeId)))).Id;
    }

    internal static bool IsSynchronousPoll(
        ulong timeoutAddress,
        uint timeoutUsec) =>
        timeoutAddress != 0 && timeoutUsec == 0;

    private static bool HasPendingEventsLocked(ulong handle) =>
        _pendingEvents.TryGetValue(handle, out var events) &&
        events.Count != 0;

    public static bool EnqueueEvent(ulong handle, KernelQueuedEvent queuedEvent)
    {
        var queued = false;
        var pendingCount = 0;
        EventQueueState state;
        lock (_eventQueueGate)
        {
            if (!_eventQueues.TryGetValue(handle, out state!) ||
                state.Deleted)
            {
                return false;
            }

            if (!_pendingEvents.TryGetValue(handle, out var queue))
            {
                queue = new KernelEventDeque();
                _pendingEvents[handle] = queue;
            }

            queue.AddLast(queuedEvent);
            queued = true;
            pendingCount = queue.Count;
        }

        if (queued)
        {
            TraceTargetedProducer(handle, "enqueue", queuedEvent, pendingCount);
            WakeEventQueue(state);
        }

        return true;
    }

    public static bool RegisterEvent(
        ulong handle,
        ulong ident,
        short filter,
        ulong userData,
        ushort flags = KernelEventFlagClear)
    {
        lock (_eventQueueGate)
        {
            if (!_eventQueues.TryGetValue(handle, out var state) ||
                state.Deleted)
            {
                return false;
            }

            if (!_registeredEvents.TryGetValue(handle, out var events))
            {
                events = new Dictionary<(ulong Ident, short Filter), KernelEventRegistration>();
                _registeredEvents[handle] = events;
            }

            events[(ident, filter)] = new KernelEventRegistration(
                ident,
                filter,
                userData,
                flags,
                unchecked((ulong)Interlocked.Increment(
                    ref _nextEventRegistrationGeneration)));
            return true;
        }
    }

    /// <summary>
    /// Captures the exact lifetime of every matching registration owned by one
    /// guest runtime. A later delete/re-add of the same tuple receives a new
    /// generation and therefore cannot consume an interrupt that was already
    /// bound to this snapshot.
    /// </summary>
    internal static KernelEventRegistrationSnapshot CaptureRegisteredEvents(
        ICpuMemory memory,
        ulong ident,
        short filter)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var runtimeId = GetEventQueueRuntimeId(memory);
        List<KernelEventRegistrationToken>? targets = null;
        lock (_eventQueueGate)
        {
            foreach (var (handle, registrations) in _registeredEvents)
            {
                if (!_eventQueues.TryGetValue(handle, out var state) ||
                    state.Deleted ||
                    state.RuntimeId != runtimeId ||
                    !registrations.TryGetValue((ident, filter), out var registration))
                {
                    continue;
                }

                (targets ??= []).Add(new KernelEventRegistrationToken(
                    handle,
                    state.Generation,
                    registration.Ident,
                    registration.Filter,
                    registration.Generation));
            }
        }

        return new KernelEventRegistrationSnapshot(
            runtimeId,
            ident,
            filter,
            targets?.ToArray() ?? []);
    }

    /// <summary>
    /// Delivers a previously captured interrupt only to registrations whose
    /// runtime, queue handle, and generation are still live.
    /// </summary>
    internal static CapturedEventDeliveryResult TriggerCapturedEvents(
        KernelEventRegistrationSnapshot snapshot,
        ulong data)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        HashSet<EventQueueState>? wakeQueues = null;
        var triggeredCount = 0;
        var staleCount = 0;
        lock (_eventQueueGate)
        {
            foreach (var target in snapshot.Targets)
            {
                if (!_eventQueues.TryGetValue(
                        target.EqueueHandle,
                        out var state) ||
                    state.Deleted ||
                    state.Generation != target.EqueueGeneration ||
                    state.RuntimeId != snapshot.RuntimeId ||
                    !_registeredEvents.TryGetValue(
                        target.EqueueHandle,
                        out var registrations) ||
                    !registrations.TryGetValue(
                        (target.Ident, target.Filter),
                        out var registration) ||
                    registration.Generation != target.Generation)
                {
                    staleCount++;
                    continue;
                }

                if (!_pendingEvents.TryGetValue(
                        target.EqueueHandle,
                        out var queue))
                {
                    queue = new KernelEventDeque();
                    _pendingEvents[target.EqueueHandle] = queue;
                }

                QueueOrUpdateEvent(
                    queue,
                    new KernelQueuedEvent(
                        registration.Ident,
                        registration.Filter,
                        registration.Flags,
                        1,
                        data,
                        registration.UserData));
                (wakeQueues ??= []).Add(state);
                triggeredCount++;
            }
        }

        if (wakeQueues is not null)
        {
            foreach (var state in wakeQueues)
            {
                WakeEventQueue(
                    state,
                    _logEqueue
                        ? $"source=trigger-captured ident=0x{snapshot.Ident:X16} " +
                          $"filter={snapshot.Filter} data=0x{data:X16}"
                        : null);
            }
        }

        return new CapturedEventDeliveryResult(triggeredCount, staleCount);
    }

    public static bool DeleteRegisteredEvent(
        ulong handle,
        ulong ident,
        short filter)
    {
        lock (_eventQueueGate)
        {
            if (!_registeredEvents.TryGetValue(handle, out var events) ||
                !events.Remove((ident, filter)))
            {
                return false;
            }

            if (_pendingEvents.TryGetValue(handle, out var pending))
            {
                _ = pending.Remove(ident, filter);
            }

            return true;
        }
    }

    public static int TriggerRegisteredEvents(
        ulong ident,
        short filter,
        ulong data)
    {
        var shouldWake = false;
        var triggeredCount = 0;
        List<(ulong Handle, KernelQueuedEvent Event, int PendingCount)>? tracedEvents = null;
        lock (_eventQueueGate)
        {
            foreach (var (handle, registrations) in _registeredEvents)
            {
                if (!_eventQueues.TryGetValue(handle, out var state) ||
                    state.Deleted ||
                    !registrations.TryGetValue((ident, filter), out var registration))
                {
                    continue;
                }

                if (!_pendingEvents.TryGetValue(handle, out var queue))
                {
                    queue = new KernelEventDeque();
                    _pendingEvents[handle] = queue;
                }

                var queuedEvent = new KernelQueuedEvent(
                    registration.Ident,
                    registration.Filter,
                    0,
                    1,
                    data,
                    registration.UserData);
                QueueOrUpdateEvent(queue, queuedEvent);
                CaptureTargetedProducer(
                    ref tracedEvents,
                    handle,
                    queuedEvent,
                    queue.Count);
                shouldWake = true;
                triggeredCount++;
            }
        }

        TraceTargetedProducers("registered_exact", tracedEvents);

        if (shouldWake)
        {
            // Every queue waiter shares _eventQueueGate and WakeEventQueue
            // pulses that single monitor. One pulse-all wakes all matching
            // queues; repeating it once per handle only creates lock and
            // scheduler contention under dense GPU EVENT_WRITE traffic.
            WakeEventQueue(0);
        }

        return triggeredCount;
    }

    /// <summary>
    /// Triggers every registered event on every queue that matches <paramref name="filter"/>
    /// regardless of the registration's <c>ident</c>. This is a workaround for PS5 AGC command
    /// buffers, where <c>IT_EVENT_WRITE</c> carries a hardware <c>EVENT_TYPE</c> that does not
    /// match the <c>eventId</c> the guest registered with <c>sceAgcDriverAddEqEvent</c>.
    /// See issue #173.
    /// </summary>
    public static int TriggerRegisteredEventsByFilter(
        short filter,
        ulong data)
    {
        var shouldWake = false;
        var triggeredCount = 0;
        List<(ulong Handle, KernelQueuedEvent Event, int PendingCount)>? tracedEvents = null;
        lock (_eventQueueGate)
        {
            foreach (var (handle, registrations) in _registeredEvents)
            {
                if (!_eventQueues.TryGetValue(handle, out var state) ||
                    state.Deleted)
                {
                    continue;
                }

                foreach (var registration in registrations.Values)
                {
                    if (registration.Filter != filter)
                    {
                        continue;
                    }

                    if (!_pendingEvents.TryGetValue(handle, out var queue))
                    {
                        queue = new KernelEventDeque();
                        _pendingEvents[handle] = queue;
                    }

                    var queuedEvent = new KernelQueuedEvent(
                        registration.Ident,
                        registration.Filter,
                        registration.Flags,
                        1,
                        data,
                        registration.UserData);
                    QueueOrUpdateEvent(queue, queuedEvent);
                    CaptureTargetedProducer(
                        ref tracedEvents,
                        handle,
                        queuedEvent,
                        queue.Count);
                    shouldWake = true;
                    triggeredCount++;

                    // A single queue only needs to be woken once, even if multiple
                    // registrations matched.
                    break;
                }
            }
        }

        TraceTargetedProducers("registered_filter", tracedEvents);

        if (shouldWake)
        {
            WakeEventQueue(0);
        }

        return triggeredCount;
    }

    /// <summary>
    /// Queues one event for every registration using <paramref name="filter"/>.
    /// Unlike <see cref="TriggerRegisteredEvents"/>, this preserves distinct
    /// event identifiers registered on the same queue. AGC driver completion
    /// queues use this form because the driver, rather than a packet-provided
    /// identifier, announces that the whole submission reached end-of-pipe.
    /// </summary>
    public static int TriggerRegisteredEventsDistinct(short filter)
    {
        var shouldWake = false;
        var triggeredCount = 0;
        List<(ulong Handle, KernelQueuedEvent Event, int PendingCount)>? tracedEvents = null;
        lock (_eventQueueGate)
        {
            foreach (var (handle, registrations) in _registeredEvents)
            {
                if (!_eventQueues.TryGetValue(handle, out var state) ||
                    state.Deleted)
                {
                    continue;
                }

                foreach (var registration in registrations.Values)
                {
                    if (registration.Filter != filter)
                    {
                        continue;
                    }

                    if (!_pendingEvents.TryGetValue(handle, out var queue))
                    {
                        queue = new KernelEventDeque();
                        _pendingEvents[handle] = queue;
                    }

                    var queuedEvent = new KernelQueuedEvent(
                        registration.Ident,
                        registration.Filter,
                        0,
                        1,
                        registration.Ident,
                        registration.UserData);
                    QueueOrUpdateEvent(queue, queuedEvent);
                    CaptureTargetedProducer(
                        ref tracedEvents,
                        handle,
                        queuedEvent,
                        queue.Count);
                    shouldWake = true;
                    triggeredCount++;
                }
            }
        }

        TraceTargetedProducers("registered_distinct", tracedEvents);

        if (shouldWake)
        {
            WakeEventQueue(0);
        }

        return triggeredCount;
    }

    private static bool TriggerRegisteredEvent(
        ulong handle,
        ulong ident,
        short filter,
        ulong userData)
    {
        KernelQueuedEvent queuedEvent;
        var pendingCount = 0;
        EventQueueState state;
        lock (_eventQueueGate)
        {
            if (!_eventQueues.TryGetValue(handle, out state!) ||
                state.Deleted ||
                !_registeredEvents.TryGetValue(handle, out var registrations) ||
                !registrations.TryGetValue((ident, filter), out var registration))
            {
                return false;
            }

            if (!_pendingEvents.TryGetValue(handle, out var queue))
            {
                queue = new KernelEventDeque();
                _pendingEvents[handle] = queue;
            }

            queuedEvent = new KernelQueuedEvent(
                registration.Ident,
                registration.Filter,
                registration.Flags,
                0,
                0,
                userData);
            QueueOrUpdateEvent(queue, queuedEvent);
            pendingCount = queue.Count;
        }

        TraceTargetedProducer(handle, "registered_direct", queuedEvent, pendingCount);
        WakeEventQueue(state);
        return true;
    }

    public static bool TriggerDisplayEvent(
        ulong handle,
        ulong ident,
        short filter,
        ulong eventHint,
        ulong userData)
    {
        var triggered = false;
        var triggeredEvent = default(KernelQueuedEvent);
        var pendingCount = 0;
        EventQueueState state;
        lock (_eventQueueGate)
        {
            if (!_eventQueues.TryGetValue(handle, out state!) ||
                state.Deleted)
            {
                return false;
            }

            if (!_pendingEvents.TryGetValue(handle, out var events))
            {
                events = new KernelEventDeque();
                _pendingEvents[handle] = events;
            }

            var count = 1UL;
            var pendingIndex = events.FindIndex(ident, filter);
            if (pendingIndex >= 0)
            {
                count = Math.Min(((events[pendingIndex].Data >> 12) & 0xFUL) + 1, 0xFUL);
            }

            var timeBits = unchecked((ulong)Environment.TickCount64) & 0xFFFUL;
            var eventData = timeBits | (count << 12) | (eventHint & 0xFFFF_FFFF_FFFF_0000UL);
            triggeredEvent = new KernelQueuedEvent(
                ident,
                filter,
                0x20,
                0,
                eventData,
                userData);

            if (pendingIndex >= 0)
            {
                events[pendingIndex] = triggeredEvent;
            }
            else
            {
                events.AddLast(triggeredEvent);
            }

            triggered = true;
            pendingCount = events.Count;
        }

        if (triggered)
        {
            TraceTargetedProducer(handle, "display", triggeredEvent, pendingCount);
            WakeEventQueue(state);
        }

        return true;
    }

    private static void QueueOrUpdateEvent(
        KernelEventDeque queue,
        KernelQueuedEvent queuedEvent)
    {
        var pendingIndex = queue.FindIndex(queuedEvent.Ident, queuedEvent.Filter);
        if (pendingIndex < 0)
        {
            queue.AddLast(queuedEvent);
            return;
        }

        queue[pendingIndex] = queuedEvent.Filter == KernelEventFilterUser
            ? queuedEvent
            : queuedEvent with
        {
            Fflags = Math.Max(queue[pendingIndex].Fflags + 1, queuedEvent.Fflags),
        };
    }

    // Wake threads parked in-place on the queue gate; each re-checks for a
    // matching pending event. The handle is unused (all queues share one gate)
    // but kept in the signature so call sites read intent-fully.
    private static void WakeEventQueue(ulong handle)
    {
        _ = handle;
        lock (_eventQueueGate)
        {
            Monitor.PulseAll(_eventQueueGate);
        }
    }

    private static void WakeEventQueue(EventQueueState state, string? detail = null)
    {
        if (_logEqueue)
        {
            TraceEventQueueHost(
                "wake",
                state.Handle,
                $"generation={state.Generation}" +
                (detail is null ? string.Empty : $" {detail}"));
        }

        WakeEventQueue(state.Handle);
    }

    private static int DequeueEvents(
        CpuContext ctx,
        EventQueueState state,
        ulong eventsAddress,
        int eventCapacity)
    {
        if (eventsAddress == 0 || eventCapacity <= 0)
        {
            return 0;
        }

        if (!TryReserveEvents(
                state,
                eventCapacity,
                out var events,
                out var count,
                out _))
        {
            return 0;
        }

        var deliveredCount = 0;
        try
        {
            for (; deliveredCount < count; deliveredCount++)
            {
                if (!WriteKernelEvent(
                        ctx,
                        eventsAddress + ((ulong)deliveredCount * KernelEventSize),
                        events[deliveredCount]))
                {
                    break;
                }

                TraceTargetedDelivery(state.Handle, events[deliveredCount], count);
            }
        }
        finally
        {
            ArrayPool<KernelQueuedEvent>.Shared.Return(events);
        }

        return deliveredCount;
    }

    private static bool TryReserveEvents(
        EventQueueState state,
        int eventCapacity,
        out KernelQueuedEvent[] events,
        out int count,
        out bool deleted)
    {
        events = null!;
        count = 0;
        deleted = false;
        lock (_eventQueueGate)
        {
            if (!IsLiveEventQueueLocked(state))
            {
                deleted = true;
                return false;
            }

            if (!_pendingEvents.TryGetValue(state.Handle, out var queue) ||
                queue.Count == 0)
            {
                return false;
            }

            count = Math.Min(eventCapacity, queue.Count);
            events = ArrayPool<KernelQueuedEvent>.Shared.Rent(count);
            for (var i = 0; i < count; i++)
            {
                events[i] = queue.RemoveFirst();
            }

            // Level-triggered events remain ready until their registration is
            // deleted or their source clears. EV_CLEAR events model edges and
            // are consumed by this delivery.
            for (var i = 0; i < count; i++)
            {
                if ((events[i].Flags & KernelEventFlagClear) == 0)
                {
                    queue.AddLast(events[i]);
                }
            }
        }

        return true;
    }

    internal static int ReservePendingEventCountForTest(
        ulong handle,
        int eventCapacity)
    {
        if (!TryGetLiveEventQueue(handle, out var state) ||
            !TryReserveEvents(
                state,
                eventCapacity,
                out var events,
                out var count,
                out _))
        {
            return 0;
        }

        try
        {
            return count;
        }
        finally
        {
            ArrayPool<KernelQueuedEvent>.Shared.Return(events);
        }
    }

    internal static bool TryReservePendingEventForTest(
        ulong handle,
        out KernelQueuedEvent queuedEvent)
    {
        queuedEvent = default;
        if (!TryGetLiveEventQueue(handle, out var state) ||
            !TryReserveEvents(
                state,
                1,
                out var events,
                out var count,
                out _))
        {
            return false;
        }

        try
        {
            queuedEvent = events[0];
            return count == 1;
        }
        finally
        {
            ArrayPool<KernelQueuedEvent>.Shared.Return(events);
        }
    }

    private static bool WriteKernelEvent(CpuContext ctx, ulong address, KernelQueuedEvent queuedEvent)
    {
        Span<byte> eventBytes = stackalloc byte[KernelEventSize];
        BinaryPrimitives.WriteUInt64LittleEndian(eventBytes[0x00..], queuedEvent.Ident);
        BinaryPrimitives.WriteInt16LittleEndian(eventBytes[0x08..], queuedEvent.Filter);
        BinaryPrimitives.WriteUInt16LittleEndian(eventBytes[0x0A..], queuedEvent.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(eventBytes[0x0C..], queuedEvent.Fflags);
        BinaryPrimitives.WriteUInt64LittleEndian(eventBytes[0x10..], queuedEvent.Data);
        BinaryPrimitives.WriteUInt64LittleEndian(eventBytes[0x18..], queuedEvent.UserData);
        return ctx.Memory.TryWrite(address, eventBytes);
    }

    private static readonly bool _logEqueue =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_EQUEUE"), "1", StringComparison.Ordinal);

    // Sample one hot guest call site without turning a sub-microsecond equeue
    // poll into an unbounded log stream. Set the guest return RIP in hex; once
    // matched, producer and delivery traces follow the resolved queue handle.
    private static readonly ulong _traceEqueueReturnRip = ParseTraceEqueueReturnRip();
    private static readonly ulong _traceEqueueHandleFilter = ParseTraceEqueueHandle();
    private static long _traceEqueueHandle = unchecked((long)_traceEqueueHandleFilter);
    private static long _traceEqueueWaitCount;
    private static long _traceEqueueProducerCount;
    private static long _traceEqueueDeliveryCount;

    private static void TraceEventQueue(
        CpuContext ctx,
        string operation,
        ulong handle,
        string? detail = null)
    {
        if (!_logEqueue && _traceEqueueReturnRip == 0)
        {
            return;
        }

        var returnRip = 0UL;
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out returnRip);
        var targeted = _traceEqueueReturnRip != 0 &&
            returnRip == _traceEqueueReturnRip &&
            (_traceEqueueHandleFilter == 0 || handle == _traceEqueueHandleFilter);
        if (!targeted && !_logEqueue)
        {
            return;
        }

        var sampleCount = 0L;
        if (targeted)
        {
            Interlocked.CompareExchange(ref _traceEqueueHandle, unchecked((long)handle), 0);
            sampleCount = Interlocked.Increment(ref _traceEqueueWaitCount);
            if (!_logEqueue && !ShouldSampleHotTrace(sampleCount))
            {
                return;
            }
        }

        var timeoutAddress = ctx[CpuRegister.R8];
        var timeoutDescription = timeoutAddress == 0
            ? "infinite"
            : TryReadUInt32(ctx, timeoutAddress, out var timeoutUsec)
                ? $"{timeoutUsec}us"
                : "unreadable";
        var guestThreadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
        var guestThreadName = KernelPthreadState.TryGetThreadIdentity(
            guestThreadHandle,
            out var guestIdentity)
            ? guestIdentity.Name
            : "<unregistered>";
        Console.Error.WriteLine(
            $"[LOADER][TRACE] equeue.{operation}: handle=0x{handle:X16} " +
            $"sample={sampleCount} ticks={Environment.TickCount64} " +
            $"rsi=0x{ctx[CpuRegister.Rsi]:X16} rdx=0x{ctx[CpuRegister.Rdx]:X16} " +
            $"r8=0x{timeoutAddress:X16} timeout={timeoutDescription} " +
            $"guest=0x{guestThreadHandle:X16} name=\"{guestThreadName}\" " +
            $"host={Environment.CurrentManagedThreadId} ret=0x{returnRip:X16} " +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : $"{detail} ") +
            $"{DescribeEventQueue(handle)}");
    }

    private static void TraceEventQueueHost(
        string operation,
        ulong handle,
        string? detail = null)
    {
        if (!_logEqueue)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] equeue.{operation}: handle=0x{handle:X16} " +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : $"{detail} ") +
            DescribeEventQueue(handle));
    }

    private static ulong ParseTraceEqueueReturnRip()
    {
        var raw = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_EQUEUE_RETURN_RIP");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[2..];
        }

        return ulong.TryParse(raw, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static ulong ParseTraceEqueueHandle()
    {
        var raw = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_EQUEUE_HANDLE");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        raw = raw.Trim();
        var style = NumberStyles.Integer;
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[2..];
            style = NumberStyles.AllowHexSpecifier;
        }

        return ulong.TryParse(raw, style, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static bool IsTargetedTraceHandle(ulong handle) =>
        _traceEqueueReturnRip != 0 &&
        unchecked((ulong)Volatile.Read(ref _traceEqueueHandle)) == handle;

    private static bool ShouldSampleHotTrace(long count) =>
        count <= 16 ||
        (count & (count - 1)) == 0 ||
        count % 1_000_000 == 0;

    private static bool ShouldSampleEventTrace(long count) =>
        count <= 128 || count % 60 == 0;

    private static void CaptureTargetedProducer(
        ref List<(ulong Handle, KernelQueuedEvent Event, int PendingCount)>? tracedEvents,
        ulong handle,
        KernelQueuedEvent queuedEvent,
        int pendingCount)
    {
        if (!IsTargetedTraceHandle(handle))
        {
            return;
        }

        (tracedEvents ??= new()).Add((handle, queuedEvent, pendingCount));
    }

    private static void TraceTargetedProducers(
        string source,
        List<(ulong Handle, KernelQueuedEvent Event, int PendingCount)>? tracedEvents)
    {
        if (tracedEvents is null)
        {
            return;
        }

        foreach (var tracedEvent in tracedEvents)
        {
            TraceTargetedProducer(
                tracedEvent.Handle,
                source,
                tracedEvent.Event,
                tracedEvent.PendingCount);
        }
    }

    private static void TraceTargetedProducer(
        ulong handle,
        string source,
        KernelQueuedEvent queuedEvent,
        int pendingCount)
    {
        if (!IsTargetedTraceHandle(handle))
        {
            return;
        }

        var count = Interlocked.Increment(ref _traceEqueueProducerCount);
        if (!ShouldSampleEventTrace(count))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] equeue.producer: sample={count} ticks={Environment.TickCount64} " +
            $"source={source} handle=0x{handle:X16} pending={pendingCount} " +
            DescribeEvent(queuedEvent));
    }

    private static void TraceTargetedDelivery(
        ulong handle,
        KernelQueuedEvent queuedEvent,
        int deliveredCount)
    {
        if (!IsTargetedTraceHandle(handle))
        {
            return;
        }

        var count = Interlocked.Increment(ref _traceEqueueDeliveryCount);
        if (!ShouldSampleEventTrace(count))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] equeue.delivery: sample={count} ticks={Environment.TickCount64} " +
            $"handle=0x{handle:X16} batch={deliveredCount} {DescribeEvent(queuedEvent)}");
    }

    private static string DescribeEventQueue(ulong handle)
    {
        lock (_eventQueueGate)
        {
            var registrations = _registeredEvents.TryGetValue(handle, out var registered)
                ? string.Join(",", registered.Values.Select(static value =>
                    $"0x{value.Ident:X}/f{value.Filter}/u0x{value.UserData:X}"))
                : string.Empty;
            var pending = _pendingEvents.TryGetValue(handle, out var queue)
                ? queue.Count
                : 0;
            return $"registrations=[{registrations}] pending={pending}";
        }
    }

    private static string DescribeEvent(KernelQueuedEvent queuedEvent) =>
        $"ident=0x{queuedEvent.Ident:X16} filter={queuedEvent.Filter} " +
        $"flags=0x{queuedEvent.Flags:X4} fflags=0x{queuedEvent.Fflags:X8} " +
        $"data=0x{queuedEvent.Data:X16} userdata=0x{queuedEvent.UserData:X16}";

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }
}
