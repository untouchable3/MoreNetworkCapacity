$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testDir = Join-Path $projectRoot 'artifacts\smoke\diagnostic-export'
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$archiveExe = Join-Path $testDir 'DiagnosticArchiveTests.exe'
& $compiler /nologo /target:exe "/out:$archiveExe" (Join-Path $PSScriptRoot 'Test-DiagnosticArchive.cs') (Join-Path $projectRoot 'Source\DiagnosticArchive.cs')
if ($LASTEXITCODE -ne 0) { throw 'Archive tests failed to compile.' }
& $archiveExe $testDir
if ($LASTEXITCODE -ne 0) { throw 'Archive tests failed.' }
$bundlePath = [IO.File]::ReadAllText((Join-Path $testDir 'latest-bundle.txt'))
if (-not ('System.IO.Compression.ZipFile' -as [type])) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
}
$zip = [IO.Compression.ZipFile]::OpenRead($bundlePath)
try {
    $names = @($zip.Entries | ForEach-Object FullName)
    foreach ($required in @('summary.txt','mods.txt','render-patches.txt','READ-ME.txt','manifest.txt','logs/1-output_log.txt')) {
        if ($names -notcontains $required) { throw "ZIP entry missing: $required" }
    }
    if ($names.Count -ne 8) { throw 'Unexpected ZIP entries or duplicate report collection.' }
    $entry = $zip.GetEntry('summary.txt')
    $reader = [IO.StreamReader]::new($entry.Open(), [Text.Encoding]::UTF8)
    # Windows PowerShell reads BOM-less UTF-8 scripts using the local code page.
    $expectedSummary = 'summary ' + [char]0x8BCA + [char]0x65AD + ' fixture'
    try { if ($reader.ReadToEnd() -ne $expectedSummary) { throw 'ZIP UTF-8 content mismatch.' } } finally { $reader.Dispose() }
    $logEntry = $zip.GetEntry('logs/1-output_log.txt')
    if ($logEntry.Length -ne 8388608) { throw 'ZIP log limit was not enforced.' }
    $stream = $logEntry.Open()
    try { if ($stream.ReadByte() -ne (100 % 251)) { throw 'ZIP log tail starts at wrong offset.' } } finally { $stream.Dispose() }
    $reader = [IO.StreamReader]::new($zip.GetEntry('manifest.txt').Open())
    try {
        $manifest = $reader.ReadToEnd()
        if ($manifest -notmatch 'TRUNCATED' -or $manifest -notmatch 'missing.log' -or $manifest -notmatch 'Unavailable:') { throw 'Manifest did not record truncation and missing inputs.' }
    } finally { $reader.Dispose() }
} finally { $zip.Dispose() }
Write-Output 'PASS: ZIP is readable by the independent .NET ZIP reader, with exact UTF-8 content, bounded log tail and manifest.'
$stateExe = Join-Path $testDir 'DiagnosticExportStateTests.exe'
& $compiler /nologo /target:exe "/out:$stateExe" (Join-Path $PSScriptRoot 'Test-DiagnosticExportState.cs') (Join-Path $projectRoot 'Source\DiagnosticExport.cs')
if ($LASTEXITCODE -ne 0) { throw 'Export state tests failed to compile.' }
& $stateExe
if ($LASTEXITCODE -ne 0) { throw 'Export state tests failed.' }
$snapshotExe = Join-Path $testDir 'DiagnosticSnapshotTests.exe'
& $compiler /nologo /target:exe "/out:$snapshotExe" (Join-Path $PSScriptRoot 'Test-DiagnosticSnapshot.cs') (Join-Path $projectRoot 'Source\DiagnosticSnapshot.cs') (Join-Path $projectRoot 'Source\DiagnosticArchive.cs')
if ($LASTEXITCODE -ne 0) { throw 'Snapshot tests failed to compile.' }
& $snapshotExe $testDir
if ($LASTEXITCODE -ne 0) { throw 'Snapshot tests failed.' }
