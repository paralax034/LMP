#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
LMP Standalone Dev Build & Release Publisher.
Implements dual-channel release publishing on GitHub:
1. Floating release 'dev' with static links to *-latest.7z.
2. Versioned snapshots 'dev-<commitCount>' for rollback and history.
"""

import sys
import os
import shutil
import subprocess
import urllib.request
import zipfile
import json
from pathlib import Path
from datetime import datetime

tools_dir = Path(__file__).resolve().parent
if str(tools_dir) not in sys.path:
    sys.path.insert(0, str(tools_dir))

from common import Color, get_project_root, print_banner

def resolve_7zip() -> str:
    candidates = [
        shutil.which("7z"),
        r"C:\Program Files\7-Zip\7z.exe",
        os.path.expandvars(r"%ProgramFiles%\7-Zip\7z.exe"),
        os.path.expandvars(r"%LOCALAPPDATA%\Programs\7-Zip\7z.exe"),
        r"C:\Program Files (x86)\7-Zip\7z.exe"
    ]
    for c in candidates:
        if c and os.path.isfile(c):
            return c
    print(f"{Color.RED}[ERROR] 7-Zip executable (7z.exe) not found. Please install 7-Zip or add it to PATH.{Color.RESET}")
    sys.exit(1)

def resolve_gh() -> str:
    gh_cmd = shutil.which("gh")
    if gh_cmd:
        return gh_cmd

    local_gh = Path(os.path.expandvars(r"%LOCALAPPDATA%\Programs\gh\gh.exe"))
    if local_gh.is_file():
        return str(local_gh)

    print(f"{Color.YELLOW}>>> Downloading GitHub CLI portable release...{Color.RESET}")
    dest_dir = local_gh.parent
    dest_dir.mkdir(parents=True, exist_ok=True)

    temp_zip = Path(os.path.expandvars(r"%TEMP%\gh_portable.zip"))
    temp_extract = Path(os.path.expandvars(r"%TEMP%\gh_extract"))

    try:
        req = urllib.request.Request(
            "https://api.github.com/repos/cli/cli/releases/latest",
            headers={"User-Agent": "LMP-Publish-Script"}
        )
        with urllib.request.urlopen(req, timeout=15) as resp:
            meta = json.loads(resp.read().decode('utf-8'))

        asset = next((a for a in meta.get("assets", []) if "_windows_amd64.zip" in a.get("name", "")), None)
        if not asset:
            print(f"{Color.RED}[ERROR] Failed to locate Windows asset in GitHub CLI releases.{Color.RESET}")
            sys.exit(1)

        urllib.request.urlretrieve(asset["browser_download_url"], temp_zip)
        with zipfile.ZipFile(temp_zip, 'r') as z:
            z.extractall(temp_extract)

        found = next(temp_extract.rglob("gh.exe"), None)
        if not found:
            print(f"{Color.RED}[ERROR] gh.exe was not found inside the downloaded archive.{Color.RESET}")
            sys.exit(1)

        shutil.copy2(found, local_gh)
        print(f"{Color.GREEN}✓ GitHub CLI installed to {local_gh}{Color.RESET}")
        return str(local_gh)
    finally:
        if temp_zip.exists():
            temp_zip.unlink(missing_ok=True)
        if temp_extract.exists():
            shutil.rmtree(temp_extract, ignore_errors=True)

def run_cmd(cmd: list, cwd: Path, error_msg: str):
    res = subprocess.run(cmd, cwd=str(cwd), shell=(os.name == 'nt' and cmd[0].endswith('.bat')))
    if res.returncode != 0:
        print(f"{Color.RED}[ERROR] {error_msg} (Exit code: {res.returncode}){Color.RESET}")
        sys.exit(res.returncode)

def main():
    skip_build = "--skip-build" in sys.argv
    release_only = "--release-only" in sys.argv
    retention_count = 10

    for arg in sys.argv:
        if arg.startswith("--retention="):
            try:
                retention_count = int(arg.split("=")[1])
            except ValueError:
                pass

    root = get_project_root()
    artifacts_dir = root / "publish-dev"
    if artifacts_dir.exists():
        shutil.rmtree(artifacts_dir, ignore_errors=True)
    artifacts_dir.mkdir(parents=True, exist_ok=True)

    seven_zip = resolve_7zip()
    gh = resolve_gh()

    auth_check = subprocess.run([gh, "auth", "status"], capture_output=True, text=True)
    if auth_check.returncode != 0:
        print(f"{Color.YELLOW}Authentication required. Running 'gh auth login'...{Color.RESET}")
        subprocess.run([gh, "auth", "login"], cwd=str(root))

    repo_slug = ""
    try:
        repo_json = subprocess.check_output([gh, "repo", "view", "--json", "owner,name"], cwd=str(root), text=True)
        repo_data = json.loads(repo_json)
        repo_slug = f"{repo_data['owner']['login']}/{repo_data['name']}"
    except Exception:
        repo_slug = "paralax034/LMP"

    commit_count = subprocess.check_output(["git", "rev-list", "--count", "HEAD"], cwd=str(root), text=True).strip()
    short_hash = subprocess.check_output(["git", "rev-parse", "--short=7", "HEAD"], cwd=str(root), text=True).strip()
    full_version = f"{commit_count}-{short_hash}"
    history_tag = f"dev-{commit_count}"
    latest_tag = "dev"
    source_env = "CI Runner" if os.getenv("GITHUB_ACTIONS") else "Local Workstation"

    print_banner(
        f"LMP Dev Deployment Pipeline: v{full_version}",
        {
            "Snapshot Tag": history_tag,
            "Floating Tag": latest_tag,
            "Release Only": release_only,
            "Skip Build": skip_build
        }
    )

    build_script = root / "build.bat"
    if not skip_build:
        if not release_only:
            print(f"{Color.YELLOW}>>> Compiling Debug configuration...{Color.RESET}")
            run_cmd([str(build_script), "debug", "nopause"], root, "Debug compilation failed")

        print(f"{Color.YELLOW}>>> Compiling Release (Native AOT publish)...{Color.RESET}")
        run_cmd([str(build_script), "publish", "nopause"], root, "Native AOT publish failed")

    publish_dir = root / "publish"
    debug_dir = root / "bin" / "Debug" / "net11.0"

    versioned_release_7z = artifacts_dir / f"LMP-Release-v{full_version}.7z"
    print(f"{Color.YELLOW}>>> Packing Release package (LZMA2 Ultra, 64MB dict)...{Color.RESET}")
    run_cmd([seven_zip, "a", "-t7z", "-m0=lzma2", "-mx=9", "-md=64m", "-mfb=64", "-ms=on", "-mmt=8", str(versioned_release_7z), f"{publish_dir}/*"], root, "Release archiving failed")

    versioned_files = [str(versioned_release_7z)]
    versioned_debug_7z = artifacts_dir / f"LMP-Debug-v{full_version}.7z"

    if not release_only and debug_dir.exists():
        print(f"{Color.YELLOW}>>> Packing Debug package (LZMA2 Fast, 16MB dict)...{Color.RESET}")
        run_cmd([seven_zip, "a", "-t7z", "-m0=lzma2", "-mx=5", "-md=16m", "-ms=on", "-mmt=8", str(versioned_debug_7z), f"{debug_dir}/*"], root, "Debug archiving failed")
        versioned_files.append(str(versioned_debug_7z))

    latest_release_7z = artifacts_dir / "LMP-Release-latest.7z"
    shutil.copy2(versioned_release_7z, latest_release_7z)
    latest_files = [str(latest_release_7z)]

    if versioned_debug_7z.exists():
        latest_debug_7z = artifacts_dir / "LMP-Debug-latest.7z"
        shutil.copy2(versioned_debug_7z, latest_debug_7z)
        latest_files.append(str(latest_debug_7z))

    history_notes = (
        f"**Snapshot dev build for testing and rollback**\n\n"
        f"| Parameter | Value |\n|---|---|\n"
        f"| Version | `{full_version}` |\n"
        f"| Architecture | Native AOT (win-x64) |\n"
        f"| Tag | `{history_tag}` |\n"
        f"| Commit | `{short_hash}` |\n"
        f"| Total Commits | {commit_count} |\n"
        f"| Environment | {source_env} |\n\n"
        f"Latest rolling release is permanently accessible at [`{latest_tag}`](https://github.com/{repo_slug}/releases/tag/{latest_tag}).\n"
    )

    latest_notes = (
        f"# 🎵 LMP Dev Build (Latest)\n\n"
        f"Automated rolling pre-release based on **Native AOT**. Asset URLs in this release remain permanent.\n\n"
        f"| Parameter | Value |\n|---|---|\n"
        f"| Latest Version | `{full_version}` |\n"
        f"| Architecture | Native AOT (win-x64) |\n"
        f"| Commit Number | `{commit_count}` |\n"
        f"| Snapshot Tag | [`{history_tag}`](https://github.com/{repo_slug}/releases/tag/{history_tag}) |\n"
        f"| Environment | {source_env} |\n"
        f"| Updated At | {datetime.utcnow().strftime('%Y-%m-%d %H:%M UTC')} |\n\n"
        f"### 📥 Permanent Download Links:\n"
        f"- **[Download LMP-Release-latest.7z](https://github.com/{repo_slug}/releases/download/{latest_tag}/LMP-Release-latest.7z)** — Native AOT Standalone (Recommended)\n"
        f"- **[Download LMP-Debug-latest.7z](https://github.com/{repo_slug}/releases/download/{latest_tag}/LMP-Debug-latest.7z)** — Debug build with logging console\n"
    )

    print(f"{Color.GREEN}>>> Publishing snapshot release '{history_tag}'...{Color.RESET}")
    rel_check = subprocess.run([gh, "release", "view", history_tag], capture_output=True, text=True)
    if rel_check.returncode == 0:
        for vf in versioned_files:
            subprocess.run([gh, "release", "upload", history_tag, vf, "--clobber"], cwd=str(root))
        subprocess.run([gh, "release", "edit", history_tag, "--title", f"Dev Build v{full_version}", "--notes", history_notes, "--prerelease"], cwd=str(root))
    else:
        create_cmd = [gh, "release", "create", history_tag] + versioned_files + ["--title", f"Dev Build v{full_version}", "--prerelease", "--notes", history_notes]
        subprocess.run(create_cmd, cwd=str(root))

    print(f"{Color.GREEN}>>> Updating rolling release '{latest_tag}'...{Color.RESET}")
    latest_check = subprocess.run([gh, "release", "view", latest_tag], capture_output=True, text=True)
    if latest_check.returncode == 0:
        for lf in latest_files:
            subprocess.run([gh, "release", "upload", latest_tag, lf, "--clobber"], cwd=str(root))
        subprocess.run([gh, "release", "edit", latest_tag, "--title", "LMP Dev Build (Latest)", "--notes", latest_notes, "--prerelease"], cwd=str(root))
    else:
        create_cmd = [gh, "release", "create", latest_tag] + latest_files + ["--title", "LMP Dev Build (Latest)", "--prerelease", "--notes", latest_notes]
        subprocess.run(create_cmd, cwd=str(root))

    print(f"{Color.GRAY}>>> Checking history rotation (retaining last {retention_count} snapshots)...{Color.RESET}")
    try:
        rel_list_out = subprocess.check_output([gh, "release", "list", "--limit", "100", "--json", "tagName"], cwd=str(root), text=True)
        rel_list = json.loads(rel_list_out)
        snapshots = [r["tagName"] for r in rel_list if re.match(r'^dev-\d+$', r.get("tagName", ""))]
        if len(snapshots) > retention_count:
            to_delete = snapshots[retention_count:]
            for tag in to_delete:
                print(f"{Color.YELLOW}>>> Pruning obsolete snapshot release: {tag}{Color.RESET}")
                subprocess.run([gh, "release", "delete", tag, "-y", "--cleanup-tag"], cwd=str(root))
    except Exception as ex:
        print(f"{Color.GRAY}[WARN] History rotation skipped: {ex}{Color.RESET}")

    print()
    print(f"{Color.GRAY}=============================================================={Color.RESET}")
    print(f"{Color.GREEN} Releases published successfully!{Color.RESET}")
    print(f" Latest : https://github.com/{repo_slug}/releases/tag/{latest_tag}")
    print(f" History: https://github.com/{repo_slug}/releases/tag/{history_tag}")
    print(f"{Color.GRAY}=============================================================={Color.RESET}\n")

if __name__ == "__main__":
    main()