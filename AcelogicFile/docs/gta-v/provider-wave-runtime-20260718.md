# GTA V provider-wave runtime validation (2026-07-18)

## Configuration

- Branch: `codex/gta-v-nids`
- CPU engine: native x64 (`arch -x86_64`)
- Import tracing: `SHARPEMU_LOG_ALL_IMPORTS=1`, `SHARPEMU_LOG_IMPORTS=1`
- Timeout: 45 seconds; the process instead exited with signal status 139 after about 8.6 seconds
- Local raw log: `artifacts/gta-v-after-provider-wave-x64.log` (ignored, not committed)
- Raw-log size: 23,027,882 bytes, 128,151 lines
- Raw-log SHA-256: `421c0627d2fa265a608c98bd54d280153f54f611ddd240c0bdd1166e00dc9b88`

## Results

- The loader installed 1,956 direct guest bridges covering 482 unique NIDs.
- All 436 newly registered `libSceNpCppWebApi` NIDs resolved to direct guest-provider bridges.
- Representative direct targets were `+6Xo+7GdUGM -> 0x8061A1890`,
  `PzLUwQXc7VM -> 0x8063AF970`, and `zy9ivTre1ko -> 0x8063BFF60`.
- The other 344 generated provider registrations did not resolve directly because their
  matching firmware providers were not loaded or mapped in this run.
- Thirteen of those registrations were called through their fail-closed fallback:
  twelve `libSceAgc` calls and one `libSceSystemService` call. None returned invented success.
- `MM4IZSEYytQ` (`sceAgcDriverSetHsOffchipParam`) was called at import ordinal 39,003
  with `(0, 0x1FF)`. Its semantic HLE implementation returned zero, and execution
  immediately continued beyond the former return gate at `0x8002957516`.
- The highest observed import ordinal was 41,427.

## Later fault

The run ultimately hit an unrecovered access violation on `RenderThread` at guest RIP
`0x805C273B7` while reading address zero. The last observed import on that thread was the
existing `strtok_r` registration (`enqPGLfmVNU`), which returned zero. The available trace
does not establish that the last import caused the fault, and the fault is not attributed
to any newly registered provider NID.

This run validates registration and routing behavior, not full GTA V playability. In
particular, static registration parity does not by itself load missing firmware providers
or reproduce their semantics.
