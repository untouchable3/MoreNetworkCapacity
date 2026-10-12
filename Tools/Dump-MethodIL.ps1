param(
    [Parameter(Mandatory = $true)]
    [string]$AssemblyPath,
    [string]$TypeFilter = "",
    [string]$MethodFilter = "",
    [string]$OutputPath = "",
    [switch]$ShowLocals
)

$ErrorActionPreference = "Stop"
$cecilPath = "D:\steam\steamapps\common\Cities_Skylines\Mono\lib\mono\2.0\Mono.Cecil.dll"

Add-Type -Path $cecilPath

if (-not (Test-Path $AssemblyPath)) {
    throw "Assembly not found: $AssemblyPath"
}

$assembly = [Mono.Cecil.AssemblyFactory]::GetAssembly($AssemblyPath)
$lines = New-Object System.Collections.Generic.List[string]

function Get-TypeName($type) {
    return $type.FullName.Replace("/", "+")
}

function Visit-Type($type, $prefix) {
    $typeName = Get-TypeName $type
    if (($TypeFilter -eq "") -or ($typeName -like $TypeFilter)) {
        foreach ($method in $type.Methods) {
            if (($MethodFilter -ne "") -and ($method.Name -notlike $MethodFilter)) {
                continue
            }
            if (-not $method.HasBody) {
                continue
            }

            $signature = @(
                $method.Parameters |
                    ForEach-Object { $_.ParameterType.FullName }
            ) -join ","
            $token = try { $method.MetadataToken.ToInt32() } catch { 0 }
            $lines.Add("=== $typeName::$($method.Name)($signature) [token=$token]")

            $body = $method.Body
            $offsets = @{}
            $index = 0
            foreach ($instruction in $body.Instructions) {
                $offsets[$instruction] = $index
                $index++
            }
            if ($ShowLocals) {
                foreach ($variable in $body.Variables) {
                    $lines.Add("      local V_$($variable.Index): $($variable.VariableType.FullName)")
                }
            }
            $index = 0
            foreach ($instruction in $body.Instructions) {
                $operand = ""
                if ($null -ne $instruction.Operand) {
                    $operand = $instruction.Operand.ToString()
                    $target = $instruction.Operand -as [Mono.Cecil.Cil.Instruction]
                    if ($null -ne $target -and $offsets.ContainsKey($target)) {
                        $operand = "IL_" + $offsets[$target]
                    }
                }
                $lines.Add(("{0,4} @0x{1:X4}: {2,-14} {3}" -f (
                    $index,
                    $instruction.Offset,
                    $instruction.OpCode.Name,
                    $operand)))
                $index++
            }

            foreach ($handler in $body.ExceptionHandlers) {
                $lines.Add(("      handler {0}: try {1}..{2}" -f $handler.HandlerType, $handler.TryStart, $handler.TryEnd))
            }

            $lines.Add("")
        }
    }

    foreach ($nested in $type.NestedTypes) {
        Visit-Type $nested $prefix
    }
}

foreach ($type in $assembly.MainModule.Types) {
    Visit-Type $type ""
}

if ($OutputPath -ne "") {
    Set-Content -Path $OutputPath -Value $lines -Encoding UTF8
    Write-Host "Wrote $($lines.Count) lines to $OutputPath"
}
else {
    $lines | ForEach-Object { Write-Output $_ }
}
