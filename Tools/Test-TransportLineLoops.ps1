$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$output = Join-Path $project 'artifacts\smoke\transportline-loops'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$managed = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\common\Cities_Skylines\Cities_Data\Managed"
$harmony = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\2040656402\CitiesHarmony.Harmony.dll"
$exe = Join-Path $output 'TransportLineLoopTests.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$compileArgs = @('/nologo', '/target:exe', '/optimize+', "/out:$exe", "/reference:$harmony")
foreach ($name in @('Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll')) {
    $file = Join-Path $managed $name
    $compileArgs += '/reference:' + $file
    Copy-Item -LiteralPath $file -Destination $output -Force
}
Copy-Item -LiteralPath $harmony -Destination $output -Force
$compileArgs += Join-Path $project 'Source\Generated\PatchTargets.generated.cs'
$compileArgs += Join-Path $project 'Source\Patches\CoreLoopPatch.cs'
$compileArgs += Join-Path $project 'Source\Patches\LoopLocalTypes.cs'
$helper = Join-Path $project 'Source\Patches\TransportLineLoopBounds.cs'
if (Test-Path -LiteralPath $helper) { $compileArgs += $helper }
$compileArgs += Join-Path $project 'Source\Patches\TransportLineLoopBounds.Paired.cs'
$compileArgs += Join-Path $project 'Source\Patches\TransportLineMaintenanceBounds.cs'
$compileArgs += Join-Path $project 'Source\Patches\NetworkToolLoopBounds.cs'
$compileArgs += Join-Path $project 'Source\Patches\LaneChainLoopBounds.cs'
$compileArgs += Join-Path $project 'Source\Patches\BuildingChainLoopBounds.cs'
$compileArgs += Join-Path $project 'Source\Patches\GridChainLoopBounds.cs'
$compileArgs += Join-Path $project 'Source\Patches\NetManagerLoopBounds.cs'
$compileArgs += Join-Path $project 'Source\Patches\RoadAccessLoopBounds.cs'
$compileArgs += Join-Path $project 'Source\Patches\ServicePathLoopBounds.cs'
$compileArgs += Join-Path $project 'Source\Patches\CinematicLoopBounds.cs'
$compileArgs += Join-Path $project 'Source\Patches\TransportLifecycleLoopBounds.cs'
$compileArgs += Join-Path $project 'Source\Patches\StationLaneLoopBounds.cs'
$compileArgs += Join-Path $project 'Source\Patches\EventRaceLoopBounds.cs'
$compileArgs += Join-Path $PSScriptRoot 'Test-TransportLineLoops.cs'
$compileArgs += Join-Path $PSScriptRoot 'Test-TransportLineIntegration.cs'
$compileArgs += Join-Path $PSScriptRoot 'Test-TransportLineOrders.cs'
$compileArgs += Join-Path $PSScriptRoot 'Test-TransportLineStops.cs'
$compileArgs += Join-Path $PSScriptRoot 'Test-TransportLineSimulation.cs'
& $compiler $compileArgs
if ($LASTEXITCODE -ne 0) { throw 'TransportLine loop tests failed to compile.' }
$runArgs = @($args) + "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\3007903394\TransportLinesManager.dll"
& $exe $runArgs
if ($LASTEXITCODE -ne 0) { throw 'TransportLine loop tests failed.' }
