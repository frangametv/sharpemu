# ASTRO BOT title/menu investigation

## Goal

Render a stable, interactive ASTRO BOT title/menu with the original guest
shaders. The canonical status journal is
[`../../ASTRO_BOT_PROGRESS.md`](../../ASTRO_BOT_PROGRESS.md); the compact
evidence ledger is [`experiments.md`](experiments.md).

## Current state

- The original-shader boot sequence visibly contains boot art, controller-symbol
  animation frames, and the PS Studios wordmark.
- The exact milestone `GAME: Level has started: title_controller_ship` passes
  reproducibly after the bounded host-buffer LRU fix.
- The live 96 KiB selector tables are populated and select active state records.
- AGC semantic mapping now routes the intro's pixel inputs from vertex outputs
  `0,2,3`; the custom packed output 3 renders coherent controller geometry and
  substantially better color.
- The visible title/worldmap is still uniform red rather than the real menu.
- The active unknown begins at compute shader `0x50740A700`'s complete output and
  continues through the large Emitter, rotating 16 MiB geometry buffers, record
  24736, and export shader `0x5002A9A00`.
- The F1 performance overlay remains enabled by default.
- Do not update an upstream PR without explicit approval.

## Current chain

```text
scene lists               live
  -> four 96 KiB selectors live
  -> indexed state gates   live
  -> ObjectUpdate CS       output unclassified
  -> paired 1.5 MiB buffers
  -> large Emitter CS      decoder/dispatch fixed; output unclassified
  -> 16 MiB geometry buffers / record 24736
  -> ES 0x5002A9A00
  -> final composition and blue/striped format handling
```

Do not return to depth, pixel exports, or final composition until record 24736
contains real position data.

## Next probe

Run without screenshots and wait for the exact live-title marker. First hash and
read back the complete paired 1.5 MiB ObjectUpdate outputs after their fences.
If they change, follow only address-filtered consumers into the large Emitter and
record 24736. If they stay zero despite active selectors and state gates, use
KawaiiDra to inspect the first active sample/store path, lane reachability, EXEC,
and writable binding.

The completed selector control was:

```sh
python3 scripts/astro-test.py test \
  --build never \
  --tag e206-live-selector-to-geometry \
  --timeout 200 --stability 20 --retries 2 \
  --no-screenshot --no-require-ps-studios \
  --env SHARPEMU_TRACE_GUEST_EXEC_ADDRS=0x800253C22,0x800253C2A,0x800253C42,0x800253C48 \
  --env SHARPEMU_TRACE_INDEXED_GLOBAL_BUFFER_SHADER_ADDRESS=0x50740A700 \
  --env SHARPEMU_TRACE_INDEXED_GLOBAL_BUFFER_SPEC=2,8,4,6,276,176,180,184,188 \
  --env SHARPEMU_TRACE_INDEXED_GLOBAL_BUFFER_INTERVAL=8 \
  --env SHARPEMU_TRACE_INDEXED_GLOBAL_BUFFER_CPU_WRITES=1
```

## Test workflow

Check the machine, game, and runtime paths:

```sh
python3 scripts/astro-test.py doctor
```

Run the exact title gate without capture overhead:

```sh
python3 scripts/astro-test.py test \
  --build never \
  --tag focused-probe \
  --no-screenshot \
  --no-require-ps-studios
```

Keep the emulator open for manual input testing:

```sh
python3 scripts/astro-test.py run --build never --tag manual-input
```

Use screenshots only when validating a visual change. The runner stores logs,
environment overrides, git state, milestones, and optional targeted window
captures under ignored `artifacts/astro-bot/` paths.

## Evidence rules

- `LevelDocument Loaded: title_controller_ship` is not a pass. Require
  `GAME: Level has started: title_controller_ship`.
- A pre-title zero buffer is not evidence about the live producer.
- Record exact flags, the observed boundary, conclusion, and artifact path for a
  meaningful experiment.
- Do not retain hundreds of raw frames after a conclusion is captured in a
  compact sheet and log.
- Preserve the PR #216 baseline at
  `artifacts/astro-bot/baselines/pr216/attempt-01-contact-sheet.png`.
- Close exact ASTRO SharpEmu processes between runs; do not broadly kill
  unrelated SharpEmu/shellcore processes.

## Acceptance criteria

- Release build and focused tests pass.
- Original shaders visibly play the ordered intro and reach exact title start.
- Recognizable title/menu graphics render without title-specific bypasses.
- The menu remains stable for at least 60 seconds.
- Keyboard or controller input operates the menu.
- MRTs and the final image are not uniformly black/red or blue/striped.
