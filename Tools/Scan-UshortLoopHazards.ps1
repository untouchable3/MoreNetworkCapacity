param(
    [string[]]$SearchRoots = @(
        "D:\steam\steamapps\workshop\content\255710",
        "$env:LOCALAPPDATA\Colossal Order\Cities_Skylines\Addons\Mods",
        "$env:LOCALAPPDATA\Colossal Order\Cities_Skylines\Addons\Assets"
    ),
    [string]$OutputPath = "",
    [int]$MaxAssemblyMb = 40
)

$ErrorActionPreference = "Stop"
$cecilPath = "D:\steam\steamapps\common\Cities_Skylines\Mono\lib\mono\2.0\Mono.Cecil.dll"

Add-Type -Path $cecilPath

$rows = New-Object System.Collections.Generic.List[object]
$scanned = 0
$skipped = 0

function Test-UshortIndexLoop($body) {
    if ($null -eq $body) {
        return $false
    }

    $instructions = @($body.Instructions)
    $variables = @($body.Variables)
    for ($v = 0; $v -lt $variables.Count; ++$v) {
        if ($variables[$v].VariableType.FullName -ne "System.UInt16") {
            continue
        }

        for ($i = 0; $i + 3 -lt $instructions.Count; ++$i) {
            if ($instructions[$i].OpCode.Name -ne "ldloc.s" -and
                $instructions[$i].OpCode.Name -ne "ldloc") {
                continue
            }
            $operand = $instructions[$i].Operand
            $index = -1
            if ($operand -is [Mono.Cecil.Cil.VariableDefinition]) {
                $index = $operand.Index
            }
            else {
                $index = [int]$operand
            }
            if ($index -ne $v) {
                continue
            }
            if ($instructions[$i + 1].OpCode.Name -ne "ldc.i4.1") {
                continue
            }
            if ($instructions[$i + 2].OpCode.Name -ne "add") {
                continue
            }
            if ($instructions[$i + 3].OpCode.Name -ne "conv.u2") {
                continue
            }

            return $true
        }
    }

    return $false
}

function Test-NetBufferReference($body) {
    foreach ($instruction in $body.Instructions) {
        $operand = $instruction.Operand
        if ($null -eq $operand) {
            continue
        }

        $text = $operand.ToString()
        if ($text.Contains("NetManager::m_nodes") -or
            $text.Contains("NetManager::m_segments") -or
            $text.Contains("NetManager::m_lanes")) {
            return $true
        }
    }

    return $false
}

function Test-IteratesWholeBuffer($body) {
    $hasBuffer = $false
    $hasLength = $false
    $hasSizeField = $false
    foreach ($instruction in $body.Instructions) {
        if ($null -eq $instruction.Operand) {
            continue
        }

        $text = $instruction.Operand.ToString()
        if ($text.Contains("m_buffer")) {
            $hasBuffer = $true
        }
        if ($text.Contains("m_size")) {
            $hasSizeField = $true
        }
    }

    foreach ($instruction in $body.Instructions) {
        if ($instruction.OpCode.Name -eq "ldlen") {
            $hasLength = $true
        }
    }

    return $hasBuffer -and $hasLength -and (-not $hasSizeField)
}

function Visit-Type($type, $assemblyName) {
    foreach ($method in $type.Methods) {
        if (-not $method.HasBody) {
            continue
        }
        if (-not (Test-NetBufferReference $method.Body)) {
            continue
        }
        if (-not (Test-UshortIndexLoop $method.Body)) {
            continue
        }

        $signature = @(
            $method.Parameters |
                ForEach-Object { $_.ParameterType.FullName }
        ) -join ","
        $rows.Add([pscustomobject]@{
            Assembly = $assemblyName
            Type = $method.DeclaringType.FullName.Replace("/", "+")
            Method = $method.Name
            Signature = $signature
            WholeBufferLoop = (Test-IteratesWholeBuffer $method.Body)
        })
    }

    foreach ($nested in $type.NestedTypes) {
        Visit-Type $nested $assemblyName
    }
}

foreach ($root in $SearchRoots) {
    if (-not (Test-Path $root)) {
        continue
    }

    $files = Get-ChildItem -Path $root -Filter "*.dll" -Recurse -File |
        Where-Object { $_.Length -le ($MaxAssemblyMb * 1MB) }
    foreach ($file in $files) {
        if ($file.FullName -match "\\Cities_Data\\Managed\\") {
            continue
        }

        try {
            $assembly = [Mono.Cecil.AssemblyFactory]::GetAssembly($file.FullName)
            foreach ($type in $assembly.MainModule.Types) {
                Visit-Type $type $file.FullName
            }
            ++$scanned
        }
        catch {
            ++$skipped
        }
    }
}

if ($OutputPath -ne "") {
    $rows | Export-Csv -Path $OutputPath -NoTypeInformation -Encoding UTF8
    Write-Host "Scanned $scanned assemblies ($skipped unreadable), found $($rows.Count) ushort-loop hazards."
    Write-Host "Wrote $OutputPath"
}
else {
    $rows | Format-Table -AutoSize
}
