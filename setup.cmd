@echo off
REM ============================================
REM  LanFileShare first-run setup (Windows cmd, ASCII)
REM
REM  Auto-elevates to admin if needed, then:
REM    1) Add Windows Firewall inbound rule for LanFileShare.exe
REM    2) Verify .NET 8 runtime available (for source-build scenarios)
REM    3) Run a quick HTTP smoke test (start + curl + kill)
REM    4) Print a clear PASS/FAIL report
REM
REM  Usage: double-click setup.cmd, OR run from cmd
REM ============================================

setlocal EnableExtensions

REM ===== 0) Self-elevate to admin =====
net session >nul 2>&1
if not errorlevel 1 goto :got_admin
echo [..] Setup needs admin rights. Requesting elevation...
powershell -NoProfile -Command "Start-Process cmd -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
exit /b

:got_admin
echo.
echo ==========================================
echo   LanFileShare First-Run Setup
echo ==========================================
echo.

REM ===== 1) Locate LanFileShare.exe =====
set "EXE_PATH="
set "EXE_DIR=%~dp0"
if exist "%EXE_DIR%publish\LanFileShare.exe" set "EXE_PATH=%EXE_DIR%publish\LanFileShare.exe"
if exist "%EXE_DIR%dist\LanFileShare.exe"   set "EXE_PATH=%EXE_DIR%dist\LanFileShare.exe"
if exist "%EXE_DIR%LanFileShare.exe"        set "EXE_PATH=%EXE_DIR%LanFileShare.exe"

if "%EXE_PATH%"=="" (
    echo [X] Cannot find LanFileShare.exe.
    echo     Please run build.cmd first, then re-run setup.cmd.
    exit /b 1
)
echo [OK] Found EXE: %EXE_PATH%
echo.

REM ===== 2) Add / refresh Windows Firewall rule =====
echo [..] Configuring Windows Firewall inbound rule...
set "RULE_NAME=LanFileShare HTTP (auto)"

REM Delete existing rule (if any) and re-create to ensure clean state
netsh advfirewall firewall delete rule name="%RULE_NAME%" >nul 2>&1

netsh advfirewall firewall add rule ^
    name="%RULE_NAME%" ^
    dir=in ^
    action=allow ^
    program="%EXE_PATH%" ^
    enable=yes ^
    profile=any ^
    localport=any ^
    protocol=tcp >nul 2>&1

if errorlevel 1 (
    echo [X] Failed to add firewall rule.
) else (
    echo [OK] Firewall rule added.
)
echo.

REM ===== 3) Verify .NET 8 Desktop Runtime =====
echo [..] Checking .NET 8 runtime...
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [WARN] 'dotnet' not in PATH.
    echo        This is fine for the prebuilt EXE in dist\.
    echo        Only needed if you want to run from source.
) else (
    dotnet --list-runtimes | findstr /i "Microsoft.WindowsDesktop.App.*8\." >nul
    if errorlevel 1 (
        echo [WARN] .NET 8 Desktop Runtime not detected.
        echo        Prebuilt single-file EXEs work without it.
        echo        Only needed if you build from source.
    ) else (
        echo [OK] .NET 8 Desktop Runtime present.
    )
)
echo.

REM ===== 3.5) Reset stale LastPort if outside 50000-50999 range =====
set "SETTINGS_FILE=%APPDATA%\LanFileShare\settings.json"
if exist "%SETTINGS_FILE%" (
    powershell -NoProfile -Command "$f='%SETTINGS_FILE%'; try { $j=Get-Content $f -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop; if($j.LastPort -lt 50000 -or $j.LastPort -gt 50999){ $j.LastPort=50000; $j.SavePath=''; $j | ConvertTo-Json -Depth 3 | Set-Content $f -Encoding UTF8; Write-Host '[OK] Reset stale settings (LastPort=50000).' } else { Write-Host '[OK] Settings OK (LastPort is in 50000-50999 range).' } } catch { Write-Host '[WARN] Settings file unreadable; will be rebuilt on first run.' }"
) else (
    echo [OK] No stale settings to reset.
)
echo.

REM ===== 4) HTTP smoke test =====
echo [..] Running HTTP smoke test (starts EXE, curls /test, kills)...
set "TEST_PORT=9999"

REM Remove old port reservation
netsh int ipv4 delete dynamicport tcp startport=%TEST_PORT% numberofports=1 >nul 2>&1

REM Start EXE in background. It auto-picks a port; we use /port arg as a hint.
start "" /B "%EXE_PATH%" /port %TEST_PORT% >nul 2>&1

REM Wait up to 5 seconds for EXE to come up
set "READY="
for /L %%i in (1,1,10) do (
    timeout /t 1 /nobreak >nul
    netsh int ipv4 show dynamicport tcp | findstr /i "%TEST_PORT%" >nul 2>&1
    if not errorlevel 1 (
        set "READY=yes"
        goto :smoke_done
    )
)

:smoke_done
if "%READY%"=="" goto :smoke_fail

REM Try the test
curl -s -m 3 "http://127.0.0.1:%TEST_PORT%/test" > "%TEMP%\lanfileshare-test.txt" 2>&1
if errorlevel 1 goto :smoke_fail

type "%TEMP%\lanfileshare-test.txt"
echo.
echo [OK] HTTP smoke test passed.

REM Kill the test EXE
taskkill /f /im LanFileShare.exe >nul 2>&1
del /f /q "%TEMP%\lanfileshare-test.txt" >nul 2>&1
goto :smoke_done2

:smoke_fail
echo [WARN] HTTP smoke test inconclusive.
echo        (curl not available OR EXE didn't start. Skipping.)
taskkill /f /im LanFileShare.exe >nul 2>&1

:smoke_done2
echo.

REM ===== 5) Final report =====
echo ==========================================
echo   SETUP COMPLETE
echo ==========================================
echo.
echo Setup summary:
echo   * Firewall rule:  ADDED for %EXE_PATH%
echo   * Save folder:    %APPDATA%\LanFileShare\settings.json (created on first run)
echo.
echo Next steps:
echo   1) Run LanFileShare.exe (just double-click)
echo   2) On first launch, pick a save folder
echo   3) Scan the QR code with your phone
echo.
echo If anything still doesn't work, run diagnose.cmd for details.
echo.
pause
endlocal
