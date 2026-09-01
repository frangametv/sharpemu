<!--
Copyright (C) 2026 SharpEmu Emulator Project
Copyright (C) 2026 FranGameTv
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu: Fran's Version

<p align="center">
  <img src="./assets/images/logo.png" width="160" alt="SharpEmu logo">
</p>

<p align="center">
  Experimental PlayStation 5 emulator fork maintained by FranGameTv.
</p>

<p align="center">
  <a href="https://github.com/frangametv/sharpemu/releases">
    <img src="https://img.shields.io/badge/Download-GitHub%20Releases-2EA44F?style=for-the-badge&logo=github&logoColor=white" alt="Download from GitHub Releases">
  </a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Discord-%40FranGameTv-5865F2?style=for-the-badge&logo=discord&logoColor=white" alt="Discord @FranGameTv">
</p>

> [!IMPORTANT]
> ### Fran7 is now in development: the focus is on gameplay!
> **Fran6 has been released**, and I am now working on Fran7's rendering path,
> stability and performance as both Astro Bot and GTA V progress beyond their introductions.
> In my current tests, GTA V reaches the game menu for the first time on SharpEmu;
> the next goal is to turn this progress into stable, rendered gameplay in both games.

> [!WARNING]
> SharpEmu is at a very early stage. Astro Bot and Grand Theft Auto V are
> development targets in this fork. Crashes, missing graphics and
> incorrect behaviour are expected.

## About this fork

This repository is an unofficial development fork of
[SharpEmu](https://github.com/sharpemu/sharpemu).

The project is kept aligned with upstream SharpEmu while selected changes
from Acelogic's fork are manually reviewed, integrated, adapted and maintained here.
Those changes provided important groundwork for investigating real execution and
rendering progress in **Astro Bot** and **Grand Theft Auto V**.

The goal is practical and evidence-driven: test a game, study its logs, fix a
verified problem and test again.

## Current focus

- Keep the project synchronized with useful upstream SharpEmu development.
- Preserve and improve the valuable Acelogic work already integrated.
- Continue investigating Astro Bot's title/menu rendering, shader and runtime
  paths now that the intro video plays smoothly.
- Advance GTA V beyond its newly reached game menu and improve its rendering,
  stability and performance.
- Improve Vulkan on Windows/Linux and Metal/MoltenVK support on macOS.
- Add focused regression tests for every fix that can be reproduced safely.

SharpEmu can already load real `eboot.bin` files, execute native CPU code and
load system modules. Fran5 enabled Astro Bot to display its complete,
synchronized intro video and advance to the title controller in the validated
Windows test. Fran6 now advances Grand Theft Auto V through its introduction
and reaches the game menu. Text, menu and guest 3D rendering are still
incomplete, and SharpEmu is not yet a general-purpose or stable PS5 emulator.

## Download and Run

Prebuilt packages for Windows, Linux and macOS are available from
[GitHub Releases](https://github.com/frangametv/sharpemu/releases). Select the
archive for your operating system and extract it before launching SharpEmu.

<p align="left">
  <a href="https://github.com/frangametv/sharpemu/releases">
    <img src="https://img.shields.io/badge/Download-GitHub%20Releases-2EA44F?style=for-the-badge&logo=github&logoColor=white" alt="Download from GitHub Releases">
  </a>
</p>

A Vulkan-capable GPU and an up-to-date graphics driver are required. The macOS
package includes MoltenVK and runs as an x64 application, including through
Rosetta 2 on Apple Silicon.

## Build from Source

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
- **🎖️ Acelogic**, for the substantial compatibility research and implementation
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