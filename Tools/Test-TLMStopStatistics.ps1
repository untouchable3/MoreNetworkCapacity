$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$output = Join-Path $project 'artifacts\smoke\tlm-stop-statistics'
New-Item -ItemType Directory -Force $output | Out-Null
$managed = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\common\Cities_Skylines\Cities_Data\Managed"
$tlm = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\3007903394"
$harmonyDir = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\2040656402"
$harmony = Join-Path $harmonyDir 'CitiesHarmony.Harmony.dll'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$exe = Join-Path $output 'TLMStopStatisticsTests.exe'
$compilerArgs = @('/nologo', '/target:exe', '/optimize+', "/out:$exe", "/reference:$harmony")
foreach ($name in @('Assembly-CSharp.dll', 'ColossalManaged.dll', 'UnityEngine.dll', 'ICities.dll')) {
    $file = Join-Path $managed $name
    $compilerArgs += '/reference:' + $file
    Copy-Item -LiteralPath $file -Destination $output -Force
}
Copy-Item -LiteralPath $harmony -Destination $output -Force
foreach ($file in @('Source\Compatibility\TLMCompatibility.cs', 'Source\Compatibility\TLMStopStatistics.cs',
    'Source\TLMStopStatisticsData.cs', 'Tools\Test-TLMStopStatistics.cs')) { $compilerArgs += Join-Path $project $file }
& $compiler $compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'TLM stop statistics test compilation failed.' }
& $exe $managed $tlm $harmonyDir
if ($LASTEXITCODE -ne 0) { throw 'TLM stop statistics tests failed.' }
