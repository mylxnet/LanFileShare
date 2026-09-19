@echo off
REM =============================================================
REM  LanFileShare build wrapper (CMD)
REM  Forwards ALL stdout/stderr from PowerShell back to this cmd
REM  window so you can copy errors directly.
REM
REM  Usage:
REM      build.cmd                  (build with default version 1.1.4)
REM      build.cmd 1.2.3            (custom version)
REM      build.cmd 1.2.3 -SkipZip   (skip zip step)
REM =============================================================
setlocal EnableExtensions

REM ===== ASCII banner =====
echo ============================================================
echo   LanFileShare build (cmd wrapper)
echo ============================================================
echo.

REM ===== Locate PowerShell =====
where powershell >nul 2>&1
if errorlevel 1 (
    echo [ERR] PowerShell not found. Install .NET 8 SDK first.
    exit /b 1
)

REM ===== Force UTF-8 output so any Chinese in error messages
REM       doesn't get re-encoded as GBK and mangle. =====
chcp 65001 >nul 2>&1

REM ===== Forward exit code from build.ps1 =====
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "PS_EXIT=%ERRORLEVEL%"

echo.
if not "%PS_EXIT%"=="0" (
    echo ============================================================
    echo   BUILD FAILED  (PowerShell exit code: %PS_EXIT%^)
    echo.
    echo   Error details live in:
    echo     .build-logs\last-error.log
    echo   Open it in Notepad, Ctrl+A, Ctrl+C and paste to me.
    echo ============================================================
) else (
    echo ============================================================
    echo   BUILD COMPLETE
    echo   Check .\dist\ for the ZIP.
    echo ============================================================
)

exit /b %PS_EXIT%
