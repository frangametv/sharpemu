#!/usr/bin/env python3
"""Validate and record the final 67-NID GTA V Gen5 parity wave."""

from __future__ import annotations

import argparse
import csv
import gzip
import hashlib
import json
import re
import subprocess
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


INVENTORY_RELATIVE = Path("AcelogicFile/docs/gta-v/gta-v-gen5-nid-inventory-base-615bae08.csv")
INVENTORY_SHA256 = "efb0a69b0e5e32274db2ca86558041318e9ba65011c0d94f3362629bf826f73a"
MANIFEST_RELATIVE = Path("AcelogicFile/GTA_V_NID_SWARM_MANIFEST.json")
UNCOVERED_RELATIVE = Path("AcelogicFile/GTA_V_UNCOVERED_NIDS.csv")
RHO_PACKET = Path("AcelogicFile/artifacts/gta-v-nid-evidence/rho-remaining90-contracts-20260718")
LIBC_QUEUE = Path("AcelogicFile/docs/gta-v/provider-evidence/libc35/prefer-lle-registration-queue.csv")
LIBC_NON_LLE_QUEUE = RHO_PACKET / "libc-non-lle-contract-queue.csv"
KERNEL_QUEUE = RHO_PACKET / "kernel-hle-contract-queue.csv"
DATA_QUEUE = RHO_PACKET / "data-import-disposition.csv"
CONSOLIDATED_EVIDENCE = RHO_PACKET / "consolidated-nid-evidence.json"
OBJECT_EVIDENCE = Path("AcelogicFile/artifacts/gta-v-nid-evidence/data5-objects-20260718/GHIDRA_OBJECT_EVIDENCE.json")
VALIDATION_EVIDENCE = Path("AcelogicFile/artifacts/gta-v-nid-evidence/final-parity-validation-20260718.json")
HISTORICAL_RUNTIME_COMMIT = "4ea43616102ba8b2a5bf59b745cd3b758d05e110"
CURRENT_RUNTIME_COMMIT = "b591baa1aab949e63c48d790b067d5beeb47b091"
ATTRIBUTE_RE = re.compile(r"\[SysAbiExport\(\s*(.*?)\)\]", re.DOTALL)
EXPECTED_UNCOVERED_HEADER = [
    "component",
    "calls",
    "nid",
    "catalog_symbol",
    "module",
    "library",
    "importing_image",
    "symbol_kind",
    "relocation_referenced",
    "relocation_count",
    "observation_kind",
    "acelogic_base_sha",
]

LIBC_SOURCES = (
    Path("src/SharpEmu.Libs/Lle/LibcProviderLleExports.cs"),
    Path("src/SharpEmu.Libs/Lle/LibcInternalProviderLleExports.cs"),
    Path("src/SharpEmu.Libs/LibcInternalBacktraceExports.cs"),
)
KERNEL_SOURCE = Path("src/SharpEmu.Libs/Kernel/GtaVKernelContractExports.cs")
DATA_SOURCE = Path("src/SharpEmu.HLE/DataSymbolRegistry.cs")

BACKTRACE_NID = "EHsF2i9FXPM"
LEGACY_STACK_GUARD_NID = "f7uOxY9mM1U"
DATA_REGISTRATIONS = {
    "djxxOmW6-aw": ("__progname", "libkernel", "ProgNameNid"),
    "P330P3dFF68": ("Need_sceLibc", "libc", "LibcNeedFlagNid"),
    "ZT4ODD2Ts9o": ("Need_sceLibcInternal", "libSceLibcInternal", "LibcInternalNeedFlagNid"),
    "H8AprKeZtNg": ("_Stderr", "libc", "StderrNid"),
    "2sWzhYqFH4E": ("_Stdout", "libc", "StdoutNid"),
}
KERNEL_SEMANTIC_NIDS = {
    "NhpspxdjEKU",  # _nanosleep
    "c7ZnT7V1B98",  # rmdir
    "cfwBSQyr5Ys",  # diagnostic sink
    "VAzswvTOCzI",  # unlink
    "TXFFFiNldU8",  # getpeername
    "5jRCs2axtr4",  # inet_ntop
    "Ez8xjo9UF4E",  # recv, flags == 0
    "fZOeZIOEmLw",  # send, flags == 0
    "TUuiYS2kE8s",  # shutdown
}
KERNEL_PARTIAL_NIDS = {
    "Ez8xjo9UF4E",  # recv is semantic only for flags == 0
    "fZOeZIOEmLw",  # send is semantic only for flags == 0
}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(message)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def read_csv(path: Path) -> tuple[list[str], list[dict[str, str]]]:
    with path.open(newline="", encoding="utf-8") as handle:
        reader = csv.DictReader(handle)
        require(reader.fieldnames is not None, f"{path} has no header")
        return list(reader.fieldnames), list(reader)


def field(body: str, name: str) -> str:
    match = re.search(rf'\b{name}\s*=\s*"([^"]+)"', body)
    require(match is not None, f"missing {name} in SysAbiExport")
    return match.group(1)


def parse_exports(repo: Path, sources: tuple[Path, ...]) -> dict[str, dict[str, Any]]:
    exports: dict[str, dict[str, Any]] = {}
    for relative in sources:
        text = (repo / relative).read_text(encoding="utf-8")
        for match in ATTRIBUTE_RE.finditer(text):
            body = match.group(1)
            require("Target = Generation.Gen5" in body, f"non-Gen5 export in {relative}")
            require("Generation.Gen4" not in body, f"Gen4 leakage in {relative}")
            nid = field(body, "Nid")
            require(nid not in exports, f"duplicate final-wave callable NID {nid}")
            exports[nid] = {
                "nid": nid,
                "name": field(body, "ExportName"),
                "library": field(body, "LibraryName"),
                "prefer_lle": "PreferLle = true" in body,
                "source": relative.as_posix(),
            }
    return exports


def verify_commit(
    repo: Path,
    commit: str,
    required_files: set[str],
    required_diff_markers: tuple[str, ...] = (),
) -> str:
    resolved = subprocess.run(
        ["git", "rev-parse", f"{commit}^{{commit}}"],
        cwd=repo,
        check=True,
        text=True,
        capture_output=True,
    ).stdout.strip()
    ancestry = subprocess.run(
        ["git", "merge-base", "--is-ancestor", resolved, "HEAD"],
        cwd=repo,
        check=False,
    )
    require(ancestry.returncode == 0, f"commit {resolved} is not integrated into HEAD")
    changed = set(subprocess.run(
        ["git", "diff-tree", "--no-commit-id", "--name-only", "-r", resolved],
        cwd=repo,
        check=True,
        text=True,
        capture_output=True,
    ).stdout.splitlines())
    require(required_files <= changed, f"commit {resolved} is missing required files: {sorted(required_files - changed)}")
    diff = subprocess.run(
        ["git", "show", "--format=", "--no-ext-diff", resolved],
        cwd=repo,
        check=True,
        text=True,
        capture_output=True,
    ).stdout
    for marker in required_diff_markers:
        require(marker in diff, f"commit {resolved} is missing required marker: {marker}")
    return resolved


def require_ancestor(repo: Path, ancestor: str, descendant: str, label: str) -> None:
    result = subprocess.run(
        ["git", "merge-base", "--is-ancestor", ancestor, descendant],
        cwd=repo,
        check=False,
    )
    require(result.returncode == 0, f"{label}: {ancestor} is not an ancestor of {descendant}")


def load_validation_evidence(
    repo: Path,
    commits: dict[str, str],
) -> tuple[str, str]:
    path = repo / VALIDATION_EVIDENCE
    payload = json.loads(path.read_text(encoding="utf-8"))
    require(payload.get("format") == "sharpemu-gta-v-final-parity-validation-v1", "bad final validation format")
    require(payload.get("pinned_inventory_sha256") == INVENTORY_SHA256, "validation inventory hash mismatch")
    validated_commit = verify_commit(
        repo,
        payload.get("validated_commit", ""),
        set(),
    )
    parity_test_at_commit = subprocess.run(
        ["git", "cat-file", "-e", f"{validated_commit}:tests/SharpEmu.Libs.Tests/GtaV/GtaVGen5RegistrationParityTests.cs"],
        cwd=repo,
        check=False,
    )
    require(parity_test_at_commit.returncode == 0, "validated commit predates the compiled parity test")
    for lane, commit in commits.items():
        require_ancestor(repo, commit, validated_commit, f"validation does not contain {lane}")

    tests = {record["name"]: record for record in payload.get("tests", [])}
    expected_tests = {
        "focused_gta_parity": 52,
        "SharpEmu.Libs.Tests": 1068,
        "SharpEmu.SourceGenerators.Tests": 36,
        "SharpEmu.ShaderCompiler.Tests": 35,
        "SharpEmu.ShaderCompiler.Metal.Tests": 27,
    }
    require(set(tests) == set(expected_tests), "validation test set mismatch")
    for name, expected_passed in expected_tests.items():
        record = tests[name]
        require(record.get("passed") == expected_passed, f"{name} passed-count mismatch")
        require(record.get("failed") == 0 and record.get("skipped") == 0, f"{name} is not fully green")
        require(bool(record.get("command")), f"{name} command is missing")

    build = payload.get("release_build", {})
    require(build.get("succeeded") is True, "Release build did not succeed")
    require(build.get("warnings") == 65 and build.get("errors") == 0, "Release build result mismatch")
    require(
        build.get("command")
        == "dotnet publish src/SharpEmu.CLI/SharpEmu.CLI.csproj -c Release -r win-x64 "
        "--self-contained true --no-restore --nologo -o /tmp/sharpemu-gta-d3c90e3.DJZwsn",
        "Release publish command mismatch",
    )
    require(build.get("runtime_identifier") == "win-x64", "Release publish RID mismatch")
    require(build.get("self_contained") is True, "Release publish is not self-contained")
    require(build.get("executable") == "SharpEmu.exe", "Release executable name mismatch")
    require(
        build.get("executable_sha256")
        == "61db9b23ffb61c8889cf84b30385f8f038e7e4a6ecd8530c04b0daacab87cb03",
        "Release executable hash mismatch",
    )

    current_runtime = payload.get("current_runtime_smoke", {})
    current_runtime_commit = verify_commit(
        repo,
        current_runtime.get("validated_commit", ""),
        set(),
    )
    require(current_runtime_commit == CURRENT_RUNTIME_COMMIT, "current GTA runtime smoke commit mismatch")
    require_ancestor(
        repo,
        current_runtime_commit,
        validated_commit,
        "validated build does not contain current GTA runtime smoke commit",
    )
    require(
        current_runtime.get("trace_scope")
        == "ten-minute unfiltered Windows smoke plus short targeted mutex trace",
        "current GTA runtime smoke scope mismatch",
    )
    require(
        current_runtime.get("target_mutex") == "0x00000008045755C8",
        "current GTA runtime smoke mutex mismatch",
    )
    require(
        current_runtime.get("handoff_threads") == ["[RAGE] RenderThread", "[RAGE] Main Thread"],
        "current GTA runtime smoke handoff mismatch",
    )
    require(
        current_runtime.get("target_mutex_permanent_starvation") is False,
        "current GTA runtime smoke still reports permanent target-mutex starvation",
    )
    require(
        current_runtime.get("snapshot_count") == 61
        and current_runtime.get("main_import_delta") == 45923560
        and current_runtime.get("render_import_delta") == 4588828,
        "current GTA runtime progress counters mismatch",
    )
    require(
        current_runtime.get("traced_flips_enqueued") == 64
        and current_runtime.get("traced_flips_presented") == 64,
        "current GTA flip trace mismatch",
    )
    require(
        current_runtime.get("stable_frame_sha256")
        == "83e9d1f12db5a8aec7088b6b5b469cf2a6bf6ca9ac80ff61d4e89e06e3b82267",
        "current GTA stable frame hash mismatch",
    )
    require(
        current_runtime.get("screen_checkpoint") == "GTA V logo with green spinner",
        "current GTA runtime smoke screen checkpoint mismatch",
    )
    require(
        current_runtime.get("status") == "alive_at_ten_minute_checkpoint",
        "current GTA unfiltered run status mismatch",
    )
    require(
        current_runtime.get("delta_from_0996")
        == "no visual or normalized main/render throughput improvement",
        "current GTA runtime delta mismatch",
    )

    runtime = payload.get("runtime", {})
    require(runtime.get("historical") is True, "GTA runtime evidence is not marked historical")
    require(
        runtime.get("validated_commit") == HISTORICAL_RUNTIME_COMMIT,
        "historical GTA runtime commit mismatch",
    )
    runtime_log = repo / runtime.get("compressed_log", "")
    require(runtime_log.is_file(), "compressed final GTA runtime log is missing")
    require(sha256(runtime_log) == runtime.get("compressed_sha256"), "compressed GTA runtime log hash mismatch")
    _, libc_rows = read_csv(repo / LIBC_QUEUE)
    expected_libc_nids = {row["nid"] for row in libc_rows}
    require(len(expected_libc_nids) == 34, "runtime validator libc queue drift")
    expected_libc_targets: dict[str, set[str]] = {}
    for row in libc_rows:
        targets = {
            address.lower()
            for address in row["runtime_symbol_addresses"].split(";")
            if address
        }
        computed_target = f"0x{int(row['runtime_load_base'], 16) + int(row['function_entry'], 16):016x}"
        require(targets == {computed_target}, f"libc runtime target evidence mismatch for {row['nid']}")
        expected_libc_targets[row["nid"]] = targets
    object_nids = set(DATA_REGISTRATIONS)
    callable_markers = (
        "SetupImportStubs: Direct bridge for",
        "[LOADER][INFO] LLE redirect:",
        "SetupImportStubs: Trampoline for",
    )
    direct_targets: dict[str, set[str]] = {}
    object_callable_events = 0
    max_import = 0
    mm4_checkpoint_reached = False
    data_rebound = None
    data_unresolved = None
    terminal: tuple[str, str, str, str] | None = None
    fault_thread: tuple[str, str] | None = None
    raw_digest = hashlib.sha256()
    raw_bytes = 0
    raw_lines = 0
    with gzip.open(runtime_log, "rb") as handle:
        for raw_line in handle:
            raw_digest.update(raw_line)
            raw_bytes += len(raw_line)
            raw_lines += 1
            line = raw_line.decode("utf-8", errors="replace")
            direct_match = re.search(
                r"SetupImportStubs: Direct bridge for (\S+) -> (0x[0-9A-Fa-f]+)",
                line,
            )
            if direct_match:
                direct_targets.setdefault(direct_match.group(1), set()).add(direct_match.group(2).lower())
            if any(nid in line for nid in object_nids) and any(marker in line for marker in callable_markers):
                object_callable_events += 1
            import_match = re.search(r"Import#(\d+)", line)
            if import_match:
                max_import = max(max_import, int(import_match.group(1)))
            if "MM4IZSEYytQ" in line and import_match:
                mm4_checkpoint_reached = True
            rebind_match = re.search(r"Imported data rebind: rebound=(\d+), unresolved=(\d+)", line)
            if rebind_match:
                data_rebound = int(rebind_match.group(1))
                data_unresolved = int(rebind_match.group(2))
            terminal_match = re.search(
                r"posix-signal#\d+: sig=(\d+) rip=(0x[0-9A-Fa-f]+) fault=(0x[0-9A-Fa-f]+) access=(\d+)",
                line,
            )
            if terminal_match:
                terminal = terminal_match.groups()
            fault_thread_match = re.search(
                r"Guest thread: .* name='([^']+)' state=\S+ last_import=(\S+) last_ret=",
                line,
            )
            if fault_thread_match:
                fault_thread = fault_thread_match.groups()

    libc_mismatches = {
        nid: {
            "expected": sorted(expected_libc_targets[nid]),
            "observed": sorted(direct_targets.get(nid, set())),
        }
        for nid in expected_libc_nids
        if direct_targets.get(nid, set()) != expected_libc_targets[nid]
    }
    require(raw_digest.hexdigest() == runtime.get("raw_sha256"), "raw GTA runtime log hash mismatch")
    require(raw_bytes == runtime.get("raw_bytes") and raw_lines == runtime.get("raw_lines"), "runtime size mismatch")
    require(not libc_mismatches, f"libc provider-target routing mismatch: {libc_mismatches}")
    require(object_callable_events == 0, "data object was routed as a callable import")
    require(max_import >= 41_427, "GTA runtime regressed before the prior import checkpoint")
    require(mm4_checkpoint_reached, "GTA runtime missed the MM4 checkpoint")
    require(data_rebound == 11 and data_unresolved == 0, "GTA data relocation rebind mismatch")
    require(terminal is not None, "GTA terminal signal evidence is missing")
    require(list(terminal) == runtime.get("terminal_signal"), "GTA terminal signal evidence mismatch")
    require(fault_thread is not None, "GTA faulting-thread evidence is missing")
    require(fault_thread[0] == runtime.get("terminal_guest_thread"), "GTA faulting thread mismatch")
    require(fault_thread[1] == runtime.get("terminal_last_import"), "GTA faulting-thread last import mismatch")
    require(runtime.get("exit_code") == 139, "GTA final trace exit code mismatch")
    require(runtime.get("exit_capture") == "rc=$?", "GTA runtime exit-capture recipe is missing")
    require(bool(runtime.get("command")), "GTA runtime command is missing")
    require(
        "> artifacts/gta-v-final-parity-x64.log 2>&1" in runtime["command"],
        "GTA runtime log redirection is missing",
    )

    integration_summary = (
        f"Validated at {validated_commit}: 1068/1068 library tests, 36/36 source-generator tests, "
        "35/35 shader tests, and 27/27 Metal shader tests passed (1166/1166 full-suite total); "
        "the separate focused GTA parity gate passed 52/52; win-x64 self-contained Release publish "
        "completed with 65 warnings/0 errors"
    )
    runtime_summary = (
        f"Historical 2026-07-18 GTA V x64 trace: 34/34 libc provider routes, 0 object callable events, "
        f"11 data relocations rebound with 0 unresolved, max import {max_import}; "
        f"terminal sig={terminal[0]} RIP={terminal[1]} fault={terminal[2]} access={terminal[3]}. "
        f"Current ten-minute Windows smoke at {current_runtime_commit}: Render/Main both repeatedly acquired "
        "mutex 0x00000008045755C8, disproving permanent starvation; 64/64 traced flips presented, but "
        "the stable frame remains the GTA V logo with green spinner and normalized throughput did not "
        "improve over 0996dab"
    )
    return integration_summary, runtime_summary


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--libc-commit", required=True)
    parser.add_argument("--kernel-commit", required=True)
    parser.add_argument("--data-commit", required=True)
    parser.add_argument("--hardening-commit", required=True)
    parser.add_argument("--check-only", action="store_true")
    parser.add_argument(
        "--repo",
        type=Path,
        default=Path(__file__).resolve().parents[2],
    )
    return parser.parse_args()


def common_validation(
    integration_validation: str,
    runtime_validation: str,
    lane: str,
) -> dict[str, Any]:
    return {
        "branch": lane,
        "integration": integration_validation,
        "games": [runtime_validation],
    }


def main() -> None:
    args = parse_args()
    repo = args.repo.resolve()
    commits = {
        "libc": verify_commit(
            repo,
            args.libc_commit,
            {
                "src/SharpEmu.Libs/Lle/LibcProviderLleExports.cs",
                "tests/SharpEmu.Libs.Tests/Lle/Libc35ExportsTests.cs",
                "docs/gta-v/libc35-lle-ghidra.md",
            },
            ("PreferLle = true", "EHsF2i9FXPM"),
        ),
        "kernel": verify_commit(
            repo,
            args.kernel_commit,
            {
                KERNEL_SOURCE.as_posix(),
                "tests/SharpEmu.Libs.Tests/Kernel/GtaVKernelContractExportsTests.cs",
                "docs/gta-v/kernel27-ghidra-contracts.md",
            },
            ("PreferLle = false", "ORBIS_GEN2_ERROR_NOT_IMPLEMENTED"),
        ),
        "data": verify_commit(
            repo,
            args.data_commit,
            {
                DATA_SOURCE.as_posix(),
                "src/SharpEmu.Core/Runtime/ImportedDataRebinder.cs",
                "tests/SharpEmu.Libs.Tests/Loader/DataSymbolRegistrationTests.cs",
            },
            ("DataSymbolRegistry", "GuestAuthoritative"),
        ),
        "hardening": verify_commit(
            repo,
            args.hardening_commit,
            {
                "src/SharpEmu.Core/Loader/SelfLoader.cs",
                "src/SharpEmu.Core/Cpu/Native/DirectExecutionBackend.Imports.cs",
                "tests/SharpEmu.Libs.Tests/Loader/DataSymbolRegistrationTests.cs",
            },
            ("_runtimeDataSymbolsByName", "symbolAddress = 0", "isData ? runtimeDataSymbols : runtimeSymbols"),
        ),
    }
    require(len(set(commits.values())) == 4, "lane implementation commits must be distinct")
    integration_validation, runtime_validation = load_validation_evidence(repo, commits)

    inventory_path = repo / INVENTORY_RELATIVE
    require(sha256(inventory_path) == INVENTORY_SHA256, "pinned 1,432-NID inventory hash mismatch")
    _, inventory = read_csv(inventory_path)
    inventory_nids = [row["nid"] for row in inventory]
    require(len(inventory_nids) == len(set(inventory_nids)) == 1_432, "inventory cardinality mismatch")
    require(
        Counter(row["symbol_kinds"] for row in inventory) == {"function": 1_426, "object": 6},
        "inventory kind split mismatch",
    )

    libc_exports = parse_exports(repo, LIBC_SOURCES)
    kernel_exports = parse_exports(repo, (KERNEL_SOURCE,))
    require(len(libc_exports) == 35, f"expected 35 libc-family exports, found {len(libc_exports)}")
    require(len(kernel_exports) == 27, f"expected 27 kernel/POSIX exports, found {len(kernel_exports)}")
    require(sum(export["prefer_lle"] for export in libc_exports.values()) == 34, "libc PreferLle split mismatch")
    require(not libc_exports[BACKTRACE_NID]["prefer_lle"], "backtrace must not PreferLle")
    require(not any(export["prefer_lle"] for export in kernel_exports.values()), "kernel/POSIX must remain HLE-bound")

    data_source_text = (repo / DATA_SOURCE).read_text(encoding="utf-8")
    for nid, (name, library, constant_name) in DATA_REGISTRATIONS.items():
        constant_pattern = re.compile(
            rf'public const string {re.escape(constant_name)}\s*=\s*"{re.escape(nid)}";'
        )
        registration_pattern = re.compile(
            rf'new\(\s*"{re.escape(library)}",\s*{re.escape(constant_name)},\s*"{re.escape(name)}",\s*Generation\.Gen5,',
            re.DOTALL,
        )
        require(constant_pattern.search(data_source_text) is not None, f"missing data NID constant {nid}")
        require(registration_pattern.search(data_source_text) is not None, f"missing exact data registration tuple {nid}")
    data_nids_in_callable_attributes: set[str] = set()
    for source in (repo / "src").rglob("*.cs"):
        for match in ATTRIBUTE_RE.finditer(source.read_text(encoding="utf-8")):
            body = match.group(1)
            data_nids_in_callable_attributes.update(
                nid for nid in DATA_REGISTRATIONS if f'"{nid}"' in body
            )
    require(not data_nids_in_callable_attributes, "data NID leaked into callable registry")

    _, libc_queue_rows = read_csv(repo / LIBC_QUEUE)
    _, non_lle_rows = read_csv(repo / LIBC_NON_LLE_QUEUE)
    _, kernel_queue_rows = read_csv(repo / KERNEL_QUEUE)
    _, data_queue_rows = read_csv(repo / DATA_QUEUE)
    libc_evidence = {row["nid"]: row for row in libc_queue_rows}
    non_lle_evidence = {row["nid"]: row for row in non_lle_rows}
    kernel_evidence = {row["nid"]: row for row in kernel_queue_rows}
    data_evidence = {row["nid"]: row for row in data_queue_rows}
    require(len(libc_evidence) == 34, "libc evidence cardinality mismatch")
    require(set(non_lle_evidence) == {BACKTRACE_NID}, "backtrace evidence mismatch")
    require(set(kernel_evidence) == set(kernel_exports), "kernel source/evidence set mismatch")
    require(set(data_evidence) == set(DATA_REGISTRATIONS), "data source/evidence set mismatch")
    require(set(libc_evidence) | {BACKTRACE_NID} == set(libc_exports), "libc source/evidence set mismatch")
    for nid, row in libc_evidence.items():
        export = libc_exports[nid]
        require(export["name"] == row["catalog_symbol"], f"libc export-name mismatch for {nid}")
        require(export["library"] == row["library"], f"libc library mismatch for {nid}")
        require(export["prefer_lle"], f"libc provider export is not PreferLle: {nid}")
    backtrace_row = non_lle_evidence[BACKTRACE_NID]
    require(libc_exports[BACKTRACE_NID]["name"] == backtrace_row["catalog_symbol"], "backtrace name mismatch")
    require(libc_exports[BACKTRACE_NID]["library"] == backtrace_row["library"], "backtrace library mismatch")
    for nid, row in kernel_evidence.items():
        export = kernel_exports[nid]
        require(export["name"] == row["catalog_symbol"], f"kernel export-name mismatch for {nid}")
        require(export["library"] == row["library"], f"kernel library mismatch for {nid}")

    object_payload = json.loads((repo / OBJECT_EVIDENCE).read_text(encoding="utf-8"))
    require(object_payload.get("schema_version") == 1, "object evidence schema mismatch")
    object_evidence = {record["nid"]: record for record in object_payload.get("objects", [])}
    require(len(object_evidence) == 5 and set(object_evidence) == set(DATA_REGISTRATIONS), "object evidence set mismatch")
    positive_object_providers: dict[str, list[dict[str, Any]]] = {}
    for nid, (name, library, _) in DATA_REGISTRATIONS.items():
        record = object_evidence[nid]
        require(record.get("name") == name, f"object evidence name mismatch for {nid}")
        require(record.get("logical_library") == library, f"object evidence library mismatch for {nid}")
        providers = [
            provider for provider in record.get("providers", [])
            if provider.get("status") == "symbol_without_function"
            and provider.get("address")
            and re.fullmatch(r"[0-9a-f]{64}", provider.get("sha256", ""))
        ]
        require(providers, f"object evidence has no positive Ghidra provider for {nid}")
        positive_object_providers[nid] = providers

    consolidated_payload = json.loads((repo / CONSOLIDATED_EVIDENCE).read_text(encoding="utf-8"))
    consolidated_rows = {row["nid"]: row for row in consolidated_payload.get("rows", [])}
    require(len(consolidated_rows) == 67, "consolidated Ghidra packet cardinality mismatch")
    backtrace_provider = consolidated_rows[BACKTRACE_NID]["provider_evidence"]["libSceLibcInternal"]
    require(backtrace_provider.get("function_present") is True, "backtrace positive provider is absent")
    require(backtrace_provider["function_entry"] == backtrace_row["firmware_function_entry"], "backtrace entry mismatch")
    require(backtrace_provider["function_body_sha256"] == backtrace_row["firmware_function_body_sha256"], "backtrace body hash mismatch")
    require(backtrace_provider["decompile_sha256"] == backtrace_row["firmware_decompile_sha256"], "backtrace decompile hash mismatch")

    final_wave_nids = set(libc_exports) | set(kernel_exports) | set(DATA_REGISTRATIONS)
    require(len(final_wave_nids) == 67, f"final wave has {len(final_wave_nids)} NIDs, expected 67")
    uncovered_path = repo / UNCOVERED_RELATIVE
    uncovered_header, uncovered = read_csv(uncovered_path)
    require(uncovered_header == EXPECTED_UNCOVERED_HEADER, "uncovered CSV header mismatch")
    uncovered_nid_list = [row["nid"] for row in uncovered]
    require(len(uncovered_nid_list) in {0, 67}, "uncovered CSV row count must be 67 or 0")
    require(len(uncovered_nid_list) == len(set(uncovered_nid_list)), "uncovered CSV contains duplicate NIDs")
    require(
        not uncovered_nid_list or set(uncovered_nid_list) == final_wave_nids,
        "uncovered CSV is neither the exact final 67 queue nor the completed empty queue",
    )

    manifest_path = repo / MANIFEST_RELATIVE
    original_manifest_text = manifest_path.read_text(encoding="utf-8")
    manifest = json.loads(original_manifest_text)
    by_nid = {item["nid"]: item for item in manifest["items"]}
    require(len(by_nid) == len(manifest["items"]) == 911, "manifest cardinality mismatch")
    require(final_wave_nids <= set(by_nid), "final wave NID absent from manifest")
    non_integrated = {item["nid"] for item in manifest["items"] if item["status"] != "integrated"}
    require(not non_integrated or non_integrated == final_wave_nids, "manifest non-integrated set mismatch")
    input_already_final = not uncovered_nid_list and not non_integrated

    for nid, export in libc_exports.items():
        item = by_nid[nid]
        item["status"] = "integrated"
        if nid == BACKTRACE_NID:
            row = non_lle_evidence[nid]
            item["evidence"] = {
                "aerolib_name": item.get("symbol"),
                "binary_hash": backtrace_provider["provider_sha256"],
                "reference_functions": [
                    f"firmware Ghidra export {row['firmware_function_entry']}",
                    f"body SHA-256 {row['firmware_function_body_sha256']}",
                    CONSOLIDATED_EVIDENCE.as_posix(),
                    LIBC_NON_LLE_QUEUE.as_posix(),
                ],
                "call_sites": [],
                "confidence": row["confidence"],
                "conflicts": ["Ghidra found a diagnostic body, but GTA did not resolve its library namespace at runtime"],
            }
            item["contract"] = {
                "signature": row["abi_summary"],
                "returns": [row["return_error_contract"], "SharpEmu currently fails closed with ORBIS_GEN2_ERROR_NOT_IMPLEMENTED"],
                "output_writes": [row["output_state_contract"], "the fail-closed path writes no guest output"],
                "validation_rules": [row["implementation_gate"]],
                "state_transitions": ["none on the fail-closed path"],
                "ownership": ["diagnostic-only; no retained guest ownership"],
                "synchronization": ["none on the fail-closed path"],
            }
            item["blockers"] = ["runtime provider routing or a fuller diagnostic/backtrace HLE contract remains unproven"]
        else:
            row = libc_evidence[nid]
            item["evidence"] = {
                "aerolib_name": item.get("symbol"),
                "binary_hash": row["provider_sha256"],
                "reference_functions": [
                    f"{row['provider']} Ghidra export {row['function_entry']}",
                    f"body SHA-256 {row['function_body_sha256']}",
                    LIBC_QUEUE.as_posix(),
                ],
                "call_sites": row["runtime_symbol_addresses"].split(";") if row["runtime_symbol_addresses"] else [],
                "confidence": row["confidence"],
                "conflicts": ["provider body is proven; a complete semantic HLE replacement is not claimed"],
            }
            item["contract"] = {
                "signature": "provider-defined Gen5 ABI; the exact guest export is authoritative",
                "returns": ["loaded guest provider result", "ORBIS_GEN2_ERROR_NOT_IMPLEMENTED when the provider is unavailable"],
                "output_writes": ["provider-defined; the fail-closed fallback writes nothing"],
                "validation_rules": ["exact NID/name/library and Ghidra body hash", "runtime-loaded provider route is required"],
                "state_transitions": ["provider-defined; none in the fallback"],
                "ownership": ["provider-defined"],
                "synchronization": ["provider-defined"],
            }
            item["blockers"] = ["semantic behavior remains guest-provider-dependent"]
        item["implementation"] = {
            "worktree": ".",
            "branch": "codex/gta-v-nids",
            "commit": commits["libc"],
            "files": [
                export["source"],
                "tests/SharpEmu.Libs.Tests/Lle/Libc35ExportsTests.cs",
                "AcelogicFile/docs/gta-v/libc35-lle-ghidra.md",
            ],
        }
        item["validation"] = common_validation(
            integration_validation,
            runtime_validation,
            "35/35 exact libc-family registrations validated",
        )

    for nid, export in kernel_exports.items():
        item = by_nid[nid]
        row = kernel_evidence[nid]
        semantic = nid in KERNEL_SEMANTIC_NIDS
        item["status"] = "integrated"
        item["evidence"] = {
            "aerolib_name": item.get("symbol"),
            "binary_hash": "0d91281f1d2cdcf4d8c2f4b920766b645ea086e679bd95074f30510178a706b0",
            "reference_functions": [
                f"libkernel Ghidra export {row['function_entry']}",
                f"body SHA-256 {row['function_body_sha256']}",
                KERNEL_QUEUE.as_posix(),
            ],
            "call_sites": [],
            "confidence": row["confidence"],
            "conflicts": [] if semantic else ["the recovered implementation gate is not yet modeled in SharpEmu"],
        }
        item["contract"] = {
            "signature": row["abi_summary"],
            "returns": [
                row["return_error_contract"],
                "implemented semantic path" if semantic else "explicit ORBIS_GEN2_ERROR_NOT_IMPLEMENTED until the gate is met",
            ],
            "output_writes": [
                row["output_state_contract"],
                "focused positive/negative tests" if semantic else "no guest output writes on the fail-closed path",
            ],
            "validation_rules": [row["implementation_gate"]],
            "state_transitions": [row["output_state_contract"] if semantic else "none on the fail-closed path"],
            "ownership": ["SharpEmu HLE-owned implementation"],
            "synchronization": ["existing subsystem synchronization" if semantic else "none"],
        }
        item["implementation"] = {
            "worktree": ".",
            "branch": "codex/gta-v-nids",
            "commit": commits["kernel"],
            "files": [
                export["source"],
                "src/SharpEmu.Libs/Kernel/KernelSocketCompatExports.cs",
                "tests/SharpEmu.Libs.Tests/Kernel/GtaVKernelContractExportsTests.cs",
                "AcelogicFile/docs/gta-v/kernel27-ghidra-contracts.md",
            ],
        }
        item["validation"] = common_validation(
            integration_validation,
            runtime_validation,
            "27/27 exact kernel/POSIX registrations and focused contracts validated",
        )
        if nid in KERNEL_PARTIAL_NIDS:
            item["contract"]["returns"][1] = "implemented semantic path for flags == 0; nonzero flags fail closed"
            item["blockers"] = ["nonzero recv/send flags remain explicitly NOT_IMPLEMENTED"]
        else:
            item["blockers"] = [] if semantic else [row["implementation_gate"]]

    for nid, (name, library, _) in DATA_REGISTRATIONS.items():
        item = by_nid[nid]
        row = data_evidence[nid]
        providers = positive_object_providers[nid]
        require(item.get("symbol") == name, f"data manifest name mismatch for {nid}")
        item["status"] = "integrated"
        item["evidence"] = {
            "aerolib_name": name,
            "binary_hash": providers[0]["sha256"],
            "reference_functions": [
                *(f"{provider['label']} Ghidra STT_OBJECT {provider['address']}" for provider in providers),
                DATA_QUEUE.as_posix(),
                OBJECT_EVIDENCE.as_posix(),
            ],
            "call_sites": row["runtime_symbol_addresses"].split(";") if row["runtime_symbol_addresses"] else [],
            "confidence": "high",
            "conflicts": ["Ghidra classifies this symbol as an object without a function body"],
        }
        item["contract"] = {
            "signature": f"addressable Gen5 ABI object '{name}' in {library}; never callable",
            "returns": ["not applicable; object import relocations bind an address"],
            "output_writes": ["writes the resolved guest-authoritative object address plus relocation addend"],
            "validation_rules": [row["registration_action"], row["forbidden_action"]],
            "state_transitions": [row["initial_value"] or "guest provider owns object state"],
            "ownership": ["guest provider first; registered HLE fallback only where documented"],
            "synchronization": ["provider-defined object synchronization"],
        }
        item["implementation"] = {
            "worktree": ".",
            "branch": "codex/gta-v-nids",
            "commit": commits["data"],
            "hardening_commit": commits["hardening"],
            "files": [
                DATA_SOURCE.as_posix(),
                "src/SharpEmu.Core/Runtime/ImportedDataRebinder.cs",
                "src/SharpEmu.Core/Loader/SelfLoader.cs",
                "tests/SharpEmu.Libs.Tests/Loader/DataSymbolRegistrationTests.cs",
                "AcelogicFile/docs/gta-v/gen5-object-import-architecture.md",
            ],
        }
        item["validation"] = common_validation(
            integration_validation,
            runtime_validation,
            "5/5 exact data-only registrations and loader contracts validated",
        )
        item["blockers"] = (
            ["the loaded GTA libc provider is required; no fabricated HLE FILE object exists"]
            if nid in {"H8AprKeZtNg", "2sWzhYqFH4E"}
            else ["the HLE fallback is compatibility state, not a complete provider semantic replacement"]
        )

    counts = Counter(item["status"] for item in manifest["items"])
    require(counts == {"integrated": 911}, f"unexpected final manifest lifecycle: {dict(counts)}")
    manifest["run"].setdefault("completed_at", datetime.now(timezone.utc).isoformat())
    manifest["run"]["pinned_inventory_sha256"] = INVENTORY_SHA256
    manifest["run"]["final_registration_coverage"] = "1432/1432"
    serialized_manifest = json.dumps(manifest, indent=2) + "\n"
    canonical_uncovered = ",".join(uncovered_header) + "\n"

    if args.check_only and input_already_final:
        require(
            original_manifest_text == serialized_manifest,
            "final manifest has drifted from the evidence-derived tracker output",
        )
        require(
            uncovered_path.read_text(encoding="utf-8") == canonical_uncovered,
            "completed uncovered CSV is not canonical header-only output",
        )

    if not args.check_only:
        manifest_path.write_text(serialized_manifest, encoding="utf-8")
        with uncovered_path.open("w", newline="", encoding="utf-8") as handle:
            writer = csv.DictWriter(handle, fieldnames=uncovered_header, lineterminator="\n")
            writer.writeheader()

    print(json.dumps({
        "updated": 67,
        "manifest_status_counts": dict(counts),
        "uncovered_rows": 0,
        "inventory_rows": len(inventory_nids),
        "callable_final_wave": len(libc_exports) + len(kernel_exports),
        "data_final_wave": len(DATA_REGISTRATIONS),
        "check_only": args.check_only,
    }, sort_keys=True))


if __name__ == "__main__":
    main()
