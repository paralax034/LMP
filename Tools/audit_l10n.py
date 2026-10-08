#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
LMP Localization & UI Hardcode Static Auditor.
Verifies dictionary symmetry, detects duplicate keys with precise line numbers,
detects unauthorized fallback values, missing translation keys, unlocalized text,
and automatically infers dynamic enum & string interpolations without manual whitelists.
Supports automatic dead-key remediation via --fix flag.
"""

import sys
import re
import json
from pathlib import Path
from typing import Dict, Set, List, Tuple

tools_dir = Path(__file__).resolve().parent
if str(tools_dir) not in sys.path:
    sys.path.insert(0, str(tools_dir))

from common import Color, get_project_root, print_banner

RE_CYRILLIC = re.compile(r'[\u0400-\u04FF]')
RE_BINDING_FALLBACK = re.compile(r'\{Binding\s+L\[(?P<key>[^\]]+)\],\s*FallbackValue=(?P<fallback>[^\}]+)\}')
RE_XAML_L_KEY = re.compile(r'\{Binding\s+L\[(?P<key>[a-zA-Z0-9_]+)\]')
RE_CS_L_KEY = re.compile(r'(?:L|SL|LocalizationService\.Instance)\["(?P<key>[a-zA-Z0-9_]+)"\]')
RE_CS_L_FALLBACK_PATTERN = re.compile(r'(?:L|SL|LocalizationService\.Instance)\["[^"]+"\]\s*\?\?\s*(?P<fallback>"[^"]*")')
RE_CS_GET_FALLBACK = re.compile(r'\.Get\(\s*"[^"]+"\s*,\s*(?P<fallback>"[^"]+")\s*\)')
RE_CS_BRACKET_HACK = re.compile(r'\.StartsWith\(\s*\'\[\'\s*\)|\.StartsWith\(\s*"\["\s*\)')
RE_STRING_LITERAL = re.compile(r'"(?P<val>[a-zA-Z0-9_]{3,})"')
RE_INTERPOLATED_PREFIX = re.compile(r'\$"(?P<prefix>[a-zA-Z0-9_]+_)\{')
RE_CREATELIST_PREFIX = re.compile(r'<[a-zA-Z0-9_]+>\s*\(\s*"(?P<prefix>[a-zA-Z0-9_]+_)"\s*\)')
RE_CONCAT_PREFIX = re.compile(r'L\[\s*"(?P<prefix>[a-zA-Z0-9_]+_)"\s*\+')
RE_ENUM_DECLARATION = re.compile(r'enum\s+(?P<name>[a-zA-Z0-9_]+)\s*\{(?P<body>[^}]+)\}', re.MULTILINE)
RE_KEY_LINE = re.compile(r'^\s*"([^"\\]+)"\s*:')

TEXT_ATTRIBUTES = {
    'Text', 'Content', 'ToolTip.Tip', 'PlaceholderText', 'Header',
    'Title', 'Watermark', 'Badge'
}

EXCLUDED_CS_PATTERNS = [
    re.compile(r'Log\.(Info|Debug|Warn|Error|Fatal)\s*\('),
    re.compile(r'Trace\.WriteLine\s*\('),
    re.compile(r'Console\.WriteLine\s*\('),
]

EXCLUDED_CYRILLIC_FILES = {
    "PlayabilityErrorClassifier.cs",
    "LocalizationService.cs",
    "OsNotificationHelper.cs",
    "NotificationService.cs"
}

ALLOWED_IDENTICAL_KEYS = {
    "Common_AppName",
    "Extension_Guide_Chrome_Header",
    "Extension_Guide_Edge_Header",
    "Extension_Guide_Opera_Header",
    "General_Discord",
    "NetProfile_Ultra",
    "Search_Source_YouTubeMusic",
    "Stream_Bitrate",
}

PLURAL_SUFFIXES = ("_0", "_1", "_2", "_3", "_4", "_5", "_one", "_few", "_many", "_other", "_zero")

class AuditReport:
    def __init__(self):
        self.errors: List[str] = []
        self.warnings: List[str] = []
        self.dead_keys: List[str] = []
        self.stats = {
            "en_keys": 0,
            "ru_keys": 0,
            "duplicate_en": 0,
            "duplicate_ru": 0,
            "missing_in_ru": 0,
            "missing_in_en": 0,
            "untranslated_ru": 0,
            "missing_in_code": 0,
            "unused_keys": 0,
            "xaml_fallbacks": 0,
            "cs_fallbacks": 0,
            "hardcoded_cyrillic": 0,
            "hardcoded_ui_english": 0,
        }

    def add_error(self, message: str, stat_key: str = None):
        self.errors.append(message)
        if stat_key:
            self.stats[stat_key] += 1

    def add_warning(self, message: str, stat_key: str = None):
        self.warnings.append(message)
        if stat_key:
            self.stats[stat_key] += 1

def load_json_and_detect_duplicates(file_path: Path, report: AuditReport, stat_key: str) -> Dict[str, str]:
    content = file_path.read_text(encoding="utf-8")
    lines = content.splitlines()

    seen_keys: Dict[str, int] = {}
    for line_no, line in enumerate(lines, start=1):
        m = RE_KEY_LINE.match(line)
        if m:
            key = m.group(1)
            if key in seen_keys:
                report.add_error(
                    f"{Color.RED}[DUPLICATE KEY in {file_path.name}]{Color.RESET} "
                    f"Line {line_no}: Key '{Color.BOLD}{key}{Color.RESET}' was already defined on line {seen_keys[key]}!",
                    stat_key
                )
            else:
                seen_keys[key] = line_no

    try:
        return json.loads(content)
    except Exception as ex:
        report.add_error(f"Failed to parse JSON '{file_path.name}': {ex}")
        return {}

def has_plural_suffix(key: str) -> bool:
    return any(key.endswith(s) for s in PLURAL_SUFFIXES)

def strip_comments_and_strings_cs(content: str) -> List[Tuple[int, str]]:
    lines = content.splitlines()
    result = []
    in_block_comment = False

    for idx, line in enumerate(lines, start=1):
        clean_line = line
        if in_block_comment:
            if "*/" in clean_line:
                clean_line = clean_line.split("*/", 1)[1]
                in_block_comment = False
            else:
                continue

        while "/*" in clean_line:
            before, after = clean_line.split("/*", 1)
            if "*/" in after:
                clean_line = before + after.split("*/", 1)[1]
            else:
                clean_line = before
                in_block_comment = True
                break

        if "//" in clean_line:
            clean_line = clean_line.split("//", 1)[0]

        result.append((idx, clean_line))

    return result

def discover_dynamic_prefixes_and_enums(cs_files: List[Path]) -> Set[str]:
    inferred_prefixes = set()

    for p in cs_files:
        try:
            text = p.read_text(encoding="utf-8")
        except Exception:
            continue

        for m in RE_CREATELIST_PREFIX.finditer(text):
            inferred_prefixes.add(m.group("prefix"))

        for m in RE_CONCAT_PREFIX.finditer(text):
            inferred_prefixes.add(m.group("prefix"))

        for m in RE_INTERPOLATED_PREFIX.finditer(text):
            inferred_prefixes.add(m.group("prefix"))

        for m in RE_ENUM_DECLARATION.finditer(text):
            enum_name = m.group("name")
            inferred_prefixes.add(f"{enum_name}_")

    return inferred_prefixes

def audit_localization_symmetry(en_dict: Dict[str, str], ru_dict: Dict[str, str], report: AuditReport):
    en_keys = set(en_dict.keys())
    ru_keys = set(ru_dict.keys())

    report.stats["en_keys"] = len(en_keys)
    report.stats["ru_keys"] = len(ru_keys)

    missing_in_ru = en_keys - ru_keys
    for k in sorted(missing_in_ru):
        report.add_error(
            f"{Color.RED}[MISSING IN ru.json]{Color.RESET} Key '{Color.BOLD}{k}{Color.RESET}' is declared in en.json but missing in ru.json",
            "missing_in_ru"
        )

    missing_in_en = ru_keys - en_keys
    for k in sorted(missing_in_en):
        report.add_error(
            f"{Color.RED}[MISSING IN en.json]{Color.RESET} Key '{Color.BOLD}{k}{Color.RESET}' is declared in ru.json but missing in en.json",
            "missing_in_en"
        )

    for k in sorted(en_keys.intersection(ru_keys)):
        if k in ALLOWED_IDENTICAL_KEYS:
            continue

        en_val = en_dict[k].strip()
        ru_val = ru_dict[k].strip()
        if en_val == ru_val and len(en_val) > 4 and " " in en_val and not RE_CYRILLIC.search(ru_val):
            report.add_warning(
                f"{Color.YELLOW}[UNTRANSLATED ru.json]{Color.RESET} Key '{Color.BOLD}{k}{Color.RESET}' contains original English text: \"{ru_val}\"",
                "untranslated_ru"
            )

def audit_xaml_file(path: Path, root: Path, all_keys: Set[str], report: AuditReport, used_keys: Set[str]):
    rel_path = path.relative_to(root)
    try:
        content = path.read_text(encoding="utf-8")
    except Exception as ex:
        report.add_error(f"Failed to read {rel_path}: {ex}")
        return

    content_no_comments = re.sub(r'<!--.*?-->', '', content, flags=re.DOTALL)
    lines = content_no_comments.splitlines()

    for line_no, line in enumerate(lines, start=1):
        for match in RE_BINDING_FALLBACK.finditer(line):
            key = match.group("key")
            fallback = match.group("fallback").strip("'\" ")
            report.add_error(
                f"{Color.RED}[XAML FALLBACK]{Color.RESET} {rel_path}:{line_no}: "
                f"Forbidden FallbackValue='{Color.BOLD}{fallback}{Color.RESET}' for key L[{key}]",
                "xaml_fallbacks"
            )

        for match in RE_XAML_L_KEY.finditer(line):
            key = match.group("key")
            used_keys.add(key)
            if key not in all_keys:
                report.add_error(
                    f"{Color.RED}[XAML UNKNOWN KEY]{Color.RESET} {rel_path}:{line_no}: "
                    f"Key '{Color.BOLD}{key}{Color.RESET}' does not exist in any JSON dictionary!",
                    "missing_in_code"
                )

        for m in RE_STRING_LITERAL.finditer(line):
            candidate = m.group("val")
            if candidate in all_keys:
                used_keys.add(candidate)

        if RE_CYRILLIC.search(line) and "FallbackValue" not in line:
            literals = re.findall(r'="([^"]*[\u0400-\u04FF][^"]*)"', line)
            raw_texts = re.findall(r'>([^<]*[\u0400-\u04FF][^<]*)<', line)
            all_found = literals + [t.strip() for t in raw_texts if t.strip()]
            for found in all_found:
                report.add_error(
                    f"{Color.RED}[HARDCODED CYRILLIC XAML]{Color.RESET} {rel_path}:{line_no}: "
                    f"Direct Russian literal: \"{Color.BOLD}{found}{Color.RESET}\"",
                    "hardcoded_cyrillic"
                )

        for attr in TEXT_ATTRIBUTES:
            pattern = rf'{attr}="([^"\{{\}}]+)"'
            for match in re.finditer(pattern, line):
                val = match.group(1).strip()
                if val and not val.isdigit() and len(val) > 2 and not val.startswith("avares://"):
                    if any(known in val for known in ["YouTube", "Search", "Close", "Queue"]):
                        report.add_warning(
                            f"{Color.YELLOW}[UNLOCALIZED XAML ATTR]{Color.RESET} {rel_path}:{line_no}: "
                            f"Attribute {attr}=\"{val}\" is hardcoded without localization",
                            "hardcoded_ui_english"
                        )

def audit_cs_file(path: Path, root: Path, all_keys: Set[str], report: AuditReport, used_keys: Set[str]):
    rel_path = path.relative_to(root)
    file_name = path.name

    try:
        content = path.read_text(encoding="utf-8")
    except Exception as ex:
        report.add_error(f"Failed to read {rel_path}: {ex}")
        return

    cleaned_lines = strip_comments_and_strings_cs(content)

    for line_no, line in cleaned_lines:
        if not line.strip():
            continue

        for match in RE_CS_L_FALLBACK_PATTERN.finditer(line):
            report.add_error(
                f"{Color.RED}[C# OPERATOR ?? FALLBACK]{Color.RESET} {rel_path}:{line_no}: "
                f"Using operator ?? to mask localization lookup: {line.strip()}",
                "cs_fallbacks"
            )

        for match in RE_CS_GET_FALLBACK.finditer(line):
            report.add_error(
                f"{Color.RED}[C# GET() FALLBACK]{Color.RESET} {rel_path}:{line_no}: "
                f"Passing fallback argument to Get() method: {line.strip()}",
                "cs_fallbacks"
            )

        if RE_CS_BRACKET_HACK.search(line):
            report.add_error(
                f"{Color.RED}[C# BRACKET HACK]{Color.RESET} {rel_path}:{line_no}: "
                f"Localization bypass via bracket '[' prefix check: {line.strip()}",
                "cs_fallbacks"
            )

        for match in RE_CS_L_KEY.finditer(line):
            key = match.group("key")
            used_keys.add(key)
            if key not in all_keys:
                report.add_error(
                    f"{Color.RED}[C# UNKNOWN KEY]{Color.RESET} {rel_path}:{line_no}: "
                    f"Key '{Color.BOLD}{key}{Color.RESET}' missing in localization dictionaries!",
                    "missing_in_code"
                )

        for m in RE_STRING_LITERAL.finditer(line):
            candidate = m.group("val")
            if candidate in all_keys:
                used_keys.add(candidate)

        if file_name not in EXCLUDED_CYRILLIC_FILES and RE_CYRILLIC.search(line):
            if not any(excl.search(line) for excl in EXCLUDED_CS_PATTERNS):
                str_literals = re.findall(r'"([^"]*[\u0400-\u04FF][^"]*)"', line)
                if str_literals and "LanguageItem" not in line and "AvailableLanguages" not in line:
                    for s in str_literals:
                        report.add_warning(
                            f"{Color.YELLOW}[HARDCODED CYRILLIC C#]{Color.RESET} {rel_path}:{line_no}: "
                            f"Hardcoded string in code: \"{Color.BOLD}{s}{Color.RESET}\"",
                            "hardcoded_cyrillic"
                        )

def apply_dead_key_cleanup(l10n_dir: Path, dead_keys: List[str]):
    print(f"\n{Color.CYAN}>>> Applying automatic dead key cleanup (--fix)...{Color.RESET}")
    dead_set = set(dead_keys)

    for json_file in sorted(l10n_dir.glob("*.json")):
        lines = json_file.read_text(encoding="utf-8").splitlines()
        cleaned: List[str] = []
        removed = 0

        for line in lines:
            m = re.match(r'^\s*"([^"\\]+)"\s*:', line)
            if m and m.group(1) in dead_set:
                removed += 1
                continue
            cleaned.append(line)

        for i in range(len(cleaned) - 1, -1, -1):
            if cleaned[i].strip() == '}':
                continue
            if cleaned[i].endswith(','):
                cleaned[i] = cleaned[i].rstrip(',')
            break

        json_file.write_text("\n".join(cleaned) + "\n", encoding="utf-8")
        print(f"  {Color.GREEN}✓ Removed {removed} dead keys from {json_file.name}{Color.RESET}")

def main():
    fix_mode = "--fix" in sys.argv
    root = get_project_root()

    print_banner(
        "LMP Localization & Hardcode Static Auditor",
        {
            "Project Root": root,
            "Mode": Color.YELLOW + "Remediation (--fix)" if fix_mode else Color.GRAY + "Static Audit"
        }
    )

    l10n_dir = root / "Assets" / "Localization"
    en_path = l10n_dir / "en.json"
    ru_path = l10n_dir / "ru.json"

    if not en_path.exists() or not ru_path.exists():
        print(f"{Color.RED}[FATAL] Dictionaries not found in {l10n_dir}{Color.RESET}")
        sys.exit(1)

    report = AuditReport()

    print(f"[{Color.BLUE}1/4{Color.RESET}] Scanning JSON dictionaries for duplicate keys and parsing...")
    en_dict = load_json_and_detect_duplicates(en_path, report, "duplicate_en")
    ru_dict = load_json_and_detect_duplicates(ru_path, report, "duplicate_ru")

    all_keys = set(en_dict.keys()).union(set(ru_dict.keys()))
    used_keys: Set[str] = set()

    print(f"[{Color.BLUE}2/4{Color.RESET}] Validating dictionary symmetry...")
    audit_localization_symmetry(en_dict, ru_dict, report)

    source_dirs = [root, root / "UI", root / "Core"]
    xaml_files: List[Path] = []
    cs_files: List[Path] = []

    seen_paths: Set[Path] = set()
    for s_dir in source_dirs:
        if s_dir.exists():
            iterator = s_dir.glob("*.axaml") if s_dir == root else s_dir.rglob("*.axaml")
            for xaml_p in iterator:
                if xaml_p not in seen_paths and "Debug" not in xaml_p.parts and "bin" not in xaml_p.parts and "obj" not in xaml_p.parts:
                    seen_paths.add(xaml_p)
                    xaml_files.append(xaml_p)

            cs_iterator = s_dir.glob("*.cs") if s_dir == root else s_dir.rglob("*.cs")
            for cs_p in cs_iterator:
                if cs_p not in seen_paths and "bin" not in cs_p.parts and "obj" not in cs_p.parts:
                    seen_paths.add(cs_p)
                    cs_files.append(cs_p)

    dynamic_prefixes = discover_dynamic_prefixes_and_enums(cs_files)

    print(f"[{Color.BLUE}3/4{Color.RESET}] Auditing {len(xaml_files)} XAML markup files...")
    for xaml_path in xaml_files:
        audit_xaml_file(xaml_path, root, all_keys, report, used_keys)

    print(f"[{Color.BLUE}4/4{Color.RESET}] Auditing {len(cs_files)} C# source files...")
    for cs_path in cs_files:
        audit_cs_file(cs_path, root, all_keys, report, used_keys)

    for k in sorted(all_keys):
        base_k = re.sub(r'(_0|_1|_2|_5|_other|_few|_many|_one|_zero)$', '', k)

        is_dynamic = any(k.startswith(pfx) or base_k.startswith(pfx) for pfx in dynamic_prefixes)
        if is_dynamic or has_plural_suffix(k):
            continue

        if k not in used_keys and base_k not in used_keys:
            report.dead_keys.append(k)
            report.stats["unused_keys"] += 1

    print(f"\n{Color.CYAN}{Color.BOLD}=== AUDIT SUMMARY ==={Color.RESET}")

    if report.errors:
        print(f"\n{Color.RED}{Color.BOLD}CRITICAL ERRORS ({len(report.errors)}):{Color.RESET}")
        for err in report.errors:
            print(f"  {err}")

    if report.warnings:
        print(f"\n{Color.YELLOW}{Color.BOLD}WARNINGS ({len(report.warnings)}):{Color.RESET}")
        for warn in report.warnings:
            print(f"  {warn}")

    if report.dead_keys:
        print(f"\n{Color.YELLOW}{Color.BOLD}UNUSED / DEAD KEYS ({len(report.dead_keys)}):{Color.RESET}")
        for dk in report.dead_keys:
            print(f"  {Color.YELLOW}• {dk}{Color.RESET}")

    print(f"\n{Color.CYAN}{Color.BOLD}--- STATISTICS ---{Color.RESET}")
    print(f"Keys in en.json:                   {report.stats['en_keys']}")
    print(f"Keys in ru.json:                   {report.stats['ru_keys']}")
    print(f"Duplicate keys in en.json:         {Color.RED if report.stats['duplicate_en'] else Color.GREEN}{report.stats['duplicate_en']}{Color.RESET}")
    print(f"Duplicate keys in ru.json:         {Color.RED if report.stats['duplicate_ru'] else Color.GREEN}{report.stats['duplicate_ru']}{Color.RESET}")
    print(f"Missing in ru.json:                {Color.RED if report.stats['missing_in_ru'] else Color.GREEN}{report.stats['missing_in_ru']}{Color.RESET}")
    print(f"Missing in en.json:                {Color.RED if report.stats['missing_in_en'] else Color.GREEN}{report.stats['missing_in_en']}{Color.RESET}")
    print(f"Untranslated in ru.json:           {Color.YELLOW if report.stats['untranslated_ru'] else Color.GREEN}{report.stats['untranslated_ru']}{Color.RESET}")
    print(f"Missing keys referenced in code:   {Color.RED if report.stats['missing_in_code'] else Color.GREEN}{report.stats['missing_in_code']}{Color.RESET}")
    print(f"XAML FallbackValue violations:     {Color.RED if report.stats['xaml_fallbacks'] else Color.GREEN}{report.stats['xaml_fallbacks']}{Color.RESET}")
    print(f"C# Fallback (?? / hack) breaches:  {Color.RED if report.stats['cs_fallbacks'] else Color.GREEN}{report.stats['cs_fallbacks']}{Color.RESET}")
    print(f"Hardcoded Cyrillic leaks:          {Color.RED if report.stats['hardcoded_cyrillic'] else Color.GREEN}{report.stats['hardcoded_cyrillic']}{Color.RESET}")
    print(f"Unused keys (Dead Keys):           {Color.YELLOW if report.stats['unused_keys'] else Color.GREEN}{report.stats['unused_keys']}{Color.RESET}")

    if fix_mode and report.dead_keys:
        apply_dead_key_cleanup(l10n_dir, report.dead_keys)

    if report.errors:
        print(f"\n{Color.RED}{Color.BOLD}[FAILURE] Critical localization violations detected!{Color.RESET}\n")
        sys.exit(1)
    else:
        print(f"\n{Color.GREEN}{Color.BOLD}[SUCCESS] Localization architecture is clean.{Color.RESET}\n")
        sys.exit(0)

if __name__ == "__main__":
    main()