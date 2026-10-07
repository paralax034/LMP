#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
LMP Localization JSON Sorter & Formatter.
Sorts localization keys alphabetically and groups them by prefix with clean blank line separation.
"""

import sys
import re
from pathlib import Path
from typing import Dict

tools_dir = Path(__file__).resolve().parent
if str(tools_dir) not in sys.path:
    sys.path.insert(0, str(tools_dir))

from common import Color, get_project_root, print_banner

RE_KEY_VALUE = re.compile(r'^\s*"([^"]+)"\s*:\s*"((?:[^"\\]|\\.)*)"')

def sort_localization_file(file_path: Path):
    print(f"  {Color.YELLOW}Sorting {file_path.name}...{Color.RESET}")

    lines = file_path.read_text(encoding="utf-8").splitlines()
    dictionary: Dict[str, str] = {}

    for line in lines:
        m = RE_KEY_VALUE.match(line)
        if m:
            dictionary[m.group(1)] = m.group(2)

    sorted_keys = sorted(dictionary.keys())
    output_lines = ["{"]
    previous_prefix = ""
    total = len(sorted_keys)

    for idx, key in enumerate(sorted_keys, start=1):
        prefix = key.split("_")[0] if "_" in key else "Other"

        if previous_prefix and prefix != previous_prefix:
            output_lines.append("")
        previous_prefix = prefix

        comma = "," if idx < total else ""
        output_lines.append(f'  "{key}": "{dictionary[key]}"{comma}')

    output_lines.append("}\n")
    file_path.write_text("\n".join(output_lines), encoding="utf-8")
    print(f"  {Color.GREEN}[OK] Successfully sorted: {file_path.name} ({total} keys){Color.RESET}")

def main():
    root = get_project_root()
    target_dir = root / "Assets" / "Localization"

    if len(sys.argv) > 1:
        custom_path = Path(sys.argv[1])
        target_dir = custom_path if custom_path.is_absolute() else (root / custom_path)

    if not target_dir.exists():
        print(f"{Color.RED}[ERROR] Localization directory not found: {target_dir}{Color.RESET}")
        sys.exit(1)

    json_files = sorted(target_dir.glob("*.json"))
    if not json_files:
        print(f"{Color.RED}[ERROR] No JSON files found in directory: {target_dir}{Color.RESET}")
        sys.exit(1)

    print_banner("LMP Localization Sorter & Formatter", {"Directory": target_dir})

    for jf in json_files:
        sort_localization_file(jf)

    print()
    print(f"{Color.GRAY}=============================================================={Color.RESET}")
    print(f"{Color.GREEN} All localization dictionaries sorted and formatted successfully!{Color.RESET}")
    print(f"{Color.GRAY}=============================================================={Color.RESET}\n")

if __name__ == "__main__":
    main()