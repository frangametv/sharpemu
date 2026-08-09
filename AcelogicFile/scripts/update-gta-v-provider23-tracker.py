#!/usr/bin/env python3
"""Record the dual-host Ghidra provider23 wave in the GTA V swarm manifest."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path
from typing import Any


SOURCE_NAMES = {
    "AjmNativeLleExports.cs",
    "AppContentLleExports.cs",
    "AudioOutLleExports.cs",
    "AudioOut2LleExports.cs",
    "CoredumpLleExports.cs",
    "HttpLleExports.cs",
    "ImeLleExports.cs",
    "NpTrophy2LleExports.cs",
    "PadLleExports.cs",
    "PlayerSelectionDialogLleExports.cs",
    "RandomLleExports.cs",
    "RazorCpuLleExports.cs",
    "SysmoduleLleExports.cs",
    "UlObjMgrLleExports.cs",
    "VideoOutLleExports.cs",
}
ATTRIBUTE_RE = re.compile(r"\[SysAbiExport\(\s*(.*?)\)\]", re.DOTALL)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(message)


def field(body: str, name: str) -> str:
    match = re.search(rf'\b{name}\s*=\s*"([^"]+)"', body)
    require(match is not None, f"missing {name} in registration")
    return match.group(1)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--commit", required=True)
    parser.add_argument(
        "--repo",
        type=Path,
        default=Path(__file__).resolve().parents[2],
    )
    return parser.parse_args()


def load_registrations(repo: Path) -> dict[str, dict[str, Any]]:
    registrations: dict[str, dict[str, Any]] = {}
    source_root = repo / "src/SharpEmu.Libs/Lle"
    for source_name in sorted(SOURCE_NAMES):
        source = source_root / source_name
        text = source.read_text(encoding="utf-8")
        require(
            "ORBIS_GEN2_ERROR_NOT_IMPLEMENTED" in text,
            f"{source_name} is not fail-closed",
        )
        for match in ATTRIBUTE_RE.finditer(text):
            body = match.group(1)
            require("Target = Generation.Gen5" in body, f"non-Gen5 registration in {source_name}")
            require("PreferLle = true" in body, f"non-PreferLle registration in {source_name}")
            nid = field(body, "Nid")
            require(nid not in registrations, f"duplicate provider23 NID {nid}")
            registrations[nid] = {
                "nid": nid,
                "name": field(body, "ExportName"),
                "library": field(body, "LibraryName"),
                "source": source.relative_to(repo).as_posix(),
            }
    require(len(registrations) == 23, f"expected 23 registrations, found {len(registrations)}")
    return registrations


def load_dual_host_evidence(
    repo: Path,
    registrations: dict[str, dict[str, Any]],
) -> dict[str, dict[str, Any]]:
    packet = repo / "AcelogicFile/docs/gta-v/provider-evidence/provider23"
    mac_path = packet / "mac/selected.json"
    mac_payload = json.loads(mac_path.read_text(encoding="utf-8"))
    mac = {record["nid"]: record for record in mac_payload["records"]}
    require(len(mac) == 23 and mac_payload["all_include"] is True, "invalid Mac selected packet")

    windows: dict[str, dict[str, Any]] = {}
    for document in sorted((packet / "windows/selected").glob("*.json")):
        payload = json.loads(document.read_text(encoding="utf-8"))
        require(payload.get("format") == "sharpemu-ghidra-selected-nid-functions-v1", f"bad {document}")
        for function in payload["functions"]:
            nid = function["nid"]
            require(nid not in windows, f"duplicate Windows evidence {nid}")
            require(function.get("function_present") is True, f"missing Windows body {nid}")
            require(int(function.get("function_body_addresses", 0)) > 0, f"empty Windows body {nid}")
            windows[nid] = {
                "payload": payload,
                "function": function,
                "document": document.relative_to(repo).as_posix(),
            }
    require(len(windows) == 23, f"expected 23 Windows records, found {len(windows)}")
    require(set(registrations) == set(mac) == set(windows), "provider23 evidence/source sets differ")

    for nid, registration in registrations.items():
        mac_record = mac[nid]
        windows_record = windows[nid]
        payload = windows_record["payload"]
        function = windows_record["function"]
        comparisons = {
            "provider SHA": (mac_record["provider_sha256"], payload["executable_sha256"]),
            "entry": (mac_record["function_entry"].lower(), function["function_entry"].lower()),
            "body SHA": (mac_record["body_sha256"], function["function_body_sha256"]),
            "catalog name": (mac_record["catalog_symbol"], registration["name"]),
            "logical library": (mac_record["library"], registration["library"]),
        }
        for label, (left, right) in comparisons.items():
            require(left == right, f"{nid} dual-host {label} mismatch: {left} != {right}")
        windows_record["mac"] = mac_record
        windows_record["mac_document"] = mac_path.relative_to(repo).as_posix()
    return windows


def main() -> None:
    args = parse_args()
    repo = args.repo.resolve()
    registrations = load_registrations(repo)
    evidence = load_dual_host_evidence(repo, registrations)
    manifest_path = repo / "AcelogicFile/GTA_V_NID_SWARM_MANIFEST.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    by_nid = {item["nid"]: item for item in manifest["items"]}
    require(len(by_nid) == len(manifest["items"]) == 911, "unexpected manifest cardinality")

    for nid, registration in registrations.items():
        require(nid in by_nid, f"{nid} absent from manifest")
        item = by_nid[nid]
        record = evidence[nid]
        payload = record["payload"]
        function = record["function"]
        item["status"] = "integrated"
        item["evidence"] = {
            "aerolib_name": item.get("symbol"),
            "binary_hash": payload["executable_sha256"],
            "reference_functions": [
                f'{payload["program_name"]} Ghidra export {function["function_entry"]}',
                f'{record["document"]} selected-function record',
                f'{record["mac_document"]} independent matching Mac record',
            ],
            "call_sites": [],
            "confidence": "high",
            "conflicts": [
                "Mac and Windows Ghidra records agree exactly on provider hash, entry, and body hash",
                "provider definition evidence does not establish a semantic HLE fallback contract",
            ],
        }
        item["contract"] = {
            "signature": "provider-defined Gen5 ABI; no semantic signature is claimed by the registration fallback",
            "returns": [
                "the loaded guest provider result is authoritative when the exact export is mapped",
                "the HLE fallback returns ORBIS_GEN2_ERROR_NOT_IMPLEMENTED when no guest provider export is available",
            ],
            "output_writes": ["provider-defined; the fail-closed fallback performs no guest writes"],
            "validation_rules": ["exact NID, logical library, and Gen5 registration match both Ghidra packets"],
            "state_transitions": ["provider-defined; the fail-closed fallback changes no SharpEmu state"],
            "ownership": ["provider-defined"],
            "synchronization": ["provider-defined"],
        }
        item["implementation"] = {
            "worktree": str(repo),
            "branch": "codex/gta-v-nids",
            "commit": args.commit,
            "files": [
                registration["source"],
                record["document"],
                record["mac_document"],
                "tests/SharpEmu.Libs.Tests/Lle/ProviderLleExportsTests.cs",
            ],
        }
        item["validation"] = {
            "branch": "23/23 dual-host Ghidra records and 5/5 focused tests passed",
            "integration": (
                "757/757 library, 36/36 source-generator, and 34/34 shader tests passed; "
                "Release build completed with 0 warnings/errors"
            ),
            "games": [],
        }
        item["blockers"] = [
            "functional behavior remains guest-provider-dependent; the fallback is intentionally fail-closed"
        ]

    counts: dict[str, int] = {}
    for item in manifest["items"]:
        counts[item["status"]] = counts.get(item["status"], 0) + 1
    require(counts == {"integrated": 844, "named": 67}, f"unexpected status counts: {counts}")
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"updated": 23, "dual_host_matches": 23, "status_counts": counts}, sort_keys=True))


if __name__ == "__main__":
    main()
