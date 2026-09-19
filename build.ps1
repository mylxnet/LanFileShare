# =============================================================
#  LanFileShare build script v1.0 (Windows PowerShell)
#  Copy-error-friendly: when build fails, the entire log (incl.
#  every dotnet warning/error line) gets dumped to:
#     1) console (for immediate copy)
#     2) last-error.log (for Ctrl+C / paste)
#  Usage:
#     powershell -ExecutionPolicy Bypass -File build.ps1
#     powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.2.3
# =============================================================

param(
    [string]$Version = "1.1.4",
    [switch]$SkipZip = $false,
    [switch]$NoColor = $false  # set if terminal mangles ANSI colors
)

$ErrorActionPreference = "Stop"

# Force UTF-8 so any Chinese in path / message does not mangle.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

# -------------------------------------------------------------
# Helper: colored Write-Host with graceful fallback
# -------------------------------------------------------------
function Say {
    param([string]$Text, [string]$Color = "White", [string]$Prefix = "")
    if ($NoColor) {
        if ($Prefix) { Write-Host "[$Prefix] $Text" }
        else         { Write-Host $Text }
    } else {
        if ($Prefix) { Write-Host "[$Prefix] $Text" -ForegroundColor $Color }
        else         { Write-Host $Text -ForegroundColor $Color }
    }
}

# -------------------------------------------------------------
# Build a log file path alongside the script. We tee ALL output
# there so the user can paste the contents straight to us.
# -------------------------------------------------------------
$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }

$logDir  = Join-Path $scriptDir ".build-logs"
$stamp   = Get-Date -Format "yyyyMMdd-HHmmss"
$logFile = Join-Path $logDir "build-$stamp.log"
$errFile = Join-Path $logDir "last-error.log"

if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }

# Make sure previous "last-error.log" is cleared so we never read
# an old one if the new build accidentally succeeds.
if (Test-Path $errFile) { Remove-Item $errFile -Force }

# -------------------------------------------------------------
# Log header
# -------------------------------------------------------------
Say "=============================================================" "Cyan"
Say "  LanFileShare build script" "Cyan"
Say "  Version:  $Version" "Cyan"
Say "  Log file: $logFile" "DarkGray"
Say "=============================================================" "Cyan"
Say ""

# Tee every line that follows into both console and logFile.
# We wrap dotnet invocation in a custom function for this.
# IMPORTANT: $Arguments must be an ARRAY of strings (one arg per slot).
# We then use & $Exe @Arguments (splatting) so each token stays its own argv entry.
function Run-Cmd {
    param(
        [string]$Label,
        [string]$Exe,
        [string[]]$Arguments = @()
    )
    $cmdLine = $Exe + " " + ($Arguments -join " ")
    Say "[RUN ] $Label" "Cyan"
    Say "       $cmdLine" "DarkGray"
    Add-Content -Path $logFile -Value "[RUN ] $cmdLine"

    $ErrorActionPreference = "Continue"
    # Splat array with @Arguments so each slot becomes its own argv entry.
    # Writing & $Exe $Arguments would also work but the @ sigil is explicit.
    $output = & $Exe @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = "Stop"

    foreach ($line in $output) {
        $lineStr = "$line"
        Add-Content -Path $logFile -Value $lineStr

        if ($lineStr -match "error CS\d+:") {
            Say "       $lineStr" "Red"
        }
        elseif ($lineStr -match "warning CS\d+:") {
            Say "       $lineStr" "Yellow"
        }
        elseif ($lineStr -match "MSB\d+:") {
            Say "       $lineStr" "Red"
        }
        else {
            Say "       $lineStr" "Gray"
        }
    }
    Add-Content -Path $logFile -Value "[EXIT] $exitCode"
    return $exitCode
}

# -------------------------------------------------------------
# Step 1: check dotnet
# -------------------------------------------------------------
$dotnet = (& dotnet --version) 2>$null
if (-not $dotnet) {
    Say "ERROR: dotnet not found." "Red" "ERR"
    Say "Install .NET 8 SDK from https://dotnet.microsoft.com/download" "Yellow"
    exit 1
}
Say ".NET SDK version: $dotnet" "Green" "OK1"
Add-Content -Path $logFile -Value "[INFO] dotnet $dotnet"

# -------------------------------------------------------------
# Step 2: cd to project root
# -------------------------------------------------------------
Set-Location $scriptDir
Say ""
Add-Content -Path $logFile -Value "[INFO] cwd = $scriptDir"

# -------------------------------------------------------------
# Step 3: kill leftover LanFileShare.exe so the publish step can
# overwrite publish/LanFileShare.exe without sharing-violation.
# -------------------------------------------------------------
Say "Checking for leftover LanFileShare.exe processes ..." "Cyan" "..."
Add-Content -Path $logFile -Value "[INFO] Checking leftover processes"
$running = Get-Process -Name "LanFileShare" -ErrorAction SilentlyContinue
if ($running) {
    Say "Found $($running.Count) old process(es), killing ..." "Yellow" "!!"
    Add-Content -Path $logFile -Value "[INFO] Found $($running.Count) leftover processes"
    $running | ForEach-Object {
        try {
            Stop-Process -Id $_.Id -Force -ErrorAction Stop
            Say "  killed PID $($_.Id)" "Gray"
            Add-Content -Path $logFile -Value "[INFO] killed PID $($_.Id)"
        } catch {
            Say "  failed to kill PID $($_.Id): $($_.Exception.Message)" "Red"
            Add-Content -Path $logFile -Value "[ERROR] failed to kill PID $($_.Id): $($_.Exception.Message)"
        }
    }
    Start-Sleep -Seconds 1
    $stillRunning = Get-Process -Name "LanFileShare" -ErrorAction SilentlyContinue
    if ($stillRunning) {
        Say "Could not kill old process. Please close LanFileShare manually." "Red" "ERR"
        Add-Content -Path $logFile -Value "[FATAL] could not kill old process"
        exit 1
    }
} else {
    Say "no leftover processes" "Green" "OK2"
    Add-Content -Path $logFile -Value "[INFO] no leftover processes"
}

# -------------------------------------------------------------
# Step 4: clean previous outputs (no errors expected here)
# -------------------------------------------------------------
$publishDir = Join-Path $scriptDir "publish"
$distDir    = Join-Path $scriptDir "dist"
foreach ($d in @($publishDir, $distDir)) {
    if (Test-Path $d) {
        Remove-Item -Recurse -Force $d
        Say "  removed $d" "Gray"
        Add-Content -Path $logFile -Value "[INFO] removed $d"
    }
}

# -------------------------------------------------------------
# Step 5: dotnet publish
# -------------------------------------------------------------
Say ""
Say "Running dotnet publish (this may take 30-60s) ..." "Cyan" "..."

# Pass parameters as a proper array. Note: paths with spaces are
# double-quoted per-item, NOT escaped with `\``.  & dotnet @{...}
# or & dotnet @(...) keeps each argv slot intact.
$publishArgs = @(
    "publish"
    "-c", "Release"
    "-r", "win-x64"
    "--self-contained", "true"
    "/p:PublishSingleFile=true"
    "/p:IncludeNativeLibrariesForSelfExtract=true"
    "/p:EnableCompressionInSingleFile=true"
    "/p:DebugType=embedded"
    "/p:Version=$Version"
    "/p:AssemblyVersion=$Version.0"
    "/p:FileVersion=$Version.0"
    ".\LanFileShare\LanFileShare.csproj"
    "-o", "$publishDir"
)

$exitCode = Run-Cmd -Label "dotnet publish" -Exe "dotnet" -Arguments $publishArgs

if ($exitCode -ne 0) {
    Say ""
    Say "=================== BUILD FAILED ===================" "Red"
    Say "" "White"
    Say "The full log was written to:" "Yellow"
    Say "  $logFile" "White"
    Say "" "White"
    Say "Here's the same content, ready to paste:" "Yellow"

    # Dump the entire log to console AND to last-error.log so user
    # can grab it from either place.
    Get-Content $logFile | ForEach-Object {
        Write-Host $_
        Add-Content -Path $errFile -Value $_
    }

    Say "" "White"
    Say "=================== END OF ERROR LOG ===================" "Red"
    Say "" "White"
    Say "How to share this error:" "Yellow"
    Say "  Option A: Drag the file `$errFile = '$errFile'` into chat" "White"
    Say "  Option B: Copy the output above (between the === markers)" "White"
    Say "  Option C: Run `type `"$errFile`"` and paste the result" "White"
    exit $exitCode
}

# -------------------------------------------------------------
# Step 6: report EXE size
# -------------------------------------------------------------
$exe = Get-Item (Join-Path $publishDir "LanFileShare.exe") -ErrorAction SilentlyContinue
if (-not $exe) {
    Say "LanFileShare.exe not found in publish/. Build may have produced something else." "Red" "ERR"
    Add-Content -Path $logFile -Value "[ERROR] no LanFileShare.exe in $publishDir"
    exit 1
}
$exeMB = "{0:N2}" -f ($exe.Length / 1MB)
Say "EXE generated: LanFileShare.exe ($exeMB MB)" "Green" "OK3"
Add-Content -Path $logFile -Value "[INFO] EXE size: $exeMB MB"

# -------------------------------------------------------------
# Step 7: create ZIP for distribution
# -------------------------------------------------------------
if (-not $SkipZip) {
    Say ""
    Say "Creating ZIP archive ..." "Cyan" "..."
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null

    $zipPath = Join-Path $distDir "LanFileShare-v$Version-win-x64.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath }

    try {
        Compress-Archive -Path "$publishDir\LanFileShare.exe" -DestinationPath $zipPath
    } catch {
        Say "ZIP creation failed: $($_.Exception.Message)" "Red" "ERR"
        Add-Content -Path $logFile -Value "[ERROR] zip failed: $($_.Exception.Message)"
        exit 1
    }

    $zipMB = "{0:N2}" -f ((Get-Item $zipPath).Length / 1MB)
    Say "ZIP: $zipPath ($zipMB MB)" "Green" "OK4"
    Add-Content -Path $logFile -Value "[INFO] ZIP size: $zipMB MB"
} else {
    Say "Skipping ZIP (per -SkipZip switch)" "Yellow"
    Add-Content -Path $logFile -Value "[INFO] skipped ZIP"
}

# -------------------------------------------------------------
# Success summary
# -------------------------------------------------------------
Say ""
Say "=============================================================" "Cyan"
Say "  BUILD COMPLETE" "Green"
Say "=============================================================" "Cyan"
Say "Distribution:  $distDir\LanFileShare-v$Version-win-x64.zip" "White"
Say "Raw EXE:       $publishDir\LanFileShare.exe" "White"
Say "EXE timestamp: $((Get-Item $publishDir\LanFileShare.exe).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))" "White"
Say "Full log:      $logFile" "DarkGray"
Say "" "White"
Say "Next: double-click the EXE inside the ZIP, send it to users via WeChat / email / LAN share." "Yellow"
Say "NOTE: if you already had an EXE running (in tray or visible)," "Yellow"
Say "      kill it first: taskkill /f /im LanFileShare.exe" "Yellow"
Say "" "White"
