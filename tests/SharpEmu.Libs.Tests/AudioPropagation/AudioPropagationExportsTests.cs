// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.AudioPropagation;
using Xunit;

namespace SharpEmu.Libs.Tests.AudioPropagation;

public sealed class AudioPropagationExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong ConfigAddress = Base + 0x0100;
    private const ulong MemoryInfoAddress = Base + 0x0200;
    private const ulong SystemOutputAddress = Base + 0x0300;
    private const ulong PrimaryBackingAddress = Base + 0x1000;
    private const ulong SecondaryBackingAddress = Base + 0x2000;
    private const ulong RaysAddress = Base + 0x4000;
    private const ulong RayCountAddress = Base + 0x6000;
    private const ulong MaterialAddress = Base + 0x7000;
    private const ulong MaterialOutputAddress = Base + 0x7100;
    private const ulong AttributesAddress = Base + 0x8000;
    private const ulong AttributePayloadAddress = Base + 0x8100;
    private const ulong RoomOutputAddress = Base + 0x9000;

    private const int ErrorInvalidValue = unchecked((int)0x8A700001);
    private const int ErrorInvalidHandle = unchecked((int)0x8A700002);
    private const int ErrorInvalidPointer = unchecked((int)0x8A700003);
    private const int ErrorInsufficientMemory = unchecked((int)0x8A700004);
    private const int ErrorResourceExhausted = unchecked((int)0x8A700006);
    private const int ErrorInvalidStructure = unchecked((int)0x8A700007);

    private const uint ConfigTag = 0x010107D5;
    private const uint MemoryInfoTag = 0x010107D4;
    private const uint MaterialTag = 0x010107D1;
    private const uint RayTag = 0x010107D7;
    private const int RaySize = 0x58;
    private const int RayCapacity = 64;

    private readonly FakeCpuMemory _memory = new(Base, 0x40_0000);
    private readonly CpuContext _ctx;

    public AudioPropagationExportsTests()
    {
        AudioPropagationExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
        WriteValidConfig();
        WriteMemoryInfoHeader();
    }

    public static TheoryData<string, string> AudioPropagationExportIdentities => new()
    {
        { "7xyAxrusLko", "sceAudioPropagationSystemQueryMemory" },
        { "aNEqtSHdUSo", "sceAudioPropagationSystemCreate" },
        { "ht-QXT3zGxo", "sceAudioPropagationSystemGetRays" },
        { "VlBT16890mA", "sceAudioPropagationSystemSetRays" },
        { "kIdb+iQUzCs", "sceAudioPropagationSystemSetAttributes" },
        { "CPLV6G-eXmk", "sceAudioPropagationSystemRegisterMaterial" },
        { "8bI5h8req30", "sceAudioPropagationRoomCreate" },
    };

    [Theory]
    [MemberData(nameof(AudioPropagationExportIdentities))]
    public void NidsRegisterWithExactIdentity(string nid, string exportName)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(exportName, export.Name);
        Assert.Equal("libSceAudioPropagation", export.LibraryName);
    }

    [Fact]
    public void SetRaysHasOneGen5Registration()
    {
        var registrations = typeof(AudioPropagationExports)
            .GetMethods()
            .SelectMany(method => method.GetCustomAttributes(inherit: false))
            .OfType<SysAbiExportAttribute>()
            .Where(attribute => attribute.Nid == "VlBT16890mA")
            .ToArray();

        var registration = Assert.Single(registrations);
        Assert.Equal("sceAudioPropagationSystemSetRays", registration.ExportName);
        Assert.Equal("libSceAudioPropagation", registration.LibraryName);
        Assert.Equal(Generation.Gen5, registration.Target);
    }

    [Fact]
    public void QueryMemoryPreservesInvalidMemoryInfoAndZeroesBeforeConfigValidation()
    {
        Fill(MemoryInfoAddress + 0x10, 0x20, 0xA5);
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = MemoryInfoAddress;
        Assert.Equal(ErrorInvalidPointer, AudioPropagationExports.SystemQueryMemory(_ctx));
        Assert.Equal(Enumerable.Repeat((byte)0xA5, 0x20), Read(MemoryInfoAddress + 0x10, 0x20));

        Fill(MemoryInfoAddress + 0x10, 0x20, 0xA5);
        WriteUInt32(MemoryInfoAddress, 0xDEADBEEF);
        _ctx[CpuRegister.Rdi] = ConfigAddress;
        Assert.Equal(ErrorInvalidStructure, AudioPropagationExports.SystemQueryMemory(_ctx));
        Assert.Equal(Enumerable.Repeat((byte)0xA5, 0x20), Read(MemoryInfoAddress + 0x10, 0x20));

        WriteMemoryInfoHeader();
        Fill(MemoryInfoAddress + 0x10, 0x20, 0xA5);
        WriteUInt32(ConfigAddress, 0xDEADBEEF);
        Assert.Equal(ErrorInvalidStructure, AudioPropagationExports.SystemQueryMemory(_ctx));
        Assert.Equal(new byte[0x20], Read(MemoryInfoAddress + 0x10, 0x20));

        Fill(MemoryInfoAddress + 0x10, 0x20, 0xA5);
        WriteValidConfig();
        WriteUInt32(ConfigAddress + 0x18, 5);
        Assert.Equal(ErrorInvalidValue, AudioPropagationExports.SystemQueryMemory(_ctx));
        Assert.Equal(new byte[0x20], Read(MemoryInfoAddress + 0x10, 0x20));

        Fill(MemoryInfoAddress + 0x10, 0x20, 0xA5);
        WriteValidConfig();
        WriteUInt64(ConfigAddress + 0x08, 0x30);
        Assert.Equal(ErrorInvalidStructure, AudioPropagationExports.SystemQueryMemory(_ctx));
        Assert.Equal(new byte[0x20], Read(MemoryInfoAddress + 0x10, 0x20));

        Fill(MemoryInfoAddress + 0x10, 0x20, 0xA5);
        _ctx[CpuRegister.Rdi] = Base + 0x50_0000;
        Assert.Equal(ErrorInvalidPointer, AudioPropagationExports.SystemQueryMemory(_ctx));
        Assert.Equal(new byte[0x20], Read(MemoryInfoAddress + 0x10, 0x20));
    }

    [Fact]
    public void QueryMemoryWritesRecoveredLayoutAndSecondaryRequirement()
    {
        Assert.Equal(0, QueryMemory());
        Assert.Equal(0UL, ReadUInt64(MemoryInfoAddress + 0x10));
        Assert.Equal(0x72550UL, ReadUInt64(MemoryInfoAddress + 0x18));
        Assert.Equal(0UL, ReadUInt64(MemoryInfoAddress + 0x20));
        Assert.Equal(0UL, ReadUInt64(MemoryInfoAddress + 0x28));

        WriteValidConfig(flags: 1, transformSize: 0x200, sourceCapacity: 2);
        Assert.Equal(0, QueryMemory());
        Assert.Equal(2UL * 0x90000UL, ReadUInt64(MemoryInfoAddress + 0x28));
    }

    [Fact]
    public void SystemCreateValidatesOrderingCapacityAndWritesOnlyOnSuccess()
    {
        const ulong Sentinel = 0xBADC0FFEE0DDF00D;
        WriteUInt64(SystemOutputAddress, Sentinel);
        WriteUInt64(MemoryInfoAddress + 0x10, 0);
        WriteUInt32(MemoryInfoAddress, 0xDEADBEEF);
        PrepareCreateRegisters();
        Assert.Equal(ErrorInvalidPointer, AudioPropagationExports.SystemCreate(_ctx));
        Assert.Equal(Sentinel, ReadUInt64(SystemOutputAddress));

        WriteMemoryInfoHeader();
        WriteUInt64(MemoryInfoAddress + 0x10, PrimaryBackingAddress);
        WriteUInt32(MemoryInfoAddress, 0xDEADBEEF);
        Assert.Equal(ErrorInvalidStructure, AudioPropagationExports.SystemCreate(_ctx));
        Assert.Equal(Sentinel, ReadUInt64(SystemOutputAddress));

        WriteMemoryInfoHeader();
        WriteUInt64(MemoryInfoAddress + 0x10, PrimaryBackingAddress);
        WriteUInt64(MemoryInfoAddress + 0x18, 1);
        Assert.Equal(ErrorInsufficientMemory, AudioPropagationExports.SystemCreate(_ctx));
        Assert.Equal(Sentinel, ReadUInt64(SystemOutputAddress));

        var system = CreateSystem();
        Assert.NotEqual(0UL, system);
        Assert.NotEqual(Sentinel, system);
        Assert.Equal(0x5348_4150_5359_5331UL, ReadUInt64(PrimaryBackingAddress));
        Assert.True(AudioPropagationExports.TryGetDebugSnapshot(system, out var snapshot));
        Assert.Equal(0, snapshot.MaterialCount);
        Assert.Equal(0, snapshot.RoomCount);
    }

    [Fact]
    public void StatefulExportsAcceptDistinctMemoryFacadeForSameGuestAddressSpace()
    {
        var system = CreateSystem(materialCapacity: 2, flags: 4);
        var material = RegisterMaterial(system);
        var facadeContext = new CpuContext(
            new ForwardingCpuMemory(_memory),
            Generation.Gen5);

        facadeContext[CpuRegister.Rdi] = system;
        facadeContext[CpuRegister.Rsi] = RoomOutputAddress;
        Assert.Equal(0, AudioPropagationExports.RoomCreate(facadeContext));
        var room = ReadUInt64(RoomOutputAddress);

        WriteAttribute(0x20000, AttributePayloadAddress, 8);
        WriteUInt64(AttributePayloadAddress, material);
        facadeContext[CpuRegister.Rsi] = AttributesAddress;
        facadeContext[CpuRegister.Rdx] = 1;
        Assert.Equal(0, AudioPropagationExports.SystemSetAttributes(facadeContext));

        InitializeRayBuffer();
        WriteUInt32(RayCountAddress, RayCapacity);
        facadeContext[CpuRegister.Rsi] = RaysAddress;
        facadeContext[CpuRegister.Rdx] = RayCountAddress;
        Assert.Equal(0, AudioPropagationExports.SystemGetRays(facadeContext));
        Assert.Equal(1U, ReadUInt32(RayCountAddress));
        Assert.Equal(room, ReadUInt64(RaysAddress + 0x10));
        Assert.Equal(material, ReadUInt64(RaysAddress + 0x18));

        var raysBeforeSet = Read(RaysAddress, RayCapacity * RaySize);
        facadeContext[CpuRegister.Rdx] = ReadUInt32(RayCountAddress);
        Assert.Equal(0, AudioPropagationExports.SystemSetRays(facadeContext));
        Assert.Equal(0UL, facadeContext[CpuRegister.Rax]);
        Assert.Equal(raysBeforeSet, Read(RaysAddress, RayCapacity * RaySize));
    }

    [Fact]
    public void RegisterMaterialValidatesHeaderMutatesStateAndExhaustsConfiguredPool()
    {
        var system = CreateSystem(materialCapacity: 2);
        WriteMaterialDescriptor();
        _ctx[CpuRegister.Rdi] = system;
        _ctx[CpuRegister.Rsi] = MaterialAddress;
        _ctx[CpuRegister.Rdx] = MaterialOutputAddress;

        WriteUInt64(MaterialAddress + 0x08, 0x38);
        WriteUInt64(MaterialOutputAddress, 0x1111);
        Assert.Equal(ErrorInvalidStructure, AudioPropagationExports.SystemRegisterMaterial(_ctx));
        Assert.Equal(0x1111UL, ReadUInt64(MaterialOutputAddress));

        WriteMaterialDescriptor();
        Assert.Equal(0, AudioPropagationExports.SystemRegisterMaterial(_ctx));
        var first = ReadUInt64(MaterialOutputAddress);
        Assert.NotEqual(0UL, first);
        Assert.Equal(0, AudioPropagationExports.SystemRegisterMaterial(_ctx));
        var second = ReadUInt64(MaterialOutputAddress);
        Assert.NotEqual(first, second);

        WriteUInt64(MaterialOutputAddress, 0x2222);
        Assert.Equal(ErrorResourceExhausted, AudioPropagationExports.SystemRegisterMaterial(_ctx));
        Assert.Equal(0x2222UL, ReadUInt64(MaterialOutputAddress));

        Assert.True(AudioPropagationExports.TryGetDebugSnapshot(system, out var snapshot));
        Assert.Equal(2, snapshot.MaterialCount);
    }

    [Fact]
    public void StatefulExportsRejectInvalidHandleBeforePointerValidation()
    {
        _ctx[CpuRegister.Rdi] = 0xDEADBEEF;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;

        Assert.Equal(ErrorInvalidHandle, AudioPropagationExports.RoomCreate(_ctx));
        Assert.Equal(ErrorInvalidHandle, AudioPropagationExports.SystemRegisterMaterial(_ctx));
        Assert.Equal(ErrorInvalidHandle, AudioPropagationExports.SystemGetRays(_ctx));
        Assert.Equal(ErrorInvalidHandle, AudioPropagationExports.SystemSetAttributes(_ctx));
    }

    [Fact]
    public void RoomCreateProducesStableUniqueHandlesAndExhaustsAtThirtyTwo()
    {
        var system = CreateSystem();
        _ctx[CpuRegister.Rdi] = system;
        _ctx[CpuRegister.Rsi] = RoomOutputAddress;
        var handles = new HashSet<ulong>();
        for (var index = 0; index < 32; index++)
        {
            Assert.Equal(0, AudioPropagationExports.RoomCreate(_ctx));
            Assert.True(handles.Add(ReadUInt64(RoomOutputAddress)));
        }

        WriteUInt64(RoomOutputAddress, 0x3333);
        Assert.Equal(ErrorResourceExhausted, AudioPropagationExports.RoomCreate(_ctx));
        Assert.Equal(0x3333UL, ReadUInt64(RoomOutputAddress));

        Assert.True(AudioPropagationExports.TryGetDebugSnapshot(system, out var snapshot));
        Assert.Equal(32, snapshot.RoomCount);
    }

    [Fact]
    public void SetAttributesValidatesDescriptorsAndAppliesSupportedMutations()
    {
        var system = CreateSystem(materialCapacity: 2, flags: 4);
        var material = RegisterMaterial(system);
        _ctx[CpuRegister.Rdi] = system;
        _ctx[CpuRegister.Rsi] = AttributesAddress;
        _ctx[CpuRegister.Rdx] = 1;

        WriteAttribute(0x20000, AttributePayloadAddress, 4);
        Assert.Equal(ErrorInvalidValue, AudioPropagationExports.SystemSetAttributes(_ctx));

        WriteAttribute(0x20000, 0, 8);
        Assert.Equal(ErrorInvalidPointer, AudioPropagationExports.SystemSetAttributes(_ctx));

        WriteAttribute(0x20000, AttributePayloadAddress, 8);
        WriteUInt64(AttributePayloadAddress, 0xDEADBEEF);
        Assert.Equal(ErrorInvalidHandle, AudioPropagationExports.SystemSetAttributes(_ctx));

        WriteUInt64(AttributePayloadAddress, material);
        Assert.Equal(0, AudioPropagationExports.SystemSetAttributes(_ctx));
        Assert.True(AudioPropagationExports.TryGetDebugSnapshot(system, out var referenced));
        Assert.Equal(material, referenced.ReferencedObjectHandle);

        WriteAttribute(0x20001, AttributePayloadAddress, 4);
        WriteSingle(AttributePayloadAddress, 0.25f);
        Assert.Equal(0, AudioPropagationExports.SystemSetAttributes(_ctx));
        Assert.True(AudioPropagationExports.TryGetDebugSnapshot(system, out var gain));
        Assert.Equal(0.25f, gain.PropagationGain);

        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(ErrorInvalidValue, AudioPropagationExports.SystemSetAttributes(_ctx));
    }

    [Fact]
    public void SetAttributesRejectsUnsupportedTypeWithoutDereferencingPayload()
    {
        var system = CreateSystem(flags: 4);
        _ctx[CpuRegister.Rdi] = system;
        _ctx[CpuRegister.Rsi] = AttributesAddress;
        _ctx[CpuRegister.Rdx] = 1;

        WriteAttribute(0x10001, Base + 0x50_0000, 0x10);

        Assert.Equal(ErrorInvalidValue, AudioPropagationExports.SystemSetAttributes(_ctx));
    }

    [Fact]
    public void SetAttributesPreservesEarlierMutationWhenLaterAttributeFails()
    {
        var system = CreateSystem(flags: 4);
        _ctx[CpuRegister.Rdi] = system;
        _ctx[CpuRegister.Rsi] = AttributesAddress;
        _ctx[CpuRegister.Rdx] = 2;

        WriteAttribute(0, 0x20001, AttributePayloadAddress, 4);
        WriteAttribute(1, 0x10001, Base + 0x50_0000, 0x10);
        WriteSingle(AttributePayloadAddress, 0.375f);

        Assert.Equal(ErrorInvalidValue, AudioPropagationExports.SystemSetAttributes(_ctx));
        Assert.True(AudioPropagationExports.TryGetDebugSnapshot(system, out var snapshot));
        Assert.Equal(0.375f, snapshot.PropagationGain);
    }

    [Fact]
    public void GetRaysRequiresFixedHeadersAndReturnsDeterministicStateDerivedRecords()
    {
        var system = CreateSystem(materialCapacity: 2);
        var material = RegisterMaterial(system);
        var firstRoom = CreateRoom(system);
        _ = CreateRoom(system);
        InitializeRayBuffer();
        WriteUInt32(RayCountAddress, RayCapacity);
        _ctx[CpuRegister.Rdi] = system;
        _ctx[CpuRegister.Rsi] = RaysAddress;
        _ctx[CpuRegister.Rdx] = RayCountAddress;

        Assert.Equal(0, AudioPropagationExports.SystemGetRays(_ctx));
        Assert.Equal(2U, ReadUInt32(RayCountAddress));
        Assert.Equal(firstRoom, ReadUInt64(RaysAddress + 0x10));
        Assert.Equal(material, ReadUInt64(RaysAddress + 0x18));
        Assert.NotEqual(0.0f, ReadSingle(RaysAddress + 0x20));
        Assert.NotEqual(0.0f, ReadSingle(RaysAddress + 0x28));
        Assert.True(AudioPropagationExports.TryGetDebugSnapshot(system, out var snapshot));
        Assert.Equal(RaysAddress, snapshot.LastRayBufferAddress);
    }

    [Fact]
    public void GetRaysErrorsLeaveCallerOutputsUnchanged()
    {
        var system = CreateSystem();
        InitializeRayBuffer();
        WriteUInt32(RayCountAddress, 63);
        var originalRays = Read(RaysAddress, RayCapacity * RaySize);
        _ctx[CpuRegister.Rdi] = system;
        _ctx[CpuRegister.Rsi] = RaysAddress;
        _ctx[CpuRegister.Rdx] = RayCountAddress;

        Assert.Equal(ErrorInvalidValue, AudioPropagationExports.SystemGetRays(_ctx));
        Assert.Equal(63U, ReadUInt32(RayCountAddress));
        Assert.Equal(originalRays, Read(RaysAddress, RayCapacity * RaySize));

        WriteUInt32(RayCountAddress, RayCapacity);
        WriteUInt32(RaysAddress + RaySize, 0xDEADBEEF);
        var invalidRays = Read(RaysAddress, RayCapacity * RaySize);
        Assert.Equal(ErrorInvalidStructure, AudioPropagationExports.SystemGetRays(_ctx));
        Assert.Equal((uint)RayCapacity, ReadUInt32(RayCountAddress));
        Assert.Equal(invalidRays, Read(RaysAddress, RayCapacity * RaySize));
    }

    [Fact]
    public void SetRaysValidatesHandlePointerAndCountInFirmwareOrder()
    {
        _ctx[CpuRegister.Rdi] = 0xDEADBEEF;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(ErrorInvalidHandle, AudioPropagationExports.SystemSetRays(_ctx));
        Assert.Equal(0x0000_0000_8A70_0002UL, _ctx[CpuRegister.Rax]);

        var system = CreateSystem();
        _ctx[CpuRegister.Rdi] = system;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(ErrorInvalidPointer, AudioPropagationExports.SystemSetRays(_ctx));
        Assert.Equal(0x0000_0000_8A70_0003UL, _ctx[CpuRegister.Rax]);

        _ctx[CpuRegister.Rsi] = Base + 0x50_0000;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(ErrorInvalidValue, AudioPropagationExports.SystemSetRays(_ctx));
        Assert.Equal(0x0000_0000_8A70_0001UL, _ctx[CpuRegister.Rax]);

        _ctx[CpuRegister.Rsi] = ulong.MaxValue - 0x10;
        _ctx[CpuRegister.Rdx] = 1;
        Assert.Equal(ErrorInvalidPointer, AudioPropagationExports.SystemSetRays(_ctx));
        Assert.Equal(0x0000_0000_8A70_0003UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void SetRaysValidatesEverySubmittedRecordBeforeNoOpSuccess()
    {
        var system = CreateSystem();
        Fill(RaysAddress, 3 * RaySize, 0);
        for (var index = 0; index < 3; index++)
        {
            var address = RaysAddress + (ulong)(index * RaySize);
            WriteUInt32(address, RayTag);
            WriteUInt64(address + 0x08, RaySize);
        }

        _ctx[CpuRegister.Rdi] = system;
        _ctx[CpuRegister.Rsi] = RaysAddress;
        _ctx[CpuRegister.Rdx] = 3;
        var validRecords = Read(RaysAddress, 3 * RaySize);
        Assert.Equal(0, AudioPropagationExports.SystemSetRays(_ctx));
        Assert.Equal(validRecords, Read(RaysAddress, 3 * RaySize));

        WriteUInt32(RaysAddress + (2 * RaySize), 0xDEADBEEF);
        var invalidRecords = Read(RaysAddress, 3 * RaySize);
        Assert.True(AudioPropagationExports.TryGetDebugSnapshot(system, out var before));
        Assert.Equal(ErrorInvalidStructure, AudioPropagationExports.SystemSetRays(_ctx));
        Assert.Equal(0x0000_0000_8A70_0007UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(invalidRecords, Read(RaysAddress, 3 * RaySize));
        Assert.True(AudioPropagationExports.TryGetDebugSnapshot(system, out var after));
        Assert.Equal(before, after);

        _ctx[CpuRegister.Rdx] = 2;
        Assert.Equal(0, AudioPropagationExports.SystemSetRays(_ctx));
        Assert.Equal(invalidRecords, Read(RaysAddress, 3 * RaySize));
    }

    [Fact]
    public void SetRaysReadsOnlyTagThenSizeForEachSubmittedRecord()
    {
        var system = CreateSystem();
        Fill(RaysAddress, 2 * RaySize, 0);
        for (var index = 0; index < 2; index++)
        {
            var address = RaysAddress + (ulong)(index * RaySize);
            WriteUInt32(address, RayTag);
            WriteUInt64(address + 0x08, RaySize);
        }

        var memory = new RecordingFaultingCpuMemory(_memory);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = system;
        context[CpuRegister.Rsi] = RaysAddress;
        context[CpuRegister.Rdx] = 2;

        Assert.Equal(0, AudioPropagationExports.SystemSetRays(context));
        Assert.Equal(
            new[]
            {
                new ReadAccess(RaysAddress, sizeof(uint)),
                new ReadAccess(RaysAddress + 0x08, sizeof(ulong)),
                new ReadAccess(RaysAddress + RaySize, sizeof(uint)),
                new ReadAccess(RaysAddress + RaySize + 0x08, sizeof(ulong)),
            },
            memory.ReadAttempts);
    }

    [Fact]
    public void SetRaysShortCircuitsHeaderReadsInFirmwareOrder()
    {
        var system = CreateSystem();
        Fill(RaysAddress, 2 * RaySize, 0);

        var faultedTagMemory = new RecordingFaultingCpuMemory(
            _memory,
            readFaultStartAddress: RaysAddress);
        var faultedTagContext = new CpuContext(faultedTagMemory, Generation.Gen5);
        faultedTagContext[CpuRegister.Rdi] = system;
        faultedTagContext[CpuRegister.Rsi] = RaysAddress;
        faultedTagContext[CpuRegister.Rdx] = 2;

        Assert.Equal(
            ErrorInvalidPointer,
            AudioPropagationExports.SystemSetRays(faultedTagContext));
        Assert.Equal(
            new[] { new ReadAccess(RaysAddress, sizeof(uint)) },
            faultedTagMemory.ReadAttempts);

        WriteUInt32(RaysAddress, 0xDEADBEEF);
        WriteUInt64(RaysAddress + 0x08, RaySize);

        var badTagMemory = new RecordingFaultingCpuMemory(
            _memory,
            readFaultStartAddress: RaysAddress + 0x08);
        var badTagContext = new CpuContext(badTagMemory, Generation.Gen5);
        badTagContext[CpuRegister.Rdi] = system;
        badTagContext[CpuRegister.Rsi] = RaysAddress;
        badTagContext[CpuRegister.Rdx] = 2;

        Assert.Equal(
            ErrorInvalidStructure,
            AudioPropagationExports.SystemSetRays(badTagContext));
        Assert.Equal(
            new[] { new ReadAccess(RaysAddress, sizeof(uint)) },
            badTagMemory.ReadAttempts);

        WriteUInt32(RaysAddress, RayTag);
        var faultedSizeMemory = new RecordingFaultingCpuMemory(
            _memory,
            readFaultStartAddress: RaysAddress + 0x08);
        var faultedSizeContext = new CpuContext(faultedSizeMemory, Generation.Gen5);
        faultedSizeContext[CpuRegister.Rdi] = system;
        faultedSizeContext[CpuRegister.Rsi] = RaysAddress;
        faultedSizeContext[CpuRegister.Rdx] = 2;

        Assert.Equal(
            ErrorInvalidPointer,
            AudioPropagationExports.SystemSetRays(faultedSizeContext));
        Assert.Equal(
            new[]
            {
                new ReadAccess(RaysAddress, sizeof(uint)),
                new ReadAccess(RaysAddress + 0x08, sizeof(ulong)),
            },
            faultedSizeMemory.ReadAttempts);

        WriteUInt64(RaysAddress + 0x08, 0x57);
        var badSizeMemory = new RecordingFaultingCpuMemory(_memory);
        var badSizeContext = new CpuContext(badSizeMemory, Generation.Gen5);
        badSizeContext[CpuRegister.Rdi] = system;
        badSizeContext[CpuRegister.Rsi] = RaysAddress;
        badSizeContext[CpuRegister.Rdx] = 2;

        Assert.Equal(
            ErrorInvalidStructure,
            AudioPropagationExports.SystemSetRays(badSizeContext));
        Assert.Equal(
            new[]
            {
                new ReadAccess(RaysAddress, sizeof(uint)),
                new ReadAccess(RaysAddress + 0x08, sizeof(ulong)),
            },
            badSizeMemory.ReadAttempts);
    }

    [Fact]
    public void SetRaysAcceptsUnmatchedRecordWhenOnlyHeaderIsReadable()
    {
        var system = CreateSystem();
        Fill(RaysAddress, RaySize, 0);
        WriteUInt32(RaysAddress, RayTag);
        WriteUInt64(RaysAddress + 0x08, RaySize);

        var memory = new RecordingFaultingCpuMemory(
            _memory,
            readFaultStartAddress: RaysAddress + 0x10);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = system;
        context[CpuRegister.Rsi] = RaysAddress;
        context[CpuRegister.Rdx] = 1;

        Assert.Equal(0, AudioPropagationExports.SystemSetRays(context));
        Assert.Equal(
            new[]
            {
                new ReadAccess(RaysAddress, sizeof(uint)),
                new ReadAccess(RaysAddress + 0x08, sizeof(ulong)),
            },
            memory.ReadAttempts);
    }

    [Fact]
    public void SystemRegistryExhaustionReturnsResourceErrorWithoutOutputWrite()
    {
        for (var index = 0; index < 256; index++)
        {
            Assert.NotEqual(0UL, CreateSystem());
        }

        WriteUInt64(SystemOutputAddress, 0x4444);
        PrepareCreateMemory();
        PrepareCreateRegisters();
        Assert.Equal(ErrorResourceExhausted, AudioPropagationExports.SystemCreate(_ctx));
        Assert.Equal(0x4444UL, ReadUInt64(SystemOutputAddress));
    }

    private int QueryMemory()
    {
        _ctx[CpuRegister.Rdi] = ConfigAddress;
        _ctx[CpuRegister.Rsi] = MemoryInfoAddress;
        return AudioPropagationExports.SystemQueryMemory(_ctx);
    }

    private ulong CreateSystem(uint materialCapacity = 2, uint flags = 0)
    {
        WriteValidConfig(materialCapacity: materialCapacity, flags: flags);
        WriteMemoryInfoHeader();
        Assert.Equal(0, QueryMemory());
        PrepareCreateMemory();
        WriteUInt64(SystemOutputAddress, 0);
        PrepareCreateRegisters();
        Assert.Equal(0, AudioPropagationExports.SystemCreate(_ctx));
        return ReadUInt64(SystemOutputAddress);
    }

    private void PrepareCreateMemory()
    {
        WriteUInt64(MemoryInfoAddress + 0x10, PrimaryBackingAddress);
        if (ReadUInt64(MemoryInfoAddress + 0x28) != 0)
        {
            WriteUInt64(MemoryInfoAddress + 0x20, SecondaryBackingAddress);
        }
    }

    private void PrepareCreateRegisters()
    {
        _ctx[CpuRegister.Rdi] = ConfigAddress;
        _ctx[CpuRegister.Rsi] = MemoryInfoAddress;
        _ctx[CpuRegister.Rdx] = SystemOutputAddress;
    }

    private ulong RegisterMaterial(ulong system)
    {
        WriteMaterialDescriptor();
        _ctx[CpuRegister.Rdi] = system;
        _ctx[CpuRegister.Rsi] = MaterialAddress;
        _ctx[CpuRegister.Rdx] = MaterialOutputAddress;
        Assert.Equal(0, AudioPropagationExports.SystemRegisterMaterial(_ctx));
        return ReadUInt64(MaterialOutputAddress);
    }

    private ulong CreateRoom(ulong system)
    {
        _ctx[CpuRegister.Rdi] = system;
        _ctx[CpuRegister.Rsi] = RoomOutputAddress;
        Assert.Equal(0, AudioPropagationExports.RoomCreate(_ctx));
        return ReadUInt64(RoomOutputAddress);
    }

    private void WriteValidConfig(
        uint sourceCapacity = 1,
        uint materialCapacity = 2,
        uint flags = 0,
        uint transformSize = 0x200)
    {
        Fill(ConfigAddress, 0x38, 0);
        WriteUInt32(ConfigAddress, ConfigTag);
        WriteUInt64(ConfigAddress + 0x08, 0x38);
        WriteUInt32(ConfigAddress + 0x10, sourceCapacity);
        WriteUInt32(ConfigAddress + 0x14, materialCapacity);
        WriteUInt32(ConfigAddress + 0x18, 6);
        WriteUInt32(ConfigAddress + 0x1C, 1);
        WriteUInt32(ConfigAddress + 0x20, transformSize);
        WriteSingle(ConfigAddress + 0x24, 100.0f);
        WriteSingle(ConfigAddress + 0x28, 200.0f);
        WriteUInt32(ConfigAddress + 0x2C, flags);
    }

    private void WriteMemoryInfoHeader()
    {
        Fill(MemoryInfoAddress, 0x30, 0);
        WriteUInt32(MemoryInfoAddress, MemoryInfoTag);
        WriteUInt64(MemoryInfoAddress + 0x08, 0x30);
    }

    private void WriteMaterialDescriptor()
    {
        Fill(MaterialAddress, 0x40, 0);
        WriteUInt32(MaterialAddress, MaterialTag);
        WriteUInt64(MaterialAddress + 0x08, 0x40);
        _memory.WriteCString(MaterialAddress + 0x10, "test-material");
        WriteSingle(MaterialAddress + 0x20, 0.25f);
        WriteSingle(MaterialAddress + 0x24, 0.5f);
        WriteSingle(MaterialAddress + 0x28, 0.75f);
        WriteSingle(MaterialAddress + 0x38, 1.0f);
    }

    private void WriteAttribute(uint type, ulong dataAddress, ulong size)
    {
        WriteAttribute(0, type, dataAddress, size);
    }

    private void WriteAttribute(int index, uint type, ulong dataAddress, ulong size)
    {
        var address = AttributesAddress + ((ulong)index * 0x20);
        Fill(address, 0x20, 0);
        WriteUInt32(address, type);
        WriteUInt64(address + 0x08, dataAddress);
        WriteUInt64(address + 0x10, size);
    }

    private void InitializeRayBuffer()
    {
        Fill(RaysAddress, RayCapacity * RaySize, 0);
        for (var index = 0; index < RayCapacity; index++)
        {
            var address = RaysAddress + (ulong)(index * RaySize);
            WriteUInt32(address, RayTag);
            WriteUInt64(address + 0x08, RaySize);
        }
    }

    private byte[] Read(ulong address, int size)
    {
        var bytes = new byte[size];
        Assert.True(_memory.TryRead(address, bytes));
        return bytes;
    }

    private uint ReadUInt32(ulong address) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Read(address, sizeof(uint)));

    private ulong ReadUInt64(ulong address) =>
        BinaryPrimitives.ReadUInt64LittleEndian(Read(address, sizeof(ulong)));

    private float ReadSingle(ulong address) =>
        BitConverter.UInt32BitsToSingle(ReadUInt32(address));

    private void WriteUInt32(ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private void WriteUInt64(ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private void WriteSingle(ulong address, float value) =>
        WriteUInt32(address, BitConverter.SingleToUInt32Bits(value));

    private void Fill(ulong address, int size, byte value)
    {
        var bytes = new byte[size];
        bytes.AsSpan().Fill(value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private sealed class ForwardingCpuMemory(ICpuMemory inner) : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) =>
            inner.TryRead(virtualAddress, destination);

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) =>
            inner.TryWrite(virtualAddress, source);
    }

    private readonly record struct ReadAccess(ulong Address, int Width);

    private sealed class RecordingFaultingCpuMemory(
        ICpuMemory inner,
        ulong? readFaultStartAddress = null) : ICpuMemory
    {
        public List<ReadAccess> ReadAttempts { get; } = [];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            ReadAttempts.Add(new ReadAccess(virtualAddress, destination.Length));
            if (readFaultStartAddress is { } faultAddress &&
                (virtualAddress >= faultAddress ||
                    faultAddress - virtualAddress < (ulong)destination.Length))
            {
                return false;
            }

            return inner.TryRead(virtualAddress, destination);
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) =>
            inner.TryWrite(virtualAddress, source);
    }
}
