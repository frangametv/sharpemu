# GTA V Gen5 final parity validation — 2026-07-18

## Scope

The current static validation covers exact registration parity for the pinned GTA V Gen5 inventory: 1,432 unique registrations (1,426 function imports and 6 object imports). It does not claim complete semantic parity or GTA V playability.

The statically validated implementation commit is `d3c90e3686a61817f3f67193664a8dcad50308ff`. The pinned inventory SHA-256 is `efb0a69b0e5e32274db2ca86558041318e9ba65011c0d94f3362629bf826f73a`. The retained full runtime trace below is historical evidence from pre-rebase commit `4ea43616102ba8b2a5bf59b745cd3b758d05e110`; the separate ten-minute Windows run remains pinned to ancestor `b591baa1aab949e63c48d790b067d5beeb47b091` and compares that build with the rebased `0996dab` checkpoint.

## Build and test gates

| Gate | Result |
|---|---:|
| Focused GTA parity, libc35, kernel contract, and data-symbol tests | 52/52 passed |
| `SharpEmu.Libs.Tests` | 1068/1068 passed |
| `SharpEmu.SourceGenerators.Tests` | 36/36 passed |
| `SharpEmu.ShaderCompiler.Tests` | 35/35 passed |
| `SharpEmu.ShaderCompiler.Metal.Tests` | 27/27 passed |
| Combined test suites | 1166/1166 passed |
| win-x64 self-contained Release publish | succeeded, 65 warnings, 0 errors |

The exact commands and counts are recorded in `artifacts/gta-v-nid-evidence/final-parity-validation-20260718.json` and are checked by `scripts/update-gta-v-final-parity-tracker.py`.

The validated Windows artifact was produced with:

```sh
dotnet publish src/SharpEmu.CLI/SharpEmu.CLI.csproj -c Release -r win-x64 --self-contained true --no-restore --nologo -o /tmp/sharpemu-gta-d3c90e3.DJZwsn
```

`SharpEmu.exe` SHA-256: `61db9b23ffb61c8889cf84b30385f8f038e7e4a6ecd8530c04b0daacab87cb03`.

## Current Windows runtime comparison

At `b591baa1aab949e63c48d790b067d5beeb47b091`, a short targeted trace showed both `[RAGE] RenderThread` and `[RAGE] Main Thread` repeatedly acquiring and releasing mutex `0x00000008045755C8`. A subsequent unfiltered ten-minute run remained alive across 61 snapshots: Main advanced by 45,923,560 imports and RenderThread by 4,588,828 imports. This disproves a permanent target-mutex deadlock, but the normalized rates did not improve over `0996dab`.

The first 64 traced flips were enqueued and presented one-for-one. Stable frames remained byte-identical at SHA-256 `83e9d1f12db5a8aec7088b6b5b469cf2a6bf6ca9ac80ff61d4e89e06e3b82267`, showing the same GTA V logo with green spinner. This is not a menu, North Yankton, or playability claim.

## Historical runtime command

The x86-64 CLI was published with:

```sh
dotnet publish src/SharpEmu.CLI/SharpEmu.CLI.csproj -c Release -r osx-x64 --nologo
```

The retained GTA V trace was collected under Rosetta with full import tracing at pre-rebase commit `4ea43616102ba8b2a5bf59b745cd3b758d05e110`:

```sh
set +e
SHARPEMU_LOG_ALL_IMPORTS=1 SHARPEMU_LOG_IMPORTS=1 \
  gtimeout -s TERM 45 \
  arch -x86_64 artifacts/publish/SharpEmu.CLI/Release/net10.0/osx-x64/SharpEmu \
  --cpu-engine=native --log-level=info \
  "/Volumes/Untitled/games/sharpemu/Games/GTA V/eboot.bin" \
  > artifacts/gta-v-final-parity-x64.log 2>&1
rc=$?
set -e
echo "exit=$rc"
```

The process reached its terminal fault naturally before the timeout. The exit status was 139.

## Historical trace results

| Check | Result |
|---|---:|
| Final libc provider routes | 34/34 direct bridges |
| Final data objects incorrectly routed as callables | 0 events |
| Imported data relocations | 11 rebound, 0 unresolved |
| Highest import ordinal | 41,427 |
| `MM4IZSEYytQ` checkpoint | reached at import 39,003 |
| Terminal signal | SIGSEGV (11) |
| Terminal RIP | `0x0000000805C273B7` |
| Fault address / access | `0x0000000000000000`, read |
| Terminal thread | `[RAGE] RenderThread` |
| Faulting guest thread's last import | `enqPGLfmVNU` (`strtok_r`) |

The terminal fault is the same later-state fault observed before the final parity wave; the registration work did not regress the prior 41,427-import checkpoint.

## Durable historical evidence

The tracked compressed trace is `artifacts/gta-v-nid-evidence/final-parity-runtime-20260718/gta-v-final-parity-x64.log.gz`.

- Compressed SHA-256: `68848eeaeb458489144abdb73c66a566f22fd3b40b57ad10f4e51c3a68012a1b`
- Raw SHA-256: `585ff7f6635ce07830b2078a46aa6e5cebdd8ddb1f83a0880b9fc40bf0b564f8`
- Raw size: 22,997,934 bytes
- Raw lines: 127,576

The final tracker decompresses this evidence and recomputes the provider-route set, object-callable count, data relocation totals, maximum import ordinal, MM4 checkpoint, and terminal signal tuple before it can close the 67-NID queue.

The recorded test/build counts validate `d3c90e3686a61817f3f67193664a8dcad50308ff` and are rerunnable validation records, not retained raw command transcripts. The historical runtime routing, relocation, checkpoint, thread, terminal-signal claims, and shell exit status belong to `4ea43616102ba8b2a5bf59b745cd3b758d05e110` and are independently recomputed from the tracked compressed trace. The current Windows comparison is limited to the ten-minute checkpoint at ancestor `b591baa1aab949e63c48d790b067d5beeb47b091`; it is not represented as a rerun of the later polling-classification commit.

## Remaining semantic limits

Registration parity is complete, but semantic work remains. Eighteen kernel/POSIX registrations intentionally fail closed, `sceLibcInternalBacktraceForGame` is a fail-closed HLE contract, nonzero `recv`/`send` flags remain unsupported, and 34 libc registrations depend on their Ghidra-identified firmware providers with fail-closed fallback behavior. These limits are tracked explicitly rather than represented as complete implementations.
