$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $projectRoot 'artifacts\smoke\network-buffer-sizes'
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$managedDir = 'D:\steam\steamapps\common\Cities_Skylines\Cities_Data\Managed'
$harmonyDir = 'D:\steam\steamapps\workshop\content\255710\2040656402'
$harmonyDll = Join-Path $harmonyDir 'CitiesHarmony.Harmony.dll'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe = Join-Path $testDir 'NetworkBufferSizeTests.exe'
$compilerArgs = @('/nologo', '/target:exe', "/out:$testExe", "/reference:$harmonyDll")
foreach ($assembly in @('Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll')) {
    $compilerArgs += '/reference:' + (Join-Path $managedDir $assembly)
}
$compilerArgs += Join-Path $projectRoot 'Source\Patches\NetworkBufferSizePatch.cs'
$compilerArgs += Join-Path $PSScriptRoot 'Test-NetworkBufferSizes.cs'
& $compiler $compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Serialization test compilation failed.' }
# Resolve the directly referenced Harmony library before Main starts.
Copy-Item -LiteralPath $harmonyDll -Destination $testDir -Force
& $testExe $managedDir $harmonyDir
if ($LASTEXITCODE -ne 0) { throw 'Serialization tests failed.' }
