# Builds the Release DLL and packs the Thunderstore upload:
#   dist\VoidCrewFleet-<version>.zip  with manifest.json, icon.png, README.md, CHANGELOG.md and the DLL at its root,
#   and dist\VoidCrewFleet.dll for a manual install.
# Usage: powershell -ExecutionPolicy Bypass -File tools\package.ps1 [-NoBuild]
param(
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

$version = (Select-String -Path (Join-Path $root "VoidCrewFleet\Plugin.cs") -Pattern 'Version = "([0-9]+\.[0-9]+\.[0-9]+)"').Matches[0].Groups[1].Value
$manifest = Get-Content (Join-Path $root "thunderstore\manifest.json") -Raw | ConvertFrom-Json
if ($manifest.version_number -ne $version) {
    throw "thunderstore\manifest.json says $($manifest.version_number) but Plugin.cs says $version; make them match."
}

if (-not $NoBuild) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "tests\build-and-test.ps1") | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) { throw "The build or the offline checks failed." }
}

$icon = Join-Path $root "thunderstore\icon.png"
if (-not (Test-Path $icon)) { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "make-icon.ps1") -Out $icon }

$dll = Join-Path $root "VoidCrewFleet\bin\Release\VoidCrewFleet.dll"
$stage = Join-Path ([System.IO.Path]::GetTempPath()) "VoidCrewFleet-package"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null
Copy-Item (Join-Path $root "thunderstore\manifest.json"), $icon, (Join-Path $root "README.md"), (Join-Path $root "CHANGELOG.md"), $dll -Destination $stage

$dist = Join-Path $root "dist"
New-Item -ItemType Directory $dist -Force | Out-Null
$zip = Join-Path $dist "VoidCrewFleet-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Copy-Item $dll (Join-Path $dist "VoidCrewFleet.dll") -Force
Remove-Item $stage -Recurse -Force
Write-Output "Packed $zip"
