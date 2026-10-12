param([string]$BuildDirectory = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ($BuildDirectory -eq '') { $BuildDirectory = Join-Path $projectRoot 'dist\MoreNetworkCapacity' }
$testDir = Join-Path $projectRoot 'artifacts\smoke\diagnostics-configuration'
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$testExe = Join-Path $testDir 'DiagnosticsConfigurationTests.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe "/out:$testExe" (Join-Path $PSScriptRoot 'Test-DiagnosticsConfiguration.cs')
if ($LASTEXITCODE -ne 0) { throw 'Diagnostics test compilation failed.' }
$previousFlag = $env:MNC_DIAGNOSTICS
try {
    foreach ($flag in @('0', '1')) {
        $env:MNC_DIAGNOSTICS = $flag
        & $testExe $BuildDirectory 'D:\steam\steamapps\common\Cities_Skylines\Cities_Data\Managed' 'D:\steam\steamapps\workshop\content\255710\2040656402'
        if ($LASTEXITCODE -ne 0) { throw 'Diagnostics configuration tests failed.' }
    }
}
finally { $env:MNC_DIAGNOSTICS = $previousFlag }
