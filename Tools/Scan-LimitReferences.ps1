param(
    [string]$AssemblyPath = "D:\steam\steamapps\common\Cities_Skylines\Cities_Data\Managed\Assembly-CSharp.dll",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$cecilPath = "D:\steam\steamapps\common\Cities_Skylines\Mono\lib\mono\2.0\Mono.Cecil.dll"

Add-Type -Path $cecilPath

if (-not (Test-Path $AssemblyPath)) {
    throw "Assembly not found: $AssemblyPath"
}

$assembly = [Mono.Cecil.AssemblyFactory]::GetAssembly($AssemblyPath)
$rows = New-Object System.Collections.Generic.List[object]

foreach ($type in $assembly.MainModule.Types) {
    foreach ($method in $type.Methods) {
            if (-not $method.HasBody) {
                continue
            }

            $referencesNodes = $false
            $referencesSegments = $false
            $referencesLanes = $false
            $constants = New-Object System.Collections.Generic.HashSet[string]
            $branchConstants = New-Object System.Collections.Generic.HashSet[string]

            $instructions = @($method.Body.Instructions)
            for ($i = 0; $i -lt $instructions.Count; $i++) {
                $instruction = $instructions[$i]
                if ($null -ne $instruction.Operand) {
                    $operand = $instruction.Operand.ToString()
                    if ($operand.Contains("NetManager::m_nodes")) {
                        $referencesNodes = $true
                    }
                    if ($operand.Contains("NetManager::m_segments")) {
                        $referencesSegments = $true
                    }
                    if ($operand.Contains("NetManager::m_lanes")) {
                        $referencesLanes = $true
                    }
                }

                if ($instruction.OpCode.Name -like "ldc.i4*") {
                    $value = [string]$instruction.Operand
                    if ($value -in @(
                        "32256",
                        "32768",
                        "36352",
                        "36864",
                        "258048",
                        "262144")) {
                        [void]$constants.Add($value)

                        if ($i + 1 -lt $instructions.Count -and
                            $instructions[$i + 1].OpCode.Name -in @(
                                "beq",
                                "bge",
                                "bgt",
                                "ble",
                                "blt",
                                "bne.un")) {
                            [void]$branchConstants.Add($value)
                        }
                    }
                }
            }

            if (($referencesNodes -or $referencesSegments -or $referencesLanes) -and
                $constants.Count -gt 0) {
                $signature = (
                    @($method.Parameters) |
                        ForEach-Object { $_.ParameterType.FullName }
                ) -join ","
                $rows.Add([pscustomobject]@{
                    Assembly = $assembly.Name.Name
                    Type = $method.DeclaringType.FullName.Replace("/", "+")
                    Method = $method.Name
                    MetadataToken = $method.MetadataToken.ToUInt()
                    Signature = $signature
                    Nodes = $referencesNodes
                    Segments = $referencesSegments
                    Lanes = $referencesLanes
                    Constants = ([string]::Join(",", [string[]]$constants))
                    BranchConstants = ([string]::Join(",", [string[]]$branchConstants))
                })
            }
    }
}

$output = $rows
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $output | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $OutputPath
    Write-Host "Wrote $($rows.Count) rows to $OutputPath"
}
else {
    $output | Format-Table -AutoSize
}
