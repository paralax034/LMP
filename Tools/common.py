#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
LMP Build & Diagnostic Tooling Common Library.
Shared environment resolution, ANSI terminal formatting, and CLI aesthetics.
"""

import sys
from pathlib import Path
from typing import Optional, Dict

class Color:
    RED = '\033[91m'
    GREEN = '\033[92m'
    YELLOW = '\033[93m'
    BLUE = '\033[94m'
    MAGENTA = '\033[95m'
    CYAN = '\033[96m'
    GRAY = '\033[90m'
    BOLD = '\033[1m'
    RESET = '\033[0m'

if not sys.stdout.isatty():
    for attr in dir(Color):
        if not attr.startswith("__"):
            setattr(Color, attr, "")

def get_project_root() -> Path:
    """
    Traverses parent directories to locate the repository root
    identified by LMP.sln, LMP.csproj, or Assets/Localization.
    """
    current = Path(__file__).resolve().parent
    for _ in range(5):
        if (current / "LMP.sln").exists() or (current / "LMP.csproj").exists():
            return current
        if (current / "Assets" / "Localization").exists():
            return current
        current = current.parent
    return Path.cwd()

def print_banner(title: str, metadata: Optional[Dict[str, object]] = None) -> None:
    """Prints a consistent, professional header across all tooling scripts."""
    print()
    print(f"{Color.GRAY}=============================================================={Color.RESET}")
    print(f"{Color.CYAN}{Color.BOLD} {title}{Color.RESET}")
    print(f"{Color.GRAY}=============================================================={Color.RESET}")
    if metadata:
        for key, value in metadata.items():
            print(f"{Color.GRAY}  {key:<14}: {value}{Color.RESET}")
        print()