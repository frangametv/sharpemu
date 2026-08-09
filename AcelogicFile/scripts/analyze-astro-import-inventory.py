#!/usr/bin/env python3
"""Join SharpEmu's static import dump with source registrations and runtime calls."""

from __future__ import annotations

import argparse
import csv
import json
import re
from collections import Counter
from pathlib import Path


INVENTORY_RE = re.compile(
    r"\[LOADER\]\[IMPORT_INVENTORY\] image=(?P<image>\S+)\t"
    r"nid=(?P<nid>\S+)\tname=(?P<name>.*?)\tcount=(?P<count>\d+)\t"
    r"kind=(?P<kind>\w+)\tstub=(?P<stub>[01])"
)
CALL_RE = re.compile(r"(?:ImportCtx#\d+: nid=|Import#\d+.*?\()(?P<nid>[-+A-Za-z0-9]{11})(?:\s|\))")
LLE_REDIRECT_RE = re.compile(r"\[LOADER\]\[INFO\] LLE redirect: \S+ (?P<nid>\S+) ->")
ATTRIBUTE_RE = re.compile(r"\[SysAbiExport\((?P<body>.*?)\)\]", re.DOTALL)
NID_RE = re.compile(r'\bNid\s*=\s*"(?P<nid>[^"]+)"')
PREFER_LLE_RE = re.compile(r"\bPreferLle\s*=\s*true\b")
METHOD_RE = re.compile(r"\b(?:public|private|internal)\s+static\s+int\s+(?P<method>\w+)\s*\(")


def source_registrations(source_root: Path) -> dict[str, list[dict]]:
    result: dict[str, list[dict]] = {}
    for path in source_root.rglob("*.cs"):
        text = path.read_text(encoding="utf-8-sig")
        tokens: list[tuple[int, str, object]] = []
        tokens.extend((match.start(), "attribute", match) for match in ATTRIBUTE_RE.finditer(text))
        tokens.extend((match.start(), "method", match) for match in METHOD_RE.finditer(text))
        pending: list[re.Match[str]] = []
        for _, kind, raw_match in sorted(tokens, key=lambda item: item[0]):
            match = raw_match
            if kind == "attribute":
                pending.append(match)
                continue
            method = match.group("method")
            for attribute in pending:
                body = attribute.group("body")
                nid_match = NID_RE.search(body)
                if nid_match:
                    result.setdefault(nid_match.group("nid"), []).append(
                        {
                            "method": method,
                            "file": path.as_posix(),
                            "prefer_lle": bool(PREFER_LLE_RE.search(body)),
                        }
                    )
            pending.clear()
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("inventory_log", type=Path)
    parser.add_argument("source_root", type=Path)
    parser.add_argument("output_csv", type=Path)
    parser.add_argument("--runtime-log", type=Path)
    parser.add_argument("--summary", type=Path)
    parser.add_argument("--force", action="store_true")
    args = parser.parse_args()

    outputs = [args.output_csv, *(path for path in [args.summary] if path)]
    if not args.force and any(path.exists() for path in outputs):
        parser.error("refusing to overwrite output; pass --force")

    inventory_text = args.inventory_log.read_text(encoding="utf-8", errors="replace")
    guest_provider_nids = {match.group("nid") for match in LLE_REDIRECT_RE.finditer(inventory_text)}
    inventory: dict[str, dict] = {}
    for match in INVENTORY_RE.finditer(inventory_text):
        nid = match.group("nid")
        row = inventory.setdefault(
            nid,
            {
                "nid": nid,
                "name": match.group("name"),
                "kind": match.group("kind"),
                "images": set(),
                "relocations": 0,
            },
        )
        row["images"].add(match.group("image"))
        row["relocations"] += int(match.group("count"))

    runtime_calls: Counter[str] = Counter()
    if args.runtime_log:
        for match in CALL_RE.finditer(args.runtime_log.read_text(encoding="utf-8", errors="replace")):
            runtime_calls[match.group("nid")] += 1

    registrations = source_registrations(args.source_root)
    rows: list[dict] = []
    for nid, item in inventory.items():
        candidates = registrations.get(nid, [])
        semantic = [entry for entry in candidates if entry["method"] != "MissingGuestProvider"]
        fail_closed = [entry for entry in candidates if entry["method"] == "MissingGuestProvider"]
        if nid in guest_provider_nids:
            status = "guest_provider_resolved"
            selected = semantic[0] if semantic else (fail_closed[0] if fail_closed else None)
        elif semantic:
            status = "semantic_or_compat"
            selected = semantic[0]
        elif fail_closed:
            status = "fail_closed_provider_fallback"
            selected = fail_closed[0]
        else:
            status = "unregistered"
            selected = None
        rows.append(
            {
                "runtime_calls": runtime_calls[nid],
                "status": status,
                "nid": nid,
                "name": item["name"],
                "kind": item["kind"],
                "image_count": len(item["images"]),
                "relocations": item["relocations"],
                "method": selected["method"] if selected else "",
                "source": selected["file"] if selected else "",
                "prefer_lle": selected["prefer_lle"] if selected else False,
            }
        )

    rank = {
        "unregistered": 0,
        "fail_closed_provider_fallback": 1,
        "semantic_or_compat": 2,
        "guest_provider_resolved": 3,
    }
    rows.sort(key=lambda row: (-row["runtime_calls"], rank[row["status"]], row["name"], row["nid"]))
    args.output_csv.parent.mkdir(parents=True, exist_ok=True)
    with args.output_csv.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)

    summary = {
        "inventory_nids": len(rows),
        "named_nids": sum(row["name"] != "<unknown>" for row in rows),
        "runtime_called_nids": sum(row["runtime_calls"] > 0 for row in rows),
        "status_counts": dict(Counter(row["status"] for row in rows)),
        "output_csv": str(args.output_csv.resolve()),
    }
    if args.summary:
        args.summary.parent.mkdir(parents=True, exist_ok=True)
        args.summary.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
