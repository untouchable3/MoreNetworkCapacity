$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=Join-Path $root 'artifacts\smoke\tmpe-array-layouts'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$executable=Join-Path $output 'TMPEArrayLayoutTests.exe'
& $compiler /nologo /target:exe "/out:$executable" (Join-Path $PSScriptRoot 'Test-TMPEArrayLayouts.cs') (Join-Path $root 'Source\Compatibility\ArrayCompatibility.cs')
if($LASTEXITCODE -ne 0){throw 'TMPE array layout compilation failed'}
& $executable
if($LASTEXITCODE -ne 0){throw 'TMPE array layout tests failed'}
