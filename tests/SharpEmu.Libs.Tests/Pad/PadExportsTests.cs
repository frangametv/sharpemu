// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class PadExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const int InvalidArgument = unchecked((int)0x80920001);
    private const int InvalidHandle = unchecked((int)0x80920003);
    private const int NotInitialized = unchecked((int)0x80920005);

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public PadExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        PadExports.ResetTriggerEffectStateForTests();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void SetTiltCorrectionState_ValidatesHandle(int handle, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(expected, PadExports.PadSetTiltCorrectionState(_ctx));
    }

    [Fact]
    public void GetTriggerEffectState_UsesInitNullHandleDeviceValidationOrder()
    {
        var stateAddress = Base + 0x100;
        Span<byte> sentinel = stackalloc byte[8];
        sentinel.Fill(0xA5);
        Assert.True(_memory.TryWrite(stateAddress, sentinel));
        _ctx[CpuRegister.Rdi] = 2;
        _ctx[CpuRegister.Rsi] = stateAddress;

        Assert.Equal(NotInitialized, PadExports.PadGetTriggerEffectState(_ctx));
        AssertBytes(stateAddress, sentinel);

        PadExports.ResetTriggerEffectStateForTests(initialized: true);
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(InvalidArgument, PadExports.PadGetTriggerEffectState(_ctx));

        _ctx[CpuRegister.Rsi] = stateAddress;
        Assert.Equal(InvalidHandle, PadExports.PadGetTriggerEffectState(_ctx));
        AssertBytes(stateAddress, sentinel);

        PadExports.ResetTriggerEffectStateForTests(
            initialized: true,
            deviceState: 3);
        PadExports.SetPrimaryPadOpenForTests(true);
        _ctx[CpuRegister.Rdi] = 1;
        Assert.Equal(InvalidArgument, PadExports.PadGetTriggerEffectState(_ctx));
        AssertBytes(stateAddress, sentinel);
    }

    [Fact]
    public void GetTriggerEffectState_NormalizesUnsupportedBackendToEightZeroBytes()
    {
        var stateAddress = Base + 0x200;
        Span<byte> sentinel = stackalloc byte[12];
        sentinel.Fill(0xCC);
        Assert.True(_memory.TryWrite(stateAddress, sentinel));
        PadExports.ResetTriggerEffectStateForTests(initialized: true);
        PadExports.SetPrimaryPadOpenForTests(true);
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = stateAddress;

        Assert.Equal(0, PadExports.PadGetTriggerEffectState(_ctx));

        Span<byte> actual = stackalloc byte[12];
        Assert.True(_memory.TryRead(stateAddress, actual));
        Assert.Equal(new byte[8], actual[..8].ToArray());
        Assert.Equal(new byte[] { 0xCC, 0xCC, 0xCC, 0xCC }, actual[8..].ToArray());
    }

    [Fact]
    public void PadOpenAndClose_ControlTriggerEffectHandleLifetime()
    {
        var stateAddress = Base + 0x280;
        PadExports.ResetTriggerEffectStateForTests(initialized: true);
        PadExports.SetHostInputForTests(new TestHostInput());
        _ctx[CpuRegister.Rdi] = 0x1000_0000;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = 0;

        Assert.Equal(1, PadExports.PadOpen(_ctx));

        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = stateAddress;
        Assert.Equal(0, PadExports.PadGetTriggerEffectState(_ctx));

        _ctx[CpuRegister.Rdi] = 1;
        Assert.Equal(0, PadExports.PadClose(_ctx));

        Span<byte> sentinel = stackalloc byte[8];
        sentinel.Fill(0xA7);
        Assert.True(_memory.TryWrite(stateAddress, sentinel));
        _ctx[CpuRegister.Rsi] = stateAddress;
        Assert.Equal(InvalidHandle, PadExports.PadGetTriggerEffectState(_ctx));
        AssertBytes(stateAddress, sentinel);
    }

    [Fact]
    public void GetTriggerEffectState_MapsFfAndCopiesSupportedState()
    {
        var stateAddress = Base + 0x300;
        PadExports.ResetTriggerEffectStateForTests(initialized: true);
        PadExports.SetPrimaryPadOpenForTests(true);
        PadExports.SetTriggerEffectStateBackendForTests(
            _ => (0, byte.MaxValue, 7));
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = stateAddress;

        Assert.Equal(0, PadExports.PadGetTriggerEffectState(_ctx));

        Span<byte> actual = stackalloc byte[8];
        Assert.True(_memory.TryRead(stateAddress, actual));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(actual));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(actual[4..]));
    }

    [Fact]
    public void GetTriggerEffectState_PropagatesBackendErrorAfterZeroingOutput()
    {
        const int BackendError = unchecked((int)0x8123_4567);
        var stateAddress = Base + 0x380;
        Span<byte> sentinel = stackalloc byte[8];
        sentinel.Fill(0x5C);
        Assert.True(_memory.TryWrite(stateAddress, sentinel));
        PadExports.ResetTriggerEffectStateForTests(initialized: true);
        PadExports.SetPrimaryPadOpenForTests(true);
        PadExports.SetTriggerEffectStateBackendForTests(
            _ => (BackendError, 4, 5));
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = stateAddress;

        Assert.Equal(BackendError, PadExports.PadGetTriggerEffectState(_ctx));
        AssertBytes(stateAddress, new byte[8]);
    }

    [Fact]
    public void PadReadState_WritesLibcBackedBuffer()
    {
        PadExports.SetHostInputForTests(new TestHostInput());
        var dataAddress = AllocateTracked(_ctx, 0x78);
        try
        {
            var sentinel = Enumerable.Repeat((byte)0xCC, 0x78).ToArray();
            Marshal.Copy(sentinel, 0, unchecked((nint)dataAddress), sentinel.Length);
            _ctx[CpuRegister.Rdi] = 1;
            _ctx[CpuRegister.Rsi] = dataAddress;

            Assert.Equal(0, PadExports.PadReadState(_ctx));

            var data = new byte[0x78];
            Marshal.Copy(unchecked((nint)dataAddress), data, 0, data.Length);
            Assert.Equal(new byte[] { 128, 128, 128, 128 }, data[4..8]);
            Assert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(0x18)));
            Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(0x30)));
            Assert.Equal(1, data[0x4C]);
            Assert.Equal(1, data[0x68]);
        }
        finally
        {
            FreeTracked(_ctx, dataAddress);
            PadExports.ResetTriggerEffectStateForTests();
        }
    }

    [Fact]
    public void PadReadState_WritesCrossAtGtaButtonOffset()
    {
        PadExports.SetHostInputForTests(new TestHostInput(HostGamepadButtons.Cross));
        var dataAddress = Base + 0x500;
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = dataAddress;

        Assert.Equal(0, PadExports.PadReadState(_ctx));

        Span<byte> data = stackalloc byte[0x78];
        Assert.True(_memory.TryRead(dataAddress, data));
        Assert.Equal(0x4000U, BinaryPrimitives.ReadUInt32LittleEndian(data));
        Assert.Equal(1, data[0x4C]);
    }

    [Fact]
    public void TriggerEffectStateNid_RegistersWithPadIdentity()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("znaWI0gpuo8", out var export));
        Assert.Equal("scePadGetTriggerEffectState", export.Name);
        Assert.Equal("libScePad", export.LibraryName);
    }

    private void AssertBytes(ulong address, ReadOnlySpan<byte> expected)
    {
        Span<byte> actual = stackalloc byte[expected.Length];
        Assert.True(_memory.TryRead(address, actual));
        Assert.Equal(expected.ToArray(), actual.ToArray());
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

    private sealed class TestHostInput : IHostInput
    {
        private readonly HostGamepadButtons _buttons;

        public TestHostInput(HostGamepadButtons buttons = HostGamepadButtons.None)
        {
            _buttons = buttons;
        }

        public void EnsureStarted()
        {
        }

        public int GetGamepadStates(Span<HostGamepadState> destination)
        {
            if (_buttons == HostGamepadButtons.None || destination.IsEmpty)
            {
                return 0;
            }

            destination[0] = new HostGamepadState(
                Connected: true,
                Buttons: _buttons,
                LeftX: 128,
                LeftY: 128,
                RightX: 128,
                RightY: 128,
                LeftTrigger: 0,
                RightTrigger: 0);
            return 1;
        }

        public string? DescribeConnectedGamepad() => null;

        public void SetRumble(byte largeMotor, byte smallMotor)
        {
        }

        public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger)
        {
        }

        public void SetAdaptiveTriggerEffect(
            HostAdaptiveTriggerEffect? leftTrigger,
            HostAdaptiveTriggerEffect? rightTrigger)
        {
        }

        public void SetLightbar(byte red, byte green, byte blue)
        {
        }

        public void ResetLightbar()
        {
        }

        public bool IsHostWindowFocused() => false;

        public bool IsKeyDown(int virtualKey) => false;
    }
}
