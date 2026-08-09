<!--
Copyright (C) 2026 SharpEmu Emulator Project
Copyright (C) 2026 FranGameTv
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu — Fran

<p align="center">
  <img src="./assets/images/logo.png" width="260" alt="SharpEmu logo">
</p>

<p align="center">
  Experimental PlayStation 5 emulator fork maintained by FranGameTv.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Discord-%40FranGameTv-5865F2?style=for-the-badge&logo=discord&logoColor=white" alt="Discord @FranGameTv">
</p>

> [!WARNING]
> SharpEmu is at a very early stage. Astro Bot and Grand Theft Auto V are
> development targets, not playable-game claims. Crashes, missing graphics and
> incorrect behaviour are expected.

## About this fork

This repository is an unofficial development fork of
[SharpEmu](https://github.com/sharpemu/sharpemu).

The project is kept aligned with upstream SharpEmu while selected changes from
Acelogic's fork are manually reviewed, adapted and maintained here. Those
changes provided important groundwork for investigating real execution and
rendering progress in **Astro Bot** and **Grand Theft Auto V**.

The goal is practical and evidence-driven: test a game, study its logs, fix a
verified problem and test again. Changes are not included merely because they
exist in another fork; they must be understandable, useful and safe for the
current codebase.

## Current focus

- Keep the project synchronized with useful upstream SharpEmu development.
- Preserve and improve the valuable Acelogic work already integrated.
- Investigate Astro Bot rendering, shader and runtime failures.
- Advance GTA V through loader, kernel, audio, AGC and GPU failures.
- Improve Vulkan on Windows/Linux and Metal/MoltenVK support on macOS.
- Add focused regression tests for every fix that can be reproduced safely.

SharpEmu can already load real `eboot.bin` files, execute native CPU code, load
system modules and reach early graphics or video-output stages in some games.
It is not yet a general-purpose or stable PS5 emulator.

## Download and Run

Prebuilt packages for Windows, Linux and macOS are available from
[GitHub Releases](https://github.com/frangametv/sharpemu/releases). Select the
archive for your operating system and extract it before launching SharpEmu.

Windows PowerShell:

```powershell
.\SharpEmu.exe "C:\path\to\game\eboot.bin" 2>&1 |
  Tee-Object -FilePath "SharpEmu.log"
```

Linux and macOS:

```bash
chmod +x ./SharpEmu
./SharpEmu "/path/to/game/eboot.bin" 2>&1 | tee SharpEmu.log
```

A Vulkan-capable GPU and an up-to-date graphics driver are required. The macOS
package includes MoltenVK and runs as an x64 application, including through
Rosetta 2 on Apple Silicon.

The custom label displayed in the user interface can be changed in
[`CUSTOM_VERSION.txt`](./CUSTOM_VERSION.txt).

## Build from source

Install the .NET SDK version specified in [`global.json`](./global.json), then:

```bash
git clone https://github.com/frangametv/sharpemu.git
cd sharpemu
dotnet build SharpEmu.slnx -c Release
```

Build output is written under `artifacts`.

## Legal notice

This project is intended exclusively for research and education. It does not
contain firmware, games, keys or proprietary PlayStation assets. Use only
software and system files legally obtained from hardware you own. Piracy is not
supported or condoned.

## Credits

- The [official SharpEmu project](https://github.com/sharpemu/sharpemu) and its contributors.
- **Acelogic**, for the substantial compatibility research and implementation
  work that forms part of this fork's foundation.
- Community researchers whose ideas are reviewed and independently validated
  before being adapted here.
- [ShadPS4](https://github.com/shadps4-emu/shadPS4),
  [Kyty](https://github.com/InoriRus/Kyty) and Ryujinx for valuable emulator
  architecture references.

## Contact and contributing

For development discussion and test results, contact **@FranGameTv** on
Discord. Bug reports are most useful when they include the game title ID, the
exact build version and a complete log.

Before contributing code, read [`CONTRIBUTING.md`](./CONTRIBUTING.md).

## License

SharpEmu and this fork are distributed under the
[GPL-2.0 license](./LICENSE.txt).