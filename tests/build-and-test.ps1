# Builds the plugin (Release) and runs the offline checks against the real game assemblies, without starting the game:
# every [GamePatch] target, overload and injected parameter, and every game member the plugin looks up by name, must
# exist in the game. Success ends with "ALL CHECKS PASSED" and exit code 0.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tests\build-and-test.ps1 [-VoidCrewDir <game folder>] [-BepInExDir <BepInEx folder>]
param(
    [string]$VoidCrewDir = "G:\SteamLibrary\steamapps\common\Void Crew",
    [string]$BepInExDir = (Join-Path $env:APPDATA "raicuparta\rai-pal\data\installed-mods\3743495372862092943\bepinex\BepInEx")
)

$root = Split-Path $PSScriptRoot -Parent
$vs = "C:\Program Files\Microsoft Visual Studio\18\Community"
$msbuild = Join-Path $vs "MSBuild\Current\Bin\MSBuild.exe"
$csc = Join-Path $vs "MSBuild\Current\Bin\Roslyn\csc.exe"

Write-Output "== Building the plugin (Release)"
& $msbuild (Join-Path $root "VoidCrewFleet\VoidCrewFleet.csproj") -p:Configuration=Release -nologo -v:minimal -t:Rebuild "-p:VoidCrewDir=$VoidCrewDir" "-p:BepInExDir=$BepInExDir"
if ($LASTEXITCODE -ne 0) {
    Write-Output "BUILD FAILED"
    exit 1
}

Write-Output "== Building the offline checker"
$out = Join-Path ([System.IO.Path]::GetTempPath()) "VoidCrewFleet-checks"
New-Item -ItemType Directory $out -Force | Out-Null
$cecil = Join-Path $BepInExDir "core\Mono.Cecil.dll"
Copy-Item $cecil $out -Force
& $csc -nologo -target:exe -platform:x64 "-r:$cecil" "-out:$(Join-Path $out 'FleetCheck.exe')" (Join-Path $PSScriptRoot "OfflineChecks\FleetCheck.cs")
if ($LASTEXITCODE -ne 0) {
    Write-Output "CHECKER BUILD FAILED"
    exit 1
}

Write-Output "== Building the crew rules tests"
& $csc -nologo -target:exe -platform:x64 "-out:$(Join-Path $out 'CrewRulesTest.exe')" (Join-Path $PSScriptRoot "OfflineChecks\CrewRulesTest.cs") (Join-Path $root "VoidCrewFleet\CrewRules.cs") (Join-Path $root "VoidCrewFleet\RosterRules.cs") (Join-Path $root "VoidCrewFleet\RoomCopyCheck.cs")
if ($LASTEXITCODE -ne 0) {
    Write-Output "CREW RULES TESTS BUILD FAILED"
    exit 1
}
& (Join-Path $out "CrewRulesTest.exe")
if ($LASTEXITCODE -ne 0) {
    exit 1
}

Write-Output "== Building the hub checks (the host's copy of the fleet choices, players waiting in the crew list)"
& $csc -nologo -target:exe -platform:x64 "-out:$(Join-Path $out 'HubChecksTest.exe')" (Join-Path $PSScriptRoot "OfflineChecks\HubChecksTest.cs") (Join-Path $root "VoidCrewFleet\RoomChoices.cs") (Join-Path $root "VoidCrewFleet\RoomCopyCheck.cs") (Join-Path $root "VoidCrewFleet\CrewRules.cs")
if ($LASTEXITCODE -ne 0) {
    Write-Output "HUB CHECKS BUILD FAILED"
    exit 1
}
& (Join-Path $out "HubChecksTest.exe")
if ($LASTEXITCODE -ne 0) {
    exit 1
}

Write-Output "== Building the enemy budget tests"
& $csc -nologo -target:exe -platform:x64 "-out:$(Join-Path $out 'SpawnBudgetTest.exe')" (Join-Path $PSScriptRoot "OfflineChecks\SpawnBudgetTest.cs") (Join-Path $root "VoidCrewFleet\SpawnBudget.cs")
if ($LASTEXITCODE -ne 0) {
    Write-Output "ENEMY BUDGET TESTS BUILD FAILED"
    exit 1
}
& (Join-Path $out "SpawnBudgetTest.exe")
if ($LASTEXITCODE -ne 0) {
    exit 1
}

Write-Output "== Running the offline checks"
& (Join-Path $out "FleetCheck.exe") (Join-Path $root "VoidCrewFleet\bin\Release\VoidCrewFleet.dll") (Join-Path $VoidCrewDir "Void Crew_Data\Managed")
exit $LASTEXITCODE
