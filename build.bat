@echo off
setlocal
chcp 65001 >nul

where python >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    python "%~dp0build.py" %*
    exit /b %ERRORLEVEL%
)

where py >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    py "%~dp0build.py" %*
    exit /b %ERRORLEVEL%
)

echo [ERROR] Python 3 was not found in PATH.
echo Please install Python 3 or add it to your system PATH to use the LMP Build System.
exit /b 1