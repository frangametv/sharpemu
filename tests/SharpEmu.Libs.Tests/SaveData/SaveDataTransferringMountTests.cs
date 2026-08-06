// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests.SaveData;

[Collection("SaveDataMemoryState")]
public sealed class SaveDataTransferringMountTests : IDisposable
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong MountAddress = Base + 0x100;
    private const ulong TitleIdAddress = Base + 0x180;
    private const ulong DirNameAddress = Base + 0x1C0;
    private const ulong ResultAddress = Base + 0x240;
    private const int UserId = 0x1000_0000;
    private const string CurrentTitleId = "PPSA21564";
    private const string SourceTitleId = "PPSA03061";
    private const string DirName = "System";
    private const int ParameterError = unchecked((int)0x809F0000);
    private const int NotInitializedError = unchecked((int)0x809F0001);
    private const int NotFoundError = unchecked((int)0x809F0008);

    private readonly FakeCpuMemory _memory = new(Base, 0x2000);
    private readonly CpuContext _ctx;
    private readonly string _root;
    private readonly string? _previousRoot;

    public SaveDataTransferringMountTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        _root = Path.Combine(Path.GetTempPath(), $"sharpemu-sdtransfer-{Guid.NewGuid():N}");
        _previousRoot = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _root);
        KernelMemoryCompatExports.ClearGuestPathMounts();
        SaveDataExports.ConfigureApplicationInfo(CurrentTitleId);
    }

    public void Dispose()
    {
        SaveDataExports.ConfigureApplicationInfo(null);
        KernelMemoryCompatExports.ClearGuestPathMounts();
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _previousRoot);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TransferringMount_RegistersForGen4AndGen5()
    {
        var gen4Manager = new ModuleManager();
        gen4Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4));
        var gen5Manager = new ModuleManager();
        gen5Manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(gen4Manager.TryGetExport("WAzWTZm1H+I", out var gen4Export));
        Assert.Equal("sceSaveDataTransferringMount", gen4Export.Name);
        Assert.Equal("libSceSaveData", gen4Export.LibraryName);
        Assert.True(gen5Manager.TryGetExport("WAzWTZm1H+I", out var gen5Export));
        Assert.Equal("sceSaveDataTransferringMount", gen5Export.Name);
        Assert.Equal("libSceSaveData", gen5Export.LibraryName);
    }

    [Fact]
    public void TransferringMount_NotInitializedPrecedesPointerValidation()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0;

        Assert.Equal(NotInitializedError, SaveDataExports.SaveDataTransferringMount(_ctx));
        Assert.Equal(unchecked((ulong)NotInitializedError), _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void TransferringMount_NullRequestAfterInitializationReturnsParameterError()
    {
        Initialize();
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = ResultAddress;

        Assert.Equal(ParameterError, SaveDataExports.SaveDataTransferringMount(_ctx));
    }

    [Fact]
    public void TransferringMount_NonzeroReservedBytesReturnParameterWithoutCreatingSave()
    {
        Initialize();
        WriteRequest();
        Assert.True(_memory.TryWrite(MountAddress + 0x20, new byte[] { 1 }));
        Invoke(ResultAddress);

        Assert.Equal(ParameterError, SaveDataExports.SaveDataTransferringMount(_ctx));
        Assert.False(Directory.Exists(SourceSavePath));
        Assert.False(KernelMemoryCompatExports.IsReadOnlyGuestMutationPath("/savedata0/test.bin"));
    }

    [Fact]
    public void TransferringMount_MissingExplicitTitleSaveReturnsNotFoundWithoutCreatingIt()
    {
        Initialize();
        WriteRequest();
        FillResult(0xA5);
        Invoke(ResultAddress);

        Assert.Equal(NotFoundError, SaveDataExports.SaveDataTransferringMount(_ctx));
        Assert.False(Directory.Exists(SourceSavePath));
        Assert.Equal(Enumerable.Repeat((byte)0xA5, 0x40), Read(ResultAddress, 0x40));
    }

    [Fact]
    public void TransferringMount_ExistingExplicitTitleSaveIsMountedReadOnly()
    {
        Initialize();
        Directory.CreateDirectory(SourceSavePath);
        File.WriteAllText(Path.Combine(SourceSavePath, "progress.bin"), "source-save");
        WriteRequest();
        FillResult(0xA5);
        Invoke(ResultAddress);

        Assert.Equal(0, SaveDataExports.SaveDataTransferringMount(_ctx));

        var result = Read(ResultAddress, 0x40);
        Assert.Equal("/savedata0", ReadAscii(result.AsSpan(0, 16)));
        Assert.All(result.AsSpan(0x10).ToArray(), value => Assert.Equal(0, value));
        Assert.True(KernelMemoryCompatExports.IsReadOnlyGuestMutationPath("/savedata0/progress.bin"));
        Assert.True(File.Exists(Path.Combine(SourceSavePath, "progress.bin")));
        Assert.False(Directory.Exists(Path.Combine(_root, UserId.ToString(), CurrentTitleId, DirName)));
    }

    [Fact]
    public void TransferringMount_UnwritableResultRollsBackMountSlot()
    {
        Initialize();
        Directory.CreateDirectory(SourceSavePath);
        WriteRequest();

        Invoke(Base + 0x10000);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            SaveDataExports.SaveDataTransferringMount(_ctx));
        Assert.False(KernelMemoryCompatExports.IsReadOnlyGuestMutationPath("/savedata0/test.bin"));

        Invoke(ResultAddress);
        Assert.Equal(0, SaveDataExports.SaveDataTransferringMount(_ctx));
        Assert.Equal("/savedata0", ReadAscii(Read(ResultAddress, 16)));
    }

    private string SourceSavePath =>
        Path.Combine(_root, UserId.ToString(), SourceTitleId, DirName);

    private void Initialize()
    {
        Assert.Equal(0, SaveDataExports.SaveDataInitialize3(_ctx));
    }

    private void WriteRequest()
    {
        Span<byte> request = stackalloc byte[0x40];
        request.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(request, UserId);
        BinaryPrimitives.WriteUInt64LittleEndian(request[0x08..], TitleIdAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(request[0x10..], DirNameAddress);
        Assert.True(_memory.TryWrite(MountAddress, request));
        WriteAscii(TitleIdAddress, 10, SourceTitleId);
        WriteAscii(DirNameAddress, 32, DirName);
    }

    private void WriteAscii(ulong address, int length, string value)
    {
        var bytes = new byte[length];
        Encoding.ASCII.GetBytes(value).CopyTo(bytes, 0);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private void FillResult(byte value)
    {
        Assert.True(_memory.TryWrite(ResultAddress, Enumerable.Repeat(value, 0x40).ToArray()));
    }

    private void Invoke(ulong resultAddress)
    {
        _ctx[CpuRegister.Rdi] = MountAddress;
        _ctx[CpuRegister.Rsi] = resultAddress;
    }

    private byte[] Read(ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(_memory.TryRead(address, bytes));
        return bytes;
    }

    private static string ReadAscii(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? bytes : bytes[..end]);
    }
}
