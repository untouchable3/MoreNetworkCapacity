$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $projectRoot 'artifacts\smoke\tmpe-overlays'
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$managedDir = 'D:\steam\steamapps\common\Cities_Skylines\Cities_Data\Managed'
$tmpeDir = 'D:\steam\steamapps\workshop\content\255710\1637663252'
$harmonyDir = 'D:\steam\steamapps\workshop\content\255710\2040656402'
$harmonyDll = Join-Path $harmonyDir 'CitiesHarmony.Harmony.dll'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$testExe = Join-Path $testDir 'TMPEOverlayTests.exe'
$compilerArgs = @('/nologo', '/target:exe', '/optimize+', "/out:$testExe", "/reference:$harmonyDll")
foreach ($dll in @('Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll')) {
    $file = Join-Path $managedDir $dll
    $compilerArgs += '/reference:' + $file
    Copy-Item -LiteralPath $file -Destination $testDir -Force
}
Copy-Item -LiteralPath $harmonyDll -Destination $testDir -Force
foreach ($file in @('Source\Patches\NetworkSimulationSlicePatch.cs', 'Source\Compatibility\TMPENetworkArrayPatches.cs',
    'Source\Compatibility\TMPEOverlayRanges.cs', 'Tools\Test-TMPEOverlays.cs')) {
    $compilerArgs += Join-Path $projectRoot $file
}
& $compiler $compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'TMPE overlay tests failed to compile.' }
& $testExe $managedDir $tmpeDir $harmonyDir
if ($LASTEXITCODE -ne 0) { throw 'TMPE overlay tests failed.' }
