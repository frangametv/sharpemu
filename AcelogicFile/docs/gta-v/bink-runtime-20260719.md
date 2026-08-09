# GTA V embedded Bink runtime evidence (2026-07-19)

## Result

GTA V's embedded Bink 2 startup movie is not the current post-logo blocker.
The game reads the complete movie, opens it successfully, advances through all
450 frames, clears its playing state, and destroys the movie. No `pread`
short-read, corruption, synthetic-EOS, or unconditional-success bypass should
be added.

## Pinned movie range

- Archive: `prosperoa.rpf`
- Offset: `14,169,088`
- Declared and observed byte length: `59,531,504`
- Signature: `KB2j`
- Frames: `450`
- Largest frame: `421,496` bytes
- Dimensions: `3840x2160`
- Rate: `30000/1001` fps
- Exact range SHA-256: `AC1D96289EA0B69EAE1FA4BF606A853E0C13225A91D540126BFA0C72F96D4545`

The temporary extracted range used for one decoder-capability check was
deleted immediately. No game archive or movie copy was retained on the remote
host.

## Decoder and bridge disposition

FFmpeg demuxed the exact range, recognized its video metadata, and decoded its
Bink audio, but reported that Bink 2 video is not implemented. It is therefore
not a Bink 2 video fallback.

`native/bink2-bridge/sharpemu_bink2_bridge.c` is a thin adapter to RAD's Bink
SDK entry points (`BinkOpen`, `BinkDoFrame`, `BinkCopyToBuffer`, and
`BinkNextFrame`). It is not a decoder itself. Building the adapter requires a
licensed `bink.h` and matching RAD library, neither of which is stored in this
repository or the Windows runtime. The optional range adapter can attach a
licensed decoder directly to an archive range without extracting it.

## Ghidra control-flow evidence

The positional-read path is generic and preserves the host byte count:

- runtime `0x802839E28`: call to `sceKernelPread`
- runtime `0x802839CE0`: VFS wrapper, returns the raw `sceKernelPread` result
- runtime `0x80283F740`: buffered/archive reader, advances its cursor by the
  returned count and returns that count unchanged

The live one-shot range trace observed:

```text
read=59531504 requested=59531504
return_rip=0x802839E2D
caller_return_rip=0x80283F865
```

The AsyncBink path uses the following movie flags:

- `movie+0x93`: loaded
- `movie+0x94`: playing
- `movie+0x95`: transient EOS transition
- `movie+0x9C`: pending decode count

Its clean EOS path clears playing, enqueues the destroy operation, and lets the
frontend completion observer dispose the movie and signal its callback.

## Live state proof

A same-user, read-only `ReadProcessMemory` probe sampled the Ghidra-identified
queue and movie fields. It never wrote guest or host process memory. The probe
observed:

1. A valid movie and HBINK with `loaded=1`, `playing=1`, `pending=0`.
2. HBINK `Frames=450`; `FrameNum` advanced from 1 through 450 while
   `LastFrameNum` tracked the prior frame.
3. At frame 450, the AsyncBink queue reached opcode 4 and the movie changed to
   `loaded=0`, `playing=0`, `pending=0`.
4. The HBINK fields were cleared after destruction.
5. The frontend completion byte at runtime `0x803A07488` read back as `1`.

The temporary Windows probe script was removed and its absence was verified.
Both SharpEmu processes used by each probe run were stopped by exact PID.

## Remaining presentation state

Despite the completed movie lifecycle, repeated swapchain dumps remained the
same GTA logo frame. Dumps 4 and later had SHA-256:

```text
83E9D1F12DB5A8AEC7088B6B5B469CF2A6BF6CA9AC80FF61D4E89E06E3B82267
```

This places the current blocker after movie completion, in the frontend
callback, UI state, renderer, or presentation path. Bink changes should be
limited to observational diagnostics or an optional licensed bridge; they
must not alter GTA's file-I/O or EOS semantics.
