#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
LMP Localization Auto-Fixer.
Автоматически устраняет все нарушения локализации:
- Удаляет FallbackValue из XAML
- Удаляет операторы ?? и fallback аргументы из C#
- Преобразует вызовы .Get("Key") и .Get("Key", "...") в индексатор ["Key"]
- Устраняет хардкод в XAML
- Синхронизирует и дополняет словари en.json и ru.json
"""

import os
import sys
import re
import json
from pathlib import Path

def find_root() -> Path:
    cur = Path(__file__).resolve().parent
    for _ in range(5):
        if (cur / "LMP.csproj").exists() or (cur / "Assets" / "Localization" / "en.json").exists():
            return cur
        cur = cur.parent
    return Path.cwd()

ROOT = find_root()

def update_json_dictionaries():
    """Синхронизирует недостающие ключи и правит непереведенные значения."""
    en_path = ROOT / "Assets" / "Localization" / "en.json"
    ru_path = ROOT / "Assets" / "Localization" / "ru.json"

    with open(en_path, "r", encoding="utf-8") as f:
        en = json.load(f)
    with open(ru_path, "r", encoding="utf-8") as f:
        ru = json.load(f)

    new_keys = {
        "Common_ComingSoon": ("Soon", "Скоро"),
        "Common_From": ("by", "от"),
        "Search_Source_YouTubeMusic": ("YouTube Music", "YouTube Music"),
        "Search_Source_YouTube": ("YouTube Video", "YouTube Видео"),
        "Search_Source_Local": ("Local Library", "Локальная библиотека"),
    }

    for key, (en_val, ru_val) in new_keys.items():
        if key not in en:
            en[key] = en_val
            print(f"[JSON] Добавлен ключ в en.json: {key}")
        if key not in ru:
            ru[key] = ru_val
            print(f"[JSON] Добавлен ключ в ru.json: {key}")

    fixes = {
        "Merge_Error_Msg": "Не удалось объединить плейлисты.",
        "Merge_Success_Msg": "Плейлисты успешно объединены.",
    }
    for key, ru_val in fixes.items():
        if ru.get(key) != ru_val:
            ru[key] = ru_val
            print(f"[JSON] Исправлен перевод в ru.json: {key} -> '{ru_val}'")

    with open(en_path, "w", encoding="utf-8") as f:
        json.dump(dict(sorted(en.items())), f, ensure_ascii=False, indent=2)
        f.write("\n")

    with open(ru_path, "w", encoding="utf-8") as f:
        json.dump(dict(sorted(ru.items())), f, ensure_ascii=False, indent=2)
        f.write("\n")

def fix_xaml_files():
    """Исправляет FallbackValue и хардкод в файлах XAML."""
    for path in ROOT.rglob("*.axaml"):
        content = path.read_text(encoding="utf-8")
        original = content

        # 1. Удаление FallbackValue из привязок к L[...]
        content = re.sub(
            r'(\{Binding\s+L\[[^\]]+\])\s*,\s*FallbackValue=(?:[\'"][^\'"]*[\'"]|""|\'\')(\s*,\s*|\s*})',
            lambda m: m.group(1) + ("," if m.group(2).startswith(",") else "}"),
            content
        )
        content = re.sub(
            r',\s*FallbackValue=(?:[\'"][^\'"]*[\'"]|""|\'\')\s*(?=\})',
            '',
            content
        )

        # 2. Точечные исправления хардкода
        if path.name == "GeneralPage.axaml":
            content = content.replace('<TextBlock Text="СКОРО" />', '<TextBlock Text="{Binding L[Common_ComingSoon]}" />')

        if path.name == "PlaylistView.axaml":
            content = content.replace('<TextBlock Text="от" FontSize="13"', '<TextBlock Text="{Binding L[Common_From]}" FontSize="13"')

        if path.name == "SearchView.axaml":
            content = content.replace('ToolTip.Tip="YouTube Music"', 'ToolTip.Tip="{Binding L[Search_Source_YouTubeMusic]}"')
            content = content.replace('ToolTip.Tip="YouTube Video"', 'ToolTip.Tip="{Binding L[Search_Source_YouTube]}"')
            content = content.replace('ToolTip.Tip="Локальная библиотека"', 'ToolTip.Tip="{Binding L[Search_Source_Local]}"')
            content = re.sub(r'FallbackValue=[\'"]Ничего не найдено[\'"]', '', content)

        if content != original:
            path.write_text(content, encoding="utf-8")
            print(f"[XAML FIX] Обновлен: {path.relative_to(ROOT)}")

def fix_cs_files():
    """Исправляет вызовы ??, .Get(...) и переводит весь C# код на индексатор [key]."""
    for path in ROOT.rglob("*.cs"):
        if path.name in ("LocalizationService.cs", "audit_l10n.py", "fix_l10n.py"):
            continue

        # Игнорируем сетевые парсеры YouTube протокола
        posix = path.as_posix()
        if "Core/Youtube" in posix:
            continue

        content = path.read_text(encoding="utf-8")
        original = content

        # 1. Замена .Get("Key", "Fallback") на индексатор ["Key"]
        content = re.sub(
            r'(\b(?:L|SL|LocalizationService\.Instance|vm\.L))\.Get\(\s*("[^"]+")\s*,\s*(?:"(?:[^"\\]|\\.)*"|@"[^"]*")\s*\)',
            r'\1[\2]',
            content
        )

        # 2. Замена одиночного .Get("Key") на индексатор ["Key"]
        content = re.sub(
            r'(\b(?:L|SL|LocalizationService\.Instance|vm\.L))\.Get\(\s*("[^"]+")\s*\)',
            r'\1[\2]',
            content
        )

        # 3. Удаление оператора ?? после обращения к L[...] / SL[...]
        content = re.sub(
            r'((?:L|SL|LocalizationService\.Instance|vm\.L)\["[^"]+"\])\s*\?\?\s*(?:"(?:[^"\\]|\\.)*"|@"[^"]*")',
            r'\1',
            content
        )

        # 4. Точечные исправления хаков
        if path.name == "StreamUnavailableDialog.axaml.cs":
            content = content.replace('Message = L.Get(locKey, GetFallbackMessage(exception));', 'Message = L[locKey];')
            content = re.sub(r'private static string GetFallbackMessage\(StreamUnavailableException ex\)\s*\{[\s\S]*?^\s*\}', '', content, flags=re.MULTILINE)

        if path.name == "SettingsViewModel.cs":
            content = re.sub(
                r'private string ResolveSpeedLabel\(TrackAnimationSpeed speed\)\s*\{[\s\S]*?return raw;\s*\}',
                'private string ResolveSpeedLabel(TrackAnimationSpeed speed)\n    {\n        return SL[$"AnimationSpeed_{speed}"];\n    }',
                content
            )

        if path.name == "CopyLinkButton.axaml.cs":
            old_methods = re.search(r'private string ResolveSuccessText\(\)[\s\S]*?private void HideHint\(\)', content)
            if old_methods:
                clean_methods = (
                    'private string ResolveSuccessText()\n'
                    '    {\n'
                    '        if (!string.IsNullOrEmpty(SuccessText))\n'
                    '            return SuccessText;\n\n'
                    '        return LocalizationService.Instance["Common_Copied"];\n'
                    '    }\n\n'
                    '    private static string ResolveErrorText()\n'
                    '    {\n'
                    '        return LocalizationService.Instance["Common_CopyFailed"];\n'
                    '    }\n\n'
                    '    private void HideHint()'
                )
                content = content[:old_methods.start()] + clean_methods + content[old_methods.end():]

        if path.name == "SingleInstanceGuard.cs":
            content = re.sub(
                r'if \(!string\.IsNullOrEmpty\(locTitle\) && !locTitle\.StartsWith\(\'\[\'\)\)[\s\S]*?question = "Lite Music Player уже запущен\.\\n\\nЗавершить предыдущий процесс и запустить новый\?";',
                'title = locTitle;\n            question = locQuestion;',
                content
            )

        if content != original:
            path.write_text(content, encoding="utf-8")
            print(f"[C# FIX] Обновлен: {path.relative_to(ROOT)}")

def main():
    print("=== Автоматическое исправление локализации LMP ===")
    print(f"Корень: {ROOT}\n")

    update_json_dictionaries()
    fix_xaml_files()
    fix_cs_files()

    print("\n[УСПЕХ] Все файлы обновлены. Вызовы Get() переведены на индексатор this[].")
    print("Запустите: python Tools/audit_l10n.py для финальной проверки.")

if __name__ == "__main__":
    main()