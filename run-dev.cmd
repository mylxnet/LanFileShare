@echo off
REM LanFileShare dev run script (cmd / .bat, ASCII-only)
REM Usage: run-dev.cmd
setlocal EnableExtensions
pushd "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [X] dotnet not found
    popd
    exit /b 1
)

echo [RUN] starting LanFileShare in Development mode (dotnet run) ...
echo       Close the window to hide to tray. Right-click tray icon -^> Exit to fully terminate.
echo.

dotnet run -c Debug --project .\LanFileShare

popd
endlocal
