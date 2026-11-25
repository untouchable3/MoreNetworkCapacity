param([string]$OutputDirectory = "")

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceDir = Join-Path $root "Source"
$distDir = Join-Path $root "dist"
$outputDir = Join-Path $distDir "MoreNetworkCapacity"
if ($OutputDirectory -ne "") {
    $outputDir = [System.IO.Path]::GetFullPath($OutputDirectory)
}
$artifactsDir = Join-Path $root "artifacts"

$steamRoot = $env:MNC_STEAM_ROOT
if ([string]::IsNullOrWhiteSpace($steamRoot)) {
    $steamRoot = Join-Path $env:ProgramFiles "Steam"
}

$steamApps = Join-Path $steamRoot "steamapps"
$gameDir = Join-Path $steamApps "common\Cities_Skylines"
$workshopDir = Join-Path $steamApps "workshop\content\255710"
$managedDir = Join-Path $gameDir "Cities_Data\Managed"
$monoReferenceDir = Join-Path $gameDir "Mono\lib\mono\2.0"
$harmonyDll = Join-Path $workshopDir "2040656402\CitiesHarmony.Harmony.dll"
$harmonyApiDll = Join-Path $workshopDir "2101551127\CitiesHarmony.API.dll"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path $managedDir)) {
    throw "Cities: Skylines managed directory not found: $managedDir. Set MNC_STEAM_ROOT to the folder containing steamapps."
}
if (-not (Test-Path $harmonyDll)) {
    throw "CitiesHarmony.Harmony.dll not found: $harmonyDll"
}
if (-not (Test-Path $harmonyApiDll)) {
    throw "CitiesHarmony.API.dll not found: $harmonyApiDll"
}
if (-not (Test-Path $csc)) {
    throw "csc.exe not found: $csc"
}

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

& powershell -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $root "Tools\Scan-LimitReferences.ps1") `
    -AssemblyPath (Join-Path $managedDir "Assembly-CSharp.dll") `
    -OutputPath (Join-Path $artifactsDir "limit-references.csv")
if ($LASTEXITCODE -ne 0) {
    throw "Limit reference scan failed with exit code $LASTEXITCODE"
}

& powershell -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $root "Tools\Generate-PatchTargets.ps1") `
    -OccurrencesPath (Join-Path $artifactsDir "patch-occurrences.csv") `
    -SourceOutputPath (Join-Path $sourceDir "Generated\PatchTargets.generated.cs") `
    -CsvOutputPath (Join-Path $artifactsDir "branch-bound-targets.csv")
if ($LASTEXITCODE -ne 0) {
    throw "Patch target generation failed with exit code $LASTEXITCODE"
}

$sources = Get-ChildItem $sourceDir -Filter "*.cs" -Recurse -File |
    Sort-Object FullName |
    ForEach-Object { $_.FullName }

if ($sources.Count -eq 0) {
    throw "No C# source files found under $sourceDir"
}

$references = @(
    (Join-Path $monoReferenceDir "mscorlib.dll"),
    (Join-Path $monoReferenceDir "System.dll"),
    (Join-Path $monoReferenceDir "System.Core.dll"),
    (Join-Path $monoReferenceDir "System.Xml.dll"),
    (Join-Path $managedDir "Assembly-CSharp.dll"),
    (Join-Path $managedDir "ColossalManaged.dll"),
    (Join-Path $managedDir "ICities.dll"),
    (Join-Path $managedDir "UnityEngine.dll"),
    $harmonyDll,
    $harmonyApiDll
)

$args = @(
    "/nologo",
    "/noconfig",
    "/nostdlib+",
    "/langversion:3",
    "/target:library",
    "/optimize+",
    "/warn:4",
    "/out:$outputDir\MoreNetworkCapacity.dll"
)

foreach ($reference in $references) {
    $args += "/reference:$reference"
}

$args += $sources

& $csc $args
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

Copy-Item $harmonyApiDll (Join-Path $outputDir "CitiesHarmony.API.dll") -Force
Copy-Item (Join-Path $root "Packaging\README.txt") (Join-Path $outputDir "README.txt") -Force

& powershell -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $root "Tools\Verify-PatchTargets.ps1") `
    -AssemblyPath (Join-Path $managedDir "Assembly-CSharp.dll") `
    -OccurrencesPath (Join-Path $artifactsDir "patch-occurrences.csv")
if ($LASTEXITCODE -ne 0) {
    throw "Patch target verification failed with exit code $LASTEXITCODE"
}

Write-Host "Built: $outputDir"
