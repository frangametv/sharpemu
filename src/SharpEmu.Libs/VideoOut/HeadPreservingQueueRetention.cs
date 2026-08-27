// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

internal static class HeadPreservingQueueRetention
{
    /// <summary>
    /// Bounds a FIFO while preserving its oldest item and the newest tail.
    /// Intermediate items are removed oldest-first so an unconsumed head
    /// cannot be starved by a producer that continuously outruns its consumer.
    /// </summary>
    public static int CoalesceIntermediateItems<T>(
        LinkedList<T> pending,
        int maximumCount,
        Action<T>? onCoalesced = null)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);

        var coalescedCount = 0;
        while (pending.Count > maximumCount)
        {
            var oldestIntermediate = pending.First!.Next;
            System.Diagnostics.Debug.Assert(oldestIntermediate is not null);
            var coalescedItem = oldestIntermediate!.Value;
            pending.Remove(oldestIntermediate!);
            onCoalesced?.Invoke(coalescedItem);
            coalescedCount++;
        }

        return coalescedCount;
    }

    /// <summary>
    /// Takes the oldest ready item and discards older items that it supersedes.
    /// A permanently incomplete FIFO head must not hide newer completed frames.
    /// </summary>
    public static bool TryTakeFirstReady<T>(
        LinkedList<T> pending,
        Predicate<T> isReady,
        out T item,
        Action<T>? onSuperseded = null)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(isReady);

        var ready = pending.First;
        while (ready is not null && !isReady(ready.Value))
        {
            ready = ready.Next;
        }

        if (ready is null)
        {
            item = default!;
            return false;
        }

        while (pending.First != ready)
        {
            var superseded = pending.First!.Value;
            pending.RemoveFirst();
            onSuperseded?.Invoke(superseded);
        }

        item = ready.Value;
        pending.Remove(ready);
        return true;
    }
}

internal static class GuestPresentationScheduling
{
    /// <summary>
    /// A wait-safe marker may be selected before its referenced flip capture.
    /// The pre-#770 presenter treated this marker as advisory; blocking the
    /// logical queue here prevents Astro Bot from reaching its title sequence.
    /// </summary>
    public static bool IsFlipWaitReady(long version, bool captureComplete) => true;

    /// <summary>
    /// An ordered flip has captured an immutable guest-image generation and
    /// enqueued its presentation. Yielding the guest-work drain at this point
    /// gives that FIFO head a presentation opportunity before newer flips can
    /// coalesce intermediate generations behind it.
    /// </summary>
    public static bool ShouldYieldToPresenter(object completedWork)
    {
        ArgumentNullException.ThrowIfNull(completedWork);
        return completedWork is VulkanOrderedGuestFlip;
    }
}
