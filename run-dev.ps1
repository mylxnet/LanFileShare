<# LanFileShare 开发期运行脚本（dotnet run，等价于 VS 中 F5）
# 用法： .\run-dev.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# 检查 dotnet
$dotnet = (& dotnet --version) 2>$null
if (-not $dotnet) {
    Write-Error "❌ 未找到 dotnet 命令，请先安装 .NET 8 SDK：https://dotnet.microsoft.com/download"
    exit 1
}

Write-Host "🚀 以 Development 模式启动 LanFileShare（dotnet run）..." -ForegroundColor Cyan
Write-Host "    关闭窗口会隐藏到托盘；右键托盘图标 → 退出 才会真正结束进程。" -ForegroundColor Yellow
Write-Host ""

dotnet run -c Debug --project .\LanFileShare
