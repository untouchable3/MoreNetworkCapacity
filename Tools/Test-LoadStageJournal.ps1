$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $projectRoot 'artifacts\smoke\load-stage-journal'
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$testExe = Join-Path $testDir 'LoadStageJournalTests.exe'
& $compiler /nologo /target:exe "/out:$testExe" (Join-Path $PSScriptRoot 'Test-LoadStageJournal.cs') (Join-Path $projectRoot 'Source\LoadStageJournal.cs')
if ($LASTEXITCODE -ne 0) { throw 'Checkpoint tests failed to compile.' }
& $testExe $testDir
if ($LASTEXITCODE -ne 0) { throw 'Checkpoint tests failed.' }
