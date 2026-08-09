#!/usr/bin/env python3
"""Convert the validated Windows Ghidra audit into selected-NID evidence."""

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


def as_bool(value: str, field: str, nid: str) -> bool:
    normalized = value.strip().lower()
    if normalized == "true":
        return True
    if normalized == "false":
        return False
    raise ValueError(f"{nid}: {field} is not a boolean: {value!r}")


def as_int(value: str, field: str, nid: str) -> int:
    try:
        return int(value, 0)
    except ValueError as error:
        try:
            return int(value, 16)
        except ValueError:
            raise ValueError(f"{nid}: {field} is not an integer: {value!r}") from error


def validate_snapshot(path: Path, summary: dict) -> None:
    expected = summary.get("outputs", {}).get(path.name)
    if not expected:
        raise ValueError(f"summary has no snapshot metadata for {path.name}")
    if path.stat().st_size != expected["bytes"]:
        raise ValueError(f"snapshot size mismatch for {path.name}")
    if sha256(path) != expected["sha256"]:
        raise ValueError(f"snapshot SHA-256 mismatch for {path.name}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("audit", type=Path)
    parser.add_argument("recommendations", type=Path)
    parser.add_argument("summary", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--library", required=True)
    parser.add_argument("--exclude-nid", action="append", default=[])
    parser.add_argument("--ghidra-version", default="12.1.2_PUBLIC_20260605")
    args = parser.parse_args()

    summary = json.loads(args.summary.read_text())
    validate_snapshot(args.audit, summary)
    validate_snapshot(args.recommendations, summary)
    if "no Kyty" not in summary.get("evidence_policy", ""):
        parser.error("summary does not explicitly exclude Kyty-derived evidence")

    with args.audit.open(newline="") as handle:
        audit_rows = [
            row for row in csv.DictReader(handle)
            if row.get("library") == args.library
        ]
    with args.recommendations.open(newline="") as handle:
        recommendation_rows = [
            row for row in csv.DictReader(handle)
            if row.get("library") == args.library
        ]
    audit_by_nid = {row["nid"]: row for row in audit_rows}
    recommendation_by_nid = {row["nid"]: row for row in recommendation_rows}
    if not audit_by_nid or set(audit_by_nid) != set(recommendation_by_nid):
        parser.error("audit and recommendation NID sets differ or are empty")
    if len(audit_by_nid) != len(audit_rows):
        parser.error("audit contains duplicate NIDs")

    excluded_nids = set(args.exclude_nid)
    unknown_exclusions = excluded_nids - set(audit_by_nid)
    if unknown_exclusions:
        parser.error(f"excluded NIDs are absent from the audit: {sorted(unknown_exclusions)}")

    functions = []
    providers = set()
    for nid in sorted(audit_by_nid):
        audit = audit_by_nid[nid]
        recommendation = recommendation_by_nid[nid]
        if not as_bool(recommendation["include_for_prefer_lle"], "include_for_prefer_lle", nid):
            continue
        if nid in excluded_nids:
            continue

        providers.add(audit["provider"])
        if audit["status"] not in {"defined_body", "defined_internal_thunk"}:
            parser.error(f"{nid}: recommendation lacks a defined provider body")
        if as_int(audit["match_count"], "match_count", nid) != 1:
            parser.error(f"{nid}: provider symbol is missing or ambiguous")
        if audit["symbol_type"] not in {"Function", "Label"}:
            parser.error(f"{nid}: unsupported symbol type {audit['symbol_type']!r}")
        if audit["symbol_source"] != "IMPORTED":
            parser.error(f"{nid}: target is not an imported provider symbol")
        if as_int(audit["symbol_address"], "symbol_address", nid) != as_int(
            audit["function_entry"], "function_entry", nid
        ):
            parser.error(f"{nid}: symbol and function entries differ")
        if as_bool(audit["external"], "external", nid):
            parser.error(f"{nid}: provider function is external")
        if not as_bool(audit["executable"], "executable", nid):
            parser.error(f"{nid}: provider function is not executable")
        if as_int(audit["body_addresses"], "body_addresses", nid) <= 0:
            parser.error(f"{nid}: provider function body is empty")
        if as_int(audit["instruction_count"], "instruction_count", nid) <= 0:
            parser.error(f"{nid}: provider function has no instructions")
        if len(audit["body_sha256"]) != 64:
            parser.error(f"{nid}: provider body SHA-256 is missing")
        is_thunk = as_bool(audit["thunk"], "thunk", nid)
        if is_thunk and as_bool(audit["thunk_target_external"], "thunk_target_external", nid):
            parser.error(f"{nid}: provider thunk leaves the analyzed module")
        if recommendation["provider_sha256"] != audit["provider_sha256"]:
            parser.error(f"{nid}: recommendation/audit provider hashes differ")

        expected_provider_hash = summary.get("provider_sha256", {}).get(audit["provider"])
        if audit["provider_sha256"] != expected_provider_hash:
            parser.error(f"{nid}: provider hash differs from the summary")
        functions.append(
            {
                "nid": nid,
                "symbol": audit["symbol_name"],
                "symbol_address": audit["symbol_address"],
                "function_present": True,
                "function_name": audit["function_name"],
                "function_entry": audit["function_entry"],
                "function_body_addresses": as_int(
                    audit["body_addresses"], "body_addresses", nid
                ),
                "instruction_count": as_int(
                    audit["instruction_count"], "instruction_count", nid
                ),
                "function_body_sha256": audit["body_sha256"],
                "external": False,
                "thunk": is_thunk,
                "thunk_target": audit["thunk_target"],
                "thunk_target_external": False if is_thunk else None,
                "result_file": audit["result_file"],
                "wave": audit["wave"],
            }
        )

    if len(providers) != 1:
        parser.error(f"expected exactly one provider file, found {sorted(providers)}")
    provider = next(iter(providers))
    output = {
        "format": "sharpemu-ghidra-selected-nid-functions-v1",
        "ghidra_version": args.ghidra_version,
        "program_name": provider,
        "executable_sha256": summary["provider_sha256"][provider],
        "target_count": len(functions),
        "function_count": len(functions),
        "missing": [],
        "without_functions": [],
        "functions": functions,
        "provenance": {
            "audit_sha256": sha256(args.audit),
            "recommendations_sha256": sha256(args.recommendations),
            "summary_sha256": sha256(args.summary),
            "excluded_nids": sorted(excluded_nids),
            "evidence_policy": summary["evidence_policy"],
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
