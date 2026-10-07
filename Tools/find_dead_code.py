#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
LMP Native AOT Dead Code Detection Pipeline.
Publishes Native AOT assembly with DGML dependency graphs and runs the high-throughput AotScanner audit engine.
"""

import sys
import subprocess
from pathlib import Path

tools_dir = Path(__file__).resolve().parent
if str(tools_dir) not in sys.path:
    sys.path.insert(0, str(tools_dir))

from common import Color, get_project_root, print_banner

def main():
    skip_build = "--skip-build" in sys.argv
    root = get_project_root()

    assembly_path = root / "Core" / "bin" / "Release" / "net11.0" / "LMP.Core.dll"
    output_file = root / "DeadCode.txt"
    dgml_path = root / "obj" / "Release" / "net11.0" / "win-x64" / "native" / "LMP.codegen.dgml.xml"
    scanner_script = root / "Tools" / "AotScanner" / "Program.cs"

    print_banner(
        "LMP Native AOT Dead Code Detection Pipeline",
        {
            "Assembly": assembly_path,
            "ILC Graph": dgml_path,
            "Report": output_file,
            "Fast Mode": skip_build
        }
    )

    if not skip_build:
        print(f"{Color.YELLOW}>>> [1/2] Building Native AOT release with DGML graph generation...{Color.RESET}")
        publish_cmd = [
            "dotnet", "publish", "LMP.csproj",
            "-c", "Release",
            "-r", "win-x64",
            "-p:PublishAot=true",
            "-p:TrimMode=full",
            "-p:IlcGenerateDgmlFile=true",
            "-p:IlcGenerateMstatFile=true",
            "-p:IlcFoldIdenticalMethodBodies=false",
            "-o", "./publish"
        ]
        res = subprocess.run(publish_cmd, cwd=str(root))
        if res.returncode != 0:
            print(f"{Color.RED}[ERROR] Native AOT publishing failed with exit code: {res.returncode}{Color.RESET}")
            sys.exit(res.returncode)
        print(f"{Color.GREEN}✓ Native AOT codegen graph successfully generated.{Color.RESET}\n")
    else:
        print(f"{Color.YELLOW}>>> Skipping build (using existing DGML graph)...{Color.RESET}")
        if not dgml_path.exists():
            print(f"{Color.RED}[ERROR] Codegen graph not found: {dgml_path}. Run without --skip-build.{Color.RESET}")
            sys.exit(1)

    if not assembly_path.exists():
        print(f"{Color.RED}[ERROR] Target assembly not found: {assembly_path}{Color.RESET}")
        sys.exit(1)

    print(f"{Color.YELLOW}>>> [2/2] Running two-factor source tree & ILC scanner...{Color.RESET}")
    scanner_cmd = [
        "dotnet", "run",
        "--file", str(scanner_script),
        "--",
        str(assembly_path),
        str(dgml_path),
        str(root),
        str(output_file)
    ]
    res_scanner = subprocess.run(scanner_cmd, cwd=str(root))
    if res_scanner.returncode != 0:
        print(f"{Color.RED}[ERROR] AotScanner terminated with exit code: {res_scanner.returncode}{Color.RESET}")
        sys.exit(res_scanner.returncode)

    print()
    print(f"{Color.GRAY}=============================================================={Color.RESET}")
    print(f"{Color.GREEN} Audit completed successfully! Report written to: {output_file}{Color.RESET}")
    print(f"{Color.GRAY}=============================================================={Color.RESET}\n")

if __name__ == "__main__":
    main()