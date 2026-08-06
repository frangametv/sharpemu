# ASTRO BOT menu progress

Last updated: 2026-07-19

This is the canonical working journal. The compact decisive-experiment ledger is in
[`docs/astro-bot/experiments.md`](docs/astro-bot/experiments.md). Earlier
committed chronology remains available in Git history; recent uncommitted work
has been reduced to the decisive results below.

## Goal

Reach a correctly rendered, stable, interactive ASTRO BOT title/menu with the
original guest shaders. Replacement colors and title-specific bypasses are
diagnostic controls only.

## Current checkpoint

- Worktree: `/Users/mcruz/Developer/sharpemu`
- Branch: `codex/astro-bot-menu-progress`
- PR 412's threading work and the upstream `gpu-bootstrap-stall` history are
  integrated. The pre-main-sync checkpoint is `6cc3f62`; the current tree also
  merges upstream `main` through `90c72eb` while retaining the PR 412
  one-host-thread-per-guest-thread synchronization model.
- The original-shader boot reaches `title_controller_ship` reproducibly and the
  cyan radial-line scene remains the retained visual baseline.
- A stale `DB_DEPTH_SIZE_XY=0` descriptor was being expanded from 1x1 to
  1920x1080 while retaining a zero clear. The first depth-writing bootstrap draw
  contains one point, so the expanded remainder rejected the later 24,192-index
  scene draw under `LESS_EQUAL`.
- Vulkan now gives inferred stale-one-by-one depth surfaces a compare-neutral
  first clear even when that bootstrap draw enables depth writes. This is a
  general inferred-surface rule, not a title shader bypass.
- Unmodified verification run
  `20260719-112341-title-stale-depth-neutral-fix-visual` reaches visible
  **Sony Interactive Entertainment presents** text. The decisive frame is
  `frame-048-t+00079.7s.png`; the log records
  `source=neutral-stale-one-by-one`, `guest_clear=0`, `effective_clear=1`.
- Remaining blocker: the title text and feeder output are duplicated and
  horizontally compressed into a central band. The next boundary is viewport /
  render-target composition and scaling, not guest timing or CPU threading.

## Solved blockers

| Area | Decisive evidence | Retained resolution |
| --- | --- | --- |
| Real render targets and MRT | E01-E02 | Decode the full Gen5 register state and retain typed two-target MRT support. |
| Missing imports and JSON ABI | E03, E44, E170 | Implement the required NIDs plus stable `sce::Json::Value`/`String` reference semantics, including `referValue(const String&)`. |
| First-use depth | E13 and 2026-07-19 stale-extent proof | Track initialization source and apply a compare-neutral first-use depth value, including inferred stale-one-by-one surfaces whose bootstrap draw enables writes. Do not use a title depth bypass. |
| Shader semantics | E26, E37 | Retain GFX10 literal FMA opcodes and EXEC-only `VCMPX` behavior. |
| Render-to-texture identity | E27, E57 | Reuse compatible UNORM/SRGB mutable views and preserve the storage-image lifecycle. |
| PS Studios video | E50 | Preserve the full FFmpeg-backed AvPlayer decode/callback/upload path during upstream merges. |
| Intro presentation starvation | E134 | Yield after ordered flips while keeping the four-frame head-plus-newest-three memory bound. |
| Sampled-image coherency | E143 | Refresh dirty promoted images and use sampled-stage transfer barriers for vertex, fragment, and compute consumers. |
| Standard-64K mip source layout | 2026-07-19 title checkpoint | Resolve the reverse-ordered non-tail levels and packed mip-tail origin before detiling; title BC7 mip 0 begins at `+0x80000`. |
| Large compute programs | E188-E190 | Decode using the AGC header's exact shader size instead of the old 4,096-instruction ceiling. |
| Post-sync title-start regression | E197-E201 | Replace the sticky 128 MiB host-buffer admission policy with a bounded global LRU that evicts cold idle allocations. |
| Video layout and packed target order | E208 | Honor extended NV12 pitch/plane layout and `CB_COLOR_INFO.COMP_SWAP`; retain the focused layout/format tests. |
| Pixel/vertex semantic linkage | E215-E216 | Match AGC semantic IDs, retain custom/flat controls, use unique host pixel locations, and fan guest vertex exports into every consuming attribute. |
| Windows stock-launch imports | E228 | Implement the required font, pad, AvPlayer/tool-discovery, and audio-propagation exports; do not rely on GUI environment toggles. |
| Compute writeback starvation | E229-E231 | Reject unchanged 4 KiB pages with vectorized comparisons before byte-run scanning. The large Emitter writeback fell from about 1.6 seconds to about 56 ms. |
| Windows guest debug traps | E232 | Treat guest `INT 0x41` as an automatic debugger/assert notification in direct execution, with an explicit recovery opt-out for debugger-oriented runs. |

## Decisive recent evidence

| IDs | Result | Conclusion | Evidence |
| --- | --- | --- | --- |
| E170-E171 | `referValue` removes JSON assertions, restores active title records and selector refill, and preserves the ordered intro; the title remains red. | Scene registration is fixed. Continue at the geometry-producer boundary. | `artifacts/astro-bot/forensics/e170-json-refervalue/` and Git history. |
| E187 | Original shaders show the wordmark and reach the exact title start with the F1 overlay visible. | The remaining visual blocker is after the intro. | Prior run recorded in Git history. |
| E188-E190 | Emitter dispatch plumbing is valid; shader `0x555F4F500` was truncated by the decoder. Exact-size decoding emits SPIR-V and dispatches `192x1x1` with GPU/global writes enabled. | Large-shader decoding is fixed; do not reopen the old instruction ceiling. | `artifacts/astro-bot/runs/20260716-160643-e190-emitter-after-size-bound/`. |
| E192-E193 | Upstream sync builds/tests, the intro survives, but exact title start regresses. | The regression entered through the sync rather than the intro or Emitter decoder. | `artifacts/astro-bot/runs/20260716-162846-e193-post-sync-full-title-visual/`. |
| E194, E196 | Before exact title start, selectors are zero and the builder only enters/maps/clears them. | Useful early-state evidence only; it is not evidence about the live menu. | `artifacts/astro-bot/runs/20260716-163734-e194-full-title-producer-chain/` and `.../20260716-165657-e196-selector-exec-ladder/`. |
| E197 | Detached pre-sync commit `890224a` reaches exact title start. | The large decoder/Emitter changes are exonerated. | `../sharpemu-astro-pre-sync-ab/artifacts/astro-bot/runs/20260716-170042-e197-pre-sync-decoder-title-ab/`. |
| E198-E199 | Both zero-cache and effectively unlimited-cache controls reach exact title start. | Reuse is valid; the bad behavior was sticky 128 MiB admission and title-stage churn. | `artifacts/astro-bot/runs/20260716-170813-e198-no-host-buffer-cache-title-ab/` and `.../20260716-171354-e199-unbounded-host-buffer-cache-title-ab/`. |
| E200-E201 | The bounded global LRU passes 193/193 library tests and reaches exact title start twice at the normal 128 MiB budget. | Host-buffer cache regression is fixed reproducibly. | `artifacts/astro-bot/runs/20260716-172145-e200-lru-host-buffer-cache-title-ab/` and `.../20260716-172407-e201-lru-host-buffer-cache-repeat/`. |
| E204 | Compact current-build evidence preserves the controller-symbol animation and PS Studios wordmark before the title frame. | Intro remains a regression gate, but capture timing is not the current work item. | `artifacts/astro-bot/runs/20260716-174618-e204-lru-visual-gate-final/`. |
| E206 | Exact title starts. The dead path at `0x800253C22` does not execute; gate `0x800253C42`, live refill `0x800253C48`, and the following store path execute. Each live 96 KiB table has 653 nonzero bytes; list counts are `28/0/0/0`, work counters are `24898/24898/24898/0/0`, and the indexed join selects 40 state records with active `+176` and `+188` gates. | The selector builder, CPU write visibility, and selector-to-state join work at the live title. The next unknown is the compute output, not selector population. | `artifacts/astro-bot/runs/20260716-180409-e206-live-selector-to-geometry/`. |
| E207 | Sandboxed launches reached `Window.Create` on managed thread 1 but blocked in `glfwInit -> NSApplication.run` before Cocoa's application-finished-launch callback. A stale E207 process made later retries non-singleton. Launching the same diagnostic build through a normal Terminal-owned `.command` completed `Window.Create`, entered initialization, attached keyboard input, selected the Apple M3 Max Vulkan device, reported Cocoa, presented the splash, started `ps_logo`, reached exact `title_controller_ship`, and loaded `worldmap`. | The missing emulator window was a macOS LaunchServices/Cocoa bootstrap problem, not headless rendering or an ASTRO shader regression. Gate Cocoa readiness within 10 seconds and run visible macOS tests from the interactive desktop session. | Failed control: `artifacts/astro-bot/runs/20260716-191348-visible-e206-literal/`; successful visible launch: `artifacts/astro-bot/runs/20260716-192736-terminal-visible/`. |
| E208 | Extended AvPlayer frames used a guest-reported aligned pitch while the HLE copied tightly packed NV12, and packed 2:10:10:10 render targets ignored `COMP_SWAP`. | Copy Y/UV rows into the declared 256-byte-aligned NV12 layout and decode component swap independently from format. These are generic video/target fixes with focused tests. | Source tests `AvPlayerNv12LayoutTests` and `VulkanRenderTargetFormatTests`. |
| E209-E211 | `V_INTERP_MOV_F32` was first attempted with `PerVertexKHR`, which MoltenVK cannot lower. Derivative reconstruction compiled only after moving derivatives before the divergent PC dispatcher, but the image remained tiled/duplicated. | Keep the standard barycentric path for ordinary interpolation. Do not retry `PerVertexKHR` on MoltenVK or emit derivatives inside divergent control flow. | `artifacts/astro-bot/forensics/e210-vinterp-derivative/`, `.../e211-vinterp-uniform/`. |
| E212-E214 | Immediate post-draw readback already contained the repeated bands. Shader dumps identified ES `0x50076BE00` and PS `0x50076D300`; treating their attributes as identity-mapped was the remaining false assumption. | Corruption originated before presentation. Decode the AGC semantic tables rather than tuning the presenter. | `artifacts/astro-bot/runs/20260716-221034-e212-vinterp-uniform-readback/`, `.../20260716-223129-e214b-vinterp-vertex-program/`. |
| E215 | Static headers prove PS semantics `0,2,3` map to VS outputs `0,2,3`; input 3 is custom/flat and carries the packed-normal values. The runtime registers are exactly `0x000,0x002,0x423`. Controller geometry/color becomes coherent, but other shaders expose duplicate host locations. | The earlier conclusion that packed VS parameter 3 was unused was wrong. Generic semantic mapping is required. | `artifacts/astro-bot/runs/20260716-225754-e215-semantic-interpolant-mapping/`. |
| E216 | Host locations keyed by pixel attribute plus vertex-export fan-out eliminate duplicate `locn0` declarations. Fourteen interpolation and two AGC mapping tests pass. A visible original-shader run renders the corrected controller sequence, reaches exact title start, loads `worldmap`, and reports no MoltenVK/pipeline errors; the final frame remains uniform red. | Stage linkage is fixed without title-specific shader replacement. Resume at the existing title producer/composition boundary; the menu is not rendered yet. | `artifacts/astro-bot/runs/20260716-230809-e216-unique-host-interpolants/`. |
| E218 | Merging the runtime-affecting `par274/main` delta through `a9a4f51` required only AGC and Vulkan-presenter conflict resolution. Release publishes cleanly; 256 library, 27 shader, 33 source-generator, and 6 harness tests pass. The visible original-shader run captures 136 frames, classifies the ordered animation/controller/wordmark sequence, reaches exact title start at 112.497 seconds, loads `worldmap`, and ends on the same uniform-red title frame. Upstream then advanced to CI-only `24b82a7`, merged as `2787757`. | Current upstream preserves the controller-symbol regression gate and all known ASTRO progress. The red menu output remains the active graphics blocker. | `artifacts/astro-bot/runs/20260716-233311-e218-post-upstream-controller-direct/`. |
| E219-E222 | Post-fence full readback found both rotating 1.5 MiB outputs of CS `0x50740A700` uniformly zero. All 71 observed dispatches bind the input at `s8` and the writable output through `s20/s0`; translation reports GPU execution and global writes with no raw or unsupported opcode. Disabling translated bounds did not change the result. A one-word direct sentinel did survive the Vulkan fence and GPU-to-guest writeback. | Descriptor binding, writable storage, fence completion, and writeback work. Bounds are not the blocker. E221's forced first-store experiment was inconclusive because E224 later proved that path is not selected. | `artifacts/astro-bot/runs/20260717-033050-e219c-live-1p5m-post-fence/`, `.../20260717-033530-e219e-live-compute-binding-shapes-guardfix/`, `.../20260717-033932-e220-unchecked-target-buffer-bounds/`, `.../20260717-034909-e222b-direct-output-binding-sentinel/`. |
| E223-E224 | An initial eight-marker probe wrote the same words from every invocation and starved presentation; it is diagnostic perturbation only. Restricting markers to workgroup zero/local invocation zero restored `ps_logo` and reached `title_controller_ship` in 50.6 seconds. Runtime bytes prove entry `0x30` and compare `0x22C` execute, branch `0x230` skips the first output path `0x234..0x9D4`, and the alternate path enters through `0x9DC` and `0x9E4`. Both real outputs otherwise remain zero. | The first-store block is not the active producer path. Continue inside the alternate `0x9E4..0xA40` load/store block and capture its address, EXEC, and source values. | Invalid control: `artifacts/astro-bot/runs/20260717-035640-e223-live-compute-block-reachability/`; decisive run: `artifacts/astro-bot/runs/20260717-040329-e224-single-invocation-block-reachability/`. |
| E225 | At PC `0x9FC`, local invocation zero has `v43=0`, EXEC active, and all 32 payload VGPRs zero. Independent markers after stores `0xA00` and `0xA40` both survive, so the complete alternate store sequence executes. Static slicing shows most payload registers are loaded directly from the zero 1.5 MiB `s8` record, while the active-record decision begins with a two-dword load from the 96 KiB `s24` selector table at PC `0x38`. | Store control flow, EXEC, address, bounds, and writeback are exonerated. Compare the live CPU selector bytes with the Vulkan resource backing `s24`; stale CPU-to-GPU upload is now the leading hypothesis. | `artifacts/astro-bot/runs/20260717-040836-e225-alternate-store-source-values/`. |
| E226-E227 | Immediate upload comparisons and a 60-second post-title run found the queued snapshot, live guest bytes, and Vulkan shadow identical and uniformly zero for all four 96 KiB `s24` tables. Replaying the exact E206 trace controls produced 136 source snapshots (34 per table), all zero, with `list_counts=0/0/0/0`; the old 653-byte refill did not recur. | Stale CPU-to-GPU upload is rejected. E234-E236 supersede the upstream-regression interpretation: the zero-selector result belongs to the uncommitted experiment layer in the playable worktree. | `artifacts/astro-bot/runs/20260717-041700-e226b-selector-upload-snapshot-vs-live-nidfix/`, `.../20260717-041902-e226c-selector-refill-live-transition/`, `.../20260717-042253-e227-replay-e206-selector-controls/`. |
| E228 | A clean Windows GUI-library launch with no environment toggles resolves the newly implemented compatibility NIDs, reaches `ps_logo`, loads `title_controller_ship`, and no longer stops at the earlier missing audio-propagation import. The guest image remains black/checkerboard and compute runs near 0.5 FPS. | Windows startup/import parity is restored. Rendering and throughput remain separate blockers. | `artifacts/astro-bot/windows/20260717-e228-e233-stock-parity/e228-stock-t95.log`. |
| E229-E231 | Shader step limits of 4096 and 64 do not change the large Emitter timing. AGC tracing shows the RTX queue wait is below 1 ms, while E230 phase timing attributes about 1.6 seconds to scanning 17.5 MiB of potentially writable host buffers. A vectorized unchanged-page fast path cuts the Emitter writeback to about 56 ms and raises early Windows presentation as high as 5.8 FPS. | The large shader is not slow on the GPU and is not trapped in the translated dispatcher. CPU writeback comparison was the dominant starvation source; retain the generic page fast path. | `artifacts/astro-bot/windows/20260717-e228-e233-stock-parity/e229-agc-trace.log`, `.../e230-phase-timing.log`. |
| E232 | Windows Event Log identifies the old close as `0xC0000005` at guest `0x8000012B4`, whose bytes are `CD 41`. KawaiiDra decompilation of the mapped raw slice shows this instruction on an error-report/assert path. With generic recovery enabled by default, a zero-toggle stock GUI launch recovers the site twice, decodes 23 PS Studios frames through 8.325 seconds, starts `title_controller_ship`, reaches the red endpoint, remains alive for more than three minutes, and logs no fatal native exception. Five policy tests pass. | The Windows-only post-logo crash is fixed without a launch flag. Windows now reaches the same known red title blocker as macOS. | `artifacts/astro-bot/forensics/e232-windows-int41/`, `artifacts/astro-bot/windows/20260717-e228-e233-stock-parity/e232-stock-parity.log`, `.../e232-stock-red.png`. |
| E233 | A whole-range `SequenceEqual` control leaves the Emitter median writeback near 56.5 ms, proving its dirty ranges contain at least one changed page. The zero-toggle run again decodes the full video, recovers two `INT 0x41` traps, starts the title, reaches red, and remains fatal-free. | Keep the safe whole-range fast path for fully unchanged descriptors, but do not treat it as the remaining ASTRO performance or menu fix. Resume the 96 KiB selector regression boundary. | `artifacts/astro-bot/windows/20260717-e228-e233-stock-parity/e233-full-range-control.log`, `.../e233-stock-red.png`. |
| E234-E236 | Replayed the exact E206 selector probe with a visible emulator at `4b4379a`, post-upstream-merge `2effc63`, and clean current tip `06e6738`. Every build reaches gate `0x800253C42`, refill `0x800253C48`, `list_counts=28/0/0/0`, and four selector tables with 653 nonzero bytes and hash `0xADD07B945876F716`. | The semantic shader commit, upstream merge, and clean current tip are exonerated. The E227 selector regression is entirely within the uncommitted dirty experiment layer. Isolate its only default-active CPU change first: adding both mutex-lock NIDs as import-loop guard boundaries. | `artifacts/astro-bot/runs/20260717-054630-e234-selector-boundary-4b4379a/`, `.../20260717-055205-e235-selector-boundary-2effc63/`, `.../20260717-055548-e236-clean-06e6738-selector-control/`. |
| E237 | Applied only the two dirty mutex-lock boundary lines to clean `06e6738` and replayed E206. The run reaches `title_controller_ship`, but the refill executes with `list_counts=0/0/0/0`; no selector table ever reaches 653 nonzero bytes. The log records millions of `scePthreadMutexLock` calls. | Culprit confirmed. A mutex acquisition is not a yielding boundary: resetting the import-loop history on every lock hides the busy loop that the watchdog must break, starving the scene-registration path. Remove both boundary additions; keep the actual wait/usleep boundaries. | `artifacts/astro-bot/runs/20260717-060449-e237-mutex-loop-boundary-isolation/`. |
| E238 | Removed the two mutex boundary additions, rebuilt the full dirty playable tree, and replayed E206. The visible run completes its title milestone; all four selector tables return to 653 nonzero bytes, `list_counts=28/0/0/0`, `unique_targets=40`, and live ObjectUpdate fields at source records beginning with 10615. | The selector → ObjectUpdate boundary is restored in the real working tree, not only the clean control. Target global invocation 10615 next instead of invocation zero. | `artifacts/astro-bot/runs/20260717-060846-e238-restored-selector-chain/`. |
| E239 | Added an environment-selected global-invocation filter to the bounded compute probe and sampled invocation 10615. The heavy probe was killed before the title milestone, but its second attempt completed the relevant dispatches: EXEC is active at PC `0x38`, `v43=10615`, and both `v5/v6` remain zero after the selector load. The alternate path at `0x9E4..0xA40` executes with zero `v0..v7`; its two markers survive writeback. | Invocation-zero evidence is superseded, but the payload result is unchanged on a genuinely selected record. The first-zero boundary is now the PC `0x38` load from the live 96 KiB `s24` table. Compare the nonzero guest snapshot, Vulkan shadow, and shader-visible descriptor in one run, and dump the decoded IR to verify destination/address semantics. | `artifacts/astro-bot/runs/20260717-061443-e239-active-selector-invocation-10615/`. |
| E240 | Replayed invocation 10615 with address-filtered upload comparison and dumped all three observed variants of CS `0x50740A700`. When the CPU fills selector table `0x400AE4280`, the queued snapshot and live guest memory both contain 653 nonzero bytes with hash `0xB7CB7CC253C30996`; the old GPU shadow is still zero immediately before that dispatch's refresh. On the following dispatch snapshot, shadow, and live memory all contain the same 653 bytes and hash. The decoded IR consistently identifies PC `0x38` as indexed `BufferLoadDwordx2 v5,v6 <- v43,s24`, and the paired 1.5 MiB target begins changing after selectors appear. | The CPU producer and Vulkan upload path are working. Do not add a forced upload or reopen scene registration. The remaining narrow question is the shader-visible `s24/s25` descriptor and exact post-load values for active invocation 10615. Add exact-offset readback so the capture is observable even when unrelated output bytes change earlier in the buffer. | `artifacts/astro-bot/runs/20260717-061907-e240-selector-read-coherence/` (including preserved IR/SPIR-V under `shader-dumps/`). |
| E241 | Added bounded exact-dword writeback logging and captured invocation 10615's post-PC-`0x38` state. Before selector refill, the capture shows `s24=0x00BD4300`, `s25=0x00080004`, `v43=10615`, and EXEC=1: the shader-visible descriptor is base `0x400BD4300` with an 8-byte stride. All four selector tables later reach 653 nonzero bytes and snapshot/shadow/live agree. The requested capture slots nevertheless logged zero because the probe's diagnostic stores were 208 bytes early, exactly the low-address bias of the Vulkan-aligned binding; marker bytes in `changed_head` prove the offset error. | Runtime descriptor decoding and stride are correct. The observed post-refill value is still unreadable because of a probe-only alignment bug, not proven zero. Apply `ApplyGuestBufferByteBias` to diagnostic stores, rebuild, and repeat the same bounded capture. | `artifacts/astro-bot/runs/20260717-062733-e241-runtime-selector-descriptor/`. |
| E242 | Rebuilt with the guest-byte-bias correction. Exact dword readback now works and confirms the pre-refill state (`v5/v6=0`, valid 8-byte descriptor, `v43=10615`, EXEC=1, both markers intact). This attempt exited at 41 seconds, before selector refill, when the import-loop watchdog fired on `9UK1vLZQft4` (`scePthreadMutexLock`) at the root guest context. | Inconclusive GPU result. This is the known intermittent top-level mutex-loop unwind, not a shader/presenter crash. Do not change mutex-boundary semantics; repeat the same bounded capture on a successful boot. | `artifacts/astro-bot/runs/20260717-063117-e242-post-refill-selector-value/`. |
| E243 | Repeated the corrected capture with retries. Attempts 1 and 2 hit the same early top-level mutex-loop unwind; attempt 3 reached `title_controller_ship`, filled all four selector tables, and continued to worldmap. After refill, the output-buffer slots contained `1/4/.../0x40000000` and both markers were gone. Those words are real later shader output overwriting the diagnostic slots, not the PC `0x38` capture. | Successful boot, inconclusive post-load value. A writable producer output cannot preserve an early-instruction capture. Move the eight words to the read-only selector binding, trace them after the fence, and restore the GPU-only words from the allocation shadow so neither guest memory nor later dispatches are perturbed. | `artifacts/astro-bot/runs/20260717-063321-e243-post-refill-selector-value-retry/`. |
| E244 | Redirected the eight-word PC `0x38` capture into the read-only selector binding and restored the touched words from its Vulkan shadow after each fence. The pre-refill result is exact and repeatable (`v5/v6=0`, valid stride-8 descriptor, `v43=10615`, EXEC=1, both markers intact). The successful title attempt was killed immediately after the selector tables reached 653 nonzero bytes, before a matching post-fence readback; all three attempts ended with `SIGKILL`. | Still no trustworthy post-refill value. Writing diagnostics into a live read-only selector creates an unsafe shader/readback race and may be the kill trigger. Do not touch a guest binding again; append a dedicated transient capture buffer to only the targeted compute dispatch and read it after the fence. | `artifacts/astro-bot/runs/20260717-063928-e244-readonly-selector-capture/`. |
| E245-E245b | Added a 64-byte transient capture descriptor only to the selected compute shader. The first full run remained stable through title/worldmap but every capture was zero because synthetic descriptors have no entry in the guest runtime-length table, so the generic bounds guard suppressed all stores. A fixed-capacity direct-store correction then made the invocation-zero control exact: EXEC=1, valid stride-8 descriptor, and both constant markers survived the fence. | The isolated diagnostic path is now proven and no longer modifies guest memory. The initial all-zero E245 capture is a probe bug, not game evidence. | `artifacts/astro-bot/runs/20260717-065158-e245-dedicated-selector-capture/`, `.../20260717-065659-e245a-dedicated-buffer-invocation-zero/`, `.../20260717-065857-e245b-dedicated-buffer-invocation-zero-boundsfix/`. |
| E246 | Replayed global invocation 10615 with the corrected isolated buffer. The probe matched 322 dispatches before selector refill (`v43=10615`, EXEC=1, markers intact), then matched none of the 17 dispatches after refill even though the tables became nonzero. | Host global-invocation numbering changes across the later dispatch/base shape. Do not infer a zero post-refill load from missing markers; match the semantic guest record register (`v43`) instead. | `artifacts/astro-bot/runs/20260717-065931-e246-post-refill-selector-load/`. |
| E247 | Added a generic VGPR-value probe selector and matched `v43 == 10615` independent of host dispatch numbering. All 17 post-refill dispatches now capture reliably with EXEC=1, valid rotating descriptors, intact markers, and `v5/v6=0`. The indexed CPU summary, however, only proves 653 nonzero bytes somewhere in each table; it identifies record 10551, not 10615, as the first definitely nonzero selector (`first_nonzero=84408`, `10551 -> 15028`). | The GPU zero for record 10615 may be correct. Before reopening descriptor/upload translation, repeat the semantic capture at known-nonzero record 10551. | `artifacts/astro-bot/runs/20260717-070553-e247-semantic-selector-load/`. |
| E248 | Matched known-nonzero selector record `v43 == 10551`. After all four CPU tables reached 653 nonzero bytes, every one of 16 rotating dispatches loaded the same nonzero pair at PC `0x38`: `v5=0x000007E4`, `v6=0x000039DC`, with EXEC=1 and both markers intact. | Selector creation, CPU-to-Vulkan upload, runtime descriptor/stride/bias, bounds, indexed `BufferLoadDwordx2`, and isolated readback all work. Do not revisit selector plumbing. Follow `v5/v6` through the next target-record load and branch/store boundary to find where the nonzero chain is lost before the 1.5 MiB output. | `artifacts/astro-bot/runs/20260717-071058-e248-known-nonzero-selector-load/`. |
| E249 | Followed known-good record 10551 through the target-record reads and branch boundary. The shader loads `v22=1` and `v3=0x1CC`, keeps EXEC active through PCs `0xF8` and `0x22C`, and reaches the main output stores at `0x9B8`/`0x9D4`. The complement at `0x9DC` makes EXEC zero for this lane, so the apparent alternate-path markers are probe-only observations and its stores are correctly suppressed. Historical dispatch traces map `s8` to the 1.5 MiB input and both `s20` and `s0` to the same writable 1.5 MiB output; the two guest buffers ping-pong between dispatches. | The selector and target records are valid and the producer's main path executes. Stop debugging scene registration and control flow. Verify the record-10551 bytes written through `s20` survive the fence/writeback, then follow that output into the Emitter. | `artifacts/astro-bot/runs/20260717-071555-e249-known-nonzero-control-flow/`. |
| E250-E250b | Traced both 1.5 MiB ping-pong buffers after the fence. They transition from zero to about 11,057 nonzero bytes, and the following dispatch sees identical nonzero snapshot, Vulkan shadow, and live guest hashes, proving GPU stores and dirty guest publication. The first exact-dword request used decimal text and was ignored by the hex parser; the corrected run read all 32 dwords of output record 10551. That particular record remains zero even while neighboring output records become nonzero. | The producer output and GPU-to-guest writeback work. Record 10551 was useful for proving selector/branch behavior but is not itself a nonzero geometry record; do not require it as the Emitter handoff. Trace address-filtered consumers of the two live ping-pong buffers and locate the actual Emitter input. | `artifacts/astro-bot/runs/20260717-072445-e250-known-nonzero-output-writeback/` and `artifacts/astro-bot/runs/20260717-072737-e250b-record10551-hex-readback/`. |
| E251 | Address-filtered every consumer of the live 1.5 MiB ping-pong pair. Large compute shader `0x555F4F500` is the Emitter handoff, and vertex shaders `0x5001BBC00`, `0x5002B3000`, and target ES `0x5002A9A00` also read the same nonzero state buffers. At title, their snapshots contain about 10-11K nonzero bytes. The Emitter's late `s8` descriptor resolves to stride 64, 262,144 records, and a 16 MiB address (`0x553F41DD0` or `0x554F41DD0`), matching the rotating geometry buffers isolated in E190. | The 1.5 MiB producer output reaches both Emitter and target ES; the handoff is not missing. Inspect the real 16 MiB `s8` Emitter outputs and exact 64-byte record 24736. | `artifacts/astro-bot/runs/20260717-073315-e251-pingpong-consumer-map/`. |
| E252-E252c | The initial generic 16 MiB diagnostic repeatedly distorted boot by hashing/copying every large binding, so it was replaced with a generic readback-only mode that skips upload probes and reads only requested post-fence dwords. The constant-cost run reached `title_controller_ship`; exact stride-64 geometry record 24736 at `0x553F41DD0 + 0x182800` changed from all zero to three stable nonzero words: dword 8 `0x3F7FFFEF`, dword 10 `0x3F800000`, and dword 12 `0x0000293B`. | Selector, ObjectUpdate, 1.5 MiB producer, GPU publication, Emitter, and rotating 16 MiB geometry are all working. Record 24736 is no longer the blocker. Validate target ES `0x5002A9A00` input and position exports next. | Inconclusive heavy probes: `artifacts/astro-bot/runs/20260717-073804-e252-emitter-geometry-record24736/` and `.../20260717-074512-e252b-emitter-record24736-readback-only/`; decisive slice: `artifacts/astro-bot/runs/20260717-075049-e252c-emitter-record24736-slice/`. |

## 2026-07-19 title checkpoint

- The Gen5 standard-64K, mode-9 BC7 mip chain for title texture
  `0x5DDB00000` places mip 0 at allocation offset `0x80000`. Resolving that
  layout produces one clean source wordmark instead of sampling the mip tail.
  Evidence: `artifacts/astro-bot/readbacks/title-bc7-mode9-mip-layout-fix/title-source-mip0.png`
  and `artifacts/astro-bot/runs/20260719-122639-title-bc7-mip-layout-long-draw-trace/attempt-01-frames/frame-082-t+00109.8s.png`.
- The live title now renders readable, animated **Sony Interactive Entertainment
  presents** text. ES `0x500779700` and PS `0x50077AC00` have a valid interface;
  PS controls `[0,2,0x423,3]` map to the intended vertex exports. The alternating
  blue/white groups are animation frames, not a remaining attribute-map fault.
- GPU execution is making ordered progress. A steady title flip contains seven
  graphics DCB submissions, about 2,100 PM4 packets, 31 draws, and 30-46
  dispatches. Guest Vulkan submissions are normally 1-10 ms and packet parsing
  accounts for only about 0.15 seconds of a roughly 1.4-second frame. Evidence:
  `artifacts/astro-bot/runs/20260719-144625-title-gpu-submission-profile/` and
  `artifacts/astro-bot/runs/20260719-145237-title-frame-packet-profile/`.
- The remaining throughput problem is CPU/thread-side: DrawThread performs
  hundreds of thousands of 1-microsecond `sceKernelWaitEqueue` polls per second,
  while graphics event producers remain healthy. Blocking those polls, changing
  sub-millisecond sleep behavior, FIFO mutex handoff, and preserving separate
  pending hardware event types either regressed title timing or left the same
  1.4-1.5 FPS wordmark. Do not repeat those variants. Evidence:
  `artifacts/astro-bot/runs/20260719-143926-title-equeue-drawthread-h2/`,
  `artifacts/astro-bot/runs/20260719-151635-title-fifo-mutex-handoff/`, and
  `artifacts/astro-bot/runs/20260719-152702-title-distinct-graphics-event-types/`.
- Live thread/register/memory diagnostics are headless artifacts only. The
  emulator graphics window remains visible; no diagnostic terminal windows are
  part of the launch flow.

## 2026-07-19 upstream-main threading A/B

- Upstream `main` through `90c72eb` is compatible with the PR 412 in-place
  thread model only when two mainline cooperative-scheduler semantics are not
  imported. With both adaptive self-lock idempotence and conditional
  `pthread_self` registration enabled, Astro never reaches `ps_logo` or VideoOut
  in 150 seconds, every captured frame is flat gray, and the guest spins through
  roughly 179 million `sceKernelWaitEqueue` imports. Evidence:
  `artifacts/astro-bot/runs/20260719-154455-title-upstream-main-90c72eb-inplace-adaptive/`.
- Restoring adaptive NORMAL/ADAPTIVE compatibility recursion alone removes the
  dead spin and reaches `ps_logo`, but only at about 92 seconds and not the title
  by the 105-second cutoff. Evidence:
  `artifacts/astro-bot/runs/20260719-154931-title-upstream-main-adaptive-recursion-ab/`.
- Restoring the original unconditional `pthread_self` scheduler registration as
  the second and only additional change recovers the checkpoint timing:
  `ps_logo` at about 43 seconds and `title_controller_ship` at 57.3 seconds. The
  title tail visibly renders the animated **Sony Interactive Entertainment
  presents** wordmark from about 70.7 through 81.1 seconds. Evidence:
  `artifacts/astro-bot/runs/20260719-155233-title-upstream-main-pthread-self-original-ab/`.
- Retain the upstream `pthread_yield` alias and all compatible memory, CPU,
  import, and runtime changes. Do not reapply direct cooperative waiter handoff,
  adaptive idempotence, or conditional `pthread_self` registration on top of
  PR 412's in-place execution model.

## 2026-07-19 live title-draw checkpoint

- The pre-merge target ES address `0x5002A9A00` is stale in the merged build.
  A lightweight `SHARPEMU_TRACE_TITLE_DRAW=1` probe now reports only unique
  title-candidate shader pairs and does not require LLDB or a diagnostic
  terminal. The current title draw is ES `0x500780B00` / PS `0x500781D00`,
  sequence 3281, primitive type 4, 24,192 indexed vertices, rendering to the
  two 1920x1080 floating-point title targets. Evidence:
  `artifacts/astro-bot/runs/20260719-162210-title-headless-filtered-shader-discovery/`.
- The 180-second visual timeline confirms the current checkpoint directly:
  readable **Sony Interactive Entertainment presents** text begins around 72
  seconds, but it flickers, duplicates, and never advances to the Astro title.
  This is a real rendered-frame result, not a log-only milestone or harness
  inference.
- Address-targeted tracing of ES `0x500780B00` identifies it as a merged NGG
  primitive-generation/passthrough shader (`stages=0x02002000`, `primgen=1`,
  `passthru=1`). Its supporting global buffers are populated, but the ordinary
  vertex-input fallback exposes malformed location-0 position samples
  (`~ -1.7e38` and NaN). The existing NGG compute/raster lowering is selected
  only for primitive type 1, at most 64 vertices, one instance, and an
  `SBarrier`; this title draw is primitive type 4 with 24,192 indexed vertices.
  The next graphics boundary is therefore large indexed NGG workgroup/index
  decomposition, not another equeue timing or harness change. Evidence:
  `artifacts/astro-bot/runs/20260719-162838-title-es500780b00-record24736-headless/`.
- Treat the NGG selection mismatch as the leading hypothesis, not yet a proven
  fix. A change counts only when the captured graphics advance beyond the SIE
  wordmark without regressing the title-level milestone.

## Corrected conclusions: do not repeat

- A zero selector snapshot before the exact title-start marker is expected early
  state, not proof that the live menu selector producer is broken.
- The selector clear at `0x800253858` is normal setup. Do not bypass it.
- E206 supersedes the live-menu interpretation of E194/E196: the refill and gate
  paths do execute once scene lists become active.
- Depth, raster coverage controls, pixel exports, final composition, and the
  blue/striped format issue are downstream. Do not reopen them until vertex
  record 24736 is populated.
- The guest-zero ray/BVH dispatches are not proven title producers. Do not force
  their workgroup counts or fake ray intersections without an address link.
- Do not set `SHARPEMU_DISABLE_IMPORT_LOOP_GUARD=1`.
- Screenshot cadence/classification is not the current bottleneck. Use no-screen
  probes while following the producer chain.
- `--no-screenshot` disables only capture; it does not make SharpEmu headless.
- On macOS, if `GLFW Vulkan loader wired` is not followed by `GLFW windowing
  platform in use: Cocoa` within roughly 10 seconds, stop the run as a Cocoa
  bootstrap failure instead of waiting for a guest milestone.
- PS inputs are not identity-mapped. For the corrected intro shader, attribute
  sources are `0,2,3`, and source 3 is the custom packed-normal payload.
- Never declare two Vulkan fragment inputs at the same host location when guest
  controls alias one vertex parameter. Keep pixel locations unique and duplicate
  the source value in the vertex stage.
- Do not force or debug the first CS `0x50740A700` output block at
  `0x234..0x9D4`: E224 proves the live branch takes the alternate path.
- A fixed diagnostic write from every compute invocation creates extreme
  contention and invalidates timing. Restrict fixed-address probes to one
  invocation or give every invocation a unique address.
- Do not add a forced live upload for `s24`: E240 proves the queued snapshot and
  live guest memory agree when the producer fills, and the allocation shadow
  matches on the following dispatch after the normal refresh.
- Do not infer a zero runtime descriptor or zero stride at PC `0x38`: E241
  captures `s24=0x00BD4300` and `s25=0x00080004`, which is base
  `0x400BD4300` with an 8-byte stride.

## Current producer chain

```text
scene lists               repaired playable build: 28 active records
  -> 96 KiB selectors     653 nonzero bytes/table
  -> indexed state table  40 selected records; live fields begin at record 10615
  -> Vulkan selector view snapshot/shadow/live match after normal refresh
  -> CS 0x50740A700       selector + target loads and main output path proven
  -> paired 1.5 MiB data  ping-pongs input/output; verify record 10551 writeback
  -> large Emitter CS     exact-size decode, dispatch, and live input proven
  -> rotating 16 MiB geometry / record 24736 becomes nonzero at title
  -> merged-build title draw ES 0x500780B00 / PS 0x500781D00
  -> large indexed NGG primitive path (type 4, 24,192 indices)
  -> final composition and blue/striped color handling
```

## Next experiment

1. Keep the verified PR 412 synchronization semantics and harness timing fixed.
   Diagnostics remain headless artifacts; do not reopen an LLDB/register
   terminal or retune the 1-microsecond equeue poll.
2. Validate how the guest partitions ES `0x500780B00`'s primitive-type-4,
   24,192-index draw into NGG workgroups, then extend the compute/raster output
   layout and dispatch counts only from that evidence. Do not force the existing
   one-group, 64-lane lowering across the entire draw.
3. Capture the resulting position exports and a visual title tail. Accept the
   change only if frames advance beyond the animated SIE wordmark while the
   title-level milestone remains near the current 57-63 second range.

## Validation and artifact policy

- Current 2026-07-19 upstream-main checkpoint: Release `osx-x64` publish and
  visible Astro launch pass; 25 focused AGC tiling/event-queue/pthread tests
  pass. The verified run reaches `title_controller_ship` at 57.3 seconds and
  visibly renders the animated SIE wordmark.
- Current solution run: shader 53/53, Metal shader 27/27, and source-generator
  33/33 pass. Library tests are 806/813; the seven SaveData failures reproduce
  unchanged at clean checkpoint `7002dd2` and are outside this GPU/threading
  change set.
- Release `osx-x64` publish passed after the `par274/main` merge with zero
  warning or error lines.
- Library tests: 271/271 passed in the Windows-parity worktree.
- Shader tests: 30/30 passed (including the wave-mask branch tests).
- Source-generator tests: 33/33 passed.
- Harness tests: 6/6 passed.
- No SharpEmu process remained after E233.
- Retain the PR #216 baseline at
  `artifacts/astro-bot/baselines/pr216/attempt-01-contact-sheet.png`.
- Retain compact proof for E190, E193, E194, E196, E198-E201, E203, E204, and
  E206. Raw per-frame PNG directories and superseded E191/E195/E202/E203b/E205
  runs were pruned after their conclusions were recorded.
- The current `runs/` tree fell from about 2.15 GB to 33 MB. No source, baseline,
  compact sheet, decisive log, or manifest was removed.

## Acceptance criteria

- Release builds with no errors and focused tests pass.
- Original shaders show the ordered intro and reach exact `title_controller_ship`.
- Recognizable menu geometry renders without title-specific bypasses.
- The menu remains stable for at least 60 seconds.
- Keyboard or controller input operates the menu.
- MRTs and the final image are neither uniformly black/red nor blue/striped.
