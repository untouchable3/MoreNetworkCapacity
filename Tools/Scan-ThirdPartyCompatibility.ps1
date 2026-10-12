param(
    [string]$LogPath = "D:\steam\steamapps\common\Cities_Skylines\Cities_Data\output_log.txt",
    [string]$OutputPath = "",
    [int]$MinimumScore = 4
)

$ErrorActionPreference = "Stop"
$cecilPath = "D:\steam\steamapps\common\Cities_Skylines\Mono\lib\mono\2.0\Mono.Cecil.dll"

if (-not (Test-Path $cecilPath)) {
    throw "Mono.Cecil not found: $cecilPath"
}
if (-not (Test-Path $LogPath)) {
    throw "Game log not found: $LogPath"
}

Add-Type -Path $cecilPath

$limitConstants = @(
    32256,
    32768,
    36352,
    36864,
    64512,
    65024,
    65535,
    73728,
    130048,
    258048,
    262144
)

$loadedDllPattern =
    "Loading (?<path>[A-Za-z]:\\[^\r\n]+?\.dll)\s+\[Mods - Internal\]"
$logText = [System.IO.File]::ReadAllText($LogPath)
$paths = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)

foreach ($match in [regex]::Matches($logText, $loadedDllPattern)) {
    $path = $match.Groups["path"].Value
    if (Test-Path $path) {
        [void]$paths.Add([System.IO.Path]::GetFullPath($path))
    }
}

function Get-TypeName($type) {
    return $type.FullName.Replace("/", "+")
}

function Get-ArrayFields($type) {
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($field in $type.Fields) {
        if (-not ($field.FieldType -is [Mono.Cecil.ArrayType])) {
            continue
        }

        $fieldName = $field.Name
        $elementName = $field.FieldType.ElementType.FullName
        $looksRelevant =
            $fieldName -match "(?i)(node|segment|buffer|array|data|cache|index|map)" -or
            $elementName -match "(?i)(node|segment|net)"
        if ($looksRelevant) {
            $names.Add(
                $fieldName + ":" + $field.FieldType.FullName)
        }
    }

    return ($names | Select-Object -First 12) -join ";"
}

function Test-ConstantInstruction($instruction) {
    if ($null -eq $instruction) {
        return $false
    }

    return $instruction.OpCode.Name -like "ldc.i4*"
}

function Get-NewArrayConstants($instructions, $index) {
    for ($offset = 1; $offset -le 4; $offset++) {
        $candidateIndex = $index - $offset
        if ($candidateIndex -lt 0) {
            break
        }

        $candidate = $instructions[$candidateIndex]
        if (Test-ConstantInstruction $candidate) {
            return [string]$candidate.Operand
        }
    }

    return ""
}

$rows = New-Object System.Collections.Generic.List[object]

foreach ($path in $paths) {
    try {
        $assembly = [Mono.Cecil.AssemblyFactory]::GetAssembly($path)
    }
    catch {
        continue
    }

    $modId = Split-Path -Leaf (Split-Path -Parent $path)
    $assemblyName = $assembly.Name.Name

    foreach ($topLevelType in $assembly.MainModule.Types) {
        $pendingTypes = New-Object System.Collections.Generic.Stack[object]
        $pendingTypes.Push($topLevelType)

        while ($pendingTypes.Count -gt 0) {
            $type = $pendingTypes.Pop()
            $typeName = Get-TypeName $type
            $arrayFields = Get-ArrayFields $type

            foreach ($nested in $type.NestedTypes) {
                $pendingTypes.Push($nested)
            }

            foreach ($method in $type.Methods) {
                if (-not $method.HasBody) {
                    continue
                }

                $constants =
                    New-Object System.Collections.Generic.HashSet[string]
                $newArrayConstants =
                    New-Object System.Collections.Generic.HashSet[string]
                $netBufferRefs =
                    New-Object System.Collections.Generic.HashSet[string]
                $bufferRefs = 0
                $arrayElementOps = 0
                $instructions = @($method.Body.Instructions)

                for ($i = 0; $i -lt $instructions.Count; $i++) {
                    $instruction = $instructions[$i]
                    $operand = ""
                    if ($null -ne $instruction.Operand) {
                        $operand = $instruction.Operand.ToString()
                    }

                    if (Test-ConstantInstruction $instruction) {
                        $value = 0
                        if ([int]::TryParse([string]$instruction.Operand,
                            [ref]$value) -and
                            $limitConstants -contains $value) {
                            [void]$constants.Add([string]$value)
                        }
                    }

                    if ($instruction.OpCode.Name -eq "newarr") {
                        $arrayConstant = Get-NewArrayConstants (
                            $instructions,
                            $i)
                        if (-not [string]::IsNullOrWhiteSpace($arrayConstant)) {
                            [void]$newArrayConstants.Add($arrayConstant)
                        }
                    }

                    if ($operand -match "NetManager::m_(nodes|segments|lanes)") {
                        [void]$netBufferRefs.Add(
                            [regex]::Match(
                                $operand,
                                "NetManager::m_(nodes|segments|lanes)").Value)
                    }

                    if ($operand -match "(?i)(m_buffer|Buffer|NodeBuffer|SegmentBuffer)") {
                        $bufferRefs++
                    }

                    if ($instruction.OpCode.Name -match "^(ldelem|stelem)") {
                        $arrayElementOps++
                    }
                }

                $parameterTypes = @(
                    $method.Parameters |
                        ForEach-Object { $_.ParameterType.FullName }
                )
                $localTypes = @(
                    $method.Body.Variables |
                        ForEach-Object { $_.VariableType.FullName }
                )
                $usesUInt16 =
                    ($parameterTypes -contains "System.UInt16") -or
                    ($localTypes -contains "System.UInt16")

                $score =
                    ($constants.Count * 2) +
                    ($newArrayConstants.Count * 4) +
                    ($netBufferRefs.Count * 8) +
                    [Math]::Min($bufferRefs, 8) +
                    [Math]::Min($arrayElementOps, 8) +
                    $(if ($usesUInt16) { 2 } else { 0 })

                if ($score -lt $MinimumScore) {
                    continue
                }

                $signature = (
                    @($method.Parameters) |
                        ForEach-Object { $_.ParameterType.FullName }
                ) -join ","

                $rows.Add([pscustomobject]@{
                    Score = $score
                    ModId = $modId
                    Assembly = $assemblyName
                    Type = $typeName
                    Method = $method.Name
                    Signature = $signature
                    Constants = (
                        [string]::Join(",", [string[]]$constants))
                    NewArrayConstants = (
                        [string]::Join(
                            ",",
                            [string[]]$newArrayConstants))
                    NetBufferRefs = (
                        [string]::Join(",", [string[]]$netBufferRefs))
                    BufferRefs = $bufferRefs
                    ArrayElementOps = $arrayElementOps
                    UsesUInt16 = $usesUInt16
                    ArrayFields = $arrayFields
                    Path = $path
                })
            }
        }
    }
}

$sorted = $rows |
    Sort-Object -Property @{
        Expression = "Score"
        Descending = $true
    }, ModId, Type, Method

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $sorted |
        Export-Csv -NoTypeInformation -Encoding UTF8 -Path $OutputPath
    Write-Host "Wrote $($sorted.Count) rows to $OutputPath"
}
else {
    $sorted |
        Select-Object -First 200 |
        Format-Table Score, ModId, Assembly, Type, Method, Constants,
            NewArrayConstants, NetBufferRefs, BufferRefs, ArrayElementOps `
            -AutoSize
}
