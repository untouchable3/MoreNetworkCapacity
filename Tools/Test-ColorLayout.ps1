param([string]$Candidate = (Join-Path $PSScriptRoot '../dist/MoreNetworkCapacity/MoreNetworkCapacity.dll'))
$ErrorActionPreference = 'Stop'
$managed = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps/common/Cities_Skylines/Cities_Data/Managed"
foreach ($name in 'UnityEngine','ColossalManaged','Assembly-CSharp') {
    [void][Reflection.Assembly]::LoadFrom("$managed/$name.dll")
}
$mod = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Candidate).Path)
$capacity = $mod.GetType('MoreNetworkCapacity.Capacity',$true)
$nodeLimit = $capacity.GetField('NodeLoopLimit')
$segmentLimit = $capacity.GetField('SegmentLoopLimit')
$nodeBase = $capacity.GetMethod('GetNodeColorIndexBase')
$eventBase = $capacity.GetMethod('GetEventColorIndexBase')
[RenderManager]::OBJECT_COLORMAP_WIDTH = 512
[RenderManager]::OBJECT_COLORMAP_HEIGHT = 512
$locations = @{}
$overlap = $false
foreach ($nodes in 32768,65024) {
    foreach ($segments in 36864,65024) {
        $nodeLimit.SetValue($null,$nodes)
        $segmentLimit.SetValue($null,$segments)
        $node = $nodeBase.Invoke($null,$null)
        $event = $eventBase.Invoke($null,$null)
        foreach ($sample in @(@('node',0,$node),@('node',32672,$node),@('node',65023,$node),
            @('event',0,$event),@('event',255,$event),@('event',256,$event),@('segment',65023,49152))) {
            $key = "$($sample[0])-$($sample[1])"
            $pixel = $sample[1]+$sample[2]
            $uv = [RenderManager]::GetColorLocation([uint32]$pixel)
            if (([int][Math]::Floor($uv.x*512) + 512*[int][Math]::Floor($uv.y*512)) -ne $pixel) {
                throw "$key maps outside its assigned pixel $pixel"
            }
            if ($locations.ContainsKey($key) -and !$locations[$key].Equals($uv)) {
                throw "$key changed cached color UV when loop limits became $nodes/$segments"
            }
            $locations[$key] = $uv
        }
        if ($node -lt 49152+65024 -or $event -lt $node+65024 -or $event+256 -ge $capacity::GetColorIndexCapacity()) {
            $overlap = $true
        }
    }
}
if ($overlap) { throw 'Reserved color ranges overlap or exceed the atlas.' }
'PASS cached road/node/event UVs and disjoint atlas ranges in all four loading states.'
