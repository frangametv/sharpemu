#!/usr/bin/env python3
"""Validate and compact the finalized rho GTA V libc35 Ghidra packet."""

from __future__ import annotations

import argparse
import base64
import csv
import hashlib
import json
from pathlib import Path


NID_SUFFIX = bytes.fromhex("518d64a635ded8c1e6b039b1c3e55230")
EXPECTED_CONSOLIDATED_FORMAT = "sharpemu-gta-v-rho-remaining67-v1"
EXPECTED_PROVIDER = "gta_v_libc"
EXPECTED_PROVIDER_SHA256 = "309cb9031209eb9b838216994d2c39613fcd65ec1eae493c4b784b9dacdd06bb"
EXPECTED_RUNTIME_LOAD_BASE = 0x0000000805AEA000
EXPECTED_BACKTRACE_NID = "EHsF2i9FXPM"
EXPECTED_BACKTRACE_NAME = "sceLibcInternalBacktraceForGame"
EXPECTED_BACKTRACE_LIBRARY = "libSceLibcInternalExt"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def name_to_nid(symbol: str) -> str:
    digest = hashlib.sha1(symbol.encode("utf-8") + NID_SUFFIX).digest()
    return base64.b64encode(digest[:8][::-1]).decode("ascii").rstrip("=").replace("/", "-")


def read_csv(path: Path) -> list[dict[str, str]]:
    with path.open(newline="") as handle:
        return list(csv.DictReader(handle))


def parse_hash_manifest(path: Path) -> dict[str, str]:
    hashes: dict[str, str] = {}
    for line in path.read_text().splitlines():
        if not line.strip():
            continue
        digest, relative_path = line.split(maxsplit=1)
        hashes[relative_path] = digest
    return hashes


def compact_provider(provider: dict[str, object]) -> dict[str, object]:
    return {
        "decompile_sha256": provider["decompile_sha256"],
        "deterministic_across_runs": provider["deterministic_across_runs"],
        "evidence_file": provider["evidence_file"],
        "evidence_file_sha256": provider["evidence_file_sha256"],
        "function_body_addresses": provider["function_body_addresses"],
        "function_body_sha256": provider["function_body_sha256"],
        "function_entry": provider["function_entry"],
        "function_name": provider["function_name"],
        "function_present": provider["function_present"],
        "ghidra_version": provider["ghidra_version"],
        "instruction_count": provider["instruction_count"],
        "provider": provider["provider"],
        "provider_label": provider["provider_label"],
        "provider_sha256": provider["provider_sha256"],
        "symbol_address": provider["symbol_address"],
        "symbol_external": provider["symbol_external"],
        "symbol_name": provider["symbol_name"],
        "symbol_type": provider["symbol_type"],
        "thunk": provider["thunk"],
        "thunk_target_entry": provider.get("thunk_target_entry"),
        "thunk_target_external": provider.get("thunk_target_external"),
        "thunk_target_name": provider.get("thunk_target_name"),
    }


def selected_function(nid: str, provider: dict[str, object], runtime: dict[str, object]) -> dict[str, object]:
    return {
        "external": provider["symbol_external"],
        "function_body_addresses": provider["function_body_addresses"],
        "function_body_sha256": provider["function_body_sha256"],
        "function_entry": provider["function_entry"],
        "function_name": provider["function_name"],
        "function_present": provider["function_present"],
        "instruction_count": provider["instruction_count"],
        "nid": nid,
        "runtime_direct_bridge_count": runtime["direct_bridge_count"],
        "runtime_load_base": f"0x{EXPECTED_RUNTIME_LOAD_BASE:016X}",
        "runtime_symbol_addresses": runtime["runtime_symbol_addresses"],
        "symbol": provider["symbol_name"],
        "symbol_address": provider["symbol_address"],
        "thunk": provider["thunk"],
        "thunk_target": provider.get("thunk_target_entry"),
        "thunk_target_external": provider.get("thunk_target_external", False),
    }


def write_selected_packet(
    path: Path,
    functions: list[dict[str, object]],
    source_hashes: dict[str, str],
    runtime_log_sha256: str,
) -> None:
    payload = {
        "executable_sha256": EXPECTED_PROVIDER_SHA256,
        "format": "sharpemu-ghidra-selected-nid-functions-v1",
        "function_count": len(functions),
        "functions": sorted(functions, key=lambda row: str(row["nid"])),
        "ghidra_version": "12.1.2",
        "missing": [],
        "program_name": "gta-v-libc.reconstructed.elf",
        "provenance": {
            "evidence_policy": "Ghidra plus SharpEmu runtime routing only; no emulator-source contracts",
            "runtime_log_sha256": runtime_log_sha256,
            "source_hashes": source_hashes,
        },
        "target_count": len(functions),
        "without_functions": [],
    }
    path.write_text(json.dumps(payload, indent=2, sort_keys=True) + "\n")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("packet", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()

    packet = args.packet.resolve()
    output = args.output.resolve()
    manifest = parse_hash_manifest(packet / "ARTIFACT_SHA256.txt")
    required_files = [
        "consolidated-nid-evidence.json",
        "prefer-lle-include-candidates.csv",
        "libc-non-lle-contract-queue.csv",
        "data-import-disposition.csv",
        "kernel-hle-contract-queue.csv",
        "provider-coverage-summary.json",
        "EVIDENCE.md",
        "cleanup-proof.txt",
        "cleanup-proof-deep.txt",
    ]
    for relative_path in required_files:
        path = packet / relative_path
        require(path.is_file(), f"missing source artifact: {relative_path}")
        require(manifest.get(relative_path) == sha256(path), f"source hash mismatch: {relative_path}")

    consolidated_path = packet / "consolidated-nid-evidence.json"
    prefer_path = packet / "prefer-lle-include-candidates.csv"
    non_lle_path = packet / "libc-non-lle-contract-queue.csv"
    data_path = packet / "data-import-disposition.csv"
    kernel_path = packet / "kernel-hle-contract-queue.csv"
    consolidated = json.loads(consolidated_path.read_text())
    prefer_rows = read_csv(prefer_path)
    non_lle_rows = read_csv(non_lle_path)
    data_rows = read_csv(data_path)
    kernel_rows = read_csv(kernel_path)

    require(consolidated.get("format") == EXPECTED_CONSOLIDATED_FORMAT, "unexpected consolidated format")
    require(consolidated.get("requested_nids") == 67, "consolidated packet is not the exact 67-NID queue")
    require(len(consolidated.get("rows", [])) == 67, "consolidated evidence row count mismatch")
    require(len(prefer_rows) == 34, "PreferLle queue must contain exactly 34 rows")
    require(len(non_lle_rows) == 1, "non-LLE queue must contain exactly one row")
    require(len(data_rows) == 5, "data disposition must contain exactly five rows")
    require(len(kernel_rows) == 27, "kernel/POSIX queue must contain exactly 27 rows")

    all_sets = [
        {row["nid"] for row in prefer_rows},
        {row["nid"] for row in non_lle_rows},
        {row["nid"] for row in data_rows},
        {row["nid"] for row in kernel_rows},
    ]
    require(sum(len(nids) for nids in all_sets) == 67, "source queues contain duplicate NIDs")
    require(len(set().union(*all_sets)) == 67, "source queue partitions overlap")

    consolidated_by_nid = {row["nid"]: row for row in consolidated["rows"]}
    require(len(consolidated_by_nid) == 67, "consolidated evidence contains duplicate NIDs")
    require(set(consolidated_by_nid) == set().union(*all_sets), "source queues do not partition consolidated evidence")

    source_hashes = {relative_path: manifest[relative_path] for relative_path in required_files}
    normalized_rows: list[dict[str, str]] = []
    selected_by_group: dict[str, list[dict[str, object]]] = {
        "LibcProvider": [],
        "LibcInternalProvider": [],
    }
    compact_records: list[dict[str, object]] = []
    hybrid_nids: list[str] = []

    for source in prefer_rows:
        nid = source["nid"]
        require(name_to_nid(source["catalog_symbol"]) == nid, f"catalog symbol/NID mismatch: {nid}")
        require(source["provider"] == EXPECTED_PROVIDER, f"unexpected provider for {nid}")
        require(source["provider_sha256"] == EXPECTED_PROVIDER_SHA256, f"provider hash mismatch for {nid}")
        require(source["confidence"] == "high", f"non-high confidence PreferLle row: {nid}")
        require("PreferLle=true" in source["registration_policy"], f"missing PreferLle policy: {nid}")

        evidence = consolidated_by_nid[nid]
        require(evidence["catalog_symbol"] == source["catalog_symbol"], f"name mismatch for {nid}")
        require(evidence["component"] == source["component"], f"component mismatch for {nid}")
        require(evidence["module"] == source["module"], f"module mismatch for {nid}")
        require(evidence["library"] == source["library"], f"library mismatch for {nid}")
        require(evidence["disposition"] == "include_gen5_prefer_lle_fail_closed", f"bad disposition for {nid}")

        provider = evidence["provider_evidence"][EXPECTED_PROVIDER]
        runtime = evidence["runtime"]
        require(provider["provider_sha256"] == source["provider_sha256"], f"provider mismatch for {nid}")
        require(provider["symbol_address"] == source["provider_symbol_address"], f"symbol mismatch for {nid}")
        require(provider["function_entry"] == source["function_entry"], f"entry mismatch for {nid}")
        require(str(provider["function_body_addresses"]) == source["function_body_addresses"], f"body size mismatch for {nid}")
        require(str(provider["instruction_count"]) == source["instruction_count"], f"instruction mismatch for {nid}")
        require(provider["function_body_sha256"] == source["function_body_sha256"], f"body hash mismatch for {nid}")
        require(provider["decompile_sha256"] == source["decompile_sha256"], f"decompile hash mismatch for {nid}")
        require(provider["function_present"] is True, f"Ghidra function missing for {nid}")
        require(provider["symbol_type"] == "Function", f"non-function PreferLle row: {nid}")
        require(provider["symbol_address"] == provider["function_entry"], f"symbol/entry mismatch for {nid}")
        require(int(provider["function_body_addresses"]) > 0, f"empty function body for {nid}")
        require(runtime["direct_bridge_count"] > 0, f"missing direct runtime proof for {nid}")
        require(runtime["runtime_symbol_count"] > 0, f"missing runtime symbol for {nid}")
        require(runtime["runtime_symbol_addresses"], f"empty runtime address set for {nid}")
        require(str(runtime["direct_bridge_count"]) == source["runtime_direct_bridge_count"], f"runtime count mismatch for {nid}")
        require(source["runtime_load_base"] == f"0x{EXPECTED_RUNTIME_LOAD_BASE:016X}", f"load base mismatch for {nid}")
        for address in runtime["runtime_symbol_addresses"]:
            require(int(address, 16) - int(provider["symbol_address"], 16) == EXPECTED_RUNTIME_LOAD_BASE,
                    f"runtime/provider address delta mismatch for {nid}")

        if source["component"] == "LibcInternal;Libc":
            group = "LibcProvider"
            registered_module = "libc"
            registered_library = "libc"
            normalization_reason = "hybrid import registered once under loaded GTA libc provider"
            hybrid_nids.append(nid)
        elif source["component"] == "Libc":
            group = "LibcProvider"
            registered_module = source["module"]
            registered_library = source["library"]
            normalization_reason = ""
        elif source["component"] == "LibcInternal":
            group = "LibcInternalProvider"
            registered_module = source["module"]
            registered_library = source["library"]
            normalization_reason = ""
        else:
            raise ValueError(f"unexpected PreferLle component for {nid}: {source['component']}")

        normalized_rows.append({
            "component": group,
            "nid": nid,
            "catalog_symbol": source["catalog_symbol"],
            "module": registered_module,
            "library": registered_library,
            "provider": source["provider"],
            "provider_sha256": source["provider_sha256"],
            "runtime_symbol_addresses": source["runtime_symbol_addresses"],
            "runtime_load_base": source["runtime_load_base"],
            "runtime_direct_bridge_count": source["runtime_direct_bridge_count"],
            "function_entry": source["function_entry"],
            "function_body_addresses": source["function_body_addresses"],
            "instruction_count": source["instruction_count"],
            "function_body_sha256": source["function_body_sha256"],
            "decompile_sha256": source["decompile_sha256"],
            "registration_policy": source["registration_policy"],
            "confidence": source["confidence"],
            "source_component": source["component"],
            "source_module": source["module"],
            "source_library": source["library"],
            "normalization_reason": normalization_reason,
        })
        selected_by_group[group].append(selected_function(nid, provider, runtime))
        compact_records.append({
            "catalog_symbol": source["catalog_symbol"],
            "component": source["component"],
            "disposition": "gen5_prefer_lle_fail_closed",
            "library": source["library"],
            "module": source["module"],
            "nid": nid,
            "provider_evidence": {EXPECTED_PROVIDER: compact_provider(provider)},
            "registered_library": registered_library,
            "runtime": runtime,
        })

    require(len(selected_by_group["LibcProvider"]) == 30, "normalized libc group must contain 30 rows")
    require(len(selected_by_group["LibcInternalProvider"]) == 4, "libSceLibcInternal group must contain four rows")
    require(len(hybrid_nids) == 3, "hybrid libc/internal set must contain three rows")

    backtrace = non_lle_rows[0]
    require(backtrace["nid"] == EXPECTED_BACKTRACE_NID, "unexpected non-LLE NID")
    require(backtrace["catalog_symbol"] == EXPECTED_BACKTRACE_NAME, "unexpected non-LLE symbol")
    require(backtrace["library"] == EXPECTED_BACKTRACE_LIBRARY, "unexpected non-LLE library")
    require(name_to_nid(backtrace["catalog_symbol"]) == backtrace["nid"], "backtrace symbol/NID mismatch")
    backtrace_evidence = consolidated_by_nid[EXPECTED_BACKTRACE_NID]
    require(backtrace_evidence["disposition"] == "exclude_lle_no_runtime_resolution_fail_closed_semantic_contract_needed",
            "backtrace disposition does not require fail-closed HLE")
    require(backtrace_evidence["runtime"]["direct_bridge_count"] == 0, "backtrace unexpectedly has direct runtime proof")
    require(backtrace_evidence["runtime"]["runtime_symbol_count"] == 0, "backtrace unexpectedly has a runtime symbol")
    backtrace_providers: dict[str, object] = {}
    for provider_name, provider in sorted(backtrace_evidence["provider_evidence"].items()):
        if provider.get("function_present"):
            require(provider["symbol_type"] == "Function", f"backtrace provider is not a function: {provider_name}")
            require(int(provider["function_body_addresses"]) > 0, f"empty backtrace body: {provider_name}")
            backtrace_providers[provider_name] = compact_provider(provider)
    require(backtrace_providers, "backtrace has no Ghidra function evidence")
    compact_records.append({
        "catalog_symbol": EXPECTED_BACKTRACE_NAME,
        "component": backtrace["component"],
        "disposition": "gen5_hle_fail_closed_no_prefer_lle",
        "library": backtrace["library"],
        "module": backtrace["module"],
        "nid": EXPECTED_BACKTRACE_NID,
        "provider_evidence": backtrace_providers,
        "registered_library": EXPECTED_BACKTRACE_LIBRARY,
        "runtime": backtrace_evidence["runtime"],
    })

    output.mkdir(parents=True, exist_ok=True)
    queue_fields = list(normalized_rows[0])
    with (output / "prefer-lle-registration-queue.csv").open("w", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=queue_fields, lineterminator="\n")
        writer.writeheader()
        writer.writerows(sorted(normalized_rows, key=lambda row: (row["component"], row["nid"])))

    write_selected_packet(
        output / "libc-provider-selected.json",
        selected_by_group["LibcProvider"],
        source_hashes,
        consolidated["runtime_log_sha256"],
    )
    write_selected_packet(
        output / "libc-internal-provider-selected.json",
        selected_by_group["LibcInternalProvider"],
        source_hashes,
        consolidated["runtime_log_sha256"],
    )

    compact = {
        "counts": {
            "excluded_data_objects": 5,
            "excluded_kernel_posix_functions": 27,
            "gen5_hle_fail_closed": 1,
            "gen5_prefer_lle": 34,
            "libSceLibcInternal_prefer_lle": 4,
            "libc_prefer_lle": 30,
        },
        "evidence_policy": consolidated["evidence_policy"],
        "format": "sharpemu-gta-v-libc35-selected-evidence-v1",
        "hybrid_library_decision": {
            "nids": sorted(hybrid_nids),
            "registered_library": "libc",
            "reason": "single NID registry plus exact loaded GTA libc runtime resolution",
        },
        "records": sorted(compact_records, key=lambda row: str(row["nid"])),
        "runtime_log_sha256": consolidated["runtime_log_sha256"],
        "source_hashes": source_hashes,
    }
    (output / "selected-evidence.json").write_text(json.dumps(compact, indent=2, sort_keys=True) + "\n")

    print(
        "validated=67 prefer_lle=34 libc=30 libc_internal=4 "
        "hle_fail_closed=1 data_excluded=5 kernel_posix_excluded=27"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
