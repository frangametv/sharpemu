# GTA V service initializer contracts

This packet records the exact GTA V consumer sites and matching firmware-provider
contracts used for three semantic Gen5 HLE fallbacks. KawaiiDRA/Ghidra 12.1.2 was
the only reverse-engineering source. The matching local provider files were read
from `Firmware/dev/ssd0.system_b_out/common/lib`; their SHA-256 values exactly
match the existing provider inventory.

The analyzed GTA program is `gta-v-eboot-ghidra-elf.bin`, image base `0x00100000`.
Runtime addresses below use guest base `0x800000000`.

| NID | Export / library | GTA call evidence | Provider evidence | HLE contract |
| --- | --- | --- | --- | --- |
| `9TrhuGzberQ` | `sceVoiceInit` / `libSceVoice` | Loaded `0x023eb2db` (runtime `0x8022eb2db`). GTA passes RDI = address of a 40-byte structure, first qword `0x10000000`, remaining bytes zero; RSI = `100`. It treats any nonzero return as initialization failure. | SHA-256 `bacd6c18bde775e27eade58f33460c86a7cad68b5c2716ce12b6bec8bd138df3`, function `0x00017a90`. The provider checks lifecycle before the pointer, rejects null with `0x804E0805`, copies five qwords, stores ESI as a 32-bit field in its initialization descriptor, returns `0` on success, and reports `0x804E0802` when already initialized. | Validate/read all 40 guest bytes, snapshot them and the low 32 bits of argument 2, create lifecycle state, and preserve provider validation order and deterministic error values. |
| `dPj4ZtRcIWk` | `sceContentSearchInit` / `libSceContentSearch` | Loaded `0x02f92781` (runtime `0x802e92781`). GTA passes RDI = address of qword `0x400000`; only a zero return sets its initialized byte. | SHA-256 `ce2ecc3765d0228fac6808b5a425b8d77ffae5b6b99c73ee3313d8ac22cfadb8`, function `0x0001d2e0`. The provider checks lifecycle first (`0x809D1002`), then requires a non-null pointer to a nonzero `0x4000`-aligned qword (`0x809D1003`), stores the pool size, and returns `0` after successful setup. | Validate and retain the pool size, create lifecycle state, and preserve provider validation order and errors. |
| `zoxb0wEChEM` | `sceContentDeleteInitialize` / `libSceContentDelete` | Loaded `0x02f92391` (runtime `0x802e92391`). GTA passes RDI = structure address with qword at `+0x08` equal to `0x4000`; only a zero return sets its initialized byte. | SHA-256 `0ad321f0f820ce2a08227a2bcf2050bc8abe349c63a7d71d1b7d1ef02d92e77c`, function `0x000005d0`. The provider validates pointer and `+0x08` pool size first (`0x809D5001`), then lifecycle (`0x809D5003`), and returns `0` after successful setup. | Read the exact field, create lifecycle state, and preserve provider validation order and errors. |

Two other live initialization calls were audited:

- `jqb7HntFQFc`, `sceWebBrowserDialogInitialize`, is called at loaded
  `0x023fff04` and `0x02b859d7`. Provider SHA-256
  `1b3db8d07aae8aa973ad33f449defc510047031a5d11b6f7084b5a3ce3f42926`,
  function `0x00001a00`, reports `0x80B8000E` when the browser service is absent.
  GTA explicitly handles the nonzero result.
- `kvYEw2lBndk`, `sceGameLiveStreamingInitialize`, is called at loaded
  `0x02b4e474` with RDI = `0x4000`. Provider SHA-256
  `93b31789fc637b0fa3f4c7fd9364f21130958583bfb10c717c222dac9fc3c369`,
  function `0x000013e0`, validates `0x4000`, rejects a second initialization
  with `0x80A00003`, and returns zero after its local allocation and service
  setup succeed. Function `0x00001340`, `sceGameLiveStreamingTerminate`, clears
  that state and reports `0x80A00004` if no initialization is active.

The earlier assumption that GTA treats the provider's `0x80A00007`
service-unavailable result as non-fatal was incorrect. In GTA Ghidra project
`gta_eboot_runtime_20260718_elf`, `FUN_02b4e420` calls
`sceGameLiveStreamingInitialize(0x4000)` and leaves its initialized flag clear
for every nonzero result. Its caller `FUN_023ed060` tests that Boolean and aborts
the user-manager initializer when it is false. The HLE fallback therefore
models the provider's local lifecycle and returns success for the valid first
initialization without claiming to implement host broadcasting.

These initializers do not make the remaining Voice, ContentSearch, or
ContentDelete operations semantic. Those exports remain LLE-preferred and
fail closed when their matching guest providers are unavailable.
