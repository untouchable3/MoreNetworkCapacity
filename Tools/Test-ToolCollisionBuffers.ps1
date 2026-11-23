$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $projectRoot 'artifacts\smoke\tool-collision-buffers'
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$managed = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\common\Cities_Skylines\Cities_Data\Managed"
$harmonyDir = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\2040656402"
$harmony = Join-Path $harmonyDir 'CitiesHarmony.Harmony.dll'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$exe = Join-Path $testDir 'ToolCollisionBufferTests.exe'
$compileArgs = @('/nologo', '/target:exe', '/optimize+', "/out:$exe", "/reference:$harmony")
foreach ($name in @('Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll')) {
    $file = Join-Path $managed $name
    $compileArgs += '/reference:' + $file
    Copy-Item -LiteralPath $file -Destination $testDir -Force
}
Copy-Item -LiteralPath $harmony -Destination $testDir -Force
$compileArgs += Join-Path $projectRoot 'Source\Patches\ToolCollisionBuffersPatch.cs'
$compileArgs += Join-Path $projectRoot 'Source\Patches\AuxiliaryArrayPatch.cs'
$compileArgs += Join-Path $PSScriptRoot 'Test-ToolCollisionBuffers.cs'
& $compiler $compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Tool collision tests failed to compile.' }
& $exe $managed $harmonyDir
if ($LASTEXITCODE -ne 0) { throw 'Tool collision tests failed.' }
