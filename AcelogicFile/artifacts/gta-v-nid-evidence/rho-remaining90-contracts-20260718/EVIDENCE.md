# Rho Ghidra evidence: GTA V remaining Kernel/libc/Posix NIDs

Ghidra 12.1.2 was the sole reverse-engineering source. No Kyty or other emulator source was used.
The selected queue contains 67 NIDs: Kernel 19, Posix 9, and libc-family 39.

## Exact disposition

- 34 function NIDs have both exact Ghidra bodies in the loaded GTA libc provider and runtime direct-symbol proof; they are safe Gen5 `PreferLle` candidates with fail-closed fallbacks.
- Every one of those 34 runtime addresses minus its Ghidra symbol VA yields the same GTA libc load base, `0x805AEA000`; `_Stderr` and `_Stdout` match that base too.
- 27 functions (Kernel 18 plus Posix 9) remain semantic HLE work. Kernel imports must never be routed through `PreferLle`.
- Primary `libkernel` and `libkernel_sys` each covered all 28 kernel/Posix targets (27 functions plus `__progname` data); `libkernel_web` covered 22 and lacked six functions.
- Three object NIDs are already handled by `HleDataSymbols`: `__progname`, `Need_sceLibc`, and `Need_sceLibcInternal`.
- `_Stderr` and `_Stdout` are object NIDs, not callable exports. GTA libc supplied runtime addresses `0x805CFA480` and `0x805CFA2B8`; add Gen5 data metadata, never `SysAbiExport` handlers.
- `sceLibcInternalBacktraceForGame` has a Ghidra body but no runtime symbol resolution in the recorded GTA run. It is excluded from the LLE include CSV and must stay fail-closed until a provider namespace/load path or semantic HLE contract is proven.

## Rho saturation

- `smoke`: 1/1 jobs, 0 failed, configured cap 2 cores, observed peak 2.430 cores and 0.62 GiB RSS.
- `kernel-variants-40x2`: 40/40 jobs, 0 failed, configured cap 80 cores, observed peak 79.655 cores and 24.44 GiB RSS.
- `libc-family-40x2`: 40/40 jobs, 0 failed, configured cap 80 cores, observed peak 84.369 cores and 30.24 GiB RSS.
- `deep-kernel-loaded-libc-40x2`: 40/40 jobs, 0 failed, configured cap 80 cores, observed peak 85.719 cores and 24.67 GiB RSS.

## Provider hashes

- GTA V original `libc.prx`: `be3b3847848b9f65a4ba880aea3dfdb0ab3b068036d187f4b63aa965000b8323`.
- Reconstructed GTA libc ELF: `309cb9031209eb9b838216994d2c39613fcd65ec1eae493c4b784b9dacdd06bb`.
- `libkernel.sprx`: `0d91281f1d2cdcf4d8c2f4b920766b645ea086e679bd95074f30510178a706b0`.
- `libkernel_sys.sprx`: `aa8e3f506501af293673b2634669c8ae7b0ffb75e8058463d961b9660e1200b9`.
- `libkernel_web.sprx`: `dec1b24247218b5cf7becb936cabff83164e66c564ce1acf8faad0980442da04`.
- `libSceLibcInternal.sprx`: `d85d61d42f7bb538caafa8b07066f36ec7553a0d6f442cc8138894f22b77370a`.
- Runtime proof log: `421c0627d2fa265a608c98bd54d280153f54f611ddd240c0bdd1166e00dc9b88`.

## Cleanup

Both unique `/dev/shm` roots were removed. `cleanup-proof.txt` and `cleanup-proof-deep.txt` each report `exact_root_exists=0`, `prefix_dirs=0`, and `exact_root_java=0`.

The machine-readable include, semantic HLE, data-object, and consolidated queues are adjacent to this file.
