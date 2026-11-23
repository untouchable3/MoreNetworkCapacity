param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testDir = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $projectRoot 'artifacts\smoke\dcr-ranges' }
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$managedDir = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\common\Cities_Skylines\Cities_Data\Managed"
$imtDir = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\3743090419"
$harmonyDir = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\2040656402"
$harmonyDll = Join-Path $harmonyDir 'CitiesHarmony.Harmony.dll'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$testExe = Join-Path $testDir 'DirectConnectRoadsRangeTests.exe'
$compilerArgs = @('/nologo', '/target:exe', '/optimize+', "/out:$testExe", "/reference:$harmonyDll")
foreach ($dll in @('Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll')) {
    $file = Join-Path $managedDir $dll
    $compilerArgs += '/reference:' + $file
    Copy-Item -LiteralPath $file -Destination $testDir -Force
}
Copy-Item -LiteralPath $harmonyDll -Destination $testDir -Force
$compilerArgs += Join-Path $projectRoot 'Source\Compatibility\DirectConnectRoadsCompatibility.cs'
$compilerArgs += Join-Path $projectRoot 'Tools\Test-DirectConnectRoadsRanges.cs'
& $compiler $compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'DCR range tests failed to compile.' }
& $testExe $managedDir $imtDir $harmonyDir
if ($LASTEXITCODE -ne 0) { throw 'DCR range tests failed.' }
