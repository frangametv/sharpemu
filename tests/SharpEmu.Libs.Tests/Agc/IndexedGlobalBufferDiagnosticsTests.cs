// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class IndexedGlobalBufferDiagnosticsTests
{
    [Fact]
    public void JoinsSourceIndicesToTargetFields()
    {
        var source = new byte[3 * 8];
        WriteUInt32(source, 4, 2);
        WriteUInt32(source, 12, 1);
        WriteUInt32(source, 20, 9);

        var target = new byte[3 * 16];
        WriteUInt32(target, 1 * 16 + 4, 0x11);
        WriteUInt32(target, 2 * 16 + 8, 0x22);

        var summary = IndexedGlobalBufferDiagnostics.Summarize(
            source,
            sourceStride: 8,
            indexByteOffset: 4,
            target,
            targetStride: 16,
            fieldOffsets: [4, 8]);

        Assert.Equal(3, summary.SourceRecords);
        Assert.Equal(3, summary.TargetRecords);
        Assert.Equal(2, summary.ValidMappings);
        Assert.Equal(1, summary.OutOfRangeMappings);
        Assert.Equal(2, summary.UniqueTargetRecords);

        var field4 = Assert.Single(summary.Fields, field => field.FieldOffset == 4);
        Assert.Equal(1, field4.NonzeroMappings);
        Assert.Equal(1, field4.FirstSourceRecord);
        Assert.Equal(1U, field4.FirstTargetRecord);
        Assert.Equal(0x11U, field4.FirstValue);

        var field8 = Assert.Single(summary.Fields, field => field.FieldOffset == 8);
        Assert.Equal(1, field8.NonzeroMappings);
        Assert.Equal(0, field8.FirstSourceRecord);
        Assert.Equal(2U, field8.FirstTargetRecord);
        Assert.Equal(0x22U, field8.FirstValue);

        var targetField4 = Assert.Single(
            summary.TargetFields,
            field => field.FieldOffset == 4);
        Assert.Equal(1, targetField4.NonzeroRecords);
        Assert.Equal(1, targetField4.FirstTargetRecord);
        Assert.Equal(0x11U, targetField4.FirstValue);

        var targetField8 = Assert.Single(
            summary.TargetFields,
            field => field.FieldOffset == 8);
        Assert.Equal(1, targetField8.NonzeroRecords);
        Assert.Equal(2, targetField8.FirstTargetRecord);
        Assert.Equal(0x22U, targetField8.FirstValue);
    }

    [Fact]
    public void RejectsFieldsOutsideTargetRecord()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IndexedGlobalBufferDiagnostics.Summarize(
                new byte[8],
                8,
                4,
                new byte[16],
                16,
                [16]));
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
