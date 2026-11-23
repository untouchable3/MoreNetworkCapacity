$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $projectRoot 'artifacts\smoke\simulation-data-read-state'
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$managedDir = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\common\Cities_Skylines\Cities_Data\Managed"
$harmonyDir = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\2040656402"
$harmonyDll = Join-Path $harmonyDir 'CitiesHarmony.Harmony.dll'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$testExe = Join-Path $testDir 'SimulationDataReadStateTests.exe'
$compilerArgs = @('/nologo', '/target:exe', '/optimize+', "/out:$testExe", "/reference:$harmonyDll")
foreach ($dll in @('Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll')) {
    $file = Join-Path $managedDir $dll
    $compilerArgs += '/reference:' + $file
    Copy-Item -LiteralPath $file -Destination $testDir -Force
}
Copy-Item -LiteralPath $harmonyDll -Destination $testDir -Force
foreach ($file in @('Source\Capacity.cs', 'Source\PatchState.cs', 'Source\Metadata.cs',
    'Source\Patches\SimulationDataReadStatePatch.cs', 'Tools\Test-SimulationDataReadState.cs')) {
    $compilerArgs += Join-Path $projectRoot $file
}
& $compiler $compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Simulation data read state tests failed to compile.' }
& $testExe $managedDir $harmonyDir
if ($LASTEXITCODE -ne 0) { throw 'Simulation data read state tests failed.' }
