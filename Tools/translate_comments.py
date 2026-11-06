#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
LMP Утилита хирургического перевода комментариев и XML-документации (RU -> EN).
Извлекает только блоки комментариев с кириллицей, переводит их пачками через AIStudio2API
и вставляет обратно, не затрагивая исполняемый C#/AXAML код и строковые литералы.
"""

import argparse
import concurrent.futures
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Dict, List, Tuple

tools_dir = Path(__file__).resolve().parent
if str(tools_dir) not in sys.path:
    sys.path.insert(0, str(tools_dir))

from common import Color, get_project_root, print_banner

RE_CYRILLIC = re.compile(r"[А-Яа-яЁё]")

# Лексер C#: в строгом порядке матчит raw-строки, verbatim-строки, обычные строки,
# char-литералы и типы комментариев. Это исключает ложные срабатывания внутри строк кода.
RE_CS_TOKENS = re.compile(
    r'("""[\s\S]*?""")'                           # Группа 1: Сырые строки C# 11+
    r'|(@"(?:""|[^"])*")'                         # Группа 2: Verbatim строки @"..."
    r'|("(?:\\.|[^"\\])*")'                       # Группа 3: Обычные строки "..."
    r"|('(?:\\.|[^'\\])*')"                       # Группа 4: Символы 'c'
    r"|((?:^[ \t]*)?///[^\r\n]*(?:\r?\n[ \t]*///[^\r\n]*)*)"  # Группа 5: XML-док блоки ///
    r"|(//[^\r\n]*)"                              # Группа 6: Однострочные //
    r"|(/\*[\s\S]*?\*/)",                         # Группа 7: Многострочные /* ... */
    re.MULTILINE,
)

# Комментарии XML / AXAML / CSPROJ
RE_XML_COMMENTS = re.compile(r"<!--[\s\S]*?-->")

EXCLUDED_DIRS = {
    ".git",
    ".vs",
    ".idea",
    "bin",
    "obj",
    "publish",
    "publish-aot-debug",
    "External",
    "Native",
    "__pycache__",
}

EXCLUDED_FILES = {
    "ru.json",
    "en.json",
    "README.md",
    "AsmAuditReport.md",
    "DeadCode.txt",
}

TARGET_EXTENSIONS = {
    ".cs",
    ".axaml",
    ".csproj",
    ".props",
    ".targets",
    ".xml",
}

SYSTEM_PROMPT = """You are a senior .NET 11 and Avalonia UI systems architect translating source code comments from Russian to concise, idiomatic technical English.

STRICT RULES:
1. Input is a JSON object mapping string IDs ("0", "1", ...) to source code comment blocks.
2. Return ONLY a valid JSON object with the EXACT same keys ("0", "1", ...) mapped to their English translations.
3. Preserve ALL comment syntax markers (`///`, `//`, `/*`, `*/`, `<!--`, `-->`), leading indentation, and line breaks.
4. Preserve ALL C# XML documentation tags (`<summary>`, `</summary>`, `<param name="...">`, `<returns>`, `<remarks>`, `<see cref="..."/>`, `<c>...</c>`) intact and well-formed.
5. Do NOT translate code identifiers, class/method names, CLI flags, file paths, or URLs.
6. Translate technical domain terms accurately (e.g., "кольцевой буфер" -> "ring buffer", "холодный старт" -> "cold startup", "атомарная запись" -> "atomic write", "поток" -> "thread" or "stream" depending on context)."""


def is_excluded(path: Path, root: Path) -> bool:
    rel = path.relative_to(root)
    if any(part in EXCLUDED_DIRS for part in rel.parts):
        return True
    if path.name in EXCLUDED_FILES:
        return True
    return False


def extract_comment_spans(content: str, suffix: str) -> List[Tuple[int, int, str]]:
    """
    Возвращает список кортежей (start_pos, end_pos, text) только для тех комментариев,
    в которых есть кириллица. Код и строковые литералы игнорируются.
    """
    spans: List[Tuple[int, int, str]] = []

    if suffix == ".cs":
        for m in RE_CS_TOKENS.finditer(content):
            # Группы 5, 6, 7 — это комментарии; группы 1..4 — строки/символы (пропускаем)
            comment_text = m.group(5) or m.group(6) or m.group(7)
            if comment_text and RE_CYRILLIC.search(comment_text):
                spans.append((m.start(), m.end(), comment_text))
    else:
        for m in RE_XML_COMMENTS.finditer(content):
            comment_text = m.group(0)
            if RE_CYRILLIC.search(comment_text):
                spans.append((m.start(), m.end(), comment_text))

    return spans


def validate_comment_safety(original: str, translated: str) -> bool:
    """Проверяет целостность синтаксиса комментариев после перевода."""
    orig_stripped = original.lstrip()
    trans_stripped = translated.lstrip()

    if orig_stripped.startswith("<!--"):
        if not (trans_stripped.startswith("<!--") and translated.rstrip().endswith("-->")):
            return False
        if "--" in trans_stripped[4:-3]:
            return False
    elif orig_stripped.startswith("/*"):
        if not (trans_stripped.startswith("/*") and translated.rstrip().endswith("*/")):
            return False
        if "*/" in trans_stripped[2:-2]:
            return False
    elif orig_stripped.startswith("///"):
        for line in translated.splitlines():
            if line.strip() and not line.lstrip().startswith("///"):
                return False
    elif orig_stripped.startswith("//"):
        for line in translated.splitlines():
            if line.strip() and not line.lstrip().startswith("//"):
                return False

    return True


def call_aistudio_api(
    batch: Dict[str, str],
    base_url: str,
    api_key: str,
    model: str,
    timeout: int = 120,
    max_retries: int = 4,
) -> Dict[str, str]:
    url = f"{base_url.rstrip('/')}/chat/completions"
    payload = {
        "model": model,
        "temperature": 0.1,
        "response_format": {"type": "json_object"},
        "messages": [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": json.dumps(batch, ensure_ascii=False)},
        ],
    }

    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    headers = {
        "Content-Type": "application/json",
        "Authorization": f"Bearer {api_key}",
    }

    for attempt in range(1, max_retries + 1):
        try:
            req = urllib.request.Request(url, data=data, headers=headers, method="POST")
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                body = json.loads(resp.read().decode("utf-8"))
                raw_content = body["choices"][0]["message"]["content"].strip()

                if raw_content.startswith("```"):
                    raw_content = re.sub(r"^```(?:json)?\s*|\s*```$", "", raw_content, flags=re.DOTALL)

                translated = json.loads(raw_content)
                if not isinstance(translated, dict):
                    raise ValueError("Ответ модели не является JSON-объектом")

                result: Dict[str, str] = {}
                for k, orig_val in batch.items():
                    if k not in translated or not isinstance(translated[k], str):
                        raise ValueError(f"В ответе отсутствует ключ '{k}'")
                    new_val = translated[k]
                    if not validate_comment_safety(orig_val, new_val):
                        raise ValueError(f"Нарушен синтаксис комментариев для ключа '{k}'")
                    result[k] = new_val

                return result
        except Exception as ex:
            if attempt == max_retries:
                raise RuntimeError(f"Сбой запроса к API после {max_retries} попыток: {ex}") from ex
            time.sleep(min(2 ** attempt, 15))

    return batch


def chunk_spans(
    spans: List[Tuple[int, int, str]], max_chars_per_batch: int = 10000, max_items: int = 35
) -> List[List[Tuple[int, int, str]]]:
    batches: List[List[Tuple[int, int, str]]] = []
    current: List[Tuple[int, int, str]] = []
    current_chars = 0

    for span in spans:
        span_len = len(span[2])
        if current and (current_chars + span_len > max_chars_per_batch or len(current) >= max_items):
            batches.append(current)
            current = []
            current_chars = 0
        current.append(span)
        current_chars += span_len

    if current:
        batches.append(current)
    return batches


def process_file(
    file_path: Path,
    root: Path,
    base_url: str,
    api_key: str,
    model: str,
    dry_run: bool,
) -> Tuple[str, int, str]:
    rel_path = str(file_path.relative_to(root))
    try:
        content = file_path.read_text(encoding="utf-8")
    except Exception as ex:
        return (rel_path, 0, f"Ошибка чтения: {ex}")

    spans = extract_comment_spans(content, file_path.suffix.lower())
    if not spans:
        return (rel_path, 0, "OK")

    if dry_run:
        return (rel_path, len(spans), "DRY-RUN")

    batches = chunk_spans(spans)
    replacements: Dict[Tuple[int, int], str] = {}

    try:
        for batch_spans in batches:
            payload_map = {str(i): s[2] for i, s in enumerate(batch_spans)}
            translated_map = call_aistudio_api(payload_map, base_url, api_key, model)
            for i, s in enumerate(batch_spans):
                replacements[(s[0], s[1])] = translated_map[str(i)]
    except Exception as ex:
        return (rel_path, len(spans), f"ERROR: {ex}")

    # Важно: заменяем с конца к началу, чтобы смещения не "съезжали"
    new_content = content
    for start, end, _ in reversed(spans):
        new_content = new_content[:start] + replacements[(start, end)] + new_content[end:]

    if new_content != content:
        newline = "\r\n" if "\r\n" in content else "\n"
        normalized = new_content.replace("\r\n", "\n")
        with open(file_path, "w", encoding="utf-8", newline=newline) as f:
            f.write(normalized)

    return (rel_path, len(spans), "ПЕРЕВЕДЕНО")


def main() -> None:
    parser = argparse.ArgumentParser(description="Хирургический перевод комментариев LMP на английский через AIStudio2API.")
    parser.add_argument("--base-url", default=os.environ.get("OPENAI_BASE_URL", "http://127.0.0.1:2048/v1"))
    parser.add_argument("--api-key", default=os.environ.get("OPENAI_API_KEY", "123"))
    parser.add_argument(
        "--model",
        default="gemini-3.6-flash",
        help="Модель в AIStudio2API (например, gemini-3.6-flash или gemini-3.8-flash(minimal))",
    )
    parser.add_argument("--workers", type=int, default=3, help="Количество параллельных потоков")
    parser.add_argument("--filter", type=str, default="", help="Фильтр пути (например, Core/Helpers или UI/Services)")
    parser.add_argument("--dry-run", action="store_true", help="Только подсчитать файлы и блоки комментариев без вызова API")
    args = parser.parse_args()

    root = get_project_root()

    candidate_files: List[Path] = []
    for p in sorted(root.rglob("*")):
        if not p.is_file() or p.suffix.lower() not in TARGET_EXTENSIONS:
            continue
        if is_excluded(p, root):
            continue
        if args.filter and args.filter.lower() not in str(p.relative_to(root)).replace("\\", "/").lower():
            continue
        try:
            text = p.read_text(encoding="utf-8")
            if RE_CYRILLIC.search(text):
                candidate_files.append(p)
        except Exception:
            continue

    print_banner(
        "LMP Конвейер перевода комментариев и документации",
        {
            "API URL": args.base_url,
            "Модель": args.model,
            "Потоков": args.workers,
            "Файлов с RU": len(candidate_files),
            "Тестовый прогон (Dry Run)": args.dry_run,
        },
    )

    if not candidate_files:
        print(f"  {Color.GREEN}[OK] Файлов с русскими комментариями не найдено.{Color.RESET}\n")
        return

    total_spans = 0
    errors = 0

    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = {
            pool.submit(
                process_file,
                p,
                root,
                args.base_url,
                args.api_key,
                args.model,
                args.dry_run,
            ): p
            for p in candidate_files
        }

        for idx, future in enumerate(concurrent.futures.as_completed(futures), 1):
            rel_path, count, status = future.result()
            total_spans += count
            if status.startswith("ERROR") or status.startswith("Ошибка"):
                errors += 1
                print(f"  {Color.RED}[{idx}/{len(candidate_files)}] {status:<14}{Color.RESET} {rel_path} ({count} блоков)")
            elif count > 0:
                color = Color.YELLOW if args.dry_run else Color.GREEN
                print(f"  {color}[{idx}/{len(candidate_files)}] {status:<14}{Color.RESET} {rel_path} ({count} блоков)")

    print(f"\n{Color.GRAY}--------------------------------------------------------------{Color.RESET}")
    print(f"  Всего обработано блоков комментариев : {total_spans}")
    print(f"  Ошибок                              : {Color.RED if errors else Color.GREEN}{errors}{Color.RESET}\n")
    sys.exit(1 if errors else 0)


if __name__ == "__main__":
    main()