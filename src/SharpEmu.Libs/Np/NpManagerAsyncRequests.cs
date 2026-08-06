// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Np;

/// <summary>
/// Local lifecycle state for Gen5 NP SDK asynchronous requests.
/// </summary>
/// <remarks>
/// The firmware creates this registry during the NpManager PRX start path. It
/// is deliberately independent of the public manager allocator lifecycle in
/// <see cref="NpManagerExports"/>. SharpEmu does not currently expose PRX
/// start/stop callbacks to HLE libraries, so loading this type models PRX start
/// and the test-only reset hooks model PRX teardown/restart.
/// </remarks>
internal static class NpManagerAsyncRequests
{
    internal const int ErrorNotInitialized = unchecked((int)0x80550002);
    internal const int ErrorInvalidArgument = unchecked((int)0x80550003);
    internal const int ErrorOutOfMemory = unchecked((int)0x80550005);
    internal const int ErrorAborted = unchecked((int)0x80550012);
    internal const int ErrorTooManyRequests = unchecked((int)0x80550013);
    internal const int ErrorRequestNotFound = unchecked((int)0x80550014);
    internal const int ErrorInvalidRequestState = unchecked((int)0x80550015);

    private const int MaximumLiveRequests = 32;
    private const int MaximumRequestId = 0x2fffffff;
    private const int Pending = 1;

    private static readonly object RegistryGate = new();
    private static readonly object StateGate = new();
    private static readonly SortedDictionary<int, Request> Requests = [];

    // The firmware registry exists from PRX start, independently of the
    // manager-global allocator initialized by fHGhS3uP52k.
    private static bool _initialized = true;

    internal enum RequestState
    {
        Created,
        Running,
        Complete,
    }

    internal readonly record struct RequestSnapshot(
        int Id,
        bool AbortRequested,
        bool ShutdownAbortRequested,
        bool OperationAssigned,
        bool Deleted,
        int Kind,
        RequestState State,
        int Result,
        uint Priority,
        ulong Affinity);

    internal sealed class LocalOperationContext
    {
        private readonly Request _request;

        private LocalOperationContext(Request request)
        {
            _request = request;
        }

        internal bool IsAbortRequested
        {
            get
            {
                lock (StateGate)
                {
                    return _request.AbortRequested || _request.ShutdownAbortRequested;
                }
            }
        }

        internal static LocalOperationContext Create(Request request) => new(request);
    }

    internal sealed class Request(uint priority, ulong affinity)
    {
        internal int Id;
        internal int ReferenceCount = 1;
        internal bool Deleted;
        internal bool AbortRequested;
        internal bool ShutdownAbortRequested;
        internal bool OperationAssigned;
        internal int Kind = 1;
        internal RequestState State;
        internal int Result;
        internal uint Priority { get; } = priority;
        internal ulong Affinity { get; } = affinity;
        internal Task? Worker;
    }

    internal static bool IsInitialized
    {
        get
        {
            lock (RegistryGate)
            {
                return _initialized;
            }
        }
    }

    internal static int Create(uint priority, ulong affinity)
    {
        lock (RegistryGate)
        {
            if (!_initialized)
            {
                return ErrorNotInitialized;
            }
        }

        Request request;
        try
        {
            // Firmware allocates its 0x90-byte record before entering the
            // capacity/ID-registration path. Managed fields model only the
            // evidence-backed portion of that record.
            request = new Request(priority, affinity);
        }
        catch (OutOfMemoryException)
        {
            return ErrorOutOfMemory;
        }

        lock (RegistryGate)
        {
            if (!_initialized)
            {
                return ErrorNotInitialized;
            }

            if (Requests.Count >= MaximumLiveRequests)
            {
                return ErrorTooManyRequests;
            }

            long candidate = 1;
            foreach (var existingId in Requests.Keys)
            {
                if (existingId < candidate)
                {
                    continue;
                }

                if (existingId != candidate)
                {
                    break;
                }

                candidate++;
            }

            if (candidate > MaximumRequestId)
            {
                return ErrorTooManyRequests;
            }

            request.Id = (int)candidate;
            Requests.Add(request.Id, request);
            return request.Id;
        }
    }

    internal static int Abort(int requestId)
    {
        var lookupResult = TryRetain(requestId, out var request);
        if (lookupResult != 0)
        {
            return lookupResult;
        }

        try
        {
            RequestAbort(request!);
            return 0;
        }
        finally
        {
            Release(request!);
        }
    }

    internal static int Delete(int requestId)
    {
        var lookupResult = TryRetain(requestId, out var request);
        if (lookupResult != 0)
        {
            return lookupResult;
        }

        Task? worker;
        try
        {
            RequestAbort(request!);

            lock (RegistryGate)
            {
                if (!request!.Deleted &&
                    Requests.TryGetValue(requestId, out var registered) &&
                    ReferenceEquals(registered, request))
                {
                    Requests.Remove(requestId);
                    request.Deleted = true;
                    Interlocked.Decrement(ref request.ReferenceCount);
                }
            }

            lock (StateGate)
            {
                worker = request!.Worker;
            }

            // Firmware drops both registry and request-state locks before its
            // wait/join helper. A local Task is the HLE worker-handle analogue;
            // a successful wait has the evidenced zero result.
            worker?.GetAwaiter().GetResult();
            return 0;
        }
        finally
        {
            Release(request!);
        }
    }

    internal static int Poll(int requestId, out bool completed, out int result)
    {
        completed = false;
        result = 0;

        var lookupResult = TryRetain(requestId, out var request);
        if (lookupResult != 0)
        {
            return lookupResult;
        }

        try
        {
            lock (StateGate)
            {
                if (request!.Kind == 0)
                {
                    return ErrorInvalidRequestState;
                }

                if (request.State != RequestState.Complete)
                {
                    // The firmware returns 1 for running requests. Its state-0
                    // path also returns 1 when the environment/version query is
                    // unavailable, which is SharpEmu's truthful offline path.
                    return Pending;
                }

                completed = true;
                result = request.Result;
                return 0;
            }
        }
        finally
        {
            Release(request!);
        }
    }

    /// <summary>
    /// Attaches an evidence-backed local operation to a request and returns the
    /// firmware request-registry result.
    /// </summary>
    internal static int StartLocalOperation(
        int requestId,
        Func<LocalOperationContext, int> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        Request? request;
        lock (RegistryGate)
        {
            if (!_initialized)
            {
                return ErrorNotInitialized;
            }

            if (requestId <= 0)
            {
                return ErrorInvalidArgument;
            }

            if (!Requests.TryGetValue(requestId, out request) || request.Deleted)
            {
                return ErrorRequestNotFound;
            }

            Interlocked.Increment(ref request.ReferenceCount);
            lock (StateGate)
            {
                if (request.OperationAssigned)
                {
                    Release(request);
                    return ErrorInvalidArgument;
                }

                request.OperationAssigned = true;
                request.State = RequestState.Running;
                AddReference(request);

                var context = LocalOperationContext.Create(request);
                request.Worker = Task.Run(() =>
                {
                    try
                    {
                        var operationResult = context.IsAbortRequested
                            ? ErrorAborted
                            : operation(context);

                        lock (StateGate)
                        {
                            request.Result = request.AbortRequested || request.ShutdownAbortRequested
                                ? ErrorAborted
                                : operationResult;
                            request.State = RequestState.Complete;
                        }
                    }
                    finally
                    {
                        Release(request);
                    }
                });
            }
        }

        Release(request);
        return 0;
    }

    internal static bool TryStartLocalOperation(
        int requestId,
        Func<LocalOperationContext, int> operation) =>
        StartLocalOperation(requestId, operation) == 0;

    internal static bool WaitForCompletionForTests(int requestId, TimeSpan timeout)
    {
        Task? worker;
        lock (RegistryGate)
        {
            if (!Requests.TryGetValue(requestId, out var request))
            {
                return false;
            }

            lock (StateGate)
            {
                worker = request.Worker;
            }
        }

        return worker?.Wait(timeout) ?? false;
    }

    internal static bool TryGetSnapshotForTests(int requestId, out RequestSnapshot snapshot)
    {
        lock (RegistryGate)
        {
            if (!Requests.TryGetValue(requestId, out var request))
            {
                snapshot = default;
                return false;
            }

            lock (StateGate)
            {
                snapshot = new RequestSnapshot(
                    request.Id,
                    request.AbortRequested,
                    request.ShutdownAbortRequested,
                    request.OperationAssigned,
                    request.Deleted,
                    request.Kind,
                    request.State,
                    request.Result,
                    request.Priority,
                    request.Affinity);
                return true;
            }
        }
    }

    internal static int LiveCountForTests
    {
        get
        {
            lock (RegistryGate)
            {
                return Requests.Count;
            }
        }
    }

    internal static void ShutdownForTests() => Shutdown();

    internal static void ResetForTests()
    {
        Shutdown();
        lock (RegistryGate)
        {
            _initialized = true;
        }
    }

    private static int TryRetain(int requestId, out Request? request)
    {
        lock (RegistryGate)
        {
            request = null;
            if (!_initialized)
            {
                return ErrorNotInitialized;
            }

            if (requestId <= 0)
            {
                return ErrorInvalidArgument;
            }

            if (!Requests.TryGetValue(requestId, out request) || request.Deleted)
            {
                request = null;
                return ErrorRequestNotFound;
            }

            Interlocked.Increment(ref request.ReferenceCount);
            return 0;
        }
    }

    private static void AddReference(Request request)
    {
        Interlocked.Increment(ref request.ReferenceCount);
    }

    private static void Release(Request request)
    {
        _ = Interlocked.Decrement(ref request.ReferenceCount);
    }

    private static void RequestAbort(Request request, bool shutdown = false)
    {
        lock (StateGate)
        {
            request.AbortRequested = true;
            if (shutdown)
            {
                request.ShutdownAbortRequested = true;
            }

            // No HLE online operation has attached any of the firmware's four
            // backend cancellation handles, so there is nothing else to call.
        }
    }

    private static void Shutdown()
    {
        Request[] requests;
        lock (RegistryGate)
        {
            _initialized = false;
            requests = [.. Requests.Values];
            Requests.Clear();
            foreach (var request in requests)
            {
                request.Deleted = true;
                Interlocked.Decrement(ref request.ReferenceCount);
            }
        }

        var workers = new List<Task>(requests.Length);
        foreach (var request in requests)
        {
            RequestAbort(request, shutdown: true);
            lock (StateGate)
            {
                if (request.Worker is not null)
                {
                    workers.Add(request.Worker);
                }
            }
        }

        foreach (var worker in workers)
        {
            worker.GetAwaiter().GetResult();
        }
    }
}
