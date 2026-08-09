#!/usr/bin/env python3
"""Extract a runtime-addressed code window from a directly mapped SELF segment."""

from __future__ import annotations

import argparse
import hashlib
from pathlib import Path


def parse_int(value: str) -> int:
    return int(value, 0)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--guest-start", required=True, type=parse_int)
    parser.add_argument("--size", required=True, type=parse_int)
    parser.add_argument("--runtime-base", default=0x800000000, type=parse_int)
    parser.add_argument("--payload-offset", default=0x3B1F0, type=parse_int)
    args = parser.parse_args()

    relative_start = args.guest_start - args.runtime_base
    if relative_start < 0:
        parser.error("guest-start precedes runtime-base")

    source_offset = args.payload_offset + relative_start
    source_size = args.source.stat().st_size
    if source_offset + args.size > source_size:
        parser.error(
            f"window exceeds source: offset=0x{source_offset:X} "
            f"size=0x{args.size:X} source_size=0x{source_size:X}"
        )

    with args.source.open("rb") as source:
        source.seek(source_offset)
        payload = source.read(args.size)
    if len(payload) != args.size:
        raise OSError(f"short read: expected {args.size}, received {len(payload)}")

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(payload)
    digest = hashlib.sha256(payload).hexdigest()
    print(
        f"guest=0x{args.guest_start:X} source_offset=0x{source_offset:X} "
        f"size=0x{args.size:X} sha256={digest} output={args.output}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
