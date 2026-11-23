$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$out = Join-Path $project 'artifacts\smoke\node-simulation-phase'
New-Item -ItemType Directory -Force $out | Out-Null
$managed = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\common\Cities_Skylines\Cities_Data\Managed"
$harmony = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\2040656402\CitiesHarmony.Harmony.dll"
$exe = Join-Path $out 'NodeSimulationPhaseTests.exe'
$argsList = @('/nologo','/target:exe','/optimize+',"/out:$exe","/reference:$harmony")
foreach($name in @('Assembly-CSharp.dll','ColossalManaged.dll','UnityEngine.dll')) {
    $dll = Join-Path $managed $name
    $argsList += '/reference:' + $dll
    Copy-Item -LiteralPath $dll -Destination $out -Force
}
Copy-Item -LiteralPath $harmony -Destination $out -Force
foreach($file in @('Source\Patches\NodeSimulationPhasePatch.cs','Source\Patches\NetworkSimulationSlicePatch.cs','Source\Patches\LoopLocalTypes.cs','Tools\Test-NodeSimulationPhase.cs')) { $argsList += Join-Path $project $file }
& "$env:WINDIR\Microsoft.NET\Framework64\v3.5\csc.exe" $argsList
if($LASTEXITCODE -ne 0) { throw 'Node phase test compilation failed' }
& $exe $managed (Split-Path -Parent $harmony)
if($LASTEXITCODE -ne 0) { throw 'Node phase tests failed' }
