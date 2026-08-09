# GTA V Gen5 registration-parity inventory

The pinned inventory is
[`gta-v-gen5-nid-inventory-base-615bae08.csv`](gta-v-gen5-nid-inventory-base-615bae08.csv).
It contains exactly 1,432 unique application/runtime NIDs extracted from GTA V's
`eboot.bin`, `sce_module/libc.prx`, `sce_module/libSceJobManager.prx`, and
`sce_module/libSceNpCppWebApi.prx` imports after the denominator rules recorded by
the extraction audit were applied.

- Inventory rows: 1,432 plus one header row
- Inventory SHA-256: `efb0a69b0e5e32274db2ca86558041318e9ba65011c0d94f3362629bf826f73a`
- Pinned Acelogic base represented by the snapshot columns: `615bae08c2613b6b8363203b8c40f58e2bf6eac6`
- GTA `eboot.bin` SHA-256: `60d394626ac62acd1b20d205599b104bb51756d468d3878ad14c230bfe305c11`

As of the final 2026-07-18 validation, the compiled Gen5 registry covers all
1,432 pinned rows: 1,426 function imports and six object imports. The effective
registration union is 1,427 callable entries (including the legacy callable
`__stack_chk_guard` object) plus five first-class data entries, with no overlap.
The manifest has 911/911 integrated queue items and the uncovered CSV has zero
data rows. Runtime and regression evidence is recorded in
[`final-parity-runtime-20260718.md`](final-parity-runtime-20260718.md).

The `acelogic_*` columns are the immutable baseline snapshot, not live coverage
fields. Current coverage is recomputed against SharpEmu's generated Gen5 export
registry and tracked in `AcelogicFile/GTA_V_PROGRESS.md`,
`AcelogicFile/GTA_V_NID_SWARM_MANIFEST.json`, and
`AcelogicFile/GTA_V_UNCOVERED_NIDS.csv`.

Registration parity means that every one of these 1,432 NIDs appears exactly once
in the effective Gen5 registry. It does not by itself prove semantic correctness,
provider availability, successful runtime calls, or game playability. Semantic HLE
implementations require Ghidra-backed ABI, return, memory-write, state, and failure
contracts; provider-backed registrations retain the guest provider as authoritative
and fail closed when it is unavailable.
