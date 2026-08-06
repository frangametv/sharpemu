# `sceSaveDataTransferringMount` evidence

## Identity

- NID: `WAzWTZm1H+I`
- Library: `libSceSaveData`
- Generations: Gen4 and Gen5
- Runtime observation: ASTRO BOT called the import once from return address
  `0x0000000801082EE4` with the request in `rdi` and result in `rsi`.
- Name validation: SharpEmu's `Ps5Nid.Compute("sceSaveDataTransferringMount")`
  produces `WAzWTZm1H+I`.

## Firmware evidence

- Binary: decrypted PS5 `libSceSaveData.sprx`
- SHA-256: `237d302887e67818f179d299158413ac9eb4c1ec71b36767afd5141a52170386`
- KawaiiDRA project: `sharpemu-savedata`
- Export address: `0x33540`
- Firmware operation dispatched by the wrapper: `0x3D`

The wrapper establishes this ABI:

```text
int sceSaveDataTransferringMount(
    const OrbisSaveDataTransferringMount *request,
    OrbisSaveDataMountResult *result);

request +0x00  int32 user_id
request +0x08  OrbisSaveDataTitleId *title_id
request +0x10  OrbisSaveDataDirName *dir_name
request +0x18  OrbisSaveDataFingerprint *fingerprint
request +0x20  byte reserved[32] (must be zero)
```

The firmware checks initialization before the request pointer. A null request or
nonzero reserved byte returns `0x809F0000`. It converts a successful service
response to zero and forwards negative errors through the SaveData error mapper.

## HLE contract

This is an explicit-title, read-only mount used to inspect an existing save while
transferring data between titles. It differs from a normal mount in three ways:

1. The title ID comes from the request rather than the running application.
2. A missing save returns `0x809F0008`; the call never creates the requested save.
3. A successful host mount is registered read-only and returns a standard 0x40-byte
   `OrbisSaveDataMountResult`.

SharpEmu stores saves as plaintext host files, so it records but does not dereference
the PFS fingerprint pointer. Read-only mount enforcement preserves the observable
guest contract without pretending to emulate proprietary PFS authentication.

Confidence: high for identity, signature, layout, validation order, and read-only
mount behavior; medium for service-internal error mapping beyond the errors covered
by focused tests.

## Validation

- Focused ABI/state tests: 7 passed.
- Complete SharpEmu.Libs test set: 567 passed.
- Source-generator tests: 33 passed.
- Shader-compiler tests: 34 passed.
- Release `osx-x64` publish: passed with no warnings or errors.
- ASTRO BOT runtime:
  - observed request: user `268435456`, title `PPSA01325`, directory
    `sce_sdmemory`, non-null fingerprint;
  - returned `0x809F0008` because the source save did not exist;
  - unresolved imports after the call: zero;
  - evidence: `artifacts/astro-bot/runs/20260717-224009-savedata-transferring-mount-pass/`.
- Dreaming Sarah regression: reached `ON SAVEGAME MISSING`, remained stable, and
  reported zero unresolved imports and zero fatal markers;
  evidence: `artifacts/astro-bot/runs/20260717-224116-savedata-transferring-dreaming-sarah/`.
