# Run the automated self test in an ISOLATED data root, so it can never clobber the
# settings.json of a normal run (that was a real bug: the self test changes accent /
# alpha / corner / font and persists them).
#
# Usage:  powershell -ExecutionPolicy Bypass -File tools\validate.ps1
param(
    [int]$HoldMs = 0
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root 'src\DsDock.App\bin\Release\net8.0-windows\DsDock.exe'
$reports = Join-Path $root 'docs\selftest'
New-Item -ItemType Directory -Force $reports | Out-Null

if (-not (Test-Path $exe)) {
    Write-Host "exe not found, run build.cmd first: $exe" -ForegroundColor Red
    exit 9
}

$report = Join-Path $reports 'report.json'
Remove-Item $report, (Join-Path $reports 'report.txt'), "$report.hold", "$report.rect" -Force -ErrorAction SilentlyContinue

Get-Process -Name DsDock -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
Start-Sleep -Milliseconds 600

$args = @('--selftest', $report, '--force-data-root', (Join-Path $env:TEMP 'dsdock-selftest'))
if ($HoldMs -gt 0) { $args += @('--hold-ms', $HoldMs) }

Write-Host "running self test (isolated data root: $env:TEMP\dsdock-selftest)"
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.UseShellExecute = $false
$psi.Arguments = ($args | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
$p = [System.Diagnostics.Process]::Start($psi)
if (-not $p.WaitForExit(180000)) { Write-Host 'self test timed out' -ForegroundColor Red; $p.Kill(); exit 8 }

$txt = Join-Path $reports 'report.txt'
if (Test-Path $txt) { Get-Content $txt -Encoding UTF8 }
Write-Host "exit code: $($p.ExitCode)"
exit $p.ExitCode
