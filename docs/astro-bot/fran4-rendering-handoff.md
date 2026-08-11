# ASTRO BOT Fran4 rendering handoff

Last updated: 2026-08-11

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
- The pre-PR-770 safety point is retained as branch
  `backup/pre-fran4-pr770`.
- Fran4 identifies itself as `0.0.3-fran4`.

Do not discard the Acelogic history or replace `main` with upstream. Future
upstream updates should be merges with explicit conflict review, followed by an
ASTRO title run and the complete test suite.

## What currently works

- The emulator creates and presents frames. The remaining black/uniform output
  is not explained by a missing swapchain present.
- The PS Studios movie is decoded and the game advances beyond it.
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

## Leads already rejected or unsafe

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
