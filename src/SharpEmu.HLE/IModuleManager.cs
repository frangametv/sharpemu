// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

public interface IModuleManager
{
    /// <summary>Registers pre-built exports (the compile-time generated registry).</summary>
    int RegisterExports(IReadOnlyList<ExportedFunction> exports);

    /// <summary>Registers addressable ABI objects without making them callable.</summary>
    int RegisterDataSymbols(IReadOnlyList<DataSymbolRegistration> registrations);

    void Freeze();

    bool TryGetFunction(string nid, out Delegate function);

    bool TryGetExport(string nid, out ExportedFunction export);

    bool TryGetExportByName(string exportName, out ExportedFunction export);

    bool TryGetDataSymbol(string nid, out DataSymbolRegistration registration);

    bool TryGetDataSymbolByName(string name, out DataSymbolRegistration registration);

    bool TryDispatch(string nid, CpuContext context, out OrbisGen2Result result);

    OrbisGen2Result Dispatch(string nid, CpuContext context);
}
