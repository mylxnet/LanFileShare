@echo off
REM ============================================
REM  LanFileShare diagnostic (no admin required, ASCII)
REM  Usage:
REM    diagnose.cmd         - basic diagnostic report
REM    diagnose.cmd --test  - also auto-launch the EXE and report
REM ============================================
setlocal EnableExtensions EnableDelayedExpansion

echo.
echo ==========================================
echo   LanFileShare Diagnostic Report
echo ==========================================
echo.

REM ===== 1) .NET runtime =====
echo [A] .NET Runtime
where dotnet >nul 2>&1
if errorlevel 1 (
    echo     [X] 'dotnet' not in PATH
    echo         (OK for prebuilt EXEs)
) else (
    for /f "delims=" %%v in ('dotnet --version') do echo     Version: %%v
    echo     Desktop runtimes:
    dotnet --list-runtimes 2>nul | findstr "Microsoft.WindowsDesktop" 2>nul
)
echo.

REM ===== 2) Network interfaces =====
echo [B] Network Interfaces
ipconfig 2>nul | findstr "IPv4"
echo.

REM ===== 3) Port usage =====
echo [C] Ports in 50000-50999 range currently LISTENING
netstat -ano | findstr "LISTENING" | findstr /R ":50[0-9][0-9][0-9] " 2>nul
if errorlevel 1 echo     (no ports in 50xxx range listening)
echo.

REM ===== 3b) Show who's listening on 0-19999 too (look for PID 4 traps) =====
echo [C2] Lower ports with PID 4 (System) - these will NOT work for HTTP
netstat -ano | findstr "LISTENING" | findstr /R ":[[:space:]]4$" 2>nul
if errorlevel 1 echo     (no obvious PID 4 traps in netstat output)
echo.

REM ===== 4) Firewall rule =====
echo [D] Firewall Rule for LanFileShare
netsh advfirewall firewall show rule name="LanFileShare HTTP (auto)" 2>&1 | findstr /i "no rules" >nul
if not errorlevel 1 (
    echo     [X] No firewall rule
    echo         Run setup.cmd to add it.
) else (
    echo     [OK] Rule exists:
    netsh advfirewall firewall show rule name="LanFileShare HTTP (auto)"
)
echo.

REM ===== 5) Process status =====
echo [E] LanFileShare.exe process
tasklist /fi "imagename eq LanFileShare.exe" 2>nul
if errorlevel 1 echo     (not running)
echo.

REM ===== 6) Last log lines =====
echo [F] Last app log
set "LOG_DIR=%APPDATA%\LanFileShare\logs"
if exist "%LOG_DIR%\app-*.log" (
    for /f "delims=" %%f in ('dir /b /od "%LOG_DIR%\app-*.log" 2^>nul') do set "LATEST_LOG=%%f"
    if defined LATEST_LOG (
        echo     File: %LOG_DIR%\!LATEST_LOG!
        echo     Last 20 lines:
        echo     ------------------------------
        powershell -NoProfile -Command "Get-Content '%LOG_DIR%\!LATEST_LOG!' -Tail 20"
        echo     ------------------------------
    )
) else (
    echo     (no logs at %LOG_DIR%\)
)
echo.

REM ===== 7) AppData directory =====
echo [G] AppData directory
if exist "%APPDATA%\LanFileShare" (
    echo     %APPDATA%\LanFileShare\
    dir "%APPDATA%\LanFileShare" /b 2>nul
) else (
    echo     (not yet created - app hasnt run yet)
)
echo.

REM ===== 8) Launch test (optional, only if --test arg given) =====
if /I "%~1"=="--test" (
    echo [H] Launch test starting EXE...
    set "EXE_PATH="
    if exist "%~dp0publish\LanFileShare.exe" set "EXE_PATH=%~dp0publish\LanFileShare.exe"
    if exist "%~dp0dist\LanFileShare.exe"    set "EXE_PATH=%~dp0dist\LanFileShare.exe"
    if exist "%~dp0LanFileShare.exe"         set "EXE_PATH=%~dp0LanFileShare.exe"
    if not defined EXE_PATH (
        echo     [X] Cannot find LanFileShare.exe
    ) else (
        echo     Starting %EXE_PATH% ...
        start "" /B "%EXE_PATH%"
        timeout /t 5 /nobreak >nul
        tasklist /fi "imagename eq LanFileShare.exe" 2>nul | findstr /i "LanFileShare.exe" >nul
        if errorlevel 1 (
            echo     [X] EXE exited within 5 seconds. Crash.
            echo     Showing latest log:
            if exist "%LOG_DIR%\app-*.log" (
                for /f "delims=" %%f in ('dir /b /od "%LOG_DIR%\app-*.log" 2^>nul') do set "NEW_LOG=%%f"
                if defined NEW_LOG powershell -NoProfile -Command "Get-Content '%LOG_DIR%\!NEW_LOG!'"
            )
        ) else (
            echo     [OK] EXE is running. Listening on:
            netstat -ano 2>nul | findstr /R ":9[0-9][0-9][0-9] .*LISTENING" | findstr /v "127.0.0.1"
        )
    )
    echo.
)

echo ==========================================
echo   END OF REPORT
echo ==========================================
echo.
echo Common fixes:
echo   * setup.cmd       - admin: add firewall rule
echo   * build.cmd 1.0.0 - rebuild EXE
echo   * Phone and PC must be on same WiFi
echo   * Disable VPN on phone and PC
echo   * On Windows first launch, allow private network access
echo.
pause
endlocal
