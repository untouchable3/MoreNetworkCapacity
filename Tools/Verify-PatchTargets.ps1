param(
    [string]$AssemblyPath = "D:\steam\steamapps\common\Cities_Skylines\Cities_Data\Managed\Assembly-CSharp.dll",
    [string]$OccurrencesPath = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OccurrencesPath)) {
    $OccurrencesPath = Join-Path $root "artifacts\patch-occurrences.csv"
}

$cecilPath = "D:\steam\steamapps\common\Cities_Skylines\Mono\lib\mono\2.0\Mono.Cecil.dll"
$expectedHash =
    "DF557994A03C93D8BB4A14F80B1FBCC0FB29D547658442445D078D069BCDD9FE"

Add-Type -Path $cecilPath

if (-not (Test-Path $AssemblyPath)) {
    throw "Assembly not found: $AssemblyPath"
}
if (-not (Test-Path $OccurrencesPath)) {
    throw "Patch occurrence list not found: $OccurrencesPath"
}

$hash = (Get-FileHash -Algorithm SHA256 $AssemblyPath).Hash
if ($hash -ne $expectedHash) {
    throw "Assembly hash mismatch: $hash"
}

$assembly = [Mono.Cecil.AssemblyFactory]::GetAssembly($AssemblyPath)
$types = @{}

function Add-TypeTree($type) {
    $types[$type.FullName.Replace("/", "+")] = $type
    foreach ($nested in $type.NestedTypes) {
        Add-TypeTree $nested
    }
}

foreach ($type in $assembly.MainModule.Types) {
    Add-TypeTree $type
}

$failures = New-Object System.Collections.Generic.List[string]

function Get-TypeOrNull([string]$fullName) {
    if ($types.ContainsKey($fullName)) {
        return $types[$fullName]
    }
    return $null
}

function Get-MethodOrNull([string]$typeName, [string]$methodName) {
    $type = Get-TypeOrNull $typeName
    if ($null -eq $type) {
        return $null
    }
    return ($type.Methods |
        Where-Object { $_.Name -eq $methodName } |
        Select-Object -First 1)
}

function Get-MethodByToken(
    [string]$typeName,
    [string]$methodName,
    [string]$tokenText) {
    $type = Get-TypeOrNull $typeName
    if ($null -eq $type) {
        return $null
    }

    [uint32]$token = 0
    if (-not [uint32]::TryParse($tokenText, [ref]$token)) {
        return $null
    }

    return ($type.Methods |
        Where-Object {
            $_.Name -eq $methodName -and
            $_.MetadataToken.ToUInt() -eq $token
        } |
        Select-Object -First 1)
}

function Get-Signature($method) {
    if ($null -eq $method) {
        return ""
    }

    return ((@($method.Parameters) |
        ForEach-Object { $_.ParameterType.FullName }) -join ",")
}

function Get-HexOffset([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value) -or
        -not $value.StartsWith("0x")) {
        return -1
    }

    try {
        return [Convert]::ToInt32($value.Substring(2), 16)
    }
    catch {
        return -1
    }
}

function Test-Int32Constant($instruction, [int]$value) {
    if ($null -eq $instruction) {
        return $false
    }

    return $instruction.OpCode.Name -like "ldc.i4*" -and
        $null -ne $instruction.Operand -and
        [int]$instruction.Operand -eq $value
}

function Test-ConditionalBranch([string]$opCodeName) {
    return $opCodeName -in @(
        "beq",
        "beq.s",
        "bge",
        "bge.s",
        "bge.un",
        "bge.un.s",
        "bgt",
        "bgt.s",
        "bgt.un",
        "bgt.un.s",
        "ble",
        "ble.s",
        "ble.un",
        "ble.un.s",
        "blt",
        "blt.s",
        "blt.un",
        "blt.un.s",
        "bne.un",
        "bne.un.s")
}

function Test-FieldNear(
    $method,
    [int]$index,
    [string]$fieldName,
    [bool]$lookBack,
    [int]$distance) {
    if ($null -eq $method -or -not $method.HasBody) {
        return $false
    }

    $instructions = @($method.Body.Instructions)
    $start = if ($lookBack) {
        [Math]::Max(0, $index - $distance)
    }
    else {
        $index
    }
    $end = if ($lookBack) {
        $index - 1
    }
    else {
        [Math]::Min($instructions.Count - 1, $index + $distance)
    }

    for ($i = $start; $i -le $end; $i++) {
        if ($null -ne $instructions[$i].Operand -and
            $instructions[$i].Operand.ToString().Contains($fieldName)) {
            return $true
        }
    }

    return $false
}

function Get-ConstantBranchIndexes($method, [int[]]$values) {
    if ($null -eq $method -or -not $method.HasBody) {
        return @()
    }

    $indexes = New-Object System.Collections.Generic.List[int]
    $instructions = @($method.Body.Instructions)
    for ($i = 0; $i + 1 -lt $instructions.Count; $i++) {
        if (-not (Test-Int32Constant $instructions[$i] $values[0]) -and
            -not (Test-Int32Constant $instructions[$i] $values[-1])) {
            continue
        }
        if (Test-ConditionalBranch $instructions[$i + 1].OpCode.Name) {
            $indexes.Add($i)
        }
    }
    return $indexes
}

function Test-NewArrayAssignment(
    $method,
    [int]$constant,
    [string]$fieldName) {
    if ($null -eq $method -or -not $method.HasBody) {
        return $false
    }

    $instructions = @($method.Body.Instructions)
    for ($i = 0; $i + 2 -lt $instructions.Count; $i++) {
        if (-not (Test-Int32Constant $instructions[$i] $constant) -or
            $instructions[$i + 1].OpCode.Name -ne "newarr") {
            continue
        }
        if ($null -ne $instructions[$i + 2].Operand -and
            $instructions[$i + 2].Operand.ToString().Contains($fieldName)) {
            return $true
        }
    }
    return $false
}

function Test-FieldAssignment(
    $method,
    [int]$constant,
    [string]$fieldName) {
    if ($null -eq $method -or -not $method.HasBody) {
        return $false
    }

    $instructions = @($method.Body.Instructions)
    for ($i = 0; $i + 1 -lt $instructions.Count; $i++) {
        if (-not (Test-Int32Constant $instructions[$i] $constant) -or
            $instructions[$i + 1].OpCode.Name -ne "stfld") {
            continue
        }
        if ($null -ne $instructions[$i + 1].Operand -and
            $instructions[$i + 1].Operand.ToString().Contains($fieldName)) {
            return $true
        }
    }
    return $false
}

function Test-BufferSizePattern(
    $method,
    [string]$managerField,
    [string]$bufferField) {
    if ($null -eq $method -or -not $method.HasBody) {
        return $false
    }

    $instructions = @($method.Body.Instructions)
    for ($i = 0; $i + 1 -lt $instructions.Count; $i++) {
        if ($null -ne $instructions[$i].Operand -and
            $instructions[$i].Operand.ToString().Contains($managerField) -and
            $null -ne $instructions[$i + 1].Operand -and
            $instructions[$i + 1].Operand.ToString().Contains($bufferField)) {
            return $true
        }
    }
    return $false
}

function Assert-ExactConstant(
    [string]$typeName,
    [string]$methodName,
    [string]$tokenText,
    [int]$index,
    [string]$offsetText,
    [int]$constant,
    [string]$description) {
    $method = Get-MethodByToken $typeName $methodName $tokenText
    if ($null -eq $method) {
        $failures.Add("Missing exact target for $description.")
        return
    }

    $instructions = @($method.Body.Instructions)
    if ($index -lt 0 -or $index -ge $instructions.Count) {
        $failures.Add("Instruction index is out of range for $description.")
        return
    }

    $expectedOffset = Get-HexOffset $offsetText
    $instruction = $instructions[$index]
    if ($expectedOffset -lt 0 -or
        $instruction.Offset -ne $expectedOffset -or
        -not (Test-Int32Constant $instruction $constant)) {
        $failures.Add("Exact IL mismatch for $description.")
    }
}

$rows = @(Import-Csv $OccurrencesPath)
if ($rows.Count -eq 0) {
    $failures.Add("Patch occurrence list is empty.")
}

$methodKeys = @{}
$nodeCount = 0
$segmentCount = 0
foreach ($row in $rows) {
    foreach ($field in @(
            "Type",
            "Method",
            "MetadataToken",
            "Signature",
            "Index",
            "IlOffset",
            "Constant",
            "Kind",
            "Reason")) {
        if (-not $row.PSObject.Properties.Name.Contains($field)) {
            $failures.Add(
                "Patch occurrence list is missing column $field.")
            continue
        }
    }

    if ([string]::IsNullOrWhiteSpace($row.Type) -or
        [string]::IsNullOrWhiteSpace($row.Method) -or
        [string]::IsNullOrWhiteSpace($row.MetadataToken) -or
        [string]::IsNullOrWhiteSpace($row.Index) -or
        [string]::IsNullOrWhiteSpace($row.IlOffset) -or
        [string]::IsNullOrWhiteSpace($row.Constant) -or
        [string]::IsNullOrWhiteSpace($row.Kind) -or
        [string]::IsNullOrWhiteSpace($row.Reason)) {
        $failures.Add("Patch occurrence list contains an incomplete row.")
        continue
    }

    if ($row.Type -eq "NetManager+Data" -and
        $row.Method -eq "Deserialize") {
        $failures.Add(
            "Historical NetManager.Data.Deserialize must not be patched.")
        continue
    }

    $method = Get-MethodByToken `
        $row.Type `
        $row.Method `
        $row.MetadataToken
    if ($null -eq $method) {
        $failures.Add(
            "Missing occurrence target: " +
            "$($row.Type).$($row.Method) [$($row.MetadataToken)]")
        continue
    }

    if ((Get-Signature $method) -ne $row.Signature) {
        $failures.Add(
            "Signature mismatch for $($row.Type).$($row.Method) " +
            "[$($row.MetadataToken)]")
    }

    [int]$index = -1
    [int]$constant = 0
    if (-not [int]::TryParse($row.Index, [ref]$index) -or
        $index -lt 0 -or
        -not [int]::TryParse($row.Constant, [ref]$constant)) {
        $failures.Add(
            "Invalid occurrence values for $($row.Type).$($row.Method).")
        continue
    }

    if ($row.Kind -eq "node") {
        $nodeCount++
    }
    elseif ($row.Kind -eq "segment") {
        $segmentCount++
    }
    else {
        $failures.Add("Invalid occurrence kind: $($row.Kind)")
        continue
    }

    $instructions = @($method.Body.Instructions)
    if ($index -ge $instructions.Count) {
        $failures.Add(
            "Occurrence index is out of range for " +
            "$($row.Type).$($row.Method).")
        continue
    }

    $instruction = $instructions[$index]
    $expectedOffset = Get-HexOffset $row.IlOffset
    if ($expectedOffset -lt 0 -or
        $instruction.Offset -ne $expectedOffset -or
        -not (Test-Int32Constant $instruction $constant)) {
        $failures.Add(
            "Occurrence IL mismatch for " +
            "$($row.Type).$($row.Method) [$($row.MetadataToken)] " +
            "index $index.")
    }

    if ($index + 1 -ge $instructions.Count -or
        -not (Test-ConditionalBranch $instructions[$index + 1].OpCode.Name)) {
        $failures.Add(
            "Occurrence is not followed by a conditional branch: " +
            "$($row.Type).$($row.Method) [$($row.MetadataToken)] " +
            "index $index.")
    }

    $methodKey = "$($row.Type)|$($row.Method)|$($row.MetadataToken)"
    $methodKeys[$methodKey] = $true
}

$generatedSource = Join-Path $root "Source\Generated\PatchTargets.generated.cs"
if (-not (Test-Path $generatedSource)) {
    $failures.Add("Generated patch target source is missing.")
}
else {
    $generatedMethods = @(
        Select-String `
            -Path $generatedSource `
            -Pattern '^\s+new string\[\] \{ "[^"]*", "[^"]*", "[^"]*", "[^"]*" \},$'
    ).Count
    $generatedOccurrences = @(
        Select-String `
            -Path $generatedSource `
            -Pattern '^\s+new string\[\] \{ "[^"]*", "[^"]*", "[^"]*", "[^"]*", "[^"]*", "[^"]*" \},$'
    ).Count

    if ($generatedMethods -ne $methodKeys.Count) {
        $failures.Add(
            "Generated method count does not match the occurrence list.")
    }
    if ($generatedOccurrences -ne $rows.Count) {
        $failures.Add(
            "Generated occurrence count does not match the occurrence list.")
    }
}

$checkLimits = Get-MethodOrNull "NetManager" "CheckLimits"
if ($null -eq $checkLimits) {
    $failures.Add("NetManager.CheckLimits was not found.")
}
else {
    $checkValues = @(
        $checkLimits.Body.Instructions |
        Where-Object { $_.OpCode.Name -like "ldc.i4*" } |
        ForEach-Object { [string]$_.Operand }
    )
    if ($checkValues -notcontains "32256") {
        $failures.Add(
            "CheckLimits does not contain node stop value 32256.")
    }
    if ($checkValues -notcontains "36352") {
        $failures.Add(
            "CheckLimits does not contain segment stop value 36352.")
    }
}

$deserialize = Get-MethodOrNull "NetManager+Data" "Deserialize"
$serialize = Get-MethodOrNull "NetManager+Data" "Serialize"
foreach ($pair in @(
        @("NetManager::m_nodes", 'Array16`1<NetNode>::m_buffer'),
        @("NetManager::m_segments", 'Array16`1<NetSegment>::m_buffer'))) {
    if (-not (Test-BufferSizePattern $deserialize $pair[0] $pair[1])) {
        $failures.Add("Deserialize pattern missing for $($pair[0]).")
    }
    if (-not (Test-BufferSizePattern $serialize $pair[0] $pair[1])) {
        $failures.Add("Serialize pattern missing for $($pair[0]).")
    }
}

$coverageAwake = Get-MethodOrNull "CoverageManager" "Awake"
$electricityAwake = Get-MethodOrNull "ElectricityManager" "Awake"
$waterAwake = Get-MethodOrNull "WaterManager" "Awake"
if (-not (Test-NewArrayAssignment $coverageAwake 32768 "m_nodes")) {
    $failures.Add("CoverageManager.m_nodes array pattern missing.")
}
if (-not (Test-NewArrayAssignment $coverageAwake 32768 "m_distances")) {
    $failures.Add("CoverageManager.m_distances array pattern missing.")
}
if (-not (Test-NewArrayAssignment $electricityAwake 32768 "m_pulseUnits")) {
    $failures.Add("ElectricityManager.m_pulseUnits array pattern missing.")
}
if (-not (Test-NewArrayAssignment $electricityAwake 32768 "m_nodeGroups")) {
    $failures.Add("ElectricityManager.m_nodeGroups array pattern missing.")
}
foreach ($fieldName in @(
        "m_waterPulseUnits",
        "m_sewagePulseUnits",
        "m_heatingPulseUnits",
        "m_nodeData")) {
    if (-not (Test-NewArrayAssignment $waterAwake 32768 $fieldName)) {
        $failures.Add("WaterManager.$fieldName array pattern missing.")
    }
}

Assert-ExactConstant `
    "CoverageManager" "Awake" "100675981" 6 "0x0012" 32768 `
    "CoverageManager.m_nodes allocation"
Assert-ExactConstant `
    "CoverageManager" "Awake" "100675981" 10 "0x0022" 32768 `
    "CoverageManager.m_distances allocation"
Assert-ExactConstant `
    "ElectricityManager" "Awake" "100676087" 11 "0x0027" 32768 `
    "ElectricityManager.m_pulseUnits allocation"
Assert-ExactConstant `
    "ElectricityManager" "Awake" "100676087" 15 "0x0037" 32768 `
    "ElectricityManager.m_nodeGroups allocation"
Assert-ExactConstant `
    "WaterManager" "Awake" "100676587" 19 "0x0047" 32768 `
    "WaterManager.m_waterPulseUnits allocation"
Assert-ExactConstant `
    "WaterManager" "Awake" "100676587" 23 "0x0057" 32768 `
    "WaterManager.m_sewagePulseUnits allocation"
Assert-ExactConstant `
    "WaterManager" "Awake" "100676587" 27 "0x0067" 32768 `
    "WaterManager.m_heatingPulseUnits allocation"
Assert-ExactConstant `
    "WaterManager" "Awake" "100676587" 31 "0x0077" 32768 `
    "WaterManager.m_nodeData allocation"

foreach ($simulationStep in @(
        @(
            "ElectricityManager",
            "100676105",
            38,
            "0x0066",
            46,
            "0x0072"),
        @(
            "WaterManager",
            "100676625",
            56,
            "0x0090",
            64,
            "0x009C"))) {
    Assert-ExactConstant `
        $simulationStep[0] `
        "SimulationStepImpl" `
        $simulationStep[1] `
        $simulationStep[2] `
        $simulationStep[3] `
        32768 `
        "$($simulationStep[0]).SimulationStepImpl first slice"
    Assert-ExactConstant `
        $simulationStep[0] `
        "SimulationStepImpl" `
        $simulationStep[1] `
        $simulationStep[4] `
        $simulationStep[5] `
        32768 `
        "$($simulationStep[0]).SimulationStepImpl second slice"
}

$colorUpdate = Get-MethodOrNull "NetManager" "UpdateColorMap"
if (-not (Test-FieldAssignment $colorUpdate 32768 "m_colorUpdateMinNode")) {
    $failures.Add("NetManager color-update node range pattern missing.")
}
if (-not (Test-FieldAssignment $colorUpdate 36864 "m_colorUpdateMinSegment")) {
    $failures.Add("NetManager color-update segment range pattern missing.")
}

Assert-ExactConstant `
    "RenderManager" "Awake" "100676925" 7 "0x0017" 119042 `
    "RenderManager.m_indices allocation"
Assert-ExactConstant `
    "RenderManager" "Awake" "100676925" 64 "0x00BB" 119042 `
    "RenderManager.m_indices initialization"

$electricitySerialize = Get-MethodOrNull `
    "ElectricityManager+Data" `
    "Serialize"
$electricityDeserialize = Get-MethodOrNull `
    "ElectricityManager+Data" `
    "Deserialize"
$waterSerialize = Get-MethodOrNull "WaterManager+Data" "Serialize"
$waterDeserialize = Get-MethodOrNull "WaterManager+Data" "Deserialize"
foreach ($method in @(
        $electricitySerialize,
        $electricityDeserialize,
        $waterSerialize,
        $waterDeserialize)) {
    if ($null -eq $method) {
        $failures.Add("Auxiliary serialization method is missing.")
    }
}

$electricitySerializeNodes = @(
    Get-ConstantBranchIndexes $electricitySerialize @(32768)
)
$electricityDeserializeNodes = @(
    Get-ConstantBranchIndexes $electricityDeserialize @(32768)
)
$waterSerializeNodes = @(
    Get-ConstantBranchIndexes $waterSerialize @(32768)
)
if ($electricitySerializeNodes.Count -ne 1) {
    $failures.Add(
        "Electricity serialize node-nodeGroups count mismatch.")
}
if ($electricityDeserializeNodes.Count -ne 3) {
    $failures.Add(
        "Electricity deserialize node-nodeGroups count mismatch.")
}
if ($waterSerializeNodes.Count -ne 12) {
    $failures.Add(
        "Water serialize node data count mismatch.")
}

$waterDeserializePattern = $false
$waterInstructions = @($waterDeserialize.Body.Instructions)
for ($i = 0; $i + 1 -lt $waterInstructions.Count; $i++) {
    if ((Test-Int32Constant $waterInstructions[$i] 32768) -and
        $waterInstructions[$i + 1].OpCode.Name -like "stloc*" -and
        (Test-FieldNear $waterDeserialize $i "m_nodeData" $false 16)) {
        $waterDeserializePattern = $true
        break
    }
}
if (-not $waterDeserializePattern) {
    $failures.Add(
        "Water deserialize node-data count assignment is missing.")
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Error $failure
    }
    exit 1
}

Write-Host (
    "Patch target verification passed: " +
    $rows.Count +
    " occurrences across " +
    $methodKeys.Count +
    " methods (nodes=" +
    $nodeCount +
    ", segments=" +
    $segmentCount +
    ").")
