// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class SelfLoaderTests
{
    private const uint Ps4SelfMagic = 0x4F153D1D;
    private const uint Ps5SelfMagic = 0x5414F5EE;
    private const int SelfHeaderSize = 0x20;
    private const int SelfSegmentSize = 0x20;
    private const int ElfHeaderSize = 0x40;
    private const int ProgramHeaderSize = 0x38;
    private const ulong SelfSegmentBlocked = 0x800;
    private const ulong SelfSegmentEncrypted = 0x2;
    private const ulong SelfSegmentCompressed = 0x8;

    [Theory]
    [InlineData(Ps4SelfMagic, (byte)0x00, 0x0000_0101u, (ushort)0x22)]
    [InlineData(Ps5SelfMagic, (byte)0x00, 0x0000_0101u, (ushort)0x22)]
    [InlineData(Ps5SelfMagic, (byte)0x10, 0x1000_0101u, (ushort)0x32)]
    public void Load_AcceptsSupportedSelfHeaderVariants(
        uint magic,
        byte version,
        uint keyType,
        ushort flags)
    {
        var imageData = CreateSelfImage(magic, version, keyType, flags);

        var image = new SelfLoader().Load(imageData, new VirtualMemory());

        Assert.True(image.IsSelf);
        Assert.Equal(2, image.ElfHeader.AbiVersion);
        Assert.Empty(image.ProgramHeaders);
        Assert.Empty(image.MappedRegions);
    }

    [Theory]
    [InlineData(0x05, (byte)0x00)]
    [InlineData(0x06, (byte)0x02)]
    [InlineData(0x07, (byte)0x00)]
    public void Load_RejectsUnsupportedStructuralSelfHeaderValues(int offset, byte value)
    {
        var imageData = CreateSelfImage(Ps5SelfMagic, 0x10, 0x1000_0101, 0x32);
        imageData[offset] = value;

        Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(imageData, new VirtualMemory()));
    }

    [Fact]
    public void Load_ResolvesDynamicHeaderInsideBlockedSelfPayload()
    {
        const ulong expectedInitializer = 0x1234_5000;
        var imageData = CreateSelfImageWithNestedDynamicSegment(
            dynamicInsidePayload: true,
            selfSegmentFlags: SelfSegmentBlocked,
            payloadFitsInImage: true,
            expectedInitializer);

        var image = new SelfLoader().Load(imageData, new VirtualMemory());

        Assert.Equal(expectedInitializer, image.InitFunctionEntryPoint);
        Assert.Equal(new[] { expectedInitializer }, image.InitializerFunctions);
    }

    [Fact]
    public void Load_RejectsDynamicHeaderOutsideBlockedSelfPayload()
    {
        var imageData = CreateSelfImageWithNestedDynamicSegment(
            dynamicInsidePayload: false,
            selfSegmentFlags: SelfSegmentBlocked,
            payloadFitsInImage: true,
            initializer: 0);

        var exception = Assert.Throws<NotSupportedException>(() =>
            new SelfLoader().Load(imageData, new VirtualMemory()));

        Assert.Contains("program header 1 could not be resolved", exception.Message);
    }

    [Fact]
    public void Load_DoesNotFallbackAroundUnavailableContainingSelfPayload()
    {
        var imageData = CreateSelfImageWithNestedDynamicSegment(
            dynamicInsidePayload: true,
            selfSegmentFlags: SelfSegmentBlocked,
            payloadFitsInImage: false,
            initializer: 0);

        var exception = Assert.Throws<NotSupportedException>(() =>
            new SelfLoader().Load(imageData, new VirtualMemory()));

        Assert.Contains("outside the available payload", exception.Message);
    }

    [Theory]
    [InlineData(SelfSegmentEncrypted)]
    [InlineData(SelfSegmentCompressed)]
    public void Load_ResolvesDumpedProtectedContainingSelfPayload(ulong protectionFlag)
    {
        const ulong expectedInitializer = 0x1234_5000;
        var imageData = CreateSelfImageWithNestedDynamicSegment(
            dynamicInsidePayload: true,
            selfSegmentFlags: SelfSegmentBlocked | protectionFlag,
            payloadFitsInImage: true,
            expectedInitializer);

        var image = new SelfLoader().Load(imageData, new VirtualMemory());

        Assert.Equal(expectedInitializer, image.InitFunctionEntryPoint);
    }

    [Theory]
    [InlineData(SelfSegmentEncrypted, "encrypted")]
    [InlineData(SelfSegmentCompressed, "compressed")]
    public void Load_DoesNotFallbackAroundProtectedContainingSelfPayload(
        ulong protectionFlag,
        string expectedProtection)
    {
        var imageData = CreateSelfImageWithNestedDynamicSegment(
            dynamicInsidePayload: true,
            selfSegmentFlags: SelfSegmentBlocked | protectionFlag,
            payloadFitsInImage: false,
            initializer: 0);

        var exception = Assert.Throws<NotSupportedException>(() =>
            new SelfLoader().Load(imageData, new VirtualMemory()));

        Assert.Contains(expectedProtection, exception.Message);
        Assert.Contains("decrypted ELF/FSELF", exception.Message);
    }

    [Theory]
    [InlineData(0xDEADBEEF)]
    [InlineData(0x7F454C47)] // bare ELF magic read big-endian as a "SELF" candidate is not a SELF
    public void Load_RejectsUnrecognizedLeadingMagic(uint magic)
    {
        var imageData = new byte[SelfHeaderSize + ElfHeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(imageData, magic);

        Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(imageData, new VirtualMemory()));
    }

    [Fact]
    public void Load_RejectsImageSmallerThanElfHeader()
    {
        // A few bytes short of ElfHeaderSize (0x40); ParseLayout guards this
        // before any magic dispatch, so the error is deterministic for both
        // SELF and ELF inputs.
        var imageData = new byte[ElfHeaderSize - 1];

        Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(imageData, new VirtualMemory()));
    }

    [Fact]
    public void Load_RejectsTruncatedSelfHeader()
    {
        // SELF magic is present and recognized, but the image ends before the
        // SELF header + embedded ELF header can be read. ParseLayout computes
        // elfOffset = SelfHeaderSize + segments*SelfSegmentSize and then
        // EnsureRange must fail.
        var imageData = new byte[SelfHeaderSize + ElfHeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(imageData, Ps5SelfMagic);
        imageData[0x05] = 0x01;
        imageData[0x06] = 0x01;
        imageData[0x07] = 0x12;
        var truncated = imageData.AsSpan(0, SelfHeaderSize + 0x10).ToArray();

        Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(truncated, new VirtualMemory()));
    }

    [Fact]
    public void Load_ParsesEmbeddedElfHeaderFromSelfContainer()
    {
        var imageData = CreateSelfImage(Ps5SelfMagic, 0x10, 0x1000_0101, 0x32);

        var image = new SelfLoader().Load(imageData, new VirtualMemory());

        Assert.True(image.IsSelf);
        // The ELF header parsed out of the SELF container must be a valid x86-64
        // ELF64 little-endian image with the PS5 ABI marker that drives Gen5
        // selection in SharpEmuRuntime.
        Assert.True(image.ElfHeader.HasElfMagic);
        Assert.True(image.ElfHeader.Is64Bit);
        Assert.True(image.ElfHeader.IsLittleEndian);
        Assert.Equal(2, image.ElfHeader.AbiVersion);
        Assert.Equal(62, image.ElfHeader.Machine);
    }

    [Fact]
    public void Load_AcceptsBareDecryptedElf()
    {
        // A decrypted eboot that has already been stripped of its SELF wrapper
        // is accepted directly; IsSelf must be false and the ELF header is read
        // from offset 0.
        var imageData = new byte[ElfHeaderSize];
        WriteMinimalElfHeader(imageData);

        var image = new SelfLoader().Load(imageData, new VirtualMemory());

        Assert.False(image.IsSelf);
        Assert.True(image.ElfHeader.HasElfMagic);
        Assert.True(image.ElfHeader.Is64Bit);
        Assert.Equal(62, image.ElfHeader.Machine);
        Assert.Empty(image.ProgramHeaders);
        Assert.Empty(image.MappedRegions);
    }

    private static byte[] CreateSelfImage(uint magic, byte version, uint keyType, ushort flags)
    {
        var imageData = new byte[SelfHeaderSize + ElfHeaderSize];
        var selfHeader = imageData.AsSpan(0, SelfHeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(selfHeader, magic);
        selfHeader[0x04] = version;
        selfHeader[0x05] = 0x01;
        selfHeader[0x06] = 0x01;
        selfHeader[0x07] = 0x12;
        BinaryPrimitives.WriteUInt32LittleEndian(selfHeader[0x08..], keyType);
        BinaryPrimitives.WriteUInt16LittleEndian(selfHeader[0x0C..], SelfHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(selfHeader[0x10..], (ulong)imageData.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(selfHeader[0x18..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(selfHeader[0x1A..], flags);

        WriteMinimalElfHeader(imageData.AsSpan(SelfHeaderSize, ElfHeaderSize));
        return imageData;
    }

    private static byte[] CreateSelfImageWithNestedDynamicSegment(
        bool dynamicInsidePayload,
        ulong selfSegmentFlags,
        bool payloadFitsInImage,
        ulong initializer)
    {
        const int imageSize = 0x500;
        const int segmentCount = 1;
        const int programHeaderCount = 2;
        const ulong payloadLogicalOffset = 0x200;
        const ulong payloadFileSize = 0x100;
        const ulong mappedPayloadOffset = 0x300;
        const ulong truncatedPayloadOffset = 0x4C0;
        const ulong dynamicOffsetInPayload = 0x40;
        const ulong unmappedDynamicOffset = 0x600;
        const ulong payloadVirtualAddress = 0x0900_0000;
        const ulong dynamicFileSize = 0x20;

        var imageData = new byte[imageSize];
        var selfHeader = imageData.AsSpan(0, SelfHeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(selfHeader, Ps5SelfMagic);
        selfHeader[0x04] = 0x10;
        selfHeader[0x05] = 0x01;
        selfHeader[0x06] = 0x01;
        selfHeader[0x07] = 0x12;
        BinaryPrimitives.WriteUInt32LittleEndian(selfHeader[0x08..], 0x1000_0101);
        BinaryPrimitives.WriteUInt16LittleEndian(selfHeader[0x0C..], SelfHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(selfHeader[0x10..], (ulong)imageData.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(selfHeader[0x18..], segmentCount);
        BinaryPrimitives.WriteUInt16LittleEndian(selfHeader[0x1A..], 0x32);

        var payloadPhysicalOffset = payloadFitsInImage
            ? mappedPayloadOffset
            : truncatedPayloadOffset;
        var selfSegment = imageData.AsSpan(SelfHeaderSize, SelfSegmentSize);
        BinaryPrimitives.WriteUInt64LittleEndian(selfSegment, selfSegmentFlags);
        BinaryPrimitives.WriteUInt64LittleEndian(selfSegment[0x08..], payloadPhysicalOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(
            selfSegment[0x10..],
            payloadFitsInImage ? payloadFileSize : (ulong)(imageSize - (int)truncatedPayloadOffset));
        BinaryPrimitives.WriteUInt64LittleEndian(selfSegment[0x18..], payloadFileSize);

        var elfOffset = SelfHeaderSize + SelfSegmentSize;
        WriteMinimalElfHeader(
            imageData.AsSpan(elfOffset, ElfHeaderSize),
            programHeaderCount);

        var programHeaderTable = imageData.AsSpan(elfOffset + ElfHeaderSize);
        WriteProgramHeader(
            programHeaderTable,
            type: (uint)ProgramHeaderType.SceDynLibData,
            flags: (uint)ProgramHeaderFlags.Read,
            offset: payloadLogicalOffset,
            virtualAddress: payloadVirtualAddress,
            fileSize: payloadFileSize,
            memorySize: payloadFileSize,
            alignment: 0x10);

        var dynamicLogicalOffset = dynamicInsidePayload
            ? payloadLogicalOffset + dynamicOffsetInPayload
            : unmappedDynamicOffset;
        var dynamicVirtualAddress = dynamicInsidePayload
            ? payloadVirtualAddress + dynamicOffsetInPayload
            : payloadVirtualAddress + unmappedDynamicOffset;
        WriteProgramHeader(
            programHeaderTable[ProgramHeaderSize..],
            type: (uint)ProgramHeaderType.Dynamic,
            flags: (uint)ProgramHeaderFlags.Read,
            offset: dynamicLogicalOffset,
            virtualAddress: dynamicVirtualAddress,
            fileSize: dynamicFileSize,
            memorySize: dynamicFileSize,
            alignment: 0x8);

        if (dynamicInsidePayload && payloadFitsInImage)
        {
            var dynamicPhysicalOffset = checked((int)(payloadPhysicalOffset + dynamicOffsetInPayload));
            var dynamicTable = imageData.AsSpan(dynamicPhysicalOffset, (int)dynamicFileSize);
            BinaryPrimitives.WriteInt64LittleEndian(dynamicTable, 0x0C);
            BinaryPrimitives.WriteUInt64LittleEndian(dynamicTable[0x08..], initializer);
            BinaryPrimitives.WriteInt64LittleEndian(dynamicTable[0x10..], 0);
            BinaryPrimitives.WriteUInt64LittleEndian(dynamicTable[0x18..], 0);
        }

        return imageData;
    }

    private static void WriteProgramHeader(
        Span<byte> header,
        uint type,
        uint flags,
        ulong offset,
        ulong virtualAddress,
        ulong fileSize,
        ulong memorySize,
        ulong alignment)
    {
        header = header[..ProgramHeaderSize];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, type);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x04..], flags);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x08..], offset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x10..], virtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x20..], fileSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x28..], memorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x30..], alignment);
    }

    private static void WriteMinimalElfHeader(Span<byte> header, ushort programHeaderCount = 0)
    {
        header.Clear();
        header[0x00] = 0x7F;
        header[0x01] = (byte)'E';
        header[0x02] = (byte)'L';
        header[0x03] = (byte)'F';
        header[0x04] = 2;
        header[0x05] = 1;
        header[0x06] = 1;
        header[0x07] = 9;
        header[0x08] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x10..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x12..], 62);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x14..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x20..], ElfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x34..], ElfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x36..], ProgramHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x38..], programHeaderCount);
    }
}
