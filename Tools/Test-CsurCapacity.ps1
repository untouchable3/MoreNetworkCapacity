$ErrorActionPreference = 'Stop'
$game = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps/common/Cities_Skylines"
$workshop = "$(if ($env:MNC_STEAM_ROOT) { $env:MNC_STEAM_ROOT } else { Join-Path $env:ProgramFiles 'Steam' })\steamapps/workshop/content/255710"
Add-Type -Path "$game/Mono/lib/mono/unity/Mono.Cecil.dll"
$box = [Mono.Cecil.AssemblyFactory]::GetAssembly("$workshop/1959342332/CSURToolBox.dll")
$loader = [Mono.Cecil.AssemblyFactory]::GetAssembly("$workshop/1959183067/CSURLoader.dll")
function Body($assembly, $type, $method) {
    $t = $assembly.MainModule.Types | Where-Object Name -eq $type
    ($t.Methods | Where-Object Name -eq $method).Body.Instructions
}
function Check($ok, $message) { if (!$ok) { throw $message } }
function Pattern($code, $expected) {
    $actual = ($code | ForEach-Object { $_.OpCode.Name }) -join ' '
    Check ($actual -ceq $expected) "Unexpected IL: $actual"
}
function Slice($code, $first, $last) {
    $code | Where-Object { $_.Offset -ge $first -and $_.Offset -le $last }
}
# Substitute only inputs (mask and segment ID). The CLR executes the DLL's
# arithmetic and array opcodes. Keep UInt64 addresses and shift counts intact.
function BitmapMethod($code, $write) {
    $ret = if ($write) { [void] } else { [long] }
    $dm = New-Object Reflection.Emit.DynamicMethod('ReplayBitmap', $ret, [type[]]@([uint64[]],[int]))
    $il = $dm.GetILGenerator()
    $il.Emit([Reflection.Emit.OpCodes]::Ldarg_0)
    foreach ($ins in $code) {
        $name = $ins.OpCode.Name
        if ($name -eq 'ldloc.s') {
            Check ("$($ins.Operand)" -eq 'V_14') 'Segment local changed'
            $il.Emit([Reflection.Emit.OpCodes]::Ldarg_1)
        } elseif ($name -eq 'ldelema') {
            Check ("$($ins.Operand)" -eq 'System.UInt64') 'Element type changed'
            $il.Emit([Reflection.Emit.OpCodes]::Ldelema,[uint64])
        } else {
            $op = [Reflection.Emit.OpCodes].GetField($name.Replace('.','_'), 'Public,Static,IgnoreCase').GetValue($null)
            if ($name -eq 'ldc.i4.s') { $il.Emit($op,[sbyte]$ins.Operand) }
            else { $il.Emit($op) }
        }
    }
    $il.Emit([Reflection.Emit.OpCodes]::Ret)
    $dm
}

$prefix = @(Body $box NetManagerOverlapQuadPatch Prefix)
Check (($prefix | Where-Object Offset -eq 343).Operand.Name -eq 'm_updatedSegments') 'Update bitmap changed'
$r = @(Slice $prefix 348 361)
Pattern $r 'ldloc.s ldc.i4.6 shr ldelem.i8 ldc.i4.1 conv.i8 ldloc.s ldc.i4.s and shl and'
$read = BitmapMethod $r $false
$w = @(Slice $prefix 1461 1481)
Pattern $w 'ldloc.s ldc.i4.6 shr ldelema dup ldind.i8 ldc.i4.1 conv.i8 ldloc.s ldc.i4.s and shl or stind.i8'
$write = BitmapMethod $w $true
Check (($prefix | Where-Object Offset -eq 1451).Operand.Offset -eq 1459) 'Non-null branch changed'
Pattern @(Slice $prefix 1453 1458) 'ldarg.s ldc.i4.1 stind.i1 ldc.i4.0 ret'

$bits = [uint64[]]::new(1016)
$ids = @(1,32767,32768,36863,36864,64960,65022,65023)
foreach ($id in $ids) {
    Check ($read.Invoke($null,@($bits,$id)) -eq 0) "Already set: $id"
    $write.Invoke($null,@($bits,$id))
}
foreach ($id in $ids) { Check ($read.Invoke($null,@($bits,$id)) -ne 0) "Missing: $id" }
Check ($bits[1015] -eq [uint64]::Parse('13835058055282163713')) 'Highest word mismatch'
foreach ($fn in @($read,$write)) {
    $failed = $false
    try { $null = $fn.Invoke($null,@(([uint64[]]::new(576)),36864)) }
    catch { $failed = $_.Exception.GetBaseException() -is [IndexOutOfRangeException] }
    Check $failed 'Vanilla-size negative control failed'
}
'PASS ToolBox: 8 IDs across vanilla and expanded ranges, exact highest word, short read/write negative controls.'

$guard = @(Slice $prefix 1510 1522)
Pattern $guard 'ldloc.s ldc.i4.1 add dup stloc.s ldc.i4 blt.s'
Check ("$($guard[0].Operand)" -eq 'V_15' -and "$($guard[4].Operand)" -eq 'V_15' -and $guard[6].Operand.Offset -eq 1547) 'Guard local or branch changed'
$limit = [int]$guard[5].Operand
Check ($limit -eq 36864) 'Unexpected traversal limit'
foreach ($len in @(1,36863,36864,36865)) {
    $steps = 0
    while ($steps -lt $len) { if (++$steps -ge $limit) { break } }
    Check ($steps -eq [Math]::Min($len,36864)) 'Traversal model failed'
}
'PASS ToolBox: guard IL and synthetic chain lengths 1, 36863, 36864, 36865.'

foreach ($method in @('RefreshNetworks','ChangeBuiltRoadColor')) {
    $code = @(Body $loader RoadSkins $method)
    $bounds = @(for ($j=0;$j -lt $code.Count;$j++) { if ($code[$j].Operand.Name -eq 'm_size') { $j } })
    Check ($bounds.Count -eq 2) "Wrong loop count in $method"
    foreach ($j in $bounds) {
        $tail = $code[($j-8)..($j+1)]
        $pool = $tail[7].Operand.Name
        $local = if ($pool -eq 'm_segments') {0} elseif ($pool -eq 'm_nodes') {2} else {throw "Unexpected pool $pool"}
        Pattern $tail "ldloc.$local ldc.i4.1 add conv.u2 stloc.$local ldloc.$local call ldfld ldfld blt.un.s"
        $entry = $tail[9].Operand.Offset
        $bodyStart = @($code | Where-Object Offset -eq $entry)[0]
        Check ($bodyStart.Operand.Name -eq 'get_instance') 'Loop body target changed'
        # Replay the checked unsigned increment and comparison at both capacities.
        foreach ($size in @($(if ($local -eq 0) {36864} else {32768}),65024)) {
            $index = 0
            $visits = 0
            do { $last=$index; $visits++; $index=($index+1) -band 65535 } while ($index -lt $size -and $visits -le $size)
            Check ($visits -eq $size -and $last -eq $size-1 -and $index -eq $size) 'Loop did not terminate correctly'
        }
        "PASS Loader: $method $pool loop model reached ID 65023 and terminated."
    }
}
