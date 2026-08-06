# GTA V Gen5 Kernel/Posix contract registrations

This packet pins the 27 GTA V function imports implemented by
`GtaVKernelContractExports`. Ghidra 12.1.2 was the sole reverse-engineering
source. Kyty and other emulator source were not used.

All 27 registrations target Gen5 only. The 18 Kernel registrations use the
catalog identity `libkernel`; the nine Posix registrations use
`libScePosix`. Every registration is semantic HLE with `PreferLle=false`.
Data imports (`__progname`, `_Stderr`, `_Stdout`, `Need_sceLibc`, and
`Need_sceLibcInternal`) and `sceLibcInternalBacktraceForGame` are deliberately
outside this function-registration set.

## Evidence chain

- Provider: `libkernel.sprx`
- Provider SHA-256: `0d91281f1d2cdcf4d8c2f4b920766b645ea086e679bd95074f30510178a706b0`
- Contract queue SHA-256: `8b9d1d6b7e66afa6d431bc8df186fd557ef8b02c18906a5cd0686b2dd589b7ce`
- Consolidated evidence SHA-256: `fa845efcb3786257c57105f7c54d318d1f1b732a865ee30b37378fb79b4e33b3`
- Original packet: `artifacts/gta-v-nid-evidence/rho-remaining90-contracts-20260718`
- Evidence policy: exact Ghidra function bodies/decompiles plus named ABI;
  no syscall-number literal is treated as a return value.

The implementation deliberately separates a proven semantic path from an
unproven one. An incomplete contract returns
`ORBIS_GEN2_ERROR_NOT_IMPLEMENTED` and does not write guest outputs or mutate
emulated state. It never returns invented success.

## Registration disposition

| NID | Export | Library | Entry | Disposition | Enforced contract |
| --- | --- | --- | --- | --- | --- |
| `crb5j7mkk1c` | `_is_signal_return` | `libkernel` | `0x27af0` | Fail closed | Requires actual SharpEmu signal-trampoline addresses before returning class 1/2/0. |
| `NhpspxdjEKU` | `_nanosleep` | `libkernel` | `0x10e0` | Semantic | Standard timespec input/remain output, raw success, and POSIX `-1`/errno failure via the existing nanosleep core. |
| `hHlZQUnlxSM` | `getrusage` | `libkernel` | `0x0b60` | Fail closed | Exact guest `rusage` layout/output population is not recovered. |
| `c7ZnT7V1B98` | `rmdir` | `libkernel` | `0x0d40` | Semantic | Performs the existing guest-path directory removal and maps HLE errors to POSIX `-1`/errno. |
| `QzB4O+bJQyA` | `sceKernelAprResolveFilepathsToIdsAndFileSizesForEach` | `libkernel` | `0x41490` | Fail closed | Shared helper `FUN_00002ae0` output and callback behavior is required. |
| `eYAh2vlCY-U` | `sceKernelAprResolveFilepathsToIdsForEach` | `libkernel` | `0x41420` | Fail closed | Shared helper `FUN_00002ae0` output and callback behavior is required. |
| `i3HWvW35jao` | `sceKernelAprResolveFilepathsWithPrefixToIds` | `libkernel` | `0x413d0` | Fail closed | Shared helper `FUN_00002ae0` output behavior is required. |
| `w5fcCG+t31g` | `sceKernelAprResolveFilepathsWithPrefixToIdsAndFileSizes` | `libkernel` | `0x41400` | Fail closed | Shared helper `FUN_00002ae0` size/output behavior is required. |
| `C+Khtbbx2g8` | `sceKernelAprResolveFilepathsWithPrefixToIdsAndFileSizesForEach` | `libkernel` | `0x41570` | Fail closed | Shared helper `FUN_00002ae0` size/output/callback behavior is required. |
| `VB-BtuIW8Xc` | `sceKernelAprResolveFilepathsWithPrefixToIdsForEach` | `libkernel` | `0x41500` | Fail closed | Shared helper `FUN_00002ae0` output/callback behavior is required. |
| `uWyW3v98sU4` | `sceKernelCheckReachability` | `libkernel` | `0x17560` | Fail closed | The recovered absolute-path validation is insufficient without the terminal path-walk/error mapping. |
| `cfwBSQyr5Ys` | `sceKernelDebugWriteCppExceptionInfo` | `libkernel` | `0x2a150` | Semantic sink | Ghidra proves a void, four-argument diagnostic command-`0x23` writer. SharpEmu intentionally omits that diagnostic sink and performs no guest write. |
| `-YTW+qXc3CQ` | `sceKernelInternalMemoryGetModuleSegmentInfo` | `libkernel` | `0x34140` | Fail closed | Helper `AmJ0mn2l4lM` must populate two qwords; zero without those writes is forbidden. |
| `3k6kx-zOOSQ` | `sceKernelMlock` | `libkernel` | `0x156b0` | Fail closed | Guest-range validation and observable memory-lock state are not modeled. |
| `0Cq8ipKr9n0` | `sceKernelUtimes` | `libkernel` | `0x172c0` | Fail closed | Guest timeval layout, path validation, and timestamp state are not completely modeled. |
| `IafI2PxcPnQ` | `scePthreadMutexTimedlock` | `libkernel` | `0x149c0` | Fail closed | Requires timed acquisition/timeout state in the SharpEmu mutex core. |
| `VADc3MNQ3cM` | `signal` | `libkernel` | `0x113a0` | Fail closed | Requires signal action state and the exact previous-handler/pointer-`-1` contract. |
| `VAzswvTOCzI` | `unlink` | `libkernel` | `0x0480` | Semantic | Performs the existing guest-path file removal and maps HLE errors to POSIX `-1`/errno. |
| `TXFFFiNldU8` | `getpeername` | `libScePosix` | `0x05e0` | Semantic | Reads a connected SharpEmu socket, writes IPv4/IPv6 sockaddr plus actual socklen, and returns `0` or POSIX `-1`/errno. |
| `6O8EwYOgH9Y` | `getsockopt` | `libScePosix` | `0x0b80` | Fail closed | PS5 option-level/name translation and output lengths are not modeled. |
| `5jRCs2axtr4` | `inet_ntop` | `libScePosix` | `0x121d0` | Semantic | Supports PS5 `AF_INET` 2 and `AF_INET6` 28, writes a terminated canonical string, returns `dst` or null with errno. |
| `Ez8xjo9UF4E` | `recv` | `libScePosix` | `0x12db0` | Semantic for flags 0 | Uses the existing connected-stream receive core. Nonzero unmodeled flags fail with explicit NOT_IMPLEMENTED before any buffer write. |
| `lUk6wrGXyMw` | `recvfrom` | `libScePosix` | `0x0e9b0` | Fail closed | Datagram/source-address output and cancellation bookkeeping are not modeled. |
| `fZOeZIOEmLw` | `send` | `libScePosix` | `0x12dc0` | Semantic for flags 0 | Uses the existing connected-stream send core. Nonzero unmodeled flags fail with explicit NOT_IMPLEMENTED. |
| `oBr313PppNE` | `sendto` | `libScePosix` | `0x0eb60` | Fail closed | Datagram/destination-address behavior and cancellation bookkeeping are not modeled. |
| `fFxGkxF2bVo` | `setsockopt` | `libScePosix` | `0x0ac0` | Fail closed | PS5 option-level/name translation and state effects are not modeled. |
| `TUuiYS2kE8s` | `shutdown` | `libScePosix` | `0x0ce0` | Semantic | Maps `SHUT_RD`/`SHUT_WR`/`SHUT_RDWR` 0/1/2 onto the connected SharpEmu socket and returns `0` or POSIX `-1`/errno. |

## Exact Ghidra body/decompile hashes

The table is copied from the immutable 27-row contract queue. It permits a
future focused pass to prove that it is extending the same analyzed bodies.

```text
NID           BODY_SHA256                                                       DECOMPILE_SHA256
crb5j7mkk1c   785fc6507120c6d6376aab904deeec50719798fa3a4c443be620c076024a08e3   02217d1a34c42d443c591a805ea473dc00c7c86bd461533c9660dbe949cc4a4a
NhpspxdjEKU   1b951c14997567d9062443421810f29d9de33031c952eb743daafb8ad0cf53a1   b53c3819207d0e502181643c96771782586b79e9c645c450ae372d6dcd5ebe49
hHlZQUnlxSM   b489c858d2e0572ba342844b91345aa834c9fc8277124f45aaf1ef3ef26febd0   06b54a15683e9dbb44d2d1857c61b29be63dde769e06a5ed3c0f4f5867f955d9
c7ZnT7V1B98   eaf0c63df0223739da6d2f237fd09f39ed9eb85452037154b3ea3d9aca8af9b2   5d1748b9269b6044f8b25792bf39ff52ef25f4b56fcc981861b2b3faf38cedf3
QzB4O+bJQyA   01f393982b58ba83357710599db71bac7f2dee0856e9a0f744969dec0c07c0d8   8b03a713ed831fe3d3b3ccbbf197a64879f5bcd6feb65164d4dbd6e39dace017
eYAh2vlCY-U   50f6a08e1f0adaadd0cd1f967542c1ddf4bef666f2c5706514d6dd8f76a4146b   c110b835fdc5ad4f2903a5bdcdfae62c80a684a27f3ac70d9d4096293eaeab2e
i3HWvW35jao   03d71f5be84d1253b6458134c666f36dfcdab7912872b3366ed23210b12cab42   df5df788fe40d41cc6644ffd8505c45c9bb3416face4a5d6aeadd9c26d802eee
w5fcCG+t31g   713575709673cca03c9b676d16e881f8608af9141dbc598f12b1f85d368957fa   1f0b911e0cc5b0a993bd133392a5e8e937e732b1f070439f4db5c5bcd6def9a5
C+Khtbbx2g8   46e1aad525b06b2b1a97e318973a8189a63cf485715ac5a9def9fd5c30f22742   0296eaac4eacba54b37b50ebbf76deab8a3a83286ad7828dbb723f5a9d7f07f7
VB-BtuIW8Xc   448439608c358b6a3be8524df40b7474c440d29dcf03960d02d10626f558af4d   76d32a7a48b64943900c5f9a173b0823d2387f3e546ad6eb07d1b1e66cc63c84
uWyW3v98sU4   a2efce6845a67ad89c308c68659c084ad12efb15ebaeba0d1347df26dbb86425   11bfbf58b449a95bb0d4b924bbaa6a8e3db2842642b17c6894cd2427e7ca448e
cfwBSQyr5Ys   d0d2141e4b9292f4b75c1ad4f2ee4f4d048d8ecc649e23cb79832acf2b3f21d5   2c5caddc2c882baf1d0599740be2b9463ff54a3f936f7c50773af0a310e71799
-YTW+qXc3CQ   ac6a5b1a61e09ca863ac14554dc1ab75eb91d12bd226749242ce1724e007729c   579666ecc2d27f84e4ea8fdfa35d7f711648aa1825ff43c37c6f545addf1cdf6
3k6kx-zOOSQ   dba1840c14832c0c74906241e48a9873e424a1e4a75f5604d6f1769118ad2a4a   f599073a0dc553c2b9b9ec71609de3c992361c6be92cd231dafa91a771895425
0Cq8ipKr9n0   874915587c7e93e3b8afc54c11c2c45b1aeb12cc3b4651b33874ab68542bbcb4   8bdbba2fa9b3c3377cd1b3bf69e1e19ca4531a9ba584d8615153d6f007e3784a
IafI2PxcPnQ   6d9b41b44f5db99e16062b022961647ed06d387af56d6e1aadf7a2d3a1a55f8f   1b2d080a00cb4af08491ff4fa5adc452b26c3b618c27e4bf53023134809d7689
VADc3MNQ3cM   91ff45bf02c64e0085a25060b1f13a7db57e83cd177dd9b1ca9a29028df1d5fa   6e8930c17b2b3b03527b646c5d2e598eb09bf04284d58d34af99f0d583349264
VAzswvTOCzI   c46cc716e597ee4003baf5a755f0f7042247d08ba121a762b527bbf2fc3c7ce8   139d8d69001cdaa2d9a89fa7cd1cefd40bccf068220160d2634995fc82825f0b
TXFFFiNldU8   5d66365599c575b365c18efb2af0a27146f33175519e8a65a38889be3b4a7a65   6e37deb476d6d63fc27a107b3ac20e32c72a9f00d4037f4f959d381b3a0dd108
6O8EwYOgH9Y   b524514b4cd1ff661aee858c67b196954fd0043769de24e494b99c0c9d7a9fa5   be7b6d6759a36a20ace8d46e715d0ce42df0efae4d688910f25ccf2f2002fe47
5jRCs2axtr4   0d6a9906028a8e6c9ace71fd6c988e3bf4c2ba110ec990fb7c18676a075439f8   2699840ce7d193f9f00df5ec94daf0a2295fb74d1094440b545d9f45bf2f201d
Ez8xjo9UF4E   36d8bc5ee3b53b6f870ae25a0fd2622bae7eb2903b82716a30fc3aaf00956d4b   c53a3f59a2c7fff42c455ace23e7bb4c02d2f51a40073e3c3323c797b8ecb87b
lUk6wrGXyMw   16c6cb2b51baabf91422c56c3af7134250f79d8211ed426fb9e2f2abbe85074d   e55e2781993a1e2a8397ea9395f3cfd1e43d0f069e414798bb04b4f7f9f51c63
fZOeZIOEmLw   bf34525f00582def2830411794676db0c6ed31c17c7c26ef21ba8dab250a3e8e   dd47144016fcfe2897a8a0416eb495c11e1e155712d367dd8391dac900da7e1d
oBr313PppNE   021d3763ebe7852efb17bca61055c0d938ebb94bb5b23ea785f49c69e66742be   13259620fef8dc127c3c575c82a5aa3537d36b838ac685632897e9630b167c7a
fFxGkxF2bVo   4511bbf5f0b2b3f76b508d242553f2bb19d85a172970396599ff04ff563e8f17   77af7660891caf19e7330ad1cde2c575e25ea618900b8ddb351c8c7e8771307d
TUuiYS2kE8s   26b7acbab4fba4560d0662f6ec07ea1e184332f0981f66cfc204d1ee6bad741e   47fb4c9220bb6a25db4a2d973e5b8912cfef43670c25074797c4bdc4e2f77278
```

## Remote saturation and cleanup proof

The Ghidra campaign completed 121/121 jobs with zero failures: one smoke job,
40 kernel-variant jobs, 40 libc-family jobs, and 40 deep-kernel jobs. The two
80-core waves peaked at 84.369 and 85.719 aggregate CPU cores.

Both ephemeral rho roots were removed after evidence retrieval:

```text
exact_root=/dev/shm/sharpemu-rho-rem90-1R57Kvrq
exact_root_exists=0
prefix_dirs=0
exact_root_java=0

exact_root=/dev/shm/sharpemu-rho-rem90-9eYXpyPJ
exact_root_exists=0
prefix_dirs=0
exact_root_java=0
```

The cleanup proofs have SHA-256 values
`bcfcd26bd2cb185f7d4538075268ad333299f3105f3b1d7899d80af931b69448`
and `66da95fde8c387f9d71ce1cb705c7301869b49c7959c2e6734b9ef9b815a1a87`.

## Regression contract

`GtaVKernelContractExportsTests` pins all of the following:

- exact 27-NID identity, spelling, and library assignment;
- Gen5-only visibility, no Gen4 projection, no duplicates, and no `PreferLle`;
- no data/InternalExt leakage into the function set;
- positive and negative behavior for every semantic implementation;
- NOT_IMPLEMENTED plus unchanged guest-output bytes for every deferred handler;
- NOT_IMPLEMENTED plus unchanged guest buffers for unmodeled `recv`/`send` flags.
