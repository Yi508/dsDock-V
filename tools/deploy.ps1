# Deploy dsDock to a folder OUTSIDE the session workspace, then verify it can register
# a tray icon.
#
# WHY: processes started from inside the session workspace (e.g. D:\dshwk001\...) are
# forced to Low integrity by the environment's policy. A Low integrity process cannot
# register a tray icon (Shell_NotifyIcon -> ERROR_ACCESS_DENIED). It is NOT about the drive
# letter: any folder outside the workspace works, e.g. D:\dsDock or %USERPROFILE%\dsDock.
#
# ASCII ONLY on purpose: PowerShell 5.1 reads a UTF-8 file without BOM as ANSI, and
# non-ASCII characters then break parsing.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\deploy.ps1
#   powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -Target D:\Apps\dsDock
#   powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 -NoLaunch
param(
    [string]$Target,
    [switch]$NoLaunch,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$app = Join-Path $root 'src\DsDock.App\bin\Release\net8.0-windows'
$exe = Join-Path $app 'DsDock.exe'

# Default: <drive of the workspace>\dsDock  - outside the workspace, not necessarily C:.
if (-not $Target) {
    $drive = [System.IO.Path]::GetPathRoot($root)
    $Target = Join-Path $drive 'dsDock'
}
$Target = [System.IO.Path]::GetFullPath($Target)

# Refuse to deploy back into the workspace: that is exactly what breaks the tray icon.
if ($Target.TrimEnd('\').StartsWith($root.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "target is inside the workspace ($root) - the tray icon will not work there." -ForegroundColor Red
    Write-Host 'choose a folder outside it, e.g. -Target D:\dsDock' -ForegroundColor Red
    exit 8
}

if (-not $SkipBuild) {
    Write-Host 'building...'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Host 'build failed' -ForegroundColor Red; exit $LASTEXITCODE }
}
if (-not (Test-Path $exe)) { Write-Host "exe not found: $exe" -ForegroundColor Red; exit 9 }

New-Item -ItemType Directory -Force $Target | Out-Null

# 1) application files
Copy-Item (Join-Path $app '*') $Target -Recurse -Force
# 2) card registry + plugin folders (the app looks for them next to the exe)
Copy-Item (Join-Path $root 'registry.json') $Target -Force
Copy-Item (Join-Path $root 'Cards') $Target -Recurse -Force
# 3) carry existing data over (settings.json / layout.json / cards/) so nothing is lost
$data = Join-Path $app 'data'
if (Test-Path $data) {
    Copy-Item $data (Join-Path $Target 'data') -Recurse -Force
    Write-Host 'copied existing settings and card data'
}

$targetExe = Join-Path $Target 'DsDock.exe'
$logs = Join-Path $Target 'data\logs'
Write-Host ''
Write-Host "deployed to: $Target" -ForegroundColor Green

if ($NoLaunch) {
    Write-Host "run: $targetExe"
    exit 0
}

# launch, then read back the integrity level the process actually got
Get-Process -Name DsDock -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Start-Sleep -Milliseconds 800
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $targetExe
$psi.UseShellExecute = $false
[System.Diagnostics.Process]::Start($psi) | Out-Null
Start-Sleep -Seconds 5

$log = Get-ChildItem $logs -Filter *.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) {
    $line = (Get-Content $log.FullName -Encoding UTF8 | Select-String -Pattern 'Integrity|' | Select-Object -First 0)
    $all = Get-Content $log.FullName -Encoding UTF8
    $integrity = ($all | Select-String -Pattern '0x1000|0x2000' | Select-Object -First 1)
    $tray = ($all | Select-String -Pattern 'NIM_ADD|Shell_NotifyIcon' | Select-Object -First 1)
    Write-Host ''
    Write-Host "log: $($log.Name)"
    if ($all -match '0x2000' -and $all -match 'Low') {
        # both texts can appear in one file (diagnostics), prefer the explicit Medium marker
    }
    if ($all | Select-String -Pattern 'Medium \(0x2000\)') {
        Write-Host 'integrity: Medium -> tray icon should work (if not visible, open the ^ overflow area).' -ForegroundColor Green
    } else {
        Write-Host 'integrity: still Low -> this location is confined too; try another folder outside the workspace.' -ForegroundColor Yellow
        Write-Host '           (the app itself still runs; only the tray icon needs Medium).' -ForegroundColor Yellow
    }
}
Write-Host "run: $targetExe"
