#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
LMP Universal Cross-Platform Build Engine.
Coordinates compilation, Native AOT publishing, dependency verification,
and clean passes across Windows, Linux, and macOS.
"""

import sys
import os
import re
import shutil
import platform
import subprocess
from pathlib import Path

class Color:
    RED = '\033[91m'
    GREEN = '\033[92m'
    YELLOW = '\033[93m'
    CYAN = '\033[96m'
    GRAY = '\033[90m'
    BOLD = '\033[1m'
    RESET = '\033[0m'

if not sys.stdout.isatty():
    for attr in dir(Color):
        if not attr.startswith("__"):
            setattr(Color, attr, "")

def get_platform_rid() -> str:
    """Detects .NET Runtime Identifier (RID) for the current host environment."""
    os_name = platform.system().lower()
    machine = platform.machine().lower()

    is_arm = any(x in machine for x in ("arm", "aarch64"))

    if os_name == "windows":
        return "win-arm64" if is_arm else "win-x64"
    elif os_name == "linux":
        return "linux-arm64" if is_arm else "linux-x64"
    elif os_name == "darwin":
        return "osx-arm64" if is_arm else "osx-x64"

    return "win-x64"

def get_git_info() -> tuple[str, str]:
    try:
        commit_count = subprocess.check_output(["git", "rev-list", "--count", "HEAD"], stderr=subprocess.DEVNULL, text=True).strip()
        git_hash = subprocess.check_output(["git", "rev-parse", "--short=7", "HEAD"], stderr=subprocess.DEVNULL, text=True).strip()
        return commit_count or "0", git_hash or "local"
    except Exception:
        return "0", "local"

def get_props_version(root: Path) -> str:
    props_file = root / "Directory.Build.props"
    major = "0"
    minor = "0"
    if props_file.is_file():
        text = props_file.read_text(encoding="utf-8")
        m_major = re.search(r'<VersionMajor>([^<]+)</VersionMajor>', text)
        m_minor = re.search(r'<VersionMinor>([^<]+)</VersionMinor>', text)
        if m_major:
            major = m_major.group(1).strip()
        if m_minor:
            minor = m_minor.group(1).strip()
    return f"{major}.{minor}"

def run_step(cmd: list[str], cwd: Path, description: str):
    print(f"{Color.YELLOW}>>> {description}...{Color.RESET}")
    res = subprocess.run(cmd, cwd=str(cwd))
    if res.returncode != 0:
        print(f"\n{Color.RED}✗ Build failed at step: {description} (Exit code: {res.returncode}){Color.RESET}")
        sys.exit(res.returncode)

def clean_tree(root: Path):
    print(f"{Color.YELLOW}>>> Cleaning all project build artifacts and temporary folders...{Color.RESET}")

    targets = ["publish", "publish-dev", "publish_aot"]
    for t in targets:
        p = root / t
        if p.exists():
            shutil.rmtree(p, ignore_errors=True)

    for archive in root.glob("*.7z"):
        archive.unlink(missing_ok=True)

    deleted_dirs = 0
    for folder in list(root.rglob("bin")) + list(root.rglob("obj")):
        if folder.is_dir():
            shutil.rmtree(folder, ignore_errors=True)
            deleted_dirs += 1

    print(f"  {Color.GREEN}✓ Clean complete! Pruned {deleted_dirs} temporary bin/obj folders.{Color.RESET}")

def main():
    root = Path(__file__).resolve().parent

    args = sys.argv[1:]
    mode = "debug"
    pause_on_exit = True

    if "nopause" in args:
        pause_on_exit = False
        args.remove("nopause")

    if args:
        mode = args[0].lower()

    rid = get_platform_rid()
    commit_count, git_hash = get_git_info()
    version_prefix = get_props_version(root)
    full_version = f"{version_prefix}.{commit_count}+{git_hash}"

    print()
    print(f"{Color.GRAY}╔══════════════════════════════════════════════════════════════╗{Color.RESET}")
    print(f"{Color.CYAN}{Color.BOLD} ║               LMP Cross-Platform Build System                ║{Color.RESET}")
    print(f"{Color.GRAY}╚══════════════════════════════════════════════════════════════╝{Color.RESET}")
    print(f"  Mode         : {Color.BOLD}{mode.upper()}{Color.RESET}")
    print(f"  Platform RID : {Color.BOLD}{rid}{Color.RESET}")
    print(f"  Version      : {Color.BOLD}v{full_version}{Color.RESET}\n")

    valid_modes = {"debug", "optimized", "release", "publish", "aot", "restore", "clean"}
    if mode not in valid_modes:
        print(f"{Color.RED}[ERROR] Unknown build mode '{mode}'.{Color.RESET}")
        print(f"Available: {' | '.join(sorted(valid_modes))}\n")
        sys.exit(1)

    try:
        if mode == "clean":
            clean_tree(root)

        elif mode == "restore":
            run_step(["dotnet", "restore", "LMP.sln", "--force", "--force-evaluate"], root, "Restoring solution dependencies")

        elif mode == "debug":
            run_step(["dotnet", "build", "LMP.sln", "-c", "Debug"], root, "Building Debug configuration")

        elif mode == "optimized":
            run_step([
                "dotnet", "build", "LMP.sln", "-c", "Debug",
                "-p:Optimize=true",
                "-p:DebugType=embedded"
            ], root, "Building Optimized Debug configuration")

        elif mode == "release":
            dead_code_tool = root / "Tools" / "find_dead_code.py"
            if dead_code_tool.is_file():
                print(f"{Color.YELLOW}>>> Auditing dead code and events (Native AOT + Source Tree)...{Color.RESET}")
                audit_res = subprocess.run([sys.executable, str(dead_code_tool)], cwd=str(root))
                if audit_res.returncode != 0:
                    print(f"\n{Color.YELLOW}[WARNING] Dead code or events detected. Check DeadCode.txt before publishing.{Color.RESET}\n")

            run_step([
                "dotnet", "build", "LMP.sln", "-c", "Release",
                "-p:DebugType=None",
                "-p:DebugSymbols=false"
            ], root, "Building Release configuration")

        elif mode in ("publish", "aot"):
            publish_dir = root / "publish"
            if publish_dir.exists():
                shutil.rmtree(publish_dir, ignore_errors=True)

            run_step([
                "dotnet", "publish", "LMP.csproj",
                "-c", "Release",
                "-r", rid,
                "-p:PublishAot=true",
                "-p:TrimMode=full",
                "-p:StripSymbols=true",
                "-p:DebugType=None",
                "-p:DebugSymbols=false",
                "-o", "./publish"
            ], root, f"Publishing Native AOT binary ({rid})")

            # Prune development leftovers from publish output
            for ext in ("*.xml", "*.pdb", "*.config"):
                for leftover in publish_dir.glob(ext):
                    leftover.unlink(missing_ok=True)

            print(f"\n{Color.GREEN}✓ Native AOT publish completed successfully: ./publish{Color.RESET}")

        print(f"\n{Color.GREEN}✓ Build successful{Color.RESET}\n")

    except KeyboardInterrupt:
        print(f"\n{Color.YELLOW}[ABORTED] Build interrupted by user.{Color.RESET}")
        sys.exit(130)

    if pause_on_exit and sys.platform.startswith("win"):
        try:
            os.system("pause")
        except Exception:
            pass

if __name__ == "__main__":
    main()