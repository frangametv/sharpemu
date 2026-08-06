// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Font;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Font;

public sealed class FontExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong FontAddress = Base + 0x100;
    private const ulong LibraryAddress = Base + 0x300;
    private const ulong RendererAddress = Base + 0x500;
    private const ulong LayoutAddress = Base + 0x800;
    private const ulong DefinitionAddress = Base + 0x900;
    private const ulong GlyphSlotAddress = Base + 0xA00;
    private const ulong SurfaceAddress = Base + 0xB00;
    private const ulong MetricsAddress = Base + 0xC00;
    private const ulong ResultAddress = Base + 0xD00;
    private const ulong HandleSlotAddress = Base + 0xE00;
    private const ulong AllocationStart = Base + 0x1_0000;

    private const int FontErrorInvalidArgument = unchecked((int)0x80460002);
    private const int FontErrorInvalidState = unchecked((int)0x80460003);
    private const int FontErrorInvalidFont = unchecked((int)0x80460005);
    private const int FontErrorInvalidRenderer = unchecked((int)0x80460007);
    private const int FontErrorInvalidCodepoint = unchecked((int)0x80460041);
    private const int FontErrorRendererAlreadyBound = unchecked((int)0x80460060);
    private const int FontErrorRendererNotBound = unchecked((int)0x80460061);

    private readonly AllocatingCpuMemory _memory = new(Base, 0x4_0000, AllocationStart);
    private readonly CpuContext _ctx;

    public FontExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        WriteUInt16(LibraryAddress, 0x0F01);
        WriteUInt16(FontAddress, 0x0F02);
        WriteUInt64(FontAddress + 0x28, LibraryAddress);
        WriteUInt16(RendererAddress, 0x0F07);
    }

    public static TheoryData<string, string> AstroFontExports => new()
    {
        { "3OdRkSjOcog", "sceFontBindRenderer" },
        { "N1EBMeGhf7E", "sceFontSetScalePixel" },
        { "imxVx8lm+KM", "sceFontGetHorizontalLayout" },
        { "6vGCkkQJOcI", "sceFontSetupRenderScalePixel" },
        { "TMtqoFQjjbA", "sceFontSetEffectSlant" },
        { "v0phZwa4R5o", "sceFontSetEffectWeight" },
        { "lz9y9UFO2UU", "sceFontSetupRenderEffectSlant" },
        { "XIGorvLusDQ", "sceFontSetupRenderEffectWeight" },
        { "kAenWy1Zw5o", "sceFontRenderCharGlyphImageHorizontal" },
        { "C-4Qw5Srlyw", "sceFontGenerateCharGlyph" },
        { "8-zmgsxkBek", "sceFontGlyphDefineAttribute" },
        { "LHDoRWVFGqk", "sceFontDeleteGlyph" },
        { "1QjhKxrsOB8", "sceFontUnbindRenderer" },
        { "vzHs3C8lWJk", "sceFontCloseFont" },
        { "exAxkyVLt0s", "sceFontDestroyRenderer" },
        { "3BrWWFU+4ts", "sceFontGetVerticalLayout" },
    };

    [Theory]
    [MemberData(nameof(AstroFontExports))]
    public void AstroFontNids_RegisterWithExpectedIdentity(string nid, string exportName)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export), $"NID {nid} did not register.");
        Assert.Equal(exportName, export.Name);
        Assert.Equal("libSceFont", export.LibraryName);
    }

    // SceFontHorizontalLayout is three floats; the sentinel directly after
    // them must survive the call.
    [Fact]
    public void GetHorizontalLayout_WritesExactlyThreeFallbackFloats()
    {
        const uint Sentinel = 0xDEADBEEF;
        WriteUInt32(LayoutAddress + 12, Sentinel);
        _ctx[CpuRegister.Rdi] = FontAddress;
        _ctx[CpuRegister.Rsi] = LayoutAddress;

        Assert.Equal(0, FontExports.GetHorizontalLayout(_ctx));

        Assert.Equal(12.0f, ReadSingle(LayoutAddress));
        Assert.Equal(16.0f, ReadSingle(LayoutAddress + 4));
        Assert.Equal(0.0f, ReadSingle(LayoutAddress + 8));
        Assert.Equal(Sentinel, ReadUInt32(LayoutAddress + 12));
    }

    [Fact]
    public void GetVerticalLayout_WritesExactlyThreeFallbackFloats()
    {
        const uint Sentinel = 0xA5A5A5A5;
        WriteUInt32(LayoutAddress + 12, Sentinel);
        _ctx[CpuRegister.Rdi] = FontAddress;
        _ctx[CpuRegister.Rsi] = LayoutAddress;

        Assert.Equal(0, FontExports.GetVerticalLayout(_ctx));

        Assert.Equal(0.0f, ReadSingle(LayoutAddress));
        Assert.Equal(16.0f, ReadSingle(LayoutAddress + 4));
        Assert.Equal(0.0f, ReadSingle(LayoutAddress + 8));
        Assert.Equal(Sentinel, ReadUInt32(LayoutAddress + 12));
    }

    [Fact]
    public void InvalidLayoutFont_ZerosOnlyTwelveBytes()
    {
        Fill(LayoutAddress, 16, 0xCC);
        _ctx[CpuRegister.Rdi] = Base + 0x700;
        _ctx[CpuRegister.Rsi] = LayoutAddress;

        Assert.Equal(FontErrorInvalidFont, FontExports.GetVerticalLayout(_ctx));

        Assert.Equal(new byte[12], Read(LayoutAddress, 12));
        Assert.Equal(new byte[] { 0xCC, 0xCC, 0xCC, 0xCC }, Read(LayoutAddress + 12, 4));
    }

    [Fact]
    public void LayoutFallback_ReflectsStoredScaleAndEffectState()
    {
        _ctx[CpuRegister.Rdi] = FontAddress;
        _ctx[CpuRegister.Rsi] = LayoutAddress;
        Assert.Equal(0, FontExports.GetHorizontalLayout(_ctx));
        var baselineHorizontal = Read(LayoutAddress, 12);
        Assert.Equal(0, FontExports.GetVerticalLayout(_ctx));
        var baselineVertical = Read(LayoutAddress, 12);

        SetXmmSingle(0, 20.0f);
        SetXmmSingle(1, 24.0f);
        Assert.Equal(0, FontExports.SetScalePixel(_ctx));
        SetXmmSingle(0, 0.5f);
        Assert.Equal(0, FontExports.SetEffectSlant(_ctx));
        SetXmmSingle(0, 1.04f);
        SetXmmSingle(1, 0.96f);
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, FontExports.SetEffectWeight(_ctx));

        _ctx[CpuRegister.Rsi] = LayoutAddress;
        Assert.Equal(0, FontExports.GetHorizontalLayout(_ctx));
        Assert.NotEqual(baselineHorizontal, Read(LayoutAddress, 12));
        Assert.Equal(0, FontExports.GetVerticalLayout(_ctx));
        Assert.NotEqual(baselineVertical, Read(LayoutAddress, 12));
        Assert.NotEqual(0.0f, ReadSingle(LayoutAddress));
    }

    [Fact]
    public void BindRenderer_CopiesRendererStateAndRejectsDoubleBind()
    {
        for (var index = 0; index < 5; index++)
        {
            WriteUInt64(FontAddress + 0x40 + (ulong)(index * 8), 0x1100UL + (ulong)index);
            WriteUInt64(RendererAddress + 0x40 + (ulong)(index * 8), 0x9900UL + (ulong)index);
        }

        _ctx[CpuRegister.Rdi] = FontAddress;
        _ctx[CpuRegister.Rsi] = RendererAddress;
        Assert.Equal(0, FontExports.BindRenderer(_ctx));

        Assert.Equal(RendererAddress, ReadUInt64(FontAddress + 0x30));
        for (var index = 0; index < 5; index++)
        {
            Assert.Equal(0x1100UL + (ulong)index, ReadUInt64(FontAddress + 0x68 + (ulong)(index * 8)));
        }

        Assert.Equal(FontErrorRendererAlreadyBound, FontExports.BindRenderer(_ctx));
    }

    [Fact]
    public void BindRenderer_AlreadyBoundWinsOverInvalidCandidateRenderer()
    {
        WriteUInt64(FontAddress + 0x30, RendererAddress);
        _ctx[CpuRegister.Rdi] = FontAddress;
        _ctx[CpuRegister.Rsi] = Base + 0x1800;

        Assert.Equal(FontErrorRendererAlreadyBound, FontExports.BindRenderer(_ctx));
        Assert.Equal(RendererAddress, ReadUInt64(FontAddress + 0x30));
    }

    [Fact]
    public void ScaleAndEffects_WritePinnedStateOffsets()
    {
        WriteUInt64(FontAddress + 0x48, ulong.MaxValue);
        WriteUInt32(FontAddress + 0x94, uint.MaxValue);
        SetXmmSingle(0, 2.5f);
        SetXmmSingle(1, 3.5f);
        _ctx[CpuRegister.Rdi] = FontAddress;

        Assert.Equal(0, FontExports.SetScalePixel(_ctx));
        Assert.Equal(2.5f, ReadSingle(FontAddress + 0x50));
        Assert.Equal(3.5f, ReadSingle(FontAddress + 0x54));
        Assert.Equal(0UL, ReadUInt64(FontAddress + 0x48));
        Assert.Equal(0U, ReadUInt32(FontAddress + 0x94));

        SetXmmSingle(0, 2.0f);
        Assert.Equal(0, FontExports.SetEffectSlant(_ctx));
        Assert.Equal(1.0f, ReadSingle(FontAddress + 0x60));

        SetXmmSingle(0, 1.5f);
        SetXmmSingle(1, 0.5f);
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, FontExports.SetEffectWeight(_ctx));
        Assert.Equal(0.04f, ReadSingle(FontAddress + 0x58));
        Assert.Equal(-0.04f, ReadSingle(FontAddress + 0x5C));
    }

    [Fact]
    public void SetupRenderEffects_RequireBindingAndWritePinnedOffsets()
    {
        _ctx[CpuRegister.Rdi] = FontAddress;
        SetXmmSingle(0, 4.0f);
        SetXmmSingle(1, 5.0f);
        Assert.Equal(FontErrorRendererNotBound, FontExports.SetupRenderScalePixel(_ctx));

        _ctx[CpuRegister.Rsi] = RendererAddress;
        Assert.Equal(0, FontExports.BindRenderer(_ctx));

        _ctx[CpuRegister.Rsi] = 0;
        WriteUInt64(FontAddress + 0x70, ulong.MaxValue);
        WriteUInt16(FontAddress + 0x98, ushort.MaxValue);
        Assert.Equal(0, FontExports.SetupRenderScalePixel(_ctx));
        Assert.Equal(4.0f, ReadSingle(FontAddress + 0x78));
        Assert.Equal(5.0f, ReadSingle(FontAddress + 0x7C));
        Assert.Equal(0UL, ReadUInt64(FontAddress + 0x70));
        Assert.Equal((ushort)0, ReadUInt16(FontAddress + 0x98));

        SetXmmSingle(0, -2.0f);
        Assert.Equal(0, FontExports.SetupRenderEffectSlant(_ctx));
        Assert.Equal(-1.0f, ReadSingle(FontAddress + 0x88));

        SetXmmSingle(0, 1.02f);
        SetXmmSingle(1, 0.98f);
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, FontExports.SetupRenderEffectWeight(_ctx));
        Assert.Equal(0.02f, ReadSingle(FontAddress + 0x80), precision: 5);
        Assert.Equal(-0.02f, ReadSingle(FontAddress + 0x84), precision: 5);
    }

    [Fact]
    public void GenerateAttributeDelete_TracksGuestGlyphLifecycle()
    {
        WriteUInt16(DefinitionAddress, 0x0FD3);
        WriteUInt16(DefinitionAddress + 6, 0x0101);
        _ctx[CpuRegister.Rdi] = FontAddress;
        _ctx[CpuRegister.Rsi] = 0x4E;
        _ctx[CpuRegister.Rdx] = DefinitionAddress;
        _ctx[CpuRegister.Rcx] = GlyphSlotAddress;

        Assert.Equal(0, FontExports.GenerateCharGlyph(_ctx));
        var glyphAddress = ReadUInt64(GlyphSlotAddress);
        Assert.NotEqual(0UL, glyphAddress);
        Assert.True(_memory.IsAllocated(glyphAddress));
        Assert.Equal((ushort)0x0F03, ReadUInt16(glyphAddress));
        Assert.Equal(0x4EU, ReadUInt32(glyphAddress + 4));
        Assert.Equal(FontAddress, ReadUInt64(glyphAddress + 8));
        Assert.Equal(FontAddress, ReadUInt64(glyphAddress + 0x18));

        _ctx[CpuRegister.Rdi] = glyphAddress;
        _ctx[CpuRegister.Rsi] = 0x11;
        _ctx[CpuRegister.Rdx] = ResultAddress;
        Assert.Equal(0, FontExports.GlyphDefineAttribute(_ctx));
        Assert.Equal((ushort)0x02, ReadUInt16(glyphAddress + 2));
        Assert.Equal(0x10U, ReadUInt32(ResultAddress));

        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = GlyphSlotAddress;
        Assert.Equal(0, FontExports.DeleteGlyph(_ctx));
        Assert.Equal(0UL, ReadUInt64(GlyphSlotAddress));
        Assert.False(_memory.IsAllocated(glyphAddress));
    }

    [Fact]
    public void GenerateFailure_ZerosPublishedGlyphSlot()
    {
        WriteUInt64(GlyphSlotAddress, ulong.MaxValue);
        _ctx[CpuRegister.Rdi] = FontAddress;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rcx] = GlyphSlotAddress;

        Assert.Equal(FontErrorInvalidCodepoint, FontExports.GenerateCharGlyph(_ctx));
        Assert.Equal(0UL, ReadUInt64(GlyphSlotAddress));
    }

    [Fact]
    public void GenerateGlyph_EnforcesFlagsDetailDependencyAndAllocatorOverride()
    {
        WriteUInt16(DefinitionAddress + 4, 0x0002);
        WriteUInt16(DefinitionAddress + 6, 0x0101);
        _ctx[CpuRegister.Rdi] = FontAddress;
        _ctx[CpuRegister.Rsi] = 0x41;
        _ctx[CpuRegister.Rdx] = DefinitionAddress;
        _ctx[CpuRegister.Rcx] = GlyphSlotAddress;

        Assert.Equal(FontErrorInvalidArgument, FontExports.GenerateCharGlyph(_ctx));
        Assert.Equal(0UL, ReadUInt64(GlyphSlotAddress));

        WriteUInt16(DefinitionAddress + 4, 0);
        WriteUInt16(DefinitionAddress + 6, 0x0100); // +6=0 cannot select nonzero +7.
        Assert.Equal(FontErrorInvalidArgument, FontExports.GenerateCharGlyph(_ctx));
        Assert.Equal(0UL, ReadUInt64(GlyphSlotAddress));

        WriteUInt16(DefinitionAddress + 6, 0x0401); // +7 values above one remain valid when +6 is set.
        Assert.Equal(0, FontExports.GenerateCharGlyph(_ctx));
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = GlyphSlotAddress;
        Assert.Equal(0, FontExports.DeleteGlyph(_ctx));

        WriteUInt16(DefinitionAddress + 6, 0x0101);
        WriteUInt64(DefinitionAddress + 8, Base + 0x1800);
        _ctx[CpuRegister.Rdi] = FontAddress;
        _ctx[CpuRegister.Rsi] = 0x41;
        Assert.Equal(FontErrorInvalidArgument, FontExports.GenerateCharGlyph(_ctx));
        Assert.Equal(0UL, ReadUInt64(GlyphSlotAddress));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(5, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void GenerateGlyph_RejectsDisallowedVerticalDetailBoundaries(
        byte verticalDetail,
        bool setFontSignBit)
    {
        WriteUInt16(FontAddress + 2, setFontSignBit ? (ushort)0x8000 : (ushort)0);
        WriteUInt16(DefinitionAddress + 4, 0);
        WriteUInt16(DefinitionAddress + 6, (ushort)((verticalDetail << 8) | 1));
        _ctx[CpuRegister.Rdi] = FontAddress;
        _ctx[CpuRegister.Rsi] = 0x41;
        _ctx[CpuRegister.Rdx] = DefinitionAddress;
        _ctx[CpuRegister.Rcx] = GlyphSlotAddress;

        Assert.Equal(FontErrorInvalidArgument, FontExports.GenerateCharGlyph(_ctx));
        Assert.Equal(0UL, ReadUInt64(GlyphSlotAddress));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void GenerateGlyph_AcceptsVerticalDetailOneThroughFourWithoutFontSignBit(byte verticalDetail)
    {
        WriteUInt16(FontAddress + 2, 0);
        WriteUInt16(DefinitionAddress + 4, 0);
        WriteUInt16(DefinitionAddress + 6, (ushort)((verticalDetail << 8) | 1));
        _ctx[CpuRegister.Rdi] = FontAddress;
        _ctx[CpuRegister.Rsi] = 0x41;
        _ctx[CpuRegister.Rdx] = DefinitionAddress;
        _ctx[CpuRegister.Rcx] = GlyphSlotAddress;

        Assert.Equal(0, FontExports.GenerateCharGlyph(_ctx));
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = GlyphSlotAddress;
        Assert.Equal(0, FontExports.DeleteGlyph(_ctx));
    }

    [Fact]
    public void DeleteGlyph_UsesValidExplicitMemoryOrStoredFallbackProvenance()
    {
        var explicitMemory = Base + 0x1200;
        var alternateMemory = Base + 0x1300;
        WriteUInt16(explicitMemory, 0x0F00);
        WriteUInt16(alternateMemory, 0x0F00);
        WriteUInt16(DefinitionAddress + 6, 0x0101);
        WriteUInt64(DefinitionAddress + 8, explicitMemory);
        _ctx[CpuRegister.Rdi] = FontAddress;
        _ctx[CpuRegister.Rsi] = 0x41;
        _ctx[CpuRegister.Rdx] = DefinitionAddress;
        _ctx[CpuRegister.Rcx] = GlyphSlotAddress;

        Assert.Equal(0, FontExports.GenerateCharGlyph(_ctx));
        var glyphAddress = ReadUInt64(GlyphSlotAddress);
        Assert.Equal(explicitMemory, ReadUInt64(glyphAddress + 0x18));

        _ctx[CpuRegister.Rdi] = alternateMemory;
        _ctx[CpuRegister.Rsi] = GlyphSlotAddress;
        Assert.Equal(0, FontExports.DeleteGlyph(_ctx));
        Assert.Equal(0UL, ReadUInt64(GlyphSlotAddress));
        Assert.False(_memory.IsAllocated(glyphAddress));

        _ctx[CpuRegister.Rdi] = FontAddress;
        _ctx[CpuRegister.Rsi] = 0x42;
        _ctx[CpuRegister.Rdx] = DefinitionAddress;
        _ctx[CpuRegister.Rcx] = GlyphSlotAddress;
        Assert.Equal(0, FontExports.GenerateCharGlyph(_ctx));
        glyphAddress = ReadUInt64(GlyphSlotAddress);

        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = GlyphSlotAddress;
        Assert.Equal(0, FontExports.DeleteGlyph(_ctx));
        Assert.Equal(0UL, ReadUInt64(GlyphSlotAddress));
        Assert.False(_memory.IsAllocated(glyphAddress));
    }

    [Fact]
    public void DeleteGlyph_RejectsInvalidExplicitMemoryAndRestoresOnFreeFailure()
    {
        WriteUInt16(DefinitionAddress + 6, 0x0101);
        _ctx[CpuRegister.Rdi] = FontAddress;
        _ctx[CpuRegister.Rsi] = 0x41;
        _ctx[CpuRegister.Rdx] = DefinitionAddress;
        _ctx[CpuRegister.Rcx] = GlyphSlotAddress;
        Assert.Equal(0, FontExports.GenerateCharGlyph(_ctx));
        var glyphAddress = ReadUInt64(GlyphSlotAddress);

        _ctx[CpuRegister.Rdi] = Base + 0x1800;
        _ctx[CpuRegister.Rsi] = GlyphSlotAddress;
        Assert.Equal(FontErrorInvalidState, FontExports.DeleteGlyph(_ctx));
        Assert.Equal(glyphAddress, ReadUInt64(GlyphSlotAddress));
        Assert.Equal((ushort)0x0F03, ReadUInt16(glyphAddress));

        _memory.FailNextFree = true;
        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(FontErrorInvalidState, FontExports.DeleteGlyph(_ctx));
        Assert.Equal(glyphAddress, ReadUInt64(GlyphSlotAddress));
        Assert.Equal((ushort)0x0F03, ReadUInt16(glyphAddress));
        Assert.True(_memory.IsAllocated(glyphAddress));

        Assert.Equal(0, FontExports.DeleteGlyph(_ctx));
        Assert.Equal(0UL, ReadUInt64(GlyphSlotAddress));
        Assert.False(_memory.IsAllocated(glyphAddress));
    }

    [Fact]
    public void RenderFallback_MutatesSurfaceAndWritesNonzeroClippedRectangleWithinExactBounds()
    {
        _ctx[CpuRegister.Rdi] = FontAddress;
        _ctx[CpuRegister.Rsi] = RendererAddress;
        Assert.Equal(0, FontExports.BindRenderer(_ctx));

        var surfaceBuffer = Base + 0x2_0000;
        WriteUInt64(SurfaceAddress, surfaceBuffer);
        WriteUInt32(SurfaceAddress + 0x08, 0x200);
        WriteUInt32(SurfaceAddress + 0x0C, 1);
        WriteUInt32(SurfaceAddress + 0x10, 320);
        WriteUInt32(SurfaceAddress + 0x14, 180);
        WriteUInt32(MetricsAddress + 0x20, 0xDEADBEEF);
        WriteUInt32(ResultAddress + 0x40, 0xA5A5A5A5);

        _ctx[CpuRegister.Rdi] = FontAddress;
        _ctx[CpuRegister.Rsi] = 0x4E;
        _ctx[CpuRegister.Rdx] = SurfaceAddress;
        _ctx[CpuRegister.Rcx] = MetricsAddress;
        _ctx[CpuRegister.R8] = ResultAddress;
        SetXmmSingle(0, 3.75f);
        SetXmmSingle(1, 4.5f);

        Assert.Equal(0, FontExports.RenderCharGlyphImageHorizontal(_ctx));
        Assert.Equal(8.0f, ReadSingle(MetricsAddress));
        Assert.Equal(16.0f, ReadSingle(MetricsAddress + 4));
        Assert.Equal(12.0f, ReadSingle(MetricsAddress + 0x10));
        Assert.Equal(surfaceBuffer, ReadUInt64(ResultAddress + 0x08));
        Assert.Equal(0x200U, ReadUInt32(ResultAddress + 0x10));
        Assert.Equal(1U, ReadUInt32(ResultAddress + 0x14));
        Assert.Equal(3U, ReadUInt32(ResultAddress + 0x18));
        Assert.Equal(4U, ReadUInt32(ResultAddress + 0x1C));
        Assert.Equal(8U, ReadUInt32(ResultAddress + 0x20));
        Assert.Equal(16U, ReadUInt32(ResultAddress + 0x24));
        Assert.Equal(12.0f, ReadSingle(ResultAddress + 0x30));
        Assert.Equal(8U, ReadUInt32(ResultAddress + 0x38));
        Assert.Equal(16U, ReadUInt32(ResultAddress + 0x3C));
        Assert.Equal((byte)0xFF, Read(surfaceBuffer + (4 * 0x200) + 3, 1)[0]);
        Assert.Equal(0xDEADBEEFU, ReadUInt32(MetricsAddress + 0x20));
        Assert.Equal(0xA5A5A5A5U, ReadUInt32(ResultAddress + 0x40));
    }

    [Fact]
    public void RenderFailure_ClearsExactOutputBounds()
    {
        Fill(MetricsAddress, 0x24, 0xCC);
        Fill(ResultAddress, 0x44, 0xDD);
        _ctx[CpuRegister.Rdi] = FontAddress;
        _ctx[CpuRegister.Rsi] = 0x4E;
        _ctx[CpuRegister.Rdx] = SurfaceAddress;
        _ctx[CpuRegister.Rcx] = MetricsAddress;
        _ctx[CpuRegister.R8] = ResultAddress;

        Assert.Equal(FontErrorRendererNotBound, FontExports.RenderCharGlyphImageHorizontal(_ctx));
        Assert.Equal(new byte[0x20], Read(MetricsAddress, 0x20));
        Assert.Equal(new byte[] { 0xCC, 0xCC, 0xCC, 0xCC }, Read(MetricsAddress + 0x20, 4));
        Assert.Equal(new byte[0x40], Read(ResultAddress, 0x40));
        Assert.Equal(new byte[] { 0xDD, 0xDD, 0xDD, 0xDD }, Read(ResultAddress + 0x40, 4));
    }

    [Fact]
    public void CreatedHandles_UnbindCloseAndDestroyWithFirmwareOwnershipShapes()
    {
        _ctx[CpuRegister.Rcx] = HandleSlotAddress;
        Assert.Equal(0, FontExports.CreateLibraryWithEdition(_ctx));
        var library = ReadUInt64(HandleSlotAddress);

        _ctx[CpuRegister.Rcx] = HandleSlotAddress + 8;
        Assert.Equal(0, FontExports.CreateRendererWithEdition(_ctx));
        var renderer = ReadUInt64(HandleSlotAddress + 8);

        _ctx[CpuRegister.Rdi] = library;
        _ctx[CpuRegister.R8] = HandleSlotAddress + 0x10;
        Assert.Equal(0, FontExports.OpenFontSet(_ctx));
        var font = ReadUInt64(HandleSlotAddress + 0x10);

        _ctx[CpuRegister.Rdi] = font;
        _ctx[CpuRegister.Rsi] = renderer;
        Assert.Equal(0, FontExports.BindRenderer(_ctx));
        Assert.Equal(0, FontExports.UnbindRenderer(_ctx));
        Assert.Equal(FontErrorRendererNotBound, FontExports.UnbindRenderer(_ctx));

        _ctx[CpuRegister.Rdi] = font;
        Assert.Equal(0, FontExports.CloseFont(_ctx));
        Assert.False(_memory.IsAllocated(font));
        Assert.Equal(font, ReadUInt64(HandleSlotAddress + 0x10)); // Close takes a direct handle.

        _ctx[CpuRegister.Rdi] = HandleSlotAddress + 8;
        Assert.Equal(0, FontExports.DestroyRenderer(_ctx));
        Assert.Equal(0UL, ReadUInt64(HandleSlotAddress + 8));
        Assert.False(_memory.IsAllocated(renderer));
    }

    [Fact]
    public void CreatedHandles_WriteThroughNativeMappedCallerSlots()
    {
        _ctx[CpuRegister.Rdi] = 0x18;
        Assert.Equal(0, KernelMemoryCompatExports.Malloc(_ctx));
        var slots = _ctx[CpuRegister.Rax];
        Assert.NotEqual(0UL, slots);

        try
        {
            _ctx[CpuRegister.Rcx] = slots;
            Assert.Equal(0, FontExports.CreateLibraryWithEdition(_ctx));
            var library = unchecked((ulong)Marshal.ReadInt64((nint)slots));
            Assert.NotEqual(0UL, library);

            _ctx[CpuRegister.Rcx] = slots + 8;
            Assert.Equal(0, FontExports.CreateRendererWithEdition(_ctx));
            var renderer = unchecked((ulong)Marshal.ReadInt64((nint)(slots + 8)));
            Assert.NotEqual(0UL, renderer);

            _ctx[CpuRegister.Rdi] = library;
            _ctx[CpuRegister.R8] = slots + 0x10;
            Assert.Equal(0, FontExports.OpenFontSet(_ctx));
            var font = unchecked((ulong)Marshal.ReadInt64((nint)(slots + 0x10)));
            Assert.NotEqual(0UL, font);
        }
        finally
        {
            _ctx[CpuRegister.Rdi] = slots;
            Assert.Equal(0, KernelMemoryCompatExports.Free(_ctx));
        }
    }

    [Fact]
    public void DestroyInvalidRenderer_LeavesCallerSlotUnchanged()
    {
        var invalidRenderer = Base + 0x1800;
        WriteUInt64(HandleSlotAddress, invalidRenderer);
        _ctx[CpuRegister.Rdi] = HandleSlotAddress;

        Assert.Equal(FontErrorInvalidRenderer, FontExports.DestroyRenderer(_ctx));
        Assert.Equal(invalidRenderer, ReadUInt64(HandleSlotAddress));
    }

    [Fact]
    public void DestroyRenderer_FreeFailureRestoresMagicAndRetainsCallerSlot()
    {
        _ctx[CpuRegister.Rcx] = HandleSlotAddress;
        Assert.Equal(0, FontExports.CreateRendererWithEdition(_ctx));
        var renderer = ReadUInt64(HandleSlotAddress);
        _memory.FailNextFree = true;

        _ctx[CpuRegister.Rdi] = HandleSlotAddress;
        Assert.Equal(FontErrorInvalidState, FontExports.DestroyRenderer(_ctx));
        Assert.Equal(renderer, ReadUInt64(HandleSlotAddress));
        Assert.Equal((ushort)0x0F07, ReadUInt16(renderer));
        Assert.True(_memory.IsAllocated(renderer));

        Assert.Equal(0, FontExports.DestroyRenderer(_ctx));
        Assert.Equal(0UL, ReadUInt64(HandleSlotAddress));
        Assert.False(_memory.IsAllocated(renderer));
    }

    [Fact]
    public void WeightModeValidation_UsesPinnedOrdering()
    {
        _ctx[CpuRegister.Rdi] = Base + 0x1800;
        _ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(FontErrorInvalidArgument, FontExports.SetEffectWeight(_ctx));

        _ctx[CpuRegister.Rdi] = FontAddress;
        Assert.Equal(FontErrorRendererNotBound, FontExports.SetupRenderEffectWeight(_ctx));
    }

    private void SetXmmSingle(int index, float value) =>
        _ctx.SetXmmRegister(index, BitConverter.SingleToUInt32Bits(value), 0);

    private void Fill(ulong address, int size, byte value)
    {
        var bytes = new byte[size];
        Array.Fill(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private byte[] Read(ulong address, int size)
    {
        var bytes = new byte[size];
        Assert.True(_memory.TryRead(address, bytes));
        return bytes;
    }

    private ushort ReadUInt16(ulong address) =>
        BinaryPrimitives.ReadUInt16LittleEndian(Read(address, sizeof(ushort)));

    private uint ReadUInt32(ulong address) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Read(address, sizeof(uint)));

    private ulong ReadUInt64(ulong address) =>
        BinaryPrimitives.ReadUInt64LittleEndian(Read(address, sizeof(ulong)));

    private float ReadSingle(ulong address) =>
        BinaryPrimitives.ReadSingleLittleEndian(Read(address, sizeof(float)));

    private void WriteUInt16(ulong address, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

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

    private sealed class AllocatingCpuMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly FakeCpuMemory _memory;
        private readonly HashSet<ulong> _allocations = [];
        private readonly ulong _endAddress;
        private ulong _nextAddress;

        public bool FailNextFree { get; set; }

        public AllocatingCpuMemory(ulong baseAddress, int size, ulong allocationStart)
        {
            _memory = new FakeCpuMemory(baseAddress, size);
            _nextAddress = allocationStart;
            _endAddress = baseAddress + (ulong)size;
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination) =>
            _memory.TryRead(virtualAddress, destination);

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) =>
            _memory.TryWrite(virtualAddress, source);

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = 0;
            if (size == 0 || alignment == 0 || (alignment & (alignment - 1)) != 0)
            {
                return false;
            }

            var aligned = (_nextAddress + alignment - 1) & ~(alignment - 1);
            if (aligned > _endAddress || size > _endAddress - aligned)
            {
                return false;
            }

            address = aligned;
            _nextAddress = aligned + size;
            _allocations.Add(address);
            return true;
        }

        public bool TryFreeGuestMemory(ulong address)
        {
            if (FailNextFree)
            {
                FailNextFree = false;
                return false;
            }

            return _allocations.Remove(address);
        }

        public bool IsAllocated(ulong address) => _allocations.Contains(address);
    }

}
