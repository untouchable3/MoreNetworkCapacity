param(
    [string]$OccurrencesPath = "",
    [string]$SourceOutputPath = "",
    [string]$CsvOutputPath = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OccurrencesPath)) {
    $OccurrencesPath = Join-Path $root "artifacts\patch-occurrences.csv"
}
if ([string]::IsNullOrWhiteSpace($SourceOutputPath)) {
    $SourceOutputPath = Join-Path $root "Source\Generated\PatchTargets.generated.cs"
}
if ([string]::IsNullOrWhiteSpace($CsvOutputPath)) {
    $CsvOutputPath = Join-Path $root "artifacts\branch-bound-targets.csv"
}

if (-not (Test-Path $OccurrencesPath)) {
    throw "Patch occurrence list not found: $OccurrencesPath"
}

$rows = @(Import-Csv $OccurrencesPath)
if ($rows.Count -eq 0) {
    throw "Patch occurrence list is empty: $OccurrencesPath"
}

function ConvertTo-CSharpString([string]$value) {
    if ($null -eq $value) {
        return ""
    }

    return $value.Replace("\", "\\").Replace('"', '\"')
}

function Get-HexOffset([string]$value, [string]$description) {
    if ([string]::IsNullOrWhiteSpace($value) -or
        -not $value.StartsWith("0x")) {
        throw "Invalid IL offset for ${description}: $value"
    }

    try {
        return [Convert]::ToInt32($value.Substring(2), 16)
    }
    catch {
        throw "Invalid IL offset for ${description}: $value"
    }
}

$occurrenceKeys = @{}
$methodSignatures = @{}
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
            throw "Patch occurrence list is missing column $field."
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
        throw "Patch occurrence list contains an incomplete row."
    }

    [uint32]$token = 0
    if (-not [uint32]::TryParse($row.MetadataToken, [ref]$token)) {
        throw "Invalid metadata token: $($row.MetadataToken)"
    }

    [int]$index = -1
    if (-not [int]::TryParse($row.Index, [ref]$index) -or $index -lt 0) {
        throw "Invalid instruction index: $($row.Index)"
    }

    [int]$constant = 0
    if (-not [int]::TryParse($row.Constant, [ref]$constant) -or
        ($constant -ne 32768 -and $constant -ne 36864)) {
        throw "Unexpected original constant: $($row.Constant)"
    }

    if ($row.Kind -ne "node" -and $row.Kind -ne "segment") {
        throw "Invalid occurrence kind: $($row.Kind)"
    }

    [void](Get-HexOffset `
        $row.IlOffset `
        "$($row.Type).$($row.Method) [$($row.MetadataToken)] index $index")

    $occurrenceKey = "$($row.MetadataToken)|$($row.Index)"
    if ($occurrenceKeys.ContainsKey($occurrenceKey)) {
        throw "Duplicate occurrence key: $occurrenceKey"
    }
    $occurrenceKeys[$occurrenceKey] = $true

    $methodKey = "$($row.Type)|$($row.Method)|$($row.MetadataToken)"
    if ($methodSignatures.ContainsKey($methodKey)) {
        if ($methodSignatures[$methodKey] -ne $row.Signature) {
            throw "Conflicting signatures for $methodKey"
        }
    }
    else {
        $methodSignatures[$methodKey] = $row.Signature
    }
}

$methods = @(
    $rows |
        Group-Object Type, Method, MetadataToken, Signature |
        ForEach-Object { $_.Group[0] } |
        Sort-Object Type, Method, MetadataToken
)

$sourceDirectory = Split-Path -Parent $SourceOutputPath
$csvDirectory = Split-Path -Parent $CsvOutputPath
New-Item -ItemType Directory -Force -Path $sourceDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $csvDirectory | Out-Null

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("namespace MoreNetworkCapacity.Patches")
$lines.Add("{")
$lines.Add("    internal static class GeneratedPatchTargets")
$lines.Add("    {")
$lines.Add("        internal static readonly string[][] BranchBoundMethods =")
$lines.Add("            new string[][]")
$lines.Add("            {")

foreach ($method in $methods) {
    $type = ConvertTo-CSharpString $method.Type
    $name = ConvertTo-CSharpString $method.Method
    $token = ConvertTo-CSharpString $method.MetadataToken
    $signature = ConvertTo-CSharpString $method.Signature
    $lines.Add(
        "                new string[] { ""$type"", ""$name"", ""$token"", ""$signature"" },")
}

$lines.Add("            };")
$lines.Add("")
$lines.Add("        internal static readonly string[][] BranchBoundOccurrences =")
$lines.Add("            new string[][]")
$lines.Add("            {")

foreach ($row in $rows | Sort-Object MetadataToken, Index) {
    $token = ConvertTo-CSharpString $row.MetadataToken
    $index = ConvertTo-CSharpString $row.Index
    $ilOffset = ConvertTo-CSharpString $row.IlOffset
    $constant = ConvertTo-CSharpString $row.Constant
    $kind = ConvertTo-CSharpString $row.Kind
    $reason = ConvertTo-CSharpString $row.Reason
    $lines.Add(
        "                new string[] { ""$token"", ""$index"", ""$ilOffset"", ""$constant"", ""$kind"", ""$reason"" },")
}

$lines.Add("            };")
$lines.Add("    }")
$lines.Add("}")

[System.IO.File]::WriteAllLines(
    $SourceOutputPath,
    [string[]]$lines,
    (New-Object System.Text.UTF8Encoding($false)))

$summary = foreach ($method in $methods) {
    $methodRows = @(
        $rows | Where-Object {
            $_.Type -eq $method.Type -and
            $_.Method -eq $method.Method -and
            $_.MetadataToken -eq $method.MetadataToken
        }
    )

    [pscustomobject][ordered]@{
        Assembly = "Assembly-CSharp"
        Type = $method.Type
        Method = $method.Method
        MetadataToken = $method.MetadataToken
        Signature = $method.Signature
        Occurrences = $methodRows.Count
        NodeOccurrences = @(
            $methodRows | Where-Object { $_.Kind -eq "node" }
        ).Count
        SegmentOccurrences = @(
            $methodRows | Where-Object { $_.Kind -eq "segment" }
        ).Count
    }
}

$summary |
    Export-Csv -NoTypeInformation -Encoding UTF8 -Path $CsvOutputPath

Write-Host (
    "Generated " +
    $rows.Count +
    " exact patch occurrences across " +
    $methods.Count +
    " methods.")
