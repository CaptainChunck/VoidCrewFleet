# Turns the VR mods (UUVR and Void Crew VR UI Fix) off or back on for testing Void Crew Fleet flat-screen.
# Nothing is deleted: "off" moves them into BepInEx\disabled-during-fleet-tests, "on" moves them back.
#   powershell -ExecutionPolicy Bypass -File tests\set-vr-mods.ps1 off
#   powershell -ExecutionPolicy Bypass -File tests\set-vr-mods.ps1 on
param(
    [Parameter(Mandatory = $true)][ValidateSet("on", "off", "status")][string]$State,
    [string]$BepInExDir = "$env:APPDATA\raicuparta\rai-pal\data\installed-mods\3743495372862092943\bepinex\BepInEx"
)
$ErrorActionPreference = "Stop"

if (Get-Process -Name "Void Crew" -ErrorAction SilentlyContinue) {
    throw "Void Crew is running; close it first."
}

$parked = Join-Path $BepInExDir "disabled-during-fleet-tests"
$items = @(
    "plugins\uuvr-mono-modern",
    "patchers\uuvr-mono-modern",
    "plugins\VoidCrewVRUIFix.dll"
)

foreach ($item in $items) {
    $live = Join-Path $BepInExDir $item
    $away = Join-Path $parked $item
    if ($State -eq "status") {
        $where = if (Test-Path $live) { "on" } elseif (Test-Path $away) { "off" } else { "missing" }
        Write-Host ("{0,-30} {1}" -f $item, $where)
        continue
    }
    $from = if ($State -eq "off") { $live } else { $away }
    $to = if ($State -eq "off") { $away } else { $live }
    if (-not (Test-Path $from)) {
        if (Test-Path $to) { Write-Host "$item is already $State." } else { Write-Host "$item is not installed; skipped." }
        continue
    }
    if (Test-Path $to) {
        if ($State -eq "on") { throw "Both $from and $to exist; sort them out by hand." }
        # The mod was reinstalled (e.g. by Rai Pal) while an older copy was parked: keep that older copy aside.
        $older = Join-Path $parked ("superseded-" + (Get-Date -Format "yyyyMMdd-HHmmss") + "\" + $item)
        New-Item -ItemType Directory -Force (Split-Path $older) | Out-Null
        Move-Item -LiteralPath $to -Destination $older
        Write-Host "$item : an older parked copy was moved to $older"
    }
    New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
    Move-Item -LiteralPath $from -Destination $to
    Write-Host "$item -> $State"
}

if ($State -eq "on" -and (Test-Path $parked) -and -not (Get-ChildItem $parked -Recurse -File)) {
    Remove-Item -LiteralPath $parked -Recurse -Force
}
