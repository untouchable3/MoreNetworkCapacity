$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $projectRoot 'artifacts\smoke\error-report'
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$testExe = Join-Path $testDir 'ErrorReportTests.exe'
& $compiler /nologo /target:exe "/out:$testExe" (Join-Path $PSScriptRoot 'Test-ErrorReport.cs') (Join-Path $projectRoot 'Source\ErrorReport.cs') (Join-Path $projectRoot 'Source\Logging.cs') (Join-Path $projectRoot 'Source\LoadStageJournal.cs')
if ($LASTEXITCODE -ne 0) { throw 'Error report test compilation failed.' }
& $testExe $testDir
if ($LASTEXITCODE -ne 0) { throw 'Error report tests failed.' }
& $testExe $testDir 'io-failure'
if ($LASTEXITCODE -ne 0) { throw 'Error report I/O failure tests failed.' }
& $testExe $testDir 'concurrent'
if ($LASTEXITCODE -ne 0) { throw 'Concurrent error report tests failed.' }
