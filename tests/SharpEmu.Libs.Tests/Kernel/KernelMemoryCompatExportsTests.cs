// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition(KernelMemoryCompatStateCollection.Name, DisableParallelization = true)]
public sealed class KernelMemoryCompatStateCollection
{
    public const string Name = "KernelMemoryCompatState";
}

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelMemoryCompatExportsTests
{
    private const ulong GuestMemoryBase = 0x1_0000_0000;
    private const ulong AllocationOutAddress = GuestMemoryBase + 0x100;
    private const ulong SpanStartOutAddress = GuestMemoryBase + 0x108;
    private const ulong SpanSizeOutAddress = GuestMemoryBase + 0x110;
    private const ulong DirectQueryInfoAddress = GuestMemoryBase + 0x200;

    [Fact]
    public void PosixStat_MissingFileReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        const ulong statAddress = memoryBase + 0x400;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/__sharpemu_test_missing__/shader.cache");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = statAddress;

        var result = KernelMemoryCompatExports.PosixStat(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixOpen_MissingFileReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/__sharpemu_test_missing__/il2cpp.usym");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = 0; // O_RDONLY

        var result = KernelMemoryCompatExports.PosixOpen(context);

        // A libc open() failure must be -1, not the raw 0x8002xxxx sentinel the
        // guest would otherwise store as a valid fd and later dereference.
        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixFstat_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong statAddress = memoryBase + 0x400;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x80020002; // the not-found sentinel misused as an fd
        context[CpuRegister.Rsi] = statAddress;

        var result = KernelMemoryCompatExports.PosixFstat(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixClose_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x80020002; // never-opened / sentinel fd

        var result = KernelMemoryCompatExports.PosixClose(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixRead_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong bufferAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x80020002; // never-opened / sentinel fd
        context[CpuRegister.Rsi] = bufferAddress;
        context[CpuRegister.Rdx] = 0x40;

        var result = KernelMemoryCompatExports.PosixRead(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixWrite_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong bufferAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(bufferAddress, "payload");
        context[CpuRegister.Rdi] = 0x80020002; // never-opened / sentinel fd
        context[CpuRegister.Rsi] = bufferAddress;
        context[CpuRegister.Rdx] = 0x7;

        var result = KernelMemoryCompatExports.PosixWrite(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Sprintf_ReadsVariadicDoubleFromXmmRegister()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong destinationAddress = memoryBase + 0x100;
        const ulong formatAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(formatAddress, "%.4f");
        context[CpuRegister.Rdi] = destinationAddress;
        context[CpuRegister.Rsi] = formatAddress;
        context.SetXmmRegister(
            0,
            unchecked((ulong)BitConverter.DoubleToInt64Bits(0.5576)),
            0);

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");

            var result = KernelMemoryCompatExports.Sprintf(context);

            Assert.Equal(0, result);
            Assert.Equal(6UL, context[CpuRegister.Rax]);
            Span<byte> output = stackalloc byte[7];
            Assert.True(memory.TryRead(destinationAddress, output));
            Assert.Equal("0.5576\0", Encoding.UTF8.GetString(output));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void Snprintf_WritesToNativeMappedGuestMemory()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong formatAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(formatAddress, "ASTRO");

        context[CpuRegister.Rdi] = 32;
        Assert.Equal(0, KernelMemoryCompatExports.Malloc(context));
        var destination = context[CpuRegister.Rax];
        try
        {
            context[CpuRegister.Rdi] = destination;
            context[CpuRegister.Rsi] = 32;
            context[CpuRegister.Rdx] = formatAddress;

            Assert.Equal(0, KernelMemoryCompatExports.Snprintf(context));
            Assert.Equal(5UL, context[CpuRegister.Rax]);
            Assert.Equal("ASTRO", Marshal.PtrToStringUTF8(unchecked((nint)destination)));
        }
        finally
        {
            context[CpuRegister.Rdi] = destination;
            Assert.Equal(0, KernelMemoryCompatExports.Free(context));
        }
    }

    [Fact]
    public void AvailableDirectMemorySize_FragmentedRangeReturnsLargestAlignedSpan()
    {
        const ulong firstAllocationStart = 0x0020_0000;
        const ulong firstAllocationLength = 0x0020_0000;
        const ulong secondAllocationStart = 0x00C0_0000;
        const ulong secondAllocationLength = 0x0040_0000;
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);

        try
        {
            AllocateDirectMemory(context, firstAllocationStart, firstAllocationLength);
            AllocateDirectMemory(context, secondAllocationStart, secondAllocationLength);

            QueryAvailableDirectMemory(context, 0, 0x0100_0000, 0x4000);

            Assert.True(context.TryReadUInt64(SpanStartOutAddress, out var spanStart));
            Assert.True(context.TryReadUInt64(SpanSizeOutAddress, out var spanSize));
            Assert.Equal(0x0040_0000UL, spanStart);
            Assert.Equal(0x0080_0000UL, spanSize);
        }
        finally
        {
            ReleaseDirectMemory(context, firstAllocationStart, firstAllocationLength);
            ReleaseDirectMemory(context, secondAllocationStart, secondAllocationLength);
        }
    }

    [Fact]
    public void DirectMemoryQuery_FlagOneEnumeratesContainingAndNextAllocations()
    {
        const ulong firstStart = 0x0020_0000;
        const ulong firstLength = 0x0000_8000;
        const int firstMemoryType = 0x0C;
        const ulong secondStart = 0x0040_0000;
        const ulong secondLength = 0x0000_C000;
        const int secondMemoryType = 0x0F;
        var memory = new FakeCpuMemory(GuestMemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        try
        {
            AllocateDirectMemory(context, firstStart, firstLength, firstMemoryType);
            AllocateDirectMemory(context, secondStart, secondLength, secondMemoryType);

            Assert.Equal(0, QueryDirectMemory(context, firstStart + 0x4000, flags: 1));
            AssertDirectMemoryInfo(memory, firstStart, firstStart + firstLength, firstMemoryType);

            Assert.Equal(0, QueryDirectMemory(context, firstStart + firstLength, flags: 1));
            AssertDirectMemoryInfo(memory, secondStart, secondStart + secondLength, secondMemoryType);
        }
        finally
        {
            ReleaseDirectMemory(context, firstStart, firstLength);
            ReleaseDirectMemory(context, secondStart, secondLength);
        }
    }

    [Fact]
    public void DirectMemoryQuery_FlagOneReturnsEaccesAtEndWithoutTouchingInfo()
    {
        var memory = new FakeCpuMemory(GuestMemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var sentinel = new byte[24];
        Array.Fill(sentinel, (byte)0xA5);
        Assert.True(memory.TryWrite(DirectQueryInfoAddress, sentinel));

        Assert.Equal(
            unchecked((int)0x8002000D),
            QueryDirectMemory(context, offset: 0, flags: 1));

        Span<byte> actual = stackalloc byte[24];
        Assert.True(memory.TryRead(DirectQueryInfoAddress, actual));
        Assert.Equal(sentinel, actual.ToArray());
    }

    [Fact]
    public void DirectMemoryQuery_ValidatesInfoAndReportsWriteFault()
    {
        const ulong allocationStart = 0x0060_0000;
        const ulong allocationLength = 0x0000_4000;
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            QueryDirectMemory(context, 0, flags: 1, infoAddress: 0));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            QueryDirectMemory(context, 0, flags: 1, infoSize: 23));

        try
        {
            AllocateDirectMemory(context, allocationStart, allocationLength);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                QueryDirectMemory(
                    context,
                    allocationStart,
                    flags: 1,
                    infoAddress: GuestMemoryBase + 0x1000));
        }
        finally
        {
            ReleaseDirectMemory(context, allocationStart, allocationLength);
        }
    }

    [Fact]
    public void Memalign_SmallBlockReservesNativeAllocatorSpanHeader()
    {
        var context = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = 4;
        context[CpuRegister.Rsi] = 64;

        Assert.Equal(0, KernelMemoryCompatExports.Memalign(context));
        var allocation = context[CpuRegister.Rax];
        Assert.NotEqual(0UL, allocation);
        try
        {
            Assert.Equal(0x10UL, allocation & 0xFFFF);
            Assert.Equal(0, Marshal.ReadInt64(unchecked((nint)(allocation - 0x10))));
            Marshal.WriteInt64(unchecked((nint)allocation), 0x1234_5678_9ABC_DEF0);
            Assert.Equal(
                unchecked((long)0x1234_5678_9ABC_DEF0),
                Marshal.ReadInt64(unchecked((nint)allocation)));
        }
        finally
        {
            context[CpuRegister.Rdi] = allocation;
            Assert.Equal(0, KernelMemoryCompatExports.Free(context));
        }
    }

    [Fact]
    public void MainDirectMemoryAndMap_AcceptLibcBackedInOutPointers()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong length = 0x4000;
        var context = new CpuContext(new FakeCpuMemory(memoryBase, (int)length), Generation.Gen5);
        var directAddressOut = AllocateTracked(context, sizeof(ulong));
        var mappedAddressInOut = AllocateTracked(context, sizeof(ulong));
        var directAddress = ulong.MaxValue;
        var mapped = false;
        try
        {
            Marshal.WriteInt64(unchecked((nint)directAddressOut), -1);
            context[CpuRegister.Rdi] = length;
            context[CpuRegister.Rsi] = length;
            context[CpuRegister.Rdx] = 0x0C;
            context[CpuRegister.Rcx] = directAddressOut;

            Assert.Equal(0, KernelMemoryCompatExports.KernelAllocateMainDirectMemory(context));
            directAddress = unchecked((ulong)Marshal.ReadInt64(unchecked((nint)directAddressOut)));
            Assert.NotEqual(ulong.MaxValue, directAddress);
            Assert.Equal(0UL, directAddress & (length - 1));

            Marshal.WriteInt64(unchecked((nint)mappedAddressInOut), unchecked((long)memoryBase));
            context[CpuRegister.Rdi] = mappedAddressInOut;
            context[CpuRegister.Rsi] = length;
            context[CpuRegister.Rdx] = 0xF2;
            context[CpuRegister.Rcx] = 0;
            context[CpuRegister.R8] = directAddress;
            context[CpuRegister.R9] = length;

            Assert.Equal(0, KernelMemoryCompatExports.KernelMapDirectMemory(context));
            Assert.Equal(
                memoryBase,
                unchecked((ulong)Marshal.ReadInt64(unchecked((nint)mappedAddressInOut))));
            mapped = true;
        }
        finally
        {
            if (mapped)
            {
                context[CpuRegister.Rdi] = memoryBase;
                context[CpuRegister.Rsi] = length;
                Assert.Equal(0, KernelMemoryCompatExports.KernelMunmap(context));
            }

            if (directAddress != ulong.MaxValue)
            {
                context[CpuRegister.Rdi] = directAddress;
                context[CpuRegister.Rsi] = length;
                Assert.Equal(0, KernelMemoryCompatExports.KernelReleaseDirectMemory(context));
            }

            FreeTracked(context, mappedAddressInOut);
            FreeTracked(context, directAddressOut);
        }
    }

    [Fact]
    public void BasicLibcCompatExports_RegisterByKnownNids()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        AssertExport(manager, "5TjaJwkLWxE", "bcmp");
        AssertExport(manager, "AEJdIVZTEmo", "qsort");
        AssertExport(manager, "1Pk0qZQGeWo", "sscanf");
        AssertExport(manager, "pXvbDfchu6k", "strncasecmp");
        AssertExport(manager, "g7zzzLDYGw0", "strdup");
        AssertExport(manager, "YQ0navp+YIc", "puts");
        AssertExport(manager, "8vE6Z6VEYyk", "access");
    }

    [Fact]
    public void BcmpAndStrncasecmp_CompareGuestMemory()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong leftAddress = memoryBase + 0x100;
        const ulong rightAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.True(memory.TryWrite(leftAddress, new byte[] { 1, 2, 3 }));
        Assert.True(memory.TryWrite(rightAddress, new byte[] { 1, 2, 4 }));
        context[CpuRegister.Rdi] = leftAddress;
        context[CpuRegister.Rsi] = rightAddress;
        context[CpuRegister.Rdx] = 3;
        Assert.Equal(0, KernelMemoryCompatExports.Bcmp(context));
        Assert.NotEqual(0UL, context[CpuRegister.Rax]);

        memory.WriteCString(leftAddress, "AbCd");
        memory.WriteCString(rightAddress, "aBcX");
        context[CpuRegister.Rdx] = 3;
        Assert.Equal(0, KernelMemoryCompatExports.Strncasecmp(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        context[CpuRegister.Rdx] = 4;
        Assert.Equal(0, KernelMemoryCompatExports.Strncasecmp(context));
        Assert.NotEqual(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Qsort_InvokesGuestComparatorAndSortsElements()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong arrayAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var values = new ulong[] { 40, 10, 30, 20 };
        for (var index = 0; index < values.Length; index++)
        {
            Assert.True(context.TryWriteUInt64(arrayAddress + ((ulong)index * sizeof(ulong)), values[index]));
        }

        context[CpuRegister.Rdi] = arrayAddress;
        context[CpuRegister.Rsi] = (ulong)values.Length;
        context[CpuRegister.Rdx] = sizeof(ulong);
        context[CpuRegister.Rcx] = 0x1234_5678;

        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = new QsortTestScheduler();
        try
        {
            Assert.Equal(0, KernelMemoryCompatExports.Qsort(context));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }

        for (var index = 0; index < values.Length; index++)
        {
            Assert.True(context.TryReadUInt64(arrayAddress + ((ulong)index * sizeof(ulong)), out var value));
            Assert.Equal((ulong)((index + 1) * 10), value);
        }
    }

    [Fact]
    public void Sscanf_ParsesShellCoreFloatScansetStringAndHexFormats()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong inputAddress = memoryBase + 0x100;
        const ulong formatAddress = memoryBase + 0x300;
        const ulong firstAddress = memoryBase + 0x500;
        const ulong secondAddress = memoryBase + 0x510;
        const ulong thirdAddress = memoryBase + 0x520;
        const ulong fourthAddress = memoryBase + 0x530;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        memory.WriteCString(inputAddress, "10%, 20%, 30%, 40");
        memory.WriteCString(
            formatAddress,
            "%f%*[%%, \t]%f%*[%%, \t]%f%*[%%, \t]%f");
        context[CpuRegister.Rdi] = inputAddress;
        context[CpuRegister.Rsi] = formatAddress;
        context[CpuRegister.Rdx] = firstAddress;
        context[CpuRegister.Rcx] = secondAddress;
        context[CpuRegister.R8] = thirdAddress;
        context[CpuRegister.R9] = fourthAddress;

        Assert.Equal(0, KernelMemoryCompatExports.Sscanf(context));
        Assert.Equal(4UL, context[CpuRegister.Rax]);
        Assert.Equal(10f, ReadSingle(memory, firstAddress));
        Assert.Equal(20f, ReadSingle(memory, secondAddress));
        Assert.Equal(30f, ReadSingle(memory, thirdAddress));
        Assert.Equal(40f, ReadSingle(memory, fourthAddress));

        memory.WriteCString(inputAddress, "12.5 label");
        memory.WriteCString(formatAddress, "%f%31s");
        context[CpuRegister.Rdx] = firstAddress;
        context[CpuRegister.Rcx] = secondAddress;
        Assert.Equal(0, KernelMemoryCompatExports.Sscanf(context));
        Assert.Equal(2UL, context[CpuRegister.Rax]);
        Assert.Equal(12.5f, ReadSingle(memory, firstAddress));
        Span<byte> text = stackalloc byte[6];
        Assert.True(memory.TryRead(secondAddress, text));
        Assert.Equal("label\0", Encoding.UTF8.GetString(text));

        memory.WriteCString(inputAddress, "ff");
        memory.WriteCString(formatAddress, "%x");
        context[CpuRegister.Rdx] = firstAddress;
        Assert.Equal(0, KernelMemoryCompatExports.Sscanf(context));
        Assert.Equal(1UL, context[CpuRegister.Rax]);
        Span<byte> hex = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(firstAddress, hex));
        Assert.Equal(0xFFu, BitConverter.ToUInt32(hex));
    }

    [Fact]
    public void Sscanf_ParsesFirmwareDecimalIntoDwordWithoutClobberingAdjacentData()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong inputAddress = memoryBase + 0x100;
        const ulong formatAddress = memoryBase + 0x300;
        const ulong outputAddress = memoryBase + 0x500;
        const ulong canary = 0xC0DEC0DECAFEBA00;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        memory.WriteCString(inputAddress, "23");
        memory.WriteCString(formatAddress, "%d");
        Span<byte> outputAndCanary = stackalloc byte[sizeof(uint) + sizeof(ulong)];
        outputAndCanary.Fill(0xA5);
        BinaryPrimitives.WriteUInt64LittleEndian(outputAndCanary[sizeof(uint)..], canary);
        Assert.True(memory.TryWrite(outputAddress, outputAndCanary));

        context[CpuRegister.Rdi] = inputAddress;
        context[CpuRegister.Rsi] = formatAddress;
        context[CpuRegister.Rdx] = outputAddress;

        Assert.Equal(0, KernelMemoryCompatExports.Sscanf(context));
        Assert.Equal(1UL, context[CpuRegister.Rax]);
        Assert.True(memory.TryRead(outputAddress, outputAndCanary));
        Assert.Equal(23U, BinaryPrimitives.ReadUInt32LittleEndian(outputAndCanary));
        Assert.Equal(canary, BinaryPrimitives.ReadUInt64LittleEndian(outputAndCanary[sizeof(uint)..]));
    }

    [Fact]
    public void Strdup_CopiesCStringIntoTrackedLibcHeap()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong sourceAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        memory.WriteCString(sourceAddress, "ShellCore");
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = sourceAddress;

        Assert.Equal(0, KernelMemoryCompatExports.Strdup(context));
        var duplicate = context[CpuRegister.Rax];
        Assert.NotEqual(0UL, duplicate);
        try
        {
            Assert.Equal("ShellCore", Marshal.PtrToStringUTF8(unchecked((nint)duplicate)));
        }
        finally
        {
            context[CpuRegister.Rdi] = duplicate;
            Assert.Equal(0, KernelMemoryCompatExports.Free(context));
        }
    }

    [Fact]
    public void Access_ReturnsPosixResultForExistingAndMissingPaths()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x2000);
        var context = new CpuContext(memory, Generation.Gen5);
        var root = Directory.CreateTempSubdirectory("sharpemu-access-");
        var existingPath = Path.Combine(root.FullName, "present.bin");
        var mountPoint = $"/sharpemu_access_{Guid.NewGuid():N}";
        try
        {
            File.WriteAllBytes(existingPath, [1]);
            KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, root.FullName);
            memory.WriteCString(pathAddress, $"{mountPoint}/present.bin");
            context[CpuRegister.Rdi] = pathAddress;
            context[CpuRegister.Rsi] = 0;
            Assert.Equal(0, KernelMemoryCompatExports.PosixAccess(context));
            Assert.Equal(0UL, context[CpuRegister.Rax]);

            memory.WriteCString(pathAddress, $"{mountPoint}/missing.bin");
            Assert.Equal(-1, KernelMemoryCompatExports.PosixAccess(context));
            Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
        }
        finally
        {
            KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            root.Delete(recursive: true);
        }
    }

    private static void AssertExport(ModuleManager manager, string nid, string name)
    {
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
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

    private static float ReadSingle(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float)];
        Assert.True(memory.TryRead(address, bytes));
        return BitConverter.ToSingle(bytes);
    }

    private sealed class QsortTestScheduler : IGuestThreadScheduler
    {
        public bool SupportsGuestContextTransfer => false;

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context)
        {
        }

        public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error)
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

        public bool TrySuspendGuestThread(ulong guestThreadHandle, out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryResumeGuestThread(ulong guestThreadHandle, out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryGetSuspendedGuestThreadContext(
            ulong guestThreadHandle,
            out GuestCpuContinuation continuation,
            out string? error)
        {
            continuation = default;
            error = "not supported";
            return false;
        }

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => Array.Empty<GuestThreadSnapshot>();

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
            var result = TryCallGuestFunction(
                callerContext,
                entryPoint,
                arg0,
                arg1,
                0,
                stackAddress,
                stackSize,
                reason,
                out _,
                out error);
            return result;
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
            if (!callerContext.TryReadUInt64(arg0, out var left) ||
                !callerContext.TryReadUInt64(arg1, out var right))
            {
                returnValue = 0;
                error = "unreadable comparator argument";
                return false;
            }

            returnValue = unchecked((uint)left.CompareTo(right));
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

    [Fact]
    public void AvailableDirectMemorySize_AppliesAlignmentBeforeComparingSpans()
    {
        const ulong allocationStart = 0x0070_0000;
        const ulong allocationLength = 0x0010_0000;
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);

        try
        {
            AllocateDirectMemory(context, allocationStart, allocationLength);

            QueryAvailableDirectMemory(context, 0x0010_0000, 0x00C0_0000, 0x0040_0000);

            Assert.True(context.TryReadUInt64(SpanStartOutAddress, out var spanStart));
            Assert.True(context.TryReadUInt64(SpanSizeOutAddress, out var spanSize));
            Assert.Equal(0x0080_0000UL, spanStart);
            Assert.Equal(0x0040_0000UL, spanSize);
        }
        finally
        {
            ReleaseDirectMemory(context, allocationStart, allocationLength);
        }
    }

    private static void AllocateDirectMemory(CpuContext context, ulong start, ulong length, int memoryType = 0)
    {
        context[CpuRegister.Rdi] = start;
        context[CpuRegister.Rsi] = start + length;
        context[CpuRegister.Rdx] = length;
        context[CpuRegister.Rcx] = 0x4000;
        context[CpuRegister.R8] = unchecked((ulong)memoryType);
        context[CpuRegister.R9] = AllocationOutAddress;

        Assert.Equal(0, KernelMemoryCompatExports.KernelAllocateDirectMemory(context));
        Assert.True(context.TryReadUInt64(AllocationOutAddress, out var allocatedAddress));
        Assert.Equal(start, allocatedAddress);
    }

    private static int QueryDirectMemory(
        CpuContext context,
        ulong offset,
        ulong flags,
        ulong infoAddress = DirectQueryInfoAddress,
        ulong infoSize = 24)
    {
        context[CpuRegister.Rdi] = offset;
        context[CpuRegister.Rsi] = flags;
        context[CpuRegister.Rdx] = infoAddress;
        context[CpuRegister.Rcx] = infoSize;

        return KernelMemoryCompatExports.KernelDirectMemoryQuery(context);
    }

    private static void AssertDirectMemoryInfo(
        FakeCpuMemory memory,
        ulong expectedStart,
        ulong expectedEnd,
        int expectedMemoryType)
    {
        Span<byte> info = stackalloc byte[(sizeof(ulong) * 2) + sizeof(int)];
        Assert.True(memory.TryRead(DirectQueryInfoAddress, info));

        Assert.Equal(expectedStart, BitConverter.ToUInt64(info[..sizeof(ulong)]));
        Assert.Equal(expectedEnd, BitConverter.ToUInt64(info.Slice(sizeof(ulong), sizeof(ulong))));
        Assert.Equal(expectedMemoryType, BitConverter.ToInt32(info[(sizeof(ulong) * 2)..]));
    }

    private static void QueryAvailableDirectMemory(
        CpuContext context,
        ulong searchStart,
        ulong searchEnd,
        ulong alignment)
    {
        context[CpuRegister.Rdi] = searchStart;
        context[CpuRegister.Rsi] = searchEnd;
        context[CpuRegister.Rdx] = alignment;
        context[CpuRegister.Rcx] = SpanStartOutAddress;
        context[CpuRegister.R8] = SpanSizeOutAddress;

        Assert.Equal(0, KernelMemoryCompatExports.KernelAvailableDirectMemorySize(context));
    }

    private static void ReleaseDirectMemory(CpuContext context, ulong start, ulong length)
    {
        context[CpuRegister.Rdi] = start;
        context[CpuRegister.Rsi] = length;

        Assert.Equal(0, KernelMemoryCompatExports.KernelReleaseDirectMemory(context));
    }

    [Fact]
    public void MapNamedFlexibleMemory_NullInOutPointerReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x1000;
        context[CpuRegister.Rdx] = 0x03; // CPU read|write
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void MapNamedFlexibleMemory_ZeroLengthReturnsInvalidArgument()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong inOutAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.TryWrite(inOutAddress, BitConverter.GetBytes(0UL));
        context[CpuRegister.Rdi] = inOutAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void MapNamedFlexibleMemory_UnreadableInOutPointerReturnsMemoryFault()
    {
        // The in-out pointer points outside the FakeCpuMemory backing store, so
        // the first TryReadUInt64 must fail before any reservation is attempted.
        const ulong memoryBase = 0x1_0000_0000;
        const ulong unreachableInOut = memoryBase + 0x10_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unreachableInOut;
        context[CpuRegister.Rsi] = 0x1000;
        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
    }

    [Fact]
    public void VirtualQuery_PreservesReservationPastFixedCommitAtSameBase()
    {
        const ulong memoryBase = 0x12_0000_0000;
        const ulong reservedLength = 0x1_0000;
        const ulong committedLength = 0x2000;
        const ulong inOutAddress = memoryBase + 0x1_7000;
        const ulong infoAddress = memoryBase + 0x1_8000;
        var memory = new FakeCpuMemory(memoryBase, 0x2_0000);
        var context = new CpuContext(memory, Generation.Gen5);

        KernelMemoryCompatExports.RegisterReservedVirtualRange(memoryBase, reservedLength);
        Assert.True(memory.TryWrite(inOutAddress, BitConverter.GetBytes(memoryBase)));
        context[CpuRegister.Rdi] = inOutAddress;
        context[CpuRegister.Rsi] = committedLength;
        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rcx] = 0x10; // fixed mapping

        Assert.Equal(0, KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context));

        context[CpuRegister.Rdi] = memoryBase + 0x8000;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = infoAddress;
        context[CpuRegister.Rcx] = 0x48;

        Assert.Equal(0, KernelMemoryCompatExports.KernelVirtualQuery(context));
        Assert.True(context.TryReadUInt64(infoAddress, out var regionStart));
        Assert.True(context.TryReadUInt64(infoAddress + 8, out var regionEnd));
        Assert.Equal(memoryBase + committedLength, regionStart);
        Assert.Equal(memoryBase + reservedLength, regionEnd);
    }

    [Fact]
    public void Mprotect_ZeroAddressReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 0x03;

        var result = KernelMemoryCompatExports.KernelMprotect(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Mprotect_ZeroLengthReturnsInvalidArgument()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = memoryBase;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0x03;

        var result = KernelMemoryCompatExports.KernelMprotect(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Mprotect_UnmappedRangeReturnsNotFound()
    {
        // A plausible guest address that FakeCpuMemory does not back and that
        // has no host reservation. TryProtectHostRange calls VirtualProtect,
        // which fails on an unmapped range, yielding NOT_FOUND rather than
        // mutating protection or throwing.
        const ulong unmappedAddress = 0x2_0000_0000;
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unmappedAddress;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 0x03;

        var result = KernelMemoryCompatExports.KernelMprotect(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
    }

    [Fact]
    public void Munmap_ZeroAddressReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x4000;

        var result = KernelMemoryCompatExports.KernelMunmap(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Munmap_OverflowRangeReturnsInvalidArgument()
    {
        // address + length would overflow; KernelMunmap guards this explicitly
        // before touching any region accounting.
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue - 0x10;
        context[CpuRegister.Rsi] = 0x20;

        var result = KernelMemoryCompatExports.KernelMunmap(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Munmap_UnmappedRangeReturnsNotFound()
    {
        // No flexible region is registered at this address and FakeCpuMemory
        // does not back it, so both physicallyBacked and removedRegions are
        // empty and the export reports NOT_FOUND.
        const ulong unmappedAddress = 0x2_0000_0000;
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unmappedAddress;
        context[CpuRegister.Rsi] = 0x4000;

        var result = KernelMemoryCompatExports.KernelMunmap(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
    }
}
