// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

[Collection(NpManagerStateCollection.Name)]
public sealed class NpManagerAsyncRequestExportsTests : IDisposable
{
    private const int ErrorNotInitialized = unchecked((int)0x80550002);
    private const int ErrorInvalidArgument = unchecked((int)0x80550003);
    private const int ErrorInvalidParameterSize = unchecked((int)0x80550011);
    private const int ErrorAborted = unchecked((int)0x80550012);
    private const int ErrorTooManyRequests = unchecked((int)0x80550013);
    private const int ErrorRequestNotFound = unchecked((int)0x80550014);
    private const int ErrorMemoryFault = unchecked((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);

    private const ulong BaseAddress = 0x5_0000_0000;
    private const int MemorySize = 0x2000;
    private const ulong ParameterAddress = BaseAddress + 0x100;
    private const ulong ResultAddress = BaseAddress + 0x200;
    private const ulong Affinity = 0x1122_3344_5566_7788;
    private const uint Priority = 0x4455_6677;

    private readonly FakeCpuMemory _memory = new(BaseAddress, MemorySize);
    private readonly CpuContext _ctx;

    public NpManagerAsyncRequestExportsTests()
    {
        NpManagerExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
        WriteParameters();
    }

    public void Dispose() => NpManagerExports.ResetForTests();

    [Fact]
    public void Exports_RegisterExactGen5IdentitiesWithoutProjectingNewOnesToGen4()
    {
        var gen5 = new ModuleManager();
        gen5.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        var gen4 = new ModuleManager();
        gen4.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));

        AssertExport(gen5, "eiqMCt9UshI", "sceNpCreateAsyncRequest");
        AssertExport(gen5, "KfGZg2y73oM", "sceNpCheckNpReachability");
        AssertExport(gen5, "S7QTn72PrDw", "sceNpDeleteRequest");
        AssertExport(gen5, "OzKvTvg3ZYU", "sceNpAbortRequest");
        AssertExport(gen5, "uqcPJLWL08M", "sceNpPollAsync");

        Assert.True(gen5.TryGetExport("KfGZg2y73oM", out var reachability));
        Assert.False(reachability.PreferLle);
        Assert.Equal(typeof(NpManagerExports), reachability.Function.Method.DeclaringType);

        Assert.False(gen4.TryGetExport("eiqMCt9UshI", out _));
        Assert.False(gen4.TryGetExport("KfGZg2y73oM", out _));
        Assert.True(gen4.TryGetExport("S7QTn72PrDw", out _));
        Assert.False(gen4.TryGetExport("OzKvTvg3ZYU", out _));
        Assert.False(gen4.TryGetExport("uqcPJLWL08M", out _));

        // Gen4 behavior was outside the evidence packet and remains the
        // pre-existing compatibility success even if Gen5 state is stopped.
        NpManagerAsyncRequests.ShutdownForTests();
        var gen4Context = new CpuContext(_memory, Generation.Gen4);
        gen4Context[CpuRegister.Rdi] = 77;
        Assert.Equal(0, NpManagerExports.NpDeleteRequest(gen4Context));
        Assert.Equal(0UL, gen4Context[CpuRegister.Rax]);
    }

    [Fact]
    public void NotInitialized_WinsBeforePointerAndIdValidation()
    {
        NpManagerAsyncRequests.ShutdownForTests();

        _ctx[CpuRegister.Rdi] = 0;
        AssertResult(ErrorNotInitialized, NpManagerExports.NpCreateAsyncRequest);
        _ctx[CpuRegister.Rsi] = unchecked((ulong)-1L);
        AssertResult(ErrorNotInitialized, NpManagerExports.NpCheckNpReachability);
        AssertResult(ErrorNotInitialized, NpManagerExports.NpAbortRequest);
        AssertResult(ErrorNotInitialized, NpManagerExports.NpDeleteRequest);

        _ctx[CpuRegister.Rsi] = 0;
        AssertResult(ErrorNotInitialized, NpManagerExports.NpPollAsync);
    }

    [Fact]
    public void CheckNpReachability_ValidatesAndCompletesTheCreatedRequest()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0x1000_0000;
        AssertResult(ErrorInvalidArgument, NpManagerExports.NpCheckNpReachability);

        _ctx[CpuRegister.Rdi] = 99;
        AssertResult(ErrorRequestNotFound, NpManagerExports.NpCheckNpReachability);

        var requestId = CreateRequest();
        _ctx[CpuRegister.Rdi] = unchecked((ulong)requestId);
        _ctx[CpuRegister.Rsi] = unchecked((ulong)-1L);
        AssertResult(ErrorInvalidArgument, NpManagerExports.NpCheckNpReachability);
        Assert.True(NpManagerAsyncRequests.TryGetSnapshotForTests(requestId, out var unstarted));
        Assert.False(unstarted.OperationAssigned);

        _ctx[CpuRegister.Rsi] = 0x1000_0000;
        AssertResult(0, NpManagerExports.NpCheckNpReachability);
        Assert.True(NpManagerAsyncRequests.WaitForCompletionForTests(requestId, TimeSpan.FromSeconds(2)));
        Assert.True(NpManagerAsyncRequests.TryGetSnapshotForTests(requestId, out var completed));
        Assert.True(completed.OperationAssigned);
        Assert.Equal(NpManagerAsyncRequests.RequestState.Complete, completed.State);
        Assert.Equal(0, completed.Result);

        WriteResult(unchecked((int)0x7bad_cafe));
        AssertPoll(requestId, ResultAddress, 0);
        Assert.Equal(0, ReadResult());

        _ctx[CpuRegister.Rdi] = unchecked((ulong)requestId);
        _ctx[CpuRegister.Rsi] = 0x1000_0000;
        AssertResult(ErrorInvalidArgument, NpManagerExports.NpCheckNpReachability);
    }

    [Fact]
    public void Create_ValidatesNullSizeAndMappedFieldsInFirmwareOrder()
    {
        _ctx[CpuRegister.Rdi] = 0;
        AssertResult(ErrorInvalidArgument, NpManagerExports.NpCreateAsyncRequest);

        _ctx[CpuRegister.Rdi] = BaseAddress + MemorySize;
        AssertResult(ErrorMemoryFault, NpManagerExports.NpCreateAsyncRequest);

        WriteParameters(size: 0x17);
        _ctx[CpuRegister.Rdi] = ParameterAddress;
        AssertResult(ErrorInvalidParameterSize, NpManagerExports.NpCreateAsyncRequest);

        var tailAddress = BaseAddress + MemorySize - sizeof(ulong);
        Assert.True(_ctx.TryWriteUInt64(tailAddress, 0x18));
        _ctx[CpuRegister.Rdi] = tailAddress;
        AssertResult(ErrorMemoryFault, NpManagerExports.NpCreateAsyncRequest);
        Assert.Equal(0, NpManagerAsyncRequests.LiveCountForTests);
    }

    [Fact]
    public void Create_StoresParametersAndAllocatesSmallestPositiveId()
    {
        var requestId = CreateRequest();

        Assert.Equal(1, requestId);
        Assert.True(NpManagerAsyncRequests.TryGetSnapshotForTests(requestId, out var snapshot));
        Assert.Equal(1, snapshot.Kind);
        Assert.Equal(NpManagerAsyncRequests.RequestState.Created, snapshot.State);
        Assert.False(snapshot.OperationAssigned);
        Assert.Equal(0, snapshot.Result);
        Assert.Equal(Priority, snapshot.Priority);
        Assert.Equal(Affinity, snapshot.Affinity);
    }

    [Fact]
    public void Create_EnforcesCapacityAndReusesSmallestGap()
    {
        for (var expectedId = 1; expectedId <= 32; expectedId++)
        {
            Assert.Equal(expectedId, CreateRequest());
        }

        Assert.Equal(32, NpManagerAsyncRequests.LiveCountForTests);
        _ctx[CpuRegister.Rdi] = ParameterAddress;
        AssertResult(ErrorTooManyRequests, NpManagerExports.NpCreateAsyncRequest);

        _ctx[CpuRegister.Rdi] = 7;
        AssertResult(0, NpManagerExports.NpDeleteRequest);
        Assert.Equal(7, CreateRequest());
        Assert.Equal(32, NpManagerAsyncRequests.LiveCountForTests);
    }

    [Fact]
    public void Abort_ValidatesIdAndLeavesUnstartedRequestUncompleted()
    {
        _ctx[CpuRegister.Rdi] = 0;
        AssertResult(ErrorInvalidArgument, NpManagerExports.NpAbortRequest);
        _ctx[CpuRegister.Rdi] = unchecked((ulong)-1L);
        AssertResult(ErrorInvalidArgument, NpManagerExports.NpAbortRequest);
        _ctx[CpuRegister.Rdi] = 99;
        AssertResult(ErrorRequestNotFound, NpManagerExports.NpAbortRequest);

        var requestId = CreateRequest();
        _ctx[CpuRegister.Rdi] = (ulong)requestId;
        AssertResult(0, NpManagerExports.NpAbortRequest);

        Assert.True(NpManagerAsyncRequests.TryGetSnapshotForTests(requestId, out var snapshot));
        Assert.True(snapshot.AbortRequested);
        Assert.False(snapshot.OperationAssigned);
        Assert.Equal(NpManagerAsyncRequests.RequestState.Created, snapshot.State);

        WriteResult(unchecked((int)0x1122_3344));
        AssertPoll(requestId, ResultAddress, 1);
        Assert.Equal(unchecked((int)0x1122_3344), ReadResult());
    }

    [Fact]
    public void Delete_ValidatesInvalidAndUnknownIds()
    {
        _ctx[CpuRegister.Rdi] = 0;
        AssertResult(ErrorInvalidArgument, NpManagerExports.NpDeleteRequest);
        _ctx[CpuRegister.Rdi] = unchecked((ulong)-1L);
        AssertResult(ErrorInvalidArgument, NpManagerExports.NpDeleteRequest);
        _ctx[CpuRegister.Rdi] = 99;
        AssertResult(ErrorRequestNotFound, NpManagerExports.NpDeleteRequest);
    }

    [Fact]
    public void Poll_ValidatesOutputBeforeIdAndNeverWritesWhilePending()
    {
        WriteResult(unchecked((int)0x7bad_cafe));
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0;
        AssertResult(ErrorInvalidArgument, NpManagerExports.NpPollAsync);

        _ctx[CpuRegister.Rsi] = ResultAddress;
        AssertResult(ErrorInvalidArgument, NpManagerExports.NpPollAsync);
        _ctx[CpuRegister.Rdi] = 123;
        AssertResult(ErrorRequestNotFound, NpManagerExports.NpPollAsync);
        Assert.Equal(unchecked((int)0x7bad_cafe), ReadResult());

        var requestId = CreateRequest();
        AssertPoll(requestId, ResultAddress, 1);
        Assert.Equal(unchecked((int)0x7bad_cafe), ReadResult());

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Assert.True(NpManagerAsyncRequests.TryStartLocalOperation(requestId, _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return unchecked((int)0x1234_5678);
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

        AssertPoll(requestId, ResultAddress, 1);
        Assert.Equal(unchecked((int)0x7bad_cafe), ReadResult());

        release.Set();
        Assert.True(NpManagerAsyncRequests.WaitForCompletionForTests(requestId, TimeSpan.FromSeconds(2)));
        AssertPoll(requestId, ResultAddress, 0);
        Assert.Equal(unchecked((int)0x1234_5678), ReadResult());
    }

    [Fact]
    public void Poll_CompleteWritesExactlyOneIntAndReportsGuestWriteFault()
    {
        var requestId = CreateRequest();
        Assert.True(NpManagerAsyncRequests.TryStartLocalOperation(
            requestId,
            _ => unchecked((int)0xa1b2_c3d4)));
        Assert.True(NpManagerAsyncRequests.WaitForCompletionForTests(requestId, TimeSpan.FromSeconds(2)));

        var guard = new byte[12];
        Array.Fill(guard, (byte)0x5a);
        Assert.True(_memory.TryWrite(ResultAddress - 4, guard));
        AssertPoll(requestId, ResultAddress, 0);

        var observed = new byte[12];
        Assert.True(_memory.TryRead(ResultAddress - 4, observed));
        Assert.Equal([0x5a, 0x5a, 0x5a, 0x5a], observed[..4]);
        Assert.Equal([0xd4, 0xc3, 0xb2, 0xa1], observed[4..8]);
        Assert.Equal([0x5a, 0x5a, 0x5a, 0x5a], observed[8..]);

        AssertPoll(requestId, BaseAddress + MemorySize, ErrorMemoryFault);
    }

    [Fact]
    public void AbortDuringLocalWorker_CompletesWithFirmwareAbortResult()
    {
        var requestId = CreateRequest();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Assert.True(NpManagerAsyncRequests.TryStartLocalOperation(requestId, context =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return context.IsAbortRequested ? ErrorAborted : 77;
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

        _ctx[CpuRegister.Rdi] = (ulong)requestId;
        AssertResult(0, NpManagerExports.NpAbortRequest);
        release.Set();
        Assert.True(NpManagerAsyncRequests.WaitForCompletionForTests(requestId, TimeSpan.FromSeconds(2)));

        AssertPoll(requestId, ResultAddress, 0);
        Assert.Equal(ErrorAborted, ReadResult());
    }

    [Fact]
    public void DeleteAfterAbort_InvalidatesHandleAndAllowsIdReuse()
    {
        var requestId = CreateRequest();
        _ctx[CpuRegister.Rdi] = (ulong)requestId;
        AssertResult(0, NpManagerExports.NpAbortRequest);
        AssertResult(0, NpManagerExports.NpDeleteRequest);

        Assert.Equal(0, NpManagerAsyncRequests.LiveCountForTests);
        _ctx[CpuRegister.Rdi] = (ulong)requestId;
        AssertResult(ErrorRequestNotFound, NpManagerExports.NpAbortRequest);
        AssertPoll(requestId, ResultAddress, ErrorRequestNotFound);
        Assert.Equal(requestId, CreateRequest());
    }

    [Fact]
    public async Task Delete_WaitsForAttachedWorkerAfterUnlinking()
    {
        var requestId = CreateRequest();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Assert.True(NpManagerAsyncRequests.TryStartLocalOperation(requestId, _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return 0;
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

        var deleteContext = new CpuContext(_memory, Generation.Gen5);
        deleteContext[CpuRegister.Rdi] = (ulong)requestId;
        var deleteTask = Task.Run(() => NpManagerExports.NpDeleteRequest(deleteContext));

        Assert.True(SpinWait.SpinUntil(
            () => NpManagerAsyncRequests.LiveCountForTests == 0,
            TimeSpan.FromSeconds(2)));
        var firstCompleted = await Task.WhenAny(deleteTask, Task.Delay(TimeSpan.FromMilliseconds(100)));
        Assert.NotSame(deleteTask, firstCompleted);

        release.Set();
        Assert.Equal(0, await deleteTask);
        Assert.Equal(0UL, deleteContext[CpuRegister.Rax]);
        AssertPoll(requestId, ResultAddress, ErrorRequestNotFound);
    }

    [Fact]
    public void PrxShutdownClearsRequestsWhileManagerGlobalTerminateStaysSeparate()
    {
        var requestId = CreateRequest();

        // fHGhS3uP52k's allocator lifecycle is distinct from the PRX-owned
        // request list, so its terminate export does not invalidate requests.
        AssertResult(0, NpManagerExports.NpManagerGlobalTerminateCompat1270);
        AssertPoll(requestId, ResultAddress, 1);

        NpManagerAsyncRequests.ShutdownForTests();
        Assert.Equal(0, NpManagerAsyncRequests.LiveCountForTests);
        AssertPoll(requestId, ResultAddress, ErrorNotInitialized);

        NpManagerAsyncRequests.ResetForTests();
        Assert.Equal(1, CreateRequest());
    }

    private int CreateRequest()
    {
        _ctx[CpuRegister.Rdi] = ParameterAddress;
        var result = NpManagerExports.NpCreateAsyncRequest(_ctx);
        Assert.True(result > 0);
        Assert.Equal((ulong)result, _ctx[CpuRegister.Rax]);
        return result;
    }

    private void AssertPoll(int requestId, ulong resultAddress, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)requestId);
        _ctx[CpuRegister.Rsi] = resultAddress;
        AssertResult(expected, NpManagerExports.NpPollAsync);
    }

    private void WriteParameters(ulong size = 0x18)
    {
        Span<byte> parameter = stackalloc byte[0x18];
        BinaryPrimitives.WriteUInt64LittleEndian(parameter, size);
        BinaryPrimitives.WriteUInt64LittleEndian(parameter[0x08..], Affinity);
        BinaryPrimitives.WriteUInt32LittleEndian(parameter[0x10..], Priority);
        Assert.True(_memory.TryWrite(ParameterAddress, parameter));
    }

    private void WriteResult(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(ResultAddress, bytes));
    }

    private int ReadResult()
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        Assert.True(_memory.TryRead(ResultAddress, bytes));
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private void AssertResult(int expected, Func<CpuContext, int> export)
    {
        Assert.Equal(expected, export(_ctx));
        Assert.Equal(unchecked((ulong)expected), _ctx[CpuRegister.Rax]);
    }

    private static void AssertExport(ModuleManager manager, string nid, string name)
    {
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceNpManager", export.LibraryName);
    }
}
