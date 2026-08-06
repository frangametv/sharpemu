# GTA V Gen5 object-import architecture

This change registers the five relocation-referenced GTA V ABI objects as data,
not as callable SysAbi functions:

| NID | Object | Library | Resolution |
| --- | --- | --- | --- |
| `djxxOmW6-aw` | `__progname` | `libkernel` | Guest first; HLE pointer-cell fallback |
| `P330P3dFF68` | `Need_sceLibc` | `libc` | Guest first; HLE `uint32(1)` fallback |
| `ZT4ODD2Ts9o` | `Need_sceLibcInternal` | `libSceLibcInternal` | Guest first; HLE `uint32(1)` fallback |
| `H8AprKeZtNg` | `_Stderr` | `libc` | Guest provider required |
| `2sWzhYqFH4E` | `_Stdout` | `libc` | Guest provider required |

The compact source packet is
[`GHIDRA_OBJECT_EVIDENCE.json`](../../artifacts/gta-v-nid-evidence/data5-objects-20260718/GHIDRA_OBJECT_EVIDENCE.json).
Every provider observation is `symbol_without_function`; `_Stderr` and `_Stdout`
also had four pre-fix runtime redirects apiece from callable import stubs to their
mapped data addresses. Page execute permission was therefore not a sufficient
function-kind check.

## Registration boundary

`DataSymbolRegistry` is independent of the generated callable
`SysAbiExportRegistry`. `ModuleManager` stores data registrations in separate
NID/name tables, rejects function/data registration conflicts, and never adds
data to its dispatch table. `PreferLle` remains a function policy and is not
used for these objects.

The legacy callable `__stack_chk_guard` compatibility handler is unchanged.
Its existing HLE object remains available to data relocation, but that legacy
handler is not a model for new object registrations.

## Loader and runtime contract

The SELF loader records `STT_OBJECT` definitions separately from the general
runtime symbol index. Data-only imports do not receive executable import stubs;
their slots are zeroed until all adjacent modules have loaded. A NID imported
as both function and object is rejected. This implementation accepts the
64-bit pointer relocation shape used by the five pinned GTA V objects and fails
closed on other object relocation shapes rather than corrupting a slot.

Runtime resolution order is:

1. main-image and loaded guest `STT_OBJECT` definitions;
2. an optional registered HLE fallback;
3. failure before module or process initializers for an unresolved strong import.

An unresolved weak data import uses the ELF definition `S=0` and still writes
`S+A`; non-zero positive and negative addends are therefore retained. A
relocation write failure is always fatal. `_Stderr` and `_Stdout` intentionally
have no fabricated HLE fallback because the provider-owned stream state and
layout have not been reconstructed as an HLE object contract.

Callable and data symbol maps remain distinct through native dispatch. Only the
callable map participates in direct-call bridge selection. Module-scoped
`sceKernelDlsym` receives the union of a guest module's callable and data
definitions, while global `sceKernelDlsym` receives a separate data lookup that
also contains registered HLE fallbacks. This keeps `__progname` and the two
`Need_*` flags discoverable without making their addresses executable targets.

## Coverage semantics

Effective Gen5 registration parity is the unique union of callable exports and
data registrations. The five entries in this change count once in that union
and zero times in callable dispatch. Registration parity alone does not claim
that the two compatibility flag values are a Ghidra-proven semantic substitute;
the exact GTA providers remain authoritative.
