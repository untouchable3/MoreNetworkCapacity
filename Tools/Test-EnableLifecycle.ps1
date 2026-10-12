$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $projectRoot 'artifacts\smoke\enable-lifecycle'
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$testExe = Join-Path $testDir 'EnableLifecycleTests.exe'
& $compiler /nologo /target:exe "/out:$testExe" (Join-Path $PSScriptRoot 'Test-EnableLifecycle.cs') (Join-Path $projectRoot 'Source\HarmonyBootstrap.cs')
if ($LASTEXITCODE -ne 0) { throw 'Lifecycle test compilation failed.' }
& $testExe
if ($LASTEXITCODE -ne 0) { throw 'Lifecycle tests failed.' }
