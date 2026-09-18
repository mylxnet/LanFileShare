# LanFileShare dev run script (PowerShell, ASCII-only)
# Usage: powershell -ExecutionPolicy Bypass -File run-dev.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$dotnet = (& dotnet --version) 2>$null
if (-not $dotnet) {
    Write-Error "[X] dotnet not found. Please install .NET 8 SDK: https://dotnet.microsoft.com/download"
    exit 1
}

Write-Host "[RUN] starting LanFileShare in Development mode (dotnet run) ..." -ForegroundColor Cyan
Write-Host "      Close the window to hide to tray. Right-click tray icon -> Exit to fully terminate." -ForegroundColor Yellow
Write-Host ""

dotnet run -c Debug --project .\LanFileShare
