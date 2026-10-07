#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
LMP Solution Dependency Verification & Upgrade Pipeline.
Queries NuGet v3 Flat-Container API, inspects all solution packages,
updates project files, and triggers a deterministic dotnet restore.
"""

import sys
import re
import json
import urllib.request
import subprocess
from pathlib import Path
from typing import Optional, Tuple, List, Dict, Any

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

def main():
    dry_run = "--dry-run" in sys.argv
    root = get_project_root()

    main_csproj = root / "LMP.csproj"
    core_csproj = root / "Core" / "LMP.Core.csproj"

    if not main_csproj.exists() or not core_csproj.exists():
        print(f"{Color.RED}[ERROR] Project files not found in root: {root}{Color.RESET}")
        sys.exit(1)

    print_banner(
        "LMP Dependency Verification & Upgrade Pipeline",
        {
            "Solution Root": root,
            "Mode": "Dry Run (Inspection Only)" if dry_run else "Active Upgrade + Restore"
        }
    )

    print(f"{Color.YELLOW}>>> Querying NuGet v3 Flat-Container API for all dependencies...{Color.RESET}\n")

    targets: List[Dict[str, Any]] = [
        {"id": "Avalonia", "major": "12", "target": "Both", "pattern": r'<AvaloniaVersion>([^<]+)</AvaloniaVersion>', "fmt": '<AvaloniaVersion>{0}</AvaloniaVersion>'},
        {"id": "Xaml.Behaviors.Avalonia", "major": "12", "target": "Main", "pattern": r'<XamlBehaviorsVersion>([^<]+)</XamlBehaviorsVersion>', "fmt": '<XamlBehaviorsVersion>{0}</XamlBehaviorsVersion>'},
        {"id": "Avalonia.Controls.ItemsRepeater", "major": "12", "target": "Main", "pattern": r'Include="Avalonia\.Controls\.ItemsRepeater" Version="([^"]+)"', "fmt": 'Include="Avalonia.Controls.ItemsRepeater" Version="{0}"'},
        {"id": "AvaloniaUI.DiagnosticsSupport", "major": "2", "target": "Main", "pattern": r'Include="AvaloniaUI\.DiagnosticsSupport" Version="([^"]+)"', "fmt": 'Include="AvaloniaUI.DiagnosticsSupport" Version="{0}"'},
        {"id": "AsyncImageLoader.Avalonia", "major": "3", "target": "Both", "pattern": r'<AsyncImageLoaderVersion>([^<]+)</AsyncImageLoaderVersion>', "fmt": '<AsyncImageLoaderVersion>{0}</AsyncImageLoaderVersion>'},
        {"id": "SkiaSharp", "major": "4", "target": "Core", "pattern": r'<SkiaSharpVersion>([^<]+)</SkiaSharpVersion>', "fmt": '<SkiaSharpVersion>{0}</SkiaSharpVersion>'},

        {"id": "CommunityToolkit.Mvvm", "major": "8", "target": "Both", "pattern": r'<CommunityToolkitMvvmVersion>([^<]+)</CommunityToolkitMvvmVersion>', "fmt": '<CommunityToolkitMvvmVersion>{0}</CommunityToolkitMvvmVersion>'},
        {"id": "MemoryPack", "major": "1", "target": "Core", "pattern": r'Include="MemoryPack" Version="([^"]+)"', "fmt": 'Include="MemoryPack" Version="{0}"'},

        {"id": "Microsoft.Extensions.DependencyInjection", "major": "10", "target": "Both", "pattern": r'<MsExtVersion>([^<]+)</MsExtVersion>', "fmt": '<MsExtVersion>{0}</MsExtVersion>'},
        {"id": "SQLitePCLRaw.bundle_e_sqlite3", "major": "3", "target": "Core", "pattern": r'Include="SQLitePCLRaw\.bundle_e_sqlite3" Version="([^"]+)"', "fmt": 'Include="SQLitePCLRaw.bundle_e_sqlite3" Version="{0}"'},
        {"id": "Tmds.DBus.Protocol", "major": "", "target": "Main", "pattern": r'Include="Tmds\.DBus\.Protocol" Version="([^"]+)"', "fmt": 'Include="Tmds.DBus.Protocol" Version="{0}"'},

        {"id": "Concentus", "major": "2", "target": "Core", "pattern": r'Include="Concentus" Version="([^"]+)"', "fmt": 'Include="Concentus" Version="{0}"'},
        {"id": "Concentus.Native", "major": "1", "target": "Main", "pattern": r'Include="Concentus\.Native" Version="([^"]+)"', "fmt": 'Include="Concentus.Native" Version="{0}"'},

        {"id": "Acornima", "major": "1", "target": "Core", "pattern": r'Include="Acornima" Version="([^"]+)"', "fmt": 'Include="Acornima" Version="{0}"'}
    ]

    main_text = main_csproj.read_text(encoding="utf-8")
    core_text = core_csproj.read_text(encoding="utf-8")

    print(f"  {'PACKAGE':<36} {'CURRENT':<12} {'LATEST':<12} STATUS")
    print(f"  {'-' * 36} {'-' * 12} {'-' * 12} {'-' * 8}")

    has_changes = False
    upgrade_count = 0

    for t in targets:
        pkg_id = t["id"]
        latest = get_latest_nuget_version(pkg_id, t["major"])
        if not latest:
            continue

        src_text = core_text if t["target"] == "Core" else main_text
        match = re.search(t["pattern"], src_text)
        current = match.group(1) if match else "Unknown"

        is_outdated = (current != "Unknown") and (current != latest)
        status_text = "UPGRADE" if is_outdated else "OK"
        status_color = Color.YELLOW if is_outdated else Color.GREEN

        print(f"  {pkg_id:<36} {current:<12} {latest:<12} {status_color}[{status_text}]{Color.RESET}")

        if is_outdated:
            has_changes = True
            upgrade_count += 1
            if not dry_run:
                replacement = t["fmt"].format(latest)
                if t["target"] in ("Main", "Both"):
                    main_text = re.sub(t["pattern"], replacement, main_text)
                if t["target"] in ("Core", "Both"):
                    core_text = re.sub(t["pattern"], replacement, core_text)

    print()

    if not has_changes:
        print(f"{Color.GREEN}✓ All solution dependencies are completely up to date!{Color.RESET}\n")
        sys.exit(0)

    if dry_run:
        print(f"{Color.YELLOW}[DryRun] {upgrade_count} package(s) can be upgraded. Run 'tool: update-dependencies' to apply.{Color.RESET}\n")
        sys.exit(0)

    print(f"{Color.YELLOW}>>> Writing updated versions to project files...{Color.RESET}")
    main_csproj.write_text(main_text, encoding="utf-8")
    core_csproj.write_text(core_text, encoding="utf-8")
    print(f"  {Color.GREEN}✓ Updated: LMP.csproj{Color.RESET}")
    print(f"  {Color.GREEN}✓ Updated: Core/LMP.Core.csproj{Color.RESET}\n")

    print(f"{Color.YELLOW}>>> Synchronizing dependency graph and packages.lock.json...{Color.RESET}")
    res_debug = subprocess.run(["dotnet", "restore", "LMP.sln", "-p:Configuration=Debug", "--force-evaluate"], cwd=str(root))
    res_release = subprocess.run(["dotnet", "restore", "LMP.csproj", "-p:Configuration=Release", "-r", "win-x64", "--force-evaluate"], cwd=str(root))
    if res_debug.returncode != 0 or res_release.returncode != 0:
        print(f"{Color.RED}[ERROR] 'dotnet restore' failed.{Color.RESET}")
        sys.exit(1)

    print()
    print(f"{Color.GRAY}=============================================================={Color.RESET}")
    print(f"{Color.GREEN} Successfully updated {upgrade_count} package(s) and synchronized lock-file!{Color.RESET}")
    print(f"{Color.GRAY}=============================================================={Color.RESET}\n")

if __name__ == "__main__":
    main()