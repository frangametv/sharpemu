#!/usr/bin/env python3
"""Record the Ghidra-backed GTA V provider-registration wave in the swarm manifest."""

from __future__ import annotations

import argparse
import json
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any


ATTRIBUTE_RE = re.compile(r"\[SysAbiExport\(\s*(.*?)\)\]", re.DOTALL)
STRING_FIELD_RE = re.compile(r'\b(Nid|ExportName|LibraryName)\s*=\s*"([^"]*)"')
LATER_PROVIDER_SOURCE_FILES = {
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


@dataclass(frozen=True)
class Registration:
    nid: str
    export_name: str
    library: str
    source: Path


@dataclass(frozen=True)
class GhidraEvidence:
    document: Path
    program_name: str
    executable_sha256: str
    function: dict[str, Any]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--repo",
        type=Path,
        default=Path(__file__).resolve().parents[1],
        help="SharpEmu worktree root",
    )
    parser.add_argument("--provider-commit", required=True)
    parser.add_argument("--semantic-commit", required=True)
    return parser.parse_args()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(message)


def relative(repo: Path, path: Path) -> str:
    return path.relative_to(repo).as_posix()


def load_registrations(repo: Path) -> dict[str, Registration]:
    sources = [repo / "src/SharpEmu.Libs/Np/NpCppWebApiLleExports.cs"]
    sources.extend(
        source
        for source in sorted((repo / "src/SharpEmu.Libs/Lle").glob("*LleExports.cs"))
        if source.name not in LATER_PROVIDER_SOURCE_FILES
    )

    registrations: dict[str, Registration] = {}
    for source in sources:
        text = source.read_text(encoding="utf-8")
        for match in ATTRIBUTE_RE.finditer(text):
            body = match.group(1)
            fields = dict(STRING_FIELD_RE.findall(body))
            if "PreferLle = true" not in body or "Target = Generation.Gen5" not in body:
                continue
            require(
                set(fields) == {"Nid", "ExportName", "LibraryName"},
                f"incomplete registration in {source}",
            )
            registration = Registration(
                nid=fields["Nid"],
                export_name=fields["ExportName"],
                library=fields["LibraryName"],
                source=source,
            )
            require(registration.nid not in registrations, f"duplicate NID {registration.nid}")
            registrations[registration.nid] = registration

    require(len(registrations) == 780, f"expected 780 provider registrations, found {len(registrations)}")
    return registrations


def body_measure(function: dict[str, Any]) -> int:
    for field in ("function_body_addresses", "function_body_bytes"):
        value = function.get(field)
        if isinstance(value, int):
            return value
    return 0


def load_evidence(repo: Path, selected_nids: set[str]) -> dict[str, GhidraEvidence]:
    documents = [repo / "docs/gta-v/npcppwebapi-lle-ghidra.json"]
    documents.extend(sorted((repo / "docs/gta-v/provider-evidence").glob("*.json")))

    evidence: dict[str, GhidraEvidence] = {}
    for document in documents:
        payload = json.loads(document.read_text(encoding="utf-8"))
        functions = payload.get("functions")
        if payload.get("format") != "sharpemu-ghidra-selected-nid-functions-v1" or not isinstance(functions, list):
            continue
        for function in functions:
            nid = function.get("nid")
            if nid not in selected_nids:
                continue
            require(nid not in evidence, f"duplicate Ghidra evidence for {nid}")
            require(function.get("function_present") is True, f"missing Ghidra function for {nid}")
            require(body_measure(function) > 0, f"empty Ghidra function body for {nid}")
            evidence[nid] = GhidraEvidence(
                document=document,
                program_name=payload["program_name"],
                executable_sha256=payload["executable_sha256"],
                function=function,
            )

    missing = sorted(selected_nids - evidence.keys())
    require(not missing, f"missing Ghidra evidence for {len(missing)} NIDs: {missing[:8]}")
    require(len(evidence) == 780, f"expected 780 evidence records, found {len(evidence)}")
    return evidence


def provider_validation(registration: Registration) -> dict[str, Any]:
    if registration.library == "libSceNpCppWebApi":
        return {
            "branch": "exact Ghidra evidence and registration-set tests passed",
            "integration": (
                "755/755 library, 36/36 source-generator, and 34/34 shader tests passed; "
                "Release build completed with 0 warnings/errors"
            ),
            "games": [
                {
                    "game": "Grand Theft Auto V",
                    "passed": True,
                    "run": (
                        "all 436 newly registered libSceNpCppWebApi NIDs resolved as direct guest-provider bridges; "
                        "the process later reached import ordinal 41427"
                    ),
                }
            ],
        }
    return {
        "branch": "exact Ghidra evidence and registration-set tests passed",
        "integration": (
            "755/755 library, 36/36 source-generator, and 34/34 shader tests passed; "
            "Release build completed with 0 warnings/errors"
        ),
        "games": [],
    }


def update_provider_item(
    repo: Path,
    item: dict[str, Any],
    registration: Registration,
    evidence: GhidraEvidence,
    provider_commit: str,
) -> None:
    function = evidence.function
    entry = function.get("function_entry") or function.get("symbol_address")
    item["status"] = "integrated"
    item["evidence"] = {
        "aerolib_name": item.get("symbol"),
        "binary_hash": evidence.executable_sha256,
        "reference_functions": [
            f"{evidence.program_name} Ghidra export {entry}",
            f"{relative(repo, evidence.document)} selected-function record",
        ],
        "call_sites": [],
        "confidence": "high",
        "conflicts": [
            "semantic ABI and behavior are not inferred from registration evidence; the loaded guest provider remains authoritative"
        ],
    }
    item["contract"] = {
        "signature": "provider-defined Gen5 ABI; no semantic signature is claimed by the registration fallback",
        "returns": [
            "the loaded guest provider result is authoritative when the exact export is mapped",
            "the HLE fallback returns ORBIS_GEN2_ERROR_NOT_IMPLEMENTED when no guest provider export is available",
        ],
        "output_writes": ["provider-defined; the fail-closed fallback performs no guest writes"],
        "validation_rules": ["exact NID, library, and Gen5 registration must match the Ghidra-selected provider export"],
        "state_transitions": ["provider-defined; the fail-closed fallback changes no SharpEmu state"],
        "ownership": ["provider-defined"],
        "synchronization": ["provider-defined"],
    }
    test_file = (
        "tests/SharpEmu.Libs.Tests/Np/NpCppWebApiLleExportsTests.cs"
        if registration.library == "libSceNpCppWebApi"
        else "tests/SharpEmu.Libs.Tests/Lle/ProviderLleExportsTests.cs"
    )
    item["implementation"] = {
        "worktree": str(repo),
        "branch": "codex/gta-v-nids",
        "commit": provider_commit,
        "files": [
            relative(repo, registration.source),
            relative(repo, evidence.document),
            test_file,
        ],
    }
    item["validation"] = provider_validation(registration)
    item["blockers"] = (
        []
        if registration.library == "libSceNpCppWebApi"
        else [
            "the matching firmware provider was not mapped during the GTA V validation run, so functional behavior remains provider-loading-dependent"
        ]
    )


def update_semantic_item(repo: Path, item: dict[str, Any], semantic_commit: str) -> None:
    item["status"] = "integrated"
    item["observations"] = [
        {
            "game": "Grand Theft Auto V",
            "calls": 1,
            "source": "GTA V x64 provider-wave runtime trace",
            "importing_image": "eboot.bin",
            "call_sites": ["return 0x0000000802957516"],
            "run_configuration": "x64 native CPU engine with import tracing",
        }
    ]
    item["priority"] = {
        "calls": 1,
        "criticality": "fatal",
        "cross_game_fanout": 1,
        "dependency_order": 39003,
        "rationale": "GTA V calls (0, 0x1FF) and requires a zero result to clear the setup gate",
    }
    item["evidence"] = {
        "aerolib_name": "sceAgcDriverSetHsOffchipParam",
        "binary_hash": "bc2ca28f3632ce69e25ab44991ed1f49bc1624fe39c2fc81f2efc6e705876348",
        "reference_functions": [
            "libSceAgcDriver.sprx export 0x70B0",
            "selected callback 0x6FC0",
            "driver helper 0x9D00",
        ],
        "call_sites": ["GTA runtime return 0x0000000802957516"],
        "confidence": "high",
        "conflicts": [],
    }
    item["contract"] = {
        "signature": "int32 sceAgcDriverSetHsOffchipParam(uint32 first, uint32 second)",
        "returns": ["0 on accepted update", "0x8A6DFFFF when submitted AGC state is unavailable or the driver rejects the request"],
        "output_writes": ["none"],
        "validation_rules": [
            "both inputs are reduced to their low 16 bits",
            "the four-byte driver payload stores second at offset 0 and first at offset 2",
        ],
        "state_transitions": ["success atomically replaces the effective Hs-offchip parameter pair; failure preserves prior state"],
        "ownership": ["SharpEmu stores only the effective parameter pair in per-memory submitted AGC state"],
        "synchronization": ["the existing submitted-AGC-state lock prevents torn reads and writes"],
    }
    item["implementation"] = {
        "worktree": str(repo),
        "branch": "codex/gta-v-nids",
        "commit": semantic_commit,
        "files": [
            "src/SharpEmu.Libs/Agc/AgcExports.cs",
            "tests/SharpEmu.Libs.Tests/Agc/AgcDriverHsOffchipParamTests.cs",
            "docs/gta-v/agc-driver-hs-offchip-param.md",
        ],
    }
    item["validation"] = {
        "branch": "6/6 focused tests and the full branch lanes passed",
        "integration": (
            "755/755 library, 36/36 source-generator, and 34/34 shader tests passed; "
            "Release build completed with 0 warnings/errors"
        ),
        "games": [
            {
                "game": "Grand Theft Auto V",
                "passed": True,
                "run": "the former import-39003 gate returned zero and runtime advanced to import ordinal 41427",
            }
        ],
    }
    item["blockers"] = []


def main() -> None:
    args = parse_args()
    repo = args.repo.resolve()
    manifest_path = repo / "GTA_V_NID_SWARM_MANIFEST.json"
    registrations = load_registrations(repo)
    evidence = load_evidence(repo, set(registrations))
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    items = manifest["items"]
    by_nid = {item["nid"]: item for item in items}
    require(len(by_nid) == len(items) == 911, "manifest must contain 911 unique queue items")
    require(set(registrations) <= by_nid.keys(), "provider registration is absent from the manifest")
    require("MM4IZSEYytQ" in by_nid, "semantic MM4 item is absent from the manifest")

    for nid, registration in registrations.items():
        update_provider_item(repo, by_nid[nid], registration, evidence[nid], args.provider_commit)
    update_semantic_item(repo, by_nid["MM4IZSEYytQ"], args.semantic_commit)

    counts: dict[str, int] = {}
    for item in items:
        counts[item["status"]] = counts.get(item["status"], 0) + 1
    require(sum(counts.values()) == 911, f"unexpected manifest cardinality: {counts}")
    require(counts.get("integrated", 0) >= 821, f"provider wave is not fully integrated: {counts}")

    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"updated": 781, "status_counts": counts}, sort_keys=True))


if __name__ == "__main__":
    main()
