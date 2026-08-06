// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Font;

public static class FontExports
{
    private const int FontErrorInvalidArgument = unchecked((int)0x80460002);
    private const int FontErrorInvalidState = unchecked((int)0x80460003);
    private const int FontErrorInvalidFont = unchecked((int)0x80460005);
    private const int FontErrorInvalidGlyph = unchecked((int)0x80460006);
    private const int FontErrorInvalidRenderer = unchecked((int)0x80460007);
    private const int FontErrorOutOfMemory = unchecked((int)0x80460010);
    private const int FontErrorInvalidCodepoint = unchecked((int)0x80460041);
    private const int FontErrorRendererAlreadyBound = unchecked((int)0x80460060);
    private const int FontErrorRendererNotBound = unchecked((int)0x80460061);

    private const ushort MemoryMagic = 0x0F00;
    private const ushort LibraryMagic = 0x0F01;
    private const ushort FontMagic = 0x0F02;
    private const ushort GlyphMagic = 0x0F03;
    private const ushort RendererMagic = 0x0F07;
    private const ushort OwnedObjectFlag = 0x0010;

    private const int OpaqueObjectSize = 0x100;
    private const int SyntheticGlyphSize = 0x20;
    private const int LayoutSize = 0x0C;
    private const int GlyphMetricsSize = 0x20;
    private const int RenderResultSize = 0x40;
    private const int FallbackPatternWidth = 5;
    private const int FallbackPatternHeight = 7;
    private const int FallbackMaxDimension = 128;
    private const ulong SyntheticGlyphOwnershipCookie = 0x5348_454D_5546_4E54; // "SHEMUFNT"

    private static readonly object AllocationGate = new();
    // Firmware uses per-handle locks. A single HLE gate keeps the fallback state
    // machine coherent until handles have first-class synchronized host objects.
    private static readonly object FontStateGate = new();
    private static ulong _librarySelectionAddress;
    private static ulong _rendererSelectionAddress;

    [SysAbiExport(
        Nid = "whrS4oksXc4",
        ExportName = "sceFontMemoryInit",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int MemoryInit(CpuContext ctx)
    {
        var descriptorAddress = ctx[CpuRegister.Rdi];
        var regionAddress = ctx[CpuRegister.Rsi];
        var regionSize = (uint)ctx[CpuRegister.Rdx];
        var interfaceAddress = ctx[CpuRegister.Rcx];
        var mspaceAddress = ctx[CpuRegister.R8];
        var destroyCallback = ctx[CpuRegister.R9];
        if (descriptorAddress == 0 ||
            !TryWriteUInt32(ctx, descriptorAddress, MemoryMagic) ||
            !TryWriteUInt32(ctx, descriptorAddress + 0x04, regionSize) ||
            !TryWriteUInt64(ctx, descriptorAddress + 0x08, regionAddress) ||
            !TryWriteUInt64(ctx, descriptorAddress + 0x10, mspaceAddress) ||
            !TryWriteUInt64(ctx, descriptorAddress + 0x18, interfaceAddress) ||
            !TryWriteUInt64(ctx, descriptorAddress + 0x20, destroyCallback) ||
            !TryWriteUInt64(ctx, descriptorAddress + 0x28, 0) ||
            !TryWriteUInt64(ctx, descriptorAddress + 0x30, 0) ||
            !TryWriteUInt64(ctx, descriptorAddress + 0x38, mspaceAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "oM+XCzVG3oM",
        ExportName = "sceFontSelectLibraryFt",
        Target = Generation.Gen5,
        LibraryName = "libSceFontFt")]
    public static int SelectLibraryFt(CpuContext ctx) =>
        ReturnSelection(ctx, ref _librarySelectionAddress, 0x38);

    [SysAbiExport(
        Nid = "Xx974EW-QFY",
        ExportName = "sceFontSelectRendererFt",
        Target = Generation.Gen5,
        LibraryName = "libSceFontFt")]
    public static int SelectRendererFt(CpuContext ctx) =>
        ReturnSelection(ctx, ref _rendererSelectionAddress, 0x100);

    [SysAbiExport(
        Nid = "n590hj5Oe-k",
        ExportName = "sceFontCreateLibraryWithEdition",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int CreateLibraryWithEdition(CpuContext ctx) =>
        CreateOpaqueHandle(ctx, ctx[CpuRegister.Rcx], OpaqueObjectSize, LibraryMagic);

    [SysAbiExport(
        Nid = "WaSFJoRWXaI",
        ExportName = "sceFontCreateRendererWithEdition",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int CreateRendererWithEdition(CpuContext ctx) =>
        CreateOpaqueHandle(ctx, ctx[CpuRegister.Rcx], OpaqueObjectSize, RendererMagic);

    [SysAbiExport(
        Nid = "3OdRkSjOcog",
        ExportName = "sceFontBindRenderer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int BindRenderer(CpuContext ctx)
    {
        lock (FontStateGate)
        {
            var fontAddress = ctx[CpuRegister.Rdi];
            var rendererAddress = ctx[CpuRegister.Rsi];
            if (!HasMagic(ctx, fontAddress, FontMagic))
            {
                return SetReturn(ctx, FontErrorInvalidFont);
            }

            if (!TryReadUInt64(ctx, fontAddress + 0x30, out var boundRenderer))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            // FW11 checks the current binding before validating the candidate.
            if (boundRenderer != 0)
            {
                return SetReturn(ctx, FontErrorRendererAlreadyBound);
            }

            if (!HasMagic(ctx, rendererAddress, RendererMagic))
            {
                return SetReturn(ctx, FontErrorInvalidRenderer);
            }

            // FW11 snapshots the font's configured scale/effects into its render
            // state, then publishes the renderer binding.
            Span<byte> configuredState = stackalloc byte[0x28];
            if (!TryReadMemory(ctx, fontAddress + 0x40, configuredState) ||
                !TryWriteMemory(ctx, fontAddress + 0x68, configuredState) ||
                !TryWriteUInt64(ctx, fontAddress + 0x30, rendererAddress))
            {
                _ = TryWriteUInt64(ctx, fontAddress + 0x30, 0);
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return SetSuccess(ctx);
        }
    }

    [SysAbiExport(
        Nid = "1QjhKxrsOB8",
        ExportName = "sceFontUnbindRenderer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int UnbindRenderer(CpuContext ctx)
    {
        lock (FontStateGate)
        {
            var fontAddress = ctx[CpuRegister.Rdi];
            if (!HasMagic(ctx, fontAddress, FontMagic))
            {
                return SetReturn(ctx, FontErrorInvalidFont);
            }

            if (!TryReadUInt64(ctx, fontAddress + 0x30, out var rendererAddress))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (rendererAddress == 0)
            {
                return SetReturn(ctx, FontErrorRendererNotBound);
            }

            return TryWriteUInt64(ctx, fontAddress + 0x30, 0)
                ? SetSuccess(ctx)
                : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
    }

    [SysAbiExport(
        Nid = "N1EBMeGhf7E",
        ExportName = "sceFontSetScalePixel",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetScalePixel(CpuContext ctx)
    {
        lock (FontStateGate)
        {
            var fontAddress = ctx[CpuRegister.Rdi];
            if (!HasMagic(ctx, fontAddress, FontMagic))
            {
                return SetReturn(ctx, FontErrorInvalidFont);
            }

            var width = ReadXmmSingle(ctx, 0);
            var height = ReadXmmSingle(ctx, 1);
            if (!TryReadSingleBits(ctx, fontAddress + 0x50, out var oldWidth) ||
                !TryReadSingleBits(ctx, fontAddress + 0x54, out var oldHeight) ||
                !TryWriteSingle(ctx, fontAddress + 0x50, width) ||
                !TryWriteSingle(ctx, fontAddress + 0x54, height) ||
                !TryClear(ctx, fontAddress + 0x48, sizeof(ulong)))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if ((oldWidth != BitConverter.SingleToUInt32Bits(width) ||
                 oldHeight != BitConverter.SingleToUInt32Bits(height)) &&
                !TryClear(ctx, fontAddress + 0x94, sizeof(uint)))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return SetSuccess(ctx);
        }
    }

    [SysAbiExport(
        Nid = "TMtqoFQjjbA",
        ExportName = "sceFontSetEffectSlant",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetEffectSlant(CpuContext ctx) =>
        StoreFontEffect(ctx, offset: 0x60, Math.Clamp(ReadXmmSingle(ctx, 0), -1.0f, 1.0f),
            requireRenderer: false, stateOffset: 0, stateSize: 0, cacheOffset: 0x94, cacheSize: sizeof(uint));

    [SysAbiExport(
        Nid = "v0phZwa4R5o",
        ExportName = "sceFontSetEffectWeight",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetEffectWeight(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] != 0)
        {
            return SetReturn(ctx, FontErrorInvalidArgument);
        }

        return StoreFontWeight(ctx, firstOffset: 0x58, secondOffset: 0x5C,
            requireRenderer: false, stateOffset: 0, stateSize: 0, cacheOffset: 0x94, cacheSize: sizeof(uint));
    }

    [SysAbiExport(
        Nid = "6vGCkkQJOcI",
        ExportName = "sceFontSetupRenderScalePixel",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetupRenderScalePixel(CpuContext ctx)
    {
        lock (FontStateGate)
        {
            var fontAddress = ctx[CpuRegister.Rdi];
            var validation = ValidateFontWithRenderer(ctx, fontAddress);
            if (validation != 0)
            {
                return SetReturn(ctx, validation);
            }

            var width = ReadXmmSingle(ctx, 0);
            var height = ReadXmmSingle(ctx, 1);
            if (!TryReadSingleBits(ctx, fontAddress + 0x78, out var oldWidth) ||
                !TryReadSingleBits(ctx, fontAddress + 0x7C, out var oldHeight) ||
                !TryWriteSingle(ctx, fontAddress + 0x78, width) ||
                !TryWriteSingle(ctx, fontAddress + 0x7C, height) ||
                !TryClear(ctx, fontAddress + 0x70, sizeof(ulong)))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if ((oldWidth != BitConverter.SingleToUInt32Bits(width) ||
                 oldHeight != BitConverter.SingleToUInt32Bits(height)) &&
                !TryClear(ctx, fontAddress + 0x98, sizeof(ushort)))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return SetSuccess(ctx);
        }
    }

    [SysAbiExport(
        Nid = "lz9y9UFO2UU",
        ExportName = "sceFontSetupRenderEffectSlant",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetupRenderEffectSlant(CpuContext ctx) =>
        StoreFontEffect(ctx, offset: 0x88, Math.Clamp(ReadXmmSingle(ctx, 0), -1.0f, 1.0f),
            requireRenderer: true, stateOffset: 0, stateSize: 0, cacheOffset: 0x98, cacheSize: sizeof(ushort));

    [SysAbiExport(
        Nid = "XIGorvLusDQ",
        ExportName = "sceFontSetupRenderEffectWeight",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetupRenderEffectWeight(CpuContext ctx)
    {
        lock (FontStateGate)
        {
            var fontAddress = ctx[CpuRegister.Rdi];
            var validation = ValidateFontWithRenderer(ctx, fontAddress);
            if (validation != 0)
            {
                return SetReturn(ctx, validation);
            }

            if (ctx[CpuRegister.Rsi] != 0)
            {
                return SetReturn(ctx, FontErrorInvalidArgument);
            }

            return StoreFontWeight(ctx, firstOffset: 0x80, secondOffset: 0x84,
                requireRenderer: true, stateOffset: 0, stateSize: 0, cacheOffset: 0x98, cacheSize: sizeof(ushort));
        }
    }

    [SysAbiExport(
        Nid = "imxVx8lm+KM",
        ExportName = "sceFontGetHorizontalLayout",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetHorizontalLayout(CpuContext ctx) =>
        WriteFallbackLayout(ctx, vertical: false);

    [SysAbiExport(
        Nid = "3BrWWFU+4ts",
        ExportName = "sceFontGetVerticalLayout",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetVerticalLayout(CpuContext ctx) =>
        WriteFallbackLayout(ctx, vertical: true);

    [SysAbiExport(
        Nid = "cKYtVmeSTcw",
        ExportName = "sceFontOpenFontSet",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int OpenFontSet(CpuContext ctx) =>
        CreateFontHandle(ctx, ctx[CpuRegister.R8], ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "KXUpebrFk1U",
        ExportName = "sceFontOpenFontMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int OpenFontMemory(CpuContext ctx) =>
        CreateFontHandle(ctx, ctx[CpuRegister.R8], ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "JzCH3SCFnAU",
        ExportName = "sceFontOpenFontInstance",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int OpenFontInstance(CpuContext ctx)
    {
        var sourceHandle = ctx[CpuRegister.Rdi];
        var setupHandle = ctx[CpuRegister.Rsi];
        var outputAddress = ctx[CpuRegister.Rdx];
        if (outputAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (setupHandle != 0)
        {
            return TryWriteUInt64(ctx, outputAddress, setupHandle)
                ? SetSuccess(ctx)
                : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryAllocateOpaque(ctx, OpaqueObjectSize, out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (sourceHandle != 0)
        {
            Span<byte> source = stackalloc byte[OpaqueObjectSize];
            if (TryReadMemory(ctx, sourceHandle, source))
            {
                _ = TryWriteMemory(ctx, handle, source);
            }
        }

        if (!TryWriteUInt16(ctx, handle, FontMagic) ||
            !TryWriteUInt16(ctx, handle + 0x02, OwnedObjectFlag) ||
            !TryWriteUInt64(ctx, outputAddress, handle))
        {
            FreeGuestAllocation(ctx, handle);
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "C-4Qw5Srlyw",
        ExportName = "sceFontGenerateCharGlyph",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GenerateCharGlyph(CpuContext ctx)
    {
        lock (FontStateGate)
        {
            var fontAddress = ctx[CpuRegister.Rdi];
            var codepoint = unchecked((uint)ctx[CpuRegister.Rsi]);
            var definitionAddress = ctx[CpuRegister.Rdx];
            var outputAddress = ctx[CpuRegister.Rcx];

            if (!HasMagic(ctx, fontAddress, FontMagic))
            {
                return ReturnGlyphFailure(ctx, outputAddress, FontErrorInvalidFont);
            }

            if (codepoint == 0)
            {
                return ReturnGlyphFailure(ctx, outputAddress, FontErrorInvalidCodepoint);
            }

            if (outputAddress == 0)
            {
                return SetReturn(ctx, FontErrorInvalidArgument);
            }

            if (!TryWriteUInt64(ctx, outputAddress, 0))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (!TryReadUInt64(ctx, fontAddress + 0x28, out var libraryAddress) ||
                !HasMagic(ctx, libraryAddress, LibraryMagic))
            {
                return SetReturn(ctx, FontErrorInvalidFont);
            }

            // The host guest allocator backs all synthetic glyphs. Keep a
            // non-null owner marker so RDI=0 deletion has usable provenance.
            var allocatorProvenance = fontAddress;
            if (definitionAddress != 0)
            {
                if (!TryReadUInt16(ctx, definitionAddress + 0x04, out var definitionFlags) ||
                    !TryReadUInt16(ctx, fontAddress + 0x02, out var fontFlags) ||
                    !TryReadByte(ctx, definitionAddress + 0x06, out var horizontalDetail) ||
                    !TryReadByte(ctx, definitionAddress + 0x07, out var verticalDetail) ||
                    !TryReadUInt64(ctx, definitionAddress + 0x08, out var allocatorOverride) ||
                    (definitionFlags & 0xFFEE) != 0 ||
                    (horizontalDetail == 0 && verticalDetail != 0) ||
                    (horizontalDetail != 0 &&
                     (verticalDetail == 0 ||
                      verticalDetail >= 5 ||
                      (verticalDetail is >= 2 and <= 4 && (fontFlags & 0x8000) != 0))) ||
                    (allocatorOverride != 0 && !HasMagic(ctx, allocatorOverride, MemoryMagic)))
                {
                    return SetReturn(ctx, FontErrorInvalidArgument);
                }

                if (allocatorOverride != 0)
                {
                    allocatorProvenance = allocatorOverride;
                }
            }

            // Guest allocator callbacks are not executable in this HLE path, so
            // both default and explicit descriptors use the host guest allocator.
            // The descriptor is retained for DeleteGlyph ownership validation.
            if (!TryAllocateOpaque(ctx, SyntheticGlyphSize, out var glyphAddress))
            {
                return SetReturn(ctx, FontErrorOutOfMemory);
            }

            if (!TryWriteUInt16(ctx, glyphAddress, GlyphMagic) ||
                !TryWriteUInt16(ctx, glyphAddress + 0x02, 0) ||
                !TryWriteUInt32(ctx, glyphAddress + 0x04, codepoint) ||
                !TryWriteUInt64(ctx, glyphAddress + 0x08, fontAddress) ||
                !TryWriteUInt64(ctx, glyphAddress + 0x10, SyntheticGlyphOwnershipCookie) ||
                !TryWriteUInt64(ctx, glyphAddress + 0x18, allocatorProvenance) ||
                !TryWriteUInt64(ctx, outputAddress, glyphAddress))
            {
                FreeGuestAllocation(ctx, glyphAddress);
                _ = TryWriteUInt64(ctx, outputAddress, 0);
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return SetSuccess(ctx);
        }
    }

    [SysAbiExport(
        Nid = "8-zmgsxkBek",
        ExportName = "sceFontGlyphDefineAttribute",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GlyphDefineAttribute(CpuContext ctx)
    {
        lock (FontStateGate)
        {
            var glyphAddress = ctx[CpuRegister.Rdi];
            var attribute = unchecked((int)ctx[CpuRegister.Rsi]);
            var previousAttributeAddress = ctx[CpuRegister.Rdx];
            if (!HasMagic(ctx, glyphAddress, GlyphMagic) ||
                !TryReadUInt16(ctx, glyphAddress + 0x02, out var flags))
            {
                _ = previousAttributeAddress == 0 || TryWriteInt32(ctx, previousAttributeAddress, 0);
                return SetReturn(ctx, FontErrorInvalidGlyph);
            }

            var previousAttribute = (flags & 0x02) == 0 ? 0x10 : 0x11;
            ushort updatedFlags;
            switch (attribute)
            {
                case 0x10:
                    updatedFlags = (ushort)(flags & ~0x02);
                    break;
                case 0x11:
                    updatedFlags = (ushort)(flags | 0x02);
                    break;
                default:
                    _ = previousAttributeAddress == 0 || TryWriteInt32(ctx, previousAttributeAddress, 0);
                    return SetReturn(ctx, FontErrorInvalidArgument);
            }

            if (!TryWriteUInt16(ctx, glyphAddress + 0x02, updatedFlags) ||
                (previousAttributeAddress != 0 &&
                 !TryWriteInt32(ctx, previousAttributeAddress, previousAttribute)))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return SetSuccess(ctx);
        }
    }

    [SysAbiExport(
        Nid = "LHDoRWVFGqk",
        ExportName = "sceFontDeleteGlyph",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int DeleteGlyph(CpuContext ctx)
    {
        lock (FontStateGate)
        {
            var explicitMemoryAddress = ctx[CpuRegister.Rdi];
            var glyphSlotAddress = ctx[CpuRegister.Rsi];
            if (glyphSlotAddress == 0)
            {
                return SetReturn(ctx, FontErrorInvalidArgument);
            }

            if (!TryReadUInt64(ctx, glyphSlotAddress, out var glyphAddress) ||
                !HasMagic(ctx, glyphAddress, GlyphMagic))
            {
                return SetReturn(ctx, FontErrorInvalidGlyph);
            }

            if (!TryReadUInt64(ctx, glyphAddress + 0x10, out var cookie) ||
                !TryReadUInt64(ctx, glyphAddress + 0x08, out var ownerFontAddress) ||
                !TryReadUInt64(ctx, glyphAddress + 0x18, out var allocatorProvenance) ||
                cookie != SyntheticGlyphOwnershipCookie ||
                ctx.Memory is not IGuestMemoryAllocator allocator)
            {
                return SetReturn(ctx, FontErrorInvalidState);
            }

            var selectedMemoryAddress = explicitMemoryAddress != 0
                ? explicitMemoryAddress
                : allocatorProvenance;
            var selectedMemoryIsValid = explicitMemoryAddress != 0
                ? HasMagic(ctx, selectedMemoryAddress, MemoryMagic)
                : HasMagic(ctx, selectedMemoryAddress, MemoryMagic) ||
                  (selectedMemoryAddress == ownerFontAddress &&
                   HasMagic(ctx, ownerFontAddress, FontMagic));
            if (!selectedMemoryIsValid)
            {
                return SetReturn(ctx, FontErrorInvalidState);
            }

            if (!TryWriteUInt16(ctx, glyphAddress, 0))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (!TryWriteUInt64(ctx, glyphSlotAddress, 0))
            {
                _ = TryWriteUInt16(ctx, glyphAddress, GlyphMagic);
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (!allocator.TryFreeGuestMemory(glyphAddress))
            {
                _ = TryWriteUInt16(ctx, glyphAddress, GlyphMagic);
                _ = TryWriteUInt64(ctx, glyphSlotAddress, glyphAddress);
                return SetReturn(ctx, FontErrorInvalidState);
            }

            return SetSuccess(ctx);
        }
    }

    [SysAbiExport(
        Nid = "kAenWy1Zw5o",
        ExportName = "sceFontRenderCharGlyphImageHorizontal",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int RenderCharGlyphImageHorizontal(CpuContext ctx)
    {
        lock (FontStateGate)
        {
            var fontAddress = ctx[CpuRegister.Rdi];
            var codepoint = unchecked((uint)ctx[CpuRegister.Rsi]);
            var surfaceAddress = ctx[CpuRegister.Rdx];
            var metricsAddress = ctx[CpuRegister.Rcx];
            var resultAddress = ctx[CpuRegister.R8];

            if (!HasMagic(ctx, fontAddress, FontMagic))
            {
                return ReturnRenderFailure(ctx, metricsAddress, resultAddress, FontErrorInvalidFont);
            }

            if (codepoint == 0)
            {
                return ReturnRenderFailure(ctx, metricsAddress, resultAddress, FontErrorInvalidCodepoint);
            }

            if (surfaceAddress == 0 || metricsAddress == 0 || resultAddress == 0)
            {
                return ReturnRenderFailure(ctx, metricsAddress, resultAddress, FontErrorInvalidArgument);
            }

            var validation = ValidateFontWithRenderer(ctx, fontAddress);
            if (validation != 0)
            {
                return ReturnRenderFailure(ctx, metricsAddress, resultAddress, validation);
            }

            Span<byte> surface = stackalloc byte[0x28];
            if (!TryReadMemory(ctx, surfaceAddress, surface) ||
                !TryReadFallbackGeometry(ctx, fontAddress, renderState: true, out var geometry))
            {
                return ReturnRenderFailure(ctx, metricsAddress, resultAddress,
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            var rasterResult = RasterizeFallbackGlyph(
                ctx,
                surface,
                codepoint,
                ReadXmmSingle(ctx, 0),
                ReadXmmSingle(ctx, 1),
                geometry,
                out var updateRectangle);
            if (rasterResult != 0)
            {
                return ReturnRenderFailure(ctx, metricsAddress, resultAddress, rasterResult);
            }

            if (!TryWriteUInt16(ctx, fontAddress + 0x9A, 1) ||
                !WriteFallbackGlyphMetrics(ctx, metricsAddress, geometry) ||
                !WriteFallbackRenderResult(ctx, resultAddress, surface, geometry, updateRectangle))
            {
                return ReturnRenderFailure(ctx, metricsAddress, resultAddress,
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return SetSuccess(ctx);
        }
    }

    [SysAbiExport(
        Nid = "vzHs3C8lWJk",
        ExportName = "sceFontCloseFont",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int CloseFont(CpuContext ctx)
    {
        lock (FontStateGate)
        {
            var fontAddress = ctx[CpuRegister.Rdi];
            if (!HasMagic(ctx, fontAddress, FontMagic) ||
                !TryReadUInt64(ctx, fontAddress + 0x28, out var libraryAddress) ||
                !HasMagic(ctx, libraryAddress, LibraryMagic) ||
                !TryReadUInt16(ctx, fontAddress + 0x02, out var flags))
            {
                return SetReturn(ctx, FontErrorInvalidFont);
            }

            _ = TryWriteUInt64(ctx, fontAddress + 0x30, 0);
            if ((flags & OwnedObjectFlag) == 0)
            {
                return TryWriteUInt32(ctx, fontAddress, 0)
                    ? SetSuccess(ctx)
                    : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (!TryWriteUInt16(ctx, fontAddress, 0) ||
                ctx.Memory is not IGuestMemoryAllocator allocator)
            {
                return SetReturn(ctx, FontErrorInvalidFont);
            }

            if (!allocator.TryFreeGuestMemory(fontAddress))
            {
                _ = TryWriteUInt16(ctx, fontAddress, FontMagic);
                return SetReturn(ctx, FontErrorInvalidState);
            }

            return SetSuccess(ctx);
        }
    }

    [SysAbiExport(
        Nid = "exAxkyVLt0s",
        ExportName = "sceFontDestroyRenderer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int DestroyRenderer(CpuContext ctx)
    {
        lock (FontStateGate)
        {
            var rendererSlotAddress = ctx[CpuRegister.Rdi];
            if (rendererSlotAddress == 0)
            {
                return SetReturn(ctx, FontErrorInvalidArgument);
            }

            if (!TryReadUInt64(ctx, rendererSlotAddress, out var rendererAddress) ||
                !HasMagic(ctx, rendererAddress, RendererMagic))
            {
                return SetReturn(ctx, FontErrorInvalidRenderer);
            }

            if (!TryWriteUInt16(ctx, rendererAddress, 0))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (!TryWriteUInt64(ctx, rendererSlotAddress, 0))
            {
                _ = TryWriteUInt16(ctx, rendererAddress, RendererMagic);
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (ctx.Memory is not IGuestMemoryAllocator allocator ||
                !allocator.TryFreeGuestMemory(rendererAddress))
            {
                _ = TryWriteUInt16(ctx, rendererAddress, RendererMagic);
                _ = TryWriteUInt64(ctx, rendererSlotAddress, rendererAddress);
                return SetReturn(ctx, FontErrorInvalidState);
            }

            return SetSuccess(ctx);
        }
    }

    [SysAbiExport(
        Nid = "SsRbbCiWoGw",
        ExportName = "sceFontSupportSystemFonts",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SupportSystemFonts(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "mz2iTY0MK4A",
        ExportName = "sceFontSupportExternalFonts",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SupportExternalFonts(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "CUKn5pX-NVY",
        ExportName = "sceFontAttachDeviceCacheBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int AttachDeviceCacheBuffer(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "IQtleGLL5pQ",
        ExportName = "sceFontGetRenderCharGlyphMetrics",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetRenderCharGlyphMetrics(CpuContext ctx)
    {
        var metricsAddress = ctx[CpuRegister.Rdx];
        if (metricsAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return WriteSyntheticGlyphMetrics(ctx, metricsAddress)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "gdUCnU0gHdI",
        ExportName = "sceFontRenderSurfaceInit",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int RenderSurfaceInit(CpuContext ctx)
    {
        var surfaceAddress = ctx[CpuRegister.Rdi];
        var bufferAddress = ctx[CpuRegister.Rsi];
        var widthBytes = (uint)ctx[CpuRegister.Rdx];
        var pixelBytes = (uint)ctx[CpuRegister.Rcx] & 0xFF;
        var width = (uint)ctx[CpuRegister.R8];
        var height = (uint)ctx[CpuRegister.R9];
        if (surfaceAddress == 0 ||
            !TryWriteUInt64(ctx, surfaceAddress, bufferAddress) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x08, widthBytes) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x0C, pixelBytes) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x10, width) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x14, height) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x18, 0) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x1C, 0) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x20, width) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x24, height))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    private static int StoreFontEffect(
        CpuContext ctx,
        ulong offset,
        float value,
        bool requireRenderer,
        ulong stateOffset,
        int stateSize,
        ulong cacheOffset,
        int cacheSize)
    {
        lock (FontStateGate)
        {
            var fontAddress = ctx[CpuRegister.Rdi];
            var validation = requireRenderer
                ? ValidateFontWithRenderer(ctx, fontAddress)
                : HasMagic(ctx, fontAddress, FontMagic) ? 0 : FontErrorInvalidFont;
            if (validation != 0)
            {
                return SetReturn(ctx, validation);
            }

            if (!TryReadSingleBits(ctx, fontAddress + offset, out var oldBits) ||
                !TryWriteSingle(ctx, fontAddress + offset, value) ||
                (stateSize != 0 && !TryClear(ctx, fontAddress + stateOffset, stateSize)))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (oldBits != BitConverter.SingleToUInt32Bits(value) &&
                !TryClear(ctx, fontAddress + cacheOffset, cacheSize))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return SetSuccess(ctx);
        }
    }

    private static int StoreFontWeight(
        CpuContext ctx,
        ulong firstOffset,
        ulong secondOffset,
        bool requireRenderer,
        ulong stateOffset,
        int stateSize,
        ulong cacheOffset,
        int cacheSize)
    {
        lock (FontStateGate)
        {
            var fontAddress = ctx[CpuRegister.Rdi];
            var validation = requireRenderer
                ? ValidateFontWithRenderer(ctx, fontAddress)
                : HasMagic(ctx, fontAddress, FontMagic) ? 0 : FontErrorInvalidFont;
            if (validation != 0)
            {
                return SetReturn(ctx, validation);
            }

            var first = Math.Clamp(ReadXmmSingle(ctx, 0) - 1.0f, -0.04f, 0.04f);
            var second = Math.Clamp(ReadXmmSingle(ctx, 1) - 1.0f, -0.04f, 0.04f);
            if (!TryReadSingleBits(ctx, fontAddress + firstOffset, out var oldFirst) ||
                !TryReadSingleBits(ctx, fontAddress + secondOffset, out var oldSecond) ||
                !TryWriteSingle(ctx, fontAddress + firstOffset, first) ||
                !TryWriteSingle(ctx, fontAddress + secondOffset, second) ||
                (stateSize != 0 && !TryClear(ctx, fontAddress + stateOffset, stateSize)))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if ((oldFirst != BitConverter.SingleToUInt32Bits(first) ||
                 oldSecond != BitConverter.SingleToUInt32Bits(second)) &&
                !TryClear(ctx, fontAddress + cacheOffset, cacheSize))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            return SetSuccess(ctx);
        }
    }

    private static int WriteFallbackLayout(CpuContext ctx, bool vertical)
    {
        lock (FontStateGate)
        {
            var fontAddress = ctx[CpuRegister.Rdi];
            var layoutAddress = ctx[CpuRegister.Rsi];
            if (!HasMagic(ctx, fontAddress, FontMagic))
            {
                _ = layoutAddress == 0 || TryClear(ctx, layoutAddress, LayoutSize);
                return SetReturn(ctx, FontErrorInvalidFont);
            }

            if (layoutAddress == 0)
            {
                return SetReturn(ctx, FontErrorInvalidArgument);
            }

            if (!TryReadFallbackGeometry(ctx, fontAddress, renderState: false, out var geometry))
            {
                _ = TryClear(ctx, layoutAddress, LayoutSize);
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            // Deterministic HLE geometry derived from stored scale/effect state;
            // these values do not claim firmware font-backend fidelity.
            Span<float> values = stackalloc float[3];
            if (vertical)
            {
                values[0] = geometry.SlantOffset;
                values[1] = geometry.Height;
                values[2] = geometry.Width - geometry.BaseWidth;
            }
            else
            {
                values[0] = geometry.Advance;
                values[1] = geometry.Height;
                values[2] = geometry.SlantOffset;
            }

            for (var index = 0; index < values.Length; index++)
            {
                if (!TryWriteSingle(ctx, layoutAddress + (ulong)(index * sizeof(float)), values[index]))
                {
                    _ = TryClear(ctx, layoutAddress, LayoutSize);
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }

            return SetSuccess(ctx);
        }
    }

    private static int ReturnGlyphFailure(CpuContext ctx, ulong outputAddress, int result)
    {
        if (outputAddress != 0)
        {
            _ = TryWriteUInt64(ctx, outputAddress, 0);
        }

        return SetReturn(ctx, result);
    }

    private static int ReturnRenderFailure(CpuContext ctx, ulong metricsAddress, ulong resultAddress, int result)
    {
        if (metricsAddress != 0)
        {
            _ = TryClear(ctx, metricsAddress, GlyphMetricsSize);
        }

        if (resultAddress != 0)
        {
            _ = TryClear(ctx, resultAddress, RenderResultSize);
        }

        return SetReturn(ctx, result);
    }

    private static bool WriteSyntheticGlyphMetrics(CpuContext ctx, ulong metricsAddress)
    {
        // Width, height, horizontal bearing/advance, vertical bearing/advance.
        // These are deterministic fallback metrics, not firmware raster metrics.
        ReadOnlySpan<float> values = [8.0f, 16.0f, 0.0f, 12.0f, 8.0f, 0.0f, 0.0f, 16.0f];
        Span<byte> metrics = stackalloc byte[GlyphMetricsSize];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                metrics[(index * sizeof(float))..],
                BitConverter.SingleToUInt32Bits(values[index]));
        }

        return TryWriteMemory(ctx, metricsAddress, metrics);
    }

    private readonly record struct FallbackGeometry(
        float BaseWidth,
        float Width,
        float Height,
        float Advance,
        float SlantOffset,
        uint PixelWidth,
        uint PixelHeight);

    private readonly record struct FallbackRectangle(uint X, uint Y, uint Width, uint Height);

    private static bool TryReadFallbackGeometry(
        CpuContext ctx,
        ulong fontAddress,
        bool renderState,
        out FallbackGeometry geometry)
    {
        geometry = default;
        var scaleOffset = renderState ? 0x78UL : 0x50UL;
        var weightOffset = renderState ? 0x80UL : 0x58UL;
        var slantOffset = renderState ? 0x88UL : 0x60UL;
        if (!TryReadSingle(ctx, fontAddress + scaleOffset, out var configuredWidth) ||
            !TryReadSingle(ctx, fontAddress + scaleOffset + sizeof(float), out var configuredHeight) ||
            !TryReadSingle(ctx, fontAddress + weightOffset, out var weightX) ||
            !TryReadSingle(ctx, fontAddress + weightOffset + sizeof(float), out var weightY) ||
            !TryReadSingle(ctx, fontAddress + slantOffset, out var slant))
        {
            return false;
        }

        var baseWidth = NormalizeFallbackScale(configuredWidth, 8.0f);
        var baseHeight = NormalizeFallbackScale(configuredHeight, 16.0f);
        weightX = float.IsFinite(weightX) ? Math.Clamp(weightX, -0.04f, 0.04f) : 0.0f;
        weightY = float.IsFinite(weightY) ? Math.Clamp(weightY, -0.04f, 0.04f) : 0.0f;
        slant = float.IsFinite(slant) ? Math.Clamp(slant, -1.0f, 1.0f) : 0.0f;

        var glyphWidth = Math.Clamp(
            (baseWidth * (1.0f + weightX)) + (MathF.Abs(slant) * baseHeight * 0.25f),
            1.0f,
            FallbackMaxDimension);
        var glyphHeight = Math.Clamp(baseHeight * (1.0f + weightY), 1.0f, FallbackMaxDimension);
        var advance = glyphWidth + (baseWidth * 0.5f);
        var glyphSlantOffset = slant * baseHeight * 0.25f;
        geometry = new FallbackGeometry(
            baseWidth,
            glyphWidth,
            glyphHeight,
            advance,
            glyphSlantOffset,
            (uint)Math.Clamp(MathF.Ceiling(glyphWidth), 1.0f, FallbackMaxDimension),
            (uint)Math.Clamp(MathF.Ceiling(glyphHeight), 1.0f, FallbackMaxDimension));
        return true;
    }

    private static float NormalizeFallbackScale(float value, float fallback) =>
        float.IsFinite(value) && value > 0.0f
            ? Math.Clamp(value, 1.0f, FallbackMaxDimension)
            : fallback;

    private static int RasterizeFallbackGlyph(
        CpuContext ctx,
        ReadOnlySpan<byte> surface,
        uint codepoint,
        float x,
        float y,
        FallbackGeometry geometry,
        out FallbackRectangle updateRectangle)
    {
        updateRectangle = default;
        var bufferAddress = BinaryPrimitives.ReadUInt64LittleEndian(surface);
        var rowBytes = BinaryPrimitives.ReadUInt32LittleEndian(surface[0x08..]);
        var pixelWord = BinaryPrimitives.ReadUInt32LittleEndian(surface[0x0C..]);
        var pixelBytes = pixelWord & 0xFF;
        var surfaceWidth = BinaryPrimitives.ReadUInt32LittleEndian(surface[0x10..]);
        var surfaceHeight = BinaryPrimitives.ReadUInt32LittleEndian(surface[0x14..]);
        if (bufferAddress == 0 || rowBytes == 0 || pixelBytes is 0 or > 4 ||
            surfaceWidth == 0 || surfaceHeight == 0 ||
            (ulong)surfaceWidth * pixelBytes > rowBytes)
        {
            return FontErrorInvalidArgument;
        }

        var scissorX = BinaryPrimitives.ReadUInt32LittleEndian(surface[0x18..]);
        var scissorY = BinaryPrimitives.ReadUInt32LittleEndian(surface[0x1C..]);
        var scissorWidth = BinaryPrimitives.ReadUInt32LittleEndian(surface[0x20..]);
        var scissorHeight = BinaryPrimitives.ReadUInt32LittleEndian(surface[0x24..]);
        if (scissorWidth == 0 || scissorHeight == 0)
        {
            scissorX = 0;
            scissorY = 0;
            scissorWidth = surfaceWidth;
            scissorHeight = surfaceHeight;
        }

        var clipLeft = Math.Min((ulong)scissorX, surfaceWidth);
        var clipTop = Math.Min((ulong)scissorY, surfaceHeight);
        var clipRight = Math.Min((ulong)surfaceWidth, (ulong)scissorX + scissorWidth);
        var clipBottom = Math.Min((ulong)surfaceHeight, (ulong)scissorY + scissorHeight);
        var originX = FloorFallbackCoordinate(x);
        var originY = FloorFallbackCoordinate(y);
        var destinationLeft = Math.Max(originX, (long)clipLeft);
        var destinationTop = Math.Max(originY, (long)clipTop);
        var destinationRight = Math.Min(originX + geometry.PixelWidth, (long)clipRight);
        var destinationBottom = Math.Min(originY + geometry.PixelHeight, (long)clipBottom);
        if (destinationRight <= destinationLeft || destinationBottom <= destinationTop)
        {
            return 0;
        }

        updateRectangle = new FallbackRectangle(
            (uint)destinationLeft,
            (uint)destinationTop,
            (uint)(destinationRight - destinationLeft),
            (uint)(destinationBottom - destinationTop));

        // This is a deterministic 5x7 rectangle/bit pattern fallback, not a
        // reproduction of the firmware font rasterizer.
        Span<byte> pixel = stackalloc byte[4];
        for (var destinationY = destinationTop; destinationY < destinationBottom; destinationY++)
        {
            var sourceY = (uint)(destinationY - originY);
            var patternY = (int)((sourceY * FallbackPatternHeight) / geometry.PixelHeight);
            for (var destinationX = destinationLeft; destinationX < destinationRight; destinationX++)
            {
                var sourceX = (uint)(destinationX - originX);
                var patternX = (int)((sourceX * FallbackPatternWidth) / geometry.PixelWidth);
                pixel[..(int)pixelBytes].Fill(IsFallbackPixelSet(codepoint, patternX, patternY)
                    ? (byte)0xFF
                    : (byte)0x00);

                var rowOffset = (ulong)destinationY * rowBytes;
                var columnOffset = (ulong)destinationX * pixelBytes;
                if (rowOffset > ulong.MaxValue - columnOffset ||
                    bufferAddress > ulong.MaxValue - rowOffset - columnOffset ||
                    !TryWriteMemory(ctx, bufferAddress + rowOffset + columnOffset, pixel[..(int)pixelBytes]))
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }
            }
        }

        return 0;
    }

    private static long FloorFallbackCoordinate(float value)
    {
        if (!float.IsFinite(value))
        {
            return 0;
        }

        return (long)Math.Clamp(MathF.Floor(value), int.MinValue, int.MaxValue);
    }

    private static bool IsFallbackPixelSet(uint codepoint, int x, int y)
    {
        if (x == 0 || x == FallbackPatternWidth - 1 ||
            y == 0 || y == FallbackPatternHeight - 1)
        {
            return true;
        }

        var bit = ((y * FallbackPatternWidth) + x) % 31;
        return ((codepoint >> bit) & 1) != 0;
    }

    private static bool WriteFallbackGlyphMetrics(
        CpuContext ctx,
        ulong metricsAddress,
        FallbackGeometry geometry)
    {
        ReadOnlySpan<float> values =
        [
            geometry.Width,
            geometry.Height,
            geometry.SlantOffset,
            geometry.Height * 0.75f,
            geometry.Advance,
            0.0f,
            0.0f,
            geometry.Height,
        ];
        Span<byte> metrics = stackalloc byte[GlyphMetricsSize];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                metrics[(index * sizeof(float))..],
                BitConverter.SingleToUInt32Bits(values[index]));
        }

        return TryWriteMemory(ctx, metricsAddress, metrics);
    }

    private static bool WriteFallbackRenderResult(
        CpuContext ctx,
        ulong resultAddress,
        ReadOnlySpan<byte> surface,
        FallbackGeometry geometry,
        FallbackRectangle updateRectangle)
    {
        Span<byte> result = stackalloc byte[RenderResultSize];
        result.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(result[0x08..], BinaryPrimitives.ReadUInt64LittleEndian(surface));
        BinaryPrimitives.WriteUInt32LittleEndian(result[0x10..], BinaryPrimitives.ReadUInt32LittleEndian(surface[0x08..]));
        BinaryPrimitives.WriteUInt32LittleEndian(result[0x14..], BinaryPrimitives.ReadUInt32LittleEndian(surface[0x0C..]));
        BinaryPrimitives.WriteUInt32LittleEndian(result[0x18..], updateRectangle.X);
        BinaryPrimitives.WriteUInt32LittleEndian(result[0x1C..], updateRectangle.Y);
        BinaryPrimitives.WriteUInt32LittleEndian(result[0x20..], updateRectangle.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(result[0x24..], updateRectangle.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(result[0x28..], BitConverter.SingleToUInt32Bits(geometry.SlantOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(result[0x2C..], BitConverter.SingleToUInt32Bits(geometry.Height * 0.75f));
        BinaryPrimitives.WriteUInt32LittleEndian(result[0x30..], BitConverter.SingleToUInt32Bits(geometry.Advance));
        BinaryPrimitives.WriteUInt32LittleEndian(result[0x34..], BitConverter.SingleToUInt32Bits(geometry.Height));
        BinaryPrimitives.WriteUInt32LittleEndian(result[0x38..], geometry.PixelWidth);
        BinaryPrimitives.WriteUInt32LittleEndian(result[0x3C..], geometry.PixelHeight);
        return TryWriteMemory(ctx, resultAddress, result);
    }

    private static int ValidateFontWithRenderer(CpuContext ctx, ulong fontAddress)
    {
        if (!HasMagic(ctx, fontAddress, FontMagic))
        {
            return FontErrorInvalidFont;
        }

        return TryReadUInt64(ctx, fontAddress + 0x30, out var rendererAddress) &&
            rendererAddress != 0 && HasMagic(ctx, rendererAddress, RendererMagic)
            ? 0
            : FontErrorRendererNotBound;
    }

    private static int ReturnSelection(CpuContext ctx, ref ulong selectionAddress, uint objectSize)
    {
        if (ctx[CpuRegister.Rdi] != 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        lock (AllocationGate)
        {
            if (selectionAddress == 0)
            {
                if (!TryAllocateOpaque(ctx, 0x20, out selectionAddress) ||
                    !TryWriteUInt32(ctx, selectionAddress, 0) ||
                    !TryWriteUInt32(ctx, selectionAddress + 4, objectSize))
                {
                    selectionAddress = 0;
                }
            }
        }

        ctx[CpuRegister.Rax] = selectionAddress;
        return 0;
    }

    private static int CreateFontHandle(CpuContext ctx, ulong outputAddress, ulong libraryAddress)
    {
        if (outputAddress == 0 || !TryAllocateOpaque(ctx, OpaqueObjectSize, out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryWriteUInt16(ctx, handle, FontMagic) ||
            !TryWriteUInt16(ctx, handle + 0x02, OwnedObjectFlag) ||
            !TryWriteUInt64(ctx, handle + 0x28, libraryAddress) ||
            !TryWriteUInt64(ctx, outputAddress, handle))
        {
            FreeGuestAllocation(ctx, handle);
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    private static int CreateOpaqueHandle(CpuContext ctx, ulong outputAddress, int size, ushort magic)
    {
        if (outputAddress == 0 || !TryAllocateOpaque(ctx, size, out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryWriteUInt16(ctx, handle, magic) || !TryWriteUInt64(ctx, outputAddress, handle))
        {
            FreeGuestAllocation(ctx, handle);
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    private static bool TryAllocateOpaque(CpuContext ctx, int size, out ulong address)
    {
        address = 0;
        if (ctx.Memory is not IGuestMemoryAllocator allocator ||
            !allocator.TryAllocateGuestMemory((ulong)size, 0x10, out address))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[size];
        bytes.Clear();
        if (TryWriteMemory(ctx, address, bytes))
        {
            return true;
        }

        _ = allocator.TryFreeGuestMemory(address);
        address = 0;
        return false;
    }

    private static void FreeGuestAllocation(CpuContext ctx, ulong address)
    {
        if (address != 0 && ctx.Memory is IGuestMemoryAllocator allocator)
        {
            _ = allocator.TryFreeGuestMemory(address);
        }
    }

    private static bool HasMagic(CpuContext ctx, ulong address, ushort magic) =>
        address != 0 && TryReadUInt16(ctx, address, out var actual) && actual == magic;

    private static float ReadXmmSingle(CpuContext ctx, int registerIndex)
    {
        ctx.GetXmmRegister(registerIndex, out var low, out _);
        return BitConverter.Int32BitsToSingle(unchecked((int)low));
    }

    private static bool TryReadSingleBits(CpuContext ctx, ulong address, out uint bits) =>
        TryReadUInt32(ctx, address, out bits);

    private static bool TryReadSingle(CpuContext ctx, ulong address, out float value)
    {
        value = 0.0f;
        if (!TryReadUInt32(ctx, address, out var bits))
        {
            return false;
        }

        value = BitConverter.UInt32BitsToSingle(bits);
        return true;
    }

    private static bool TryReadByte(CpuContext ctx, ulong address, out byte value)
    {
        Span<byte> byteValue = stackalloc byte[1];
        if (!TryReadMemory(ctx, address, byteValue))
        {
            value = 0;
            return false;
        }

        value = byteValue[0];
        return true;
    }

    private static bool TryWriteSingle(CpuContext ctx, ulong address, float value) =>
        TryWriteUInt32(ctx, address, BitConverter.SingleToUInt32Bits(value));

    private static bool TryClear(CpuContext ctx, ulong address, int size)
    {
        if (address == 0 || size < 0 || size > RenderResultSize)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[size];
        bytes.Clear();
        return TryWriteMemory(ctx, address, bytes);
    }

    private static bool TryReadMemory(CpuContext ctx, ulong address, Span<byte> destination) =>
        KernelMemoryCompatExports.TryReadCompat(ctx, address, destination);

    private static bool TryWriteMemory(CpuContext ctx, ulong address, ReadOnlySpan<byte> source) =>
        KernelMemoryCompatExports.TryWriteCompat(ctx, address, source);

    private static bool TryReadUInt16(CpuContext ctx, ulong address, out ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        if (!TryReadMemory(ctx, address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        return true;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value) =>
        KernelMemoryCompatExports.TryReadUInt32Compat(ctx, address, out value);

    private static bool TryReadUInt64(CpuContext ctx, ulong address, out ulong value) =>
        KernelMemoryCompatExports.TryReadUInt64Compat(ctx, address, out value);

    private static bool TryWriteInt32(CpuContext ctx, ulong address, int value) =>
        TryWriteUInt32(ctx, address, unchecked((uint)value));

    private static bool TryWriteUInt16(CpuContext ctx, ulong address, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return TryWriteMemory(ctx, address, bytes);
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return TryWriteMemory(ctx, address, bytes);
    }

    private static bool TryWriteUInt64(CpuContext ctx, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return TryWriteMemory(ctx, address, bytes);
    }

    private static int SetSuccess(CpuContext ctx) => SetReturn(ctx, 0);

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result) =>
        SetReturn(ctx, (int)result);

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
