#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
LMP Universal Cross-Platform Build Engine.
Coordinates compilation, Native AOT publishing, dependency restoration,
test execution, and deployment pipelines.
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
    major, minor = "0", "0"
    if props_file.is_file():
        text = props_file.read_text(encoding="utf-8")
        m_major = re.search(r'<VersionMajor>([^<]+)</VersionMajor>', text)
        m_minor = re.search(r'<VersionMinor>([^<]+)</VersionMinor>', text)
        if m_major:
            major = m_major.group(1).strip()
        if m_minor:
            minor = m_minor.group(1).strip()
    return f"{major}.{minor}"

def get_debug_executable(root: Path) -> Path:
    exe_name = "LMP.exe" if platform.system().lower() == "windows" else "LMP"
    return root / "bin" / "Debug" / "net11.0" / exe_name

def get_debug_aot_executable(root: Path) -> Path:
    exe_name = "LMP.exe" if platform.system().lower() == "windows" else "LMP"
    return root / "publish-aot-debug" / exe_name

def publish_debug_aot(root: Path, rid: str) -> Path:
    """
    Публикует Debug-сборку как Native AOT в ./publish-aot-debug для тестеров
    (содержит консоль логов, тест-раннер и единый машинный рантайм с релизом).
    """
    out_dir = root / "publish-aot-debug"
    if out_dir.exists():
        shutil.rmtree(out_dir, ignore_errors=True)

    avalonia_cache = root / "obj" / "Debug" / "net11.0" / rid / "Avalonia"
    if avalonia_cache.exists():
        shutil.rmtree(avalonia_cache, ignore_errors=True)

    run_step([
        "dotnet", "publish", "LMP.csproj",
        "-c", "Debug",
        "-r", rid,
        "-p:PublishAot=true",
        "-p:PublishTrimmed=true",
        "-p:TrimMode=full",
        "-p:TrimmerRootDescriptor=trimming.xml",
        "-p:JsonSerializerIsReflectionEnabledByDefault=false",
        "-p:Optimize=true",
        "-p:IlcOptimizationPreference=Speed",
        "-p:NuGetLockFilePath=packages.DebugAot.lock.json",
        "-o", str(out_dir)
    ], root, f"Publishing Debug Native AOT build ({rid})")
    return get_debug_aot_executable(root)

def run_step(cmd: list[str], cwd: Path, description: str):
    print(f"{Color.YELLOW}>>> {description}...{Color.RESET}")
    res = subprocess.run(cmd, cwd=str(cwd))
    if res.returncode != 0:
        print(f"\n{Color.RED}✗ Step failed: {description} (Exit code: {res.returncode}){Color.RESET}")
        sys.exit(res.returncode)

def clean_tree(root: Path):
    print(f"{Color.YELLOW}>>> Cleaning all project build artifacts and temporary folders...{Color.RESET}")

    targets = ["publish", "publish-dev", "publish_aot", "publish-aot-debug", "publish-aot-audit"]
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

def normalize_test_args(extra_args: list[str]) -> tuple[list[str], bool]:
    known_groups = {"audio", "cipher", "network", "youtube", "storage", "playlists", "system", "cache"}
    known_categories = {"unit", "integration", "benchmark", "e2e", "diagnostic", "action"}

    skip_build = False
    app_args: list[str] = []

    for token in extra_args:
        low = token.lower()
        if low in ("--no-build", "nobuild"):
            skip_build = True
        elif low in ("ui", "--ui", "--test-ui", "--debug-ui"):
            if "--test-ui" not in app_args:
                app_args.append("--test-ui")
        elif low in ("list", "--list", "--test-list"):
            app_args.append("--test-list")
        elif low in ("run", "--run", "--autorun"):
            app_args.append("--autorun")
        elif low in ("verbose", "--verbose"):
            app_args.append("--verbose")
        elif low.startswith("--group="):
            app_args.append(f"--test-group={token[8:]}")
        elif low.startswith("--category="):
            app_args.append(f"--test-category={token[11:]}")
        elif low.startswith("--filter="):
            app_args.append(f"--test-filter={token[9:]}")
        elif token.startswith("--"):
            app_args.append(token)
        elif low in known_groups:
            app_args.append(f"--test-group={token}")
        elif low in known_categories:
            app_args.append(f"--test-category={token}")
        else:
            app_args.append(f"--test-filter={token}")

    if not any(a.startswith(("--test", "--run-")) for a in app_args):
        app_args.insert(0, "--test")

    return app_args, skip_build

def configure_utf8_io():
    os.environ.setdefault("PYTHONUTF8", "1")
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    if sys.platform.startswith("win"):
        try:
            import ctypes
            ctypes.windll.kernel32.SetConsoleOutputCP(65001)
            ctypes.windll.kernel32.SetConsoleCP(65001)
        except (AttributeError, OSError):
            pass

def ensure_git_hooks(root: Path):
    git_dir = root / ".git"
    hook_file = root / "Tools" / "githooks" / "pre-commit"
    if not git_dir.exists() or not hook_file.is_file():
        return

    try:
        raw = hook_file.read_bytes()
        if b"\r\n" in raw:
            hook_file.write_bytes(raw.replace(b"\r\n", b"\n"))
        if not sys.platform.startswith("win"):
            hook_file.chmod(hook_file.stat().st_mode | 0o111)

        current_path = subprocess.run(
            ["git", "config", "--local", "--get", "core.hooksPath"],
            cwd=str(root),
            capture_output=True,
            text=True
        ).stdout.strip()

        if current_path != "Tools/githooks":
            subprocess.run(
                ["git", "config", "--local", "core.hooksPath", "Tools/githooks"],
                cwd=str(root),
                check=False
            )
    except Exception:
        pass

def main():
    configure_utf8_io()
    root = Path(__file__).resolve().parent
    ensure_git_hooks(root)

    args = sys.argv[1:]
    mode = "debug"
    pause_on_exit = True

    if "nopause" in args:
        pause_on_exit = False
        args.remove("nopause")

    extra_args: list[str] = []
    if args:
        mode = args[0].lower()
        extra_args = args[1:]

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

    valid_modes = {
        "debug", "fast", "release", "publish", "aot", "restore",
        "clean", "test", "aot-debug", "aot-test", "asm-audit", "pre-commit"
    }
    if mode not in valid_modes:
        print(f"{Color.RED}[ERROR] Unknown build mode '{mode}'.{Color.RESET}")
        print("Available: aot-debug | aot-test | asm-audit | clean | debug | fast | pre-commit | publish (aot) | release | restore | test\n")
        sys.exit(1)

    try:
        if mode == "clean":
            clean_tree(root)

        elif mode == "restore":
            run_step(["dotnet", "restore", "LMP.sln", "-p:Configuration=Debug", "--force", "--force-evaluate"], root, "Restoring Debug dependencies")
            run_step(["dotnet", "restore", "LMP.csproj", "-p:Configuration=Release", "-r", rid, "--force", "--force-evaluate"], root, "Restoring Release AOT dependencies")
            run_step([
                "dotnet", "restore", "LMP.csproj",
                "-p:Configuration=Debug",
                "-r", rid,
                "-p:PublishAot=true",
                "-p:NuGetLockFilePath=packages.DebugAot.lock.json",
                "--force", "--force-evaluate"
            ], root, "Restoring DebugAot dependencies")

        elif mode == "debug":
            run_step(["dotnet", "build", "LMP.sln", "-c", "Debug"] + extra_args, root, "Building Debug configuration")

        elif mode == "fast":
            skip_build = any(a.lower() in ("--no-build", "nobuild") for a in extra_args)
            fwd_args = [a for a in extra_args if a.lower() not in ("--no-build", "nobuild")]
            exe_path = get_debug_executable(root)
            if not skip_build or not exe_path.is_file():
                run_step(["dotnet", "build", "LMP.sln", "-c", "Debug"], root, "Building Debug configuration")
            run_step([str(exe_path), "--fast"] + fwd_args, root, "Launching LMP in Fast Startup Mode (--fast)")

        elif mode == "release":
            run_step([
                "dotnet", "build", "LMP.sln", "-c", "Release",
                "-p:DebugType=None",
                "-p:DebugSymbols=false"
            ], root, "Building Release configuration")

        elif mode in ("publish", "aot"):
            publish_dir = root / "publish"
            if publish_dir.exists():
                shutil.rmtree(publish_dir, ignore_errors=True)

            avalonia_cache = root / "obj" / "Release" / "net11.0" / rid / "Avalonia"
            if avalonia_cache.exists():
                shutil.rmtree(avalonia_cache, ignore_errors=True)

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

            for ext in ("*.xml", "*.pdb", "*.config"):
                for leftover in publish_dir.glob(ext):
                    leftover.unlink(missing_ok=True)

            print(f"\n{Color.GREEN}✓ Native AOT publish completed successfully: ./publish{Color.RESET}")

        elif mode == "aot-debug":
            exe_path = publish_debug_aot(root, rid)
            print(f"\n{Color.GREEN}✓ Debug Native AOT publish completed successfully: {exe_path}{Color.RESET}")

        elif mode == "aot-test":
            test_args, skip_build = normalize_test_args(extra_args)
            exe_path = get_debug_aot_executable(root)
            if not skip_build or not exe_path.is_file():
                exe_path = publish_debug_aot(root, rid)
            run_step([str(exe_path)] + test_args, root, f"Running LMP Tests under Native AOT ({' '.join(test_args)})")

        elif mode == "test":
            test_args, skip_build = normalize_test_args(extra_args)
            exe_path = get_debug_executable(root)
            if not skip_build or not exe_path.is_file():
                run_step(["dotnet", "build", "LMP.sln", "-c", "Debug"], root, "Building Debug configuration")
            run_step([str(exe_path)] + test_args, root, f"Running LMP Tests ({' '.join(test_args)})")

        elif mode == "pre-commit":
            run_step([
                "dotnet", "build", "LMP.sln",
                "-c", "Debug",
                "--nologo",
                "-v", "q"
            ], root, "Pre-commit Gate 1/3: Building Debug configuration")

            exe_path = get_debug_executable(root)
            run_step([
                str(exe_path),
                "--test",
                "--test-category=Unit"
            ], root, "Pre-commit Gate 2/3: Running isolated Unit test suite")

            run_step([
                "dotnet", "build", "LMP.sln",
                "-c", "Release",
                "--no-restore",
                "--nologo",
                "-v", "q",
                "-p:DebugType=None",
                "-p:DebugSymbols=false"
            ], root, "Pre-commit Gate 3/3: Verifying Release compilation")

        elif mode == "asm-audit":
            asm_tool = root / "Tools" / "audit_asm.py"
            run_step([sys.executable, str(asm_tool)] + extra_args, root, "Running Native AOT Binary Assembler Linter (LMP.AsmAudit)")

        print(f"\n{Color.GREEN}✓ Completed successfully{Color.RESET}\n")

    except KeyboardInterrupt:
        print(f"\n{Color.YELLOW}[ABORTED] Interrupted by user.{Color.RESET}")
        sys.exit(130)

    if pause_on_exit and sys.platform.startswith("win") and mode not in ("test", "fast", "aot-test", "asm-audit", "pre-commit"):
        try:
            os.system("pause")
        except Exception:
            pass

if __name__ == "__main__":
    main()