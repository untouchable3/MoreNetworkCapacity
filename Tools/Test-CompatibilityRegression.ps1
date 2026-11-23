param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if($OutputDirectory -eq '') { $OutputDirectory = Join-Path $project 'artifacts\validation\compatibility-regression' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$tests = @(
    'Test-TransportLineLoops','Test-TransportLineMaintenance','Test-NetworkToolLoops',
    'Test-LaneChainLoops','Test-BuildingChainLoops','Test-GridChainLoops','Test-NetManagerLoops',
    'Test-RoadAccessLoops','Test-ServicePathLoops','Test-CinematicLoops','Test-TransportLifecycleLoops',
    'Test-StationLaneLoops','Test-EventRaceLoops','Test-NodeSimulationPhase',
    'Test-HighIdRanges','Test-TMPEArrayLayouts','Test-TMPEAdditionalRanges','Test-TMPEBufferValidation',
    'Test-TMPEOverlays','Test-TMPEBollardPhase','Test-TMPEFailureGate',
    'Test-IntersectionMarkingRanges','Test-NodeControllerLoadUpdates','Test-NodeControllerFlagChecks',
    'Test-NodeControllerXmlRanges','Test-EightyOne2SimulationRanges','Test-EarlyCompatibility',
    'Test-SimulationRetry','Test-ToolCollisionBuffers','Test-NetworkBufferSizes',
    'Test-EnableLifecycle','Test-SimulationDataReadState','Test-DiagnosticExport','Test-DiagnosticsConfiguration','Test-ErrorReport',
    'Test-TLMStopStatistics','Test-TMCEDistance','Test-SaveListMetadata'
)
$rows = @()
foreach($test in $tests) {
    $log = Join-Path $OutputDirectory ($test + '.txt')
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot ($test + '.ps1')) *> $log
    $code = $LASTEXITCODE
    $rows += [pscustomobject]@{Test=$test; ExitCode=$code; Log=$log}
    Write-Output ($test + ': ' + $(if($code -eq 0){'PASS'}else{'FAIL'}))
}
$rows | Export-Csv -NoTypeInformation -Encoding UTF8 (Join-Path $OutputDirectory 'results.csv')
$failed = @($rows | Where-Object {$_.ExitCode -ne 0})
if($failed.Count -ne 0) { throw ('Compatibility regression failed: ' + (($failed | ForEach-Object {$_.Test}) -join ', ') + '. See logs in ' + $OutputDirectory) }
Write-Output ('PASS: all ' + $rows.Count + ' compatibility regression groups. These fixtures do not replace in-game mod-combination testing.')
