param([string]$Candidate)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if (!$Candidate) { $Candidate=Join-Path $root 'dist/MoreNetworkCapacity' }
$dir=Join-Path $root "artifacts/smoke/network-search-tests/$PID"
$null=New-Item -ItemType Directory -Force $dir
$game="$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps/common/Cities_Skylines/Cities_Data/Managed"
$workshop="$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps/workshop/content/255710"
$harmony="$workshop/2040656402"
$refs=@()
foreach($path in @("$game/Assembly-CSharp.dll","$game/UnityEngine.dll","$game/ColossalManaged.dll","$game/ICities.dll","$harmony/CitiesHarmony.Harmony.dll")){
    Copy-Item -LiteralPath $path -Destination $dir -Force
    $refs+="/reference:$path"
}
$exe=Join-Path $dir 'NetworkSearchTests.exe'
& "$env:WINDIR/Microsoft.NET/Framework64/v3.5/csc.exe" /nologo /target:exe "/out:$exe" $refs (Join-Path $PSScriptRoot 'Test-NetworkSearch.cs')
if($LASTEXITCODE){throw 'Compile failed'}
$gameRoot=Split-Path -Parent (Split-Path -Parent $game)
$mono=Join-Path $gameRoot 'Mono/bin/mono.exe'
$env:MONO_PATH=$game
foreach($mod in @('2472062376/NodeControllerRenewal.dll','2140418403/IntersectionMarkingTool.dll','1619685021/MoveIt.dll','3007903394/TransportLinesManager.dll')){
    $dll="$workshop/$mod"
    $moveIt=$mod -like '*/MoveIt.dll'
    $env:MNC_TEST_IL_ONLY=if($moveIt){'1'}else{'0'}
    & $mono $exe (Resolve-Path $Candidate).Path (Split-Path -Parent $dll) $game $harmony "$workshop/1637663252" $dll
    if($LASTEXITCODE){throw "Failed: $mod (exit $LASTEXITCODE)"}
    if($moveIt){
        # The shipped type initializer requires a running Unity scene. Only in a
        # detached test copy, bypass it to exercise Harmony installation on Mono.
        Add-Type -Path (Join-Path $gameRoot 'Mono/lib/mono/unity/Mono.Cecil.dll')
        $image=[Mono.Cecil.AssemblyFactory]::GetAssembly($dll)
        $type=$image.MainModule.Types | Where-Object FullName -eq 'MoveIt.MoveItTool'
        $cctor=$type.Constructors | Where-Object Name -eq '.cctor'
        $fixture=Join-Path $dir 'MoveIt.dll'
        $bytes=[IO.File]::ReadAllBytes($dll)
        $pe=[BitConverter]::ToInt32($bytes,60)
        $sections=[BitConverter]::ToUInt16($bytes,$pe+6)
        $table=$pe+24+[BitConverter]::ToUInt16($bytes,$pe+20)
        $rva=[Convert]::ToInt32($cctor.RVA.ToString(),16)
        $offset=-1
        for($i=0;$i -lt $sections;$i++){
            $section=$table+40*$i
            $start=[BitConverter]::ToInt32($bytes,$section+12)
            $size=[BitConverter]::ToInt32($bytes,$section+16)
            if($rva -ge $start -and $rva -lt $start+$size){
                $offset=[BitConverter]::ToInt32($bytes,$section+20)+$rva-$start
                break
            }
        }
        if($offset -lt 0 -or ($bytes[$offset] -band 3) -ne 3){throw 'Unexpected MoveItTool initializer header'}
        # Tiny method header (one IL byte), followed by ret. Other method bodies stay byte-identical.
        $bytes[$offset]=6
        $bytes[$offset+1]=0x2a
        [IO.File]::WriteAllBytes($fixture,$bytes)
        $env:MNC_TEST_IL_ONLY='0'
        & $mono $exe (Resolve-Path $Candidate).Path $dir $game $harmony (Split-Path -Parent $dll) $fixture
        if($LASTEXITCODE){throw "Move It offline installation fixture failed ($LASTEXITCODE)"}
    }
}
