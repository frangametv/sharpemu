# GTA V `sceAgcDriverSetHsOffchipParam`

## Evidence

- NID: `MM4IZSEYytQ`
- Library/generation: `libSceAgcDriver`, Gen5
- Provider: `libSceAgcDriver.sprx`
- Provider SHA-256: `bc2ca28f3632ce69e25ab44991ed1f49bc1624fe39c2fc81f2efc6e705876348`
- Ghidra export path: `0x70B0` -> selected callback `0x6FC0` -> helper `0x9D00`
- Evidence packet SHA-256: `d605f6ee4ddf901fc619d6396970d267ab247fc51849a571868fef70c8ac4520`
- Contract JSON SHA-256: `8ec69a480cd5a62e37b539acb67257b0c2442fffc68ff1fb4f887a926fffa3e4`

The recovered ABI is `int32(uint32 first, uint32 second)`. Both inputs are
reduced to their low 16 bits. The four-byte driver payload stores `second` at
offset 0 and `first` at offset 2. A zero driver result maps to zero; every
nonzero driver result maps to `0x8A6DFFFF`. The call does not read or write
guest memory.

GTA V calls the export as `(0, 0x1FF)` immediately after a successful
`sceAgcDriverSetTFRing` call and traps if the return value is nonzero.

## SharpEmu contract

SharpEmu uses the existing per-memory submitted AGC state created by the setup
sequence. Under its existing lock, the effective pair is packed in firmware
payload order and committed as one state transition. If submitted state is not
available, the call returns `0x8A6DFFFF` without creating or changing state.

## Branch validation

- Focused Hs-offchip tests: 6 passed
- Full `SharpEmu.Libs.Tests`: 745 passed
- `SharpEmu.SourceGenerators.Tests`: 33 passed
- `SharpEmu.ShaderCompiler.Tests`: 34 passed
- Release solution build: 0 warnings, 0 errors

The tests cover exact registration, GTA's success path, no guest-memory writes,
both-input truncation, payload order, missing-state failure, replacement and
process-state isolation, and concurrent readers/writers without torn pairs.
