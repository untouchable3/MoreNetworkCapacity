$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=Join-Path $root 'artifacts\validation\autosave-metadata-20261019'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$exe=Join-Path $output 'SaveListMetadataTests.exe'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$harmony="$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\2040656402\CitiesHarmony.Harmony.dll"
Copy-Item -LiteralPath $harmony -Destination $output -Force
& $compiler /nologo /target:exe "/r:$harmony" "/out:$exe" (Join-Path $PSScriptRoot 'Test-SaveListMetadata.cs') (Join-Path $root 'Source\Patches\SaveListMetadataRefreshPatch.cs')
if ($LASTEXITCODE -ne 0) {throw 'Save-list metadata test compilation failed'}
& $exe $output
if ($LASTEXITCODE -ne 0) {throw 'Save-list metadata behavior tests failed'}
& $exe $output real-stream "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\common\Cities_Skylines\Cities_Data\Managed"
if ($LASTEXITCODE -ne 0) {throw 'Actual game stream replay failed'}
