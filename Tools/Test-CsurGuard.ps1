$ErrorActionPreference='Stop'
$w="$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps/workshop/content/255710"
$g="$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps/common/Cities_Skylines/Cities_Data/Managed"
foreach($name in @('UnityEngine','ICities','ColossalManaged','Assembly-CSharp')) { $null=[Reflection.Assembly]::LoadFrom("$g/$name.dll") }
$h=[Reflection.Assembly]::LoadFrom("$w/2040656402/CitiesHarmony.Harmony.dll")
$b=[Reflection.Assembly]::LoadFrom("$w/1959342332/CSURToolBox.dll")
$m=[Reflection.Assembly]::LoadFrom((Join-Path (Split-Path -Parent $PSScriptRoot) 'dist/MoreNetworkCapacity/MoreNetworkCapacity.dll'))
$p=$b.GetType('CSURToolBox.Patch.NetManagerOverlapQuadPatch',$true).GetMethod('Prefix')
$code=[HarmonyLib.PatchProcessor].GetMethod('GetOriginalInstructions',[type[]]@([Reflection.MethodBase],[Reflection.Emit.ILGenerator])).Invoke($null,@($p,$null))
$t=$m.GetType('MoreNetworkCapacity.Compatibility.CsurToolBoxCompatibility',$true)
$rewrite=$t.GetMethod('Rewrite',[Reflection.BindingFlags]'NonPublic,Static')
$arr=[object[]]::new(1)
$arr[0]=$code
$out=$rewrite.Invoke($null,$arr)
$field=$m.GetType('MoreNetworkCapacity.Capacity').GetField('SegmentLoopLimit')
$bound=@($out | Where-Object { $_.opcode -eq [Reflection.Emit.OpCodes]::Ldsfld -and $_.operand -eq $field })
if($bound.Count -ne 1){throw 'Missing bound'}
foreach($size in @(36864,65024)){
$field.SetValue($null,$size)
$dm=[Reflection.Emit.DynamicMethod]::new('within',[bool],[type[]]@([int]),$m.ManifestModule,$true)
$il=$dm.GetILGenerator()
$il.Emit([Reflection.Emit.OpCodes]::Ldarg_0)
$il.Emit($bound[0].opcode,$bound[0].operand)
$il.Emit([Reflection.Emit.OpCodes]::Clt)
$il.Emit([Reflection.Emit.OpCodes]::Ret)
foreach($count in @(1,36863,36864,65023,65024)){if($dm.Invoke($null,@($count)) -ne ($count -lt $size)){throw 'Guard mismatch'}}
"PASS actual CSUR Prefix rewrite: active capacity $size"
}
