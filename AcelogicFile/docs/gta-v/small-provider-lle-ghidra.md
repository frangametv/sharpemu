# GTA V small-provider LLE registrations

This batch registers 47 GTA V imports whose implementations are present in
loaded Gen5 firmware providers. Ghidra 12.1.2 is the primary and only
reverse-engineering source for the registration evidence. Kyty and other
emulator source trees were not used.

The registrations do not claim that SharpEmu reimplements these functions.
Each registration is marked `PreferLle`, so a matching function in the loaded
guest provider wins. If that provider cannot be resolved, the shared HLE
fallback returns `ORBIS_GEN2_ERROR_NOT_IMPLEMENTED`; it never reports success.

| Provider | Library | Exact functions | Provider SHA-256 | Evidence SHA-256 |
| --- | --- | ---: | --- | --- |
| `libSceContentDelete.sprx` | `libSceContentDelete` | 3 | `0ad321f0f820ce2a08227a2bcf2050bc8abe349c63a7d71d1b7d1ef02d92e77c` | `a5f968ff13dea70669a104f2a1e0fb5c8c4803b71c2e6320f325abb5114ec5d7` |
| `libSceContentExport.sprx` | `libSceContentExport` | 5 | `a0a5564e5fa5a949af5f73db9f0b81550c413b1474a06e29d8f713538b35d5e7` | `4b978c4c53fe982927f28bf0f64588fd881fade04a578e5a31b5269172ab30a4` |
| `libSceContentSearch.sprx` | `libSceContentSearch` | 7 | `ce2ecc3765d0228fac6808b5a425b8d77ffae5b6b99c73ee3313d8ac22cfadb8` | `7710db872222b450936063e3aa44bee2649d69d8bf36fb780a3607f590feef35` |
| `libSceImeDialog.sprx` | `libSceImeDialog` | 6 | `78324a0cbeb0ed78df0abb200c840ad4d4b5307e2f245ea2ebdaa09f28dea4cb` | `9bb2b954e623ec6dd4738267a2330fc2cf131febe98a34034a09c564e9ff676f` |
| `libSceNpCommerce.sprx` | `libSceNpCommerce` | 7 | `7ad70ea553cdff96d91394c79e5484dc49f20d17b681862fb3dfdf098006b7b3` | `6536aa505d8443eda907151335c8e2a2ae98bf8d427d056e6ffc25623a420894` |
| `libSceSigninDialog.sprx` | `libSceSigninDialog` | 4 | `bc1e3b69a190e27649a540e556bc96382c69bad597784f162af2b788c84b27fb` | `1d4fbea1ab2cb65fc85dfad0cbd277531f045897fc9ede11f5ccbb43aeca618c` |
| `libSceVideoRecording.native.sprx` | `libSceVideoRecordingP` | 9 | `6b2adfdf208c96b37680927cfc9837598f4d01d96536cd49c72d693d84749537` | `435c10117b4d80737c5c77a0c2dabceb12fb54a85f66723298a6e27bb647b62b` |
| `libSceWebBrowserDialog.sprx` | `libSceWebBrowserDialog` | 6 | `1b3db8d07aae8aa973ad33f449defc510047031a5d11b6f7084b5a3ce3f42926` | `2f3cab90f8c0566c1fe4291d6bdf22ae907722ecc0cba050edacb384a02fcd04` |

Every evidence file reports an exact target/function count, no missing target,
no target without a function, a symbol address equal to its function entry,
and a positive Ghidra function body. The private VideoRecording provider is
intentional: the public provider contains eight of the nine targets, while
`libSceVideoRecording.native.sprx` contains all nine, including the unnamed
`iQS6DUtLybE` export.

The machine-readable evidence is stored in `AcelogicFile/docs/gta-v/provider-evidence/`.
Generated registrations are stored in `src/SharpEmu.Libs/Lle/` and can be
reproduced with `AcelogicFile/scripts/generate-lle-nid-registrations.py`.
