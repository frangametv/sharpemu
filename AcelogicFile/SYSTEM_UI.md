# System UI boot mode

SharpEmu has an experimental boot harness for bringing up a user-supplied,
extracted System UI in the same incremental style used to bring up RPCS3's
VSH/XMB. It is infrastructure, not a compatibility claim: the shell will still
stop at missing kernel, service, graphics, or multi-process behavior as those
pieces are identified and implemented.

No firmware, keys, decrypted assets, or copyrighted system software belong in
this repository. Use only material you are legally entitled to use and keep it
outside the source tree.

## Expected layout

Select the directory that represents the root of the extracted guest
filesystem. Its top-level directories become matching guest mounts:

```text
SystemSoftware/             host root
  system/                   mounted as /system (read-only)
    vsh/SceShellCore.elf    selected PS5 shell entry (when present)
  system_ex/                mounted as /system_ex (read-only)
  ...
```

The entry executable must be inside one of the selected root's top-level
directories so its guest path is mounted. SharpEmu rejects roots without any
top-level directories and blocks guest writes, creates, deletes, and directory
mutations beneath every system-software mount.

An official update PUP is not an extracted filesystem and cannot be passed
directly to this mode. PUP verification/extraction is a separate future tool;
it must not bypass encryption, require bundled keys, or redistribute Sony data.

A decrypted outer PUP is also not necessarily boot-ready: executables inside
its filesystem may still be encrypted SELF containers. SharpEmu can parse PS4
and PS5 SELF headers, but it does not perform runtime decryption. Supply a
lawfully obtained decrypted ELF/FSELF shell dump before working on the next
emulation blocker.

## Validate without booting

Preflight works on any host architecture. It inspects only the selected entry's
ELF/SELF header and segment table; it does not execute the binary:

```sh
SharpEmu --system-ui-preflight \
  --system-root="/path/to/SystemSoftware" \
  "/path/to/SystemSoftware/system/vsh/SceShellCore.elf"
```

It reports the guest entry path, file size, image format, SELF segment state,
and complete read-only mount table. It fails early when the entry is not an ELF
or recognized SELF, has a malformed embedded ELF header, or still contains
encrypted SELF segments.

## Boot

```sh
SharpEmu --system-ui \
  --system-root="/path/to/SystemSoftware" \
  "/path/to/SystemSoftware/system/vsh/SceShellCore.elf"
```

System UI mode enables a 256-entry import trace by default. Existing options
such as `--strict`, `--trace-imports=N`, and `--log-level=debug` remain
available. The desktop frontend exposes the same flow through **Boot System
UI...**: choose the extracted root first, then choose its shell executable.

## Development sequence

The harness makes failures reproducible without putting system software in Git.
Work should proceed from the first deterministic stop:

1. Capture the preflight mount table and first unimplemented import/service.
2. Implement generic HLE behavior with a focused test or minimal fixture.
3. Advance through shell initialization and graphics composition.
4. Add service/process isolation before treating the shell as a game launcher.

Do not add title- or firmware-specific return-value hacks. Keep findings
artifact-independent so another contributor can reproduce them with their own
lawfully obtained system-software tree.
