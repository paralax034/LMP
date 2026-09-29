<#
.SYNOPSIS
    Localization JSON dictionary sorter and formatter for LMP.

.PARAMETER LocalizationDir
    Relative or absolute path to the localization directory.

.EXAMPLE
    .\Tools\sort-l10n.ps1
#>

param (
    [string]$LocalizationDir = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Resolve repository root path
$root = Split-Path $PSScriptRoot -Parent

$targetDir = if ([string]::IsNullOrWhiteSpace($LocalizationDir)) {
    Join-Path $root "Assets\Localization"
} else {
    if ([System.IO.Path]::IsPathRooted($LocalizationDir)) {
        $LocalizationDir
    } else {
        Join-Path $root $LocalizationDir
    }
}

if (-not (Test-Path $targetDir)) {
    Write-Error "Localization directory not found: $targetDir"
    exit 1
}

$jsonFiles = @(Get-ChildItem -Path $targetDir -Filter "*.json" | Sort-Object Name)
if ($jsonFiles.Count -eq 0) {
    Write-Error "No JSON files found in directory: $targetDir"
    exit 1
}

Write-Host ""
Write-Host "==============================================================" -ForegroundColor DarkGray
Write-Host " LMP Localization Sorter & Formatter" -ForegroundColor Cyan
Write-Host "==============================================================" -ForegroundColor DarkGray
Write-Host "  Directory : $targetDir" -ForegroundColor Gray
Write-Host ""

foreach ($file in $jsonFiles) {
    Write-Host "  Sorting $($file.Name)..." -ForegroundColor Yellow

    $lines = [System.IO.File]::ReadAllLines($file.FullName, [System.Text.Encoding]::UTF8)
    $dict = [System.Collections.Generic.SortedDictionary[string, string]]::new([System.StringComparer]::Ordinal)

    foreach ($line in $lines) {
        if ($line -match '^\s*"([^"]+)"\s*:\s*"((?:[^"\\]|\\.)*)"') {
            $dict[$Matches[1]] = $Matches[2]
        }
    }

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("{")

    $previousPrefix = ""
    $keys = @($dict.Keys)
    $count = $keys.Count
    $index = 0

    foreach ($key in $keys) {
        $index++
        $value = $dict[$key]

        # Logical prefix grouping by underscore
        $prefix = if ($key.Contains("_")) { $key.Split("_")[0] } else { "Other" }

        if ($previousPrefix -ne "" -and $prefix -ne $previousPrefix) {
            [void]$sb.AppendLine()
        }
        $previousPrefix = $prefix

        $comma = if ($index -lt $count) { "," } else { "" }
        [void]$sb.AppendLine("  ""$key"": ""$value""$comma")
    }

    [void]$sb.AppendLine("}")

    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($file.FullName, $sb.ToString(), $utf8NoBom)

    Write-Host "  [OK] Successfully sorted: $($file.Name) ($count keys)" -ForegroundColor Green
}

Write-Host ""
Write-Host "==============================================================" -ForegroundColor DarkGray
Write-Host " All localization files sorted and formatted successfully!" -ForegroundColor Green
Write-Host "==============================================================" -ForegroundColor DarkGray
Write-Host ""