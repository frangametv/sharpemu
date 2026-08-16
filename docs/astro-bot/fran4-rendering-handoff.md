<!--
Copyright (C) 2026 SharpEmu Emulator Project
Copyright (C) 2026 FranGameTv
SPDX-License-Identifier: GPL-2.0-or-later
-->

# ASTRO BOT Fran4 rendering handoff

Last updated: 2026-08-16

## Purpose

This document records the current Fran4 rendering state, the evidence collected
on the Windows test machine, the change made in this iteration, and the next
bounded experiments. It is intended to let another developer or AI continue
without repeating the broad black-screen investigation.

The longer inherited Acelogic journal remains in
`AcelogicFile/ASTRO_BOT_PROGRESS.md`; the compact inherited rendering summary is
`AcelogicFile/docs/astro-bot/README.md`.

## Source state

- Working branch: `main` in `frangametv/SharpEmu`.
- Investigation base before this iteration: `1b733f3`.
- Upstream was merged through `sharpemu/sharpemu@7caf430` by `93c5df7`.
- The runtime changes from upstream PR #770 were deliberately reapplied by
  `f196999` after comparing the upstream and Fran runs.
- The former pre-PR-770 safety point remains recoverable as commit `93c5df7`;
  its temporary backup branch was removed before the Fran4 release.
- Fran4 identifies itself as `0.0.3-fran4`.

Do not discard the Acelogic history or replace `main` with upstream. Future
upstream updates should be merges with explicit conflict review, followed by an
ASTRO title run and the complete test suite.

## Foufouadi compatibility review (2026-08-16)

The local `sharpemu-foufouadi` checkout was fast-forward checked before the
review. It was already current at `1090098`; its last code-bearing revision is
`87689f9`, seven documentation/media commits behind the branch tip. Changes
were inspected commit by commit and rewritten against the newer Fran codebase;
the fork was not merged or copied wholesale.

Integrated compatibility work:

- safe page-by-page host-memory probing before diagnostic reads;
- `sceLibcMspaceMemalign`, `asctime`, and thread-scoped `strtok` compatibility;
- `sceVideoOutDeleteFlipEvent` and `sceVideoOutGetEventCount`;
- APR wait-all (`sceKernelAprWaitCommandBuffer(UINT32_MAX)`) and AJM
  initialization with real Gen5 configuration flags;
- TLS patch scanning that includes the main PS4/PS5 image even when execution
  starts in a bootstrap allocation, plus rescanning of lazily committed
  executable pages;
- complete unsigned 64-bit VOP3 compare decoding/emission;
- `DS_READ_ADDTID_B32`, including the hardware M0 low-16-bit address rule;
- `DS_READ2_B64`, `DS_SWIZZLE_B32`, `S_FLBIT_I32_B32`, `V_XOR3_B32`, and
  `V_XAD_U32`, plus the previously missing Metal path for `DS_PERMUTE_B32`;
- the complete Gen5 `libSceVideodec2` query/lifecycle/decode surface and an
  asynchronous, bounded, real-time-paced H.264 pipeline. The fork's
  Vulkan-only presenter call was replaced with Fran's backend-neutral GPU
  seam so the path is also valid for Metal;
- equivalent emission paths for Vulkan and Metal where applicable, plus
  focused regression tests.

The review deliberately excluded null/poison-pointer scratch redirection,
automatic recovery of writes through invalid imports, and the fork''s 512 MiB
fallback allocation arena. Those changes can hide the real producer failure,
silently corrupt guest state, or solve allocator pressure that has not been
observed in Fran. The fork''s EQ-event reinterpretation was also excluded
because the current provider-derived ABI is newer and better evidenced.
Title-specific HiZ/page-guard probes and poison-recovery-only disassembly tools
were not carried over: they do not change compatibility with their environment
variables disabled, depend on an intentionally excluded recovery path, and the
current tree already has newer address-filtered shader, buffer and live
disassembly diagnostics.

Several apparently new changes were already present in Fran/upstream, including
the vectorized guest-buffer writeback scan, fragmented-page fast path, AGC
arena/fence work, GuestDataPool lease fix, the AvPlayer/Bink host decode
pipeline, and most libc exports. The distinct `libSceVideodec2` path was not
present and is now implemented separately. VOP3P dot-product and
`V_PK_FMAC_F16` additions remain intentionally
deferred: the reviewed implementation only covered SPIR-V and did not preserve
the complete packed selector/modifier semantics required for a safe
cross-backend implementation.

These additions remove concrete unsupported shader and import paths that may
block the ASTRO menu. They do not by themselves prove that the missing first
half of geometry record 24736 is fixed; the focused live trace below remains
the required acceptance test.

Post-audit validation is green: 37 source-generator, 110 Vulkan/general shader,
33 Metal shader, and 1,680 library tests (1,860 total), with no failed or
skipped tests and no compiler warnings. No commit was created.

## What currently works

- The emulator creates and presents frames. The remaining black/uniform output
  is not explained by a missing swapchain present.
- The PS Studios movie is decoded and the game advances beyond it.
- The Fran4 `20260811-133753` run proved that the decoded PS Studios frame was
  valid but stranded in HLE fallback memory: no sampled-image descriptor or
  texture upload ever referenced its three 12,441,600-byte NV12 buffers.
- A compatibility presentation path now converts that fallback NV12 frame to
  BGRA and hands it directly to the Vulkan presenter. A live ASTRO run emitted
  `AvPlayer host fallback frame presented: serial=1 size=3840x2160`, and the
  frame was visibly confirmed on the host window.
- The follow-up `20260811-140236` run independently confirmed the same visible
  frame. Its preserved copy is
  `W:\SharpEmuLab\Log\AstroBot-Fran4-2026-08-11_14-02-first-frame.log`
  (SHA-256
  `96000472B1A9D22B40A6B761B230D4AFC3D37A966345D62748AAACFC1C7102D4`).
- The exact game milestone
  `GAME: Level has started: title_controller_ship` is reached.
- `worldmap` is subsequently loaded.
- The Acelogic selector/scene fixes are still active after the upstream merge
  and PR #770 reapplication.
- The focused selector trace for compute shader `0x50740A700`, with spec
  `2,8,4,6,276,176,180,184,188`, changes around occurrences 476-480 from one
  unique target to 40 unique targets. Offset `+176` has 15 active gates and
  offset `+188` has 12. This rejects the theory that Fran4 lost the selector
  population fix.
- Compute writes are visible in both the 1.5 MiB and 16 MiB stages. The failure
  is narrower than "all GPU writes are lost".

No current build, including the tested upstream build containing PR #770,
produces the correct interactive menu. Fran is still useful as the research
base because it retains the earlier Acelogic title/selector/format work, but the
claim that it visibly renders the menu better than upstream is not yet proven.

## Narrowed failing chain

The live chain is:

```text
scene lists and four 96 KiB selector tables
  -> ObjectUpdate compute shader 0x50740A700
  -> alternating 1.5 MiB buffers
       0x553C41DD0
       0x553DC1DD0
  -> large Emitter compute shader 0x555F4F500
  -> alternating 16 MiB geometry buffers
       0x553F41DD0
       0x554F41DD0
  -> geometry record 24736 (64-byte stride, offset 1,583,104)
  -> expected export shader 0x5002A9A00
  -> raster/final composition
```

Observed at the 16 MiB output boundary:

- One `0x554F41DD0` snapshot contained 199,184 nonzero bytes, starting at byte
  32 and ending at byte 1,593,457.
- Record 24736 in that buffer had a zero first 32-byte half and a nonzero second
  half:

  ```text
  0000000000000000000000000000000000000000000000000000000000000000
  EFFF7F3F000000000000803F000000007C290000000000000000000000000000
  ```

- The paired `0x553F41DD0` sample had a fully zero record 24736; the complete
  buffer had only 919 nonzero bytes and its last nonzero byte preceded the
  record.
- The expected export shader `0x5002A9A00` had no hits in the focused run.
  Other vertex/export shaders, including `0x5001BD400`, `0x5002B5000`, and
  `0x5002ABA00`, did consume the rotating geometry buffers.
- IR dumps show that both compute shaders decode and emit stores; no simple
  unsupported-opcode exit was found. Relevant dumps in the Fran4 runtime are:

  ```text
  shader-dumps/000000050740A700-913C97B1BA638A8D.cs.ir.txt
  shader-dumps/0000000555F4F500-0D0842CE15819576.cs.ir.txt
  ```

The best current hypothesis is therefore missing/incorrect position production
or selection for the exact live geometry record, followed by failure to reach
the expected export path. Final presentation, generic shader decoding, and
generic buffer writeback are lower-priority suspects.

## Change made in this iteration

### AvPlayer fallback presentation

The game-provided texture and generic allocation callbacks both return null for
the 3840x2160 PS Studios movie buffers. The decoder then correctly writes a
nonempty NV12 frame to HLE-managed guest memory, but that address is outside the
guest texture-binding path used by the title. This was the immediate reason the
decoded movie was invisible.

Fran4 now:

1. defers AvPlayer video-buffer allocation until the first video-data request,
   after `sceAvPlayerStart`, matching the reference AvPlayer lifetime more
   closely;
2. keeps the guest callback allocation path as the preferred path;
3. only when both guest allocators fail, converts the decoded NV12 frame to
   BGRA and exposes it to the Vulkan presenter;
4. gives Bink presentation priority, so the existing Bink bridge is unchanged.

The verified diagnostic log is:

```text
artifacts/diagnostics/fran4-avplayer/astrobot-fran4-video-test.log
```

This is a real visible-output improvement. The first implementation still
depended on repeated guest `sceAvPlayerGetVideoData*` calls, however. ASTRO
explicitly called `sceAvPlayerPause` after acquiring the first video frame, so
that version froze on the first displayed image.

The follow-up implementation makes the allocation-failure compatibility path
a bounded host playback:

1. the first guest frame is still returned exactly as before;
2. a separate FFmpeg decoder and the existing `MediaFramePlayback` queue decode
   BGRA frames away from the Vulkan presentation thread;
3. the presenter advances those frames on the movie clock even if the guest
   pauses the unusable guest-buffer path;
4. late frames are dropped rather than stretching an 8.5-second movie at the
   emulator's low frame rate;
5. at EOF the fallback removes itself and guest rendering regains presentation.

The Fran4 run `PPSA21567-20260811-163141.log` confirmed the complete host
movie visually and in the log. Presentation advanced through distinct serials
(`frame=1 serial=2` through at least `frame=4 serial=5`), then emitted
`host_fallback_finished` about 9.27 seconds after fallback startup and continued
to `title_controller_ship`. The preserved log is
`W:\SharpEmuLab\Log\AstroBot-Fran4-2026-08-11_16-31-full-video.log`
(SHA-256
`7CC6C9F382CDAC2FCB24E3A664D149588D863763F3FC93943A361E5F81D374C7`).

The remaining movie issue is throughput, not correctness or lifetime. The
first visible host frames were presented about 0.6, 0.6, and 1.1 seconds apart,
so the 59.94-fps 3840x2160 source appears at roughly one frame per second while
still completing on its wall-clock time base. Future work should preserve this
known-good commit and isolate presentation cadence and 4K decode/BGRA upload
cost. This improvement does not claim to solve the independent title/menu
geometry-production chain.

Two local validation launches on 2026-08-11 reached Vulkan initialization and
early title submissions, but both terminated in the pre-existing native CPU
backend failure `attempted to call a UnmanagedCallersOnly method from managed
code` before the AvPlayer sequence began. They therefore neither confirm nor
invalidate this path. Their logs are retained under
`artifacts/diagnostics/fran4-avplayer/astrobot-fran4-multiframe-*.log`.

### Geometry diagnostics

The address-filtered global-buffer diagnostic previously had two problems:

1. Compute submissions did not emit the complete post-fence buffer trace, even
   though compute is the critical producer in the chain above.
2. The graphics-only path forced all submissions visible and could retire and
   destroy the resources before reading them. It also forced an early guest
   writeback, changing diagnostic timing.

Fran4 now traces both compute and graphics writable global buffers in one safe
place: after their submission fence signals and before their resources are
destroyed. Normal execution is unchanged unless
`SHARPEMU_TRACE_GLOBAL_BUFFER_ADDRS` is configured. The trace also includes up
to 64 bytes beginning at each exact requested interior address, rather than
only a whole-buffer summary and the first changed dwords.

The new output field is:

```text
address_samples=[0x<guest-address>+<buffer-offset>:<hex-bytes>]
```

This is diagnostic infrastructure, not a claimed visual fix. It makes the next
producer/consumer comparison reliable and repeatable without introducing an
extra GPU wait.

## Focused reproduction

Use the same ASTRO launch arguments as the normal Fran4 run and set:

```powershell
$env:SHARPEMU_TRACE_GLOBAL_BUFFER_ADDRS = `
  '0x553C41DD0,0x553DC1DD0,0x5540C45D0,0x5550C45D0'
$env:SHARPEMU_TRACE_VERTEX_SHADER_ADDRESS = '0x5002A9A00'
$env:SHARPEMU_TRACE_VERTEX_BUFFER_RECORD = '24736'
$env:SHARPEMU_TRACE_VERTEX_BUFFER_RECORD_SUMMARY = '1'
```

The last two addresses are exact record-24736 addresses inside the two 16 MiB
buffers. With this iteration, their `address_samples` should appear on compute
shader `0x555F4F500` at fence retirement. Keep the run alive through the exact
`title_controller_ship` start marker; a pre-title zero sample is not evidence
about the live producer.

For selector correlation, use:

```powershell
$env:SHARPEMU_TRACE_INDEXED_GLOBAL_BUFFER_SHADER_ADDRESS = '0x50740A700'
$env:SHARPEMU_TRACE_INDEXED_GLOBAL_BUFFER_SPEC = `
  '2,8,4,6,276,176,180,184,188'
$env:SHARPEMU_TRACE_INDEXED_GLOBAL_BUFFER_INTERVAL = '8'
$env:SHARPEMU_TRACE_INDEXED_GLOBAL_BUFFER_CPU_WRITES = '1'
```

Useful captured logs on the investigation machine:

```text
W:\SharpEmuLab\Log\AstroBot-Fran4-geometry-to512.stderr.log
W:\SharpEmuLab\Log\AstroBot-Fran4-output-boundary.stderr.log
W:\SharpEmuLab\Log\AstroBot-Main-2026-08-11_09-37.log
```

## Next experiments

1. Run the new exact-address trace and group samples by shader, buffer pair, and
   occurrence after the title marker.
2. Determine whether record 24736 is already missing in both 1.5 MiB inputs or
   is lost inside `0x555F4F500`. Compare the same occurrence, not arbitrary
   snapshots from alternating buffers.
3. If the inputs are valid but the first 32 bytes of the output record remain
   zero, trace the stores reaching record offset 1,583,104 in
   `0x555F4F500`. Inspect lane/EXEC reachability and the source values around the
   store PCs visible in its IR (`0x1AEC`, `0x1B08`, `0x1B2C`, `0x1B4C`, and the
   later `0x38D4`/`0x5A54` region).
4. If a valid record exists, trace why `0x5002A9A00` is not selected. Compare
   the shader program register pair and geometry-buffer binding with the
   shaders that do consume the buffer.
5. Only after real positions reach the export shader should investigation move
   back to viewport, depth, pixel exports, blue/striped formats, or final
   composition.

## External-client compatibility follow-up (2026-08-13)

Three follow-up logs in `NUOVI_LOG` separated two independent failures:

- The Steam Deck run no longer reports the earlier
  `sceKernelMapNamedFlexibleMemory` memory faults, confirming that compat
  reads/writes for native/libc-backed pointers are active automatically. Its
  later opening crash was preceded instead by ten Json2 fail-closed calls and
  three unresolved Json2 imports: `JP-PtKMiI1E`, `dFCphqnd+a4`, and
  `iZeYfOxtMRg`.
- Both Radeon RX 7900 XT Windows runs print the automatic no-optimization
  workaround message, yet still fault inside AMD's compiler during
  `vkCreateComputePipelines`. No launch argument was missing; the first AMD
  workaround was active but insufficient.

The current worktree therefore adds stateful Json2 fallback semantics for
Object, Array, String, and Value operations, including the three exact missing
NIDs. On AMD/Windows only, native compute subgroup lowering is now disabled by
default in favor of the existing compatibility lowering; NVIDIA and AMD/Linux
remain unchanged. `SHARPEMU_VK_AMD_COMPUTE_SUBGROUPS=1` is an explicit opt-out
for comparison runs.

Every compute module also receives a managed header/instruction/entry-point/
local-size preflight before it reaches Vulkan. On AMD/Windows, every unique
pipeline candidate is automatically dumped under
`shader-dumps/amd-compute`, and the immediately preceding
`vk.amd_compute_pipeline_candidate` log contains its guest shader address,
digest, size, and path. This needs a fresh RX 7900 XT run to confirm whether
compat subgroup lowering removes the native driver crash; if it does not, the
last automatic dump identifies the exact remaining compiler input without
requiring command-line switches.

## AvPlayer worktree review (2026-08-13, pending live validation)

The xnetcat worktree branch a3204ad2268ce5080 was reviewed as a source of
ideas only. No commit was imported. The useful PS5 AvPlayer behavior was
rewritten around Fran's existing allocator fallback and asynchronous
MediaFramePlayback path:

- Gen5 init reads autoStart from the PS5 offsets (112/168) while Gen4 retains
  108/164.
- legacy stream descriptors remain 32 bytes on Gen5 and 40 bytes on Gen4;
  sceAvPlayerGetStreamInfoEx now writes the actual 104-byte Gen5 descriptor,
  including frame rate at 0x40 and duration at 0x60.
- stream types use the Gen5 numbering and video-only media no longer advertises
  or attempts to decode a nonexistent audio stream.
- Gen5 extended frame metadata now carries its 256-byte pitch, crop-right value,
  bit depths and frame rate consistently. A paused Gen5 player can return the
  last valid frame descriptor without decoding another frame.
- the host fallback decoder is capped to the configured output resolution
  instead of decoding a 3840x2160 movie into a 3840x2160 BGRA surface for a
  smaller window.
- decoded width and height now travel with the fallback pixel buffer and are
  validated against its byte length before Vulkan sees it. This is the missing
  safety condition that caused the reverted 0924a9a experiment to crash.
- the synchronous NV12-to-BGRA conversion is retained only for the first poster
  frame. Later presentation uses the bounded background decoder instead of
  repeating a full 4K CPU conversion on the emulation thread.

The standalone presentation-cadence part of 0924a9a was deliberately not
restored in the same change. The xnetcat queued-event/pthread-yield mechanism
was also deferred because it changes guest callback timing independently of
video throughput.

Release tests pass locally: 38/38 AvPlayer tests and the complete solution
suite (37 source-generator, 85 shader, 28 Metal shader, and 1647 library
tests). A real Astro Bot run must now confirm:

1. host_fallback_started reports source=3840x2160 and an output no larger than
   the configured host resolution.
2. No 0xC0000005 follows the first scaled frame.
3. Presentation cadence improves and playback still reaches
   host_fallback_finished followed by title_controller_ship.

## Leads already rejected or unsafe

### Rejected performance experiment (2026-08-11)

Commit `0924a9a` attempted to decouple host fallback presentation from guest
flip cadence and to cap FFmpeg decode output to the configured host resolution.
The user run in
`AstroBot-Fran4-2026-08-11_16-44-performance-regression.log` is a confirmed
regression. The fallback opened a 3840x2160 source as 1280x720 at
16:44:54.865. `TryGetFallbackPresentationFrame`, however, still advertised the
guest source dimensions (3840x2160) for that smaller pixel buffer. About 80 ms
later multiple WebApi workers entered a `0xC0000005` access-violation cascade;
the fallback never reached `host_fallback_finished` and the process exited with
code `-2146233082`.

The experiment was reverted by `9e95339`. Fran4 was restored byte-for-byte to
the known-good `632e727` executable (SHA-256
`746E5001242EB82264A5CBD429DDC46C0EA164F70E3F6FCA0536507F5D2A55CB`). Do not
reintroduce standalone fallback pacing together with decode downscaling. Any
future optimization must be split into independently testable changes, must
carry the decoded buffer's actual width and height with the frame, and must be
packaged separately from the known-good Fran4 build until an ASTRO run
completes the video and reaches `title_controller_ship`.

- Do not treat the black screen alone as a swapchain/present failure; present
  is active and repeated nearly-black patterned frames have been captured.
- Do not disable broad compute ranges or force fullscreen/solid shaders as a
  permanent fix. Those switches are useful only for classification.
- Do not invent BVH/ray-intersection output. The current blocker is in the
  observed geometry-production chain.
- Do not infer failure from an early zero snapshot. Alternate buffers and title
  timing matter.
- Do not delete the `Fran4` runtime directory when publishing: it contains
  user data and shader evidence. Overlay only the newly published executable
  files.

## Completion criteria

- Original shaders produce recognizable title/menu geometry.
- The output is stable for at least 60 seconds after exact title start.
- Keyboard or controller input operates the menu.
- The focused traces show real position data in record 24736 and the expected
  export/raster path consumes it.
- Release build and the complete solution test suite pass.
