// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using SharpEmu.Libs.Media;
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SharpEmu.Libs.Agc;

// Attribution: substantial PS5 AGC command and shader-runtime portions in this file were
// originally authored by @xnetcat and later adapted in PR #216. Source snapshot:
// https://github.com/xnetcat/sharpemu/tree/2497ea6799432ac2385a50f739eff2ce922d6fd4

public static partial class AgcExports
{
    // The backend is a process-fixed singleton, so its offset-alignment
    // requirement is snapshot once: several per-draw paths (shader-key
    // hashing, buffer-offset alignment) read it in loops.
    private static readonly ulong _storageBufferOffsetAlignment =
        GuestGpu.Current.GuestStorageBufferOffsetAlignment;

#if DEBUG
    static AgcExports()
    {
        ValidateWriteDataControlDecoders();
        ValidateDispatchInitiators();
        ValidateSubmittedQueueAndReleaseMemDecoders();
        ValidateAcquireMemAndQueueResetDecoders();
        ValidateDepthTargetDecoder();
    }
#endif

    private const uint ShaderFileHeader = 0x34333231;
    private const uint ShaderVersion = 0x18;
    private const uint ItNop = 0x10;
    private const uint ItSetBase = 0x11;
    private const uint ItIndexBufferSize = 0x13;
    private const uint ItIndexBase = 0x26;
    private const uint ItDrawIndirect = 0x24;
    private const uint ItDrawIndexIndirect = 0x25;
    private const uint ItDrawIndex2 = 0x27;
    private const uint ItIndexType = 0x2A;
    private const uint ItDrawIndexAuto = 0x2D;
    private const uint ItNumInstances = 0x2F;
    private const uint ItDrawIndexMultiAuto = 0x30;
    private const uint ItDrawIndexOffset2 = 0x35;
    private const uint ItDrawIndexIndirectMulti = 0x38;
    private const uint DrawIndexedIndirectArgsSize = 20;
    private const uint DrawIndexedIndirectMaxScan = 1024;
    private const uint ItWriteData = 0x37;
    private const uint ItDispatchDirect = 0x15;
    private const uint ItDispatchIndirect = 0x16;
    private const uint ItSetPredication = 0x20;
    private const uint ItCondExec = 0x22;
    private const uint ItWaitRegMem = 0x3C;
    private const uint ItIndirectBuffer = 0x3F;
    private const uint ItCopyData = 0x40;
    private const uint ItEventWrite = 0x46;
    private const uint ItReleaseMem = 0x49;
    private const uint ItDmaData = 0x50;
    private const uint ItRewind = 0x59;
    private const uint ItSetContextReg = 0x69;
    private const uint ItSetShReg = 0x76;
    private const uint ItSetUconfigReg = 0x79;
    private const uint RewindValidBit = 1u << 31;
    private const uint RewindOffloadEnableBit = 1u << 24;
    private const uint ItGetLodStats = 0x8E;

    private static readonly HashSet<uint> KnownPm4Opcodes =
    [
        ItNop, ItSetBase, ItIndexBufferSize, ItIndexBase, ItDrawIndirect,
        ItDrawIndexIndirect, ItDrawIndex2, ItIndexType, ItDrawIndexAuto,
        ItNumInstances, ItDrawIndexMultiAuto, ItDrawIndexOffset2, ItWriteData,
        ItDispatchDirect, ItDispatchIndirect, ItCondExec, ItWaitRegMem,
        ItIndirectBuffer, ItEventWrite, ItReleaseMem, ItDmaData,
        ItSetContextReg, ItSetShReg, ItSetUconfigReg, ItGetLodStats,
    ];

    private const uint RZero = 0x00;
    private const uint RDrawIndexAuto = 0x04;
    private const uint RDrawReset = 0x05;
    private const uint RWaitFlipDone = 0x06;
    private const uint RAcbReset = 0x09;
    private const uint RWaitMem32 = 0x0A;
    private const uint RPushMarker = 0x0B;
    private const uint RPopMarker = 0x0C;
    private const uint RShRegsIndirect = 0x11;
    private const uint RCxRegsIndirect = 0x12;
    private const uint RUcRegsIndirect = 0x13;
    private const uint RAcquireMem = 0x14;
    private const uint RWriteData = 0x15;
    private const uint RWaitMem64 = 0x16;
    private const uint RFlip = 0x17;
    private const uint RReleaseMem = 0x18;
    private const uint RDmaData = 0x19;

    // Command rings advance through contiguous fixed-size chunks; the sentinel
    // terminator (IT_INDIRECT_BUFFER target=1 size=0) continues at the next one.
    private const uint RingChunkBytes = 0x10000;

    // release_mem here raises an EOP interrupt; above this range it's
    // GPU-internal queue sync with no interrupt.
    private const ulong GpuLabelPoolBase = 0x2000000000UL;
    private const ulong GpuLabelPoolSize = 0x10000UL;

    private static bool IsCpuVisibleLabel(ulong address) =>
        address >= GpuLabelPoolBase &&
        address < GpuLabelPoolBase + GpuLabelPoolSize;

    // Async-compute ring tracking, env-gated. Off by default; only
    // validated against Ghost of Yotei.
    private static readonly bool _forceSubmitOrphanPreamblesEnabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_FORCE_SUBMIT_ORPHAN_PREAMBLES"),
        "1",
        StringComparison.Ordinal);
    private static readonly object _orphanPreambleGate = new();
    // Multiple producers can share one target label; last-writer-wins would
    // starve waits on the others.
    private static readonly Dictionary<ulong, List<ulong>> _cbReleaseMemTargets = new();
    // header -> {ring base, write cursor} of the last submitted slice.
    // Submissions stay cursor-bounded since rings aren't zeroed. Lap
    // distinguishes a stale cursor from a previous pass over the same base.
    private static readonly Dictionary<ulong, (ulong Base, ulong Cursor, long Lap)> _orphanPreambleSubmitted = new();
    private static readonly HashSet<ulong> _orphanPreambleUnreadableLogged = new();
    // Last {base, cursor} per builder header, to detect arena switches.
    // Updated only from release_mem builds, not every packet.
    private static readonly Dictionary<ulong, (ulong Base, ulong Cursor, ulong ThreadHandle, long Timestamp)> _builderArenaLastSeen = new();

    // Keyed by the literal write address (rounded to its 64KB chunk), not
    // the builder header's self-reported Base — that can be far from where
    // a persistent ring is actually stalled.
    private static readonly Dictionary<ulong, (ulong ThreadHandle, long Timestamp)> _ringChunkWriters = new();

    private static void RecordRingChunkWriter(ulong writeAddress)
    {
        if (!_forceSubmitOrphanPreamblesEnabled || writeAddress == 0)
        {
            return;
        }

        var chunkKey = writeAddress & ~(ulong)(RingChunkBytes - 1);
        lock (_orphanPreambleGate)
        {
            _ringChunkWriters[chunkKey] = (GuestThreadExecution.CurrentGuestThreadHandle, System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }

    /// <summary>
    /// Stall diagnostics: which guest thread last wrote near
    /// <paramref name="ringWindowStart"/>, and how long ago.
    /// </summary>
    public static bool TryFindRingProducer(ulong ringWindowStart, out ulong threadHandle, out double secondsSinceLastWrite)
    {
        threadHandle = 0;
        secondsSinceLastWrite = 0;

        var chunkKey = ringWindowStart & ~(ulong)(RingChunkBytes - 1);
        lock (_orphanPreambleGate)
        {
            if (_ringChunkWriters.TryGetValue(chunkKey, out var writer))
            {
                threadHandle = writer.ThreadHandle;
                secondsSinceLastWrite =
                    (System.Diagnostics.Stopwatch.GetTimestamp() - writer.Timestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
                return true;
            }
        }

        // Fallback: match by chunk-range membership, preferring the closest
        // base at or below the wait address (CommandBufferAddress is the
        // parse window start, not the arena's absolute base).
        var bestBase = 0UL;
        var found = false;
        (ulong Base, ulong Cursor, ulong ThreadHandle, long Timestamp) bestSeen = default;
        lock (_orphanPreambleGate)
        {
            foreach (var (_, seen) in _builderArenaLastSeen)
            {
                if (seen.Base > ringWindowStart ||
                    ringWindowStart >= seen.Base + RingChunkBytes ||
                    (found && seen.Base <= bestBase))
                {
                    continue;
                }

                bestBase = seen.Base;
                bestSeen = seen;
                found = true;
            }
        }

        if (!found)
        {
            lock (_orphanPreambleGate)
            {
                var count = _builderArenaLastSeen.Count;
                var closestDelta = ulong.MaxValue;
                var closestBase = 0UL;
                foreach (var (_, seen) in _builderArenaLastSeen)
                {
                    var delta = seen.Base > ringWindowStart ? seen.Base - ringWindowStart : ringWindowStart - seen.Base;
                    if (delta < closestDelta)
                    {
                        closestDelta = delta;
                        closestBase = seen.Base;
                    }
                }

                Console.Error.WriteLine(
                    $"[LOADER][WARN] agc.ring_producer_miss window=0x{ringWindowStart:X16} entries={count} " +
                    $"closest_base=0x{closestBase:X16} delta=0x{closestDelta:X16}");
            }

            return false;
        }

        threadHandle = bestSeen.ThreadHandle;
        secondsSinceLastWrite =
            (System.Diagnostics.Stopwatch.GetTimestamp() - bestSeen.Timestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        return true;
    }

    private static readonly HashSet<ulong> _knownBuilderHeaders = new();
    // Unsubmitted tails of abandoned arenas — still valid, ring memory isn't zeroed.
    private static readonly List<(ulong Header, ulong Base, ulong Start, ulong End)> _orphanPreambleClosedSlices = new();

    private static bool IsKnownBuilderVtable(ulong vtable) =>
        vtable is 0x8009F5750UL or 0x800AB4550UL;
    // Ranges the game itself submitted; a header overlapping one is not an orphan.
    private static readonly Dictionary<ulong, (ulong End, long Seq)> _gameSubmittedRanges = new();
    private static long _orphanTrackSequence;
    private static readonly Dictionary<(ulong Header, ulong Base), long> _arenaLapStartSequences = new();
    // Submission is deferred here, not inline, since IsSuspended isn't set
    // until the suspending queue's parse call unwinds.
    private static readonly List<ulong> _orphanPreamblePendingTargets = new();
    private static uint _orphanPreambleSyntheticOwner = 900000;

    // Last packet address to write each fence label, for salvaging a stuck one.
    private static readonly Dictionary<ulong, List<(ulong Packet, ulong OwnerHeader)>> _fenceWritePacketSites = new();
    private static long _lastFenceSalvageTimestamp;
    private static long _lastFenceScanTimestamp;

    private const ulong FenceLabelRegionStart = 0x2000000000UL;
    private const ulong FenceLabelRegionEnd = 0x2000001000UL;

    private static void RecordFenceWritePacketSite(ulong packetAddress, ulong destinationAddress)
    {
        if (!_forceSubmitOrphanPreamblesEnabled ||
            destinationAddress < FenceLabelRegionStart ||
            destinationAddress >= FenceLabelRegionEnd)
        {
            return;
        }

        lock (_orphanPreambleGate)
        {
            if (!_fenceWritePacketSites.TryGetValue(destinationAddress, out var sites))
            {
                sites = new List<(ulong, ulong)>();
                _fenceWritePacketSites[destinationAddress] = sites;
            }

            foreach (var (existingPacket, _) in sites)
            {
                if (existingPacket == packetAddress)
                {
                    return;
                }
            }

            if (sites.Count >= 8)
            {
                return;
            }

            // Resolve the owning builder now, before its header moves on.
            ulong owner = 0;
            foreach (var (headerAddress, seen) in _builderArenaLastSeen)
            {
                if (seen.Base != 0 &&
                    packetAddress >= seen.Base &&
                    packetAddress < seen.Base + 0x10000)
                {
                    owner = headerAddress;
                    break;
                }
            }

            sites.Add((packetAddress, owner));
        }
    }

    private static void SalvageStuckFenceWrites(CpuContext ctx, SubmittedGpuState gpuState)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - Volatile.Read(ref _lastFenceSalvageTimestamp) <
            System.Diagnostics.Stopwatch.Frequency / 10)
        {
            return;
        }

        Volatile.Write(ref _lastFenceSalvageTimestamp, now);

        var candidates = new List<(ulong Dest, ulong Packet, ulong Owner)>();
        lock (_orphanPreambleGate)
        {
            foreach (var (dest, sites) in _fenceWritePacketSites)
            {
                foreach (var (packet, owner) in sites)
                {
                    candidates.Add((dest, packet, owner));
                }
            }
        }

        var salvagedAny = false;
        foreach (var (dest, packet, recordedOwner) in candidates)
        {
            salvagedAny |= TrySalvageFenceWritePacket(ctx, gpuState, dest, packet, recordedOwner);
        }

        // Fallback when the learned-site probe finds nothing: a bounded scan
        // over tracked arenas, matching the label's high dword, handed to
        // the same strict validator (throttled to once a second).
        if (salvagedAny ||
            now - Volatile.Read(ref _lastFenceScanTimestamp) <
                System.Diagnostics.Stopwatch.Frequency)
        {
            return;
        }

        Volatile.Write(ref _lastFenceScanTimestamp, now);

        var destinations = new HashSet<ulong>();
        var windows = new HashSet<ulong>();
        lock (_orphanPreambleGate)
        {
            foreach (var (dest, sites) in _fenceWritePacketSites)
            {
                destinations.Add(dest);
                foreach (var (site, _) in sites)
                {
                    windows.Add(site & ~0xFFFFUL);
                }
            }

            foreach (var (_, seen) in _builderArenaLastSeen)
            {
                if (seen.Base != 0)
                {
                    windows.Add(seen.Base & ~0xFFFFUL);
                }
            }
        }

        foreach (var window in windows)
        {
            var end = window + 0x20000;
            for (var address = window + 12; address + 4 <= end; address += 4)
            {
                if (!TryReadUInt32(ctx, address, out var destinationHigh))
                {
                    break;
                }

                if (destinationHigh != 0x20u ||
                    !TryReadUInt32(ctx, address - 4, out var destinationLow))
                {
                    continue;
                }

                var dest = ((ulong)destinationHigh << 32) | destinationLow;
                if (destinations.Contains(dest))
                {
                    _ = TrySalvageFenceWritePacket(ctx, gpuState, dest, address - 12, recordedOwner: 0);
                }
            }
        }
    }

    private static bool TrySalvageFenceWritePacket(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        ulong dest,
        ulong packet,
        ulong recordedOwner)
    {
        if (!TryReadUInt32(ctx, packet, out var header) ||
            (header >> 30) != 3)
        {
            return false;
        }

        var length = Pm4Length(header);
        var op = (header >> 8) & 0xFFu;
        var register = (header >> 2) & 0x3Fu;
        if (length < 4 || length > 16 ||
            (op != ItWriteData && !(op == ItNop && register == RWriteData)))
        {
            return false;
        }

        if (!TryReadUInt64(ctx, packet + 8, out var packetDest) ||
            packetDest != dest ||
            !TryReadUInt32(ctx, packet + 16, out var value) ||
            !TryReadUInt32(ctx, dest, out var current) ||
            value != current + 1)
        {
            return false;
        }

        var owner = recordedOwner;
        if (owner == 0)
        {
            lock (_orphanPreambleGate)
            {
                foreach (var (headerAddress, seen) in _builderArenaLastSeen)
                {
                    if (seen.Base != 0 &&
                        packet >= seen.Base &&
                        packet < seen.Base + 0x10000)
                    {
                        owner = headerAddress;
                        break;
                    }
                }

                if (owner == 0)
                {
                    // Any known builder's queue can parse this; identity only affects ordering.
                    foreach (var headerAddress in _builderArenaLastSeen.Keys)
                    {
                        owner = headerAddress;
                        break;
                    }
                }
            }
        }

        if (owner == 0)
        {
            return false;
        }

        Console.Error.WriteLine(
            $"[LOADER][WARN] agc.fence_write_salvage packet=0x{packet:X16} " +
            $"dst=0x{dest:X16} value=0x{value:X} current=0x{current:X} header=0x{owner:X}");
        // Deliberately unclipped: the packet may sit inside a range the
        // game submitted on an earlier lap; the strict current+1 check
        // already proves the content is this lap's, and re-executing a
        // literal write of the same value later is idempotent.
        SubmitOrphanSlice(ctx, gpuState, owner, packet, packet + (ulong)length * sizeof(uint), 0);
        return true;
    }

    private static ulong ExtendClosedSliceOverTrailingFenceWrites(
        CpuContext ctx,
        ulong sliceEnd)
    {
        // Crosses decoration packets (EVENT_WRITE, NOPs); only commits through a qualifying write_data.
        var tentativeEnd = sliceEnd;
        for (var walked = 0; walked < 8; walked++)
        {
            if (!TryReadUInt32(ctx, tentativeEnd, out var header) ||
                (header >> 30) != 3)
            {
                return sliceEnd;
            }

            var length = Pm4Length(header);
            if (length == 0 || length > 64)
            {
                return sliceEnd;
            }

            var op = (header >> 8) & 0xFFu;
            var register = (header >> 2) & 0x3Fu;
            var standardWriteData = op == ItWriteData && length >= 4;
            var agcWriteData = op == ItNop && register == RWriteData && length >= 4;
            if (!standardWriteData && !agcWriteData)
            {
                if (op == ItEventWrite || (op == ItNop && register == 0))
                {
                    tentativeEnd += (ulong)length * sizeof(uint);
                    continue;
                }

                return sliceEnd;
            }

            if (!TryReadUInt32(ctx, tentativeEnd + 4, out var control) ||
                !TryReadUInt64(ctx, tentativeEnd + 8, out var destination) ||
                destination == 0)
            {
                return sliceEnd;
            }

            var (dst, _, _, _) = standardWriteData
                ? DecodeStandardWriteDataControl(control)
                : DecodeAgcWriteDataControl(control);
            if (dst is not (1 or 2 or 4 or 5) ||
                !TryReadUInt32(ctx, tentativeEnd + 16, out var packetValue) ||
                !TryReadUInt32(ctx, destination, out var currentValue) ||
                packetValue <= currentValue)
            {
                return sliceEnd;
            }

            tentativeEnd += (ulong)length * sizeof(uint);
            sliceEnd = tentativeEnd;
            Console.Error.WriteLine(
                $"[LOADER][WARN] agc.orphan_slice_tail_extend end=0x{sliceEnd:X16} " +
                $"dst=0x{destination:X16} value=0x{packetValue:X} current=0x{currentValue:X}");
        }

        return sliceEnd;
    }

    private static void TrackCbReleaseMemTarget(
        CpuContext ctx,
        ulong commandBufferAddress,
        ulong destinationAddress)
    {
        // Deliberately NOT filtered to a CPU-visible-label pool: this tracker
        // needs every release_mem target, not just a subset.
        if (!_forceSubmitOrphanPreamblesEnabled || destinationAddress == 0)
        {
            return;
        }

        // Snapshot the header on every packet the builder records; when its
        // base changes, queue the closed arena's remaining slice for the
        // next drain, or its unsubmitted tail is lost for good.
        ulong closedBase = 0, closedStart = 0, closedEnd = 0;
        ulong arenaCursor = 0;
        var haveHeader =
            TryReadUInt64(ctx, commandBufferAddress, out var arenaBase) &&
            TryReadUInt64(ctx, commandBufferAddress + 0x10, out arenaCursor) &&
            arenaBase != 0;

        lock (_orphanPreambleGate)
        {
            if (!_cbReleaseMemTargets.TryGetValue(destinationAddress, out var headers))
            {
                headers = new List<ulong>();
                _cbReleaseMemTargets[destinationAddress] = headers;
            }

            if (!headers.Contains(commandBufferAddress))
            {
                headers.Add(commandBufferAddress);
            }

            _knownBuilderHeaders.Add(commandBufferAddress);
            if (haveHeader)
            {
                var hadSeen = _builderArenaLastSeen.TryGetValue(commandBufferAddress, out var seen);
                if (hadSeen &&
                    seen.Base != 0 &&
                    seen.Base != arenaBase &&
                    seen.Cursor > seen.Base)
                {
                    closedBase = seen.Base;
                    closedEnd = ExtendClosedSliceOverTrailingFenceWrites(ctx, seen.Cursor);
                    _arenaLapStartSequences.TryGetValue(
                        (commandBufferAddress, seen.Base),
                        out var closingLap);
                    closedStart =
                        _orphanPreambleSubmitted.TryGetValue(commandBufferAddress, out var submitted) &&
                        submitted.Base == seen.Base &&
                        submitted.Lap == closingLap
                            ? submitted.Cursor
                            : seen.Base;
                    if (closedStart < closedEnd)
                    {
                        _orphanPreambleClosedSlices.Add(
                            (commandBufferAddress, closedBase, closedStart, closedEnd));
                    }
                }

                if (!hadSeen || seen.Base != arenaBase)
                {
                    // The builder just (re-)entered this arena: a fresh lap
                    // begins, invalidating earlier-lap game submissions of
                    // these addresses for clipping purposes.
                    _arenaLapStartSequences[(commandBufferAddress, arenaBase)] =
                        ++_orphanTrackSequence;
                }

                _builderArenaLastSeen[commandBufferAddress] =
                    (arenaBase, arenaCursor, GuestThreadExecution.CurrentGuestThreadHandle, System.Diagnostics.Stopwatch.GetTimestamp());
            }
        }

        if (closedBase != 0 && closedStart < closedEnd)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] agc.orphan_arena_closed header=0x{commandBufferAddress:X16} " +
                $"base=0x{closedBase:X16} slice=0x{closedStart:X16}-0x{closedEnd:X16}");
        }
    }

    // Hooked at the exact WAIT_REG_MEM suspend site so ctx/gpuState identity
    // matches what GpuWaitRegistry filters waiters on.
    private static void TryForceSubmitOrphanPreamble(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        ulong targetAddress)
    {
        if (!_forceSubmitOrphanPreamblesEnabled)
        {
            return;
        }

        // Actual submission is deferred to DrainPendingOrphanPreambles.
        lock (_orphanPreambleGate)
        {
            if (_cbReleaseMemTargets.ContainsKey(targetAddress) &&
                !_orphanPreamblePendingTargets.Contains(targetAddress))
            {
                _orphanPreamblePendingTargets.Add(targetAddress);
            }
        }
    }

    private static void RecordGameSubmittedRange(ulong commandAddress, uint dwordCount)
    {
        if (!_forceSubmitOrphanPreamblesEnabled || commandAddress == 0 || dwordCount == 0)
        {
            return;
        }

        var end = commandAddress + (ulong)dwordCount * 4;
        lock (_orphanPreambleGate)
        {
            // Replace, never merge — a shorter re-submission (lap restart)
            // must not keep shielding bytes past its real end.
            _gameSubmittedRanges[commandAddress] = (end, ++_orphanTrackSequence);
        }
    }

    // Called only where no DCB parse is on the stack. Loops because
    // submitting one orphan buffer can suspend on the next stage's target.
    private static void DrainPendingOrphanPreambles(CpuContext ctx, SubmittedGpuState gpuState)
    {
        if (!_forceSubmitOrphanPreamblesEnabled)
        {
            return;
        }

        while (true)
        {
            (ulong Header, ulong Base, ulong Start, ulong End) slice;
            lock (_orphanPreambleGate)
            {
                if (_orphanPreambleClosedSlices.Count == 0)
                {
                    break;
                }

                slice = _orphanPreambleClosedSlices[0];
                _orphanPreambleClosedSlices.RemoveAt(0);
            }

            long lapSequence;
            lock (_orphanPreambleGate)
            {
                _arenaLapStartSequences.TryGetValue((slice.Header, slice.Base), out lapSequence);
            }

            SubmitOrphanSliceClipped(
                ctx,
                gpuState,
                slice.Header,
                slice.Start,
                slice.End,
                targetAddress: 0,
                minimumRangeSequence: lapSequence);
        }

        while (true)
        {
            ulong targetAddress;
            ulong[] pendingHeaders;
            lock (_orphanPreambleGate)
            {
                if (_orphanPreamblePendingTargets.Count == 0)
                {
                    return;
                }

                targetAddress = _orphanPreamblePendingTargets[0];
                _orphanPreamblePendingTargets.RemoveAt(0);

                // Offer every builder of this target, not just one — counter
                // fences only pass once all producers' release_mem lands.
                pendingHeaders = _cbReleaseMemTargets.TryGetValue(targetAddress, out var headers)
                    ? OrderHeadersByConstructionTimeLocked(headers)
                    : Array.Empty<ulong>();
            }

            foreach (var headerAddress in pendingHeaders)
            {
                ForceSubmitOrphanPreambleHeader(ctx, gpuState, headerAddress, targetAddress);
            }
        }
    }

    // FIFO-preserving flush of one header's staged closed-arena slices. See
    // the call site in ForceSubmitOrphanPreambleHeader: a header's current
    // arena must never be enqueued ahead of its own abandoned predecessor.
    private static void FlushClosedSlicesForHeader(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        ulong headerAddress)
    {
        while (true)
        {
            (ulong Header, ulong Base, ulong Start, ulong End) slice = default;
            long lapSequence = 0;
            lock (_orphanPreambleGate)
            {
                var found = false;
                for (var index = 0; index < _orphanPreambleClosedSlices.Count; index++)
                {
                    if (_orphanPreambleClosedSlices[index].Header == headerAddress)
                    {
                        slice = _orphanPreambleClosedSlices[index];
                        _orphanPreambleClosedSlices.RemoveAt(index);
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return;
                }

                _arenaLapStartSequences.TryGetValue((slice.Header, slice.Base), out lapSequence);
            }

            SubmitOrphanSliceClipped(
                ctx,
                gpuState,
                slice.Header,
                slice.Start,
                slice.End,
                targetAddress: 0,
                minimumRangeSequence: lapSequence);
        }
    }

    private static void ForceSubmitOrphanPreambleHeader(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        ulong headerAddress,
        ulong targetAddress)
    {
        if (!TryReadUInt64(ctx, headerAddress, out var commandAddress) ||
            !TryReadUInt64(ctx, headerAddress + 8, out var limitAddress) ||
            !TryReadUInt64(ctx, headerAddress + 0x10, out var cursor) ||
            !TryReadUInt64(ctx, headerAddress + 0x20, out var vtable) ||
            commandAddress == 0)
        {
            // Transient, not a permanent skip — a builder can read as garbage
            // before it's fully constructed and become valid later.
            lock (_orphanPreambleGate)
            {
                if (!_orphanPreambleUnreadableLogged.Add(headerAddress))
                {
                    return;
                }
            }

            Console.Error.WriteLine(
                $"[LOADER][WARN] agc.orphan_preamble_skip header=0x{headerAddress:X16} " +
                $"target=0x{targetAddress:X16} (unreadable at trigger time; will retry)");
            return;
        }

        if (!IsKnownBuilderVtable(vtable))
        {
            lock (_orphanPreambleGate)
            {
                if (_orphanPreambleSubmitted.TryGetValue(headerAddress, out var seen) &&
                    seen.Base == 0)
                {
                    return;
                }

                _orphanPreambleSubmitted[headerAddress] = (0, 0, 0);
            }

            Console.Error.WriteLine(
                $"[LOADER][WARN] agc.orphan_preamble_skip header=0x{headerAddress:X16} " +
                $"target=0x{targetAddress:X16} vtable=0x{vtable:X16} (not an orphan builder class)");
            return;
        }

        // Covers a builder that moves to a new arena without ever building
        // another release_mem, which would otherwise leave its abandoned tail lost.
        lock (_orphanPreambleGate)
        {
            if (_builderArenaLastSeen.TryGetValue(headerAddress, out var lastSeen) &&
                lastSeen.Base != 0 &&
                lastSeen.Base != commandAddress &&
                lastSeen.Cursor > lastSeen.Base)
            {
                _arenaLapStartSequences.TryGetValue(
                    (headerAddress, lastSeen.Base),
                    out var closingLap);
                var closedStart =
                    _orphanPreambleSubmitted.TryGetValue(headerAddress, out var submittedBefore) &&
                    submittedBefore.Base == lastSeen.Base &&
                    submittedBefore.Lap == closingLap
                        ? submittedBefore.Cursor
                        : lastSeen.Base;
                var closedEnd = ExtendClosedSliceOverTrailingFenceWrites(ctx, lastSeen.Cursor);
                if (closedStart < closedEnd)
                {
                    _orphanPreambleClosedSlices.Add(
                        (headerAddress, lastSeen.Base, closedStart, closedEnd));
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] agc.orphan_arena_closed header=0x{headerAddress:X16} " +
                        $"base=0x{lastSeen.Base:X16} slice=0x{closedStart:X16}-0x{closedEnd:X16} " +
                        "(sweep-detected switch)");
                }

                _arenaLapStartSequences[(headerAddress, commandAddress)] = ++_orphanTrackSequence;
                _builderArenaLastSeen[headerAddress] =
                    (commandAddress, cursor, lastSeen.ThreadHandle, lastSeen.Timestamp);
            }
        }

        // Flush this header's staged closures first, or the current-arena
        // submission below could overtake its own predecessor.
        FlushClosedSlicesForHeader(ctx, gpuState, headerAddress);

        if (cursor <= commandAddress)
        {
            return;
        }

        // Extends past the cursor for fence writes some titles make via raw
        // guest code, invisible to any AGC builder API.
        var extendedEnd = ExtendClosedSliceOverTrailingFenceWrites(ctx, cursor);

        ulong sliceStart;
        long lapSequence;
        lock (_orphanPreambleGate)
        {
            _arenaLapStartSequences.TryGetValue((headerAddress, commandAddress), out lapSequence);
            if (_orphanPreambleSubmitted.TryGetValue(headerAddress, out var last))
            {
                if (last.Base == 0)
                {
                    return; // permanently skipped
                }

                if (last.Base == commandAddress &&
                    last.Lap == lapSequence &&
                    cursor == last.Cursor)
                {
                    if (extendedEnd == cursor)
                    {
                        return; // no new content since the last slice
                    }

                    // Cursor unchanged; only the raw-written fence tail is new.
                    sliceStart = cursor;
                }
                else
                {
                    // Same base+lap grown -> delta only. Otherwise restart
                    // from the arena base; cross-lap cursors are meaningless.
                    sliceStart = last.Base == commandAddress &&
                        last.Lap == lapSequence &&
                        cursor > last.Cursor
                        ? last.Cursor
                        : commandAddress;
                }
            }
            else
            {
                sliceStart = commandAddress;
            }

            _orphanPreambleSubmitted[headerAddress] = (commandAddress, cursor, lapSequence);
        }

        SubmitOrphanSliceClipped(
            ctx,
            gpuState,
            headerAddress,
            sliceStart,
            extendedEnd,
            targetAddress,
            minimumRangeSequence: lapSequence);
    }

    // Clips out sub-ranges the game already submitted, to avoid double-executing them.
    private static void SubmitOrphanSliceClipped(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        ulong headerAddress,
        ulong sliceStart,
        ulong sliceEnd,
        ulong targetAddress,
        long minimumRangeSequence = 0)
    {
        while (sliceStart < sliceEnd)
        {
            ulong segmentEnd;
            lock (_orphanPreambleGate)
            {
                var advanced = true;
                while (advanced)
                {
                    advanced = false;
                    foreach (var (rangeStart, range) in _gameSubmittedRanges)
                    {
                        if (range.Seq >= minimumRangeSequence &&
                            sliceStart >= rangeStart && sliceStart < range.End)
                        {
                            sliceStart = range.End;
                            advanced = true;
                        }
                    }
                }

                if (sliceStart >= sliceEnd)
                {
                    return;
                }

                segmentEnd = sliceEnd;
                foreach (var (rangeStart, range) in _gameSubmittedRanges)
                {
                    if (range.Seq >= minimumRangeSequence &&
                        rangeStart > sliceStart && rangeStart < segmentEnd)
                    {
                        segmentEnd = rangeStart;
                    }
                }
            }

            SubmitOrphanSlice(ctx, gpuState, headerAddress, sliceStart, segmentEnd, targetAddress);
            sliceStart = segmentEnd;
        }
    }

    // Catches labels only ever polled via CPU-side usleep loops (no waiter registered).
    private static void SweepBuilderArenas(CpuContext ctx, SubmittedGpuState gpuState)
    {
        ulong[] headers;
        lock (_orphanPreambleGate)
        {
            if (_knownBuilderHeaders.Count == 0)
            {
                return;
            }

            headers = OrderHeadersByConstructionTimeLocked(_knownBuilderHeaders);
        }

        foreach (var headerAddress in headers)
        {
            ForceSubmitOrphanPreambleHeader(ctx, gpuState, headerAddress, targetAddress: 0);
        }
    }

    // Sorts by wall-clock time of each header's latest checkpoint, oldest
    // first. Must be called with _orphanPreambleGate already held.
    private static ulong[] OrderHeadersByConstructionTimeLocked(IEnumerable<ulong> headers)
    {
        var ordered = headers.ToArray();
        Array.Sort(ordered, (a, b) =>
        {
            var hasA = _builderArenaLastSeen.TryGetValue(a, out var seenA);
            var hasB = _builderArenaLastSeen.TryGetValue(b, out var seenB);
            var tsA = hasA ? seenA.Timestamp : 0;
            var tsB = hasB ? seenB.Timestamp : 0;
            return tsA.CompareTo(tsB);
        });
        return ordered;
    }

    private static void SubmitOrphanSlice(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        ulong headerAddress,
        ulong sliceStart,
        ulong sliceEnd,
        ulong targetAddress)
    {
        var dwordCount = (uint)((sliceEnd - sliceStart) / 4);
        if (dwordCount == 0)
        {
            return;
        }

        uint owner;
        lock (_orphanPreambleGate)
        {
            owner = ++_orphanPreambleSyntheticOwner;
        }

        Console.Error.WriteLine(
            $"[LOADER][WARN] agc.orphan_preamble_force_submit header=0x{headerAddress:X16} " +
            $"command=0x{sliceStart:X16} dwords={dwordCount} targetLabel=0x{targetAddress:X16} " +
            $"owner={owner}");

        lock (gpuState.Gate)
        {
            var queueState = new SubmittedDcbState
            {
                QueueName = $"acb.orphan_preamble[0x{headerAddress:X}]",
                IsForceSubmittedRing = true,
            };
            gpuState.ComputeQueues.Add(owner, queueState);
            EnqueueSubmittedDcb(
                ctx,
                gpuState,
                queueState,
                sliceStart,
                dwordCount,
                ++gpuState.SubmissionSequence,
                tracePackets: true);
            DrainResumableDcbs(ctx, gpuState, tracePackets: true);
        }
    }

    // Parse window for a ring resuming at appended commands: covers a full
    // chunk, safe since parsing re-suspends at the next unwritten word.
    private const uint RingResumeWindowDwords = 0x8000;
    private const uint RIndexBase = 0x1B;
    private const uint RIndexCount = 0x1C;
    private const uint SpiShaderPgmLoPs = 0x8;
    private const uint SpiShaderPgmHiPs = 0x9;
    private const uint SpiShaderPgmLoVs = 0x48;
    private const uint SpiShaderPgmHiVs = 0x49;
    private const uint SpiShaderPgmLoEs = 0xC8;
    private const uint SpiShaderPgmHiEs = 0xC9;
    private const uint SpiShaderPgmLoHs = 0x108;
    private const uint SpiShaderPgmHiHs = 0x109;
    private const uint SpiShaderPgmLoLs = 0x148;
    private const uint SpiShaderPgmHiLs = 0x149;
    // Not 0x8A/0x8B - those are SPI_SHADER_PGM_RSRC1/RSRC2_GS, and reading them
    // as an address yields a nonsensical 58-bit value.
    private const uint SpiShaderPgmLoGs = 0x88;
    private const uint SpiShaderPgmHiGs = 0x89;
    private const uint SpiShaderPgmRsrc1Gs = 0x8A;
    private const uint SpiShaderPgmRsrc2Gs = 0x8B;
    private const uint SpiPsInputEna = 0x1B3;
    private const uint SpiPsInputAddr = 0x1B4;
    private const uint ComputePgmLo = 0x20C;
    private const uint ComputePgmHi = 0x20D;
    private const uint ComputePgmRsrc2 = 0x213;
    private const uint ComputeStartX = 0x204;
    private const uint ComputeStartY = 0x205;
    private const uint ComputeStartZ = 0x206;
    private const uint ComputeNumThreadX = 0x207;
    private const uint ComputeNumThreadY = 0x208;
    private const uint ComputeNumThreadZ = 0x209;
    private const uint SpiPsInputCntl0 = 0x191;
    private const uint VgtPrimitiveType = 0x242;
    private const uint VgtIndexType = 0x243;
    // GE_INDX_OFFSET — base vertex for DrawIndexed / firstVertex for
    // DrawIndexAuto. Glyph meshes and UI icon batches rely on this.
    private const uint GeIndxOffset = 0x24A;
    private const uint VgtGsOutPrimType = 0x29B;
    private const uint VgtShaderStagesEn = 0x2D5;
    private const uint GeCntl = 0x25B;
    private const uint GeUserVgpr1 = 0x25C;
    private const uint GeUserVgpr2 = 0x25D;
    private const uint GeUserVgpr3 = 0x25E;
    private const uint GeUserVgprEn = 0x262;
    private const uint PaScScreenScissorTl = 0x0C;
    private const uint PaScScreenScissorBr = 0x0D;
    private const uint CbTargetMask = 0x8E;
    private const uint PaScWindowOffset = 0x80;
    private const uint PaScWindowScissorTl = 0x81;
    private const uint PaScWindowScissorBr = 0x82;
    private const uint PaScGenericScissorTl = 0x90;
    private const uint PaScGenericScissorBr = 0x91;
    private const uint PaScVportScissor0Tl = 0x94;
    private const uint PaScVportScissor0Br = 0x95;
    private const uint PaClVportXScale = 0x10F;
    private const uint PaClVportXOffset = 0x110;
    private const uint PaClVportYScale = 0x111;
    private const uint PaClVportYOffset = 0x112;
    private const uint PaScVportZMin0 = 0xB4;
    private const uint PaScVportZMax0 = 0xB5;
    private const uint CbColorControl = 0x202;
    private const uint CbBlendRed = 0x105;
    private const uint CbBlendGreen = 0x106;
    private const uint CbBlendBlue = 0x107;
    private const uint CbBlendAlpha = 0x108;
    private const uint CbColor0Base = 0x318;
    private const uint CbColorRegisterStride = 15;
    private const uint CbColor0Info = 0x31C;
    private const uint CbColor0ClearWord0 = 0x323;
    private const uint CbColor0ClearWord1 = 0x324;
    private const uint CbColor0BaseExt = 0x390;
    private const uint CbColor0Attrib2 = 0x3B0;
    private const uint CbColor0Attrib3 = 0x3B8;
    // CB_COLORn_INFO.DCC_ENABLE (gc_10_1_0_sh_mask.h). On GFX10 the legacy
    // FAST_CLEAR and COMPRESSION bits stay clear because DCC, not CMASK,
    // carries the compression.
    private const uint CbColorInfoDccEnableMask = 1u << 28;
    private const uint CbBlend0Control = 0x1E0;
    private const uint PaScModeCntl0 = 0x292;
    // GFX10 DB context registers (register byte address minus 0x28000, / 4).
    private const uint DbRenderControl = 0x000;
    private const uint DbDepthView = 0x002;
    private const uint DbDepthSizeXy = 0x007;
    private const uint DbDepthClear = 0x00B;
    private const uint DbZInfo = 0x010;
    private const uint DbZReadBase = 0x012;
    private const uint DbZWriteBase = 0x014;
    private const uint DbZReadBaseHi = 0x01A;
    private const uint DbZWriteBaseHi = 0x01C;
    private const int ColorTargetCount = 8;
    private const uint PsTextureUserDataRegister = 0xC;
    private const uint VsUserDataRegister = 0x4C;
    private const uint GsIndirectUserDataLowRegister = 0x82;
    private const uint GsIndirectUserDataHighRegister = 0x83;
    private const uint GsUserDataRegister = 0x8C;
    private const uint EsUserDataRegister = 0xCC;
    private const uint ComputeUserDataRegister = 0x240;
    private const uint NggUserDataScalarRegisterBase = 8;
    private const uint Gen5TextureFormatR8G8B8A8Unorm = 10;
    private const uint Gen5TextureFormatR16G16B16A16Float = 12;
    private const uint Gen5TextureType1D = 8;
    private const uint Gen5TextureType2D = 9;
    private const uint Gen5TextureType3D = 10;
    private const uint Gen5TextureTypeCube = 11;
    private const uint Gen5TextureType1DArray = 12;
    private const uint Gen5TextureType2DArray = 13;
    private const ulong MaxPresentedTextureBytes = 128UL * 1024UL * 1024UL;
    private const ulong VideoOutPixelFormatA8R8G8B8Srgb = 0x80000000;
    private const ulong VideoOutPixelFormatA8B8G8R8Srgb = 0x80002200;
    private const ulong VideoOutPixelFormat2R8G8B8A8Srgb = 0x8000000022000000;
    private const ulong VideoOutPixelFormat2B8G8R8A8Srgb = 0x8000000000000000;
    private const ulong VideoOutPixelFormat2R10G10B10A2 = 0x8100000622000000;
    private const ulong VideoOutPixelFormat2B10G10R10A2 = 0x8100000600000000;
    private const ulong VideoOutPixelFormat2R10G10B10A2Srgb = 0x8100000022000000;
    private const ulong VideoOutPixelFormat2B10G10R10A2Srgb = 0x8100000000000000;
    private const ulong VideoOutPixelFormat2R10G10B10A2Bt2100Pq = 0x8100070422000000;
    private const ulong VideoOutPixelFormat2B10G10R10A2Bt2100Pq = 0x8100070400000000;
    private const uint RegisterDefaultsVersion7 = 7;
    private const uint RegisterDefaultsVersion8 = 8;
    private const uint RegisterDefaultsVersion10 = 10;
    private const uint RegisterDefaultsVersion11 = 11;
    private const uint RegisterDefaultsVersion13 = 13;
    private const int RegisterDefaultsSize = 0x40;
    private const int RegisterDefaultBlockSize = 16 * 8;
    // GDS is device-global storage, not address-zero per-draw scratch.  Give it
    // a stable synthetic identity so the Vulkan guest-buffer cache preserves
    // its contents across translated draws without exposing it as guest RAM.
    private const ulong SyntheticGdsBaseAddress = 0xFFFF_FFFE_0000_0000;
    private const ulong SyntheticNggOutputBaseAddress = 0xFFFF_FFFD_0000_0000;
    private static readonly byte[] _persistentGds =
        new byte[Gen5SpirvTranslator.GdsByteSize];

    private const ulong ShaderUserDataOffset = 0x08;
    private const ulong ShaderCodeOffset = 0x10;
    private const ulong ShaderCxRegistersOffset = 0x18;
    private const ulong ShaderShRegistersOffset = 0x20;
    private const ulong ShaderSpecialsOffset = 0x28;
    private const ulong ShaderInputSemanticsOffset = 0x30;
    private const ulong ShaderOutputSemanticsOffset = 0x38;
    private const ulong ShaderSizeOffset = 0x44;
    private const ulong ResourceRegistrationBytesPerResource = 0x118;
    private const ulong ResourceRegistrationBytesPerOwner = 0x1E0;
    private const int ResourceRegistrationMaxNameLength = 256;
    private const ulong ShaderNumInputSemanticsOffset = 0x50;
    private const ulong ShaderNumOutputSemanticsOffset = 0x56;
    private const ulong ShaderTypeOffset = 0x5A;
    private const byte ComputeShaderType = 0;
    private const byte PsShaderType = 1;
    private const byte GsShaderType = 2;
    private const byte HsShaderType = 3;
    private const byte GsFrontShaderType = 4;
    private const byte HsFrontShaderType = 5;
    private const byte GsBackShaderType = 6;
    private const byte HsBackShaderType = 7;
    private const ulong ShaderNumShRegistersOffset = 0x5C;
    private const ulong FusedShaderImageAlignment = 4;
    private const uint SpiShaderPgmChksumGs = 0x80;
    private const int AgcErrorIncompatibleShaderPair = unchecked((int)0x8A6C0008);
    private const int AgcErrorInvalidPatchDescriptor = unchecked((int)0x8A6C000C);
    private const int AgcDriverErrorInvalidArgument = unchecked((int)0x8A6DFFFF);
    private const uint AgcDriverTfRingMaximumSize = 0x4000;
    private const int ShaderDescriptorSize = 0x60;
    private const uint InternalGsRegister = 0x080;
    private const uint InternalHsRegister = 0x100;
    private const uint SpiShaderPgmRsrc1Hs = 0x10A;
    private const uint SpiShaderPgmRsrc2Hs = 0x10B;
    private const ulong CommandBufferCursorUpOffset = 0x10;
    private const ulong CommandBufferCursorDownOffset = 0x18;
    private const ulong CommandBufferCallbackOffset = 0x20;
    private const ulong CommandBufferUserDataOffset = 0x28;
    private const ulong CommandBufferReservedDwOffset = 0x30;
    private const ulong ShaderSpecialGeCntlOffset = 0x00;
    private const ulong ShaderSpecialVgtShaderStagesEnOffset = 0x08;
    private const uint VgtShaderStagesHsW32EnBit = 1u << 21;
    private const uint VgtShaderStagesGsW32EnBit = 1u << 22;
    private const ulong ShaderSpecialVgtGsOutPrimTypeOffset = 0x20;
    private const ulong ShaderSpecialGeUserVgprEnOffset = 0x28;
    private const uint CbSetShRegisterRangeMarker = 0x6875000D;
    private const int AvPlayerComputeBindingTraceLimit = 256;
    private static readonly object _submitTraceGate = new();
    private static readonly HashSet<uint> _tracedDcbSizes = new();
    private static readonly HashSet<(ulong Es, ulong Ps, GuestDrawKind Kind)> _tracedShaderTranslations = new();
    private static readonly HashSet<(ulong Es, ulong Ps)> _tracedShaderDecodePairs = new();
    private static readonly HashSet<string> _tracedVertexBufferStates = new();
    private static readonly HashSet<ulong> _tracedVertexShaderInstructions = new();
    private static readonly HashSet<string> _tracedVertexBufferDistributions = new();
    private static readonly HashSet<string> _tracedGlobalBufferLengthStates = new();
    private static readonly HashSet<string> _tracedIndexedGlobalBufferStates = new();
    private static readonly HashSet<string> _tracedIndexedGlobalBufferVertexDraws = new();
    private static readonly HashSet<(ulong Es, ulong Ps, ulong Target, ulong Texture, uint VertexCount)> _tracedShaderDraws = new();
    private static readonly HashSet<(ulong Ps, string Error)> _tracedShaderFailures = new();
    private static readonly ConcurrentDictionary<
        (ulong Ps, ulong Address, uint Width, uint Height, uint Format, uint NumberType,
         uint TileMode, uint Pitch, uint DstSelect), byte> _tracedAddressedTextureBindings = new();
    private static readonly ConcurrentDictionary<
        (string Stage, ulong Shader, ulong Address, int Length, bool Writable), byte>
        _tracedAvPlayerGlobalBindings = new();
    private static readonly HashSet<
        (ulong Cs, ulong Address, int Length, bool Writable, uint ScalarAddress,
         uint GroupX, uint GroupY, uint GroupZ, uint LocalX, uint LocalY, uint LocalZ)>
        _tracedAvPlayerComputeGlobalBindings = new();
    private static readonly HashSet<
        (ulong Cs, uint Pc, ulong Address, uint Width, uint Height, uint Format,
         uint NumberType, uint TileMode, uint Pitch, uint DstSelect, bool Storage,
         uint GroupX, uint GroupY, uint GroupZ, uint LocalX, uint LocalY, uint LocalZ)>
        _tracedAvPlayerComputeImageBindings = new();
    private static readonly HashSet<(int Handle, int Index, ulong Address, string Path)> _tracedDisplayBuffers = new();
    private static readonly HashSet<ulong> _tracedComputeShaders = new();
    private static readonly HashSet<ulong> _tracedEmptySrtDrawRejects = new();
    private static readonly HashSet<(ulong Es, ulong Ps)> _tracedFixedFullscreenClears = new();
    private static readonly HashSet<(ulong Address, uint X, uint Y, uint Z)>
        _tracedDispatchArguments = new();
    private static readonly HashSet<(ulong Address, uint Initiator, string Reason)>
        _rejectedDispatchArguments = new();
    private static readonly ulong[] _dumpShaderProgramAddresses = ParseHexAddresses(
        Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SHADER_PROGRAM_ADDRS"));
    private static readonly ulong[] _dumpSpirvAddresses = ParseHexAddresses(
        Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV_ADDRS"));
    private static readonly string? _dumpShaderProgramDirectory =
        Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SHADER_PROGRAM_DIR");
    private static readonly ConcurrentDictionary<(string Stage, ulong Address), byte>
        _dumpedShaderPrograms = new();
    private static readonly HashSet<uint> _tracedSubmittedDrawOpcodes = new();
    // Every PM4 opcode with no handler, logged once with its first two
    // payload dwords as a possible target address.
    private static readonly HashSet<uint> _seenUnknownOpcodes = new();
    // Concurrent so the per-draw/per-dispatch hit path is lock-free (and no longer
    // shares _submitTraceGate with tracing).
    private static readonly ConcurrentDictionary<
        (ulong Es, ulong EsState, uint EsRsrc1, ulong Ps, ulong PsState,
         uint PsRsrc1, ulong OutputLayout, uint OutputMasks, uint OutputCount, uint Attributes,
         uint PsInputEna, uint PsInputAddr, ulong InputControls, bool UsesGds,
         ulong AliasAlignment),
        (IGuestCompiledShader Vertex, IGuestCompiledShader Pixel)> _graphicsShaderCache = new();
    private static readonly ConcurrentDictionary<
        (ulong Es, ulong EsState, uint EsRsrc1, ulong Ps, ulong PsState,
         uint PsRsrc1, ulong OutputLayout, uint OutputMasks, uint OutputCount, uint Attributes,
         uint PsInputEna, uint PsInputAddr, ulong InputControls, bool UsesGds,
         uint NggParameters, ulong AliasAlignment),
        (IGuestCompiledShader Compute, IGuestCompiledShader Vertex,
         IGuestCompiledShader Pixel)> _nggGraphicsShaderCache = new();
    private static readonly ConcurrentDictionary<(ulong Shader, int Bytes), byte[]>
        _nggOutputBuffers = new();
    private static readonly ConcurrentDictionary<
        (ulong Cs, ulong State, uint Rsrc1, uint LocalX, uint LocalY, uint LocalZ,
         uint WaveLanes, bool UsesGds, ulong AliasAlignment),
        IGuestCompiledShader> _computeShaderCache = new();
    private static readonly ConcurrentDictionary<
        (ulong Es, ulong State, uint Rsrc1, ulong AliasAlignment),
        IGuestCompiledShader> _depthOnlyVertexShaderCache = new();
    private static readonly Dictionary<ulong, ulong> _shaderHeadersByCode = new();
    private static readonly Dictionary<ulong, ulong> _createdShaderHeadersByCode = new();
    private static readonly ConditionalWeakTable<
        object,
        ConcurrentDictionary<(ulong Code, ulong Header), byte>>
        _embeddedCombinedShaderScanAttempts = new();
    private static readonly ConcurrentDictionary<ulong, byte> _arrayUploadUnsupported = new();
    private static long _duplicateTargetTraceCount;
    private static long _labelWriteFailureCount;
    private static readonly bool _traceAgc = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"),
        "1",
        StringComparison.Ordinal);
    // Diagnostic settings are process-launch configuration. Keep them off the
    // draw/texture hot paths: Environment.GetEnvironmentVariable crosses into
    // native code and is especially expensive while the guest runs in Rosetta.
    private static readonly string? _traceGuestImagesMode =
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES");
    private static readonly bool _traceTitleGlobals = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TITLE_GLOBALS"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceTitleGlobalsLive = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TITLE_GLOBALS_LIVE"),
        "1",
        StringComparison.Ordinal);
    private static readonly string? _traceVertexBufferRecord =
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_VERTEX_BUFFER_RECORD");
    private static readonly bool _traceVertexBufferRecordSummary = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_VERTEX_BUFFER_RECORD_SUMMARY"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceVertexShaderFull = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_VERTEX_SHADER_FULL"),
        "1",
        StringComparison.Ordinal);
    private static readonly ulong? _traceStorageImageInitAddress = ParseOptionalHexAddress(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_STORAGE_IMAGE_INIT_ADDRESS"));
    private static readonly string? _textureDumpDirectory =
        Environment.GetEnvironmentVariable("SHARPEMU_TEXTURE_DUMP_DIR");
    private static readonly string? _linearTextureDumpDirectory =
        Environment.GetEnvironmentVariable("SHARPEMU_TEXTURE_LINEAR_DUMP_DIR");
    private static readonly bool _dumpSpirv = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV"),
        "1",
        StringComparison.Ordinal);
    private static readonly string? _dumpSpirvAddress =
        Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV_ADDRESS");
    // Drop a draw on an undecodable texture descriptor instead of substituting
    // a 1x1 fallback binding. Off by default so a garbage descriptor degrades
    // the pass rather than dropping it (Demon's Souls composite feeders).
    private static readonly bool _strictShaderDescriptors = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_STRICT_SHADER_DESCRIPTORS"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceAgcShader =
        _traceAgc ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC_SHADER"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool _enableNggComputeRaster =
        IsNggComputeRasterEnabled(
            Environment.GetEnvironmentVariable("SHARPEMU_ENABLE_NGG_COMPUTE_RASTER"));
    private static readonly ulong? _traceComputeShaderAddress = ParseOptionalHexAddress(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_COMPUTE_SHADER_ADDRESS"));
    private static readonly ulong[] _tracePixelShaderAddresses = ParseHexAddresses(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_PIXEL_SHADER_ADDRESS"));
    private static readonly ulong? _traceVertexShaderAddress = ParseOptionalHexAddress(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_VERTEX_SHADER_ADDRESS"));
    private static readonly ulong? _traceCombinedShaderAddress = ParseOptionalHexAddress(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_COMBINED_SHADER_ADDRESS"));
    private static readonly int? _traceGlobalBufferLength = ParseOptionalPositiveInt(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GLOBAL_BUFFER_LENGTH"));
    private static readonly ulong[] _traceGlobalBufferAddresses = ParseHexAddresses(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GLOBAL_BUFFER_ADDRS"));
    private static readonly ulong[] _traceGuestImageAddresses = ParseHexAddresses(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_ADDRS"));
    private static readonly ulong[] _traceTextureBindingAddresses = ParseHexAddresses(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TEXTURE_BINDING_ADDRS"));
    private static readonly ulong[] _probeZeroIndirectDispatchShaderAddresses =
        ParseHexAddresses(
            Environment.GetEnvironmentVariable(
                "SHARPEMU_PROBE_ZERO_INDIRECT_DISPATCH_SHADER_ADDRS"));
    private static readonly ulong? _traceIndexedGlobalBufferShaderAddress =
        ParseOptionalHexAddress(
            Environment.GetEnvironmentVariable(
                "SHARPEMU_TRACE_INDEXED_GLOBAL_BUFFER_SHADER_ADDRESS"));
    private static readonly int[] _traceIndexedGlobalBufferSpec = ParseNonnegativeInts(
        Environment.GetEnvironmentVariable(
            "SHARPEMU_TRACE_INDEXED_GLOBAL_BUFFER_SPEC"));
    private static readonly int? _traceIndexedGlobalBufferVertexBaseScalar =
        ParseOptionalPositiveInt(
            Environment.GetEnvironmentVariable(
                "SHARPEMU_TRACE_INDEXED_GLOBAL_BUFFER_VERTEX_BASE_SGPR"));
    private static readonly int _traceIndexedGlobalBufferInterval =
        ParseOptionalPositiveInt(
            Environment.GetEnvironmentVariable(
                "SHARPEMU_TRACE_INDEXED_GLOBAL_BUFFER_INTERVAL")) ?? 16;
    private static readonly bool _traceIndexedGlobalBufferCpuWrites = string.Equals(
        Environment.GetEnvironmentVariable(
            "SHARPEMU_TRACE_INDEXED_GLOBAL_BUFFER_CPU_WRITES"),
        "1",
        StringComparison.Ordinal);
    private static long _traceIndexedGlobalBufferOccurrence;
    private static readonly (ulong Address, ulong Length)[] _traceGuestMemoryRanges =
        ParseHexRanges(
            Environment.GetEnvironmentVariable(
                "SHARPEMU_TRACE_GUEST_MEMORY_RANGES"));
    private static readonly bool _traceGuestMemoryCpuWrites = string.Equals(
        Environment.GetEnvironmentVariable(
            "SHARPEMU_TRACE_GUEST_MEMORY_CPU_WRITES"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceCopyData = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_COPY_DATA"),
        "1",
        StringComparison.Ordinal);
    private static readonly ulong? _traceRenderTargetAddress = ParseOptionalHexAddress(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_RENDER_TARGET_ADDRESS"));
    private static readonly ulong? _forceExplicitVertexFetchShaderAddress =
        ParseOptionalHexAddress(
            Environment.GetEnvironmentVariable(
                "SHARPEMU_FORCE_EXPLICIT_VERTEX_FETCH_SHADER_ADDRESS"));
    private static readonly bool _traceDraws = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_DRAWS"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceTitleDraws = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TITLE_DRAW"),
        "1",
        StringComparison.Ordinal);
    private static readonly ConcurrentDictionary<
        (ulong ExportShaderAddress, ulong PixelShaderAddress), byte>
        _tracedTitleShaderPairs = new();
    private static readonly bool _traceFramePackets = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_FRAME_PACKETS"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceVertexRanges = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_VERTEX_RANGES"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceVertexBufferDistribution = string.Equals(
        Environment.GetEnvironmentVariable(
            "SHARPEMU_TRACE_VERTEX_BUFFER_DISTRIBUTION"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _compatibilitySubmitCompletionEvent = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_AGC_SUBMIT_COMPLETION_EVENT"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceGuestThroughput = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_THROUGHPUT"),
        "1",
        StringComparison.Ordinal);
    private static long _agcThroughputWindowStartTicks =
        System.Diagnostics.Stopwatch.GetTimestamp();
    private static long _agcThroughputParseTicks;
    private static long _agcThroughputParseMaxTicks;
    private static long _agcThroughputParseDwords;
    private static long _agcThroughputParseCalls;
    // Escape hatch for the cached-texture copy skip (per-draw texel copies
    // are re-enabled unconditionally when set), for A/B-ing rendering issues.
    private static readonly bool _textureCopySkipDisabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_NO_TEXTURE_SKIP"),
        "1",
        StringComparison.Ordinal);

    // GPU deswizzle: ship raw tiled bytes + params to the backend instead of
    // detiling on the CPU. On by default; SHARPEMU_GPU_DETILE=0 forces the CPU
    // path. Backend-agnostic here (only inspects DetileParams); the Vulkan/Metal
    // backends detile on the GPU, others fall back to the CPU path.
    private static readonly bool _gpuDetileEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_DETILE"),
        "0",
        StringComparison.Ordinal);

    // Diagnostics (SHARPEMU_LOG_GPU_DETILE=1): one line per distinct texture tile
    // mode and per-gate decision, so we can see which swizzle modes/formats a
    // title uses and whether each takes the GPU or CPU path.
    private static readonly bool _gpuDetileLog = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_GPU_DETILE"),
        "1",
        StringComparison.Ordinal);
    private static readonly HashSet<uint> _seenTextureTileModes = new();
    private static readonly HashSet<uint> _gpuDetileGateDiag = new();
    private static long _dcbWriteDataTraceCount;
    private static long _copyDataProbeTraceCount;
    private static long _guestMemoryPacketProbeTraceCount;
    private const int MaximumGuestMemoryCpuWriterOccurrences = 16;
    private static readonly ConcurrentDictionary<ulong, int>
        _guestMemoryCpuWriterOccurrences = new();
    private static int _tracedVertexRangeCount;
    private static long _dcbWaitRegMemTraceCount;
    private static long _createShaderTraceCount;
    private static long _cbMetadataSkipTraceCount;
    private static long _packetPayloadTraceCount;
    private static bool _tracedMissingPixelShaderBindings;
    private static long _unsatisfiedWaitTraceCount;
    private static long _labelProducerSequence;
    private static readonly object _labelProducerGate = new();
    private static readonly List<LabelProducerTrace> _labelProducers = [];
    private const int LabelProducerSoftBound = 4096;
    // Raised when a compaction pass frees nothing because every record is still
    // active, so registration does not rescan the whole list on every add while
    // a queue is suspended. Reset once compaction can make progress again.
    private static int _labelProducerCompactionBound = LabelProducerSoftBound;
    private static readonly HashSet<(object Memory, ulong Address)>
        _tracedProducerlessWaits = new();
    private static long _shaderTranslationMissTraceCount;
    private static long _translatedDrawTraceCount;
    private static long _standardDmaTraceCount;
    private static long _packetParseFailureTraceCount;
    private static int _textureFallbackTraceCount;
    private static readonly object _softwarePresenterGate = new();
    private static readonly Dictionary<(ulong Source, ulong Destination), ulong> _softwarePresenterFingerprints = new();
    private static readonly Dictionary<(ulong Shader, ulong Source, ulong Destination), ulong> _softwareComputeBlitFingerprints = new();
    private static readonly object _registerDefaultsGate = new();
    private static readonly ConditionalWeakTable<object, RegisterDefaultsAllocation> _registerDefaultsAllocations = new();
    private static readonly ConditionalWeakTable<object, SubmittedGpuState> _submittedGpuStates = new();

    // Unwraps decorator chains so all threads resolve to one shared root —
    // ctx.Memory identity otherwise differs per native worker thread.
    private static object CanonicalMemory(object memory)
    {
        while (memory is SharpEmu.HLE.ICpuMemoryWrapper wrapper)
        {
            memory = wrapper.Inner;
        }

        return memory;
    }

    private static readonly RegisterDefaultGroup[] PrimaryRegisterDefaults =
        CreatePrimaryRegisterDefaults();

    private static readonly RegisterDefaultGroup[] InternalRegisterDefaults =
    [
        new(0, 0, 0x8FB4EDB5, [new(0x00E, 0)]),
        new(0, 1, 0xB994AD29, [new(0x2AF, 0)]),
        new(0, 2, 0xD427322F, [new(0x314, 0)]),
        new(0, 3, 0xF58FEA31, [new(0x1B5, 0)]),
        new(1, 0, 0x6AC156EF, [new(0x216, 0)]),
        new(1, 1, 0x6AC15610, [new(0x217, 0)]),
        new(1, 2, 0x6AC15009, [new(0x219, 0)]),
        new(1, 3, 0x6AC153BA, [new(0x21A, 0)]),
        new(1, 4, 0xBE7DCD73, [new(0x27D, 0)]),
        new(1, 5, 0x0C4B1438, [new(0x22A, 0)]),
        new(1, 6, 0xDB00D71A, [new(0x204, 0)]),
        new(1, 7, 0xDB00D249, [new(0x205, 0)]),
        new(1, 8, 0xDB00EC60, [new(0x206, 0)]),
        new(1, 9, 0x0C4D6FE4, [new(0x080, 0)]),
        new(1, 10, 0x0C4A80EF, [new(0x100, 0)]),
        new(1, 11, 0x0DD283E7, [new(0x006, 0)]),
        new(1, 12, 0xC620E68C, [new(0x081, 0)]),
        new(1, 13, 0xC67EFACF, [new(0x101, 0)]),
        new(1, 14, 0xD9E6D9F7, [new(0x001, 0)]),
        new(2, 0, 0x31F34B9F, [new(0x24F, 0)]),
        new(2, 1, 0xAC0F9E76, [new(0x80003FFF, 0)]),
        new(2, 2, 0x929FD95D, [new(0x250, 0)]),
    ];

    private readonly record struct TextureDescriptor(
        ulong Address,
        uint Width,
        uint Height,
        uint Format,
        uint NumberType,
        uint TileMode,
        uint Type,
        uint BaseLevel,
        uint LastLevel,
        uint Pitch,
        uint DstSelect,
        uint Depth = 1,
        uint BaseArray = 0,
        uint ArrayPitch = 0,
        uint MaxMip = 0,
        uint MinLod = 0,
        uint MinLodWarn = 0,
        uint BcSwizzle = 0,
        ulong MetadataAddress = 0,
        uint DescriptorFlags = 0,
        bool HasExtendedDescriptor = false)
    {
        public uint ResourceMipLevels
        {
            get
            {
                // RDNA2 table 45 explicitly distinguishes MAX_MIP (the
                // resource allocation) from BASE_LEVEL/LAST_LEVEL (the
                // resource view). Do not size a Vulkan image from a view:
                // another descriptor for the same allocation may expose a
                // different subset of its mip chain.
                var maximumMipLevels = GetMaximumMipLevels();
                var resourceMipLevels = HasExtendedDescriptor
                    ? MaxMip + 1
                    : maximumMipLevels;
                return Math.Min(Math.Max(resourceMipLevels, 1u), maximumMipLevels);
            }
        }

        public uint MipLevels
        {
            get
            {
                var descriptorMipLevels = LastLevel >= ViewBaseLevel
                    ? LastLevel - ViewBaseLevel + 1
                    : 1;
                return Math.Min(
                    descriptorMipLevels,
                    ResourceMipLevels - ViewBaseLevel);
            }
        }

        public uint ViewBaseLevel
        {
            get
            {
                // Some single-mip Gen5 descriptors use the reserved/inverted
                // 15-0 range as a mip-disabled sentinel. The resource still
                // has exactly one addressable level (MAX_MIP=0). Treating 15
                // literally makes Vulkan reject an otherwise compatible GPU
                // image and falls back to stale guest-memory pixels. For any
                // malformed range, keep BASE_LEVEL's meaning and clamp it to
                // the allocation's last addressable mip. In particular, the
                // common 15-0/MAX_MIP=0 sentinel resolves to mip 0 without
                // making LAST_LEVEL the base of unrelated inverted views.
                return Math.Min(BaseLevel, ResourceMipLevels - 1);
            }
        }

        private uint GetMaximumMipLevels()
        {
            var largestDimension = Type == 10
                ? Math.Max(Math.Max(Width, Height), Depth)
                : Math.Max(Width, Height);
            uint maximumMipLevels = 1;
            while (largestDimension > 1)
            {
                largestDimension >>= 1;
                maximumMipLevels++;
            }

            return maximumMipLevels;
        }
    }

    private readonly record struct RenderTargetDescriptor(
        uint Slot,
        ulong Address,
        uint Width,
        uint Height,
        uint Format,
        uint NumberType,
        uint ComponentSwap,
        uint TileMode);

    private sealed record TranslatedGuestDraw(
        ulong ExportShaderAddress,
        ulong PixelShaderAddress,
        uint PrimitiveType,
        IGuestCompiledShader VertexShader,
        IGuestCompiledShader PixelShader,
        uint AttributeCount,
        uint VertexCount,
        uint InstanceCount,
        int BaseVertex,
        GuestIndexBuffer? IndexBuffer,
        IReadOnlyList<TranslatedImageBinding> Textures,
        IReadOnlyList<Gen5GlobalMemoryBinding> GlobalMemoryBindings,
        IReadOnlyList<Gen5VertexInputBinding> VertexInputs,
        IReadOnlyList<RenderTargetDescriptor> RenderTargets,
        GuestDepthTarget? DepthTarget,
        // Seam-shaped color targets are built once with the cached translation.
        IReadOnlyList<GuestRenderTarget> GuestTargets,
        GuestRenderState RenderState,
        IReadOnlyList<uint> PixelUserData,
        uint RawBlendControl,
        uint RawColorInfo,
        IReadOnlyList<uint> PixelInitialScalars,
        IReadOnlyList<uint> VertexInitialScalars,
        bool UsesGds = false,
        TranslatedNggDraw? Ngg = null,
        bool IsFullscreenColorClear = false,
        float ClearRed = 0f,
        float ClearGreen = 0f,
        float ClearBlue = 0f,
        float ClearAlpha = 1f,
        bool IsDccFastClear = false);

    private sealed record TranslatedNggDraw(
        IGuestCompiledShader ComputeShader,
        Gen5NggOutputLayout OutputLayout,
        GuestMemoryBuffer OutputBuffer);

    private sealed record TranslatedImageBinding(
        TextureDescriptor Descriptor,
        bool IsStorage,
        bool WritesImage,
        uint MipLevel,
        IReadOnlyList<uint> SamplerDescriptor,
        bool IsArrayed = false);

    private readonly record struct RenderTargetWriter(
        ulong Sequence,
        ulong ExportShaderAddress,
        ulong PixelShaderAddress,
        uint VertexCount,
        uint PrimitiveType);

    private readonly record struct ComputeImageWriter(
        ulong Sequence,
        ulong ShaderAddress,
        string Opcode);

    private readonly record struct ComputeDispatch(
        uint GroupCountX,
        uint GroupCountY,
        uint GroupCountZ,
        uint BaseGroupX,
        uint BaseGroupY,
        uint BaseGroupZ,
        uint WaveLaneCount,
        bool IsIndirect,
        uint ThreadCountX,
        uint ThreadCountY,
        uint ThreadCountZ);

    private readonly record struct SubmittedAcquireMem(
        uint Engine,
        uint CbDbControl,
        ulong BaseAddress,
        ulong SizeBytes,
        uint PollInterval,
        uint GcrControl)
    {
        // GFX10 GCR_CNTL invalidation controls. The host has no separate GLI,
        // GLM, GLK, GLV, GL1 and GL2 caches; they all converge on the guest
        // memory snapshots used to build Vulkan resources.
        private const uint GliInvalidateMask = 0x3u;
        private const int Gl1RangeShift = 2;
        private const uint Gl1RangeMask = 0x3u;
        private const uint GlmInvalidate = 1u << 5;
        private const uint GlkInvalidate = 1u << 7;
        private const uint GlvInvalidate = 1u << 8;
        private const uint Gl1Invalidate = 1u << 9;
        private const uint Gl2Discard = 1u << 13;
        private const uint Gl2Invalidate = 1u << 14;
        private const int Gl2RangeShift = 11;
        private const uint Gl2RangeMask = 0x3u;

        public bool InvalidatesGuestResources =>
            (GcrControl & (GliInvalidateMask |
                           GlmInvalidate |
                           GlkInvalidate |
                           GlvInvalidate |
                           Gl1Invalidate |
                           Gl2Discard |
                           Gl2Invalidate)) != 0;

        // sceAgc encodes its all-memory sentinel with a zero COHER_SIZE. GFX10
        // can also request ALL independently in GLI_INV, GL1_RANGE or
        // GL2_RANGE; in the host's unified resource cache, any invalidated
        // domain with ALL scope expands the operation to all tracked images.
        public bool CoversAllGuestMemory =>
            SizeBytes == 0 ||
            (GcrControl & GliInvalidateMask) == 1u ||
            ((GcrControl & (GlmInvalidate |
                            GlkInvalidate |
                            GlvInvalidate |
                            Gl1Invalidate)) != 0 &&
             ((GcrControl >> Gl1RangeShift) & Gl1RangeMask) == 0) ||
            ((GcrControl & (Gl2Discard | Gl2Invalidate)) != 0 &&
             ((GcrControl >> Gl2RangeShift) & Gl2RangeMask) == 0);
    }

    private sealed class SubmittedDcbState
    {
        public readonly record struct PendingSubmission(
            ulong CommandAddress,
            uint DwordCount,
            ulong SubmissionId,
            bool TracePackets);

        public Dictionary<uint, uint> CxRegisters { get; } = new();
        public Dictionary<uint, uint> ShRegisters { get; } = new();
        public Dictionary<uint, uint> UcRegisters { get; } = new();
        public TextureDescriptor? PresenterTexture { get; set; }
        public GuestDrawKind GuestDrawKind { get; set; }
        public TranslatedGuestDraw? TranslatedDraw { get; set; }
        public TranslatedGuestDraw? PendingTargetlessDraw { get; set; }
        public Dictionary<ulong, RenderTargetDescriptor> KnownRenderTargets { get; } = new();
        public Dictionary<ulong, RenderTargetWriter> RenderTargetWriters { get; } = new();
        public ulong IndirectArgsAddress { get; set; }
        public bool SawIndexedDraw { get; set; }
        public ulong IndexBufferAddress { get; set; }
        public uint IndexBufferCount { get; set; }
        public uint IndexSize { get; set; }
        public uint InstanceCount { get; set; } = 1;
        public uint DrawIndexOffset { get; set; }
        public bool PredicateSkip { get; set; }
        public string QueueName { get; set; } = "graphics";
        // Ident this queue's end-of-pipe completion interrupt is published under.
        // The graphics queue keeps 0; a compute queue takes the owner handle it
        // was submitted with, which is the same value the guest registers through
        // sceAgcDriverAddEqEvent.
        public ulong CompletionEventId { get; set; }
        public ulong ActiveSubmissionId { get; set; }
        public Queue<PendingSubmission> PendingSubmissions { get; } = new();
        public bool HasActiveSubmission { get; set; }
        public bool IsSuspended { get; set; }

        // Set when parsing stops on an INDIRECT_BUFFER packet so the caller can
        // continue into the buffer it links to.
        public ulong PendingChainAddress { get; set; }
        public uint PendingChainDwords { get; set; }

        // Base of the ring chunk currently being parsed; advances by RingChunkBytes.
        public ulong RingChunkBase { get; set; }
        public bool FollowedChunkAdvance { get; set; }
        public ulong CompletionEventNotifiedSubmissionId { get; set; }
        public Dictionary<(uint Op, uint Register), uint> FramePacketCounts { get; } = new();
        public uint FramePacketCount { get; set; }
        public uint FrameDrawCount { get; set; }
        public uint FrameDispatchCount { get; set; }
        public ulong FlipCount { get; set; }
        // Coalesce ACQUIRE_MEM invalidations within one DCB parse so North
        // Yankton load does not enqueue hundreds of empty OrderedGuestActions.
        public bool PendingAcquireInvalidation { get; set; }
        public ulong PendingAcquireBase { get; set; }
        public ulong PendingAcquireSize { get; set; }

        // Growing ring: never follows the chunk-advance sentinel (builders jump
        // to non-contiguous chunks), parks on the first not-yet-written word instead.
        public bool IsForceSubmittedRing { get; set; }

        // Address of a synthetic ring-tail park (SuspendOnUnwrittenRingWord);
        // abandoned if a new submission means the game moved to a fresh ring.
        public ulong RingTailParkAddress { get; set; }

        // One-past the last fully-processed packet, so the orphan sweep can
        // reach packets a suspended queue never got back to.
        public ulong LastParsedAddress { get; set; }
    }

    private sealed class SubmittedGpuState
    {
        public object Gate { get; } = new();
        public SubmittedDcbState Graphics { get; } = new();
        public Dictionary<uint, SubmittedDcbState> ComputeQueues { get; } = new();
        public Dictionary<ulong, ComputeImageWriter> ComputeImageWriters { get; } = new();
        public Dictionary<uint, string> ResourceOwners { get; } = new();
        public Dictionary<uint, RegisteredAgcResource> RegisteredResources { get; } = new();
        public bool ResourceRegistrationInitialized { get; set; }
        public ulong ResourceRegistrationMemory { get; set; }
        public ulong ResourceRegistrationMemorySize { get; set; }
        public uint ResourceRegistrationMaxOwners { get; set; }
        public uint DefaultOwner { get; set; } = DefaultAgcOwner;
        public uint NextOwner { get; set; } = 1;
        public uint NextResource { get; set; } = 1;
        public ulong WorkSequence { get; set; }
        public ulong SubmissionSequence { get; set; }
        public int WaitMonitorRunning;
        public object WaitMonitorSignalGate { get; } = new();
        public long WaitMonitorSignalVersion { get; set; }
        public bool TfRingConfigured { get; set; }
        public ulong TfRingAddress { get; set; }
        public uint TfRingSize { get; set; }
        public bool HsOffchipParamConfigured { get; set; }
        // Firmware payload layout: low16(second) at +0, low16(first) at +2.
        public uint HsOffchipParamPayload { get; set; }

        // Coalesced drain scheduling; fields (not properties) so Interlocked can target them.
        public int DrainWorkerActive;
        public int DrainPending;
        public CpuContext? PendingDrainContext;
    }

    private readonly record struct RegisteredAgcResource(
        uint Owner,
        ulong Address,
        ulong Size,
        string Name,
        uint Type,
        uint Flags);

    private sealed class LabelProducerTrace
    {
        public long Sequence;
        public required object Memory;
        public ulong Address;
        public ulong Length;
        public ulong PacketAddress;
        public ulong SubmissionId;
        public required string QueueName;
        public required string DebugName;
        public bool Completed;
    }

    private readonly record struct RegisterDefaultValue(uint Offset, uint Value);

    private readonly record struct RegisterDefaultGroup(
        uint Space,
        uint Index,
        uint Type,
        RegisterDefaultValue[] Registers);

    private sealed record RegisterDefaultsAllocation(ulong Primary, ulong Internal);

    // NID captured from shipped titles; 'sceAgcInit' is a working label that collides with a real catalog symbol of a different NID. Rename pending AGC API confirmation.
    #pragma warning disable SHEM004
    [SysAbiExport(
        Nid = "23LRUSvYu1M",
        ExportName = "sceAgcInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int Init(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rdi];
        var version = (uint)ctx[CpuRegister.Rsi];
        if (stateAddress == 0 || !IsSupportedRegisterDefaultsVersion(version))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        TraceAgc($"agc.init state=0x{stateAddress:X16} version={version}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }
    #pragma warning restore SHEM004

    [SysAbiExport(
        Nid = "2JtWUUiYBXs",
        ExportName = "sceAgcGetRegisterDefaults2",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetRegisterDefaults2(CpuContext ctx) =>
        ReturnRegisterDefaults(ctx, internalDefaults: false);

    [SysAbiExport(
        Nid = "wRbq6ZjNop4",
        ExportName = "sceAgcGetRegisterDefaults2Internal",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetRegisterDefaults2Internal(CpuContext ctx) =>
        ReturnRegisterDefaults(ctx, internalDefaults: true);

    [SysAbiExport(
        Nid = "f3dg2CSgRKY",
        ExportName = "sceAgcCreateShader",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CreateShader(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        var headerAddress = ctx[CpuRegister.Rsi];
        var codeAddress = ctx[CpuRegister.Rdx];
        if (headerAddress == 0 || codeAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt32(ctx, headerAddress, out var fileHeader) ||
            !TryReadUInt32(ctx, headerAddress + sizeof(uint), out var version))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (fileHeader != ShaderFileHeader || version != ShaderVersion)
        {
            TraceCreateShader(destinationAddress, headerAddress, codeAddress, $"invalid-header file=0x{fileHeader:X8} version=0x{version:X8}");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!RelocatePointerField(ctx, headerAddress + ShaderCxRegistersOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderShRegistersOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderUserDataOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderSpecialsOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderInputSemanticsOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderOutputSemanticsOffset) ||
            !TryWriteUInt64(ctx, headerAddress + ShaderCodeOffset, codeAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryReadUInt64(ctx, headerAddress + ShaderUserDataOffset, out var userDataAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (userDataAddress != 0 &&
            (!RelocatePointerField(ctx, userDataAddress) ||
             !RelocatePointerField(ctx, userDataAddress + 0x08) ||
             !RelocatePointerField(ctx, userDataAddress + 0x10) ||
             !RelocatePointerField(ctx, userDataAddress + 0x18) ||
             !RelocatePointerField(ctx, userDataAddress + 0x20)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!PatchShaderProgramRegisters(ctx, headerAddress, codeAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (destinationAddress != 0 &&
            !TryWriteUInt64(ctx, destinationAddress, headerAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (_submitTraceGate)
        {
            _shaderHeadersByCode[codeAddress] = headerAddress;
            _createdShaderHeadersByCode[codeAddress] = headerAddress;
        }

        TryRegisterCreatedCombinedShader(ctx, codeAddress, headerAddress);

        TraceCreateShader(destinationAddress, headerAddress, codeAddress, "ok");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static void TryRegisterCreatedCombinedShader(
        CpuContext ctx,
        ulong codeAddress,
        ulong headerAddress)
    {
        if (!TryReadByte(ctx, headerAddress + ShaderTypeOffset, out var shaderType))
        {
            return;
        }

        var currentIsEntry = shaderType is 4 or 5;
        var requiredOtherType = shaderType switch
        {
            4 => (byte)6,
            5 => (byte)7,
            6 => (byte)4,
            7 => (byte)5,
            _ => byte.MaxValue,
        };
        if (requiredOtherType == byte.MaxValue)
        {
            return;
        }

        KeyValuePair<ulong, ulong>[] createdShaders;
        lock (_submitTraceGate)
        {
            createdShaders = _createdShaderHeadersByCode.ToArray();
        }

        var candidates = createdShaders
            .Where(candidate => currentIsEntry
                ? candidate.Key > codeAddress
                : candidate.Key < codeAddress)
            .OrderBy(candidate => currentIsEntry
                ? candidate.Key - codeAddress
                : codeAddress - candidate.Key);
        Span<byte> otherDescriptor = stackalloc byte[ShaderDescriptorSize];
        foreach (var candidate in candidates)
        {
            if (!TryReadByte(
                    ctx,
                    candidate.Value + ShaderTypeOffset,
                    out var candidateType) ||
                candidateType != requiredOtherType)
            {
                continue;
            }

            var entryCodeAddress = currentIsEntry ? codeAddress : candidate.Key;
            var entryHeaderAddress = currentIsEntry ? headerAddress : candidate.Value;
            var continuationCodeAddress = currentIsEntry ? candidate.Key : codeAddress;
            var continuationHeaderAddress = currentIsEntry ? candidate.Value : headerAddress;
            if (continuationCodeAddress <= entryCodeAddress ||
                continuationCodeAddress - entryCodeAddress > uint.MaxValue ||
                !ctx.Memory.TryRead(continuationHeaderAddress, otherDescriptor) ||
                !TryShaderPairCompatibility(
                    ctx,
                    entryHeaderAddress,
                    otherDescriptor,
                    shaderType is 5 || candidateType is 5,
                    out var compatible) ||
                !compatible)
            {
                continue;
            }

            Gen5ShaderTranslator.RegisterCombinedShader(
                ctx,
                entryCodeAddress,
                entryHeaderAddress,
                continuationCodeAddress,
                continuationHeaderAddress);
            lock (_submitTraceGate)
            {
                // The firmware-created combined object is descriptor-compatible
                // with the continuation half. Until its separate output pointer
                // is observed, this original header provides the same declared
                // code size and resource metadata for translated draws.
                _shaderHeadersByCode[entryCodeAddress] = continuationHeaderAddress;
            }

            TraceCreateShader(
                0,
                entryHeaderAddress,
                entryCodeAddress,
                $"paired continuation=0x{continuationCodeAddress:X} " +
                $"entry_type={(currentIsEntry ? shaderType : candidateType)} " +
                $"continuation_type={(currentIsEntry ? candidateType : shaderType)}");
            return;
        }
    }

    private static bool TryRegisterEmbeddedCombinedShader(
        CpuContext ctx,
        ulong entryCodeAddress,
        ulong entryHeaderAddress,
        out ulong continuationHeaderAddress)
    {
        continuationHeaderAddress = 0;
        var trace = _traceCombinedShaderAddress == entryCodeAddress;
        if (!TryReadByte(
                ctx,
                entryHeaderAddress + ShaderTypeOffset,
                out var entryType))
        {
            if (trace)
            {
                Console.Error.WriteLine(
                    $"[AGC][COMBINED-SCAN] entry=0x{entryCodeAddress:X16} " +
                    $"header=0x{entryHeaderAddress:X16} " +
                    $"result=unreadable-entry");
            }
            return false;
        }

        var attempts = _embeddedCombinedShaderScanAttempts.GetValue(
            ctx.Memory,
            static _ => new ConcurrentDictionary<(ulong Code, ulong Header), byte>());
        var attemptKey = (entryCodeAddress, entryHeaderAddress);
        if (attempts.ContainsKey(attemptKey))
        {
            if (trace)
            {
                Console.Error.WriteLine(
                    $"[AGC][COMBINED-SCAN] entry=0x{entryCodeAddress:X16} " +
                    $"header=0x{entryHeaderAddress:X16} result=already-attempted");
            }
            return false;
        }

        if (entryType is 2 or 3)
        {
            ulong originalEntryHeader;
            lock (_submitTraceGate)
            {
                _createdShaderHeadersByCode.TryGetValue(
                    entryCodeAddress,
                    out originalEntryHeader);
            }

            var expectedOriginalType = entryType == 2 ? (byte)4 : (byte)5;
            if (originalEntryHeader == 0 ||
                !TryReadByte(
                    ctx,
                    originalEntryHeader + ShaderTypeOffset,
                    out var originalEntryType) ||
                originalEntryType != expectedOriginalType ||
                !TryReadUInt64(
                    ctx,
                    entryHeaderAddress + ShaderCodeOffset,
                    out var combinedContinuationCode) ||
                combinedContinuationCode <= entryCodeAddress ||
                combinedContinuationCode - entryCodeAddress > uint.MaxValue)
            {
                if (trace)
                {
                    Console.Error.WriteLine(
                        $"[AGC][COMBINED-SCAN] entry=0x{entryCodeAddress:X16} " +
                        $"header=0x{entryHeaderAddress:X16} type={entryType} " +
                        $"original=0x{originalEntryHeader:X16} " +
                        $"result=invalid-combined-descriptor");
                }
                return false;
            }

            Gen5ShaderTranslator.RegisterCombinedShader(
                ctx,
                entryCodeAddress,
                originalEntryHeader,
                combinedContinuationCode,
                entryHeaderAddress);
            attempts.TryAdd(attemptKey, 0);
            continuationHeaderAddress = entryHeaderAddress;
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.combined_shader_discovered " +
                $"entry=0x{entryCodeAddress:X16} type={originalEntryType} " +
                $"continuation=0x{combinedContinuationCode:X16} " +
                $"header=0x{entryHeaderAddress:X16} type={entryType} " +
                $"source=combined-descriptor");
            return true;
        }

        if (entryType is not (4 or 5))
        {
            if (trace)
            {
                Console.Error.WriteLine(
                    $"[AGC][COMBINED-SCAN] entry=0x{entryCodeAddress:X16} " +
                    $"header=0x{entryHeaderAddress:X16} " +
                    $"result=invalid-entry type={entryType}");
            }
            return false;
        }

        // AGC upload blobs place the paired continuation and its descriptor in
        // the same mapped allocation as the entry program. The continuation's
        // descriptor is not necessarily passed through sceAgcCreateShader on
        // LLE-backed paths, so recover it from the ordinary 1234/v24 header.
        const int maximumScanBytes = 64 * 1024;
        const int readChunkBytes = 4 * 1024;
        var uploadBytes = new byte[maximumScanBytes];
        var bytesRead = 0;
        while (bytesRead < uploadBytes.Length)
        {
            var chunkLength = Math.Min(readChunkBytes, uploadBytes.Length - bytesRead);
            if (!ctx.Memory.TryRead(
                    entryCodeAddress + (ulong)bytesRead,
                    uploadBytes.AsSpan(bytesRead, chunkLength)))
            {
                break;
            }

            bytesRead += chunkLength;
        }

        if (bytesRead < ShaderDescriptorSize)
        {
            if (trace)
            {
                Console.Error.WriteLine(
                    $"[AGC][COMBINED-SCAN] entry=0x{entryCodeAddress:X16} " +
                    $"header=0x{entryHeaderAddress:X16} type={entryType} " +
                    $"result=short-read bytes={bytesRead}");
            }
            return false;
        }

        if (bytesRead != uploadBytes.Length)
        {
            Array.Resize(ref uploadBytes, bytesRead);
        }

        attempts.TryAdd(attemptKey, 0);
        var requiredContinuationType = entryType == 4 ? (byte)6 : (byte)7;
        ulong bestCodeAddress = 0;
        ulong bestHeaderAddress = 0;
        var bestDistance = ulong.MaxValue;
        var matchingHeaders = 0;
        var compatibleHeaders = 0;
        for (var offset = 0;
             offset <= uploadBytes.Length - ShaderDescriptorSize;
             offset += sizeof(uint))
        {
            var descriptor = uploadBytes.AsSpan(offset, ShaderDescriptorSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(descriptor) != ShaderFileHeader ||
                BinaryPrimitives.ReadUInt32LittleEndian(descriptor[sizeof(uint)..]) != ShaderVersion ||
                descriptor[(int)ShaderTypeOffset] != requiredContinuationType)
            {
                continue;
            }

            matchingHeaders++;

            var candidateCodeAddress = BinaryPrimitives.ReadUInt64LittleEndian(
                descriptor[(int)ShaderCodeOffset..]);
            var candidateSize = BinaryPrimitives.ReadUInt32LittleEndian(
                descriptor[(int)ShaderSizeOffset..]);
            if (candidateCodeAddress <= entryCodeAddress ||
                candidateCodeAddress - entryCodeAddress > uint.MaxValue ||
                candidateSize == 0 ||
                (candidateSize & (sizeof(uint) - 1)) != 0 ||
                !TryShaderPairCompatibility(
                    ctx,
                    entryHeaderAddress,
                    descriptor,
                    entryType == 5,
                    out var compatible) ||
                !compatible)
            {
                continue;
            }

            compatibleHeaders++;

            var distance = candidateCodeAddress - entryCodeAddress;
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            bestCodeAddress = candidateCodeAddress;
            bestHeaderAddress = entryCodeAddress + (ulong)offset;
        }

        if (bestHeaderAddress == 0)
        {
            if (trace)
            {
                Console.Error.WriteLine(
                    $"[AGC][COMBINED-SCAN] entry=0x{entryCodeAddress:X16} " +
                    $"header=0x{entryHeaderAddress:X16} type={entryType} " +
                    $"bytes={bytesRead} matching={matchingHeaders} " +
                    $"compatible={compatibleHeaders} result=not-found");
            }
            return false;
        }

        Gen5ShaderTranslator.RegisterCombinedShader(
            ctx,
            entryCodeAddress,
            entryHeaderAddress,
            bestCodeAddress,
            bestHeaderAddress);
        lock (_submitTraceGate)
        {
            _shaderHeadersByCode[entryCodeAddress] = bestHeaderAddress;
        }

        continuationHeaderAddress = bestHeaderAddress;
        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.combined_shader_discovered " +
            $"entry=0x{entryCodeAddress:X16} type={entryType} " +
            $"continuation=0x{bestCodeAddress:X16} " +
            $"header=0x{bestHeaderAddress:X16} type={requiredContinuationType}");
        return true;
    }

    [SysAbiExport(
        Nid = "BfBDZGbti7A",
        ExportName = "sceAgcGetIsTrinityMode",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetIsTrinityMode(CpuContext ctx)
    {
        // This is a void firmware ABI. SharpEmu currently models base Prospero,
        // so write one false byte. Mark the incoming RAX value as explicitly
        // preserved so ModuleManager/native dispatch does not replace it with
        // this handler's internal return value.
        var preservedRax = ctx[CpuRegister.Rax];
        Span<byte> mode = stackalloc byte[1];
        mode[0] = 0;
        _ = ctx.Memory.TryWrite(ctx[CpuRegister.Rdi], mode);
        ctx[CpuRegister.Rax] = preservedRax;
        return 0;
    }

    // Human-readable names for these exports have not been recovered. Keep
    // explicit placeholders and dispatch solely by the verified NIDs.
#pragma warning disable SHEM004, SHEM006
    [SysAbiExport(
        Nid = "dolOmWH+huQ",
        ExportName = "unknown_dolOmWH_huQ",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int UnknownGetCombinedShaderRegisterStorageSize(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        var firstShaderAddress = ctx[CpuRegister.Rsi];
        var secondShaderAddress = ctx[CpuRegister.Rdx];

        if (!TryReadByte(ctx, firstShaderAddress + ShaderTypeOffset, out var firstType))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var requiredSecondType = firstType switch
        {
            5 => (byte)7,
            4 => (byte)6,
            _ => byte.MaxValue,
        };
        if (requiredSecondType == byte.MaxValue ||
            !TryReadByte(ctx, secondShaderAddress + ShaderTypeOffset, out var secondType))
        {
            return requiredSecondType == byte.MaxValue
                ? ctx.SetReturn(AgcErrorIncompatibleShaderPair)
                : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (secondType != requiredSecondType)
        {
            return ctx.SetReturn(AgcErrorIncompatibleShaderPair);
        }

        if (!TryReadByte(
                ctx,
                secondShaderAddress + ShaderNumShRegistersOffset,
                out var registerCount) ||
            !ctx.TryWriteUInt64(outputAddress, (ulong)registerCount * 8) ||
            !ctx.TryWriteUInt64(outputAddress + sizeof(ulong), 4))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(0);
    }

    // Compatibility names retained for callers and tests added on upstream
    // after these two NIDs were renamed to their verified placeholder names.
    public static int GetFusedShaderSize(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        var frontAddress = ctx[CpuRegister.Rsi];
        var backAddress = ctx[CpuRegister.Rdx];
        if (destinationAddress == 0 || frontAddress == 0 || backAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadByte(ctx, frontAddress + ShaderTypeOffset, out var frontType) ||
            !TryReadByte(ctx, backAddress + ShaderTypeOffset, out var backType) ||
            !TryReadByte(ctx, backAddress + ShaderNumShRegistersOffset, out var registerCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!IsFusedShaderHalfPair(frontType, backType))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryWriteUInt64(destinationAddress, registerCount * 8UL) ||
            !ctx.TryWriteUInt64(destinationAddress + 8, FusedShaderImageAlignment))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc(
            $"agc.get_fused_shader_size front=0x{frontAddress:X16} back=0x{backAddress:X16} " +
            $"types={frontType}/{backType} registers={registerCount}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "fd5Bp5tGTgo",
        ExportName = "unknown_fd5Bp5tGTgo",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int UnknownCreateCombinedShader(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        var firstShaderAddress = ctx[CpuRegister.Rsi];
        var secondShaderAddress = ctx[CpuRegister.Rdx];
        var registerBufferAddress = ctx[CpuRegister.Rcx];

        if (!TryReadByte(ctx, firstShaderAddress + ShaderTypeOffset, out var firstType))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var requiredSecondType = firstType switch
        {
            5 => (byte)7,
            4 => (byte)6,
            _ => byte.MaxValue,
        };
        var combinedType = firstType == 5 ? (byte)3 : (byte)2;
        if (requiredSecondType == byte.MaxValue ||
            !TryReadByte(ctx, secondShaderAddress + ShaderTypeOffset, out var secondType))
        {
            return requiredSecondType == byte.MaxValue
                ? ctx.SetReturn(AgcErrorIncompatibleShaderPair)
                : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (secondType != requiredSecondType)
        {
            return ctx.SetReturn(AgcErrorIncompatibleShaderPair);
        }

        if (!TryReadUInt64(
                ctx,
                firstShaderAddress + ShaderCodeOffset,
                out var firstCodeAddress) ||
            !TryReadUInt64(
                ctx,
                secondShaderAddress + ShaderCodeOffset,
                out var secondCodeAddress))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Span<byte> descriptor = stackalloc byte[ShaderDescriptorSize];
        if (!ctx.Memory.TryRead(secondShaderAddress, descriptor) ||
            !ctx.Memory.TryWrite(outputAddress, descriptor))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Span<byte> type = stackalloc byte[1];
        type[0] = combinedType;
        if (!ctx.Memory.TryWrite(outputAddress + ShaderTypeOffset, type) ||
            !TryShaderPairCompatibility(
                ctx,
                firstShaderAddress,
                descriptor,
                firstType == 5,
                out var compatible))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // Firmware has already copied the descriptor and changed its type when
        // this check fails. Preserve that deliberately non-atomic behavior.
        if (!compatible)
        {
            return ctx.SetReturn(AgcErrorIncompatibleShaderPair);
        }

        if (registerBufferAddress != 0 &&
            !CopyCombinedShaderRegisters(
                ctx,
                outputAddress,
                descriptor,
                registerBufferAddress))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryReconcileCombinedShaderRegisters(
                ctx,
                outputAddress,
                firstShaderAddress,
                firstType == 5) ||
            !ctx.TryWriteUInt64(outputAddress + sizeof(ulong), 0))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (firstCodeAddress != 0 && secondCodeAddress != 0)
        {
            Gen5ShaderTranslator.RegisterCombinedShader(
                ctx,
                firstCodeAddress,
                firstShaderAddress,
                secondCodeAddress,
                secondShaderAddress);
            lock (_submitTraceGate)
            {
                // Draw packets execute the first code object after the combined
                // register list is reconciled. Associate that entry with the
                // combined descriptor so metadata/user-data comes from the
                // second (continuation) half, matching the firmware object.
                _shaderHeadersByCode[firstCodeAddress] = outputAddress;
            }
        }

        return ctx.SetReturn(0);
    }

    public static int FuseShaderHalves(CpuContext ctx)
    {
        var fusedAddress = ctx[CpuRegister.Rdi];
        var frontAddress = ctx[CpuRegister.Rsi];
        var backAddress = ctx[CpuRegister.Rdx];
        var scratchAddress = ctx[CpuRegister.Rcx];
        if (fusedAddress == 0 || frontAddress == 0 || backAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadByte(ctx, frontAddress + ShaderTypeOffset, out var frontType) ||
            !TryReadByte(ctx, backAddress + ShaderTypeOffset, out var backType))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!IsFusedShaderHalfPair(frontType, backType))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt64(ctx, frontAddress + ShaderSpecialsOffset, out var frontSpecialsAddress) ||
            !TryReadUInt64(ctx, backAddress + ShaderSpecialsOffset, out var backSpecialsAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var isGeometryPair = frontType == GsFrontShaderType;
        if (frontSpecialsAddress != 0 && backSpecialsAddress != 0)
        {
            if (!TryReadUInt32(ctx, frontSpecialsAddress + ShaderSpecialVgtShaderStagesEnOffset + sizeof(uint), out var frontStages) ||
                !TryReadUInt32(ctx, backSpecialsAddress + ShaderSpecialVgtShaderStagesEnOffset + sizeof(uint), out var backStages))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            var waveSizeBit = isGeometryPair ? VgtShaderStagesGsW32EnBit : VgtShaderStagesHsW32EnBit;
            if (((frontStages ^ backStages) & waveSizeBit) != 0)
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }
        }

        if (!TryReadUInt64(ctx, backAddress + ShaderShRegistersOffset, out var backRegistersAddress) ||
            !TryReadByte(ctx, backAddress + ShaderNumShRegistersOffset, out var registerCount) ||
            !TryReadUInt64(ctx, frontAddress + ShaderCodeOffset, out var frontCodeAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Span<byte> header = stackalloc byte[ShaderDescriptorSize];
        if (!ctx.Memory.TryRead(backAddress, header) ||
            !ctx.Memory.TryWrite(fusedAddress, header))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var fusedRegistersAddress = backRegistersAddress;
        if (scratchAddress != 0 && backRegistersAddress != 0 && registerCount != 0)
        {
            Span<byte> registers = stackalloc byte[registerCount * 8];
            if (!ctx.Memory.TryRead(backRegistersAddress, registers) ||
                !ctx.Memory.TryWrite(scratchAddress, registers))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            fusedRegistersAddress = scratchAddress;
        }

        if (!TryWriteByte(ctx, fusedAddress + ShaderTypeOffset, isGeometryPair ? GsShaderType : HsShaderType) ||
            !ctx.TryWriteUInt64(fusedAddress + ShaderUserDataOffset, 0) ||
            !ctx.TryWriteUInt64(fusedAddress + ShaderShRegistersOffset, fusedRegistersAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (isGeometryPair)
        {
            if (!TryReadUInt64(ctx, frontAddress + ShaderShRegistersOffset, out var frontRegistersAddress) ||
                !TryReadByte(ctx, frontAddress + ShaderNumShRegistersOffset, out var frontRegisterCount))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            for (var occurrence = 0; occurrence < 2; occurrence++)
            {
                if (!TryFindShaderRegister(ctx, fusedRegistersAddress, registerCount, SpiShaderPgmChksumGs, occurrence, out var fusedEntry) ||
                    !TryFindShaderRegister(ctx, frontRegistersAddress, frontRegisterCount, SpiShaderPgmChksumGs, occurrence, out var frontEntry))
                {
                    continue;
                }

                if (!TryReadUInt32(ctx, frontEntry + sizeof(uint), out var checksum) ||
                    !TryWriteUInt32(ctx, fusedEntry + sizeof(uint), checksum))
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }
        }

        if (!PatchFusedProgramAddress(
                ctx,
                fusedRegistersAddress,
                registerCount,
                isGeometryPair ? SpiShaderPgmLoEs : SpiShaderPgmLoLs,
                frontCodeAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc(
            $"agc.fuse_shader_halves fused=0x{fusedAddress:X16} front=0x{frontAddress:X16} " +
            $"back=0x{backAddress:X16} scratch=0x{scratchAddress:X16} types={frontType}/{backType} " +
            $"registers={registerCount} code=0x{frontCodeAddress:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
#pragma warning restore SHEM004, SHEM006

    private static bool TryShaderPairCompatibility(
        CpuContext ctx,
        ulong firstShaderAddress,
        ReadOnlySpan<byte> secondDescriptor,
        bool hullLocalPair,
        out bool compatible)
    {
        compatible = false;
        if (!TryReadUInt64(
                ctx,
                firstShaderAddress + ShaderSpecialsOffset,
                out var firstSpecialsAddress))
        {
            return false;
        }

        var secondSpecialsAddress = BinaryPrimitives.ReadUInt64LittleEndian(
            secondDescriptor[(int)ShaderSpecialsOffset..]);
        if (!TryReadUInt64(ctx, firstSpecialsAddress + 8, out var firstFlags) ||
            !TryReadUInt64(ctx, secondSpecialsAddress + 8, out var secondFlags))
        {
            return false;
        }

        var compatibilityBit = hullLocalPair ? 53 : 54;
        compatible = (((firstFlags ^ secondFlags) >> compatibilityBit) & 1) == 0;
        return true;
    }

    private static bool CopyCombinedShaderRegisters(
        CpuContext ctx,
        ulong outputAddress,
        ReadOnlySpan<byte> secondDescriptor,
        ulong registerBufferAddress)
    {
        var sourceAddress = BinaryPrimitives.ReadUInt64LittleEndian(
            secondDescriptor[(int)ShaderShRegistersOffset..]);
        var byteCount = secondDescriptor[(int)ShaderNumShRegistersOffset] * 8;
        if (byteCount != 0)
        {
            var registers = new byte[byteCount];
            if (!ctx.Memory.TryRead(sourceAddress, registers) ||
                !ctx.Memory.TryWrite(registerBufferAddress, registers))
            {
                return false;
            }
        }

        return ctx.TryWriteUInt64(
            outputAddress + ShaderShRegistersOffset,
            registerBufferAddress);
    }

    private static bool TryReconcileCombinedShaderRegisters(
        CpuContext ctx,
        ulong outputAddress,
        ulong firstShaderAddress,
        bool hullLocalPair)
    {
        if (!TryReadUInt64(
                ctx,
                firstShaderAddress + ShaderShRegistersOffset,
                out var firstRegistersAddress) ||
            !TryReadByte(
                ctx,
                firstShaderAddress + ShaderNumShRegistersOffset,
                out var firstRegisterCount) ||
            !TryReadUInt64(
                ctx,
                outputAddress + ShaderShRegistersOffset,
                out var outputRegistersAddress) ||
            !TryReadByte(
                ctx,
                outputAddress + ShaderNumShRegistersOffset,
                out var outputRegisterCount))
        {
            return false;
        }

        var internalRegister = hullLocalPair
            ? InternalHsRegister
            : InternalGsRegister;
        var resource1Register = hullLocalPair
            ? SpiShaderPgmRsrc1Hs
            : SpiShaderPgmRsrc1Gs;
        var resource2Register = hullLocalPair
            ? SpiShaderPgmRsrc2Hs
            : SpiShaderPgmRsrc2Gs;
        var programLoRegister = hullLocalPair
            ? SpiShaderPgmLoLs
            : SpiShaderPgmLoEs;

        if (!TryFindShaderRegister(
                ctx,
                firstRegistersAddress,
                firstRegisterCount,
                internalRegister,
                0,
                out var firstInternal0) ||
            !TryFindShaderRegister(
                ctx,
                firstRegistersAddress,
                firstRegisterCount,
                internalRegister,
                1,
                out var firstInternal1) ||
            !TryFindShaderRegister(
                ctx,
                outputRegistersAddress,
                outputRegisterCount,
                internalRegister,
                0,
                out var outputInternal0) ||
            !TryFindShaderRegister(
                ctx,
                outputRegistersAddress,
                outputRegisterCount,
                internalRegister,
                1,
                out var outputInternal1) ||
            !TryReadUInt32(ctx, firstInternal0 + sizeof(uint), out var firstInternalValue0) ||
            !TryReadUInt32(ctx, firstInternal1 + sizeof(uint), out var firstInternalValue1) ||
            !TryWriteUInt32(ctx, outputInternal0 + sizeof(uint), firstInternalValue0) ||
            !TryWriteUInt32(ctx, outputInternal1 + sizeof(uint), firstInternalValue1) ||
            !TryFindShaderRegister(
                ctx,
                firstRegistersAddress,
                firstRegisterCount,
                resource1Register,
                0,
                out var firstResource1) ||
            !TryFindShaderRegister(
                ctx,
                firstRegistersAddress,
                firstRegisterCount,
                resource2Register,
                0,
                out var firstResource2) ||
            !TryFindShaderRegister(
                ctx,
                outputRegistersAddress,
                outputRegisterCount,
                resource1Register,
                0,
                out var outputResource1) ||
            !TryFindShaderRegister(
                ctx,
                outputRegistersAddress,
                outputRegisterCount,
                resource2Register,
                0,
                out var outputResource2) ||
            !TryReadUInt32(ctx, firstResource1 + sizeof(uint), out var firstResourceValue1) ||
            !TryReadUInt32(ctx, firstResource2 + sizeof(uint), out var firstResourceValue2) ||
            !TryReadUInt32(ctx, outputResource1 + sizeof(uint), out var outputResourceValue1) ||
            !TryReadUInt32(ctx, outputResource2 + sizeof(uint), out var outputResourceValue2))
        {
            return false;
        }

        ReconcileShaderResourceRegisters(
            hullLocalPair,
            firstResourceValue1,
            firstResourceValue2,
            ref outputResourceValue1,
            ref outputResourceValue2);
        if (!TryWriteUInt32(
                ctx,
                outputResource1 + sizeof(uint),
                outputResourceValue1) ||
            !TryWriteUInt32(
                ctx,
                outputResource2 + sizeof(uint),
                outputResourceValue2) ||
            !TryFindShaderRegister(
                ctx,
                outputRegistersAddress,
                outputRegisterCount,
                programLoRegister,
                0,
                out var programLoAddress) ||
            !TryReadUInt64(
                ctx,
                firstShaderAddress + ShaderCodeOffset,
                out var codeAddress))
        {
            return false;
        }

        Span<byte> programHi = stackalloc byte[1];
        programHi[0] = (byte)(codeAddress >> 40);
        return ctx.Memory.TryWrite(programLoAddress + 12, programHi) &&
               TryWriteUInt32(
                   ctx,
                   programLoAddress + sizeof(uint),
                   (uint)(codeAddress >> 8));
    }

    private static bool TryFindShaderRegister(
        CpuContext ctx,
        ulong registersAddress,
        byte registerCount,
        uint register,
        int occurrence,
        out ulong registerAddress)
    {
        for (var index = 0; index < registerCount; index++)
        {
            var candidateAddress = registersAddress + ((ulong)index * 8);
            if (!TryReadUInt32(ctx, candidateAddress, out var candidate))
            {
                registerAddress = 0;
                return false;
            }

            if (candidate != register)
            {
                continue;
            }

            if (occurrence-- == 0)
            {
                registerAddress = candidateAddress;
                return true;
            }
        }

        registerAddress = 0;
        return false;
    }

    private static void ReconcileShaderResourceRegisters(
        bool hullLocalPair,
        uint firstResource1,
        uint firstResource2,
        ref uint outputResource1,
        ref uint outputResource2)
    {
        var firstWidth = (firstResource1 & 0x3F) * 8 + 8;
        var outputWidth = (outputResource1 & 0x3F) * 8 + 8;
        var mergedWidth = Math.Max(firstWidth, outputWidth);
        var firstRequirement =
            (firstWidth >> 1) + ((firstResource2 >> 28) * 8);
        var outputRequirement =
            (outputWidth >> 1) + ((outputResource2 >> 28) * 8);
        var mergedRequirement = Math.Max(firstRequirement, outputRequirement);
        uint mergedRingSize = 0;
        if ((mergedWidth >> 1) < mergedRequirement)
        {
            var minimumRequirement = Math.Min(
                firstRequirement,
                outputRequirement);
            mergedRingSize =
                ((mergedRequirement - minimumRequirement) * 0x0040_0000u +
                 0x01C0_0000u) &
                0xF000_0000u;
        }

        outputResource1 =
            (((mergedWidth >> 3) - 1) & 0x3F) |
            (outputResource1 & 0xFFFF_FFC0u);
        outputResource2 =
            (outputResource2 & 0x0FFF_FFFFu) | mergedRingSize;

        if (hullLocalPair)
        {
            var mergedMode = Math.Max(
                (firstResource1 >> 28) & 3,
                (outputResource1 >> 28) & 3);
            outputResource1 =
                (mergedMode << 28) | (outputResource1 & 0xCFFF_FFFFu);
            outputResource2 =
                (firstResource2 & 0x0800_003Eu) |
                (outputResource2 & 0xF7FF_FFC1u);
            return;
        }

        var mergedGsMode = Math.Max(
            (firstResource1 >> 29) & 3,
            (outputResource1 >> 29) & 3);
        outputResource1 =
            (mergedGsMode << 29) | (outputResource1 & 0x9FFF_FFFFu);
        var mergedComponentCount = Math.Max(
            (firstResource2 >> 16) & 3,
            (outputResource2 >> 16) & 3) << 16;
        outputResource2 =
            (firstResource2 & 0x0800_003Eu) |
            mergedComponentCount |
            (outputResource2 & 0xF7F8_FFC1u) |
            (firstResource2 & 0x0004_0000u);
    }

    [SysAbiExport(
        Nid = "vcmNN+AAXnY",
        ExportName = "sceAgcSetCxRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetCxRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "cx");

    [SysAbiExport(
        Nid = "Qrj4c+61z4A",
        ExportName = "sceAgcSetShRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetShRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "sh");

    [SysAbiExport(
        Nid = "6lNcCp+fxi4",
        ExportName = "sceAgcSetUcRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetUcRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "uc");

    [SysAbiExport(
        Nid = "d-6uF9sZDIU",
        ExportName = "sceAgcSetCxRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetCxRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "cx");

    [SysAbiExport(
        Nid = "z2duB-hHQSM",
        ExportName = "sceAgcSetShRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetShRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "sh");

    [SysAbiExport(
        Nid = "vRoArM9zaIk",
        ExportName = "sceAgcSetUcRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetUcRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "uc");

    [SysAbiExport(
        Nid = "Ikfdt-rIqCE",
        ExportName = "Ikfdt-rIqCE#G#A",
        IsSyntheticName = true,
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int ConfigureUnknownPatchDescriptor(CpuContext ctx)
    {
        var descriptorAddress = ctx[CpuRegister.Rdi];
        var mode = (byte)ctx[CpuRegister.Rsi];
        var targetAddress = ctx[CpuRegister.Rdx];
        var count = (uint)ctx[CpuRegister.Rcx];

        // Firmware 12.70 libSceAgc.sprx SHA-256
        // 110df81f759ae3dffcc9b5e3fa062c74058518631847641b8e08a54f6b8b6e2d:
        // Ikfdt-rIqCE at 0xc360 accepts only a descriptor tagged 0x3f at
        // byte +1. It preserves the low two address flags and selected control
        // bits while installing the supplied address, mode, and 20-bit count.
        if (!TryReadByte(ctx, descriptorAddress + 1, out var tag))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (tag != 0x3F)
        {
            return ctx.SetReturn(AgcErrorInvalidPatchDescriptor);
        }

        if (!TryReadUInt32(ctx, descriptorAddress + 4, out var addressLowAndFlags) ||
            !TryReadUInt32(ctx, descriptorAddress + 12, out var control) ||
            !TryWriteUInt32(
                ctx,
                descriptorAddress + 4,
                (addressLowAndFlags & 0x3) | ((uint)targetAddress & 0xFFFF_FFFC)) ||
            !TryWriteUInt32(ctx, descriptorAddress + 8, (uint)(targetAddress >> 32)) ||
            !TryWriteUInt32(
                ctx,
                descriptorAddress + 12,
                ((uint)(mode & 0x3) << 28) | (control & 0xCFF0_0000) | (count & 0x000F_FFFF)))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "D9sr1xGUriE",
        ExportName = "sceAgcCreatePrimState",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CreatePrimState(CpuContext ctx)
    {
        var cxRegistersAddress = ctx[CpuRegister.Rdi];
        var ucRegistersAddress = ctx[CpuRegister.Rsi];
        var hullShaderAddress = ctx[CpuRegister.Rdx];
        var geometryShaderAddress = ctx[CpuRegister.Rcx];
        var primitiveType = (uint)ctx[CpuRegister.R8];

        // Firmware 12.70 libSceAgc.sprx SHA-256
        // 110df81f759ae3dffcc9b5e3fa062c74058518631847641b8e08a54f6b8b6e2d:
        // D9sr1xGUriE at 0x10c60 accepts an optional hull shader and optional
        // CX/UC output arrays.  In particular, the hull+geometry path used by
        // GTA V is a normal merge path rather than an invalid argument.
        if (cxRegistersAddress == 0 && ucRegistersAddress == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!TryReadUInt64(
                ctx,
                geometryShaderAddress + ShaderSpecialsOffset,
                out var geometrySpecialsAddress))
        {
            // The native provider would fault on an inaccessible descriptor.
            // Convert that guest fault into SharpEmu's recoverable memory error.
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var hullSpecialsAddress = 0UL;
        if (hullShaderAddress != 0 &&
            !TryReadUInt64(
                ctx,
                hullShaderAddress + ShaderSpecialsOffset,
                out hullSpecialsAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (cxRegistersAddress != 0)
        {
            if (!TryReadUInt32(
                    ctx,
                    geometrySpecialsAddress + ShaderSpecialVgtShaderStagesEnOffset,
                    out var stagesRegister) ||
                !TryReadUInt32(
                    ctx,
                    geometrySpecialsAddress + ShaderSpecialVgtShaderStagesEnOffset + sizeof(uint),
                    out var stagesValue) ||
                !TryReadUInt32(
                    ctx,
                    geometrySpecialsAddress + ShaderSpecialVgtGsOutPrimTypeOffset,
                    out var outputPrimitiveRegister) ||
                !TryReadUInt32(
                    ctx,
                    geometrySpecialsAddress + ShaderSpecialVgtGsOutPrimTypeOffset + sizeof(uint),
                    out var outputPrimitiveValue))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if ((stagesValue & 0x20) == 0)
            {
                // The provider's Vsh register-default table selects
                // { VGT_GS_OUT_PRIM_TYPE (0x29b), 2 } and replaces its low
                // three value bits with this API's primitive mapping.
                outputPrimitiveRegister = VgtGsOutPrimType;
                outputPrimitiveValue = MapPrimStateOutputPrimitive(primitiveType);
            }

            if (hullShaderAddress != 0)
            {
                if (!TryReadUInt32(
                        ctx,
                        hullSpecialsAddress + ShaderSpecialVgtShaderStagesEnOffset + sizeof(uint),
                        out var hullStagesValue))
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                stagesValue |= hullStagesValue;
                if ((stagesValue & 0x20) == 0 &&
                    (!TryReadUInt32(
                         ctx,
                         hullSpecialsAddress + ShaderSpecialVgtGsOutPrimTypeOffset,
                         out outputPrimitiveRegister) ||
                     !TryReadUInt32(
                         ctx,
                         hullSpecialsAddress + ShaderSpecialVgtGsOutPrimTypeOffset + sizeof(uint),
                         out outputPrimitiveValue)))
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }

            Span<byte> cxRegisters = stackalloc byte[0x10];
            BinaryPrimitives.WriteUInt32LittleEndian(cxRegisters, stagesRegister);
            BinaryPrimitives.WriteUInt32LittleEndian(cxRegisters[4..], stagesValue);
            BinaryPrimitives.WriteUInt32LittleEndian(cxRegisters[8..], outputPrimitiveRegister);
            BinaryPrimitives.WriteUInt32LittleEndian(cxRegisters[12..], outputPrimitiveValue);
            if (!ctx.Memory.TryWrite(cxRegistersAddress, cxRegisters))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        if (ucRegistersAddress != 0)
        {
            if (!TryReadUInt32(
                    ctx,
                    geometrySpecialsAddress + ShaderSpecialGeCntlOffset,
                    out var geControlRegister) ||
                !TryReadUInt32(
                    ctx,
                    geometrySpecialsAddress + ShaderSpecialGeCntlOffset + sizeof(uint),
                    out var geControlValue) ||
                !TryReadUInt32(
                    ctx,
                    geometrySpecialsAddress + ShaderSpecialGeUserVgprEnOffset,
                    out var userVgprRegister) ||
                !TryReadUInt32(
                    ctx,
                    geometrySpecialsAddress + ShaderSpecialGeUserVgprEnOffset + sizeof(uint),
                    out var userVgprValue))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (hullShaderAddress != 0 &&
                (!TryReadUInt32(
                     ctx,
                     hullSpecialsAddress + ShaderSpecialGeUserVgprEnOffset,
                     out userVgprRegister) ||
                 !TryReadUInt32(
                     ctx,
                     hullSpecialsAddress + ShaderSpecialGeUserVgprEnOffset + sizeof(uint),
                     out userVgprValue)))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            Span<byte> ucRegisters = stackalloc byte[0x18];
            BinaryPrimitives.WriteUInt32LittleEndian(ucRegisters, geControlRegister);
            BinaryPrimitives.WriteUInt32LittleEndian(ucRegisters[4..], geControlValue);
            BinaryPrimitives.WriteUInt32LittleEndian(ucRegisters[8..], userVgprRegister);
            BinaryPrimitives.WriteUInt32LittleEndian(ucRegisters[12..], userVgprValue);
            BinaryPrimitives.WriteUInt32LittleEndian(ucRegisters[16..], VgtPrimitiveType);
            BinaryPrimitives.WriteUInt32LittleEndian(ucRegisters[20..], primitiveType);
            if (!ctx.Memory.TryWrite(ucRegistersAddress, ucRegisters))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        TraceAgc(
            $"agc.create_prim_state cx=0x{cxRegistersAddress:X16} " +
            $"uc=0x{ucRegistersAddress:X16} hs=0x{hullShaderAddress:X16} " +
            $"gs=0x{geometryShaderAddress:X16} prim=0x{primitiveType:X8}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // NID captured from shipped titles; the friendly name collides with a real catalog symbol of a different NID. Rename pending AGC API confirmation.
    #pragma warning disable SHEM004
    [SysAbiExport(
        Nid = "HV4j+E0MBHE",
        ExportName = "sceAgcCreateInterpolantMapping",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CreateInterpolantMapping(CpuContext ctx)
    {
        var registersAddress = ctx[CpuRegister.Rdi];
        var geometryShaderAddress = ctx[CpuRegister.Rsi];
        var pixelShaderAddress = ctx[CpuRegister.Rdx];

        if (registersAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (pixelShaderAddress == 0)
        {
            return WriteIdentityInterpolantMapping(ctx, registersAddress, 0);
        }

        if (!TryReadUInt64(
                ctx,
                pixelShaderAddress + ShaderInputSemanticsOffset,
                out var inputSemanticsAddress) ||
            !TryReadUInt32(
                ctx,
                pixelShaderAddress + ShaderNumInputSemanticsOffset,
                out var inputSemanticsCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (inputSemanticsCount == 0)
        {
            return WriteIdentityInterpolantMapping(ctx, registersAddress, 0);
        }

        if (geometryShaderAddress == 0 || inputSemanticsAddress == 0 ||
            !TryReadUInt64(
                ctx,
                geometryShaderAddress + ShaderOutputSemanticsOffset,
                out var outputSemanticsAddress) ||
            !TryReadUInt32(
                ctx,
                geometryShaderAddress + ShaderNumOutputSemanticsOffset,
                out var packedOutputSemanticsCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // num_output_semantics is a uint16 followed by other packed header
        // fields. Reading the enclosing dword is safe, but only the low half
        // belongs to the count.
        var outputSemanticsCount = packedOutputSemanticsCount & 0xFFFFu;
        var mappedCount = Math.Min(inputSemanticsCount, 32u);
        for (uint pixelIndex = 0; pixelIndex < mappedCount; pixelIndex++)
        {
            if (!TryReadUInt32(
                    ctx,
                    inputSemanticsAddress + (pixelIndex * sizeof(uint)),
                    out var pixelSemantic))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            uint? geometrySemantic = null;
            if (outputSemanticsAddress != 0)
            {
                for (uint geometryIndex = 0;
                     geometryIndex < outputSemanticsCount;
                     geometryIndex++)
                {
                    if (!TryReadUInt32(
                            ctx,
                            outputSemanticsAddress + (geometryIndex * sizeof(uint)),
                            out var candidate))
                    {
                        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                    }

                    if ((candidate & 0xFFu) == (pixelSemantic & 0xFFu))
                    {
                        geometrySemantic = candidate;
                        break;
                    }
                }
            }

            var value = CreateInterpolantMappingValue(
                pixelSemantic,
                geometrySemantic);
            if (!WriteInterpolantMappingRegister(
                    ctx,
                    registersAddress,
                    pixelIndex,
                    value))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        var identityResult = WriteIdentityInterpolantMapping(
            ctx,
            registersAddress,
            mappedCount);
        if (identityResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return identityResult;
        }

        TraceAgc($"agc.create_interpolant_mapping regs=0x{registersAddress:X16} gs=0x{geometryShaderAddress:X16} ps=0x{pixelShaderAddress:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static uint CreateInterpolantMappingValue(
        uint pixelSemantic,
        uint? geometrySemantic)
    {
        uint value;
        if ((pixelSemantic & 0x0030_0000u) != 0)
        {
            value = (pixelSemantic << 4) & 0x0300_0000u;
            if (geometrySemantic is { } geometry)
            {
                var common = pixelSemantic & geometry;
                value &= 0xFFF7_FFDFu;
                value |= (common >> 15) & 0x20u;
                value ^= 0x0008_0020u;
                value &= ~0x0010_0000u;
                value |= (~common >> 1) & 0x0010_0000u;
            }
            else
            {
                value |= 0x0018_0020u;
            }

            value &= ~0x0060_0000u;
            value |= ((pixelSemantic >> 30) & 0x3u) << 21;
        }
        else
        {
            value = (pixelSemantic & 0x0100_0000u) != 0 ||
                geometrySemantic is null
                    ? 0x20u
                    : 0u;
        }

        value &= ~0x1Fu;
        value |= geometrySemantic is { } mapped
            ? (mapped >> 8) & 0x1Fu
            : 0u;
        value &= ~0x400u;
        if (geometrySemantic is not null &&
            (pixelSemantic & 0x0140_0000u) != 0)
        {
            value |= 0x400u;
        }

        value &= ~0x300u;
        value |= ((pixelSemantic >> 28) & 0x3u) << 8;
        return value;
    }

    private static int WriteIdentityInterpolantMapping(
        CpuContext ctx,
        ulong registersAddress,
        uint firstIndex)
    {
        for (var index = firstIndex; index < 32u; index++)
        {
            if (!WriteInterpolantMappingRegister(
                    ctx,
                    registersAddress,
                    index,
                    index))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool WriteInterpolantMappingRegister(
        CpuContext ctx,
        ulong registersAddress,
        uint index,
        uint value)
    {
        var destination = registersAddress + (index * 8);
        return TryWriteUInt32(ctx, destination, SpiPsInputCntl0 + index) &&
            TryWriteUInt32(ctx, destination + sizeof(uint), value);
    }
    #pragma warning restore SHEM004

    private static uint ApplyInterpolantDefaultValue(uint value, uint psWord)
    {
        value &= ~0x0000_0300u;
        value |= ((psWord >> 28) & 0x3u) << 8;
        return value;
    }

    private static uint ApplyInterpolantDefaultValueHi(uint value, uint psWord)
    {
        value &= ~0x0060_0000u;
        value |= ((psWord >> 30) & 0x3u) << 21;
        return value;
    }

    private static uint CreateInterpolantMappingValue(uint value, uint psWord, uint gsWord)
    {
        var flatShade =
            (psWord & 0x0040_0000u) != 0 || (psWord & 0x0100_0000u) != 0
                ? 0x0000_0400u
                : 0u;
        value &= ~0x0000_001Fu;
        value |= (gsWord >> 8) & 0x1Fu;
        value &= ~0x0000_0400u;
        value |= flatShade;
        return ApplyInterpolantDefaultValue(value, psWord);
    }

    private static uint CreateInterpolantDefaultParamValue(uint value, uint psWord)
    {
        value &= ~0x0000_001Fu;
        value &= ~0x0000_0400u;
        return ApplyInterpolantDefaultValue(value, psWord);
    }

    private static uint CreateInterpolantF16Value(uint psWord, uint? gsWord)
    {
        var value = (psWord << 4) & 0x0300_0000u;
        if (gsWord is null)
        {
            value |= 0x0018_0020u;
        }
        else
        {
            var commonWord = psWord & gsWord.Value;
            value &= 0xFFF7_FFDFu;
            value |= (commonWord >> 15) & 0x20u;
            value ^= 0x0008_0020u;
            value &= ~0x0010_0000u;
            value |= (~commonWord >> 1) & 0x0010_0000u;
        }

        return ApplyInterpolantDefaultValueHi(value, psWord);
    }

    private static uint CreateInterpolantNonF16Value(uint psWord, bool hasGsSemantic)
    {
        uint value = 0;
        if ((psWord & 0x0100_0000u) != 0 || !hasGsSemantic)
        {
            value |= 0x20u;
        }

        return value;
    }

    private static bool TryWriteInterpolantRegister(
        CpuContext ctx,
        ulong registersAddress,
        uint index,
        uint value)
    {
        var destination = registersAddress + (index * 8);
        return TryWriteUInt32(ctx, destination, SpiPsInputCntl0 + index) &&
               TryWriteUInt32(ctx, destination + sizeof(uint), value);
    }

    private static bool TryWriteIdentityInterpolantRegisters(
        CpuContext ctx,
        ulong registersAddress,
        uint firstIndex)
    {
        for (uint i = firstIndex; i < 32u; i++)
        {
            if (!TryWriteInterpolantRegister(ctx, registersAddress, i, i))
            {
                return false;
            }
        }

        return true;
    }

    private static uint[] ReadPsInputCntlRegisters(IReadOnlyDictionary<uint, uint> cxRegisters)
    {
        var cntl = new uint[32];
        for (uint i = 0; i < 32u; i++)
        {
            // Unprogrammed slots default to identity (ATTR i → param i).
            cntl[i] = cxRegisters.TryGetValue(SpiPsInputCntl0 + i, out var value)
                ? value
                : i;
        }

        return cntl;
    }

    private static ulong ComputePsInputCntlFingerprint(ReadOnlySpan<uint> cntl)
    {
        const ulong prime = 1099511628211UL;
        var hash = 14695981039346656037UL;
        foreach (var value in cntl)
        {
            hash = (hash ^ value) * prime;
        }

        return hash;
    }

    // NID captured from shipped titles; the friendly name collides with a real catalog symbol of a different NID. Rename pending AGC API confirmation.
    #pragma warning disable SHEM004
    [SysAbiExport(
        Nid = "V++UgBtQhn0",
        ExportName = "sceAgcGetDataPacketPayloadAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetDataPacketPayloadAddress(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        var commandAddress = ctx[CpuRegister.Rsi];
        var type = (int)ctx[CpuRegister.Rdx];
        if (outputAddress == 0 || commandAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var payloadAddress = commandAddress + 8;
        if (type == 0)
        {
            if (!TryReadUInt32(ctx, commandAddress, out var header))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            payloadAddress = (header & 0x3FFF_0000u) == 0x3FFF_0000u
                ? 0
                : commandAddress + 4;
        }

        if (!TryWriteUInt64(ctx, outputAddress, payloadAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (ShouldTraceHotPath(ref _packetPayloadTraceCount))
        {
            TraceAgc(
                $"agc.get_packet_payload out=0x{outputAddress:X16} cmd=0x{commandAddress:X16} " +
                $"type={type} payload=0x{payloadAddress:X16}");
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
    #pragma warning restore SHEM004

    [SysAbiExport(
        Nid = "LtTouSCZjHM",
        ExportName = "sceAgcCbNop",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbNop(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dwordCount = (uint)ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0 || dwordCount < 2 || dwordCount > 0x4001)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, dwordCount, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(dwordCount, ItNop, RZero)))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint index = 1; index < dwordCount; index++)
        {
            if (!TryWriteUInt32(ctx, commandAddress + ((ulong)index * sizeof(uint)), 0))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "k3GhuSNmBLU",
        ExportName = "sceAgcCbDispatch",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbDispatch(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var groupCountX = (uint)ctx[CpuRegister.Rsi];
        var groupCountY = (uint)ctx[CpuRegister.Rdx];
        var groupCountZ = (uint)ctx[CpuRegister.Rcx];
        var modifier = (uint)ctx[CpuRegister.R8];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItDispatchDirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, groupCountX) ||
            !TryWriteUInt32(ctx, commandAddress + 8, groupCountY) ||
            !TryWriteUInt32(ctx, commandAddress + 12, groupCountZ) ||
            !TryWriteUInt32(ctx, commandAddress + 16, DirectDispatchInitiator(modifier)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    private static uint DirectDispatchInitiator(uint modifier) =>
        // AGC's direct API takes workgroup counts by default. Preserve the
        // caller's USE_THREAD_DIMENSIONS bit when explicitly requested; do not
        // force it. Demon's Souls' 0xF00100 dispatch is paired with a
        // 0x3C004000 element bound (exactly 64 lanes per group), proving the
        // default packet is group-dimensional.
        (modifier & 0xA038u) | 0x41u;

    [SysAbiExport(
        Nid = "UZbQjYAwwXM",
        ExportName = "sceAgcCbSetShRegistersDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbSetShRegistersDirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var registersAddress = ctx[CpuRegister.Rsi];
        var registerCount = (uint)ctx[CpuRegister.Rdx];
        if (registerCount == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (commandBufferAddress == 0 || registersAddress == 0 || registerCount > 4096)
        {
            return ReturnPointer(ctx, 0);
        }

        var registers = new RegisterDefaultValue[registerCount];
        for (uint index = 0; index < registerCount; index++)
        {
            var entryAddress = registersAddress + ((ulong)index * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var offset) ||
                !TryReadUInt32(ctx, entryAddress + sizeof(uint), out var value))
            {
                return ReturnPointer(ctx, 0);
            }

            registers[index] = new RegisterDefaultValue(offset, value);
        }

        Array.Sort(registers, static (left, right) => left.Offset.CompareTo(right.Offset));
        ulong firstCommandAddress = 0;
        var startIndex = 0;
        while (startIndex < registers.Length)
        {
            var endIndex = startIndex + 1;
            while (endIndex < registers.Length &&
                   registers[endIndex].Offset == registers[endIndex - 1].Offset + 1)
            {
                endIndex++;
            }

            var valueCount = (uint)(endIndex - startIndex);
            var packetDwords = valueCount + 2;
            if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
                !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItSetShReg, 0)) ||
                !TryWriteUInt32(ctx, commandAddress + 4, registers[startIndex].Offset & 0xFFFFu))
            {
                return ReturnPointer(ctx, 0);
            }

            firstCommandAddress = firstCommandAddress == 0 ? commandAddress : firstCommandAddress;
            for (var index = startIndex; index < endIndex; index++)
            {
                if (!TryWriteUInt32(
                        ctx,
                        commandAddress + 8 + ((ulong)(index - startIndex) * sizeof(uint)),
                        registers[index].Value))
                {
                    return ReturnPointer(ctx, 0);
                }
            }

            startIndex = endIndex;
        }

        return ReturnPointer(ctx, firstCommandAddress);
    }

    [SysAbiExport(
        Nid = "JrtiDtKeS38",
        ExportName = "sceAgcAcbResetQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbResetQueue(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RAcbReset)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "cFazmnXpJOE",
        ExportName = "sceAgcAcbEventWrite",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbEventWrite(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var eventType = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var eventAddress = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || eventType >= 0x40)
        {
            return ReturnPointer(ctx, 0);
        }

        var hasAddress = (eventType & ~1u) == 0x38;
        var packetDwords = hasAddress ? 4u : 2u;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItEventWrite, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, hasAddress ? eventType | 0x100u : eventType & 0x3Fu))
        {
            return ReturnPointer(ctx, 0);
        }

        if (hasAddress &&
            (!TryWriteUInt32(ctx, commandAddress + 8, (uint)eventAddress & ~7u) ||
             !TryWriteUInt32(ctx, commandAddress + 12, (uint)(eventAddress >> 32))))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "KT-hTp-Ch14",
        ExportName = "sceAgcAcbAcquireMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbAcquireMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var gcrControl = (uint)ctx[CpuRegister.Rsi];
        var baseAddress = ctx[CpuRegister.Rdx];
        var sizeBytes = ctx[CpuRegister.Rcx];
        var pollCycles = (uint)ctx[CpuRegister.R8];
        var noSize = sizeBytes == ulong.MaxValue;
        if (commandBufferAddress == 0 ||
            (!noSize && (sizeBytes & 0xFF) != 0) ||
            (!noSize && (sizeBytes >> 40) != 0) ||
            (baseAddress & 0xFF) != 0 ||
            (baseAddress >> 40) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RAcquireMem)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0x8000_0000u) ||
            !TryWriteUInt32(ctx, commandAddress + 8, noSize ? 0 : (uint)(sizeBytes >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(baseAddress >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, pollCycles / 40) ||
            !TryWriteUInt32(ctx, commandAddress + 28, gcrControl))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "htn36gPnBk4",
        ExportName = "sceAgcAcbWaitRegMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbWaitRegMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var size = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var compareFunction = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var address = ctx[CpuRegister.R8];
        var reference = ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var mask) ||
            !TryReadUInt32(ctx, stackAddress + (2 * sizeof(ulong)), out var pollCycles) ||
            commandBufferAddress == 0 ||
            size > 1 ||
            compareFunction > 7 ||
            cachePolicy > 3)
        {
            return ReturnPointer(ctx, 0);
        }

        var packetDwords = size == 0 ? 7u : 9u;
        var packetRegister = size == 0 ? RWaitMem32 : RWaitMem64;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, packetRegister)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)address & (size == 0 ? ~0x3u : ~0x7u)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(address >> 32) & 0x3FFFFu) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)mask))
        {
            return ReturnPointer(ctx, 0);
        }

        if (size == 0)
        {
            if (!TryWriteUInt32(ctx, commandAddress + 16, (uint)reference) ||
                !TryWriteUInt32(ctx, commandAddress + 20, EncodeWaitRegMem32Control(compareFunction, 0, cachePolicy)) ||
                !TryWriteUInt32(ctx, commandAddress + 24, EncodeWaitRegMemPoll(pollCycles)))
            {
                return ReturnPointer(ctx, 0);
            }
        }
        else if (!TryWriteUInt32(ctx, commandAddress + 16, (uint)(mask >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 20, (uint)reference) ||
                 !TryWriteUInt32(ctx, commandAddress + 24, (uint)(reference >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 28, EncodeWaitRegMem64Control(compareFunction, 0, cachePolicy)) ||
                 !TryWriteUInt32(ctx, commandAddress + 32, EncodeWaitRegMemPoll(pollCycles)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "eZ4+17OQz4Q",
        ExportName = "sceAgcAcbWriteData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbWriteData(CpuContext ctx) =>
        DcbWriteData(ctx);

    [SysAbiExport(
        Nid = "j3EtxFkSIhQ",
        ExportName = "sceAgcAcbDispatchIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbDispatchIndirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var argumentsAddress = ctx[CpuRegister.Rsi];
        var modifier = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItDispatchIndirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)argumentsAddress) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(argumentsAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (modifier & 0xA038u) | 0x41u))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "n2fD4A+pb+g",
        ExportName = "sceAgcCbSetShRegisterRangeDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbSetShRegisterRangeDirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var offset = (uint)ctx[CpuRegister.Rsi];
        var valuesAddress = ctx[CpuRegister.Rdx];
        var valueCount = (uint)ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0 || offset == 0 || offset > 0x3FF || valueCount == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var markerAddress) ||
            !TryWriteUInt32(ctx, markerAddress, Pm4(2, ItNop, RZero)) ||
            !TryWriteUInt32(ctx, markerAddress + 4, CbSetShRegisterRangeMarker) ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, valueCount + 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(valueCount + 2, ItSetShReg, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, offset))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint i = 0; i < valueCount; i++)
        {
            var value = 0u;
            if (valuesAddress != 0 &&
                !TryReadUInt32(ctx, valuesAddress + (i * sizeof(uint)), out value))
            {
                return ReturnPointer(ctx, 0);
            }

            if (!TryWriteUInt32(ctx, commandAddress + 8 + (i * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        TraceAgc($"agc.cb_set_sh_range buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} offset=0x{offset:X8} count={valueCount}");
        RefreshBuilderArenaCursorPassive(ctx, commandBufferAddress);
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "wr23dPKyWc0",
        ExportName = "sceAgcCbReleaseMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbReleaseMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var action = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var gcrControl = (uint)(ctx[CpuRegister.Rdx] & 0xFFFF);
        var destination = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.R8] & 0xFF);
        var destinationAddress = ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + 8, out var dataSelectionRaw) ||
            !TryReadUInt64(ctx, stackAddress + 16, out var data) ||
            !TryReadUInt64(ctx, stackAddress + 24, out var gdsOffsetRaw) ||
            !TryReadUInt64(ctx, stackAddress + 32, out var gdsSizeRaw) ||
            !TryReadUInt64(ctx, stackAddress + 40, out var interruptRaw) ||
            !TryReadUInt64(ctx, stackAddress + 48, out var interruptContextIdRaw))
        {
            return ReturnPointer(ctx, 0);
        }

        var dataSelection = (uint)(dataSelectionRaw & 0xFF);
        var gdsOffset = (uint)(gdsOffsetRaw & 0xFFFF);
        var gdsSize = (uint)(gdsSizeRaw & 0xFFFF);
        var interrupt = (uint)(interruptRaw & 0xFF);
        var interruptContextId = (uint)interruptContextIdRaw;
        if (commandBufferAddress == 0 ||
            destination > 1 ||
            dataSelection > 3 ||
            gdsOffset != 0 ||
            gdsSize > 2 ||
            interrupt > 3)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RReleaseMem)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, action | (cachePolicy << 8)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 8,
                gcrControl | (dataSelection << 16) | (interrupt << 24)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)destinationAddress) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(destinationAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, (uint)data) ||
            !TryWriteUInt32(ctx, commandAddress + 24, (uint)(data >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 28, interruptContextId))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.cb_release_mem buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"action=0x{action:X2} gcr=0x{gcrControl:X4} dst=0x{destinationAddress:X16} data_sel={dataSelection} data=0x{data:X16}");
        TrackCbReleaseMemTarget(ctx, commandBufferAddress, destinationAddress);
        RecordRingChunkWriter(commandAddress);
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "TRO721eVt4g",
        ExportName = "sceAgcDcbResetQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbResetQueue(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var op = (uint)ctx[CpuRegister.Rsi];
        var state = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || op != 0x3FF || state != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RDrawReset)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_reset_queue buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "ZvwO9euwYzc",
        ExportName = "sceAgcDcbSetCxRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetCxRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RCxRegsIndirect, "cx");

    [SysAbiExport(
        Nid = "-HOOCn0JY48",
        ExportName = "sceAgcDcbSetShRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetShRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RShRegsIndirect, "sh");

    [SysAbiExport(
        Nid = "hvUfkUIQcOE",
        ExportName = "sceAgcDcbSetUcRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetUcRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RUcRegsIndirect, "uc");

    [SysAbiExport(
        Nid = "w4-d0n60hdo",
        ExportName = "sceAgcDcbSetUcRegisterDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetUcRegisterDirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var packedRegisterAndValue = ctx[CpuRegister.Rsi];

        // Firmware 12.70 libSceAgc.sprx SHA-256
        // 110df81f759ae3dffcc9b5e3fa062c74058518631847641b8e08a54f6b8b6e2d:
        // w4-d0n60hdo at 0x4900 reserves three dwords, then emits exactly
        // C0017900, the low 16 bits of RSI, and the high 32 bits of RSI.
        // GTA packs the UCONFIG register offset and value into that argument.
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 3, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(3, ItSetUconfigReg, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + sizeof(uint), (uint)packedRegisterAndValue & 0xFFFF) ||
            !TryWriteUInt32(ctx, commandAddress + (2 * sizeof(uint)), (uint)(packedRegisterAndValue >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_set_uc_direct buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"reg=0x{((uint)packedRegisterAndValue & 0xFFFF):X4} value=0x{((uint)(packedRegisterAndValue >> 32)):X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "GIIW2J37e70",
        ExportName = "sceAgcDcbSetIndexSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexSize(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexSize = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        if (commandBufferAddress == 0 || cachePolicy != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItIndexType, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexSize))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_index_size buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} size={indexSize}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "8N2tmT3jmC8",
        ExportName = "sceAgcDcbSetIndexCount",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexCount(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexCount = (uint)ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RIndexCount)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexCount))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "mljzuGDZRQ4",
        ExportName = "sceAgcDcbSetIndexCountGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexCountGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 7u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "tSBxhAPyytQ",
        ExportName = "sceAgcDcbSetNumInstances",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetNumInstances(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var instanceCount = (uint)ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNumInstances, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, instanceCount))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_num_instances buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} count={instanceCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "q88lQ+GP5Yk",
        ExportName = "sceAgcDcbDrawIndex",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndex(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexCount = (uint)ctx[CpuRegister.Rsi];
        var indexAddress = ctx[CpuRegister.Rdx];
        var modifier = (uint)ctx[CpuRegister.Rcx];

        if (commandBufferAddress == 0 || modifier != 0x4000_0000)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var baseCommand) ||
            !TryWriteUInt32(ctx, baseCommand, Pm4(3, ItIndexBase, 0)) ||
            !TryWriteUInt32(ctx, baseCommand + 4, (uint)indexAddress) ||
            !TryWriteUInt32(ctx, baseCommand + 8, (uint)(indexAddress >> 32)) ||
            !TryWriteUInt32(ctx, baseCommand + 12, Pm4(2, ItIndexBufferSize, 0)) ||
            !TryWriteUInt32(ctx, baseCommand + 16, indexCount))
        {
            return ReturnPointer(ctx, 0);
        }

        // DRAW_INDEX_2 is six dwords: header, maximum index count, the
        // 64-bit index-buffer base, the draw count and the initiator.  The
        // former five-dword packet omitted both the real base and the count
        // field, so every call made by Unity looked like a zero-count draw to
        // the submitted-command parser and the complete scene was discarded.
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 6, out var drawCommand) ||
            !TryWriteUInt32(ctx, drawCommand, Pm4(6, ItDrawIndex2, 0)) ||
            !TryWriteUInt32(ctx, drawCommand + 4, indexCount) ||
            !TryWriteUInt32(ctx, drawCommand + 8, (uint)indexAddress) ||
            !TryWriteUInt32(ctx, drawCommand + 12, (uint)(indexAddress >> 32)) ||
            !TryWriteUInt32(ctx, drawCommand + 16, indexCount) ||
            !TryWriteUInt32(ctx, drawCommand + 20, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_draw_index buf=0x{commandBufferAddress:X16} " +
            $"base=0x{baseCommand:X16} draw=0x{drawCommand:X16} " +
            $"count={indexCount} index=0x{indexAddress:X16}");

        return ReturnPointer(ctx, drawCommand);
    }

    [SysAbiExport(
        Nid = "1q1titRBL6o",
        ExportName = "sceAgcDcbDrawIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dataOffset = (uint)ctx[CpuRegister.Rsi];
        var emit = Interlocked.Increment(ref _indirectDrawEmitCount);

        if (emit <= 12 || emit % 250 == 0)
        {
            var rcx = ctx[CpuRegister.Rcx];
            var dump = string.Empty;
            for (var word = 0; word < 8; word++)
            {
                dump += TryReadUInt32(ctx, rcx + dataOffset + ((ulong)word * 4), out var raw)
                    ? $" {raw}"
                    : " ?";
            }

            Console.Error.WriteLine(
                $"[LOADER][WARN] agc.emit_indirect#{emit} buf=0x{commandBufferAddress:X16} " +
                $"off=0x{dataOffset:X} rdx=0x{ctx[CpuRegister.Rdx]:X} rcx=0x{rcx:X} " +
                $"r8=0x{ctx[CpuRegister.R8]:X} rcx_words:{dump}");
        }

        if (commandBufferAddress == 0)
        {
            Interlocked.Increment(ref _indirectDrawEmitRejectCount);
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var drawCommand) ||
            !TryWriteUInt32(ctx, drawCommand, Pm4(5, ItDrawIndirect, 0)) ||
            !TryWriteUInt32(ctx, drawCommand + 4, dataOffset) ||
            !TryWriteUInt32(ctx, drawCommand + 8, 0) ||
            !TryWriteUInt32(ctx, drawCommand + 12, 0) ||
            !TryWriteUInt32(ctx, drawCommand + 16, 0))
        {
            var rejects = Interlocked.Increment(ref _indirectDrawEmitRejectCount);
            if (rejects <= 8 || rejects % 250 == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] agc.emit_indirect_reject#{rejects} " +
                    $"buf=0x{commandBufferAddress:X16} reason=alloc_or_write");
            }

            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_draw_indirect buf=0x{commandBufferAddress:X16} " +
            $"draw=0x{drawCommand:X16} offset=0x{dataOffset:X}");

        return ReturnPointer(ctx, drawCommand);
    }

    [SysAbiExport(
        Nid = "Yw0jKSqop+E",
        ExportName = "sceAgcDcbDrawIndexAuto",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexAuto(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexCount = (uint)ctx[CpuRegister.Rsi];
        var modifier = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || modifier != 0x4000_0000)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 7, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(7, ItNop, RDrawIndexAuto)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexCount) ||
            !TryWriteUInt32(ctx, commandAddress + 8, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_draw_index_auto buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} count={indexCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "t1vNu082-jM",
        ExportName = "sceAgcDcbDrawIndexIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexIndirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dataOffset = (uint)ctx[CpuRegister.Rsi];
        var modifier = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItDrawIndexIndirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, dataOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 8, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, modifier))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_draw_index_indirect buf=0x{commandBufferAddress:X16} " +
            $"cmd=0x{commandAddress:X16} offset=0x{dataOffset:X8} modifier=0x{modifier:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "ypVBz4uPKcQ",
        ExportName = "sceAgcDcbDrawIndexIndirectMulti",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexIndirectMulti(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dataOffset = (uint)ctx[CpuRegister.Rsi];
        var drawCount = (uint)ctx[CpuRegister.Rdx];
        var stride = DrawIndexedIndirectArgsSize;
        var modifier = (uint)ctx[CpuRegister.R8];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItDrawIndexIndirectMulti, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, dataOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 8, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 20, drawCount) ||
            !TryWriteUInt32(ctx, commandAddress + 24, stride) ||
            !TryWriteUInt32(ctx, commandAddress + 28, modifier))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_draw_index_indirect_multi buf=0x{commandBufferAddress:X16} " +
            $"cmd=0x{commandAddress:X16} offset=0x{dataOffset:X8} draws={drawCount} " +
            $"stride={stride} modifier=0x{modifier:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "mStuvI0zOtc",
        ExportName = "sceAgcDcbDrawIndexIndirectGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexIndirectGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 5u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "r98I08t+LOg",
        ExportName = "sceAgcDcbDrawIndexIndirectMultiGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexIndirectMultiGetSize(CpuContext ctx)
    {
        // Eight, matching the packet DcbDrawIndexIndirectMulti emits.
        ctx[CpuRegister.Rax] = 8u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "rUuVjyR+Rd4",
        ExportName = "sceAgcDcbGetLodStatsGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbGetLodStatsGetSize(CpuContext ctx)
    {
        var counterCount = (uint)ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = 0x10u + (counterCount * sizeof(uint));
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "vuSXe69VILM",
        ExportName = "sceAgcDcbGetLodStats",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbGetLodStats(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var cachePolicy = (uint)ctx[CpuRegister.Rsi] & 0x3u;
        var destinationAddress = ctx[CpuRegister.Rdx];
        var control = (uint)ctx[CpuRegister.Rcx];
        var counterMask = (uint)ctx[CpuRegister.R8] & 0xFFu;
        var resetCounters = (uint)ctx[CpuRegister.R9] & 0x1u;
        if (!TryReadUInt64(ctx, ctx[CpuRegister.Rsp] + sizeof(ulong), out var enableRaw) ||
            !TryReadUInt64(ctx, ctx[CpuRegister.Rsp] + (2 * sizeof(ulong)), out var counterSelectRaw) ||
            commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var enable = (uint)enableRaw & 0x1u;
        var counterSelect = (uint)counterSelectRaw & 0xFFu;
        var packetControl =
            (cachePolicy << 28) |
            (enable << 19) |
            (resetCounters << 18) |
            (counterMask << 10) |
            (counterSelect << 2);
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItGetLodStats, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, control) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)destinationAddress & ~0x3Fu) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(destinationAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 16, packetControl))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_get_lod_stats buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"dst=0x{destinationAddress:X16} control=0x{control:X8} counters=0x{counterMask:X2}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "aJf+j5yntiU",
        ExportName = "sceAgcDcbEventWrite",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbEventWrite(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var eventType = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var eventAddress = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || eventType > 0x3F || eventAddress != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItEventWrite, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, eventType))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_event_write buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} type={eventType}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "57labkp+rSQ",
        ExportName = "sceAgcDcbAcquireMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbAcquireMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var engine = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var cbDbOp = (uint)ctx[CpuRegister.Rdx];
        var gcrControl = (uint)ctx[CpuRegister.Rcx];
        var baseAddress = ctx[CpuRegister.R8];
        var sizeBytes = ctx[CpuRegister.R9];
        if (!TryReadUInt32(ctx, ctx[CpuRegister.Rsp] + sizeof(ulong), out var pollCycles))
        {
            return ReturnPointer(ctx, 0);
        }

        var noSize = sizeBytes == ulong.MaxValue;
        if (commandBufferAddress == 0 ||
            engine > 1 ||
            (!noSize && (sizeBytes & 0xFF) != 0) ||
            (!noSize && (sizeBytes >> 40) != 0) ||
            (baseAddress & 0xFF) != 0 ||
            (baseAddress >> 40) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RAcquireMem)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (engine << 31) | cbDbOp) ||
            !TryWriteUInt32(ctx, commandAddress + 8, noSize ? 0 : (uint)(sizeBytes >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(baseAddress >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, pollCycles / 40) ||
            !TryWriteUInt32(ctx, commandAddress + 28, gcrControl))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_acquire_mem buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"engine={engine} cbdb=0x{cbDbOp:X8} gcr=0x{gcrControl:X8} base=0x{baseAddress:X16} size=0x{sizeBytes:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "1rZSWUv1IRc",
        ExportName = "sceAgcDcbCopyData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbCopyData(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var destinationSelector = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var destinationCachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var destinationAddress = ctx[CpuRegister.Rcx];
        var sourceAndEngineSelector = (uint)ctx[CpuRegister.R8];
        var sourceCachePolicy = (uint)ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var sourceAddress) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var countSelectRaw) ||
            !TryReadUInt64(ctx, stackAddress + (3 * sizeof(ulong)), out var writeConfirmRaw))
        {
            return ReturnPointer(ctx, 0);
        }

        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var countSelect = (uint)(countSelectRaw & 0xFF);
        var writeConfirm = (uint)(writeConfirmRaw & 0xFF);
        var control =
            ((sourceAndEngineSelector & 1u) << 30) |
            ((destinationCachePolicy & 3u) << 25) |
            ((writeConfirm & 1u) << 20) |
            ((countSelect & 1u) << 16) |
            ((sourceCachePolicy & 3u) << 13) |
            (((destinationSelector >> 1) & 0xFu) << 8) |
            ((sourceAndEngineSelector >> 1) & 0xFu);

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 6, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(6, ItCopyData, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, control) ||
            !ctx.TryWriteUInt64(commandAddress + 8, sourceAddress) ||
            !ctx.TryWriteUInt64(commandAddress + 16, destinationAddress))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_copy_data buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"src=0x{sourceAddress:X16} dst=0x{destinationAddress:X16} control=0x{control:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "i1jyy49AjXU",
        ExportName = "sceAgcDcbWriteData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbWriteData(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var destination = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var destinationAddress = ctx[CpuRegister.Rcx];
        var dataAddress = ctx[CpuRegister.R8];
        var dwordCount = (uint)ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var incrementRaw) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var writeConfirmRaw))
        {
            return ReturnPointer(ctx, 0);
        }

        var increment = (uint)(incrementRaw & 0xFF);
        var writeConfirm = (uint)(writeConfirmRaw & 0xFF);
        if (commandBufferAddress == 0 ||
            destinationAddress == 0 ||
            dataAddress == 0 ||
            dwordCount > 0x3FFD)
        {
            return ReturnPointer(ctx, 0);
        }

        var packetDwords = dwordCount + 4;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, RWriteData)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 4,
                destination | (cachePolicy << 8) | (increment << 16) | (writeConfirm << 24)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)destinationAddress) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(destinationAddress >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint index = 0; index < dwordCount; index++)
        {
            if (!TryReadUInt32(ctx, dataAddress + ((ulong)index * sizeof(uint)), out var value) ||
                !TryWriteUInt32(ctx, commandAddress + 16 + ((ulong)index * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        if (ShouldTraceHotPath(ref _dcbWriteDataTraceCount))
        {
            TraceAgc(
                $"agc.dcb_write_data buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
                $"dst={destination} cache={cachePolicy} addr=0x{destinationAddress:X16} count={dwordCount} " +
                $"increment={increment} confirm={writeConfirm}");
        }

        RefreshBuilderArenaCursorPassive(ctx, commandBufferAddress);
        return ReturnPointer(ctx, commandAddress);
    }

    // Extends the cursor cache over trailer packets built after a lap's last
    // release_mem, which would otherwise be dropped from the closed slice.
    private static void RefreshBuilderArenaCursorPassive(CpuContext ctx, ulong commandBufferAddress)
    {
        if (!_forceSubmitOrphanPreamblesEnabled)
        {
            return;
        }

        lock (_orphanPreambleGate)
        {
            if (!_knownBuilderHeaders.Contains(commandBufferAddress))
            {
                return;
            }
        }

        if (!TryReadUInt64(ctx, commandBufferAddress, out var arenaBase) ||
            arenaBase == 0 ||
            !TryReadUInt64(ctx, commandBufferAddress + 0x10, out var arenaCursor))
        {
            return;
        }

        lock (_orphanPreambleGate)
        {
            if (_builderArenaLastSeen.TryGetValue(commandBufferAddress, out var seen) &&
                seen.Base == arenaBase &&
                arenaCursor > seen.Cursor)
            {
                _builderArenaLastSeen[commandBufferAddress] =
                    (arenaBase, arenaCursor, GuestThreadExecution.CurrentGuestThreadHandle, System.Diagnostics.Stopwatch.GetTimestamp());
            }
        }
    }

    // Single-register variant of the SET_SH_REG builders: the register rides
    // in rsi as a packed struct (low 16 bits = register offset, high dword =
    // byte offset of this dword within a multi-dword register write) and the
    // value in edx. Emits the same 3-dword SET_SH_REG packet the plural
    // sceAgcCbSetShRegistersDirect path produces per run. Hades calls this
    // ~1k times per boot; leaving it unresolved corrupted its DCB stream.
    [SysAbiExport(
        Nid = "pFLArOT53+w",
        ExportName = "sceAgcDcbSetShRegisterDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc",
        PreferLle = true)]
    public static int DcbSetShRegisterDirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var packedRegister = ctx[CpuRegister.Rsi];
        var value = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var offset = (uint)(packedRegister & 0xFFFFu) + (uint)((packedRegister >> 32) >> 2);
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 3, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(3, ItSetShReg, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, offset & 0xFFFFu) ||
            !TryWriteUInt32(ctx, commandAddress + 8, value))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_sh_register_direct buf=0x{commandBufferAddress:X16} reg=0x{offset:X4} value=0x{value:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    // Size probe for the wait-on-address writer below: same argument prefix
    // minus the command buffer, returns the byte size the writer will emit so
    // the game can reserve DCB space (7 dwords for a standard WAIT_REG_MEM,
    // 6/9 for the 32/64-bit polled-NOP forms).
    [SysAbiExport(
        Nid = "43WJ08sSugE",
        ExportName = "sceAgcDcbWaitOnAddressGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc",
        PreferLle = true)]
    public static int DcbWaitOnAddressGetSize(CpuContext ctx)
    {
        var size = (uint)(ctx[CpuRegister.Rdi] & 0xFF);
        var operation = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var packetDwords = operation is 2 or 3 ? 7u : size == 0 ? 6u : 9u;
        ctx[CpuRegister.Rax] = packetDwords * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "VmW0Tdpy420",
        ExportName = "sceAgcDcbWaitRegMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbWaitRegMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var size = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var compareFunction = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var operation = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.R8] & 0xFF);
        var address = ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var reference) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var mask) ||
            !TryReadUInt32(ctx, stackAddress + (3 * sizeof(ulong)), out var pollCycles))
        {
            return ReturnPointer(ctx, 0);
        }

        if (commandBufferAddress == 0 ||
            size > 1 ||
            compareFunction > 7 ||
            operation > 4 ||
            cachePolicy > 3)
        {
            return ReturnPointer(ctx, 0);
        }

        var packetDwords = size == 0 ? 7u : 9u;
        var packetRegister = size == 0 ? RWaitMem32 : RWaitMem64;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, packetRegister)) ||
                 !TryWriteUInt32(ctx, commandAddress + 4, (uint)address & (size == 0 ? ~0x3u : ~0x7u)) ||
                 !TryWriteUInt32(ctx, commandAddress + 8, (uint)(address >> 32) & 0x3FFFFu) ||
                 !TryWriteUInt32(ctx, commandAddress + 12, (uint)mask))
        {
            return ReturnPointer(ctx, 0);
        }
        else if (size == 0)
        {
            if (!TryWriteUInt32(ctx, commandAddress + 16, (uint)reference) ||
                !TryWriteUInt32(ctx, commandAddress + 20, EncodeWaitRegMem32Control(compareFunction, operation, cachePolicy)) ||
                !TryWriteUInt32(ctx, commandAddress + 24, EncodeWaitRegMemPoll(pollCycles)))
            {
                return ReturnPointer(ctx, 0);
            }
        }
        else if (!TryWriteUInt32(ctx, commandAddress + 16, (uint)(mask >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 20, (uint)reference) ||
                 !TryWriteUInt32(ctx, commandAddress + 24, (uint)(reference >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 28, EncodeWaitRegMem64Control(compareFunction, operation, cachePolicy)) ||
                 !TryWriteUInt32(ctx, commandAddress + 32, EncodeWaitRegMemPoll(pollCycles)))
        {
            return ReturnPointer(ctx, 0);
        }

        if (ShouldTraceHotPath(ref _dcbWaitRegMemTraceCount))
        {
            TraceAgc(
                $"agc.dcb_wait_reg_mem buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
                $"size={size} compare={compareFunction} op={operation} cache={cachePolicy} " +
                $"addr=0x{address:X16} ref=0x{reference:X16} mask=0x{mask:X16} poll={pollCycles}");
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "u2T2DiA5hRI",
        ExportName = "sceAgcDcbStallCommandBufferParser",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbStallCommandBufferParser(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var size = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var address = ctx[CpuRegister.Rdx];
        var reference = ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0 || size > 1 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RZero)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        // Direct execution submits work synchronously, so there is no independent
        // hardware command processor to stall. Keep a well-formed no-op in the DCB
        // so packet addresses and the command-buffer cursor remain coherent.
        TraceAgc(
            $"agc.dcb_stall_parser buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"size={size} addr=0x{address:X16} reference=0x{reference:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "+u6dKSLWM2o",
        ExportName = "sceAgcDcbStallCommandBufferParserGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbStallCommandBufferParserGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 2u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "WmAc2MEj6Io",
        ExportName = "sceAgcDcbDmaData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDmaData(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var destination = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var destinationCachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var source = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var destinationAddress = ctx[CpuRegister.R8];
        var sourceCachePolicy = (uint)(ctx[CpuRegister.R9] & 0xFF);
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var control4Raw) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var sourceAddress) ||
            !TryReadUInt32(ctx, stackAddress + (3 * sizeof(ulong)), out var byteCount) ||
            !TryReadUInt64(ctx, stackAddress + (4 * sizeof(ulong)), out var control7Raw) ||
            !TryReadUInt64(ctx, stackAddress + (5 * sizeof(ulong)), out var control8Raw) ||
            !TryReadUInt64(ctx, stackAddress + (6 * sizeof(ulong)), out var control9Raw))
        {
            return ReturnPointer(ctx, 0);
        }

        if (commandBufferAddress == 0 || byteCount == 0 || (byteCount & 3) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var control4 = (uint)(control4Raw & 0xFF);
        var control7 = (uint)(control7Raw & 0xFF);
        var control8 = (uint)(control8Raw & 0xFF);
        var control9 = (uint)(control9Raw & 0xFF);
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RDmaData)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 4,
                destination |
                (destinationCachePolicy << 8) |
                (source << 16) |
                (sourceCachePolicy << 24)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 8,
                control4 | (control7 << 8) | (control8 << 16) | (control9 << 24)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, byteCount) ||
            !ctx.TryWriteUInt64(commandAddress + 16, destinationAddress) ||
            !ctx.TryWriteUInt64(commandAddress + 24, sourceAddress))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_dma_data buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"dst=0x{destinationAddress:X16} src=0x{sourceAddress:X16} bytes={byteCount} " +
            $"control0=0x{destination | (destinationCachePolicy << 8) | (source << 16) | (sourceCachePolicy << 24):X8}");
        RefreshBuilderArenaCursorPassive(ctx, commandBufferAddress);
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "2ccJz9LQI+w",
        ExportName = "sceAgcDcbDmaDataGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDmaDataGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 8u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "-RnpfpxIhec",
        ExportName = "sceAgcAcbDmaData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbDmaData(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var sourceSelector = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var destinationSelector = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var destinationAddress = ctx[CpuRegister.Rcx];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var sourceOrImmediate) ||
            !TryReadUInt32(ctx, stackAddress + (2 * sizeof(ulong)), out var byteCount) ||
            commandBufferAddress == 0 ||
            byteCount == 0 ||
            byteCount > 256u * 1024u * 1024u ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 7, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(7, ItNop, RDmaData)) ||
            !ctx.TryWriteUInt64(commandAddress + 4, destinationAddress) ||
            !ctx.TryWriteUInt64(commandAddress + 12, sourceOrImmediate) ||
            !TryWriteUInt32(ctx, commandAddress + 20, byteCount) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 24,
                sourceSelector | (destinationSelector << 8)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "M0ttm8h7SKA",
        ExportName = "sceAgcAcbDmaDataGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbDmaDataGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 8u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "RmaJwLtc8rY",
        ExportName = "sceAgcDcbSetBaseIndirectArgs",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetBaseIndirectArgs(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var baseIndex = (uint)ctx[CpuRegister.Rsi];
        var address = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItSetBase, 0) | (baseIndex << 1)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 1) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)address & ~7u) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(address >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "CtB+A9-VxO0",
        ExportName = "sceAgcDcbDispatchIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDispatchIndirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dataOffset = (uint)ctx[CpuRegister.Rsi];
        var modifier = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 3, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(3, ItDispatchIndirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, dataOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (modifier & 0xA038u) | 0x41u))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "+kSrjIVxKFE",
        ExportName = "sceAgcDcbPushMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbPushMarker(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var markerAddress = ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0 ||
            !TryReadGuestCString(ctx, markerAddress, 4095, out var marker))
        {
            return ReturnPointer(ctx, 0);
        }

        var payloadDwords = Math.Max(((uint)marker.Length + 4) / 4, 1);
        var packetDwords = payloadDwords + 1;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, RPushMarker)))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint index = 0; index < payloadDwords; index++)
        {
            uint value = 0;
            for (uint byteIndex = 0; byteIndex < sizeof(uint); byteIndex++)
            {
                var markerIndex = (index * sizeof(uint)) + byteIndex;
                if (markerIndex < (uint)marker.Length)
                {
                    value |= (uint)marker[(int)markerIndex] << ((int)byteIndex * 8);
                }
            }

            if (!TryWriteUInt32(ctx, commandAddress + 4 + ((ulong)index * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "cpCILPya5Zk",
        ExportName = "sceAgcAcbPushMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbPushMarker(CpuContext ctx) => DcbPushMarker(ctx);

    [SysAbiExport(
        Nid = "H7uZqCoNuWk",
        ExportName = "sceAgcDcbPopMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbPopMarker(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RPopMarker)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "6mFxkVqdmbQ",
        ExportName = "sceAgcAcbPopMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbPopMarker(CpuContext ctx) => DcbPopMarker(ctx);

    [SysAbiExport(
        Nid = "IxYiarKlXxM",
        ExportName = "sceAgcDmaDataPatchSetDstAddressOrOffset",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DmaDataPatchSetDstAddressOrOffset(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var destinationAddress = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) ||
            op != ItNop ||
            register != RDmaData ||
            !TryReadUInt32(ctx, commandAddress, out var header))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var packetLength = ((header >> 16) & 0x3FFFu) + 2;
        var destinationOffset = packetLength == 7 ? 4UL : 16UL;
        return ctx.TryWriteUInt64(commandAddress + destinationOffset, destinationAddress)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    // SRC counterpart of sceAgcDmaDataPatchSetDstAddressOrOffset. Without this,
    // the source stays 0 and the DMA copy — often a label write — never runs.
    [SysAbiExport(
        Nid = "cdDRpqcFGbU",
        ExportName = "sceAgcDmaDataPatchSetSrcAddressOrOffsetOrImmediate",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DmaDataPatchSetSrcAddressOrOffsetOrImmediate(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var sourceValue = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) ||
            op != ItNop ||
            register != RDmaData ||
            !TryReadUInt32(ctx, commandAddress, out var header))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var packetLength = ((header >> 16) & 0x3FFFu) + 2;
        var sourceOffset = packetLength == 7 ? 12UL : 24UL;
        return ctx.TryWriteUInt64(commandAddress + sourceOffset, sourceValue)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "eAy8eGNsCuU",
        ExportName = "sceAgcWriteDataPatchSetCachePolicy",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WriteDataPatchSetCachePolicy(CpuContext ctx) =>
        PatchWriteDataControlByte(ctx, byteIndex: 1);

    [SysAbiExport(
        Nid = "tmy-+rBpspY",
        ExportName = "sceAgcWriteDataPatchSetDst",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WriteDataPatchSetDst(CpuContext ctx) =>
        PatchWriteDataControlByte(ctx, byteIndex: 0);

    [SysAbiExport(
        Nid = "fPSCdQxgpSw",
        ExportName = "sceAgcWriteDataPatchSetAddressOrOffset",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WriteDataPatchSetAddressOrOffset(CpuContext ctx)
    {
        // SDK revisions disagree on whether the packet or destination is the
        // first argument. Astro passes (destination, packet), while older
        // captures use (packet, destination), so identify the packet by its
        // header instead of hard-coding one ordering.
        var first = ctx[CpuRegister.Rdi];
        var second = ctx[CpuRegister.Rsi];
        ulong commandAddress;
        ulong destinationAddress;
        if (TryGetPacketIdentity(ctx, first, out var firstOp, out var firstRegister) &&
            firstOp == ItNop && firstRegister == RWriteData)
        {
            commandAddress = first;
            destinationAddress = second;
        }
        else if (TryGetPacketIdentity(ctx, second, out var secondOp, out var secondRegister) &&
                 secondOp == ItNop && secondRegister == RWriteData)
        {
            commandAddress = second;
            destinationAddress = first;
        }
        else
        {
            // Astro's SDK 9 ABI passes (address-or-offset, pointer-to-field)
            // rather than the whole packet. The field is already the packet's
            // 64-bit address payload, so patch it directly.
            if (second == 0 || !ctx.TryWriteUInt64(second, first))
            {
                return SetReturn(ctx, second == 0
                    ? OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT
                    : OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            TraceAgc(
                $"agc.patch_write_data_field field=0x{second:X16} value=0x{first:X16}");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        TraceAgc(
            $"agc.patch_write_data_addr cmd=0x{commandAddress:X16} dst=0x{destinationAddress:X16}");
        return ctx.TryWriteUInt64(commandAddress + 8, destinationAddress)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "3KDcnM3lrcU",
        ExportName = "sceAgcWaitRegMemPatchAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WaitRegMemPatchAddress(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var fieldOffset = op == ItWaitRegMem
            ? 8UL
            : op == ItNop && register is RWaitMem32 or RWaitMem64
                ? 4UL
                : 0;
        if (fieldOffset == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var wrote = op == ItNop && register is RWaitMem32 or RWaitMem64
            ? TryWriteUInt32(
                  ctx,
                  commandAddress + fieldOffset,
                  (uint)address & (register == RWaitMem32 ? ~0x3u : ~0x7u)) &&
              TryWriteUInt32(ctx, commandAddress + fieldOffset + 4, (uint)(address >> 32) & 0x3FFFFu)
            : ctx.TryWriteUInt64(commandAddress + fieldOffset, address);
        return wrote
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "n485EBnIWmk",
        ExportName = "sceAgcWaitRegMemPatchCompareFunction",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WaitRegMemPatchCompareFunction(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var compareFunction = (uint)ctx[CpuRegister.Rsi];
        if (compareFunction > 7 ||
            !TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var fieldOffset = op == ItWaitRegMem
            ? 4UL
            : op == ItNop && register == RWaitMem32
                ? 20UL
                : op == ItNop && register == RWaitMem64
                    ? 28UL
                    : 0;
        return fieldOffset != 0 &&
               TryPatchUInt32Bits(ctx, commandAddress + fieldOffset, 0x7u, compareFunction)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, fieldOffset == 0
                ? OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT
                : OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "7nOoijNPvEU",
        ExportName = "sceAgcWaitRegMemPatchReference",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WaitRegMemPatchReference(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var reference = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var wrote = op == ItWaitRegMem
            ? TryWriteUInt32(ctx, commandAddress + 16, (uint)reference)
            : op == ItNop && register == RWaitMem32
                ? TryWriteUInt32(ctx, commandAddress + 16, (uint)reference)
                : op == ItNop && register == RWaitMem64 &&
                  ctx.TryWriteUInt64(commandAddress + 20, reference);
        return wrote
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, op == ItWaitRegMem ||
                             (op == ItNop && register is RWaitMem32 or RWaitMem64)
                ? OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT
                : OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [SysAbiExport(
        Nid = "hXAnLgDHCoI",
        ExportName = "sceAgcWaitRegMemPatchMask",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WaitRegMemPatchMask(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var mask = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var wrote = op == ItWaitRegMem
            ? TryWriteUInt32(ctx, commandAddress + 20, (uint)mask)
            : op == ItNop && register == RWaitMem32
                ? TryWriteUInt32(ctx, commandAddress + 12, (uint)mask)
                : op == ItNop && register == RWaitMem64 &&
                  ctx.TryWriteUInt64(commandAddress + 12, mask);
        return wrote
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, op == ItWaitRegMem ||
                             (op == ItNop && register is RWaitMem32 or RWaitMem64)
                ? OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT
                : OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [SysAbiExport(
        Nid = "0fWWK5uG9rQ",
        ExportName = "sceAgcQueueEndOfPipeActionPatchAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int QueueEndOfPipeActionPatchAddress(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) ||
            op != ItNop ||
            register != RReleaseMem)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(commandAddress + 12, address)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "J8YCgfKAMQs",
        ExportName = "sceAgcQueueEndOfPipeActionPatchGcrCntl",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int QueueEndOfPipeActionPatchGcrCntl(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        if (!IsAgcReleaseMemPacket(ctx, commandAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return TryPatchUInt32Bits(
                ctx,
                commandAddress + 8,
                0x0000_FFFFu,
                (uint)ctx[CpuRegister.Rsi] & 0xFFFFu)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "MlEw1feXcjg",
        ExportName = "sceAgcQueueEndOfPipeActionPatchData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int QueueEndOfPipeActionPatchData(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        if (!IsAgcReleaseMemPacket(ctx, commandAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(commandAddress + 20, ctx[CpuRegister.Rsi])
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "T9fjQIINoeE",
        ExportName = "sceAgcQueueEndOfPipeActionPatchType",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int QueueEndOfPipeActionPatchType(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var dataSelection = (uint)ctx[CpuRegister.Rsi];
        TraceAgc(
            $"agc.eop_patch_type cmd=0x{commandAddress:X16} value=0x{dataSelection:X8}");
        if (dataSelection > 3 || !IsAgcReleaseMemPacket(ctx, commandAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return TryPatchUInt32Bits(
                ctx,
                commandAddress + 8,
                0x00FF_0000u,
                dataSelection << 16)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool IsAgcReleaseMemPacket(CpuContext ctx, ulong commandAddress) =>
        TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) &&
        op == ItNop &&
        register == RReleaseMem;

    private static bool TryPatchUInt32Bits(
        CpuContext ctx,
        ulong address,
        uint mask,
        uint value)
    {
        return TryReadUInt32(ctx, address, out var current) &&
               TryWriteUInt32(ctx, address, PatchUInt32Bits(current, mask, value));
    }

    private static uint PatchUInt32Bits(uint current, uint mask, uint value) =>
        (current & ~mask) | (value & mask);

    [SysAbiExport(
        Nid = "l4fM9K-Lyks",
        ExportName = "sceAgcDcbSetIndexBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexBuffer(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexBufferAddress = ctx[CpuRegister.Rsi];
        var indexCount = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(3, ItIndexBase, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)(indexBufferAddress & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(indexBufferAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, Pm4(2, ItIndexBufferSize, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 16, indexCount))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_index_buffer buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} addr=0x{indexBufferAddress:X16} count={indexCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "B+aG9DUnTKA",
        ExportName = "sceAgcDcbDrawIndexOffset",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexOffset(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexOffset = (uint)ctx[CpuRegister.Rsi];
        var indexCount = (uint)ctx[CpuRegister.Rdx];
        var flags = (uint)ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItDrawIndexOffset2, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexCount) ||
            !TryWriteUInt32(ctx, commandAddress + 8, indexOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 12, indexCount) ||
            !TryWriteUInt32(ctx, commandAddress + 16, flags & 0xE000_0001u))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_draw_index_offset buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} offset={indexOffset} count={indexCount} flags=0x{flags:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "MWiElSNE8j8",
        ExportName = "sceAgcDcbWaitUntilSafeForRendering",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbWaitUntilSafeForRendering(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var videoOutHandle = (uint)ctx[CpuRegister.Rsi];
        var displayBufferIndex = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 7, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(7, ItNop, RWaitFlipDone)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, videoOutHandle) ||
            !TryWriteUInt32(ctx, commandAddress + 8, displayBufferIndex) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_wait_safe buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} handle={videoOutHandle} index={displayBufferIndex}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "YUeqkyT7mEQ",
        ExportName = "sceAgcDcbSetFlip",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetFlip(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var videoOutHandle = (uint)ctx[CpuRegister.Rsi];
        var displayBufferIndex = (int)ctx[CpuRegister.Rdx];
        var flipMode = (uint)ctx[CpuRegister.Rcx];
        var flipArg = unchecked((ulong)ctx[CpuRegister.R8]);
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 6, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(6, ItNop, RFlip)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, videoOutHandle) ||
            !TryWriteUInt32(ctx, commandAddress + 8, unchecked((uint)displayBufferIndex)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, flipMode) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(flipArg & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, (uint)(flipArg >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_flip buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} handle={videoOutHandle} index={displayBufferIndex} mode={flipMode} arg=0x{flipArg:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "w2rJhmD+dsE",
        ExportName = "sceAgcDriverAddEqEvent",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverAddEqEvent(CpuContext ctx)
    {
        var equeue = ctx[CpuRegister.Rdi];
        var eventId = ctx[CpuRegister.Rsi];
        var userData = ctx[CpuRegister.Rdx];
        if (!KernelEventQueueCompatExports.RegisterEvent(
                equeue,
                eventId,
                KernelEventQueueCompatExports.KernelEventFilterGraphics,
                userData))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        TraceAgc($"agc.driver_add_eq_event eq=0x{equeue:X16} id=0x{eventId:X16} udata=0x{userData:X16}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // Hands the game the driver-side context id its GPU event-queue packets
    // reference. We key events purely on (equeue, ident, filter), so a single
    // stable id satisfies the contract; the game only checks the call
    // succeeded and threads the id back through later driver calls.
    [SysAbiExport(
        Nid = "Zw7uUVPulbw",
        ExportName = "sceAgcDriverGetEqContextId",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver",
        PreferLle = true)]
    public static int DriverGetEqContextId(CpuContext ctx)
    {
        var contextIdAddress = ctx[CpuRegister.Rdi];
        if (contextIdAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, contextIdAddress, 1))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.driver_get_eq_context_id out=0x{contextIdAddress:X16} -> 1");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "DL2RXaXOy88",
        ExportName = "sceAgcDriverDeleteEqEvent",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverDeleteEqEvent(CpuContext ctx)
    {
        var equeue = ctx[CpuRegister.Rdi];
        var eventId = ctx[CpuRegister.Rsi];
        if (!KernelEventQueueCompatExports.DeleteRegisteredEvent(
                equeue,
                eventId,
                KernelEventQueueCompatExports.KernelEventFilterGraphics))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        TraceAgc($"agc.driver_delete_eq_event eq=0x{equeue:X16} id=0x{eventId:X16}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // Ghidra: libSceAgcDriver.sprx
    // SHA-256 bc2ca28f3632ce69e25ab44991ed1f49bc1624fe39c2fc81f2efc6e705876348,
    // public export 0x6FF0 -> selected Prospero callback 0x6F90 -> helper
    // 0x9C20. The base path clamps the requested size before the callback
    // validates the address and effective size, in that order.
    [SysAbiExport(
        Nid = "XlNp7jzGiPo",
        ExportName = "sceAgcDriverSetTFRing",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSetTfRing(CpuContext ctx)
    {
        var ringAddress = ctx[CpuRegister.Rdi];
        var requestedRingSize = (uint)ctx[CpuRegister.Rsi];
        var effectiveRingSize = Math.Min(requestedRingSize, AgcDriverTfRingMaximumSize);

        // GTA checks the pointer before entering the provider, so the direct
        // null-provider outcome is not present in the recovered path. Reject
        // it conservatively instead of recording a synthetic null ring.
        if (ringAddress == 0 ||
            (ringAddress & 0xFF) != 0 ||
            (effectiveRingSize & 3) != 0)
        {
            TraceAgc(
                $"agc.driver_set_tf_ring invalid addr=0x{ringAddress:X16} " +
                $"requested=0x{requestedRingSize:X} effective=0x{effectiveRingSize:X}");
            return ctx.SetReturn(AgcDriverErrorInvalidArgument);
        }

        var gpuState = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (gpuState.Gate)
        {
            gpuState.TfRingConfigured = true;
            gpuState.TfRingAddress = ringAddress;
            gpuState.TfRingSize = effectiveRingSize;
        }

        TraceAgc(
            $"agc.driver_set_tf_ring addr=0x{ringAddress:X16} " +
            $"requested=0x{requestedRingSize:X} effective=0x{effectiveRingSize:X}");
        return ctx.SetReturn(0);
    }

    internal static bool TryGetDriverTfRingState(
        ICpuMemory memory,
        out ulong ringAddress,
        out uint ringSize)
    {
        if (!_submittedGpuStates.TryGetValue(CanonicalMemory(memory), out var gpuState))
        {
            ringAddress = 0;
            ringSize = 0;
            return false;
        }

        lock (gpuState.Gate)
        {
            ringAddress = gpuState.TfRingAddress;
            ringSize = gpuState.TfRingSize;
            return gpuState.TfRingConfigured;
        }
    }

    // Ghidra: libSceAgcDriver.sprx
    // SHA-256 bc2ca28f3632ce69e25ab44991ed1f49bc1624fe39c2fc81f2efc6e705876348,
    // public export 0x70B0 -> selected Prospero callback 0x6FC0 -> helper
    // 0x9D00. The helper submits a four-byte payload with low16(second) at
    // offset 0 and low16(first) at offset 2, mapping driver failure to
    // 0x8A6DFFFF. GTA V calls this as (0, 0x1FF) immediately after TFRing.
    [SysAbiExport(
        Nid = "MM4IZSEYytQ",
        ExportName = "sceAgcDriverSetHsOffchipParam",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver",
        PreferLle = true)]
    public static int DriverSetHsOffchipParam(CpuContext ctx)
    {
        var requestedFirst = (uint)ctx[CpuRegister.Rdi];
        var requestedSecond = (uint)ctx[CpuRegister.Rsi];
        var first = (ushort)requestedFirst;
        var second = (ushort)requestedSecond;

        // The firmware call is stateful and can fail at the driver boundary.
        // Require the per-process AGC state established by the preceding setup
        // instead of creating state here and returning blind success.
        if (!_submittedGpuStates.TryGetValue(CanonicalMemory(ctx.Memory), out var gpuState))
        {
            TraceAgc(
                $"agc.driver_set_hs_offchip_param unavailable " +
                $"first=0x{requestedFirst:X8} second=0x{requestedSecond:X8}");
            return ctx.SetReturn(AgcDriverErrorInvalidArgument);
        }

        lock (gpuState.Gate)
        {
            gpuState.HsOffchipParamPayload = (uint)second | ((uint)first << 16);
            gpuState.HsOffchipParamConfigured = true;
        }

        TraceAgc(
            $"agc.driver_set_hs_offchip_param " +
            $"first=0x{requestedFirst:X8}->0x{first:X4} " +
            $"second=0x{requestedSecond:X8}->0x{second:X4}");
        return ctx.SetReturn(0);
    }

    internal static bool TryGetDriverHsOffchipParamState(
        ICpuMemory memory,
        out ushort first,
        out ushort second,
        out uint payload)
    {
        if (!_submittedGpuStates.TryGetValue(CanonicalMemory(memory), out var gpuState))
        {
            first = 0;
            second = 0;
            payload = 0;
            return false;
        }

        lock (gpuState.Gate)
        {
            payload = gpuState.HsOffchipParamPayload;
            first = (ushort)(payload >> 16);
            second = (ushort)payload;
            return gpuState.HsOffchipParamConfigured;
        }
    }

    [SysAbiExport(
        Nid = "UglJIZjGssM",
        ExportName = "sceAgcDriverSubmitDcb",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitDcb(CpuContext ctx)
    {
        Interlocked.Increment(ref _dcbSubmitCount);
        Volatile.Write(ref _lastDcbSubmitTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());

        var packetAddress = ctx[CpuRegister.Rdi];
        if (packetAddress == 0 ||
            !TryReadUInt64(ctx, packetAddress, out var commandAddress) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var dwordCount))
        {
            TraceAgc($"agc.driver_submit_dcb_rejected packet=0x{packetAddress:X16}");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var tracePackets = false;
        if (_traceAgc)
        {
            lock (_submitTraceGate)
            {
                tracePackets = _tracedDcbSizes.Add(dwordCount);
            }

            // Unconditional (unlike tracePackets above, not deduped by dwordCount):
            // every DriverSubmitDcb call's target address and size, so submission
            // history can be reconstructed even when most sizes repeat.
            TraceAgc($"agc.driver_submit_dcb_call addr=0x{commandAddress:X16} dwords={dwordCount}");
        }

        TraceAgc(
            $"agc.driver_submit_dcb packet=0x{packetAddress:X16} addr=0x{commandAddress:X16} " +
            $"dwords={dwordCount} end=0x{commandAddress + ((ulong)dwordCount * sizeof(uint)):X16}");

        GuestGpu.Current.AttachGuestMemory(ctx.Memory);
        TraceGuestMemoryCpuWriters(ctx, "dcb-before");
        RecordGameSubmittedRange(commandAddress, dwordCount);
        var gpuState = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (gpuState.Gate)
        {
            gpuState.Graphics.QueueName = "dcb.graphics";
            EnqueueSubmittedDcb(
                ctx,
                gpuState,
                gpuState.Graphics,
                commandAddress,
                dwordCount,
                ++gpuState.SubmissionSequence,
                tracePackets);
            DrainResumableDcbs(ctx, gpuState, tracePackets);
        }
        TraceGuestMemoryCpuWriters(ctx, "dcb-after");

        // No orphan-preamble drain here — this runs on a native guest worker
        // thread, where long managed work fail-fasts the runtime. The GPU
        // wait monitor drains this instead.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "gSRnr79F8tQ",
        ExportName = "sceAgcDriverSubmitAcb",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitAcb(CpuContext ctx)
    {
        var ownerHandle = (uint)ctx[CpuRegister.Rdi];
        var packetAddress = ctx[CpuRegister.Rsi];
        if (packetAddress == 0 ||
            !TryReadUInt64(ctx, packetAddress, out var commandAddress) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var dwordCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var tracePackets = false;
        if (_traceAgc)
        {
            lock (_submitTraceGate)
            {
                tracePackets = _tracedDcbSizes.Add(dwordCount);
            }

            // Unconditional (unlike tracePackets above, not deduped by dwordCount):
            // every DriverSubmitAcb call's target address and size.
            TraceAgc(
                $"agc.driver_submit_acb_call owner={ownerHandle} addr=0x{commandAddress:X16} dwords={dwordCount}");
        }

        TraceAgc(
            $"agc.driver_submit_acb owner={ownerHandle} packet=0x{packetAddress:X16} " +
            $"addr=0x{commandAddress:X16} dwords={dwordCount} " +
            $"end=0x{commandAddress + ((ulong)dwordCount * sizeof(uint)):X16}");

        GuestGpu.Current.AttachGuestMemory(ctx.Memory);
        TraceGuestMemoryCpuWriters(ctx, "acb-before");
        RecordGameSubmittedRange(commandAddress, dwordCount);
        var gpuState = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (gpuState.Gate)
        {
            if (!gpuState.ComputeQueues.TryGetValue(ownerHandle, out var queueState))
            {
                queueState = new SubmittedDcbState();
                gpuState.ComputeQueues.Add(ownerHandle, queueState);
            }

            queueState.QueueName = $"acb.compute[{ownerHandle}]";
            queueState.CompletionEventId = ownerHandle;
            EnqueueSubmittedDcb(
                ctx,
                gpuState,
                queueState,
                commandAddress,
                dwordCount,
                ++gpuState.SubmissionSequence,
                tracePackets);
            DrainResumableDcbs(ctx, gpuState, tracePackets);
        }
        TraceGuestMemoryCpuWriters(ctx, "acb-after");

        // See DriverSubmitDcb: orphan drains run only on the wait monitor
        // thread, never in this guest-thread import window.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static void TraceGuestMemoryCpuWriters(CpuContext ctx, string phase)
    {
        if (!_traceGuestMemoryCpuWrites ||
            _traceGuestMemoryRanges.Length == 0 ||
            !GuestImageWriteTracker.Enabled)
        {
            return;
        }

        GuestImageWriteTracker.FlushPendingDiagnostics();
        Span<byte> readable = stackalloc byte[1];
        Span<byte> sample = stackalloc byte[64];
        foreach (var range in _traceGuestMemoryRanges)
        {
            if (_guestMemoryCpuWriterOccurrences.TryGetValue(
                    range.Address,
                    out var previousOccurrences) &&
                previousOccurrences >= MaximumGuestMemoryCpuWriterOccurrences)
            {
                continue;
            }

            if (GuestImageWriteTracker.TryGetFirstCpuWriteInfo(
                    range.Address,
                    out var write))
            {
                var occurrence = _guestMemoryCpuWriterOccurrences.AddOrUpdate(
                    range.Address,
                    1,
                    static (_, current) => current + 1);
                var sampleLength = (int)Math.Min((ulong)sample.Length, range.Length);
                var sampleReadable = sampleLength != 0 &&
                    ctx.Memory.TryRead(range.Address, sample[..sampleLength]);
                var sampleBytes = sampleReadable
                    ? Convert.ToHexString(sample[..sampleLength])
                    : "unreadable";
                var context = write.Context;
                Console.Error.WriteLine(
                    $"[AGC][GUEST-MEMORY-CPU-WRITER] phase={phase} " +
                    $"occurrence={occurrence}/{MaximumGuestMemoryCpuWriterOccurrences} " +
                    $"range=0x{range.Address:X16}:0x{range.Length:X} " +
                    $"write=0x{write.Address:X16} page=0x{write.Page:X16} " +
                    $"ip=0x{context.InstructionAddress:X16} " +
                    $"rax=0x{context.Rax:X16} rbx=0x{context.Rbx:X16} " +
                    $"rcx=0x{context.Rcx:X16} rdx=0x{context.Rdx:X16} " +
                    $"rsi=0x{context.Rsi:X16} rdi=0x{context.Rdi:X16} " +
                    $"rsp=0x{context.Rsp:X16} rbp=0x{context.Rbp:X16} " +
                    $"r12=0x{context.R12:X16} r13=0x{context.R13:X16} " +
                    $"r14=0x{context.R14:X16} r15=0x{context.R15:X16} " +
                    $"stack=0x{context.Stack0:X16}/0x{context.Stack1:X16}/" +
                    $"0x{context.Stack2:X16}/0x{context.Stack3:X16}/" +
                    $"0x{context.Stack4:X16}/0x{context.Stack5:X16}/" +
                    $"0x{context.Stack6:X16}/0x{context.Stack7:X16} " +
                    $"sample={sampleBytes}");

                if (occurrence < MaximumGuestMemoryCpuWriterOccurrences)
                {
                    GuestImageWriteTracker.Track(
                        range.Address,
                        range.Length,
                        occurrence,
                        "agc-range-probe");
                }
                else
                {
                    GuestImageWriteTracker.Untrack(range.Address);
                }

                continue;
            }

            // The title allocations do not exist during the first submits.
            // Retry arming only after the range is readable, then leave it
            // protected until the first native or managed CPU write arrives.
            if (ctx.Memory.TryRead(range.Address, readable))
            {
                GuestImageWriteTracker.Track(
                    range.Address,
                    range.Length,
                    source: "agc-range-probe");
            }
        }
    }

    [SysAbiExport(
    Nid = "uJziRsODk1c",
    ExportName = "sceAgcDriverGetResourceRegistrationMaxNameLength",
    Target = Generation.Gen5,
    LibraryName = "libSceAgc")]
    public static int DriverGetResourceRegistrationMaxNameLength(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];

        if (outAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, outAddress, 256))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.driver_get_resource_registration_max_name_length out=0x{outAddress:X16} value=256");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private const uint DefaultAgcOwner = 1;
    [SysAbiExport(
        Nid = "F0ZXt5q0ZTA",
        ExportName = "sceAgcDriverGetDefaultOwner",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverGetDefaultOwner(CpuContext ctx)
    {
        var ownerAddress = ctx[CpuRegister.Rdi];

        if (ownerAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, ownerAddress, DefaultAgcOwner))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.driver_get_default_owner out=0x{ownerAddress:X16} owner={DefaultAgcOwner}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
    Nid = "W5z4eZrjEas",
    ExportName = "sceAgcDriverRegisterResource",
    Target = Generation.Gen5,
    LibraryName = "libSceAgc")]
    public static int DriverRegisterResource(CpuContext ctx)
    {
        var resourceAddress = ctx[CpuRegister.Rdi];
        var owner = (uint)ctx[CpuRegister.Rsi];
        var nameAddress = ctx[CpuRegister.Rdx];
        var type = (uint)ctx[CpuRegister.R8];
        var flags = (uint)ctx[CpuRegister.R9];

        TraceAgc(
            $"agc.driver_register_resource resource=0x{resourceAddress:X16} owner={owner} " +
            $"name=0x{nameAddress:X16} type={type} flags={flags}");

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // Synthetic label for an uncatalogued NID (the Unknown* convention); the NID is authoritative.
    #pragma warning disable SHEM006
    [SysAbiExport(
    Nid = "-KRzWekV120",
    ExportName = "sceAgcDriverUnknown_KRzWekV120",
    Target = Generation.Gen5,
    LibraryName = "libSceAgc")]
    public static int DriverUnknownKRzWekV120(CpuContext ctx)
    {
        TraceAgc(
            $"agc.driver_unknown_krz rdi=0x{ctx[CpuRegister.Rdi]:X16} " +
            $"rsi=0x{ctx[CpuRegister.Rsi]:X16} rdx=0x{ctx[CpuRegister.Rdx]:X16} " +
            $"rcx=0x{ctx[CpuRegister.Rcx]:X16} r8=0x{ctx[CpuRegister.R8]:X16} r9=0x{ctx[CpuRegister.R9]:X16}");

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }
    #pragma warning restore SHEM006

    [SysAbiExport(
        Nid = "h9z6+0hEydk",
        ExportName = "sceAgcSuspendPoint",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SuspendPoint(CpuContext ctx)
    {
        TraceAgc("agc.suspend_point");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Synthetic label for an uncatalogued NID (the Unknown* convention); the NID is authoritative.
    #pragma warning disable SHEM006
    [SysAbiExport(
        Nid = "qj7QZpgr9Uw",
        ExportName = "sceAgcUnknownQj7QZpgr9Uw",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int UnknownQj7QZpgr9Uw(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 1, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, 0x8000_0000))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.unknown_qj7 buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"arg1=0x{ctx[CpuRegister.Rsi]:X16} arg2=0x{ctx[CpuRegister.Rdx]:X16}");
        return ReturnPointer(ctx, commandAddress);
    }
    #pragma warning restore SHEM006

    private static void EnqueueSubmittedDcb(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong commandAddress,
        uint dwordCount,
        ulong submissionId,
        bool tracePackets)
    {
        state.PendingSubmissions.Enqueue(new SubmittedDcbState.PendingSubmission(
            commandAddress,
            dwordCount,
            submissionId,
            tracePackets));
        PumpSubmittedQueue(ctx, gpuState, state);
    }

    private static void PumpSubmittedQueue(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state)
    {
        if (state.IsSuspended)
        {
            // An explicit new submission supersedes a ring-tail park — the
            // game moved to a fresh ring, so abandon the park.
            if (state.RingTailParkAddress == 0 ||
                state.PendingSubmissions.Count == 0 ||
                !GpuWaitRegistry.TryRemoveByState(state, state.RingTailParkAddress))
            {
                return;
            }

            TraceAgc(
                $"agc.dcb.ring_tail_superseded addr=0x{state.RingTailParkAddress:X16} " +
                $"queue={state.QueueName} submission={state.ActiveSubmissionId}");
            // Keeps its full recorded extent so the arena sweep doesn't
            // double-run the tail once the game's own re-parse reaches it.
            state.RingTailParkAddress = 0;
            state.IsSuspended = false;
            state.HasActiveSubmission = false;
            NotifySubmittedDcbCompleted(gpuState, state, state.ActiveSubmissionId);
        }

        while (!state.HasActiveSubmission &&
               state.PendingSubmissions.TryDequeue(out var submission))
        {
            state.HasActiveSubmission = true;
            state.ActiveSubmissionId = submission.SubmissionId;
            state.RingChunkBase = state.IsForceSubmittedRing ? 0 : submission.CommandAddress;
            state.FollowedChunkAdvance = false;
            state.IsSuspended = ParseSubmittedDcb(
                ctx,
                gpuState,
                state,
                submission.CommandAddress,
                submission.DwordCount,
                submission.TracePackets);
            if (state.IsSuspended)
            {
                return;
            }

            state.HasActiveSubmission = false;
            NotifySubmittedDcbCompleted(gpuState, state, submission.SubmissionId);
        }
    }

    private static void NotifySubmittedDcbCompleted(
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong submissionId)
    {
        if (state.CompletionEventNotifiedSubmissionId == submissionId)
        {
            return;
        }

        state.CompletionEventNotifiedSubmissionId = submissionId;
        // Hardware raises an end-of-pipe interrupt for every submission on every
        // queue, so this is unconditional. It stays safe for titles that do not
        // want it because delivery is registration-gated: TriggerRegisteredEvents
        // only queues onto equeues that registered this exact ident through
        // sceAgcDriverAddEqEvent. Graphics keeps ident 0; a compute queue uses the
        // owner handle it was submitted under.
        var completionEventId = state.CompletionEventId;
        var isGraphics = ReferenceEquals(state, gpuState.Graphics);
        var queueName = state.QueueName;
        void TriggerCompletionEvents()
        {
            var triggered = KernelEventQueueCompatExports.TriggerRegisteredEvents(
                completionEventId,
                KernelEventQueueCompatExports.KernelEventFilterGraphics,
                completionEventId);
            // The broad fan-out wakes graphics registrations whose ident never
            // matches anything the driver publishes. That is a compatibility
            // guess rather than hardware behavior, so it stays opt-in and stays
            // on the graphics queue where it was measured.
            if (isGraphics && _compatibilitySubmitCompletionEvent)
            {
                triggered += KernelEventQueueCompatExports.TriggerRegisteredEventsDistinct(
                    KernelEventQueueCompatExports.KernelEventFilterGraphics);
            }
            TraceAgc(
                $"agc.completion_event queue={queueName} submission={submissionId} " +
                $"event=0x{completionEventId:X} queues={triggered}");
        }

        // A submission is complete only after its translated Vulkan work and
        // ordered guest-memory writes have finished. Put the notification on that
        // same logical queue instead of approximating completion with a timer or a
        // ThreadPool hop, either of which can only make the interrupt late and
        // reorder it against registration changes (and can wake Unity while its
        // upload data is still stale).
        if (GuestGpu.Current.SubmitOrderedGuestAction(
                TriggerCompletionEvents,
                $"agc submit completion {submissionId}") == 0)
        {
            TriggerCompletionEvents();
        }
    }

    // Returns true only when parsing stopped on an unsatisfied WAIT_REG_MEM.
    // Malformed packets are dropped as completed so one bad submission cannot
    // permanently wedge all later work on the same hardware queue.
    private static bool ParseSubmittedDcb(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong commandAddress,
        uint dwordCount,
        bool tracePackets)
    {
        if (!_traceGuestThroughput)
        {
            return ParseSubmittedDcbWithWindow(
                ctx,
                gpuState,
                state,
                commandAddress,
                dwordCount,
                tracePackets);
        }

        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return ParseSubmittedDcbWithWindow(
                ctx,
                gpuState,
                state,
                commandAddress,
                dwordCount,
                tracePackets);
        }
        finally
        {
            RecordAgcParseThroughput(
                dwordCount,
                System.Diagnostics.Stopwatch.GetTimestamp() - start);
        }
    }

    private static bool ParseSubmittedDcbWithWindow(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong commandAddress,
        uint dwordCount,
        bool tracePackets)
    {
        if (commandAddress == 0 || dwordCount == 0 || dwordCount > 1_000_000)
        {
            return false;
        }

        using var guestQueueScope = GuestGpu.Current.EnterGuestQueue(
            state.QueueName,
            state.ActiveSubmissionId);
        // A submission is one link of a chain, not necessarily the whole stream:
        // when a title's command arena fills mid-frame it continues in a fresh
        // buffer and links the two with an INDIRECT_BUFFER packet, then submits
        // only the first link. Stopping at the end of the submitted window drops
        // every packet past the switch -- including the flip and the end-of-frame
        // completion labels the guest is waiting on.
        for (var chainDepth = 0; ; chainDepth++)
        {
            if (chainDepth > MaxSubmittedChainDepth)
            {
                TraceAgc(
                    $"agc.dcb_chain_depth_exceeded queue={state.QueueName} " +
                    $"submission={state.ActiveSubmissionId} addr=0x{commandAddress:X16}");
                return false;
            }

            state.PendingChainAddress = 0;
            state.PendingChainDwords = 0;
            state.LastParsedAddress = commandAddress;
            var windowByteCount = checked((int)(dwordCount * sizeof(uint)));
            var rented = GuestDataPool.Shared.Rent(windowByteCount);
            bool suspended;
            try
            {
                if (ctx.Memory.TryRead(commandAddress, rented.AsSpan(0, windowByteCount)))
                {
                    _dcbWindowBuffer = rented;
                    _dcbWindowStart = commandAddress;
                    _dcbWindowByteLength = windowByteCount;
                }

                suspended = ParseSubmittedDcbCore(
                    ctx,
                    gpuState,
                    state,
                    commandAddress,
                    dwordCount,
                    tracePackets);
            }
            finally
            {
                _dcbWindowBuffer = null;
                _dcbWindowByteLength = 0;
                GuestDataPool.Shared.Return(rented);
            }

            // Record only what was actually parsed, not the full declared
            // size — else the orphan sweep either starves a suspended
            // queue's remaining packets or double-runs ones it already ran.
            if (!state.IsForceSubmittedRing && state.LastParsedAddress > commandAddress)
            {
                var consumedDwords = (uint)Math.Min(
                    (state.LastParsedAddress - commandAddress) / sizeof(uint),
                    dwordCount);
                RecordGameSubmittedRange(commandAddress, consumedDwords);
            }

            if (suspended)
            {
                return true;
            }

            var chainAddress = state.PendingChainAddress;
            var chainDwords = state.PendingChainDwords;
            if (chainAddress == 0 || chainDwords == 0 || chainDwords > 1_000_000)
            {
                return false;
            }

            commandAddress = chainAddress;
            dwordCount = chainDwords;
        }
    }

    // Deep enough for a title that links one continuation buffer per frame,
    // shallow enough that a self-referencing chain cannot spin forever.
    private const int MaxSubmittedChainDepth = 64;

    private static bool ParseSubmittedDcbCore(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong commandAddress,
        uint dwordCount,
        bool tracePackets)
    {
        var offset = 0u;
        while (offset < dwordCount)
        {
            var currentAddress = commandAddress + ((ulong)offset * sizeof(uint));
            if (!TryReadUInt32(ctx, currentAddress, out var header))
            {
                TracePacketParseFailure(state, currentAddress, offset, 0, "header-read");
                return false;
            }

            var packetType = header >> 30;
            if (packetType == 2)
            {
                if (tracePackets)
                {
                    TraceAgc(
                        $"agc.dcb.packet dw={offset} addr=0x{currentAddress:X16} " +
                        $"header=0x{header:X8} len=1 type=2");
                }

                offset++;
                continue;
            }

            if (header == 0 &&
                (state.FollowedChunkAdvance || state.IsForceSubmittedRing) &&
                _gpuWaitSuspendEnabled)
            {
                // Ring memory the game has not appended to yet — the bound the
                // CP's write pointer would impose. Park until it is written.
                return SuspendOnUnwrittenRingWord(
                    ctx, state, commandAddress, currentAddress, offset, tracePackets);
            }

            if (packetType != 3)
            {
                TracePacketParseFailure(
                    state,
                    currentAddress,
                    offset,
                    header,
                    $"packet-type-{packetType}");
                return false;
            }

            var length = Pm4Length(header);
            if (length == 0 || offset + length > dwordCount)
            {
                TracePacketParseFailure(
                    state,
                    currentAddress,
                    offset,
                    header,
                    $"length-{length}-remaining-{dwordCount - offset}");
                return false;
            }

            var op = (header >> 8) & 0xFFu;
            var register = (header >> 2) & 0x3Fu;
            if (!KnownPm4Opcodes.Contains(op) && _seenUnknownOpcodes.Add(op))
            {
                TryReadUInt32(ctx, currentAddress + 4, out var unknownPayload0);
                TryReadUInt32(ctx, currentAddress + 8, out var unknownPayload1);
                var possibleTarget = ((ulong)(unknownPayload1 & 0xFFFFu) << 32) | unknownPayload0;
                Console.Error.WriteLine(
                    $"[LOADER][WARN] agc.dcb.unknown_opcode op=0x{op:X2} reg=0x{register:X2} " +
                    $"len={length} addr=0x{currentAddress:X16} queue={state.QueueName} " +
                    $"payload0=0x{unknownPayload0:X8} payload1=0x{unknownPayload1:X8} " +
                    $"possible_target=0x{possibleTarget:X16}");
            }
            if (_traceFramePackets && ReferenceEquals(state, gpuState.Graphics))
            {
                var packetKey = (op, op == ItNop ? register : uint.MaxValue);
                state.FramePacketCounts[packetKey] =
                    state.FramePacketCounts.TryGetValue(packetKey, out var packetCount)
                        ? packetCount + 1
                        : 1;
                state.FramePacketCount++;
            }
            if (tracePackets)
            {
                TraceSubmittedPacket(ctx, currentAddress, offset, header, length, op, register);
            }

            if (_traceDraws)
            {
                CountSubmittedOpcode(op, register);
            }

            if ((header & 1u) != 0 && state.PredicateSkip)
            {
                if (tracePackets)
                {
                    TraceAgc(
                        $"agc.dcb.predicated_skip queue={state.QueueName} " +
                        $"packet=0x{currentAddress:X16} op=0x{op:X2} len={length}");
                }

                offset += length;
                continue;
            }

            var isAcquireMem = op == ItNop && register == RAcquireMem && length >= 8;
            // Flush coalesced ACQUIRE_MEM only before packets that consume guest
            // resources (draw/dispatch/dma/flip). Flushing before every register
            // write produced a storm of tiny ordered actions during load.
            if (!isAcquireMem &&
                PacketRequiresPendingAcquireFlush(op, register, length))
            {
                FlushPendingAcquireInvalidation(ctx, state, tracePackets);
            }

            if (op == ItSetPredication)
            {
                ApplySubmittedPredication(ctx, state, currentAddress, length, tracePackets);
                offset += length;
                continue;
            }

            if (op == ItRewind && length >= 2)
            {
                if (HandleSubmittedRewind(
                        ctx,
                        state,
                        commandAddress,
                        currentAddress,
                        offset,
                        length,
                        dwordCount,
                        tracePackets))
                {
                    FlushPendingAcquireInvalidation(ctx, state, tracePackets);
                    return true; // suspended until RewindPatchSetRewindState
                }

                offset += length;
                continue;
            }

            if (op == ItIndirectBuffer &&
                length >= 4 &&
                TryReadUInt32(ctx, currentAddress + 4, out var chainLow) &&
                TryReadUInt32(ctx, currentAddress + 8, out var chainHigh) &&
                TryReadUInt32(ctx, currentAddress + 12, out var chainDwords))
            {
                var chainAddress = ((ulong)(chainHigh & 0xFFFFu) << 32) | chainLow;
                var chainLength = chainDwords & 0xFFFFFu;
                // Titles emit a zeroed INDIRECT_BUFFER as padding for a branch they
                // decided not to take. Only a populated one redirects the stream.
                if (chainAddress != 0 && chainLength != 0)
                {
                    state.PendingChainAddress = chainAddress;
                    state.PendingChainDwords = chainLength;
                    state.RingChunkBase = chainAddress;
                    TraceAgc(
                        $"agc.dcb_chain queue={state.QueueName} " +
                        $"submission={state.ActiveSubmissionId} " +
                        $"packet=0x{currentAddress:X16} " +
                        $"target=0x{chainAddress:X16} dwords={chainLength}");

                    // The link is a jump, not a call: whatever follows it in this
                    // buffer is unreachable padding.
                    return false;
                }

                // target=1, size=0 is the ring-chunk-advance sentinel: continue
                // at the next contiguous chunk. Distinct from padding (target=0).
                if (chainAddress == 1 && state.RingChunkBase != 0)
                {
                    var nextChunk = state.RingChunkBase + RingChunkBytes;
                    TraceAgc(
                        $"agc.dcb.chunk_advance from=0x{currentAddress:X16} " +
                        $"next=0x{nextChunk:X16}");

                    state.PendingChainAddress = nextChunk;
                    state.PendingChainDwords = RingChunkBytes / sizeof(uint);
                    state.RingChunkBase = nextChunk;
                    state.FollowedChunkAdvance = true;
                    return false;
                }
            }

            if (op == ItNop &&
                register is RDrawReset or RAcbReset &&
                length >= 2)
            {
                ResetSubmittedParserState(state);
                TraceAgc(
                    $"agc.queue_reset queue={state.QueueName} " +
                    $"submission={state.ActiveSubmissionId} " +
                    $"kind={(register == RDrawReset ? "draw" : "acb")} " +
                    $"packet=0x{currentAddress:X16}");
            }

            if (isAcquireMem)
            {
                ApplySubmittedAcquireMem(
                    ctx,
                    state,
                    currentAddress,
                    tracePackets);
            }

            if (op == ItSetShReg &&
                TryReadTextureDescriptor(ctx, currentAddress, length, out var texture))
            {
                state.PresenterTexture = texture;
            }

            ApplySubmittedRegisters(ctx, state, currentAddress, length, op, register);

            if (op == ItSetBase &&
                length >= 4 &&
                TryReadUInt32(ctx, currentAddress + 4, out var baseSelector) &&
                baseSelector == 1 &&
                TryReadUInt64(ctx, currentAddress + 8, out var indirectArgsAddress))
            {
                state.IndirectArgsAddress = indirectArgsAddress;
            }

            if (op == ItEventWrite &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + sizeof(uint), out var eventTypeRaw))
            {
                // IT_EVENT_WRITE has no interrupt selector on hardware; EOP
                // interrupts come from RELEASE_MEM only. Delivering kernel
                // events here would over-count completions.
                if (tracePackets)
                {
                    TraceAgc($"agc.dcb.event type=0x{eventTypeRaw & 0x3Fu:X2} queues=none");
                }
            }

            if (op == ItNop && register == RReleaseMem && length >= 7)
            {
                ApplySubmittedReleaseMem(ctx, gpuState, state, currentAddress, tracePackets);
            }

            if (op == ItReleaseMem && length >= 8)
            {
                ApplySubmittedStandardReleaseMem(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    tracePackets);
            }

            if (op == ItNop && register == RWriteData && length >= 4)
            {
                ApplySubmittedWriteData(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    length,
                    standardPacket: false,
                    tracePacket: tracePackets);
            }

            if (op == ItWriteData && length >= 4)
            {
                ApplySubmittedWriteData(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    length,
                    standardPacket: true,
                    tracePacket: tracePackets);
            }

            if (op == ItNop && register == RDmaData && length >= 7)
            {
                ApplySubmittedDmaData(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    compactLayout: length == 7,
                    tracePacket: tracePackets);
            }

            if (op == ItDmaData && length >= 7)
            {
                ApplySubmittedStandardDmaData(ctx, gpuState, state, currentAddress);
            }

            if (op == ItCopyData &&
                length >= 6 &&
                (_traceCopyData || _traceGuestMemoryRanges.Length != 0))
            {
                TraceSubmittedCopyData(ctx, state, currentAddress, length);
            }

            if (op == ItIndexBase &&
                length >= 3 &&
                TryReadUInt32(ctx, currentAddress + 4, out var indexBaseLo) &&
                TryReadUInt32(ctx, currentAddress + 8, out var indexBaseHi))
            {
                state.IndexBufferAddress =
                    indexBaseLo | ((ulong)indexBaseHi << 32);
            }

            if (op == ItIndexBufferSize &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var indexBufferCount))
            {
                state.IndexBufferCount = indexBufferCount;
            }

            if (op == ItNop &&
                register == RIndexCount &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var customIndexCount))
            {
                state.IndexBufferCount = customIndexCount;
            }

            if (op == ItIndexType &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var indexSize))
            {
                state.IndexSize = indexSize & 0x3;
            }

            if (op == ItNumInstances &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var instanceCount))
            {
                state.InstanceCount = Math.Max(instanceCount, 1);
            }

            if (op == ItNop &&
                register is RWaitMem32 or RWaitMem64 &&
                length >= (register == RWaitMem32 ? 6u : 9u))
            {
                if (HandleSubmittedWaitRegMem(
                        ctx, state, commandAddress, currentAddress, offset, length,
                        dwordCount, is64Bit: register == RWaitMem64, isStandard: false,
                        tracePackets))
                {
                    FlushPendingAcquireInvalidation(ctx, state, tracePackets);
                    return true; // DCB suspended until the awaited label is written
                }
            }

            if (op == ItWaitRegMem && length >= 7)
            {
                if (HandleSubmittedWaitRegMem(
                        ctx, state, commandAddress, currentAddress, offset, length,
                        dwordCount, is64Bit: false, isStandard: true, tracePackets))
                {
                    FlushPendingAcquireInvalidation(ctx, state, tracePackets);
                    return true; // DCB suspended until the awaited label is written
                }
            }

            if (TryReadSubmittedDrawCount(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    length,
                    op,
                    out var indexCount) &&
                indexCount != 0)
            {
                state.FrameDrawCount++;
                if (_traceAgcShader)
                {
                    lock (_submitTraceGate)
                    {
                        if (_tracedSubmittedDrawOpcodes.Add(op))
                        {
                            TraceAgcShader(
                                $"agc.draw_packet op=0x{op:X2} count={indexCount}");
                        }
                    }
                }

                var indexed = op is
                    ItDrawIndex2 or
                    ItDrawIndexOffset2 or
                    ItDrawIndexIndirect or
                    ItDrawIndexIndirectMulti;
                state.SawIndexedDraw |= indexed;
                TryTranslateGuestDraw(ctx, gpuState, state, indexCount, indexed);
            }

            if (op == ItNop &&
                register == RDrawIndexAuto &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var autoIndexCount) &&
                autoIndexCount != 0)
            {
                state.FrameDrawCount++;
                TryTranslateGuestDraw(
                    ctx,
                    gpuState,
                    state,
                    autoIndexCount,
                    indexed: false);
            }

            if (op is ItDispatchDirect or ItDispatchIndirect)
            {
                if (TryReadComputeDispatch(
                        ctx,
                        state,
                        currentAddress,
                        length,
                        op,
                        out var dispatch,
                        out _))
                {
                    state.FrameDispatchCount++;
                    ObserveComputeDispatch(ctx, gpuState, state, dispatch);
                }
            }

            if (op == ItNop &&
                register == RWaitFlipDone &&
                length >= 3 &&
                TryReadUInt32(ctx, currentAddress + 4, out var waitVideoOutHandle) &&
                TryReadUInt32(ctx, currentAddress + 8, out var waitDisplayBufferIndex))
            {
                var waitSequence = GuestGpu.Current.SubmitOrderedGuestFlipWait(
                    unchecked((int)waitVideoOutHandle),
                    unchecked((int)waitDisplayBufferIndex));
                TraceAgcShader(
                    $"agc.flip_wait_safe queue={state.QueueName} " +
                    $"submission={state.ActiveSubmissionId} " +
                    $"handle={waitVideoOutHandle} index={waitDisplayBufferIndex} " +
                    $"work_sequence={waitSequence}");
            }

            if (op == ItNop && register == RFlip && length >= 6)
            {
                TraceFramePacketSummary(state);
                SyncCpuWrittenGuestImages(ctx);
                if (!TryReadUInt32(ctx, currentAddress + 4, out var videoOutHandle) ||
                    !TryReadUInt32(ctx, currentAddress + 8, out var displayBufferIndexRaw) ||
                    !TryReadUInt32(ctx, currentAddress + 12, out var flipMode) ||
                    !TryReadUInt32(ctx, currentAddress + 16, out var flipArgLo) ||
                    !TryReadUInt32(ctx, currentAddress + 20, out var flipArgHi))
                {
                    return false;
                }

                var flipArg = unchecked((long)(((ulong)flipArgHi << 32) | flipArgLo));
                var displayBufferIndex = unchecked((int)displayBufferIndexRaw);
                var handle = unchecked((int)videoOutHandle);
                if (state.PendingTargetlessDraw is { } pendingComposite &&
                    VideoOutExports.TryGetDisplayBufferInfo(
                        handle,
                        displayBufferIndex,
                        out var pendingDisplayBuffer) &&
                    state.KnownRenderTargets.TryGetValue(
                        pendingDisplayBuffer.Address,
                        out var pendingDisplayTarget))
                {
                    var textures = CreateGuestDrawTextures(
                        ctx,
                        pendingComposite.Textures,
                        out _);
                    var globalMemoryBuffers =
                        CreateTranslatedDrawGlobalBuffers(pendingComposite);
                    var vertexBuffers =
                        CreateGuestVertexBuffers(pendingComposite.VertexInputs);
                    ProvideRenderTargetInitialData(ctx, pendingDisplayTarget);
                    SubmitNggComputePrepass(
                        pendingComposite,
                        textures,
                        globalMemoryBuffers);
                    GuestGpu.Current.SubmitOffscreenTranslatedDraw(
                        pendingComposite.PixelShader,
                        textures,
                        globalMemoryBuffers,
                        pendingComposite.AttributeCount,
                        [new GuestRenderTarget(
                            pendingDisplayTarget.Address,
                            pendingDisplayTarget.Width,
                            pendingDisplayTarget.Height,
                            pendingDisplayTarget.Format,
                            pendingDisplayTarget.NumberType,
                            ComponentSwap: pendingDisplayTarget.ComponentSwap)],
                        pendingComposite.VertexShader,
                        pendingComposite.Ngg is { } submittedNgg
                            ? checked(submittedNgg.OutputLayout.MaximumPrimitiveCount * 3)
                            : pendingComposite.VertexCount,
                        pendingComposite.Ngg is null
                            ? pendingComposite.InstanceCount
                            : 1,
                        pendingComposite.Ngg is null
                            ? pendingComposite.PrimitiveType
                            : 4,
                        pendingComposite.Ngg is null
                            ? pendingComposite.IndexBuffer
                            : null,
                        pendingComposite.Ngg is null
                            ? vertexBuffers
                            : null,
                        pendingComposite.RenderState,
                        pendingComposite.DepthTarget,
                        pendingComposite.PixelShaderAddress,
                        pendingComposite.BaseVertex);
                    TraceAgcShader(
                        $"agc.deferred_composite ps=0x{pendingComposite.PixelShaderAddress:X16} " +
                        $"src=0x{pendingComposite.Textures.FirstOrDefault()?.Descriptor.Address ?? 0:X16} " +
                        $"dst=0x{pendingDisplayTarget.Address:X16} " +
                        $"size={pendingDisplayTarget.Width}x{pendingDisplayTarget.Height}");
                    state.PendingTargetlessDraw = null;
                    state.TranslatedDraw = null;
                }

                if (VideoOutExports.TryGetDisplayBufferInfo(
                        handle,
                        displayBufferIndex,
                        out var cachedDisplayBuffer) &&
                    GuestGpu.Current.TrySubmitOrderedGuestImageFlip(
                        handle,
                        displayBufferIndex,
                        cachedDisplayBuffer.Address,
                        cachedDisplayBuffer.Width,
                        cachedDisplayBuffer.Height,
                        cachedDisplayBuffer.PitchInPixel))
                {
                    TraceDisplayBuffer(
                        handle,
                        displayBufferIndex,
                        cachedDisplayBuffer,
                        "gpu-cache");
                }
                else if (state.SawIndexedDraw &&
                    state.TranslatedDraw is { } translatedDraw &&
                    VideoOutExports.TryGetDisplayBufferInfo(
                        handle,
                        displayBufferIndex,
                        out var translatedDisplayBuffer))
                {
                    TraceDisplayBuffer(
                        handle,
                        displayBufferIndex,
                        translatedDisplayBuffer,
                        "draw-fallback");
                    var textures = CreateGuestDrawTextures(ctx, translatedDraw.Textures, out var fallbackTextureCount);
                    var globalMemoryBuffers =
                        CreateTranslatedDrawGlobalBuffersForPresent(ctx, translatedDraw);
                    SubmitNggComputePrepass(
                        translatedDraw,
                        textures,
                        globalMemoryBuffers);
                    GuestGpu.Current.SubmitTranslatedDraw(
                        translatedDraw.PixelShader,
                        textures,
                        globalMemoryBuffers,
                        translatedDisplayBuffer.Width,
                        translatedDisplayBuffer.Height,
                        translatedDraw.AttributeCount,
                        translatedDraw.Ngg is null
                            ? null
                            : translatedDraw.VertexShader,
                        translatedDraw.Ngg is { } submittedNgg
                            ? checked(submittedNgg.OutputLayout.MaximumPrimitiveCount * 3)
                            : 3,
                        instanceCount: 1,
                        primitiveType: 4);
                    TraceAgcShader(
                        $"agc.shader_present ps=0x{translatedDraw.PixelShaderAddress:X16} " +
                        $"spirv={translatedDraw.PixelShader.Payload.Length} textures={textures.Count} " +
                        $"global_buffers={globalMemoryBuffers.Count} " +
                        $"fallback={fallbackTextureCount} {translatedDisplayBuffer.Width}x{translatedDisplayBuffer.Height}");

                    for (var i = 0; i < translatedDraw.Textures.Count; i++)
                    {
                        var binding = translatedDraw.Textures[i];
                        var d = binding.Descriptor;

                        TraceAgcShader(
                            $"agc.present_desc[{i}] " +
                            $"addr=0x{d.Address:X16} " +
                            $"size={d.Width}x{d.Height} " +
                            $"fmt={d.Format} " +
                            $"num={d.NumberType} " +
                            $"type={d.Type} " +
                            $"tile={d.TileMode} " +
                            $"storage={binding.IsStorage}");
                    }
                }
                else if (state.SawIndexedDraw && state.PresenterTexture is { } sourceTexture)
                {
                    _ = TrySoftwarePresent(
                        ctx,
                        sourceTexture,
                        unchecked((int)videoOutHandle),
                        displayBufferIndex);
                }
                else if (state.SawIndexedDraw &&
                         state.GuestDrawKind != GuestDrawKind.None &&
                         VideoOutExports.TryGetDisplayBufferInfo(
                             handle,
                             displayBufferIndex,
                             out var displayBuffer))
                {
                    GuestGpu.Current.SubmitGuestDraw(
                        state.GuestDrawKind,
                        displayBuffer.Width,
                        displayBuffer.Height);
                }

                // A SetFlip reached via the orphan force-submit path is the
                // game's own packet, physically shared with a real queue's
                // ring — force-submitting it can race ahead of that queue's
                // natural parse and present it twice once the real queue
                // catches up. Only a genuine game queue may flip.
                if (!state.IsForceSubmittedRing)
                {
                    _ = VideoOutExports.SubmitFlipFromAgc(ctx, handle, displayBufferIndex, unchecked((int)flipMode), flipArg);
                }

                state.SawIndexedDraw = false;
                state.GuestDrawKind = GuestDrawKind.None;
                if (state.PendingTargetlessDraw is { } unusedPendingDraw)
                {
                    ReturnPooledDrawArrays(
                        unusedPendingDraw,
                        globals: true,
                        vertex: true,
                        index: true);
                    state.PendingTargetlessDraw = null;
                }
                state.TranslatedDraw = null;
            }

            offset += length;
            state.LastParsedAddress = commandAddress + (ulong)offset * sizeof(uint);
        }

        FlushPendingAcquireInvalidation(ctx, state, tracePackets);
        return false;
    }

    private static void RecordAgcParseThroughput(uint dwordCount, long elapsedTicks)
    {
        Interlocked.Increment(ref _agcThroughputParseCalls);
        Interlocked.Add(ref _agcThroughputParseDwords, dwordCount);
        Interlocked.Add(ref _agcThroughputParseTicks, elapsedTicks);
        var observedMax = Volatile.Read(ref _agcThroughputParseMaxTicks);
        while (elapsedTicks > observedMax)
        {
            var previous = Interlocked.CompareExchange(
                ref _agcThroughputParseMaxTicks,
                elapsedTicks,
                observedMax);
            if (previous == observedMax)
            {
                break;
            }

            observedMax = previous;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var windowStart = Volatile.Read(ref _agcThroughputWindowStartTicks);
        var frequency = System.Diagnostics.Stopwatch.Frequency;
        if (now - windowStart < frequency ||
            Interlocked.CompareExchange(
                ref _agcThroughputWindowStartTicks,
                now,
                windowStart) != windowStart)
        {
            return;
        }

        var calls = Interlocked.Exchange(ref _agcThroughputParseCalls, 0);
        var dwords = Interlocked.Exchange(ref _agcThroughputParseDwords, 0);
        var parseTicks = Interlocked.Exchange(ref _agcThroughputParseTicks, 0);
        var maxTicks = Interlocked.Exchange(ref _agcThroughputParseMaxTicks, 0);
        var frequencyDouble = (double)frequency;
        Console.Error.WriteLine(
            $"[AGC][TRACE] agc.guest_throughput " +
            $"window_ms={(now - windowStart) * 1000.0 / frequencyDouble:F1} " +
            $"parse_calls={calls} dwords={dwords} " +
            $"parse_ms={parseTicks * 1000.0 / frequencyDouble:F1} " +
            $"parse_avg_ms={(calls == 0 ? 0.0 : parseTicks * 1000.0 / frequencyDouble / calls):F3} " +
            $"parse_max_ms={maxTicks * 1000.0 / frequencyDouble:F3}");
    }

    private static void TraceFramePacketSummary(SubmittedDcbState state)
    {
        if (!_traceFramePackets)
        {
            return;
        }

        var flip = ++state.FlipCount;
        if (flip <= 8 || flip % 60 == 0 || state.FrameDrawCount == 0)
        {
            var opcodes = string.Join(
                ',',
                state.FramePacketCounts
                    .OrderByDescending(entry => entry.Value)
                    .ThenBy(entry => entry.Key.Op)
                    .Take(32)
                    .Select(entry => entry.Key.Register == uint.MaxValue
                        ? $"0x{entry.Key.Op:X2}:{entry.Value}"
                        : $"0x{entry.Key.Op:X2}/r{entry.Key.Register}:{entry.Value}"));
            Console.Error.WriteLine(
                $"[FRAMEPKT] flip={flip} submission={state.ActiveSubmissionId} " +
                $"packets={state.FramePacketCount} draws={state.FrameDrawCount} " +
                $"dispatches={state.FrameDispatchCount} opcodes=[{opcodes}]");
        }

        state.FramePacketCounts.Clear();
        state.FramePacketCount = 0;
        state.FrameDrawCount = 0;
        state.FrameDispatchCount = 0;
    }

    private static void TracePacketParseFailure(
        SubmittedDcbState state,
        ulong address,
        uint offset,
        uint header,
        string reason)
    {
        if (!_traceFramePackets ||
            Interlocked.Increment(ref _packetParseFailureTraceCount) > 128)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[FRAMEPKT] parse-failure queue={state.QueueName} " +
            $"submission={state.ActiveSubmissionId} offset={offset} " +
            $"address=0x{address:X16} header=0x{header:X8} reason={reason}");
    }

    private static void TraceDisplayBuffer(
        int handle,
        int index,
        VideoOutExports.DisplayBufferInfo buffer,
        string path)
    {
        lock (_submitTraceGate)
        {
            if (!_tracedDisplayBuffers.Add((handle, index, buffer.Address, path)))
            {
                return;
            }
        }

        TraceAgcShader(
            $"agc.display_buffer handle={handle} index={index} " +
            $"addr=0x{buffer.Address:X16} fmt=0x{buffer.PixelFormat:X16} " +
            $"tile={buffer.TilingMode} size={buffer.Width}x{buffer.Height} " +
            $"pitch={buffer.PitchInPixel} path={path}");
    }

    private static void ApplySubmittedDmaData(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        bool compactLayout,
        bool tracePacket)
    {
        var byteCountOffset = compactLayout ? 20UL : 12UL;
        var destinationOffset = compactLayout ? 4UL : 16UL;
        var sourceOffset = compactLayout ? 12UL : 24UL;
        var controlOffset = compactLayout ? 24UL : 4UL;
        if (!TryReadUInt32(ctx, packetAddress + byteCountOffset, out var byteCount) ||
            !TryReadUInt64(ctx, packetAddress + destinationOffset, out var destinationAddress) ||
            !TryReadUInt64(ctx, packetAddress + sourceOffset, out var sourceAddress) ||
            !TryReadUInt32(ctx, packetAddress + controlOffset, out var control))
        {
            return;
        }

        // DCB packets store destination/source selectors in control bytes 0/2;
        // the compact ACB form stores source/destination in bytes 0/1. Selector
        // 2 is immediate data, matching PM4 DMA_DATA's DmaDataSrc::Data value.
        // Previously only compact packets were guessed to be immediate fills,
        // so Astro's DCB packet (control=0x01020300, src=4) tried to copy from
        // guest address 4 and silently left its GPU list header at zero.
        var destinationSelector = compactLayout
            ? (control >> 8) & 0xFFu
            : control & 0xFFu;
        var sourceSelector = compactLayout
            ? control & 0xFFu
            : (control >> 16) & 0xFFu;

        if (ShouldTraceGuestMemoryRange(destinationAddress, byteCount) &&
            Interlocked.Increment(ref _guestMemoryPacketProbeTraceCount) <= 256)
        {
            Console.Error.WriteLine(
                $"[AGC][GUEST-MEMORY-PACKET] kind=agc-dma " +
                $"packet=0x{packetAddress:X16} dst=0x{destinationAddress:X16} " +
                $"src=0x{sourceAddress:X16} bytes={byteCount} " +
                $"compact={(compactLayout ? 1 : 0)}");
        }

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                InvalidateDcbWindowIfOverlaps(destinationAddress, byteCount);
                var immediateFill =
                    sourceSelector == 2 &&
                    destinationSelector is 0 or 3 &&
                    destinationAddress >= 0x10000 &&
                    sourceAddress <= uint.MaxValue;
                var memoryCopy =
                    sourceSelector is 0 or 3 &&
                    destinationSelector is 0 or 3;
                var copied =
                    byteCount != 0 &&
                    byteCount <= 256u * 1024u * 1024u &&
                    destinationAddress != 0 &&
                    (immediateFill
                        ? TryFillGuestMemory(ctx, (uint)sourceAddress, destinationAddress, byteCount)
                        : memoryCopy &&
                          sourceAddress != 0 &&
                          TryCopyGuestMemory(ctx, sourceAddress, destinationAddress, byteCount));
                if (copied)
                {
                    MirrorDmaWriteToGuestImage(
                        ctx,
                        destinationAddress,
                        byteCount,
                        immediateFill ? (uint)sourceAddress : null,
                        immediateFill ? 0 : sourceAddress);
                }

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.dma_data dst=0x{destinationAddress:X16} " +
                        $"src=0x{sourceAddress:X16} bytes={byteCount} " +
                        $"src_sel={sourceSelector} dst_sel={destinationSelector} " +
                        $"fill={immediateFill} copied={copied}");
                }
            },
            $"agc_dma_data dst=0x{destinationAddress:X16} bytes={byteCount}",
            packetAddress,
            destinationAddress,
            byteCount,
            deferLabelCompletion: true);
    }

    private static bool PacketRequiresPendingAcquireFlush(
        uint op,
        uint register,
        uint length) =>
        op is ItDispatchDirect or ItDispatchIndirect ||
        op is ItDrawIndirect or
            ItDrawIndexIndirect or
            ItDrawIndexIndirectMulti or
            ItDrawIndex2 or
            ItDrawIndexAuto or
            ItDrawIndexMultiAuto or
            ItDrawIndexOffset2 ||
        op == ItDmaData ||
        (op == ItNop && register == RDmaData && length >= 7) ||
        (op == ItNop && register == RFlip && length >= 6) ||
        (op == ItNop && register == RDrawIndexAuto && length >= 2) ||
        (op == ItNop && register == RWaitFlipDone && length >= 3);

    private static void SubmitOrderedGpuSideEffect(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        Action action,
        string debugName,
        ulong packetAddress,
        ulong producerAddress = 0,
        ulong producerLength = 0,
        bool deferLabelCompletion = false)
    {
        var producer = RegisterLabelProducer(
            ctx.Memory,
            state,
            packetAddress,
            producerAddress,
            producerLength,
            debugName);

        void CompleteAndWake()
        {
            CompleteLabelProducer(producer);
            lock (gpuState.WaitMonitorSignalGate)
            {
                gpuState.WaitMonitorSignalVersion++;
                Monitor.Pulse(gpuState.WaitMonitorSignalGate);
            }

            // Resuming a DCB can enqueue and wait for another dispatch, never
            // reentrantly on the Vulkan render thread. Drains are coalesced —
            // hundreds of completions per frame each queueing an independent
            // full drain turns the shared Gate into a thundering herd — via
            // one request flag and a single re-looping worker.
            RequestResumableDcbDrain(ctx, gpuState);
        }

        void ApplyAndQueueCompletion()
        {
            var enqueuedBeforeAction =
                GuestGpu.Current.CurrentThreadEnqueuedGuestWorkSequenceForDiagnostics;
            action();
            // DMA side effects can enqueue a Vulkan image mirror while this
            // ordered action is executing. Completing the label here would
            // wake another queue before that mirror is visible. Queue a
            // second same-queue ordered action after all immediate follow-up
            // writes; it fences those writes before publishing the producer.
            // Most RELEASE_MEM/WRITE_DATA/EVENT_WRITE actions do not enqueue
            // any GPU work, though, so completing them here avoids doubling
            // every PM4 side-effect packet into two presenter work items.
            if (GuestGpu.Current.CurrentThreadEnqueuedGuestWorkSequenceForDiagnostics ==
                enqueuedBeforeAction)
            {
                CompleteAndWake();
                return;
            }

            if (GuestGpu.Current.SubmitOrderedGuestAction(
                    CompleteAndWake,
                    $"{debugName} completion") == 0)
            {
                CompleteAndWake();
            }
        }

        if (GuestGpu.Current.SubmitOrderedGuestAction(
                ApplyAndQueueCompletion,
                debugName) == 0)
        {
            // Headless/startup submissions have no Vulkan queue to order
            // against, so retaining the previous immediate behavior is exact.
            ApplyAndQueueCompletion();
        }
    }

    private static void RequestResumableDcbDrain(CpuContext ctx, SubmittedGpuState gpuState)
    {
        Volatile.Write(ref gpuState.PendingDrainContext, ctx);
        Interlocked.Exchange(ref gpuState.DrainPending, 1);
        if (Interlocked.CompareExchange(ref gpuState.DrainWorkerActive, 1, 0) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                static state => RunResumableDcbDrainWorker(state),
                gpuState,
                preferLocal: false);
        }
    }

    private static void RunResumableDcbDrainWorker(SubmittedGpuState gpuState)
    {
        while (true)
        {
            Interlocked.Exchange(ref gpuState.DrainPending, 0);
            if (Volatile.Read(ref gpuState.PendingDrainContext) is { } drainContext)
            {
                lock (gpuState.Gate)
                {
                    DrainResumableDcbs(drainContext, gpuState, tracePackets: _traceAgc);
                }
            }

            if (Volatile.Read(ref gpuState.DrainPending) != 0)
            {
                continue;
            }

            Volatile.Write(ref gpuState.DrainWorkerActive, 0);
            // A request may have slipped in between the pending check and the
            // hand-back; re-claim the duty unless another worker already did.
            if (Volatile.Read(ref gpuState.DrainPending) == 0 ||
                Interlocked.CompareExchange(ref gpuState.DrainWorkerActive, 1, 0) != 0)
            {
                return;
            }
        }
    }

    private static LabelProducerTrace? RegisterLabelProducer(
        object memory,
        SubmittedDcbState state,
        ulong packetAddress,
        ulong address,
        ulong length,
        string debugName)
    {
        if (address == 0 || length == 0)
        {
            return null;
        }

        memory = CanonicalMemory(memory);
        var producer = new LabelProducerTrace
        {
            Sequence = Interlocked.Increment(ref _labelProducerSequence),
            Memory = memory,
            Address = address,
            Length = length,
            PacketAddress = packetAddress,
            SubmissionId = state.ActiveSubmissionId,
            QueueName = state.QueueName,
            DebugName = debugName,
        };
        lock (_labelProducerGate)
        {
            if (_labelProducers.Count >= _labelProducerCompactionBound)
            {
                // Active producer records are synchronization state, not a
                // diagnostic cache. Removing one can hide an earlier
                // same-submission label write and make a valid in-stream fence
                // suspend forever. Compact only completed history; if all
                // records are active, correctness takes precedence over the
                // soft diagnostic bound.
                var removed = CompactCompletedEntries(
                    _labelProducers,
                    static candidate => candidate.Completed,
                    targetCount: LabelProducerSoftBound * 3 / 4);
                _labelProducerCompactionBound = removed == 0
                    ? _labelProducers.Count * 2
                    : LabelProducerSoftBound;
            }

            _labelProducers.Add(producer);
        }

        if (_traceAgc)
        {
            foreach (var waiting in GpuWaitRegistry.SnapshotInRange(memory, address, length))
            {
                TraceAgc(
                    $"agc.wait_producer_scheduled label=0x{waiting.Address:X16} " +
                    $"waiters={waiting.Count} producer_seq={producer.Sequence} " +
                    $"queue={producer.QueueName} submission={producer.SubmissionId} " +
                    $"packet=0x{packetAddress:X16} action='{debugName}'");
            }
        }

        return producer;
    }

    internal static int CompactCompletedEntries<T>(
        List<T> entries,
        Func<T, bool> isCompleted,
        int targetCount)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(isCompleted);
        targetCount = Math.Max(0, targetCount);

        // Single order-preserving pass. Removing one-by-one would shift the
        // tail on every eviction, which is quadratic on a list this size and
        // runs while the label gate is held.
        var removable = entries.Count - targetCount;
        var removed = 0;
        var write = 0;
        for (var read = 0; read < entries.Count; read++)
        {
            if (removed < removable && isCompleted(entries[read]))
            {
                removed++;
                continue;
            }

            entries[write++] = entries[read];
        }

        entries.RemoveRange(write, entries.Count - write);
        return removed;
    }

    private static void CompleteLabelProducer(LabelProducerTrace? producer)
    {
        if (producer is null)
        {
            return;
        }

        lock (_labelProducerGate)
        {
            producer.Completed = true;
        }

        if (_traceAgc)
        {
            foreach (var waiting in GpuWaitRegistry.SnapshotInRange(
                         producer.Memory,
                         producer.Address,
                         producer.Length))
            {
                TraceAgc(
                    $"agc.wait_producer_completed label=0x{waiting.Address:X16} " +
                    $"waiters={waiting.Count} producer_seq={producer.Sequence} " +
                    $"queue={producer.QueueName} submission={producer.SubmissionId} " +
                    $"action='{producer.DebugName}'");
            }
        }
    }

    private static void TraceWaitProducerState(
        object memory,
        in GpuWaitRegistry.WaitingDcb waiter,
        ulong commandAddress,
        ulong packetAddress,
        bool stale,
        ulong? currentValue = null)
    {
        memory = CanonicalMemory(memory);
        LabelProducerTrace? producer = null;
        lock (_labelProducerGate)
        {
            for (var index = _labelProducers.Count - 1; index >= 0; index--)
            {
                var candidate = _labelProducers[index];
                if (!ReferenceEquals(candidate.Memory, memory) ||
                    !RangesOverlap(
                        candidate.Address,
                        candidate.Length,
                        waiter.WaitAddress,
                        waiter.Is64Bit ? (ulong)sizeof(ulong) : sizeof(uint)))
                {
                    continue;
                }

                producer = candidate;
                break;
            }

            if (_tracedProducerlessWaits.Count >= 4096)
            {
                _tracedProducerlessWaits.Clear();
            }

            if (!stale)
            {
                // Count before the deduplication below: the warning fires once
                // per label, so on its own it cannot say how often a queue
                // actually suspends.
                GpuWaitProfile.RecordSuspend(producer is not null);
            }

            if (!stale && producer is null &&
                !_tracedProducerlessWaits.Add(
                    (memory, waiter.WaitAddress)))
            {
                return;
            }
        }

        // Producer-backed waits are trace-only. Keep the producer lookup above
        // because producerless waits are always warned, but do not build the
        // detailed condition strings when AGC tracing is disabled.
        if (producer is not null && !_traceAgc)
        {
            return;
        }

        var prefix = stale ? "agc.wait_stale" : "agc.wait_suspended";
        var current = currentValue.HasValue
            ? $"0x{currentValue.Value:X16}"
            : "unreadable";
        var condition =
            $"value={current} mask=0x{waiter.Mask:X16} " +
            $"ref=0x{waiter.ReferenceValue:X16} cmp={waiter.CompareFunction} " +
            $"control=0x{waiter.ControlValue:X8} bits={(waiter.Is64Bit ? 64 : 32)} " +
            $"form={(waiter.IsStandard ? "standard" : "agc-nop")}";
        if (producer is null)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] {prefix} label=0x{waiter.WaitAddress:X16} " +
                $"queue={waiter.QueueName} submission={waiter.SubmissionId} " +
                $"command=0x{commandAddress:X16} packet=0x{packetAddress:X16} " +
                condition + " " +
                "producer=none-observed; remaining-suspended");
            return;
        }

        TraceAgc(
            $"{prefix} label=0x{waiter.WaitAddress:X16} " +
            $"queue={waiter.QueueName} submission={waiter.SubmissionId} " +
            condition + " " +
            $"producer_seq={producer.Sequence} producer_state=" +
            $"{(producer.Completed ? "completed" : "queued")} " +
            $"producer_queue={producer.QueueName} " +
            $"producer_submission={producer.SubmissionId} " +
            $"producer_packet=0x{producer.PacketAddress:X16} " +
            $"action='{producer.DebugName}'");
    }

    private static void ApplySubmittedAcquireMem(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!TryDecodeSubmittedAcquireMem(ctx, packetAddress, out var acquire))
        {
            TraceAgc(
                $"agc.acquire_mem_decode_failed queue={state.QueueName} " +
                $"submission={state.ActiveSubmissionId} packet=0x{packetAddress:X16}");
            return;
        }

        // The bulk PM4 read is itself a parser-side cache. Do not retain it
        // across a guest cache-invalidation point.
        _dcbWindowBuffer = null;
        _dcbWindowByteLength = 0;

        if (!acquire.InvalidatesGuestResources)
        {
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.acquire_mem_skip_no_invalidate queue={state.QueueName} " +
                    $"submission={state.ActiveSubmissionId} packet=0x{packetAddress:X16} " +
                    $"gcr=0x{acquire.GcrControl:X8}");
            }

            return;
        }

        var size = acquire.CoversAllGuestMemory ? ulong.MaxValue : acquire.SizeBytes;
        NotePendingAcquireInvalidation(state, acquire.BaseAddress, size);

        if (tracePacket)
        {
            TraceAgc(
                $"agc.acquire_mem_coalesce queue={state.QueueName} " +
                $"submission={state.ActiveSubmissionId} packet=0x{packetAddress:X16} " +
                $"engine={acquire.Engine} cbdb=0x{acquire.CbDbControl:X8} " +
                $"base=0x{acquire.BaseAddress:X16} size=0x{acquire.SizeBytes:X16} " +
                $"scope={(acquire.CoversAllGuestMemory ? "all" : "range")} " +
                $"poll={acquire.PollInterval} gcr=0x{acquire.GcrControl:X8} " +
                $"pending_base=0x{state.PendingAcquireBase:X16} " +
                $"pending_size=0x{state.PendingAcquireSize:X16}");
        }
    }

    private static void NotePendingAcquireInvalidation(
        SubmittedDcbState state,
        ulong baseAddress,
        ulong sizeBytes)
    {
        if (!state.PendingAcquireInvalidation)
        {
            state.PendingAcquireInvalidation = true;
            state.PendingAcquireBase = baseAddress;
            state.PendingAcquireSize = sizeBytes;
            return;
        }

        if (state.PendingAcquireSize == ulong.MaxValue || sizeBytes == ulong.MaxValue)
        {
            state.PendingAcquireBase = 0;
            state.PendingAcquireSize = ulong.MaxValue;
            return;
        }

        var existingEnd = state.PendingAcquireBase > ulong.MaxValue - state.PendingAcquireSize
            ? ulong.MaxValue
            : state.PendingAcquireBase + state.PendingAcquireSize;
        var newEnd = baseAddress > ulong.MaxValue - sizeBytes
            ? ulong.MaxValue
            : baseAddress + sizeBytes;
        var mergedBase = Math.Min(state.PendingAcquireBase, baseAddress);
        var mergedEnd = Math.Max(existingEnd, newEnd);
        state.PendingAcquireBase = mergedBase;
        state.PendingAcquireSize = mergedEnd == ulong.MaxValue
            ? ulong.MaxValue
            : mergedEnd - mergedBase;
    }

    private static void FlushPendingAcquireInvalidation(
        CpuContext ctx,
        SubmittedDcbState state,
        bool tracePacket)
    {
        if (!state.PendingAcquireInvalidation)
        {
            return;
        }

        var baseAddress = state.PendingAcquireBase;
        var sizeBytes = state.PendingAcquireSize;
        state.PendingAcquireInvalidation = false;
        state.PendingAcquireBase = 0;
        state.PendingAcquireSize = 0;

        var queueName = state.QueueName;
        var submissionId = state.ActiveSubmissionId;
        var debugName =
            $"acquire_mem_flush base=0x{baseAddress:X16} size=0x{sizeBytes:X16}";
        void ApplyAcquire()
        {
            SyncCpuWrittenGuestImages(ctx, baseAddress, sizeBytes);
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.acquire_mem_applied queue={queueName} " +
                    $"submission={submissionId} " +
                    $"work_sequence={GuestGpu.Current.CurrentGuestWorkSequenceForDiagnostics} " +
                    $"base=0x{baseAddress:X16} size=0x{sizeBytes:X16}");
            }
        }

        var sequence = GuestGpu.Current.SubmitOrderedGuestAction(ApplyAcquire, debugName);
        if (sequence == 0)
        {
            ApplyAcquire();
        }
    }

    private static bool TryDecodeSubmittedAcquireMem(
        CpuContext ctx,
        ulong packetAddress,
        out SubmittedAcquireMem acquire)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var coherControl) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var sizeLow) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var sizeHigh) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var baseLow) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var baseHigh) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var pollInterval) ||
            !TryReadUInt32(ctx, packetAddress + 28, out var gcrControl))
        {
            acquire = default;
            return false;
        }

        acquire = DecodeSubmittedAcquireMem(
            coherControl,
            sizeLow,
            sizeHigh,
            baseLow,
            baseHigh,
            pollInterval,
            gcrControl);
        return true;
    }

    private static SubmittedAcquireMem DecodeSubmittedAcquireMem(
        uint coherControl,
        uint sizeLow,
        uint sizeHigh,
        uint baseLow,
        uint baseHigh,
        uint pollInterval,
        uint gcrControl)
    {
        // GFX10 ACQUIRE_MEM expresses COHER_SIZE and COHER_BASE in 256-byte
        // units. SIZE_HI is 8 bits and BASE_HI is 24 bits in the packet.
        var sizeUnits = sizeLow | ((ulong)(sizeHigh & 0xFFu) << 32);
        var baseUnits = baseLow | ((ulong)(baseHigh & 0x00FF_FFFFu) << 32);
        return new SubmittedAcquireMem(
            Engine: coherControl >> 31,
            CbDbControl: coherControl & 0x7FFF_FFFFu,
            BaseAddress: baseUnits << 8,
            SizeBytes: sizeUnits << 8,
            PollInterval: pollInterval & 0xFFFFu,
            GcrControl: gcrControl & 0x7FFFFu);
    }

    private static void ResetSubmittedParserState(SubmittedDcbState state)
    {
        // Queue ownership, pending submissions and suspension bookkeeping are
        // deliberately retained. Work emitted before this packet already owns
        // immutable snapshots; clearing these fields affects only commands
        // translated after RESET at this precise packet position.
        state.CxRegisters.Clear();
        state.ShRegisters.Clear();
        state.UcRegisters.Clear();
        state.PresenterTexture = null;
        state.GuestDrawKind = GuestDrawKind.None;
        state.TranslatedDraw = null;
        state.RenderTargetWriters.Clear();
        state.IndirectArgsAddress = 0;
        state.SawIndexedDraw = false;
        state.IndexBufferAddress = 0;
        state.IndexBufferCount = 0;
        state.IndexSize = 0;
        state.InstanceCount = 1;
        state.DrawIndexOffset = 0;
    }

    private static void ApplySubmittedPredication(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        bool tracePacket)
    {
        if (packetLength < 3 ||
            !TryReadUInt32(ctx, packetAddress + 4, out var first) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var second))
        {
            return;
        }

        const uint flagsMask = 0x0007_1100u;
        uint flags;
        ulong predicateAddress;
        if (packetLength >= 4 &&
            (first & ~flagsMask) == 0 &&
            TryReadUInt32(ctx, packetAddress + 12, out var third) &&
            third <= 0xFFFFu)
        {
            flags = first;
            predicateAddress = ((ulong)third << 32) | (second & 0xFFFF_FFF0u);
        }
        else
        {
            flags = second;
            predicateAddress = (first & 0xFFFF_FFF0u) | ((ulong)(second & 0xFFu) << 32);
        }

        var operation = (flags >> 16) & 0x7u;
        if (operation == 0)
        {
            state.PredicateSkip = false;
            return;
        }

        if (operation != 3)
        {
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.dcb.predication_unsupported packet=0x{packetAddress:X16} " +
                    $"op={operation} addr=0x{predicateAddress:X16}");
            }

            return;
        }

        var waitOperation = (flags >> 12) & 1u;
        var value = 0UL;
        var readSucceeded = false;
        void ReadPredicate() =>
            readSucceeded = ctx.TryReadUInt64(predicateAddress, out value);

        if (waitOperation != 0)
        {
            var sequence = GuestGpu.Current.SubmitOrderedGuestAction(
                ReadPredicate,
                $"set_predication read 0x{predicateAddress:X16}");
            if (sequence == 0)
            {
                ReadPredicate();
            }
            else if (!GuestGpu.Current.WaitForGuestWork(sequence))
            {
                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.predication_wait_failed packet=0x{packetAddress:X16} " +
                        $"addr=0x{predicateAddress:X16} sequence={sequence}");
                }

                return;
            }
        }
        else
        {
            ReadPredicate();
        }

        if (!readSucceeded)
        {
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.dcb.predication_read_failed packet=0x{packetAddress:X16} " +
                    $"addr=0x{predicateAddress:X16}");
            }

            return;
        }

        var condition = (flags >> 8) & 1u;
        state.PredicateSkip = condition == 0 ? value != 0 : value == 0;
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.predication packet=0x{packetAddress:X16} " +
                $"addr=0x{predicateAddress:X16} value=0x{value:X16} " +
                $"condition={condition} wait={waitOperation} skip={state.PredicateSkip}");
        }
    }

    private static bool RangesOverlap(
        ulong leftAddress,
        ulong leftLength,
        ulong rightAddress,
        ulong rightLength)
    {
        var leftEnd = leftAddress > ulong.MaxValue - leftLength
            ? ulong.MaxValue
            : leftAddress + leftLength;
        var rightEnd = rightAddress > ulong.MaxValue - rightLength
            ? ulong.MaxValue
            : rightAddress + rightLength;
        return leftAddress < rightEnd && rightAddress < leftEnd;
    }

    /// <summary>
    /// Mirrors guest-side DMA/CPU writes to a render target's surface into
    /// our separate Vulkan image once per flip (Dreaming Sarah's fog layer,
    /// Chowdren's fog-noise memset). Surfaces only the GPU writes are skipped.
    /// </summary>
    private static void SyncCpuWrittenGuestImages(
        CpuContext ctx,
        ulong scopeAddress = 0,
        ulong scopeByteCount = ulong.MaxValue)
    {
        // Uploads used to copy full planes here and SubmitGuestImageWrite on the
        // AGC producer thread, which hit the payload guest-work caps and
        // soft-locked titles (GTA). The presenter's render drain owns the
        // read/upload/re-arm; this call is only a scoped wake.
        _ = ctx;
        if (!SharpEmu.HLE.GuestImageWriteTracker.Enabled || scopeByteCount == 0)
        {
            return;
        }

        GuestGpu.Current.RequestCpuWrittenGuestImageSync(scopeAddress, scopeByteCount);
    }

    private static long _dmaMirrorTraceCount;
    private static readonly Dictionary<(uint Op, uint Register), long> _submittedOpcodeCounts = new();
    private static long _submittedOpcodeTotal;

    private static void CountSubmittedOpcode(uint op, uint register)
    {
        var key = (op, op == ItNop ? register : uint.MaxValue);
        lock (_submittedOpcodeCounts)
        {
            _submittedOpcodeCounts[key] =
                _submittedOpcodeCounts.TryGetValue(key, out var count) ? count + 1 : 1;
            if (++_submittedOpcodeTotal % 500_000 == 0)
            {
                var summary = string.Join(
                    ' ',
                    _submittedOpcodeCounts
                        .OrderByDescending(entry => entry.Value)
                        .Select(entry => entry.Key.Register == uint.MaxValue
                            ? $"0x{entry.Key.Op:X2}:{entry.Value}"
                            : $"0x{entry.Key.Op:X2}/r{entry.Key.Register}:{entry.Value}"));
                Console.Error.WriteLine($"[PKT] total={_submittedOpcodeTotal} {summary}");
            }
        }
    }

    private static void MirrorDmaWriteToGuestImage(
        CpuContext ctx,
        ulong destinationAddress,
        ulong byteCount,
        uint? fillValue,
        ulong sourceAddress = 0)
    {
        var hasImage = GuestGpu.Current.TryGetGuestImageExtent(
            destinationAddress,
            out var width,
            out var height,
            out var imageBytes);
        if (_traceDraws && Interlocked.Increment(ref _dmaMirrorTraceCount) <= 400)
        {
            Console.Error.WriteLine(
                $"[DMA] src=0x{sourceAddress:X} dst=0x{destinationAddress:X} bytes={byteCount} " +
                $"fill={(fillValue is { } f ? $"0x{f:X8}" : "copy")} image={hasImage}");
        }

        if (!hasImage)
        {
            return;
        }

        if (imageBytes == 0 || byteCount < imageBytes)
        {
            return;
        }

        if (fillValue is { } fill)
        {
            GuestGpu.Current.SubmitGuestImageFill(destinationAddress, fill);
            return;
        }

        // When the DMA source is itself a live guest image (e.g. a movie
        // frame just rendered by the NV12 conversion pass), its freshest
        // pixels exist only GPU-side; the guest CPU copy this DMA moved is
        // stale until a writeback lands. Copy image-to-image so the
        // destination sees what unified memory would hold on hardware.
        if (sourceAddress != 0 &&
            GuestGpu.Current.TrySubmitGuestImageCopy(sourceAddress, destinationAddress))
        {
            return;
        }

        var pixels = new byte[imageBytes];
        if (ctx.Memory.TryRead(destinationAddress, pixels))
        {
            GuestGpu.Current.SubmitGuestImageWrite(destinationAddress, pixels);
        }
    }

    private static void ApplySubmittedStandardDmaData(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var sourceLow) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var sourceHigh) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var destinationLow) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var destinationHigh) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var command))
        {
            return;
        }

        var byteCount = command & 0x1F_FFFFu;
        var destinationSelect = (control >> 20) & 0x3u;
        var destinationSwap = (command >> 24) & 0x3u;
        var destinationAddressSpace = (command >> 27) & 0x1u;
        var destinationAddress = destinationLow | ((ulong)destinationHigh << 32);
        var writesGuestMemory =
            byteCount != 0 &&
            destinationSwap == 0 &&
            destinationSelect is 0 or 3 &&
            (destinationSelect == 3 || destinationAddressSpace == 0);

        if (ShouldTraceGuestMemoryRange(destinationAddress, byteCount) &&
            Interlocked.Increment(ref _guestMemoryPacketProbeTraceCount) <= 256)
        {
            Console.Error.WriteLine(
                $"[AGC][GUEST-MEMORY-PACKET] kind=standard-dma " +
                $"packet=0x{packetAddress:X16} dst=0x{destinationAddress:X16} " +
                $"src=0x{sourceHigh:X8}{sourceLow:X8} bytes={byteCount} " +
                $"control=0x{control:X8} command=0x{command:X8} " +
                $"dst_sel={destinationSelect} dst_swap={destinationSwap} " +
                $"dst_space={destinationAddressSpace} " +
                $"writes_guest={(writesGuestMemory ? 1 : 0)}");
        }

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () => ApplySubmittedStandardDmaDataSnapshot(
                ctx,
                control,
                sourceLow,
                sourceHigh,
                destinationLow,
                destinationHigh,
                command),
            $"dma_data dst=0x{destinationHigh:X8}{destinationLow:X8} bytes={byteCount}",
            packetAddress,
            writesGuestMemory ? destinationAddress : 0,
            writesGuestMemory ? byteCount : 0,
            deferLabelCompletion: true);
    }

    private static void ApplySubmittedStandardDmaDataSnapshot(
        CpuContext ctx,
        uint control,
        uint sourceLow,
        uint sourceHigh,
        uint destinationLow,
        uint destinationHigh,
        uint command)
    {
        var byteCount = command & 0x1F_FFFFu;
        var sourceSelect = (control >> 29) & 0x3u;
        var destinationSelect = (control >> 20) & 0x3u;
        var destinationSwap = (command >> 24) & 0x3u;
        var sourceAddressSpace = (command >> 26) & 0x1u;
        var destinationAddressSpace = (command >> 27) & 0x1u;
        var sourceAddressIncrement = (command >> 28) & 0x1u;
        if (byteCount == 0 ||
            destinationSwap != 0 ||
            destinationSelect is not (0 or 3) ||
            (destinationSelect == 0 && destinationAddressSpace != 0))
        {
            return;
        }

        var destinationAddress =
            destinationLow | ((ulong)destinationHigh << 32);
        InvalidateDcbWindowIfOverlaps(destinationAddress, byteCount);
        bool copied;
        ulong sourceAddress;
        if (sourceSelect is 0 or 3 &&
            (sourceSelect == 3 || sourceAddressSpace == 0))
        {
            sourceAddress = sourceLow | ((ulong)sourceHigh << 32);
            if (sourceAddressIncrement != 0)
            {
                copied =
                    TryReadUInt32(ctx, sourceAddress, out var fillValue) &&
                    TryFillGuestMemory(
                        ctx,
                        fillValue,
                        destinationAddress,
                        byteCount);
                if (copied)
                {
                    MirrorDmaWriteToGuestImage(ctx, destinationAddress, byteCount, fillValue);
                }
            }
            else
            {
                copied = TryCopyGuestMemory(
                    ctx,
                    sourceAddress,
                    destinationAddress,
                    byteCount);
                if (copied)
                {
                    MirrorDmaWriteToGuestImage(
                        ctx,
                        destinationAddress,
                        byteCount,
                        fillValue: null,
                        sourceAddress);
                }
            }
        }
        else if (sourceSelect == 2)
        {
            sourceAddress = 0;
            copied = TryFillGuestMemory(
                ctx,
                sourceLow,
                destinationAddress,
                byteCount);
            if (copied)
            {
                MirrorDmaWriteToGuestImage(ctx, destinationAddress, byteCount, sourceLow);
            }
        }
        else
        {
            return;
        }

        if (ShouldTraceHotPath(ref _standardDmaTraceCount))
        {
            TraceAgcShader(
                $"agc.dma_packet dst=0x{destinationAddress:X16} " +
                $"src=0x{sourceAddress:X16} bytes={byteCount} " +
                $"src_sel={sourceSelect} fill={sourceAddressIncrement != 0 || sourceSelect == 2} " +
                $"copied={copied}");
        }
    }

    private static void ApplySubmittedWriteData(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        bool standardPacket,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var control) ||
            !TryReadUInt64(ctx, packetAddress + 8, out var destinationAddress))
        {
            return;
        }

        var (destination, incrementAddress, writeConfirm, cachePolicy) = standardPacket
            ? DecodeStandardWriteDataControl(control)
            : DecodeAgcWriteDataControl(control);
        RecordFenceWritePacketSite(packetAddress, destinationAddress);
        var dwordCount = packetLength - 4;
        var values = new uint[dwordCount];
        for (uint index = 0; index < dwordCount; index++)
        {
            var sourceAddress = packetAddress + 16 + ((ulong)index * sizeof(uint));
            if (!TryReadUInt32(ctx, sourceAddress, out values[index]))
            {
                return;
            }
        }

        var writeLength = incrementAddress
            ? (ulong)dwordCount * sizeof(uint)
            : (ulong)sizeof(uint);
        if (ShouldTraceGuestMemoryRange(destinationAddress, writeLength) &&
            Interlocked.Increment(ref _guestMemoryPacketProbeTraceCount) <= 256)
        {
            Console.Error.WriteLine(
                $"[AGC][GUEST-MEMORY-PACKET] kind=write-data " +
                $"packet=0x{packetAddress:X16} standard={(standardPacket ? 1 : 0)} " +
                $"dst=0x{destinationAddress:X16} bytes={writeLength} " +
                $"dst_sel={destination} increment={(incrementAddress ? 1 : 0)} " +
                $"values={string.Join('/', values.Take(8).Select(value => $"{value:X8}"))}");
        }

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                InvalidateDcbWindowIfOverlaps(
                    destinationAddress,
                    incrementAddress ? (ulong)dwordCount * sizeof(uint) : sizeof(uint));
                var wroteData = destination is 1 or 2 or 4 or 5;
                for (uint index = 0; wroteData && index < dwordCount; index++)
                {
                    var targetAddress = destinationAddress +
                        (incrementAddress ? (ulong)index * sizeof(uint) : 0);
                    wroteData = TryWriteUInt32(ctx, targetAddress, values[index]);
                }

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.write_data dst={destination} " +
                        $"addr=0x{destinationAddress:X16} count={dwordCount} " +
                        $"increment={incrementAddress} confirm={writeConfirm} " +
                        $"cache={cachePolicy} standard={standardPacket} wrote={wroteData}");
                }
            },
            $"write_data dst=0x{destinationAddress:X16} count={dwordCount}",
            packetAddress,
            destination is 1 or 2 or 4 or 5 ? destinationAddress : 0,
            destination is 1 or 2 or 4 or 5
                ? incrementAddress ? (ulong)dwordCount * sizeof(uint) : sizeof(uint)
                : 0);
    }

    private static (uint Destination, bool IncrementAddress, bool WriteConfirm, uint CachePolicy)
        DecodeStandardWriteDataControl(uint control)
    {
        // GFX10 PKT3_WRITE_DATA is not byte-packed like sceAgcDcbWriteData's
        // NOP wrapper: DST_SEL is 11:8, ADDR_INCR is bit 16 (0 increments),
        // WR_CONFIRM is bit 20, and CACHE_POLICY is 26:25. In particular, the
        // low byte is reserved and must never be interpreted as DST_SEL.
        return (
            Destination: (control >> 8) & 0xFu,
            IncrementAddress: (control & (1u << 16)) == 0,
            WriteConfirm: (control & (1u << 20)) != 0,
            CachePolicy: (control >> 25) & 0x3u);
    }

    private static (uint Destination, bool IncrementAddress, bool WriteConfirm, uint CachePolicy)
        DecodeAgcWriteDataControl(uint control) =>
        (
            Destination: control & 0xFFu,
            IncrementAddress: ((control >> 16) & 0xFFu) == 0,
            WriteConfirm: ((control >> 24) & 0xFFu) != 0,
            CachePolicy: (control >> 8) & 0xFFu);

#if DEBUG
    private static void ValidateWriteDataControlDecoders()
    {
        // Regression vector: reserved low-byte noise previously decoded 0xA5
        // as DST_SEL, causing a valid standard memory write to be discarded.
        const uint standardControl = 0xA5u | (5u << 8) | (1u << 16) | (1u << 20) | (2u << 25);
        var standard = DecodeStandardWriteDataControl(standardControl);
        System.Diagnostics.Debug.Assert(standard.Destination == 5u);
        System.Diagnostics.Debug.Assert(!standard.IncrementAddress);
        System.Diagnostics.Debug.Assert(standard.WriteConfirm);
        System.Diagnostics.Debug.Assert(standard.CachePolicy == 2u);

        const uint agcControl = 4u | (3u << 8) | (1u << 24);
        var agc = DecodeAgcWriteDataControl(agcControl);
        System.Diagnostics.Debug.Assert(agc.Destination == 4u);
        System.Diagnostics.Debug.Assert(agc.IncrementAddress);
        System.Diagnostics.Debug.Assert(agc.WriteConfirm);
        System.Diagnostics.Debug.Assert(agc.CachePolicy == 3u);
    }

    private static void ValidateDispatchInitiators()
    {
        const uint threadCount = 0x00F0_0100u;
        const uint localSize = 64u;
        var initiator = DirectDispatchInitiator(0);
        System.Diagnostics.Debug.Assert((initiator & (1u << 5)) == 0);
        System.Diagnostics.Debug.Assert((initiator & (1u << 6)) != 0);
        System.Diagnostics.Debug.Assert(threadCount * localSize == 0x3C00_4000u);
        System.Diagnostics.Debug.Assert(CeilDivide(20, 8) == 3);
        System.Diagnostics.Debug.Assert(CeilDivide(12, 8) == 2);
    }

    private static void ValidateSubmittedQueueAndReleaseMemDecoders()
    {
        var nggRegisters = new Dictionary<uint, uint>
        {
            [GsUserDataRegister - 1] = 3u << 1,
        };
        System.Diagnostics.Debug.Assert(
            SelectExportUserDataRegister(nggRegisters) == GsUserDataRegister);

        var queue = new SubmittedDcbState();
        queue.PendingSubmissions.Enqueue(new(0x1000, 8, 11, false));
        queue.PendingSubmissions.Enqueue(new(0x2000, 16, 12, true));
        System.Diagnostics.Debug.Assert(
            queue.PendingSubmissions.Dequeue().SubmissionId == 11);
        System.Diagnostics.Debug.Assert(
            queue.PendingSubmissions.Dequeue().SubmissionId == 12);

        var control = (1u << 16) | (2u << 29);
        var decoded = DecodeStandardReleaseMemControl(control);
        System.Diagnostics.Debug.Assert(decoded.Destination == 1u);
        System.Diagnostics.Debug.Assert(decoded.DataSelection == 2u);
        System.Diagnostics.Debug.Assert(
            PatchUInt32Bits(0xABCD_1234u, 0x00FF_0000u, 3u << 16) ==
            0xAB03_1234u);
    }

    private static void ValidateAcquireMemAndQueueResetDecoders()
    {
        var range = DecodeSubmittedAcquireMem(
            0x8000_7FC0u,
            0x0000_0123u,
            0x45u,
            0x89AB_CDEFu,
            0x0012_3456u,
            0x1_000Au,
            0x0001_0388u);
        System.Diagnostics.Debug.Assert(range.Engine == 1u);
        System.Diagnostics.Debug.Assert(range.CbDbControl == 0x7FC0u);
        System.Diagnostics.Debug.Assert(range.SizeBytes == 0x0000_4500_0001_2300UL);
        System.Diagnostics.Debug.Assert(range.BaseAddress == 0x1234_5689_ABCD_EF00UL);
        System.Diagnostics.Debug.Assert(range.PollInterval == 0xAu);
        System.Diagnostics.Debug.Assert(range.InvalidatesGuestResources);
        System.Diagnostics.Debug.Assert(!range.CoversAllGuestMemory);

        var all = DecodeSubmittedAcquireMem(0, 0, 0, 0, 0, 0, 0x280u);
        System.Diagnostics.Debug.Assert(all.CoversAllGuestMemory);
        System.Diagnostics.Debug.Assert(all.InvalidatesGuestResources);
        var explicitAll = DecodeSubmittedAcquireMem(0, 1, 0, 0, 0, 0, 0x103C0u);
        System.Diagnostics.Debug.Assert(explicitAll.CoversAllGuestMemory);

        var queue = new SubmittedDcbState
        {
            QueueName = "validator",
            ActiveSubmissionId = 7,
            HasActiveSubmission = true,
            IsSuspended = true,
            IndexBufferAddress = 0x1000,
            IndexBufferCount = 12,
            IndexSize = 1,
            InstanceCount = 4,
            DrawIndexOffset = 2,
            IndirectArgsAddress = 0x2000,
            SawIndexedDraw = true,
            GuestDrawKind = GuestDrawKind.FullscreenBarycentric,
        };
        queue.CxRegisters.Add(1, 2);
        queue.ShRegisters.Add(3, 4);
        queue.UcRegisters.Add(5, 6);
        queue.PendingSubmissions.Enqueue(new(0x3000, 2, 8, false));
        ResetSubmittedParserState(queue);
        System.Diagnostics.Debug.Assert(queue.CxRegisters.Count == 0);
        System.Diagnostics.Debug.Assert(queue.ShRegisters.Count == 0);
        System.Diagnostics.Debug.Assert(queue.UcRegisters.Count == 0);
        System.Diagnostics.Debug.Assert(queue.IndexBufferAddress == 0);
        System.Diagnostics.Debug.Assert(queue.IndexBufferCount == 0);
        System.Diagnostics.Debug.Assert(queue.IndexSize == 0);
        System.Diagnostics.Debug.Assert(queue.InstanceCount == 1);
        System.Diagnostics.Debug.Assert(queue.DrawIndexOffset == 0);
        System.Diagnostics.Debug.Assert(queue.IndirectArgsAddress == 0);
        System.Diagnostics.Debug.Assert(!queue.SawIndexedDraw);
        System.Diagnostics.Debug.Assert(queue.GuestDrawKind == GuestDrawKind.None);
        System.Diagnostics.Debug.Assert(queue.QueueName == "validator");
        System.Diagnostics.Debug.Assert(queue.ActiveSubmissionId == 7);
        System.Diagnostics.Debug.Assert(queue.HasActiveSubmission);
        System.Diagnostics.Debug.Assert(queue.IsSuspended);
        System.Diagnostics.Debug.Assert(queue.PendingSubmissions.Count == 1);
    }

    private static void ValidateDepthTargetDecoder()
    {
        var registers = new Dictionary<uint, uint>
        {
            [DbDepthControl] = 0x2u | 0x4u | (1u << 4),
            [DbDepthSizeXy] = 1919u | (1079u << 16),
            [DbDepthClear] = BitConverter.SingleToUInt32Bits(1f),
            [DbZInfo] = 3u | (24u << 4),
            [DbZReadBase] = 0x0123_4567u,
            [DbZWriteBase] = 0x0123_4567u,
            [DbZReadBaseHi] = 2u,
            [DbZWriteBaseHi] = 2u,
        };
        var depth = DecodeDepthTarget(registers);
        System.Diagnostics.Debug.Assert(depth is not null);
        System.Diagnostics.Debug.Assert(depth.Width == 1920 && depth.Height == 1080);
        System.Diagnostics.Debug.Assert(depth.GuestFormat == 3u);
        System.Diagnostics.Debug.Assert(depth.SwizzleMode == 24u);
        System.Diagnostics.Debug.Assert(depth.Address == 0x0000_0201_2345_6700UL);
        System.Diagnostics.Debug.Assert(depth.ClearDepth == 1f);
    }
#endif

    // SHARPEMU_GPU_WAIT_MODE=force reverts to the legacy behaviour of faking a
    // satisfying value at parse time. Default (suspend) properly suspends the
    // DCB on an unmet WAIT_REG_MEM and resumes it once the awaited completion
    // label is genuinely written by a later submit — preserving cross-submit
    // ordering so the work after a wait (e.g. the final composite) does not run
    // ahead of the compute it samples.
    private static readonly bool _gpuWaitSuspendEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_WAIT_MODE"),
        "force",
        StringComparison.OrdinalIgnoreCase);

    // Optional age for one-shot missing-producer diagnostics. Stale waits are
    // never removed or force-satisfied in the default suspend mode: doing so
    // advances a queue without its real cross-queue producer and can publish
    // incomplete CPU/GPU state. Only SHARPEMU_GPU_WAIT_MODE=force retains the
    // explicit legacy mutation path above. Default 0 disables age diagnostics.
    private static readonly long _gpuWaitStaleTicks =
        (long.TryParse(
             Environment.GetEnvironmentVariable("SHARPEMU_GPU_WAIT_FALLBACK_MS"),
             out var fallbackMs) && fallbackMs >= 0
            ? fallbackMs
            : 0L) * System.Diagnostics.Stopwatch.Frequency / 1000L;

    // How long a suspended GPU wait may sit before the deadlock breaker may
    // release it using the last value a real producer wrote to its label. Long
    // enough that legitimate GPU work (which completes within a frame) never
    // trips it; short enough that a wedged cross-queue cycle unblocks quickly.
    private static readonly long _gpuDeadlockBreakTicks =
        (long.TryParse(
             Environment.GetEnvironmentVariable("SHARPEMU_GPU_DEADLOCK_BREAK_MS"),
             out var deadlockMs) && deadlockMs > 0
            ? deadlockMs
            : 500L) * System.Diagnostics.Stopwatch.Frequency / 1000L;

    // Reads the WAIT_REG_MEM watched address, reference, mask, and 3-bit compare
    // function for both the AGC NOP-encapsulated (RWaitMem32/64) and the standard
    // ItWaitRegMem packet layouts.
    private static bool TryParseSubmittedWait(
        CpuContext ctx,
        ulong packetAddress,
        uint packetLength,
        bool is64Bit,
        bool isStandard,
        out ulong waitAddress,
        out ulong reference,
        out ulong mask,
        out uint compareFunction,
        out uint controlValue)
    {
        waitAddress = 0;
        reference = 0;
        mask = 0;
        compareFunction = 0;
        controlValue = 0;
        if (isStandard)
        {
            if (!TryReadUInt32(ctx, packetAddress + 4, out var stdControl) ||
                !TryReadUInt64(ctx, packetAddress + 8, out waitAddress) ||
                !TryReadUInt32(ctx, packetAddress + 16, out var stdRef) ||
                !TryReadUInt32(ctx, packetAddress + 20, out var stdMask))
            {
                return false;
            }

            compareFunction = stdControl & 0x7u;
            controlValue = stdControl;
            reference = stdRef;
            mask = stdMask;
            return true;
        }

        var legacyWait32 = !is64Bit && packetLength == 6;
        var controlOffset = is64Bit ? 28u : legacyWait32 ? 16u : 20u;
        if (!TryReadUInt64(ctx, packetAddress + 4, out waitAddress) ||
            !TryReadUInt32(ctx, packetAddress + controlOffset, out var control))
        {
            return false;
        }

        compareFunction = control & 0x7u;
        controlValue = control;
        if (is64Bit)
        {
            return TryReadUInt64(ctx, packetAddress + 12, out mask) &&
                   TryReadUInt64(ctx, packetAddress + 20, out reference);
        }

        var referenceOffset = legacyWait32 ? 20u : 16u;
        if (!TryReadUInt32(ctx, packetAddress + 12, out var mask32) ||
            !TryReadUInt32(ctx, packetAddress + referenceOffset, out var reference32))
        {
            return false;
        }

        mask = mask32;
        reference = reference32;
        return true;
    }

    // Parks on ring memory not yet written by the game; resumes once it appends more.
    private static bool SuspendOnUnwrittenRingWord(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong commandAddress,
        ulong wordAddress,
        uint offset,
        bool tracePacket)
    {
        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            CommandBufferAddress = commandAddress,
            ResumeAddress = wordAddress,
            TotalDwords = offset + RingResumeWindowDwords,
            ResumeOffset = offset,
            ReferenceValue = 0,
            Mask = 0xFFFF_FFFFu,
            CompareFunction = 4, // resume once the dword becomes nonzero
            ControlValue = 0,
            Is64Bit = false,
            IsStandard = false,
            WaitAddress = wordAddress,
            Memory = ctx.Memory,
            QueueName = state.QueueName,
            SubmissionId = state.ActiveSubmissionId,
            RegisteredTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            State = state,
        };
        GpuWaitRegistry.Register(waiter.WaitAddress, waiter);
        state.RingTailParkAddress = wordAddress;
        var gpuState = _submittedGpuStates.GetValue(
            CanonicalMemory(ctx.Memory),
            static _ => new SubmittedGpuState());
        EnsureGpuWaitMonitor(ctx, gpuState);
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.ring_tail_pending addr=0x{wordAddress:X16} " +
                $"queue={state.QueueName}");
        }

        return true;
    }

    // Returns true when the DCB should suspend parsing at this wait (its
    // continuation was registered into GpuWaitRegistry); false to keep parsing
    // (already satisfied, unreadable, or legacy force-satisfy mode).
    // How long an indirect dispatch may wait for its producing dispatch to write
    // non-zero dimensions before we give up and drop it (matching the pre-existing
    // reject behavior). The producer runs on the render thread within a frame or
    // two; this only bounds the pathological/legitimately-empty case.
    private const long IndirectDimsRetryBudgetMs = 150;

    private static readonly object _indirectDimsGate = new();
    // Keys (memory, packetAddress) whose retry deadline elapsed. Added by
    // DrainResumableDcbs when it resumes an expired retry, consumed by the very
    // next re-parse of that packet so it drops instead of re-suspending. Never
    // persists across frames — a fresh submit of the same packet retries anew.
    private static readonly HashSet<(object, ulong)> _indirectDimsExpired = new();

    // Suspends an indirect-dispatch DCB until the guest buffer holding its
    // thread-group dimensions becomes non-zero (written by a prior GPU dispatch),
    // then re-parses the dispatch. Returns false — so the caller drops the work —
    // when the dims already expired once (genuinely empty dispatch).
    private static bool HandleSubmittedIndirectDimsWait(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong commandAddress,
        ulong packetAddress,
        uint offset,
        uint dwordCount,
        ulong dimsAddress,
        bool tracePacket)
    {
        if (!_gpuWaitSuspendEnabled ||
            dimsAddress == 0 ||
            dimsAddress % sizeof(uint) != 0)
        {
            return false;
        }

        var key = (ctx.Memory, packetAddress);
        lock (_indirectDimsGate)
        {
            // This is the re-parse right after the deadline elapsed: drop the
            // dispatch instead of suspending again.
            if (_indirectDimsExpired.Remove(key))
            {
                return false;
            }
        }

        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            CommandBufferAddress = commandAddress,
            ResumeAddress = packetAddress, // re-parse this dispatch packet
            ResumeOffset = offset,
            TotalDwords = dwordCount,
            WaitAddress = dimsAddress,
            ReferenceValue = 0,
            Mask = 0xFFFFFFFF,
            CompareFunction = 4, // NOT_EQUAL: dims became available
            Is64Bit = false,
            IsStandard = false,
            Memory = ctx.Memory,
            QueueName = state.QueueName,
            SubmissionId = state.ActiveSubmissionId,
            RegisteredTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            RetryDeadlineTicks = System.Diagnostics.Stopwatch.GetTimestamp() +
                (IndirectDimsRetryBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000L),
            State = state,
        };

        GpuWaitRegistry.Register(dimsAddress, waiter);
        var gpuState = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        EnsureGpuWaitMonitor(ctx, gpuState);
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dispatch_indirect_wait dims=0x{dimsAddress:X16} " +
                $"packet=0x{packetAddress:X16} queue={state.QueueName}");
        }

        return true;
    }

    private static bool HandleSubmittedRewind(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong commandAddress,
        ulong packetAddress,
        uint offset,
        uint length,
        uint dwordCount,
        bool tracePacket)
    {
        var bodyAddress = packetAddress + sizeof(uint);
        if (!TryReadUInt32(ctx, bodyAddress, out var body))
        {
            return false;
        }

        if ((body & RewindValidBit) != 0)
        {
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.dcb.rewind_valid queue={state.QueueName} " +
                    $"packet=0x{packetAddress:X16} body=0x{body:X8}");
            }

            return false; // already valid — keep parsing
        }

        if (!_gpuWaitSuspendEnabled)
        {
            return false;
        }

        // Suspend until RewindPatchSetRewindState sets bit 31 on the body dword.
        const uint compareEqual = 3;
        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            CommandBufferAddress = commandAddress,
            ResumeAddress = packetAddress + ((ulong)length * sizeof(uint)),
            TotalDwords = dwordCount,
            ResumeOffset = offset + length,
            ReferenceValue = RewindValidBit,
            Mask = RewindValidBit,
            CompareFunction = compareEqual,
            ControlValue = 0,
            Is64Bit = false,
            IsStandard = true,
            WaitAddress = bodyAddress,
            Memory = ctx.Memory,
            QueueName = state.QueueName,
            SubmissionId = state.ActiveSubmissionId,
            RegisteredTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            State = state,
        };

        GpuWaitRegistry.Register(bodyAddress, waiter);
        var gpuState = _submittedGpuStates.GetValue(
            CanonicalMemory(ctx.Memory),
            static _ => new SubmittedGpuState());
        EnsureGpuWaitMonitor(ctx, gpuState);
        TraceAgcShader(
            $"agc.rewind_suspend queue={state.QueueName} " +
            $"submission={state.ActiveSubmissionId} " +
            $"packet=0x{packetAddress:X16} body=0x{bodyAddress:X16}");
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.rewind_suspend queue={state.QueueName} " +
                $"packet=0x{packetAddress:X16} body=0x{body:X8}");
        }

        return true;
    }

    private static bool HandleSubmittedWaitRegMem(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong commandAddress,
        ulong packetAddress,
        uint offset,
        uint length,
        uint dwordCount,
        bool is64Bit,
        bool isStandard,
        bool tracePacket)
    {
        if (!TryParseSubmittedWait(
                ctx, packetAddress, length, is64Bit, isStandard,
                out var waitAddress, out var reference, out var mask, out var compareFunction,
                out var controlValue))
        {
            return false;
        }

        // COMPARE_FUNC=0 is the hardware "always" condition. Reserved 7 is
        // also fail-open; neither condition may register a waiter. Validate
        // the watched memory before any read so null/malformed packets cannot
        // become permanent entries keyed by address zero.
        if (compareFunction is 0 or 7)
        {
            TraceSubmittedWait(
                waitAddress,
                0,
                mask,
                reference,
                compareFunction,
                is64Bit ? 64 : 32,
                tracePacket);
            return false;
        }

        var requiredAlignment = is64Bit ? sizeof(ulong) : sizeof(uint);
        if (waitAddress == 0 ||
            mask == 0 ||
            waitAddress % (ulong)requiredAlignment != 0)
        {
            TraceAgc(
                $"agc.dcb.wait_reject addr=0x{waitAddress:X16} " +
                $"mask=0x{mask:X16} compare={compareFunction} bits=" +
                $"{(is64Bit ? 64 : 32)} standard={isStandard} " +
                $"packet=0x{packetAddress:X16} reason=invalid-address-or-mask");
            return false;
        }

        ulong currentValue = 0;
        bool hasCurrent;
        if (is64Bit)
        {
            hasCurrent = TryReadUInt64(ctx, waitAddress, out currentValue);
        }
        else if (TryReadUInt32(ctx, waitAddress, out var current32))
        {
            currentValue = current32;
            hasCurrent = true;
        }
        else
        {
            hasCurrent = false;
        }

        TraceSubmittedWait(
            waitAddress, currentValue, mask, reference, compareFunction,
            is64Bit ? 64 : 32, tracePacket);

        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            CommandBufferAddress = commandAddress,
            ResumeAddress = packetAddress + ((ulong)length * sizeof(uint)),
            TotalDwords = dwordCount,
            ResumeOffset = offset + length,
            ReferenceValue = reference,
            Mask = mask,
            CompareFunction = compareFunction,
            ControlValue = controlValue,
            Is64Bit = is64Bit,
            IsStandard = isStandard,
            WaitAddress = waitAddress,
            Memory = ctx.Memory,
            QueueName = state.QueueName,
            SubmissionId = state.ActiveSubmissionId,
            RegisteredTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            State = state,
        };

        if (hasCurrent && GpuWaitRegistry.Compare(waiter, currentValue))
        {
            return false; // already satisfied — keep parsing
        }

        if (!_gpuWaitSuspendEnabled)
        {
            if (hasCurrent)
            {
                ForceSatisfyGpuWait(ctx, waiter, currentValue);
            }

            return false;
        }

        if (!hasCurrent)
        {
            return false; // cannot evaluate the label — do not stall the DCB
        }

        GpuWaitRegistry.Register(waitAddress, waiter);
        var gpuState = _submittedGpuStates.GetValue(
            CanonicalMemory(ctx.Memory),
            static _ => new SubmittedGpuState());
        EnsureGpuWaitMonitor(ctx, gpuState);
        TryForceSubmitOrphanPreamble(ctx, gpuState, waitAddress);
        TraceWaitProducerState(
            ctx.Memory,
            waiter,
            commandAddress,
            packetAddress,
            stale: false,
            currentValue);
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.suspended addr=0x{waitAddress:X16} ref=0x{reference:X16} " +
                $"mask=0x{mask:X16} cur=0x{currentValue:X16} cmp={compareFunction}");
        }

        return true;
    }

    /// <summary>
    /// Direct guest CPU stores can satisfy a GPU wait without crossing another
    /// AGC import. Keep one low-frequency monitor per guest memory while waits
    /// exist so those real stores wake their queues. The monitor never changes
    /// a label: it uses the same masked comparison as submission-time parsing
    /// and resumes only after the guest value genuinely satisfies the packet.
    /// </summary>
    private static void EnsureGpuWaitMonitor(
        CpuContext submitContext,
        SubmittedGpuState gpuState)
    {
        // Multiple graphics/compute submissions can discover their first wait
        // concurrently. Claim the monitor slot atomically so they cannot each
        // start a lifetime worker for the same guest memory.
        if (Interlocked.CompareExchange(ref gpuState.WaitMonitorRunning, 1, 0) != 0)
        {
            return;
        }

        var monitorContext = new CpuContext(
            submitContext.Memory,
            submitContext.TargetGeneration);
        try
        {
            // The monitor can stay alive for the title's lifetime. Keeping it
            // on the ThreadPool makes GPU progress depend on the pool noticing
            // that loader/TBB work has saturated its current workers; heavy
            // tracing used to hide this by provoking worker injection.
            var monitorThread = new Thread(
                static state =>
                {
                    var (context, submittedState) =
                        ((CpuContext Context, SubmittedGpuState GpuState))state!;
                    MonitorGpuWaits(context, submittedState);
                })
            {
                IsBackground = true,
                Name = "SharpEmu-GpuWaitMonitor",
            };
            monitorThread.Start((monitorContext, gpuState));
        }
        catch
        {
            Interlocked.Exchange(ref gpuState.WaitMonitorRunning, 0);
            throw;
        }
    }

    // Lets a stall snapshot show whether MonitorGpuWaits' loop is still alive.
    private static long _gpuWaitMonitorHeartbeatCount;
    private static long _gpuWaitMonitorHeartbeatTimestamp;

    public static (long Count, double SecondsSinceLastIteration) GpuWaitMonitorHeartbeat()
    {
        var count = Volatile.Read(ref _gpuWaitMonitorHeartbeatCount);
        var lastTicks = Volatile.Read(ref _gpuWaitMonitorHeartbeatTimestamp);
        var seconds = lastTicks == 0
            ? -1
            : (System.Diagnostics.Stopwatch.GetTimestamp() - lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        return (count, seconds);
    }

    // Distinguishes a stopped render loop from an orphan-mechanism gap.
    private static long _dcbSubmitCount;
    private static long _lastDcbSubmitTimestamp;

    public static (long Count, double SecondsSinceLastSubmit) DcbSubmitHeartbeat()
    {
        var count = Volatile.Read(ref _dcbSubmitCount);
        var lastTicks = Volatile.Read(ref _lastDcbSubmitTimestamp);
        var seconds = lastTicks == 0
            ? -1
            : (System.Diagnostics.Stopwatch.GetTimestamp() - lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        return (count, seconds);
    }

    // Stall-diagnostic accessor for DirectExecutionBackend's producer scan.
    public static List<(ulong Header, ulong Base, ulong Cursor)> SnapshotBuilderArenas()
    {
        var result = new List<(ulong, ulong, ulong)>();
        lock (_orphanPreambleGate)
        {
            foreach (var (headerAddress, seen) in _builderArenaLastSeen)
            {
                result.Add((headerAddress, seen.Base, seen.Cursor));
            }
        }

        return result;
    }

    // Shows whether a header is silently blacklisted vs. genuinely idle.
    public static string DumpOrphanPreambleState()
    {
        var sb = new System.Text.StringBuilder();
        var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        var freq = (double)System.Diagnostics.Stopwatch.Frequency;
        lock (_orphanPreambleGate)
        {
            sb.Append(
                $"pending_targets={_orphanPreamblePendingTargets.Count} " +
                $"closed_slices={_orphanPreambleClosedSlices.Count} " +
                $"tracked_targets={_cbReleaseMemTargets.Count} " +
                $"tracked_headers={_builderArenaLastSeen.Count}");

            var allHeaders = new HashSet<ulong>(_builderArenaLastSeen.Keys);
            allHeaders.UnionWith(_orphanPreambleSubmitted.Keys);
            foreach (var header in allHeaders)
            {
                var hasSubmitted = _orphanPreambleSubmitted.TryGetValue(header, out var submitted);
                var hasSeen = _builderArenaLastSeen.TryGetValue(header, out var seen);
                var blacklisted = hasSubmitted && submitted.Base == 0;
                var seenAgeText = hasSeen
                    ? $"{(nowTicks - seen.Timestamp) / freq:F1}s"
                    : "never";
                sb.Append(
                    $"\n  header=0x{header:X} blacklisted={blacklisted} " +
                    $"submitted_base=0x{(hasSubmitted ? submitted.Base : 0):X} " +
                    $"submitted_cursor=0x{(hasSubmitted ? submitted.Cursor : 0):X} " +
                    $"last_seen_base=0x{(hasSeen ? seen.Base : 0):X} " +
                    $"last_seen_cursor=0x{(hasSeen ? seen.Cursor : 0):X} " +
                    $"last_seen_age={seenAgeText}");
            }
        }

        return sb.ToString();
    }

    private static void MonitorGpuWaits(
        CpuContext ctx,
        SubmittedGpuState gpuState)
    {
        var delayMilliseconds = 1;
        long observedSignal;
        lock (gpuState.WaitMonitorSignalGate)
        {
            observedSignal = gpuState.WaitMonitorSignalVersion;
        }

        while (true)
        {
            Interlocked.Increment(ref _gpuWaitMonitorHeartbeatCount);
            Volatile.Write(ref _gpuWaitMonitorHeartbeatTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
            try
            {
            var madeProgress = false;
            lock (gpuState.Gate)
            {
                var before = GpuWaitRegistry.CountForMemory(ctx.Memory);
                // Under orphan force-submit, the monitor must outlive the
                // waits — the arena sweep below is the only thing that
                // catches CPU-side usleep polling with no WAIT_REG_MEM.
                if (before == 0 && !_forceSubmitOrphanPreamblesEnabled)
                {
                    Interlocked.Exchange(ref gpuState.WaitMonitorRunning, 0);
                    return;
                }

                var remaining = before;
                if (before != 0)
                {
                    var resumed = DrainResumableDcbs(ctx, gpuState, tracePackets: _traceAgc);
                    remaining = GpuWaitRegistry.CountForMemory(ctx.Memory);
                    madeProgress = resumed != 0;
                    if (_traceAgc && resumed != 0)
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][TRACE] agc.wait_monitor_resumed count={resumed} " +
                            $"remaining={remaining}");
                    }

                    SharpEmu.Libs.Diagnostics.LoadProgressDiagnostics.TraceGpuWaitSnapshot(
                        ctx.Memory);
                    GpuWaitProfile.RecordMonitorPoll(resumed != 0);
                    GpuWaitProfile.ReportIfDue(remaining);
                    if (remaining == 0 && !_forceSubmitOrphanPreamblesEnabled)
                    {
                        Interlocked.Exchange(ref gpuState.WaitMonitorRunning, 0);
                        return;
                    }
                }
            }

            // Re-offering every live wait also covers producers the game
            // builds after the wait registered.
            if (_forceSubmitOrphanPreamblesEnabled)
            {
                foreach (var (address, _) in
                         GpuWaitRegistry.SnapshotInRange(ctx.Memory, 0, ulong.MaxValue))
                {
                    TryForceSubmitOrphanPreamble(ctx, gpuState, address);
                }

                DrainPendingOrphanPreambles(ctx, gpuState);
                SweepBuilderArenas(ctx, gpuState);
                SalvageStuckFenceWrites(ctx, gpuState);
            }

            delayMilliseconds = madeProgress
                ? 1
                : Math.Min(delayMilliseconds * 2, 16);
            lock (gpuState.WaitMonitorSignalGate)
            {
                if (gpuState.WaitMonitorSignalVersion == observedSignal)
                {
                    Monitor.Wait(gpuState.WaitMonitorSignalGate, delayMilliseconds);
                }

                observedSignal = gpuState.WaitMonitorSignalVersion;
            }
            }
            catch (Exception ex)
            {
                // No other supervisor: an unlogged exception here would
                // silently end AGC activity forever. Log and keep looping.
                Console.Error.WriteLine(
                    $"[LOADER][ERROR] agc.wait_monitor_iteration_exception " +
                    $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                Thread.Sleep(16);
            }
        }
    }

    /// <summary>
    /// Writes a value that satisfies the waiter's comparison. This deliberately
    /// exists only behind SHARPEMU_GPU_WAIT_MODE=force for legacy A/B testing;
    /// normal and stale waits must never mutate their watched label.
    /// </summary>
    private static void ForceSatisfyGpuWait(
        CpuContext ctx,
        in GpuWaitRegistry.WaitingDcb waiter,
        ulong value)
    {
        var address = waiter.WaitAddress;
        var mask = waiter.Mask;
        if (address == 0 || mask == 0)
        {
            return;
        }

        var maskedRef = waiter.ReferenceValue & mask;
        ulong? satisfyMasked = waiter.CompareFunction switch
        {
            1 => maskedRef == 0 ? null : (maskedRef - 1) & mask,            // <
            2 => maskedRef,                                                 // <=
            3 => maskedRef,                                                 // ==
            4 => (~maskedRef) & mask,                                       // !=
            5 => maskedRef,                                                 // >=
            6 => maskedRef == mask ? null : (maskedRef + 1) & mask,         // >
            _ => null,
        };

        if (satisfyMasked is not { } satisfy)
        {
            return;
        }

        var newValue = (value & ~mask) | (satisfy & mask);
        if (waiter.Is64Bit)
        {
            ctx.TryWriteUInt64(address, newValue);
        }
        else
        {
            TryWriteUInt32(ctx, address, unchecked((uint)newValue));
        }
    }

    // WAIT_REG_MEM packets whose condition is not met suspend their DCB into
    // GpuWaitRegistry. Each submit re-checks every suspended DCB against current
    // guest memory (labels are advanced by ReleaseMem/WriteData/DmaData packets
    // or direct CPU writes) and resumes the ones now satisfied. A resumed DCB
    // can itself write labels that unblock others, so loop to a fixed point.
    private static int DrainResumableDcbs(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        bool tracePackets)
    {
        if (!_gpuWaitSuspendEnabled)
        {
            return 0;
        }

        var resumedCount = 0;
        for (var pass = 0; pass < 256; pass++)
        {
            var woken = GpuWaitRegistry.CollectSatisfied(ctx.Memory, (address, is64Bit) =>
                is64Bit
                    ? TryReadUInt64(ctx, address, out var value64) ? value64 : (ulong?)null
                    : TryReadUInt32(ctx, address, out var value32) ? value32 : (ulong?)null);

            // Indirect-dispatch dimension retries whose deadline elapsed are
            // resumed so they drop instead of stalling. Flag each so its immediate
            // re-parse drops the dispatch rather than suspending again.
            var expiredRetries = GpuWaitRegistry.CollectExpiredRetries(
                ctx.Memory, System.Diagnostics.Stopwatch.GetTimestamp());
            if (expiredRetries is not null)
            {
                lock (_indirectDimsGate)
                {
                    foreach (var retry in expiredRetries)
                    {
                        _indirectDimsExpired.Add((ctx.Memory, retry.ResumeAddress));
                    }
                }

                foreach (var retry in expiredRetries)
                {
                    ResumeSuspendedDcb(ctx, gpuState, retry, tracePackets);
                }
            }

            // Break cross-queue deadlocks: a waiter stuck past the deadline whose
            // label a real producer already signalled (but guest memory has since
            // been reset for reuse) is released using that produced value. Only
            // fires for genuinely wedged waits, so fast-resolving ones on working
            // titles are untouched.
            var deadlockBroken = GpuWaitRegistry.CollectDeadlockBroken(
                ctx.Memory, System.Diagnostics.Stopwatch.GetTimestamp(), _gpuDeadlockBreakTicks);
            if (deadlockBroken is not null)
            {
                foreach (var waiter in deadlockBroken)
                {
                    if (tracePackets)
                    {
                        TraceAgc(
                            $"agc.deadlock_break label=0x{waiter.WaitAddress:X16} " +
                            $"queue={waiter.QueueName} submission={waiter.SubmissionId}");
                    }

                    ResumeSuspendedDcb(ctx, gpuState, waiter, tracePackets);
                }
            }

            if (woken is null && expiredRetries is null && deadlockBroken is null)
            {
                if (_gpuWaitStaleTicks > 0 &&
                    GpuWaitRegistry.CollectUnreportedStale(
                        ctx.Memory,
                        System.Diagnostics.Stopwatch.GetTimestamp(),
                        _gpuWaitStaleTicks) is { } stale)
                {
                    foreach (var waiter in stale)
                    {
                        ulong? currentValue = waiter.Is64Bit
                            ? TryReadUInt64(ctx, waiter.WaitAddress, out var value64)
                                ? value64
                                : null
                            : TryReadUInt32(ctx, waiter.WaitAddress, out var value32)
                                ? value32
                                : null;
                        TraceWaitProducerState(
                            ctx.Memory,
                            waiter,
                            waiter.CommandBufferAddress,
                            waiter.ResumeAddress,
                            stale: true,
                            currentValue);
                    }
                }

                return resumedCount;
            }

            if (woken is not null)
            {
                foreach (var waiter in woken)
                {
                    ResumeSuspendedDcb(ctx, gpuState, waiter, tracePackets);
                    resumedCount++;
                }
            }
        }

        return resumedCount;
    }

    private static void ResumeSuspendedDcb(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        in GpuWaitRegistry.WaitingDcb waiter,
        bool tracePackets)
    {
        var state = waiter.State as SubmittedDcbState ?? gpuState.Graphics;
        // Any resume ends a ring-tail park; SuspendOnUnwrittenRingWord re-arms
        // it if the continued parse parks again.
        state.RingTailParkAddress = 0;
        var remainingDwords = waiter.TotalDwords - waiter.ResumeOffset;
        var waitedMilliseconds = waiter.RegisteredTicks == 0
            ? 0.0
            : (System.Diagnostics.Stopwatch.GetTimestamp() - waiter.RegisteredTicks) *
              1000.0 / System.Diagnostics.Stopwatch.Frequency;
        TraceAgcShader(
            $"agc.queue_resumed queue={waiter.QueueName} " +
            $"submission={waiter.SubmissionId} label=0x{waiter.WaitAddress:X16} " +
            $"resume=0x{waiter.ResumeAddress:X16} remaining_dwords={remainingDwords} " +
            $"waited_ms={waitedMilliseconds:F3}");
        GpuWaitProfile.RecordResume(waiter.WaitAddress, waitedMilliseconds);
        if (remainingDwords == 0)
        {
            state.IsSuspended = false;
            state.HasActiveSubmission = false;
            NotifySubmittedDcbCompleted(gpuState, state, waiter.SubmissionId);
            PumpSubmittedQueue(ctx, gpuState, state);
            return;
        }

        if (tracePackets)
        {
            TraceAgc(
                $"agc.dcb.resumed addr=0x{waiter.WaitAddress:X16} " +
                $"resume=0x{waiter.ResumeAddress:X16} dwords={remainingDwords} forced=False");
        }

        System.Diagnostics.Debug.Assert(state.HasActiveSubmission);
        System.Diagnostics.Debug.Assert(state.IsSuspended);
        state.QueueName = waiter.QueueName ?? state.QueueName;
        state.ActiveSubmissionId = waiter.SubmissionId;
        state.IsSuspended = false;
        if (ParseSubmittedDcb(
                ctx,
                gpuState,
                state,
                waiter.ResumeAddress,
                remainingDwords,
                tracePackets))
        {
            state.IsSuspended = true;
            return;
        }

        state.HasActiveSubmission = false;
        NotifySubmittedDcbCompleted(gpuState, state, waiter.SubmissionId);
        PumpSubmittedQueue(ctx, gpuState, state);
    }

    private static void TraceSubmittedWait(
        ulong address,
        ulong value,
        ulong mask,
        ulong reference,
        uint compareFunction,
        int bits,
        bool tracePacket)
    {
        var maskedValue = value & mask;
        var satisfied = compareFunction switch
        {
            0 => true,
            1 => maskedValue < reference,
            2 => maskedValue <= reference,
            3 => maskedValue == reference,
            4 => maskedValue != reference,
            5 => maskedValue >= reference,
            6 => maskedValue > reference,
            _ => true,
        };
        if (!tracePacket && (satisfied || !ShouldTraceHotPath(ref _unsatisfiedWaitTraceCount)))
        {
            return;
        }

        TraceAgc(
            $"agc.dcb.wait_reg_mem bits={bits} addr=0x{address:X16} " +
            $"value=0x{value:X16} mask=0x{mask:X16} ref=0x{reference:X16} " +
            $"compare={compareFunction} satisfied={satisfied}");
    }

    private static void ApplySubmittedStandardReleaseMem(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 8, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var destinationLo) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var destinationHi) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var dataLo) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var dataHi))
        {
            return;
        }

        var (destination, dataSelection) = DecodeStandardReleaseMemControl(control);
        var interruptSelection = (control >> 24) & 0x7u;
        var destinationAddress = ((ulong)destinationHi << 32) | destinationLo;
        var data = ((ulong)dataHi << 32) | dataLo;
        var writeLength = dataSelection switch
        {
            1 => (ulong)sizeof(uint),
            2 or 3 or 4 => (ulong)sizeof(ulong),
            _ => 0UL,
        };
        var writesGuestMemory = destination is 0 or 1 &&
                                destinationAddress != 0 &&
                                writeLength != 0;

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                if (writesGuestMemory)
                {
                    InvalidateDcbWindowIfOverlaps(destinationAddress, writeLength);
                }

                var wroteData = writesGuestMemory && (dataSelection switch
                {
                    1 => TryWriteUInt32(ctx, destinationAddress, dataLo),
                    // Native/JIT guest code can place writable host-backed libc
                    // pointers in AGC packets. Match the 32-bit path above and
                    // accept those validated mappings instead of silently losing
                    // the completion fence.
                    2 => TryWriteUInt64(ctx, destinationAddress, data),
                    // Hardware counter writes are timing values sampled at the
                    // release point, not the immediate payload in ordinal 6/7.
                    3 or 4 => TryWriteUInt64(
                        ctx,
                        destinationAddress,
                        unchecked((ulong)System.Diagnostics.Stopwatch.GetTimestamp())),
                    _ => false,
                });

                // Record + latch the written value so a same-frame label reset
                // cannot lose the wakeup, and so the deadlock breaker can release
                // a cross-queue waiter later (see ApplySubmittedReleaseMem).
                if (wroteData && dataSelection is 1 or 2)
                {
                    GpuWaitRegistry.RecordProduced(
                        ctx.Memory, destinationAddress, dataSelection == 1 ? dataLo : data);
                }
                else if (!wroteData && dataSelection is 1 or 2)
                {
                    // See ApplySubmittedReleaseMem: a dropped label write strands
                    // every waiter on this label permanently.
                    ReportLabelWriteFailure(
                        "release_mem_standard", destinationAddress, data, dataSelection);
                }

                // Only deliver a kevent when int_sel requests one — the
                // driver's completion refcount signals on an exact zero
                // crossing, and an unrequested kevent drives it negative and
                // permanently loses the frame-graph kick.
                var wokenQueues = interruptSelection != 0
                    ? KernelEventQueueCompatExports.TriggerRegisteredEventsByFilter(
                        KernelEventQueueCompatExports.KernelEventFilterGraphics,
                        data)
                    : 0;

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.release_mem_standard dst_sel={destination} " +
                        $"dst=0x{destinationAddress:X16} data_sel={dataSelection} " +
                        $"data=0x{data:X16} wrote={wroteData} " +
                        $"int={interruptSelection} woken={wokenQueues}");
                }
            },
            $"release_mem_standard dst=0x{destinationAddress:X16} data=0x{data:X16}",
            packetAddress,
            writesGuestMemory ? destinationAddress : 0,
            writesGuestMemory ? writeLength : 0);
    }

    private static void TraceSubmittedCopyData(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var sourceLow) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var sourceHigh) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var destinationLow) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var destinationHigh))
        {
            return;
        }

        var destinationAddress = destinationLow | ((ulong)destinationHigh << 32);
        var writeLength = ((control >> 16) & 0x1u) != 0
            ? (ulong)sizeof(ulong)
            : (ulong)sizeof(uint);
        var rangeMatch = ShouldTraceGuestMemoryRange(destinationAddress, writeLength);
        if ((_traceGuestMemoryRanges.Length != 0 && !rangeMatch) ||
            Interlocked.Increment(ref _copyDataProbeTraceCount) > 256)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[AGC][COPY-DATA-PROBE] queue={state.QueueName} " +
            $"submission={state.ActiveSubmissionId} packet=0x{packetAddress:X16} " +
            $"length={packetLength} control=0x{control:X8} " +
            $"src=0x{sourceHigh:X8}{sourceLow:X8} " +
            $"dst=0x{destinationAddress:X16} bytes={writeLength} " +
            $"range_match={(rangeMatch ? 1 : 0)}");
    }

    private static (uint Destination, uint DataSelection)
        DecodeStandardReleaseMemControl(uint control) =>
        (
            Destination: (control >> 16) & 0x3u,
            DataSelection: (control >> 29) & 0x7u);

    private static void ApplySubmittedReleaseMem(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 8, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var destinationLo) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var destinationHi) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var dataLo) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var dataHi))
        {
            return;
        }

        var dataSelection = (control >> 16) & 0xFFu;
        var interrupt = (control >> 24) & 0xFFu;
        var destinationAddress = ((ulong)destinationHi << 32) | destinationLo;
        var data = ((ulong)dataHi << 32) | dataLo;
        var writeLength = dataSelection switch
        {
            1 => (ulong)sizeof(uint),
            2 or 3 => (ulong)sizeof(ulong),
            _ => 0UL,
        };
        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                InvalidateDcbWindowIfOverlaps(destinationAddress, writeLength);
                var wroteData = dataSelection switch
                {
                    1 => TryWriteUInt32(ctx, destinationAddress, dataLo),
                    // Keep 64-bit releases compatible with writable host-backed
                    // libc pointers, as the 32-bit TryWriteUInt32 path already is.
                    2 => TryWriteUInt64(ctx, destinationAddress, data),
                    // Data selection 3 samples the GPU clock at the release
                    // point. The packet payload is ignored by hardware; Unity
                    // uses the nonzero timestamp as submit-completion state.
                    3 => TryWriteUInt64(
                        ctx,
                        destinationAddress,
                        unchecked((ulong)System.Diagnostics.Stopwatch.GetTimestamp())),
                    _ => false,
                };

                // Latch waiters against the value we just wrote: the guest reuses
                // these labels and can reset them to 0 before the wake pass reads
                // memory, which otherwise loses the wakeup and stalls at a black
                // screen (Astro Bot: graphics queue waiting on a compute EOP label).
                if (wroteData && dataSelection is 1 or 2)
                {
                    GpuWaitRegistry.RecordProduced(
                        ctx.Memory, destinationAddress, dataSelection == 1 ? dataLo : data);
                }
                else if (!wroteData && dataSelection is 1 or 2)
                {
                    // A label write that fails is not a benign miss: this packet
                    // is the producer a suspended WAIT_REG_MEM is waiting for, and
                    // RecordProduced above is skipped, so the deadlock breaker has
                    // no value to replay either. The queue then never resumes.
                    // Never let that happen quietly.
                    ReportLabelWriteFailure("release_mem", destinationAddress, data, dataSelection);
                }

                // Same interrupt gating as the standard form above.
                var wokenQueues = interrupt != 0
                    ? KernelEventQueueCompatExports.TriggerRegisteredEventsByFilter(
                        KernelEventQueueCompatExports.KernelEventFilterGraphics,
                        data)
                    : 0;

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.release_mem dst=0x{destinationAddress:X16} " +
                        $"data_sel={dataSelection} data=0x{data:X16} wrote={wroteData} " +
                        $"int={interrupt} woken={wokenQueues}");
                }
            },
            $"release_mem dst=0x{destinationAddress:X16} data=0x{data:X16}",
            packetAddress,
            dataSelection is 1 or 2 or 3 ? destinationAddress : 0,
            writeLength);
    }

    private static void ReportLabelWriteFailure(
        string packet,
        ulong destinationAddress,
        ulong data,
        uint dataSelection)
    {
        var count = Interlocked.Increment(ref _labelWriteFailureCount);
        if (count > 16 && (count & (count - 1)) != 0)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][ERROR] agc.label_write_failed packet={packet} " +
            $"dst=0x{destinationAddress:X16} data=0x{data:X16} " +
            $"data_sel={dataSelection} count={count} — a suspended WAIT_REG_MEM " +
            "on this label can no longer be satisfied or deadlock-broken.");
    }

    private static void ApplySubmittedRegisters(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        uint op,
        uint register)
    {
        if (op is ItSetShReg or ItSetContextReg or ItSetUconfigReg)
        {
            if (packetLength < 3 ||
                !TryReadUInt32(ctx, packetAddress + sizeof(uint), out var startRegister))
            {
                return;
            }

            var directDestination = op switch
            {
                ItSetShReg => state.ShRegisters,
                ItSetContextReg => state.CxRegisters,
                _ => state.UcRegisters,
            };
            for (uint index = 0; index < packetLength - 2; index++)
            {
                if (!TryReadUInt32(
                        ctx,
                        packetAddress + 8 + ((ulong)index * sizeof(uint)),
                        out var value))
                {
                    return;
                }

                directDestination[startRegister + index] = value;
                if (op == ItSetUconfigReg)
                {
                    ApplyUcIndexTypeIfNeeded(state, startRegister + index, value);
                }
            }

            return;
        }

        if (op != ItNop ||
            register is not (RCxRegsIndirect or RShRegsIndirect or RUcRegsIndirect) ||
            packetLength < 4 ||
            !TryReadUInt32(ctx, packetAddress + sizeof(uint), out var registerCount) ||
            !TryReadUInt64(ctx, packetAddress + 8, out var registersAddress))
        {
            return;
        }

        var destination = register switch
        {
            RCxRegsIndirect => state.CxRegisters,
            RShRegsIndirect => state.ShRegisters,
            _ => state.UcRegisters,
        };
        for (uint index = 0; index < registerCount; index++)
        {
            var entryAddress = registersAddress + ((ulong)index * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var registerOffset) ||
                !TryReadUInt32(ctx, entryAddress + sizeof(uint), out var value))
            {
                return;
            }

            // The indirect table has an explicit count; offset zero is a real
            // context-register index (DB_RENDER_CONTROL), not a terminator.
            // Dropping it leaves stale depth/render-control state active in
            // later passes.
            destination[registerOffset] = value;
            if (register == RUcRegsIndirect)
            {
                ApplyUcIndexTypeIfNeeded(state, registerOffset, value);
            }
        }
    }

    /// <summary>
    /// Test-only view of a parsed graphics context register. False when the
    /// register was never written.
    /// </summary>
    internal static bool TryGetGraphicsContextRegisterForTests(
        CpuContext ctx,
        uint registerOffset,
        out uint value)
    {
        value = 0;
        if (!_submittedGpuStates.TryGetValue(CanonicalMemory(ctx.Memory), out var gpuState))
        {
            return false;
        }

        lock (gpuState.Gate)
        {
            return gpuState.Graphics.CxRegisters.TryGetValue(registerOffset, out value);
        }
    }

    /// <summary>
    /// SH-register counterpart of <see cref="TryGetGraphicsContextRegisterForTests"/>;
    /// the shader stage addresses live here.
    /// </summary>
    internal static bool TryGetGraphicsShRegisterForTests(
        CpuContext ctx,
        uint registerOffset,
        out uint value)
    {
        value = 0;
        if (!_submittedGpuStates.TryGetValue(CanonicalMemory(ctx.Memory), out var gpuState))
        {
            return false;
        }

        lock (gpuState.Gate)
        {
            return gpuState.Graphics.ShRegisters.TryGetValue(registerOffset, out value);
        }
    }

    /// <summary>
    /// GraphicsDcbSetIndexSize writes VGT_INDEX_TYPE via SET_UCONFIG_REG.
    /// Mirror that into <see cref="SubmittedDcbState.IndexSize"/>.
    /// </summary>
    private static void ApplyUcIndexTypeIfNeeded(
        SubmittedDcbState state,
        uint registerOffset,
        uint value)
    {
        if (registerOffset == VgtIndexType)
        {
            state.IndexSize = value & 0x3;
        }
    }

    private const int IndirectArgsFlushTimeoutMilliseconds = 250;

    private static void FlushGpuWorkForIndirectArgs(SubmittedGpuState gpuState)
    {
        var pending = gpuState.WorkSequence;
        if (pending == 0 || pending > long.MaxValue)
        {
            return;
        }

        GuestGpu.Current.WaitForGuestWork(
            (long)pending,
            IndirectArgsFlushTimeoutMilliseconds);
    }

    private static bool TryReadSubmittedDrawCount(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        uint op,
        out uint drawCount)
    {
        drawCount = 0;
        switch (op)
        {
            case ItDrawIndexAuto when packetLength >= 3:
                return TryReadUInt32(ctx, packetAddress + 4, out drawCount);
            case ItDrawIndex2 when packetLength >= 6:
                state.DrawIndexOffset = 0;
                return TryReadUInt32(ctx, packetAddress + 16, out drawCount);
            case ItDrawIndexOffset2 when packetLength >= 5:
                if (!TryReadUInt32(ctx, packetAddress + 8, out var indexOffset))
                {
                    return false;
                }

                state.DrawIndexOffset = indexOffset;
                return TryReadUInt32(ctx, packetAddress + 12, out drawCount);
            case ItDrawIndexMultiAuto when packetLength >= 4:
                if (!TryReadUInt32(ctx, packetAddress + 12, out var control))
                {
                    return false;
                }

                drawCount = (control >> 21) & 0x7FFu;
                return true;
            case ItDrawIndexIndirectMulti when packetLength >= 8 &&
                state.IndirectArgsAddress != 0:
                if (!TryReadUInt32(ctx, packetAddress + 4, out var multiOffset) ||
                    !TryReadUInt32(ctx, packetAddress + 20, out var multiDraws) ||
                    !TryReadUInt32(ctx, packetAddress + 24, out var multiStride))
                {
                    return false;
                }


                if (multiStride < DrawIndexedIndirectArgsSize)
                {
                    multiStride = DrawIndexedIndirectArgsSize;
                }

                var multiTotal = 0UL;
                var multiCapped = multiDraws == 0
                    ? DrawIndexedIndirectMaxScan
                    : Math.Min(multiDraws, 4096u);
                for (var draw = 0u; draw < multiCapped; draw++)
                {
                    if (!TryReadUInt32(
                            ctx,
                            state.IndirectArgsAddress + multiOffset + ((ulong)draw * multiStride),
                            out var subCount))
                    {
                        break;
                    }

                    if (subCount == 0 && multiDraws == 0)
                    {
                        break;
                    }

                    multiTotal += subCount;
                }

                var multiProbe = Interlocked.Increment(ref _indirectMultiProbeCount);
                if (multiProbe <= 12 || multiProbe % 250 == 0)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] agc.draw_multi#{multiProbe} args=0x{state.IndirectArgsAddress:X} " +
                        $"off=0x{multiOffset:X} draws={multiDraws} stride={multiStride} " +
                        $"total={multiTotal}");
                }

                drawCount = (uint)Math.Min(multiTotal, uint.MaxValue);
                return drawCount != 0;
            case ItDrawIndirect or ItDrawIndexIndirect:
                var probe = Interlocked.Increment(ref _indirectDrawProbeCount);
                if (!TryReadUInt32(ctx, packetAddress + 4, out var dataOffset))
                {
                    dataOffset = 0xFFFFFFFFu;
                }

                var readable = packetLength >= 5 &&
                    state.IndirectArgsAddress != 0 &&
                    dataOffset != 0xFFFFFFFFu;
                var resolved = readable &&
                    TryReadUInt32(
                        ctx,
                        state.IndirectArgsAddress + dataOffset,
                        out drawCount);
                if (probe <= 12 || probe % 100 == 0)
                {
                    var dump = string.Empty;
                    for (var word = 0; word < 8; word++)
                    {
                        dump += TryReadUInt32(
                            ctx,
                            state.IndirectArgsAddress + dataOffset + ((ulong)word * 4),
                            out var raw)
                            ? $" {raw}"
                            : " ?";
                    }

                    Console.Error.WriteLine(
                        $"[LOADER][WARN] agc.draw_indirect#{probe} op=0x{op:X} len={packetLength} " +
                        $"args=0x{state.IndirectArgsAddress:X} off=0x{dataOffset:X} " +
                        $"resolved={resolved} count={drawCount} words:{dump}");
                }

                return resolved;
            default:
                return false;
        }
    }

    private static void TryTranslateGuestDraw(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        uint vertexCount,
        bool indexed)
    {
        var hasExportShader = TryGetShaderAddress(
            state.ShRegisters,
            SpiShaderPgmLoEs,
            SpiShaderPgmHiEs,
            out var exportShaderAddress);
        var hasPixelShader = TryGetShaderAddress(
            state.ShRegisters,
            SpiShaderPgmLoPs,
            SpiShaderPgmHiPs,
            out var pixelShaderAddress);
        var hasPsInputEna = state.CxRegisters.TryGetValue(SpiPsInputEna, out var psInputEna);
        var hasPsInputAddr = state.CxRegisters.TryGetValue(SpiPsInputAddr, out var psInputAddr);
        state.UcRegisters.TryGetValue(VgtPrimitiveType, out var primitiveType);
        var renderTargets = GetRenderTargets(state.CxRegisters);
        var drawSequence = ++gpuState.WorkSequence;
        if (state.PendingTargetlessDraw is { } stalePendingDraw)
        {
            ReturnPooledDrawArrays(
                stalePendingDraw,
                globals: true,
                vertex: true,
                index: true);
            state.PendingTargetlessDraw = null;
        }
        state.TranslatedDraw = null;
        state.GuestDrawKind = GuestDrawKind.None;

        // CB modes EliminateFastClear / FmaskDecompress / DccDecompress run
        // colour-buffer metadata ops. The bound shader is only a vehicle and
        // must not be applied as a normal colour draw.
        if (TryGetCbColorControlMode(state.CxRegisters, out var cbMode) &&
            IsCbMetadataColorMode(cbMode))
        {
            if (_traceAgcShader || ShouldTraceHotPath(ref _cbMetadataSkipTraceCount))
            {
                TraceAgcShader(
                    $"agc.cb_metadata_skip seq={drawSequence} mode={cbMode} " +
                    $"es=0x{(hasExportShader ? exportShaderAddress : 0):X16} " +
                    $"ps=0x{(hasPixelShader ? pixelShaderAddress : 0):X16} " +
                    $"vertices={vertexCount}");
            }

            return;
        }

        foreach (var target in renderTargets)
        {
            state.KnownRenderTargets[target.Address] = target;
            // Colour exports originate in the pixel stage.  A depth-only draw
            // can leave old CB registers bound, but it must not become the
            // advertised writer of those surfaces merely because they remain
            // in state.
            if (hasPixelShader)
            {
                state.RenderTargetWriters[target.Address] = new RenderTargetWriter(
                    drawSequence,
                    hasExportShader ? exportShaderAddress : 0,
                    pixelShaderAddress,
                    vertexCount,
                    primitiveType);
            }

            if (_traceAgcShader ||
                Array.IndexOf(_tracePixelShaderAddresses, pixelShaderAddress) >= 0 ||
                _traceRenderTargetAddress == target.Address)
            {
                var pixelInputControls = string.Join(
                    ',',
                    Enumerable.Range(0, 4).Select(index =>
                        state.CxRegisters.TryGetValue(
                            SpiPsInputCntl0 + (uint)index,
                            out var inputControl)
                                ? $"0x{inputControl:X8}"
                                : "missing"));
                var targetBaseRegister = CbColor0Base + target.Slot * CbColorRegisterStride;
                state.CxRegisters.TryGetValue(targetBaseRegister + 3, out var rawView);
                state.CxRegisters.TryGetValue(
                    CbColor0Info + target.Slot * CbColorRegisterStride,
                    out var rawInfo);
                state.CxRegisters.TryGetValue(CbBlend0Control + target.Slot, out var rawBlend);
                var blend = DecodeBlendState(state.CxRegisters, target.Slot);
                Console.Error.WriteLine(
                    "[LOADER][TRACE] " +
                    $"agc.rt_writer seq={drawSequence} target=0x{target.Address:X16} " +
                    $"fmt={target.Format} num={target.NumberType} " +
                    $"swap={target.ComponentSwap} info=0x{rawInfo:X8} " +
                    $"tile={target.TileMode} " +
                    $"size={target.Width}x{target.Height} slot={target.Slot} " +
                    $"view=0x{rawView:X8} blend=0x{rawBlend:X8}:" +
                    $"{(blend.Enable ? 1 : 0)}:{blend.ColorSrcFactor}/" +
                    $"{blend.ColorDstFactor}/{blend.ColorFunc}:" +
                    $"a{blend.AlphaSrcFactor}/{blend.AlphaDstFactor}/{blend.AlphaFunc}:" +
                    $"s{(blend.SeparateAlphaBlend ? 1 : 0)}:m{blend.WriteMask:X} " +
                    $"vertices={vertexCount} " +
                    $"prim=0x{primitiveType:X} indexed={indexed} " +
                    $"es=0x{(hasExportShader ? exportShaderAddress : 0):X16} " +
                    $"ps=0x{(hasPixelShader ? pixelShaderAddress : 0):X16} " +
                    $"ps_inputs=[{pixelInputControls}] " +
                    $"color_write={(hasPixelShader ? 1 : 0)}");
            }
        }

        if (vertexCount == 0 || vertexCount > 1_048_576)
        {
            return;
        }

        var translationError = string.Empty;
        var depthState = DecodeDepthState(state.CxRegisters);
        var depthTarget = DecodeDepthTarget(state.CxRegisters);
        var hasDepthOnlyCandidate = hasExportShader &&
            !hasPixelShader &&
            depthTarget is not null &&
            (depthState.TestEnable || depthState.WriteEnable || depthState.ClearEnable);
        if (hasDepthOnlyCandidate &&
            TryCreateTranslatedDepthOnlyGuestDraw(
                ctx,
                state,
                exportShaderAddress,
                vertexCount,
                indexed,
                depthTarget!,
                out var depthOnlyDraw,
                out translationError))
        {
            state.TranslatedDraw = depthOnlyDraw;
            var activeDepthTarget = depthOnlyDraw.DepthTarget!;
            var textures = CreateGuestDrawTextures(
                ctx,
                depthOnlyDraw.Textures,
                out _);
            var globalMemoryBuffers =
                CreateTranslatedDrawGlobalBuffers(depthOnlyDraw);
            var vertexBuffers =
                CreateGuestVertexBuffers(depthOnlyDraw.VertexInputs);
            var renderState = depthOnlyDraw.RenderState;
            if (activeDepthTarget.ReadOnly && renderState.Depth.WriteEnable)
            {
                renderState = renderState with
                {
                    Depth = renderState.Depth with { WriteEnable = false },
                };
            }

            TraceDrawCompact(
                drawSequence,
                depthOnlyDraw,
                textures,
                vertexBuffers);
            GuestGpu.Current.SubmitDepthOnlyTranslatedDraw(
                depthOnlyDraw.PixelShader,
                textures,
                globalMemoryBuffers,
                depthOnlyDraw.AttributeCount,
                activeDepthTarget,
                depthOnlyDraw.VertexShader,
                depthOnlyDraw.VertexCount,
                depthOnlyDraw.InstanceCount,
                depthOnlyDraw.PrimitiveType,
                depthOnlyDraw.IndexBuffer,
                vertexBuffers,
                renderState,
                depthOnlyDraw.PixelShaderAddress,
                depthOnlyDraw.BaseVertex);

            if (_traceAgcShader)
            {
                TraceAgcShader(
                    $"agc.depth_only_draw seq={drawSequence} " +
                    $"es=0x{exportShaderAddress:X16} " +
                    $"depth=0x{activeDepthTarget.Address:X16}:" +
                    $"{activeDepthTarget.Width}x{activeDepthTarget.Height}:" +
                    $"fmt{activeDepthTarget.GuestFormat}/sw{activeDepthTarget.SwizzleMode} " +
                    $"test={(renderState.Depth.TestEnable ? 1 : 0)} " +
                    $"write={(renderState.Depth.WriteEnable ? 1 : 0)} " +
                    $"func={renderState.Depth.CompareOp} ro={(activeDepthTarget.ReadOnly ? 1 : 0)}");
            }

            return;
        }

        if (hasExportShader &&
            hasPixelShader &&
            hasPsInputEna &&
            hasPsInputAddr &&
            TryCreateTranslatedGuestDraw(
                ctx,
                state,
                exportShaderAddress,
                pixelShaderAddress,
                psInputEna,
                psInputAddr,
                vertexCount,
                indexed,
                out var translatedDraw,
                out translationError))
        {
            state.TranslatedDraw = translatedDraw;
            if (TryGetHardwareColorResolveTargets(
                    state.CxRegisters,
                    out var resolveSource,
                    out var resolveDestination))
            {
                state.KnownRenderTargets[resolveSource.Address] = resolveSource;
                state.KnownRenderTargets[resolveDestination.Address] = resolveDestination;
                ProvideRenderTargetInitialData(ctx, resolveSource);
                if (GuestGpu.Current.TrySubmitGuestImageBlit(
                        resolveSource.Address,
                        resolveSource.Width,
                        resolveSource.Height,
                        resolveSource.Format,
                        resolveSource.NumberType,
                        resolveDestination.Address,
                        resolveDestination.Width,
                        resolveDestination.Height,
                        resolveDestination.Format,
                        resolveDestination.NumberType))
                {
                    state.RenderTargetWriters[resolveDestination.Address] =
                        new RenderTargetWriter(
                            drawSequence,
                            exportShaderAddress,
                            pixelShaderAddress,
                            vertexCount,
                            primitiveType);
                    TraceAgcShader(
                        $"agc.hardware_color_resolve seq={drawSequence} " +
                        $"src=0x{resolveSource.Address:X16}:" +
                        $"{resolveSource.Width}x{resolveSource.Height}:" +
                        $"fmt{resolveSource.Format}/num{resolveSource.NumberType} " +
                        $"dst=0x{resolveDestination.Address:X16}:" +
                        $"{resolveDestination.Width}x{resolveDestination.Height}:" +
                        $"fmt{resolveDestination.Format}/num{resolveDestination.NumberType}");
                    ReturnPooledDrawArrays(
                        translatedDraw,
                        globals: true,
                        vertex: true,
                        index: true);
                    state.TranslatedDraw = null;
                    return;
                }

                TraceAgcShader(
                    $"agc.hardware_color_resolve_unavailable seq={drawSequence} " +
                    $"src=0x{resolveSource.Address:X16} " +
                    $"dst=0x{resolveDestination.Address:X16}");
            }

            // A DCC fast clear writes metadata only; the colour block discards
            // the quad's shaded output. Reset the attachment and drop the draw,
            // which reproduces the observable effect of a clear to zero without
            // modelling DCC block state.
            if (translatedDraw.IsDccFastClear)
            {
                foreach (var target in translatedDraw.GuestTargets)
                {
                    if (target.Address != 0)
                    {
                        VulkanVideoPresenter.RequestGuestColorClear(target.Address);
                    }
                }

                ReturnPooledDrawArrays(
                    translatedDraw,
                    globals: true,
                    vertex: true,
                    index: true);
                state.TranslatedDraw = null;
                return;
            }

            var firstTarget = translatedDraw.RenderTargets.FirstOrDefault();
            if (firstTarget.Address != 0)
            {
                // Render every bound color target. A deferred G-buffer draw
                // writes several targets in one guest pass; we render one bound
                // target per Vulkan pass, each with the pixel variant that
                // routes that target's MRT export slot to the fragment output.
                // Every pass is enqueued in order on the same guest render
                // queue. Share the immutable snapshots between those passes
                // and let only the final pass return pooled arrays after its
                // host upload. Copying the full vertex/global payload for each
                // secondary target made deferred G-buffer draws allocate
                // hundreds of MiB per second on the managed large-object heap.
                var drawRenderTargets = translatedDraw.RenderTargets;
                var lastTargetIndex = 0;
                for (var targetIndex = 1; targetIndex < drawRenderTargets.Count; targetIndex++)
                {
                    if (drawRenderTargets[targetIndex].Address != 0)
                    {
                        lastTargetIndex = targetIndex;
                    }
                }

                var sharedTextures = CreateGuestDrawTextures(
                    ctx,
                    translatedDraw.Textures,
                    out _);
                var sharedGlobalMemoryBuffers =
                    CreateTranslatedDrawGlobalBuffers(translatedDraw);
                var sharedVertexBuffers =
                    CreateGuestVertexBuffers(translatedDraw.VertexInputs);
                TraceRectListVertices(translatedDraw, sharedVertexBuffers);
                TraceGrassDrawVertices(translatedDraw, sharedTextures, sharedVertexBuffers);
                TraceDrawCompact(
                    drawSequence,
                    translatedDraw,
                    sharedTextures,
                    sharedVertexBuffers);
                foreach (var renderTarget in drawRenderTargets)
                {
                    if (renderTarget.Address != 0)
                    {
                        ProvideRenderTargetInitialData(ctx, renderTarget);
                    }
                }

                if (translatedDraw.IsFullscreenColorClear)
                {
                    VulkanVideoPresenter.SubmitOffscreenColorClear(
                        translatedDraw.GuestTargets,
                        translatedDraw.ClearRed,
                        translatedDraw.ClearGreen,
                        translatedDraw.ClearBlue,
                        translatedDraw.ClearAlpha,
                        translatedDraw.PixelShaderAddress);
                }
                else
                {
                    SubmitNggComputePrepass(
                        translatedDraw,
                        sharedTextures,
                        sharedGlobalMemoryBuffers);
                    GuestGpu.Current.SubmitOffscreenTranslatedDraw(
                        translatedDraw.PixelShader,
                        sharedTextures,
                        sharedGlobalMemoryBuffers,
                        translatedDraw.AttributeCount,
                        translatedDraw.GuestTargets,
                        translatedDraw.VertexShader,
                        translatedDraw.Ngg is { } submittedNgg
                            ? checked(submittedNgg.OutputLayout.MaximumPrimitiveCount * 3)
                            : translatedDraw.VertexCount,
                        translatedDraw.Ngg is null
                            ? translatedDraw.InstanceCount
                            : 1,
                        translatedDraw.Ngg is null
                            ? translatedDraw.PrimitiveType
                            : 4,
                        translatedDraw.Ngg is null
                            ? translatedDraw.IndexBuffer
                            : null,
                        translatedDraw.Ngg is null
                            ? sharedVertexBuffers
                            : null,
                        translatedDraw.RenderState,
                        translatedDraw.DepthTarget,
                        translatedDraw.PixelShaderAddress,
                        translatedDraw.BaseVertex);
                }
            }
            else
            {
                if (translatedDraw.DepthTarget is { } translatedDepthTarget)
                {
                    var textures = CreateGuestDrawTextures(
                        ctx,
                        translatedDraw.Textures,
                        out _);
                    var globalMemoryBuffers =
                        CreateTranslatedDrawGlobalBuffers(translatedDraw);
                    var vertexBuffers =
                        CreateGuestVertexBuffers(translatedDraw.VertexInputs);
                    var renderState = translatedDraw.RenderState;
                    if (translatedDepthTarget.ReadOnly && renderState.Depth.WriteEnable)
                    {
                        renderState = renderState with
                        {
                            Depth = renderState.Depth with { WriteEnable = false },
                        };
                    }

                    TraceDrawCompact(
                        drawSequence,
                        translatedDraw,
                        textures,
                        vertexBuffers);
                    GuestGpu.Current.SubmitDepthOnlyTranslatedDraw(
                        translatedDraw.PixelShader,
                        textures,
                        globalMemoryBuffers,
                        translatedDraw.AttributeCount,
                        translatedDepthTarget,
                        translatedDraw.VertexShader,
                        translatedDraw.VertexCount,
                        translatedDraw.InstanceCount,
                        translatedDraw.PrimitiveType,
                        translatedDraw.IndexBuffer,
                        vertexBuffers,
                        renderState,
                        translatedDraw.PixelShaderAddress,
                        translatedDraw.BaseVertex);
                }
                else
                {
                    var storageTarget = translatedDraw.Textures
                        .FirstOrDefault(binding => binding.IsStorage);
                    if (storageTarget is not null)
                    {
                        var textures = CreateGuestDrawTextures(
                            ctx,
                            translatedDraw.Textures,
                            out _);
                        var globalMemoryBuffers =
                            CreateTranslatedDrawGlobalBuffers(translatedDraw);
                        TraceDrawCompact(drawSequence, translatedDraw, textures, []);
                        GuestGpu.Current.SubmitStorageTranslatedDraw(
                            translatedDraw.PixelShader,
                            textures,
                            globalMemoryBuffers,
                            translatedDraw.AttributeCount,
                            storageTarget.Descriptor.Width,
                            storageTarget.Descriptor.Height,
                            translatedDraw.PixelShaderAddress);
                        // The storage submit consumes the global buffers (the
                        // presenter returns them) but never the vertex/index
                        // arrays; return those here so they don't leak the pool.
                        ReturnPooledDrawArrays(
                            translatedDraw,
                            globals: false,
                            vertex: true,
                            index: true);
                    }
                    else
                    {
                        if (translatedDraw.Textures.Count != 0)
                        {
                            // Unity's PS5 final blit can omit CB registers and
                            // rely on the following AGC flip to name the scanout
                            // target. Retain that sampled draw until RFlip, then
                            // enqueue it against the known display surface before
                            // the ordered capture.
                            state.PendingTargetlessDraw = translatedDraw;
                        }
                        else
                        {
                            // No render target, storage sink or sampled source:
                            // nothing can consume this draw.
                            ReturnPooledDrawArrays(
                                translatedDraw,
                                globals: true,
                                vertex: true,
                                index: true);
                        }
                    }
                }
            }

            if (ShouldTraceHotPath(ref _translatedDrawTraceCount))
            {
                TraceAgcShader(
                    $"agc.shader_draw_seen seq={drawSequence} " +
                    $"es=0x{exportShaderAddress:X16} ps=0x{pixelShaderAddress:X16} " +
                    $"target=0x{firstTarget.Address:X16}:{firstTarget.Width}x{firstTarget.Height}:fmt{firstTarget.Format}/tile{firstTarget.TileMode} " +
                    $"textures={translatedDraw.Textures.Count}");
            }

            // Trace-only: broad shader tracing and the address-specific filters
            // share the same detailed draw summary. The latter must stay useful
            // without enabling the extremely noisy global shader trace.
            var traceTargetedDraw =
                Array.IndexOf(_tracePixelShaderAddresses, pixelShaderAddress) >= 0 ||
                _traceVertexShaderAddress == exportShaderAddress ||
                _traceRenderTargetAddress == firstTarget.Address ||
                translatedDraw.Textures.Any(texture =>
                    Array.IndexOf(
                        _traceTextureBindingAddresses,
                        texture.Descriptor.Address) >= 0);
            if (_traceAgcShader || traceTargetedDraw)
            {
                lock (_submitTraceGate)
                {
                    var firstTextureAddress = translatedDraw.Textures.FirstOrDefault()?.Descriptor.Address ?? 0;
                    if (_tracedShaderDraws.Add(
                            (exportShaderAddress, pixelShaderAddress, firstTarget.Address, firstTextureAddress, vertexCount)))
                    {
                        TraceTranslatedGuestDraw(
                            ctx,
                            gpuState,
                            state,
                            translatedDraw,
                            psInputEna,
                            psInputAddr,
                            force: traceTargetedDraw && !_traceAgcShader);
                    }
                }
            }

            return;
        }

        TraceDrawCompactMiss(
            drawSequence,
            vertexCount,
            hasExportShader && hasPixelShader
                ? translationError
                : hasDepthOnlyCandidate && !string.IsNullOrEmpty(translationError)
                    ? $"depth-only: {translationError}"
                : $"missing-shaders es={hasExportShader} ps={hasPixelShader} ena={hasPsInputEna} addr={hasPsInputAddr}");
        TraceShaderTranslationMiss(
            ctx,
            state,
            vertexCount,
            hasExportShader,
            exportShaderAddress,
            hasPixelShader,
            pixelShaderAddress,
            hasPsInputEna,
            psInputEna,
            hasPsInputAddr,
            psInputAddr,
            hasExportShader && hasPixelShader || hasDepthOnlyCandidate
                ? translationError
                : null);
    }

    private static bool TryCreateTranslatedDepthOnlyGuestDraw(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong exportShaderAddress,
        uint vertexCount,
        bool indexed,
        GuestDepthTarget depthTarget,
        out TranslatedGuestDraw draw,
        out string error)
    {
        draw = default!;
        error = string.Empty;
        ulong exportShaderHeader;
        lock (_submitTraceGate)
        {
            _shaderHeadersByCode.TryGetValue(exportShaderAddress, out exportShaderHeader);
        }

        if (TryRegisterEmbeddedCombinedShader(
                ctx,
                exportShaderAddress,
                exportShaderHeader,
                out var embeddedContinuationHeader))
        {
            exportShaderHeader = embeddedContinuationHeader;
        }

        var isCombinedExportShader =
            Gen5ShaderTranslator.IsCombinedShader(ctx, exportShaderAddress);
        var exportUserDataLayout = DecodeExportUserDataLayout(state.ShRegisters);
        state.UcRegisters.TryGetValue(VgtPrimitiveType, out var primitiveType);
        var mergedWaveInfo = isCombinedExportShader
            ? EncodeNggMergedWaveInfo(primitiveType, vertexCount)
            : (uint?)null;

        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                exportShaderAddress,
                exportShaderHeader,
                state.ShRegisters,
                exportUserDataLayout.UserDataRegister,
                out var exportState,
                out error,
                userDataScalarRegisterBase:
                    exportUserDataLayout.ScalarRegisterBase,
                graphicsSystemRegisters: isCombinedExportShader
                    ? DecodeNggGraphicsSystemRegisters(
                        state.ShRegisters,
                        mergedWaveInfo)
                    : null) ||
            !Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                exportState,
                out var exportEvaluation,
                out error,
                resolveVertexInputs:
                    !isCombinedExportShader &&
                    _forceExplicitVertexFetchShaderAddress != exportShaderAddress,
                requiredVertexRecordCount: TryGetRequiredVertexRecordCount(
                    ctx,
                    state,
                    vertexCount,
                    indexed,
                    out var depthVertexRecords)
                        ? depthVertexRecords
                        : null))
        {
            return false;
        }

        var exportFingerprint = _bakeScalars
            ? ComputeShaderStateFingerprint(exportEvaluation)
            : ComputeShaderStructuralFingerprint(exportEvaluation);
        var cacheKey = (
            exportShaderAddress,
            exportFingerprint,
            exportState.ProgramResource1,
            _storageBufferOffsetAlignment);
        _depthOnlyVertexShaderCache.TryGetValue(cacheKey, out var vertexShader);

        if (vertexShader is null)
        {
            var guestGlobalBufferCount = exportEvaluation.GlobalMemoryBindings.Count;
            // CreateTranslatedDrawGlobalBuffers appends both stage scalar
            // blocks.  The pixel block is unused by the fixed fragment stage;
            // the vertex block remains at guestCount+1, matching this layout.
            var totalGlobalBufferCount = _bakeScalars
                ? guestGlobalBufferCount
                : guestGlobalBufferCount + 2;
            if (!GuestGpu.Current.TryCompileVertexShader(
                    exportState,
                    exportEvaluation,
                    out vertexShader,
                    out error,
                    globalBufferBase: 0,
                    totalGlobalBufferCount: totalGlobalBufferCount,
                    imageBindingBase: 0,
                    scalarRegisterBufferIndex: _bakeScalars
                        ? -1
                        : guestGlobalBufferCount + 1,
                    requiredVertexOutputCount: 0,
                    storageBufferOffsetAlignment:
                        _storageBufferOffsetAlignment))
            {
                ReturnPooledEvaluationArrays(exportEvaluation);
                return false;
            }

            DumpCompiledShader(
                "depth-vs",
                exportShaderAddress,
                exportFingerprint,
                vertexShader!,
                exportState.Program);
            GuestGpu.Current.CountShaderCompilation();
            _depthOnlyVertexShaderCache.TryAdd(cacheKey, vertexShader!);
        }

        var textures = new List<TranslatedImageBinding>(
            exportEvaluation.ImageBindings.Count);
        foreach (var binding in exportEvaluation.ImageBindings)
        {
            if (!TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture))
            {
                if (_strictShaderDescriptors)
                {
                    error = $"invalid export texture descriptor at pc=0x{binding.Pc:X}";
                    ReturnPooledEvaluationArrays(exportEvaluation);
                    return false;
                }

                texture = new TextureDescriptor(
                    0,
                    1,
                    1,
                    Gen5TextureFormatR8G8B8A8Unorm,
                    0,
                    0,
                    0,
                    0,
                    0,
                    1,
                    0xFAC);
            }

            textures.Add(new TranslatedImageBinding(
                texture,
                Gen5ShaderTranslator.RequiresStorageImage(
                    binding,
                    exportEvaluation.ImageBindings),
                Gen5ShaderTranslator.IsImageWriteOperation(binding.Opcode),
                binding.MipLevel ?? 0,
                binding.SamplerDescriptor,
                Gen5ShaderTranslator.IsArrayedImageBinding(binding)));
        }

        IReadOnlyList<Gen5VertexInputBinding> vertexInputs =
            exportEvaluation.VertexInputs ?? [];
        var syntheticTarget = new RenderTargetDescriptor(
            Slot: 0,
            Address: 0,
            depthTarget.Width,
            depthTarget.Height,
            Format: 0,
            NumberType: 0,
            ComponentSwap: 0,
            TileMode: 0);
        var renderState = CreateRenderState(state.CxRegisters, syntheticTarget) with
        {
            // A guest pass without a pixel shader has no colour exports.  The
            // presenter uses a private compatibility attachment, so disable
            // all writes to it and expose only the persistent DB result.
            Blends = [GuestBlendState.Default with { WriteMask = 0 }],
        };
        if (depthTarget.Width == 1 &&
            depthTarget.Height == 1 &&
            renderState.Viewport is { } depthViewport)
        {
            var inferredWidth = (uint)Math.Clamp(
                MathF.Ceiling(MathF.Abs(depthViewport.Width)),
                1f,
                16384f);
            var inferredHeight = (uint)Math.Clamp(
                MathF.Ceiling(MathF.Abs(depthViewport.Height)),
                1f,
                16384f);
            if (inferredWidth > 1 || inferredHeight > 1)
            {
                depthTarget = depthTarget with
                {
                    Width = inferredWidth,
                    Height = inferredHeight,
                };
                syntheticTarget = syntheticTarget with
                {
                    Width = inferredWidth,
                    Height = inferredHeight,
                };
                renderState = CreateRenderState(state.CxRegisters, syntheticTarget) with
                {
                    Blends = [GuestBlendState.Default with { WriteMask = 0 }],
                };
            }
        }
        draw = new TranslatedGuestDraw(
            exportShaderAddress,
            PixelShaderAddress: 0,
            primitiveType,
            vertexShader!,
            GuestGpu.Current.GetDepthOnlyFragmentShader(),
            AttributeCount: 0,
            vertexCount,
            state.InstanceCount,
            GetBaseVertex(state),
            indexed ? CreateGuestIndexBuffer(ctx, state, vertexCount) : null,
            textures,
            exportEvaluation.GlobalMemoryBindings,
            vertexInputs,
            RenderTargets: [],
            depthTarget,
            GuestTargets: [],
            renderState,
            PixelUserData: [],
            RawBlendControl: 0,
            RawColorInfo: 0,
            PixelInitialScalars: [],
            exportEvaluation.InitialScalarRegisters);
        return true;
    }

    private static bool TryCreateTranslatedGuestDraw(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong exportShaderAddress,
        ulong pixelShaderAddress,
        uint psInputEna,
        uint psInputAddr,
        uint vertexCount,
        bool indexed,
        out TranslatedGuestDraw draw,
        out string error)
    {
        draw = default!;
        error = string.Empty;
        ulong exportShaderHeader;
        ulong pixelShaderHeader;
        lock (_submitTraceGate)
        {
            _shaderHeadersByCode.TryGetValue(exportShaderAddress, out exportShaderHeader);
            _shaderHeadersByCode.TryGetValue(pixelShaderAddress, out pixelShaderHeader);
        }


        if (TryRegisterEmbeddedCombinedShader(
                ctx,
                exportShaderAddress,
                exportShaderHeader,
                out var embeddedContinuationHeader))
        {
            exportShaderHeader = embeddedContinuationHeader;
        }

        var isCombinedExportShader =
            Gen5ShaderTranslator.IsCombinedShader(ctx, exportShaderAddress);
        var exportUserDataLayout = DecodeExportUserDataLayout(state.ShRegisters);
        state.UcRegisters.TryGetValue(VgtPrimitiveType, out var primitiveType);
        var mergedWaveInfo = isCombinedExportShader
            ? EncodeNggMergedWaveInfo(primitiveType, vertexCount)
            : (uint?)null;

        DumpShaderProgramIfRequested(
            ctx,
            "es",
            exportShaderAddress,
            exportShaderHeader,
            "requested-capture");
        DumpShaderProgramIfRequested(
            ctx,
            "ps",
            pixelShaderAddress,
            pixelShaderHeader,
            "requested-capture");

        // Sequential (not short-circuited into one condition) so a failure
        // after an evaluation succeeded can return that evaluation's pooled
        // buffer arrays to the pool instead of leaking them.
        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                exportShaderAddress,
                exportShaderHeader,
                state.ShRegisters,
                exportUserDataLayout.UserDataRegister,
                out var exportState,
                out error,
                userDataScalarRegisterBase:
                    exportUserDataLayout.ScalarRegisterBase,
                graphicsSystemRegisters: isCombinedExportShader
                    ? DecodeNggGraphicsSystemRegisters(
                        state.ShRegisters,
                        mergedWaveInfo)
                    : null))
        {
            return false;
        }

        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                exportState,
                out var exportEvaluation,
                out error,
                resolveVertexInputs:
                    !isCombinedExportShader &&
                    _forceExplicitVertexFetchShaderAddress != exportShaderAddress,
                requiredVertexRecordCount: TryGetRequiredVertexRecordCount(
                    ctx,
                    state,
                    vertexCount,
                    indexed,
                    out var vertexRecords)
                        ? vertexRecords
                        : null))
        {
            return false;
        }

        if (_forceExplicitVertexFetchShaderAddress == exportShaderAddress)
        {
            Console.Error.WriteLine(
                $"[AGC][VERTEX-FETCH-PROBE] address=0x{exportShaderAddress:X16} " +
                $"mode=explicit-buffer globals={exportEvaluation.GlobalMemoryBindings.Count} " +
                $"attributes={exportEvaluation.VertexInputs?.Count ?? 0}");
        }

        TraceGlobalBufferProbe(
            "vertex",
            exportShaderAddress,
            exportEvaluation);
        TraceIndexedGlobalBufferProbe(
            ctx,
            exportShaderAddress,
            exportEvaluation);
        TraceIndexedGlobalBufferVertexDrawProbe(
            ctx,
            state,
            exportShaderAddress,
            vertexCount,
            indexed,
            exportEvaluation);

        if (_traceVertexShaderAddress == exportShaderAddress)
        {
            TraceVertexBufferState(
                ctx,
                state,
                exportShaderAddress,
                pixelShaderAddress,
                vertexCount,
                indexed,
                exportState,
                exportEvaluation);
        }

        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                pixelShaderAddress,
                pixelShaderHeader,
                state.ShRegisters,
                PsTextureUserDataRegister,
                out var pixelState,
                out error))
        {
            ReturnPooledEvaluationArrays(exportEvaluation);
            return false;
        }

        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                pixelState,
                out var pixelEvaluation,
                out error))
        {
            ReturnPooledEvaluationArrays(exportEvaluation);
            return false;
        }

        TraceGlobalBufferProbe(
            "pixel",
            pixelShaderAddress,
            pixelEvaluation);

        // Empty SRT/EUD is fine for clears/passthroughs that bind nothing
        // (Astro title PS 0x808E88000 is a procedural fullscreen clear).
        // Reject only when evaluation produced image/global slots that
        // collapsed to Address-0 — that layout mismatches SPIR-V and loses
        // the device on QueueSubmit.
        if (pixelState.Metadata is
            {
                ShaderResourceTableSizeDwords: 0,
                ExtendedUserDataSizeDwords: 0,
            } ||
            Gen5ShaderScalarEvaluator.WasEmptySrtScalarPointerFallback(
                pixelShaderAddress))
        {
            var hasAnyImageSlot = pixelEvaluation.ImageBindings.Count > 0;
            var hasUsablePixelImage = false;
            foreach (var binding in pixelEvaluation.ImageBindings)
            {
                if (TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture) &&
                    texture.Address != 0)
                {
                    hasUsablePixelImage = true;
                    break;
                }
            }

            var hasUsablePixelGlobal = pixelEvaluation.GlobalMemoryBindings.Any(
                static binding => binding.BaseAddress != 0);
            var hasPoisonImageSlots = hasAnyImageSlot && !hasUsablePixelImage;
            if (hasPoisonImageSlots && !hasUsablePixelGlobal)
            {
                error = Gen5ShaderScalarEvaluator.WasEmptySrtScalarPointerFallback(
                    pixelShaderAddress)
                    ? "empty-srt-scalar-pointer-fallback"
                    : "empty-srt-no-usable-resources";
                lock (_submitTraceGate)
                {
                    if (_tracedEmptySrtDrawRejects.Add(pixelShaderAddress))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] agc.draw_reject ps=0x{pixelShaderAddress:X16} " +
                            $"es=0x{exportShaderAddress:X16} reason={error}");
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] agc.draw_reject_state ps=0x{pixelShaderAddress:X16} " +
                            $"header=0x{pixelShaderHeader:X16} " +
                            Gen5ShaderTranslator.DescribeState(pixelState));
                        var shDump = new List<string>(16);
                        for (uint reg = 0x8; reg <= 0x1C; reg++)
                        {
                            if (state.ShRegisters.TryGetValue(reg, out var value))
                            {
                                shDump.Add($"0x{reg:X}={value:X8}");
                            }
                        }

                        Console.Error.WriteLine(
                            $"[LOADER][WARN] agc.draw_reject_sh ps=0x{pixelShaderAddress:X16} " +
                            $"[{string.Join(',', shDump)}]");
                        var bindingIndex = 0;
                        foreach (var binding in pixelEvaluation.ImageBindings)
                        {
                            Console.Error.WriteLine(
                                $"[LOADER][WARN] agc.draw_reject_binding ps=0x{pixelShaderAddress:X16} " +
                                $"[{bindingIndex++}] pc=0x{binding.Pc:X} op={binding.Opcode} " +
                                $"resource={FormatShaderDwords(binding.ResourceDescriptor)} " +
                                $"sampler={FormatShaderDwords(binding.SamplerDescriptor)}");
                        }

                        foreach (var binding in pixelEvaluation.GlobalMemoryBindings)
                        {
                            Console.Error.WriteLine(
                                $"[LOADER][WARN] agc.draw_reject_global ps=0x{pixelShaderAddress:X16} " +
                                $"s{binding.ScalarAddress} base=0x{binding.BaseAddress:X16} " +
                                $"bytes={binding.DataLength}");
                        }
                    }
                }

                ReturnPooledEvaluationArrays(exportEvaluation);
                ReturnPooledEvaluationArrays(pixelEvaluation);
                return false;
            }
        }

        if (pixelShaderAddress == 0x0000000500781200 &&
            _traceTitleGlobals)
        {
            TraceAstroTitlePixelGlobals(pixelEvaluation);
        }

        if (pixelShaderAddress == 0x0000000500781200 &&
            _traceTitleGlobalsLive)
        {
            TraceAstroTitlePixelGlobalProbe(pixelEvaluation);
        }

        // Patch BufferFormat from the attrib table onto the V# before host
        // vertex input. IR discovery often keeps a stale float format from the
        // unpatched sharp — that turns UI glyphs into gradient triangles.
        // Match by stride+offset (not bare base address) so interleaved streams
        // keep loading-video bindings intact.
        if (exportEvaluation.VertexInputs is { Count: > 0 } discoveredInputs &&
            AgcVertexMetadata.TryGetVertexTableRegisters(
                ctx,
                exportShaderAddress,
                exportShaderHeader,
                out var vertexTables))
        {
            var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
                ctx,
                exportEvaluation.ScalarRegisters,
                vertexTables,
                discoveredInputs);
            if (!ReferenceEquals(merged, discoveredInputs))
            {
                TraceAgcShader(
                    $"agc.vertex_metadata_format es=0x{exportShaderAddress:X16} " +
                    $"count={merged.Count}");
                exportEvaluation = exportEvaluation with { VertexInputs = merged };
            }
        }

        state.UcRegisters.TryGetValue(VgtPrimitiveType, out var earlyPrimitiveType);
        if (IsRectListPrimitive(earlyPrimitiveType) &&
            (exportEvaluation.VertexInputs is null || exportEvaluation.VertexInputs.Count == 0) &&
            !VertexProgramExportsParameters(exportState.Program) &&
            GetInterpolatedAttributeCount(pixelState) != 0)
        {
            ReturnPooledEvaluationArrays(exportEvaluation);
            ReturnPooledEvaluationArrays(pixelEvaluation);
            error =
                $"rect-list-no-param-exports ps_inputs={GetInterpolatedAttributeCount(pixelState)}";
            TraceAgcShader(
                $"agc.rect_list_skip es=0x{exportShaderAddress:X16} " +
                $"ps=0x{pixelShaderAddress:X16} {error}");
            return false;
        }

        // Every bound color target the shader exports to. Deferred renderers
        // draw a multi-render-target G-buffer (up to eight slots) in one pass.
        // Fall back to slot 0 if we cannot match any export to a bound target.
        var pixelColorExportMasks = pixelState.Program.PixelColorExportMasks;
        var allBoundTargets = GetRenderTargets(state.CxRegisters);
        // At most 8 slots; a manual filter avoids the per-draw LINQ iterator/
        // closure allocations. Slots are distinct, so sorting by slot is stable.
        var selectedTargets = new List<RenderTargetDescriptor>(allBoundTargets.Count);
        foreach (var target in allBoundTargets)
        {
            if (GetPixelColorExportMask(pixelColorExportMasks, target.Slot) != 0)
            {
                selectedTargets.Add(target);
            }
        }

        if (selectedTargets.Count == 0)
        {
            foreach (var target in allBoundTargets)
            {
                if (target.Slot == 0)
                {
                    selectedTargets.Add(target);
                }
            }
        }

        selectedTargets.Sort(static (left, right) => left.Slot.CompareTo(right.Slot));
        var renderTargets = selectedTargets.ToArray();
        if (_traceAgcShader && allBoundTargets.Count > 1)
        {
            TraceAgcShader(
                $"agc.mrt_filter ps=0x{pixelShaderAddress:X16} " +
                $"bound=[{string.Join(",", allBoundTargets.Select(t => $"s{t.Slot}:0x{t.Address:X}:exp{(GetPixelColorExportMask(pixelColorExportMasks, t.Slot) != 0 ? 1 : 0)}"))}] " +
                 $"kept={renderTargets.Length}");
        }

        var renderTargetOutputKinds = new Gen5PixelOutputKind[renderTargets.Length];
        for (var index = 0; index < renderTargets.Length; index++)
        {
            var target = renderTargets[index];
            if (!GuestGpu.Current.TryGetRenderTargetOutputKind(
                    target.Format,
                    target.NumberType,
                    out renderTargetOutputKinds[index]))
            {
                error =
                    $"unsupported color target format={target.Format} number_type={target.NumberType}";
                ReturnPooledEvaluationArrays(exportEvaluation);
                ReturnPooledEvaluationArrays(pixelEvaluation);
                return false;
            }
        }

        var guestRenderState = CreateRenderState(
            state.CxRegisters,
            renderTargets,
            pixelColorExportMasks);
        var hostRenderTargets = BuildHostRenderTargets(
            renderTargets,
            guestRenderState,
            out var hostOutputLocations);

        // Exact packed encoding of the output layout: guest slot (3 bits), host
        // location (3 bits), and output kind (2 bits) per guest export. The
        // separate nibble-packed masks are part of the shader key because
        // aliased guest MRT slots can merge disjoint channels into one Vulkan
        // attachment.
        var outputLayout = 0UL;
        var outputMasks = 0u;
        for (var index = 0; index < renderTargets.Length; index++)
        {
            outputLayout |= (ulong)(
                ((renderTargets[index].Slot & 0x7u) << 5) |
                ((hostOutputLocations[index] & 0x7u) << 2) |
                (uint)renderTargetOutputKinds[index]) << (index * 8);
            outputMasks |=
                (guestRenderState.Blends[index].WriteMask & 0xFu) << (index * 4);
        }

        var attributeCount = GetInterpolatedAttributeCount(pixelState);
        var pixelInputControls = GetPixelInputControls(
            state.CxRegisters,
            attributeCount);
        var inputControlsFingerprint = ComputePixelInputControlsFingerprint(
            pixelInputControls);
        var requiredVertexOutputCount = GetRequiredVertexOutputCount(
            pixelInputControls);
        var exportStateFingerprint = _bakeScalars
            ? ComputeShaderStateFingerprint(exportEvaluation)
            : ComputeShaderStructuralFingerprint(exportEvaluation);
        var pixelStateFingerprint = _bakeScalars
            ? ComputeShaderStateFingerprint(pixelEvaluation)
            : ComputeShaderStructuralFingerprint(pixelEvaluation);
        var usesGds = pixelState.Program.Instructions.Any(static instruction =>
            (instruction.Opcode is "DsConsume" or "DsAppend") &&
            instruction.Control is Gen5DataShareControl { Gds: true });

        var guestGlobalBuffers =
            pixelEvaluation.GlobalMemoryBindings.Count +
            exportEvaluation.GlobalMemoryBindings.Count;
        // Runtime scalar blocks and the optional GDS allocation ride after the
        // guest buffers: [pixel guest][vertex guest][pixel sgprs][vertex sgprs][gds].
        var scalarBufferCount = _bakeScalars ? 0 : 2;
        var gdsBufferIndex = usesGds
            ? guestGlobalBuffers + scalarBufferCount
            : -1;
        var baseGlobalBufferCount = (_bakeScalars
            ? guestGlobalBuffers
            : guestGlobalBuffers + 2) + (usesGds ? 1 : 0);
        var useFixedFullscreenClear = IsProceduralFullscreenClearPair(
            exportState,
            exportEvaluation,
            pixelState,
            pixelEvaluation);
        var fullscreenClearColor = useFixedFullscreenClear
            ? DecodeSolidClearColor(pixelEvaluation)
            : default;
        if (useFixedFullscreenClear)
        {
            lock (_submitTraceGate)
            {
                if (_tracedFixedFullscreenClears.Add(
                        (exportShaderAddress, pixelShaderAddress)))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] agc.shader_color_clear " +
                        $"es=0x{exportShaderAddress:X16} " +
                        $"ps=0x{pixelShaderAddress:X16} " +
                        $"rgba=({fullscreenClearColor.Red:0.###}," +
                        $"{fullscreenClearColor.Green:0.###}," +
                        $"{fullscreenClearColor.Blue:0.###}," +
                        $"{fullscreenClearColor.Alpha:0.###})");
                }
            }
        }

        var pixelOutputs = new Gen5PixelOutputBinding[renderTargets.Length];
        for (var location = 0; location < renderTargets.Length; location++)
        {
            pixelOutputs[location] = new Gen5PixelOutputBinding(
                renderTargets[location].Slot,
                hostOutputLocations[location],
                renderTargetOutputKinds[location],
                guestRenderState.Blends[location].WriteMask);
        }

        (IGuestCompiledShader Vertex, IGuestCompiledShader Pixel) compiled = default;
        TranslatedNggDraw? translatedNgg = null;
        var totalGlobalBuffers = baseGlobalBufferCount;
        if (!useFixedFullscreenClear &&
            _enableNggComputeRaster &&
            isCombinedExportShader &&
            TryGetNggParameterCount(
                exportState.Program,
                primitiveType,
                vertexCount,
                state.InstanceCount,
                out var nggParameterCount))
        {
            totalGlobalBuffers = checked(baseGlobalBufferCount + 1);
            var nggOutputLayout = new Gen5NggOutputLayout(
                baseGlobalBufferCount,
                MaximumPrimitiveCount: 64,
                MaximumVertexCount: 64,
                nggParameterCount);
            var nggShaderKey = (
                exportShaderAddress,
                exportStateFingerprint,
                exportState.ProgramResource1,
                pixelShaderAddress,
                pixelStateFingerprint,
                pixelState.ProgramResource1,
                outputLayout,
                outputMasks,
                (uint)renderTargets.Length,
                attributeCount,
                psInputEna,
                psInputAddr,
                inputControlsFingerprint,
                usesGds,
                nggParameterCount,
                _storageBufferOffsetAlignment);
            _nggGraphicsShaderCache.TryGetValue(nggShaderKey, out var nggCompiled);
            if (nggCompiled.Compute is null ||
                nggCompiled.Vertex is null ||
                nggCompiled.Pixel is null)
            {
                if (GuestGpu.Current.TryCompileNggComputeShader(
                        exportState,
                        exportEvaluation,
                        nggOutputLayout,
                        out var computeShader,
                        out var nggError,
                        globalBufferBase:
                            pixelEvaluation.GlobalMemoryBindings.Count,
                        totalGlobalBufferCount: totalGlobalBuffers,
                        imageBindingBase: pixelEvaluation.ImageBindings.Count,
                        initialScalarBufferIndex: _bakeScalars
                            ? -1
                            : guestGlobalBuffers + 1,
                        storageBufferOffsetAlignment:
                            _storageBufferOffsetAlignment) &&
                    GuestGpu.Current.TryCreateNggRasterVertexShader(
                        nggOutputLayout,
                        totalGlobalBuffers,
                        pixelInputControls,
                        out var rasterShader,
                        out nggError) &&
                    GuestGpu.Current.TryCompilePixelShader(
                        pixelState,
                        pixelEvaluation,
                        pixelOutputs,
                        out var pixelShader,
                        out nggError,
                        globalBufferBase: 0,
                        totalGlobalBufferCount: totalGlobalBuffers,
                        imageBindingBase: 0,
                        scalarRegisterBufferIndex: _bakeScalars
                            ? -1
                            : guestGlobalBuffers,
                        pixelInputEnable: psInputEna,
                        pixelInputAddress: psInputAddr,
                        storageBufferOffsetAlignment:
                            _storageBufferOffsetAlignment,
                        pixelInputControls: pixelInputControls,
                        gdsBufferIndex: gdsBufferIndex))
                {
                    nggCompiled = (computeShader!, rasterShader!, pixelShader!);
                    DumpCompiledShader(
                        "ngg-cs",
                        exportShaderAddress,
                        exportStateFingerprint,
                        nggCompiled.Compute,
                        exportState.Program);
                    DumpCompiledShader(
                        "ngg-raster-vs",
                        exportShaderAddress,
                        exportStateFingerprint,
                        nggCompiled.Vertex,
                        exportState.Program);
                    DumpCompiledShader(
                        "ps",
                        pixelShaderAddress,
                        pixelStateFingerprint,
                        nggCompiled.Pixel,
                        pixelState.Program);
                    GuestGpu.Current.CountShaderCompilation();
                    _nggGraphicsShaderCache.TryAdd(nggShaderKey, nggCompiled);
                }
                else
                {
                    TraceAgcShader(
                        $"agc.ngg_lowering_unavailable es=0x{exportShaderAddress:X16} " +
                        $"reason={nggError}");
                }
            }

            if (nggCompiled.Compute is not null &&
                nggCompiled.Vertex is not null &&
                nggCompiled.Pixel is not null)
            {
                compiled = (nggCompiled.Vertex, nggCompiled.Pixel);
                translatedNgg = new TranslatedNggDraw(
                    nggCompiled.Compute,
                    nggOutputLayout,
                    CreateNggOutputBuffer(exportShaderAddress, nggOutputLayout));
            }
            else
            {
                totalGlobalBuffers = baseGlobalBufferCount;
            }
        }

        if (translatedNgg is null)
        {
            var shaderKey = (
                exportShaderAddress,
                exportStateFingerprint,
                exportState.ProgramResource1,
                pixelShaderAddress,
                pixelStateFingerprint,
                pixelState.ProgramResource1,
                outputLayout,
                outputMasks,
                (uint)renderTargets.Length,
                attributeCount,
                psInputEna,
                psInputAddr,
                inputControlsFingerprint,
                usesGds,
                _storageBufferOffsetAlignment);
            _graphicsShaderCache.TryGetValue(shaderKey, out compiled);
            if (useFixedFullscreenClear)
            {
                if (compiled.Vertex is null || compiled.Pixel is null)
                {
                    compiled = (
                        GuestGpu.Current.GetDepthOnlyFragmentShader(),
                        GuestGpu.Current.GetDepthOnlyFragmentShader());
                    _graphicsShaderCache.TryAdd(shaderKey, compiled);
                }
            }
            else if (compiled.Vertex is null || compiled.Pixel is null)
            {
                if (!GuestGpu.Current.TryCompilePixelShader(
                    pixelState,
                    pixelEvaluation,
                    pixelOutputs,
                    out var pixelShader,
                    out error,
                    globalBufferBase: 0,
                    totalGlobalBufferCount: totalGlobalBuffers,
                    imageBindingBase: 0,
                    scalarRegisterBufferIndex: _bakeScalars ? -1 : guestGlobalBuffers,
                    pixelInputEnable: psInputEna,
                    pixelInputAddress: psInputAddr,
                    storageBufferOffsetAlignment:
                        _storageBufferOffsetAlignment,
                    pixelInputControls: pixelInputControls,
                    gdsBufferIndex: gdsBufferIndex) ||
                !GuestGpu.Current.TryCompileVertexShader(
                    exportState,
                    exportEvaluation,
                    out var vertexShader,
                    out error,
                    globalBufferBase: pixelEvaluation.GlobalMemoryBindings.Count,
                    totalGlobalBufferCount: totalGlobalBuffers,
                    imageBindingBase: pixelEvaluation.ImageBindings.Count,
                    scalarRegisterBufferIndex: _bakeScalars ? -1 : guestGlobalBuffers + 1,
                    requiredVertexOutputCount: requiredVertexOutputCount,
                    storageBufferOffsetAlignment:
                        _storageBufferOffsetAlignment,
                    pixelInputControls: pixelInputControls))
                {
                    ReturnPooledEvaluationArrays(exportEvaluation);
                    ReturnPooledEvaluationArrays(pixelEvaluation);
                    return false;
                }

                compiled = (vertexShader!, pixelShader!);
                DumpCompiledShader(
                    "vs",
                    exportShaderAddress,
                    exportStateFingerprint,
                    compiled.Vertex,
                    exportState.Program);
                DumpCompiledShader(
                    "ps",
                    pixelShaderAddress,
                    pixelStateFingerprint,
                    compiled.Pixel,
                    pixelState.Program);
                GuestGpu.Current.CountShaderCompilation();
                _graphicsShaderCache.TryAdd(shaderKey, compiled);
            }
        }

        var imageBindings = pixelEvaluation.ImageBindings
            .Concat(exportEvaluation.ImageBindings)
            .ToArray();
        var textures = new List<TranslatedImageBinding>(
            pixelEvaluation.ImageBindings.Count +
            exportEvaluation.ImageBindings.Count);
        if (!TryAppendTranslatedImageBindings(
                pixelEvaluation.ImageBindings,
                imageBindings,
                textures,
                pixelShaderAddress,
                exportShaderAddress,
                out error) ||
            !TryAppendTranslatedImageBindings(
                exportEvaluation.ImageBindings,
                imageBindings,
                textures,
                pixelShaderAddress,
                exportShaderAddress,
                out error))
        {
            ReturnPooledEvaluationArrays(exportEvaluation);
            ReturnPooledEvaluationArrays(pixelEvaluation);
            return false;
        }

        var globalMemoryBindings = new Gen5GlobalMemoryBinding[
            pixelEvaluation.GlobalMemoryBindings.Count +
            exportEvaluation.GlobalMemoryBindings.Count];
        for (var index = 0; index < pixelEvaluation.GlobalMemoryBindings.Count; index++)
        {
            globalMemoryBindings[index] = pixelEvaluation.GlobalMemoryBindings[index];
        }
        for (var index = 0; index < exportEvaluation.GlobalMemoryBindings.Count; index++)
        {
            globalMemoryBindings[pixelEvaluation.GlobalMemoryBindings.Count + index] =
                exportEvaluation.GlobalMemoryBindings[index];
        }
        for (var index = 0; index < globalMemoryBindings.Length; index++)
        {
            var binding = globalMemoryBindings[index];
            if (!AvPlayerExports.ShouldTraceVideoBufferRange(
                    binding.BaseAddress,
                    checked((ulong)Math.Max(binding.DataLength, 1))))
            {
                continue;
            }

            var pixelStage = index < pixelEvaluation.GlobalMemoryBindings.Count;
            var stage = pixelStage ? "ps" : "es";
            var shaderAddress = pixelStage ? pixelShaderAddress : exportShaderAddress;
            if (!_tracedAvPlayerGlobalBindings.TryAdd(
                    (stage,
                     shaderAddress,
                     binding.BaseAddress,
                     binding.DataLength,
                     binding.Writable),
                    0))
            {
                continue;
            }

            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.avplayer_global_binding stage={stage} " +
                $"shader=0x{shaderAddress:X16} base=0x{binding.BaseAddress:X16} " +
                $"bytes={binding.DataLength} writable={(binding.Writable ? 1 : 0)} " +
                $"scalar=s{binding.ScalarAddress} pcs=[{string.Join(',', binding.InstructionPcs.Select(static pc => $"0x{pc:X}"))}]");
        }
        IReadOnlyList<Gen5VertexInputBinding> vertexInputs =
            exportEvaluation.VertexInputs ?? [];
        var guestTargets = new GuestRenderTarget[hostRenderTargets.Length];
        for (var index = 0; index < hostRenderTargets.Length; index++)
        {
            guestTargets[index] = new GuestRenderTarget(
                hostRenderTargets[index].Address,
                hostRenderTargets[index].Width,
                hostRenderTargets[index].Height,
                hostRenderTargets[index].Format,
                hostRenderTargets[index].NumberType,
                ComponentSwap: hostRenderTargets[index].ComponentSwap);
        }

        var adjustedGuestRenderState = ApplyTransparentPremultipliedFillClear(
            guestRenderState,
            textures,
            vertexInputs,
            pixelEvaluation.InitialScalarRegisters);
        var hostRenderState = CollapseGuestRenderState(
            adjustedGuestRenderState,
            hostOutputLocations,
            hostRenderTargets.Length);
        var pixelUserDataCount = Math.Min(pixelEvaluation.InitialScalarRegisters.Count, 8);
        var pixelUserData = new uint[pixelUserDataCount];
        for (var index = 0; index < pixelUserDataCount; index++)
        {
            pixelUserData[index] = pixelEvaluation.InitialScalarRegisters[index];
        }

        var renderState = ApplyTransparentPremultipliedFillClear(
            CreateRenderState(state.CxRegisters, renderTargets, pixelColorExportMasks),
            textures,
            vertexInputs,
            pixelEvaluation.InitialScalarRegisters);

        draw = new TranslatedGuestDraw(
            exportShaderAddress,
            pixelShaderAddress,
            primitiveType,
            compiled.Vertex!,
            compiled.Pixel!,
            GetInterpolatedAttributeCount(pixelState),
            vertexCount,
            state.InstanceCount,
            GetBaseVertex(state),
            translatedNgg is null && indexed
                ? CreateGuestIndexBuffer(ctx, state, vertexCount)
                : null,
            textures,
            globalMemoryBindings,
            translatedNgg is null ? vertexInputs : [],
            renderTargets,
            DecodeDepthTarget(state.CxRegisters),
            guestTargets,
            hostRenderState,
            pixelUserData,
            state.CxRegisters.TryGetValue(CbBlend0Control, out var rawBlend) ? rawBlend : 0,
            state.CxRegisters.TryGetValue(
                CbColor0Info + renderTargets.FirstOrDefault().Slot * CbColorRegisterStride,
                out var rawInfo)
                ? rawInfo
                : 0,
            pixelEvaluation.InitialScalarRegisters,
            exportEvaluation.InitialScalarRegisters,
            UsesGds: usesGds,
            Ngg: translatedNgg,
            IsFullscreenColorClear: useFixedFullscreenClear,
            ClearRed: fullscreenClearColor.Red,
            ClearGreen: fullscreenClearColor.Green,
            ClearBlue: fullscreenClearColor.Blue,
            ClearAlpha: fullscreenClearColor.Alpha);
        return true;
    }

    private static bool TryGetNggParameterCount(
        Gen5ShaderProgram program,
        uint primitiveType,
        uint vertexCount,
        uint instanceCount,
        out uint parameterCount)
    {
        parameterCount = 0;
        if (primitiveType != 1 ||
            vertexCount is 0 or > 64 ||
            instanceCount != 1 ||
            !program.Instructions.Any(static instruction =>
                instruction.Opcode == "SBarrier"))
        {
            return false;
        }

        var hasPrimitiveExport = false;
        var hasPositionExport = false;
        foreach (var instruction in program.Instructions)
        {
            if (instruction.Control is not Gen5ExportControl export)
            {
                continue;
            }

            hasPrimitiveExport |= export.Target == 20;
            hasPositionExport |= export.Target == 12;
            if (export.Target is >= 32 and < 64)
            {
                parameterCount = Math.Max(
                    parameterCount,
                    export.Target - 31);
            }
        }

        return hasPrimitiveExport && hasPositionExport;
    }

    private static GuestMemoryBuffer CreateNggOutputBuffer(
        ulong exportShaderAddress,
        Gen5NggOutputLayout layout)
    {
        var data = _nggOutputBuffers.GetOrAdd(
            (exportShaderAddress, layout.ByteLength),
            static key => new byte[key.Bytes]);
        var address = checked(
            SyntheticNggOutputBaseAddress +
            (exportShaderAddress & 0x0000_0000_0FFF_F000ul));
        return new GuestMemoryBuffer(
            address,
            data,
            layout.ByteLength,
            Pooled: false,
            Writable: true,
            WriteBackToGuest: false);
    }

    private static void TraceVertexBufferState(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong exportShaderAddress,
        ulong pixelShaderAddress,
        uint drawCount,
        bool indexed,
        Gen5ShaderState shaderState,
        Gen5ShaderEvaluation evaluation)
    {
        var hasRecordProbe = uint.TryParse(
            _traceVertexBufferRecord,
            out var recordProbe);
        var initialScalars = string.Join(
            ',',
            evaluation.InitialScalarRegisters
                .Skip(16)
                .Take(20)
                .Select((value, index) => $"s{index + 16}={value:X8}"));
        var initialSystemScalars = string.Join(
            ',',
            evaluation.InitialScalarRegisters
                .Take(16)
                .Select((value, index) => $"s{index}={value:X8}"));
        var evaluatedScalars = string.Join(
            ',',
            evaluation.ScalarRegisters
                .Skip(16)
                .Take(20)
                .Select((value, index) => $"s{index + 16}={value:X8}"));
        var evaluatedVertexInputs = string.Join(
            ',',
            evaluation.ScalarRegisters
                .Skip(68)
                .Take(4)
                .Select((value, index) => $"s{index + 68}={value:X8}"));
        var bindings = string.Join(
            ';',
            evaluation.GlobalMemoryBindings.Select((binding, index) =>
            {
                var headLength = Math.Min(binding.DataLength, 32);
                var head = headLength == 0
                    ? string.Empty
                    : Convert.ToHexString(binding.Data.AsSpan(0, headLength));
                var byteBias = binding.BaseAddress &
                    (_storageBufferOffsetAlignment - 1);
                var recordSample = string.Empty;
                if (hasRecordProbe &&
                    binding.ScalarAddress + 1 <
                    (uint)evaluation.InitialScalarRegisters.Count)
                {
                    var stride =
                        (evaluation.InitialScalarRegisters[(int)binding.ScalarAddress + 1] >> 16) &
                        0x3FFFu;
                    var recordOffset = (ulong)recordProbe * stride;
                    if (stride != 0 && recordOffset < (ulong)binding.DataLength)
                    {
                        var sampleLength = Math.Min(
                            (int)stride,
                            Math.Min(64, binding.DataLength - (int)recordOffset));
                        recordSample =
                            $":record={recordProbe}:stride={stride}:offset={recordOffset}:" +
                            $"sample={Convert.ToHexString(binding.Data.AsSpan(
                                (int)recordOffset,
                                sampleLength))}";
                    }
                }
                return
                    $"b{index}:s{binding.ScalarAddress}:" +
                    $"base=0x{binding.BaseAddress:X16}:bytes={binding.DataLength}:" +
                    $"bias={byteBias}:writeback={(binding.WriteBackToGuest ? 1 : 0)}:" +
                    $"pcs={string.Join(',', binding.InstructionPcs.Select(pc => $"0x{pc:X}"))}:" +
                    $"head={head}{recordSample}";
            }));
        var key = $"{exportShaderAddress:X16}|{initialScalars}|{bindings}";
        lock (_submitTraceGate)
        {
            if (!_tracedVertexBufferStates.Add(key))
            {
                return;
            }
        }

        Console.Error.WriteLine(
            $"[AGC][VERTEX-BUFFER-STATE] " +
            $"es=0x{exportShaderAddress:X16} ps=0x{pixelShaderAddress:X16} " +
            $"draw_count={drawCount} instances={state.InstanceCount} " +
            $"indexed={(indexed ? 1 : 0)} " +
            $"system=[{initialSystemScalars}] " +
            $"initial=[{initialScalars}] evaluated=[{evaluatedScalars}] " +
            $"vertex_inputs=[{evaluatedVertexInputs}] " +
            $"bindings=[{bindings}]");

        state.CxRegisters.TryGetValue(VgtShaderStagesEn, out var shaderStages);
        state.ShRegisters.TryGetValue(SpiShaderPgmRsrc1Gs, out var gsRsrc1);
        state.ShRegisters.TryGetValue(SpiShaderPgmRsrc2Gs, out var gsRsrc2);
        state.ShRegisters.TryGetValue(
            GsIndirectUserDataLowRegister,
            out var gsIndirectUserDataLow);
        state.ShRegisters.TryGetValue(
            GsIndirectUserDataHighRegister,
            out var gsIndirectUserDataHigh);
        state.UcRegisters.TryGetValue(GeCntl, out var geCntl);
        state.UcRegisters.TryGetValue(GeUserVgprEn, out var geUserVgprEn);
        state.UcRegisters.TryGetValue(GeUserVgpr1, out var geUserVgpr1);
        state.UcRegisters.TryGetValue(GeUserVgpr2, out var geUserVgpr2);
        state.UcRegisters.TryGetValue(GeUserVgpr3, out var geUserVgpr3);
        Console.Error.WriteLine(
            $"[AGC][SHADER-STAGE-STATE] target_es=0x{exportShaderAddress:X16} " +
            $"stages=0x{shaderStages:X8} " +
            $"primgen={(shaderStages >> 13) & 1} " +
            $"passthru={(shaderStages >> 25) & 1} " +
            $"ls={DescribeSubmittedShaderStage(ctx, state, SpiShaderPgmLoLs, SpiShaderPgmHiLs)} " +
            $"es={DescribeSubmittedShaderStage(ctx, state, SpiShaderPgmLoEs, SpiShaderPgmHiEs)} " +
            $"ps={DescribeSubmittedShaderStage(ctx, state, SpiShaderPgmLoPs, SpiShaderPgmHiPs)} " +
            $"gs_rsrc1=0x{gsRsrc1:X8}:vgpr_comp={(gsRsrc1 >> 29) & 3} " +
            $"gs_rsrc2=0x{gsRsrc2:X8}:es_vgpr_comp={(gsRsrc2 >> 16) & 3}:" +
            $"lds_size={(gsRsrc2 >> 19) & 0xFF} " +
            $"gs_indirect_ud=0x{gsIndirectUserDataHigh:X8}{gsIndirectUserDataLow:X8} " +
            $"ge_cntl=0x{geCntl:X8} " +
            $"user_vgpr_en=0x{geUserVgprEn:X8} " +
            $"user_vgpr=[0x{geUserVgpr1:X8},0x{geUserVgpr2:X8},0x{geUserVgpr3:X8}]");

        TraceVertexShaderInstructions(exportShaderAddress, shaderState);

        if (_traceVertexBufferDistribution)
        {
            TraceVertexBufferDistribution(
                ctx,
                state,
                exportShaderAddress,
                drawCount,
                indexed,
                evaluation);
        }
    }

    private static void TraceVertexShaderInstructions(
        ulong exportShaderAddress,
        Gen5ShaderState shaderState)
    {
        lock (_submitTraceGate)
        {
            if (!_tracedVertexShaderInstructions.Add(exportShaderAddress))
            {
                return;
            }
        }

        var traceFullProgram = _traceVertexShaderFull;
        foreach (var instruction in shaderState.Program.Instructions)
        {
            // The title failure is established by the first global accesses.
            // Retain every instruction through that setup region, then only
            // memory/export operations so this remains useful for other shaders
            // without producing an unbounded disassembly log.
            if (!traceFullProgram &&
                instruction.Pc > 0x220 &&
                instruction.Control is not (
                    Gen5GlobalMemoryControl or
                    Gen5BufferMemoryControl or
                    Gen5ScalarMemoryControl or
                    Gen5DataShareControl or
                    Gen5ExportControl))
            {
                continue;
            }

            var control = instruction.Control switch
            {
                Gen5GlobalMemoryControl global =>
                    $"global:s{global.ScalarAddress}:vaddr=v{global.VectorAddress}:" +
                    $"vdata=v{global.VectorData}:dw={global.DwordCount}:off={global.OffsetBytes}",
                Gen5BufferMemoryControl buffer =>
                    $"buffer:s{buffer.ScalarResource}:vaddr=v{buffer.VectorAddress}:" +
                    $"vdata=v{buffer.VectorData}:dw={buffer.DwordCount}:off={buffer.OffsetBytes}:" +
                    $"idx={(buffer.IndexEnabled ? 1 : 0)}:offen={(buffer.OffsetEnabled ? 1 : 0)}",
                Gen5ScalarMemoryControl scalar =>
                    $"scalar:dw={scalar.DestinationCount}:off={scalar.ImmediateOffsetBytes}:" +
                    $"dyn={(scalar.DynamicOffsetRegister is { } dynamicRegister ? $"s{dynamicRegister}" : "-")}",
                Gen5DataShareControl dataShare =>
                    $"lds:off0={dataShare.Offset0}:off1={dataShare.Offset1}:gds={(dataShare.Gds ? 1 : 0)}",
                Gen5ExportControl export =>
                    $"export:target=0x{export.Target:X}:mask=0x{export.EnableMask:X}:" +
                    $"done={(export.Done ? 1 : 0)}:vm={(export.ValidMask ? 1 : 0)}",
                _ => "-",
            };
            Console.Error.WriteLine(
                $"[AGC][VERTEX-IR] es=0x{exportShaderAddress:X16} " +
                $"pc=0x{instruction.Pc:X} enc={instruction.Encoding} op={instruction.Opcode} " +
                $"words={string.Join(',', instruction.Words.Select(word => $"{word:X8}"))} " +
                $"src={string.Join('/', instruction.Sources)} " +
                $"dst={string.Join('/', instruction.Destinations)} control={control}");
        }
    }

    private static void TraceVertexBufferDistribution(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong exportShaderAddress,
        uint drawCount,
        bool indexed,
        Gen5ShaderEvaluation evaluation)
    {
        var indices = ReadDiagnosticDrawIndices(
            ctx,
            state,
            drawCount,
            indexed,
            out var indicesComplete);
        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            if (binding.ScalarAddress + 1 >=
                (uint)evaluation.InitialScalarRegisters.Count)
            {
                continue;
            }

            var stride =
                (evaluation.InitialScalarRegisters[(int)binding.ScalarAddress + 1] >> 16) &
                0x3FFFu;
            if (stride == 0 || binding.DataLength == 0)
            {
                continue;
            }

            var data = binding.Data.AsSpan(0, binding.DataLength);
            long nonzeroBytes = 0;
            var nonzeroPages = 0;
            var nonzeroRecords = 0;
            var previousPage = -1;
            var previousRecord = -1;
            var firstNonzeroRecord = -1;
            var lastNonzeroRecord = -1;
            var hash = 14695981039346656037UL;
            for (var offset = 0; offset < data.Length; offset++)
            {
                var value = data[offset];
                hash = unchecked((hash ^ value) * 1099511628211UL);
                if (value == 0)
                {
                    continue;
                }

                nonzeroBytes++;
                var page = offset / 4096;
                if (page != previousPage)
                {
                    nonzeroPages++;
                    previousPage = page;
                }

                var record = offset / (int)stride;
                if (record != previousRecord)
                {
                    nonzeroRecords++;
                    previousRecord = record;
                    firstNonzeroRecord = firstNonzeroRecord < 0
                        ? record
                        : firstNonzeroRecord;
                    lastNonzeroRecord = record;
                }
            }

            var distributionKey =
                $"{exportShaderAddress:X16}|{binding.ScalarAddress}|" +
                $"{binding.BaseAddress:X16}|{binding.DataLength}|{stride}|{hash:X16}";
            lock (_submitTraceGate)
            {
                if (_tracedVertexBufferDistributions.Count >= 128 ||
                    !_tracedVertexBufferDistributions.Add(distributionKey))
                {
                    continue;
                }
            }

            var referenced = new List<string>(indices.Count);
            var validReferenced = 0;
            var nonzeroReferenced = 0;
            foreach (var index in indices.Distinct())
            {
                var recordOffset = (ulong)index * stride;
                if (recordOffset >= (ulong)data.Length)
                {
                    referenced.Add($"{index}:oor");
                    continue;
                }

                validReferenced++;
                var recordLength = Math.Min(
                    (int)stride,
                    data.Length - (int)recordOffset);
                var isNonzero = ContainsNonzero(
                    data.Slice((int)recordOffset, recordLength));
                if (isNonzero)
                {
                    nonzeroReferenced++;
                }
                referenced.Add($"{index}:{(isNonzero ? "nonzero" : "zero")}");
            }

            Console.Error.WriteLine(
                $"[AGC][VERTEX-BUFFER-DISTRIBUTION] " +
                $"es=0x{exportShaderAddress:X16} saddr=s{binding.ScalarAddress} " +
                $"base=0x{binding.BaseAddress:X16} bytes={binding.DataLength} " +
                $"stride={stride} records={binding.DataLength / stride} " +
                $"nonzero_bytes={nonzeroBytes} nonzero_pages={nonzeroPages} " +
                $"nonzero_records={nonzeroRecords} " +
                $"first_nonzero_record={firstNonzeroRecord} " +
                $"last_nonzero_record={lastNonzeroRecord} fnv64=0x{hash:X16} " +
                $"draw_count={drawCount} indexed={(indexed ? 1 : 0)} " +
                $"indices_complete={(indicesComplete ? 1 : 0)} " +
                $"unique_indices={referenced.Count} valid_indices={validReferenced} " +
                $"nonzero_indices={nonzeroReferenced} " +
                $"indices=[{string.Join(',', referenced)}]");
        }
    }

    private static List<uint> ReadDiagnosticDrawIndices(
        CpuContext ctx,
        SubmittedDcbState state,
        uint drawCount,
        bool indexed,
        out bool complete)
    {
        const uint maxDiagnosticIndices = 4096;
        var count = Math.Min(drawCount, maxDiagnosticIndices);
        complete = drawCount <= maxDiagnosticIndices;
        var indices = new List<uint>((int)count);
        if (!indexed)
        {
            for (uint index = 0; index < count; index++)
            {
                indices.Add(index);
            }
            return indices;
        }

        if (state.IndexBufferAddress == 0 || count == 0)
        {
            complete = false;
            return indices;
        }

        var is32Bit = state.IndexSize != 0;
        var bytesPerIndex = is32Bit ? sizeof(uint) : sizeof(ushort);
        var byteOffset = checked((ulong)state.DrawIndexOffset * (uint)bytesPerIndex);
        var byteCount = checked((int)(count * (uint)bytesPerIndex));
        var bytes = new byte[byteCount];
        var address = state.IndexBufferAddress + byteOffset;
        if (!ctx.Memory.TryRead(address, bytes) &&
            !KernelMemoryCompatExports.TryReadTrackedLibcHeap(address, bytes))
        {
            complete = false;
            return indices;
        }

        for (var index = 0; index < (int)count; index++)
        {
            var value = is32Bit
                ? BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(index * sizeof(uint), sizeof(uint)))
                : BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.AsSpan(index * sizeof(ushort), sizeof(ushort)));
            if (value != (is32Bit ? uint.MaxValue : ushort.MaxValue))
            {
                indices.Add(value);
            }
        }
        return indices;
    }

    private static bool ContainsNonzero(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value != 0)
            {
                return true;
            }
        }
        return false;
    }

    private static string DescribeSubmittedShaderStage(
        CpuContext ctx,
        SubmittedDcbState state,
        uint lowRegister,
        uint highRegister)
    {
        if (!TryGetShaderAddress(
                state.ShRegisters,
                lowRegister,
                highRegister,
                out var address))
        {
            return "none";
        }

        ulong header;
        lock (_submitTraceGate)
        {
            _shaderHeadersByCode.TryGetValue(address, out header);
        }
        var type = header != 0 &&
                   TryReadByte(ctx, header + ShaderTypeOffset, out var shaderType)
            ? shaderType.ToString()
            : "?";
        return $"0x{address:X16}:header=0x{header:X16}:type={type}";
    }

    private static void TraceGlobalBufferProbe(
        string stage,
        ulong shaderAddress,
        Gen5ShaderEvaluation evaluation)
    {
        if (_traceGlobalBufferLength is null &&
            _traceGlobalBufferAddresses.Length == 0)
        {
            return;
        }

        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            var lengthMatch =
                _traceGlobalBufferLength is { } requestedLength &&
                binding.DataLength == requestedLength;
            var addressMatches = _traceGlobalBufferAddresses
                .Where(address =>
                    address >= binding.BaseAddress &&
                    address - binding.BaseAddress < (ulong)binding.DataLength)
                .ToArray();
            if (!lengthMatch && addressMatches.Length == 0)
            {
                continue;
            }

            var descriptorWords = binding.ScalarAddress + 3 <
                    (uint)evaluation.ScalarRegisters.Count
                ? evaluation.ScalarRegisters
                    .Skip(checked((int)binding.ScalarAddress))
                    .Take(4)
                    .ToArray()
                : [];
            var initialDescriptorWords = binding.ScalarAddress + 3 <
                    (uint)evaluation.InitialScalarRegisters.Count
                ? evaluation.InitialScalarRegisters
                    .Skip(checked((int)binding.ScalarAddress))
                    .Take(4)
                    .ToArray()
                : [];
            var descriptorText = descriptorWords.Length == 4
                ? string.Join(':', descriptorWords.Select(value => $"{value:X8}"))
                : "unavailable";
            var initialDescriptorText = initialDescriptorWords.Length == 4
                ? string.Join(':', initialDescriptorWords.Select(value => $"{value:X8}"))
                : "unavailable";
            var descriptorStride = descriptorWords.Length == 4
                ? (descriptorWords[1] >> 16) & 0x3FFFu
                : 0;
            var initialDescriptorStride = initialDescriptorWords.Length == 4
                ? (initialDescriptorWords[1] >> 16) & 0x3FFFu
                : 0;
            var descriptorRecords = descriptorWords.Length == 4
                ? descriptorWords[2]
                : 0;
            var descriptorBytes = descriptorStride == 0
                ? descriptorRecords
                : (ulong)descriptorStride * descriptorRecords;

            var recordSample = "none";
            if (stage == "vertex" &&
                initialDescriptorStride != 0 &&
                uint.TryParse(
                    _traceVertexBufferRecord,
                    out var record))
            {
                var recordOffset = (ulong)record * initialDescriptorStride;
                if (recordOffset < (ulong)binding.DataLength)
                {
                    var sampleLength = Math.Min(
                        checked((int)initialDescriptorStride),
                        Math.Min(64, binding.DataLength - checked((int)recordOffset)));
                    recordSample =
                        $"record={record}:offset={recordOffset}:" +
                        Convert.ToHexString(binding.Data.AsSpan(
                            checked((int)recordOffset),
                            sampleLength));
                }
            }

            var recordSummary = "none";
            if (stage == "vertex" &&
                _traceVertexShaderAddress == shaderAddress &&
                _traceVertexBufferRecordSummary &&
                initialDescriptorStride is >= 32 and <= 256)
            {
                var stride = checked((int)initialDescriptorStride);
                var recordCount = binding.DataLength / stride;
                var first16Count = 0;
                var first32Count = 0;
                var upper32Count = 0;
                var firstActive = new List<string>(8);
                var dword7Count = 0;
                var firstDword7 = new List<string>(8);
                for (var recordIndex = 0; recordIndex < recordCount; recordIndex++)
                {
                    var recordBytes = binding.Data.AsSpan(
                        recordIndex * stride,
                        Math.Min(stride, 64));
                    var first16 = ContainsNonzero(recordBytes[..Math.Min(16, recordBytes.Length)]);
                    var first32 = ContainsNonzero(recordBytes[..Math.Min(32, recordBytes.Length)]);
                    var upper32 = recordBytes.Length > 32 &&
                        ContainsNonzero(recordBytes[32..]);
                    first16Count += first16 ? 1 : 0;
                    first32Count += first32 ? 1 : 0;
                    upper32Count += upper32 ? 1 : 0;
                    var dword7 = recordBytes.Length >= 32
                        ? BinaryPrimitives.ReadUInt32LittleEndian(
                            recordBytes.Slice(28, sizeof(uint)))
                        : 0;
                    if (dword7 != 0)
                    {
                        dword7Count++;
                        if (firstDword7.Count < 8)
                        {
                            firstDword7.Add($"{recordIndex}:0x{dword7:X8}");
                        }
                    }
                    if (first32 && firstActive.Count < 8)
                    {
                        firstActive.Add(
                            $"{recordIndex}:" + Convert.ToHexString(recordBytes));
                    }
                }

                recordSummary =
                    $"records={recordCount}:first16={first16Count}:" +
                    $"first32={first32Count}:upper32={upper32Count}:" +
                    $"dword7={dword7Count}:" +
                    $"dword7_active=[{string.Join(';', firstDword7)}]:" +
                    $"active=[{string.Join(';', firstActive)}]";
            }

            var key =
                $"{stage}|{shaderAddress:X16}|{binding.BaseAddress:X16}|" +
                $"{binding.ScalarAddress}|{binding.Writable}|" +
                $"{descriptorText}|{initialDescriptorText}|" +
                string.Join(',', binding.InstructionPcs);
            lock (_submitTraceGate)
            {
                if (_tracedGlobalBufferLengthStates.Count >= 512 ||
                    !_tracedGlobalBufferLengthStates.Add(key))
                {
                    continue;
                }
            }

            var headLength = Math.Min(binding.DataLength, 32);
            var head = Convert.ToHexString(
                binding.Data.AsSpan(0, headLength));
            var content = GlobalBufferWritebackDiagnostics.Summarize(
                binding.Data.AsSpan(0, binding.DataLength));
            Console.Error.WriteLine(
                $"[AGC][GLOBAL-BUFFER-PROBE] stage={stage} " +
                $"shader=0x{shaderAddress:X16} saddr=s{binding.ScalarAddress} " +
                $"base=0x{binding.BaseAddress:X16} bytes={binding.DataLength} " +
                $"writable={(binding.Writable ? 1 : 0)} " +
                $"writeback={(binding.WriteBackToGuest ? 1 : 0)} " +
                $"descriptor=[{descriptorText}] initial=[{initialDescriptorText}] " +
                $"stride={descriptorStride} initial_stride={initialDescriptorStride} " +
                $"records={descriptorRecords} " +
                $"descriptor_bytes={descriptorBytes} " +
                $"nonzero_bytes={content.NonzeroBytes}/{binding.DataLength} " +
                $"first_nonzero={content.FirstNonzeroOffset} " +
                $"last_nonzero={content.LastNonzeroOffset} " +
                $"hash=0x{content.Hash:X16} " +
                $"match={(lengthMatch ? "length" : "address")}:" +
                $"{string.Join(',', addressMatches.Select(address => $"0x{address:X16}"))} " +
                $"pcs={string.Join(',', binding.InstructionPcs.Select(pc => $"0x{pc:X}"))} " +
                $"head={head} record_sample={recordSample} " +
                $"record_summary={recordSummary}");
        }
    }

    private static void TraceIndexedGlobalBufferProbe(
        CpuContext ctx,
        ulong shaderAddress,
        Gen5ShaderEvaluation evaluation)
    {
        if (_traceIndexedGlobalBufferShaderAddress != shaderAddress ||
            _traceIndexedGlobalBufferSpec.Length < 6)
        {
            return;
        }

        var occurrence = Interlocked.Increment(
            ref _traceIndexedGlobalBufferOccurrence);
        var sourceBindingIndex = _traceIndexedGlobalBufferSpec[0];
        var sourceStride = _traceIndexedGlobalBufferSpec[1];
        var indexByteOffset = _traceIndexedGlobalBufferSpec[2];
        var targetBindingIndex = _traceIndexedGlobalBufferSpec[3];
        var targetStride = _traceIndexedGlobalBufferSpec[4];
        var fieldOffsets = _traceIndexedGlobalBufferSpec.AsSpan(5).ToArray();
        if (sourceBindingIndex >= evaluation.GlobalMemoryBindings.Count ||
            targetBindingIndex >= evaluation.GlobalMemoryBindings.Count ||
            sourceStride == 0 ||
            targetStride == 0)
        {
            return;
        }

        var source = evaluation.GlobalMemoryBindings[sourceBindingIndex];
        var target = evaluation.GlobalMemoryBindings[targetBindingIndex];
        if (_traceIndexedGlobalBufferCpuWrites)
        {
            GuestImageWriteTracker.FlushPendingDiagnostics();
            var sourceCpuDirty = GuestImageWriteTracker.ConsumeDirty(
                source.BaseAddress);
            if (sourceCpuDirty)
            {
                _ = GuestImageWriteTracker.TryGetFirstCpuWriteContext(
                    source.BaseAddress,
                    out var firstCpuWriteContext);
                var sourceCpuContent =
                    GlobalBufferWritebackDiagnostics.Summarize(
                        source.Data.AsSpan(0, source.DataLength));
                Console.Error.WriteLine(
                    $"[AGC][INDEXED-GLOBAL-BUFFER-CPU-SNAPSHOT] " +
                    $"shader=0x{shaderAddress:X16} occurrence={occurrence} " +
                    $"role=source base=0x{source.BaseAddress:X16} " +
                    $"bytes={source.DataLength} " +
                    $"nonzero_bytes={sourceCpuContent.NonzeroBytes}/{source.DataLength} " +
                    $"first_nonzero={sourceCpuContent.FirstNonzeroOffset} " +
                    $"last_nonzero={sourceCpuContent.LastNonzeroOffset} " +
                    $"hash=0x{sourceCpuContent.Hash:X16}");
                TraceIndexedGlobalBufferCpuContext(
                    ctx,
                    shaderAddress,
                    occurrence,
                    source,
                    firstCpuWriteContext);
            }

            GuestImageWriteTracker.Track(
                source.BaseAddress,
                (ulong)source.DataLength,
                occurrence,
                $"indexed-global-source-0x{shaderAddress:X}");
            GuestImageWriteTracker.Track(
                target.BaseAddress,
                (ulong)target.DataLength,
                occurrence,
                $"indexed-global-target-0x{shaderAddress:X}");
        }

        if (occurrence != 1 &&
            occurrence % _traceIndexedGlobalBufferInterval != 0)
        {
            return;
        }

        var sourceContent = GlobalBufferWritebackDiagnostics.Summarize(
            source.Data.AsSpan(0, source.DataLength));
        var targetContent = GlobalBufferWritebackDiagnostics.Summarize(
            target.Data.AsSpan(0, target.DataLength));
        var key =
            $"{shaderAddress:X16}|{occurrence}|" +
            $"{string.Join(',', _traceIndexedGlobalBufferSpec)}|" +
            $"{source.BaseAddress:X16}|{sourceContent.Hash:X16}|" +
            $"{target.BaseAddress:X16}|{targetContent.Hash:X16}";
        lock (_submitTraceGate)
        {
            if (_tracedIndexedGlobalBufferStates.Count >= 128 ||
                !_tracedIndexedGlobalBufferStates.Add(key))
            {
                return;
            }
        }

        IndexedGlobalBufferSummary summary;
        try
        {
            summary = IndexedGlobalBufferDiagnostics.Summarize(
                source.Data.AsSpan(0, source.DataLength),
                sourceStride,
                indexByteOffset,
                target.Data.AsSpan(0, target.DataLength),
                targetStride,
                fieldOffsets);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            Console.Error.WriteLine(
                $"[AGC][INDEXED-GLOBAL-BUFFER-PROBE] " +
                $"shader=0x{shaderAddress:X16} invalid_spec=1 " +
                $"error={exception.ParamName}");
            return;
        }

        var fieldDescriptions = string.Join(
            ',',
            summary.Fields.Select(field =>
                $"+{field.FieldOffset}:nonzero={field.NonzeroMappings}" +
                $":first={field.FirstSourceRecord}->" +
                $"{FormatOptionalRecord(field.FirstTargetRecord)}" +
                $"@0x{field.FirstValue:X8}" +
                $":last={field.LastSourceRecord}->" +
                $"{FormatOptionalRecord(field.LastTargetRecord)}" +
                $"@0x{field.LastValue:X8}"));
        var lastTargetRecord = summary.TargetRecords - 1;
        var lastTargetFields = summary.TargetRecords == 0
            ? "none"
            : string.Join(
                ',',
                fieldOffsets.Select(fieldOffset =>
                    $"+{fieldOffset}=0x" +
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        target.Data.AsSpan(
                            checked(lastTargetRecord * targetStride) + fieldOffset,
                            sizeof(uint))).ToString("X8")));
        var targetFieldDescriptions = string.Join(
            ',',
            summary.TargetFields.Select(field =>
                $"+{field.FieldOffset}:nonzero_records={field.NonzeroRecords}" +
                $":first={field.FirstTargetRecord}@0x{field.FirstValue:X8}" +
                $":last={field.LastTargetRecord}@0x{field.LastValue:X8}"));
        Console.Error.WriteLine(
            $"[AGC][INDEXED-GLOBAL-BUFFER-PROBE] " +
            $"shader=0x{shaderAddress:X16} occurrence={occurrence} " +
            $"source=b{sourceBindingIndex}/s{source.ScalarAddress}/" +
            $"0x{source.BaseAddress:X16}/{source.DataLength} " +
            $"target=b{targetBindingIndex}/s{target.ScalarAddress}/" +
            $"0x{target.BaseAddress:X16}/{target.DataLength} " +
            $"source_records={summary.SourceRecords} " +
            $"target_records={summary.TargetRecords} " +
            $"valid={summary.ValidMappings} " +
            $"out_of_range={summary.OutOfRangeMappings} " +
            $"unique_targets={summary.UniqueTargetRecords} " +
            $"source_hash=0x{sourceContent.Hash:X16} " +
            $"target_hash=0x{targetContent.Hash:X16} " +
            $"fields=[{fieldDescriptions}] " +
            $"target_fields=[{targetFieldDescriptions}] " +
            $"last_target={lastTargetRecord}:[{lastTargetFields}]");
    }

    private static readonly HashSet<ulong> _tracedIndexedGlobalCpuContextBases = [];

    private static void TraceIndexedGlobalBufferVertexDrawProbe(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong shaderAddress,
        uint drawCount,
        bool indexed,
        Gen5ShaderEvaluation evaluation)
    {
        if (_traceIndexedGlobalBufferShaderAddress != shaderAddress ||
            _traceIndexedGlobalBufferSpec.Length < 6 ||
            _traceIndexedGlobalBufferVertexBaseScalar is not { } baseScalar ||
            baseScalar >= evaluation.ScalarRegisters.Count)
        {
            return;
        }

        var sourceBindingIndex = _traceIndexedGlobalBufferSpec[0];
        var sourceStride = _traceIndexedGlobalBufferSpec[1];
        var indexByteOffset = _traceIndexedGlobalBufferSpec[2];
        var targetBindingIndex = _traceIndexedGlobalBufferSpec[3];
        var targetStride = _traceIndexedGlobalBufferSpec[4];
        var fieldOffsets = _traceIndexedGlobalBufferSpec.AsSpan(5).ToArray();
        if (sourceBindingIndex >= evaluation.GlobalMemoryBindings.Count ||
            targetBindingIndex >= evaluation.GlobalMemoryBindings.Count ||
            sourceStride <= 0 ||
            targetStride <= 0 ||
            indexByteOffset < 0 ||
            indexByteOffset + sizeof(uint) > sourceStride ||
            fieldOffsets.Any(offset =>
                offset < 0 || offset + sizeof(uint) > targetStride))
        {
            return;
        }

        var source = evaluation.GlobalMemoryBindings[sourceBindingIndex];
        var target = evaluation.GlobalMemoryBindings[targetBindingIndex];
        var baseRecord = evaluation.ScalarRegisters[baseScalar];
        var indices = ReadDiagnosticDrawIndices(
            ctx,
            state,
            drawCount,
            indexed,
            out var indicesComplete);
        var mappings = new List<string>();
        foreach (var index in indices.Distinct().Take(128))
        {
            var sourceRecord = (ulong)baseRecord + index;
            var sourceOffset = sourceRecord * (uint)sourceStride;
            if (sourceOffset + (uint)indexByteOffset + sizeof(uint) >
                (ulong)source.DataLength)
            {
                mappings.Add($"{index}->{sourceRecord}:source-oor");
                continue;
            }

            var targetRecord = BinaryPrimitives.ReadUInt32LittleEndian(
                source.Data.AsSpan(
                    checked((int)sourceOffset) + indexByteOffset,
                    sizeof(uint)));
            var targetOffset = (ulong)targetRecord * (uint)targetStride;
            if (targetOffset + (uint)targetStride > (ulong)target.DataLength)
            {
                mappings.Add($"{index}->{sourceRecord}->{targetRecord}:target-oor");
                continue;
            }

            var targetBytes = target.Data.AsSpan(
                checked((int)targetOffset),
                targetStride);
            var fieldValues = new List<string>(fieldOffsets.Length);
            foreach (var offset in fieldOffsets)
            {
                fieldValues.Add(
                    $"+{offset}=0x" +
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        targetBytes.Slice(offset, sizeof(uint))).ToString("X8"));
            }
            var fields = string.Join(',', fieldValues);
            mappings.Add(
                $"{index}->{sourceRecord}->{targetRecord}:" +
                $"{(ContainsNonzero(targetBytes) ? "nonzero" : "zero")}:" +
                $"[{fields}]");
        }

        var key =
            $"{shaderAddress:X16}|{source.BaseAddress:X16}|" +
            $"{target.BaseAddress:X16}|{baseRecord}|{drawCount}|" +
            string.Join(';', mappings);
        lock (_submitTraceGate)
        {
            if (_tracedIndexedGlobalBufferVertexDraws.Count >= 256 ||
                !_tracedIndexedGlobalBufferVertexDraws.Add(key))
            {
                return;
            }
        }

        Console.Error.WriteLine(
            $"[AGC][INDEXED-GLOBAL-BUFFER-VERTEX-DRAW] " +
            $"shader=0x{shaderAddress:X16} base_s{baseScalar}={baseRecord} " +
            $"draw_count={drawCount} indexed={(indexed ? 1 : 0)} " +
            $"indices_complete={(indicesComplete ? 1 : 0)} " +
            $"source=b{sourceBindingIndex}/0x{source.BaseAddress:X16}/{source.DataLength} " +
            $"target=b{targetBindingIndex}/0x{target.BaseAddress:X16}/{target.DataLength} " +
            $"mappings=[{string.Join(';', mappings)}]");
    }

    private static void TraceIndexedGlobalBufferCpuContext(
        CpuContext ctx,
        ulong shaderAddress,
        long occurrence,
        Gen5GlobalMemoryBinding source,
        GuestWriteFaultContext context)
    {
        if (context.InstructionAddress == 0)
        {
            return;
        }

        var selectorMatches = context.R15 == source.BaseAddress;
        var shouldSummarize = false;
        lock (_submitTraceGate)
        {
            shouldSummarize = selectorMatches &&
                _tracedIndexedGlobalCpuContextBases.Count < 16 &&
                _tracedIndexedGlobalCpuContextBases.Add(source.BaseAddress);
        }

        var manager = context.R12;
        var managerCount = 0u;
        var sourceRecords = 0UL;
        var packedListCounts = 0u;
        var thirdListCountRaw = 0u;
        var pendingListCountRaw = 0u;
        var listPointers = new ulong[4];
        var ringStateHandle = 0UL;
        var ringSelectorHandle = 0UL;
        var registry = 0UL;
        var entityQueue = 0UL;
        var recordInput = 0UL;
        var resolveInput = 0UL;
        var recordInputCount = 0u;
        var resolveInputCount = 0u;
        var entityQueueIndices = new uint[4];
        var workCounters = new uint[5];
        var managerReadable = selectorMatches &&
            manager != 0 &&
            TryReadUInt32(ctx, manager + 0x16C, out managerCount) &&
            TryReadUInt64(ctx, manager + 0x378, out sourceRecords) &&
            TryReadUInt32(ctx, manager + 0x3EE, out packedListCounts) &&
            TryReadUInt32(ctx, manager + 0x3F2, out thirdListCountRaw) &&
            TryReadUInt32(ctx, manager + 0x3F4, out pendingListCountRaw) &&
            TryReadUInt64(ctx, manager + 0x3F8, out listPointers[0]) &&
            TryReadUInt64(ctx, manager + 0x400, out listPointers[1]) &&
            TryReadUInt64(ctx, manager + 0x408, out listPointers[2]) &&
            TryReadUInt64(ctx, manager + 0x410, out listPointers[3]);
        if (managerReadable && context.R13 <= 3)
        {
            _ = TryReadUInt64(
                ctx,
                manager + 0x2C8 + (context.R13 * sizeof(ulong)),
                out ringStateHandle);
            _ = TryReadUInt64(
                ctx,
                manager + 0x2E8 + (context.R13 * sizeof(ulong)),
                out ringSelectorHandle);
        }
        if (managerReadable)
        {
            _ = TryReadUInt64(ctx, manager + 0x288, out registry);
            _ = TryReadUInt64(ctx, manager + 0x298, out entityQueue);
            _ = TryReadUInt64(ctx, manager + 0x6D0, out recordInput);
            _ = TryReadUInt64(ctx, manager + 0x6D8, out resolveInput);
            if (recordInput != 0)
            {
                _ = TryReadUInt32(ctx, recordInput, out recordInputCount);
            }
            if (resolveInput != 0)
            {
                _ = TryReadUInt32(ctx, resolveInput, out resolveInputCount);
            }
            if (entityQueue != 0)
            {
                _ = TryReadUInt32(ctx, entityQueue + 0x2110, out entityQueueIndices[0]);
                _ = TryReadUInt32(ctx, entityQueue + 0x2114, out entityQueueIndices[1]);
                _ = TryReadUInt32(ctx, entityQueue + 0x2118, out entityQueueIndices[2]);
                _ = TryReadUInt32(ctx, entityQueue + 0x211C, out entityQueueIndices[3]);
            }
            for (var index = 0; index < workCounters.Length; index++)
            {
                _ = TryReadUInt32(
                    ctx,
                    manager + 0x380 + ((ulong)index * sizeof(uint)),
                    out workCounters[index]);
            }
        }

        var listCounts = new[]
        {
            (ushort)packedListCounts,
            (ushort)(packedListCounts >> 16),
            (ushort)thirdListCountRaw,
            (ushort)pendingListCountRaw,
        };
        var listSummary = shouldSummarize && managerReadable
            ? string.Join(
                ';',
                Enumerable.Range(0, 4).Select(index =>
                    SummarizeSelectorPopulationList(
                        ctx,
                        sourceRecords,
                        listPointers[index],
                        listCounts[index],
                        index)))
            : "skipped";

        Console.Error.WriteLine(
            $"[AGC][INDEXED-GLOBAL-BUFFER-CPU-CONTEXT] " +
            $"shader=0x{shaderAddress:X16} occurrence={occurrence} " +
            $"source=0x{source.BaseAddress:X16} " +
            $"selector_match={(selectorMatches ? 1 : 0)} " +
            $"ip=0x{context.InstructionAddress:X16} " +
            $"rax=0x{context.Rax:X16} rcx=0x{context.Rcx:X16} " +
            $"rdx=0x{context.Rdx:X16} r12=0x{context.R12:X16} " +
            $"r13=0x{context.R13:X16} r14=0x{context.R14:X16} " +
            $"r15=0x{context.R15:X16} " +
            $"manager_readable={(managerReadable ? 1 : 0)} " +
            $"manager_count={managerCount} source_records=0x{sourceRecords:X16} " +
            $"list_counts={listCounts[0]}/{listCounts[1]}/{listCounts[2]}/" +
            $"{listCounts[3]} " +
            $"list_ptrs=0x{listPointers[0]:X16}/0x{listPointers[1]:X16}/" +
            $"0x{listPointers[2]:X16}/0x{listPointers[3]:X16} " +
            $"registry=0x{registry:X16} entity_queue=0x{entityQueue:X16} " +
            $"entity_queue_indices={entityQueueIndices[0]}/{entityQueueIndices[1]}/" +
            $"{entityQueueIndices[2]}/{entityQueueIndices[3]} " +
            $"record_input=0x{recordInput:X16}:{recordInputCount} " +
            $"resolve_input=0x{resolveInput:X16}:{resolveInputCount} " +
            $"work_counters={string.Join('/', workCounters)} " +
            $"ring_state_handle=0x{ringStateHandle:X16} " +
            $"ring_selector_handle=0x{ringSelectorHandle:X16} " +
            $"lists=[{listSummary}]");
    }

    private static string SummarizeSelectorPopulationList(
        CpuContext ctx,
        ulong sourceRecords,
        ulong listAddress,
        ushort listCount,
        int listIndex)
    {
        const int sourceRecordStride = 0x430;
        const int selectorSlotOffset = 0xD4;
        const int selectorSlotCount = 64;
        const int selectorPayloadOffset = 0x1D4;
        const int recordProbeLength = selectorPayloadOffset +
            (selectorSlotCount * sizeof(uint));

        if (listCount == 0)
        {
            return $"l{listIndex}:count=0";
        }
        if (sourceRecords == 0 || listAddress == 0)
        {
            return $"l{listIndex}:count={listCount}:unreadable-root";
        }

        var listBytes = new byte[listCount * sizeof(ushort)];
        if (!ctx.Memory.TryRead(listAddress, listBytes))
        {
            return $"l{listIndex}:count={listCount}:unreadable-list";
        }

        var recordProbe = new byte[recordProbeLength];
        var readable = 0;
        var active = 0;
        var recordsWithSlots = 0;
        var nonzeroSlots = 0;
        var activeNonzeroSlots = 0;
        var inactiveNonzeroSlots = 0;
        var firstActive = -1;
        var lastActive = -1;
        var firstSlot = uint.MaxValue;
        var lastSlot = uint.MaxValue;
        for (var offset = 0; offset < listBytes.Length; offset += sizeof(ushort))
        {
            var recordIndex = BinaryPrimitives.ReadUInt16LittleEndian(
                listBytes.AsSpan(offset, sizeof(ushort)));
            var recordAddress = sourceRecords +
                ((ulong)recordIndex * sourceRecordStride);
            if (!ctx.Memory.TryRead(recordAddress, recordProbe))
            {
                continue;
            }

            readable++;
            var isActive = BinaryPrimitives.ReadUInt64LittleEndian(recordProbe) != 0;
            if (isActive)
            {
                active++;
                firstActive = firstActive < 0 ? recordIndex : firstActive;
                lastActive = recordIndex;
            }

            var recordSlots = 0;
            for (var slotIndex = 0; slotIndex < selectorSlotCount; slotIndex++)
            {
                var slot = BinaryPrimitives.ReadUInt32LittleEndian(
                    recordProbe.AsSpan(
                        selectorSlotOffset + (slotIndex * sizeof(uint)),
                        sizeof(uint)));
                if (slot == 0)
                {
                    continue;
                }

                recordSlots++;
                nonzeroSlots++;
                if (isActive)
                {
                    activeNonzeroSlots++;
                }
                else
                {
                    inactiveNonzeroSlots++;
                }
                firstSlot = firstSlot == uint.MaxValue ? slot : firstSlot;
                lastSlot = slot;
            }
            if (recordSlots != 0)
            {
                recordsWithSlots++;
            }
        }

        return $"l{listIndex}:count={listCount}:readable={readable}:" +
            $"active={active}:first_active={firstActive}:last_active={lastActive}:" +
            $"records_with_slots={recordsWithSlots}:slots={nonzeroSlots}:" +
            $"active_slots={activeNonzeroSlots}:inactive_slots={inactiveNonzeroSlots}:" +
            $"first_slot={(firstSlot == uint.MaxValue ? -1 : firstSlot)}:" +
            $"last_slot={(lastSlot == uint.MaxValue ? -1 : lastSlot)}";
    }

    private static string FormatOptionalRecord(uint record) =>
        record == uint.MaxValue ? "none" : record.ToString();

    private static bool TryAppendTranslatedImageBindings(
        IReadOnlyList<Gen5ImageBinding> bindings,
        IReadOnlyList<Gen5ImageBinding> stageBindings,
        List<TranslatedImageBinding> textures,
        ulong pixelShaderAddress,
        ulong exportShaderAddress,
        out string error)
    {
        foreach (var binding in bindings)
        {
            if (!TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture))
            {
                // A garbage/zeroed texture descriptor (from a per-draw descriptor
                // setup race — the same root as scalar-load-failed) would drop
                // the whole draw, so deferred-lighting/composite passes that
                // produce the composite's feeder targets never run. Keep the
                // existing 1x1 fallback unless strict diagnostics are requested.
                if (_strictShaderDescriptors)
                {
                    error = $"invalid texture descriptor at pc=0x{binding.Pc:X}";
                    return false;
                }

                texture = new TextureDescriptor(
                    0, 1, 1, Gen5TextureFormatR8G8B8A8Unorm, 0, 0, 0, 0, 0, 1, 0xFAC);
            }

            var isStorage =
                Gen5ShaderTranslator.RequiresStorageImage(
                    binding,
                    stageBindings);
            var traceAddressedTextureBinding =
                texture.Address != 0 &&
                (Array.IndexOf(_traceGuestImageAddresses, texture.Address) >= 0 ||
                 Array.IndexOf(_traceTextureBindingAddresses, texture.Address) >= 0 ||
                 AvPlayerExports.ShouldTraceVideoBufferAddress(texture.Address)) &&
                _tracedAddressedTextureBindings.TryAdd(
                    (pixelShaderAddress,
                     texture.Address,
                     texture.Width,
                     texture.Height,
                     texture.Format,
                     texture.NumberType,
                     texture.TileMode,
                     texture.Pitch,
                     texture.DstSelect),
                    0);
            if (_traceAgcShader ||
                Array.IndexOf(_tracePixelShaderAddresses, pixelShaderAddress) >= 0 ||
                traceAddressedTextureBinding)
            {
                Console.Error.WriteLine(
                    "[LOADER][TRACE] " +
                    $"{(traceAddressedTextureBinding ? "agc.addressed_texture_binding" : "agc.texture_binding")} " +
                    $"ps=0x{pixelShaderAddress:X16} es=0x{exportShaderAddress:X16} " +
                    $"pc=0x{binding.Pc:X} op={binding.Opcode} storage={(isStorage ? 1 : 0)} " +
                    $"decoded={FormatTextureDescriptor(texture)} " +
                    $"raw={FormatShaderDwords(binding.ResourceDescriptor)} sampler={FormatShaderDwords(binding.SamplerDescriptor)}");
            }
            textures.Add(
                new TranslatedImageBinding(
                    texture,
                    isStorage,
                    Gen5ShaderTranslator.IsImageWriteOperation(binding.Opcode),
                    binding.MipLevel ?? 0,
                    binding.SamplerDescriptor,
                    Gen5ShaderTranslator.IsArrayedImageBinding(binding)));
        }

        error = string.Empty;
        return true;
    }

    private static int _tracedAstroTitlePixelGlobals;
    private static int _tracedAstroTitlePixelGlobalProbe;

    private static void TraceAstroTitlePixelGlobalProbe(Gen5ShaderEvaluation evaluation)
    {
        const int probeOffset = 17216;
        var draw = Interlocked.Increment(ref _tracedAstroTitlePixelGlobalProbe);
        foreach (var (binding, index) in evaluation.GlobalMemoryBindings.Select((value, index) => (value, index)))
        {
            if (probeOffset + 16 > binding.DataLength)
            {
                continue;
            }

            Console.Error.WriteLine(
                $"[TITLE-GLOBALS-LIVE] draw={draw} binding={index} " +
                $"base=0x{binding.BaseAddress:X16} offset=0x{probeOffset:X} " +
                $"bytes={Convert.ToHexString(binding.Data.AsSpan(probeOffset, 16))}");
        }
    }

    private static void TraceAstroTitlePixelGlobals(Gen5ShaderEvaluation evaluation)
    {
        if (Interlocked.Exchange(ref _tracedAstroTitlePixelGlobals, 1) != 0)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[TITLE-GLOBALS] initial_s0_31=" +
            string.Join(',', evaluation.InitialScalarRegisters
                .Take(32)
                .Select((value, index) => $"s{index}={value:X8}")));

        var probeOffsets = new[]
        {
            0, 16, 24, 32, 48,
            192, 256, 400, 432,
            17100, 17104, 17136, 17168, 17184, 17200, 17216,
        };
        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            Console.Error.WriteLine(
                $"[TITLE-GLOBALS] binding s{binding.ScalarAddress} " +
                $"base=0x{binding.BaseAddress:X16} bytes={binding.DataLength} " +
                $"pcs={string.Join(',', binding.InstructionPcs.Select(pc => $"0x{pc:X}"))}");
            foreach (var offset in probeOffsets)
            {
                if (offset < 0 || offset + 16 > binding.DataLength)
                {
                    continue;
                }

                Console.Error.WriteLine(
                    $"[TITLE-GLOBALS] s{binding.ScalarAddress}+0x{offset:X}=" +
                    Convert.ToHexString(binding.Data.AsSpan(offset, 16)));
            }
        }
    }

    private static bool IsCachedFixedFullscreenClearPair(
        Gen5ShaderState exportState,
        Gen5ShaderEvaluation exportEvaluation,
        Gen5ShaderState pixelState,
        Gen5ShaderEvaluation pixelEvaluation) =>
        IsProceduralFullscreenClearPair(
            exportState,
            exportEvaluation,
            pixelState,
            pixelEvaluation);

    private static bool IsProceduralFullscreenClearPair(
        Gen5ShaderState exportState,
        Gen5ShaderEvaluation exportEvaluation,
        Gen5ShaderState pixelState,
        Gen5ShaderEvaluation pixelEvaluation)
    {
        if ((exportEvaluation.VertexInputs?.Count ?? 0) != 0 ||
            exportEvaluation.ImageBindings.Count != 0 ||
            pixelEvaluation.ImageBindings.Count != 0 ||
            exportEvaluation.GlobalMemoryBindings.Count != 0 ||
            pixelEvaluation.GlobalMemoryBindings.Count != 0)
        {
            return false;
        }

        if (!HasExportTarget(exportState, target: 12) ||
            !HasExportTarget(pixelState, target: 0))
        {
            return false;
        }

        if (pixelState.Program.Instructions.Count is 0 or > 8 ||
            exportState.Program.Instructions.Count is 0 or > 48)
        {
            return false;
        }

        return pixelState.Program.Instructions.All(IsBenignClearPixelInstruction) &&
               exportState.Program.Instructions.All(IsBenignProceduralVertexInstruction);
    }

    private static bool HasExportTarget(Gen5ShaderState state, uint target) =>
        state.Program.Instructions.Any(instruction =>
            instruction.Control is Gen5ExportControl export &&
            export.Target == target);

    private static bool IsBenignClearPixelInstruction(Gen5ShaderInstruction instruction) =>
        instruction.Opcode is
            "SNop" or
            "SWaitcnt" or
            "SInstPrefetch" or
            "SEndpgm" or
            "VMovB32" ||
        instruction.Control is Gen5ExportControl { Target: 0 };

    private static bool IsBenignProceduralVertexInstruction(Gen5ShaderInstruction instruction)
    {
        if (instruction.Control is Gen5BufferMemoryControl or
            Gen5ImageControl or
            Gen5GlobalMemoryControl or
            Gen5ScalarMemoryControl)
        {
            return false;
        }

        if (instruction.Control is Gen5ExportControl export)
        {
            // Position (12) plus ignored NGG/param exports.
            return export.Target is 12 or (>= 13 and < 32) or 20;
        }

        return instruction.Opcode is
            "SNop" or
            "SWaitcnt" or
            "SInstPrefetch" or
            "SEndpgm" or
            "SSendmsg" or
            "VMovB32" or
            "VAndB32" or
            "VAddI32" or
            "VLshlrevB32" or
            "VCvtF32I32" or
            "VCvtF32U32" ||
            instruction.Encoding is
                Gen5ShaderEncoding.Sop1 or
                Gen5ShaderEncoding.Sop2 or
                Gen5ShaderEncoding.Sopc or
                Gen5ShaderEncoding.Sopk or
                Gen5ShaderEncoding.Sopp;
    }

    private static (float Red, float Green, float Blue, float Alpha) DecodeSolidClearColor(
        Gen5ShaderEvaluation pixelEvaluation)
    {
        // Default opaque white; guest clear shaders often mov a 1.0 literal into v0.
        float red = 1f, green = 1f, blue = 1f, alpha = 1f;
        if (pixelEvaluation.InitialScalarRegisters.Count > 0)
        {
            var bits = pixelEvaluation.InitialScalarRegisters[0];
            if (bits != 0)
            {
                red = green = blue = alpha = BitConverter.UInt32BitsToSingle(bits);
                if (!float.IsFinite(red) || red < 0f || red > 4f)
                {
                    red = green = blue = alpha = 1f;
                }
            }
        }

        return (red, green, blue, alpha);
    }

    private static readonly bool _fillClearHack = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_FILL_CLEAR"),
        "1",
        StringComparison.Ordinal);

    /// <summary>
    /// Treat an untextured fill that outputs pure transparent black through
    /// premultiplied blending as an overwrite. Chowdren issues exactly this
    /// draw once per frame to reset its effect layers (fog smoke, vignette
    /// masks); under the blend factors it sets (One, OneMinusSrcAlpha) a
    /// (0,0,0,0) source is a mathematical no-op, so without this the layers
    /// accumulate until they saturate and the fog composites as a flat veil
    /// over the whole scene. The workaround applies only when every MRT
    /// attachment uses the same blend pattern. Disable with
    /// SHARPEMU_DISABLE_FILL_CLEAR=1.
    /// </summary>
    private static GuestRenderState ApplyTransparentPremultipliedFillClear(
        GuestRenderState renderState,
        IReadOnlyList<TranslatedImageBinding> textures,
        IReadOnlyList<Gen5VertexInputBinding> vertexInputs,
        IReadOnlyList<uint> pixelUserData)
    {
        if (!_fillClearHack ||
            textures.Count != 0 ||
            vertexInputs.Count != 0 ||
            pixelUserData.Count < 4 ||
            !renderState.Blends.All(IsTransparentPremultipliedFillBlend))
        {
            return renderState;
        }

        for (var index = 0; index < 4; index++)
        {
            // Positive or negative zero.
            if ((pixelUserData[index] & 0x7FFF_FFFFu) != 0)
            {
                return renderState;
            }
        }

        return renderState with
        {
            Blends = renderState.Blends
                .Select(blend => blend with { Enable = false })
                .ToArray(),
        };
    }

    /// <summary>
    /// Recognises the covering quad a GFX10 driver issues to clear a
    /// DCC-compressed colour target. There is no clear packet: the driver
    /// programs CB_COLORn_CLEAR_WORD0/1 and draws a quad that the colour block
    /// turns into DCC clear codes, discarding whatever the pixel shader
    /// exported. Executing it as an ordinary draw writes the shaded output
    /// instead, and because the blend it uses computes
    /// <c>a &lt;- a_src + a_dst * (1 - a_src)</c> - fixed point 1 - the target's
    /// alpha then climbs every frame and saturates.
    ///
    /// Restricted to clear-to-zero. The reset performed for a match clears the
    /// attachment to zero, so a nonzero CLEAR_WORD would be cleared to the
    /// wrong colour; those fall through and are drawn. Zero is zero under every
    /// encoding the register can carry, so the pair needs no format handling.
    ///
    /// The clip-space test is load-bearing rather than belt-and-braces: fills
    /// sharing the vertex count, topology and blend outnumber the clears by two
    /// orders of magnitude and sit at coordinates well outside the frame.
    /// </summary>
    private const uint TriangleStripPrimitive = 6;

    // A float32x3 vertex position stream (BUF_DATA_FORMAT_32_32_32 / FLOAT).
    private const uint PositionDataFormat = 13;
    private const uint PositionNumberFormat = 7;

    private static bool IsDccFastClearDraw(
        IReadOnlyDictionary<uint, uint> registers,
        IReadOnlyList<RenderTargetDescriptor> renderTargets,
        IReadOnlyList<TranslatedImageBinding> textures,
        IReadOnlyList<Gen5VertexInputBinding> vertexInputs,
        GuestRenderState renderState,
        uint primitiveType,
        uint vertexCount)
    {
        if (textures.Count != 0 ||
            vertexCount != 4 ||
            primitiveType != TriangleStripPrimitive ||
            renderTargets.Count == 0 ||
            renderState.Blends.Count == 0 ||
            !renderState.Blends.All(IsTransparentPremultipliedFillBlend))
        {
            return false;
        }

        var slotStride = renderTargets[0].Slot * CbColorRegisterStride;
        return registers.TryGetValue(CbColor0Info + slotStride, out var info) &&
            (info & CbColorInfoDccEnableMask) != 0 &&
            registers.TryGetValue(CbColor0ClearWord0 + slotStride, out var clearWord0) &&
            registers.TryGetValue(CbColor0ClearWord1 + slotStride, out var clearWord1) &&
            clearWord0 == 0 &&
            clearWord1 == 0 &&
            CoversClipSpace(vertexInputs, vertexCount);
    }

    /// <summary>
    /// True when the draw's float32x3 position stream spans the full clip
    /// rectangle, i.e. x and y both reach -1 and +1.
    /// </summary>
    private static bool CoversClipSpace(
        IReadOnlyList<Gen5VertexInputBinding> vertexInputs,
        uint vertexCount)
    {
        const float Tolerance = 0.001f;
        foreach (var input in vertexInputs)
        {
            if (input.DataFormat != PositionDataFormat ||
                input.NumberFormat != PositionNumberFormat)
            {
                continue;
            }

            var stride = input.Stride == 0 ? 12u : input.Stride;
            var available = Math.Min(input.DataLength, input.Data.Length);
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            var seen = 0;
            for (var vertex = 0u; vertex < vertexCount; vertex++)
            {
                var at = (int)(input.OffsetBytes + (vertex * stride));
                if (at + 12 > available)
                {
                    break;
                }

                var position = input.Data.AsSpan(at);
                var x = BitConverter.ToSingle(position);
                var y = BitConverter.ToSingle(position[4..]);
                if (!float.IsFinite(x) || !float.IsFinite(y))
                {
                    return false;
                }

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                seen++;
            }

            return seen >= 3 &&
                minX <= -1f + Tolerance && maxX >= 1f - Tolerance &&
                minY <= -1f + Tolerance && maxY >= 1f - Tolerance;
        }

        return false;
    }

    private static bool IsTransparentPremultipliedFillBlend(GuestBlendState blend) =>
        blend is
        {
            Enable: true,
            ColorSrcFactor: 1,
            ColorDstFactor: 5,
            ColorFunc: 0,
        };

    private static AgcIndexHelpers.ProsperoIndexType GetProsperoIndexType(SubmittedDcbState state) =>
        // IndexSize is latched from ItIndexType and from UC VGT_INDEX_TYPE
        // writes. Do not fall back to a stale UC value when IndexSize is 0 —
        // that mis-classified 16-bit draws as index8 and blanked meshes.
        AgcIndexHelpers.Decode(state.IndexSize);

    /// <summary>
    /// ResolveVertexOffset for the common UC path: GE_INDX_OFFSET is the
    /// DrawIndexed vertexOffset / DrawAuto firstVertex. Embedded-fetch SGPR
    /// fallback is not required when the game latches this register (GTA UI).
    /// </summary>
    private static int GetBaseVertex(SubmittedDcbState state) =>
        state.UcRegisters.TryGetValue(GeIndxOffset, out var indexOffset)
            ? unchecked((int)indexOffset)
            : 0;

    private static GuestIndexBuffer? CreateGuestIndexBuffer(
        CpuContext ctx,
        SubmittedDcbState state,
        uint indexCount)
    {
        if (state.IndexBufferAddress == 0 || indexCount == 0)
        {
            return null;
        }

        var indexType = GetProsperoIndexType(state);
        var guestBytesPerIndex = AgcIndexHelpers.GetGuestStrideBytes(indexType);
        var byteOffset = checked((ulong)state.DrawIndexOffset * (uint)guestBytesPerIndex);
        var guestByteCount = checked((int)(indexCount * (uint)guestBytesPerIndex));
        var address = state.IndexBufferAddress + byteOffset;

        // Host backends only bind u16/u32. Expand kIndex8 -> u16.
        if (indexType == AgcIndexHelpers.ProsperoIndexType.Index8)
        {
            var guestData = GuestDataPool.Shared.Rent(guestByteCount);
            var guestSpan = guestData.AsSpan(0, guestByteCount);
            if (!ctx.Memory.TryRead(address, guestSpan) &&
                !KernelMemoryCompatExports.TryReadTrackedLibcHeap(address, guestSpan))
            {
                GuestDataPool.Shared.Return(guestData);
                return null;
            }

            var hostByteCount = checked((int)(indexCount * sizeof(ushort)));
            var hostData = GuestDataPool.Shared.Rent(hostByteCount);
            AgcIndexHelpers.ExpandIndex8ToU16(
                guestSpan,
                hostData.AsSpan(0, hostByteCount));
            GuestDataPool.Shared.Return(guestData);
            return new GuestIndexBuffer(hostData, hostByteCount, Is32Bit: false, Pooled: true);
        }

        var is32Bit = indexType == AgcIndexHelpers.ProsperoIndexType.Index32;
        var data = GuestDataPool.Shared.Rent(guestByteCount);
        var span = data.AsSpan(0, guestByteCount);
        if (ctx.Memory.TryRead(address, span) ||
            KernelMemoryCompatExports.TryReadTrackedLibcHeap(address, span))
        {
            return new GuestIndexBuffer(data, guestByteCount, is32Bit, Pooled: true);
        }

        GuestDataPool.Shared.Return(data);
        return null;
    }

    private static bool TryGetRequiredVertexRecordCount(
        CpuContext ctx,
        SubmittedDcbState state,
        uint drawCount,
        bool indexed,
        out uint recordCount)
    {
        var baseVertex = (uint)Math.Max(GetBaseVertex(state), 0);
        recordCount = Math.Max(
            baseVertex + drawCount,
            Math.Max(state.InstanceCount, 1u));
        if (!indexed)
        {
            return true;
        }

        if (state.IndexBufferAddress == 0 || drawCount == 0)
        {
            return false;
        }

        var indexType = GetProsperoIndexType(state);
        var bytesPerIndex = AgcIndexHelpers.GetGuestStrideBytes(indexType);
        var byteOffset = checked((ulong)state.DrawIndexOffset * (uint)bytesPerIndex);
        var address = state.IndexBufferAddress + byteOffset;
        const int chunkBytes = 64 * 1024;
        var scratch = GuestDataPool.Shared.Rent(chunkBytes);
        var remaining = drawCount;
        var maxIndex = 0u;
        var sawIndex = false;
        try
        {
            while (remaining != 0)
            {
                var chunkIndices = (int)Math.Min(
                    remaining,
                    (uint)(chunkBytes / bytesPerIndex));
                var bytes = chunkIndices * bytesPerIndex;
                var span = scratch.AsSpan(0, bytes);
                if (!ctx.Memory.TryRead(address, span) &&
                    !KernelMemoryCompatExports.TryReadTrackedLibcHeap(address, span))
                {
                    return false;
                }

                for (var index = 0; index < chunkIndices; index++)
                {
                    uint value = indexType switch
                    {
                        AgcIndexHelpers.ProsperoIndexType.Index32 =>
                            BinaryPrimitives.ReadUInt32LittleEndian(
                                span.Slice(index * sizeof(uint), sizeof(uint))),
                        AgcIndexHelpers.ProsperoIndexType.Index8 => span[index],
                        _ => BinaryPrimitives.ReadUInt16LittleEndian(
                            span.Slice(index * sizeof(ushort), sizeof(ushort))),
                    };
                    var restart = indexType switch
                    {
                        AgcIndexHelpers.ProsperoIndexType.Index32 => uint.MaxValue,
                        AgcIndexHelpers.ProsperoIndexType.Index8 => 0xFFu,
                        _ => ushort.MaxValue,
                    };
                    if (value == restart)
                    {
                        // Primitive-restart markers do not address vertex data.
                        continue;
                    }

                    maxIndex = Math.Max(maxIndex, value);
                    sawIndex = true;
                }

                address += (uint)bytes;
                remaining -= (uint)chunkIndices;
            }
        }
        finally
        {
            GuestDataPool.Shared.Return(scratch);
        }

        var indexedRecords = sawIndex && maxIndex != uint.MaxValue
            ? baseVertex + maxIndex + 1
            : Math.Max(baseVertex + 1, 1u);
        recordCount = Math.Max(indexedRecords, Math.Max(state.InstanceCount, 1u));
        if (_traceVertexRanges &&
            Interlocked.Increment(ref _tracedVertexRangeCount) <= 512)
        {
            var indexBits = indexType switch
            {
                AgcIndexHelpers.ProsperoIndexType.Index32 => 32,
                AgcIndexHelpers.ProsperoIndexType.Index8 => 8,
                _ => 16,
            };
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.vertex_range indexed=1 draw_count={drawCount} " +
                $"max_index={(sawIndex ? maxIndex : 0)} base_vertex={baseVertex} " +
                $"records={recordCount} instances={state.InstanceCount} " +
                $"index_size={indexBits} index_addr=0x{state.IndexBufferAddress:X16} " +
                $"offset={state.DrawIndexOffset}");
        }
        return true;
    }

    private static uint GetPixelColorExportMask(uint packedMasks, uint target) =>
        target < ColorTargetCount
            ? (packedMasks >> (int)(target * 4)) & 0xFu
            : 0;

    private static bool VertexProgramExportsParameters(Gen5ShaderProgram program)
    {
        foreach (var instruction in program.Instructions)
        {
            if (instruction.Control is Gen5ExportControl export &&
                export.Target is >= 32 and < 64)
            {
                return true;
            }
        }

        return false;
    }

    private static uint GetInterpolatedAttributeCount(Gen5ShaderState state)
    {
        var maxAttribute = -1;
        foreach (var instruction in state.Program.Instructions)
        {
            if (instruction.Control is Gen5InterpolationControl interpolation)
            {
                maxAttribute = Math.Max(maxAttribute, (int)interpolation.Attribute);
            }
        }

        return (uint)(maxAttribute + 1);
    }

    private static uint[] GetPixelInputControls(
        IReadOnlyDictionary<uint, uint> contextRegisters,
        uint attributeCount)
    {
        var controls = new uint[attributeCount];
        for (uint attribute = 0; attribute < attributeCount; attribute++)
        {
            controls[attribute] = contextRegisters.TryGetValue(
                SpiPsInputCntl0 + attribute,
                out var control)
                    ? control
                    : attribute;
        }

        return controls;
    }

    private static ulong ComputePixelInputControlsFingerprint(
        IReadOnlyList<uint> controls)
    {
        const ulong prime = 1099511628211UL;
        var hash = 14695981039346656037UL;
        foreach (var control in controls)
        {
            hash = (hash ^ control) * prime;
        }

        return hash;
    }

    private static int GetRequiredVertexOutputCount(
        IReadOnlyList<uint> controls)
    {
        var maxLocation = -1;
        foreach (var control in controls)
        {
            maxLocation = Math.Max(maxLocation, (int)(control & 0x1Fu));
        }

        return maxLocation + 1;
    }

    private static readonly bool _bakeScalars = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_BAKE_SGPRS"),
        "1",
        StringComparison.Ordinal);

    /// <summary>
    /// Fingerprint of everything that shapes the translated SPIR-V besides
    /// scalar register values (those arrive in a per-draw buffer): the
    /// resolved binding set with its format-shaping descriptor words, vertex
    /// input layouts, and compute system registers. Value churn in user data
    /// no longer forces a new translation and pipeline.
    /// </summary>
    private static ulong ComputeShaderStructuralFingerprint(Gen5ShaderEvaluation evaluation)
    {
        const ulong prime = 1099511628211UL;
        var hash = 14695981039346656037UL;
        void Mix(ulong value) => hash = (hash ^ value) * prime;

        foreach (var binding in evaluation.ImageBindings)
        {
            Mix(binding.Pc);
            Mix((ulong)(uint)binding.Opcode.GetHashCode());
            if (binding.ResourceDescriptor.Count > 1)
            {
                // The generated image type depends only on unified format.
                // Bounds are queried from the bound view in SPIR-V; guest image
                // addresses, dimensions, swizzles and sampler state are all
                // runtime descriptor data and must not create pipeline variants.
                Mix(binding.ResourceDescriptor[1] & 0x1FF0_0000u);
            }

            Mix(binding.MipLevel ?? 0xFFFF_FFFFUL);
        }

        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            Mix(binding.ScalarAddress);
            Mix((ulong)binding.InstructionPcs.Count);
            foreach (var pc in binding.InstructionPcs)
            {
                Mix(pc);
            }
        }

        if (evaluation.VertexInputs is { } vertexInputs)
        {
            foreach (var input in vertexInputs)
            {
                Mix(input.Pc);
                Mix(input.Location);
                Mix(input.ComponentCount);
                Mix(input.DataFormat);
                Mix(input.NumberFormat);
                Mix(input.Stride);
                Mix(input.OffsetBytes);
                Mix(input.PerInstance ? 1u : 0u);
            }
        }

        if (evaluation.ComputeSystemRegisters is { } computeSystemRegisters)
        {
            Mix(computeSystemRegisters.WorkGroupXRegister ?? uint.MaxValue);
            Mix(computeSystemRegisters.WorkGroupYRegister ?? uint.MaxValue);
            Mix(computeSystemRegisters.WorkGroupZRegister ?? uint.MaxValue);
            Mix(computeSystemRegisters.ThreadGroupSizeRegister ?? uint.MaxValue);
        }

        return hash;
    }

    private static ulong ComputeShaderStateFingerprint(Gen5ShaderEvaluation evaluation)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var value in evaluation.ScalarRegisters)
        {
            hash = (hash ^ value) * prime;
        }

        // Baked-scalar mode has no runtime state block from which the shader
        // can load descriptor-alignment biases, so the low guest address bits
        // remain part of the generated module and must participate in its key.
        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            hash = (hash ^ (
                binding.BaseAddress &
                (_storageBufferOffsetAlignment - 1))) * prime;
        }

        if (evaluation.ComputeSystemRegisters is { } computeSystemRegisters)
        {
            hash = (hash ^ (computeSystemRegisters.WorkGroupXRegister ?? uint.MaxValue)) * prime;
            hash = (hash ^ (computeSystemRegisters.WorkGroupYRegister ?? uint.MaxValue)) * prime;
            hash = (hash ^ (computeSystemRegisters.WorkGroupZRegister ?? uint.MaxValue)) * prime;
            hash = (hash ^ (computeSystemRegisters.ThreadGroupSizeRegister ?? uint.MaxValue)) * prime;
        }

        return hash;
    }

    private enum CbColorMode : byte
    {
        Disable = 0,
        Normal = 1,
        EliminateFastClear = 2,
        Resolve = 3,
        FmaskDecompress = 5,
        DccDecompress = 6,
    }

    private static bool TryGetCbColorControlMode(
        IReadOnlyDictionary<uint, uint> registers,
        out uint mode)
    {
        mode = 0;
        if (!registers.TryGetValue(CbColorControl, out var colorControl))
        {
            return false;
        }

        mode = (colorControl >> 4) & 0x7u;
        return true;
    }

    private static bool IsCbMetadataColorMode(uint mode) =>
        mode is (uint)CbColorMode.EliminateFastClear or
            (uint)CbColorMode.FmaskDecompress or
            (uint)CbColorMode.DccDecompress;

    private static bool TryGetHardwareColorResolveTargets(
        IReadOnlyDictionary<uint, uint> registers,
        out RenderTargetDescriptor source,
        out RenderTargetDescriptor destination)
    {
        source = default;
        destination = default;
        if (!TryGetCbColorControlMode(registers, out var mode) ||
            mode != (uint)CbColorMode.Resolve)
        {
            return false;
        }

        // CB_COLOR_CONTROL.MODE=RESOLVE uses color slot 0 as the multisampled
        // source and slot 1 as the single-sample destination. CB_TARGET_MASK
        // still enables only slot 0, so treating this like a normal MRT draw
        // rewrites the source and leaves the following composite's input blank.
        var boundTargets = GetRenderTargets(registers, includeMaskedTargets: true);
        source = boundTargets.FirstOrDefault(target => target.Slot == 0);
        destination = boundTargets.FirstOrDefault(target => target.Slot == 1);
        return source.Address != 0 &&
            destination.Address != 0 &&
            source.Width == destination.Width &&
            source.Height == destination.Height &&
            source.Format == destination.Format;
    }

    private static readonly HashSet<ulong> _renderTargetAddresses = new();
    private static readonly HashSet<ulong> _sampledRenderTargets = new();
    private static readonly object _renderTargetProbeGate = new();
    private static long _renderTargetSampleTraceCount;
    private static long _indirectDrawProbeCount;
    private static long _indirectDrawEmitCount;
    private static long _indirectDrawEmitRejectCount;
    private static long _indirectMultiProbeCount;

    private static void NoteRenderTargetAddress(ulong address)
    {
        if (address == 0)
        {
            return;
        }

        lock (_renderTargetProbeGate)
        {
            if (_renderTargetAddresses.Count < 512)
            {
                _renderTargetAddresses.Add(address);
            }
        }
    }

    private static void NoteSampledAddress(ulong address, uint format = 0, uint numberType = 0)
    {
        if (address == 0)
        {
            return;
        }

        bool firstTime;
        int distinctTargets;
        lock (_renderTargetProbeGate)
        {
            if (!_renderTargetAddresses.Contains(address))
            {
                return;
            }

            firstTime = _sampledRenderTargets.Add(address);
            distinctTargets = _renderTargetAddresses.Count;
        }

        var count = Interlocked.Increment(ref _renderTargetSampleTraceCount);
        if (firstTime || count % 2000 == 0)
        {
            var gpuResident = GuestGpu.Current.IsGpuGuestImageAvailable(address, format, numberType);
            Console.Error.WriteLine(
                $"[LOADER][WARN] agc.rt_sampled#{count} addr=0x{address:X} first={firstTime} " +
                $"gpu_resident={gpuResident} fmt={format}/{numberType} known_targets={distinctTargets}");
        }
    }

    private static IReadOnlyList<RenderTargetDescriptor> GetRenderTargets(
        IReadOnlyDictionary<uint, uint> registers,
        bool includeMaskedTargets = false)
    {
        var hasTargetMask = registers.TryGetValue(CbTargetMask, out var targetMask);
        var targets = new List<RenderTargetDescriptor>(ColorTargetCount);
        for (uint slot = 0; slot < ColorTargetCount; slot++)
        {
            var baseRegister = CbColor0Base + slot * CbColorRegisterStride;
            if (!registers.TryGetValue(baseRegister, out var baseLow) ||
                !registers.TryGetValue(CbColor0BaseExt + slot, out var baseHigh) ||
                !registers.TryGetValue(CbColor0Attrib2 + slot, out var attrib2) ||
                !registers.TryGetValue(CbColor0Attrib3 + slot, out var attrib3) ||
                !registers.TryGetValue(CbColor0Info + slot * CbColorRegisterStride, out var info))
            {
                continue;
            }

            var address = ((ulong)(baseHigh & 0xFFu) << 40) | ((ulong)baseLow << 8);
            var writeMask = (targetMask >> ((int)slot * 4)) & 0xFu;
            if (address == 0 ||
                (!includeMaskedTargets && hasTargetMask && writeMask == 0))
            {
                continue;
            }

            if (targets.Exists(existing => existing.Address == address))
            {
                continue;
            }

            NoteRenderTargetAddress(address);

            targets.Add(new RenderTargetDescriptor(
                slot,
                address,
                ((attrib2 >> 14) & 0x3FFFu) + 1,
                (attrib2 & 0x3FFFu) + 1,
                (info >> 2) & 0x1Fu,
                (info >> 8) & 0x7u,
                (info >> 11) & 0x3u,
                (attrib3 >> 14) & 0x1Fu));
        }

        if (targets.Count > 1 &&
            targets.Select(t => t.Address).Distinct().Count() != targets.Count)
        {
            var dupCount = Interlocked.Increment(ref _duplicateTargetTraceCount);
            if (dupCount <= 12 || dupCount % 500 == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] agc.rt_duplicate#{dupCount} has_mask={hasTargetMask} " +
                    $"mask=0x{targetMask:X8} slots=[" +
                    string.Join(",", targets.Select(t =>
                        $"{t.Slot}:0x{t.Address:X}:m{(targetMask >> ((int)t.Slot * 4)) & 0xFu}")) +
                    "]");
            }
        }

        return targets;
    }

    private static RenderTargetDescriptor[] BuildHostRenderTargets(
        IReadOnlyList<RenderTargetDescriptor> guestTargets,
        GuestRenderState guestRenderState,
        out uint[] hostOutputLocations)
    {
        hostOutputLocations = new uint[guestTargets.Count];
        var hostTargets = new List<RenderTargetDescriptor>(guestTargets.Count);
        for (var guestIndex = 0; guestIndex < guestTargets.Count; guestIndex++)
        {
            var guestTarget = guestTargets[guestIndex];
            var hostIndex = -1;
            if (CanMergeAliasedRenderTarget(
                    guestTargets,
                    guestRenderState.Blends,
                    guestIndex))
            {
                hostIndex = hostTargets.FindIndex(
                    target => RenderTargetsShareSurface(target, guestTarget));
            }

            if (hostIndex < 0)
            {
                hostIndex = hostTargets.Count;
                hostTargets.Add(guestTarget);
            }

            hostOutputLocations[guestIndex] = (uint)hostIndex;
        }

        return hostTargets.ToArray();
    }

    private static bool CanMergeAliasedRenderTarget(
        IReadOnlyList<RenderTargetDescriptor> targets,
        IReadOnlyList<GuestBlendState> blends,
        int targetIndex)
    {
        var target = targets[targetIndex];
        var matchingTargets = 0;
        var combinedMask = 0u;
        GuestBlendState? commonBlend = null;
        for (var index = 0; index < targets.Count; index++)
        {
            if (!RenderTargetsShareSurface(targets[index], target))
            {
                continue;
            }

            matchingTargets++;
            var blend = blends[index];
            var blendWithoutMask = blend with { WriteMask = 0 };
            if (commonBlend is { } expectedBlend && expectedBlend != blendWithoutMask)
            {
                return false;
            }

            commonBlend ??= blendWithoutMask;
            if ((combinedMask & blend.WriteMask) != 0)
            {
                return false;
            }

            combinedMask |= blend.WriteMask;
        }

        return matchingTargets > 1;
    }

    private static bool RenderTargetsShareSurface(
        RenderTargetDescriptor left,
        RenderTargetDescriptor right) =>
        left.Address == right.Address &&
        left.Width == right.Width &&
        left.Height == right.Height &&
        left.Format == right.Format &&
        left.NumberType == right.NumberType &&
        left.TileMode == right.TileMode;

    private static GuestRenderState CollapseGuestRenderState(
        GuestRenderState guestRenderState,
        IReadOnlyList<uint> hostOutputLocations,
        int hostTargetCount)
    {
        var hostBlends = new GuestBlendState[hostTargetCount];
        var initialized = new bool[hostTargetCount];
        for (var guestIndex = 0; guestIndex < hostOutputLocations.Count; guestIndex++)
        {
            var hostIndex = checked((int)hostOutputLocations[guestIndex]);
            var guestBlend = guestRenderState.Blends[guestIndex];
            if (!initialized[hostIndex])
            {
                hostBlends[hostIndex] = guestBlend;
                initialized[hostIndex] = true;
                continue;
            }

            hostBlends[hostIndex] = hostBlends[hostIndex] with
            {
                WriteMask = hostBlends[hostIndex].WriteMask | guestBlend.WriteMask,
            };
        }

        return guestRenderState with { Blends = hostBlends };
    }

    private static GuestRenderState CreateRenderState(
        IReadOnlyDictionary<uint, uint> registers,
        RenderTargetDescriptor target)
    {
        var scissor = DecodeScissor(registers, target.Width, target.Height);
        return new GuestRenderState(
            [DecodeBlendState(registers, target.Slot)],
            scissor,
            DecodeViewport(registers, target.Width, target.Height, scissor),
            DecodeRasterState(registers),
            DecodeDepthState(registers),
            DecodeBlendConstant(registers));
    }

    private static GuestRenderState CreateRenderState(
        IReadOnlyDictionary<uint, uint> registers,
        IReadOnlyList<RenderTargetDescriptor> targets,
        uint pixelColorExportMasks)
    {
        if (targets.Count == 0)
        {
            return GuestRenderState.Default;
        }

        var target = targets[0];
        var scissor = DecodeScissor(registers, target.Width, target.Height);
        var blends = new GuestBlendState[targets.Count];
        for (var index = 0; index < targets.Count; index++)
        {
            var blend = DecodeBlendState(registers, targets[index].Slot);
            blends[index] = blend with
            {
                WriteMask = blend.WriteMask &
                    GetPixelColorExportMask(
                        pixelColorExportMasks,
                        targets[index].Slot),
            };
        }

        return new GuestRenderState(
            blends,
            scissor,
            DecodeViewport(registers, target.Width, target.Height, scissor),
            DecodeRasterState(registers),
            DecodeDepthState(registers),
            DecodeBlendConstant(registers));
    }

    // DB_DEPTH_CONTROL (context register 0x200): Z_ENABLE bit1, Z_WRITE_ENABLE
    // bit2, ZFUNC bits[6:4] (GCN compare, matches Vulkan CompareOp ordering).
    // DB_RENDER_CONTROL (context register 0x000): DEPTH_CLEAR_ENABLE bit0.
    private const uint DbDepthControl = 0x200;

    internal static GuestDepthState DecodeDepthState(
        IReadOnlyDictionary<uint, uint> registers)
    {
        var hasDepthControl = registers.TryGetValue(DbDepthControl, out var control);
        registers.TryGetValue(DbRenderControl, out var renderControl);
        var testEnable = (control & 0x2u) != 0;
        var writeEnable = (control & 0x4u) != 0;
        var compareOp = hasDepthControl
            ? (control >> 4) & 0x7u
            : GuestDepthState.Default.CompareOp;
        var clearEnable = (renderControl & 0x1u) != 0;
        return new GuestDepthState(testEnable, writeEnable, compareOp, clearEnable);
    }

    private static GuestDepthTarget? DecodeDepthTarget(
        IReadOnlyDictionary<uint, uint> registers)
    {
        var depthState = DecodeDepthState(registers);
        if (!depthState.TestEnable &&
            !depthState.WriteEnable &&
            !depthState.ClearEnable)
        {
            return null;
        }

        if (!registers.TryGetValue(DbZInfo, out var zInfo) ||
            !registers.TryGetValue(DbDepthSizeXy, out var sizeXy))
        {
            return null;
        }

        var guestFormat = zInfo & 0x3u;
        if (guestFormat == 0)
        {
            return null;
        }

        registers.TryGetValue(DbZReadBase, out var readBase);
        registers.TryGetValue(DbZWriteBase, out var writeBase);
        registers.TryGetValue(DbZReadBaseHi, out var readBaseHi);
        registers.TryGetValue(DbZWriteBaseHi, out var writeBaseHi);
        var readAddress = ((ulong)(readBaseHi & 0xFFu) << 40) | ((ulong)readBase << 8);
        var writeAddress = ((ulong)(writeBaseHi & 0xFFu) << 40) | ((ulong)writeBase << 8);
        if (readAddress == 0 && writeAddress == 0)
        {
            return null;
        }

        var width = (sizeXy & 0x3FFFu) + 1;
        var height = ((sizeXy >> 16) & 0x3FFFu) + 1;
        if (width == 0 || height == 0 || width > 16384 || height > 16384)
        {
            return null;
        }

        registers.TryGetValue(DbDepthView, out var depthView);
        var clearDepth = registers.TryGetValue(DbDepthClear, out var clearBits)
            ? BitConverter.UInt32BitsToSingle(clearBits)
            : 1f;
        if (!float.IsFinite(clearDepth) || clearDepth < 0f || clearDepth > 1f)
        {
            clearDepth = 1f;
        }

        return new GuestDepthTarget(
            readAddress,
            writeAddress,
            width,
            height,
            guestFormat,
            (zInfo >> 4) & 0x1Fu,
            clearDepth,
            ReadOnly: (depthView & (1u << 24)) != 0 || writeAddress == 0);
    }

    // PA_SU_SC_MODE_CNTL (context register 0x205) carries face culling, the
    // front-face winding and polygon (wireframe) mode.
    private const uint PaSuScModeCntl = 0x205;

    private static GuestRasterState DecodeRasterState(
        IReadOnlyDictionary<uint, uint> registers)
    {
        if (!registers.TryGetValue(PaSuScModeCntl, out var mode))
        {
            return GuestRasterState.Default;
        }

        var cullFront = (mode & 0x1u) != 0;
        var cullBack = (mode & 0x2u) != 0;
        var frontFaceClockwise = (mode & 0x4u) != 0;
        var polyMode = (mode >> 3) & 0x3u;
        var frontPtype = (mode >> 5) & 0x7u;
        // POLY_MODE != 0 with a line front primitive type renders wireframe.
        var wireframe = polyMode != 0 && frontPtype == 1;
        return new GuestRasterState(cullFront, cullBack, frontFaceClockwise, wireframe);
    }

    /// <summary>CB_BLEND_RED..ALPHA carry the constant blend color as raw
    /// float bits; unwritten registers read as the reset value (0.0).</summary>
    private static GuestBlendConstant DecodeBlendConstant(
        IReadOnlyDictionary<uint, uint> registers)
    {
        registers.TryGetValue(CbBlendRed, out var red);
        registers.TryGetValue(CbBlendGreen, out var green);
        registers.TryGetValue(CbBlendBlue, out var blue);
        registers.TryGetValue(CbBlendAlpha, out var alpha);
        return new GuestBlendConstant(
            BitConverter.Int32BitsToSingle(unchecked((int)red)),
            BitConverter.Int32BitsToSingle(unchecked((int)green)),
            BitConverter.Int32BitsToSingle(unchecked((int)blue)),
            BitConverter.Int32BitsToSingle(unchecked((int)alpha)));
    }

    private static GuestBlendState DecodeBlendState(
        IReadOnlyDictionary<uint, uint> registers,
        uint slot)
    {
        var writeMask = 0xFu;
        if (registers.TryGetValue(CbTargetMask, out var targetMask))
        {
            writeMask = (targetMask >> checked((int)(slot * 4))) & 0xFu;
        }

        registers.TryGetValue(CbBlend0Control + slot, out var control);
        return new GuestBlendState(
            ((control >> 30) & 1u) != 0,
            control & 0x1Fu,
            (control >> 8) & 0x1Fu,
            (control >> 5) & 0x7u,
            (control >> 16) & 0x1Fu,
            (control >> 24) & 0x1Fu,
            (control >> 21) & 0x7u,
            ((control >> 29) & 1u) != 0,
            writeMask);
    }

    private static GuestRect? DecodeScissor(
        IReadOnlyDictionary<uint, uint> registers,
        uint targetWidth,
        uint targetHeight)
    {
        if (targetWidth == 0 || targetHeight == 0)
        {
            return new GuestRect(0, 0, 0, 0);
        }

        var left = 0;
        var top = 0;
        var right = checked((int)Math.Min(targetWidth, int.MaxValue));
        var bottom = checked((int)Math.Min(targetHeight, int.MaxValue));

        var windowOffsetX = 0;
        var windowOffsetY = 0;
        var enableWindowOffset = true;
        if (registers.TryGetValue(PaScWindowScissorTl, out var windowScissorTl))
        {
            enableWindowOffset = (windowScissorTl & 0x80000000u) == 0;
        }

        if (enableWindowOffset &&
            registers.TryGetValue(PaScWindowOffset, out var windowOffset))
        {
            windowOffsetX = (short)(windowOffset & 0xFFFFu);
            windowOffsetY = (short)(windowOffset >> 16);
        }

        // AGC reset-state blocks can carry an all-zero screen-scissor pair as
        // an unpatched placeholder while the generic/viewport scissors hold
        // the active bounds. Treat only that exact reset value as absent. A
        // nonzero empty rectangle remains meaningful and still clips the draw.
        IntersectScissorPair(
            registers,
            PaScScreenScissorTl,
            PaScScreenScissorBr,
            ref left,
            ref top,
            ref right,
            ref bottom,
            ignoreAllZeroPair: true);
        IntersectScissorPair(
            registers,
            PaScWindowScissorTl,
            PaScWindowScissorBr,
            ref left,
            ref top,
            ref right,
            ref bottom,
            windowOffsetX,
            windowOffsetY);
        IntersectScissorPair(
            registers,
            PaScGenericScissorTl,
            PaScGenericScissorBr,
            ref left,
            ref top,
            ref right,
            ref bottom,
            windowOffsetX,
            windowOffsetY);
        var vportScissorEnabled =
            !registers.TryGetValue(PaScModeCntl0, out var modeControl) ||
            ((modeControl >> 1) & 1u) != 0;
        if (vportScissorEnabled)
        {
            IntersectScissorPair(registers, PaScVportScissor0Tl, PaScVportScissor0Br, ref left, ref top, ref right, ref bottom);
        }

        left = Math.Clamp(left, 0, checked((int)targetWidth));
        top = Math.Clamp(top, 0, checked((int)targetHeight));
        right = Math.Clamp(right, left, checked((int)targetWidth));
        bottom = Math.Clamp(bottom, top, checked((int)targetHeight));

        if (left == 0 &&
            top == 0 &&
            right == (int)targetWidth &&
            bottom == (int)targetHeight)
        {
            return null;
        }

        return new GuestRect(
            left,
            top,
            checked((uint)(right - left)),
            checked((uint)(bottom - top)));
    }

    private static GuestViewport? DecodeViewport(
        IReadOnlyDictionary<uint, uint> registers,
        uint targetWidth,
        uint targetHeight,
        GuestRect? scissor)
    {
        if (targetWidth == 0 || targetHeight == 0)
        {
            return new GuestViewport(0, 0, 0, 0, 0, 1);
        }

        var minDepth = 0f;
        var maxDepth = 1f;
        if (registers.TryGetValue(PaScVportZMin0, out var zMinBits) &&
            registers.TryGetValue(PaScVportZMax0, out var zMaxBits))
        {
            var decodedMin = BitConverter.UInt32BitsToSingle(zMinBits);
            var decodedMax = BitConverter.UInt32BitsToSingle(zMaxBits);
            if (float.IsFinite(decodedMin) &&
                float.IsFinite(decodedMax) &&
                decodedMax > decodedMin)
            {
                minDepth = decodedMin;
                maxDepth = decodedMax;
            }
        }

        if (TryDecodeFiniteFloat(registers, PaClVportXScale, out var xScale) &&
            TryDecodeFiniteFloat(registers, PaClVportXOffset, out var xOffset) &&
            TryDecodeFiniteFloat(registers, PaClVportYScale, out var yScale) &&
            TryDecodeFiniteFloat(registers, PaClVportYOffset, out var yOffset) &&
            xScale > 0f &&
            yScale != 0f)
        {
            return new GuestViewport(
                xOffset - xScale,
                yOffset - yScale,
                xScale * 2f,
                yScale * 2f,
                minDepth,
                maxDepth);
        }

        if (scissor is not { } rect)
        {
            return minDepth == 0f && maxDepth == 1f
                ? null
                : new GuestViewport(0, 0, targetWidth, targetHeight, minDepth, maxDepth);
        }

        return new GuestViewport(
            rect.X,
            rect.Y,
            rect.Width,
            rect.Height,
            minDepth,
            maxDepth);
    }

    private static bool TryDecodeFiniteFloat(
        IReadOnlyDictionary<uint, uint> registers,
        uint register,
        out float value)
    {
        value = 0;
        if (!registers.TryGetValue(register, out var bits))
        {
            return false;
        }

        value = BitConverter.UInt32BitsToSingle(bits);
        return float.IsFinite(value);
    }

    private static void IntersectScissorPair(
        IReadOnlyDictionary<uint, uint> registers,
        uint tlRegister,
        uint brRegister,
        ref int left,
        ref int top,
        ref int right,
        ref int bottom,
        int offsetX = 0,
        int offsetY = 0,
        bool ignoreAllZeroPair = false)
    {
        if (!TryDecodeScissorPair(
                registers,
                tlRegister,
                brRegister,
                out var pairLeft,
                out var pairTop,
                out var pairRight,
                out var pairBottom,
                out var allZero) ||
            (ignoreAllZeroPair && allZero))
        {
            return;
        }

        pairLeft += offsetX;
        pairTop += offsetY;
        pairRight += offsetX;
        pairBottom += offsetY;

        left = Math.Max(left, pairLeft);
        top = Math.Max(top, pairTop);
        right = Math.Min(right, pairRight);
        bottom = Math.Min(bottom, pairBottom);
    }

    private static bool TryDecodeScissorPair(
        IReadOnlyDictionary<uint, uint> registers,
        uint tlRegister,
        uint brRegister,
        out int left,
        out int top,
        out int right,
        out int bottom,
        out bool allZero)
    {
        left = 0;
        top = 0;
        right = 0;
        bottom = 0;
        allZero = false;
        if (!registers.TryGetValue(tlRegister, out var tl) ||
            !registers.TryGetValue(brRegister, out var br))
        {
            return false;
        }

        allZero = tl == 0 && br == 0;
        left = (int)(tl & 0x7FFFu);
        top = (int)((tl >> 16) & 0x7FFFu);
        right = (int)(br & 0x7FFFu);
        bottom = (int)((br >> 16) & 0x7FFFu);
        return true;
    }

    private static void TraceTranslatedGuestDraw(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        TranslatedGuestDraw draw,
        uint psInputEna,
        uint psInputAddr,
        bool force)
    {
        var targets = draw.RenderTargets.Count == 0
            ? "none"
            : string.Join(
                ',',
                draw.RenderTargets.Select(target =>
                    $"{target.Slot}:0x{target.Address:X16}:{target.Width}x{target.Height}:" +
                    $"fmt{target.Format}/num{target.NumberType}/tile{target.TileMode}"));
        var depthTarget = draw.DepthTarget is { } depth
            ? $"0x{depth.Address:X16}:{depth.Width}x{depth.Height}:" +
              $"fmt{depth.GuestFormat}/sw{depth.SwizzleMode}:" +
              $"read=0x{depth.ReadAddress:X16}/write=0x{depth.WriteAddress:X16}:" +
              $"clear={depth.ClearDepth:0.######}/ro={(depth.ReadOnly ? 1 : 0)}"
            : "none";
        var probes = new Dictionary<ulong, string>();
        var textures = string.Join(
            ',',
            draw.Textures.Select(binding =>
            {
                var texture = binding.Descriptor;
                var targetSlot = draw.RenderTargets
                    .FirstOrDefault(target => target.Address == texture.Address)
                    .Slot;
                var target = draw.RenderTargets.Any(candidate => candidate.Address == texture.Address)
                    ? $"/rt{targetSlot}"
                    : string.Empty;
                if (!probes.TryGetValue(texture.Address, out var probe))
                {
                    probe = ProbeTexture(ctx, texture);
                    probes.Add(texture.Address, probe);
                }

                state.RenderTargetWriters.TryGetValue(texture.Address, out var sourceWriter);
                gpuState.ComputeImageWriters.TryGetValue(texture.Address, out var computeWriter);
                var writer = sourceWriter.Sequence >= computeWriter.Sequence && sourceWriter.Sequence != 0
                    ? $"/writer={sourceWriter.Sequence}:" +
                      $"es0x{sourceWriter.ExportShaderAddress:X}:" +
                      $"ps0x{sourceWriter.PixelShaderAddress:X}:" +
                      $"v{sourceWriter.VertexCount}:prim0x{sourceWriter.PrimitiveType:X}"
                    : computeWriter.Sequence != 0
                        ? $"/compute={computeWriter.Sequence}:" +
                          $"cs0x{computeWriter.ShaderAddress:X}:{computeWriter.Opcode}"
                        : "/writer=none";
                return
                    $"0x{texture.Address:X16}:{texture.Width}x{texture.Height}:" +
                    $"fmt{texture.Format}/num{texture.NumberType}/tile{texture.TileMode}" +
                    $"/storage={binding.IsStorage}{target}/{probe}{writer}";
            }));
        var buffers = string.Join(
            ',',
            draw.GlobalMemoryBindings.Select((binding, index) =>
                $"{index}:0x{binding.BaseAddress:X16}:{binding.DataLength}:" +
                Convert.ToHexString(binding.Data.AsSpan(0, Math.Min(binding.DataLength, 256)))));
        var indices = draw.IndexBuffer is { } indexBuffer
            ? $"{(indexBuffer.Is32Bit ? 32 : 16)}:" +
              Convert.ToHexString(indexBuffer.Data.AsSpan(0, Math.Min(indexBuffer.Length, 32)))
            : "none";
        var vertexInputs = draw.VertexInputs.Count == 0
            ? "none"
            : string.Join(
                ',',
                draw.VertexInputs.Select(input =>
                    $"{input.Location}:pc=0x{input.Pc:X}:0x{input.BaseAddress:X16}" +
                    $":stride{input.Stride}:off{input.OffsetBytes}:c{input.ComponentCount}" +
                    $":fmt{input.DataFormat}/num{input.NumberFormat}"));
        var scissor = draw.RenderState.Scissor is { } drawScissor
            ? $"{drawScissor.X},{drawScissor.Y},{drawScissor.Width}x{drawScissor.Height}"
            : "full";
        var viewport = draw.RenderState.Viewport is { } drawViewport
            ? $"{drawViewport.X:0.###},{drawViewport.Y:0.###}," +
              $"{drawViewport.Width:0.###}x{drawViewport.Height:0.###}:" +
              $"{drawViewport.MinDepth:0.###}-{drawViewport.MaxDepth:0.###}"
            : "full";
        var rasterRegisters = new (string Name, uint Offset)[]
        {
            ("screen_tl", PaScScreenScissorTl),
            ("screen_br", PaScScreenScissorBr),
            ("window_off", PaScWindowOffset),
            ("window_tl", PaScWindowScissorTl),
            ("window_br", PaScWindowScissorBr),
            ("generic_tl", PaScGenericScissorTl),
            ("generic_br", PaScGenericScissorBr),
            ("vport_tl", PaScVportScissor0Tl),
            ("vport_br", PaScVportScissor0Br),
            ("mode", PaScModeCntl0),
            ("xscale", PaClVportXScale),
            ("xoffset", PaClVportXOffset),
            ("yscale", PaClVportYScale),
            ("yoffset", PaClVportYOffset),
        };
        var raster = string.Join(
            ',',
            rasterRegisters.Select(entry =>
                state.CxRegisters.TryGetValue(entry.Offset, out var value)
                    ? $"{entry.Name}=0x{value:X8}"
                    : $"{entry.Name}=missing"));
        var blend = draw.RenderState.Blend;
        var rectExpanded = AgcPrimitiveHelpers.GetRectListDrawVertexCount(
            draw.PrimitiveType,
            draw.VertexCount,
            indexed: draw.IndexBuffer is not null,
            hasVertexBuffers: draw.VertexInputs.Count > 0);
        var message =
            $"agc.shader_draw es=0x{draw.ExportShaderAddress:X16} " +
            $"ps=0x{draw.PixelShaderAddress:X16} spirv={draw.PixelShader.Payload.Length} " +
            $"primitive=0x{draw.PrimitiveType:X} verts={draw.VertexCount}->{rectExpanded} " +
            $"blend={(blend.Enable ? 1 : 0)}:{blend.ColorSrcFactor}/{blend.ColorDstFactor}/{blend.ColorFunc} " +
            $"write_mask=0x{blend.WriteMask:X} scissor={scissor} viewport={viewport} " +
            $"raster=[{raster}] " +
            $"ps_ena=0x{psInputEna:X8} ps_addr=0x{psInputAddr:X8} " +
            $"targets=[{targets}] depth=[{depthTarget}] textures=[{textures}] " +
            $"buffers=[{buffers}] vertex=[{vertexInputs}] indices=[{indices}]";
        if (force)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] {message}");
        }
        else
        {
            TraceAgcShader(message);
        }
    }

    private static IReadOnlyList<GuestDrawTexture> CreateGuestDrawTextures(
        CpuContext ctx,
        IReadOnlyList<TranslatedImageBinding> bindings,
        out int fallbackTextureCount)
    {
        var textures = new List<GuestDrawTexture>(bindings.Count);
        fallbackTextureCount = 0;
        foreach (var binding in bindings)
        {
            if (TryCreateGuestDrawTexture(
                    ctx,
                    binding.Descriptor,
                    binding.IsStorage,
                    binding.WritesImage,
                    binding.MipLevel,
                    binding.SamplerDescriptor,
                    binding.IsArrayed,
                    out var texture))
            {
                textures.Add(texture);
                if (texture.IsFallback)
                {
                    fallbackTextureCount++;
                }
            }
        }

        return textures;
    }

    /// <summary>
    /// Guest storage buffers for a translated draw, followed by the per-draw
    /// initial scalar registers of each stage (pixel then vertex), matching
    /// the binding layout the shaders were compiled against.
    /// </summary>
    private static IReadOnlyList<GuestMemoryBuffer> CreateTranslatedDrawGlobalBuffers(
        TranslatedGuestDraw translatedDraw)
    {
        var buffers = CreateGuestMemoryBuffers(translatedDraw.GlobalMemoryBindings);
        if (_bakeScalars && !translatedDraw.UsesGds && translatedDraw.Ngg is null)
        {
            return buffers;
        }

        var combined = new List<GuestMemoryBuffer>(
            buffers.Count +
            (_bakeScalars ? 0 : 2) +
            (translatedDraw.UsesGds ? 1 : 0) +
            (translatedDraw.Ngg is null ? 0 : 1));
        combined.AddRange(buffers);
        if (!_bakeScalars)
        {
            var runtimeStateLength = GetRuntimeScalarBufferLength(
                translatedDraw.GlobalMemoryBindings.Count);
            combined.Add(new GuestMemoryBuffer(
                0,
                PackRuntimeScalarState(
                    translatedDraw.PixelInitialScalars,
                    translatedDraw.GlobalMemoryBindings),
                runtimeStateLength,
                Pooled: true));
            combined.Add(new GuestMemoryBuffer(
                0,
                PackRuntimeScalarState(
                    translatedDraw.VertexInitialScalars,
                    translatedDraw.GlobalMemoryBindings),
                runtimeStateLength,
                Pooled: true));
        }

        if (translatedDraw.UsesGds)
        {
            combined.Add(new GuestMemoryBuffer(
                SyntheticGdsBaseAddress,
                _persistentGds,
                Gen5SpirvTranslator.GdsByteSize,
                Pooled: false,
                Writable: true,
                WriteBackToGuest: false));
        }
        if (translatedDraw.Ngg is { } ngg)
        {
            combined.Add(ngg.OutputBuffer);
        }
        return combined;
    }

    private static IReadOnlyList<GuestMemoryBuffer>
        CreateGlobalBufferOwnershipView(
            IReadOnlyList<GuestMemoryBuffer> buffers,
            bool ownsPooledData)
    {
        var view = new GuestMemoryBuffer[buffers.Count];
        for (var index = 0; index < buffers.Count; index++)
        {
            var buffer = buffers[index];
            view[index] = buffer with
            {
                Pooled = ownsPooledData && buffer.Pooled,
            };
        }

        return view;
    }

    private static void SubmitNggComputePrepass(
        TranslatedGuestDraw draw,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers)
    {
        if (draw.Ngg is not { } ngg)
        {
            return;
        }

        GuestGpu.Current.SubmitComputeDispatch(
            draw.ExportShaderAddress,
            ngg.ComputeShader,
            textures,
            CreateGlobalBufferOwnershipView(
                globalMemoryBuffers,
                ownsPooledData: false),
            groupCountX: 1,
            groupCountY: 1,
            groupCountZ: 1,
            baseGroupX: 0,
            baseGroupY: 0,
            baseGroupZ: 0,
            localSizeX: 64,
            localSizeY: 1,
            localSizeZ: 1,
            isIndirect: false,
            writesGlobalMemory: true,
            threadCountX: 64,
            threadCountY: 1,
            threadCountZ: 1);
        TraceAgcShader(
            $"agc.ngg_compute_submit es=0x{draw.ExportShaderAddress:X16} " +
            $"output=0x{ngg.OutputBuffer.BaseAddress:X16}:" +
            $"{ngg.OutputBuffer.Length}");
    }

    /// <summary>
    /// Present-time variant: the flip path can reuse the same translated
    /// draw across several flips and swapchain retries, so it must not wrap
    /// the (pooled, single-consumption) binding arrays. Buffer contents are
    /// re-read from guest memory instead, which also presents current data.
    /// </summary>
    private static IReadOnlyList<GuestMemoryBuffer> CreateTranslatedDrawGlobalBuffersForPresent(
        CpuContext ctx,
        TranslatedGuestDraw translatedDraw)
    {
        var bindings = translatedDraw.GlobalMemoryBindings;
        var combined = new List<GuestMemoryBuffer>(
            bindings.Count +
            (_bakeScalars ? 0 : 2) +
            (translatedDraw.UsesGds ? 1 : 0) +
            (translatedDraw.Ngg is null ? 0 : 1));
        foreach (var binding in bindings)
        {
            var data = new byte[Math.Max(binding.DataLength, sizeof(uint))];
            var guestMemoryBacked = binding.BaseAddress != 0 &&
                (ctx.Memory.TryRead(binding.BaseAddress, data) ||
                 KernelMemoryCompatExports.TryReadTrackedLibcHeap(binding.BaseAddress, data));
            if (!guestMemoryBacked)
            {
                // Keep the zero-filled buffer; layout must match the shader.
            }

            combined.Add(new GuestMemoryBuffer(
                binding.BaseAddress,
                data,
                data.Length,
                Pooled: false,
                Writable: binding.Writable,
                WriteBackToGuest: binding.WriteBackToGuest && guestMemoryBacked));
        }

        if (!_bakeScalars)
        {
            var runtimeStateLength = GetRuntimeScalarBufferLength(bindings.Count);
            combined.Add(new GuestMemoryBuffer(
                0,
                PackRuntimeScalarStateUnpooled(
                    translatedDraw.PixelInitialScalars,
                    bindings),
                runtimeStateLength,
                Pooled: false));
            combined.Add(new GuestMemoryBuffer(
                0,
                PackRuntimeScalarStateUnpooled(
                    translatedDraw.VertexInitialScalars,
                    bindings),
                runtimeStateLength,
                Pooled: false));
        }

        if (translatedDraw.UsesGds)
        {
            combined.Add(new GuestMemoryBuffer(
                SyntheticGdsBaseAddress,
                _persistentGds,
                Gen5SpirvTranslator.GdsByteSize,
                Pooled: false,
                Writable: true,
                WriteBackToGuest: false));
        }

        if (translatedDraw.Ngg is { } ngg)
        {
            combined.Add(ngg.OutputBuffer);
        }

        return combined;
    }

    private static int GetRuntimeScalarBufferLength(int bindingCount) =>
        checked(Gen5RuntimeScalarLayout.GetDwordLength(bindingCount) * sizeof(uint));

    private static byte[] PackRuntimeScalarState(
        IReadOnlyList<uint> registers,
        IReadOnlyList<Gen5GlobalMemoryBinding> bindings)
    {
        var bytes = GuestDataPool.Shared.Rent(
            GetRuntimeScalarBufferLength(bindings.Count));
        PackRuntimeScalarStateInto(bytes, registers, bindings);
        return bytes;
    }

    private static byte[] PackRuntimeScalarStateUnpooled(
        IReadOnlyList<uint> registers,
        IReadOnlyList<Gen5GlobalMemoryBinding> bindings)
    {
        var bytes = new byte[GetRuntimeScalarBufferLength(bindings.Count)];
        PackRuntimeScalarStateInto(bytes, registers, bindings);
        return bytes;
    }

    internal static void PackRuntimeScalarStateInto(
        byte[] bytes,
        IReadOnlyList<uint> registers,
        IReadOnlyList<Gen5GlobalMemoryBinding> bindings)
    {
        PackScalarRegistersInto(bytes, registers);
        for (var index = 0; index < bindings.Count; index++)
        {
            var byteBias = checked((uint)(
                bindings[index].BaseAddress &
                (_storageBufferOffsetAlignment - 1)));
            var biasOffset = checked(
                Gen5RuntimeScalarLayout.GetByteBiasDwordIndex(index) *
                sizeof(uint));
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(biasOffset, sizeof(uint)),
                byteBias);
            // The presenter rounds the descriptor offset down to the portable
            // storage-buffer alignment and exposes the discarded low address
            // bits as byteBias. Shader addresses include that bias, so the
            // explicit bounds metadata must describe the same descriptor range
            // as Vulkan: max(data length, one dword) + byteBias, dword-aligned.
            var descriptorBytes = checked(
                (long)Math.Max(bindings[index].DataLength, sizeof(uint)) +
                byteBias);
            var dwordCount = checked((uint)(
                (descriptorBytes + sizeof(uint) - 1) /
                sizeof(uint)));
            var dwordCountOffset = checked(
                Gen5RuntimeScalarLayout.GetBufferDwordCountDwordIndex(index) *
                sizeof(uint));
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(dwordCountOffset, sizeof(uint)),
                dwordCount);
        }
    }

    private static void PackScalarRegistersInto(byte[] bytes, IReadOnlyList<uint> registers)
    {
        if (registers is uint[] { Length: >= 256 } array)
        {
            // Guest scalar registers are little-endian dwords and the host
            // is x86-64, so a bulk copy replaces 256 per-element writes.
            System.Runtime.InteropServices.MemoryMarshal
                .AsBytes(array.AsSpan(0, 256))
                .CopyTo(bytes);
            return;
        }

        // Rented arrays carry stale bytes; clear the packed window first.
        Array.Clear(bytes, 0, 256 * sizeof(uint));
        var count = Math.Min(registers.Count, 256);
        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                registers[index]);
        }
    }

    /// <summary>
    /// Returns the pooled buffer arrays an evaluation produced. Called only
    /// on translation-failure paths, where no <see cref="TranslatedGuestDraw"/>
    /// is built to take ownership; on success the draw's consumers return them.
    /// </summary>
    private static void ReturnPooledEvaluationArrays(Gen5ShaderEvaluation evaluation)
    {
        var returned = new HashSet<byte[]>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            if (binding.DataPooled && returned.Add(binding.Data))
            {
                GuestDataPool.Shared.Return(binding.Data);
            }
        }

        if (evaluation.VertexInputs is { } vertexInputs)
        {
            foreach (var binding in vertexInputs)
            {
                if (binding.DataPooled && returned.Add(binding.Data))
                {
                    GuestDataPool.Shared.Return(binding.Data);
                }
            }
        }
    }

    /// <summary>
    /// Returns pooled data arrays a translated draw owns but did not hand to
    /// a presenter consumer. The offscreen path hands globals, vertex and
    /// index buffers to the presenter (which returns them), so it passes all
    /// three false; other draw sinks pass true for whatever they dropped.
    /// </summary>
    private static void ReturnPooledDrawArrays(
        TranslatedGuestDraw draw,
        bool globals,
        bool vertex,
        bool index)
    {
        var returned = new HashSet<byte[]>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        if (globals)
        {
            foreach (var binding in draw.GlobalMemoryBindings)
            {
                if (binding.DataPooled && returned.Add(binding.Data))
                {
                    GuestDataPool.Shared.Return(binding.Data);
                }
            }
        }

        if (vertex)
        {
            foreach (var binding in draw.VertexInputs)
            {
                if (binding.DataPooled && returned.Add(binding.Data))
                {
                    GuestDataPool.Shared.Return(binding.Data);
                }
            }
        }

        if (index && draw.IndexBuffer is { Pooled: true } indexBuffer &&
            returned.Add(indexBuffer.Data))
        {
            GuestDataPool.Shared.Return(indexBuffer.Data);
        }
    }

    private static IReadOnlyList<GuestMemoryBuffer> CreateGuestMemoryBuffers(
        IReadOnlyList<Gen5GlobalMemoryBinding> bindings)
    {
        var buffers = new GuestMemoryBuffer[bindings.Count];
        for (var index = 0; index < bindings.Count; index++)
        {
            buffers[index] = new GuestMemoryBuffer(
                bindings[index].BaseAddress,
                bindings[index].Data,
                bindings[index].DataLength,
                bindings[index].DataPooled,
                bindings[index].Writable,
                bindings[index].WriteBackToGuest);
        }

        return buffers;
    }

    /// <summary>
    /// Guest storage buffers for a compute dispatch followed by its initial
    /// scalar registers. Dispatch-specific SGPR values remain runtime data so
    /// one translated pipeline serves every matching shader/resource shape.
    /// </summary>
    private static IReadOnlyList<GuestMemoryBuffer> CreateTranslatedComputeGlobalBuffers(
        Gen5ShaderEvaluation evaluation,
        bool usesGds)
    {
        var buffers = CreateGuestMemoryBuffers(evaluation.GlobalMemoryBindings);
        if (_bakeScalars && !usesGds)
        {
            return buffers;
        }

        var combined = new List<GuestMemoryBuffer>(
            buffers.Count + (_bakeScalars ? 0 : 1) + (usesGds ? 1 : 0));
        combined.AddRange(buffers);
        if (!_bakeScalars)
        {
            combined.Add(new GuestMemoryBuffer(
                0,
                PackRuntimeScalarState(
                    evaluation.InitialScalarRegisters,
                    evaluation.GlobalMemoryBindings),
                GetRuntimeScalarBufferLength(evaluation.GlobalMemoryBindings.Count),
                Pooled: true));
        }

        if (usesGds)
        {
            combined.Add(new GuestMemoryBuffer(
                SyntheticGdsBaseAddress,
                _persistentGds,
                Gen5SpirvTranslator.GdsByteSize,
                Pooled: false,
                Writable: true,
                WriteBackToGuest: false));
        }

        return combined;
    }

    private static IReadOnlyList<GuestVertexBuffer> CreateGuestVertexBuffers(
        IReadOnlyList<Gen5VertexInputBinding> bindings)
    {
        var buffers = new GuestVertexBuffer[bindings.Count];
        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = bindings[index];
            buffers[index] = new GuestVertexBuffer(
                binding.Location,
                binding.ComponentCount,
                binding.DataFormat,
                binding.NumberFormat,
                binding.BaseAddress,
                binding.Stride,
                binding.OffsetBytes,
                binding.Data,
                binding.DataLength,
                binding.DataPooled,
                binding.PerInstance);
        }

        return buffers;
    }

    private static IReadOnlyList<GuestVertexBuffer>
        CreateVertexBufferOwnershipView(
            IReadOnlyList<GuestVertexBuffer> buffers,
            bool ownsPooledData)
    {
        var view = new GuestVertexBuffer[buffers.Count];
        for (var index = 0; index < buffers.Count; index++)
        {
            var buffer = buffers[index];
            view[index] = buffer with
            {
                Pooled = ownsPooledData && buffer.Pooled,
            };
        }

        return view;
    }

    private static GuestIndexBuffer? CreateIndexBufferOwnershipView(
        GuestIndexBuffer? buffer,
        bool ownsPooledData) =>
        buffer is null
            ? null
            : buffer with { Pooled = ownsPooledData && buffer.Pooled };

    // BCn block-compressed guest formats and the bytes per 4x4 block.
    private static int GetBlockCompressedBlockBytes(uint format) => format switch
    {
        169 or 170 or 175 or 176 => 8,
        171 or 172 or 173 or 174 or 177 or 178 or 179 or 180 or 181 or 182 => 16,
        _ => 0,
    };

    /// <summary>
    /// Deswizzles a tiled texture source into linear layout when tiling is
    /// enabled and the format is understood; returns null to keep the raw
    /// bytes (linear surfaces, unknown modes, or non-power-of-two elements).
    /// </summary>
    // The GPU detile kernel implements these two equation families at 4/8/16 bpp
    // (one/two/four 32-bit words per element; 1/2 bpp are sub-word and stay on the
    // CPU). Keep in lockstep with VulkanDetilePass.Supports / MetalDetilePass.Supports.
    private static bool IsGpuDetileEquation(DetileEquation equation) =>
        equation == DetileEquation.ExactXor || equation == DetileEquation.BlockTable;

    private static bool IsGpuDetileBytesPerElement(int bytesPerElement) =>
        bytesPerElement is 4 or 8 or 16;

    private static bool IsGpuDetileTextureType(uint type) =>
        type != Gen5TextureType3D;

    private static bool TryGetTextureElementLayout(
        TextureDescriptor descriptor,
        uint sourceWidth,
        out int elementsWide,
        out int elementsHigh,
        out int bytesPerElement)
    {
        var blockBytes = GetBlockCompressedBlockBytes(descriptor.Format);
        if (blockBytes != 0)
        {
            bytesPerElement = blockBytes;
            elementsWide = (int)((sourceWidth + 3) / 4);
            elementsHigh = (int)((descriptor.Height + 3) / 4);
        }
        else
        {
            bytesPerElement = (int)GetTextureBytesPerTexel(descriptor.Format);
            if (bytesPerElement == 0)
            {
                elementsWide = 0;
                elementsHigh = 0;
                return false;
            }

            elementsWide = (int)sourceWidth;
            elementsHigh = (int)descriptor.Height;
        }

        return true;
    }

    private static byte[]? TryDetileTextureSource(
        TextureDescriptor descriptor,
        uint sourceWidth,
        int logicalByteCount,
        byte[] source,
        int sourceX = 0,
        int sourceY = 0)
    {
        if (!GnmTiling.NeedsDetile(descriptor.TileMode) ||
            !TryGetTextureElementLayout(
                descriptor,
                sourceWidth,
                out var elementsWide,
                out var elementsHigh,
                out var bytesPerElement))
        {
            return null;
        }

        var volumeDepth = checked((int)GetTextureVolumeDepth(
            descriptor.Type,
            descriptor.Depth));
        if (logicalByteCount % volumeDepth != 0 ||
            source.Length % volumeDepth != 0)
        {
            return null;
        }

        var logicalSliceByteCount = logicalByteCount / volumeDepth;
        var physicalSliceByteCount = source.Length / volumeDepth;
        var linear = new byte[logicalByteCount];
        for (var slice = 0; slice < volumeDepth; slice++)
        {
            if (!GnmTiling.TryDetile(
                    source.AsSpan(slice * physicalSliceByteCount, physicalSliceByteCount),
                    linear.AsSpan(slice * logicalSliceByteCount, logicalSliceByteCount),
                    descriptor.TileMode,
                    elementsWide,
                    elementsHigh,
                    bytesPerElement,
                    sourceX,
                    sourceY))
            {
                return null;
            }
        }

        return linear;
    }

    private static void TraceTextureFallback(TextureDescriptor descriptor, string reason)
    {
        var mode = _traceGuestImagesMode;
        if ((!string.Equals(mode, "1", StringComparison.Ordinal) &&
             !string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase)) ||
            Interlocked.Increment(ref _textureFallbackTraceCount) > 64)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.texture_fallback reason={reason} " +
            $"addr=0x{descriptor.Address:X16} type={descriptor.Type} " +
            $"size={descriptor.Width}x{descriptor.Height} pitch={descriptor.Pitch} " +
            $"fmt={descriptor.Format} num={descriptor.NumberType} " +
            $"tile={descriptor.TileMode} mip={descriptor.MipLevels} " +
            $"dst=0x{descriptor.DstSelect:X3}");
    }

    private static bool TryCreateGuestDrawTexture(
        CpuContext ctx,
        TextureDescriptor descriptor,
        bool isStorage,
        bool writesImage,
        uint mipLevel,
        IReadOnlyList<uint> samplerDescriptor,
        bool isArrayed,
        out GuestDrawTexture texture)
    {
        texture = default!;
        var textureDepth = GetTextureVolumeDepth(
            descriptor.Type,
            descriptor.Depth);
        if ((descriptor.Type != Gen5TextureType1D &&
             descriptor.Type != Gen5TextureType2D &&
             descriptor.Type != Gen5TextureType3D &&
             descriptor.Type != Gen5TextureTypeCube &&
             descriptor.Type != Gen5TextureType1DArray &&
             descriptor.Type != Gen5TextureType2DArray) ||
            descriptor.Width == 0 ||
            descriptor.Height == 0 ||
            descriptor.Width > 8192 ||
            descriptor.Height > 8192)
        {
            TraceTextureFallback(descriptor, "invalid-descriptor");
            texture = CreateFallbackGuestDrawTexture(
                isStorage,
                writesImage,
                descriptor.Format,
                descriptor.NumberType,
                isArrayed,
                descriptor.Type,
                textureDepth);
            return true;
        }

        if (_gpuDetileLog)
        {
            lock (_seenTextureTileModes)
            {
                if (_seenTextureTileModes.Add(descriptor.TileMode))
                {
                    Console.Error.WriteLine(
                        $"[GPU-DETILE] texture tile_mode={descriptor.TileMode} fmt={descriptor.Format} " +
                        $"{descriptor.Width}x{descriptor.Height} " +
                        $"(0=linear; GPU covers exact-XOR 5/9/24/27 @ 4bpp).");
                }
            }
        }

        var sourceWidth = descriptor.TileMode == 0
            ? GetLinearTexturePitch(
                Math.Max(descriptor.Width, descriptor.Pitch),
                descriptor.Height,
                descriptor.Format)
            : descriptor.Width;
        var sourceSliceByteCount = GetTextureByteCount(
            descriptor.Format,
            sourceWidth,
            descriptor.Height);
        var sourceByteCount = GetTextureByteCount(
            descriptor.Format,
            sourceWidth,
            descriptor.Height,
            textureDepth);
        if (sourceByteCount == 0 ||
            sourceByteCount > MaxPresentedTextureBytes ||
            sourceByteCount > int.MaxValue)
        {
            TraceTextureFallback(
                descriptor,
                $"invalid-byte-count:{sourceByteCount}");
            texture = CreateFallbackGuestDrawTexture(
                isStorage,
                writesImage,
                descriptor.Format,
                descriptor.NumberType,
                isArrayed,
                descriptor.Type,
                textureDepth);
            return true;
        }

        var physicalSourceByteCount = sourceSliceByteCount;
        var guestAllocationByteCount = sourceSliceByteCount;
        ulong sourceOffset = 0;
        var resourceMipLevels = descriptor.HasExtendedDescriptor
            ? descriptor.ResourceMipLevels
            : 1u;
        var chainSliceBytes = sourceSliceByteCount;
        var sourceX = 0;
        var sourceY = 0;
        var elementsWide = 0;
        var elementsHigh = 0;
        var bytesPerElement = 0;
        var hasElementLayout = TryGetTextureElementLayout(
            descriptor,
            sourceWidth,
            out elementsWide,
            out elementsHigh,
            out bytesPerElement);
        if (GnmTiling.NeedsDetile(descriptor.TileMode) &&
            hasElementLayout &&
            GnmTiling.TryGetTiledByteCount(
                descriptor.TileMode,
                elementsWide,
                elementsHigh,
                bytesPerElement,
                out var tiledByteCount))
        {
            physicalSourceByteCount = tiledByteCount;
            guestAllocationByteCount = tiledByteCount;
            chainSliceBytes = tiledByteCount;
        }

        // Thin 64 KiB standard mip chains reserve their first block for the
        // mip tail and store larger levels in reverse order. Standalone CPU
        // textures currently upload resource mip 0, so resolve its real byte
        // range instead of interpreting the tail and smaller levels as mip 0.
        GnmTiling.TiledMipLayout tiledMipLayout = default;
        var hasStandard64KMipLayout = GnmTiling.NeedsDetile(descriptor.TileMode) &&
            hasElementLayout &&
            GnmTiling.TryGetStandard64KMipLayout(
                descriptor.TileMode,
                elementsWide,
                elementsHigh,
                bytesPerElement,
                resourceMipLevels,
                mipLevel: 0,
                out tiledMipLayout);
        if (hasStandard64KMipLayout)
        {
            sourceOffset = tiledMipLayout.SourceOffset;
            physicalSourceByteCount = tiledMipLayout.SourceByteCount;
            guestAllocationByteCount = tiledMipLayout.AllocationByteCount;
            chainSliceBytes = tiledMipLayout.AllocationByteCount;
            sourceX = tiledMipLayout.SourceX;
            sourceY = tiledMipLayout.SourceY;
        }
        else if (hasElementLayout &&
                 resourceMipLevels > 1 &&
                 GnmTiling.TryGetBaseMipPlacement(
                     descriptor.TileMode,
                     elementsWide,
                     elementsHigh,
                     bytesPerElement,
                     resourceMipLevels,
                     out var baseMipByteOffset,
                     out var baseMipInTail,
                     out var mipTailElementX,
                     out var mipTailElementY,
                     out var placedChainSliceBytes))
        {
            sourceOffset = baseMipByteOffset;
            guestAllocationByteCount = placedChainSliceBytes;
            chainSliceBytes = placedChainSliceBytes;
            if (baseMipInTail)
            {
                sourceX = mipTailElementX;
                sourceY = mipTailElementY;
            }
        }

        physicalSourceByteCount = checked(physicalSourceByteCount * textureDepth);
        guestAllocationByteCount = checked(guestAllocationByteCount * textureDepth);
        if (physicalSourceByteCount > MaxPresentedTextureBytes ||
            physicalSourceByteCount > int.MaxValue ||
            guestAllocationByteCount > MaxPresentedTextureBytes)
        {
            texture = CreateFallbackGuestDrawTexture(
                isStorage,
                writesImage,
                descriptor.Format,
                descriptor.NumberType,
                isArrayed,
                descriptor.Type,
                textureDepth);
            return true;
        }

        ulong physicalSourceAddress;
        try
        {
            physicalSourceAddress = checked(descriptor.Address + sourceOffset);
        }
        catch (OverflowException)
        {
            TraceTextureFallback(descriptor, $"source-address-overflow:{sourceOffset}");
            texture = CreateFallbackGuestDrawTexture(
                isStorage,
                writesImage,
                descriptor.Format,
                descriptor.NumberType,
                isArrayed,
                descriptor.Type,
                textureDepth);
            return true;
        }

        physicalSourceByteCount = checked(physicalSourceByteCount * textureDepth);
        if (physicalSourceByteCount > MaxPresentedTextureBytes ||
            physicalSourceByteCount > int.MaxValue)
        {
            texture = CreateFallbackGuestDrawTexture(
                isStorage,
                writesImage,
                descriptor.Format,
                descriptor.NumberType,
                isArrayed,
                descriptor.Type,
                textureDepth);
            return true;
        }

        var wantsArrayUpload = isArrayed &&
            !isStorage &&
            descriptor.Address != 0 &&
            (descriptor.Type == Gen5TextureType2DArray ||
             descriptor.Type == Gen5TextureType1DArray) &&
            descriptor.Depth > 1 &&
            !_arrayUploadUnsupported.ContainsKey(descriptor.Address);
        var arrayUploadLayers = wantsArrayUpload ? descriptor.Depth : 1u;

        // Upload-known (not plain availability): the presenter's answer goes
        // generation-stale when the guest CPU rewrites a CPU-backed image
        // (video planes, streamed font atlases), which routes this draw back
        // through the texel copy below so the refresh path re-uploads.
        var guestImageAvailable = !isStorage &&
            !wantsArrayUpload &&
            descriptor.Address != 0 &&
            GuestGpu.Current.IsGuestImageUploadKnown(
                descriptor.Address,
                descriptor.Format,
                descriptor.NumberType);
        if (TryUseAvailableGuestImageWithoutSnapshot(
                descriptor.Address,
                guestImageAvailable,
                out var dirtyGuestImageSnapshotClaimed))
        {
            NoteSampledAddress(descriptor.Address, descriptor.Format, descriptor.NumberType);
            texture = new GuestDrawTexture(
                descriptor.Address,
                descriptor.Width,
                descriptor.Height,
                descriptor.Format,
                descriptor.NumberType,
                [],
                IsFallback: false,
                IsStorage: false,
                MipLevels: descriptor.MipLevels,
                MipLevel: mipLevel,
                BaseMipLevel: descriptor.ViewBaseLevel,
                ResourceMipLevels: descriptor.ResourceMipLevels,
                Pitch: sourceWidth,
                TileMode: descriptor.TileMode,
                DstSelect: descriptor.DstSelect,
                Sampler: ToGuestSampler(samplerDescriptor),
                WritesImage: writesImage,
                SourceOffset: sourceOffset,
                PhysicalSourceByteCount: physicalSourceByteCount,
                GuestAllocationByteCount: guestAllocationByteCount,
                SourceX: sourceX,
                SourceY: sourceY,
                ElementsWide: elementsWide,
                ElementsHigh: elementsHigh,
                BytesPerElement: bytesPerElement,
                ArrayedView: isArrayed,
                ArrayLayers: arrayUploadLayers,
                Type: descriptor.Type,
                Depth: textureDepth);
            return true;
        }

        var dirtyGuestImageSnapshotSucceeded = false;
        try
        {
            if (isStorage)
            {
                var initialPixels = Array.Empty<byte>();
                var uploadKnown = descriptor.Address != 0 &&
                    GuestGpu.Current.IsGuestImageUploadKnown(
                        descriptor.Address,
                        descriptor.Format,
                        descriptor.NumberType);
                var readSucceeded = false;
                var linearNonzero = false;
                if (descriptor.Address != 0 && !uploadKnown)
                {
                    // Storage images can be pre-populated in tiled guest memory
                    // just like sampled images. Reading only the logical linear
                    // byte count both truncates 64 KiB swizzle blocks and uploads
                    // tiled bytes as scanlines. Read the full physical footprint
                    // and run the same AddrLib-derived detile path used below for
                    // sampled textures before seeding the Vulkan image.
                    var storageSource = new byte[(int)physicalSourceByteCount];
                    if (ctx.Memory.TryRead(physicalSourceAddress, storageSource))
                    {
                        readSucceeded = true;
                        var linearStorage = TryDetileTextureSource(
                            descriptor,
                            sourceWidth,
                            checked((int)sourceByteCount),
                            storageSource,
                            sourceX,
                            sourceY) ?? storageSource
                                .AsSpan(0, checked((int)sourceByteCount))
                                .ToArray();
                        if (linearStorage.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
                        {
                            linearNonzero = true;
                            initialPixels = linearStorage;
                        }
                    }
                }

                if (_traceStorageImageInitAddress == descriptor.Address)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] agc.storage_initial_data " +
                        $"addr=0x{descriptor.Address:X16} op_storage={isStorage} " +
                        $"upload_known={uploadKnown} read={readSucceeded} " +
                        $"nonzero={linearNonzero} initial_bytes={initialPixels.Length} " +
                        $"logical_bytes={sourceByteCount} physical_bytes={physicalSourceByteCount} " +
                        $"source_offset={sourceOffset} allocation_bytes={guestAllocationByteCount} " +
                        $"size={descriptor.Width}x{descriptor.Height} pitch={sourceWidth} " +
                        $"fmt={descriptor.Format} num={descriptor.NumberType} " +
                        $"tile={descriptor.TileMode} mip={mipLevel}");
                }

                texture = new GuestDrawTexture(
                    descriptor.Address,
                    descriptor.Width,
                    descriptor.Height,
                    descriptor.Format,
                    descriptor.NumberType,
                    initialPixels,
                    IsFallback: descriptor.Address == 0,
                    IsStorage: true,
                    MipLevels: descriptor.MipLevels,
                    MipLevel: mipLevel,
                    BaseMipLevel: descriptor.ViewBaseLevel,
                    ResourceMipLevels: descriptor.ResourceMipLevels,
                    Pitch: sourceWidth,
                    TileMode: descriptor.TileMode,
                    DstSelect: descriptor.DstSelect,
                    Sampler: ToGuestSampler(samplerDescriptor),
                    WritesImage: writesImage,
                    SourceOffset: sourceOffset,
                    PhysicalSourceByteCount: physicalSourceByteCount,
                    GuestAllocationByteCount: guestAllocationByteCount,
                    SourceX: sourceX,
                    SourceY: sourceY,
                    ElementsWide: elementsWide,
                    ElementsHigh: elementsHigh,
                    BytesPerElement: bytesPerElement,
                    ArrayedView: isArrayed,
                    ArrayLayers: 1,
                    Type: descriptor.Type,
                    Depth: textureDepth);
                return true;
            }

            // When the presenter already holds this exact texture identity in
            // its cache, the texel copy below would be discarded on arrival; for
            // scenes that sample large textures every draw this copy dominated
            // CPU time. The dirty peek closes the race with eviction: a texture
            // the guest rewrote must ship fresh texels with this draw, because
            // the render thread evicts the stale cache entry before executing it
            // (skipping would leave the draw with no pixels and a fallback
            // texture for the frame — visible flicker on animated textures).
            var sampler = ToGuestSampler(samplerDescriptor);
            var trackedByteCount = guestAllocationByteCount;
            if (wantsArrayUpload)
            {
                try
                {
                    trackedByteCount = checked(chainSliceBytes * arrayUploadLayers);
                }
                catch (OverflowException)
                {
                    trackedByteCount = guestAllocationByteCount;
                }
            }

            if (descriptor.Address != 0)
            {
                GuestImageWriteTracker.Track(
                    descriptor.Address,
                    trackedByteCount,
                    source: "agc.decoded-texture");
            }

            var hasWriteGeneration = GuestImageWriteTracker.TryGetWriteGeneration(
                descriptor.Address,
                out var writeGeneration);
            if (!dirtyGuestImageSnapshotClaimed &&
                !_textureCopySkipDisabled &&
                descriptor.Address != 0 &&
                !SharpEmu.HLE.GuestImageWriteTracker.PeekDirty(descriptor.Address) &&
                GuestGpu.Current.IsTextureContentCached(
                    new TextureContentIdentity(
                        descriptor.Address,
                        descriptor.Width,
                        descriptor.Height,
                        descriptor.Format,
                        descriptor.NumberType,
                        descriptor.DstSelect,
                        descriptor.TileMode,
                        sourceWidth,
                        sourceOffset,
                        sampler,
                        isArrayed,
                        arrayUploadLayers,
                        Type: descriptor.Type,
                        Depth: textureDepth)))
            {
                texture = new GuestDrawTexture(
                    descriptor.Address,
                    descriptor.Width,
                    descriptor.Height,
                    descriptor.Format,
                    descriptor.NumberType,
                    [],
                    IsFallback: false,
                    IsStorage: false,
                    MipLevels: descriptor.MipLevels,
                    MipLevel: mipLevel,
                    BaseMipLevel: descriptor.ViewBaseLevel,
                    ResourceMipLevels: descriptor.ResourceMipLevels,
                    Pitch: sourceWidth,
                    TileMode: descriptor.TileMode,
                    DstSelect: descriptor.DstSelect,
                    Sampler: sampler,
                    WritesImage: writesImage,
                    SourceOffset: sourceOffset,
                    PhysicalSourceByteCount: physicalSourceByteCount,
                    GuestAllocationByteCount: guestAllocationByteCount,
                    SourceX: sourceX,
                    SourceY: sourceY,
                    ElementsWide: elementsWide,
                    ElementsHigh: elementsHigh,
                    BytesPerElement: bytesPerElement,
                    ArrayedView: isArrayed,
                    ArrayLayers: arrayUploadLayers,
                    Type: descriptor.Type,
                    Depth: textureDepth);
                return true;
            }

            if (wantsArrayUpload)
            {
                var arrayLayers = arrayUploadLayers;
                var layerBytes = checked((int)sourceByteCount);
                var totalBytes = (long)layerBytes * arrayLayers;
                if (totalBytes <= int.MaxValue)
                {
                    var layered = new byte[totalBytes];
                    var uploadedLayers = 0u;
                    for (var layer = 0u; layer < arrayLayers; layer++)
                    {
                        var sliceSource = new byte[(int)physicalSourceByteCount];
                        ulong sliceAddress;
                        try
                        {
                            sliceAddress = checked(
                                descriptor.Address + layer * chainSliceBytes + sourceOffset);
                        }
                        catch (OverflowException)
                        {
                            break;
                        }

                        if (!ctx.Memory.TryRead(sliceAddress, sliceSource))
                        {
                            break;
                        }

                        var sliceLinear = TryDetileTextureSource(
                            descriptor,
                            sourceWidth,
                            layerBytes,
                            sliceSource,
                            sourceX,
                            sourceY) ?? sliceSource.AsSpan(0, layerBytes).ToArray();
                        sliceLinear.AsSpan(0, layerBytes)
                            .CopyTo(layered.AsSpan(checked((int)(layer * layerBytes))));
                        uploadedLayers++;
                    }

                    if (uploadedLayers == arrayLayers)
                    {
                        texture = new GuestDrawTexture(
                            descriptor.Address,
                            descriptor.Width,
                            descriptor.Height,
                            descriptor.Format,
                            descriptor.NumberType,
                            layered,
                            IsFallback: false,
                            IsStorage: false,
                            MipLevels: descriptor.MipLevels,
                            MipLevel: mipLevel,
                            BaseMipLevel: descriptor.ViewBaseLevel,
                            ResourceMipLevels: descriptor.ResourceMipLevels,
                            Pitch: sourceWidth,
                            TileMode: descriptor.TileMode,
                            DstSelect: descriptor.DstSelect,
                            Sampler: sampler,
                            WritesImage: writesImage,
                            SourceOffset: sourceOffset,
                            PhysicalSourceByteCount: physicalSourceByteCount,
                            GuestAllocationByteCount: trackedByteCount,
                            SourceX: sourceX,
                            SourceY: sourceY,
                            ElementsWide: elementsWide,
                            ElementsHigh: elementsHigh,
                            BytesPerElement: bytesPerElement,
                            WriteGeneration: hasWriteGeneration ? writeGeneration : -1,
                            ArrayedView: true,
                            ArrayLayers: arrayLayers,
                            Type: descriptor.Type,
                            Depth: textureDepth);
                        dirtyGuestImageSnapshotSucceeded = true;
                        return true;
                    }
                }

                _arrayUploadUnsupported.TryAdd(descriptor.Address, 0);
                arrayUploadLayers = 1;
            }

            var source = new byte[(int)physicalSourceByteCount];
            if (!ctx.Memory.TryRead(physicalSourceAddress, source))
            {
                TraceTextureFallback(
                    descriptor,
                    $"guest-read-failed:{sourceByteCount}");
                texture = CreateFallbackGuestDrawTexture(
                    isStorage,
                    writesImage,
                    descriptor.Format,
                    descriptor.NumberType,
                    isArrayed,
                    descriptor.Type,
                    textureDepth);
                return true;
            }

            if (_traceAgcShader)
            {
                var nonZero = 0;
                for (var i = 0; i < source.Length; i++)
                {
                    if (source[i] != 0)
                    {
                        nonZero++;
                        if (nonZero >= 64)
                        {
                            break;
                        }
                    }
                }

                TraceAgcShader(
                    $"agc.texture_source addr=0x{descriptor.Address:X16} " +
                    $"fmt={descriptor.Format} num={descriptor.NumberType} tile={descriptor.TileMode} " +
                    $"size={descriptor.Width}x{descriptor.Height} pitch={descriptor.Pitch} " +
                    $"dst=0x{descriptor.DstSelect:X3} " +
                    $"bytes={source.Length} logical_bytes={sourceByteCount} nonzero64={nonZero} " +
                    $"source_offset={sourceOffset} allocation_bytes={guestAllocationByteCount}");
            }
            DumpTextureSourceIfRequested(descriptor, sourceWidth, source);

            var rgba = TryDetileTextureSource(
                descriptor,
                sourceWidth,
                checked((int)sourceByteCount),
                source,
                sourceX,
                sourceY) ?? source.AsSpan(0, checked((int)sourceByteCount)).ToArray();
            DumpLinearTextureIfRequested(descriptor, sourceWidth, rgba);
            texture = new GuestDrawTexture(
                descriptor.Address,
                descriptor.Width,
                descriptor.Height,
                descriptor.Format,
                descriptor.NumberType,
                rgba,
                IsFallback: false,
                IsStorage: isStorage,
                MipLevels: descriptor.MipLevels,
                MipLevel: mipLevel,
                BaseMipLevel: descriptor.ViewBaseLevel,
                ResourceMipLevels: descriptor.ResourceMipLevels,
                Pitch: sourceWidth,
                TileMode: descriptor.TileMode,
                DstSelect: descriptor.DstSelect,
                Sampler: ToGuestSampler(samplerDescriptor),
                WritesImage: writesImage,
                SourceOffset: sourceOffset,
                PhysicalSourceByteCount: physicalSourceByteCount,
                GuestAllocationByteCount: guestAllocationByteCount,
                SourceX: sourceX,
                SourceY: sourceY,
                ElementsWide: elementsWide,
                ElementsHigh: elementsHigh,
                BytesPerElement: bytesPerElement,
                WriteGeneration: hasWriteGeneration ? writeGeneration : -1,
                ArrayedView: isArrayed,
                ArrayLayers: arrayUploadLayers,
                Type: descriptor.Type,
                Depth: textureDepth);
            dirtyGuestImageSnapshotSucceeded = true;
            return true;
        }
        finally
        {
            if (dirtyGuestImageSnapshotClaimed)
            {
                CompleteGuestImageSnapshot(
                    descriptor.Address,
                    dirtyGuestImageSnapshotSucceeded);
            }
        }
    }

    /// <summary>
    /// On PS5 render targets alias guest memory, so pixels the game wrote with
    /// the CPU are visible before the first GPU draw (Chowdren pre-fills its
    /// fog/overlay layers that way). Seed newly created Vulkan guest images
    /// with the current guest memory contents to preserve that base layer.
    /// </summary>
    private static void ProvideRenderTargetInitialData(
        CpuContext ctx,
        RenderTargetDescriptor target)
    {
        if (!GuestGpu.Current.GuestImageWantsInitialData(target.Address))
        {
            return;
        }

        var byteCount = VulkanVideoPresenter.GetGuestImageByteCount(
            target.Format,
            target.Width,
            target.Height);
        if (byteCount == 0 || byteCount > MaxPresentedTextureBytes)
        {
            return;
        }

        var initialData = new byte[byteCount];
        var readOk = ctx.Memory.TryRead(target.Address, initialData);
        var nonZero = readOk && initialData.AsSpan().IndexOfAnyExcept((byte)0) >= 0;
        if (_traceDraws && _rtSeedTraced.Add(target.Address))
        {
            Console.Error.WriteLine(
                $"[RTSEED] addr=0x{target.Address:X} {target.Width}x{target.Height} " +
                $"read={readOk} nonZero={nonZero}");
        }

        if (nonZero)
        {
            GuestGpu.Current.ProvideGuestImageInitialData(target.Address, initialData);
        }
    }

    private static readonly HashSet<ulong> _rtSeedTraced = new();

    private static void TraceDrawCompact(
        ulong sequence,
        TranslatedGuestDraw draw,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestVertexBuffer> vertexBuffers)
    {
        var isTitleDraw = vertexBuffers.Any(IsTitleVertexDrawBuffer);
        if (!_traceDraws &&
            (!_traceTitleDraws ||
             !isTitleDraw ||
             !_tracedTitleShaderPairs.TryAdd(
                 (draw.ExportShaderAddress, draw.PixelShaderAddress),
                 0)))
        {
            return;
        }

        var target = draw.RenderTargets.FirstOrDefault();
        var blend = draw.RenderState.Blend;
        var viewport = draw.RenderState.Viewport is { } vp
            ? $"{vp.X:0.#},{vp.Y:0.#},{vp.Width:0.#}x{vp.Height:0.#}"
            : "none";
        var textureList = string.Join(
            '|',
            textures.Select(texture =>
                $"0x{texture.Address:X}:{texture.Width}x{texture.Height}" +
                $":f{texture.Format}/n{texture.NumberType}/d{texture.DstSelect:X3}" +
                (texture.IsFallback ? ":FALLBACK" : string.Empty)));
        var positions = string.Empty;
        var positionBuffer = vertexBuffers.FirstOrDefault(buffer => buffer.Location == 0);
        if (positionBuffer is { Length: >= 8 })
        {
            var stride = Math.Max(positionBuffer.Stride, 4u);
            var vertexTotal = (int)((positionBuffer.Length - positionBuffer.OffsetBytes) / stride);
            var sampled = new List<string>();
            foreach (var vertex in new[] { 0, 1, vertexTotal - 1 })
            {
                var baseOffset = (int)(positionBuffer.OffsetBytes + vertex * stride);
                if (vertex < 0 || baseOffset + 8 > positionBuffer.Length)
                {
                    continue;
                }

                sampled.Add(
                    $"{BitConverter.ToSingle(positionBuffer.Data, baseOffset):0.##}," +
                    $"{BitConverter.ToSingle(positionBuffer.Data, baseOffset + 4):0.##}");
            }

            positions = string.Join(';', sampled);
        }

        Console.Error.WriteLine(
            $"[{(_traceDraws ? "DRAW" : "TITLE-DRAW")}] seq={sequence} " +
            $"es=0x{draw.ExportShaderAddress:X} ps=0x{draw.PixelShaderAddress:X} " +
            $"target=0x{target.Address:X}:{target.Width}x{target.Height}:f{target.Format}/n{target.NumberType} " +
            $"prim=0x{draw.PrimitiveType:X} verts={draw.VertexCount} indexed={draw.IndexBuffer is not null} " +
            $"blend={(blend.Enable ? 1 : 0)}:{blend.ColorSrcFactor}/{blend.ColorDstFactor}/{blend.ColorFunc}" +
            $":a{blend.AlphaSrcFactor}/{blend.AlphaDstFactor}/{blend.AlphaFunc}/s{(blend.SeparateAlphaBlend ? 1 : 0)} " +
            $"mask=0x{blend.WriteMask:X} viewport={viewport} textures={textureList} pos={positions} " +
            $"ps_s0..3={string.Join(',', draw.PixelUserData.Take(4).Select(value => BitConverter.UInt32BitsToSingle(value).ToString("0.###")))} " +
            $"rawblend=0x{draw.RawBlendControl:X8} info=0x{draw.RawColorInfo:X8}");
    }

    private static bool IsTitleVertexDrawBuffer(GuestVertexBuffer buffer) =>
        buffer.Location == 0 &&
        buffer.ComponentCount == 4 &&
        buffer.DataFormat == 10 &&
        buffer.NumberFormat == 0 &&
        buffer.Stride == 16 &&
        buffer.OffsetBytes == 12 &&
        buffer.Length == 67568;

    private static void TraceDrawCompactMiss(ulong sequence, uint vertexCount, string error)
    {
        if (!_traceDraws)
        {
            return;
        }

        Console.Error.WriteLine($"[DRAW] seq={sequence} MISS verts={vertexCount} error={error}");
    }

    private static int _grassTraceCount;

    private static void TraceGrassDrawVertices(
        TranslatedGuestDraw draw,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestVertexBuffer> vertexBuffers)
    {
        if (_grassTraceCount >= 6 ||
            !textures.Any(texture => texture.Width == 288 && texture.Height == 160) ||
            vertexBuffers.Count == 0 ||
            Interlocked.Increment(ref _grassTraceCount) > 6)
        {
            return;
        }

        var text = new System.Text.StringBuilder();
        text.Append($"agc.grassdraw prim=0x{draw.PrimitiveType:X} verts={draw.VertexCount} ");
        text.Append($"indexed={draw.IndexBuffer is not null} buffers={vertexBuffers.Count}");
        foreach (var buffer in vertexBuffers)
        {
            text.Append(
                $"\n  loc={buffer.Location} fmt={buffer.DataFormat}/{buffer.NumberFormat}x{buffer.ComponentCount} " +
                $"stride={buffer.Stride} offset={buffer.OffsetBytes} bytes={buffer.Length}");
            var stride = Math.Max(buffer.Stride, 4u);
            var maxVerts = Math.Min(6, (int)((buffer.Length - buffer.OffsetBytes) / stride));
            for (var vertex = 0; vertex < maxVerts; vertex++)
            {
                var baseOffset = (int)(buffer.OffsetBytes + vertex * stride);
                var components = Math.Min(4, (int)((buffer.Length - baseOffset) / 4));
                text.Append($"\n    v{vertex}:");
                for (var c = 0; c < components; c++)
                {
                    text.Append($" {BitConverter.ToSingle(buffer.Data, baseOffset + c * 4):0.#####}");
                }
            }
        }

        TraceAgcShader(text.ToString());
    }

    private static int _rectListTraceCount;

    private static void TraceRectListVertices(
        TranslatedGuestDraw draw,
        IReadOnlyList<GuestVertexBuffer> vertexBuffers)
    {
        if (!AgcPrimitiveHelpers.IsRectListPrimitive(draw.PrimitiveType) ||
            _rectListTraceCount >= 16 ||
            Interlocked.Increment(ref _rectListTraceCount) > 16)
        {
            return;
        }

        var expanded = AgcPrimitiveHelpers.GetRectListDrawVertexCount(
            draw.PrimitiveType,
            draw.VertexCount,
            indexed: draw.IndexBuffer is not null,
            hasVertexBuffers: vertexBuffers.Count > 0);
        var text = new System.Text.StringBuilder();
        text.Append(
            $"agc.rectlist prim=0x{draw.PrimitiveType:X} verts={draw.VertexCount}->{expanded} " +
            $"indexed={(draw.IndexBuffer is not null ? 1 : 0)} vb={vertexBuffers.Count}");

        if (vertexBuffers.Count > 0)
        {
            var buffer = vertexBuffers[0];
            var stride = Math.Max(buffer.Stride, 4u);
            text.Append(
                $" stride={buffer.Stride} " +
                $"fmt={buffer.DataFormat}/{buffer.NumberFormat}x{buffer.ComponentCount}");
            for (var vertex = 0; vertex < 3; vertex++)
            {
                var baseOffset = (int)(buffer.OffsetBytes + vertex * stride);
                if (baseOffset + 16 > buffer.Length)
                {
                    break;
                }

                var x = BitConverter.ToSingle(buffer.Data, baseOffset);
                var y = BitConverter.ToSingle(buffer.Data, baseOffset + 4);
                var z = BitConverter.ToSingle(buffer.Data, baseOffset + 8);
                var w = BitConverter.ToSingle(buffer.Data, baseOffset + 12);
                text.Append($" v{vertex}=({x:0.###},{y:0.###},{z:0.###},{w:0.###})");
            }
        }
        else
        {
            text.Append(" procedural=1");
        }

        TraceAgcShader(text.ToString());
    }

    private static int _textureDumpCount;
    private static readonly ConcurrentDictionary<string, int> _textureDumpKeys = new();

    /// <summary>
    /// Writes raw sampled-texture bytes (as read from guest memory) when
    /// SHARPEMU_TEXTURE_DUMP_DIR is set, so upload-time content can be
    /// inspected offline. File name records size and effective pitch.
    /// </summary>
    private static void DumpTextureSourceIfRequested(
        in TextureDescriptor descriptor,
        uint sourcePitch,
        byte[] source)
    {
        var directory = _textureDumpDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var key = $"0x{descriptor.Address:X}-{descriptor.Width}x{descriptor.Height}";
        var occurrence = _textureDumpKeys.AddOrUpdate(key, 1, static (_, count) => count + 1);
        // First uses plus periodic later snapshots (the game reuses the same
        // allocation for successive full-screen images).
        if ((occurrence > 3 && occurrence % 500 >= 3) ||
            Interlocked.Increment(ref _textureDumpCount) > 200)
        {
            return;
        }

        var index = _textureDumpCount;

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"{index:D3}-0x{descriptor.Address:X}-{descriptor.Width}x{descriptor.Height}" +
                $"-p{sourcePitch}-f{descriptor.Format}-t{descriptor.TileMode}.bin");
            File.WriteAllBytes(path, source);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Writes the bytes after detiling when SHARPEMU_TEXTURE_LINEAR_DUMP_DIR is
    /// set. Keeping this separate from the raw-source dump makes AddrLib
    /// equation changes directly inspectable with ordinary image tools.
    /// </summary>
    private static void DumpLinearTextureIfRequested(
        in TextureDescriptor descriptor,
        uint sourcePitch,
        byte[] source)
    {
        var directory = _linearTextureDumpDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var key = $"linear-0x{descriptor.Address:X}-{descriptor.Width}x{descriptor.Height}";
        var occurrence = _textureDumpKeys.AddOrUpdate(key, 1, static (_, count) => count + 1);
        if ((occurrence > 3 && occurrence % 500 >= 3) ||
            Interlocked.Increment(ref _textureDumpCount) > 200)
        {
            return;
        }

        var index = _textureDumpCount;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"{index:D3}-0x{descriptor.Address:X}-{descriptor.Width}x{descriptor.Height}" +
                $"-p{sourcePitch}-f{descriptor.Format}-t{descriptor.TileMode}.linear.bin");
            File.WriteAllBytes(path, source);
        }
        catch (IOException)
        {
        }
    }

    internal static bool TryUseAvailableGuestImageWithoutSnapshot(
        ulong address,
        bool guestImageAvailable,
        out bool dirtySnapshotClaimed)
    {
        dirtySnapshotClaimed = guestImageAvailable &&
            GuestImageWriteTracker.ConsumeDirty(address);
        return guestImageAvailable && !dirtySnapshotClaimed;
    }

    internal static void CompleteGuestImageSnapshot(ulong address, bool succeeded)
    {
        GuestImageWriteTracker.Rearm(address);
        if (!succeeded)
        {
            // Put the consumed generation back for a later attempt. Rearm first
            // so this synthetic notification leaves the range dirty/disarmed,
            // matching a real CPU write and preventing a failed read from
            // making the stale host image look current.
            GuestImageWriteTracker.NotifyManagedWrite(address, 1);
        }
    }



    /// <summary>
    /// On PS5 render targets alias guest memory, so pixels the game wrote with
    /// the CPU are visible before the first GPU draw (Chowdren pre-fills its
    /// fog/overlay layers that way). Seed newly created Vulkan guest images
    /// with the current guest memory contents to preserve that base layer.
    /// </summary>
    private static GuestDrawTexture CreateFallbackGuestDrawTexture(
        bool isStorage,
        bool writesImage,
        uint format,
        uint numberType,
        bool isArrayed = false,
        uint type = Gen5TextureType2D,
        uint depth = 1)
    {
        var fallbackFormat = format == 0 ? 10u : format;
        var fallbackNumberType = numberType;
        return new(
            0,
            1,
            1,
            fallbackFormat,
            fallbackNumberType,
            [0, 0, 0, 255],
            IsFallback: true,
            IsStorage: isStorage,
            MipLevels: 1,
            MipLevel: 0,
            WritesImage: writesImage,
            ArrayedView: isArrayed,
            Type: type,
            Depth: GetTextureVolumeDepth(type, depth));
    }

    private static GuestSampler ToGuestSampler(IReadOnlyList<uint> descriptor) =>
        descriptor.Count >= 4
            ? new GuestSampler(
                descriptor[0],
                descriptor[1],
                descriptor[2],
                descriptor[3])
            : default;

    private static byte[] ConvertRgba16FloatToRgba8(ReadOnlySpan<byte> source, uint width, uint height)
    {
        var destination = new byte[checked((int)((ulong)width * height * 4))];
        var pixelCount = destination.Length / 4;
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var sourceOffset = pixel * 8;
            var destinationOffset = pixel * 4;
            destination[destinationOffset + 0] = HalfToByte(BinaryPrimitives.ReadUInt16LittleEndian(source[sourceOffset..]));
            destination[destinationOffset + 1] = HalfToByte(BinaryPrimitives.ReadUInt16LittleEndian(source[(sourceOffset + 2)..]));
            destination[destinationOffset + 2] = HalfToByte(BinaryPrimitives.ReadUInt16LittleEndian(source[(sourceOffset + 4)..]));
            destination[destinationOffset + 3] = HalfToByte(BinaryPrimitives.ReadUInt16LittleEndian(source[(sourceOffset + 6)..]));
        }

        return destination;
    }

    private static byte HalfToByte(ushort bits)
    {
        var value = (float)BitConverter.UInt16BitsToHalf(bits);
        if (!float.IsFinite(value))
        {
            return 0;
        }

        return (byte)Math.Clamp((int)MathF.Round(value * 255.0f), 0, 255);
    }

    private static bool TryReadComputeDispatch(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        uint opcode,
        out ComputeDispatch dispatch,
        out ulong indirectDimsRetryAddress)
    {
        dispatch = default;
        // Non-zero only when this is an INDIRECT dispatch whose dimensions read as
        // zero — meaning the producing GPU dispatch that computes them has not run
        // yet. The caller suspends on this address instead of dropping the work.
        indirectDimsRetryAddress = 0;
        ulong dimensionsAddress;
        uint initiator;
        string dispatchSource;
        if (opcode == ItDispatchDirect)
        {
            if (packetLength < 5 ||
                !TryReadUInt32(ctx, packetAddress + 16, out initiator))
            {
                return false;
            }

            dimensionsAddress = packetAddress + 4;
            dispatchSource = "direct";
        }
        else if (packetLength >= 4)
        {
            if (!TryReadUInt64(ctx, packetAddress + 4, out dimensionsAddress) ||
                !TryReadUInt32(ctx, packetAddress + 12, out initiator))
            {
                return false;
            }

            dispatchSource = "absolute-indirect";
        }
        else
        {
            if (packetLength < 3 ||
                state.IndirectArgsAddress == 0 ||
                !TryReadUInt32(ctx, packetAddress + 4, out var dataOffset) ||
                !TryReadUInt32(ctx, packetAddress + 8, out initiator))
            {
                return false;
            }

            dimensionsAddress = state.IndirectArgsAddress + dataOffset;
            dispatchSource = "base-indirect";
        }

        if ((initiator & 1) == 0 ||
            !TryReadUInt32(ctx, dimensionsAddress, out var dispatchEndX) ||
            !TryReadUInt32(ctx, dimensionsAddress + 4, out var dispatchEndY) ||
            !TryReadUInt32(ctx, dimensionsAddress + 8, out var dispatchEndZ))
        {
            return false;
        }

        _ = TryGetShaderAddress(
            state.ShRegisters,
            ComputePgmLo,
            ComputePgmHi,
            out var shaderAddress);

        var probeZeroIndirectDispatch =
            dispatchSource == "base-indirect" &&
            (dispatchEndX == 0 || dispatchEndY == 0 || dispatchEndZ == 0) &&
            _probeZeroIndirectDispatchShaderAddresses.Contains(shaderAddress);
        if (probeZeroIndirectDispatch)
        {
            Console.Error.WriteLine(
                $"[AGC][ZERO-INDIRECT-DISPATCH-PROBE] " +
                $"cs=0x{shaderAddress:X16} dims=0x{dimensionsAddress:X16} " +
                $"raw={dispatchEndX:X8}/{dispatchEndY:X8}/{dispatchEndZ:X8} " +
                $"forced=00000001/00000001/00000001");
            dispatchEndX = 1;
            dispatchEndY = 1;
            dispatchEndZ = 1;
        }
        else if (dispatchEndX == 0 || dispatchEndY == 0 || dispatchEndZ == 0)
        {
            // Indirect dispatches read their dimensions from a guest buffer a
            // prior GPU dispatch fills. Zero here means that producer has not run
            // yet — signal the caller to suspend on the dims buffer and retry,
            // rather than dropping the work (which black-screens GPU-driven games
            // like Astro Bot). Direct dispatches carry dims inline, so a zero is
            // genuinely malformed and still rejected.
            if (opcode == ItDispatchIndirect)
            {
                indirectDimsRetryAddress = dimensionsAddress;
            }

            return RejectComputeDispatch(
                shaderAddress,
                dimensionsAddress,
                initiator,
                dispatchSource,
                dispatchEndX,
                dispatchEndY,
                dispatchEndZ,
                "zero-dimension");
        }

        // When FORCE_START_AT_000 is clear, RDNA2 interprets the three packet
        // values as end coordinates, not group counts. Vulkan expresses the
        // same operation as vkCmdDispatchBase(base, end - base). Ignoring the
        // COMPUTE_START registers turned small high-base clears into apparent
        // multi-million/billion-group dispatches and forced an unsafe cap.
        const uint forceStartAtZero = 1u << 2;
        const uint partialThreadGroupEnabled = 1u << 1;
        const uint useThreadDimensions = 1u << 5;
        uint baseGroupX = 0;
        uint baseGroupY = 0;
        uint baseGroupZ = 0;
        if ((initiator & forceStartAtZero) == 0)
        {
            state.ShRegisters.TryGetValue(ComputeStartX, out baseGroupX);
            state.ShRegisters.TryGetValue(ComputeStartY, out baseGroupY);
            state.ShRegisters.TryGetValue(ComputeStartZ, out baseGroupZ);
        }

        var localSizeX = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadX);
        var localSizeY = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadY);
        var localSizeZ = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadZ);
        uint groupCountX;
        uint groupCountY;
        uint groupCountZ;
        var threadCountX = uint.MaxValue;
        var threadCountY = uint.MaxValue;
        var threadCountZ = uint.MaxValue;
        if ((initiator & useThreadDimensions) != 0)
        {
            // In thread-dimension mode the packet contains thread counts, not
            // group end coordinates. Vulkan still dispatches whole workgroups,
            // so round up and pass the exact exclusive thread bounds to the
            // translated shader. Its entry guard disables invocations in the
            // partially populated final group before any guest instruction.
            var startThreadX = (ulong)baseGroupX * localSizeX;
            var startThreadY = (ulong)baseGroupY * localSizeY;
            var startThreadZ = (ulong)baseGroupZ * localSizeZ;
            if ((ulong)dispatchEndX <= startThreadX ||
                (ulong)dispatchEndY <= startThreadY ||
                (ulong)dispatchEndZ <= startThreadZ)
            {
                return RejectComputeDispatch(
                    shaderAddress,
                    dimensionsAddress,
                    initiator,
                    dispatchSource,
                    dispatchEndX,
                    dispatchEndY,
                    dispatchEndZ,
                    $"thread-end-not-after-base(" +
                    $"{startThreadX}x{startThreadY}x{startThreadZ})");
            }

            groupCountX = CeilDivide((ulong)dispatchEndX - startThreadX, localSizeX);
            groupCountY = CeilDivide((ulong)dispatchEndY - startThreadY, localSizeY);
            groupCountZ = CeilDivide((ulong)dispatchEndZ - startThreadZ, localSizeZ);
            threadCountX = dispatchEndX;
            threadCountY = dispatchEndY;
            threadCountZ = dispatchEndZ;
        }
        else
        {
            if (dispatchEndX <= baseGroupX ||
                dispatchEndY <= baseGroupY ||
                dispatchEndZ <= baseGroupZ)
            {
                return RejectComputeDispatch(
                    shaderAddress,
                    dimensionsAddress,
                    initiator,
                    dispatchSource,
                    dispatchEndX,
                    dispatchEndY,
                    dispatchEndZ,
                    $"end-not-after-base({baseGroupX}x{baseGroupY}x{baseGroupZ})");
            }

            groupCountX = dispatchEndX - baseGroupX;
            groupCountY = dispatchEndY - baseGroupY;
            groupCountZ = dispatchEndZ - baseGroupZ;
        }

        if ((initiator & partialThreadGroupEnabled) != 0)
        {
            var partialSizeX = GetComputePartialSize(state.ShRegisters, ComputeNumThreadX);
            var partialSizeY = GetComputePartialSize(state.ShRegisters, ComputeNumThreadY);
            var partialSizeZ = GetComputePartialSize(state.ShRegisters, ComputeNumThreadZ);
            if (partialSizeX == 0 || partialSizeX > localSizeX ||
                partialSizeY == 0 || partialSizeY > localSizeY ||
                partialSizeZ == 0 || partialSizeZ > localSizeZ)
            {
                return RejectComputeDispatch(
                    shaderAddress,
                    dimensionsAddress,
                    initiator,
                    dispatchSource,
                    dispatchEndX,
                    dispatchEndY,
                    dispatchEndZ,
                    $"invalid-partial-size({partialSizeX}x{partialSizeY}x{partialSizeZ}/" +
                    $"{localSizeX}x{localSizeY}x{localSizeZ})");
            }

            if (partialSizeX != localSizeX ||
                partialSizeY != localSizeY ||
                partialSizeZ != localSizeZ)
            {
                return RejectComputeDispatch(
                    shaderAddress,
                    dimensionsAddress,
                    initiator,
                    dispatchSource,
                    dispatchEndX,
                    dispatchEndY,
                    dispatchEndZ,
                    $"unrepresentable-partial-group({partialSizeX}x{partialSizeY}x{partialSizeZ}/" +
                    $"{localSizeX}x{localSizeY}x{localSizeZ})");
            }
        }

        var waveLaneCount = (initiator & (1u << 15)) != 0 ? 32u : 64u;

        if (_traceAgcShader &&
            ((ulong)groupCountX * groupCountY * groupCountZ >= 1_000_000UL ||
             groupCountX >= 1_000_000u))
        {
            lock (_submitTraceGate)
            {
                if (_tracedDispatchArguments.Add(
                        (dimensionsAddress, groupCountX, groupCountY, groupCountZ)))
                {
                    TraceAgcShader(
                        $"agc.dispatch_args source={dispatchSource} op=0x{opcode:X2} " +
                        $"queue={state.QueueName} submission={state.ActiveSubmissionId} " +
                        $"packet=0x{packetAddress:X16} len={packetLength} " +
                        $"dims=0x{dimensionsAddress:X16} " +
                        $"raw={dispatchEndX:X8}/{dispatchEndY:X8}/{dispatchEndZ:X8} " +
                        $"base={baseGroupX:X8}/{baseGroupY:X8}/{baseGroupZ:X8} " +
                        $"count={groupCountX:X8}/{groupCountY:X8}/{groupCountZ:X8} " +
                        $"wave={waveLaneCount} " +
                        $"initiator=0x{initiator:X8} " +
                        $"indirect_base=0x{state.IndirectArgsAddress:X16}");
                }
            }
        }

        dispatch = new ComputeDispatch(
            groupCountX,
            groupCountY,
            groupCountZ,
            baseGroupX,
            baseGroupY,
            baseGroupZ,
            waveLaneCount,
            IsIndirect: opcode == ItDispatchIndirect,
            threadCountX,
            threadCountY,
            threadCountZ);
        return true;
    }

    private static uint CeilDivide(ulong value, uint divisor) =>
        checked((uint)((value + divisor - 1) / divisor));

    private static bool RejectComputeDispatch(
        ulong shaderAddress,
        ulong dimensionsAddress,
        uint initiator,
        string source,
        uint rawX,
        uint rawY,
        uint rawZ,
        string reason)
    {
        lock (_submitTraceGate)
        {
            if (_rejectedDispatchArguments.Count < 256 &&
                _rejectedDispatchArguments.Add((dimensionsAddress, initiator, reason)))
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] agc.dispatch_reject source={source} " +
                    $"cs=0x{shaderAddress:X16} " +
                    $"dims=0x{dimensionsAddress:X16} raw={rawX:X8}/{rawY:X8}/{rawZ:X8} " +
                    $"initiator=0x{initiator:X8} reason={reason}");
            }
        }

        return false;
    }

    private static void ObserveComputeDispatch(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ComputeDispatch dispatch)
    {
        if (!TryGetShaderAddress(
                state.ShRegisters,
                ComputePgmLo,
                ComputePgmHi,
                out var shaderAddress))
        {
            return;
        }

        var sequence = ++gpuState.WorkSequence;
        ulong shaderHeader;
        lock (_submitTraceGate)
        {
            _shaderHeadersByCode.TryGetValue(shaderAddress, out shaderHeader);
        }

        DumpShaderProgramIfRequested(
            ctx,
            "cs",
            shaderAddress,
            shaderHeader,
            "requested-capture");

        var computeSystemRegisters = DecodeComputeSystemRegisters(state.ShRegisters);
        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                shaderAddress,
                shaderHeader,
                state.ShRegisters,
                ComputeUserDataRegister,
                out var shaderState,
                out var error,
                computeSystemRegisters) ||
            !Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                shaderState,
                out var evaluation,
                out error))
        {
            DumpShaderProgramIfRequested(
                ctx,
                "cs",
                shaderAddress,
                shaderHeader,
                error);
            lock (_submitTraceGate)
            {
                if (TryCreateComputeShaderCompatibilityDiagnostic(
                        _tracedComputeShaders,
                        shaderAddress,
                        error,
                        out var compatibilityDiagnostic))
                {
                    Console.Error.WriteLine(compatibilityDiagnostic);
                    TraceAgcShader(
                        $"agc.compute_shader cs=0x{shaderAddress:X16} error={error}");
                }
            }

            return;
        }

        TraceGlobalBufferProbe(
            "compute",
            shaderAddress,
            evaluation);
        TraceIndexedGlobalBufferProbe(ctx, shaderAddress, evaluation);

        var localSizeX = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadX);
        var localSizeY = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadY);
        var localSizeZ = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadZ);
        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            var bindingLength = checked((ulong)Math.Max(binding.DataLength, 1));
            var matchesAvPlayerBuffer = AvPlayerExports.ShouldTraceVideoBufferRange(
                binding.BaseAddress,
                bindingLength);
            var matchesExplicitAddress = _traceGuestImageAddresses.Any(address =>
                address >= binding.BaseAddress &&
                address - binding.BaseAddress < bindingLength);
            if (!matchesAvPlayerBuffer && !matchesExplicitAddress)
            {
                continue;
            }

            var traceBinding = false;
            lock (_submitTraceGate)
            {
                traceBinding =
                    _tracedAvPlayerComputeGlobalBindings.Count < AvPlayerComputeBindingTraceLimit &&
                    _tracedAvPlayerComputeGlobalBindings.Add(
                        (shaderAddress,
                         binding.BaseAddress,
                         binding.DataLength,
                         binding.Writable,
                         binding.ScalarAddress,
                         dispatch.GroupCountX,
                         dispatch.GroupCountY,
                         dispatch.GroupCountZ,
                         localSizeX,
                         localSizeY,
                         localSizeZ));
            }

            if (traceBinding)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] agc.avplayer_compute_global_binding " +
                    $"cs=0x{shaderAddress:X16} base=0x{binding.BaseAddress:X16} " +
                    $"bytes={binding.DataLength} writable={(binding.Writable ? 1 : 0)} " +
                    $"matched={(matchesAvPlayerBuffer ? "avplayer" : string.Empty)}" +
                    $"{(matchesAvPlayerBuffer && matchesExplicitAddress ? "+" : string.Empty)}" +
                    $"{(matchesExplicitAddress ? "explicit" : string.Empty)} " +
                    $"scalar=s{binding.ScalarAddress} " +
                    $"pcs=[{string.Join(',', binding.InstructionPcs.Select(static pc => $"0x{pc:X}"))}] " +
                    $"groups={dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ} " +
                    $"base_groups={dispatch.BaseGroupX}x{dispatch.BaseGroupY}x{dispatch.BaseGroupZ} " +
                    $"local={localSizeX}x{localSizeY}x{localSizeZ}");
            }
        }

        var bindings = evaluation.ImageBindings;
        var descriptions = new List<string>(bindings.Count);
        var translatedBindings = new List<TranslatedImageBinding>(bindings.Count);
        var hasStorageBinding = false;
        foreach (var binding in bindings)
        {
            var isStorage = Gen5ShaderTranslator.RequiresStorageImage(binding, bindings);
            var writesImage = Gen5ShaderTranslator.IsImageWriteOperation(binding.Opcode);
            var writesStorage = Gen5ShaderTranslator.IsStorageImageOperation(binding.Opcode);
            var descriptorValid = TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture);
            if (!descriptorValid)
            {
                texture = CreateFallbackTextureDescriptor(binding.ResourceDescriptor);
            }

            var traceBinding = false;
            var matchesAvPlayerBuffer =
                AvPlayerExports.ShouldTraceVideoBufferAddress(texture.Address);
            var matchesExplicitAddress =
                Array.IndexOf(_traceGuestImageAddresses, texture.Address) >= 0;
            var matchesTracedShader = _traceComputeShaderAddress == shaderAddress;
            if (texture.Address != 0 &&
                (matchesAvPlayerBuffer || matchesExplicitAddress || matchesTracedShader))
            {
                lock (_submitTraceGate)
                {
                    traceBinding =
                        _tracedAvPlayerComputeImageBindings.Count < AvPlayerComputeBindingTraceLimit &&
                        _tracedAvPlayerComputeImageBindings.Add(
                            (shaderAddress,
                             binding.Pc,
                             texture.Address,
                             texture.Width,
                             texture.Height,
                             texture.Format,
                             texture.NumberType,
                             texture.TileMode,
                             texture.Pitch,
                             texture.DstSelect,
                             isStorage,
                             dispatch.GroupCountX,
                             dispatch.GroupCountY,
                             dispatch.GroupCountZ,
                             localSizeX,
                             localSizeY,
                             localSizeZ));
                }
            }

            if (traceBinding)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] agc.avplayer_compute_image_binding " +
                    $"cs=0x{shaderAddress:X16} pc=0x{binding.Pc:X} op={binding.Opcode} " +
                    $"storage={(isStorage ? 1 : 0)} descriptor_valid={(descriptorValid ? 1 : 0)} " +
                    $"matched={(matchesAvPlayerBuffer ? "avplayer" : string.Empty)}" +
                    $"{(matchesAvPlayerBuffer && matchesExplicitAddress ? "+" : string.Empty)}" +
                    $"{(matchesExplicitAddress ? "explicit" : string.Empty)}" +
                    $"{((matchesAvPlayerBuffer || matchesExplicitAddress) && matchesTracedShader ? "+" : string.Empty)}" +
                    $"{(matchesTracedShader ? "shader" : string.Empty)} " +
                    $"decoded={FormatTextureDescriptor(texture)} " +
                    $"raw={FormatShaderDwords(binding.ResourceDescriptor)} " +
                    $"sampler={FormatShaderDwords(binding.SamplerDescriptor)} " +
                    $"groups={dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ} " +
                    $"base_groups={dispatch.BaseGroupX}x{dispatch.BaseGroupY}x{dispatch.BaseGroupZ} " +
                    $"local={localSizeX}x{localSizeY}x{localSizeZ}");
            }

            translatedBindings.Add(
                new TranslatedImageBinding(
                    texture,
                    isStorage,
                    writesImage,
                    binding.MipLevel ?? 0,
                    binding.SamplerDescriptor,
                    Gen5ShaderTranslator.IsArrayedImageBinding(binding)));
            hasStorageBinding |= isStorage;

            var descriptorState = descriptorValid ? string.Empty : "/invalid-desc";
            descriptions.Add(
                $"{binding.Opcode}@0x{binding.Pc:X}:" +
                $"0x{texture.Address:X16}:{texture.Width}x{texture.Height}:" +
                $"fmt{texture.Format}/num{texture.NumberType}/tile{texture.TileMode}" +
                $"{descriptorState}/{ProbeTexture(ctx, texture)}");
            if (writesStorage && descriptorValid && texture.Address != 0)
            {
                gpuState.ComputeImageWriters[texture.Address] = new ComputeImageWriter(
                    sequence,
                    shaderAddress,
                    binding.Opcode);

                TraceAgcShader(
                    $"agc.compute_writer addr=0x{texture.Address:X16} " +
                    $"fmt={texture.Format} num={texture.NumberType} tile={texture.TileMode} " +
                    $"size={texture.Width}x{texture.Height} " +
                    $"cs=0x{shaderAddress:X16} op={binding.Opcode}");
            }
        }

        if (_traceComputeShaderAddress == shaderAddress)
        {
            var globalHeads = evaluation.GlobalMemoryBindings.Count == 0
                ? string.Empty
                : $" global_heads=[{string.Join(',', evaluation.GlobalMemoryBindings.Select(
                    binding =>
                        $"0x{binding.BaseAddress:X16}:{binding.DataLength}:" +
                        Convert.ToHexString(binding.Data.AsSpan(
                            0,
                            Math.Min(binding.DataLength, 512)))))}]";
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.compute_dispatch_trace seq={sequence} " +
                $"cs=0x{shaderAddress:X16} " +
                $"groups={dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ} " +
                $"base={dispatch.BaseGroupX}x{dispatch.BaseGroupY}x{dispatch.BaseGroupZ} " +
                $"local={localSizeX}x{localSizeY}x{localSizeZ}" +
                globalHeads +
                $" bindings=[{string.Join(',', descriptions)}]");
        }

        var writesGlobalMemory = evaluation.GlobalMemoryBindings.Any(static binding =>
            binding.Writable);
        var usesGds = shaderState.Program.Instructions.Any(static instruction =>
            (instruction.Opcode is "DsConsume" or "DsAppend") &&
            instruction.Control is Gen5DataShareControl { Gds: true });
        var gpuDispatch = false;
        var evaluationHandledByCpu = false;
        var computeError = string.Empty;
        // Empty SRT/EUD with a recorded null-base scalar pointer fallback
        // produces Address-0 storage that can lose the Vulkan device on submit.
        var emptyResourceTables =
            shaderState.Metadata is
            {
                ShaderResourceTableSizeDwords: 0,
                ExtendedUserDataSizeDwords: 0,
            };
        if (emptyResourceTables &&
            (Gen5ShaderScalarEvaluator.WasEmptySrtScalarPointerFallback(shaderAddress) ||
             (translatedBindings.All(static binding => binding.Descriptor.Address == 0) &&
              !evaluation.GlobalMemoryBindings.Any(static binding => binding.BaseAddress != 0))))
        {
            computeError = Gen5ShaderScalarEvaluator.WasEmptySrtScalarPointerFallback(shaderAddress)
                ? "empty-srt-scalar-pointer-fallback"
                : "empty-srt-no-usable-resources";
            lock (_submitTraceGate)
            {
                if (_tracedComputeShaders.Add(shaderAddress))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] agc.compute_reject cs=0x{shaderAddress:X16} " +
                        $"source={(dispatch.IsIndirect ? "indirect" : "direct")} " +
                        $"groups={dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ} " +
                        $"reason={computeError}");
                }
            }
        }
        else if (!hasStorageBinding &&
            writesGlobalMemory &&
            TrySubmitMaskedDwordCopyKernel(
                ctx,
                shaderState.Program,
                evaluation,
                dispatch,
                localSizeX,
                localSizeY,
                localSizeZ,
                out var semanticCopySequence,
                out var copyDescription))
        {
            gpuDispatch = true;
            evaluationHandledByCpu = true;
            TraceAgcShader(
                $"agc.compute_semantic_fast_path cs=0x{shaderAddress:X16} " +
                $"queue={state.QueueName} submission={state.ActiveSubmissionId} " +
                copyDescription);
            // The scalar evaluator snapshots guest buffers while parsing the
            // command stream.  Do not let another submission (or the CPU)
            // observe that snapshot until the semantic replacement has
            // reached the same CPU-visible completion point as a translated
            // writable-buffer dispatch below.  Returning early here allowed
            // the guest to reuse a transient heap while its delayed clear was
            // still queued, so the clear could erase newly constructed CPU
            // objects.  Waiting on the work sequence also retires preceding
            // Vulkan writes before the next evaluator snapshot is captured.
            if (!GuestGpu.Current.WaitForGuestWork(semanticCopySequence))
            {
                computeError =
                    $"semantic-global-write-sync-timeout sequence={semanticCopySequence}";
            }
        }
        else if (!hasStorageBinding &&
            writesGlobalMemory &&
            TrySubmitConstantFillKernel(
                ctx,
                shaderState.Program,
                evaluation,
                dispatch,
                localSizeX,
                localSizeY,
                localSizeZ,
                out var semanticFillSequence,
                out var fillDescription))
        {
            gpuDispatch = true;
            evaluationHandledByCpu = true;
            TraceAgcShader(
                $"agc.compute_semantic_fast_path cs=0x{shaderAddress:X16} " +
                $"queue={state.QueueName} submission={state.ActiveSubmissionId} " +
                fillDescription);
            // Same CPU-visibility ordering requirement as the masked-copy
            // replacement above.
            if (!GuestGpu.Current.WaitForGuestWork(semanticFillSequence))
            {
                computeError =
                    $"semantic-global-write-sync-timeout sequence={semanticFillSequence}";
            }
        }
        else if ((hasStorageBinding || writesGlobalMemory || usesGds) &&
            (ulong)localSizeX * localSizeY * localSizeZ <= 1024)
        {
            var shaderKey = (
                shaderAddress,
                _bakeScalars
                    ? ComputeShaderStateFingerprint(evaluation)
                    : ComputeShaderStructuralFingerprint(evaluation),
                shaderState.ProgramResource1,
                localSizeX,
                localSizeY,
                localSizeZ,
                dispatch.WaveLaneCount,
                usesGds,
                _storageBufferOffsetAlignment);
            var guestGlobalBufferCount = evaluation.GlobalMemoryBindings.Count;
            var scalarBufferCount = _bakeScalars ? 0 : 1;
            var gdsBufferIndex = usesGds
                ? guestGlobalBufferCount + scalarBufferCount
                : -1;
            var totalGlobalBufferCount =
                guestGlobalBufferCount + scalarBufferCount + (usesGds ? 1 : 0);
            _computeShaderCache.TryGetValue(shaderKey, out var computeShader);

            if (computeShader is null &&
                GuestGpu.Current.TryCompileComputeShader(
                    shaderState,
                    evaluation,
                    localSizeX,
                    localSizeY,
                    localSizeZ,
                    out computeShader,
                    out computeError,
                    totalGlobalBufferCount,
                    initialScalarBufferIndex: _bakeScalars
                        ? -1
                        : guestGlobalBufferCount,
                    waveLaneCount: dispatch.WaveLaneCount,
                    storageBufferOffsetAlignment:
                        _storageBufferOffsetAlignment,
                    gdsBufferIndex: gdsBufferIndex))
            {
                DumpCompiledShader(
                    "cs",
                    shaderAddress,
                    shaderKey.Item2,
                    computeShader!,
                    shaderState.Program);
            }

            if (computeShader is not null)
            {
                _computeShaderCache.TryAdd(shaderKey, computeShader);

                var textures = CreateGuestDrawTextures(
                    ctx,
                    translatedBindings,
                    out _);
                var globalMemoryBuffers =
                    CreateTranslatedComputeGlobalBuffers(evaluation, usesGds);
                GuestGpu.Current.SubmitComputeDispatch(
                    shaderAddress,
                    computeShader,
                    textures,
                    globalMemoryBuffers,
                    dispatch.GroupCountX,
                    dispatch.GroupCountY,
                    dispatch.GroupCountZ,
                    dispatch.BaseGroupX,
                    dispatch.BaseGroupY,
                    dispatch.BaseGroupZ,
                    localSizeX,
                    localSizeY,
                    localSizeZ,
                    dispatch.IsIndirect,
                    writesGlobalMemory || usesGds,
                    dispatch.ThreadCountX,
                    dispatch.ThreadCountY,
                    dispatch.ThreadCountZ);
                // Vulkan queue order keeps dependent dispatches coherent. CPU visibility is
                // published by explicit PM4 release/write actions instead of per dispatch.
                gpuDispatch = true;
            }
        }

        const int blitCount = 0;

        lock (_submitTraceGate)
        {
            if (_tracedComputeShaders.Add(shaderAddress))
            {
                var globalBuffers = evaluation.GlobalMemoryBindings.Count == 0
                    ? string.Empty
                    : $" global_buffers=[{string.Join(',', evaluation.GlobalMemoryBindings.Select(
                        binding => $"0x{binding.BaseAddress:X16}:{binding.DataLength}"))}]";
                var scalarProbe = string.Join(
                    ',',
                    evaluation.InitialScalarRegisters
                        .Take(16)
                        .Select((value, index) => $"s{index}={value:X8}"));
                var globalProbes = evaluation.GlobalMemoryBindings.Count == 0
                    ? string.Empty
                    : $" global_heads=[{string.Join(',', evaluation.GlobalMemoryBindings.Select(
                        binding =>
                            $"0x{binding.BaseAddress:X16}:" +
                            Convert.ToHexString(binding.Data.AsSpan(
                                0,
                                Math.Min(binding.DataLength, 16)))))}]";
                var globalDescriptors = evaluation.GlobalMemoryBindings.Count == 0
                    ? string.Empty
                    : $" global_descriptors=[{string.Join(',', evaluation.GlobalMemoryBindings.Select(
                        binding =>
                            $"s{binding.ScalarAddress}=" +
                            string.Join(':', evaluation.ScalarRegisters
                                .Skip(checked((int)binding.ScalarAddress))
                                .Take(4)
                                .Select(value => $"{value:X8}"))))}]";
                var opcodes = string.Join(
                    ',',
                    shaderState.Program.Instructions
                        .Select(instruction => instruction.Opcode)
                        .Distinct()
                        .Take(48));
                var computeSummary =
                    $"agc.compute_shader cs=0x{shaderAddress:X16} " +
                    $"groups={dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ} " +
                    $"base={dispatch.BaseGroupX}x{dispatch.BaseGroupY}x{dispatch.BaseGroupZ} " +
                    $"wave={dispatch.WaveLaneCount} " +
                    $"local={localSizeX}x{localSizeY}x{localSizeZ} " +
                    $"sys={DescribeComputeSystemRegisters(computeSystemRegisters)} " +
                    $"gpu={gpuDispatch} blits={blitCount} globals={evaluation.GlobalMemoryBindings.Count} " +
                    $"global_writes={writesGlobalMemory}" +
                    (computeError.Length == 0 ? string.Empty : $" error={computeError}") +
                    $" sgprs=[{scalarProbe}]" +
                    globalBuffers +
                    globalProbes +
                    globalDescriptors +
                    $" opcodes=[{opcodes}]" +
                    $" bindings=[{string.Join(',', descriptions)}]";
                TraceAgcShader(computeSummary);
                if (_traceComputeShaderAddress == shaderAddress && !_traceAgcShader)
                {
                    Console.Error.WriteLine($"[AGC][COMPUTE-RESULT] {computeSummary}");
                }
            }
        }

        // Rejected/CPU-handled dispatches never hand evaluation's pooled buffers to a
        // consumer that would return them; reclaim here to keep GuestDataPool.Shared bounded.
        if (evaluationHandledByCpu || !gpuDispatch)
        {
            ReturnPooledEvaluationArrays(evaluation);
        }
    }

    /// <summary>
    /// Recognizes the SDK's masked-dword resource initialization kernel and
    /// executes its exact semantics over the guest-memory window that the
    /// emulator can map. The guest dispatches this kernel over multi-gigabyte
    /// virtual heaps (up to ~67 million 64-lane workgroups); translating every
    /// out-of-window invocation to Vulkan dominated startup despite those
    /// stores being bounds-discarded. This is a semantic kernel replacement,
    /// not a generic dispatch cap: the complete instruction shape and SGPR
    /// bindings must match before the ordered CPU action is used.
    /// </summary>
    private static bool TrySubmitMaskedDwordCopyKernel(
        CpuContext ctx,
        Gen5ShaderProgram program,
        Gen5ShaderEvaluation evaluation,
        ComputeDispatch dispatch,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        out long workSequence,
        out string description)
    {
        workSequence = 0;
        description = string.Empty;
        var instructions = program.Instructions;
        string[] expectedOpcodes =
        [
            "SMovB32",
            "STtraceData",
            "SInstPrefetch",
            "VLshlAddU32",
            "SBufferLoadDword",
            "SWaitcnt",
            "VCmpxGtU32",
            "SCbranchExecz",
            "SBufferLoadDword",
            "SWaitcnt",
            "VAndB32",
            "BufferLoadFormatX",
            "SWaitcnt",
            "BufferStoreFormatX",
            "SEndpgm",
        ];
        if (instructions.Count != expectedOpcodes.Length ||
            !instructions.Select(static instruction => instruction.Opcode)
                .SequenceEqual(expectedOpcodes) ||
            !IsExactMaskedDwordCopyInstructionShape(instructions) ||
            dispatch.BaseGroupX != 0 ||
            dispatch.BaseGroupY != 0 ||
            dispatch.BaseGroupZ != 0 ||
            dispatch.GroupCountY != 1 ||
            dispatch.GroupCountZ != 1 ||
            localSizeX != 64 ||
            localSizeY != 1 ||
            localSizeZ != 1 ||
            evaluation.ComputeSystemRegisters?.WorkGroupXRegister != 12)
        {
            return false;
        }

        var control = evaluation.GlobalMemoryBindings.SingleOrDefault(
            static binding => binding.ScalarAddress == 8 && !binding.Writable);
        var source = evaluation.GlobalMemoryBindings.SingleOrDefault(
            static binding => binding.ScalarAddress == 0 && !binding.Writable);
        var destination = evaluation.GlobalMemoryBindings.SingleOrDefault(
            static binding => binding.ScalarAddress == 4 &&
                              binding.Writable &&
                              binding.WriteBackToGuest);
        if (control is null || source is null || destination is null ||
            control.DataLength < 2 * sizeof(uint) ||
            source.DataLength < sizeof(uint) ||
            destination.BaseAddress == 0 ||
            destination.DataLength < sizeof(uint) ||
            !IsExactMaskedDwordCopyDescriptor(
                evaluation.InitialScalarRegisters,
                source.ScalarAddress,
                source.BaseAddress) ||
            !IsExactMaskedDwordCopyDescriptor(
                evaluation.InitialScalarRegisters,
                destination.ScalarAddress,
                destination.BaseAddress))
        {
            return false;
        }

        var elementCount = BinaryPrimitives.ReadUInt32LittleEndian(
            control.Data.AsSpan(0, sizeof(uint)));
        var sourceMask = BinaryPrimitives.ReadUInt32LittleEndian(
            control.Data.AsSpan(sizeof(uint), sizeof(uint)));
        var dispatchedThreads = dispatch.ThreadCountX != uint.MaxValue
            ? dispatch.ThreadCountX
            : Math.Min(
                (ulong)uint.MaxValue,
                (ulong)dispatch.GroupCountX * localSizeX);
        var writableDwords = (uint)(destination.DataLength / sizeof(uint));
        var outputDwords = (uint)Math.Min(
            Math.Min((ulong)elementCount, dispatchedThreads),
            writableDwords);
        if (outputDwords == 0)
        {
            return false;
        }

        var output = new byte[checked((int)outputDwords * sizeof(uint))];
        var outputWords = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
            output.AsSpan());
        if (sourceMask == 0)
        {
            outputWords.Fill(BinaryPrimitives.ReadUInt32LittleEndian(
                source.Data.AsSpan(0, sizeof(uint))));
        }
        else
        {
            var sourceWords = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                source.Data.AsSpan(0, source.DataLength - (source.DataLength % sizeof(uint))));
            for (uint index = 0; index < outputDwords; index++)
            {
                var sourceIndex = index & sourceMask;
                outputWords[(int)index] = sourceIndex < (uint)sourceWords.Length
                    ? sourceWords[(int)sourceIndex]
                    : 0;
            }
        }

        var destinationAddress = destination.BaseAddress;
        workSequence = GuestGpu.Current.SubmitOrderedGuestAction(
            () =>
            {
                if (!ctx.Memory.TryWrite(destinationAddress, output))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] AGC masked-copy fast path failed " +
                        $"dst=0x{destinationAddress:X16} bytes={output.Length}");
                    return;
                }

                GuestImageWriteTracker.Track(
                    destinationAddress,
                    (ulong)output.Length,
                    GuestGpu.Current.CurrentGuestWorkSequenceForDiagnostics,
                    "agc.masked-dword-copy");
            },
            $"masked_dword_copy dst=0x{destinationAddress:X16} bytes={output.Length}");
        description =
            $"dst=0x{destinationAddress:X16} bytes={output.Length} " +
            $"elements={elementCount} mask=0x{sourceMask:X8} " +
            $"dispatch={dispatch.GroupCountX}x{localSizeX}";
        return workSequence > 0;
    }

    private static bool IsExactMaskedDwordCopyInstructionShape(
        IReadOnlyList<Gen5ShaderInstruction> instructions)
    {
        static bool IsOperand(
            Gen5Operand operand,
            Gen5OperandKind kind,
            uint value) =>
            operand.Kind == kind && operand.Value == value;

        static bool IsBufferControl(
            Gen5ShaderInstruction instruction,
            uint vectorAddress,
            uint vectorData,
            uint scalarResource) =>
            instruction.Control is Gen5BufferMemoryControl
            {
                DwordCount: 1,
                OffsetBytes: 0,
                IndexEnabled: true,
                OffsetEnabled: false,
            } control &&
            control.VectorAddress == vectorAddress &&
            control.VectorData == vectorData &&
            control.ScalarResource == scalarResource;

        static bool IsScalarLoad(
            Gen5ShaderInstruction instruction,
            int offsetBytes) =>
            instruction.Control is Gen5ScalarMemoryControl
            {
                DestinationCount: 1,
                DynamicOffsetRegister: null,
            } control &&
            control.ImmediateOffsetBytes == offsetBytes &&
            instruction.Destinations.Count == 1 &&
            IsOperand(
                instruction.Destinations[0],
                Gen5OperandKind.ScalarRegister,
                106) &&
            instruction.Sources.Count >= 1 &&
            IsOperand(
                instruction.Sources[0],
                Gen5OperandKind.ScalarRegister,
                8);

        // This replacement depends on the operands as much as the opcode
        // sequence. Reversing V_CMPX_GT or enabling offen on either MUBUF
        // operation changes the set or address of written lanes.
        var globalId = instructions[3];
        var compare = instructions[6];
        var sourceIndex = instructions[10];
        var load = instructions[11];
        var store = instructions[13];
        return
            globalId.Destinations.Count == 1 &&
            IsOperand(globalId.Destinations[0], Gen5OperandKind.VectorRegister, 0) &&
            globalId.Sources.Count == 3 &&
            IsOperand(globalId.Sources[0], Gen5OperandKind.ScalarRegister, 12) &&
            IsOperand(globalId.Sources[1], Gen5OperandKind.EncodedConstant, 134) &&
            IsOperand(globalId.Sources[2], Gen5OperandKind.VectorRegister, 0) &&
            IsScalarLoad(instructions[4], offsetBytes: 0) &&
            compare.Sources.Count == 2 &&
            IsOperand(compare.Sources[0], Gen5OperandKind.ScalarRegister, 106) &&
            IsOperand(compare.Sources[1], Gen5OperandKind.VectorRegister, 0) &&
            instructions[7].Words.Count == 1 &&
            (instructions[7].Words[0] & 0xFFFFu) == 9 &&
            IsScalarLoad(instructions[8], offsetBytes: sizeof(uint)) &&
            sourceIndex.Destinations.Count == 1 &&
            IsOperand(sourceIndex.Destinations[0], Gen5OperandKind.VectorRegister, 1) &&
            sourceIndex.Sources.Count == 2 &&
            IsOperand(sourceIndex.Sources[0], Gen5OperandKind.ScalarRegister, 106) &&
            IsOperand(sourceIndex.Sources[1], Gen5OperandKind.VectorRegister, 0) &&
            IsBufferControl(load, vectorAddress: 1, vectorData: 1, scalarResource: 0) &&
            IsBufferControl(store, vectorAddress: 0, vectorData: 1, scalarResource: 4);
    }

    /// <summary>
    /// Semantic CPU replacement for Yotei's constant-fill kernel (v4 =
    /// wgid*64 + tid; BufferStoreFormatXyzw writes s4..s7 at record v4). The
    /// translated Vulkan form measured ~2.2s per dispatch against
    /// microseconds for the CPU fill. Same discipline as the masked-dword-copy
    /// replacement above: full instruction shape and descriptor must match.
    /// </summary>
    private static bool TrySubmitConstantFillKernel(
        CpuContext ctx,
        Gen5ShaderProgram program,
        Gen5ShaderEvaluation evaluation,
        ComputeDispatch dispatch,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        out long workSequence,
        out string description)
    {
        workSequence = 0;
        description = string.Empty;
        var instructions = program.Instructions;
        string[] expectedOpcodes =
        [
            "VLshlAddU32",
            "VMovB32",
            "VMovB32",
            "VMovB32",
            "VMovB32",
            "BufferStoreFormatXyzw",
            "SEndpgm",
        ];
        if (instructions.Count != expectedOpcodes.Length ||
            !instructions.Select(static instruction => instruction.Opcode)
                .SequenceEqual(expectedOpcodes) ||
            !IsExactConstantFillInstructionShape(instructions) ||
            dispatch.BaseGroupX != 0 ||
            dispatch.BaseGroupY != 0 ||
            dispatch.BaseGroupZ != 0 ||
            dispatch.GroupCountY != 1 ||
            dispatch.GroupCountZ != 1 ||
            localSizeX != 64 ||
            localSizeY != 1 ||
            localSizeZ != 1 ||
            evaluation.ComputeSystemRegisters?.WorkGroupXRegister != 8)
        {
            return false;
        }

        var destination = evaluation.GlobalMemoryBindings.SingleOrDefault(
            static binding => binding.ScalarAddress == 0 &&
                              binding.Writable &&
                              binding.WriteBackToGuest);
        var scalars = evaluation.InitialScalarRegisters;
        if (destination is null ||
            destination.BaseAddress == 0 ||
            destination.DataLength < FillRecordBytes ||
            scalars.Count < 8 ||
            !IsExactConstantFillDescriptor(scalars, destination.BaseAddress))
        {
            return false;
        }

        var numRecords = scalars[2];
        var dispatchedThreads = dispatch.ThreadCountX != uint.MaxValue
            ? dispatch.ThreadCountX
            : Math.Min(
                (ulong)uint.MaxValue,
                (ulong)dispatch.GroupCountX * localSizeX);
        var writableRecords = (uint)(destination.DataLength / FillRecordBytes);
        var outputRecords = (uint)Math.Min(
            Math.Min((ulong)numRecords, dispatchedThreads),
            writableRecords);
        if (outputRecords == 0)
        {
            return false;
        }

        var pattern = new byte[FillRecordBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(pattern.AsSpan(0), scalars[4]);
        BinaryPrimitives.WriteUInt32LittleEndian(pattern.AsSpan(4), scalars[5]);
        BinaryPrimitives.WriteUInt32LittleEndian(pattern.AsSpan(8), scalars[6]);
        BinaryPrimitives.WriteUInt32LittleEndian(pattern.AsSpan(12), scalars[7]);
        var output = new byte[checked((int)outputRecords * FillRecordBytes)];
        var outputWindow = output.AsSpan();
        for (var offset = 0; offset < outputWindow.Length; offset += FillRecordBytes)
        {
            pattern.CopyTo(outputWindow[offset..]);
        }

        var destinationAddress = destination.BaseAddress;
        workSequence = VulkanVideoPresenter.SubmitOrderedGuestAction(
            () =>
            {
                if (!ctx.Memory.TryWrite(destinationAddress, output))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] AGC constant-fill fast path failed " +
                        $"dst=0x{destinationAddress:X16} bytes={output.Length}");
                    return;
                }

                GuestImageWriteTracker.Track(
                    destinationAddress,
                    (ulong)output.Length,
                    VulkanVideoPresenter.CurrentGuestWorkSequenceForDiagnostics,
                    "agc.constant-fill");
            },
            $"constant_fill dst=0x{destinationAddress:X16} bytes={output.Length}");
        description =
            $"dst=0x{destinationAddress:X16} bytes={output.Length} " +
            $"records={outputRecords} pattern=0x{scalars[7]:X8}{scalars[6]:X8}{scalars[5]:X8}{scalars[4]:X8} " +
            $"dispatch={dispatch.GroupCountX}x{localSizeX}";
        return workSequence > 0;
    }

    private const int FillRecordBytes = 4 * sizeof(uint);

    private static bool IsExactConstantFillInstructionShape(
        IReadOnlyList<Gen5ShaderInstruction> instructions)
    {
        static bool IsOperand(
            Gen5Operand operand,
            Gen5OperandKind kind,
            uint value) =>
            operand.Kind == kind && operand.Value == value;

        var globalId = instructions[0];
        var store = instructions[5];
        if (globalId.Destinations.Count != 1 ||
            !IsOperand(globalId.Destinations[0], Gen5OperandKind.VectorRegister, 4) ||
            globalId.Sources.Count != 3 ||
            !IsOperand(globalId.Sources[0], Gen5OperandKind.ScalarRegister, 8) ||
            !IsOperand(globalId.Sources[1], Gen5OperandKind.EncodedConstant, 134) ||
            !IsOperand(globalId.Sources[2], Gen5OperandKind.VectorRegister, 0))
        {
            return false;
        }

        for (var index = 0; index < 4; index++)
        {
            var move = instructions[1 + index];
            if (move.Destinations.Count != 1 ||
                !IsOperand(
                    move.Destinations[0],
                    Gen5OperandKind.VectorRegister,
                    (uint)index) ||
                move.Sources.Count != 1 ||
                !IsOperand(
                    move.Sources[0],
                    Gen5OperandKind.ScalarRegister,
                    (uint)(4 + index)))
            {
                return false;
            }
        }

        return store.Control is Gen5BufferMemoryControl
        {
            DwordCount: 4,
            OffsetBytes: 0,
            IndexEnabled: true,
            OffsetEnabled: false,
            Glc: false,
            Slc: false,
        } control &&
            control.VectorAddress == 4 &&
            control.VectorData == 0 &&
            control.ScalarResource == 0;
    }

    private static bool IsExactConstantFillDescriptor(
        IReadOnlyList<uint> scalarRegisters,
        ulong expectedBaseAddress)
    {
        var word0 = scalarRegisters[0];
        var word1 = scalarRegisters[1];
        var word3 = scalarRegisters[3];
        var baseAddress = word0 | ((ulong)(word1 & 0xFFFFu) << 32);
        var stride = (word1 >> 16) & 0x3FFFu;
        var cacheSwizzle = (word1 & (1u << 30)) != 0;
        var swizzleEnabled = (word1 & (1u << 31)) != 0;
        var unifiedFormat = (word3 >> 12) & 0x7Fu;
        var addTidEnabled = (word3 & (1u << 23)) != 0;
        var outOfBoundsSelect = (word3 >> 28) & 0x3u;
        var type = word3 >> 30;
        var dstSelectX = word3 & 0x7u;

        var matches = baseAddress == expectedBaseAddress &&
            stride == FillRecordBytes &&
            !cacheSwizzle &&
            !swizzleEnabled &&
            unifiedFormat == BufFmt32323232Uint &&
            !addTidEnabled &&
            outOfBoundsSelect == 0 &&
            type == 0 &&
            dstSelectX == 4;
        if (!matches && baseAddress == expectedBaseAddress && _traceAgcShader)
        {
            // Shape matched but the descriptor didn't: dump the raw V# so the
            // constants above can be corrected from evidence, not guessed.
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.constant_fill_descriptor_mismatch " +
                $"word1=0x{word1:X8} word3=0x{word3:X8} stride={stride} " +
                $"format={unifiedFormat} oob={outOfBoundsSelect} type={type} " +
                $"dst_sel_x={dstSelectX}");
        }

        return matches;
    }

    // RDNA2 table 37: BUF_FMT_32_32_32_32_FLOAT. Bit-preserving, so the raw
    // dword copy has identical semantics to the UINT variant.
    private const uint BufFmt32323232Uint = 75;

    private static bool IsExactMaskedDwordCopyDescriptor(
        IReadOnlyList<uint> scalarRegisters,
        uint scalarBase,
        ulong expectedBaseAddress)
    {
        if (scalarBase + 3 >= scalarRegisters.Count)
        {
            return false;
        }

        var word0 = scalarRegisters[(int)scalarBase];
        var word1 = scalarRegisters[(int)scalarBase + 1];
        var word3 = scalarRegisters[(int)scalarBase + 3];
        var baseAddress = word0 | ((ulong)(word1 & 0xFFFFu) << 32);
        var stride = (word1 >> 16) & 0x3FFFu;
        var cacheSwizzle = (word1 & (1u << 30)) != 0;
        var swizzleEnabled = (word1 & (1u << 31)) != 0;
        var unifiedFormat = (word3 >> 12) & 0x7Fu;
        var addTidEnabled = (word3 & (1u << 23)) != 0;
        var outOfBoundsSelect = (word3 >> 28) & 0x3u;
        var type = word3 >> 30;
        var dstSelectX = word3 & 0x7u;

        // RDNA2 tables 35 and 37: OOB_SELECT=0 is structured indexing, so
        // NUM_RECORDS counts stride-sized records. FORMAT=20 is 32_UINT and
        // dst_sel_x=4 selects its R component. ADD_TID and either swizzle bit
        // alter addressing and are therefore outside this replacement.
        return baseAddress == expectedBaseAddress &&
               stride == sizeof(uint) &&
               !cacheSwizzle &&
               !swizzleEnabled &&
               unifiedFormat == 20 &&
               !addTidEnabled &&
               outOfBoundsSelect == 0 &&
               type == 0 &&
               dstSelectX == 4;
    }

    private static Gen5ComputeSystemRegisters DecodeComputeSystemRegisters(
        IReadOnlyDictionary<uint, uint> registers)
    {
        registers.TryGetValue(ComputePgmRsrc2, out var rsrc2);
        var nextRegister = (rsrc2 >> 1) & 0x1Fu;
        uint? workGroupX = null;
        uint? workGroupY = null;
        uint? workGroupZ = null;
        uint? threadGroupSize = null;

        if ((rsrc2 & (1u << 7)) != 0)
        {
            workGroupX = nextRegister++;
        }

        if ((rsrc2 & (1u << 8)) != 0)
        {
            workGroupY = nextRegister++;
        }

        if ((rsrc2 & (1u << 9)) != 0)
        {
            workGroupZ = nextRegister++;
        }

        if ((rsrc2 & (1u << 10)) != 0)
        {
            threadGroupSize = nextRegister++;
        }

        return new Gen5ComputeSystemRegisters(
            workGroupX,
            workGroupY,
            workGroupZ,
            threadGroupSize);
    }

    private static string DescribeComputeSystemRegisters(Gen5ComputeSystemRegisters registers) =>
        $"x={DescribeRegister(registers.WorkGroupXRegister)}," +
        $"y={DescribeRegister(registers.WorkGroupYRegister)}," +
        $"z={DescribeRegister(registers.WorkGroupZRegister)}," +
        $"size={DescribeRegister(registers.ThreadGroupSizeRegister)}";

    private static string DescribeRegister(uint? register) =>
        register.HasValue ? $"s{register.Value}" : "-";

    private static uint SelectExportUserDataRegister(
        IReadOnlyDictionary<uint, uint> registers)
    {
        // RSRC2 is the authoritative stage selector: its USER_SGPR field
        // describes the hardware SGPR window even when the shader has zero
        // user-data dwords and therefore no USER_DATA register was written.
        // GFX10 NGG export shaders use the GS user-data bank (RSRC2 at 0x8B),
        // while their program address is carried in the ES/NGG registers.
        // Looking only for a populated USER_DATA range made those shaders
        // fall through to ES (0xCC) and reject every graphics draw because
        // the unrelated ES RSRC2 register at 0xCB was legitimately absent.
        if (HasShaderResource2(registers, GsUserDataRegister))
        {
            return GsUserDataRegister;
        }

        if (HasShaderResource2(registers, EsUserDataRegister))
        {
            return EsUserDataRegister;
        }

        if (HasShaderResource2(registers, VsUserDataRegister))
        {
            return VsUserDataRegister;
        }

        if (HasUserDataRange(registers, GsUserDataRegister))
        {
            return GsUserDataRegister;
        }

        if (HasUserDataRange(registers, EsUserDataRegister))
        {
            return EsUserDataRegister;
        }

        if (HasUserDataRange(registers, VsUserDataRegister))
        {
            return VsUserDataRegister;
        }

        var esValues = CountUserDataValues(registers, EsUserDataRegister);
        var vsValues = CountUserDataValues(registers, VsUserDataRegister);
        return esValues == 0 && vsValues != 0
            ? VsUserDataRegister
            : EsUserDataRegister;
    }

    internal static (uint UserDataRegister, uint ScalarRegisterBase)
        DecodeExportUserDataLayout(IReadOnlyDictionary<uint, uint> registers)
    {
        var userDataRegister = SelectExportUserDataRegister(registers);
        // GFX10 NGG programs use the GS bank. Hardware system SGPRs occupy
        // s0-s7 there, so USER_DATA_0 maps to s8 even before the two halves of
        // a combined shader have been registered. Combined-shader discovery is
        // therefore not a reliable source for this register-layout decision.
        var scalarRegisterBase = userDataRegister == GsUserDataRegister
            ? NggUserDataScalarRegisterBase
            : 0;
        return (userDataRegister, scalarRegisterBase);
    }

    internal static bool IsNggComputeRasterEnabled(string? value) =>
        // The lowering remains experimental: it can create the final target
        // without reproducing the guest export shader's colour writes. Keep
        // the established graphics path unless a developer opts in explicitly.
        string.Equals(value, "1", StringComparison.Ordinal);

    internal static Gen5GraphicsSystemRegisters?
        DecodeNggGraphicsSystemRegisters(
            IReadOnlyDictionary<uint, uint> registers,
            uint? mergedWaveInfo = null)
    {
        var hasLow = registers.TryGetValue(
            GsIndirectUserDataLowRegister,
            out var low);
        var hasHigh = registers.TryGetValue(
            GsIndirectUserDataHighRegister,
            out var high);
        if (mergedWaveInfo is null && (!hasLow || !hasHigh))
        {
            return null;
        }

        var address = low | ((ulong)high << 32);
        if (address == 0 && mergedWaveInfo is null)
        {
            return null;
        }

        return new Gen5GraphicsSystemRegisters(address, mergedWaveInfo);
    }

    internal static uint EncodeNggMergedWaveInfo(
        uint primitiveType,
        uint vertexCount)
    {
        var inputPrimitiveCount = primitiveType switch
        {
            1 => vertexCount,
            2 => (vertexCount + 1) / 2,
            3 => vertexCount > 1 ? vertexCount - 1 : 0,
            5 or 6 => vertexCount > 2 ? vertexCount - 2 : 0,
            _ => (vertexCount + 2) / 3,
        };
        var esThreadCount = Math.Min(vertexCount, 64u);
        var gsThreadCount = Math.Min(inputPrimitiveCount, 64u);
        var waveCount = Math.Clamp(
            (Math.Max(vertexCount, inputPrimitiveCount) + 63) / 64,
            1u,
            15u);
        return esThreadCount |
            (gsThreadCount << 8) |
            (waveCount << 28);
    }

    private static bool HasShaderResource2(
        IReadOnlyDictionary<uint, uint> registers,
        uint userDataBaseRegister) =>
        registers.ContainsKey(userDataBaseRegister - 1);

    private static bool HasUserDataRange(
        IReadOnlyDictionary<uint, uint> registers,
        uint startRegister)
    {
        for (var index = 0u; index < 16; index++)
        {
            if (registers.ContainsKey(startRegister + index))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountUserDataValues(
        IReadOnlyDictionary<uint, uint> registers,
        uint startRegister)
    {
        var count = 0;
        for (var index = 0u; index < 16; index++)
        {
            count += registers.TryGetValue(startRegister + index, out var value) &&
                     value != 0
                ? 1
                : 0;
        }

        return count;
    }

    private static uint GetComputeLocalSize(
        IReadOnlyDictionary<uint, uint> registers,
        uint register)
    {
        return registers.TryGetValue(register, out var value)
            ? Math.Max(value & 0xFFFFu, 1u)
            : 1u;
    }

    private static uint GetComputePartialSize(
        IReadOnlyDictionary<uint, uint> registers,
        uint register) =>
        registers.TryGetValue(register, out var value)
            ? value >> 16
            : 0u;

    private static int TryApplySoftwareComputeBlits(
        CpuContext ctx,
        ulong shaderAddress,
        IReadOnlyList<(Gen5ImageBinding Binding, TextureDescriptor Texture)> bindings)
    {
        var blits = 0;
        TextureDescriptor? source = null;
        foreach (var (binding, texture) in bindings)
        {
            if (binding.Opcode.StartsWith("ImageStore", StringComparison.Ordinal))
            {
                if (source is { } sourceTexture &&
                    TrySoftwareTextureBlit(ctx, sourceTexture, texture, out var fingerprint))
                {
                    blits++;
                    var key = (shaderAddress, sourceTexture.Address, texture.Address);
                    lock (_softwarePresenterGate)
                    {
                        if (!_softwareComputeBlitFingerprints.TryGetValue(key, out var previous) ||
                            previous != fingerprint)
                        {
                            _softwareComputeBlitFingerprints[key] = fingerprint;
                            TraceAgcShader(
                                $"agc.compute_blit cs=0x{shaderAddress:X16} " +
                                $"src=0x{sourceTexture.Address:X16}:{sourceTexture.Width}x{sourceTexture.Height}:fmt{sourceTexture.Format}/num{sourceTexture.NumberType}/tile{sourceTexture.TileMode} " +
                                $"dst=0x{texture.Address:X16}:{texture.Width}x{texture.Height}:fmt{texture.Format}/num{texture.NumberType}/tile{texture.TileMode} " +
                                $"fingerprint=0x{fingerprint:X16}");
                        }
                    }
                }
                else if (source is { } cachedSourceTexture &&
                    GuestGpu.Current.TrySubmitGuestImageBlit(
                        cachedSourceTexture.Address,
                        cachedSourceTexture.Width,
                        cachedSourceTexture.Height,
                        cachedSourceTexture.Format,
                        cachedSourceTexture.NumberType,
                        texture.Address,
                        texture.Width,
                        texture.Height,
                        texture.Format,
                        texture.NumberType))
                {
                    blits++;
                    TraceAgcShader(
                        $"agc.compute_gpu_blit cs=0x{shaderAddress:X16} " +
                        $"src=0x{cachedSourceTexture.Address:X16}:{cachedSourceTexture.Width}x{cachedSourceTexture.Height}:fmt{cachedSourceTexture.Format}/num{cachedSourceTexture.NumberType}/tile{cachedSourceTexture.TileMode} " +
                        $"dst=0x{texture.Address:X16}:{texture.Width}x{texture.Height}:fmt{texture.Format}/num{texture.NumberType}/tile{texture.TileMode}");
                }

                continue;
            }

            if (binding.Opcode.StartsWith("Image", StringComparison.Ordinal))
            {
                source = texture;
            }
        }

        return blits;
    }

    private static bool TrySoftwareTextureBlit(
        CpuContext ctx,
        TextureDescriptor source,
        TextureDescriptor destination,
        out ulong fingerprint)
    {
        fingerprint = 0;
        var bytesPerTexel = GetTextureBytesPerTexel(source.Format);
        if (bytesPerTexel == 0 ||
            bytesPerTexel != GetTextureBytesPerTexel(destination.Format) ||
            source.Type != Gen5TextureType2D ||
            destination.Type != Gen5TextureType2D ||
            source.Width == 0 ||
            source.Height == 0 ||
            destination.Width == 0 ||
            destination.Height == 0 ||
            source.Width > 8192 ||
            source.Height > 8192 ||
            destination.Width > 8192 ||
            destination.Height > 8192)
        {
            return false;
        }

        var sourceBytes = checked((ulong)source.Width * source.Height * bytesPerTexel);
        var destinationBytes = checked((ulong)destination.Width * destination.Height * bytesPerTexel);
        if (sourceBytes == 0 ||
            destinationBytes == 0 ||
            sourceBytes > MaxPresentedTextureBytes ||
            destinationBytes > MaxPresentedTextureBytes ||
            sourceBytes > int.MaxValue ||
            destinationBytes > int.MaxValue)
        {
            return false;
        }

        var sourceData = new byte[(int)sourceBytes];
        if (!ctx.Memory.TryRead(source.Address, sourceData))
        {
            return false;
        }

        var nonzero = 0;
        foreach (var value in sourceData)
        {
            if (value != 0)
            {
                nonzero++;
                break;
            }
        }

        if (nonzero == 0)
        {
            return false;
        }

        var destinationData = new byte[(int)destinationBytes];
        for (uint y = 0; y < destination.Height; y++)
        {
            var sourceY = (uint)(((ulong)y * source.Height) / destination.Height);
            for (uint x = 0; x < destination.Width; x++)
            {
                var sourceX = (uint)(((ulong)x * source.Width) / destination.Width);
                var sourceOffset = checked((int)(((ulong)sourceY * source.Width + sourceX) * bytesPerTexel));
                var destinationOffset = checked((int)(((ulong)y * destination.Width + x) * bytesPerTexel));
                sourceData.AsSpan(sourceOffset, (int)bytesPerTexel)
                    .CopyTo(destinationData.AsSpan(destinationOffset, (int)bytesPerTexel));
            }
        }

        if (!ctx.Memory.TryWrite(destination.Address, destinationData))
        {
            return false;
        }

        fingerprint = ComputeFingerprint(destinationData);
        return true;
    }

    private static string ProbeTexture(CpuContext ctx, TextureDescriptor texture)
    {
        if (texture.Width == 0 ||
            texture.Height == 0)
        {
            return "probe=unsupported";
        }

        var totalBytes = GetTextureByteCount(
            texture.Format,
            texture.Width,
            texture.Height,
            GetTextureVolumeDepth(texture.Type, texture.Depth));
        if (totalBytes == 0)
        {
            return "probe=unsupported";
        }

        const int sampleCount = 32;
        const int sampleSize = 256;
        var sample = new byte[sampleSize];
        var reads = 0;
        var nonzero = 0;
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        for (var index = 0; index < sampleCount; index++)
        {
            var maxOffset = totalBytes > sampleSize ? totalBytes - sampleSize : 0;
            var offset = sampleCount == 1
                ? 0
                : maxOffset * (ulong)index / (sampleCount - 1);
            if (!ctx.Memory.TryRead(texture.Address + offset, sample))
            {
                continue;
            }

            reads++;
            foreach (var value in sample)
            {
                if (value != 0)
                {
                    nonzero++;
                }

                hash = (hash ^ value) * prime;
            }
        }

        var bytesPerTexel = GetTextureBytesPerTexel(texture.Format);
        var texels = bytesPerTexel is > 0 and <= 16
            ? string.Join(
                '/',
                ProbeTextureTexel(ctx, texture.Address, (int)bytesPerTexel),
                ProbeTextureTexel(
                    ctx,
                    texture.Address +
                    (((ulong)(texture.Height / 2) * texture.Width) + (texture.Width / 2)) *
                    bytesPerTexel,
                    (int)bytesPerTexel),
                ProbeTextureTexel(
                    ctx,
                    texture.Address + totalBytes - bytesPerTexel,
                    (int)bytesPerTexel))
            : "unsupported";
        return $"probe={reads}/{sampleCount}:{nonzero}:0x{hash:X16}:texels={texels}";
    }

    private static string ProbeTextureTexel(CpuContext ctx, ulong address, int size)
    {
        var texel = new byte[size];
        return ctx.Memory.TryRead(address, texel)
            ? Convert.ToHexString(texel)
            : "unreadable";
    }

    private static ulong GetTextureBytesPerTexel(uint format) =>
        format switch
        {
            1 => 1UL,
            2 => 2UL,
            3 => 2UL,
            4 => 4UL,
            5 => 4UL,
            6 => 4UL,
            7 => 4UL,
            9 => 4UL,
            10 => 4UL,
            11 => 8UL,
            12 => 8UL,
            13 => 12UL,
            14 => 16UL,
            _ => 0UL,
        };

    internal static ulong GetTextureByteCount(
        uint format,
        uint width,
        uint height,
        uint depth = 1)
    {
        var bytesPerTexel = GetTextureBytesPerTexel(format);
        if (bytesPerTexel != 0)
        {
            return checked(
                (ulong)width *
                height *
                Math.Max(depth, 1u) *
                bytesPerTexel);
        }

        var blockBytes = (ulong)GetBlockCompressedBlockBytes(format);
        return blockBytes == 0
            ? 0
            : checked(
                ((ulong)width + 3) / 4 *
                (((ulong)height + 3) / 4) *
                Math.Max(depth, 1u) *
                blockBytes);
    }

    internal static uint GetTextureVolumeDepth(uint type, uint depth) =>
        type == Gen5TextureType3D
            ? Math.Max(depth, 1u)
            : 1u;

    private static uint GetLinearTexturePitch(uint pitch, uint height, uint format)
    {
        var bytesPerTexel = GetTextureBytesPerTexel(format);
        if (bytesPerTexel == 0 || height == 0)
        {
            return pitch;
        }

        // GNM linear surfaces align the row pitch to 256 bytes, so a 32px
        // RGBA8 texture is stored with a 64px (256-byte) pitch and a 288px
        // one with 320px. Reading at the unpadded width made every padded
        // tail land on the next row, which showed as transparent gaps every
        // other row on small tiles and diagonal dashes on wider surfaces.
        var pitchBytes = AlignUp((ulong)pitch * bytesPerTexel, 256UL);
        return checked((uint)(pitchBytes / bytesPerTexel));
    }

    private static ulong AlignUp(ulong value, ulong alignment) =>
        (value + alignment - 1) & ~(alignment - 1);

    private static void TraceShaderTranslationMiss(
        CpuContext ctx,
        SubmittedDcbState state,
        uint vertexCount,
        bool hasExportShader,
        ulong exportShaderAddress,
        bool hasPixelShader,
        ulong pixelShaderAddress,
        bool hasPsInputEna,
        uint psInputEna,
        bool hasPsInputAddr,
        uint psInputAddr,
        string? translationError = null)
    {
        var firstFailure = false;
        if (!string.IsNullOrEmpty(translationError))
        {
            lock (_submitTraceGate)
            {
                firstFailure = _tracedShaderFailures.Add(
                    (pixelShaderAddress, translationError));
            }
        }

        if (!firstFailure &&
            !ShouldTraceHotPath(ref _shaderTranslationMissTraceCount))
        {
            return;
        }

        // Translation failures are compatibility issues, not merely verbose
        // shader diagnostics. Report each distinct failure once even when AGC
        // tracing is disabled so normal runs preserve the missing opcode or
        // unsupported translation reason needed to fix the game.
        if (firstFailure)
        {
            ulong exportShaderHeader;
            ulong pixelShaderHeader;
            lock (_submitTraceGate)
            {
                _shaderHeadersByCode.TryGetValue(exportShaderAddress, out exportShaderHeader);
                _shaderHeadersByCode.TryGetValue(pixelShaderAddress, out pixelShaderHeader);
            }

            DumpShaderProgramIfRequested(
                ctx,
                "es",
                exportShaderAddress,
                exportShaderHeader,
                translationError!);
            DumpShaderProgramIfRequested(
                ctx,
                "ps",
                pixelShaderAddress,
                pixelShaderHeader,
                translationError!);

            Console.Error.WriteLine(
                $"[COMPAT][SHADER] ps=0x{pixelShaderAddress:X16} " +
                $"es=0x{exportShaderAddress:X16} error={translationError}");
        }

        if ((!hasPixelShader || !hasPsInputEna || !hasPsInputAddr) &&
            TryMarkMissingPixelShaderBindingsTrace())
        {
            TraceAgcShader(
                $"agc.shader_register_candidates " +
                DescribeShaderRegisterCandidates(ctx, state.ShRegisters));
        }

        if (!hasPixelShader)
        {
            state.CxRegisters.TryGetValue(DbDepthControl, out var rawDepthControl);
            state.CxRegisters.TryGetValue(DbZInfo, out var rawZInfo);
            state.CxRegisters.TryGetValue(DbDepthSizeXy, out var rawDepthSize);
            state.CxRegisters.TryGetValue(DbDepthView, out var rawDepthView);
            var depthState = DecodeDepthState(state.CxRegisters);
            var depthTarget = DecodeDepthTarget(state.CxRegisters);
            TraceAgcShader(
                $"agc.shader_depth_state control=0x{rawDepthControl:X8} " +
                $"zinfo=0x{rawZInfo:X8} size=0x{rawDepthSize:X8} " +
                $"view=0x{rawDepthView:X8} " +
                $"test={(depthState.TestEnable ? 1 : 0)} " +
                $"write={(depthState.WriteEnable ? 1 : 0)} " +
                $"func={depthState.CompareOp} " +
                (depthTarget is null
                    ? "target=none"
                    : $"target=0x{depthTarget.Address:X16}:" +
                      $"{depthTarget.Width}x{depthTarget.Height}:" +
                      $"fmt{depthTarget.GuestFormat}/sw{depthTarget.SwizzleMode}:" +
                      $"ro={(depthTarget.ReadOnly ? 1 : 0)}"));
        }

        var shaderDecode = string.Empty;
        if (hasExportShader && hasPixelShader)
        {
            var shouldDescribe = false;
            ulong exportShaderHeader;
            ulong pixelShaderHeader;
            lock (_submitTraceGate)
            {
                shouldDescribe = _tracedShaderDecodePairs.Add((exportShaderAddress, pixelShaderAddress));
                _shaderHeadersByCode.TryGetValue(exportShaderAddress, out exportShaderHeader);
                _shaderHeadersByCode.TryGetValue(pixelShaderAddress, out pixelShaderHeader);
            }

            if (shouldDescribe)
            {
                var isCombinedExportShader =
                    Gen5ShaderTranslator.IsCombinedShader(ctx, exportShaderAddress);
                var exportUserDataLayout =
                    DecodeExportUserDataLayout(state.ShRegisters);
                shaderDecode = $" decode={Gen5ShaderTranslator.Describe(ctx, exportShaderAddress, pixelShaderAddress)}";
                TraceAgcShader(
                    $"agc.shader_words es=0x{exportShaderAddress:X16} " +
                    Gen5ShaderTranslator.DescribeWords(ctx, exportShaderAddress));
                if (Gen5ShaderTranslator.TryCreateState(
                        ctx,
                        exportShaderAddress,
                        exportShaderHeader,
                        state.ShRegisters,
                        exportUserDataLayout.UserDataRegister,
                        out var exportState,
                        out _,
                        userDataScalarRegisterBase:
                            exportUserDataLayout.ScalarRegisterBase,
                        graphicsSystemRegisters: isCombinedExportShader
                            ? DecodeNggGraphicsSystemRegisters(state.ShRegisters)
                            : null) &&
                    Gen5ShaderTranslator.TryCreateState(
                        ctx,
                        pixelShaderAddress,
                        pixelShaderHeader,
                        state.ShRegisters,
                        PsTextureUserDataRegister,
                        out var pixelState,
                        out _))
                {
                    TraceAgcShader(
                        $"agc.shader_state es=0x{exportShaderAddress:X16} " +
                        Gen5ShaderTranslator.DescribeState(exportState));
                    TraceAgcShader(
                        $"agc.shader_state ps=0x{pixelShaderAddress:X16} " +
                        Gen5ShaderTranslator.DescribeState(pixelState));
                    if (Gen5ShaderScalarEvaluator.TryEvaluate(
                            ctx,
                            pixelState,
                            out var evaluation,
                            out var bindingError))
                    {
                        foreach (var binding in evaluation.ImageBindings)
                        {
                            TraceAgcShader(
                                $"agc.shader_binding ps=0x{pixelShaderAddress:X16} " +
                                $"pc=0x{binding.Pc:X} op={binding.Opcode} " +
                                $"resource={FormatShaderDwords(binding.ResourceDescriptor)} " +
                                $"sampler={FormatShaderDwords(binding.SamplerDescriptor)}");
                        }

                        foreach (var binding in evaluation.GlobalMemoryBindings)
                        {
                            TraceAgcShader(
                                $"agc.shader_global_binding ps=0x{pixelShaderAddress:X16} " +
                                $"saddr=s{binding.ScalarAddress} " +
                                $"base=0x{binding.BaseAddress:X16} bytes={binding.DataLength} " +
                                $"pcs={string.Join(',', binding.InstructionPcs.Select(pc => $"0x{pc:X}"))}");
                        }

                        if (GuestGpu.Current.TryCompilePixelShader(
                                 pixelState,
                                 evaluation,
                                 [new(0, 0, Gen5PixelOutputKind.Float)],
                                 out var compiledPixel,
                                 out var compileError,
                                 pixelInputEnable: psInputEna,
                                 pixelInputAddress: psInputAddr,
                                 pixelInputControls: ReadPsInputCntlRegisters(state.CxRegisters),
                                 storageBufferOffsetAlignment:
                                     _storageBufferOffsetAlignment))
                        {
                            TraceAgcShader(
                                $"agc.shader_spirv ps=0x{pixelShaderAddress:X16} " +
                                $"bytes={compiledPixel!.Payload.Length} bindings={evaluation.ImageBindings.Count} " +
                                $"global_buffers={evaluation.GlobalMemoryBindings.Count}");
                        }
                        else
                        {
                            TraceAgcShader(
                                $"agc.shader_spirv_error ps=0x{pixelShaderAddress:X16} " +
                                compileError.ReplaceLineEndings(" "));
                        }
                    }
                    else
                    {
                        TraceAgcShader(
                            $"agc.shader_binding_error ps=0x{pixelShaderAddress:X16} " +
                            bindingError);
                    }
                }
            }
        }

        TraceAgcShader(
            $"agc.shader_translate_miss vertices={vertexCount} " +
            $"es={(hasExportShader ? $"0x{exportShaderAddress:X16}" : "missing")} " +
            $"ps={(hasPixelShader ? $"0x{pixelShaderAddress:X16}" : "missing")} " +
            $"ps_ena={(hasPsInputEna ? $"0x{psInputEna:X8}" : "missing")} " +
            $"ps_addr={(hasPsInputAddr ? $"0x{psInputAddr:X8}" : "missing")}" +
            (string.IsNullOrEmpty(translationError) ? string.Empty : $" error={translationError}") +
            shaderDecode);
    }

    private static bool TryMarkMissingPixelShaderBindingsTrace()
    {
        lock (_submitTraceGate)
        {
            if (_tracedMissingPixelShaderBindings)
            {
                return false;
            }

            _tracedMissingPixelShaderBindings = true;
            return true;
        }
    }

    private static string DescribeShaderRegisterCandidates(
        CpuContext ctx,
        IReadOnlyDictionary<uint, uint> registers)
    {
        var candidates = new List<(uint Register, ulong Address, ulong Header)>();
        lock (_submitTraceGate)
        {
            foreach (var (register, lo) in registers)
            {
                if (!registers.TryGetValue(register + 1, out var hi))
                {
                    continue;
                }

                var address = ((ulong)hi << 40) | ((ulong)lo << 8);
                if (address != 0 &&
                    _shaderHeadersByCode.TryGetValue(address, out var header))
                {
                    candidates.Add((register, address, header));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return "none";
        }

        return string.Join(
            ',',
            candidates
                .OrderBy(candidate => candidate.Register)
                .Take(16)
                .Select(candidate =>
                {
                    var type = TryReadByte(
                        ctx,
                        candidate.Header + ShaderTypeOffset,
                        out var shaderType)
                        ? shaderType.ToString()
                        : "?";
                    return
                        $"sh[0x{candidate.Register:X}/0x{candidate.Register + 1:X}]=" +
                        $"0x{candidate.Address:X16}:type{type}";
                }));
    }

    private static bool TryGetShaderAddress(
        IReadOnlyDictionary<uint, uint> registers,
        uint loRegister,
        uint hiRegister,
        out ulong address)
    {
        address = 0;
        if (!registers.TryGetValue(loRegister, out var lo) ||
            !registers.TryGetValue(hiRegister, out var hi))
        {
            return false;
        }

        address = ((ulong)hi << 40) | ((ulong)lo << 8);
        return address != 0;
    }

    private static bool TryReadTextureDescriptor(
        CpuContext ctx,
        ulong packetAddress,
        uint packetLength,
        out TextureDescriptor descriptor)
    {
        descriptor = default;
        if (packetLength < 10 ||
            !TryReadUInt32(ctx, packetAddress + 4, out var startRegister))
        {
            return false;
        }

        var valueCount = packetLength - 2;
        if (startRegister > PsTextureUserDataRegister ||
            startRegister + valueCount < PsTextureUserDataRegister + 8)
        {
            return false;
        }

        var descriptorAddress =
            packetAddress +
            8 +
            ((ulong)(PsTextureUserDataRegister - startRegister) * sizeof(uint));
        Span<uint> fields = stackalloc uint[8];
        for (var i = 0; i < fields.Length; i++)
        {
            if (!TryReadUInt32(ctx, descriptorAddress + ((ulong)i * sizeof(uint)), out fields[i]))
            {
                return false;
            }
        }

        return TryDecodeTextureDescriptor(fields.ToArray(), out descriptor);
    }

    private static bool TryDecodeTextureDescriptor(
        IReadOnlyList<uint> fields,
        out TextureDescriptor descriptor)
    {
        descriptor = default;
        if (fields.Count < 4)
        {
            return false;
        }

        // RDNA2 ISA table 45: BASE_ADDRESS is addr[47:8], WIDTH is the full
        // 16-bit field split across word1/word2, and HEIGHT is word2[29:14].
        // Keeping the high base byte is required for legal guest VAs above
        // 1 TiB; it is not descriptor metadata.
        var address = (((ulong)(fields[1] & 0xFFu) << 32) | fields[0]) << 8;
        var width = (((fields[1] >> 30) & 0x3u) | ((fields[2] & 0x3FFFu) << 2)) + 1;
        var height = ((fields[2] >> 14) & 0xFFFFu) + 1;
        var unifiedFormat = (fields[1] >> 20) & 0x1FFu;
        if (unifiedFormat == 0 ||
            !Gfx10UnifiedFormat.TryDecode(
                unifiedFormat,
                out var format,
                out var numberType))
        {
            return false;
        }
        var tileMode = (fields[3] >> 20) & 0x1Fu;
        var type = (fields[3] >> 28) & 0xFu;
        var baseLevel = (fields[3] >> 12) & 0xFu;
        var lastLevel = (fields[3] >> 16) & 0xFu;
        var bcSwizzle = (fields[3] >> 25) & 0x7u;
        var hasExtendedDescriptor = fields.Count >= 8;
        var word4 = fields.Count >= 5 ? fields[4] : 0u;
        var depthOrLastSlice = (word4 & 0x1FFFu) + 1;
        var baseArray = (word4 >> 16) & 0x1FFFu;
        // In a 256-bit 1D/2D/2D-MSAA descriptor word4[13:0] is
        // (pitch-1). A zeroed upper half denotes the common 128-bit resource,
        // where pitch is implicit; use width rather than inventing pitch=1.
        var pitch = type is 8u or 9u or 14u && word4 != 0
            ? (word4 & 0x3FFFu) + 1
            : width;
        var depth = type is 10u or 11u or 12u or 13u or 15u
            ? depthOrLastSlice
            : 1u;
        var word5 = fields.Count >= 6 ? fields[5] : 0u;
        var arrayPitch = word5 & 0xFu;
        var maxMip = (word5 >> 4) & 0xFu;
        var minLod = (fields[1] >> 8) & 0xFFFu;
        var minLodWarn = (word5 >> 8) & 0xFFFu;
        var word6 = fields.Count >= 7 ? fields[6] : 0u;
        var word7 = fields.Count >= 8 ? fields[7] : 0u;
        var metadataAddress = ((((ulong)word7 << 8) | (word6 >> 24)) << 8);
        var descriptorFlags = word6 & 0x00FF_FFFFu;
        var dstSelect = fields[3] & 0xFFFu;
        if (address == 0 || width == 0 || height == 0 || type is >= 1 and <= 7)
        {
            return false;
        }

        descriptor = new TextureDescriptor(
            address,
            width,
            height,
            format,
            numberType,
            tileMode,
            type,
            baseLevel,
            lastLevel,
            pitch,
            dstSelect,
            depth,
            baseArray,
            arrayPitch,
            maxMip,
            minLod,
            minLodWarn,
            bcSwizzle,
            metadataAddress,
            descriptorFlags,
            hasExtendedDescriptor);
        return true;
    }

    private static TextureDescriptor CreateFallbackTextureDescriptor(IReadOnlyList<uint> fields)
    {
        var format = Gen5TextureFormatR8G8B8A8Unorm;
        var numberType = 0u;
        var tileMode = 0u;
        if (fields.Count >= 4)
        {
            var unifiedFormat = (fields[1] >> 20) & 0x1FFu;
            if (!Gfx10UnifiedFormat.TryDecode(
                    unifiedFormat,
                    out format,
                    out numberType))
            {
                format = Gen5TextureFormatR8G8B8A8Unorm;
                numberType = 0;
            }
            tileMode = (fields[3] >> 20) & 0x1Fu;
            if (format == 0)
            {
                format = Gen5TextureFormatR8G8B8A8Unorm;
            }
        }

        return new TextureDescriptor(
            Address: 0,
            Width: 1,
            Height: 1,
            Format: format,
            NumberType: numberType,
            TileMode: tileMode,
            Type: Gen5TextureType2D,
            BaseLevel: 0,
            LastLevel: 0,
            Pitch: 1,
            DstSelect: 0xFAC);
    }

    private static bool TrySoftwarePresent(
        CpuContext ctx,
        TextureDescriptor source,
        int videoOutHandle,
        int displayBufferIndex)
    {
        if (source.Format != Gen5TextureFormatR8G8B8A8Unorm ||
            source.TileMode != 0 ||
            source.Type != Gen5TextureType2D ||
            source.Width > 8192 ||
            source.Height > 8192 ||
            !VideoOutExports.TryGetDisplayBufferInfo(videoOutHandle, displayBufferIndex, out var destination) ||
            destination.Address == 0 ||
            destination.Width == 0 ||
            destination.Height == 0 ||
            destination.Width > 8192 ||
            destination.Height > 8192 ||
            destination.TilingMode != 0 ||
            destination.PixelFormat is not (
                VideoOutPixelFormatA8R8G8B8Srgb or
                VideoOutPixelFormatA8B8G8R8Srgb or
                VideoOutPixelFormat2R8G8B8A8Srgb or
                VideoOutPixelFormat2B8G8R8A8Srgb or
                VideoOutPixelFormat2R10G10B10A2 or
                VideoOutPixelFormat2B10G10R10A2 or
                VideoOutPixelFormat2R10G10B10A2Srgb or
                VideoOutPixelFormat2B10G10R10A2Srgb or
                VideoOutPixelFormat2R10G10B10A2Bt2100Pq or
                VideoOutPixelFormat2B10G10R10A2Bt2100Pq))
        {
            return false;
        }

        var sourceByteCount = checked((ulong)source.Width * source.Height * 4);
        if (sourceByteCount > 256UL * 1024UL * 1024UL)
        {
            return false;
        }

        var sourceBytes = new byte[(int)sourceByteCount];
        if (!ctx.Memory.TryRead(source.Address, sourceBytes))
        {
            return false;
        }

        var fingerprint = ComputeFingerprint(sourceBytes);
        var fingerprintKey = (source.Address, destination.Address);
        lock (_softwarePresenterGate)
        {
            if (_softwarePresenterFingerprints.TryGetValue(fingerprintKey, out var previousFingerprint) &&
                previousFingerprint == fingerprint)
            {
                return true;
            }
        }

        var destinationPitch = destination.PitchInPixel == 0
            ? destination.Width
            : destination.PitchInPixel;
        if (destinationPitch < destination.Width)
        {
            return false;
        }

        var destinationRow = new byte[checked((int)destinationPitch * 4)];
        var rgbaDestination = destination.PixelFormat is
            VideoOutPixelFormatA8B8G8R8Srgb or
            VideoOutPixelFormat2R8G8B8A8Srgb;
        var packed10Destination =
            VideoOutExports.IsPacked10BitPixelFormat(destination.PixelFormat);
        for (uint y = 0; y < destination.Height; y++)
        {
            var sourceY = (uint)(((ulong)y * source.Height) / destination.Height);
            for (uint x = 0; x < destination.Width; x++)
            {
                var sourceX = (uint)(((ulong)x * source.Width) / destination.Width);
                var sourceOffset = checked((int)(((ulong)sourceY * source.Width + sourceX) * 4));
                var destinationOffset = checked((int)x * 4);
                if (packed10Destination)
                {
                    if (!VideoOutExports.TryPackRgba8Pixel(
                            destination.PixelFormat,
                            sourceBytes[sourceOffset + 0],
                            sourceBytes[sourceOffset + 1],
                            sourceBytes[sourceOffset + 2],
                            sourceBytes[sourceOffset + 3],
                            out var packed))
                    {
                        return false;
                    }

                    BinaryPrimitives.WriteUInt32LittleEndian(
                        destinationRow.AsSpan(destinationOffset, sizeof(uint)),
                        packed);
                }
                else if (rgbaDestination)
                {
                    destinationRow[destinationOffset + 0] = sourceBytes[sourceOffset + 0];
                    destinationRow[destinationOffset + 1] = sourceBytes[sourceOffset + 1];
                    destinationRow[destinationOffset + 2] = sourceBytes[sourceOffset + 2];
                }
                else
                {
                    destinationRow[destinationOffset + 0] = sourceBytes[sourceOffset + 2];
                    destinationRow[destinationOffset + 1] = sourceBytes[sourceOffset + 1];
                    destinationRow[destinationOffset + 2] = sourceBytes[sourceOffset + 0];
                }

                if (!packed10Destination)
                {
                    destinationRow[destinationOffset + 3] = sourceBytes[sourceOffset + 3];
                }
            }

            var destinationAddress = destination.Address + ((ulong)y * destinationPitch * 4);
            if (!ctx.Memory.TryWrite(destinationAddress, destinationRow))
            {
                return false;
            }
        }

        lock (_softwarePresenterGate)
        {
            _softwarePresenterFingerprints[fingerprintKey] = fingerprint;
        }

        VideoOutExports.SubmitHostRgbaFrame(sourceBytes, source.Width, source.Height);
        TraceAgc(
            $"agc.software_presenter src=0x{source.Address:X16} {source.Width}x{source.Height} fmt={source.Format}/num{source.NumberType} " +
            $"dst=0x{destination.Address:X16} {destination.Width}x{destination.Height} fingerprint=0x{fingerprint:X16}");
        return true;
    }

    private static ulong ComputeFingerprint(ReadOnlySpan<byte> bytes)
    {
        const ulong fnvOffsetBasis = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        var fingerprint = fnvOffsetBasis;
        foreach (var value in bytes)
        {
            fingerprint = (fingerprint ^ value) * fnvPrime;
        }

        return fingerprint;
    }

    private static void TraceSubmittedPacket(
        CpuContext ctx,
        ulong packetAddress,
        uint dwordOffset,
        uint header,
        uint length,
        uint op,
        uint register)
    {
        TraceAgc(
            $"agc.dcb.packet dw={dwordOffset} addr=0x{packetAddress:X16} header=0x{header:X8} len={length} op=0x{op:X2} reg=0x{register:X2}");

        var payloadCount = Math.Min(length - 1, 32u);
        for (uint i = 0; i < payloadCount; i++)
        {
            if (!TryReadUInt32(ctx, packetAddress + ((ulong)(i + 1) * sizeof(uint)), out var value))
            {
                return;
            }

            TraceAgc($"agc.dcb.payload dw={dwordOffset + i + 1} value=0x{value:X8}");
        }

        if (op != ItNop ||
            register is not (RCxRegsIndirect or RShRegsIndirect or RUcRegsIndirect) ||
            length < 4 ||
            !TryReadUInt32(ctx, packetAddress + 4, out var registerCount) ||
            !TryReadUInt64(ctx, packetAddress + 8, out var registersAddress))
        {
            return;
        }

        var registerSpace = register == RCxRegsIndirect ? "cx" : register == RShRegsIndirect ? "sh" : "uc";
        var tracedCount = Math.Min(registerCount, 256u);
        TraceAgc($"agc.dcb.indirect space={registerSpace} regs=0x{registersAddress:X16} count={registerCount}");
        for (uint i = 0; i < tracedCount; i++)
        {
            var entryAddress = registersAddress + ((ulong)i * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var registerOffset) ||
                !TryReadUInt32(ctx, entryAddress + 4, out var value))
            {
                TraceAgc($"agc.dcb.indirect_read_failed space={registerSpace} index={i} addr=0x{entryAddress:X16}");
                return;
            }

            TraceAgc($"agc.dcb.reg space={registerSpace} index={i} offset=0x{registerOffset:X4} value=0x{value:X8}");
        }

        if (tracedCount != registerCount)
        {
            TraceAgc($"agc.dcb.indirect_truncated space={registerSpace} traced={tracedCount} total={registerCount}");
        }
    }

    private static bool PatchShaderProgramRegisters(CpuContext ctx, ulong headerAddress, ulong codeAddress)
    {
        if (!TryReadByte(ctx, headerAddress + ShaderTypeOffset, out var shaderType))
        {
            return false;
        }

        // Firmware 12.70 libSceAgc.sprx SHA-256
        // 110df81f759ae3dffcc9b5e3fa062c74058518631847641b8e08a54f6b8b6e2d,
        // f3dg2CSgRKY at 0xe770: types 4 and 5 deliberately skip program-address
        // relocation. They are the first half of a combined shader; the paired
        // type 6/7 descriptor carries the address that is reconciled later.
        if (shaderType is 4 or 5)
        {
            return true;
        }

        if (!TryReadUInt64(ctx, headerAddress + ShaderShRegistersOffset, out var shRegistersAddress) ||
            !TryReadByte(ctx, headerAddress + ShaderNumShRegistersOffset, out var registerCount) ||
            shRegistersAddress == 0 ||
            registerCount == 0)
        {
            return false;
        }

        var expectedLo = shaderType switch
        {
            0 => ComputePgmLo,
            1 => SpiShaderPgmLoPs,
            2 or 6 => SpiShaderPgmLoEs,
            3 or 7 => SpiShaderPgmLoLs,
            _ => 0u,
        };
        if (expectedLo == 0)
        {
            return false;
        }

        // The provider scans the complete register table, then treats the
        // existing LO/HI value as a relative address and adds the supplied code
        // base. The HI component is the low byte of the following entry's value.
        for (var index = 0; index < registerCount; index++)
        {
            var entryAddress = shRegistersAddress + (ulong)index * 8;
            if (!TryReadUInt32(ctx, entryAddress, out var register))
            {
                return false;
            }

            if (register != expectedLo)
            {
                continue;
            }

            if (!TryReadUInt32(ctx, entryAddress + sizeof(uint), out var relativeLo) ||
                !TryReadByte(ctx, entryAddress + 12, out var relativeHi))
            {
                return false;
            }

            var relativeAddress = ((ulong)relativeLo << 8) | ((ulong)relativeHi << 40);
            var relocatedAddress = relativeAddress + codeAddress;
            if (!TryWriteUInt32(
                    ctx,
                    entryAddress + sizeof(uint),
                    (uint)(relocatedAddress >> 8)))
            {
                return false;
            }

            Span<byte> highAddress = stackalloc byte[1];
            highAddress[0] = (byte)(relocatedAddress >> 40);
            return ctx.Memory.TryWrite(entryAddress + 12, highAddress);
        }

        TraceCreateShader(
            0,
            headerAddress,
            codeAddress,
            $"missing-program-register type={shaderType} expected=0x{expectedLo:X8}");
        return false;
    }

    private static bool IsEsGeometryShaderType(byte shaderType) =>
        shaderType is GsShaderType or GsBackShaderType;

    private static uint MapPrimStateOutputPrimitive(uint primitiveType) =>
        primitiveType switch
        {
            1 => 0,
            2 or 3 => 1,
            4 or 5 or 6 => 2,
            7 => 3,
            8 or 9 => 2,
            10 or 11 => 1,
            12 or 13 or 14 or 15 or 16 => 2,
            17 => 4,
            18 => 1,
            _ => 2,
        };

    private static bool IsRectListPrimitive(uint primitiveType) =>
        AgcPrimitiveHelpers.IsRectListPrimitive(primitiveType);

    private static int SetIndirectPatchAddress(CpuContext ctx, string registerSpace)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var registersAddress = ctx[CpuRegister.Rsi];
        if (commandAddress == 0 || registersAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, commandAddress + 8, (uint)(registersAddress & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(registersAddress >> 32)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.patch_{registerSpace}_addr cmd=0x{commandAddress:X16} regs=0x{registersAddress:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PatchWriteDataControlByte(CpuContext ctx, int byteIndex)
    {
        if (!TryResolveWriteDataPatchArguments(
                ctx,
                ctx[CpuRegister.Rdi],
                ctx[CpuRegister.Rsi],
                out var commandAddress,
                out var value) ||
            !TryReadUInt32(ctx, commandAddress + 4, out var control))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var shift = byteIndex * 8;
        var patchedControl = (control & ~(0xFFu << shift)) | (((uint)value & 0xFFu) << shift);
        return TryWriteUInt32(ctx, commandAddress + 4, patchedControl)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool TryResolveWriteDataPatchArguments(
        CpuContext ctx,
        ulong first,
        ulong second,
        out ulong commandAddress,
        out ulong value)
    {
        if (IsWriteDataPacket(ctx, first))
        {
            commandAddress = first;
            value = second;
            return true;
        }

        if (IsWriteDataPacket(ctx, second))
        {
            commandAddress = second;
            value = first;
            return true;
        }

        commandAddress = 0;
        value = 0;
        return false;
    }

    private static bool IsWriteDataPacket(CpuContext ctx, ulong commandAddress)
    {
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return false;
        }

        return op == ItWriteData || (op == ItNop && register == RWriteData);
    }

    private static int AddIndirectPatchRegisters(CpuContext ctx, string registerSpace)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var registerCount = (uint)ctx[CpuRegister.Rsi];
        if (commandAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt32(ctx, commandAddress + 4, out var currentCount) ||
            !TryWriteUInt32(ctx, commandAddress + 4, currentCount + registerCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.patch_{registerSpace}_add cmd=0x{commandAddress:X16} add={registerCount} total={currentCount + registerCount}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int DcbSetRegistersIndirect(CpuContext ctx, uint packetRegister, string registerSpace)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var registersAddress = ctx[CpuRegister.Rsi];
        var registerCount = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItNop, packetRegister)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, registerCount) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(registersAddress & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(registersAddress >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_{registerSpace}_indirect buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} regs=0x{registersAddress:X16} count={registerCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    private static int DcbSetRegisterDirect(CpuContext ctx, uint op, string registerSpace)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        // Uc/Cx/Sh register is passed by value as {u32 offset, u32 value} in RSI.
        var packedRegister = ctx[CpuRegister.Rsi];
        var registerOffset = (uint)(packedRegister & 0xFFFF_FFFFUL);
        var registerValue = (uint)(packedRegister >> 32);
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        const uint packetDwords = 3;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, op, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, registerOffset & 0xFFFFu) ||
            !TryWriteUInt32(ctx, commandAddress + 8, registerValue))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_set_{registerSpace}_direct buf=0x{commandBufferAddress:X16} " +
            $"cmd=0x{commandAddress:X16} offset=0x{registerOffset:X4} value=0x{registerValue:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    private static bool TryAllocateCommandDwords(CpuContext ctx, ulong commandBufferAddress, uint sizeDwords, out ulong commandAddress)
    {
        commandAddress = 0;
        if (sizeDwords == 0 ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCursorUpOffset, out var cursorUp) ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCursorDownOffset, out var cursorDown) ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCallbackOffset, out var callback) ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferUserDataOffset, out var userData) ||
            !TryReadUInt32(ctx, commandBufferAddress + CommandBufferReservedDwOffset, out var reservedDwords))
        {
            return false;
        }

        var remainingDwords = GetRemainingCommandDwords(cursorUp, cursorDown, reservedDwords);
        if (sizeDwords > remainingDwords)
        {
            // The one place that knows an arena's true final cursor before a
            // switch happens, regardless of which builder export wrote its
            // last bytes — fires only on genuine exhaustion, not per packet.
            if (_forceSubmitOrphanPreamblesEnabled &&
                TryReadUInt64(ctx, commandBufferAddress, out var exhaustedBase) &&
                exhaustedBase != 0)
            {
                lock (_orphanPreambleGate)
                {
                    if ((!_builderArenaLastSeen.TryGetValue(commandBufferAddress, out var seen) ||
                        seen.Base != exhaustedBase ||
                        cursorUp > seen.Cursor))
                    {
                        _builderArenaLastSeen[commandBufferAddress] =
                            (exhaustedBase, cursorUp, GuestThreadExecution.CurrentGuestThreadHandle, System.Diagnostics.Stopwatch.GetTimestamp());
                    }
                }
            }

            TraceAgc($"agc.cmd_alloc_full buf=0x{commandBufferAddress:X16} need={sizeDwords} remaining={remainingDwords} callback=0x{callback:X16}");
            var scheduler = GuestThreadExecution.Scheduler;
            ulong callbackResult = 0;
            string? callbackError = null;
            if (callback == 0 ||
                scheduler is null ||
                !scheduler.TryCallGuestFunction(
                    ctx,
                    callback,
                    commandBufferAddress,
                    (ulong)sizeDwords + reservedDwords,
                    userData,
                    0,
                    0,
                    "agc_command_buffer_full",
                    out callbackResult,
                    out callbackError))
            {
                TraceAgc(
                    $"agc.cmd_alloc_callback_failed buf=0x{commandBufferAddress:X16} " +
                    $"callback=0x{callback:X16} result=0x{callbackResult:X16} " +
                    $"error={callbackError ?? "none"}");
                return false;
            }

            TraceAgc(
                $"agc.cmd_alloc_callback_complete buf=0x{commandBufferAddress:X16} " +
                $"callback=0x{callback:X16} result=0x{callbackResult:X16}");

            if (!TryReadUInt64(ctx, commandBufferAddress + CommandBufferCursorUpOffset, out cursorUp) ||
                !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCursorDownOffset, out cursorDown) ||
                !TryReadUInt32(ctx, commandBufferAddress + CommandBufferReservedDwOffset, out reservedDwords) ||
                sizeDwords > GetRemainingCommandDwords(cursorUp, cursorDown, reservedDwords))
            {
                TraceAgc($"agc.cmd_alloc_callback_no_space buf=0x{commandBufferAddress:X16} need={sizeDwords}");
                return false;
            }
        }

        var nextCursor = cursorUp + ((ulong)sizeDwords * sizeof(uint));
        if (!TryWriteUInt64(ctx, commandBufferAddress + CommandBufferCursorUpOffset, nextCursor))
        {
            return false;
        }

        commandAddress = cursorUp;
        return true;
    }

    private static uint GetRemainingCommandDwords(
        ulong cursorUp,
        ulong cursorDown,
        uint reservedDwords)
    {
        var availableDwords = cursorDown >= cursorUp
            ? Math.Min((cursorDown - cursorUp) / sizeof(uint), uint.MaxValue)
            : 0;
        return availableDwords > reservedDwords
            ? (uint)availableDwords - reservedDwords
            : 0;
    }

    private static bool CopyShaderRegister(CpuContext ctx, ulong sourceAddress, ulong destinationAddress)
    {
        if (!TryReadUInt32(ctx, sourceAddress, out var offset) ||
            !TryReadUInt32(ctx, sourceAddress + sizeof(uint), out var value))
        {
            return false;
        }

        return TryWriteUInt32(ctx, destinationAddress, offset) &&
               TryWriteUInt32(ctx, destinationAddress + sizeof(uint), value);
    }

    private static bool IsFusedShaderHalfPair(byte frontType, byte backType) =>
        (frontType == GsFrontShaderType && backType == GsBackShaderType) ||
        (frontType == HsFrontShaderType && backType == HsBackShaderType);

    private static bool TryFindShaderRegister(
        CpuContext ctx,
        ulong registersAddress,
        int registerCount,
        uint registerOffset,
        int occurrence,
        out ulong entryAddress)
    {
        if (registersAddress != 0)
        {
            for (var index = 0; index < registerCount; index++)
            {
                var address = registersAddress + (ulong)index * 8;
                if (!TryReadUInt32(ctx, address, out var current) || current != registerOffset)
                {
                    continue;
                }

                if (occurrence == 0)
                {
                    entryAddress = address;
                    return true;
                }

                occurrence--;
            }
        }

        entryAddress = 0;
        return false;
    }

    // A missing or unpaired lo/hi register is not an error: the retail library
    // leaves absent registers untouched, unlike the create-time patch which
    // requires them.
    private static bool PatchFusedProgramAddress(
        CpuContext ctx,
        ulong registersAddress,
        int registerCount,
        uint loRegisterOffset,
        ulong codeAddress)
    {
        if (!TryFindShaderRegister(ctx, registersAddress, registerCount, loRegisterOffset, 0, out var loEntry))
        {
            TraceAgc($"agc.fuse_shader_halves.pgm_absent lo=0x{loRegisterOffset:X} regs=0x{registersAddress:X16}");
            return true;
        }

        var hiEntry = loEntry + 8;
        if (hiEntry >= registersAddress + (ulong)registerCount * 8 ||
            !TryReadUInt32(ctx, hiEntry, out var hiOffset) ||
            hiOffset != loRegisterOffset + 1)
        {
            TraceAgc($"agc.fuse_shader_halves.pgm_unpaired lo=0x{loRegisterOffset:X} regs=0x{registersAddress:X16}");
            return true;
        }

        if (!TryReadUInt32(ctx, hiEntry + sizeof(uint), out var hiValue))
        {
            return false;
        }

        return TryWriteUInt32(ctx, loEntry + sizeof(uint), (uint)(codeAddress >> 8)) &&
               TryWriteUInt32(ctx, hiEntry + sizeof(uint), (hiValue & 0xFFFF_FF00u) | (uint)((codeAddress >> 40) & 0xFFUL));
    }

    private static bool TryWriteByte(CpuContext ctx, ulong address, byte value)
    {
        Span<byte> buffer = [value];
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool RelocatePointerField(CpuContext ctx, ulong fieldAddress)
    {
        if (!TryReadUInt64(ctx, fieldAddress, out var relativeAddress))
        {
            return false;
        }

        if (relativeAddress == 0)
        {
            return true;
        }

        return TryWriteUInt64(ctx, fieldAddress, fieldAddress + relativeAddress);
    }

    private static int ReturnRegisterDefaults(CpuContext ctx, bool internalDefaults)
    {
        var version = (uint)ctx[CpuRegister.Rdi];
        if (!IsSupportedRegisterDefaultsVersion(version))
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryGetRegisterDefaultsAllocation(ctx, out var allocation))
        {
            return ReturnPointer(ctx, 0);
        }

        var address = internalDefaults ? allocation.Internal : allocation.Primary;
        TraceAgc($"agc.get_register_defaults internal={internalDefaults} version={version} address=0x{address:X16}");
        return ReturnPointer(ctx, address);
    }

    private static bool IsSupportedRegisterDefaultsVersion(uint version)
    {
        return version is
            RegisterDefaultsVersion7 or
            RegisterDefaultsVersion8 or
            RegisterDefaultsVersion10 or
            RegisterDefaultsVersion11 or
            RegisterDefaultsVersion13;
    }

    private static bool TryGetRegisterDefaultsAllocation(
        CpuContext ctx,
        out RegisterDefaultsAllocation allocation)
    {
        lock (_registerDefaultsGate)
        {
            if (_registerDefaultsAllocations.TryGetValue(ctx.Memory, out allocation!))
            {
                return true;
            }

            if (!TryBuildRegisterDefaults(
                    ctx,
                    PrimaryRegisterDefaults,
                    cxTableLength: 78,
                    shTableLength: 29,
                    ucTableLength: 20,
                    out var primaryAddress) ||
                !TryBuildRegisterDefaults(
                    ctx,
                    InternalRegisterDefaults,
                    cxTableLength: 4,
                    shTableLength: 15,
                    ucTableLength: 3,
                    out var internalAddress))
            {
                allocation = null!;
                return false;
            }

            allocation = new RegisterDefaultsAllocation(primaryAddress, internalAddress);
            _registerDefaultsAllocations.Add(ctx.Memory, allocation);
            return true;
        }
    }

    private static bool TryBuildRegisterDefaults(
        CpuContext ctx,
        RegisterDefaultGroup[] groups,
        int cxTableLength,
        int shTableLength,
        int ucTableLength,
        out ulong address)
    {
        var cxTableOffset = AlignUp(RegisterDefaultsSize, sizeof(ulong));
        var shTableOffset = cxTableOffset + (cxTableLength * sizeof(ulong));
        var ucTableOffset = shTableOffset + (shTableLength * sizeof(ulong));
        var typesOffset = AlignUp(ucTableOffset + (ucTableLength * sizeof(ulong)), sizeof(uint));
        var registerBlocksOffset = AlignUp(typesOffset + (groups.Length * 3 * sizeof(uint)), sizeof(ulong));
        var blobLength = registerBlocksOffset + (groups.Length * RegisterDefaultBlockSize);

        if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, (ulong)blobLength, 0x1000, out address))
        {
            return false;
        }

        var blob = new byte[blobLength];
        WriteBlobUInt64(blob, 0x00, address + (ulong)cxTableOffset);
        WriteBlobUInt64(blob, 0x08, address + (ulong)shTableOffset);
        WriteBlobUInt64(blob, 0x10, address + (ulong)ucTableOffset);
        WriteBlobUInt64(blob, 0x30, address + (ulong)typesOffset);
        WriteBlobUInt32(blob, 0x38, (uint)groups.Length);

        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex];
            if (group.Registers.Length > 16)
            {
                return false;
            }

            var tableOffset = group.Space switch
            {
                0 => cxTableOffset,
                1 => shTableOffset,
                2 => ucTableOffset,
                _ => -1,
            };
            var tableLength = group.Space switch
            {
                0 => cxTableLength,
                1 => shTableLength,
                2 => ucTableLength,
                _ => 0,
            };
            if (tableOffset < 0 || group.Index >= tableLength)
            {
                return false;
            }

            var registerBlockOffset = registerBlocksOffset + (groupIndex * RegisterDefaultBlockSize);
            WriteBlobUInt64(
                blob,
                tableOffset + ((int)group.Index * sizeof(ulong)),
                address + (ulong)registerBlockOffset);

            var typeEntryOffset = typesOffset + (groupIndex * 3 * sizeof(uint));
            WriteBlobUInt32(blob, typeEntryOffset, group.Type);
            WriteBlobUInt32(blob, typeEntryOffset + sizeof(uint), (group.Index * 4) + group.Space);

            for (var registerIndex = 0; registerIndex < group.Registers.Length; registerIndex++)
            {
                var register = group.Registers[registerIndex];
                var registerOffset = registerBlockOffset + (registerIndex * 2 * sizeof(uint));
                WriteBlobUInt32(blob, registerOffset, register.Offset);
                WriteBlobUInt32(blob, registerOffset + sizeof(uint), register.Value);
            }
        }

        return ctx.Memory.TryWrite(address, blob);
    }

    private static int AlignUp(int value, int alignment) =>
        (value + alignment - 1) & -alignment;

    private static void WriteBlobUInt32(Span<byte> blob, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(blob[offset..], value);

    private static void WriteBlobUInt64(Span<byte> blob, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(blob[offset..], value);

    private static int ReturnPointer(CpuContext ctx, ulong pointer)
    {
        ctx[CpuRegister.Rax] = pointer;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(int)result);
        return (int)result;
    }

    private static uint Pm4(uint lengthDwords, uint op, uint register) =>
        0xC0000000u |
        ((((ushort)lengthDwords - 2u) & 0x3FFFu) << 16) |
        ((op & 0xFFu) << 8) |
        ((register & 0x3Fu) << 2);

    private static uint EncodeWaitRegMemPoll(uint pollCycles) =>
        Math.Min(pollCycles >> 4, 0xFFFFu);

    private static uint EncodeWaitRegMem32Control(uint compareFunction, uint operation, uint cachePolicy) =>
        0x10u |
        (compareFunction & 0x7u) |
        ((operation & 0x3u) << 8) |
        ((operation & 0xCu) << 4) |
        ((cachePolicy & 0x3u) << 25);

    private static uint EncodeWaitRegMem64Control(uint compareFunction, uint operation, uint cachePolicy) =>
        0x10u |
        (compareFunction & 0x7u) |
        ((operation & 0x1u) << 8) |
        ((operation & 0x6u) << 5) |
        ((cachePolicy & 0x3u) << 25);

    private static uint Pm4Length(uint header) =>
        ((header >> 16) & 0x3FFFu) + 2u;

    private static bool TryReadByte(CpuContext ctx, ulong address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = buffer[0];
        return true;
    }

    // A submitted command buffer is bulk-copied once per submit and served
    // from this thread-local window: the previous per-dword reads each took
    // the guest-memory reader lock and ran a region binary search, which
    // dominated submit parsing (thousands of locked 4-byte reads per DCB).
    [ThreadStatic]
    private static byte[]? _dcbWindowBuffer;
    [ThreadStatic]
    private static ulong _dcbWindowStart;
    [ThreadStatic]
    private static int _dcbWindowByteLength;

    /// <summary>
    /// Drops the bulk-read window when a self-patching command buffer writes
    /// into its own bytes during parse, so subsequent reads see live guest
    /// memory instead of the pre-write snapshot. Self-patching is rare, so
    /// paying live-read cost for the rest of that one submit is acceptable.
    /// </summary>
    private static void InvalidateDcbWindowIfOverlaps(ulong address, ulong length)
    {
        if (_dcbWindowBuffer is null || length == 0)
        {
            return;
        }

        var windowEnd = _dcbWindowStart + (ulong)_dcbWindowByteLength;
        if (address < windowEnd && address + length > _dcbWindowStart)
        {
            _dcbWindowBuffer = null;
            _dcbWindowByteLength = 0;
        }
    }

    private static bool TryReadUInt16(CpuContext ctx, ulong address, out ushort value)
    {
        if (_dcbWindowBuffer is { } window &&
            address >= _dcbWindowStart &&
            address - _dcbWindowStart + sizeof(ushort) <= (ulong)_dcbWindowByteLength)
        {
            value = BinaryPrimitives.ReadUInt16LittleEndian(
                window.AsSpan((int)(address - _dcbWindowStart)));
            return true;
        }

        Span<byte> buffer = stackalloc byte[sizeof(ushort)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        return true;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        if (_dcbWindowBuffer is { } window &&
            address >= _dcbWindowStart &&
            address - _dcbWindowStart + sizeof(uint) <= (ulong)_dcbWindowByteLength)
        {
            value = BinaryPrimitives.ReadUInt32LittleEndian(
                window.AsSpan((int)(address - _dcbWindowStart)));
            return true;
        }

        return KernelMemoryCompatExports.TryReadUInt32Compat(ctx, address, out value);
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        return KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, address, value);
    }

    private static bool TryReadUInt64(CpuContext ctx, ulong address, out ulong value)
    {
        if (_dcbWindowBuffer is { } window &&
            address >= _dcbWindowStart &&
            address - _dcbWindowStart + sizeof(ulong) <= (ulong)_dcbWindowByteLength)
        {
            value = BinaryPrimitives.ReadUInt64LittleEndian(
                window.AsSpan((int)(address - _dcbWindowStart)));
            return true;
        }

        return KernelMemoryCompatExports.TryReadUInt64Compat(ctx, address, out value);
    }

    private static bool TryWriteUInt64(CpuContext ctx, ulong address, ulong value) =>
        KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, address, value);

    private static bool TryReadGuestCString(
        CpuContext ctx,
        ulong address,
        int maximumLength,
        out byte[] bytes)
    {
        if (address == 0)
        {
            bytes = [];
            return true;
        }

        var values = new List<byte>(Math.Min(maximumLength, 128));
        for (var index = 0; index < maximumLength; index++)
        {
            if (!TryReadByte(ctx, address + (ulong)index, out var value))
            {
                bytes = [];
                return false;
            }

            if (value == 0)
            {
                bytes = [.. values];
                return true;
            }

            values.Add(value);
        }

        bytes = [];
        return false;
    }

    private static bool TryGetPacketIdentity(
        CpuContext ctx,
        ulong commandAddress,
        out uint op,
        out uint register)
    {
        op = 0;
        register = 0;
        if (commandAddress == 0 || !TryReadUInt32(ctx, commandAddress, out var header))
        {
            return false;
        }

        op = (header >> 8) & 0xFFu;
        register = (header >> 2) & 0x3Fu;
        return true;
    }

    private static bool TryCopyGuestMemory(
        CpuContext ctx,
        ulong sourceAddress,
        ulong destinationAddress,
        uint byteCount)
    {
        if (sourceAddress == destinationAddress)
        {
            return true;
        }

        var buffer = new byte[Math.Min(byteCount, 64u * 1024u)];
        ulong offset = 0;
        while (offset < byteCount)
        {
            var chunkLength = (int)Math.Min((ulong)buffer.Length, byteCount - offset);
            var chunk = buffer.AsSpan(0, chunkLength);
            if (!ctx.Memory.TryRead(sourceAddress + offset, chunk) ||
                !ctx.Memory.TryWrite(destinationAddress + offset, chunk))
            {
                return false;
            }

            offset += (uint)chunkLength;
        }

        return true;
    }

    private static bool TryFillGuestMemory(
        CpuContext ctx,
        uint value,
        ulong destinationAddress,
        uint byteCount)
    {
        var buffer = new byte[Math.Min(byteCount, 64u * 1024u)];
        Span<byte> encoded = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded, value);
        for (var offset = 0; offset < buffer.Length; offset += sizeof(uint))
        {
            var remaining = Math.Min(sizeof(uint), buffer.Length - offset);
            encoded[..remaining].CopyTo(buffer.AsSpan(offset, remaining));
        }

        ulong destinationOffset = 0;
        while (destinationOffset < byteCount)
        {
            var chunkLength = (int)Math.Min(
                (ulong)buffer.Length,
                byteCount - destinationOffset);
            if (!ctx.Memory.TryWrite(
                    destinationAddress + destinationOffset,
                    buffer.AsSpan(0, chunkLength)))
            {
                return false;
            }

            destinationOffset += (uint)chunkLength;
        }

        return true;
    }

    private static bool ShouldTraceHotPath(ref long counter)
    {
        var count = Interlocked.Increment(ref counter);
        return count <= 8 || count % 100_000 == 0;
    }

    // Interpolated-string handlers gated on the trace flags: when tracing is
    // off (the normal case) the compiler skips every AppendFormatted call, so
    // the interpolation never runs. These functions are on the hottest guest
    // paths — e.g. AddIndirectPatchRegisters fires tens of thousands of times
    // per second — and previously formatted a discarded string every call.
    [System.Runtime.CompilerServices.InterpolatedStringHandler]
    private ref struct AgcTraceHandler
    {
        private System.Runtime.CompilerServices.DefaultInterpolatedStringHandler _inner;
        private readonly bool _enabled;

        public AgcTraceHandler(int literalLength, int formattedCount, out bool shouldAppend)
        {
            _enabled = _traceAgc;
            shouldAppend = _enabled;
            _inner = _enabled
                ? new System.Runtime.CompilerServices.DefaultInterpolatedStringHandler(literalLength, formattedCount)
                : default;
        }

        public void AppendLiteral(string value) => _inner.AppendLiteral(value);
        public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
        public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);
        public string ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : string.Empty;
    }

    [System.Runtime.CompilerServices.InterpolatedStringHandler]
    private ref struct AgcShaderTraceHandler
    {
        private System.Runtime.CompilerServices.DefaultInterpolatedStringHandler _inner;
        private readonly bool _enabled;

        public AgcShaderTraceHandler(int literalLength, int formattedCount, out bool shouldAppend)
        {
            _enabled = _traceAgcShader;
            shouldAppend = _enabled;
            _inner = _enabled
                ? new System.Runtime.CompilerServices.DefaultInterpolatedStringHandler(literalLength, formattedCount)
                : default;
        }

        public void AppendLiteral(string value) => _inner.AppendLiteral(value);
        public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
        public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);
        public string ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : string.Empty;
    }

    // Monotonic seconds since process start, prefixed on every AGC trace
    // line — the frame pipeline's dependency chains span tens of seconds.
    private static readonly long _traceStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();

    private static string TraceSeconds() =>
        ((System.Diagnostics.Stopwatch.GetTimestamp() - _traceStartTicks) /
         (double)System.Diagnostics.Stopwatch.Frequency).ToString(
            "F3", System.Globalization.CultureInfo.InvariantCulture);

    private static void TraceAgc(
        [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument] ref AgcTraceHandler message)
    {
        if (_traceAgc)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] t={TraceSeconds()} {message.ToStringAndClear()}");
        }
    }

    private static void TraceAgc(string message)
    {
        if (!_traceAgc)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] t={TraceSeconds()} {message}");
    }

    internal static bool TryCreateComputeShaderCompatibilityDiagnostic(
        ISet<ulong> tracedComputeShaders,
        ulong shaderAddress,
        string error,
        out string diagnostic)
    {
        if (!tracedComputeShaders.Add(shaderAddress))
        {
            diagnostic = string.Empty;
            return false;
        }

        diagnostic = $"[COMPAT][SHADER] cs=0x{shaderAddress:X16} error={error}";
        return true;
    }

    private static void TraceAgcShader(
        [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument] ref AgcShaderTraceHandler message)
    {
        if (_traceAgcShader)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] t={TraceSeconds()} {message.ToStringAndClear()}");
        }
    }

    private static void TraceAgcShader(string message)
    {
        if (!_traceAgcShader)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] t={TraceSeconds()} {message}");
    }

    private static string FormatShaderDwords(IReadOnlyList<uint> values) =>
        values.Count == 0
            ? "none"
            : string.Join(',', values.Select(static value => $"{value:X8}"));

    private static string FormatTextureDescriptor(TextureDescriptor descriptor) =>
        $"addr=0x{descriptor.Address:X16} {descriptor.Width}x{descriptor.Height} " +
        $"fmt={descriptor.Format} num={descriptor.NumberType} tile={descriptor.TileMode} " +
        $"type={descriptor.Type} depth={descriptor.Depth} base_array={descriptor.BaseArray} " +
        $"levels={descriptor.BaseLevel}-{descriptor.LastLevel}/max{descriptor.MaxMip} " +
        $"pitch={descriptor.Pitch} array_pitch={descriptor.ArrayPitch} " +
        $"lod={descriptor.MinLod:X3}/{descriptor.MinLodWarn:X3} " +
        $"bc={descriptor.BcSwizzle} meta=0x{descriptor.MetadataAddress:X16} " +
        $"flags=0x{descriptor.DescriptorFlags:X6} dst=0x{descriptor.DstSelect:X3}";

    private static int? ParseOptionalPositiveInt(string? value) =>
        int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : null;

    private static int[] ParseNonnegativeInts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var values = new List<int>();
        foreach (var item in value.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(item, out var parsed) || parsed < 0)
            {
                return [];
            }

            values.Add(parsed);
        }

        return values.ToArray();
    }

    private static ulong[] ParseHexAddresses(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Select(ParseOptionalHexAddress)
            .Where(static address => address.HasValue)
            .Select(static address => address!.Value)
            .Distinct()
            .ToArray();
    }

    private static (ulong Address, ulong Length)[] ParseHexRanges(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var ranges = new List<(ulong Address, ulong Length)>();
        foreach (var item in value.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            var separator = item.IndexOf(':');
            if (separator <= 0 || separator == item.Length - 1)
            {
                continue;
            }

            var address = ParseOptionalHexAddress(item[..separator]);
            var length = ParseOptionalHexAddress(item[(separator + 1)..]);
            if (address is { } parsedAddress && length is > 0)
            {
                ranges.Add((parsedAddress, length.Value));
            }
        }
        return ranges.Distinct().ToArray();
    }

    private static bool ShouldTraceGuestMemoryRange(ulong address, ulong length) =>
        length != 0 && _traceGuestMemoryRanges.Any(range =>
            MemoryRangesOverlap(address, length, range.Address, range.Length));

    private static bool MemoryRangesOverlap(
        ulong firstAddress,
        ulong firstLength,
        ulong secondAddress,
        ulong secondLength)
    {
        if (firstLength == 0 || secondLength == 0)
        {
            return false;
        }

        var firstEnd = firstLength > ulong.MaxValue - firstAddress
            ? ulong.MaxValue
            : firstAddress + firstLength;
        var secondEnd = secondLength > ulong.MaxValue - secondAddress
            ? ulong.MaxValue
            : secondAddress + secondLength;
        return firstAddress < secondEnd && secondAddress < firstEnd;
    }

    private static ulong? ParseOptionalHexAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var span = value.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return ulong.TryParse(
            span,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out var address)
            ? address
            : null;
    }

    private static void DumpCompiledShader(
        string stage,
        ulong shaderAddress,
        ulong stateFingerprint,
        IGuestCompiledShader shader,
        Gen5ShaderProgram program)
    {
        if (shader.Payload.Length == 0 || !_dumpSpirv)
        {
            return;
        }

        var addressFilter = _dumpSpirvAddress;
        if (_dumpSpirvAddresses.Length > 0)
        {
            if (!_dumpSpirvAddresses.Contains(shaderAddress))
            {
                return;
            }
        }
        else if (!string.IsNullOrWhiteSpace(addressFilter))
        {
            var span = addressFilter.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            if (!ulong.TryParse(
                    span,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var filteredAddress) ||
                shaderAddress != filteredAddress)
            {
                return;
            }
        }

        var directory = Path.Combine(AppContext.BaseDirectory, "shader-dumps");
        Directory.CreateDirectory(directory);
        var name = $"{shaderAddress:X16}-{stateFingerprint:X16}.{stage}";
        File.WriteAllBytes(
            Path.Combine(directory, $"{name}.{shader.PayloadFileExtension}"),
            shader.Payload);

        var lines = new List<string>(program.Instructions.Count + 2)
        {
            $"address=0x{program.Address:X16}",
            "pc words opcode destinations <- sources control",
        };
        foreach (var instruction in program.Instructions)
        {
            lines.Add(
                $"0x{instruction.Pc:X4} " +
                $"{string.Join('_', instruction.Words.Select(static word => $"{word:X8}"))} " +
                $"{instruction.Opcode} " +
                $"{string.Join(',', instruction.Destinations)} <- " +
                $"{string.Join(',', instruction.Sources)} " +
                $"{instruction.Control}");
        }

        File.WriteAllLines(Path.Combine(directory, $"{name}.ir.txt"), lines);
    }

    /// <summary>
    /// Captures a bounded raw guest shader window for an explicitly requested
    /// program. Both the output directory and an exact address allow-list are
    /// required so a broad shader trace cannot accidentally dump every program
    /// in a title.
    /// </summary>
    private static void DumpShaderProgramIfRequested(
        CpuContext ctx,
        string stage,
        ulong shaderAddress,
        ulong shaderHeaderAddress,
        string error)
    {
        if (string.IsNullOrWhiteSpace(_dumpShaderProgramDirectory) ||
            !_dumpShaderProgramAddresses.Contains(shaderAddress) ||
            !_dumpedShaderPrograms.TryAdd((stage, shaderAddress), 0))
        {
            return;
        }

        const int maximumBytes = 64 * 1024;
        const int readChunkBytes = 4 * 1024;
        var bytes = new byte[maximumBytes];
        var bytesRead = 0;
        while (bytesRead < bytes.Length)
        {
            var chunkLength = Math.Min(readChunkBytes, bytes.Length - bytesRead);
            if (!ctx.Memory.TryRead(
                    shaderAddress + (ulong)bytesRead,
                    bytes.AsSpan(bytesRead, chunkLength)))
            {
                break;
            }

            bytesRead += chunkLength;
        }

        if (bytesRead == 0)
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.shader_program_dump stage={stage} " +
                $"addr=0x{shaderAddress:X16} error=read-failed");
            return;
        }

        Array.Resize(ref bytes, bytesRead);
        try
        {
            Directory.CreateDirectory(_dumpShaderProgramDirectory);
            var name = $"{shaderAddress:X16}.{stage}";
            var programPath = Path.Combine(_dumpShaderProgramDirectory, $"{name}.bin");
            File.WriteAllBytes(programPath, bytes);

            var dwordCount = bytes.Length / sizeof(uint);
            var endProgramOffsets = new List<int>();
            var nonzeroDwords = 0;
            for (var index = 0; index < dwordCount; index++)
            {
                var word = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(index * sizeof(uint), sizeof(uint)));
                if (word != 0)
                {
                    nonzeroDwords++;
                }

                if ((word & 0xFFFF0000u) == 0xBF810000u)
                {
                    endProgramOffsets.Add(index * sizeof(uint));
                }
            }

            static string DescribeDwords(byte[] source, int startDword, int count)
            {
                var available = Math.Max(0, source.Length / sizeof(uint) - startDword);
                return string.Join(
                    ' ',
                    Enumerable.Range(startDword, Math.Min(count, available))
                        .Select(index => BinaryPrimitives.ReadUInt32LittleEndian(
                            source.AsSpan(index * sizeof(uint), sizeof(uint))))
                        .Select(word => $"{word:X8}"));
            }

            var tailStart = Math.Max(0, dwordCount - 32);
            var declaredShaderSize = 0u;
            if (shaderHeaderAddress != 0)
            {
                _ = TryReadUInt32(
                    ctx,
                    shaderHeaderAddress + ShaderSizeOffset,
                    out declaredShaderSize);
            }

            var metadata = new List<string>
            {
                $"stage={stage}",
                $"address=0x{shaderAddress:X16}",
                $"header=0x{shaderHeaderAddress:X16}",
                $"declared_shader_size=0x{declaredShaderSize:X}",
                $"error={error}",
                $"bytes={bytes.Length}",
                $"dwords={dwordCount}",
                $"nonzero_dwords={nonzeroDwords}",
                $"sha256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))}",
                $"s_endpgm_offsets={string.Join(',', endProgramOffsets.Select(offset => $"0x{offset:X}"))}",
                $"head={DescribeDwords(bytes, 0, 32)}",
                $"tail={DescribeDwords(bytes, tailStart, 32)}",
            };
            File.WriteAllLines(
                Path.Combine(_dumpShaderProgramDirectory, $"{name}.txt"),
                metadata);

            if (shaderHeaderAddress != 0)
            {
                var headerBytes = new byte[128];
                if (ctx.Memory.TryRead(shaderHeaderAddress, headerBytes))
                {
                    File.WriteAllBytes(
                        Path.Combine(_dumpShaderProgramDirectory, $"{name}.header.bin"),
                        headerBytes);
                }
            }

            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.shader_program_dump stage={stage} " +
                $"addr=0x{shaderAddress:X16} header=0x{shaderHeaderAddress:X16} " +
                $"bytes={bytes.Length} nonzero_dw={nonzeroDwords}/{dwordCount} " +
                $"endpgm={endProgramOffsets.Count} path={programPath}");
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.shader_program_dump stage={stage} " +
                $"addr=0x{shaderAddress:X16} error={exception.GetType().Name}");
        }
    }

    private static void TraceCreateShader(ulong destinationAddress, ulong headerAddress, ulong codeAddress, string detail)
    {
        var isOk = string.Equals(detail, "ok", StringComparison.Ordinal);
        if (isOk &&
            (!_traceAgc || !ShouldTraceHotPath(ref _createShaderTraceCount)))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.create_shader dst=0x{destinationAddress:X16} header=0x{headerAddress:X16} code=0x{codeAddress:X16} {detail}");
    }

    // Firmware 12.70 libSceAgc.sprx SHA-256
    // 110df81f759ae3dffcc9b5e3fa062c74058518631847641b8e08a54f6b8b6e2d:
    // -vnlTPPXPrw at 0xce50 and ewobAQeMo5k at 0xd160 return 0x20
    // in the non-Trinity mode exposed by GetIsTrinityMode.
    [SysAbiExport(
        Nid = "-vnlTPPXPrw",
        ExportName = "sceAgcDcbAcquireMemGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc",
        PreferLle = true)]
    public static int DcbAcquireMemGetSize(CpuContext ctx) => ReturnAgcSize(ctx, 0x20);

    [SysAbiExport(
        Nid = "ewobAQeMo5k",
        ExportName = "sceAgcAcbAcquireMemGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc",
        PreferLle = true)]
    public static int AcbAcquireMemGetSize(CpuContext ctx) => ReturnAgcSize(ctx, 0x20);

    // t7PlZ9nt5Lc at 0xcd90 is `lea eax,[rdi*4]; ret`.
    [SysAbiExport(
        Nid = "t7PlZ9nt5Lc",
        ExportName = "sceAgcCbNopGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc",
        PreferLle = true)]
    public static int CbNopGetSize(CpuContext ctx) =>
        ReturnAgcSize(ctx, unchecked((uint)ctx[CpuRegister.Rdi] * sizeof(uint)));

    // hL7C0IRpWZI at 0xcda0 is `mov eax,0x20; ret`.
    [SysAbiExport(
        Nid = "hL7C0IRpWZI",
        ExportName = "sceAgcCbQueueEndOfPipeActionGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc",
        PreferLle = true)]
    public static int CbQueueEndOfPipeActionGetSize(CpuContext ctx) => ReturnAgcSize(ctx, 0x20);

    // QIXCsbipds0 at 0xd0d0 is `mov eax,8; ret`.
    [SysAbiExport(
        Nid = "QIXCsbipds0",
        ExportName = "sceAgcDcbRewindGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc",
        PreferLle = true)]
    public static int DcbRewindGetSize(CpuContext ctx) => ReturnAgcSize(ctx, 8);

    // VEGu4dixjUg at 0xcec0 is exactly `mov eax, 0x10; ret`.
    [SysAbiExport(
        Nid = "VEGu4dixjUg",
        ExportName = "sceAgcDcbJumpGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc",
        PreferLle = true)]
    public static int DcbJumpGetSize(CpuContext ctx) => ReturnAgcSize(ctx, 0x10);

    private static int ReturnAgcSize(CpuContext ctx, uint size)
    {
        ctx[CpuRegister.Rax] = size;
        return unchecked((int)size);
    }

    [SysAbiExport(
        Nid = "xSAR0LTcRKM",
        ExportName = "sceAgcDcbJump",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbJump(CpuContext ctx)
    {
        var dcb = ctx[CpuRegister.Rdi];
        var target = ctx[CpuRegister.Rsi];
        var sizeDwords = (uint)ctx[CpuRegister.Rdx];
        if (dcb == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, dcb, 4, out var cmd) ||
            !ctx.TryWriteUInt32(cmd, Pm4(4, ItIndirectBuffer, RZero)) ||
            !ctx.TryWriteUInt32(cmd + 4, (uint)(target & 0xFFFF_FFFFUL)) ||
            !ctx.TryWriteUInt32(cmd + 8, (uint)((target >> 32) & 0xFFFFUL)) ||
            !ctx.TryWriteUInt32(cmd + 12, sizeDwords & 0xFFFFF))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, cmd);
    }

    // Matches the 4-dword INDIRECT_BUFFER packet CbBranch writes below.
    [SysAbiExport(
        Nid = "uZW-mqsxkrM",
        ExportName = "sceAgcCbBranchGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbBranchGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 4u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    // COND_EXEC gates the following execCount dwords on a 32-bit predicate in
    // memory. Keep the upstream HLE builder available to focused callers, but do
    // not register the NID twice: AgcLleExports owns BIPexNBSGog and prefers the
    // exact guest provider recovered by Acelogic/Ghidra.
    public static int DcbCondExec(CpuContext ctx)
    {
        var dcb = ctx[CpuRegister.Rdi];
        var predicateAddress = ctx[CpuRegister.Rsi];
        var execCountDwords = (uint)ctx[CpuRegister.Rdx];
        if (dcb == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, dcb, 5, out var cmd) ||
            !ctx.TryWriteUInt32(cmd, Pm4(5, ItCondExec, RZero)) ||
            !ctx.TryWriteUInt32(cmd + 4, (uint)(predicateAddress & 0xFFFF_FFFFUL)) ||
            !ctx.TryWriteUInt32(cmd + 8, (uint)(predicateAddress >> 32)) ||
            !ctx.TryWriteUInt32(cmd + 12, 0) ||
            !ctx.TryWriteUInt32(cmd + 16, execCountDwords & 0x3FFF))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, cmd);
    }

    // How a title continues a frame whose command arena filled: it branches from
    // the tail of the exhausted buffer into a fresh one and submits only the first
    // buffer, leaving the driver to follow the link. Dropping this packet strands
    // everything written after the switch -- for UE 4.27 that is the rest of the
    // frame, including its flip and the end-of-frame labels the guest's AGC
    // interrupt thread needs before it will trigger the backbuffer event.
    //
    // The branch target and its length arrive on the stack, past six register
    // arguments (verified against a live call: the values matched the continuation
    // buffer the title had already written into).
    [SysAbiExport(
        Nid = "w1KFAHVqpaU",
        ExportName = "sceAgcCbBranch",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbBranch(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryReadUInt64(ctx, ctx[CpuRegister.Rsp] + (2 * sizeof(ulong)), out var target) ||
            !TryReadUInt64(ctx, ctx[CpuRegister.Rsp] + (3 * sizeof(ulong)), out var targetDwords))
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItIndirectBuffer, RZero)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)(target & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)((target >> 32) & 0xFFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)targetDwords & 0xFFFFFu))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.cb_branch buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"target=0x{target:X16} dwords={targetDwords}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "b-oySn+G2tE",
        ExportName = "sceAgcAcbJumpGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbJumpGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 4u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "e1DFTg+Sd8U",
        ExportName = "sceAgcAcbJump",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbJump(CpuContext ctx) => DcbJump(ctx);

    // Sony SetCf* range writer — SET_CONTEXT_REG packet (same shape as SH range).
    [SysAbiExport(
        Nid = "BVFg3CWU6Eo",
        ExportName = "sceAgcDcbSetCfRegisterRangeDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetCfRegisterRangeDirect(CpuContext ctx) =>
        DcbSetRegisterRangeDirect(ctx, ItSetContextReg, "cf");

    // Logged unresolved as LHFXRrlTPD8 during North Yankton load.
    [SysAbiExport(
        Nid = "LHFXRrlTPD8",
        ExportName = "sceAgcDcbSetCxRegisterDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetCxRegisterDirect(CpuContext ctx) =>
        DcbSetRegisterDirect(ctx, ItSetContextReg, "cx");

    private static int DcbSetRegisterRangeDirect(CpuContext ctx, uint op, string registerSpace)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var offset = (uint)ctx[CpuRegister.Rsi];
        var valuesAddress = ctx[CpuRegister.Rdx];
        var valueCount = (uint)ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0 || valueCount == 0 || valueCount > 0x3FFE)
        {
            return ReturnPointer(ctx, 0);
        }

        var packetDwords = valueCount + 2;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, op, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, offset & 0xFFFFu))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint i = 0; i < valueCount; i++)
        {
            var value = 0u;
            if (valuesAddress != 0 &&
                !TryReadUInt32(ctx, valuesAddress + (i * sizeof(uint)), out value))
            {
                return ReturnPointer(ctx, 0);
            }

            if (!TryWriteUInt32(ctx, commandAddress + 8 + (i * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        TraceAgc(
            $"agc.dcb_set_{registerSpace}_range buf=0x{commandBufferAddress:X16} " +
            $"cmd=0x{commandAddress:X16} offset=0x{offset:X4} count={valueCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "bbFueFP+J4k",
        ExportName = "sceAgcDcbSetPredication",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetPredication(CpuContext ctx)
    {
        var dcb = ctx[CpuRegister.Rdi];
        var condition = (uint)(ctx[CpuRegister.Rsi] & 1u);
        var operation = (uint)(ctx[CpuRegister.Rdx] & 0x7u);
        var waitOperation = (uint)(ctx[CpuRegister.Rcx] & 1u);
        var address = ctx[CpuRegister.R8];
        if (dcb == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var flags = (condition << 8) | (waitOperation << 12) | (operation << 16);
        if (!TryAllocateCommandDwords(ctx, dcb, 4, out var cmd) ||
            !ctx.TryWriteUInt32(cmd, Pm4(4, ItSetPredication, RZero)) ||
            !ctx.TryWriteUInt32(cmd + 4, flags) ||
            !ctx.TryWriteUInt32(cmd + 8, (uint)address & 0xFFFF_FFF0u) ||
            !ctx.TryWriteUInt32(cmd + 12, (uint)(address >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, cmd);
    }

    [SysAbiExport(
        Nid = "w6Dj1VJt5qY",
        ExportName = "sceAgcSetPacketPredication",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetPacketPredication(CpuContext ctx)
    {
        var packetAddress = ctx[CpuRegister.Rdi];
        var predication = ctx[CpuRegister.Rsi];
        if (packetAddress == 0 || !TryReadUInt32(ctx, packetAddress, out var header))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        header = (header & ~1u) | (predication == 1 ? 1u : 0u);
        return !ctx.TryWriteUInt32(packetAddress, header)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // ABI (reversed from Quake): rdi = array of DCB base addresses (u64 each),
    // rsi = array of DCB sizes in dwords (u32 each), rdx = buffer count.
    [SysAbiExport(
        Nid = "6UzEidRZwkg",
        ExportName = "sceAgcDriverSubmitMultiDcbs",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitMultiDcbs(CpuContext ctx)
    {
        Interlocked.Increment(ref _dcbSubmitCount);
        Volatile.Write(ref _lastDcbSubmitTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());

        var addressArray = ctx[CpuRegister.Rdi];
        var sizeArray = ctx[CpuRegister.Rsi];
        var bufferCount = (uint)ctx[CpuRegister.Rdx];
        if (addressArray == 0 || sizeArray == 0 || bufferCount == 0 || bufferCount > 4096)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var tracePackets = _traceAgc;

        var gpuState = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (gpuState.Gate)
        {
            Gen5ShaderScalarEvaluator.BeginGlobalMemoryReadScope();
            try
            {
                for (uint i = 0; i < bufferCount; i++)
                {
                    if (!ctx.TryReadUInt64(addressArray + i * 8, out var commandAddress) ||
                        commandAddress == 0 ||
                        !ctx.TryReadUInt32(sizeArray + i * 4, out var dwordCount) ||
                        dwordCount == 0)
                    {
                        continue;
                    }

                    if (tracePackets)
                    {
                        TraceAgc(
                            $"agc.driver_submit_multi_dcbs index={i}/{bufferCount} " +
                            $"addr=0x{commandAddress:X16} dwords={dwordCount}");
                    }

                    ParseSubmittedDcb(ctx, gpuState, gpuState.Graphics, commandAddress, dwordCount, tracePackets);
                }

                DrainResumableDcbs(ctx, gpuState, tracePackets);
            }
            finally
            {
                Gen5ShaderScalarEvaluator.EndGlobalMemoryReadScope();
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "AOLcoIkQDgM",
        ExportName = "sceAgcDriverQueryResourceRegistrationUserMemoryRequirements",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverQueryResourceRegistrationUserMemoryRequirements(CpuContext ctx)
    {
        var sizeAddress = ctx[CpuRegister.Rdi];
        var resourceCount = ctx[CpuRegister.Rsi];
        var ownerCount = ctx[CpuRegister.Rdx];
        if (sizeAddress == 0 || resourceCount == 0 || ownerCount == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ulong requiredSize;
        try
        {
            requiredSize = checked(
                resourceCount * ResourceRegistrationBytesPerResource +
                ownerCount * ResourceRegistrationBytesPerOwner);
        }
        catch (OverflowException)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryWriteUInt64(sizeAddress, requiredSize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc(
            $"agc.driver_query_resource_registration_memory resources={resourceCount} " +
            $"owners={ownerCount} bytes=0x{requiredSize:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "F0Y42t-3e18",
        ExportName = "sceAgcDriverInitResourceRegistration",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverInitResourceRegistration(CpuContext ctx)
    {
        var memoryAddress = ctx[CpuRegister.Rdi];
        var memorySize = ctx[CpuRegister.Rsi];
        var ownerCount = ctx[CpuRegister.Rdx];
        if (memoryAddress == 0 || memorySize == 0 || ownerCount == 0 || ownerCount > uint.MaxValue)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var state = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (state.Gate)
        {
            state.ResourceRegistrationInitialized = true;
            state.ResourceRegistrationMemory = memoryAddress;
            state.ResourceRegistrationMemorySize = memorySize;
            state.ResourceRegistrationMaxOwners = (uint)ownerCount;
            state.ResourceOwners.Clear();
            state.RegisteredResources.Clear();
            state.DefaultOwner = DefaultAgcOwner;
            state.NextOwner = 1;
            state.NextResource = 1;
        }

        TraceAgc(
            $"agc.driver_init_resource_registration memory=0x{memoryAddress:X16} " +
            $"bytes=0x{memorySize:X} owners={ownerCount}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "U9ueyEhSkF4",
        ExportName = "sceAgcDriverRegisterDefaultOwner",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverRegisterDefaultOwner(CpuContext ctx)
    {
        var owner = (uint)ctx[CpuRegister.Rdi];
        var state = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (state.Gate)
        {
            state.DefaultOwner = owner;
        }

        TraceAgc($"agc.driver_register_default_owner owner={owner}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "X-Nm5KLREeg",
        ExportName = "sceAgcDriverRegisterOwner",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverRegisterOwner(CpuContext ctx)
    {
        // Ghidra 12.1.2_PUBLIC_20260605, developer libSceAgc.sprx
        // provider SHA-256 prefix bc2ca28f, entry RVA 0x71C0. The complete body is
        // `mov eax, 0x8A6C9018; ret`: it reads no arguments, writes no owner,
        // and creates no registration state.
        return ctx.SetReturn(unchecked((int)0x8A6C9018));
    }

    private static int RemoveResourcesForOwner(SubmittedGpuState state, uint owner)
    {
        var stale = new List<uint>();
        foreach (var (handle, resource) in state.RegisteredResources)
        {
            if (resource.Owner == owner)
            {
                stale.Add(handle);
            }
        }

        foreach (var handle in stale)
        {
            state.RegisteredResources.Remove(handle);
        }

        return stale.Count;
    }

    [SysAbiExport(
        Nid = "ZLJk9r2+2Aw",
        ExportName = "sceAgcDriverUnregisterOwnerAndResources",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver",
        PreferLle = true)]
    public static int DriverUnregisterOwnerAndResources(CpuContext ctx)
    {
        var owner = (uint)ctx[CpuRegister.Rdi];
        var state = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        int resources;
        lock (state.Gate)
        {
            if (!state.ResourceOwners.Remove(owner))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            resources = RemoveResourcesForOwner(state, owner);
            state.ComputeQueues.Remove(owner);
        }

        TraceAgc($"agc.driver_unregister_owner owner={owner} resources={resources}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "SCoAN5fYlUM",
        ExportName = "sceAgcDriverUnregisterAllResourcesForOwner",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver",
        PreferLle = true)]
    public static int DriverUnregisterAllResourcesForOwner(CpuContext ctx)
    {
        var owner = (uint)ctx[CpuRegister.Rdi];
        var state = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        int resources;
        lock (state.Gate)
        {
            resources = RemoveResourcesForOwner(state, owner);
        }

        TraceAgc($"agc.driver_unregister_owner_resources owner={owner} resources={resources}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "pWLG7WOpVcw",
        ExportName = "sceAgcDriverUnregisterResource",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverUnregisterResource(CpuContext ctx)
    {
        var resourceHandle = (uint)ctx[CpuRegister.Rdi];
        var state = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (state.Gate)
        {
            if (!state.RegisteredResources.Remove(resourceHandle))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }
        }

        TraceAgc($"agc.driver_unregister_resource handle={resourceHandle}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

}
