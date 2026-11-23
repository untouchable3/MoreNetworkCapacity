param([string]$Candidate)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$Candidate) { $Candidate = Join-Path $root 'dist/MoreNetworkCapacity/MoreNetworkCapacity.dll' }
$testDir = Join-Path $root 'artifacts/smoke/split-mesh-uvs'
$null = New-Item -ItemType Directory -Path $testDir -Force
$game = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps/common/Cities_Skylines"
$managed = "$game/Cities_Data/Managed"
$harmony = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps/workshop/content/255710/2040656402/CitiesHarmony.Harmony.dll"
$refs = @()
foreach ($dll in @("$managed/Assembly-CSharp.dll","$managed/UnityEngine.dll","$managed/ColossalManaged.dll","$managed/ICities.dll",$harmony)) {
    Copy-Item -LiteralPath $dll -Destination $testDir -Force
    $refs += "/reference:$dll"
}
$exe = Join-Path $testDir 'SplitMeshUvTests.exe'
& "$env:WINDIR/Microsoft.NET/Framework64/v3.5/csc.exe" /nologo "/out:$exe" $refs (Join-Path $PSScriptRoot 'Test-SplitMeshUvs.cs')
if ($LASTEXITCODE) { throw 'Split mesh test compilation failed.' }
$env:MONO_PATH = $managed
& "$game/Mono/bin/mono.exe" $exe (Resolve-Path $Candidate).Path
if ($LASTEXITCODE) { throw 'Split mesh UV regression failed.' }
