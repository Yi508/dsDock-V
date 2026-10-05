# Build the app, stage the payload, embed it into the installer, and build the single-file SFX.
# ASCII only on purpose: Windows PowerShell 5.1 reads UTF-8 .ps1 as ANSI, so non-ASCII here would be mangled.
#
#   .\make-installer.ps1                    -> dist\dsDock-Setup.exe (single file) + install-readme.txt
#   .\make-installer.ps1 -KeepSetupFolder   -> also leave dist\DsDockSetup\ (needed for --silent installs)
param([switch]$KeepSetupFolder)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$appOut = Join-Path $root 'src\DsDock.App\bin\Release\net8.0-windows'
$setupProj = Join-Path $root 'src\DsDock.Setup\DsDock.Setup.csproj'
$payloadStage = Join-Path $env:TEMP 'dsdock-payload-stage'
$payloadZip = Join-Path $root 'src\DsDock.Setup\payload.zip'
$setupStage = Join-Path $env:TEMP 'dsdock-setup-stage'
$sfxStage = Join-Path $env:TEMP 'dsdock-sfx-stage'
$dist = Join-Path $root 'dist'

# Keep CLI caches out of the user profile (works in restricted shells too).
$env:DOTNET_CLI_HOME = Join-Path $env:TEMP 'dsdock-build\home'
$env:NUGET_PACKAGES = Join-Path $env:TEMP 'dsdock-build\packages'
$env:APPDATA = Join-Path $env:TEMP 'dsdock-build\appdata'

Write-Host '[1/5] build app + cards'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'build.ps1 failed' }
if (-not (Test-Path (Join-Path $appOut 'DsDock.exe'))) { throw "missing $appOut\DsDock.exe" }

Write-Host '[2/5] stage payload (app files + registry.json + Cards)'
if (Test-Path $payloadStage) { Remove-Item $payloadStage -Recurse -Force }
New-Item -ItemType Directory -Force $payloadStage | Out-Null
Get-ChildItem $appOut -Force | Where-Object { $_.Name -ne 'data' -and $_.Extension -ne '.pdb' } | ForEach-Object {
    Copy-Item $_.FullName $payloadStage -Recurse -Force
}
Copy-Item (Join-Path $root 'registry.json') $payloadStage -Force
Copy-Item (Join-Path $root 'Cards') $payloadStage -Recurse -Force
New-Item -ItemType Directory -Force (Join-Path $payloadStage 'data') | Out-Null

if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Compress-Archive -Path (Join-Path $payloadStage '*') -DestinationPath $payloadZip -CompressionLevel Optimal
Write-Host ("      payload: {0} files, {1:N2} MB" -f (Get-ChildItem $payloadStage -Recurse -File).Count, ((Get-Item $payloadZip).Length / 1MB))

Write-Host '[3/5] build installer'
& dotnet build $setupProj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'setup build failed' }
if (Test-Path $setupStage) { Remove-Item $setupStage -Recurse -Force }
New-Item -ItemType Directory -Force $setupStage | Out-Null
Get-ChildItem (Join-Path $root 'src\DsDock.Setup\bin\Release\net8.0-windows') -Force |
    Where-Object { $_.Name -ne 'payload.zip' -and $_.Extension -ne '.pdb' } |
    ForEach-Object { Copy-Item $_.FullName $setupStage -Recurse -Force }

Write-Host '[4/5] build single-file self-extracting installer (iexpress, no download needed)'
$iexpress = Join-Path $env:WINDIR 'System32\iexpress.exe'
$single = Join-Path $dist 'dsDock-Setup.exe'
New-Item -ItemType Directory -Force $dist | Out-Null
if (Test-Path $iexpress) {
    if (Test-Path $sfxStage) { Remove-Item $sfxStage -Recurse -Force }
    New-Item -ItemType Directory -Force $sfxStage | Out-Null

    $payloadFiles = Get-ChildItem $setupStage -File
    $strings = ($payloadFiles | ForEach-Object -Begin { $i = 0 } -Process { "FILE$i=`"$($_.Name)`""; $i++ }) -join "`n"
    $entries = ($payloadFiles | ForEach-Object -Begin { $i = 0 } -Process { "%FILE$i%=`n"; $i++ }) -join ""

    $sedPath = Join-Path $sfxStage 'build.sed'
    $sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$single
FriendlyName=dsDock
AppLaunched=DsDockSetup.exe
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[Strings]
$strings
[SourceFiles]
SourceFiles0=$setupStage\
[SourceFiles0]
$entries
"@
    Set-Content -Path $sedPath -Value $sed -Encoding ASCII
    if (Test-Path $single) { Remove-Item $single -Force }
    & $iexpress /N $sedPath | Out-Null
    Start-Sleep -Seconds 2
    if (Test-Path $single) { Write-Host ("      dsDock-Setup.exe {0:N0} bytes (single file)" -f (Get-Item $single).Length) }
    else { Write-Host '      WARNING: iexpress failed' }
} else {
    Write-Host '      iexpress is not available on this system'
}

# Optional: keep the folder layout for silent installs (DsDockSetup.exe --silent --dir ...)
$folderOut = Join-Path $dist 'DsDockSetup'
if ($KeepSetupFolder) {
    if (Test-Path $folderOut) { Remove-Item $folderOut -Recurse -Force }
    New-Item -ItemType Directory -Force $folderOut | Out-Null
    Copy-Item (Join-Path $setupStage '*') $folderOut -Recurse -Force
    Write-Host '      kept dist\DsDockSetup\ (silent install: DsDockSetup.exe --silent --dir "D:\dsDock")'
} elseif (Test-Path $folderOut) {
    Remove-Item $folderOut -Recurse -Force
}

Write-Host '[5/5] copy readme + clean intermediates'
Copy-Item (Join-Path $root 'docs\install-readme.txt') (Join-Path $dist 'install-readme.txt') -Force
Remove-Item $payloadStage, $setupStage, $sfxStage -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $payloadZip -Force -ErrorAction SilentlyContinue

Get-ChildItem $dist -Force | ForEach-Object { Write-Host ("      {0,-24} {1,12:N0} bytes" -f $_.Name, $(if ($_.PSIsContainer) { 0 } else { $_.Length })) }
Write-Host 'done: dist\dsDock-Setup.exe'
