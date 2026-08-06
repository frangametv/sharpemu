# GTA V NID implementation progress

## Goal

Reach exact static Gen5 registration parity for all 1,432 GTA V application/runtime imports with Ghidra-backed contracts, without weakening failure behavior, then validate the result against GTA V and the existing multi-game test surface.

## Pinned inputs

- Integration branch: `codex/gta-v-nids`
- Integration worktree: `/Users/mcruz/Developer/sharpemu-gta-v-nids`
- Acelogic `main` base: `615bae08c2613b6b8363203b8c40f58e2bf6eac6`
- Current Acelogic fork-main sync: `6f1d28e` (merged `sharpemu/sharpemu:main` through `6db095e`)
- Remaining uncovered queue: `GTA_V_UNCOVERED_NIDS.csv`
- Coordinator manifest: `GTA_V_NID_SWARM_MANIFEST.json`
- Initial Acelogic-main queue: 911 unique uncovered application/runtime imports
- Pinned Aerolib symbol names: 1,418/1,432; the seven formerly catalog-unnamed queue entries now have exact Ghidra provider-function evidence without invented names
- Integrated from that queue on this branch: 911
- Remaining uncovered on this branch: 0
- Current static registration coverage: 1,432/1,432 (100.00%), up from 521/1,432 (36.38%) on the pinned main base
- Manifest lifecycle: 911 integrated

The queue is a static import inventory. It is not yet a runtime call-frequency trace; `calls=0` means no runtime count has been established.

### Current static coverage by importing image

| Importing image | Gen5-registered NIDs | Unique imported NIDs | Coverage |
|---|---:|---:|---:|
| `eboot.bin` | 1,301 | 1,301 | 100.00% |
| `sce_module/libc.prx` | 104 | 104 | 100.00% |
| `sce_module/libSceJobManager.prx` | 146 | 146 | 100.00% |
| `sce_module/libSceNpCppWebApi.prx` | 95 | 95 | 100.00% |

These image rows overlap because the same NID can be imported by more than one image; they must not be summed. The deduplicated application/runtime union is the 1,432-NID denominator above.

## Current checkpoint

The generic blocked-SELF mapping fix, the expanded Variant-II static-TLS reservation, and the Ghidra-backed `sceKernelDirectMemoryQuery` enumeration fix are integrated. The current branch contains 839 Gen5 provider-preferred registrations. Of those, 837 are exact evidence-wave registrations backed by selected Ghidra function records: 436 from GTA's shipped `libSceNpCppWebApi` provider and 401 from firmware providers analyzed on the Mac, rho, and Windows. The other two are semantic handlers (`MM4IZSEYytQ` and `UYPxv8MIzGo`) that also prefer an authoritative guest provider. Every generated HLE fallback is fail-closed; it returns `ORBIS_GEN2_ERROR_NOT_IMPLEMENTED` and does not invent provider behavior.

Mac-local firmware Ghidra and an independent rho GTA-consumer Ghidra campaign proved the direct-memory-query contract used by GTA: flags `1`, a 24-byte output buffer, `[info+8]` continuation, and terminal result `0x8002000D`. The integrated fix returns containing-or-next direct allocations and uses that exact terminal result without inventing unproven coalescing or terminal-success behavior. On post-fix runs, all four GTA loops terminate at imports 419, 447, 463, and 473; execution advances beyond import 37,900.

Mac-local and independent rho provider Ghidra recovered `XlNp7jzGiPo` (`sceAgcDriverSetTFRing`) and `MM4IZSEYytQ` (`sceAgcDriverSetHsOffchipParam`) from `libSceAgcDriver.sprx`. Both semantic implementations are integrated. The Hs-offchip call uses the recovered two-`uint32` ABI, low-16-bit packing order, state gate, and error mapping; it is also provider-preferred when the exact guest export is available.

The final 67-NID wave consists of 34 provider-preferred libc functions, one fail-closed `sceLibcInternalBacktraceForGame` HLE contract, 27 kernel/POSIX contracts, and five first-class data registrations. Nine of the kernel/POSIX contracts have semantic or deliberately partial implementations; 18 fail closed without output writes. The data registrations are resolved through a separate data-symbol path, not callable trampolines.

The final x64 GTA run routed all 34 new libc registrations directly to their providers, produced zero callable-routing events for the five data NIDs, and rebound 11 imported data relocations with zero unresolved. The Hs-offchip call at import 39,003 still received `(0, 0x1FF)`, returned zero, and execution again reached import 41,427. The later stop remains the same unattributed RenderThread read from address zero at guest RIP `0x805C273B7`. Full final evidence is retained in [`docs/gta-v/final-parity-runtime-20260718.md`](docs/gta-v/final-parity-runtime-20260718.md); this is exact registration parity, not a claim of full semantic parity or playability.

### 2026-07-19 post-logo checkpoint

Later loader, directory-walk, provider-contract, and renderer fixes now let the
Windows x64 build run stably on GTA's logo/loading presentation for several
minutes. The branch has exact stateful fallbacks for the seven fail-closed
service calls exposed by that run: SharePlay initialization, NP reachability
callback registration, NP Game Intent termination, NP Universal Data System
termination, Voice initialization, Content Search initialization, and Content
Delete initialization. Their combined integration checkpoint is `2d2b850`.

Ghidra plus a read-only live process-memory probe proved that the embedded
`KB2j` movie is read completely, opens, advances through all 450 frames,
clears its playing state, is destroyed, and sets GTA's frontend completion
byte. Repeated swapchain dumps nevertheless remain the same GTA logo image.
The exact evidence and bridge disposition are retained in
[`docs/gta-v/bink-runtime-20260719.md`](docs/gta-v/bink-runtime-20260719.md).
The active blocker is therefore downstream UI/render/presentation state, not
Bink file I/O, decode lifecycle, or EOS.

### 2026-07-23 frontend text and 3D-LUT checkpoint

The Windows x64 build now advances through the GTA V title, legal notice,
alert, and frontend artwork. Bitmap/atlas text renders on the legal and alert
screens, but the frontend tab labels over the Lester/Michael artwork remain
missing. This is a rendering-path defect, not an unresolved-NID or Bink
lifecycle blocker.

Targeted shader and GPU readback evidence isolated the missing labels to GTA's
vector-mesh text path. Its pixel shader samples a type-10 `32x32x32` 3D color
lookup texture using three coordinates; a compute shader generates all 32 Z
slices. SharpEmu previously declared every Gen5 image as 2D, allocated depth
one in Vulkan, raced every compute Z slice into the same plane, and discarded
the pixel shader's third sample coordinate. The working atlas-text path uses a
conventional 2D texture and therefore did not expose the bug.

Commit `322449f` implements the general emulator fix rather than a GTA-specific
address or shader workaround:

- Gen5 FLAT-memory IR, scalar evaluation, translation, and regression tests;
- MIMG DIM=2 to SPIR-V `Dim3D`, including vec3/ivec3 sample, load, store,
  atomic, size-query, offset, and bounds behavior;
- texture `Type`/`Depth` transport and depth-aware uncompressed/BC sizing;
- Vulkan `Type3D` images, `Extent3D` depth, `ImageViewType3D`, volume uploads,
  copies/readback, and depth-aware cache/alias identities while preserving 2D
  array-layer behavior.

The Acelogic fork was then synchronized with `sharpemu/sharpemu:main` through
upstream commit `6db095e` in merge commit `6f1d28e`. Conflict resolution kept
the fork's Ghidra-backed AGC/SystemService contracts, NGG renderer, exact GTA
registration parity, and in-place pthread scheduler while incorporating
upstream's independent fixes. The synchronized solution builds with zero
errors (70 existing catalog/XML warnings) and all 1,462 tests pass: 1,341
library tests, 58 Vulkan shader-compiler tests, 27 Metal shader-compiler tests,
and 36 source-generator tests. The next runtime gate is a visible Windows
deployment confirming that the Lester/Michael frontend labels render with the
new 3D-image path.

## Active lanes

| Lane | Branch/worktree | Ownership | Status |
|---|---|---|---|
| Integration | `codex/gta-v-nids` / `/Users/mcruz/Developer/sharpemu-gta-v-nids` | coordinator-owned manifest, queue, integration, regression | exact 1,432-registration parity complete |
| Loader prerequisite | `codex/nid-gta-loader` / `/Users/mcruz/.codex/worktrees/sharpemu-gta-loader` | `SelfLoader.cs` and focused loader tests only | integrated as `e6e71ac` |
| TLS prerequisite | `codex/nid-gta-tls` / `/Users/mcruz/.codex/worktrees/sharpemu-gta-tls` | shared Variant-II reservation and focused TLS tests only | integrated as `84652f1` |
| libc math implementation | `codex/nid-gta-libc` / `/Users/mcruz/.codex/worktrees/sharpemu-gta-libc` | 20 approved libc math exports and tests only | integrated as `0c84a2f` |
| libc core implementation | `codex/nid-gta-libc-core` / `/Users/mcruz/.codex/worktrees/sharpemu-gta-libc-core` | 12 approved libc math/RNG/string/time exports and tests only | integrated as `6fb1d12` |
| Direct-memory-query implementation | `codex/nid-gta-direct-query` / `/Users/mcruz/.codex/worktrees/sharpemu-gta-direct-query` | firmware/GTA Ghidra contract and kernel implementation/tests | integrated as `ce35c99`; GTA loop removal runtime-verified |
| NpManager premium callbacks | `codex/nid-gta-np-premium-callbacks` / `/Users/mcruz/.codex/worktrees/sharpemu-gta-np-premium-callbacks` | two firmware-proven callback exports and focused tests only | integrated as `f92ed50` |
| NpManager async requests | `codex/gta-v-np-async` / `/Users/mcruz/Developer/sharpemu-gta-v-np-async` | Create/Delete/Abort/Poll registry and focused tests only | integrated as `f7105d4`; [Ghidra packet](docs/gta-v/npmanager-async-ghidra.md) |
| libc search/conversion | `codex/gta-v-libc-deferred` / `/Users/mcruz/Developer/sharpemu-gta-v-libc-deferred` | Ghidra-exact `bsearch` and `strtoull` contracts and tests | integrated as `eb7a842` plus errno-order fix `8302781`; independent review passed |
| AGC TFRing | `codex/gta-v-agcdriver-tfring` / `/Users/mcruz/Developer/sharpemu-gta-v-agcdriver-tfring` | `sceAgcDriverSetTFRing` contract, state, and focused tests | integrated as `63f3515`; [Ghidra packet](docs/gta-v/agcdriver-settfring-ghidra.md) |
| AGC Hs-offchip parameter | `codex/gta-v-nids` | Ghidra-recovered `sceAgcDriverSetHsOffchipParam` contract, state, tests, and runtime gate | integrated as `740915e`; [Ghidra packet](docs/gta-v/agc-driver-hs-offchip-param.md) |
| Provider registration waves | `codex/gta-v-nids` | 837 exact Gen5, Ghidra-backed, provider-preferred evidence-wave registrations with fail-closed fallback | integrated; total `PreferLle` count is 839 when the two semantic handlers are included; final libc lane commit `f46fc0c` |
| Final kernel/POSIX contracts | `codex/gta-v-nids` | 27 exact Gen5 contracts, including nine semantic/partial and 18 fail-closed implementations | integrated as `9d8858f` |
| Final data-symbol contracts | `codex/gta-v-nids` | five first-class object registrations and relocation/dlsym separation | integrated as `9508369`, hardened as `aed5201` |
| Local reverse engineering | eight read-only Ghidra workers on the Mac | alternate/native variants for the 23 small-provider NIDs | complete 23/23; cleanup proof retained and zero campaign processes remain |
| Remote reverse engineering (Linux) | 40 two-core jobs plus targeted lanes in unique `/dev/shm` roots on `rho.cs.oswego.edu` | final kernel/libc/POSIX contracts and provider evidence | complete 121/121 with zero failures; peak measured use 85.719 cores; zero residue and zero Java workers |
| Remote reverse engineering (Windows) | 16 one-core jobs in unique `%TEMP%` roots on `192.168.68.54` | independent 23-NID small-provider audit and alternate/native variants | complete 23/23; independent cleanup found zero residue and zero Java processes |

No worker may edit this progress file, the central manifest, or the integration branch.

## Final-wave disposition

| Contract class | NIDs | Final disposition |
|---|---:|---|
| Libc provider functions | 34 | Ghidra-backed provider-preferred registrations with fail-closed fallback |
| LibcInternal backtrace | 1 | Ghidra-backed fail-closed HLE contract |
| Kernel/POSIX | 27 | nine semantic/partial contracts and 18 fail-closed contracts |
| Data objects | 5 | first-class data registrations with guest-first resolution |
| **Total** | **67** | **integrated** |

`GTA_V_UNCOVERED_NIDS.csv` now contains the canonical header and zero data rows. All 911 manifest items are integrated.

## Implementation contract

Every implementation must have:

1. A pinned source or binary-evidence reference.
2. A recovered signature and parameter/output contract.
3. Explicit success, failure, and side-effect behavior.
4. Focused positive and negative tests.
5. No unconditional success stub, invented output, or silent state mutation.

Large subsystems remain evidence/research lanes until this contract is met. The coordinator integrates one reviewed commit at a time and updates the manifest only after validation.

## Remote-worker policy

`rho` is suitable for parallel headless-analysis jobs: it exposes 88 CPUs, roughly 125 GiB RAM, and a 63 GiB empty `/dev/shm`. `DESKTOP-RAAKAQJ` (`192.168.68.54`) adds 32 logical CPUs, roughly 191 GiB RAM, and ample temporary disk. It currently has Java 17 but no Ghidra, so its jobs require an ephemeral JDK 21 and Ghidra 12.1.2 bundle. Remote jobs must:

- use a unique directory beneath `/dev/shm` on rho or `%TEMP%` on Windows;
- install/copy only the portable tooling and the smallest required binary slice or module;
- never transfer the whole game;
- register cleanup traps and remove the job directory on success or failure;
- return only reports, logs, scripts, and compact analysis artifacts;
- stay within the measured campaign points: 40 two-core Ghidra jobs on rho, 16 one-core Ghidra jobs on Windows, and eight one-core local Mac workers; scale again only after a new benchmark.

The rho smoke used only the 71,654-byte `libSceJobManager.prx` and completed in 20.09 seconds at 173% CPU with about 1.32 GiB peak RSS. It proved the ephemeral pipeline and cleanup, but stock Ghidra classified the PS5 SELF as a raw binary and recovered no real imports. Meaningful remote contracts therefore require a PS5 SELF loader or a locally reconstructed/decrypted ELF derivative before fan-out.

The Windows proof transferred only a locally reconstructed 1,334,184-byte sectionless libc ELF derivative, not the original SELF or the full game. A pinned Ghidra 12.1.2/JDK 21 run completed analysis in 30.783 seconds with eight analysis CPUs and about 1.40 GiB peak Java working set. It recovered 2,761 functions, 177,012 instructions, and three direct callers of the selected libc import. The later provider benchmark established 16 simultaneous one-core jobs as the throughput optimum at 0.4806 jobs/second, 82.92% average host CPU, and 100% peak CPU; 24-way concurrency was slower. Independent post-checks found zero campaign directories and zero campaign Java processes remaining. The retained capacity and cleanup records are [`windows-capacity-benchmark.json`](docs/gta-v/provider-evidence/windows-capacity-benchmark.json) and [`windows-cleanup-proof.json`](docs/gta-v/provider-evidence/windows-cleanup-proof.json).

The final small-provider campaign used 16 simultaneous one-core Windows jobs and eight one-core Mac workers. Both hosts independently resolved all 23 targets to the same provider hashes, function entries, and body hashes. The regular AJM provider lacked two exports; `libSceAjm.native.sprx` defined both. The durable cross-host packet and cleanup proofs are retained under [`docs/gta-v/provider-evidence/provider23`](docs/gta-v/provider-evidence/provider23), and both hosts reported zero live campaign processes after cleanup.

The rho GTA campaign transferred only a 65,928,068-byte sectionless eboot derivative, not the original eboot or full game. Its eight-worker Ghidra run independently recovered all four direct-memory-query loops and their `0x8002000D` termination rule. Whole-program auto-analysis reached its 900-second cap, but the targeted import resolution and containing-function decompilation completed; the unique `/dev/shm` campaign directory was removed and a fresh glob check found zero residual directories. The compact hashes, address normalization, decompile evidence, measurements, and cleanup proof are retained in [`docs/gta-v/rho-direct-memory-query-ghidra.md`](docs/gta-v/rho-direct-memory-query-ghidra.md).

The rho AGC campaign transferred only the 141,176-byte reconstructed `libSceAgcDriver.sprx` provider. Three independent RAM-backed Ghidra passes recovered the public export, selected callback/helper, and initializer in 14.74-15.34 seconds each at roughly 0.83-1.17 GiB peak RSS. Cleanup traps removed every `/dev/shm/sharpemu-agc-settfring-*` root, and independent checks found zero residual campaign directories or Java processes. The Mac independently recovered the same control flow. The evidence and machine-readable contract are retained in [`docs/gta-v/agcdriver-settfring-ghidra.md`](docs/gta-v/agcdriver-settfring-ghidra.md) and [`docs/gta-v/agcdriver-settfring-contract.json`](docs/gta-v/agcdriver-settfring-contract.json).

The rho provider saturation campaign ran 40 two-core Ghidra jobs concurrently. It observed 80.35 cores in use with about 19.19 GiB peak aggregate RSS, no swap pressure, and no material I/O wait. It produced exact executable-body evidence for 191 AGC/AGC-driver/AMPR exports; 190 became provider registrations and the semantic `MM4IZSEYytQ` implementation was provider-preferred instead of duplicated. An independent cleanup check found zero campaign directories and zero Java workers. The evidence is retained in [`docs/gta-v/rho-provider-lle-ghidra.md`](docs/gta-v/rho-provider-lle-ghidra.md).

The final rho contract campaign completed 121/121 Ghidra jobs with zero failures. Its kernel, libc, and deeper-analysis phases peaked at 79.655, 84.369, and 85.719 cores respectively, using unique RAM-backed roots. Both campaign roots were removed afterward, and independent checks found zero residual campaign directories and zero Java workers. The durable compact contract packet is retained under [`artifacts/gta-v-nid-evidence/rho-remaining90-contracts-20260718`](artifacts/gta-v-nid-evidence/rho-remaining90-contracts-20260718).

The local Mac remains responsible for integration, builds, runtime capture, final regression, and additional read-only Ghidra evidence lanes. The two remote hosts add parallel workers; they do not replace the local coordinator.

## Validation gates

- Pinned-base build and test baseline: passed on 2026-07-18
  - Release solution build: passed (pre-existing catalog warnings remain)
  - SharpEmu.Libs.Tests: 567/567 passed
  - SharpEmu.SourceGenerators.Tests: 33/33 passed
  - SharpEmu.ShaderCompiler.Tests: 34/34 passed
- Focused tests for each implemented contract, including failure paths
  - blocked-SELF loader tests: 13/13 passed
  - static-TLS focused tests: 7/7 passed
  - libc math focused tests: 77/77 passed
  - libc core focused tests: 109/109 passed
  - NpManager premium callback focused tests: 7/7 passed
  - direct-memory-query focused tests: 16/16 passed
  - NpManager async-request focused tests: 13/13 passed; concurrency case repeated 20 times in the isolated lane
  - AGC TFRing focused tests: 8/8 passed
  - AGC Hs-offchip focused tests: 6/6 passed
  - libc `bsearch`/`strtoull` focused tests: 18/18 passed, including errno/TLS fault ordering
  - provider/NpCpp/direct-routing focused tests: 29/29 passed
  - dual-host provider23 focused tests: 5/5 passed
- NID manifest/registration uniqueness check
  - manifest validator: 911/911 unique items valid
  - lifecycle: 911 integrated, 0 non-integrated
  - remaining CSV: exact canonical header with zero data rows
  - compiled parity test: exact 1,432/1,432 union, consisting of 1,427 callable registrations and five first-class data registrations
  - provider preference: exactly 837 Ghidra-backed evidence-wave registrations plus two semantic provider-preferred handlers, for 839 total `PreferLle` registrations; fail-closed fallback behavior remains explicit
- GTA V loader/import probe, then runtime unresolved trace
  - blocked-SELF `PT_DYNAMIC` translation: passed
  - static TLS reservation for the observed `0x13570` requirement: passed
  - guest entry and initial module initializers: reached
  - direct-memory-query enumeration contract: passed and runtime-verified across all four GTA loops
  - `sceAgcDriverSetTFRing` (`XlNp7jzGiPo`): former fatal gate cleared in the final x64 run
  - `sceAgcDriverSetHsOffchipParam` (`MM4IZSEYytQ`): former import-39,003 gate cleared with `(0, 0x1FF)`
  - all 436 new NpCppWebApi registrations resolved to direct guest-provider bridges
  - all 34 final libc registrations resolved to direct guest-provider bridges
  - final five data NIDs produced zero callable-routing events; 11 data relocations rebound with zero unresolved
  - highest observed import ordinal: 41,427; later RenderThread access violation remains unattributed
- SharpEmu library and source-generator tests
  - focused final parity/libc/kernel/data tests: 53/53 passed
  - SharpEmu.Libs.Tests after all integrations: 810/810 passed
  - Release solution build: passed with 0 warnings and 0 errors
  - SharpEmu.SourceGenerators.Tests: 36/36 passed
  - SharpEmu.ShaderCompiler.Tests: 34/34 passed
  - 2026-07-23 synchronized fork: solution build passed with 0 errors; full
    solution tests passed 1,462/1,462
- GTA V launch regression: prior 41,427-import checkpoint retained; exact terminal state recorded in the final evidence packet
- Registration parity does not close semantic follow-up: 18 kernel/POSIX contracts and the backtrace contract intentionally fail closed, `recv`/`send` support only flags zero, and provider registrations require their guest providers for semantics
