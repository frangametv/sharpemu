# `sceAgcDriverSetTFRing` / `XlNp7jzGiPo` evidence packet

## Verdict

The contract is implementation-ready. Firmware `libSceAgcDriver.sprx` exports `XlNp7jzGiPo#G#A` at VA `0x6ff0`. It is a two-argument tail dispatcher whose selected callback returns `int32`; Ghidra's first-pass `void` prototype was an indirect-tail-call artifact, not the ABI. GTA tests `EAX` immediately after the import.

The selected firmware callback returns `0` only when the TF-ring driver operation succeeds. Address/size validation or driver failure returns `0x8A6DFFFF`. There are no output-memory writes.

## Source identity

- Decrypted/reconstructed provider: `/Users/mcruz/Developer/sharpemu/Firmware/dev/ssd0.system_b_out/common/lib/libSceAgcDriver.sprx`
- Size: `141,176` bytes
- SHA-256: `bc2ca28f3632ce69e25ab44991ed1f49bc1624fe39c2fc81f2efc6e705876348`
- ELF: x86-64, FreeBSD ABI, PS5 type `0xFE18`
- Encrypted 12.70 SELF was not transferred or analyzed on rho: `45,084` bytes, SHA-256 `f4eb57dbcefda5ccca70be909b5138890c4c33daa6976a8985c4dd9a55c9d212`
- Ghidra 12.1.2 recovered `396` functions, `9,886` instructions, and `761` symbols.

No Kyty or other emulator source was used.

## Exact provider flow

### Public export at `0x6ff0`

Effective ABI:

```c
int32_t sceAgcDriverSetTFRing(uint64_t ringAddress, uint32_t requestedSize);
```

The export:

1. Calls opaque imported NID `0vTn5IDMU9A`.
2. If that call returns `0x00840F00`, computes `effectiveSize = min(requestedSize, 0x4000)`; otherwise it preserves the requested size.
3. Loads the selected interface index from `0x1AA2C`.
4. Tail-jumps to callback slot `0x1A988 + index * 0x78`, passing `(ringAddress, effectiveSize)`.
5. Because this is a tail jump, the callback's `EAX` is the public export's return value.

### Interface initialization at `0x1d0`

The module initializer installs callback `0x6f90` into the interface's `+0x80` slot. It then chooses the first initialized interface and writes its index to `0x1AA2C`; this installation selects index `0`. The raw ELF BSS is zero before initialization, which is why a static memory read alone shows null slots.

If modules were already installed, this initializer returns `0x8A6D0006`. That lifecycle error belongs to initialization, not to `SetTFRing` itself.

### Selected callback at `0x6f90`

The callback invokes helper `0x9c20` with:

- driver handle from dword `0x1A90C`;
- ring address;
- effective size.

It maps helper `true` to `0`, and helper `false` to `0x8A6DFFFF`.

### Helper at `0x9c20`

Validation and operation order are exact:

1. Reject if `(ringAddress & 0xFF) != 0` (256-byte alignment).
2. Only if the address passed, reject if `(effectiveSize & 3) != 0` (4-byte size granularity).
3. Only if both passed, call imported NID `PfccT7qURYE` with `(driverHandle, 0x80108128, &args)`.
4. `args` is a 16-byte ioctl input containing the 64-bit ring address at offset `0` and 32-bit size at offset `8`.
5. Return true only when that imported call returns `0`.

Imported NID `Ou3iL1abvng` is the stack-guard failure path and is not normal API behavior.

Both validation failures and a nonzero driver result collapse to public error `0x8A6DFFFF`; no guest buffer is written.

## GTA consumer correlation

The runtime invocation was:

```text
NID XlNp7jzGiPo
return address 0x00000008029574A5
rdi = 0x0000000311F00200
rsi = 0x000000000003FFF8
```

The address is 256-byte aligned and the size is divisible by four. If the provider's process query takes the special branch, the request is capped to `0x4000`; otherwise `0x3FFF8` is passed. Either value clears the helper's size-granularity check.

GTA's local wrapper independently returns `0x8A6C000A` for null and `0x8A6C0002` for a low-byte-misaligned address before tail-calling the import. After the import, GTA tests `EAX` and executes `INT 0x41` on nonzero. SharpEmu's unresolved fallback `0xFFFFFFFF80020002` therefore caused the observed fatal gate.

## Implementation recommendation

Add the exact NID to `src/SharpEmu.Libs/Agc/AgcExports.cs` as a stateful ring-registration export:

1. Preserve the conditional `0x4000` cap when the emulated environment corresponds to the provider's special `0x00840F00` mode; do not silently cap in other modeled modes.
2. Enforce address-then-size validation in provider order.
3. Return `0x8A6DFFFF` without changing state on validation/backend failure.
4. On success, store the effective TF-ring address and size in AGC driver/session state, then return `0`.
5. Do not write guest output memory.

Do not implement this as an unconditional success stub. The driver ioctl's deeper kernel-side effect is not present in this user-space provider, so the safe HLE equivalent is explicit validated TF-ring registration with backend acceptance. Tests should cover GTA's exact arguments, both alignment failures, backend rejection, the `0x4000` cap branch, no-state-change on failure, and successful state replacement.

## Rho execution and cleanup

Only the 141,176-byte provider and compact Java evidence scripts were copied from the Mac. GTA, its eboot, and the firmware corpus were never transferred. Ghidra/JDK archives were downloaded directly into each RAM-backed job root and verified before use:

- Ghidra 12.1.2 archive SHA-256: `b62e81a0390618466c019c60d8c2f796ced2509c4c1aea4a37644a77272cf99d`
- Temurin JDK 21.0.11 archive SHA-256: `4b2220e232a97997b436ca6ab15cbf70171ecff52958a46159dfa5a8c44ca4de`

Headless analysis metrics:

| Pass | Wall | User | System | CPU | Peak RSS |
|---|---:|---:|---:|---:|---:|
| export | 15.34 s | 29.90 s | 2.09 s | 208% | 1,106,328 KB |
| callback/helper | 14.74 s | 30.19 s | 2.08 s | 218% | 1,168,664 KB |
| initializer | 14.84 s | 30.42 s | 1.99 s | 218% | 827,948 KB |

All three unique `/dev/shm/sharpemu-agc-settfring-*` roots were removed by traps. Each pass independently verified `0` residual matching campaign directories and `0` campaign Java processes.

## Packet files

- `contract.json`: machine-readable contract.
- `rho-first-pass.log`: export and tail-dispatch evidence.
- `rho-deep-pass.log`: selected callback/helper evidence.
- `rho-initializer-pass.log`: interface installation and selection evidence.
- `AgcDriverSetTFRingEvidence.java`, `AgcDriverSetTFRingDeepEvidence.java`: compact Ghidra scripts.
