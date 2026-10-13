$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=Join-Path $root 'artifacts\smoke\tmpe-failure-gate'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$executable=Join-Path $output 'TMPEFailureGateTests.exe'
& $compiler /nologo /target:exe "/out:$executable" (Join-Path $PSScriptRoot 'Test-TMPEFailureGate.cs') (Join-Path $root 'Source\Compatibility\ThirdPartyCapacityCompatibility.cs')
if($LASTEXITCODE -ne 0){throw 'TMPE failure gate compilation failed'}
& $executable
if($LASTEXITCODE -ne 0){throw 'TMPE failure gate tests failed'}
