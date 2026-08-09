#!/usr/bin/env python3
"""Run isolated parallel Ghidra evidence jobs for selected PS5 provider ELFs."""

from __future__ import annotations

import argparse
import concurrent.futures
import csv
import hashlib
import json
import os
import re
import subprocess
import time
from pathlib import Path


ELF_MAGIC = b"\x7fELF"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def slugify(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", "_", value.lower()).strip("_")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("queue", type=Path)
    parser.add_argument("provider_dir", type=Path)
    parser.add_argument("output_root", type=Path)
    parser.add_argument("project_root", type=Path)
    parser.add_argument("--campaign", required=True)
    parser.add_argument("--module", action="append", required=True, dest="modules")
    parser.add_argument("--jobs", type=int, default=4)
    parser.add_argument("--max-cpu", type=int, default=1)
    parser.add_argument("--max-memory", default="4G")
    parser.add_argument(
        "--ghidra",
        type=Path,
        default=Path("/opt/homebrew/opt/ghidra/libexec/support/analyzeHeadless"),
    )
    parser.add_argument(
        "--java-home",
        default="/Library/Java/JavaVirtualMachines/graalvm-21.jdk/Contents/Home",
    )
    parser.add_argument(
        "--script-path",
        type=Path,
        default=Path(__file__).resolve().parent / "ghidra",
    )
    args = parser.parse_args()

    with args.queue.open(newline="") as handle:
        queue = list(csv.DictReader(handle))
    by_module = {
        module: sorted({row["nid"] for row in queue if row.get("module") == module})
        for module in args.modules
    }
    empty = [module for module, nids in by_module.items() if not nids]
    if empty:
        parser.error("modules have no exact queue rows: " + ", ".join(empty))

    args.output_root.mkdir(parents=True, exist_ok=True)
    args.project_root.mkdir(parents=True, exist_ok=True)

    def run_one(module: str) -> dict:
        started = time.monotonic()
        slug = slugify(module)
        source = next(
            (path for path in (args.provider_dir / f"{module}.sprx", args.provider_dir / f"{module}.prx") if path.exists()),
            None,
        )
        if source is None:
            raise FileNotFoundError(f"provider not found for {module}")
        if source.read_bytes()[:4] != ELF_MAGIC:
            raise ValueError(f"provider must be a reconstructed or bare ELF: {source}")

        job_root = args.output_root / slug
        project_location = args.project_root / f"gta_v_{slug}_{args.campaign}"
        project_name = project_location.name
        if project_location.exists() and any(project_location.iterdir()):
            raise FileExistsError(f"refusing to reuse nonempty Ghidra project: {project_location}")
        job_root.mkdir(parents=True, exist_ok=True)
        project_location.mkdir(parents=True, exist_ok=True)
        user_home = job_root / "ghidra-user"
        user_home.mkdir(parents=True, exist_ok=True)

        targets = job_root / "targets.csv"
        with targets.open("w", newline="") as handle:
            writer = csv.writer(handle)
            writer.writerow(["nid"])
            writer.writerows([nid] for nid in by_module[module])
        evidence = job_root / "ghidra-evidence.json"
        log = job_root / "ghidra.log"

        environment = os.environ.copy()
        environment.update({
            "JAVA_HOME": args.java_home,
            "GHIDRA_HEADLESS_MAXMEM": args.max_memory,
            "GHIDRA_HEADLESS_JAVA_OPTIONS": f"-Duser.home={user_home}",
        })
        analyze = [
            str(args.ghidra),
            str(project_location),
            project_name,
            "-import",
            str(source),
            "-overwrite",
            "-max-cpu",
            str(args.max_cpu),
        ]
        with log.open("wb") as output:
            analyze_result = subprocess.run(
                analyze,
                stdout=output,
                stderr=subprocess.STDOUT,
                env=environment,
                check=False,
            )
        if analyze_result.returncode:
            raise RuntimeError(f"Ghidra analysis failed for {module}: {log}")

        post = [
            str(args.ghidra),
            str(project_location),
            project_name,
            "-process",
            source.name,
            "-noanalysis",
            "-scriptPath",
            str(args.script_path),
            "-postScript",
            "ExportSelectedNidFunctions.java",
            str(targets),
            str(evidence),
        ]
        with log.open("ab") as output:
            post_result = subprocess.run(
                post,
                stdout=output,
                stderr=subprocess.STDOUT,
                env=environment,
                check=False,
            )
        if post_result.returncode:
            raise RuntimeError(f"Ghidra evidence export failed for {module}: {log}")

        packet = json.loads(evidence.read_text())
        expected = len(by_module[module])
        if packet.get("target_count") != expected or packet.get("function_count") != expected:
            raise RuntimeError(
                f"incomplete Ghidra evidence for {module}: "
                f"{packet.get('function_count')}/{expected}"
            )
        result = {
            "module": module,
            "target_count": expected,
            "function_count": packet["function_count"],
            "source": str(source.resolve()),
            "source_size": source.stat().st_size,
            "source_sha256": sha256_file(source),
            "evidence": str(evidence.resolve()),
            "evidence_sha256": sha256_file(evidence),
            "project": str(project_location.resolve()),
            "elapsed_seconds": round(time.monotonic() - started, 3),
        }
        print(
            f"DONE module={module} functions={expected}/{expected} "
            f"elapsed={result['elapsed_seconds']}s",
            flush=True,
        )
        return result

    results: list[dict] = []
    errors: list[str] = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.jobs) as executor:
        future_modules = {
            executor.submit(run_one, module): module for module in args.modules
        }
        for future in concurrent.futures.as_completed(future_modules):
            module = future_modules[future]
            try:
                results.append(future.result())
            except Exception as error:
                errors.append(f"{module}: {error}")
                print(f"FAILED module={module} error={error}", flush=True)

    summary = {
        "format": "sharpemu-ghidra-provider-sweep-v1",
        "campaign": args.campaign,
        "jobs": args.jobs,
        "max_cpu_per_job": args.max_cpu,
        "results": sorted(results, key=lambda row: row["module"]),
        "errors": errors,
    }
    summary_path = args.output_root / "campaign-summary.json"
    summary_path.write_text(json.dumps(summary, indent=2, sort_keys=True) + "\n")
    print(
        f"SUMMARY passed={len(results)} failed={len(errors)} "
        f"targets={sum(row['target_count'] for row in results)} output={summary_path}",
        flush=True,
    )
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
