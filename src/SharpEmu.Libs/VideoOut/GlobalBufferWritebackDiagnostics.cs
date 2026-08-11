// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct GlobalBufferWritebackSummary(
    long NonzeroBytes,
    long ChangedBytes,
    int FirstChangedOffset,
    ulong Hash);

internal readonly record struct GlobalBufferContentSummary(
    long NonzeroBytes,
    int FirstNonzeroOffset,
    int LastNonzeroOffset,
    ulong Hash);

internal readonly record struct GlobalBufferAddressSample(
    ulong Address,
    int Offset,
    byte[] Bytes);

internal static class GlobalBufferWritebackDiagnostics
{
    internal static GlobalBufferAddressSample[] SampleAddresses(
        ReadOnlySpan<byte> current,
        ulong baseAddress,
        IReadOnlyList<ulong> addresses,
        int maximumBytes = 64)
    {
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var samples = new List<GlobalBufferAddressSample>();
        foreach (var address in addresses)
        {
            if (address < baseAddress)
            {
                continue;
            }

            var relativeAddress = address - baseAddress;
            if (relativeAddress >= (ulong)current.Length)
            {
                continue;
            }

            var offset = checked((int)relativeAddress);
            var byteCount = Math.Min(maximumBytes, current.Length - offset);
            samples.Add(new GlobalBufferAddressSample(
                address,
                offset,
                current.Slice(offset, byteCount).ToArray()));
        }

        return samples.ToArray();
    }

    internal static bool ShouldEmitAddressFilteredTrace(
        int occurrence,
        long changedBytes,
        int interval = 64)
    {
        if (occurrence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrence));
        }

        if (interval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        return occurrence <= 4 ||
            changedBytes > 0 ||
            occurrence % interval == 0;
    }

    internal static GlobalBufferContentSummary Summarize(
        ReadOnlySpan<byte> current)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var nonzeroBytes = 0L;
        var firstNonzeroOffset = -1;
        var lastNonzeroOffset = -1;
        var hash = offsetBasis;
        for (var index = 0; index < current.Length; index++)
        {
            var value = current[index];
            hash = (hash ^ value) * prime;
            if (value == 0)
            {
                continue;
            }

            nonzeroBytes++;
            firstNonzeroOffset = firstNonzeroOffset < 0
                ? index
                : firstNonzeroOffset;
            lastNonzeroOffset = index;
        }

        return new GlobalBufferContentSummary(
            nonzeroBytes,
            firstNonzeroOffset,
            lastNonzeroOffset,
            hash);
    }

    internal static GlobalBufferWritebackSummary Summarize(
        ReadOnlySpan<byte> current,
        ReadOnlySpan<byte> previous)
    {
        if (current.Length != previous.Length)
        {
            throw new ArgumentException("Buffer snapshots must have equal lengths.");
        }

        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var nonzeroBytes = 0L;
        var changedBytes = 0L;
        var firstChangedOffset = -1;
        var hash = offsetBasis;
        for (var index = 0; index < current.Length; index++)
        {
            var value = current[index];
            nonzeroBytes += value == 0 ? 0 : 1;
            hash = (hash ^ value) * prime;
            if (value == previous[index])
            {
                continue;
            }

            changedBytes++;
            if (firstChangedOffset < 0)
            {
                firstChangedOffset = index;
            }
        }

        return new GlobalBufferWritebackSummary(
            nonzeroBytes,
            changedBytes,
            firstChangedOffset,
            hash);
    }
}
