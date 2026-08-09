# GTA V `sceKernelDirectMemoryQuery` rho/Ghidra evidence

## Scope and provenance

- Evidence source: Ghidra 12.1.2 analysis of a locally reconstructed, sectionless ELF64 derivative of the user-provided GTA V `eboot.bin`.
- No Kyty or other-emulator source was used.
- Original SELF was read-only and was not transferred.
- Original: 66,008,859 bytes, SHA-256 `60d394626ac62acd1b20d205599b104bb51756d468d3878ad14c230bfe305c11`.
- Derivative: 65,928,068 bytes, SHA-256 `76831e36c14b7e2b2597605c246a86633a9dbd507dac4d3e7a4fa1fa7c59badf`.
- Derivative changes: embedded ELF copied to file offset zero, SELF payloads placed at their ELF `p_offset`, `e_type` changed from `0xfe10` to `ET_DYN`, and the nonexistent section table zeroed. Direct blocked SELF mappings and nested program-header mappings are recorded in `reconstruction-report.json`.
- Reconstruction script SHA-256: `e6bfbd271f21c6a8103328f7e91e11a36a1d2f045dcfe4e9be44d2a72f6b4eb1`.
- Reconstruction report SHA-256: `cb337ada2781f1466c62544cb9c1684f6fed88ad87fee94cb38a99230145fd4e`.
- Ghidra evidence script SHA-256: `83a5c6815067392ad5f92db4bd6a1dd2c7761edd3e8f6b5f3a90d5473538855d`.
- rho campaign log: 86,895 bytes / 828 lines, SHA-256 `7e092a9f1312b95435db02ec7b4a48cf5f1dd9768a5802473a15820e2cf4d07e`.

## Address normalization

Ghidra chose image base `0x00100000`. GTA ran the main image at `0x0000000800000000`.

`runtime = 0x800000000 + (ghidra - 0x100000)`

| Object | Ghidra | ELF-relative | GTA runtime |
|---|---:|---:|---:|
| containing function | `0x0037a910` | `0x0027a910` | `0x80027a910` |
| observed call | `0x0037a94d` | `0x0027a94d` | `0x80027a94d` |
| observed return | `0x0037a952` | `0x0027a952` | `0x80027a952` |
| `BHouLQzh0X0#A#B` PLT thunk | `0x03176280` | `0x03076280` | `0x803076280` |
| `BHouLQzh0X0#A#B` GOT slot | `0x03a2aa88` | `0x0392aa88` | `0x80392aa88` |
| `pO96TwzOm5E#A#B` PLT thunk | `0x03175340` | `0x03075340` | `0x803075340` |
| `pO96TwzOm5E#A#B` GOT slot | `0x03a2a2e8` | `0x0392a2e8` | `0x80392a2e8` |

The exact runtime call has one unconditional-call reference to the Ghidra-resolved `BHouLQzh0X0#A#B` thunk. The thunk is `JMP qword ptr [0x03a2aa88]`. Ghidra found four calls to that thunk, all from `FUN_0037a910`.

## Containing function and repeated consumer contract

Ghidra created `FUN_0037a910`, body `0x0037a910..0x0037b13f` (2,096 bytes), called from `_DT_INIT`. Decompilation completed successfully. The four query call sites are:

| Ghidra call | GTA runtime call | return/CMP |
|---:|---:|---:|
| `0x0037a94d` | `0x80027a94d` | `0x0037a952` |
| `0x0037a98d` | `0x80027a98d` | `0x0037a992` |
| `0x0037a9dd` | `0x80027a9dd` | `0x0037a9e2` |
| `0x0037aaad` | `0x80027aaad` | `0x0037aab2` |

Each loop has the same machine-level contract:

```text
lea  rbx,[rbp-0x48]          ; 24-byte output buffer
xor  edi,edi                 ; initial offset = 0
loop:
mov  ecx,0x18                ; info size = 24
mov  esi,0x1                 ; flags = 1
mov  rdx,rbx                 ; info pointer
call BHouLQzh0X0#A#B
cmp  eax,0x8002000d
jz   done                    ; terminal result
mov  rdi,qword ptr [rbp-0x40]; next offset = info + 8
jmp  loop
done:
```

Equivalent consumer pseudocode, constrained to what the instructions prove:

```c
offset = 0;
for (;;) {
    result = BHouLQzh0X0(offset, 1, &info, 24);
    if ((uint32_t)result == 0x8002000d)
        break;
    offset = *(uint64_t *)((uint8_t *)&info + 8);
}
```

Ghidra's decompiler expresses the same loop as `BHouLQzh0X0_A_B(uVar6,1,&lStack_50,0x18)`, assigns `uStack_48` (the output qword at `+8`) as the next offset, and repeats while the result is not signed `-0x7ffdfff3` (`0x8002000d`). The disassembly is stronger on ordering: the `+8` field is read only after a non-terminal result. GTA does not consume the output buffer after the terminal result in these four loops.

This proves that, for GTA's `flags=1` enumeration path:

- arguments are `(offset, 1, info*, 24)` in `RDI, RSI, RDX, RCX`;
- a non-terminal response must provide the continuation offset at output byte `+8`;
- `0x8002000d` is the loop's terminal result;
- the caller does not reveal the semantic names of output bytes `+0`, `+8`, or `+16`, beyond using `+8` as the continuation offset.

## Related `sceKernelGetDirectMemorySize`

Ghidra resolved `pO96TwzOm5E#A#B` through PLT `0x03175340` and GOT `0x03a2a2e8`. It found ten direct calls: two from `FUN_028ee360`/`FUN_028ee9f0` and eight from `FUN_0037a910`.

At the first four enumeration sites, calls at `0x0037a92d`, `0x0037a96d`, `0x0037a9bb`, and `0x0037aa90` precede the four query loops. At the first observed site, the instructions between `pO96TwzOm5E` and `BHouLQzh0X0` do not consume the returned `RAX`; they set up the query buffer and arguments instead. Other calls in the containing function do use the size result as an argument to later allocation APIs, so this evidence does not imply the API is generally ignorable.

## SharpEmu comparison and bounded conclusion

The current SharpEmu handler returns `ORBIS_GEN2_ERROR_NOT_FOUND`, whose enum value is `0x80020002`, when it finds no tracked allocation. GTA compares only against `0x8002000d` at all four proven termination points. Therefore the observed loop is explained without guessing provider internals: `0x80020002` is non-terminal to this caller, so GTA reads the stale qword at `info+8` and queries it again.

This campaign does **not** prove which direct-memory regions the kernel must enumerate, the names of all three output fields, or the complete validation/error behavior of the provider. Those require firmware-side Ghidra evidence. No provider implementation beyond the proven caller contract is recommended here.

## rho execution and cleanup

- Host gate: 88 logical CPUs, 129,131,360 KiB available RAM, 65,823,772 KiB available `/dev/shm`, load 0.20.
- Pinned Ghidra archive: 572,803,866 bytes, SHA-256 `b62e81a0390618466c019c60d8c2f796ced2509c4c1aea4a37644a77272cf99d`.
- Pinned Temurin JDK 21 archive: 207,513,939 bytes, SHA-256 `4b2220e232a97997b436ca6ab15cbf70171ecff52958a46159dfa5a8c44ca4de`.
- Ghidra imported the derivative as ELF x86-64, produced 14,150 functions / 2,803,776 instructions, and the target decompilation completed.
- Whole-program auto-analysis reached the 900-second cap; the targeted post-script still completed and the headless process exited zero.
- Wall time 15:33.67; user 1,601.25 s; system 63.95 s; average 178% CPU; maximum RSS 2,031,744 KiB. Eight parallel decompiler workers were observed during analysis bursts.
- Ephemeral footprint: 2.0 GiB before analysis, 2.2 GiB after analysis; project contained 10 files.
- Campaign root: `/dev/shm/sharpemu-gta-dmq-mcruz-fjkYvCqX`.
- Trap cleanup succeeded, the independent `test ! -e` check succeeded, and an independent glob count found zero residual `sharpemu-gta-dmq-mcruz-*` directories.
