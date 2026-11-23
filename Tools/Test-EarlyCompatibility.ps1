$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $projectRoot 'artifacts\smoke\early-compatibility'
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$managedDir = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\common\Cities_Skylines\Cities_Data\Managed"
$tilesDir = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\2862121823"
$zoningDir = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\2389414419"
$harmonyDir = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\2040656402"
$harmonyDll = Join-Path $harmonyDir 'CitiesHarmony.Harmony.dll'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$testExe = Join-Path $testDir 'EarlyCompatibilityTests.exe'
$compilerArgs = @('/nologo', '/target:exe', '/optimize+', "/out:$testExe", "/reference:$harmonyDll")
foreach ($dll in @('Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll')) {
    $file = Join-Path $managedDir $dll
    $compilerArgs += '/reference:' + $file
    Copy-Item -LiteralPath $file -Destination $testDir -Force
}
Copy-Item -LiteralPath $harmonyDll -Destination $testDir -Force
foreach ($file in @('Source\Patches\NetworkSimulationSlicePatch.cs', 'Source\Compatibility\ArrayCompatibility.cs',
    'Source\Compatibility\EightyOne2SimulationRanges.cs', 'Source\Compatibility\EightyOne2Compatibility.cs',
    'Source\Compatibility\EightyOne2WaterRecovery.cs',
    'Source\Compatibility\EightyOne2ElectricityRecovery.cs',
    'Source\Compatibility\ZoningAdjusterCompatibility.cs', 'Tools\Test-EarlyCompatibility.cs')) {
    $compilerArgs += Join-Path $projectRoot $file
}
& $compiler $compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Early compatibility tests failed to compile.' }
& $testExe $managedDir $tilesDir $zoningDir $harmonyDir
if ($LASTEXITCODE -ne 0) { throw 'Early compatibility tests failed.' }
