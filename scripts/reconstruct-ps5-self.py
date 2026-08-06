#!/usr/bin/env python3
"""Reconstruct a sectionless ELF64 image from a decrypted PS5 SELF.

The output is intended for deterministic Ghidra analysis.  It copies only the
embedded ELF header/program-header table and the uncompressed, unencrypted
blocked payloads described by the SELF segment table.  It does not decrypt or
decompress retail payloads.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import struct
from dataclasses import asdict, dataclass
from pathlib import Path


SELF_MAGIC = b"\x4f\x15\x3d\x1d"
ELF_MAGIC = b"\x7fELF"
ELF64_HEADER_SIZE = 64
ELF64_PROGRAM_HEADER_SIZE = 56
ET_DYN = 3
PT_LOAD = 1
PT_DYNAMIC = 2
PT_SCE_DYNLIBDATA = 0x61000000


@dataclass(frozen=True)
class ProgramHeader:
    index: int
    type: int
    flags: int
    offset: int
    vaddr: int
    paddr: int
    filesz: int
    memsz: int
    align: int


@dataclass(frozen=True)
class SelfSegment:
    index: int
    flags: int
    file_offset: int
    file_size: int
    memory_size: int

    @property
    def program_index(self) -> int:
        return (self.flags >> 20) & 0xFFF

    @property
    def is_blocked(self) -> bool:
        return bool(self.flags & 0x800)

    @property
    def is_encrypted(self) -> bool:
        return bool(self.flags & 0x2)

    @property
    def is_compressed(self) -> bool:
        return bool(self.flags & 0x8)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def ensure_range(total: int, offset: int, size: int, label: str) -> None:
    if offset < 0 or size < 0 or offset > total or size > total - offset:
        raise ValueError(
            f"{label} is outside the input: offset=0x{offset:X} "
            f"size=0x{size:X} input_size=0x{total:X}"
        )


def read_exact(source, offset: int, size: int) -> bytes:
    source.seek(offset)
    data = source.read(size)
    if len(data) != size:
        raise OSError(
            f"short read at 0x{offset:X}: expected 0x{size:X}, got 0x{len(data):X}"
        )
    return data


def parse_headers(source_path: Path) -> tuple[int, list[SelfSegment], list[ProgramHeader], bytes, int]:
    input_size = source_path.stat().st_size
    if input_size < 32:
        raise ValueError("input is too small to contain a SELF header")

    with source_path.open("rb") as source:
        prefix = read_exact(source, 0, 32)
        if prefix[:4] != SELF_MAGIC:
            raise ValueError("input does not have the decrypted PS5 SELF magic")

        segment_count = struct.unpack_from("<H", prefix, 24)[0]
        segment_table_size = segment_count * 32
        ensure_range(input_size, 32, segment_table_size, "SELF segment table")
        segment_table = read_exact(source, 32, segment_table_size)
        segments = [
            SelfSegment(index, *struct.unpack_from("<QQQQ", segment_table, index * 32))
            for index in range(segment_count)
        ]

        elf_base = 32 + segment_table_size
        ensure_range(input_size, elf_base, ELF64_HEADER_SIZE, "embedded ELF header")
        elf_header = read_exact(source, elf_base, ELF64_HEADER_SIZE)
        if elf_header[:4] != ELF_MAGIC:
            raise ValueError("embedded ELF magic is missing")
        if elf_header[4] != 2 or elf_header[5] != 1:
            raise ValueError("expected a little-endian ELF64 image")

        phoff = struct.unpack_from("<Q", elf_header, 32)[0]
        ehsize = struct.unpack_from("<H", elf_header, 52)[0]
        phentsize = struct.unpack_from("<H", elf_header, 54)[0]
        phnum = struct.unpack_from("<H", elf_header, 56)[0]
        if ehsize != ELF64_HEADER_SIZE:
            raise ValueError(f"unsupported ELF header size: {ehsize}")
        if phentsize != ELF64_PROGRAM_HEADER_SIZE:
            raise ValueError(f"unsupported ELF64 program-header size: {phentsize}")

        header_span = max(ehsize, phoff + phentsize * phnum)
        ensure_range(input_size, elf_base, header_span, "embedded ELF header table")
        embedded_headers = read_exact(source, elf_base, header_span)
        phdrs = [
            ProgramHeader(
                index,
                *struct.unpack_from(
                    "<IIQQQQQQ", embedded_headers, phoff + index * phentsize
                ),
            )
            for index in range(phnum)
        ]

    return elf_base, segments, phdrs, embedded_headers, input_size


def reconstruct(source_path: Path, output_path: Path, report_path: Path, force: bool) -> dict:
    if source_path.resolve() in {output_path.resolve(), report_path.resolve()}:
        raise ValueError("source, output, and report paths must be distinct")
    for path in (output_path, report_path):
        if path.exists() and not force:
            raise FileExistsError(f"refusing to overwrite {path}; pass --force")

    elf_base, segments, phdrs, headers, input_size = parse_headers(source_path)
    output_size = max(
        len(headers),
        *(header.offset + header.filesz for header in phdrs if header.filesz),
    )
    mappings: list[dict] = []

    output_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    with source_path.open("rb") as source, output_path.open("w+b") as output:
        output.truncate(output_size)
        output.seek(0)
        output.write(headers)

        for segment in segments:
            if not segment.is_blocked:
                continue
            if segment.program_index >= len(phdrs):
                raise ValueError(
                    f"SELF segment {segment.index} refers to missing program header "
                    f"{segment.program_index}"
                )
            header = phdrs[segment.program_index]
            if segment.is_encrypted or segment.is_compressed:
                state = "encrypted" if segment.is_encrypted else "compressed"
                raise ValueError(
                    f"SELF segment {segment.index} is {state}; this tool only accepts "
                    "decrypted, uncompressed payloads"
                )
            if segment.file_size < header.filesz:
                raise ValueError(
                    f"SELF segment {segment.index} is shorter than program header "
                    f"{header.index}: 0x{segment.file_size:X} < 0x{header.filesz:X}"
                )
            ensure_range(
                input_size,
                segment.file_offset,
                header.filesz,
                f"SELF segment {segment.index} payload",
            )

            output.seek(header.offset)
            remaining = header.filesz
            source.seek(segment.file_offset)
            while remaining:
                chunk = source.read(min(1024 * 1024, remaining))
                if not chunk:
                    raise OSError(f"short payload read for SELF segment {segment.index}")
                output.write(chunk)
                remaining -= len(chunk)

            nested = [
                candidate.index
                for candidate in phdrs
                if candidate.filesz
                and header.offset <= candidate.offset
                and candidate.offset + candidate.filesz <= header.offset + header.filesz
            ]
            mappings.append(
                {
                    "self_segment": segment.index,
                    "program_header": header.index,
                    "source_offset": segment.file_offset,
                    "destination_offset": header.offset,
                    "size": header.filesz,
                    "nested_program_headers": nested,
                }
            )

        # Sony SELF ELF types are not recognized by stock Ghidra importers.
        output.seek(16)
        output.write(struct.pack("<H", ET_DYN))
        # These reconstructed derivatives deliberately have no section table.
        output.seek(40)
        output.write(struct.pack("<Q", 0))
        output.seek(58)
        output.write(struct.pack("<HHH", 0, 0, 0))
        output.flush()
        os.fsync(output.fileno())

    covered = set()
    for mapping in mappings:
        covered.update(mapping["nested_program_headers"])
    required = {
        header.index
        for header in phdrs
        if header.filesz and header.type in {PT_LOAD, PT_DYNAMIC, PT_SCE_DYNLIBDATA}
    }
    missing_required = sorted(required - covered)
    if missing_required:
        output_path.unlink(missing_ok=True)
        raise ValueError(
            "reconstruction does not cover required program headers: "
            + ", ".join(map(str, missing_required))
        )

    report = {
        "format": "sharpemu-ps5-self-reconstruction-v1",
        "source": str(source_path.resolve()),
        "source_size": input_size,
        "source_sha256": sha256_file(source_path),
        "elf_base": elf_base,
        "original_elf_type": struct.unpack_from("<H", headers, 16)[0],
        "reconstructed_elf_type": ET_DYN,
        "output": str(output_path.resolve()),
        "output_size": output_path.stat().st_size,
        "output_sha256": sha256_file(output_path),
        "section_table_cleared": True,
        "self_segments": [asdict(segment) for segment in segments],
        "program_headers": [asdict(header) for header in phdrs],
        "payload_mappings": mappings,
        "uncovered_nonempty_program_headers": sorted(
            header.index for header in phdrs if header.filesz and header.index not in covered
        ),
    }
    report_path.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n")
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="decrypted, uncompressed PS5 SELF")
    parser.add_argument("output", type=Path, help="sectionless ELF64 derivative")
    parser.add_argument("--report", required=True, type=Path, help="JSON provenance report")
    parser.add_argument("--force", action="store_true", help="overwrite output/report")
    args = parser.parse_args()

    report = reconstruct(args.source, args.output, args.report, args.force)
    print(
        f"source_sha256={report['source_sha256']} "
        f"output_sha256={report['output_sha256']} "
        f"mappings={len(report['payload_mappings'])} "
        f"output_size={report['output_size']} output={args.output} report={args.report}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
