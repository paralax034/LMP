#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
LMP Solution Dependency Verification & Upgrade Pipeline.
Queries NuGet v3 Flat-Container API, inspects Central Package Management
(Directory.Packages.props), updates versions, and synchronizes all lock files.
"""

import sys
import re
import json
import urllib.request
import subprocess
from pathlib import Path
from typing import Optional, Tuple, List, Dict

tools_dir = Path(__file__).resolve().parent
if str(tools_dir) not in sys.path:
    sys.path.insert(0, str(tools_dir))

from common import Color, get_project_root, print_banner

def parse_semver(v: str) -> Optional[Tuple[int, ...]]:
    clean = re.split(r'[-+]', v)[0].strip()
    parts = clean.split('.')
    try:
        return tuple(int(p) for p in parts)
    except ValueError:
        return None

def get_latest_nuget_version(package_id: str, major_constraint: str = "") -> Optional[str]:
    url = f"https://api.nuget.org/v3-flatcontainer/{package_id.lower()}/index.json"
    req = urllib.request.Request(url, headers={"User-Agent": "LMP-Dependency-Tool"})
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            data = json.loads(resp.read().decode('utf-8'))
            versions = data.get("versions", [])
    except Exception as ex:
        print(f"{Color.YELLOW}[WARN] Failed to query NuGet for {package_id}: {ex}{Color.RESET}")
        return None

    stable_versions: List[Tuple[Tuple[int, ...], str]] = []
    for ver_str in versions:
        if '-' in ver_str:
            continue
        parsed = parse_semver(ver_str)
        if not parsed:
            continue
        if major_constraint and str(parsed[0]) != major_constraint:
            continue
        stable_versions.append((parsed, ver_str))

    if not stable_versions:
        return None

    stable_versions.sort(key=lambda x: x[0])
    return stable_versions[-1][1]

def pkg_entry(pkg_id: str, major: str = "") -> Dict[str, str]:
    escaped = re.escape(pkg_id)
    return {
        "id": pkg_id,
        "major": major,
        "pattern": rf'(<PackageVersion\s+Include="{escaped}"\s+Version=")([^"]+)(")',
        "fmt": r'\g<1>{0}\g<3>'
    }

def main():
    dry_run = "--dry-run" in sys.argv
    root = get_project_root()
    props_file = root / "Directory.Packages.props"

    if not props_file.exists():
        print(f"{Color.RED}[ERROR] Directory.Packages.props not found in: {root}{Color.RESET}")
        sys.exit(1)

    print_banner(
        "LMP Dependency Verification & Upgrade Pipeline (CPM)",
        {
            "Packages File": props_file,
            "Mode": "Dry Run (Inspection Only)" if dry_run else "Active Upgrade + Restore"
        }
    )

    print(f"{Color.YELLOW}>>> Querying NuGet v3 Flat-Container API for all dependencies...{Color.RESET}\n")

    targets: List[Dict[str, str]] = [
        {
            "id": "Avalonia",
            "major": "12",
            "pattern": r'(<AvaloniaVersion>)([^<]+)(</AvaloniaVersion>)',
            "fmt": r'\g<1>{0}\g<3>'
        },
        pkg_entry("AsyncImageLoader.Avalonia", "3"),
        pkg_entry("Xaml.Behaviors.Avalonia", "12"),
        pkg_entry("CommunityToolkit.Mvvm", "8"),
        pkg_entry("Microsoft.Extensions.DependencyInjection", "10"),
        pkg_entry("Microsoft.Data.Sqlite", "10"),
        pkg_entry("System.Security.Cryptography.ProtectedData", "10"),
        pkg_entry("MemoryPack", "1"),
        pkg_entry("Concentus", "2"),
        pkg_entry("Concentus.Native", "1"),
        pkg_entry("Acornima", "1"),
        pkg_entry("Tmds.DBus.Protocol", ""),
        pkg_entry("Iced", "1"),
    ]

    props_text = props_file.read_text(encoding="utf-8")

    print(f"  {'PACKAGE':<44} {'CURRENT':<12} {'LATEST':<12} STATUS")
    print(f"  {'-' * 44} {'-' * 12} {'-' * 12} {'-' * 9}")

    has_changes = False
    upgrade_count = 0

    for t in targets:
        pkg_id = t["id"]
        latest = get_latest_nuget_version(pkg_id, t["major"])
        if not latest:
            continue

        match = re.search(t["pattern"], props_text)
        current = match.group(2).strip() if match else "Unknown"

        is_outdated = (current != "Unknown") and (current != latest)
        status_text = "UPGRADE" if is_outdated else "OK"
        status_color = Color.YELLOW if is_outdated else Color.GREEN

        print(f"  {pkg_id:<44} {current:<12} {latest:<12} {status_color}[{status_text}]{Color.RESET}")

        if is_outdated:
            has_changes = True
            upgrade_count += 1
            if not dry_run:
                props_text = re.sub(t["pattern"], t["fmt"].format(latest), props_text)

    print()

    if not has_changes:
        print(f"{Color.GREEN}✓ All solution dependencies in Directory.Packages.props are up to date!{Color.RESET}\n")
        sys.exit(0)

    if dry_run:
        print(f"{Color.YELLOW}[DryRun] {upgrade_count} package(s) can be upgraded. Run without --dry-run to apply.{Color.RESET}\n")
        sys.exit(0)

    print(f"{Color.YELLOW}>>> Writing updated versions to Directory.Packages.props...{Color.RESET}")
    props_file.write_text(props_text, encoding="utf-8")
    print(f"  {Color.GREEN}✓ Updated: Directory.Packages.props{Color.RESET}\n")

    print(f"{Color.YELLOW}>>> Synchronizing dependency graph and lock files (Debug / Release / DebugAot)...{Color.RESET}")
    restore_steps = [
        ["dotnet", "restore", "LMP.sln", "-p:Configuration=Debug", "--force-evaluate"],
        ["dotnet", "restore", "LMP.csproj", "-p:Configuration=Release", "-r", "win-x64", "--force-evaluate"],
        [
            "dotnet", "restore", "LMP.csproj",
            "-p:Configuration=Debug",
            "-r", "win-x64",
            "-p:PublishAot=true",
            "-p:NuGetLockFilePath=packages.DebugAot.lock.json",
            "--force-evaluate"
        ]
    ]

    for cmd in restore_steps:
        res = subprocess.run(cmd, cwd=str(root))
        if res.returncode != 0:
            print(f"{Color.RED}[ERROR] 'dotnet restore' failed on command: {' '.join(cmd)}{Color.RESET}")
            sys.exit(1)

    print()
    print(f"{Color.GRAY}=============================================================={Color.RESET}")
    print(f"{Color.GREEN} Successfully updated {upgrade_count} package(s) and synchronized all lock files!{Color.RESET}")
    print(f"{Color.GRAY}=============================================================={Color.RESET}\n")

if __name__ == "__main__":
    main()