// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct VulkanHostBufferPoolKey(
    BufferUsageFlags Usage,
    ulong Capacity);

internal readonly record struct VulkanHostBufferAllocation(
    VkBuffer Buffer,
    DeviceMemory Memory,
    VulkanHostBufferPoolKey Key,
    nint Mapped);

internal sealed class VulkanHostBufferPool : IDisposable
{
    private sealed record CachedAllocation(
        VulkanHostBufferAllocation Allocation,
        LinkedListNode<VulkanHostBufferAllocation> BucketNode,
        LinkedListNode<ulong> RecencyNode);

    private readonly object _gate = new();
    private readonly Dictionary<VulkanHostBufferPoolKey, LinkedList<VulkanHostBufferAllocation>>
        _available = [];
    private readonly Dictionary<ulong, VulkanHostBufferAllocation> _allocations = [];
    private readonly Dictionary<ulong, CachedAllocation> _cached = [];
    private readonly LinkedList<ulong> _recency = [];
    private readonly Action<VulkanHostBufferAllocation> _destroy;

    public VulkanHostBufferPool(
        ulong maximumCachedBytes,
        Action<VulkanHostBufferAllocation> destroy)
    {
        MaximumCachedBytes = maximumCachedBytes;
        _destroy = destroy;
    }

    public ulong MaximumCachedBytes { get; }

    public ulong CachedBytes { get; private set; }

    public bool TryRent(
        VulkanHostBufferPoolKey key,
        out VulkanHostBufferAllocation allocation)
    {
        lock (_gate)
        {
            if (!_available.TryGetValue(key, out var available) ||
                available.Last is not { } availableNode)
            {
                allocation = default;
                return false;
            }

            allocation = availableNode.Value;
            var cached = _cached[allocation.Buffer.Handle];
            available.Remove(availableNode);
            if (available.Count == 0)
            {
                _available.Remove(key);
            }

            _recency.Remove(cached.RecencyNode);
            _cached.Remove(allocation.Buffer.Handle);
            CachedBytes -= allocation.Key.Capacity;
            return true;
        }
    }

    public void Register(VulkanHostBufferAllocation allocation)
    {
        if (allocation.Buffer.Handle == 0)
        {
            throw new ArgumentException("A pooled buffer must have a valid handle.", nameof(allocation));
        }

        lock (_gate)
        {
            _allocations.Add(allocation.Buffer.Handle, allocation);
        }
    }

    public bool Return(VkBuffer buffer, DeviceMemory memory)
    {
        List<VulkanHostBufferAllocation>? toDestroy = null;
        lock (_gate)
        {
            if (!_allocations.TryGetValue(buffer.Handle, out var allocation) ||
                allocation.Memory.Handle != memory.Handle)
            {
                return false;
            }

            if (_cached.ContainsKey(buffer.Handle))
            {
                return true;
            }

            if (allocation.Key.Capacity > MaximumCachedBytes)
            {
                _allocations.Remove(buffer.Handle);
                toDestroy = [allocation];
            }
            else
            {
                while (CachedBytes > MaximumCachedBytes - allocation.Key.Capacity)
                {
                    (toDestroy ??= []).Add(EvictLeastRecentlyUsedLocked());
                }

                if (!_available.TryGetValue(allocation.Key, out var available))
                {
                    available = [];
                    _available.Add(allocation.Key, available);
                }

                var bucketNode = available.AddLast(allocation);
                var recencyNode = _recency.AddLast(buffer.Handle);
                _cached.Add(
                    buffer.Handle,
                    new CachedAllocation(allocation, bucketNode, recencyNode));
                CachedBytes += allocation.Key.Capacity;
            }
        }

        if (toDestroy is not null)
        {
            foreach (var evicted in toDestroy)
            {
                _destroy(evicted);
            }
        }

        return true;
    }

    private VulkanHostBufferAllocation EvictLeastRecentlyUsedLocked()
    {
        var recencyNode = _recency.First ??
            throw new InvalidOperationException("Host-buffer cache accounting is inconsistent.");
        var handle = recencyNode.Value;
        var cached = _cached[handle];
        var allocation = cached.Allocation;

        _recency.Remove(recencyNode);
        _cached.Remove(handle);
        var available = _available[allocation.Key];
        available.Remove(cached.BucketNode);
        if (available.Count == 0)
        {
            _available.Remove(allocation.Key);
        }

        CachedBytes -= allocation.Key.Capacity;
        _allocations.Remove(handle);
        return allocation;
    }

    public void Dispose()
    {
        // Snapshot under the lock, destroy outside — _destroy calls into
        // Vulkan which may grab device-level locks; holding _gate while
        // doing so risks a lock-ordering deadlock with any thread that
        // acquires the device lock first and then waits on _gate.
        List<VulkanHostBufferAllocation> toDestroy;
        lock (_gate)
        {
            toDestroy = new List<VulkanHostBufferAllocation>(_allocations.Values);
            _allocations.Clear();
            _available.Clear();
            _cached.Clear();
            _recency.Clear();
            CachedBytes = 0;
        }

        foreach (var allocation in toDestroy)
        {
            _destroy(allocation);
        }
    }
}
