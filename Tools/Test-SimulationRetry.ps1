$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $projectRoot 'artifacts\smoke\simulation-retry'
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$managedDir = 'D:\steam\steamapps\common\Cities_Skylines\Cities_Data\Managed'
$harmonyDll = 'D:\steam\steamapps\workshop\content\255710\2040656402\CitiesHarmony.Harmony.dll'
$testExe = Join-Path $testDir 'SimulationRetryTests.exe'
$compilerArgs = @('/nologo', '/target:exe', "/out:$testExe", "/reference:$harmonyDll")
foreach ($dll in @('Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll')) {
    $dllPath = Join-Path $managedDir $dll
    $compilerArgs += '/reference:' + $dllPath
    Copy-Item -LiteralPath $dllPath -Destination $testDir -Force
}
Copy-Item -LiteralPath $harmonyDll -Destination $testDir -Force
foreach ($file in @('Source\Compatibility\AdaptiveRoadsCompatibility.cs',
    'Source\Compatibility\CompatibilityRetryGate.cs', 'Source\Patches\DeferredLoopLimitActivationPatch.cs',
    'Tools\Test-SimulationRetry.cs')) { $compilerArgs += Join-Path $projectRoot $file }
& $compiler $compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Simulation retry test compilation failed.' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Simulation retry tests failed.' }
