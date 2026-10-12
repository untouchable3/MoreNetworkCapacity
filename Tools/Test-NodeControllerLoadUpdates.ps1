$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $projectRoot 'artifacts\smoke\node-controller-load-updates'
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$testExe = Join-Path $testDir 'NodeControllerLoadUpdateTests.exe'
& $compiler /nologo /target:exe "/out:$testExe" (Join-Path $PSScriptRoot 'Test-NodeControllerLoadUpdates.cs') (Join-Path $projectRoot 'Source\Compatibility\NodeControllerLoadUpdates.cs') (Join-Path $projectRoot 'Source\Compatibility\NodeControllerCompatibility.cs')
if ($LASTEXITCODE -ne 0) { throw 'Node Controller replay tests failed to compile.' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Node Controller replay tests failed.' }
