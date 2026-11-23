$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'artifacts\validation\tmce-adaptation-20261018'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$harmony = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\2040656402\CitiesHarmony.Harmony.dll"
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v3.5\csc.exe'
$executable = Join-Path $output 'TMCEDistanceTests.exe'
& $compiler /nologo /noconfig /r:System.dll /r:System.Core.dll /target:exe "/r:$harmony" "/out:$executable" (Join-Path $PSScriptRoot 'Test-TMCEDistance.cs') (Join-Path $root 'Source\Compatibility\TMCECompatibility.cs') (Join-Path $root 'Source\Patches\LoopLocalTypes.cs')
if ($LASTEXITCODE -ne 0) { throw 'TMCE distance regression compilation failed.' }
Copy-Item -LiteralPath $harmony -Destination $output -Force
$versions = @(
    @{ Name = '3.1.39'; Path = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps\workshop\content\255710\2804719780\TransferManagerCE.dll" },
    @{ Name = '3.1.14'; Path = (Join-Path $root 'artifacts\research\four-mod-audit-20261018\tmce\binary-3.1.14\TransferManagerCE\TransferManagerCE.dll') }
)
foreach ($version in $versions) {
    & $executable $version.Path 2>&1 | Tee-Object -FilePath (Join-Path $output ($version.Name + '.txt'))
    if ($LASTEXITCODE -ne 0) { throw ('TMCE ' + $version.Name + ' distance regression failed.') }
}
'PASS: current 3.1.39 and legacy 3.1.14, separate CLR processes. Actual IL precision/metadata and guarded rejection, replayed allocation and post-increment search guard, real UpdateNode high-ID boundary, bitmap storage preservation, deferred-cache-reset fixture. No complete worker or native patch installation.' | Set-Content -LiteralPath (Join-Path $output 'status.txt') -Encoding UTF8
Write-Output 'PASS TMCE current and legacy binaries tested in separate processes.'
