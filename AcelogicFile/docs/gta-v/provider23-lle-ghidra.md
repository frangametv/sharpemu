# GTA V remaining small-provider registrations

This batch registers the remaining 23 GTA V small-provider imports found in
the pinned Gen5 inventory. Ghidra 12.1.2 is the only reverse-engineering
source. Kyty and other emulator implementations were not used as identity,
contract, or behavior evidence.

The Windows campaign produced the canonical selected-function packets. A
separate Mac campaign reconstructed and analyzed the same 14 provider images
and independently matched all 23 NIDs on provider SHA-256, function entry,
function-body SHA-256, catalog symbol, provider filename, and logical import
library. Each comparison category passed 23/23 with no mismatch.

These registrations do not claim an HLE implementation of provider behavior.
They are Gen5-only and `PreferLle`, so the loaded guest provider is
authoritative. If the provider is unavailable, the explicit HLE fallback
returns `ORBIS_GEN2_ERROR_NOT_IMPLEMENTED` and writes that result to `RAX`.

## Exact provider set

| Analyzed provider | Imported library | NIDs | Provider SHA-256 | Selected evidence SHA-256 |
| --- | --- | ---: | --- | --- |
| `libSceAjm.native.sprx` | `libSceAjm` | 2 | `4da65731b07fa2911b9468505b2f1fc0a56df7373356fdc1dfa886b00385d8d9` | `5b5f94f008c20115c3a12c886570f136bb0babfdcdf08ac58a44771d2278fc3b` |
| `libSceAppContent.sprx` | `libSceAppContent` | 1 | `16ea1a4db751772ca5f0bacb440a95a20eaa2cb8b75f4d6b5999da178d8594fc` | `1fb3b6db0ae23fbf665525297e52fe23bccf302908d64a379979f4222e5bd652` |
| `libSceAudioOut.sprx` | `libSceAudioOut` | 1 | `948dfdc30b9c974c5447d9078853beb1555a2e548de6093b05a114e99445ab33` | `bfda7756f65d52b8913bbe557f4b6f4d37125a4f25a0ed8ce9d51d54b8d00476` |
| `libSceAudioOut.sprx` | `libSceAudioOut2` | 4 | `948dfdc30b9c974c5447d9078853beb1555a2e548de6093b05a114e99445ab33` | `3eadbbb43c77f72f2364464fa5c31dddd8d618039d64b750aab4cdf4ebca1c38` |
| `libkernel.sprx` | `libSceCoredump` | 2 | `0d91281f1d2cdcf4d8c2f4b920766b645ea086e679bd95074f30510178a706b0` | `8e730976ff77eeb6e52d933bc744d85e036cce9bb732e33093500cd806305fa0` |
| `libSceHttp.sprx` | `libSceHttp` | 1 | `fdb6ddc34e9d8c566421511c693294ca93ab1636f797f8fc705588b678fb1f6a` | `efcb61de37de982d1435f749e35a29ea5e8db0e84a36730441ac6e1ec5494b85` |
| `libSceIme.sprx` | `libSceIme` | 1 | `a9fb46b1809ab3f849b82b98b23c273b28e4794a4380185c3dcc472f06ec446e` | `52c12623118ceda81d8b1a64790dd21e480bb7bb3dbfc9b577bedf20d96c40c5` |
| `libSceNpTrophy2.sprx` | `libSceNpTrophy2` | 1 | `fa81a4a54680504dbed9abe582c5a2b36d4279d4fd5d85e118018e1305c96106` | `f8cba62aecbcf7e1f47aa1e5ad509e2649b53cfab3b97a251a08b113d115dfbf` |
| `libScePad.sprx` | `libScePad` | 1 | `9396eb7947760db43eec22be66cc40f2a5e999be09645412cb92020b1e9dfe34` | `f512101f0fd9a3cdecb2383f2f9027a15a00f45d9169d1597c900ef06dcabea5` |
| `libScePlayerSelectionDialog.sprx` | `libScePlayerSelectionDialog` | 1 | `c2e61df112cf0a0b2405de812d7c0d9ae22cd1094a6531d5087703c0f4baacc6` | `1b44c6c459e3d37338a6c704adfd338463c2f3fdffa8e15d0b8c1bf66f926645` |
| `libSceRandom.sprx` | `libSceRandom` | 1 | `4af0ac1f1dc40c45a267a13f623e87080bf11cdedc210c1062e474d59b98b8fd` | `d6edf09bfaaa98c9603d854e929f7dbd48d3cd3e58efe9f6f13b6aecf9a46557` |
| `libSceRazorCpu.sprx` | `libSceRazorCpu` | 3 | `3f7958cd6c115830ebd151ef3d5daf0bdd898dd89e93c5612d8f7546d9254fe9` | `862ae7e33b6f0a56fcef2f7210b1c2e2900b7116e5c5dfc4d04c654336c03458` |
| `libSceSysmodule.sprx` | `libSceSysmodule` | 1 | `ba80a6c669e034fae536f3ef97eda3704c658a56989be126a2e6086a3af83711` | `4f7f3cf3a9b42cc1babc0045c5e70324616c187adda48ae980675e7e23973b78` |
| `ulobjmgr.sprx` | `ulobjmgr` | 2 | `82ee954d51f7d3eb9015b96bf4afcbb9f00ffff51afd99baaad5d90451f1b2a8` | `a678dfa91a0b013697d72acefafa137bcac4d7dcfa62875879770ee5ea49976b` |
| `libSceVideoOut.sprx` | `libSceVideoOut` | 1 | `c1a1b5647a29d5d114fccbfe45487c6712e3f06ebadb78d52e5ccf5604e83412` | `816634cef0047f2505af1da6d9f3c980aa0845bed9048f8b3a8f146bb1154497` |

The library column intentionally preserves GTA V's import metadata rather
than the provider filename. In particular, both AJM bodies are supplied by
`libSceAjm.native.sprx` but register under `libSceAjm`, and both coredump
bodies are supplied by `libkernel.sprx` but register under
`libSceCoredump`.

## Durable evidence

The self-contained packet lives under
`AcelogicFile/docs/gta-v/provider-evidence/provider23/`:

- `windows/queue.csv`: exact 23-row registration queue, SHA-256
  `c7adf2491aa780e04c4f0eba129607a66f6a681859ddefbca266354b02199e67`.
- `windows/selected/`: 15 canonical Ghidra selected-function JSON packets,
  whose exact hashes are in the table above.
- `windows/summary.json`: campaign summary, SHA-256
  `7da42885a63ae4aa514d3c264593f4f7ee054f065977f907e921079d189b67c3`.
- `windows/cleanup-proof.json`: independent ephemeral-host cleanup proof,
  SHA-256
  `d7d36d76e5d54ac8b40488f81571d0884f97fdca2c955ad34b6cc033af641587`.
- `mac/selected.json`: independent 23-function selection, SHA-256
  `236fe4177fffc4952c97d782ed087e28c06521f80bfe6313155af42ee538babb`.
- `mac/summary.json`: independent Mac audit summary, SHA-256
  `071e72aa39371cf3dc9ef68ac8086e70e00644e797bc401bcf7d49b71c71bbff`.
- `mac/packet-manifest.json`: source and derivative inventory, SHA-256
  `bb5c0148743dea64be376ce60a7a775676aa45a396adee084df283cfe0b1f8a5`.
- `mac/cleanup-proof.json`: Mac worker cleanup proof, SHA-256
  `af74fa36a502fd46b7a6ca26ca048335723052d434e6a9874bde2ab83bf03731`.
- `mac/ajm-caller-evidence.json`: GTA V AJM caller evidence, SHA-256
  `5d7a3436f58f71b69b3342d17772aad08bcfba508b52b722c877c6e0e4c84f90`.

No GTA V image, full firmware set, or Ghidra project is stored in this
packet. It contains only the selected metadata needed to reproduce and audit
the registrations.

## Reproduction

Each catalog is generated with
`AcelogicFile/scripts/generate-lle-nid-registrations.py`, using `windows/queue.csv`, its
matching file under `windows/selected/`, the exact component name, and the
logical library shown above. The generator independently recomputes every
catalog-symbol NID and rejects missing functions, evidence-set mismatches,
symbol/function-entry mismatches, or empty Ghidra function bodies.
