$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'artifacts\smoke\tmpe-buffer-validation'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$executable = Join-Path $output 'TMPEBufferValidationTests.exe'
& $compiler /nologo /target:exe "/out:$executable" (Join-Path $PSScriptRoot 'Test-TMPEBufferValidation.cs') (Join-Path $root 'Source\Compatibility\TMPEBufferValidation.cs') (Join-Path $root 'Source\Compatibility\TMPELaneConnectionPreparation.cs') (Join-Path $root 'Source\Compatibility\ArrayCompatibility.cs')
if ($LASTEXITCODE -ne 0) { throw 'TMPE buffer validation compilation failed.' }
& $executable "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\1637663252" "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\common\Cities_Skylines\Cities_Data\Managed" "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\2040656402"
if ($LASTEXITCODE -ne 0) { throw 'TMPE buffer validation tests failed.' }
