#!/usr/bin/env python3
"""Convert a validated Ghidra provider inventory into selected-NID evidence."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def parse_bool(value: str, field: str, nid: str) -> bool:
    normalized = value.strip().lower()
    if normalized == "true":
        return True
    if normalized == "false":
        return False
    raise ValueError(f"{nid}: {field} is not a boolean: {value!r}")


def parse_int(value: str, field: str, nid: str) -> int:
    try:
        return int(value, 0)
    except ValueError as error:
        raise ValueError(f"{nid}: {field} is not an integer: {value!r}") from error


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("inventory", type=Path)
    parser.add_argument("metadata", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--library", required=True)
    parser.add_argument("--exclude-nid", action="append", default=[])
    parser.add_argument(
        "--log-root",
        type=Path,
        help="Optional campaign root used to re-hash every per-NID Ghidra log.",
    )
    args = parser.parse_args()

    metadata = json.loads(args.metadata.read_text())
    expected_inventory_hash = metadata.get("rows_csv_sha256")
    actual_inventory_hash = sha256(args.inventory)
    if actual_inventory_hash != expected_inventory_hash:
        parser.error(
            "inventory SHA-256 differs from metadata: "
            f"expected={expected_inventory_hash} actual={actual_inventory_hash}"
        )

    providers = [
        provider for provider in metadata.get("providers", [])
        if provider.get("library") == args.library
    ]
    if len(providers) != 1:
        parser.error(f"expected exactly one metadata provider for {args.library}")
    provider = providers[0]

    with args.inventory.open(newline="") as handle:
        library_rows = [
            row for row in csv.DictReader(handle)
            if row.get("library") == args.library
        ]
    if len(library_rows) != int(provider.get("targets", -1)):
        parser.error("inventory/provider target counts differ")

    by_nid = {row["nid"]: row for row in library_rows}
    if len(by_nid) != len(library_rows):
        parser.error("provider inventory contains duplicate NIDs")
    excluded_nids = set(args.exclude_nid)
    unknown_exclusions = excluded_nids - set(by_nid)
    if unknown_exclusions:
        parser.error(f"excluded NIDs are absent from the provider: {sorted(unknown_exclusions)}")

    expected_provider_hash = provider["reconstructed_derivative_sha256"]
    functions = []
    for row in sorted(library_rows, key=lambda item: item["nid"]):
        nid = row["nid"]
        if nid in excluded_nids:
            continue
        if row["symbol_type"] != "Function":
            parser.error(f"{nid}: target symbol is not a Function")
        if not parse_bool(row["symbol_primary"], "symbol_primary", nid):
            parser.error(f"{nid}: target symbol is not primary")
        if parse_int(row["entry"], "entry", nid) != parse_int(
            row["function_entry"], "function_entry", nid
        ):
            parser.error(f"{nid}: symbol and function entries differ")
        if parse_bool(row["external"], "external", nid):
            parser.error(f"{nid}: function is external")
        if parse_int(row["body_bytes"], "body_bytes", nid) <= 0:
            parser.error(f"{nid}: function body is empty")
        if not parse_bool(row["decompile_completed"], "decompile_completed", nid):
            parser.error(f"{nid}: decompilation did not complete")
        if parse_int(row["target_symbol_count"], "target_symbol_count", nid) != 1:
            parser.error(f"{nid}: target symbol is missing or ambiguous")
        if parse_int(row["headless_exit"], "headless_exit", nid) != 0:
            parser.error(f"{nid}: Ghidra headless job failed")
        if row["provider_sha256"] != expected_provider_hash:
            parser.error(f"{nid}: provider SHA-256 differs from metadata")

        if args.log_root is not None:
            evidence_log = args.log_root / row["evidence_log"]
            if not evidence_log.is_file():
                parser.error(f"{nid}: evidence log is missing: {evidence_log}")
            if sha256(evidence_log) != row["evidence_log_sha256"]:
                parser.error(f"{nid}: evidence log SHA-256 mismatch")

        functions.append(
            {
                "nid": nid,
                "symbol": row["qualified_symbol"],
                "symbol_address": row["entry"],
                "function_present": True,
                "function_name": row["function_name"],
                "function_entry": row["function_entry"],
                "function_body_bytes": parse_int(row["body_bytes"], "body_bytes", nid),
                "external": False,
                "thunk": parse_bool(row["thunk"], "thunk", nid),
                "decompile_completed": True,
                "ghidra_project": row["ghidra_project"],
                "evidence_log": row["evidence_log"],
                "evidence_log_sha256": row["evidence_log_sha256"],
            }
        )

    output = {
        "format": "sharpemu-ghidra-selected-nid-functions-v1",
        "ghidra_version": metadata["ghidra"]["version"],
        "program_name": Path(provider["reconstructed_derivative_path"]).name,
        "executable_sha256": expected_provider_hash,
        "target_count": len(functions),
        "function_count": len(functions),
        "missing": [],
        "without_functions": [],
        "functions": functions,
        "provenance": {
            "inventory_sha256": actual_inventory_hash,
            "metadata_sha256": sha256(args.metadata),
            "source_provider_sha256": provider["source_sha256"],
            "excluded_nids": sorted(excluded_nids),
            "per_nid_logs_rehashed": args.log_root is not None,
            "other_emulator_source_used": metadata.get("other_emulator_source_used"),
        },
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(output, indent=2, sort_keys=True) + "\n")
    print(
        f"library={args.library} functions={len(functions)} "
        f"excluded={len(excluded_nids)} output={args.output}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
