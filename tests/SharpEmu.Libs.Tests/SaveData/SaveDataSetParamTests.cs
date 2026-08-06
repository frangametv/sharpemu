// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests.SaveData;

[Collection("SaveDataMemoryState")]
public sealed class SaveDataSetParamTests : IDisposable
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong MountAddress = Base + 0x100;
    private const ulong DirNameAddress = Base + 0x180;
    private const ulong MountResultAddress = Base + 0x200;
    private const ulong IconAddress = Base + 0x300;
    private const int UserId = 0x1001;
    private const string TitleId = "PARAMTEST";
    private const string DirName = "slot0";
    private const int ParamSize = 0x530;

    private readonly FakeCpuMemory _memory = new(Base, 0x2000);
    private readonly CpuContext _ctx;
    private readonly string _root;
    private readonly string? _previousRoot;

    public SaveDataSetParamTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        _root = Path.Combine(Path.GetTempPath(), $"sharpemu-sdparam-{Guid.NewGuid():N}");
        _previousRoot = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _root);
        SaveDataExports.ConfigureApplicationInfo(TitleId);
        Mount();
    }

    private string MetadataPath =>
        Path.Combine(_root, UserId.ToString(), TitleId, "sce_metadata", $"{DirName}.param");

    private string IconPath =>
        Path.Combine(_root, UserId.ToString(), TitleId, "sce_metadata", $"{DirName}.icon");

    public void Dispose()
    {
        SaveDataExports.ConfigureApplicationInfo(null);
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _previousRoot);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SetParam_All_PersistsLibcBackedMetadata()
    {
        var expected = Enumerable.Range(0, ParamSize).Select(i => (byte)(i * 31 + 7)).ToArray();
        var paramAddress = AllocateTracked(ParamSize);
        try
        {
            Marshal.Copy(expected, 0, unchecked((nint)paramAddress), expected.Length);
            InvokeSetParam(type: 0, paramAddress, ParamSize);

            Assert.Equal(0, SaveDataExports.SaveDataSetParam(_ctx));
            Assert.Equal(expected, File.ReadAllBytes(MetadataPath));
        }
        finally
        {
            FreeTracked(paramAddress);
        }
    }

    [Fact]
    public void SetParam_Title_UpdatesOnlyTitleField()
    {
        var initial = Enumerable.Repeat((byte)0xA5, ParamSize).ToArray();
        var allAddress = AllocateTracked(ParamSize);
        var titleAddress = AllocateTracked(6);
        try
        {
            Marshal.Copy(initial, 0, unchecked((nint)allAddress), initial.Length);
            InvokeSetParam(type: 0, allAddress, ParamSize);
            Assert.Equal(0, SaveDataExports.SaveDataSetParam(_ctx));

            var title = "Title\0"u8.ToArray();
            Marshal.Copy(title, 0, unchecked((nint)titleAddress), title.Length);
            InvokeSetParam(type: 1, titleAddress, title.Length);
            Assert.Equal(0, SaveDataExports.SaveDataSetParam(_ctx));

            var persisted = File.ReadAllBytes(MetadataPath);
            Assert.Equal(title, persisted[..title.Length]);
            Assert.All(persisted[title.Length..0x80], value => Assert.Equal(0, value));
            Assert.All(persisted[0x80..], value => Assert.Equal(0xA5, value));
        }
        finally
        {
            FreeTracked(titleAddress);
            FreeTracked(allAddress);
        }
    }

    [Fact]
    public void SetParam_UnmappedBuffer_DoesNotCreateMetadata()
    {
        InvokeSetParam(type: 0, Base + 0x10000, ParamSize);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            SaveDataExports.SaveDataSetParam(_ctx));
        Assert.False(File.Exists(MetadataPath));
    }

    [Fact]
    public void SaveIcon_PersistsLibcBackedImage()
    {
        var expected = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
        var bufferAddress = AllocateTracked(expected.Length);
        try
        {
            Marshal.Copy(expected, 0, unchecked((nint)bufferAddress), expected.Length);
            Span<byte> icon = stackalloc byte[0x10];
            BinaryPrimitives.WriteUInt64LittleEndian(icon, bufferAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(icon[0x08..], unchecked((ulong)expected.Length));
            Assert.True(_memory.TryWrite(IconAddress, icon));

            _ctx[CpuRegister.Rdi] = MountResultAddress;
            _ctx[CpuRegister.Rsi] = IconAddress;
            Assert.Equal(0, SaveDataExports.SaveDataSaveIcon(_ctx));
            Assert.Equal(expected, File.ReadAllBytes(IconPath));
        }
        finally
        {
            FreeTracked(bufferAddress);
        }
    }

    private void Mount()
    {
        Span<byte> mount = stackalloc byte[0x30];
        mount.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(mount, UserId);
        BinaryPrimitives.WriteUInt64LittleEndian(mount[0x08..], DirNameAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(mount[0x10..], 96);
        BinaryPrimitives.WriteUInt32LittleEndian(mount[0x20..], 1u << 5);
        Assert.True(_memory.TryWrite(MountAddress, mount));

        Span<byte> dirName = stackalloc byte[32];
        dirName.Clear();
        "slot0"u8.CopyTo(dirName);
        Assert.True(_memory.TryWrite(DirNameAddress, dirName));

        _ctx[CpuRegister.Rdi] = MountAddress;
        _ctx[CpuRegister.Rsi] = MountResultAddress;
        Assert.Equal(0, SaveDataExports.SaveDataMount3(_ctx));
    }

    private void InvokeSetParam(uint type, ulong paramAddress, int paramSize)
    {
        _ctx[CpuRegister.Rdi] = MountResultAddress;
        _ctx[CpuRegister.Rsi] = type;
        _ctx[CpuRegister.Rdx] = paramAddress;
        _ctx[CpuRegister.Rcx] = unchecked((ulong)paramSize);
    }

    private ulong AllocateTracked(int length)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)length);
        Assert.Equal(0, KernelMemoryCompatExports.Malloc(_ctx));
        Assert.NotEqual(0UL, _ctx[CpuRegister.Rax]);
        return _ctx[CpuRegister.Rax];
    }

    private void FreeTracked(ulong address)
    {
        _ctx[CpuRegister.Rdi] = address;
        Assert.Equal(0, KernelMemoryCompatExports.Free(_ctx));
    }
}
