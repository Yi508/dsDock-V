# Build dsDock (offline: the project has zero third party NuGet dependencies).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File build.ps1
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Configuration Debug
#
# NOTE: this script builds the csproj and stages every registered card into Cards\<id>\.
# There is intentionally NO .sln in this repo: cards are plugins, so the script (not a
# solution) is the authoritative build entry point.
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Keep the CLI home and package cache inside the repo instead of the user profile.
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

New-Item -ItemType Directory -Force $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES | Out-Null

# Resolve dotnet explicitly: a double clicked .cmd inherits the environment from logon,
# so a freshly installed SDK may not be on PATH yet.
$dotnet = $null
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd) { $dotnet = $cmd.Source }
if (-not $dotnet) {
    foreach ($candidate in @("$env:ProgramFiles\dotnet\dotnet.exe",
                             "${env:ProgramFiles(x86)}\dotnet\dotnet.exe",
                             "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")) {
        if ($candidate -and (Test-Path $candidate)) { $dotnet = $candidate; break }
    }
}
if (-not $dotnet) {
    Write-Host 'dotnet not found. Install the .NET 8 SDK (x64) and reopen the terminal.' -ForegroundColor Red
    exit 9
}
$env:PATH = (Split-Path -Parent $dotnet) + ';' + $env:PATH

$csproj = Join-Path $root 'src\DsDock.App\DsDock.App.csproj'
Write-Host "building $csproj ($Configuration)"
& $dotnet build $csproj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# ---------------------------------------------------------------------------
# Card plugins: build each src/Cards/<name> and stage it into <repo>/Cards/<manifest.id>/
# Rules enforced here (see docs/M2-施工方案.md 7.3):
#   * target folder name comes from manifest "id", never from the source folder name
#   * DsDock.Card.Abstractions.dll must NOT end up in the plugin folder
#   * registry.json and Cards/ must agree
# ---------------------------------------------------------------------------
$cardsRoot = Join-Path $root 'Cards'
$sourceRoot = Join-Path $root 'src\Cards'
$registryFile = Join-Path $root 'registry.json'
New-Item -ItemType Directory -Force $cardsRoot | Out-Null

$ids = @()
if (Test-Path $registryFile) {
    $ids = @((Get-Content -Raw -Encoding UTF8 $registryFile | ConvertFrom-Json).cards)
}
$staged = @()

foreach ($dir in (Get-ChildItem $sourceRoot -Directory -ErrorAction SilentlyContinue)) {
    $manifestPath = Join-Path $dir.FullName 'manifest.json'
    if (-not (Test-Path $manifestPath)) { Write-Host "skip $($dir.Name): no manifest.json" -ForegroundColor Yellow; continue }
    $manifest = Get-Content -Raw -Encoding UTF8 $manifestPath | ConvertFrom-Json
    $id = $manifest.id
    if ($ids -notcontains $id) { Write-Host "skip $($dir.Name): id '$id' is not registered in registry.json" -ForegroundColor Yellow; continue }

    $proj = Get-ChildItem $dir.FullName -Filter *.csproj | Select-Object -First 1
    if ($proj) {
        & $dotnet build $proj.FullName -c $Configuration --nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Host "card build FAILED: $id" -ForegroundColor Red; exit $LASTEXITCODE }
    }

    $outDir = Join-Path $dir.FullName "bin\$Configuration\net8.0-windows"
    if (-not (Test-Path $outDir)) { Write-Host "card output missing: $id ($outDir)" -ForegroundColor Red; exit 10 }
    $entry = Join-Path $outDir $manifest.entry
    if (-not (Test-Path $entry)) { Write-Host "card entry missing: $entry" -ForegroundColor Red; exit 11 }

    $target = Join-Path $cardsRoot $id
    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item $entry $target -Force
    Copy-Item $manifestPath $target -Force
    if ($manifest.icon) {
        $icon = Join-Path $dir.FullName $manifest.icon
        if (Test-Path $icon) { Copy-Item $icon $target -Force }
        else { Write-Host "warn: $id icon '$($manifest.icon)' not found" -ForegroundColor Yellow }
    }
    if (Test-Path (Join-Path $target 'DsDock.Card.Abstractions.dll')) {
        Write-Host "ERROR: DsDock.Card.Abstractions.dll was copied into Cards/$id - this breaks the plugin ABI" -ForegroundColor Red
        exit 12
    }
    Write-Host "staged Cards/$id/ <- src/Cards/$($dir.Name) ($($manifest.entry))"
    $staged += $id
}

$missing = @($ids | Where-Object { $staged -notcontains $_ })
if ($missing.Count -gt 0) {
    Write-Host "registered but not staged: $($missing -join ', ')" -ForegroundColor Yellow
}
exit 0
