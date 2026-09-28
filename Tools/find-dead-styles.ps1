<#
.SYNOPSIS
    Статический анализатор и аудит стилей Avalonia 12 (мёртвые, дублирующие, похожие стили).

.DESCRIPTION
    1. Сканирует все AXAML и CS файлы на использование классов (Classes="...", Classes.prop="...", Classes.Add/Remove).
    2. Извлекает все блоки <Style Selector="..."> и их сеттеры с номерами строк.
    3. Выявляет:
       - МЁРТВЫЕ: класс селектора нигде не объявлен в разметке и коде.
       - ФАНТОМНЫЕ: несовпадение целевого типа контрола (например, Border.x при использовании только Button.x).
       - ПУСТЫЕ: блоки Style без единого сеттера или анимации.
       - ШАБЛОННЫЕ: дублирование селекторов шаблона (/template/ ContentPresenter vs #PART_ContentPresenter).
       - ДУБЛИКАТЫ: идентичные селекторы и одинаковые сеттеры в разных файлах/областях видимости.
       - ПОХОЖИЕ (КЛОНЫ): кластерный анализ на основе индекса Жаккара (порог похожести свойств).

.PARAMETER SimilarityThreshold
    Порог схожести свойств (сеттеров) для выявления стилей-клонов (от 0.5 до 1.0, по умолчанию: 0.75).

.PARAMETER SourceDirs
    Папки для поиска исходников через запятую относительно корня проекта.

.EXAMPLE
    .\Tools\find-dead-styles.ps1
    .\Tools\find-dead-styles.ps1 -SimilarityThreshold 0.80
#>

param(
    [double]$SimilarityThreshold = 0.75,
    [string]$SourceDirs = "Core,Features,UI"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# --- Инициализация путей ---

$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

Write-Host ""
Write-Host "══════════════════════════════════════════════════════════════" -ForegroundColor DarkGray
Write-Host " LMP Avalonia 12 Style Audit & Similarity Scanner" -ForegroundColor Cyan
Write-Host "══════════════════════════════════════════════════════════════" -ForegroundColor DarkGray
Write-Host "  Корень проекта : $root" -ForegroundColor Gray
Write-Host "  Порог подобия  : $([math]::Round($SimilarityThreshold * 100))%" -ForegroundColor Gray
Write-Host ""

# --- Вспомогательные функции форматирования вывода ---

function Write-Issue([string]$category, [ConsoleColor]$color, [string]$location, [string]$details) {
    Write-Host ("  {0,-12} {1,-50} {2}" -f "[$category]", $location, $details) -ForegroundColor $color
}

# --- Сбор файлов ---

$sourceFiles = [System.Collections.Generic.List[string]]::new()
foreach ($dir in ($SourceDirs -split ",")) {
    $full = Join-Path $root $dir.Trim()
    if (Test-Path $full) {
        Get-ChildItem $full -Recurse -Include "*.axaml","*.cs" |
            Where-Object { $_.FullName -notmatch '\\(obj|bin|External|Docs|Assets|Tests)\\' } |
            ForEach-Object { $sourceFiles.Add($_.FullName) }
    }
}
Get-ChildItem $root -Filter "*.axaml" -File | ForEach-Object { $sourceFiles.Add($_.FullName) }
Get-ChildItem $root -Filter "*.cs" -File | ForEach-Object { $sourceFiles.Add($_.FullName) }

$allFiles = @($sourceFiles | Sort-Object -Unique)
$axamlFiles = @($allFiles | Where-Object { $_.EndsWith(".axaml", [System.StringComparison]::OrdinalIgnoreCase) })
$csFiles = @($allFiles | Where-Object { $_.EndsWith(".cs", [System.StringComparison]::OrdinalIgnoreCase) })

Write-Host "  Файлов разметки AXAML : $($axamlFiles.Count)" -ForegroundColor Gray
Write-Host "  Файлов кода C#        : $($csFiles.Count)" -ForegroundColor Gray
Write-Host ""

# --- Фаза 1: Сбор используемых классов и целевых типов контролов ---

$usedClasses = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$classToTypes = [System.Collections.Generic.Dictionary[string, [System.Collections.Generic.HashSet[string]]]]::new([System.StringComparer]::OrdinalIgnoreCase)

function Register-ClassUsage([string]$className, [string]$controlType) {
    $clean = $className.Trim()
    if ([string]::IsNullOrWhiteSpace($clean)) { return }
    [void]$usedClasses.Add($clean)

    if (-not [string]::IsNullOrWhiteSpace($controlType)) {
        $cType = $controlType.Trim()
        if (-not $classToTypes.ContainsKey($clean)) {
            $classToTypes[$clean] = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        }
        [void]$classToTypes[$clean].Add($cType)
    }
}

# 1. Сканирование AXAML на предмет статических и реактивных классов
$axamlClassRegex = [regex]'<(?:\w+:)?([A-Za-z0-9_]+)\b[^>]*?\bClasses="([^"]+)"'
$axamlBoolClassRegex = [regex]'\bClasses\.([A-Za-z0-9_-]+)\s*='

foreach ($file in $axamlFiles) {
    $content = Get-Content $file -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    if ([string]::IsNullOrEmpty($content)) { continue }

    foreach ($m in $axamlClassRegex.Matches($content)) {
        $ctrlType = $m.Groups[1].Value
        $classesStr = $m.Groups[2].Value
        foreach ($cls in ($classesStr -split '\s+')) {
            Register-ClassUsage $cls $ctrlType
        }
    }

    foreach ($m in $axamlBoolClassRegex.Matches($content)) {
        Register-ClassUsage $m.Groups[1].Value ""
    }
}

# 2. Сканирование C# на императивные модификации Classes
$csClassRegexes = @(
    [regex]'\.Classes\.Add\(\s*"([^"]+)"\s*\)',
    [regex]'\.Classes\.Remove\(\s*"([^"]+)"\s*\)',
    [regex]'\.Classes\.Set\(\s*"([^"]+)"\s*,',
    [regex]'\.Classes\s*=\s*"([^"]+)"'
)

foreach ($file in $csFiles) {
    $content = Get-Content $file -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    if ([string]::IsNullOrEmpty($content)) { continue }

    foreach ($re in $csClassRegexes) {
        foreach ($m in $re.Matches($content)) {
            foreach ($cls in ($m.Groups[1].Value -split '\s+')) {
                Register-ClassUsage $cls ""
            }
        }
    }
}

# Всегда активные системные псевдоклассы и псевдоэлементы
$systemBuiltIns = @(
    "pointerover", "pressed", "disabled", "focus", "focus-visible", "selected",
    "checked", "indeterminate", "open", "empty", "active", "loaded", "dragging",
    "drop-target", "visible", "loading", "error", "success", "header-strip",
    "horizontal", "vertical", "left", "right", "top", "bottom"
)
foreach ($builtin in $systemBuiltIns) {
    [void]$usedClasses.Add($builtin)
}

Write-Host "  Обнаружено активных классов в кодовой базе: $($usedClasses.Count)" -ForegroundColor Gray
Write-Host ""

# --- Фаза 2: Извлечение стилей из AXAML ---

class ParsedStyle {
    [string]$File
    [int]$Line
    [string]$Selector
    [string]$TargetType
    [System.Collections.Generic.List[string]]$Classes
    [System.Collections.Generic.Dictionary[string, string]]$Setters
    [bool]$HasTemplateParts
    [bool]$HasAnimations
    [bool]$IsEmpty

    ParsedStyle() {
        $this.Classes = [System.Collections.Generic.List[string]]::new()
        $this.Setters = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    }

    [string] GetSignature() {
        $sortedKeys = [System.Linq.Enumerable]::ToArray([System.Linq.Enumerable]::OrderBy($this.Setters.Keys, [System.Func[string, string]]{ param($k) $k }))
        $pairs = [System.Collections.Generic.List[string]]::new()
        foreach ($k in $sortedKeys) {
            $pairs.Add("$k=$($this.Setters[$k])")
        }
        return [string]::Join(";", $pairs)
    }
}

$allStyles = [System.Collections.Generic.List[ParsedStyle]]::new()

$styleBlockRegex = [regex]'(?s)<Style\s+([^>]*?)>(.*?)</Style>|<Style\s+([^>]*?)\s*/>'
$selectorAttrRegex = [regex]'Selector\s*=\s*"([^"]+)"'
$setterRegex = [regex]'<Setter\s+Property\s*=\s*"([^"]+)"(?:\s+Value\s*=\s*"([^"]*?)")?'

foreach ($file in $axamlFiles) {
    $relPath = $file.Substring($root.Length).TrimStart('\')
    $content = Get-Content $file -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
    if ([string]::IsNullOrEmpty($content)) { continue }

    $matches = $styleBlockRegex.Matches($content)
    foreach ($m in $matches) {
        $attrs = if ($m.Groups[1].Success) { $m.Groups[1].Value } else { $m.Groups[3].Value }
        $body  = if ($m.Groups[2].Success) { $m.Groups[2].Value } else { "" }

        $selMatch = $selectorAttrRegex.Match($attrs)
        if (-not $selMatch.Success) { continue }

        $selector = $selMatch.Groups[1].Value.Trim()
        $lineNum = [regex]::Matches($content.Substring(0, $m.Index), "\r?\n").Count + 1

        $style = [ParsedStyle]::new()
        $style.File = $relPath
        $style.Line = $lineNum
        $style.Selector = $selector

        # Определение целевого типа и классов селектора
        $typeMatch = [regex]::Match($selector, '^([A-Za-z0-9_]+)')
        if ($typeMatch.Success) {
            $style.TargetType = $typeMatch.Groups[1].Value
        }

        $classMatches = [regex]::Matches($selector, '\.([A-Za-z0-9_-]+)')
        foreach ($cm in $classMatches) {
            $style.Classes.Add($cm.Groups[1].Value)
        }

        # Извлечение сеттеров
        $setterMatches = $setterRegex.Matches($body)
        foreach ($sm in $setterMatches) {
            $prop = $sm.Groups[1].Value.Trim()
            $val  = if ($sm.Groups[2].Success) { $sm.Groups[2].Value.Trim() } else { "Complex/Inline" }
            $style.Setters[$prop] = $val
        }

        $style.HasTemplateParts = $selector.Contains("/template/")
        $style.HasAnimations    = $body.Contains("<Style.Animations>") -or $body.Contains("<Animation")
        $style.IsEmpty          = ($style.Setters.Count -eq 0) -and (-not $style.HasAnimations)

        $allStyles.Add($style)
    }
}

Write-Host "  Собрано деклараций <Style> : $($allStyles.Count)" -ForegroundColor Gray
Write-Host ""

# --- Фаза 3: Аудит дефектов стилей ---

$deadCount      = 0
$phantomCount   = 0
$emptyCount     = 0
$templateDupCount = 0
$exactDupCount  = 0
$similarCount   = 0

# 1. Проверка на пустые стили (Empty No-Op)
Write-Host ("-- Пустые стили (Empty No-Op Blocks) ").PadRight(60, '-') -ForegroundColor Yellow
foreach ($s in $allStyles) {
    if ($s.IsEmpty) {
        $emptyCount++
        Write-Issue "ПУСТОЙ" Yellow "$($s.File):$($s.Line)" "Селектор: $($s.Selector)"
    }
}
if ($emptyCount -eq 0) { Write-Host "  [OK] Пустых стилей не обнаружено." -ForegroundColor Green }
Write-Host ""

# 2. Проверка на мёртвые стили и фантомные несовпадения типов
Write-Host ("-- Мёртвые и фантомные стили ").PadRight(60, '-') -ForegroundColor Red
foreach ($s in $allStyles) {
    if ($s.Classes.Count -eq 0) { continue }

    # Проверяем, существует ли хоть один пользовательский класс из селектора в кодовой базе
    $isDead = $true
    foreach ($cls in $s.Classes) {
        if ($usedClasses.Contains($cls)) {
            $isDead = $false
            break
        }
    }

    if ($isDead) {
        $deadCount++
        $deadClassesStr = [string]::Join(", ", $s.Classes)
        Write-Issue "МЁРТВЫЙ" Red "$($s.File):$($s.Line)" "Класс '$deadClassesStr' нигде не используется в проекте"
        continue
    }

    # Проверка фантомного несовпадения типов (например, Border.playlist-card-inner vs Button)
    if (-not [string]::IsNullOrEmpty($s.TargetType) -and $s.Classes.Count -gt 0) {
        $primaryClass = $s.Classes[0]
        if ($classToTypes.ContainsKey($primaryClass)) {
            $actualTypes = $classToTypes[$primaryClass]
            if ($actualTypes.Count -gt 0 -and -not $actualTypes.Contains($s.TargetType)) {
                $actualTypesStr = [string]::Join(", ", $actualTypes)
                $phantomCount++
                Write-Issue "ФАНТОМ" Magenta "$($s.File):$($s.Line)" "Селектор ожидает '$($s.TargetType)', но класс применяется только к: [$actualTypesStr]"
            }
        }
    }
}
if ($deadCount -eq 0 -and $phantomCount -eq 0) { Write-Host "  [OK] Мёртвых и фантомных стилей не обнаружено." -ForegroundColor Green }
Write-Host ""

# 3. Проверка на шаблонное двойное таргетирование (/template/ ContentPresenter)
Write-Host ("-- Избыточное двойное таргетирование шаблона ").PadRight(60, '-') -ForegroundColor Yellow
$templateGroups = $allStyles | Where-Object { $_.HasTemplateParts } | Group-Object File
foreach ($fg in $templateGroups) {
    $fileStyles = @($fg.Group)
    for ($i = 0; $i -lt $fileStyles.Count; $i++) {
        $s1 = $fileStyles[$i]
        if ($s1.Selector -match '/template/\s*ContentPresenter$') {
            $baseSel = $s1.Selector.Substring(0, $s1.Selector.IndexOf('/template/')).Trim()
            foreach ($s2 in $fileStyles) {
                if ($s2.Selector -eq "$baseSel /template/ ContentPresenter#PART_ContentPresenter") {
                    $templateDupCount++
                    Write-Issue "ШАБЛОН" Yellow "$($s1.File):$($s1.Line)" "Дублируется с #PART_ContentPresenter на строке $($s2.Line)"
                }
            }
        }
    }
}
if ($templateDupCount -eq 0) { Write-Host "  [OK] Двойного таргетирования шаблонов не обнаружено." -ForegroundColor Green }
Write-Host ""

# 4. Проверка на точные дубликаты стилей
Write-Host ("-- Точные дубликаты (Exact Style Duplicates) ").PadRight(60, '-') -ForegroundColor Red
for ($i = 0; $i -lt $allStyles.Count; $i++) {
    $s1 = $allStyles[$i]
    if ($s1.IsEmpty -or $s1.Setters.Count -eq 0) { continue }
    $sig1 = $s1.GetSignature()

    for ($j = $i + 1; $j -lt $allStyles.Count; $j++) {
        $s2 = $allStyles[$j]
        if ($s1.Selector -eq $s2.Selector) {
            $sig2 = $s2.GetSignature()
            if ($sig1 -eq $sig2) {
                $exactDupCount++
                Write-Issue "ДУБЛИКАТ" Red "$($s2.File):$($s2.Line)" "100% дубликат $($s1.File):$($s1.Line) ('$($s1.Selector)')"
            }
        }
    }
}
if ($exactDupCount -eq 0) { Write-Host "  [OK] Точных дубликатов стилей не обнаружено." -ForegroundColor Green }
Write-Host ""

# 5. Кластерный поиск похожих стилей (Жаккар-индекс сеттеров)
Write-Host ("-- Стили-клоны (Похожие стили >= $([math]::Round($SimilarityThreshold * 100))%) ").PadRight(60, '-') -ForegroundColor Cyan

$reportedPairs = [System.Collections.Generic.HashSet[string]]::new()

for ($i = 0; $i -lt $allStyles.Count; $i++) {
    $s1 = $allStyles[$i]
    if ($s1.Setters.Count -lt 2) { continue }

    for ($j = $i + 1; $j -lt $allStyles.Count; $j++) {
        $s2 = $allStyles[$j]
        if ($s2.Setters.Count -lt 2) { continue }
        if ($s1.File -eq $s2.File -and $s1.Selector -eq $s2.Selector) { continue }

        $pairKey = [string]::Join("|", (@("$($s1.File):$($s1.Line)", "$($s2.File):$($s2.Line)") | Sort-Object))
        if ($reportedPairs.Contains($pairKey)) { continue }

        # Сравнение пар ключ=значение
        $s1Pairs = [System.Collections.Generic.HashSet[string]]::new()
        foreach ($k in $s1.Setters.Keys) { [void]$s1Pairs.Add("$k=$($s1.Setters[$k])") }

        $s2Pairs = [System.Collections.Generic.HashSet[string]]::new()
        foreach ($k in $s2.Setters.Keys) { [void]$s2Pairs.Add("$k=$($s2.Setters[$k])") }

        $intersection = 0
        foreach ($p in $s1Pairs) {
            if ($s2Pairs.Contains($p)) { $intersection++ }
        }

        $union = $s1Pairs.Count + $s2Pairs.Count - $intersection
        if ($union -eq 0) { continue }

        $similarity = [double]$intersection / [double]$union
        if ($similarity -ge $SimilarityThreshold -and $similarity -lt 1.0) {
            [void]$reportedPairs.Add($pairKey)
            $similarCount++
            $pct = [math]::Round($similarity * 100)
            Write-Host ("  {0,-12} {1,-50} {2}" -f "[$pct% КЛОН]", "$($s1.File):$($s1.Line)", "$($s1.Selector)") -ForegroundColor Cyan
            Write-Host ("  {0,-12} {1,-50} {2}" -f "   ПОХОЖ НА", "$($s2.File):$($s2.Line)", "$($s2.Selector)") -ForegroundColor DarkCyan
            Write-Host ""
        }
    }
}
if ($similarCount -eq 0) { Write-Host "  [OK] Скрытых стилей-клонов не обнаружено." -ForegroundColor Green }
Write-Host ""

# --- Итоговая сводка ---

$totalDefects = $deadCount + $phantomCount + $emptyCount + $templateDupCount + $exactDupCount

Write-Host ("-" * 62) -ForegroundColor DarkGray
Write-Host "  Итоговая статистика аудита стилей" -ForegroundColor White
Write-Host "    Просканировано стилей         : $($allStyles.Count)" -ForegroundColor Gray
Write-Host "    Мёртвых стилей                : $deadCount" -ForegroundColor $(if ($deadCount -eq 0) { "Green" } else { "Red" })
Write-Host "    Фантомных (Type Mismatch)     : $phantomCount" -ForegroundColor $(if ($phantomCount -eq 0) { "Green" } else { "Magenta" })
Write-Host "    Пустых блоков (No-Op)         : $emptyCount" -ForegroundColor $(if ($emptyCount -eq 0) { "Green" } else { "Yellow" })
Write-Host "    Шаблонных дубликатов          : $templateDupCount" -ForegroundColor $(if ($templateDupCount -eq 0) { "Green" } else { "Yellow" })
Write-Host "    Точных дубликатов             : $exactDupCount" -ForegroundColor $(if ($exactDupCount -eq 0) { "Green" } else { "Red" })
Write-Host "    Похожих стилей (Клонов)       : $similarCount" -ForegroundColor $(if ($similarCount -eq 0) { "Green" } else { "Cyan" })
Write-Host ""

if ($totalDefects -eq 0) {
    Write-Host "  [УСПЕХ] В кодовой базе стилей критических дефектов не обнаружено!" -ForegroundColor Green
} else {
    Write-Host "  [ВНИМАНИЕ] Обнаружено дефектов стилизации: $totalDefects" -ForegroundColor Red
}
Write-Host ""

exit $totalDefects