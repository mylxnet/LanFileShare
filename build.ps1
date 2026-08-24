# LanFileShare 构建脚本 (Windows PowerShell)
# 用法： .\build.ps1 -Version 1.0.0
param(
    [string]$Version = "1.0.0",
    [switch]$SkipZip = $false
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  LanFileShare 构建脚本 v$Version" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# ===== 1) 检查 dotnet =====
$dotnet = (& dotnet --version) 2>$null
if (-not $dotnet) {
    Write-Error "❌ 未找到 dotnet 命令，请先安装 .NET 8 SDK：https://dotnet.microsoft.com/download"
    exit 1
}
Write-Host "✅ .NET SDK 版本：$dotnet" -ForegroundColor Green

# ===== 2) 清理旧构建 =====
$projectDir = $PSScriptRoot
$publishDir = Join-Path $projectDir "publish"
$distDir    = Join-Path $projectDir "dist"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
if (Test-Path $distDir)    { Remove-Item -Recurse -Force $distDir }

# ===== 3) dotnet publish =====
Write-Host ""
Write-Host "🔨 开始 dotnet publish ..." -ForegroundColor Yellow
Set-Location $projectDir
dotnet publish `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true `
    /p:DebugType=embedded `
    /p:Version="$Version" `
    /p:AssemblyVersion="$Version.0" `
    /p:FileVersion="$Version.0" `
    .\LanFileShare\LanFileShare.csproj `
    -o (Join-Path $projectDir "publish")

if ($LASTEXITCODE -ne 0) {
    Write-Error "❌ dotnet publish 失败"
    exit 1
}

# ===== 4) 计算产物大小 =====
$exe = Get-Item (Join-Path $publishDir "LanFileShare.exe")
$exeMB = "{0:N2}" -f ($exe.Length / 1MB)
Write-Host "✅ 生成 EXE：LanFileShare.exe  ($exeMB MB)" -ForegroundColor Green

# ===== 5) 打包成 ZIP =====
if (-not $SkipZip) {
    Write-Host ""
    Write-Host "📦 打包成 ZIP ..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null

    $zipPath = Join-Path $distDir "LanFileShare-v$Version-win-x64.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath }

    Compress-Archive `
        -Path "$publishDir\LanFileShare.exe" `
        -DestinationPath $zipPath

    $zipMB = "{0:N2}" -f ((Get-Item $zipPath).Length / 1MB)
    Write-Host "✅ ZIP 产物：$zipPath ($zipMB MB)" -ForegroundColor Green
}

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  🎉 构建完成！" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "📦 分发件：$distDir\LanFileShare-v$Version-win-x64.zip"
Write-Host "📁 原始 EXE：$publishDir\LanFileShare.exe"
Write-Host ""
Write-Host "👉 把 ZIP 发给用户即可，解压后双击 EXE 即用。" -ForegroundColor Yellow
Write-Host ""
