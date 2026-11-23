$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$output = Join-Path $project 'artifacts\smoke\transportline-maintenance'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$managed = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\common\Cities_Skylines\Cities_Data\Managed"
$harmony = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\2040656402\CitiesHarmony.Harmony.dll"
$exe = Join-Path $output 'TransportLineMaintenanceTests.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$compileArgs = @('/nologo', '/target:exe', '/optimize+', "/out:$exe", "/reference:$harmony")
foreach ($name in @('Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll')) {
    $file = Join-Path $managed $name
    $compileArgs += '/reference:' + $file
    Copy-Item -LiteralPath $file -Destination $output -Force
}
Copy-Item -LiteralPath $harmony -Destination $output -Force
$compileArgs += Join-Path $project 'Source\Patches\TransportLineLoopBounds.cs'
$compileArgs += Join-Path $project 'Source\Patches\TransportLineMaintenanceBounds.cs'
$compileArgs += Join-Path $PSScriptRoot 'Test-TransportLineMaintenance.cs'
& $compiler $compileArgs
if ($LASTEXITCODE -ne 0) { throw 'TransportLine maintenance tests failed to compile.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'TransportLine maintenance tests failed.' }
