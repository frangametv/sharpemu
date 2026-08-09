# ASTRO BOT decisive experiment ledger

This ledger intentionally keeps only experiments that solved a blocker, moved the
active boundary, or corrected a conclusion. Earlier committed details remain in
Git history; recent uncommitted work is summarized here rather than preserved as
repetitive micro-probes. Current status and next actions live in
[`../../ASTRO_BOT_PROGRESS.md`](../../ASTRO_BOT_PROGRESS.md).

## Solved foundations

| IDs | Question | Decisive result | Kept conclusion |
| --- | --- | --- | --- |
| E01-E03 | Are real target registers, MRTs, and required imports available? | Real 4K/1080p targets decode, typed MRT executes, and the title loads after the missing NIDs are implemented. | Retain Gen5 defaults, typed MRT, and compatibility exports. |
| E13 | Is uninitialized depth rejecting the title? | Neutral first-use depth makes the solid control pass with normal depth state. | Generic first-use depth initialization is required and solved. |
| E26-E27 | Are shader opcodes or render-to-texture aliases dropping UI passes? | Literal FMA translation executes; compatible UNORM/SRGB views reuse the same host image. | Retain both fixes; neither alone renders the menu. |
| E37 | Why do valid late pixel values disappear? | GFX10 `VCMPX` must update EXEC only; writing a scalar destination destroyed the guest-saved mask. | Retain EXEC-only `VCMPX` lowering. |
| E44 | Why does `RoomLoad_ATQT` crash? | The exercised `sce::Json` ABI removes the crash and reaches exact title start with recognizable original-shader geometry/logo. | JSON ABI support is a real independent boot/title fix. |
| E50 | Why did an upstream merge lose the PS Studios sequence? | Restoring the full FFmpeg-backed AvPlayer path restores decoded frames, completion signaling, and title transition. | Preserve the full AvPlayer implementation during syncs. |
| E57 | Can the original graph produce anything after the logo? | Correct storage-image lifecycle changes uniform black into animated nonblack output, albeit blue/striped and wrongly composed. | Geometry/composition exists; storage lifecycle fix is necessary. |

## Geometry boundary

| IDs | Question | Decisive result | Kept conclusion |
| --- | --- | --- | --- |
| E99-E101 | Is the pixel shader the cause of the blank title target? | Solid PS plus original ES remains empty; fixed fullscreen VS plus solid PS fills the target. | Coverage is lost before pixel shading. |
| E104-E111 | Where does original vertex coverage collapse? | Exported position is `(0,0,0,1)`; vertex index 24736 is valid, but the exact 64-byte backing record is zero. | Follow the producer of the rotating geometry buffers. |
| E134 | Why were intro frames missing despite decoded work? | Post-flip yielding presents 35/35 ordered flips with no coalescing and restores the controller-symbol/wordmark sequence. | Presentation starvation is solved while retaining the four-frame bound. |
| E143 | Why did title compute stall after dirty guest image writes? | Dirty refresh plus correct vertex/fragment/compute sampled-stage barriers passes the intro and advances beyond the old compute stall. | Guest/host image refresh and barriers are solved at this boundary. |
| E170-E171 | Why were scene lists and selectors empty at the live title? | KawaiiDra identifies `wLsJlmgEIaI` as `sce::Json::Value::referValue(const String&)`; implementing it removes assertions and restores title records, selector refill, and producer execution. | Scene registration is solved; the red title remains downstream. |

## Current-build regression and recovery

| IDs | Question | Decisive result | Kept conclusion | Evidence |
| --- | --- | --- | --- | --- |
| E188-E190 | Why does the large Emitter never dispatch correctly? | The AGC header proves a valid program beyond the old instruction ceiling. Exact-size decoding emits SPIR-V and dispatches `192x1x1`. | Large-shader decoding is fixed. | `artifacts/astro-bot/runs/20260716-160643-e190-emitter-after-size-bound/` |
| E192-E193 | Did the upstream sync preserve intro/title progress? | Build/tests and intro pass, but exact title start regresses. | Find the merged runtime regression; intro is not the culprit. | `artifacts/astro-bot/runs/20260716-162846-e193-post-sync-full-title-visual/` |
| E194, E196 | Are zero selectors before title start the live failure? | Early calls only map and clear the tables; the run never reaches exact title start. | Early zero snapshots are timing evidence, not live-menu evidence. | `artifacts/astro-bot/runs/20260716-163734-e194-full-title-producer-chain/`; `.../20260716-165657-e196-selector-exec-ladder/` |
| E197 | Did the Emitter decoder introduce the regression? | Detached pre-sync commit `890224a` reaches exact title start. | Emitter decoder is exonerated; regression entered in the sync. | Detached A/B worktree run. |
| E198-E199 | Is host-buffer reuse itself invalid? | Both no-cache and effectively unlimited-cache controls reach exact title start. | Reuse is valid; sticky 128 MiB admission is the failure. | `artifacts/astro-bot/runs/20260716-170813-e198-no-host-buffer-cache-title-ab/`; `.../20260716-171354-e199-unbounded-host-buffer-cache-title-ab/` |
| E200-E201 | Does bounded global LRU fix title-stage churn? | 193/193 library tests pass and exact title start succeeds twice at the normal 128 MiB budget. | The post-sync title-start regression is fixed reproducibly. | `artifacts/astro-bot/runs/20260716-172145-e200-lru-host-buffer-cache-title-ab/`; `.../20260716-172407-e201-lru-host-buffer-cache-repeat/` |
| E204 | Is the intro still visually present after the LRU fix? | Compact evidence contains controller symbols and the PS Studios wordmark before the title frame. | Keep this as the visual regression proof; capture timing is not the active task. | `artifacts/astro-bot/runs/20260716-174618-e204-lru-visual-gate-final/` |
| E206 | Are the 96 KiB selector tables still zero at the live title? | Exact title starts; `0x800253C42`, `0x800253C48`, and the following store path execute. Each table has 653 nonzero bytes, list 0 has 28 records, work counters reach 24898, and 40 indexed state records are selected with active gates. | Selector production and visibility work. Probe ObjectUpdate output, Emitter output, and record 24736 next. | `artifacts/astro-bot/runs/20260716-180409-e206-live-selector-to-geometry/` |
| E208 | Are the intro's dark/incorrect colors caused by packed target or NV12 layout decoding? | Extended frames now use aligned Y/UV planes, and 2:10:10:10 targets honor `COMP_SWAP`; focused layout/format tests cover both. | Retain both generic fixes. | Source tests. |
| E209-E211 | Can `V_INTERP_MOV_F32` be lowered directly through Vulkan barycentrics? | MoltenVK rejects `PerVertexKHR`; derivative reconstruction must be emitted before divergent dispatcher flow, but alone still produces repeated image bands. | Keep the ordinary barycentric path; retire `PerVertexKHR` on MoltenVK. | `artifacts/astro-bot/forensics/e210-vinterp-derivative/`; `.../e211-vinterp-uniform/` |
| E212-E214 | Is the repeated-band corruption introduced during presentation? | Immediate render-target readback is already corrupt. Exact ES/PS capture identifies `0x50076BE00`/`0x50076D300`. | The presenter is exonerated; inspect semantic linkage. | E212 and E214b run directories. |
| E215-E216 | Do AGC semantics remap packed vertex data? | Headers and live registers prove mapping `0,2,0x423`; output 3 carries the custom packed values. Unique host pixel locations plus vertex fan-out remove MoltenVK duplicate-location errors. The controller animation is now coherent and better colored; exact title/worldmap load passes, while the final frame remains red. | Semantic linkage is fixed generically. Resume title producers/composition; menu rendering is still incomplete. | `artifacts/astro-bot/runs/20260716-230809-e216-unique-host-interpolants/` |
| E218 | Does current `par274/main` preserve the corrected controller animation and title milestone? | The runtime merge through `a9a4f51` passes Release, all four test groups, and the visible ordered controller/wordmark/title gate. Follow-up tip `24b82a7` adds only the website-release workflow and is merged at `2787757`. | Current upstream is regression-safe. Keep the merges and resume at the title producer/composition boundary. | `artifacts/astro-bot/runs/20260716-233311-e218-post-upstream-controller-direct/` |

## Retired experiments and storage

The following current-day runs were deleted because they were aborted,
duplicative, or superseded: E191, E195/E195b, E202, E203b, and E205. Raw
per-frame PNG directories were also removed from retained runs; compact logs,
manifests, contact sheets, milestone sheets, and the PR #216 baseline remain.

Current retained run evidence is roughly 33 MB instead of 2.15 GB. Do not add a
new row for a launch that exits before the exact milestone unless the exit itself
is the question being tested.
