// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    private const long MaximumRetainedVertexBytesPerSubmission = 256L * 1024 * 1024;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte>
        _reportedAppliedVertexSnapshots = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte>
        _reportedCapturedVertexSnapshots = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Shader, uint Op), byte>
        _reportedMissingVertexSnapshots = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Shader, string Reason), byte>
        _reportedVertexSnapshotCaptureFailures = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte>
        _reportedRebuiltVertexSnapshots = new();

    private sealed record SubmittedVertexSnapshot(
        ulong ExportShaderAddress,
        IReadOnlyList<Gen5VertexInputBinding> Bindings);

    private static Dictionary<ulong, SubmittedVertexSnapshot>?
        CaptureSubmittedVertexPackets(
            CpuContext ctx,
            ulong commandAddress,
            uint dwordCount,
            uint initialIndexSize)
    {
        var snapshots = new Dictionary<ulong, SubmittedVertexSnapshot>();
        var visited = new HashSet<(ulong Address, uint Dwords)>();
        var captureState = new SubmittedDcbState { IndexSize = initialIndexSize };
        var retainedBytes = 0L;
        CaptureSubmittedVertexPacketsCore(
            ctx,
            commandAddress,
            dwordCount,
            visited,
            captureState,
            snapshots,
            ref retainedBytes,
            depth: 0);
        return snapshots.Count == 0 ? null : snapshots;
    }

    private static void CaptureSubmittedVertexPacketsCore(
        CpuContext ctx,
        ulong commandAddress,
        uint dwordCount,
        HashSet<(ulong Address, uint Dwords)> visited,
        SubmittedDcbState state,
        Dictionary<ulong, SubmittedVertexSnapshot> snapshots,
        ref long retainedBytes,
        int depth)
    {
        if (commandAddress == 0 || dwordCount == 0 || depth > 8 ||
            !visited.Add((commandAddress, dwordCount)))
        {
            return;
        }

        for (var offset = 0u; offset < dwordCount;)
        {
            var packetAddress = commandAddress + ((ulong)offset * sizeof(uint));
            if (!TryReadUInt32(ctx, packetAddress, out var header))
            {
                return;
            }

            var packetType = header >> 30;
            if (packetType == 2)
            {
                offset++;
                continue;
            }

            if (packetType != 3)
            {
                return;
            }

            var length = Pm4Length(header);
            if (length == 0 || offset + length > dwordCount)
            {
                return;
            }

            var op = (header >> 8) & 0xFFu;
            var register = (header >> 2) & 0x3Fu;
            if (op == ItIndexType && length >= 2 &&
                TryReadUInt32(ctx, packetAddress + sizeof(uint), out var indexSize))
            {
                state.IndexSize = indexSize & 0x3u;
            }

            ApplySubmittedRegisters(ctx, state, packetAddress, length, op, register);
            if (op == ItNumInstances && length >= 2 &&
                TryReadUInt32(ctx, packetAddress + sizeof(uint), out var instanceCount))
            {
                state.InstanceCount = Math.Max(instanceCount, 1u);
            }

            if (op == ItNop &&
                register is RDrawReset or RAcbReset &&
                length >= 2)
            {
                ResetSubmittedParserState(state);
            }

            if (op == ItIndexBase && length >= 3 &&
                TryReadUInt32(ctx, packetAddress + 4, out var streamIndexBaseLo) &&
                TryReadUInt32(ctx, packetAddress + 8, out var streamIndexBaseHi))
            {
                state.IndexBufferAddress =
                    streamIndexBaseLo | ((ulong)streamIndexBaseHi << 32);
            }

            if (op == ItIndexBufferSize && length >= 2 &&
                TryReadUInt32(ctx, packetAddress + 4, out var streamIndexCount))
            {
                state.IndexBufferCount = streamIndexCount;
            }

            if (op == ItDrawIndex2 && length >= 6 &&
                TryReadUInt32(ctx, packetAddress + 4, out var maximumIndexCount) &&
                TryReadUInt32(ctx, packetAddress + 8, out var indexBaseLo) &&
                TryReadUInt32(ctx, packetAddress + 12, out var indexBaseHi) &&
                TryReadUInt32(ctx, packetAddress + 16, out var indexCount))
            {
                state.IndexBufferAddress = indexBaseLo | ((ulong)indexBaseHi << 32);
                state.IndexBufferCount = maximumIndexCount;
                state.DrawIndexOffset = 0;
                TryCaptureSubmittedVertexSnapshot(
                    ctx,
                    state,
                    packetAddress,
                    indexCount,
                    indexed: true,
                    snapshots,
                    ref retainedBytes);
            }
            else if (op == ItDrawIndexAuto && length >= 3 &&
                     TryReadUInt32(ctx, packetAddress + 4, out var vertexCount))
            {
                TryCaptureSubmittedVertexSnapshot(
                    ctx,
                    state,
                    packetAddress,
                    vertexCount,
                    indexed: false,
                    snapshots,
                    ref retainedBytes);
            }
            else if (op == ItDrawIndexOffset2 && length >= 5 &&
                     TryReadUInt32(ctx, packetAddress + 8, out var indexOffset) &&
                     TryReadUInt32(ctx, packetAddress + 12, out var offsetIndexCount))
            {
                state.DrawIndexOffset = indexOffset;
                TryCaptureSubmittedVertexSnapshot(
                    ctx,
                    state,
                    packetAddress,
                    offsetIndexCount,
                    indexed: true,
                    snapshots,
                    ref retainedBytes);
            }
            else if (op == ItDrawIndexMultiAuto && length >= 4 &&
                     TryReadUInt32(ctx, packetAddress + 12, out var multiAutoControl))
            {
                var multiAutoCount = (multiAutoControl >> 21) & 0x7FFu;
                TryCaptureSubmittedVertexSnapshot(
                    ctx,
                    state,
                    packetAddress,
                    multiAutoCount,
                    indexed: false,
                    snapshots,
                    ref retainedBytes);
            }
            else if (op == ItNop && register == RDrawIndexAuto && length >= 2 &&
                     TryReadUInt32(ctx, packetAddress + 4, out var nopVertexCount))
            {
                TryCaptureSubmittedVertexSnapshot(
                    ctx,
                    state,
                    packetAddress,
                    nopVertexCount,
                    indexed: false,
                    snapshots,
                    ref retainedBytes);
            }

            if (op == ItIndirectBuffer && length >= 4 &&
                TryReadUInt32(ctx, packetAddress + 4, out var chainLow) &&
                TryReadUInt32(ctx, packetAddress + 8, out var chainHigh) &&
                TryReadUInt32(ctx, packetAddress + 12, out var chainDwords))
            {
                var chainAddress = ((ulong)(chainHigh & 0xFFFFu) << 32) | chainLow;
                var chainLength = chainDwords & 0xFFFFFu;
                CaptureSubmittedVertexPacketsCore(
                    ctx,
                    chainAddress,
                    chainLength,
                    visited,
                    state,
                    snapshots,
                    ref retainedBytes,
                    depth + 1);
            }

            offset += length;
        }
    }

    private static void TryCaptureSubmittedVertexSnapshot(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint drawCount,
        bool indexed,
        Dictionary<ulong, SubmittedVertexSnapshot> snapshots,
        ref long retainedBytes)
    {
        if (drawCount == 0 ||
            !TryGetShaderAddress(
                state.ShRegisters,
                SpiShaderPgmLoEs,
                SpiShaderPgmHiEs,
                out var exportShaderAddress))
        {
            return;
        }

        if (retainedBytes >= MaximumRetainedVertexBytesPerSubmission)
        {
            TraceSubmittedVertexCaptureFailure(
                exportShaderAddress,
                packetAddress,
                "budget-exhausted");
            return;
        }

        ulong exportShaderHeader;
        lock (_submitTraceGate)
        {
            _shaderHeadersByCode.TryGetValue(exportShaderAddress, out exportShaderHeader);
        }

        var userDataLayout = DecodeExportUserDataLayout(state.ShRegisters);
        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                exportShaderAddress,
                exportShaderHeader,
                state.ShRegisters,
                userDataLayout.UserDataRegister,
                out var exportState,
                out var createError,
                userDataScalarRegisterBase: userDataLayout.ScalarRegisterBase))
        {
            TraceSubmittedVertexCaptureFailure(
                exportShaderAddress,
                packetAddress,
                $"create-state:{createError}");
            return;
        }

        if (Gen5ShaderTranslator.IsCombinedShader(ctx, exportShaderAddress))
        {
            return;
        }

        if (!TryGetRequiredVertexRecordCount(
                ctx,
                state,
                drawCount,
                indexed,
                out var recordCount))
        {
            TraceSubmittedVertexCaptureFailure(
                exportShaderAddress,
                packetAddress,
                "vertex-range-fallback");
            // The producer queue can expose its draw before GPU-written index
            // data is CPU-readable. Preserve the V# with a conservative upper
            // bound instead of losing the descriptor needed when the draw is
            // resumed. UI draws are small and commonly use sequential indices.
            recordCount = Math.Max(drawCount, Math.Max(state.InstanceCount, 1u));
        }

        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                exportState,
                out var evaluation,
                out var evaluateError,
                resolveVertexInputs: true,
                requiredVertexRecordCount: recordCount))
        {
            TraceSubmittedVertexCaptureFailure(
                exportShaderAddress,
                packetAddress,
                $"evaluate:{evaluateError}");
            return;
        }

        try
        {
            if (evaluation.VertexInputs is not { Count: > 0 } inputs)
            {
                TraceSubmittedVertexCaptureFailure(
                    exportShaderAddress,
                    packetAddress,
                    "no-inputs");
                return;
            }

            if (AgcVertexMetadata.TryGetVertexTableRegisters(
                    ctx,
                    exportShaderAddress,
                    exportShaderHeader,
                    out var tables))
            {
                var resolvedTables = AgcVertexMetadata.AddUserDataScalarRegisterBase(
                    tables,
                    exportState.UserDataScalarRegisterBase);
                inputs = AgcVertexMetadata.MergeVertexInputsFromMetadata(
                    ctx,
                    evaluation.InitialScalarRegisters,
                    resolvedTables,
                    exportState.Program,
                    inputs);
            }

            if (!TryCopySubmittedVertexInputs(
                    inputs,
                    MaximumRetainedVertexBytesPerSubmission - retainedBytes,
                    out var retainedInputs,
                    out var snapshotBytes))
            {
                TraceSubmittedVertexCaptureFailure(
                    exportShaderAddress,
                    packetAddress,
                    "copy-budget");
                return;
            }

            snapshots[packetAddress] = new SubmittedVertexSnapshot(
                exportShaderAddress,
                retainedInputs);
            retainedBytes += snapshotBytes;
            if (_reportedCapturedVertexSnapshots.TryAdd(exportShaderAddress, 0))
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] agc.vertex_snapshot_captured " +
                    $"shader=0x{exportShaderAddress:X16} inputs={retainedInputs.Length} " +
                    $"bytes={snapshotBytes}");
            }
        }
        finally
        {
            ReturnPooledEvaluationArrays(evaluation);
        }
    }

    private static bool TryCopySubmittedVertexInputs(
        IReadOnlyList<Gen5VertexInputBinding> inputs,
        long maximumBytes,
        out Gen5VertexInputBinding[] retainedInputs,
        out long retainedBytes)
    {
        retainedInputs = [];
        retainedBytes = 0;
        var uniqueLengths = new Dictionary<byte[], int>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var input in inputs)
        {
            var length = Math.Clamp(input.DataLength, 0, input.Data.Length);
            if (!uniqueLengths.TryGetValue(input.Data, out var previous) || length > previous)
            {
                uniqueLengths[input.Data] = length;
            }
        }

        retainedBytes = uniqueLengths.Values.Sum(static length => (long)length);
        if (retainedBytes == 0 || retainedBytes > maximumBytes)
        {
            retainedBytes = 0;
            return false;
        }

        var copies = new Dictionary<byte[], byte[]>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var (source, length) in uniqueLengths)
        {
            copies[source] = source.AsSpan(0, length).ToArray();
        }

        retainedInputs = new Gen5VertexInputBinding[inputs.Count];
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var copy = copies[input.Data];
            retainedInputs[index] = input with
            {
                Data = copy,
                DataLength = Math.Clamp(input.DataLength, 0, copy.Length),
                DataPooled = false,
            };
        }

        return true;
    }

    private static bool ApplySubmittedVertexSnapshot(
        SubmittedDcbState state,
        ulong exportShaderAddress,
        ref Gen5ShaderEvaluation evaluation)
    {
        var snapshot = state.CurrentVertexSnapshot;
        var live = evaluation.VertexInputs;
        if (snapshot is null ||
            snapshot.ExportShaderAddress != exportShaderAddress ||
            live is null ||
            live.Count != snapshot.Bindings.Count)
        {
            return false;
        }

        var retainedPcs = new HashSet<uint>(
            snapshot.Bindings.Select(static binding => binding.Pc));
        for (var index = 0; index < live.Count; index++)
        {
            if (!retainedPcs.Remove(live[index].Pc))
            {
                return false;
            }
        }

        if (retainedPcs.Count != 0)
        {
            return false;
        }

        evaluation = evaluation with { VertexInputs = snapshot.Bindings };
        if (_reportedAppliedVertexSnapshots.TryAdd(exportShaderAddress, 0))
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] agc.vertex_snapshot_applied " +
                $"shader=0x{exportShaderAddress:X16} inputs={snapshot.Bindings.Count}");
        }
        return true;
    }

    private static SubmittedVertexSnapshot? TryRebuildSubmittedVertexSnapshot(
        CpuContext ctx,
        SubmittedDcbState state,
        SubmittedVertexSnapshot template,
        uint drawCount,
        bool indexed)
    {
        if (!TryGetRequiredVertexRecordCount(
                ctx,
                state,
                drawCount,
                indexed,
                out var recordCount))
        {
            recordCount = Math.Max(drawCount, Math.Max(state.InstanceCount, 1u));
        }

        if (recordCount == 0)
        {
            return null;
        }

        var rebuilt = new Gen5VertexInputBinding[template.Bindings.Count];
        for (var index = 0; index < template.Bindings.Count; index++)
        {
            var binding = template.Bindings[index];
            var elementBytes = (ulong)Math.Max(binding.ComponentCount, 1u) * sizeof(uint);
            var requiredBytes = ((ulong)(recordCount - 1) * binding.Stride) +
                                binding.OffsetBytes + elementBytes;
            if (requiredBytes == 0 || requiredBytes > 16ul * 1024 * 1024)
            {
                return null;
            }

            var data = new byte[(int)requiredBytes];
            if (!ctx.Memory.TryRead(binding.BaseAddress, data) &&
                !KernelMemoryCompatExports.TryReadTrackedLibcHeap(binding.BaseAddress, data))
            {
                return null;
            }

            rebuilt[index] = binding with
            {
                Data = data,
                DataLength = data.Length,
                DataPooled = false,
            };
        }

        if (_reportedRebuiltVertexSnapshots.TryAdd(template.ExportShaderAddress, 0))
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] agc.vertex_snapshot_rebuilt " +
                $"shader=0x{template.ExportShaderAddress:X16} " +
                $"inputs={rebuilt.Length} records={recordCount}");
        }

        return new SubmittedVertexSnapshot(template.ExportShaderAddress, rebuilt);
    }

    private static void TraceMissingSubmittedVertexSnapshot(
        SubmittedDcbState state,
        ulong packetAddress,
        uint op)
    {
        if (state.CurrentVertexSnapshot is not null ||
            !TryGetShaderAddress(
                state.ShRegisters,
                SpiShaderPgmLoEs,
                SpiShaderPgmHiEs,
                out var shaderAddress) ||
            !_reportedMissingVertexSnapshots.TryAdd((shaderAddress, op), 0))
        {
            return;
        }

        Console.Error.WriteLine(
            "[LOADER][WARN] agc.vertex_snapshot_missing " +
            $"shader=0x{shaderAddress:X16} op=0x{op:X2} " +
            $"packet=0x{packetAddress:X16} queue={state.QueueName}");
    }

    private static void TraceSubmittedVertexCaptureFailure(
        ulong shaderAddress,
        ulong packetAddress,
        string reason)
    {
        if (!_reportedVertexSnapshotCaptureFailures.TryAdd((shaderAddress, reason), 0))
        {
            return;
        }

        Console.Error.WriteLine(
            "[LOADER][WARN] agc.vertex_snapshot_capture_failed " +
            $"shader=0x{shaderAddress:X16} packet=0x{packetAddress:X16} " +
            $"reason='{reason}'");
    }
}
