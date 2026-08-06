// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Agc;

internal readonly record struct IndexedGlobalBufferFieldSummary(
    int FieldOffset,
    int NonzeroMappings,
    int FirstSourceRecord,
    uint FirstTargetRecord,
    uint FirstValue,
    int LastSourceRecord,
    uint LastTargetRecord,
    uint LastValue);

internal readonly record struct IndexedGlobalBufferTargetFieldSummary(
    int FieldOffset,
    int NonzeroRecords,
    int FirstTargetRecord,
    uint FirstValue,
    int LastTargetRecord,
    uint LastValue);

internal sealed record IndexedGlobalBufferSummary(
    int SourceRecords,
    int TargetRecords,
    int ValidMappings,
    int OutOfRangeMappings,
    int UniqueTargetRecords,
    IReadOnlyList<IndexedGlobalBufferFieldSummary> Fields,
    IReadOnlyList<IndexedGlobalBufferTargetFieldSummary> TargetFields);

internal static class IndexedGlobalBufferDiagnostics
{
    internal static IndexedGlobalBufferSummary Summarize(
        ReadOnlySpan<byte> source,
        int sourceStride,
        int indexByteOffset,
        ReadOnlySpan<byte> target,
        int targetStride,
        IReadOnlyList<int> fieldOffsets)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceStride);
        ArgumentOutOfRangeException.ThrowIfNegative(indexByteOffset);
        if (sourceStride < sizeof(uint) ||
            indexByteOffset > sourceStride - sizeof(uint))
        {
            throw new ArgumentOutOfRangeException(nameof(indexByteOffset));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetStride);
        ArgumentNullException.ThrowIfNull(fieldOffsets);

        foreach (var fieldOffset in fieldOffsets)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(fieldOffset);
            if (targetStride < sizeof(uint) ||
                fieldOffset > targetStride - sizeof(uint))
            {
                throw new ArgumentOutOfRangeException(nameof(fieldOffsets));
            }
        }

        var sourceRecords = source.Length / sourceStride;
        var targetRecords = target.Length / targetStride;
        var validMappings = 0;
        var outOfRangeMappings = 0;
        var uniqueTargetRecords = new HashSet<uint>();
        var fieldStates = fieldOffsets
            .Select(static fieldOffset => new MutableFieldSummary(fieldOffset))
            .ToArray();
        var targetFieldStates = fieldOffsets
            .Select(static fieldOffset => new MutableTargetFieldSummary(fieldOffset))
            .ToArray();

        for (var sourceRecord = 0; sourceRecord < sourceRecords; sourceRecord++)
        {
            var sourceOffset = checked(sourceRecord * sourceStride + indexByteOffset);
            var targetRecord = BinaryPrimitives.ReadUInt32LittleEndian(
                source.Slice(sourceOffset, sizeof(uint)));
            if (targetRecord >= (uint)targetRecords)
            {
                outOfRangeMappings++;
                continue;
            }

            validMappings++;
            uniqueTargetRecords.Add(targetRecord);
            var targetOffset = checked((int)targetRecord * targetStride);
            foreach (var fieldState in fieldStates)
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(
                    target.Slice(
                        targetOffset + fieldState.FieldOffset,
                        sizeof(uint)));
                fieldState.Observe(sourceRecord, targetRecord, value);
            }
        }

        for (var targetRecord = 0; targetRecord < targetRecords; targetRecord++)
        {
            var targetOffset = checked(targetRecord * targetStride);
            foreach (var fieldState in targetFieldStates)
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(
                    target.Slice(
                        targetOffset + fieldState.FieldOffset,
                        sizeof(uint)));
                fieldState.Observe(targetRecord, value);
            }
        }

        return new IndexedGlobalBufferSummary(
            sourceRecords,
            targetRecords,
            validMappings,
            outOfRangeMappings,
            uniqueTargetRecords.Count,
            fieldStates.Select(static state => state.Snapshot()).ToArray(),
            targetFieldStates.Select(static state => state.Snapshot()).ToArray());
    }

    private sealed class MutableFieldSummary(int fieldOffset)
    {
        internal int FieldOffset { get; } = fieldOffset;
        private int NonzeroMappings { get; set; }
        private int FirstSourceRecord { get; set; } = -1;
        private uint FirstTargetRecord { get; set; } = uint.MaxValue;
        private uint FirstValue { get; set; }
        private int LastSourceRecord { get; set; } = -1;
        private uint LastTargetRecord { get; set; } = uint.MaxValue;
        private uint LastValue { get; set; }

        internal void Observe(int sourceRecord, uint targetRecord, uint value)
        {
            if (value == 0)
            {
                return;
            }

            NonzeroMappings++;
            if (FirstSourceRecord < 0)
            {
                FirstSourceRecord = sourceRecord;
                FirstTargetRecord = targetRecord;
                FirstValue = value;
            }

            LastSourceRecord = sourceRecord;
            LastTargetRecord = targetRecord;
            LastValue = value;
        }

        internal IndexedGlobalBufferFieldSummary Snapshot() =>
            new(
                FieldOffset,
                NonzeroMappings,
                FirstSourceRecord,
                FirstTargetRecord,
                FirstValue,
                LastSourceRecord,
                LastTargetRecord,
                LastValue);
    }

    private sealed class MutableTargetFieldSummary(int fieldOffset)
    {
        internal int FieldOffset { get; } = fieldOffset;
        private int NonzeroRecords { get; set; }
        private int FirstTargetRecord { get; set; } = -1;
        private uint FirstValue { get; set; }
        private int LastTargetRecord { get; set; } = -1;
        private uint LastValue { get; set; }

        internal void Observe(int targetRecord, uint value)
        {
            if (value == 0)
            {
                return;
            }

            NonzeroRecords++;
            if (FirstTargetRecord < 0)
            {
                FirstTargetRecord = targetRecord;
                FirstValue = value;
            }

            LastTargetRecord = targetRecord;
            LastValue = value;
        }

        internal IndexedGlobalBufferTargetFieldSummary Snapshot() =>
            new(
                FieldOffset,
                NonzeroRecords,
                FirstTargetRecord,
                FirstValue,
                LastTargetRecord,
                LastValue);
    }
}
