# Fran5 xnetcat experiment

This local-only branch evaluates selected ideas from xnetcat worktrees
`worktree-agent-f8834bd1` and `worktree-agent-a68e9885b6eafbbd6` against the
current Fran codebase. No xnetcat commit was merged or cherry-picked and no
source file was copied wholesale. Each retained behavior was rewritten around
the current SharpEmu/Fran APIs.

## Adapted changes

- DMA_DATA and WRITE_DATA can expose safe guest-memory writes while the command
  stream is still being walked, allowing later packets in the same DCB to read
  descriptors or indirect arguments produced by earlier packets. Ordered GPU
  mirroring and producer completion are preserved.
- Managed guest writes hold a tracker lease for the complete copy. Image-page
  rearming is deferred until the write finishes, preventing a concurrent rearm
  from changing page protection underneath the copy.
- Nested safe-point exception callbacks isolate staged entry-exit and context
  transfer state from the interrupted guest import.
- Metal buffer writeback records overlapping 32-bit and 64-bit GPU wait values
  using the current wait-registry model.
- Native stalls now report plausible guest return addresses found on the stack.
- The existing adaptive pthread self-lock heuristic has an opt-out for live
  compatibility experiments; its default behavior is unchanged.

The current branch already contained the useful command-buffer chaining,
indirect draw, label retention, shader opcode, image synchronization and
submission-lifetime work found in the reviewed branches. Those implementations
were kept instead of adding older duplicates.

## Deliberately excluded

- The large mixed `19105c1` change was not imported. Most useful components are
  already present, while the commit also combines unrelated code and an asset.
- The allocator free-list recovery heuristic was excluded because it changes
  global memory behavior without sufficient title-independent evidence.
- PPSA10112-specific behavior and per-title pthread policy were excluded.

## Experiment controls

- `SHARPEMU_EAGER_GPU_DATA_WRITES=0` disables the eager DMA/WRITE_DATA path.
- `SHARPEMU_PTHREAD_ADAPTIVE_SELF_LOCK_DEADLOCK=0` disables only the adaptive
  guest self-lock deadlock heuristic.
- `SHARPEMU_GUEST_IMAGE_CPU_SYNC=1` enables the existing CPU image-write
  tracking path and therefore its new managed-write lease.

## Verification

`dotnet test SharpEmu.slnx -c Release --no-restore` passes locally:

- 37 source-generator tests
- 85 shader-compiler tests
- 28 Metal shader-compiler tests
- 1653 library tests

The remaining build warning for `sceAgcAddPrimStateRegisters` predates this
experiment and concerns the export-name catalog, not the rewritten paths.
