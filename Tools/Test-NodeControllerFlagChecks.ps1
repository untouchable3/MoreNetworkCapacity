$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $projectRoot 'artifacts\smoke\nc-flag-checks'
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$managedDir = 'D:\steam\steamapps\common\Cities_Skylines\Cities_Data\Managed'
$ncDir = 'D:\steam\steamapps\workshop\content\255710\2472062376'
$harmonyDir = 'D:\steam\steamapps\workshop\content\255710\2040656402'
$harmonyDll = Join-Path $harmonyDir 'CitiesHarmony.Harmony.dll'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$testExe = Join-Path $testDir 'NodeControllerFlagTests.exe'
$compilerArgs = @('/nologo', '/target:exe', '/optimize+', "/out:$testExe", "/reference:$harmonyDll")
foreach ($dll in @('Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll')) {
    $file = Join-Path $managedDir $dll
    $compilerArgs += '/reference:' + $file
    Copy-Item -LiteralPath $file -Destination $testDir -Force
}
Copy-Item -LiteralPath $harmonyDll -Destination $testDir -Force
$compilerArgs += Join-Path $projectRoot 'Source\Compatibility\NodeControllerFlagChecks.cs'
$compilerArgs += Join-Path $projectRoot 'Tools\Test-NodeControllerFlagChecks.cs'
& $compiler $compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'NC flag tests failed to compile.' }
& $testExe $managedDir $ncDir $harmonyDir
if ($LASTEXITCODE -ne 0) { throw 'NC flag tests failed.' }
