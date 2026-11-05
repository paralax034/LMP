#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
LMP Native AOT Binary Assembler & Hot-Loop Audit Pipeline.
Publishes Native AOT release with native PDB symbols (EmitAotPdb=true)
and runs Tools/LMP.AsmAudit to verify zero calls, zero divisions, and zero stack spills in DSP hot loops.
"""

import sys
import shutil
import subprocess
from pathlib import Path

tools_dir = Path(__file__).resolve().parent
if str(tools_dir) not in sys.path:
    sys.path.insert(0, str(tools_dir))

from common import Color, get_project_root, print_banner

def main():
    args = sys.argv[1:]
    skip_build = any(a.lower() in ("--skip-build", "--no-build", "nobuild") for a in args)
    linter_args = [a for a in args if a.lower() not in ("--skip-build", "--no-build", "nobuild")]

    root = get_project_root()
    audit_dir = root / "publish-aot-audit"
    exe_path = audit_dir / "LMP.exe"
    pdb_path = audit_dir / "LMP.pdb"
    report_path = root / "AsmAuditReport.md"
    linter_proj = root / "Tools" / "LMP.AsmAudit" / "LMP.AsmAudit.csproj"

    print_banner(
        "LMP Native AOT Binary Assembler Linter (LMP.AsmAudit)",
        {
            "Binary": exe_path,
            "Native PDB": pdb_path,
            "Report": report_path,
            "Skip Build": skip_build
        }
    )

    if not skip_build:
        if audit_dir.exists():
            shutil.rmtree(audit_dir, ignore_errors=True)

        print(f"{Color.YELLOW}>>> [1/2] Publishing Native AOT Release with native MSF 7.0 PDB...{Color.RESET}")
        publish_cmd = [
            "dotnet", "publish", "LMP.csproj",
            "-c", "Release",
            "-r", "win-x64",
            "-p:PublishAot=true",
            "-p:TrimMode=full",
            "-p:EmitAotPdb=true",
            "-o", str(audit_dir)
        ]
        res = subprocess.run(publish_cmd, cwd=str(root))
        if res.returncode != 0:
            print(f"{Color.RED}[ERROR] Native AOT publishing failed with exit code: {res.returncode}{Color.RESET}")
            sys.exit(res.returncode)
        print(f"{Color.GREEN}✓ Native AOT binary and PDB successfully generated.{Color.RESET}\n")
    else:
        print(f"{Color.YELLOW}>>> Skipping Native AOT build (reusing {audit_dir})...{Color.RESET}")
        if not exe_path.is_file() or not pdb_path.is_file():
            print(f"{Color.RED}[ERROR] Binary or PDB not found in {audit_dir}. Run without --skip-build first.{Color.RESET}")
            sys.exit(1)

    print(f"{Color.YELLOW}>>> [2/2] Running Iced.Intel + DbgHelp Hot-Loop Assembler Auditor...{Color.RESET}")
    cmd = [
        "dotnet", "run",
        "--project", str(linter_proj),
        "-c", "Release",
        "--",
        str(exe_path),
        str(pdb_path),
        str(report_path)
    ] + linter_args

    res_audit = subprocess.run(cmd, cwd=str(root))
    sys.exit(res_audit.returncode)

if __name__ == "__main__":
    main()