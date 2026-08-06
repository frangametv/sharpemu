# GTA V libc-family Gen5 registrations

This change registers the 35 function imports selected from the finalized rho
Ghidra packet. The evidence basis is limited to Ghidra 12.1.2 analysis and
SharpEmu runtime-routing observations. Kyty and other emulator implementations
were not used as evidence.

## Disposition

The source packet contains 67 mutually exclusive rows:

- 34 functions are registered as Gen5 `PreferLle = true` exports with an
  explicit `ORBIS_GEN2_ERROR_NOT_IMPLEMENTED` fallback.
  - 30 use the loaded GTA V provider's logical `libc` library.
  - 4 use `libSceLibcInternal`.
- `EHsF2i9FXPM` / `sceLibcInternalBacktraceForGame` is registered once as a
  Gen5 `libSceLibcInternalExt` HLE export. It has no `PreferLle` flag and fails
  closed with `ORBIS_GEN2_ERROR_NOT_IMPLEMENTED`. Ghidra found bodies in the
  GTA V and firmware providers, but the runtime packet found no direct bridge,
  LLE redirect, or runtime symbol for this import.
- 5 data/object imports are excluded from function registration.
- 27 kernel/POSIX functions are excluded from this libc-provider change and
  remain in their semantic-contract queue.

Three catalog rows list both `libSceLibcInternal` and `libc`. They are
registered exactly once under `libc`, because the loaded GTA V libc provider
resolved them directly at runtime:

- `MELi-cKqWq0`
- `zlfEH8FmyUA`
- `zr094EQ39Ww`

This avoids duplicate NID attributes while preserving the provider that GTA V
actually loaded.

## Generated registration sources

- `src/SharpEmu.Libs/Lle/LibcProviderLleExports.cs`: 30 exact Gen5
  `PreferLle` registrations.
- `src/SharpEmu.Libs/Lle/LibcInternalProviderLleExports.cs`: 4 exact Gen5
  `PreferLle` registrations.
- `src/SharpEmu.Libs/LibcInternalBacktraceExports.cs`: 1 exact Gen5 HLE,
  fail-closed registration without `PreferLle`.

The two LLE files are reproducible from the compact queue with
`scripts/generate-lle-nid-registrations.py`. The evidence packet is reproduced
and validated with:

```text
python3 scripts/convert-rho-libc35-evidence.py <final-rho-packet> docs/gta-v/provider-evidence/libc35
```

The converter verifies the source manifest, the exact 67-row partition, all
Ghidra body/decompile hashes, runtime evidence for every `PreferLle` row, and
the single-library normalization for the three hybrid rows before writing any
derived packet.

## Durable evidence

The self-contained selected packet is under
`docs/gta-v/provider-evidence/libc35/`. It contains only compact selected
records, source hashes, the exact registration queue, provider selections, and
remote cleanup proofs. It does not contain the game, firmware images, Ghidra
projects, or the full runtime log.

Derived files:

- `selected-evidence.json`: SHA-256
  `378ad9594df7c83c0340a7e75b3995985422441354c918ecac4dd02f7ac25548`.
- `prefer-lle-registration-queue.csv`: SHA-256
  `864c0d2d8825d321571311a6ffca8e6a75b9537e8d301b59962e31b263836f9d`.
- `libc-provider-selected.json`: SHA-256
  `6133d61bfb78ee04cd2b583e603511f6266ff014eb5f107df8423e4acbd3fff5`.
- `libc-internal-provider-selected.json`: SHA-256
  `2916039f70d49fb1a0dcbf81da27878686c34a771e5d9c907d4564bcd56c3991`.
- `provider-coverage-summary.json`: SHA-256
  `9bc760c8cda928fd848d5dd93610cf95dca912f291d1ab9f7d9f404e3c82525e`.
- `rho-evidence.md`: SHA-256
  `70bce710805b582b134c923038ecf9a82329e24a7a2d4f955400ebcb32e87e0d`.
- `source-hash-manifest.txt`: SHA-256
  `d1f5871ba4d33a6d0e268f2da84ab42951ff92be6dad0f51645a5fce4fd36b42`.
- `rho-cleanup-proof.txt`: SHA-256
  `bcfcd26bd2cb185f7d4538075268ad333299f3105f3b1d7899d80af931b69448`.
- `rho-cleanup-proof-deep.txt`: SHA-256
  `66da95fde8c387f9d71ce1cb705c7301869b49c7959c2e6734b9ef9b815a1a87`.

Final source packet hashes checked by the converter:

- `consolidated-nid-evidence.json`:
  `fa845efcb3786257c57105f7c54d318d1f1b732a865ee30b37378fb79b4e33b3`.
- `prefer-lle-include-candidates.csv`:
  `1967c50f439cf71ea00c481bd86277a22251fc21988e41ff780d26b3c9f3c337`.
- `libc-non-lle-contract-queue.csv`:
  `f0add93f4100a99f556e6b871fdbc76bb4d20f606287e49f69b5da82251ad045`.
- `data-import-disposition.csv`:
  `6028eb14a42456f3aaedaf41d1c9a8c0d07ca47835e65e391acace3ded4cadf8`.
- `kernel-hle-contract-queue.csv`:
  `8b9d1d6b7e66afa6d431bc8df186fd557ef8b02c18906a5cd0686b2dd589b7ce`.
- `provider-coverage-summary.json`:
  `9bc760c8cda928fd848d5dd93610cf95dca912f291d1ab9f7d9f404e3c82525e`.
- `EVIDENCE.md`:
  `70bce710805b582b134c923038ecf9a82329e24a7a2d4f955400ebcb32e87e0d`.
- `cleanup-proof.txt`:
  `bcfcd26bd2cb185f7d4538075268ad333299f3105f3b1d7899d80af931b69448`.
- `cleanup-proof-deep.txt`:
  `66da95fde8c387f9d71ce1cb705c7301869b49c7959c2e6734b9ef9b815a1a87`.

The selected GTA V libc provider image has SHA-256
`309cb9031209eb9b838216994d2c39613fcd65ec1eae493c4b784b9dacdd06bb`.
The independently captured runtime log has SHA-256
`421c0627d2fa265a608c98bd54d280153f54f611ddd240c0bdd1166e00dc9b88`.
Both rho cleanup proofs report that the unique `/dev/shm` campaign roots and
their Ghidra/Java worker processes were removed after evidence transfer.

## Regression contract

`Libc35ExportsTests` pins all 35 NID/name/library tuples, Gen5-only registration,
the exact 34/1 LLE-versus-HLE split, the 32 exclusions, unique registrations,
direct LLE routing for the 34 provider rows, no direct routing for backtrace,
and fail-closed behavior for all three fallback handlers.
