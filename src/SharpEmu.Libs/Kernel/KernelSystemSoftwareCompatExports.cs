// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Small kernel queries used while bringing up the PS5 system shell. These
/// report conservative host-independent defaults until a system-service model
/// supplies real console state.
/// </summary>
public static class KernelSystemSoftwareCompatExports
{
    private static readonly object SandboxWordGate = new();
    private static ulong _emptySandboxWordAddress;
    private static readonly object Gen5CtypeGate = new();
    private static ulong _gen5CtypeTableAddress;
    private static readonly object Gen5GlobalLocaleGate = new();
    private static ulong _gen5GlobalLocaleAddress;
    private const int Gen5GlobalLocaleFacetCount = 0x28;
    private const int Gen5GlobalLocaleAllocationSize = 0x1000;
    private const int Gen5GlobalLocaleFacetTableOffset = 0x100;
    private const int Gen5GlobalLocaleFacetObjectsOffset = 0x300;
    private const int Gen5GlobalLocaleFacetObjectStride = 0x20;
    private const int Gen5GlobalLocaleNameOffset = 0x900;
    private static ulong _psmPInvokeTableAddress;
    private static ulong _psmInternalCallTableAddress;

    [SysAbiExport(
        Nid = "HoLVWNanBBc",
        ExportName = "getpid",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetProcessId(CpuContext ctx)
    {
        // System applications use the PID as a stable database/logging key.
        // Process scheduling is not modeled yet, but a small positive value
        // matches the Unix contract and avoids propagating an error sentinel.
        ctx[CpuRegister.Rax] = 100;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "nQ5AHlyJghQ",
        ExportName = "sceKernelIsCronos",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelIsCronos(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "JGfTMBOdUJo",
        ExportName = "sceKernelGetFsSandboxRandomWord",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetFsSandboxRandomWord(CpuContext ctx)
    {
        // ShellCore has call sites that accept null and others that
        // unconditionally inspect the first character. A stable empty guest
        // string represents "no sandbox word" safely for both forms.
        lock (SandboxWordGate)
        {
            Span<byte> terminator = stackalloc byte[1];
            terminator.Clear();
            if (_emptySandboxWordAddress == 0 ||
                !ctx.Memory.TryRead(_emptySandboxWordAddress, terminator))
            {
                if (ctx.Memory is not IGuestMemoryAllocator allocator ||
                    !allocator.TryAllocateGuestMemory(1, alignment: 1, out _emptySandboxWordAddress) ||
                    !ctx.Memory.TryWrite(_emptySandboxWordAddress, terminator))
                {
                    _emptySandboxWordAddress = 0;
                }
            }

            ctx[CpuRegister.Rax] = _emptySandboxWordAddress;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "9PCBPZcDU7Q",
        ExportName = "sceApplicationInitializeForShellCore",
        Target = Generation.Gen5,
        LibraryName = "libSceApplication")]
    public static int ApplicationInitializeForShellCore(CpuContext ctx)
    {
        // The native service establishes console-global application state.
        // SharpEmu currently models those services individually, so successful
        // bootstrap is the useful compatibility contract for ShellCore Init2.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "XFYItOxS6r0",
        ExportName = "sceApplicationInitialize",
        Target = Generation.Gen5,
        LibraryName = "libSceApplication")]
    public static int ApplicationInitialize(CpuContext ctx)
    {
        return ApplicationInitializeForShellCore(ctx);
    }

    [SysAbiExport(
        Nid = "yvbO67OvrFc",
        ExportName = "sceApplicationGetShellCoreAppId",
        Target = Generation.Gen5,
        LibraryName = "libSceApplication")]
    public static int ApplicationGetShellCoreAppId(CpuContext ctx)
    {
        var appIdAddress = ctx[CpuRegister.Rdi];
        if (appIdAddress == 0 ||
            !KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, appIdAddress, 1))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // ShellCore only requires a stable, non-invalid identifier here. The
        // native SysCore service owns its real process identifier; use one as a
        // conservative compatibility value until process management is modeled.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Mt6co-yzyjg",
        ExportName = "__sharpemu_gen5_libc_interface_factory",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int Gen5LibcInterfaceFactory(CpuContext ctx)
    {
        // This Gen5 libc entry point supplies an optional reference-counted
        // formatting helper. Every observed caller accepts null and skips both
        // use and virtual release. Returning null is therefore safer than a
        // dummy object whose no-op virtual methods cannot build the expected
        // formatting state.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "hEQ2Yi4PJXA",
        ExportName = "_ZNSt6locale16_GetgloballocaleEv",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int Gen5GetGlobalLocale(CpuContext ctx)
    {
        lock (Gen5GlobalLocaleGate)
        {
            if (!HasUsableGen5GlobalLocale(ctx, _gen5GlobalLocaleAddress))
            {
                if (!TryCreateGen5GlobalLocale(ctx, out _gen5GlobalLocaleAddress))
                {
                    _gen5GlobalLocaleAddress = 0;
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }

            // Firmware 12.70 libSceLibcInternal hEQ2Yi4PJXA returns the
            // process-global locale implementation at DAT_001bb5b8. Its
            // initialized object has a vtable at +0, refcount at +8, a facet
            // pointer table at +0x10, 0x28 entries at +0x18, the all-category
            // mask at +0x20, and the locale name at +0x28. Keep that ABI shape
            // even while individual facet behavior remains a no-op fallback.
            ctx[CpuRegister.Rax] = _gen5GlobalLocaleAddress;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool HasUsableGen5GlobalLocale(CpuContext ctx, ulong localeAddress)
    {
        if (localeAddress == 0 ||
            !ctx.TryReadUInt64(localeAddress, out var vtableAddress) ||
            vtableAddress == 0 ||
            !ctx.TryReadUInt64(localeAddress + 0x10, out var facetTableAddress) ||
            facetTableAddress != localeAddress + Gen5GlobalLocaleFacetTableOffset ||
            !ctx.TryReadUInt64(localeAddress + 0x18, out var facetCount) ||
            facetCount != Gen5GlobalLocaleFacetCount ||
            !ctx.TryReadUInt64(facetTableAddress, out var firstFacetAddress) ||
            firstFacetAddress == 0 ||
            !ctx.TryReadUInt64(
                facetTableAddress + ((Gen5GlobalLocaleFacetCount - 1) * sizeof(ulong)),
                out var lastFacetAddress) ||
            lastFacetAddress == 0)
        {
            return false;
        }

        Span<byte> localeName = stackalloc byte[2];
        return ctx.Memory.TryRead(localeAddress + Gen5GlobalLocaleNameOffset, localeName) &&
               localeName[0] == (byte)'C' &&
               localeName[1] == 0;
    }

    private static bool TryCreateGen5GlobalLocale(CpuContext ctx, out ulong localeAddress)
    {
        localeAddress = 0;
        if (!KernelMemoryCompatExports.TryGetDummyCallbackTable(ctx, out var fallbackVtableAddress) ||
            fallbackVtableAddress == 0 ||
            !KernelMemoryCompatExports.TryAllocateHleData(
                ctx,
                Gen5GlobalLocaleAllocationSize,
                0x1000,
                out localeAddress))
        {
            localeAddress = 0;
            return false;
        }

        var localeBytes = new byte[Gen5GlobalLocaleAllocationSize];
        BinaryPrimitives.WriteUInt64LittleEndian(localeBytes.AsSpan(0x00), fallbackVtableAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(localeBytes.AsSpan(0x08), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(
            localeBytes.AsSpan(0x10),
            localeAddress + Gen5GlobalLocaleFacetTableOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(
            localeBytes.AsSpan(0x18),
            Gen5GlobalLocaleFacetCount);
        BinaryPrimitives.WriteUInt64LittleEndian(localeBytes.AsSpan(0x20), 0x3F);
        BinaryPrimitives.WriteUInt64LittleEndian(
            localeBytes.AsSpan(0x28),
            localeAddress + Gen5GlobalLocaleNameOffset);
        localeBytes[Gen5GlobalLocaleNameOffset] = (byte)'C';

        for (var index = 0; index < Gen5GlobalLocaleFacetCount; index++)
        {
            var facetAddress = localeAddress +
                Gen5GlobalLocaleFacetObjectsOffset +
                (ulong)(index * Gen5GlobalLocaleFacetObjectStride);
            BinaryPrimitives.WriteUInt64LittleEndian(
                localeBytes.AsSpan(Gen5GlobalLocaleFacetTableOffset + (index * sizeof(ulong))),
                facetAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(
                localeBytes.AsSpan(
                    Gen5GlobalLocaleFacetObjectsOffset +
                    (index * Gen5GlobalLocaleFacetObjectStride)),
                fallbackVtableAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(
                localeBytes.AsSpan(
                    Gen5GlobalLocaleFacetObjectsOffset +
                    (index * Gen5GlobalLocaleFacetObjectStride) +
                    sizeof(ulong)),
                1);
        }

        if (!ctx.Memory.TryWrite(localeAddress, localeBytes))
        {
            localeAddress = 0;
            return false;
        }

        return true;
    }

    [SysAbiExport(
        Nid = "9rMML086SEE",
        ExportName = "_ZNSt6locale5_InitEv",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int Gen5LocaleInitGuard(CpuContext ctx)
    {
        // Firmware 12.70 libSceLibcInternal 9rMML086SEE at 0x90530 calls the
        // global-locale initializer and leaves its _Locimp pointer in RAX.
        // Returning null here breaks the caller's lazy facet holder: it stores
        // this result at +0x10 and immediately invokes virtual slot +0x10.
        return Gen5GetGlobalLocale(ctx);
    }

    [SysAbiExport(
        Nid = "qx1TYGQDX3I",
        ExportName = "_ZNSt6locale5_InitEb",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int Gen5LocaleInitGuardWithFlag(CpuContext ctx) => Gen5LocaleInitGuard(ctx);

    [SysAbiExport(
        Nid = "K+YIrBadUlc",
        ExportName = "__sharpemu_gen5_ctype_locale_begin",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int Gen5CtypeLocaleBegin(CpuContext ctx)
    {
        // The libc locale helper records a small lock/cleanup token through
        // its first argument. ShellCore only passes that token to the matching
        // cleanup helper, so a zero-initialized token is sufficient for the
        // classic C locale modeled below.
        var tokenAddress = ctx[CpuRegister.Rdi];
        if (tokenAddress != 0 &&
            !KernelMemoryCompatExports.TryWriteUInt32Compat(ctx, tokenAddress, 0))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "gwAf8VMkXS0",
        ExportName = "__sharpemu_gen5_ctype_table",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int Gen5CtypeTable(CpuContext ctx)
    {
        lock (Gen5CtypeGate)
        {
            Span<byte> probe = stackalloc byte[1];
            if (_gen5CtypeTableAddress == 0 ||
                !ctx.Memory.TryRead(_gen5CtypeTableAddress, probe))
            {
                const int characterCount = 256;
                var table = new byte[characterCount * sizeof(ushort)];
                for (var value = 0; value < characterCount; value++)
                {
                    var c = (char)value;
                    ushort mask = 0;
                    if (c is >= 'A' and <= 'Z') mask |= 0x0001;
                    if (c is >= 'a' and <= 'z') mask |= 0x0002;
                    if (c is >= '0' and <= '9') mask |= 0x0004;
                    if (c is ' ' or '\t' or '\n' or '\r' or '\v' or '\f') mask |= 0x0008;
                    if (value < 0x20 || value == 0x7F) mask |= 0x0020;
                    if (c is ' ' or '\t') mask |= 0x0040;
                    if (c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f') mask |= 0x0080;
                    if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') mask |= 0x0100;
                    if (value is >= 0x21 and <= 0x7E &&
                        !char.IsAsciiLetterOrDigit(c)) mask |= 0x0010;

                    table[value * sizeof(ushort)] = (byte)mask;
                    table[(value * sizeof(ushort)) + 1] = (byte)(mask >> 8);
                }

                if (!KernelMemoryCompatExports.TryAllocateHleData(
                        ctx,
                        (ulong)table.Length,
                        sizeof(ushort),
                        out _gen5CtypeTableAddress) ||
                    !ctx.Memory.TryWrite(_gen5CtypeTableAddress, table))
                {
                    _gen5CtypeTableAddress = 0;
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }

            // The native helper returns a two-register aggregate. RAX is the
            // classification table and EDX is the ownership flag.
            ctx[CpuRegister.Rax] = _gen5CtypeTableAddress;
            ctx[CpuRegister.Rdx] = 0;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "taOj6NIm7DM",
        ExportName = "__sharpemu_gen5_ctype_locale_end",
        Target = Generation.Gen5,
        LibraryName = "libc")]
    public static int Gen5CtypeLocaleEnd(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "vo2FiTXAekk",
        ExportName = "_ZN4IPMI6Server6ConfigC1Ev",
        Target = Generation.Gen5,
        LibraryName = "libSceIpmi")]
    public static int IpmiServerConfigConstruct(CpuContext ctx)
    {
        // The caller owns the Config storage. Its built-in defaults only size
        // the server's temporary workspace; no host-side IPMI endpoint exists.
        ctx[CpuRegister.Rax] = ctx[CpuRegister.Rdi];
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "OXc1BksfEIg",
        ExportName = "_ZNK4IPMI6Server6Config29estimateTempWorkingMemorySizeEv",
        Target = Generation.Gen5,
        LibraryName = "libSceIpmi")]
    public static int IpmiServerConfigEstimateTempWorkingMemorySize(CpuContext ctx)
    {
        // A bounded workspace prevents the unresolved-error sentinel from being
        // interpreted as a multi-exabyte malloc request.
        ctx[CpuRegister.Rax] = 0x1_0000;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "hoWSsRT0ktA",
        ExportName = "_ZN4IPMI6Server6createEPPS0_PKNS0_6ConfigEPvS6_",
        Target = Generation.Gen5,
        LibraryName = "libSceIpmi")]
    public static int IpmiServerCreate(CpuContext ctx)
    {
        var serverOutAddress = ctx[CpuRegister.Rdi];
        if (serverOutAddress == 0 ||
            !KernelMemoryCompatExports.TryAllocateHleData(ctx, 0x100, 16, out var serverAddress) ||
            !KernelMemoryCompatExports.TryWriteDummyVtable(ctx, serverAddress) ||
            !KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, serverOutAddress, serverAddress))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        // ShellCore needs a stable server object even though SharpEmu has no
        // host IPMI transport. Its virtual methods land on the shared no-op
        // vtable, allowing service initialization and orderly teardown.
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "FqHN0elWA6E",
        ExportName = "_ZN3sce3pss5orbis9framework12PsmInitParamC1Ev",
        Target = Generation.Gen5,
        LibraryName = "libScePsm")]
    public static int PsmPrepareLaunchParameters(CpuContext ctx)
    {
        var parametersAddress = ctx[CpuRegister.Rdi];
        if (parametersAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // The dev ShellUI launcher fills the path, memory-size, process and
        // feature fields after this call. Clear the complete observed launch
        // parameter block first so all optional fields retain their defaults.
        Span<byte> parameters = stackalloc byte[0x110];
        parameters.Clear();
        if (!ctx.Memory.TryWrite(parametersAddress, parameters))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "lWlBrUu77Kg",
        ExportName = "_ZN3sce3pss5orbis9framework12PsmFramework10InitializeERKNS2_12PsmInitParamEiPPc",
        Target = Generation.Gen5,
        LibraryName = "libScePsm")]
    public static int PsmStartApplication(CpuContext ctx)
    {
        // Process isolation and the managed PSM host are not modeled yet. The
        // launcher can still initialize its native services in-process, which
        // exposes the next concrete dependency needed for full ShellUI boot.
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "MEuF5zm-r4o",
        ExportName = "_ZN3sce3pss5orbis9framework12PsmFramework24RegisterPInvokeCallTableEPKNS2_15PsmLibraryEntryE",
        Target = Generation.Gen5,
        LibraryName = "libScePsm")]
    public static int PsmRegisterPInvokeCallTable(CpuContext ctx)
    {
        _psmPInvokeTableAddress = ctx[CpuRegister.Rdi];
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "vNcLSBfLtbk",
        ExportName = "_ZN3sce3pss5orbis9framework12PsmFramework20RegisterInternalCallEPKNS2_12PsmCallEntryE",
        Target = Generation.Gen5,
        LibraryName = "libScePsm")]
    public static int PsmRegisterInternalCall(CpuContext ctx)
    {
        _psmInternalCallTableAddress = ctx[CpuRegister.Rdi];
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "oVZ+-KgZJGo",
        ExportName = "scePthreadSetDefaultstacksize",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSetDefaultStackSize(CpuContext ctx)
    {
        // SharpEmu allocates explicit guest stacks for each created thread.
        // Accept the process-wide preference used by the ShellUI launcher;
        // individual thread attributes still take precedence.
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "rNRtm1uioyY",
        ExportName = "sceKernelHasNeoMode",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelHasNeoMode(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "BVDkopEoLMk",
        ExportName = "sceNKWebInitialize",
        Target = Generation.Gen5,
        LibraryName = "libSceNKWeb")]
    public static int NkWebInitialize(CpuContext ctx)
    {
        // The native WebKit service owns a large process-global arena. The
        // menu launcher only requires successful service initialization here;
        // its visible surface is presented through VideoOut separately.
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "fJALl2F0A3I",
        ExportName = "sceNKWebTerminate",
        Target = Generation.Gen5,
        LibraryName = "libSceNKWeb")]
    public static int NkWebTerminate(CpuContext ctx) => ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(
        Nid = "BZ0olR8Da0g",
        ExportName = "sceBgftServiceIntInit",
        Target = Generation.Gen5,
        LibraryName = "libSceBgft")]
    public static int BgftServiceInternalInitialize(CpuContext ctx)
    {
        // BGFT receives caller-owned work memory here. Download scheduling is
        // outside ShellUI bring-up, but the service must be initialized before
        // the menu can register its managed bridge.
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "ODC4-mOiwl0",
        ExportName = "sceBgftServiceIntTerm",
        Target = Generation.Gen5,
        LibraryName = "libSceBgft")]
    public static int BgftServiceInternalTerminate(CpuContext ctx) => ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
}
