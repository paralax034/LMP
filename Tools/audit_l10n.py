#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
LMP Localization & Hardcode Auditor.
Проверяет целостность локализации, симметрию словарей, выявляет хардкод строк и утечки кириллицы.
"""

import sys
import os
import re
import json
from pathlib import Path
from typing import Dict, Set, List, Tuple

# Цветовая разметка терминала ANSI
class Color:
    RED = '\033[91m'
    GREEN = '\033[92m'
    YELLOW = '\033[93m'
    BLUE = '\033[94m'
    MAGENTA = '\033[95m'
    CYAN = '\033[96m'
    BOLD = '\033[1m'
    RESET = '\033[0m'

if not sys.stdout.isatty():
    # Отключение цветов при перенаправлении вывода в файл
    for attr in dir(Color):
        if not attr.startswith("__"):
            setattr(Color, attr, "")

# Регулярные выражения для поиска
RE_CYRILLIC = re.compile(r'[\u0400-\u04FF]')
RE_BINDING_FALLBACK = re.compile(r'\{Binding\s+L\[(?P<key>[^\]]+)\],\s*FallbackValue=(?P<fallback>[^\}]+)\}')
RE_XAML_L_KEY = re.compile(r'\{Binding\s+L\[(?P<key>[a-zA-Z0-9_]+)\]')
RE_CS_L_KEY = re.compile(r'(?:L|SL|LocalizationService\.Instance)\["(?P<key>[a-zA-Z0-9_]+)"\]')
RE_CS_L_FALLBACK_PATTERN = re.compile(r'(?:L|SL|LocalizationService\.Instance)\["[^"]+"\]\s*\?\?\s*(?P<fallback>"[^"]*")')
RE_CS_GET_FALLBACK = re.compile(r'\.Get\(\s*"[^"]+"\s*,\s*(?P<fallback>"[^"]+")\s*\)')
RE_CS_BRACKET_HACK = re.compile(r'\.StartsWith\(\s*\'\[\'\s*\)|\.StartsWith\(\s*"\["\s*\)')

# Атрибуты XAML, подлежащие проверке на хардкод текста
TEXT_ATTRIBUTES = {
    'Text', 'Content', 'ToolTip.Tip', 'PlaceholderText', 'Header', 
    'Title', 'Watermark', 'Badge'
}

# Исключения для проверки строк в C#
EXCLUDED_CS_PATTERNS = [
    re.compile(r'Log\.(Info|Debug|Warn|Error|Fatal)\s*\('),
    re.compile(r'Trace\.WriteLine\s*\('),
    re.compile(r'Console\.WriteLine\s*\('),
]


class AuditReport:
    def __init__(self):
        self.errors: List[str] = []
        self.warnings: List[str] = []
        self.stats = {
            "en_keys": 0,
            "ru_keys": 0,
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


def find_project_root() -> Path:
    """Определяет корень репозитория по наличию файла проекта или локализации."""
    current = Path(__file__).resolve().parent
    for _ in range(5):
        if (current / "Assets" / "Localization" / "en.json").exists():
            return current
        if (current / "LMP.csproj").exists():
            return current
        current = current.parent
    return Path.cwd()


def load_json_keys(file_path: Path) -> Dict[str, str]:
    """Безопасно читает JSON-словарь локализации."""
    try:
        with open(file_path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception as ex:
        print(f"{Color.RED}[FAIL] Не удалось загрузить JSON '{file_path}': {ex}{Color.RESET}")
        sys.exit(1)


def strip_comments_and_strings_cs(content: str) -> List[Tuple[int, str]]:
    """
    Разбивает C# код построчно, удаляя комментарии и маркируя строки с кодом.
    Возвращает список пар (номер_строки, очищенная_строка).
    """
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


def audit_localization_symmetry(en_dict: Dict[str, str], ru_dict: Dict[str, str], report: AuditReport):
    """Проверяет полноту и симметрию словарей en.json и ru.json."""
    en_keys = set(en_dict.keys())
    ru_keys = set(ru_dict.keys())

    report.stats["en_keys"] = len(en_keys)
    report.stats["ru_keys"] = len(ru_keys)

    # 1. Отсутствие ключей
    missing_in_ru = en_keys - ru_keys
    for k in sorted(missing_in_ru):
        report.add_error(
            f"{Color.RED}[MISSING KEY in ru.json]{Color.RESET} Ключ '{Color.BOLD}{k}{Color.RESET}' объявлен в en.json, но отсутствует в ru.json",
            "missing_in_ru"
        )

    missing_in_en = ru_keys - en_keys
    for k in sorted(missing_in_en):
        report.add_error(
            f"{Color.RED}[MISSING KEY in en.json]{Color.RESET} Ключ '{Color.BOLD}{k}{Color.RESET}' объявлен в ru.json, но отсутствует в en.json",
            "missing_in_en"
        )

    # 2. Подозрение на непереведенные значения в ru.json
    for k in sorted(en_keys.intersection(ru_keys)):
        en_val = en_dict[k].strip()
        ru_val = ru_dict[k].strip()
        
        # Если значение идентично, содержит пробелы (предложение) и только ASCII-символы
        if en_val == ru_val and len(en_val) > 4 and " " in en_val and not RE_CYRILLIC.search(ru_val):
            report.add_warning(
                f"{Color.YELLOW}[UNTRANSLATED ru.json]{Color.RESET} Ключ '{Color.BOLD}{k}{Color.RESET}' в ru.json содержит исходный английский текст: \"{ru_val}\"",
                "untranslated_ru"
            )


def audit_xaml_file(path: Path, root: Path, all_keys: Set[str], report: AuditReport, used_keys: Set[str]):
    """Аудит XAML разметки на запрещенные fallback, валидность ключей и хардкод."""
    rel_path = path.relative_to(root)
    try:
        content = path.read_text(encoding="utf-8")
    except Exception as ex:
        report.add_error(f"Не удалось прочитать {rel_path}: {ex}")
        return

    # Удаление XML-комментариев для исключения ложных срабатываний
    content_no_comments = re.sub(r'<!--.*?-->', '', content, flags=re.DOTALL)
    lines = content_no_comments.splitlines()

    for line_no, line in enumerate(lines, start=1):
        # 1. Проверка FallbackValue на привязках локализации
        for match in RE_BINDING_FALLBACK.finditer(line):
            key = match.group("key")
            fallback = match.group("fallback").strip("'\" ")
            report.add_error(
                f"{Color.RED}[XAML FALLBACK]{Color.RESET} {rel_path}:{line_no}: "
                f"Запрещен FallbackValue='{Color.BOLD}{fallback}{Color.RESET}' для ключа L[{key}]",
                "xaml_fallbacks"
            )

        # 2. Сбор ключей L[...] и проверка их наличия в словарях
        for match in RE_XAML_L_KEY.finditer(line):
            key = match.group("key")
            used_keys.add(key)
            if key not in all_keys:
                report.add_error(
                    f"{Color.RED}[XAML UNKNOWN KEY]{Color.RESET} {rel_path}:{line_no}: "
                    f"Ключ '{Color.BOLD}{key}{Color.RESET}' не существует ни в одном JSON-словаре!",
                    "missing_in_code"
                )

        # 3. Поиск хардкодной кириллицы в XAML
        if RE_CYRILLIC.search(line):
            # Проверяем, не является ли это комментарием или FallbackValue (который уже отловлен)
            if "FallbackValue" not in line:
                # Извлекаем строковые литералы
                literals = re.findall(r'="([^"]*[\u0400-\u04FF][^"]*)"', line)
                raw_texts = re.findall(r'>([^<]*[\u0400-\u04FF][^<]*)<', line)
                all_found = literals + [t.strip() for t in raw_texts if t.strip()]
                for found in all_found:
                    report.add_error(
                        f"{Color.RED}[HARDCODED CYRILLIC XAML]{Color.RESET} {rel_path}:{line_no}: "
                        f"Прямой текст на русском: \"{Color.BOLD}{found}{Color.RESET}\"",
                        "hardcoded_cyrillic"
                    )

        # 4. Проверка подозрительного прямого английского текста в UI атрибутах (например ToolTip.Tip="YouTube Video")
        for attr in TEXT_ATTRIBUTES:
            pattern = rf'{attr}="([^"\{{\}}]+)"'
            for match in re.finditer(pattern, line):
                val = match.group(1).strip()
                # Игнорируем пустые, чистые числа, пути и системные привязки
                if val and not val.isdigit() and len(val) > 2 and not val.startswith("avares://"):
                    # Если значение не начинается с {Binding / StaticResource
                    if any(known in val for known in ["YouTube", "Локальная", "Search", "Close", "Queue"]):
                        report.add_warning(
                            f"{Color.YELLOW}[UNLOCALIZED XAML ATTR]{Color.RESET} {rel_path}:{line_no}: "
                            f"Атрибут {attr}=\"{val}\" зашит без локализации",
                            "hardcoded_ui_english"
                        )


def audit_cs_file(path: Path, root: Path, all_keys: Set[str], report: AuditReport, used_keys: Set[str]):
    """Аудит C# файлов на fallback-операторы, обходы локализации и непереведенный текст."""
    rel_path = path.relative_to(root)
    try:
        content = path.read_text(encoding="utf-8")
    except Exception as ex:
        report.add_error(f"Не удалось прочитать {rel_path}: {ex}")
        return

    cleaned_lines = strip_comments_and_strings_cs(content)

    for line_no, line in cleaned_lines:
        if not line.strip():
            continue

        # 1. Поиск антипаттерна L["..."] ?? "Дефолт"
        for match in RE_CS_L_FALLBACK_PATTERN.finditer(line):
            fb = match.group("fallback")
            report.add_error(
                f"{Color.RED}[C# OPERATOR ?? FALLBACK]{Color.RESET} {rel_path}:{line_no}: "
                f"Использование оператора ?? для маскировки локализации: {line.strip()}",
                "cs_fallbacks"
            )

        # 2. Поиск вызовов .Get("Key", "Fallback")
        for match in RE_CS_GET_FALLBACK.finditer(line):
            report.add_error(
                f"{Color.RED}[C# GET() FALLBACK]{Color.RESET} {rel_path}:{line_no}: "
                f"Передача аргумента fallback в метод Get(): {line.strip()}",
                "cs_fallbacks"
            )

        # 3. Поиск костылей проверки маркера '[' (например raw.StartsWith('['))
        if RE_CS_BRACKET_HACK.search(line):
            report.add_error(
                f"{Color.RED}[C# BRACKET HACK]{Color.RESET} {rel_path}:{line_no}: "
                f"Обход локализации через проверку маркера скобки '[': {line.strip()}",
                "cs_fallbacks"
            )

        # 4. Сбор используемых ключей L["..."], SL["..."]
        for match in RE_CS_L_KEY.finditer(line):
            key = match.group("key")
            used_keys.add(key)
            if key not in all_keys:
                report.add_error(
                    f"{Color.RED}[C# UNKNOWN KEY]{Color.RESET} {rel_path}:{line_no}: "
                    f"Ключ '{Color.BOLD}{key}{Color.RESET}' отсутствует в словарях!",
                    "missing_in_code"
                )

        # 5. Проверка утечки кириллического текста в коде (за исключением логов и системных проверок)
        if RE_CYRILLIC.search(line):
            # Пропускаем строки логирования, трассировки и генерации исключений в диагностике
            if not any(excl.search(line) for excl in EXCLUDED_CS_PATTERNS):
                # Находим строковые литералы с кириллицей
                str_literals = re.findall(r'"([^"]*[\u0400-\u04FF][^"]*)"', line)
                if str_literals and "LanguageItem" not in line and "AvailableLanguages" not in line:
                    for s in str_literals:
                        report.add_warning(
                            f"{Color.YELLOW}[HARDCODED CYRILLIC C#]{Color.RESET} {rel_path}:{line_no}: "
                            f"Строка в коде: \"{Color.BOLD}{s}{Color.RESET}\"",
                            "hardcoded_cyrillic"
                        )


def main():
    root = find_project_root()
    print(f"{Color.CYAN}{Color.BOLD}=== LMP Localization & Hardcode Static Auditor ==={Color.RESET}")
    print(f"Корень проекта: {Color.BOLD}{root}{Color.RESET}\n")

    en_path = root / "Assets" / "Localization" / "en.json"
    ru_path = root / "Assets" / "Localization" / "ru.json"

    if not en_path.exists() or not ru_path.exists():
        print(f"{Color.RED}[FATAL] Не найдены словари локализации в {en_path.parent}{Color.RESET}")
        sys.exit(1)

    en_dict = load_json_keys(en_path)
    ru_dict = load_json_keys(ru_path)
    all_keys = set(en_dict.keys()).union(set(ru_dict.keys()))

    report = AuditReport()
    used_keys: Set[str] = set()

    # 1. Проверка симметрии и полноты словарей
    print(f"[{Color.BLUE}1/3{Color.RESET}] Проверка словарей en.json ({len(en_dict)} keys) и ru.json ({len(ru_dict)} keys)...")
    audit_localization_symmetry(en_dict, ru_dict, report)

    # 2. Поиск файлов для анализа
    source_dirs = [root / "UI", root / "Core"]
    xaml_files: List[Path] = []
    cs_files: List[Path] = []

    for s_dir in source_dirs:
        if s_dir.exists():
            xaml_files.extend(s_dir.rglob("*.axaml"))
            cs_files.extend(s_dir.rglob("*.cs"))

    # 3. Аудит XAML файлов
    print(f"[{Color.BLUE}2/3{Color.RESET}] Сканирование {len(xaml_files)} файлов XAML разметки...")
    for xaml_path in xaml_files:
        audit_xaml_file(xaml_path, root, all_keys, report, used_keys)

    # 4. Аудит C# файлов
    print(f"[{Color.BLUE}3/3{Color.RESET}] Сканирование {len(cs_files)} файлов исходного C# кода...")
    for cs_path in cs_files:
        audit_cs_file(cs_path, root, all_keys, report, used_keys)

    # 5. Проверка неиспользуемых ключей (Dead Keys)
    # Исключаем составные плюрализованные ключи с суффиксами _0, _1, _2, _5, _other
    for k in sorted(all_keys):
        base_k = re.sub(r'(_0|_1|_2|_5|_other|_few|_many|_one)$', '', k)
        if k not in used_keys and base_k not in used_keys:
            # Проверяем не формируется ли ключ динамически
            if not any(k.startswith(prefix) for prefix in ["AnimationSpeed_", "VolumeCurve_", "AudioQuality_", "NetProfile_", "CloseAction_", "NormMode_"]):
                report.stats["unused_keys"] += 1

    # ВЫВОД РЕЗУЛЬТАТОВ
    print(f"\n{Color.CYAN}{Color.BOLD}=== РЕЗУЛЬТАТЫ АУДИТА ==={Color.RESET}")

    if report.errors:
        print(f"\n{Color.RED}{Color.BOLD}КРИТИЧЕСКИЕ ОШИБКИ ({len(report.errors)}):{Color.RESET}")
        for err in report.errors:
            print(f"  {err}")

    if report.warnings:
        print(f"\n{Color.YELLOW}{Color.BOLD}ПРЕДУПРЕЖДЕНИЯ ({len(report.warnings)}):{Color.RESET}")
        for warn in report.warnings:
            print(f"  {warn}")

    print(f"\n{Color.CYAN}{Color.BOLD}--- СВОДНАЯ СТАТИСТИКА ---{Color.RESET}")
    print(f"Ключей в en.json:                  {report.stats['en_keys']}")
    print(f"Ключей в ru.json:                  {report.stats['ru_keys']}")
    print(f"Отсутствует в ru.json:             {Color.RED if report.stats['missing_in_ru'] else Color.GREEN}{report.stats['missing_in_ru']}{Color.RESET}")
    print(f"Отсутствует в en.json:             {Color.RED if report.stats['missing_in_en'] else Color.GREEN}{report.stats['missing_in_en']}{Color.RESET}")
    print(f"Непереведено в ru.json:            {Color.YELLOW if report.stats['untranslated_ru'] else Color.GREEN}{report.stats['untranslated_ru']}{Color.RESET}")
    print(f"Несуществующие ключи в коде:       {Color.RED if report.stats['missing_in_code'] else Color.GREEN}{report.stats['missing_in_code']}{Color.RESET}")
    print(f"XAML FallbackValue нарушений:      {Color.RED if report.stats['xaml_fallbacks'] else Color.GREEN}{report.stats['xaml_fallbacks']}{Color.RESET}")
    print(f"C# Fallback (?? / hack) нарушений: {Color.RED if report.stats['cs_fallbacks'] else Color.GREEN}{report.stats['cs_fallbacks']}{Color.RESET}")
    print(f"Утечек кириллицы (хардкод):        {Color.RED if report.stats['hardcoded_cyrillic'] else Color.GREEN}{report.stats['hardcoded_cyrillic']}{Color.RESET}")
    print(f"Потенциально мертвых ключей:       {report.stats['unused_keys']}")

    if report.errors:
        print(f"\n{Color.RED}{Color.BOLD}[ПРОВАЛ] Обнаружены критические несоответствия локализации!{Color.RESET}\n")
        sys.exit(1)
    else:
        print(f"\n{Color.GREEN}{Color.BOLD}[УСПЕХ] Архитектура локализации чиста от критических fallback-нарушений.{Color.RESET}\n")
        sys.exit(0)


if __name__ == "__main__":
    main()