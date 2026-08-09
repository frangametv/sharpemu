# GTA V `libSceNpCppWebApi` LLE registration evidence

## Scope

This packet supports 436 Gen5 registrations imported by GTA V from
`libSceNpCppWebApi`. It does not reimplement or guess any C++ Web API behavior.
Each registration prefers the function exported by GTA's loaded guest PRX; its
HLE path returns `ORBIS_GEN2_ERROR_NOT_IMPLEMENTED` if that provider is absent.

No Kyty or other-emulator source was used. Ghidra 12.1.2 is the primary source.

## Provenance

- Original GTA PRX SHA-256:
  `12e445a98b441989626489412e6adb0ba21e8b00cbbc94b117a92267f4a16ebf`
- Original size: 7,834,919 bytes
- Deterministically reconstructed sectionless ELF SHA-256:
  `e58144c96637b9f620846aa67d2df4987f96541214d7ece1d4eac2a0a10408aa`
- Reconstructed size: 7,864,967 bytes
- Ghidra project:
  `gta_v_npcpp_exports_mac_20260718/gta_v_npcpp_exports_mac_20260718`
- Ghidra program: `libSceNpCppWebApi.elf`
- Language: `x86:LE:64:default`
- Image base: `0x00100000`
- Full Ghidra analysis time: 101 seconds on the local Mac

The reconstruction copies the embedded ELF header/program headers and six
uncompressed, unencrypted SELF payload mappings to their ELF offsets, changes
the Sony ELF type to `ET_DYN`, and clears the nonexistent section table. The
original PRX remains read-only.

## Ghidra result

`ExportSelectedNidFunctions.java` queried the analyzed Ghidra symbol and
function databases for the exact 436 NIDs in the coordinator queue:

- targets: 436
- matched symbols: 436
- functions at those symbol entries: 436
- missing: 0
- symbols without a function: 0
- empty function bodies: 0
- symbol/function entry mismatches: 0

The complete per-NID evidence, including raw symbol, function entry, body range,
body-address count, signature, calling convention, and thunk status, is in
[`npcppwebapi-lle-ghidra.json`](npcppwebapi-lle-ghidra.json). SELF mappings and
hashes are in
[`npcppwebapi-reconstruction.json`](npcppwebapi-reconstruction.json).

## Implementation boundary

The evidence proves that GTA's provider owns concrete guest functions for all
436 queued NIDs. It intentionally does not claim their internal semantics. The
runtime must resolve and patch the import to the guest function before it
considers the HLE trampoline. If no usable guest target exists, the shared
fallback fails closed; it never returns invented success or mutates guest state.

The registrations are generated from the coordinator CSV plus the immutable
Ghidra result. Generation rejects missing NIDs, extra NIDs, duplicate queue
entries, missing Ghidra functions, empty bodies, and symbol/entry mismatches.
