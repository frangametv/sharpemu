// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

[Collection("AmprFileRegistry")]
public sealed class AprStreamingContractTests
{
    [Fact]
    public void ResolveWithEmptyPrefix_UsesRecoveredOutputWidthsAndLow32Count()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong prefixAddress = memoryBase + 0x100;
        const ulong pathListAddress = memoryBase + 0x200;
        const ulong pathAddress = memoryBase + 0x300;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong sizesAddress = memoryBase + 0x900;
        byte[] fileContents = [1, 3, 5, 7, 9];
        var temporaryDirectory = Directory.CreateTempSubdirectory("sharpemu-apr-empty-prefix-");
        var hostPath = Path.Combine(temporaryDirectory.FullName, "asset.bin");
        var mountPoint = $"/sharpemu_apr_empty_{Guid.NewGuid():N}";

        try
        {
            File.WriteAllBytes(hostPath, fileContents);
            KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, temporaryDirectory.FullName);
            var memory = new FakeCpuMemory(memoryBase, 0x2000);
            var context = new CpuContext(memory, Generation.Gen5);
            memory.WriteCString(prefixAddress, string.Empty);
            memory.WriteCString(pathAddress, $"{mountPoint}/asset.bin");
            WriteUInt64(memory, pathListAddress, pathAddress);
            context[CpuRegister.Rdi] = prefixAddress;
            context[CpuRegister.Rsi] = pathListAddress;
            context[CpuRegister.Rdx] = 0x1_0000_0001;
            context[CpuRegister.Rcx] = idsAddress;
            context[CpuRegister.R8] = sizesAddress;

            Assert.Equal(
                0,
                Gen5KernelContractExports.AprResolveFilepathsWithPrefixToIdsAndFileSizes(context));

            Assert.NotEqual(uint.MaxValue, ReadUInt32(memory, idsAddress));
            Assert.Equal((ulong)fileContents.Length, ReadUInt64(memory, sizesAddress));
        }
        finally
        {
            KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveWithPrefix_CombinesPathsAndContinuesPastMissingFiles()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong prefixAddress = memoryBase + 0x100;
        const ulong pathListAddress = memoryBase + 0x200;
        const ulong firstPathAddress = memoryBase + 0x300;
        const ulong secondPathAddress = memoryBase + 0x400;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong sizesAddress = memoryBase + 0x900;
        byte[] fileContents = [2, 4, 6, 8];
        var temporaryDirectory = Directory.CreateTempSubdirectory("sharpemu-apr-prefix-");
        var hostPath = Path.Combine(temporaryDirectory.FullName, "asset.bin");
        var mountPoint = $"/sharpemu_apr_prefix_{Guid.NewGuid():N}";

        try
        {
            File.WriteAllBytes(hostPath, fileContents);
            KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, temporaryDirectory.FullName);
            var memory = new FakeCpuMemory(memoryBase, 0x2000);
            var context = new CpuContext(memory, Generation.Gen5);
            memory.WriteCString(prefixAddress, mountPoint + "/");
            memory.WriteCString(firstPathAddress, "asset.bin");
            memory.WriteCString(secondPathAddress, "missing.bin");
            WriteUInt64(memory, pathListAddress, firstPathAddress);
            WriteUInt64(memory, pathListAddress + sizeof(ulong), secondPathAddress);
            context[CpuRegister.Rdi] = prefixAddress;
            context[CpuRegister.Rsi] = pathListAddress;
            context[CpuRegister.Rdx] = 2;
            context[CpuRegister.Rcx] = idsAddress;
            context[CpuRegister.R8] = sizesAddress;

            Assert.Equal(
                0,
                Gen5KernelContractExports.AprResolveFilepathsWithPrefixToIdsAndFileSizes(context));

            Assert.NotEqual(uint.MaxValue, ReadUInt32(memory, idsAddress));
            Assert.Equal(uint.MaxValue, ReadUInt32(memory, idsAddress + sizeof(uint)));
            Assert.Equal((ulong)fileContents.Length, ReadUInt64(memory, sizesAddress));
            Assert.Equal(0UL, ReadUInt64(memory, sizesAddress + sizeof(ulong)));
        }
        finally
        {
            KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ResolveWithRequiredNullPointer_ReturnsKernelEfault(
        bool nullPrefix,
        bool nullPathList,
        bool nullSizes)
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong prefixAddress = memoryBase + 0x100;
        const ulong pathListAddress = memoryBase + 0x200;
        const ulong sizesAddress = memoryBase + 0x300;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = nullPrefix ? 0 : prefixAddress;
        context[CpuRegister.Rsi] = nullPathList ? 0 : pathListAddress;
        context[CpuRegister.Rdx] = 1;
        context[CpuRegister.R8] = nullSizes ? 0 : sizesAddress;

        Assert.Equal(
            unchecked((int)0x8002000E),
            Gen5KernelContractExports.AprResolveFilepathsWithPrefixToIdsAndFileSizes(context));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1025UL)]
    public void ResolveWithInvalidCount_ReturnsInvalidArgument(ulong count)
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = memoryBase + 0x100;
        context[CpuRegister.Rsi] = memoryBase + 0x200;
        context[CpuRegister.Rdx] = count;
        context[CpuRegister.R8] = memoryBase + 0x300;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            Gen5KernelContractExports.AprResolveFilepathsWithPrefixToIdsAndFileSizes(context));
    }

    [Fact]
    public void ResolveStatAndReadFile_UsesSharedAprFileId()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathListAddress = memoryBase + 0x100;
        const ulong pathAddress = memoryBase + 0x200;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong statAddress = memoryBase + 0x900;
        const ulong commandBufferAddress = memoryBase + 0x1000;
        const ulong recordBufferAddress = memoryBase + 0x1100;
        const ulong destinationAddress = memoryBase + 0x2000;
        const ulong stackAddress = memoryBase + 0x3000;
        byte[] fileContents = [10, 11, 12, 13, 14, 15, 16, 17];
        // The kernel FS resolver default-denies raw absolute host paths, so the
        // guest addresses the file through a registered mount instead of handing
        // in a bare host temp path.
        var mountRoot = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-apr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mountRoot);
        var mountPoint = $"/sharpemu_apr_mnt_{Guid.NewGuid():N}";
        const string fileName = "asset.bin";
        var hostPath = Path.Combine(mountRoot, fileName);
        var guestPath = $"{mountPoint}/{fileName}";

        try
        {
            File.WriteAllBytes(hostPath, fileContents);
            KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, mountRoot);
            var memory = new FakeCpuMemory(memoryBase, 0x4000);
            var context = new CpuContext(memory, Generation.Gen5);
            memory.WriteCString(pathAddress, guestPath);
            WriteUInt64(memory, pathListAddress, pathAddress);

            context[CpuRegister.Rdi] = pathListAddress;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = idsAddress;

            Assert.Equal(0, KernelMemoryCompatExports.KernelAprResolveFilepathsToIds(context));

            Span<byte> idBytes = stackalloc byte[sizeof(uint)];
            Assert.True(memory.TryRead(idsAddress, idBytes));
            var fileId = BinaryPrimitives.ReadUInt32LittleEndian(idBytes);
            Assert.NotEqual(uint.MaxValue, fileId);

            context[CpuRegister.Rdi] = fileId;
            context[CpuRegister.Rsi] = statAddress;

            Assert.Equal(0, KernelMemoryCompatExports.KernelAprGetFileStat(context));

            Span<byte> stat = stackalloc byte[120];
            Assert.True(memory.TryRead(statAddress, stat));
            Assert.Equal(fileContents.Length, BinaryPrimitives.ReadInt64LittleEndian(stat[72..]));

            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rsi] = recordBufferAddress;
            context[CpuRegister.Rdx] = 0x100;

            Assert.Equal(0, AmprExports.CommandBufferConstructor(context));
            Assert.Equal(0, AmprExports.CommandBufferSetBuffer(context));

            const ulong readOffset = 2;
            const ulong readSize = 4;
            WriteUInt64(memory, stackAddress + sizeof(ulong), readOffset);
            context[CpuRegister.Rsp] = stackAddress;
            context[CpuRegister.Rdi] = commandBufferAddress;
            context[CpuRegister.Rcx] = fileId;
            context[CpuRegister.R8] = destinationAddress;
            context[CpuRegister.R9] = readSize;

            Assert.Equal(0, AmprExports.AprCommandBufferReadFile(context));

            Span<byte> destination = stackalloc byte[(int)readSize];
            Assert.True(memory.TryRead(destinationAddress, destination));
            Assert.Equal(fileContents.AsSpan((int)readOffset, (int)readSize), destination);

            Span<byte> record = stackalloc byte[0x30];
            Assert.True(memory.TryRead(recordBufferAddress, record));
            Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(record));
            Assert.Equal(fileId, BinaryPrimitives.ReadUInt32LittleEndian(record[0x04..]));
            Assert.Equal(destinationAddress, BinaryPrimitives.ReadUInt64LittleEndian(record[0x08..]));
            Assert.Equal(readSize, BinaryPrimitives.ReadUInt64LittleEndian(record[0x10..]));
            Assert.Equal(readOffset, BinaryPrimitives.ReadUInt64LittleEndian(record[0x18..]));
            Assert.Equal(readSize, BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]));
        }
        finally
        {
            KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            if (Directory.Exists(mountRoot))
            {
                Directory.Delete(mountRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveFilepathsWithPrefixToIdsAndFileSizes_CombinesPrefixAndResolvesRealFile()
    {
        // Resource streamers call WithPrefix to join a directory prefix with a
        // relative asset path. Without HLE every call returned NOT_FOUND and no
        // asset received a real file id/size.
        const ulong memoryBase = 0x1_0000_0000;
        const ulong prefixAddress = memoryBase + 0x80;
        const ulong pathListAddress = memoryBase + 0x100;
        const ulong pathAddress = memoryBase + 0x200;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong sizesAddress = memoryBase + 0x880;
        byte[] fileContents = [1, 2, 3, 4, 5, 6];
        var mountRoot = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-apr-prefix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mountRoot);
        var mountPoint = $"/sharpemu_apr_prefix_mnt_{Guid.NewGuid():N}";
        const string fileName = "asset.bin";
        var hostPath = Path.Combine(mountRoot, fileName);

        try
        {
            File.WriteAllBytes(hostPath, fileContents);
            KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, mountRoot);
            var memory = new FakeCpuMemory(memoryBase, 0x4000);
            var context = new CpuContext(memory, Generation.Gen5);
            memory.WriteCString(prefixAddress, mountPoint);
            memory.WriteCString(pathAddress, fileName);
            WriteUInt64(memory, pathListAddress, pathAddress);

            context[CpuRegister.Rdi] = prefixAddress;
            context[CpuRegister.Rsi] = pathListAddress;
            context[CpuRegister.Rdx] = 1;
            context[CpuRegister.Rcx] = idsAddress;
            context[CpuRegister.R8] = sizesAddress;
            context[CpuRegister.R9] = 0;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelMemoryCompatExports.KernelAprResolveFilepathsWithPrefixToIdsAndFileSizes(context));
            Assert.NotEqual(uint.MaxValue, ReadUInt32(memory, idsAddress));
            Assert.Equal((ulong)fileContents.Length, ReadUInt64(memory, sizesAddress));
        }
        finally
        {
            KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            if (Directory.Exists(mountRoot))
            {
                Directory.Delete(mountRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveFilepathsWithPrefixToIdsAndFileSizes_MissingFile_FailsFastWithErrorIndex()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong prefixAddress = memoryBase + 0x80;
        const ulong pathListAddress = memoryBase + 0x100;
        const ulong pathAddress = memoryBase + 0x200;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong sizesAddress = memoryBase + 0x880;
        const ulong errorIndexAddress = memoryBase + 0x8F0;
        var memory = new FakeCpuMemory(memoryBase, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(prefixAddress, "/does-not-exist-prefix");
        memory.WriteCString(pathAddress, $"missing-{Guid.NewGuid():N}.bin");
        WriteUInt64(memory, pathListAddress, pathAddress);

        context[CpuRegister.Rdi] = prefixAddress;
        context[CpuRegister.Rsi] = pathListAddress;
        context[CpuRegister.Rdx] = 1;
        context[CpuRegister.Rcx] = idsAddress;
        context[CpuRegister.R8] = sizesAddress;
        context[CpuRegister.R9] = errorIndexAddress;

        Assert.Equal(
            -1,
            KernelMemoryCompatExports.KernelAprResolveFilepathsWithPrefixToIdsAndFileSizes(context));
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
        Assert.Equal(uint.MaxValue, ReadUInt32(memory, idsAddress));
        Assert.Equal(0ul, ReadUInt64(memory, sizesAddress));
        Assert.Equal(0u, ReadUInt32(memory, errorIndexAddress));
    }

    [Fact]
    public void ResolveFilepathsToIdsAndFileSizes_MissingFile_FailsFastWithErrorIndex()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathListAddress = memoryBase + 0x100;
        const ulong pathAddress = memoryBase + 0x200;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong sizesAddress = memoryBase + 0x880;
        const ulong errorIndexAddress = memoryBase + 0x8F0;
        var memory = new FakeCpuMemory(memoryBase, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);
        var missingHostPath = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-apr-missing-{Guid.NewGuid():N}.bin");
        memory.WriteCString(pathAddress, missingHostPath);
        WriteUInt64(memory, pathListAddress, pathAddress);

        context[CpuRegister.Rdi] = pathListAddress;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = idsAddress;
        context[CpuRegister.Rcx] = sizesAddress;
        context[CpuRegister.R8] = errorIndexAddress;

        Assert.Equal(-1, KernelMemoryCompatExports.KernelAprResolveFilepathsToIdsAndFileSizes(context));
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
        Assert.Equal(uint.MaxValue, ReadUInt32(memory, idsAddress));
        Assert.Equal(0ul, ReadUInt64(memory, sizesAddress));
        Assert.Equal(0u, ReadUInt32(memory, errorIndexAddress));
    }

    [Fact]
    public void ResolveFilepathsToIdsAndFileSizes_InvalidErrorIndex_ReturnsMemoryFault()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathListAddress = memoryBase + 0x100;
        const ulong pathAddress = memoryBase + 0x200;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong sizesAddress = memoryBase + 0x880;
        var memory = new FakeCpuMemory(memoryBase, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);
        var missingHostPath = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-apr-missing-{Guid.NewGuid():N}.bin");
        memory.WriteCString(pathAddress, missingHostPath);
        WriteUInt64(memory, pathListAddress, pathAddress);

        context[CpuRegister.Rdi] = pathListAddress;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = idsAddress;
        context[CpuRegister.Rcx] = sizesAddress;
        context[CpuRegister.R8] = memoryBase + 0x5000;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelMemoryCompatExports.KernelAprResolveFilepathsToIdsAndFileSizes(context));
    }

    [Fact]
    public void ResolveFilepathsToIdsAndFileSizes_MissingMidBatch_StopsAtFailingEntry()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathListAddress = memoryBase + 0x100;
        const ulong idsAddress = memoryBase + 0x800;
        const ulong sizesAddress = memoryBase + 0x880;
        const ulong errorIndexAddress = memoryBase + 0x8F0;
        byte[] fileContents = [1, 2, 3, 4, 5];
        // Entries 0 and 2 must resolve to a real file; the kernel FS resolver
        // default-denies raw absolute host paths, so the present file is reached
        // through a registered mount. The missing entry stays an unresolvable
        // path so the batch fails mid-way at index 1.
        var mountRoot = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-apr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mountRoot);
        var mountPoint = $"/sharpemu_apr_mnt_{Guid.NewGuid():N}";
        const string fileName = "asset.bin";
        var hostPath = Path.Combine(mountRoot, fileName);
        var guestPath = $"{mountPoint}/{fileName}";
        var missingGuestPath = $"{mountPoint}/missing-{Guid.NewGuid():N}.bin";

        try
        {
            File.WriteAllBytes(hostPath, fileContents);
            KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, mountRoot);
            var memory = new FakeCpuMemory(memoryBase, 0x4000);
            var context = new CpuContext(memory, Generation.Gen5);
            memory.WriteCString(memoryBase + 0x200, guestPath);
            memory.WriteCString(memoryBase + 0x400, missingGuestPath);
            memory.WriteCString(memoryBase + 0x600, guestPath);
            WriteUInt64(memory, pathListAddress, memoryBase + 0x200);
            WriteUInt64(memory, pathListAddress + 8, memoryBase + 0x400);
            WriteUInt64(memory, pathListAddress + 16, memoryBase + 0x600);
            WriteUInt32(memory, idsAddress + 8, 0x1234_5678);   // sentinel: entry 2 untouched
            WriteUInt64(memory, sizesAddress + 16, 0xDEAD);

            context[CpuRegister.Rdi] = pathListAddress;
            context[CpuRegister.Rsi] = 3;
            context[CpuRegister.Rdx] = idsAddress;
            context[CpuRegister.Rcx] = sizesAddress;
            context[CpuRegister.R8] = errorIndexAddress;

            Assert.Equal(-1, KernelMemoryCompatExports.KernelAprResolveFilepathsToIdsAndFileSizes(context));
            Assert.NotEqual(uint.MaxValue, ReadUInt32(memory, idsAddress));
            Assert.Equal((ulong)fileContents.Length, ReadUInt64(memory, sizesAddress));
            Assert.Equal(uint.MaxValue, ReadUInt32(memory, idsAddress + 4));
            Assert.Equal(0ul, ReadUInt64(memory, sizesAddress + 8));
            Assert.Equal(1u, ReadUInt32(memory, errorIndexAddress));
            Assert.Equal(0x1234_5678u, ReadUInt32(memory, idsAddress + 8));
            Assert.Equal(0xDEADul, ReadUInt64(memory, sizesAddress + 16));
        }
        finally
        {
            KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            if (Directory.Exists(mountRoot))
            {
                Directory.Delete(mountRoot, recursive: true);
            }
        }
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    [Fact]
    public void Register_AlsoPublishesApp0AndDollarPathAliases()
    {
        // Resolve may register "$/asset.bin" while cooked tables look up
        // FNV("/app0/asset.bin"). Both ids must map to the same host file.
        var hostPath = Path.Combine(Path.GetTempPath(), $"sharpemu-apr-alias-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(hostPath, [1, 2, 3]);
        try
        {
            var dollarId = AmprFileRegistry.Register("$/weapons/demo.cani", hostPath);
            Assert.True(AmprFileRegistry.TryGetHostPath(dollarId, out var viaDollar));
            Assert.Equal(hostPath, viaDollar);

            var app0Id = AmprFileRegistry.ComputeFileId("/app0/weapons/demo.cani");
            Assert.NotEqual(dollarId, app0Id);
            Assert.True(AmprFileRegistry.TryGetHostPath(app0Id, out var viaApp0));
            Assert.Equal(hostPath, viaApp0);
        }
        finally
        {
            File.Delete(hostPath);
        }
    }

    [Fact]
    public void EnsureApp0Indexed_PublishesCookedApp0FileIds()
    {
        var mountRoot = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-apr-index-{Guid.NewGuid():N}");
        var relativeDir = Path.Combine(mountRoot, "weapons");
        Directory.CreateDirectory(relativeDir);
        var hostPath = Path.Combine(relativeDir, "demo.cani");
        File.WriteAllBytes(hostPath, [9, 8, 7]);
        try
        {
            AmprFileRegistry.EnsureApp0Indexed(mountRoot);
            var cookedId = AmprFileRegistry.ComputeFileId("/app0/weapons/demo.cani");
            Assert.True(AmprFileRegistry.TryGetHostPath(cookedId, out var resolved));
            Assert.Equal(Path.GetFullPath(hostPath), Path.GetFullPath(resolved));
        }
        finally
        {
            Directory.Delete(mountRoot, recursive: true);
        }
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

}
