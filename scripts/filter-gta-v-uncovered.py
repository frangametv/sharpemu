#!/usr/bin/env python3
"""Filter GTA V's uncovered queue by explicit SysAbi NIDs in source files."""

from __future__ import annotations

import argparse
import csv
import json
import re
from collections import Counter
from pathlib import Path


NID_PATTERN = re.compile(r'\bNid\s*=\s*"([A-Za-z0-9+\-]{11})"')


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("queue", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("sources", nargs="+", type=Path)
    parser.add_argument("--nid", action="append", default=[])
    parser.add_argument("--expected-removed", type=int)
    args = parser.parse_args()

    registered = set(args.nid)
    for source in args.sources:
        if not source.is_file():
            parser.error(f"registration source does not exist: {source}")
        registered.update(NID_PATTERN.findall(source.read_text()))

    with args.queue.open(newline="") as handle:
        reader = csv.DictReader(handle)
        fieldnames = reader.fieldnames
        rows = list(reader)
    if not fieldnames:
        parser.error("queue has no CSV header")
    queue_nids = [row["nid"] for row in rows]
    if len(queue_nids) != len(set(queue_nids)):
        parser.error("queue contains duplicate NIDs")

    removed = [row for row in rows if row["nid"] in registered]
    remaining = [row for row in rows if row["nid"] not in registered]
    if args.expected_removed is not None and len(removed) != args.expected_removed:
        parser.error(
            f"removed {len(removed)} rows, expected {args.expected_removed}"
        )

    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames, lineterminator="\n")
        writer.writeheader()
        writer.writerows(remaining)

    summary = {
        "input_rows": len(rows),
        "registered_source_nids": len(registered),
        "removed_rows": len(removed),
        "remaining_rows": len(remaining),
        "remaining_by_component": dict(sorted(Counter(
            row["component"] for row in remaining
        ).items())),
        "output": str(args.output),
    }
    print(json.dumps(summary, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
