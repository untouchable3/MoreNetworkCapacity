param([string]$Candidate)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$Candidate) { $Candidate = Join-Path $root 'dist/MoreNetworkCapacity' }
$dir = Join-Path $env:TEMP ('MncReporterNetwork-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $dir
$game = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps/common/Cities_Skylines"
$managed = "$game/Cities_Data/Managed"
$workshop = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps/workshop/content/255710"
$references = @()
foreach ($dll in @("$managed/Assembly-CSharp.dll","$managed/UnityEngine.dll","$managed/ColossalManaged.dll",
    "$managed/ICities.dll","$workshop/2040656402/CitiesHarmony.Harmony.dll")) {
    Copy-Item -LiteralPath $dll -Destination $dir
    $references += "/reference:$dll"
}
Copy-Item -Path "$Candidate/*.dll" -Destination $dir
$exe = Join-Path $dir 'ReporterNetworkTests.exe'
& "$env:WINDIR/Microsoft.NET/Framework64/v3.5/csc.exe" /nologo "/out:$exe" $references (Join-Path $PSScriptRoot 'Test-ReporterNetwork.cs')
if ($LASTEXITCODE) { throw 'Compilation failed' }
$env:MONO_PATH = "$managed;$workshop/2560782729;$workshop/2862973068;$workshop/2040656402;$workshop/1637663252"
$env:MNC_TEST_IL_ONLY = '1'
& "$game/Mono/bin/mono.exe" $exe "$dir/MoreNetworkCapacity.dll" "$workshop/949504539/SingleTrackAI.dll" `
    "$workshop/2560782729/NetworkMultitool.dll" "$workshop/2862973068/AdvancedStopSelection.dll"
if ($LASTEXITCODE) { throw 'Reporter network compatibility regression failed' }
