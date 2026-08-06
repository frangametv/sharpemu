#!/usr/bin/env python3
"""Repeatable ASTRO BOT build, launch, capture, and evidence harness."""

from __future__ import annotations

import argparse
import datetime as dt
import itertools
import json
import math
import os
import platform
import queue
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path
from typing import Callable, Iterable


TOOL_VERSION = 10
TITLE_MILESTONE = "GAME: Level has started: title_controller_ship"
PS_LOGO_MILESTONE = "GAME: Level has started: ps_logo"
PS_STUDIOS_VIDEO = Path("data/prein/video/ps_studio_armadillo.mp4")
PS_STUDIOS_ANIMATION_REFERENCE_SECONDS = tuple(index / 4 for index in range(1, 25))
PS_STUDIOS_CONTROLLER_REFERENCE_SECONDS = tuple(
    round(4.55 + index / 10, 2) for index in range(8)
)
PS_STUDIOS_WORDMARK_REFERENCE_SECONDS = tuple(
    round(6.45 + index / 10, 2) for index in range(9)
)
PS_STUDIOS_REFERENCE_SECONDS = 6.5
# The translated intro is deliberately accepted even when its colors are
# overexposed or vertically striped. Current-frame diversity plus the later
# controller/wordmark phase matches carry the false-positive protection.
PS_STUDIOS_ANIMATION_SCORE = 0.06
PS_STUDIOS_CONTROLLER_SCORE = 0.08
PS_STUDIOS_CONTROLLER_WORDMARK_MARGIN = 0.10
PS_STUDIOS_WORDMARK_SCORE = 0.65
PS_STUDIOS_WORDMARK_CONTROLLER_MARGIN = 0.20
PS_STUDIOS_MIN_ANIMATION_FRAMES = 3
PS_STUDIOS_MIN_ANIMATION_SPAN_SECONDS = 1.0
PS_STUDIOS_MIN_REFERENCE_SPAN_SECONDS = 1.0
PS_STUDIOS_REFERENCE_BACKTRACK_SECONDS = 0.25
PS_STUDIOS_MIN_PHASE_DELAY_SECONDS = 0.25
PS_STUDIOS_MAX_FRAME_SIMILARITY = 0.95
PS_STUDIOS_SEQUENCE_SEARCH_LIMIT = 12
PS_STUDIOS_TOP_CANDIDATES = 8
PS_STUDIOS_MILESTONE_LEAD_SCORE = 0.15
VISUAL_SAMPLE_WIDTH = 320
VISUAL_SAMPLE_HEIGHT = 180
VISUAL_ROI_X = 50
VISUAL_ROI_Y = 15
VISUAL_ROI_WIDTH = 230
VISUAL_ROI_HEIGHT = 155
VISUAL_DECODE_BATCH_SIZE = 32
DEFAULT_GAME = Path(
    "/Volumes/Untitled/games/sharpemu/Games/Astro Bot/"
    "PPSA21564 [ 01 007 ]/PPSA21564-app-1/eboot.bin"
)
BASE_ENV = {
    "SHARPEMU_IGNORE_STACK_CHK": "1",
    "SHARPEMU_IGNORE_INT41": "1",
    # ASTRO regresses before the title milestone when newly imported HLE libc
    # compatibility exports take precedence over the firmware implementations.
    # Keep the title-specific harness on the validated all-LLE routing while the
    # emulator-wide default remains the safer mixed HLE/LLE policy.
    "SHARPEMU_LLE_LIBC_ALL": "1",
    "SHARPEMU_OVERLAY": "1",
    "SHARPEMU_SHADER_MAX_STEPS": "4096",
}
FORBIDDEN_ENV = {"SHARPEMU_DISABLE_IMPORT_LOOP_GUARD": "1"}
IMPORTANT_LINE = re.compile(
    r"GAME:|Level has started|FATAL|Unhandled|exception|DeviceLost|"
    r"AccessViolation|Illegal instruction|import thunk loop|vk\.guest_image",
    re.IGNORECASE,
)
TRANSIENT_STARTUP = re.compile(
    r"import thunk loop exceeded|UnmanagedCallersOnly|AccessViolation|"
    r"illegal instruction|0x8000082E6|TBB",
    re.IGNORECASE,
)
SOURCE_SUFFIXES = {".cs", ".csproj", ".props", ".targets", ".json", ".slnx"}


class SetupError(RuntimeError):
    pass


def repo_root() -> Path:
    return Path(__file__).resolve().parents[1]


def host_rid() -> str:
    system = platform.system()
    if system == "Darwin":
        return "osx-x64"
    if system == "Windows":
        return "win-x64"
    if system == "Linux":
        return "linux-x64"
    raise SetupError(f"unsupported host: {system}")


def executable_name(rid: str) -> str:
    return "SharpEmu.exe" if rid.startswith("win-") else "SharpEmu"


def publish_dir(root: Path, configuration: str, rid: str) -> Path:
    return root / "artifacts" / "publish" / "SharpEmu.CLI" / configuration / "net10.0" / rid


def find_dotnet() -> str:
    root = repo_root()
    pinned = json.loads((root / "global.json").read_text(encoding="utf-8"))["sdk"]["version"]
    name = "dotnet.exe" if platform.system() == "Windows" else "dotnet"
    candidates: list[str] = []
    if os.environ.get("DOTNET_ROOT"):
        candidates.append(str(Path(os.environ["DOTNET_ROOT"]) / name))
    candidates.append(str(Path.home() / ".dotnet" / name))
    if shutil.which("dotnet"):
        candidates.append(shutil.which("dotnet") or "")
    installed: list[str] = []
    for candidate in dict.fromkeys(candidates):
        if not candidate or not Path(candidate).is_file():
            continue
        result = run_quiet([candidate, "--list-sdks"])
        installed.extend(line.strip() for line in result.stdout.splitlines() if line.strip())
        if any(line.startswith(f"{pinned} ") for line in result.stdout.splitlines()):
            return candidate
    detail = "; ".join(dict.fromkeys(installed)) or "none"
    raise SetupError(f"global.json requires .NET SDK {pinned}; installed SDKs: {detail}")


def resolve_game(value: str | None, *, require: bool = True) -> Path:
    raw = value or os.environ.get("SHARPEMU_ASTRO_EBOOT")
    candidate = Path(raw).expanduser() if raw else DEFAULT_GAME
    if candidate.is_dir():
        direct = candidate / "eboot.bin"
        decrypted = candidate / "decrypted" / "eboot.bin"
        candidate = direct if direct.is_file() else decrypted
    candidate = candidate.resolve(strict=False)
    if require and not candidate.is_file():
        raise SetupError(
            f"ASTRO BOT eboot was not found at {candidate}; use --game or SHARPEMU_ASTRO_EBOOT"
        )
    return candidate


def parse_env(items: Iterable[str], env_file: str | None) -> dict[str, str]:
    result: dict[str, str] = {}
    entries = list(items)
    if env_file:
        for raw in Path(env_file).expanduser().read_text(encoding="utf-8").splitlines():
            line = raw.strip()
            if line and not line.startswith("#"):
                entries.append(line.removeprefix("export ").strip())
    for item in entries:
        if "=" not in item:
            raise SetupError(f"environment override must be KEY=VALUE: {item}")
        key, value = item.split("=", 1)
        key = key.strip()
        if not key.startswith("SHARPEMU_"):
            raise SetupError(f"only SHARPEMU_* overrides are accepted: {key}")
        result[key] = value
    for key, forbidden in FORBIDDEN_ENV.items():
        if result.get(key) == forbidden:
            raise SetupError(f"{key}={forbidden} is forbidden because it can create an unbounded guest loop")
    return result


def run_quiet(command: list[str], **kwargs: object) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        command,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        check=False,
        **kwargs,
    )


def kill_sharpemu() -> None:
    if platform.system() == "Windows":
        run_quiet(["taskkill", "/F", "/IM", "SharpEmu.exe"])
    else:
        run_quiet(["pkill", "-9", "-x", "SharpEmu"])
    time.sleep(0.25)


def sharpemu_running() -> bool:
    if platform.system() == "Windows":
        result = run_quiet(["tasklist", "/FI", "IMAGENAME eq SharpEmu.exe", "/NH"])
        return "SharpEmu.exe" in result.stdout
    return run_quiet(["pgrep", "-x", "SharpEmu"]).returncode == 0


def newest_source_mtime(root: Path) -> float:
    newest = 0.0
    for base in (root / "src", root):
        if not base.exists():
            continue
        for path in base.rglob("*") if base.name == "src" else base.iterdir():
            if path.is_file() and path.suffix in SOURCE_SUFFIXES:
                newest = max(newest, path.stat().st_mtime)
    return newest


def build_required(root: Path, binary: Path, policy: str) -> bool:
    if policy == "always":
        return True
    if policy == "never":
        if not binary.is_file():
            raise SetupError(f"prebuilt emulator not found: {binary}")
        return False
    return not binary.is_file() or newest_source_mtime(root) > binary.stat().st_mtime


def tail_text(path: Path, limit: int = 40) -> str:
    try:
        return "\n".join(path.read_text(encoding="utf-8", errors="replace").splitlines()[-limit:])
    except OSError:
        return ""


def publish(root: Path, configuration: str, rid: str, restore: bool) -> Path:
    dotnet = find_dotnet()
    stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    log_dir = root / "artifacts" / "astro-bot" / "build"
    log_dir.mkdir(parents=True, exist_ok=True)
    log_path = log_dir / f"{stamp}-{rid}.log"
    project = str(root / "src" / "SharpEmu.CLI" / "SharpEmu.CLI.csproj")
    command = [
        dotnet,
        "publish",
        project,
        "-c",
        configuration,
        "-r",
        rid,
        "--self-contained",
        "true",
        "--no-restore",
    ]
    local_scanner = root / "src" / "SharpEmu.Libs" / "Agc" / "Gen5ShaderPreflightScanner.cs"
    scanner_tracked = run_quiet(
        ["git", "ls-files", "--error-unmatch", str(local_scanner.relative_to(root))],
        cwd=root,
    ).returncode == 0
    if local_scanner.is_file() and not scanner_tracked:
        exclude_targets = log_dir / "local-scratch-excludes.targets"
        exclude_targets.write_text(
            "<Project><ItemGroup>"
            f'<Compile Remove="{local_scanner}" />'
            "</ItemGroup></Project>\n",
            encoding="utf-8",
        )
        command.append(f"-p:CustomAfterMicrosoftCommonTargets={exclude_targets}")
        print(f"[astro-test] excluding preserved untracked scratch source: {local_scanner}")
    print(f"[astro-test] publishing {rid}; full output: {log_path}")
    with log_path.open("w", encoding="utf-8") as handle:
        if restore:
            # Restore the complete RuntimeIdentifiers set recorded in the lock
            # file. Passing a single RID changes NuGet's evaluated RID set and
            # makes a valid multi-platform lock file fail locked-mode restore.
            restore_command = [dotnet, "restore", project, "--locked-mode"]
            restore_result = subprocess.run(
                restore_command,
                cwd=root,
                stdout=handle,
                stderr=subprocess.STDOUT,
                check=False,
            )
            if restore_result.returncode:
                print(tail_text(log_path), file=sys.stderr)
                raise SetupError(f"locked restore failed with exit code {restore_result.returncode}")
        result = subprocess.run(command, cwd=root, stdout=handle, stderr=subprocess.STDOUT, check=False)
    if result.returncode:
        print(tail_text(log_path), file=sys.stderr)
        if not restore:
            print("[astro-test] retry with --restore if assets are missing", file=sys.stderr)
        raise SetupError(f"publish failed with exit code {result.returncode}")
    binary = publish_dir(root, configuration, rid) / executable_name(rid)
    if not binary.is_file():
        raise SetupError(f"publish succeeded but binary is missing: {binary}")
    return binary


def prepare_macos_runtime(root: Path, binary: Path) -> None:
    output = binary.parent
    glfw_source = root / ".packages" / "ultz.native.glfw" / "3.4.0" / "runtimes" / "osx-x64" / "native" / "libglfw.3.dylib"
    glfw_target = output / "libglfw.3.dylib"
    if not glfw_target.is_file() and glfw_source.is_file():
        shutil.copy2(glfw_source, glfw_target)
    vulkan = output / "libvulkan.1.dylib"
    if not vulkan.is_file():
        helper = root / "scripts" / "fetch-macos-moltenvk.sh"
        if not helper.is_file():
            raise SetupError("macOS Vulkan loader is missing and fetch-macos-moltenvk.sh was not found")
        result = subprocess.run([str(helper), str(output)], cwd=root, check=False)
        if result.returncode:
            raise SetupError("failed to provision MoltenVK")


def safe_tag(value: str) -> str:
    tag = re.sub(r"[^A-Za-z0-9._-]+", "-", value.strip()).strip("-.")
    return tag or "run"


def write_json_atomic(path: Path, payload: object) -> None:
    temporary = path.with_name(f".{path.name}.{os.getpid()}.tmp")
    try:
        temporary.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def git_state(root: Path) -> dict[str, object]:
    head = run_quiet(["git", "rev-parse", "HEAD"], cwd=root).stdout.strip()
    branch = run_quiet(["git", "branch", "--show-current"], cwd=root).stdout.strip()
    status = run_quiet(["git", "status", "--short"], cwd=root).stdout.splitlines()
    return {"head": head, "branch": branch, "dirty": status}


def launch_command(binary: Path, eboot: Path, log_level: str) -> list[str]:
    base = [str(binary), "--cpu-engine=native", f"--log-level={log_level}", str(eboot)]
    if platform.system() == "Darwin":
        return ["arch", "-x86_64", *base]
    return base


_MACOS_WINDOW_IDS: dict[int, str] = {}
_MACOS_CAPTURE_FAILURE_COUNTS: dict[int, int] = {}
_MACOS_CAPTURE_LAST_FAILURES: dict[int, dict[str, object]] = {}


def _macos_window_candidate_rank(
    pid: int,
    candidate: dict[str, str],
) -> tuple[int, int, int, int]:
    """Prefer the launched process, then visible, larger, stable-ID windows."""
    try:
        owner_pid = int(candidate.get("OWNER_PID", "-1"))
        on_screen = int(candidate.get("ONSCREEN", "0")) != 0
        width = int(candidate.get("WIDTH", "0"))
        height = int(candidate.get("HEIGHT", "0"))
        window_id = int(candidate.get("WINDOW_ID", str(sys.maxsize)))
    except ValueError:
        owner_pid = -1
        on_screen = False
        width = 0
        height = 0
        window_id = sys.maxsize
    return (
        0 if owner_pid == pid else 1,
        0 if on_screen else 1,
        -(width * height),
        window_id,
    )


def find_macos_window_candidates(pid: int) -> list[dict[str, str]]:
    script = r'''
import CoreGraphics
import Foundation

let requestedPID = Int32(CommandLine.arguments[1]) ?? -1
// Enumerate every Space. The exact-PID window can legitimately be offscreen
// while Codex or another application owns the active full-screen Space.
let options: CGWindowListOption = [.excludeDesktopElements]
guard let windows = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else {
    exit(2)
}

struct Candidate {
    let number: UInt32
    let ownerPID: Int32
    let ownerName: String
    let width: Double
    let height: Double
    let exactPID: Bool
    let onScreen: Bool
}

var candidates: [Candidate] = []
for window in windows {
    let ownerPID = (window[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value ?? -2
    let ownerName = window[kCGWindowOwnerName as String] as? String ?? ""
    let isSharpEmu = ownerName.localizedCaseInsensitiveContains("SharpEmu")
    if ownerPID != requestedPID && !isSharpEmu { continue }
    let layer = (window[kCGWindowLayer as String] as? NSNumber)?.intValue ?? -1
    if layer != 0 { continue }
    guard let number = (window[kCGWindowNumber as String] as? NSNumber)?.uint32Value,
          let bounds = window[kCGWindowBounds as String] as? [String: Any],
          let width = (bounds["Width"] as? NSNumber)?.doubleValue,
          let height = (bounds["Height"] as? NSNumber)?.doubleValue else { continue }
    let onScreen = (window[kCGWindowIsOnscreen as String] as? NSNumber)?.boolValue ?? false
    let sharingState = (window[kCGWindowSharingState as String] as? NSNumber)?.intValue ?? 0
    let alpha = (window[kCGWindowAlpha as String] as? NSNumber)?.doubleValue ?? 0
    if sharingState == 0 || alpha <= 0 || width < 64 || height < 64 { continue }
    candidates.append(Candidate(
        number: number,
        ownerPID: ownerPID,
        ownerName: ownerName,
        width: width,
        height: height,
        exactPID: ownerPID == requestedPID,
        onScreen: onScreen
    ))
}
candidates.sort {
    if $0.exactPID != $1.exactPID { return $0.exactPID && !$1.exactPID }
    if $0.onScreen != $1.onScreen { return $0.onScreen && !$1.onScreen }
    let leftArea = $0.width * $0.height
    let rightArea = $1.width * $1.height
    if leftArea != rightArea { return leftArea > rightArea }
    return $0.number < $1.number
}
for candidate in candidates {
    let safeOwner = candidate.ownerName.replacingOccurrences(of: "\t", with: " ")
    print("WINDOW_ID=\(candidate.number)\tOWNER_PID=\(candidate.ownerPID)\tEXACT=\(candidate.exactPID ? 1 : 0)\tONSCREEN=\(candidate.onScreen ? 1 : 0)\tWIDTH=\(Int(candidate.width))\tHEIGHT=\(Int(candidate.height))\tOWNER=\(safeOwner)")
}
if candidates.isEmpty { exit(3) }
'''
    result = run_quiet(["/usr/bin/swift", "-", str(pid)], input=script)
    if result.returncode:
        return []
    candidates: list[dict[str, str]] = []
    for line in result.stdout.splitlines():
        if not line.startswith("WINDOW_ID="):
            continue
        fields = dict(
            field.split("=", 1)
            for field in line.split("\t")
            if "=" in field
        )
        if fields.get("WINDOW_ID", "").isdigit():
            candidates.append(fields)
    return sorted(candidates, key=lambda candidate: _macos_window_candidate_rank(pid, candidate))


def find_macos_window_id(pid: int) -> str | None:
    candidates = find_macos_window_candidates(pid)
    return candidates[0]["WINDOW_ID"] if candidates else None


def _capture_is_fresh_png(output: Path) -> bool:
    try:
        return output.stat().st_size > 8 and output.read_bytes()[:8] == b"\x89PNG\r\n\x1a\n"
    except OSError:
        return False


def _log_macos_capture_failure(
    pid: int,
    candidates: list[dict[str, str]],
    errors: list[str],
) -> None:
    count = _MACOS_CAPTURE_FAILURE_COUNTS.get(pid, 0) + 1
    _MACOS_CAPTURE_FAILURE_COUNTS[pid] = count
    _MACOS_CAPTURE_LAST_FAILURES[pid] = {
        "failure_number": count,
        "candidates": [dict(candidate) for candidate in candidates],
        "errors": list(errors),
    }
    if count <= 3:
        descriptions = [
            "id={WINDOW_ID} pid={OWNER_PID} exact={EXACT} "
            "size={WIDTH}x{HEIGHT} owner={OWNER}".format_map(candidate)
            for candidate in candidates
        ]
        print(
            f"[astro-test] macOS window capture failed ({count}/3); "
            f"launch_pid={pid}; candidates={descriptions or ['none']}; "
            f"errors={errors or ['none']}"
        )
    elif count == 4:
        print("[astro-test] further macOS window-capture failures suppressed")


def _macos_capture_diagnostics(pid: int) -> dict[str, object]:
    return {
        "backend": "macos-coregraphics",
        "failure_count": _MACOS_CAPTURE_FAILURE_COUNTS.get(pid, 0),
        "last_failure": _MACOS_CAPTURE_LAST_FAILURES.get(pid),
    }


def capture_macos(pid: int, output: Path) -> bool:
    candidates = find_macos_window_candidates(pid)
    cached_id = _MACOS_WINDOW_IDS.get(pid)
    ordered_ids = list(
        dict.fromkeys(
            ([cached_id] if cached_id is not None else []) +
            [candidate["WINDOW_ID"] for candidate in candidates]
        )
    )
    errors: list[str] = []
    for window_id in ordered_ids:
        try:
            output.unlink(missing_ok=True)
        except OSError:
            pass
        result = subprocess.run(
            ["screencapture", "-x", "-o", f"-l{window_id}", str(output)],
            check=False,
            capture_output=True,
            text=True,
        )
        if result.returncode == 0 and _capture_is_fresh_png(output):
            _MACOS_WINDOW_IDS[pid] = window_id
            return True
        error = (result.stderr or result.stdout).strip().splitlines()
        errors.append(f"id={window_id}: rc={result.returncode} {error[0] if error else 'no image'}")

    _MACOS_WINDOW_IDS.pop(pid, None)
    _log_macos_capture_failure(pid, candidates, errors)
    return False


def capture_windows(pid: int, output: Path) -> bool:
    escaped = str(output).replace("'", "''")
    script = rf'''
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class AstroCapture {{
  [StructLayout(LayoutKind.Sequential)] public struct RECT {{ public int Left, Top, Right, Bottom; }}
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}}
'@
$p = Get-Process -Id {pid}
$r = New-Object AstroCapture+RECT
if (-not [AstroCapture]::GetWindowRect($p.MainWindowHandle, [ref]$r)) {{ exit 2 }}
$bmp = New-Object Drawing.Bitmap ($r.Right-$r.Left), ($r.Bottom-$r.Top)
$gfx = [Drawing.Graphics]::FromImage($bmp)
$gfx.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$bmp.Save('{escaped}', [Drawing.Imaging.ImageFormat]::Png)
$gfx.Dispose(); $bmp.Dispose()
'''
    shell = shutil.which("pwsh") or shutil.which("powershell")
    return bool(shell) and subprocess.run([shell, "-NoProfile", "-Command", script], check=False).returncode == 0


def capture_linux(pid: int, output: Path) -> bool:
    xdotool = shutil.which("xdotool")
    image_import = shutil.which("import")
    if not xdotool or not image_import:
        return False
    result = run_quiet([xdotool, "search", "--onlyvisible", "--pid", str(pid)])
    window = next((line.strip() for line in result.stdout.splitlines() if line.strip()), "")
    return bool(window) and subprocess.run([image_import, "-window", window, str(output)], check=False).returncode == 0


def capture_window(pid: int, output: Path) -> bool:
    output.parent.mkdir(parents=True, exist_ok=True)
    system = platform.system()
    if system == "Darwin":
        return capture_macos(pid, output)
    if system == "Windows":
        return capture_windows(pid, output)
    if system == "Linux":
        return capture_linux(pid, output)
    return False


def write_timestamp_label(path: Path, text: str, width: int = 480, height: int = 30) -> None:
    """Write a tiny dependency-free PPM label understood by every FFmpeg build."""
    font = {
        "A": ("01110", "10001", "10001", "11111", "10001", "10001", "10001"),
        "B": ("11110", "10001", "10001", "11110", "10001", "10001", "11110"),
        "C": ("01110", "10001", "10000", "10000", "10000", "10001", "01110"),
        "D": ("11110", "10001", "10001", "10001", "10001", "10001", "11110"),
        "E": ("11111", "10000", "10000", "11110", "10000", "10000", "11111"),
        "F": ("11111", "10000", "10000", "11110", "10000", "10000", "10000"),
        "G": ("01110", "10001", "10000", "10111", "10001", "10001", "01110"),
        "H": ("10001", "10001", "10001", "11111", "10001", "10001", "10001"),
        "I": ("01110", "00100", "00100", "00100", "00100", "00100", "01110"),
        "J": ("00111", "00010", "00010", "00010", "00010", "10010", "01100"),
        "K": ("10001", "10010", "10100", "11000", "10100", "10010", "10001"),
        "L": ("10000", "10000", "10000", "10000", "10000", "10000", "11111"),
        "M": ("10001", "11011", "10101", "10101", "10001", "10001", "10001"),
        "N": ("10001", "11001", "10101", "10011", "10001", "10001", "10001"),
        "O": ("01110", "10001", "10001", "10001", "10001", "10001", "01110"),
        "P": ("11110", "10001", "10001", "11110", "10000", "10000", "10000"),
        "Q": ("01110", "10001", "10001", "10001", "10101", "10010", "01101"),
        "R": ("11110", "10001", "10001", "11110", "10100", "10010", "10001"),
        "S": ("01111", "10000", "10000", "01110", "00001", "00001", "11110"),
        "T": ("11111", "00100", "00100", "00100", "00100", "00100", "00100"),
        "U": ("10001", "10001", "10001", "10001", "10001", "10001", "01110"),
        "V": ("10001", "10001", "10001", "10001", "10001", "01010", "00100"),
        "W": ("10001", "10001", "10001", "10101", "10101", "10101", "01010"),
        "X": ("10001", "10001", "01010", "00100", "01010", "10001", "10001"),
        "Y": ("10001", "10001", "01010", "00100", "00100", "00100", "00100"),
        "Z": ("11111", "00001", "00010", "00100", "01000", "10000", "11111"),
        "0": ("01110", "10001", "10011", "10101", "11001", "10001", "01110"),
        "1": ("00100", "01100", "00100", "00100", "00100", "00100", "01110"),
        "2": ("01110", "10001", "00001", "00010", "00100", "01000", "11111"),
        "3": ("11110", "00001", "00001", "01110", "00001", "00001", "11110"),
        "4": ("00010", "00110", "01010", "10010", "11111", "00010", "00010"),
        "5": ("11111", "10000", "10000", "11110", "00001", "00001", "11110"),
        "6": ("01110", "10000", "10000", "11110", "10001", "10001", "01110"),
        "7": ("11111", "00001", "00010", "00100", "01000", "01000", "01000"),
        "8": ("01110", "10001", "10001", "01110", "10001", "10001", "01110"),
        "9": ("01110", "10001", "10001", "01111", "00001", "00001", "01110"),
        "t": ("00100", "00100", "11111", "00100", "00100", "00101", "00010"),
        "s": ("01111", "10000", "10000", "01110", "00001", "00001", "11110"),
        "+": ("00000", "00100", "00100", "11111", "00100", "00100", "00000"),
        "-": ("00000", "00000", "00000", "11111", "00000", "00000", "00000"),
        ".": ("00000", "00000", "00000", "00000", "00000", "00110", "00110"),
        " ": ("00000",) * 7,
    }
    background = (17, 17, 17)
    foreground = (245, 245, 245)
    pixels = bytearray(background * (width * height))
    scale = 3
    cursor_x = 8
    top = 4
    for character in text:
        glyph = font.get(character, font[" "])
        for row, bits in enumerate(glyph):
            for column, bit in enumerate(bits):
                if bit != "1":
                    continue
                for dy in range(scale):
                    for dx in range(scale):
                        x = cursor_x + column * scale + dx
                        y = top + row * scale + dy
                        if x >= width or y >= height:
                            continue
                        offset = (y * width + x) * 3
                        pixels[offset:offset + 3] = bytes(foreground)
        cursor_x += 6 * scale
    path.write_bytes(f"P6\n{width} {height}\n255\n".encode("ascii") + pixels)


def build_contact_sheet(
    frames: list[tuple[Path, float]],
    output: Path,
    columns: int,
    ffmpeg: str | None,
    labels: list[str] | None = None,
) -> bool:
    """Build a labelled PNG grid without adding a Python imaging dependency."""
    if not frames or not ffmpeg:
        return False
    columns = max(1, columns)
    cell_width = 480
    image_height = 270
    cell_height = 300
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="astro-contact-sheet-") as temporary:
        temporary_path = Path(temporary)
        command = [ffmpeg, "-y", "-loglevel", "error"]
        for index, (frame_path, elapsed) in enumerate(frames):
            label_path = temporary_path / f"label-{index:03d}.ppm"
            semantic_label = f"{labels[index].upper()} " if labels and index < len(labels) else ""
            write_timestamp_label(label_path, f"{semantic_label}t+{elapsed:07.1f}s")
            command.extend(["-i", str(frame_path), "-i", str(label_path)])
        filters: list[str] = []
        inputs: list[str] = []
        for index in range(len(frames)):
            image_input = index * 2
            label_input = image_input + 1
            filters.append(
                f"[{image_input}:v]"
                f"scale={cell_width}:{image_height}:force_original_aspect_ratio=decrease,"
                f"pad={cell_width}:{image_height}:(ow-iw)/2:(oh-ih)/2:color=0x111111"
                f"[image{index}];"
                f"[{label_input}:v]scale={cell_width}:{cell_height - image_height}"
                f"[label{index}];"
                f"[image{index}][label{index}]vstack=inputs=2[v{index}]"
            )
            inputs.append(f"[v{index}]")
        if len(frames) == 1:
            filters.append("[v0]copy[out]")
        else:
            layout = "|".join(
                f"{index % columns * cell_width}_{index // columns * cell_height}"
                for index in range(len(frames))
            )
            filters.append(
                "".join(inputs) +
                f"xstack=inputs={len(frames)}:layout={layout}:fill=0x111111[out]"
            )
        command.extend(
            [
                "-filter_complex",
                ";".join(filters),
                "-map",
                "[out]",
                "-frames:v",
                "1",
                str(output),
            ]
        )
        return run_quiet(command).returncode == 0 and output.is_file()


def pearson_correlation(left: bytes, right: bytes) -> float:
    if len(left) != len(right) or not left:
        return 0.0
    count = len(left)
    left_sum = sum(left)
    right_sum = sum(right)
    numerator = count * sum(a * b for a, b in zip(left, right)) - left_sum * right_sum
    left_variance = count * sum(value * value for value in left) - left_sum * left_sum
    right_variance = count * sum(value * value for value in right) - right_sum * right_sum
    denominator = math.sqrt(max(0, left_variance) * max(0, right_variance))
    return numerator / denominator if denominator else 0.0


def visual_sample_filter(*, screenshot: bool) -> str:
    screenshot_crop = (
        "crop=iw:ih*0.975:0:ih*0.025,"
        if screenshot and platform.system() == "Darwin"
        else ""
    )
    return (
        f"{screenshot_crop}scale={VISUAL_SAMPLE_WIDTH}:{VISUAL_SAMPLE_HEIGHT},"
        "histeq=strength=1:intensity=1,format=gray,gblur=sigma=3,sobel=scale=1,"
        f"crop={VISUAL_ROI_WIDTH}:{VISUAL_ROI_HEIGHT}:{VISUAL_ROI_X}:{VISUAL_ROI_Y}"
    )


def extract_visual_sample(
    image: Path,
    ffmpeg: str,
    *,
    screenshot: bool,
    timestamp: float | None = None,
) -> bytes | None:
    """Return an exposure-normalized, blurred Sobel ROI using only ffmpeg decoding."""
    command = [ffmpeg, "-hide_banner", "-loglevel", "error"]
    if timestamp is not None:
        command.extend(["-ss", str(timestamp)])
    command.extend(["-i", str(image), "-frames:v", "1"])
    filter_graph = visual_sample_filter(screenshot=screenshot)
    command.extend(
        [
            "-vf",
            filter_graph,
            "-f",
            "rawvideo",
            "-pix_fmt",
            "gray",
            "pipe:1",
        ]
    )
    try:
        completed = subprocess.run(
            command,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=15,
        )
    except (OSError, subprocess.TimeoutExpired):
        return None
    edge_size = VISUAL_ROI_WIDTH * VISUAL_ROI_HEIGHT
    if completed.returncode != 0 or len(completed.stdout) != edge_size:
        return None
    return completed.stdout


def extract_visual_samples(
    images: list[Path],
    ffmpeg: str,
    *,
    screenshot: bool,
) -> list[bytes | None]:
    """Decode screenshots in bounded ffmpeg batches, preserving exact order."""
    results: list[bytes | None] = []
    edge_size = VISUAL_ROI_WIDTH * VISUAL_ROI_HEIGHT
    filter_graph = visual_sample_filter(screenshot=screenshot)
    for start in range(0, len(images), VISUAL_DECODE_BATCH_SIZE):
        chunk = images[start:start + VISUAL_DECODE_BATCH_SIZE]
        if len(chunk) == 1:
            results.append(
                extract_visual_sample(chunk[0], ffmpeg, screenshot=screenshot)
            )
            continue

        command = [ffmpeg, "-hide_banner", "-loglevel", "error"]
        for image in chunk:
            command.extend(["-i", str(image)])
        filters = [
            f"[{index}:v]{filter_graph}[sample{index}]"
            for index in range(len(chunk))
        ]
        filters.append(
            "".join(f"[sample{index}]" for index in range(len(chunk)))
            + f"concat=n={len(chunk)}:v=1:a=0[out]"
        )
        command.extend(
            [
                "-filter_complex",
                ";".join(filters),
                "-map",
                "[out]",
                "-fps_mode",
                "passthrough",
                "-f",
                "rawvideo",
                "-pix_fmt",
                "gray",
                "pipe:1",
            ]
        )
        try:
            completed = subprocess.run(
                command,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=max(15, len(chunk) * 2),
            )
        except (OSError, subprocess.TimeoutExpired):
            completed = None

        expected_size = edge_size * len(chunk)
        if (
            completed is not None
            and completed.returncode == 0
            and len(completed.stdout) == expected_size
        ):
            results.extend(
                completed.stdout[offset:offset + edge_size]
                for offset in range(0, expected_size, edge_size)
            )
            continue

        # Keep the validator fail-safe: a batch-level decoder problem falls
        # back to the previously validated per-file extraction for that chunk.
        results.extend(
            extract_visual_sample(image, ffmpeg, screenshot=screenshot)
            for image in chunk
        )
    return results


def best_reference_score(
    edges: bytes,
    references: list[tuple[float, bytes]],
    correlate: Callable[[bytes, bytes], float] = pearson_correlation,
) -> tuple[float, float]:
    reference_seconds, score = max(
        (
            (reference_seconds, correlate(edges, reference_edges))
            for reference_seconds, reference_edges in references
        ),
        key=lambda item: item[1],
    )
    return score, reference_seconds


def visual_candidate_payload(candidate: dict[str, object]) -> dict[str, object]:
    """Strip the in-memory edge sample and round visual scores for JSON evidence."""
    return {
        "path": candidate["path"],
        "elapsed_seconds": candidate["elapsed_seconds"],
        "animation_score": round(float(candidate["animation_score"]), 6),
        "animation_reference_seconds": candidate["animation_reference_seconds"],
        "controller_score": round(float(candidate["controller_score"]), 6),
        "controller_reference_seconds": candidate["controller_reference_seconds"],
        "wordmark_score": round(float(candidate["wordmark_score"]), 6),
        "wordmark_reference_seconds": candidate["wordmark_reference_seconds"],
        "controller_wordmark_margin": round(
            float(candidate["controller_score"]) - float(candidate["wordmark_score"]),
            6,
        ),
        "wordmark_controller_margin": round(
            float(candidate["wordmark_score"]) - float(candidate["controller_score"]),
            6,
        ),
    }


def find_animation_sequence(
    candidates: list[dict[str, object]],
    controller: dict[str, object],
    correlate: Callable[[bytes, bytes], float] = pearson_correlation,
) -> tuple[list[dict[str, object]], float] | None:
    """Find distinct ordered current-run frames ending at the controller frame.

    Corrupt or overexposed guest output can correlate several visibly distinct
    frames to the same source-video timestamp. Reference-time progression is
    therefore a ranking preference, while current-run time span and image
    diversity remain hard requirements that prevent one stale frame passing.
    """
    prior = [
        candidate for candidate in candidates
        if (
            float(candidate["elapsed_seconds"]) < float(controller["elapsed_seconds"])
            and float(candidate["animation_score"]) >= PS_STUDIOS_ANIMATION_SCORE
        )
    ]
    strongest = sorted(
        prior,
        key=lambda item: float(item["animation_score"]),
        reverse=True,
    )[:PS_STUDIOS_SEQUENCE_SEARCH_LIMIT]
    ordered = sorted(strongest, key=lambda item: float(item["elapsed_seconds"]))
    best: tuple[
        tuple[float, int, float, float, float],
        list[dict[str, object]],
        float,
    ] | None = None
    for prefix in itertools.combinations(ordered, PS_STUDIOS_MIN_ANIMATION_FRAMES - 1):
        sequence = [*prefix, controller]
        elapsed = [float(item["elapsed_seconds"]) for item in sequence]
        span = elapsed[-1] - elapsed[0]
        if span < PS_STUDIOS_MIN_ANIMATION_SPAN_SECONDS:
            continue
        reference_times = [
            float(item["animation_reference_seconds"])
            for item in sequence
        ]
        reference_span = max(reference_times) - min(reference_times)
        reference_backtracks = sum(
            later + PS_STUDIOS_REFERENCE_BACKTRACK_SECONDS < earlier
            for earlier, later in zip(reference_times, reference_times[1:])
        )
        reference_is_ordered = int(reference_backtracks == 0)
        similarities = [
            correlate(
                left["edges"],  # type: ignore[arg-type]
                right["edges"],  # type: ignore[arg-type]
            )
            for left, right in itertools.combinations(sequence, 2)
        ]
        maximum_similarity = max(similarities, default=0.0)
        if maximum_similarity > PS_STUDIOS_MAX_FRAME_SIMILARITY:
            continue
        scores = [float(item["animation_score"]) for item in sequence]
        rank = (
            sum(scores),
            reference_is_ordered,
            reference_span,
            min(scores),
            -span,
        )
        if best is None or rank > best[0]:
            best = (rank, sequence, maximum_similarity)
    if best is None:
        return None
    return best[1], best[2]


def evaluate_ps_studios_splash(
    frames: list[tuple[Path, float]],
    reference_video: Path | None,
    ffmpeg: str | None,
    ps_logo_seconds: float | None,
    title_seconds: float | None,
    title_tail_seconds: float,
) -> dict[str, object]:
    evidence: dict[str, object] = {
        "detected": False,
        "reference": str(reference_video) if reference_video is not None else None,
        "reference_seconds": PS_STUDIOS_REFERENCE_SECONDS,
        "animation_reference_seconds": list(PS_STUDIOS_ANIMATION_REFERENCE_SECONDS),
        "controller_reference_seconds": list(PS_STUDIOS_CONTROLLER_REFERENCE_SECONDS),
        "wordmark_reference_seconds": list(PS_STUDIOS_WORDMARK_REFERENCE_SECONDS),
        "animation_score_threshold": PS_STUDIOS_ANIMATION_SCORE,
        "controller_score_threshold": PS_STUDIOS_CONTROLLER_SCORE,
        "controller_wordmark_margin": PS_STUDIOS_CONTROLLER_WORDMARK_MARGIN,
        "wordmark_score_threshold": PS_STUDIOS_WORDMARK_SCORE,
        "wordmark_controller_margin": PS_STUDIOS_WORDMARK_CONTROLLER_MARGIN,
        "minimum_animation_frames": PS_STUDIOS_MIN_ANIMATION_FRAMES,
        "minimum_animation_span_seconds": PS_STUDIOS_MIN_ANIMATION_SPAN_SECONDS,
        "preferred_reference_span_seconds": PS_STUDIOS_MIN_REFERENCE_SPAN_SECONDS,
        "minimum_phase_delay_seconds": PS_STUDIOS_MIN_PHASE_DELAY_SECONDS,
        "maximum_frame_similarity": PS_STUDIOS_MAX_FRAME_SIMILARITY,
        "candidate_frames": 0,
        "decoded_frames": 0,
        "best_score": None,
        "best_frame": None,
        "best_elapsed_seconds": None,
        "animation_frames": [],
        "animation_span_seconds": None,
        "reference_span_seconds": None,
        "controller": None,
        "controller_lead_in": None,
        "wordmark": None,
    }
    if not ffmpeg:
        evidence["reason"] = "ffmpeg-unavailable"
        return evidence
    if reference_video is None or not reference_video.is_file():
        evidence["reason"] = "reference-video-unavailable"
        return evidence
    if ps_logo_seconds is None:
        evidence["reason"] = "ps-logo-milestone-missing"
        return evidence
    latest = title_seconds + title_tail_seconds if title_seconds is not None else None
    candidates = [
        (path, elapsed)
        for path, elapsed in frames
        if elapsed >= ps_logo_seconds and (latest is None or elapsed <= latest)
    ]
    evidence["candidate_frames"] = len(candidates)

    def decode_references(
        phase: str,
        timestamps: tuple[float, ...],
    ) -> list[tuple[float, bytes]] | None:
        references: list[tuple[float, bytes]] = []
        for timestamp in timestamps:
            edges = extract_visual_sample(
                reference_video,
                ffmpeg,
                screenshot=False,
                timestamp=timestamp,
            )
            if edges is None:
                evidence["reason"] = f"reference-decode-failed:{phase}@{timestamp}"
                return None
            references.append((timestamp, edges))
        return references

    animation_references = decode_references(
        "animation",
        PS_STUDIOS_ANIMATION_REFERENCE_SECONDS,
    )
    controller_references = decode_references(
        "controller",
        PS_STUDIOS_CONTROLLER_REFERENCE_SECONDS,
    )
    wordmark_references = decode_references(
        "wordmark",
        PS_STUDIOS_WORDMARK_REFERENCE_SECONDS,
    )
    if animation_references is None or controller_references is None or wordmark_references is None:
        return evidence

    correlation_cache: dict[tuple[bytes, bytes], float] = {}

    def correlate(left: bytes, right: bytes) -> float:
        key = (left, right)
        cached = correlation_cache.get(key)
        if cached is not None:
            return cached
        score = pearson_correlation(left, right)
        correlation_cache[key] = score
        return score

    scored: list[dict[str, object]] = []
    decoded_candidates = extract_visual_samples(
        [path for path, _ in candidates],
        ffmpeg,
        screenshot=True,
    )
    for (path, elapsed), edges in zip(candidates, decoded_candidates):
        if edges is None:
            continue
        animation_score, animation_seconds = best_reference_score(
            edges,
            animation_references,
            correlate,
        )
        controller_score, controller_seconds = best_reference_score(
            edges,
            controller_references,
            correlate,
        )
        wordmark_score, wordmark_seconds = best_reference_score(
            edges,
            wordmark_references,
            correlate,
        )
        scored.append(
            {
                "path": str(path),
                "elapsed_seconds": elapsed,
                "animation_score": animation_score,
                "animation_reference_seconds": animation_seconds,
                "controller_score": controller_score,
                "controller_reference_seconds": controller_seconds,
                "wordmark_score": wordmark_score,
                "wordmark_reference_seconds": wordmark_seconds,
                "edges": edges,
            }
        )
    evidence["decoded_frames"] = len(scored)
    if not scored:
        evidence["reason"] = "candidate-decode-failed" if candidates else "no-candidate-frames"
        return evidence
    animation_ranked = sorted(
        scored,
        key=lambda item: float(item["animation_score"]),
        reverse=True,
    )
    controller_ranked = sorted(
        scored,
        key=lambda item: float(item["controller_score"]),
        reverse=True,
    )
    wordmark_ranked = sorted(
        scored,
        key=lambda item: float(item["wordmark_score"]),
        reverse=True,
    )
    best = wordmark_ranked[0]
    evidence.update(
        {
            "best_score": round(float(best["wordmark_score"]), 6),
            "best_frame": best["path"],
            "best_elapsed_seconds": best["elapsed_seconds"],
            "top_animation_candidates": [
                visual_candidate_payload(item)
                for item in animation_ranked[:PS_STUDIOS_TOP_CANDIDATES]
            ],
            "top_controller_candidates": [
                visual_candidate_payload(item)
                for item in controller_ranked[:PS_STUDIOS_TOP_CANDIDATES]
            ],
            "top_wordmark_candidates": [
                visual_candidate_payload(item)
                for item in wordmark_ranked[:PS_STUDIOS_TOP_CANDIDATES]
            ],
        }
    )
    controller_matches = [
        item for item in controller_ranked
        if (
            float(item["controller_score"]) >= PS_STUDIOS_CONTROLLER_SCORE
            and float(item["controller_score"]) - float(item["wordmark_score"])
            >= PS_STUDIOS_CONTROLLER_WORDMARK_MARGIN
        )
    ]
    wordmark_matches = [
        item for item in wordmark_ranked
        if (
            float(item["wordmark_score"]) >= PS_STUDIOS_WORDMARK_SCORE
            and float(item["wordmark_score"]) - float(item["controller_score"])
            >= PS_STUDIOS_WORDMARK_CONTROLLER_MARGIN
        )
    ]
    evidence["controller_matching_frames"] = len(controller_matches)
    evidence["wordmark_matching_frames"] = len(wordmark_matches)
    animation_sequence_cache: dict[
        str,
        tuple[list[dict[str, object]], float] | None,
    ] = {}

    def animation_sequence(
        controller: dict[str, object],
    ) -> tuple[list[dict[str, object]], float] | None:
        key = str(controller["path"])
        if key not in animation_sequence_cache:
            animation_sequence_cache[key] = find_animation_sequence(
                scored,
                controller,
                correlate,
            )
        return animation_sequence_cache[key]

    if controller_matches:
        evidence["controller"] = visual_candidate_payload(controller_matches[0])
        sequence_result = animation_sequence(controller_matches[0])
        if sequence_result is not None:
            diagnostic_sequence, diagnostic_similarity = sequence_result
            diagnostic_elapsed = [float(item["elapsed_seconds"]) for item in diagnostic_sequence]
            diagnostic_reference = [
                float(item["animation_reference_seconds"])
                for item in diagnostic_sequence
            ]
            evidence.update(
                {
                    "animation_frames": [
                        visual_candidate_payload(item)
                        for item in diagnostic_sequence
                    ],
                    "animation_span_seconds": round(
                        diagnostic_elapsed[-1] - diagnostic_elapsed[0],
                        6,
                    ),
                    "reference_span_seconds": round(
                        max(diagnostic_reference) - min(diagnostic_reference),
                        6,
                    ),
                    "maximum_animation_frame_similarity": round(
                        diagnostic_similarity,
                        6,
                    ),
                }
            )
    if wordmark_matches:
        evidence["wordmark"] = visual_candidate_payload(wordmark_matches[0])
    if not wordmark_matches:
        evidence["reason"] = "wordmark-missing"
        return evidence
    if not controller_matches:
        evidence["reason"] = "controller-animation-missing"
        return evidence

    proofs: list[
        tuple[
            tuple[float, float, float],
            list[dict[str, object]],
            dict[str, object],
            dict[str, object],
            float,
        ]
    ] = []
    ordered_phase_pairs = 0
    for controller in controller_matches:
        for wordmark in wordmark_matches:
            if (
                float(wordmark["elapsed_seconds"]) - float(controller["elapsed_seconds"])
                < PS_STUDIOS_MIN_PHASE_DELAY_SECONDS
            ):
                continue
            ordered_phase_pairs += 1
            sequence_result = animation_sequence(controller)
            if sequence_result is None:
                continue
            sequence, maximum_similarity = sequence_result
            quality = (
                sum(float(item["animation_score"]) for item in sequence)
                + float(controller["controller_score"])
                + float(wordmark["wordmark_score"])
            )
            rank = (
                quality,
                float(controller["controller_score"]),
                float(wordmark["wordmark_score"]),
            )
            proofs.append((rank, sequence, controller, wordmark, maximum_similarity))
    evidence["ordered_controller_wordmark_pairs"] = ordered_phase_pairs
    if not proofs:
        evidence["reason"] = (
            "controller-wordmark-order-missing"
            if ordered_phase_pairs == 0
            else "animation-sequence-missing"
        )
        return evidence

    _, sequence, controller, wordmark, maximum_similarity = max(
        proofs,
        key=lambda proof: proof[0],
    )
    animation_elapsed = [float(item["elapsed_seconds"]) for item in sequence]
    reference_elapsed = [float(item["animation_reference_seconds"]) for item in sequence]
    lead_in_candidates = [
        item for item in scored
        if (
            float(item["elapsed_seconds"]) < float(controller["elapsed_seconds"])
            and float(item["animation_reference_seconds"]) >= 4.0
            and float(item["animation_score"]) >= PS_STUDIOS_MILESTONE_LEAD_SCORE
            and correlate(
                item["edges"],  # type: ignore[arg-type]
                controller["edges"],  # type: ignore[arg-type]
            ) <= PS_STUDIOS_MAX_FRAME_SIMILARITY
        )
    ]
    controller_lead_in = max(
        lead_in_candidates,
        key=lambda item: float(item["elapsed_seconds"]),
        default=None,
    )
    evidence.update(
        {
            "detected": True,
            "reason": "ordered-animation-controller-wordmark",
            "animation_frames": [visual_candidate_payload(item) for item in sequence],
            "animation_span_seconds": round(animation_elapsed[-1] - animation_elapsed[0], 6),
            "reference_span_seconds": round(max(reference_elapsed) - min(reference_elapsed), 6),
            "maximum_animation_frame_similarity": round(maximum_similarity, 6),
            "controller": visual_candidate_payload(controller),
            "controller_lead_in": (
                visual_candidate_payload(controller_lead_in)
                if controller_lead_in is not None
                else None
            ),
            "wordmark": visual_candidate_payload(wordmark),
            "controller_delay_seconds": round(
                float(controller["elapsed_seconds"]) - animation_elapsed[-2],
                6,
            ),
            "wordmark_delay_seconds": round(
                float(wordmark["elapsed_seconds"]) - float(controller["elapsed_seconds"]),
                6,
            ),
            "matched_frame": wordmark["path"],
            "matched_elapsed_seconds": wordmark["elapsed_seconds"],
            "matched_score": round(float(wordmark["wordmark_score"]), 6),
            "matching_frames": len(sequence) + 1,
        }
    )
    return evidence


def select_milestone_frames(
    frames: list[tuple[Path, float]],
    ps_studios: dict[str, object],
    ps_logo_seconds: float | None,
    title_seconds: float | None,
) -> list[tuple[str, Path, float]]:
    """Select boot art, intro phases, first title frame, and final frame."""
    if not frames:
        return []
    ordered = sorted(frames, key=lambda frame: frame[1])
    selected: list[tuple[str, Path, float]] = []

    def add(role: str, frame: tuple[Path, float]) -> None:
        if any(existing_role == role and path == frame[0] for existing_role, path, _ in selected):
            return
        selected.append((role, frame[0], frame[1]))

    def candidate_frame(key: str) -> tuple[Path, float] | None:
        candidates = ps_studios.get(key)
        if not isinstance(candidates, list) or not candidates:
            return None
        candidate = candidates[0]
        if not isinstance(candidate, dict) or not candidate.get("path"):
            return None
        candidate_path = Path(str(candidate["path"]))
        return next((frame for frame in ordered if frame[0] == candidate_path), None)

    boot_candidates = [
        frame for frame in ordered
        if ps_logo_seconds is None or frame[1] < ps_logo_seconds
    ] or ordered[:1]

    def artifact_size(frame: tuple[Path, float]) -> int:
        try:
            return frame[0].stat().st_size
        except OSError:
            return -1

    add("boot-art", max(boot_candidates, key=artifact_size))
    for animation in ps_studios.get("animation_frames", []):
        if not isinstance(animation, dict) or not animation.get("path"):
            continue
        animation_path = Path(str(animation["path"]))
        animation_frame = next((frame for frame in ordered if frame[0] == animation_path), None)
        if animation_frame is not None:
            add("controller-animation", animation_frame)
    if not any(role == "controller-animation" for role, _, _ in selected):
        animation_candidate = candidate_frame("top_animation_candidates")
        if animation_candidate is not None:
            add("animation-candidate", animation_candidate)
    controller_lead = ps_studios.get("controller_lead_in")
    if isinstance(controller_lead, dict) and controller_lead.get("path"):
        lead_path = Path(str(controller_lead["path"]))
        lead_frame = next((frame for frame in ordered if frame[0] == lead_path), None)
        if lead_frame is not None:
            add("controller-symbols-lead-in", lead_frame)
    controller = ps_studios.get("controller")
    if isinstance(controller, dict) and controller.get("path"):
        controller_path = Path(str(controller["path"]))
        controller_frame = next((frame for frame in ordered if frame[0] == controller_path), None)
        if controller_frame is not None:
            add("controller-symbols", controller_frame)
    else:
        controller_candidate = candidate_frame("top_controller_candidates")
        if controller_candidate is not None:
            add("controller-candidate", controller_candidate)
    wordmark = ps_studios.get("wordmark")
    if isinstance(wordmark, dict) and wordmark.get("path"):
        wordmark_path = Path(str(wordmark["path"]))
        wordmark_frame = next((frame for frame in ordered if frame[0] == wordmark_path), None)
        if wordmark_frame is not None:
            add("wordmark", wordmark_frame)
    else:
        wordmark_candidate = candidate_frame("top_wordmark_candidates")
        if wordmark_candidate is not None:
            add("wordmark-candidate", wordmark_candidate)
    if title_seconds is not None:
        first_title = next((frame for frame in ordered if frame[1] >= title_seconds), None)
        if first_title is not None:
            add("first-title", first_title)
        else:
            add("title-not-captured", ordered[-1])
    add("final", ordered[-1])
    return sorted(selected, key=lambda milestone: milestone[2])


def stop_process(process: subprocess.Popen[str]) -> None:
    if process.poll() is not None:
        return
    try:
        process.send_signal(signal.SIGTERM)
        process.wait(timeout=5)
    except (ProcessLookupError, subprocess.TimeoutExpired):
        process.kill()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            pass


def stream_reader(stream: object, lines: queue.Queue[str | None]) -> None:
    try:
        for line in stream:  # type: ignore[union-attr]
            lines.put(line)
    finally:
        lines.put(None)


def run_attempt(
    command: list[str],
    environment: dict[str, str],
    log_path: Path,
    screenshot_path: Path,
    expectations: list[str],
    timeout: int,
    stall_timeout: int,
    stability: int,
    screenshot_delay: float,
    screenshot_interval: float,
    screenshot_boot_interval: float,
    screenshot_title_tail: float,
    screenshot_max_frames: int,
    screenshot_grid_columns: int,
    take_screenshot: bool,
    verbose: bool,
    keep_open: bool,
    ps_studios_video: Path | None,
    require_ps_studios: bool,
) -> dict[str, object]:
    kill_sharpemu()
    started = time.monotonic()
    last_output = started
    found: dict[str, float] = {}
    screenshot_done = False
    screenshot_due: float | None = None
    ps_logo_seen_at: float | None = None
    title_seen_at: float | None = None
    initial_timeline_interval = screenshot_interval
    timeline_due = (
        started + initial_timeline_interval
        if take_screenshot and screenshot_interval > 0
        else None
    )
    timeline_frames: list[tuple[Path, float]] = []
    timeline_attempts = 0
    attempt_stem = screenshot_path.name.removesuffix("-window.png")
    timeline_dir = screenshot_path.parent / f"{attempt_stem}-frames"
    contact_sheet_path = screenshot_path.parent / f"{attempt_stem}-contact-sheet.png"
    milestone_contact_sheet_path = screenshot_path.parent / f"{attempt_stem}-milestones.png"
    ps_studios_path = screenshot_path.parent / f"{attempt_stem}-ps-studios.png"
    timeline_path = screenshot_path.parent / f"{attempt_stem}-timeline.json"
    complete_at: float | None = None
    window_capture_diagnostics: dict[str, object] | None = None
    recent: list[str] = []
    process = subprocess.Popen(
        command,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
        bufsize=1,
        env=environment,
    )
    assert process.stdout is not None
    line_queue: queue.Queue[str | None] = queue.Queue()
    threading.Thread(target=stream_reader, args=(process.stdout, line_queue), daemon=True).start()
    result: dict[str, object] = {"pid": process.pid, "log": str(log_path), "expectations": found}
    try:
        with log_path.open("w", encoding="utf-8") as log:
            while True:
                now = time.monotonic()
                try:
                    line = line_queue.get(timeout=0.2)
                except queue.Empty:
                    line = ""
                if line is None:
                    line = ""
                elif line:
                    last_output = now
                    log.write(line)
                    log.flush()
                    clean = line.rstrip()
                    recent.append(clean)
                    recent[:] = recent[-80:]
                    apple_double_warning = "Module load failed:" in clean and "/._" in clean
                    if verbose or (IMPORTANT_LINE.search(clean) and not apple_double_warning):
                        print(clean)
                    if ps_logo_seen_at is None and PS_LOGO_MILESTONE in clean:
                        ps_logo_seen_at = now
                        if take_screenshot and screenshot_interval > 0:
                            timeline_due = now
                        print(
                            "[astro-test] ps_logo reached; dense capture starts now and remains active "
                            "until the title level"
                        )
                    if title_seen_at is None and TITLE_MILESTONE in clean:
                        title_seen_at = now
                        print(
                            "[astro-test] dense boot capture through "
                            f"t+{now - started + screenshot_title_tail:.1f}s"
                        )
                    for expected in expectations:
                        if expected not in found and expected in clean:
                            found[expected] = round(now - started, 3)
                            print(f"[astro-test] milestone at {found[expected]:.1f}s: {expected}")
                    if expectations and len(found) == len(expectations) and complete_at is None:
                        complete_at = now
                        screenshot_due = now + screenshot_delay
                if screenshot_due is not None and not screenshot_done and now >= screenshot_due:
                    screenshot_done = capture_window(process.pid, screenshot_path) if take_screenshot else False
                    print(
                        f"[astro-test] screenshot: {screenshot_path}"
                        if screenshot_done
                        else "[astro-test] targeted screenshot unavailable"
                    )
                    screenshot_due = None
                if (
                    timeline_due is not None and
                    len(timeline_frames) < screenshot_max_frames and
                    now >= timeline_due
                ):
                    timeline_attempts += 1
                    elapsed = round(now - started, 3)
                    frame_path = timeline_dir / (
                        f"frame-{len(timeline_frames) + 1:03d}-t+{elapsed:07.1f}s.png"
                    )
                    if capture_window(process.pid, frame_path):
                        timeline_frames.append((frame_path, elapsed))
                        print(f"[astro-test] timeline frame {len(timeline_frames):03d}: {frame_path}")
                    elif frame_path.exists():
                        frame_path.unlink()
                    dense_logo_capture = (
                        screenshot_boot_interval > 0 and
                        ps_logo_seen_at is not None and
                        (
                            title_seen_at is None or
                            now <= title_seen_at + screenshot_title_tail
                        )
                    )
                    timeline_due = now + (
                        screenshot_boot_interval
                        if dense_logo_capture
                        else screenshot_interval
                    )
                if not keep_open and complete_at is not None and now - complete_at >= stability:
                    result.update({"success": True, "reason": "milestones-stable", "elapsed": round(now - started, 3)})
                    break
                if timeout > 0 and now - started >= timeout:
                    result.update({"success": False, "reason": "timeout", "elapsed": round(now - started, 3)})
                    break
                if (
                    not keep_open
                    and complete_at is None
                    and stall_timeout > 0
                    and now - last_output >= stall_timeout
                ):
                    result.update(
                        {
                            "success": False,
                            "reason": "no-output-stall",
                            "elapsed": round(now - started, 3),
                        }
                    )
                    break
                code = process.poll()
                if code is not None and line_queue.empty():
                    success = keep_open and code == 0
                    result.update(
                        {
                            "success": success,
                            "reason": "process-exit",
                            "exit_code": code,
                            "elapsed": round(now - started, 3),
                        }
                    )
                    break
    except KeyboardInterrupt:
        result.update({"success": keep_open, "reason": "interrupted", "elapsed": round(time.monotonic() - started, 3)})
    finally:
        stop_process(process)
        process.stdout.close()
        if platform.system() == "Darwin":
            window_capture_diagnostics = _macos_capture_diagnostics(process.pid)
        _MACOS_WINDOW_IDS.pop(process.pid, None)
        _MACOS_CAPTURE_FAILURE_COUNTS.pop(process.pid, None)
        _MACOS_CAPTURE_LAST_FAILURES.pop(process.pid, None)
    result["screenshot"] = str(screenshot_path) if screenshot_done else None
    ps_logo_seconds = (
        round(ps_logo_seen_at - started, 3)
        if ps_logo_seen_at is not None
        else None
    )
    title_seconds = (
        round(title_seen_at - started, 3)
        if title_seen_at is not None
        else None
    )
    timeline_payload = {
        "capture_enabled": take_screenshot,
        "interval_seconds": screenshot_interval,
        "boot_interval_seconds": screenshot_boot_interval,
        "title_tail_seconds": screenshot_title_tail,
        "ps_logo_seen_seconds": ps_logo_seconds,
        "title_seen_seconds": title_seconds,
        "capture_attempts": timeline_attempts,
        "capture_successes": len(timeline_frames),
        "capture_failures": timeline_attempts - len(timeline_frames),
        "window_capture": window_capture_diagnostics,
        "frames": [
            {"path": str(path), "elapsed_seconds": elapsed}
            for path, elapsed in timeline_frames
        ],
    }
    ffmpeg = environment.get("SHARPEMU_FFMPEG_PATH") or shutil.which("ffmpeg")
    if timeline_frames:
        contact_sheet_done = build_contact_sheet(
            timeline_frames,
            contact_sheet_path,
            screenshot_grid_columns,
            ffmpeg,
        )
        result["contact_sheet"] = str(contact_sheet_path) if contact_sheet_done else None
        print(
            f"[astro-test] contact sheet: {contact_sheet_path}"
            if contact_sheet_done
            else "[astro-test] contact sheet unavailable; raw timeline frames were preserved"
        )
    else:
        result["contact_sheet"] = None
    result["timeline"] = str(timeline_path)
    result["timeline_frames"] = len(timeline_frames)
    result["timeline_capture_attempts"] = timeline_attempts
    result["timeline_capture_failures"] = timeline_attempts - len(timeline_frames)
    result["window_capture"] = window_capture_diagnostics
    ps_studios = evaluate_ps_studios_splash(
        timeline_frames,
        ps_studios_video,
        ffmpeg,
        ps_logo_seconds,
        title_seconds,
        screenshot_title_tail,
    )
    ps_studios["required"] = require_ps_studios
    result["ps_studios"] = ps_studios
    if bool(ps_studios["detected"]):
        matched_frame = Path(str(ps_studios["matched_frame"]))
        shutil.copy2(matched_frame, ps_studios_path)
        ps_studios["artifact"] = str(ps_studios_path)
        result["ps_studios_screenshot"] = str(ps_studios_path)
        print(
            "[astro-test] PS Studios intro sequence detected; wordmark at "
            f"t+{float(ps_studios['matched_elapsed_seconds']):.3f}s "
            f"(score={float(ps_studios['matched_score']):.4f}): {ps_studios_path}"
        )
    else:
        result["ps_studios_screenshot"] = None
        print(
            "[astro-test] PS Studios intro sequence missing; "
            f"reason={ps_studios['reason']}; best wordmark score={ps_studios['best_score']}"
        )
    milestone_entries = select_milestone_frames(
        timeline_frames,
        ps_studios,
        ps_logo_seconds,
        title_seconds,
    )
    milestone_frames = [
        (path, elapsed)
        for _, path, elapsed in milestone_entries
    ]
    milestone_labels = {
        "boot-art": "BOOT",
        "controller-animation": "ANIMATION",
        "animation-candidate": "ANIM-CAND",
        "controller-symbols-lead-in": "CONTROL-LEAD",
        "controller-symbols": "CONTROLLER",
        "controller-candidate": "CONTROL-CAND",
        "wordmark": "WORDMARK",
        "wordmark-candidate": "WORD-CAND",
        "first-title": "TITLE",
        "title-not-captured": "TITLE-MISSING",
        "final": "FINAL",
    }
    milestone_sheet_done = build_contact_sheet(
        milestone_frames,
        milestone_contact_sheet_path,
        max(1, len(milestone_frames)),
        ffmpeg,
        [milestone_labels.get(role, role) for role, _, _ in milestone_entries],
    )
    result["milestone_contact_sheet"] = (
        str(milestone_contact_sheet_path) if milestone_sheet_done else None
    )
    if milestone_sheet_done:
        print(f"[astro-test] milestone contact sheet: {milestone_contact_sheet_path}")
    timeline_payload["ps_studios"] = ps_studios
    timeline_payload["milestone_frames"] = [
        {"role": role, "path": str(path), "elapsed_seconds": elapsed}
        for role, path, elapsed in milestone_entries
    ]
    write_json_atomic(timeline_path, timeline_payload)
    if require_ps_studios and not bool(ps_studios["detected"]) and bool(result.get("success")):
        result.update(
            {
                "success": False,
                "reason": f"visual-milestone-missing:ps-studios:{ps_studios['reason']}",
            }
        )
    result["transient_startup_failure"] = bool(TRANSIENT_STARTUP.search("\n".join(recent))) and len(found) < len(expectations)
    return result


def build_environment(eboot: Path, binary: Path, overrides: dict[str, str], unset: list[str]) -> dict[str, str]:
    environment = os.environ.copy()
    for key in [*BASE_ENV, *FORBIDDEN_ENV, "SHARPEMU_GPU_WAIT_MODE"]:
        environment.pop(key, None)
    environment.update(BASE_ENV)
    environment.update(overrides)
    for key in unset:
        environment.pop(key, None)
    environment["SHARPEMU_APP0_DIR"] = str(eboot.parent)
    ffmpeg = environment.get("SHARPEMU_FFMPEG_PATH") or shutil.which("ffmpeg")
    if ffmpeg:
        environment["SHARPEMU_FFMPEG_PATH"] = ffmpeg
    if platform.system() == "Darwin":
        existing = environment.get("DYLD_LIBRARY_PATH", "")
        environment["DYLD_LIBRARY_PATH"] = str(binary.parent) + (f":{existing}" if existing else "")
    return environment


def execute_run(args: argparse.Namespace, *, keep_open: bool) -> int:
    if args.screenshot_interval < 0:
        raise SetupError("--screenshot-interval must be 0 or greater")
    if args.screenshot_boot_interval < 0:
        raise SetupError("--screenshot-boot-interval must be 0 or greater")
    if args.screenshot_title_tail < 0:
        raise SetupError("--screenshot-title-tail must be 0 or greater")
    if args.screenshot_max_frames < 1:
        raise SetupError("--screenshot-max-frames must be at least 1")
    if args.screenshot_grid_columns < 1:
        raise SetupError("--screenshot-grid-columns must be at least 1")
    if args.require_ps_studios and not args.screenshot:
        raise SetupError("--require-ps-studios requires screenshot capture")
    if args.require_ps_studios and args.screenshot_interval <= 0:
        raise SetupError("--require-ps-studios requires --screenshot-interval greater than 0")
    root = repo_root()
    rid = args.rid or host_rid()
    eboot = resolve_game(args.game)
    ps_studios_reference = eboot.parent / PS_STUDIOS_VIDEO
    ps_studios_video = ps_studios_reference if ps_studios_reference.is_file() else None
    if args.require_ps_studios and ps_studios_video is None:
        raise SetupError(f"PS Studios reference video not found: {ps_studios_reference}")
    output = publish_dir(root, args.configuration, rid)
    binary = output / executable_name(rid)
    if build_required(root, binary, args.build):
        binary = publish(root, args.configuration, rid, args.restore)
    if platform.system() == "Darwin":
        prepare_macos_runtime(root, binary)
    overrides = parse_env(args.env, args.env_file)
    environment = build_environment(eboot, binary, overrides, args.unset)
    if args.require_ps_studios and not environment.get("SHARPEMU_FFMPEG_PATH"):
        raise SetupError("--require-ps-studios requires ffmpeg")
    expectations = args.expect or [TITLE_MILESTONE]
    stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    run_dir = root / "artifacts" / "astro-bot" / "runs" / f"{stamp}-{safe_tag(args.tag)}"
    run_dir.mkdir(parents=True, exist_ok=False)
    command = launch_command(binary, eboot, args.log_level)
    manifest: dict[str, object] = {
        "tool_version": TOOL_VERSION,
        "created": dt.datetime.now(dt.timezone.utc).isoformat(),
        "tag": args.tag,
        "repo": str(root),
        "git": git_state(root),
        "rid": rid,
        "configuration": args.configuration,
        "binary": str(binary),
        "game": str(eboot),
        "command": command,
        "environment": {key: environment[key] for key in sorted(environment) if key.startswith("SHARPEMU_")},
        "expect": expectations,
        "capture": {
            "enabled": args.screenshot,
            "interval_seconds": args.screenshot_interval,
            "boot_interval_seconds": args.screenshot_boot_interval,
            "title_tail_seconds": args.screenshot_title_tail,
            "max_frames": args.screenshot_max_frames,
            "grid_columns": args.screenshot_grid_columns,
            "require_ps_studios": args.require_ps_studios,
            "ps_studios_reference": str(ps_studios_reference),
            "ps_studios_reference_seconds": PS_STUDIOS_REFERENCE_SECONDS,
            "ps_studios_animation_reference_seconds": list(
                PS_STUDIOS_ANIMATION_REFERENCE_SECONDS
            ),
            "ps_studios_controller_reference_seconds": list(
                PS_STUDIOS_CONTROLLER_REFERENCE_SECONDS
            ),
            "ps_studios_wordmark_reference_seconds": list(
                PS_STUDIOS_WORDMARK_REFERENCE_SECONDS
            ),
            "ps_studios_thresholds": {
                "animation_score": PS_STUDIOS_ANIMATION_SCORE,
                "controller_score": PS_STUDIOS_CONTROLLER_SCORE,
                "controller_wordmark_margin": PS_STUDIOS_CONTROLLER_WORDMARK_MARGIN,
                "wordmark_score": PS_STUDIOS_WORDMARK_SCORE,
                "wordmark_controller_margin": PS_STUDIOS_WORDMARK_CONTROLLER_MARGIN,
                "animation_frames": PS_STUDIOS_MIN_ANIMATION_FRAMES,
                "animation_span_seconds": PS_STUDIOS_MIN_ANIMATION_SPAN_SECONDS,
                "preferred_reference_span_seconds": PS_STUDIOS_MIN_REFERENCE_SPAN_SECONDS,
                "maximum_frame_similarity": PS_STUDIOS_MAX_FRAME_SIMILARITY,
            },
        },
        "attempts": [],
    }
    manifest_path = run_dir / "run.json"
    max_attempts = 1 if keep_open else args.retries + 1
    success = False
    attempts = manifest["attempts"]
    assert isinstance(attempts, list)
    write_json_atomic(manifest_path, manifest)
    print(f"[astro-test] artifacts: {run_dir}")
    for attempt in range(1, max_attempts + 1):
        print(f"[astro-test] attempt {attempt}/{max_attempts}")
        log_path = run_dir / f"attempt-{attempt:02d}.log"
        screenshot_path = run_dir / f"attempt-{attempt:02d}-window.png"
        attempt_started = dt.datetime.now(dt.timezone.utc).isoformat()
        attempts.append(
            {
                "attempt_number": attempt,
                "status": "in-progress",
                "started": attempt_started,
                "log": str(log_path),
                "screenshot": str(screenshot_path),
            }
        )
        write_json_atomic(manifest_path, manifest)
        result = run_attempt(
            command,
            environment,
            log_path,
            screenshot_path,
            expectations,
            args.timeout,
            args.stall_timeout,
            args.stability,
            args.screenshot_delay,
            args.screenshot_interval,
            args.screenshot_boot_interval,
            args.screenshot_title_tail,
            args.screenshot_max_frames,
            args.screenshot_grid_columns,
            args.screenshot,
            args.verbose,
            keep_open,
            ps_studios_video,
            args.require_ps_studios,
        )
        result.update(
            {
                "attempt_number": attempt,
                "status": "complete",
                "started": attempt_started,
                "finished": dt.datetime.now(dt.timezone.utc).isoformat(),
            }
        )
        attempts[-1] = result
        write_json_atomic(manifest_path, manifest)
        success = bool(result["success"])
        if success:
            break
        if attempt < max_attempts:
            qualifier = "known transient startup failure" if result["transient_startup_failure"] else result["reason"]
            print(f"[astro-test] retrying after {qualifier}")
    kill_sharpemu()
    latest = root / "artifacts" / "astro-bot" / "last-run.json"
    shutil.copy2(manifest_path, latest)
    print(f"[astro-test] {'PASS' if success else 'FAIL'}: {manifest_path}")
    return 0 if success else 1


def doctor(args: argparse.Namespace) -> int:
    root = repo_root()
    rid = args.rid or host_rid()
    binary = publish_dir(root, args.configuration, rid) / executable_name(rid)
    game = resolve_game(args.game, require=False)
    try:
        dotnet = find_dotnet()
        dotnet_ready = True
    except SetupError as error:
        dotnet = f"MISSING ({error})"
        dotnet_ready = False
    checks = {
        "repo": str(root),
        "branch": git_state(root)["branch"],
        "host": platform.platform(),
        "rid": rid,
        "dotnet": dotnet,
        "game": f"{'OK' if game.is_file() else 'MISSING'} {game}",
        "binary": f"{'OK' if binary.is_file() else 'MISSING'} {binary}",
        "ffmpeg": shutil.which("ffmpeg") or "MISSING",
        "SharpEmu processes": "RUNNING" if sharpemu_running() else "none",
    }
    for key, value in checks.items():
        print(f"{key}: {value}")
    return 0 if game.is_file() and dotnet_ready else 1


def summarize(path: str) -> int:
    content = Path(path).expanduser().read_text(encoding="utf-8", errors="replace").splitlines()
    interesting = [line for line in content if IMPORTANT_LINE.search(line)]
    print(f"lines: {len(content)}; significant: {len(interesting)}")
    for line in interesting[-40:]:
        print(line)
    return 0


def add_runtime_arguments(parser: argparse.ArgumentParser, *, manual: bool) -> None:
    parser.add_argument("--game", help="path to eboot.bin or its game directory")
    parser.add_argument("--rid", help="runtime identifier (auto-detected by default)")
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--build", choices=("auto", "always", "never"), default="auto")
    parser.add_argument("--restore", action="store_true", help="allow locked restore while publishing")
    parser.add_argument("--tag", default="manual" if manual else "test")
    parser.add_argument("--env", action="append", default=[], metavar="KEY=VALUE")
    parser.add_argument("--env-file", help="file containing SHARPEMU_KEY=VALUE lines")
    parser.add_argument("--unset", action="append", default=[], metavar="KEY")
    parser.add_argument("--expect", action="append", help="required log substring; repeat for multiple milestones")
    parser.add_argument("--timeout", type=int, default=0 if manual else 180)
    parser.add_argument(
        "--stall-timeout",
        type=int,
        default=0 if manual else 60,
        help="retry if no emulator output appears for this many pre-milestone seconds",
    )
    parser.add_argument("--stability", type=int, default=10)
    parser.add_argument("--retries", type=int, default=0 if manual else 2)
    parser.add_argument("--screenshot-delay", type=float, default=2.0)
    parser.add_argument(
        "--screenshot-interval",
        type=float,
        default=5.0,
        help="capture the SharpEmu window every N seconds; 0 disables the timeline",
    )
    parser.add_argument(
        "--screenshot-boot-interval",
        type=float,
        default=0.5,
        help="dense capture interval from ps_logo through title; 0 uses the normal interval",
    )
    parser.add_argument(
        "--screenshot-title-tail",
        type=float,
        default=2.0,
        help="seconds to keep dense capture after the title level starts",
    )
    parser.add_argument(
        "--screenshot-max-frames",
        type=int,
        default=160,
        help="maximum raw frames retained per attempt",
    )
    parser.add_argument(
        "--screenshot-grid-columns",
        type=int,
        default=4,
        help="number of columns in the generated timestamped contact sheet",
    )
    parser.add_argument("--screenshot", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument(
        "--require-ps-studios",
        action=argparse.BooleanOptionalAction,
        default=not manual,
        help="require a current-run visual match for the PS Studios splash",
    )
    parser.add_argument("--log-level", default="info")
    parser.add_argument("--verbose", action="store_true", help="mirror every emulator log line to the terminal")


def create_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    test = sub.add_parser("test", help="boot, verify milestones, capture, then stop")
    add_runtime_arguments(test, manual=False)
    run = sub.add_parser("run", help="boot and remain open for manual testing")
    add_runtime_arguments(run, manual=True)
    build = sub.add_parser("build", help="publish a Release runtime without launching")
    build.add_argument("--rid")
    build.add_argument("--configuration", default="Release")
    build.add_argument("--restore", action="store_true")
    check = sub.add_parser("doctor", help="show host, game, runtime, and process readiness")
    check.add_argument("--game")
    check.add_argument("--rid")
    check.add_argument("--configuration", default="Release")
    sub.add_parser("kill", help="close every SharpEmu process")
    summary = sub.add_parser("summarize", help="print milestones and failures from a run log")
    summary.add_argument("log")
    return parser


def main() -> int:
    args = create_parser().parse_args()
    try:
        if args.command == "kill":
            kill_sharpemu()
            print("SharpEmu processes: none" if not sharpemu_running() else "SharpEmu processes: still running")
            return 0 if not sharpemu_running() else 1
        if args.command == "doctor":
            return doctor(args)
        if args.command == "summarize":
            return summarize(args.log)
        if args.command == "build":
            root = repo_root()
            rid = args.rid or host_rid()
            binary = publish(root, args.configuration, rid, args.restore)
            if platform.system() == "Darwin":
                prepare_macos_runtime(root, binary)
            print(binary)
            return 0
        return execute_run(args, keep_open=args.command == "run")
    except (OSError, SetupError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
