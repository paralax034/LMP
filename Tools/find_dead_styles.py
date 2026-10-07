#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
LMP Avalonia 12 Style Audit & Similarity Scanner.
Identifies dead styles, control type mismatches, empty blocks, template duplicates, and cloned styles.
"""

import sys
import re
from pathlib import Path
from typing import Set, Dict, List

tools_dir = Path(__file__).resolve().parent
if str(tools_dir) not in sys.path:
    sys.path.insert(0, str(tools_dir))

from common import Color, get_project_root, print_banner

EXCLUDED_DIRS = {'bin', 'obj', 'External', 'Docs', 'Assets', 'Tests'}

SYSTEM_BUILTINS = {
    "pointerover", "pressed", "disabled", "focus", "focus-visible", "selected",
    "checked", "indeterminate", "open", "empty", "active", "loaded", "dragging",
    "drop-target", "visible", "loading", "error", "success", "header-strip",
    "horizontal", "vertical", "left", "right", "top", "bottom"
}

class ParsedStyle:
    def __init__(self, file: str, line: int, selector: str, target_type: str, classes: List[str], setters: Dict[str, str], has_animations: bool):
        self.file = file
        self.line = line
        self.selector = selector
        self.target_type = target_type
        self.classes = classes
        self.setters = setters
        self.has_template = "/template/" in selector
        self.has_animations = has_animations
        self.is_empty = (len(setters) == 0) and not has_animations

    def get_signature(self) -> str:
        sorted_pairs = [f"{k}={self.setters[k]}" for k in sorted(self.setters.keys())]
        return ";".join(sorted_pairs)

def is_excluded(path: Path) -> bool:
    return any(part in EXCLUDED_DIRS for part in path.parts)

def main():
    similarity_threshold = 0.75
    if len(sys.argv) > 1:
        try:
            similarity_threshold = float(sys.argv[1])
        except ValueError:
            pass

    root = get_project_root()

    axaml_files = [p for p in root.rglob("*.axaml") if not is_excluded(p)]
    cs_files = [p for p in root.rglob("*.cs") if not is_excluded(p)]

    print_banner(
        "LMP Avalonia 12 Style Audit & Similarity Scanner",
        {
            "Solution Root": root,
            "Similarity": f"{int(similarity_threshold * 100)}%"
        }
    )

    used_classes: Set[str] = set(SYSTEM_BUILTINS)
    class_to_types: Dict[str, Set[str]] = {}

    re_axaml_class = re.compile(r'<(?:\w+:)?([A-Za-z0-9_]+)\b[^>]*?\bClasses="([^"]+)"')
    re_axaml_bool_class = re.compile(r'\bClasses\.([A-Za-z0-9_-]+)\s*=')
    re_cs_classes = [
        re.compile(r'\.Classes\.Add\(\s*"([^"]+)"\s*\)'),
        re.compile(r'\.Classes\.Remove\(\s*"([^"]+)"\s*\)'),
        re.compile(r'\.Classes\.Set\(\s*"([^"]+)"\s*,'),
        re.compile(r'\.Classes\s*=\s*"([^"]+)"')
    ]

    for p in axaml_files:
        try:
            content = p.read_text(encoding="utf-8")
        except Exception:
            continue

        for m in re_axaml_class.finditer(content):
            ctrl_type, classes_str = m.group(1), m.group(2)
            for cls in classes_str.split():
                cls = cls.strip()
                if cls:
                    used_classes.add(cls)
                    class_to_types.setdefault(cls, set()).add(ctrl_type)

        for m in re_axaml_bool_class.finditer(content):
            cls = m.group(1).strip()
            if cls:
                used_classes.add(cls)

    for p in cs_files:
        try:
            content = p.read_text(encoding="utf-8")
        except Exception:
            continue

        for reg in re_cs_classes:
            for m in reg.finditer(content):
                for cls in m.group(1).split():
                    cls = cls.strip()
                    if cls:
                        used_classes.add(cls)

    all_styles: List[ParsedStyle] = []
    re_style_block = re.compile(r'<Style\s+([^>]*?)>(.*?)</Style>|<Style\s+([^>]*?)\s*/>', re.DOTALL)
    re_selector = re.compile(r'Selector\s*=\s*"([^"]+)"')
    re_setter = re.compile(r'<Setter\s+Property\s*=\s*"([^"]+)"(?:\s+Value\s*=\s*"([^"]*?)")?')

    for p in axaml_files:
        try:
            content = p.read_text(encoding="utf-8")
        except Exception:
            continue

        rel_path = str(p.relative_to(root))

        for m in re_style_block.finditer(content):
            attrs = m.group(1) or m.group(3) or ""
            body = m.group(2) or ""

            sel_m = re_selector.search(attrs)
            if not sel_m:
                continue

            selector = sel_m.group(1).strip()
            line_no = content[:m.start()].count('\n') + 1

            type_m = re.match(r'^([A-Za-z0-9_]+)', selector)
            target_type = type_m.group(1) if type_m else ""

            classes = re.findall(r'\.([A-Za-z0-9_-]+)', selector)

            setters: Dict[str, str] = {}
            for sm in re_setter.finditer(body):
                prop = sm.group(1).strip()
                val = sm.group(2).strip() if sm.group(2) is not None else "Complex"
                setters[prop] = val

            has_animations = ("<Style.Animations>" in body) or ("<Animation" in body)

            all_styles.append(ParsedStyle(rel_path, line_no, selector, target_type, classes, setters, has_animations))

    print(f"  Parsed {len(all_styles)} style declarations across {len(axaml_files)} AXAML files.\n")

    dead_count = 0
    phantom_count = 0
    empty_count = 0
    exact_dup_count = 0
    similar_count = 0

    print(f"{Color.YELLOW}-- Empty Style Blocks (No-Op) ---------------------------------{Color.RESET}")
    for s in all_styles:
        if s.is_empty:
            empty_count += 1
            print(f"  {Color.YELLOW}[EMPTY]{Color.RESET}        {s.file}:{s.line:<4}  Selector: {s.selector}")
    if empty_count == 0:
        print(f"  {Color.GREEN}[OK] No empty styles found.{Color.RESET}")
    print()

    print(f"{Color.RED}-- Dead and Type-Mismatched Styles ----------------------------{Color.RESET}")
    for s in all_styles:
        if not s.classes:
            continue

        is_dead = not any(cls in used_classes for cls in s.classes)
        if is_dead:
            dead_count += 1
            cls_str = ", ".join(s.classes)
            print(f"  {Color.RED}[DEAD STYLE]{Color.RESET}   {s.file}:{s.line:<4}  Class '{cls_str}' is never referenced in code/markup")
            continue

        if s.target_type and s.classes:
            primary_cls = s.classes[0]
            if primary_cls in class_to_types:
                actual_types = class_to_types[primary_cls]
                if actual_types and s.target_type not in actual_types:
                    phantom_count += 1
                    actual_str = ", ".join(sorted(actual_types))
                    print(f"  {Color.MAGENTA}[PHANTOM]{Color.RESET}      {s.file}:{s.line:<4}  Selector targets '{s.target_type}', but class is only used on: [{actual_str}]")
    if dead_count == 0 and phantom_count == 0:
        print(f"  {Color.GREEN}[OK] No dead or phantom styles detected.{Color.RESET}")
    print()

    print(f"{Color.RED}-- Exact Style Duplicates -------------------------------------{Color.RESET}")
    for i in range(len(all_styles)):
        s1 = all_styles[i]
        if s1.is_empty or not s1.setters:
            continue
        sig1 = s1.get_signature()
        for j in range(i + 1, len(all_styles)):
            s2 = all_styles[j]
            if s1.selector == s2.selector and sig1 == s2.get_signature():
                exact_dup_count += 1
                print(f"  {Color.RED}[DUPLICATE]{Color.RESET}   {s2.file}:{s2.line:<4}  100% duplicate of {s1.file}:{s1.line} ('{s1.selector}')")
    if exact_dup_count == 0:
        print(f"  {Color.GREEN}[OK] No exact style duplicates found.{Color.RESET}")
    print()

    print(f"{Color.CYAN}-- Cloned Styles (Similarity >= {int(similarity_threshold * 100)}%) ------------------------{Color.RESET}")
    reported_pairs: Set[str] = set()
    for i in range(len(all_styles)):
        s1 = all_styles[i]
        if len(s1.setters) < 2:
            continue
        for j in range(i + 1, len(all_styles)):
            s2 = all_styles[j]
            if len(s2.setters) < 2 or (s1.file == s2.file and s1.selector == s2.selector):
                continue

            pair_key = "|".join(sorted([f"{s1.file}:{s1.line}", f"{s2.file}:{s2.line}"]))
            if pair_key in reported_pairs:
                continue

            s1_pairs = set(s1.setters.items())
            s2_pairs = set(s2.setters.items())

            intersection = len(s1_pairs & s2_pairs)
            union = len(s1_pairs | s2_pairs)
            if union == 0:
                continue

            sim = intersection / union
            if similarity_threshold <= sim < 1.0:
                reported_pairs.add(pair_key)
                similar_count += 1
                pct = int(sim * 100)
                print(f"  {Color.CYAN}[{pct}% CLONE]{Color.RESET}    {s1.file}:{s1.line:<4}  {s1.selector}")
                print(f"  {Color.GRAY}  SIMILAR TO  {s2.file}:{s2.line:<4}  {s2.selector}{Color.RESET}\n")
    if similar_count == 0:
        print(f"  {Color.GREEN}[OK] No hidden style clones discovered.{Color.RESET}")
    print()

    total_defects = dead_count + phantom_count + empty_count + exact_dup_count

    print(f"{Color.GRAY}--------------------------------------------------------------{Color.RESET}")
    print(f"  Total Scanned Styles       : {len(all_styles)}")
    print(f"  Dead Styles                : {Color.RED if dead_count else Color.GREEN}{dead_count}{Color.RESET}")
    print(f"  Phantom (Type Mismatches)  : {Color.MAGENTA if phantom_count else Color.GREEN}{phantom_count}{Color.RESET}")
    print(f"  Empty Blocks (No-Op)       : {Color.YELLOW if empty_count else Color.GREEN}{empty_count}{Color.RESET}")
    print(f"  Exact Duplicates           : {Color.RED if exact_dup_count else Color.GREEN}{exact_dup_count}{Color.RESET}")
    print(f"  Cloned Styles (>= {int(similarity_threshold * 100)}%)     : {Color.CYAN if similar_count else Color.GREEN}{similar_count}{Color.RESET}\n")

    sys.exit(total_defects)

if __name__ == "__main__":
    main()