<#
.SYNOPSIS
    Двухфакторный поиск и аудит мёртвого кода в решении LMP (Native AOT + Source Tree).

.DESCRIPTION
    1. Запускает компиляцию Native AOT с генерацией графов кодогенерации (DGML).
    2. Запускает высокопроизводительный потоковый сканер AotScanner.
    3. Выполняет перекрёстную верификацию вызовов по всему дереву исходников.
    4. Генерирует кликабельный отчет с номерами строк и типами сущностей.

.PARAMETER SkipBuild
    Пропустить публикацию AOT (использовать уже сгенерированный DGML).

.PARAMETER AssemblyPath
    Путь к анализируемой сборке (по умолчанию Core\bin\Release\net11.0\LMP.Core.dll).

.PARAMETER OutputFile
    Имя файла отчета в корне проекта (по умолчанию DeadCode.txt).

.EXAMPLE
    .\Tools\find-dead-code.ps1
    .\Tools\find-dead-code.ps1 -SkipBuild
    .\Tools\find-dead-code.ps1 -AssemblyPath "bin\Release\net11.0\LMP.dll" -OutputFile "DeadCode_UI.txt"
#>
param(
    [switch]$SkipBuild,
    [string]$AssemblyPath = "Core\bin\Release\net11.0\LMP.Core.dll",
    [string]$OutputFile   = "DeadCode.txt"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Корень репозитория
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$fullAssemblyPath = Join-Path $root $AssemblyPath
$fullOutputPath   = Join-Path $root $OutputFile
$dgmlPath         = Join-Path $root "obj\Release\net11.0\win-x64\native\LMP.codegen.dgml.xml"
$scannerScript    = Join-Path $root "Tools\AotScanner\Program.cs"

Write-Host ""
Write-Host "══════════════════════════════════════════════════════════════" -ForegroundColor DarkGray
Write-Host " LMP Native AOT Dead Code Detection Pipeline" -ForegroundColor Cyan
Write-Host "══════════════════════════════════════════════════════════════" -ForegroundColor DarkGray
Write-Host "  Сборка       : $fullAssemblyPath" -ForegroundColor Gray
Write-Host "  Граф ILC     : $dgmlPath" -ForegroundColor Gray
Write-Host "  Файл отчета  : $fullOutputPath" -ForegroundColor Gray
Write-Host ""

if (-not $SkipBuild) {
    Write-Host ">>> [1/2] Сборка Native AOT с генерацией графа зависимостей (DGML)..." -ForegroundColor Yellow

    $publishArgs = @(
        "publish", "LMP.csproj",
        "-c", "Release",
        "-r", "win-x64",
        "-p:PublishAot=true",
        "-p:TrimMode=full",
        "-p:IlcGenerateDgmlFile=true",
        "-p:IlcGenerateMstatFile=true",
        "-p:IlcFoldIdenticalMethodBodies=false",
        "-o", "./publish"
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Ошибка при публикации AOT-сборки. Код выхода: $LASTEXITCODE"
        exit $LASTEXITCODE
    }
    Write-Host "✓ AOT-граф успешно сформирован." -ForegroundColor Green
    Write-Host ""
} else {
    Write-Host ">>> Пропуск компиляции (используется существующий DGML-граф)..." -ForegroundColor DarkYellow
    if (-not (Test-Path $dgmlPath)) {
        Write-Error "DGML-граф не найден: $dgmlPath. Запустите скрипт без флага -SkipBuild."
        exit 1
    }
}

if (-not (Test-Path $fullAssemblyPath)) {
    Write-Error "Сборка не найдена: $fullAssemblyPath. Убедитесь, что проект скомпилирован в конфигурации Release."
    exit 1
}

Write-Host ">>> [2/2] Двухфакторный анализ исходников и AOT-кодогенерации..." -ForegroundColor Yellow

$runArgs = @(
    "run",
    "--file", $scannerScript,
    "--",
    $fullAssemblyPath,
    $dgmlPath,
    $root,
    $fullOutputPath
)

& dotnet @runArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "Ошибка при выполнении анализатора AotScanner. Код выхода: $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "══════════════════════════════════════════════════════════════" -ForegroundColor DarkGray
Write-Host " Анализ успешно завершен! Отчет: $fullOutputPath" -ForegroundColor Green
Write-Host "══════════════════════════════════════════════════════════════" -ForegroundColor DarkGray
Write-Host ""