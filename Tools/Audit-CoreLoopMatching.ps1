param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if ($OutputDirectory -eq '') { $OutputDirectory = Join-Path $project 'artifacts\research\core-loop-screen-current' }
$build = Join-Path $project 'artifacts\smoke\core-loop-matching-audit'
New-Item -ItemType Directory -Force -Path $build | Out-Null
$managed = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\common\Cities_Skylines\Cities_Data\Managed"
$harmony = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\2040656402\CitiesHarmony.Harmony.dll"
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$exe = Join-Path $build 'CoreLoopMatchingAudit.exe'
$compileArgs = @('/nologo', '/target:exe', '/optimize+', "/out:$exe", "/reference:$harmony")
foreach ($name in @('Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll', 'ICities.dll')) {
    $file = Join-Path $managed $name
    $compileArgs += '/reference:' + $file
    Copy-Item -LiteralPath $file -Destination $build -Force
}
Copy-Item -LiteralPath $harmony -Destination $build -Force
foreach ($file in @('Source\Generated\PatchTargets.generated.cs', 'Source\Patches\CoreLoopPatch.cs',
    'Source\Patches\LoopLocalTypes.cs',
    'Source\Patches\TransportLineLoopBounds.cs', 'Source\Patches\TransportLineLoopBounds.Paired.cs',
    'Source\Patches\TransportLineMaintenanceBounds.cs',
    'Source\Patches\NetworkToolLoopBounds.cs',
    'Source\Patches\LaneChainLoopBounds.cs', 'Source\Patches\BuildingChainLoopBounds.cs',
    'Source\Patches\GridChainLoopBounds.cs',
    'Source\Patches\NetManagerLoopBounds.cs', 'Source\Patches\RoadAccessLoopBounds.cs',
    'Source\Patches\ServicePathLoopBounds.cs',
    'Source\Patches\CinematicLoopBounds.cs', 'Source\Patches\TransportLifecycleLoopBounds.cs',
    'Source\Patches\StationLaneLoopBounds.cs', 'Source\Patches\EventRaceLoopBounds.cs',
    'Tools\Audit-CoreLoopMatching.cs')) { $compileArgs += Join-Path $project $file }
& $compiler $compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Matching audit compilation failed.' }
& $exe $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Matching audit failed.' }
