// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition("KernelSystemSoftwareCompat", DisableParallelization = true)]
public sealed class KernelSystemSoftwareCompatCollection;

[Collection("KernelSystemSoftwareCompat")]
public sealed class KernelSystemSoftwareCompatExportsTests
{
    [Theory]
    [InlineData("9rMML086SEE", "_ZNSt6locale5_InitEv")]
    [InlineData("hEQ2Yi4PJXA", "_ZNSt6locale16_GetgloballocaleEv")]
    public void LocaleExports_RegisterForGen5(string nid, string name)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libc", export.LibraryName);
    }

    [Fact]
    public void LocaleInitAndGetter_ReturnSameFirmwareShapedGlobalLocale()
    {
        var memory = new TestGuestAddressSpace();
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.Equal(0, KernelSystemSoftwareCompatExports.Gen5LocaleInitGuard(ctx));
        var initAddress = ctx[CpuRegister.Rax];
        Assert.NotEqual(0UL, initAddress);

        Assert.Equal(0, KernelSystemSoftwareCompatExports.Gen5GetGlobalLocale(ctx));
        Assert.Equal(initAddress, ctx[CpuRegister.Rax]);

        Assert.True(ctx.TryReadUInt64(initAddress, out var localeVtable));
        Assert.NotEqual(0UL, localeVtable);
        Assert.True(ctx.TryReadUInt64(initAddress + 0x08, out var referenceCount));
        Assert.Equal(1UL, referenceCount);
        Assert.True(ctx.TryReadUInt64(initAddress + 0x10, out var facetTable));
        Assert.Equal(initAddress + 0x100, facetTable);
        Assert.True(ctx.TryReadUInt64(initAddress + 0x18, out var facetCount));
        Assert.Equal(0x28UL, facetCount);
        Assert.True(ctx.TryReadUInt64(initAddress + 0x20, out var categoryMask));
        Assert.Equal(0x3FUL, categoryMask);
        Assert.True(ctx.TryReadUInt64(initAddress + 0x28, out var nameAddress));
        Assert.Equal(initAddress + 0x900, nameAddress);

        Span<byte> localeName = stackalloc byte[2];
        Assert.True(memory.TryRead(nameAddress, localeName));
        Assert.Equal(new byte[] { (byte)'C', 0 }, localeName.ToArray());

        Assert.True(ctx.TryReadUInt64(facetTable, out var firstFacet));
        Assert.True(ctx.TryReadUInt64(facetTable + (0x27 * sizeof(ulong)), out var lastFacet));
        Assert.NotEqual(0UL, firstFacet);
        Assert.NotEqual(0UL, lastFacet);
        Assert.True(ctx.TryReadUInt64(firstFacet, out var firstFacetVtable));
        Assert.True(ctx.TryReadUInt64(lastFacet, out var lastFacetVtable));
        Assert.Equal(localeVtable, firstFacetVtable);
        Assert.Equal(localeVtable, lastFacetVtable);
    }

    private sealed class TestGuestAddressSpace : ICpuMemory, IGuestAddressSpace
    {
        private readonly SortedDictionary<ulong, byte[]> _regions = [];
        private ulong _nextAddress = 0x20_0000_0000;

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var region, out var offset))
            {
                return false;
            }

            region.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var region, out var offset))
            {
                return false;
            }

            source.CopyTo(region.AsSpan(offset, source.Length));
            return true;
        }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address) =>
            TryAllocateAtOrAbove(_nextAddress, size, executable: false, alignment, out address);

        public bool TryFreeGuestMemory(ulong address) => _regions.Remove(address);

        public ulong AllocateAt(
            ulong desiredAddress,
            ulong size,
            bool executable = true,
            bool allowAlternative = true)
        {
            if (TryAllocateExact(desiredAddress, size))
            {
                return desiredAddress;
            }

            return allowAlternative &&
                TryAllocateAtOrAbove(desiredAddress, size, executable, 0x1000, out var alternative)
                    ? alternative
                    : 0;
        }

        public bool TryAllocateAtOrAbove(
            ulong desiredAddress,
            ulong size,
            bool executable,
            ulong alignment,
            out ulong actualAddress)
        {
            actualAddress = 0;
            if (size == 0 || size > int.MaxValue)
            {
                return false;
            }

            var effectiveAlignment = Math.Max(1UL, alignment);
            var candidate = AlignUp(Math.Max(desiredAddress, _nextAddress), effectiveAlignment);
            while (!TryAllocateExact(candidate, size))
            {
                if (ulong.MaxValue - candidate < effectiveAlignment)
                {
                    return false;
                }

                candidate = AlignUp(candidate + effectiveAlignment, effectiveAlignment);
            }

            actualAddress = candidate;
            _nextAddress = candidate + size;
            return true;
        }

        public bool TryBackFixedRange(ulong address, ulong size, bool executable) =>
            size <= int.MaxValue &&
            (TryResolve(address, (int)size, out _, out _) || TryAllocateExact(address, size));

        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection) =>
            size <= int.MaxValue && TryResolve(address, (int)size, out _, out _);

        private bool TryAllocateExact(ulong address, ulong size)
        {
            if (address == 0 || size == 0 || size > int.MaxValue || ulong.MaxValue - address < size)
            {
                return false;
            }

            var endAddress = address + size;
            foreach (var (regionAddress, region) in _regions)
            {
                var regionEnd = regionAddress + (ulong)region.Length;
                if (address < regionEnd && regionAddress < endAddress)
                {
                    return false;
                }
            }

            _regions.Add(address, new byte[(int)size]);
            return true;
        }

        private bool TryResolve(
            ulong address,
            int length,
            out byte[] region,
            out int offset)
        {
            foreach (var (regionAddress, candidate) in _regions)
            {
                if (address < regionAddress)
                {
                    break;
                }

                var relative = address - regionAddress;
                if (relative <= (ulong)candidate.Length &&
                    (ulong)length <= (ulong)candidate.Length - relative)
                {
                    region = candidate;
                    offset = (int)relative;
                    return true;
                }
            }

            region = [];
            offset = 0;
            return false;
        }

        private static ulong AlignUp(ulong value, ulong alignment) =>
            (value + alignment - 1) & ~(alignment - 1);
    }
}
