# GTA V rho AGC and AMPR provider registrations

This batch adds 190 fail-closed, LLE-preferred Gen5 registrations after an
80-core Ghidra 12.1.2 campaign on `rho.cs.oswego.edu` and an independent local
integration audit.

| Provider library | Queue targets | Generated LLE registrations | Analyzed provider SHA-256 |
| --- | ---: | ---: | --- |
| `libSceAgc` | 119 | 119 | `110df81f759ae3dffcc9b5e3fa062c74058518631847641b8e08a54f6b8b6e2d` |
| `libSceAgcDriver` | 26 | 25 | `bc2ca28f3632ce69e25ab44991ed1f49bc1624fe39c2fc81f2efc6e705876348` |
| `libSceAmpr` | 46 | 46 | `129a972cdb762d1b362ad136cdb802f5cac7d3a5f48ecb8b7e5d695c70af4047` |

`MM4IZSEYytQ` is the only catalog exclusion. It already has a Ghidra-backed
semantic implementation of `sceAgcDriverSetHsOffchipParam`; that existing
registration is marked `PreferLle` so the firmware provider remains
authoritative and the semantic HLE implementation is its fallback. There is no
duplicate registration.

All 191 audited targets had exactly one primary Ghidra `Function`, matching
symbol/function entries, a positive function body, a completed decompilation,
a non-external definition, a successful headless exit, and a re-hashed per-NID
log. Six catalog-unnamed `libSceAgc` targets retain their exact qualified Ghidra
symbols. The audit found no other current registration collision. Ghidra was
the only reverse-engineering source; no Kyty or other emulator source was used.

The campaign ran 40 independent two-core Ghidra jobs. Observed utilization was
80.35 CPU cores with 19.19 GiB peak live RSS, no swap, and no I/O wait. Two
independent cleanup checks reported zero residual campaign directories and
zero residual Java processes on rho.

Machine-readable evidence:

- `provider-evidence/rho-agc-provider-inventory.csv` — full 191-row inventory,
  SHA-256 `890033c1b53293ba505f9f4782c33f9f2c34eedd4fece1ab560a944a90d95cd3`.
- `provider-evidence/rho-agc-provider-inventory.json` — provider/campaign
  metadata, SHA-256 `61f49e429719b5bd962d897ca4e3440316f80a13098fce0b3902920258dfb600`.
- `provider-evidence/libSceAgc-rho-ghidra.json` — selected 119-function packet,
  SHA-256 `dbee03cecf184cc987aa5176959844642580b786ea395810aa94ffe6399bee99`.
- `provider-evidence/libSceAgcDriver-rho-ghidra.json` — selected 25-function
  packet, SHA-256 `72b0ab5910fb1bc25fed02015c4734d8b79b3b1e17b81d4adcebd9e088d2d310`.
- `provider-evidence/libSceAmpr-rho-ghidra.json` — selected 46-function packet,
  SHA-256 `aac85e3a77fde54179eb6a49e6c375e9a6ddb55027941e8056d1361975ea272b`.

The selected packets are reproducibly derived with
`AcelogicFile/scripts/convert-ghidra-provider-inventory.py`, and the registrations are
reproducibly emitted with `AcelogicFile/scripts/generate-lle-nid-registrations.py`.
