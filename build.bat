@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion

title LMP Build System

echo.
echo ╔══════════════════════════════════════════════╗
echo ║           LMP Build System                   ║
echo ╚══════════════════════════════════════════════╝
echo.

set MODE=%1
set PAUSE=1

:: Если передан второй параметр nopause — не ставим паузу в конце
if /i "%2"=="nopause" set PAUSE=0
if /i "%MODE%"=="" set MODE=debug

:: Получение версии из Git только для информационного вывода в консоль
where git >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    set COMMIT_COUNT=0
    set GIT_HASH=local
) else (
    for /f %%i in ('git rev-list --count HEAD 2^>nul') do set COMMIT_COUNT=%%i
    for /f %%i in ('git rev-parse --short=7 HEAD 2^>nul') do set GIT_HASH=%%i
    if "!COMMIT_COUNT!"=="" set COMMIT_COUNT=0
    if "!GIT_HASH!"=="" set GIT_HASH=local
)

:: Динамическое чтение мажорной и минорной версий из Directory.Build.props
set "VERSION_MAJOR="
set "VERSION_MINOR="
set "VERSION_PREFIX="

if exist Directory.Build.props (
    for /f "tokens=2 delims=>< " %%a in ('type Directory.Build.props ^| find "VersionMajor"') do set "VERSION_MAJOR=%%a"
    for /f "tokens=2 delims=>< " %%a in ('type Directory.Build.props ^| find "VersionMinor"') do set "VERSION_MINOR=%%a"
    for /f "tokens=2 delims=>< " %%a in ('type Directory.Build.props ^| find "VersionPrefix"') do set "VERSION_PREFIX=%%a"
)

if defined VERSION_MAJOR set "VERSION_MAJOR=!VERSION_MAJOR: =!"
if defined VERSION_MINOR set "VERSION_MINOR=!VERSION_MINOR: =!"
if defined VERSION_PREFIX set "VERSION_PREFIX=!VERSION_PREFIX: =!"

set "VER_PREF="
if defined VERSION_MAJOR (
    if defined VERSION_MINOR (
        set "VER_PREF=!VERSION_MAJOR!.!VERSION_MINOR!"
    )
)
if "!VER_PREF!"=="" (
    if defined VERSION_PREFIX (
        set "VER_PREF=!VERSION_PREFIX!"
    ) else (
        set "VER_PREF=0.0"
    )
)

set VERSION=!VER_PREF!.!COMMIT_COUNT!
set FULL_VERSION=!VER_PREF!.!COMMIT_COUNT!+!GIT_HASH!

echo Mode: %MODE%
echo Version: v!VERSION!
echo Full Version: v!FULL_VERSION!
echo.

if /i "%MODE%"=="clean" goto :CLEAN
if /i "%MODE%"=="debug" goto :DEBUG
if /i "%MODE%"=="optimized" goto :OPTIMIZED
if /i "%MODE%"=="release" goto :RELEASE
if /i "%MODE%"=="publish" goto :PUBLISH
if /i "%MODE%"=="aot" goto :PUBLISH
if /i "%MODE%"=="restore" goto :RESTORE

echo Unknown mode "%MODE%".
echo Available: debug ^| optimized ^| release ^| publish ^| aot ^| restore ^| clean
goto :END

:RESTORE
echo Performing full restore of the solution...
dotnet restore LMP.sln --force --force-evaluate
goto :SUCCESS

:DEBUG
echo Building Debug (Full Solution)...
dotnet build LMP.sln -c Debug
goto :CHECK

:OPTIMIZED
echo Building Optimized Debug (Full Solution)...
dotnet build LMP.sln -c Debug ^
    -p:Optimize=true ^
    -p:DebugType=embedded
goto :CHECK

:RELEASE
echo Checking for dead events...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\find-dead-events.ps1"
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [WARNING] Dead events detected. Consider fixing before release.
    echo.
)

echo Building Release (Full Solution)...
dotnet build LMP.sln -c Release ^
    -p:DebugType=None ^
    -p:DebugSymbols=false
goto :CHECK

:PUBLISH
echo Publishing Native AOT Binary (ILC Native Machine Code)...

if exist "publish" rmdir /s /q "publish"

dotnet publish LMP.csproj -c Release -r win-x64 ^
    -p:PublishAot=true ^
    -p:TrimMode=full ^
    -p:StripSymbols=true ^
    -p:DebugType=None ^
    -p:DebugSymbols=false ^
    -o "./publish"

if %ERRORLEVEL% NEQ 0 goto :FAIL

del /q "publish\*.xml" 2>nul
del /q "publish\*.pdb" 2>nul
del /q "publish\*.config" 2>nul

echo.
echo ✓ Native AOT Publish completed: ./publish
goto :END

:CLEAN
echo Cleaning all project folders recursively...

if exist "publish" rmdir /s /q "publish"
if exist "publish_aot" rmdir /s /q "publish_aot"
del /q "*.7z" 2>nul

for /d /r . %%d in (bin,obj) do (
    if exist "%%d" (
        echo Deleting: %%d
        rmdir /s /q "%%d"
    )
)

echo ✓ Clean complete!
goto :END

:CHECK
if %ERRORLEVEL% NEQ 0 goto :FAIL

:SUCCESS
echo.
echo ✓ Build successful
goto :END

:FAIL
echo.
echo ✗ Build failed!
exit /b 1

:END
if %PAUSE%==1 (
    echo.
    pause
)