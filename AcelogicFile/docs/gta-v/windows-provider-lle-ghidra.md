# GTA V Windows provider registrations

The Windows campaign audited 181 GTA V NIDs across 22 exact Gen5 provider
ELFs with Ghidra `12.1.2_PUBLIC_20260605`. After deduplication against the rho
AGC/AMPR batch and the semantic Hs-offchip implementation, it contributes 107
new fail-closed, LLE-preferred registrations across 20 libraries.

| Library | New registrations |
| --- | ---: |
| `libSceAjm` | 1 |
| `libSceGameLiveStreaming` | 2 |
| `libSceJson2` | 17 |
| `libSceNet` | 5 |
| `libSceNetCtl` | 3 |
| `libSceNpAuth` | 5 |
| `libSceNpEntitlementAccess` | 5 |
| `libSceNpGameIntent` | 3 |
| `libSceNpManager` | 5 |
| `libSceNpUniversalDataSystem` | 5 |
| `libSceNpUtility` | 5 |
| `libSceNpWebApi2` | 17 |
| `libScePlayerInvitationDialog` | 2 |
| `libSceRemoteplay` | 3 |
| `libSceRtc` | 2 |
| `libSceSaveData_native` | 3 |
| `libSceShare` | 2 |
| `libSceSharePlay` | 2 |
| `libSceSystemService` | 5 |
| `libSceVoice` | 15 |

The unfiltered packet contains 179 safe provider definitions: 173 direct
bodies and six internal, non-external one-jump thunks. Seventy-one definitions
overlap the rho-generated catalogs (`libSceAgcDriver` 25 and `libSceAmpr` 46),
and `MM4IZSEYytQ` maps to the existing semantic HLE fallback. Those 72 are not
duplicated here.

Two AJM NIDs are deliberately not registered from this evidence:
`39WxhR-ePew` (`sceAjmBatchJobDecode`) and `5tOfnaClcqM`
(`sceAjmBatchStart`). Ghidra found neither exact symbol in the audited Gen5
`libSceAjm` provider. `-qLsfDAywIY` (`sceAjmBatchWait`) has a defined provider
body and is the one included AJM registration.

The three SaveData registrations preserve GTA's exact imported library name,
`libSceSaveData_native`. The two Share registrations preserve `libSceShare`
even though the analyzed provider file is `libSceShare.native.sprx`.

All 32 raw Ghidra logs contain import-success, analysis-success, and audit
markers, with zero unclassified errors. Only small provider binaries, scripts,
and results crossed hosts; no GTA V image and no full firmware set was
transferred. No Kyty-derived contract or implementation was used.

The Windows host is a 16-core/32-thread Ryzen 9 7950X with about 191 GiB RAM.
Six capacity stages showed that 16 simultaneous one-core Ghidra workers were
the throughput optimum: 0.4806 jobs/s, 82.92% average host CPU, and 100% peak.
After collection, an independent SSH check found the campaign root, scripts,
Java processes, and matching temporary entries all absent.

Machine-readable evidence:

- `provider-evidence/windows-ghidra-nid-audit.csv`, SHA-256
  `a28951db44df6f026715f9ab8e47b992cea172e396bc6d80b4383e00c14f2be6`.
- `provider-evidence/windows-ghidra-prefer-lle-recommendations.csv`, SHA-256
  `f059a17f1440499c499311093ac225a428f19c5a8776c41ca609aecd670df7c5`.
- `provider-evidence/windows-ghidra-audit-summary.json`, SHA-256
  `e1e93ff2f5967daa1d6f838b08f4490de7144118032e0d4e413cb767ab50e0a2`.
- `provider-evidence/windows-capacity-benchmark.json`, SHA-256
  `58f96d080daee69cd4b4448f101e929d00d6a61826a9b1f6d3d6c4636f885339`.
- `provider-evidence/windows-cleanup-proof.json`, SHA-256
  `cd4fe3472751199781dc93860d0e137342d0e9936d3973da361cd0d0d9bdaab7`.

The per-library selected packets are reproducibly derived by
`AcelogicFile/scripts/convert-ghidra-windows-audit.py`; registrations are emitted by
`AcelogicFile/scripts/generate-lle-nid-registrations.py`. The Ghidra archive SHA-256 was
`b62e81a0390618466c019c60d8c2f796ced2509c4c1aea4a37644a77272cf99d`.
