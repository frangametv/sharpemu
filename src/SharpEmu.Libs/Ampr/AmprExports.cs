// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Kernel;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;

namespace SharpEmu.Libs.Ampr;

public static class AmprExports
{
    // Firmware 12.70 libSceAmpr stores the command buffer as a 0x18-byte
    // structure: flags/reserved at +0, current offset at +4, command count at
    // +8, capacity at +0xC, and the backing-buffer pointer at +0x10.
    private const int CommandBufferHeaderSize = 0x18;
    private const ulong CommandBufferCurrentOffsetOffset = 0x04;
    private const ulong CommandBufferCommandCountOffset = 0x08;
    private const ulong CommandBufferSizeOffset = 0x0C;
    private const ulong CommandBufferDataOffset = 0x10;
    private const uint MaximumCommandBufferSize = 0x0400_0000;
    private const int SceAmprErrorInvalidArgument = unchecked((int)0x80020016);
    private const ulong ReadFileRecordSize = 0x30;
    private const ulong KernelEventQueueRecordSize = 0x30;
    private const ulong WriteAddressRecordSize = 0x20;
    private const uint ReadFileRecordType = 1;
    private const uint KernelEventQueueRecordType = 2;
    private const uint WriteAddressRecordType = 3;
    private static readonly ConcurrentDictionary<ulong, CommandBufferState> _commandBuffers = new();
    private static readonly bool _traceAmpr =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AMPR"), "1", StringComparison.Ordinal);
    private static readonly bool _traceAmprReads =
        _traceAmpr ||
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AMPR_READS"), "1", StringComparison.Ordinal);

    private sealed class CommandBufferState
    {
        public ulong Buffer;
        public ulong Size;
        public ulong WriteOffset;
        public ulong CommandCount;
    }

    private sealed class CachedHostFile : IDisposable
    {
        public CachedHostFile(string path)
        {
            Handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.RandomAccess);
            Length = RandomAccess.GetLength(Handle);
        }

        public SafeFileHandle Handle { get; }
        public long Length { get; }

        public void Dispose() => Handle.Dispose();
    }

    private sealed class CachedHostFileEntry
    {
        public required string Path { get; init; }
        public required CachedHostFile File { get; init; }
    }

    // Keep a bounded LRU of open host files. An unbounded cache exhausts the
    // process FD limit (~10k on macOS) during large asset storms, after
    // which every new open throws IOException and surfaces as NOT_FOUND — the
    // guest then reports InvalidFileFourCC on empty buffers.
    private const int MaxCachedHostFiles = 1536;
    private static readonly object _hostFileCacheGate = new();
    private static readonly Dictionary<string, LinkedListNode<CachedHostFileEntry>> _hostFileByPath =
        new(HostFsPath.Comparer);
    private static readonly LinkedList<CachedHostFileEntry> _hostFileLru = new();

    [SysAbiExport(
        Nid = "8aI7R7WaOlc",
        ExportName = "sceAmprCommandBufferConstructor",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferConstructor(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];

        if (commandBuffer == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        Span<byte> header = stackalloc byte[CommandBufferHeaderSize];
        header.Clear();
        if (!KernelMemoryCompatExports.TryWriteCompat(ctx, commandBuffer, header))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        UpdateCommandBufferState(commandBuffer, buffer: 0, size: 0, writeOffset: 0);
        TraceAmpr(ctx, "ctor", commandBuffer, 0, 0);
        ctx[CpuRegister.Rax] = commandBuffer;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "a8uLzYY--tM",
        ExportName = "sceAmprAprCommandBufferConstructor",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AprCommandBufferConstructor(CpuContext ctx)
    {
        var outAprContext = ctx[CpuRegister.Rsi];
        var outAprAux = ctx[CpuRegister.Rdx];
        if (outAprContext == 0 || outAprAux == 0 ||
            !KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, outAprContext, 0) ||
            !KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, outAprAux, 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "apr_ctor", ctx[CpuRegister.Rdi], outAprContext, outAprAux);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Qs1xtplKo0U",
        ExportName = "sceAmprAprCommandBufferDestructor",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AprCommandBufferDestructor(CpuContext ctx)
    {
        // Firmware 12.70 Qs1xtplKo0U is a no-op destructor.
        TraceAmpr(ctx, "apr_dtor", ctx[CpuRegister.Rdi], 0, 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "GuchCTefuZw",
        ExportName = "sceAmprCommandBufferDestructor",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferDestructor(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        // Firmware 12.70 GuchCTefuZw leaves the caller-owned object untouched.
        _commandBuffers.TryRemove(commandBuffer, out _);
        TraceAmpr(ctx, "dtor", commandBuffer, 0, 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "N-FSPA4S3nI",
        ExportName = "sceAmprCommandBufferSetBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferSetBuffer(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var buffer = ctx[CpuRegister.Rsi];
        var size = ctx[CpuRegister.Rdx];

        if (commandBuffer == 0 || buffer == 0 || size == 0 ||
            size > MaximumCommandBufferSize || (buffer & 3) != 0 || (size & 3) != 0)
        {
            return SceAmprErrorInvalidArgument;
        }

        if (!KernelMemoryCompatExports.TryReadUInt64Compat(
                ctx,
                commandBuffer + CommandBufferDataOffset,
                out var existingBuffer))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }
        if (existingBuffer != 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
        }

        if (!WriteCommandBufferPointers(ctx, commandBuffer, buffer, size))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "set_buffer", commandBuffer, buffer, size);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "baQO9ez2gL4",
        ExportName = "sceAmprCommandBufferReset",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferReset(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!KernelMemoryCompatExports.TryReadUInt64Compat(
                ctx,
                commandBuffer + CommandBufferDataOffset,
                out var buffer))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }
        if (buffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        Span<byte> counters = stackalloc byte[sizeof(uint) * 2];
        counters.Clear();
        if (!KernelMemoryCompatExports.TryWriteCompat(
                ctx,
                commandBuffer + CommandBufferCurrentOffsetOffset,
                counters) ||
            !KernelMemoryCompatExports.TryReadUInt32Compat(
                ctx,
                commandBuffer + CommandBufferSizeOffset,
                out var size))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        UpdateCommandBufferState(commandBuffer, buffer, size, writeOffset: 0);

        TraceAmpr(ctx, "reset", commandBuffer, buffer, size);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "ULvXMDz56po",
        ExportName = "sceAmprCommandBufferClearBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferClearBuffer(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!KernelMemoryCompatExports.TryReadUInt32Compat(
                ctx,
                commandBuffer + CommandBufferSizeOffset,
                out var size) ||
            !KernelMemoryCompatExports.TryReadUInt64Compat(
                ctx,
                commandBuffer + CommandBufferDataOffset,
                out var buffer))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (size != 0 && buffer != 0)
        {
            Span<byte> binding = stackalloc byte[sizeof(uint) + sizeof(ulong)];
            binding.Clear();
            if (!KernelMemoryCompatExports.TryWriteCompat(
                    ctx,
                    commandBuffer + CommandBufferSizeOffset,
                    binding))
            {
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            _commandBuffers.TryRemove(commandBuffer, out _);
            ctx[CpuRegister.Rax] = buffer;
        }
        else
        {
            ctx[CpuRegister.Rax] = 0;
        }

        TraceAmpr(ctx, "clear_buffer", commandBuffer, buffer, size);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "mQ16-QdKv7k",
        ExportName = "sceAmprAprCommandBufferReadFile",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AprCommandBufferReadFile(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var fileId = unchecked((uint)ctx[CpuRegister.Rcx]);
        var destination = ctx[CpuRegister.R8];
        var size = ctx[CpuRegister.R9];

        if (commandBuffer == 0 || (destination == 0 && size != 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!KernelMemoryCompatExports.TryReadUInt64Compat(
                ctx,
                ctx[CpuRegister.Rsp] + sizeof(ulong),
                out var fileOffset))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (!AmprFileRegistry.TryGetHostPath(fileId, out var hostPath))
        {
            // Cooked Insomniac content ids are FNV("/app0/...") and may never
            // pass through APR resolve. Index app0 once, then retry the lookup.
            var app0Root = KernelMemoryCompatExports.ResolveGuestPath("$/");
            if (!string.IsNullOrEmpty(app0Root))
            {
                AmprFileRegistry.EnsureApp0Indexed(app0Root);
            }

            if (!AmprFileRegistry.TryGetHostPath(fileId, out hostPath))
            {
                TraceAmprRead(ctx, commandBuffer, fileId, destination, size, fileOffset, bytesRead: 0, hostPath, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }
        }

        // Offset -1 means "continue after the previous read of this file id".
        // #216 dropped this wiring; without it sequential pack/streamer reads
        // fail as INVALID_ARGUMENT and RAGE load jobs never complete while the
        // North Yankton UI keeps flipping.
        if (fileOffset == unchecked((ulong)(long)-1))
        {
            fileOffset = PakDirectoryTracker.ResolveSequentialOffset(fileId, size);
        }
        else if (fileOffset > long.MaxValue)
        {
            fileOffset = 0;
        }

        var result = TryReadFileToGuestMemory(ctx, hostPath, fileOffset, destination, size, out var bytesRead);
        if (result != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            TraceAmprRead(ctx, commandBuffer, fileId, destination, size, fileOffset, bytesRead, hostPath, result);
            return result;
        }

        PakDirectoryTracker.OnReadCompleted(ctx, fileId, destination, fileOffset, bytesRead);

        if (!AppendReadFileRecord(ctx, commandBuffer, fileId, destination, size, fileOffset, bytesRead))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmprRead(ctx, commandBuffer, fileId, destination, size, fileOffset, bytesRead, hostPath, (int)OrbisGen2Result.ORBIS_GEN2_OK);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "vWU-odnS+fU",
        ExportName = "sceAmprMeasureCommandSizeReadFile",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int MeasureCommandSizeReadFile(CpuContext ctx)
    {
        TraceAmpr(ctx, "measure_read_file", 0, ReadFileRecordSize, 0);
        ctx[CpuRegister.Rax] = ReadFileRecordSize;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "sSAUCCU1dv4",
        ExportName = "sceAmprMeasureCommandSizeWriteKernelEventQueue_04_00",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int MeasureCommandSizeWriteKernelEventQueue0400(CpuContext ctx)
    {
        TraceAmpr(ctx, "measure_write_equeue", 0, KernelEventQueueRecordSize, 0);
        ctx[CpuRegister.Rax] = KernelEventQueueRecordSize;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Zi3dBUjgyXI",
        ExportName = "sceAmprMeasureCommandSizeWriteKernelEventQueueOnCompletion",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int MeasureCommandSizeWriteKernelEventQueueOnCompletion(CpuContext ctx)
    {
        TraceAmpr(ctx, "measure_write_equeue_complete", 0, KernelEventQueueRecordSize, 0);
        ctx[CpuRegister.Rax] = KernelEventQueueRecordSize;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "C+IEj+BsAFM",
        ExportName = "sceAmprMeasureCommandSizeWriteAddressOnCompletion",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int MeasureCommandSizeWriteAddressOnCompletion(CpuContext ctx)
    {
        TraceAmpr(ctx, "measure_write_address_complete", 0, WriteAddressRecordSize, 0);
        ctx[CpuRegister.Rax] = WriteAddressRecordSize;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "4fgtGfXDrFc",
        ExportName = "sceAmprMeasureCommandSizeWriteAddress_04_00",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr",
        PreferLle = true)]
    public static int MeasureCommandSizeWriteAddress0400(CpuContext ctx)
    {
        TraceAmpr(ctx, "measure_write_address", 0, WriteAddressRecordSize, 0);
        ctx[CpuRegister.Rax] = WriteAddressRecordSize;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "tZDDEo2tE5k",
        ExportName = "sceAmprCommandBufferGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferGetSize(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!KernelMemoryCompatExports.TryReadUInt32Compat(
                ctx,
                commandBuffer + CommandBufferSizeOffset,
                out var size))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "get_size", commandBuffer, size, 0);
        ctx[CpuRegister.Rax] = size;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "GnxKOHEawhk",
        ExportName = "sceAmprCommandBufferGetCurrentOffset",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferGetCurrentOffset(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!KernelMemoryCompatExports.TryReadUInt32Compat(
                ctx,
                commandBuffer + CommandBufferCurrentOffsetOffset,
                out var offset))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "get_offset", commandBuffer, offset, 0);
        ctx[CpuRegister.Rax] = offset;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "gzndltBEzWc",
        ExportName = "sceAmprCommandBufferGetNumCommands",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferGetNumCommands(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!KernelMemoryCompatExports.TryReadUInt32Compat(
                ctx,
                commandBuffer + CommandBufferCommandCountOffset,
                out var commandCount))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "get_num_commands", commandBuffer, commandCount, 0);
        ctx[CpuRegister.Rax] = commandCount;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "H896Pt-yB4I",
        ExportName = "sceAmprCommandBufferWriteKernelEventQueue_04_00",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferWriteKernelEventQueue0400(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var equeue = ctx[CpuRegister.Rsi];
        var ident = ctx[CpuRegister.Rdx];
        var completionToken = ctx[CpuRegister.Rcx];
        var userData = ctx[CpuRegister.R8];

        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!AppendKernelEventQueueRecord(
                ctx,
                commandBuffer,
                equeue,
                ident,
                completionToken,
                userData))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "write_equeue", commandBuffer, ident, completionToken);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "o67gODLFpls",
        ExportName = "sceAmprCommandBufferWriteKernelEventQueueOnCompletion",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferWriteKernelEventQueueOnCompletion(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var equeue = ctx[CpuRegister.Rsi];
        var ident = ctx[CpuRegister.Rdx];
        var completionToken = ctx[CpuRegister.Rcx];
        var userData = ctx[CpuRegister.R8];

        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!AppendKernelEventQueueRecord(
                ctx,
                commandBuffer,
                equeue,
                ident,
                completionToken,
                userData))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "write_equeue_complete", commandBuffer, ident, completionToken);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "sJXyWHjP-F8",
        ExportName = "sceAmprCommandBufferWriteAddressOnCompletion",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferWriteAddressOnCompletion(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        var value = ctx[CpuRegister.Rdx];

        if (commandBuffer == 0 || address == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!AppendWriteAddressRecord(ctx, commandBuffer, address, value))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "write_address_complete", commandBuffer, address, value);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "j0+3uJMxYJY",
        ExportName = "sceAmprCommandBufferWriteAddress_04_00",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr",
        PreferLle = true)]
    public static int CommandBufferWriteAddress0400(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        var value = ctx[CpuRegister.Rdx];

        if (commandBuffer == 0 || address == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!AppendWriteAddressRecord(ctx, commandBuffer, address, value))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "write_address", commandBuffer, address, value);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    public static int CompleteCommandBuffer(CpuContext ctx, ulong commandBuffer)
    {
        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryGetCommandBufferState(ctx, commandBuffer, out var buffer, out _, out var state) || state is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ulong writeOffset;
        lock (state)
        {
            writeOffset = state.WriteOffset;
        }

        var offset = 0UL;
        while (offset < writeOffset)
        {
            if (!TryReadUInt32(ctx, buffer + offset, out var recordType))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            switch (recordType)
            {
                case ReadFileRecordType:
                    offset += ReadFileRecordSize;
                    break;

                case KernelEventQueueRecordType:
                    if (!CompleteKernelEventQueueRecord(ctx, buffer + offset))
                    {
                        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                    }

                    offset += KernelEventQueueRecordSize;
                    break;

                case WriteAddressRecordType:
                    if (!CompleteWriteAddressRecord(ctx, buffer + offset))
                    {
                        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                    }

                    offset += WriteAddressRecordSize;
                    break;

                default:
                    TraceAmpr(ctx, "complete_unknown", commandBuffer, recordType, offset);
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            }
        }

        TraceAmpr(ctx, "complete", commandBuffer, buffer, writeOffset);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool WriteCommandBufferPointers(CpuContext ctx, ulong commandBuffer, ulong buffer, ulong size)
    {
        return WriteCommandBufferPointers(ctx, commandBuffer, buffer, size, writeOffset: 0);
    }

    private static bool WriteCommandBufferPointers(CpuContext ctx, ulong commandBuffer, ulong buffer, ulong size, ulong writeOffset)
    {
        if (size > uint.MaxValue)
        {
            return false;
        }

        Span<byte> binding = stackalloc byte[sizeof(uint) + sizeof(ulong)];
        BinaryPrimitives.WriteUInt32LittleEndian(binding, unchecked((uint)size));
        BinaryPrimitives.WriteUInt64LittleEndian(binding[sizeof(uint)..], buffer);
        if (!KernelMemoryCompatExports.TryWriteCompat(
                ctx,
                commandBuffer + CommandBufferSizeOffset,
                binding))
        {
            return false;
        }

        UpdateCommandBufferState(commandBuffer, buffer, size, writeOffset);

        return true;
    }

    private static void UpdateCommandBufferState(
        ulong commandBuffer,
        ulong buffer,
        ulong size,
        ulong writeOffset)
    {
        var state = _commandBuffers.GetOrAdd(commandBuffer, static _ => new CommandBufferState());
        lock (state)
        {
            state.Buffer = buffer;
            state.Size = size;
            state.WriteOffset = writeOffset;
            state.CommandCount = 0;
        }
    }

    private static bool TryGetCommandBufferState(
        CpuContext ctx,
        ulong commandBuffer,
        out ulong buffer,
        out ulong size,
        out CommandBufferState? state)
    {
        if (!KernelMemoryCompatExports.TryReadUInt64Compat(
                ctx,
                commandBuffer + CommandBufferDataOffset,
                out buffer) ||
            !KernelMemoryCompatExports.TryReadUInt32Compat(
                ctx,
                commandBuffer + CommandBufferSizeOffset,
                out var size32) ||
            !KernelMemoryCompatExports.TryReadUInt32Compat(
                ctx,
                commandBuffer + CommandBufferCurrentOffsetOffset,
                out var offset32) ||
            !KernelMemoryCompatExports.TryReadUInt32Compat(
                ctx,
                commandBuffer + CommandBufferCommandCountOffset,
                out var count32))
        {
            size = 0;
            state = null;
            return false;
        }

        size = size32;
        state = _commandBuffers.GetOrAdd(commandBuffer, static _ => new CommandBufferState());
        lock (state)
        {
            state.Buffer = buffer;
            state.Size = size;
            state.WriteOffset = offset32;
            state.CommandCount = count32;
        }

        return true;
    }

    private static int TryReadFileToGuestMemory(
        CpuContext ctx,
        string hostPath,
        ulong fileOffset,
        ulong destination,
        ulong size,
        out ulong bytesRead)
    {
        bytesRead = 0;
        if (size == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (fileOffset > long.MaxValue)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        // 4 MiB chunks cut syscall/Rosetta round-trips on DeS' large sequential
        // APR reads without blowing the ArrayPool for small probes.
        const int ChunkSize = 4 * 1024 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min((ulong)ChunkSize, size));

        try
        {
            if (!TryGetCachedHostFile(hostPath, out var cachedFile, out var openResult))
            {
                return openResult;
            }

            if (fileOffset >= (ulong)cachedFile.Length)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            while (bytesRead < size)
            {
                if (bytesRead > ulong.MaxValue - fileOffset)
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
                }

                var absoluteOffset = fileOffset + bytesRead;
                if (absoluteOffset > long.MaxValue)
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
                }

                var request = (int)Math.Min((ulong)buffer.Length, size - bytesRead);
                var read = RandomAccess.Read(
                    cachedFile.Handle,
                    buffer.AsSpan(0, request),
                    unchecked((long)absoluteOffset));

                if (read <= 0)
                {
                    break;
                }

                if (!KernelMemoryCompatExports.TryWriteCompat(
                        ctx,
                        destination + bytesRead,
                        buffer.AsSpan(0, read)))
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }

                bytesRead += (ulong)read;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryGetCachedHostFile(string hostPath, out CachedHostFile file, out int result)
    {
        file = null!;
        result = (int)OrbisGen2Result.ORBIS_GEN2_OK;

        string cachePath;
        try
        {
            cachePath = Path.GetFullPath(hostPath);
        }
        catch
        {
            cachePath = hostPath;
        }

        lock (_hostFileCacheGate)
        {
            if (_hostFileByPath.TryGetValue(cachePath, out var existing))
            {
                _hostFileLru.Remove(existing);
                _hostFileLru.AddFirst(existing);
                file = existing.Value.File;
                return true;
            }
        }

        CachedHostFile opened;
        try
        {
            opened = new CachedHostFile(cachePath);
        }
        catch (UnauthorizedAccessException)
        {
            result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
            return false;
        }
        catch (IOException)
        {
            // Likely EMFILE from a prior unbounded cache, or a transient miss.
            // Evict everything we hold and retry once so a full FD table can
            // recover without restarting the process.
            EvictAllCachedHostFiles();
            try
            {
                opened = new CachedHostFile(cachePath);
            }
            catch (UnauthorizedAccessException)
            {
                result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
                return false;
            }
            catch (IOException)
            {
                result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
                return false;
            }
        }

        lock (_hostFileCacheGate)
        {
            if (_hostFileByPath.TryGetValue(cachePath, out var raced))
            {
                opened.Dispose();
                _hostFileLru.Remove(raced);
                _hostFileLru.AddFirst(raced);
                file = raced.Value.File;
                return true;
            }

            while (_hostFileByPath.Count >= MaxCachedHostFiles)
            {
                EvictLeastRecentlyUsedHostFileLocked();
            }

            var entry = new CachedHostFileEntry { Path = cachePath, File = opened };
            var node = _hostFileLru.AddFirst(entry);
            _hostFileByPath[cachePath] = node;
            file = opened;
            return true;
        }
    }

    private static void EvictAllCachedHostFiles()
    {
        List<CachedHostFile> doomed;
        lock (_hostFileCacheGate)
        {
            doomed = _hostFileLru.Select(entry => entry.File).ToList();
            _hostFileLru.Clear();
            _hostFileByPath.Clear();
        }

        foreach (var cached in doomed)
        {
            cached.Dispose();
        }
    }

    private static void EvictLeastRecentlyUsedHostFileLocked()
    {
        var last = _hostFileLru.Last;
        if (last is null)
        {
            return;
        }

        _hostFileLru.RemoveLast();
        _hostFileByPath.Remove(last.Value.Path);
        last.Value.File.Dispose();
    }

    private static bool AppendReadFileRecord(
        CpuContext ctx,
        ulong commandBuffer,
        uint fileId,
        ulong destination,
        ulong size,
        ulong fileOffset,
        ulong bytesRead)
    {
        Span<byte> record = stackalloc byte[(int)ReadFileRecordSize];
        record.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x00..], ReadFileRecordType);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x04..], fileId);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x08..], destination);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x10..], size);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x18..], fileOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x20..], bytesRead);

        return AppendCommandBufferRecord(ctx, commandBuffer, record);
    }

    private static bool AppendKernelEventQueueRecord(
        CpuContext ctx,
        ulong commandBuffer,
        ulong equeue,
        ulong ident,
        ulong completionToken,
        ulong userData)
    {
        Span<byte> record = stackalloc byte[(int)KernelEventQueueRecordSize];
        record.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x00..], KernelEventQueueRecordType);
        BinaryPrimitives.WriteInt16LittleEndian(record[0x04..], KernelEventQueueCompatExports.KernelEventFilterAmpr);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x08..], equeue);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x10..], ident);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x18..], userData);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x20..], completionToken);

        return AppendCommandBufferRecord(ctx, commandBuffer, record);
    }

    private static bool AppendWriteAddressRecord(CpuContext ctx, ulong commandBuffer, ulong address, ulong value)
    {
        Span<byte> record = stackalloc byte[(int)WriteAddressRecordSize];
        record.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x00..], WriteAddressRecordType);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x08..], address);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x10..], value);

        return AppendCommandBufferRecord(ctx, commandBuffer, record);
    }

    private static bool AppendCommandBufferRecord(CpuContext ctx, ulong commandBuffer, ReadOnlySpan<byte> record)
    {
        if (!TryGetCommandBufferState(ctx, commandBuffer, out _, out _, out var state) || state is null)
        {
            return false;
        }

        var recordSize = (ulong)record.Length;
        lock (state)
        {
            if (state.Buffer == 0 ||
                state.WriteOffset > state.Size ||
                recordSize > state.Size - state.WriteOffset)
            {
                return false;
            }

            if (!KernelMemoryCompatExports.TryWriteCompat(
                    ctx,
                    state.Buffer + state.WriteOffset,
                    record))
            {
                return false;
            }

            state.WriteOffset += recordSize;
            state.CommandCount++;
            if (state.WriteOffset > uint.MaxValue || state.CommandCount > uint.MaxValue ||
                !KernelMemoryCompatExports.TryWriteUInt32Compat(
                    ctx,
                    commandBuffer + CommandBufferCurrentOffsetOffset,
                    unchecked((uint)state.WriteOffset)) ||
                !KernelMemoryCompatExports.TryWriteUInt32Compat(
                    ctx,
                    commandBuffer + CommandBufferCommandCountOffset,
                    unchecked((uint)state.CommandCount)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CompleteKernelEventQueueRecord(CpuContext ctx, ulong recordAddress)
    {
        Span<byte> record = stackalloc byte[(int)KernelEventQueueRecordSize];
        if (!KernelMemoryCompatExports.TryReadCompat(ctx, recordAddress, record))
        {
            return false;
        }

        var filter = unchecked((short)BinaryPrimitives.ReadUInt32LittleEndian(record[0x04..]));
        var equeue = BinaryPrimitives.ReadUInt64LittleEndian(record[0x08..]);
        var ident = BinaryPrimitives.ReadUInt64LittleEndian(record[0x10..]);
        var userData = BinaryPrimitives.ReadUInt64LittleEndian(record[0x18..]);
        var data = BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]);
        var extra = BinaryPrimitives.ReadUInt64LittleEndian(record[0x28..]);

        var queuedEvent = new KernelEventQueueCompatExports.KernelQueuedEvent(
            ident,
            filter,
            0x20,
            unchecked((uint)extra),
            data,
            userData);

        _ = KernelEventQueueCompatExports.EnqueueEvent(equeue, queuedEvent);
        TraceAmpr(ctx, "complete_equeue", equeue, ident, data);
        return true;
    }

    private static bool CompleteWriteAddressRecord(CpuContext ctx, ulong recordAddress)
    {
        Span<byte> record = stackalloc byte[(int)WriteAddressRecordSize];
        if (!KernelMemoryCompatExports.TryReadCompat(ctx, recordAddress, record))
        {
            return false;
        }

        var address = BinaryPrimitives.ReadUInt64LittleEndian(record[0x08..]);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(record[0x10..]);
        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, address, value))
        {
            return false;
        }

        // GPU WAIT_REG_MEM often watches these APR completion labels
        // (e.g. 0x20505xxx DEADBEEF/counter fences). Without
        // RecordProduced the wait sits producerless after the guest recycles
        // the dword, and CollectDeadlockBroken cannot replay the wake.
        _ = GpuWaitRegistry.RecordProduced(ctx.Memory, address, value);
        if ((address & 4ul) == 0)
        {
            // 32-bit GPU waits use the low dword; also latch that view when the
            // address is dword-aligned so a u32 compare against ref sees it.
            _ = GpuWaitRegistry.RecordProduced(
                ctx.Memory, address, unchecked((uint)value));
        }

        TraceAmpr(ctx, "complete_write_address", address, value, 0);
        return true;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        return KernelMemoryCompatExports.TryReadUInt32Compat(ctx, address, out value);
    }

    private static void TryPreindexApp0()
    {
        var app0Root = KernelMemoryCompatExports.ResolveGuestPath("$/");
        if (!string.IsNullOrEmpty(app0Root))
        {
            AmprFileRegistry.EnsureApp0Indexed(app0Root);
        }
    }

    private static void TraceAmpr(CpuContext ctx, string operation, ulong commandBuffer, ulong arg0, ulong arg1)
    {
        if (!_traceAmpr)
        {
            return;
        }

        var returnRip = 0UL;
        _ = KernelMemoryCompatExports.TryReadUInt64Compat(ctx, ctx[CpuRegister.Rsp], out returnRip);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] ampr.{operation}: cmd=0x{commandBuffer:X16} arg0=0x{arg0:X16} arg1=0x{arg1:X16} ret=0x{returnRip:X16}");
    }

    private static void TraceAmprRead(
        CpuContext ctx,
        ulong commandBuffer,
        uint fileId,
        ulong destination,
        ulong size,
        ulong fileOffset,
        ulong bytesRead,
        string? hostPath,
        int result)
    {
        if (!_traceAmprReads)
        {
            return;
        }

        var returnRip = 0UL;
        _ = KernelMemoryCompatExports.TryReadUInt64Compat(ctx, ctx[CpuRegister.Rsp], out returnRip);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] ampr.read_file: cmd=0x{commandBuffer:X16} id=0x{fileId:X8} dst=0x{destination:X16} size=0x{size:X16} offset=0x{fileOffset:X16} read=0x{bytesRead:X16} result=0x{result:X8} path='{hostPath ?? string.Empty}' ret=0x{returnRip:X16}");
    }
}
