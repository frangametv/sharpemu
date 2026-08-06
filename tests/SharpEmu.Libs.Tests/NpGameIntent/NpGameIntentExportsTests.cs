// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.NpGameIntent;
using Xunit;

namespace SharpEmu.Libs.Tests.NpGameIntent;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NpGameIntentStateCollection
{
    public const string Name = "NpGameIntentState";
}

[Collection(NpGameIntentStateCollection.Name)]
public sealed class NpGameIntentExportsTests : IDisposable
{
    private const int NpGameIntentErrorNotInitialized = unchecked((int)0x80553802);
    private readonly CpuContext _ctx = new(new NullMemory(), Generation.Gen5);

    public NpGameIntentExportsTests() => NpGameIntentExports.ResetForTests();

    public void Dispose() => NpGameIntentExports.ResetForTests();

    [Fact]
    public void TerminateRegistersExactGen5ProviderPreferredIdentity()
    {
        var export = Assert.Single(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            candidate => candidate.Nid == "0HBYxYAjmf0");

        Assert.Equal("sceNpGameIntentTerminate", export.Name);
        Assert.Equal("libSceNpGameIntent", export.LibraryName);
        Assert.True(export.PreferLle);
        Assert.Equal(typeof(NpGameIntentExports), export.Function.Method.DeclaringType);
    }

    [Fact]
    public void TerminateMatchesProviderLifecycleResults()
    {
        AssertResult(NpGameIntentErrorNotInitialized, NpGameIntentExports.NpGameIntentTerminate);
        AssertResult(0, NpGameIntentExports.NpGameIntentInitialize);
        AssertResult(0, NpGameIntentExports.NpGameIntentTerminate);
        AssertResult(NpGameIntentErrorNotInitialized, NpGameIntentExports.NpGameIntentTerminate);
    }

    private void AssertResult(int expected, Func<CpuContext, int> export)
    {
        Assert.Equal(expected, export(_ctx));
        Assert.Equal(unchecked((ulong)expected), _ctx[CpuRegister.Rax]);
    }

    private sealed class NullMemory : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }
}
