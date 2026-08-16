// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Codec;

/// <summary>
/// Compatibility surface for the Gen5 compute video decoder. The observed
/// argument layouts and NIDs were researched in foufouadi's SharpEmu fork;
/// this implementation is rewritten around SharpEmu's current memory, FFmpeg,
/// Vulkan and Metal abstractions.
/// </summary>
public static class Videodec2Exports
{
    private const int Ok = 0;
    private const int InvalidArgument = unchecked((int)0x80620801);
    private const ulong ComputeQueueToken = 0x56D2_C0DE_0001UL;
    private const ulong InitialDecoderToken = 0x56D2_C0DE_0002UL;
    private const ulong MaxAccessUnitBytes = 32UL * 1024 * 1024;
    private const ulong MaxOutputSlotBytes = 64UL * 1024 * 1024;

    private static readonly ConcurrentDictionary<ulong, DecoderEntry> Decoders = new();
    private static long _nextDecoderHandle = unchecked((long)InitialDecoderToken);
    private static readonly bool DebugDumpEnabled =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_VIDEODEC2_DEBUG_DUMP"),
            "1",
            StringComparison.Ordinal);

    // A wrapper is intentional: ConcurrentDictionary does not accept null
    // values. A valid opaque handle can still use stub behavior when the
    // optional FFmpeg runtime is unavailable.
    private sealed record DecoderEntry(Videodec2Decoder? Decoder);

    [SysAbiExport(
        Nid = "RnDibcGCPKw",
        ExportName = "sceVideodec2QueryComputeMemoryInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2QueryComputeMemoryInfo(CpuContext ctx) =>
        SetReturn(ctx, ctx[CpuRegister.Rdi] == 0 ? InvalidArgument : Ok);

    [SysAbiExport(
        Nid = "eD+X2SmxUt4",
        ExportName = "sceVideodec2AllocateComputeQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2AllocateComputeQueue(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        return SetReturn(
            ctx,
            outputAddress != 0 &&
            KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, outputAddress, ComputeQueueToken)
                ? Ok
                : InvalidArgument);
    }

    [SysAbiExport(
        Nid = "qqMCwlULR+E",
        ExportName = "sceVideodec2QueryDecoderMemoryInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2QueryDecoderMemoryInfo(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rsi];
        if (outputAddress == 0 ||
            !KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, outputAddress + 0x08, 0) ||
            !KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, outputAddress + 0x28, 0) ||
            // A nonzero slot size is required: an observed caller divides its
            // arena by this field while zero is accepted for both work arenas.
            !KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, outputAddress + 0x38, 0x1000))
        {
            return SetReturn(ctx, InvalidArgument);
        }

        return SetReturn(ctx, Ok);
    }

    [SysAbiExport(
        Nid = "CNNRoRYd8XI",
        ExportName = "sceVideodec2CreateDecoder",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2CreateDecoder(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdx];
        if (outputAddress == 0)
        {
            return SetReturn(ctx, InvalidArgument);
        }

        var handle = unchecked((ulong)Interlocked.Increment(ref _nextDecoderHandle));
        var entry = new DecoderEntry(Videodec2Decoder.TryCreate());
        Decoders[handle] = entry;
        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, outputAddress, handle))
        {
            Decoders.TryRemove(handle, out _);
            entry.Decoder?.Dispose();
            return SetReturn(ctx, InvalidArgument);
        }

        DumpDebugStruct(ctx, "create.config", ctx[CpuRegister.Rdi], 0x80);
        DumpDebugStruct(ctx, "create.memory", ctx[CpuRegister.Rsi], 0x60);
        return SetReturn(ctx, Ok);
    }

    [SysAbiExport(
        Nid = "l1hXwscLuCY",
        ExportName = "sceVideodec2Flush",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2Flush(CpuContext ctx)
    {
        var outputInfoAddress = ctx[CpuRegister.Rdx];
        if (!TryWriteNoPicture(ctx, outputInfoAddress))
        {
            return SetReturn(ctx, InvalidArgument);
        }

        if (Decoders.TryGetValue(ctx[CpuRegister.Rdi], out var entry) &&
            entry.Decoder is { } decoder)
        {
            if (decoder.TryConsumeReadySignal(out var width, out var height))
            {
                _ = TryWritePictureReady(ctx, outputInfoAddress, width, height);
            }
            else
            {
                decoder.RequestDrain();
            }
        }

        return SetReturn(ctx, Ok);
    }

    [SysAbiExport(
        Nid = "wJXikG6QFN8",
        ExportName = "sceVideodec2Reset",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2Reset(CpuContext ctx)
    {
        if (Decoders.TryGetValue(ctx[CpuRegister.Rdi], out var entry))
        {
            entry.Decoder?.RequestReset();
        }

        return SetReturn(ctx, Ok);
    }

    [SysAbiExport(
        Nid = "jwImxXRGSKA",
        ExportName = "sceVideodec2DeleteDecoder",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2DeleteDecoder(CpuContext ctx)
    {
        if (Decoders.TryRemove(ctx[CpuRegister.Rdi], out var entry))
        {
            entry.Decoder?.Dispose();
        }

        return SetReturn(ctx, Ok);
    }

    [SysAbiExport(
        Nid = "852F5+q6+iM",
        ExportName = "sceVideodec2Decode",
        Target = Generation.Gen5,
        LibraryName = "libSceVideodec2")]
    public static int Videodec2Decode(CpuContext ctx)
    {
        var outputInfoAddress = ctx[CpuRegister.Rcx];
        if (!TryWriteNoPicture(ctx, outputInfoAddress))
        {
            return SetReturn(ctx, InvalidArgument);
        }

        if (!Decoders.TryGetValue(ctx[CpuRegister.Rdi], out var entry) ||
            entry.Decoder is not { } decoder)
        {
            // Preserve the non-fatal stub path when FFmpeg is unavailable.
            return SetReturn(ctx, Ok);
        }

        var inputAddress = ctx[CpuRegister.Rsi];
        var outputSlotAddress = ctx[CpuRegister.Rdx];
        if (inputAddress == 0 || outputSlotAddress == 0 ||
            !KernelMemoryCompatExports.TryReadUInt64Compat(ctx, inputAddress + 0x08, out var accessUnitAddress) ||
            !KernelMemoryCompatExports.TryReadUInt64Compat(ctx, inputAddress + 0x10, out var accessUnitSize) ||
            accessUnitAddress == 0 || accessUnitSize == 0 || accessUnitSize > MaxAccessUnitBytes ||
            !KernelMemoryCompatExports.TryReadUInt64Compat(ctx, outputSlotAddress + 0x08, out var outputSlotPointer) ||
            !KernelMemoryCompatExports.TryReadUInt64Compat(ctx, outputSlotAddress + 0x10, out var outputSlotSize) ||
            outputSlotPointer == 0 || outputSlotSize == 0 || outputSlotSize > MaxOutputSlotBytes)
        {
            return SetReturn(ctx, Ok);
        }

        var accessUnit = GC.AllocateUninitializedArray<byte>(checked((int)accessUnitSize));
        if (!KernelMemoryCompatExports.TryReadCompat(ctx, accessUnitAddress, accessUnit))
        {
            return SetReturn(ctx, Ok);
        }

        decoder.EnqueueAccessUnit(accessUnit);
        if (decoder.TryConsumeReadySignal(out var width, out var height))
        {
            _ = TryWritePictureReady(ctx, outputInfoAddress, width, height);
        }

        return SetReturn(ctx, Ok);
    }

    internal static void ResetForTests()
    {
        foreach (var pair in Decoders)
        {
            if (Decoders.TryRemove(pair.Key, out var entry))
            {
                entry.Decoder?.Dispose();
            }
        }

        Interlocked.Exchange(ref _nextDecoderHandle, unchecked((long)InitialDecoderToken));
    }

    private static bool TryWriteNoPicture(CpuContext ctx, ulong outputInfoAddress)
    {
        if (outputInfoAddress == 0)
        {
            return false;
        }

        Span<byte> value = stackalloc byte[1] { 0 };
        return KernelMemoryCompatExports.TryWriteCompat(ctx, outputInfoAddress, value);
    }

    private static bool TryWritePictureReady(
        CpuContext ctx,
        ulong outputInfoAddress,
        uint width,
        uint height)
    {
        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, outputInfoAddress + 0x08, width) ||
            !KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, outputInfoAddress + 0x10, height))
        {
            return false;
        }

        Span<byte> value = stackalloc byte[1] { 1 };
        return KernelMemoryCompatExports.TryWriteCompat(ctx, outputInfoAddress, value);
    }

    private static void DumpDebugStruct(CpuContext ctx, string label, ulong address, int length)
    {
        if (!DebugDumpEnabled || address == 0)
        {
            return;
        }

        var buffer = new byte[length];
        Console.Error.WriteLine(
            KernelMemoryCompatExports.TryReadCompat(ctx, address, buffer)
                ? $"[VIDEODEC2][DEBUG] {label} @0x{address:X16}: {Convert.ToHexString(buffer)}"
                : $"[VIDEODEC2][DEBUG] {label} @0x{address:X16}: <unreadable>");
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
