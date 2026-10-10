[CmdletBinding()]
param(
    [switch] $Check
)

$ErrorActionPreference = 'Stop'
$compiledModelRoot = Resolve-Path (Join-Path $PSScriptRoot '../../Shared/ConduitLLM.Configuration/Data/CompiledModels')
$entityTypes = @(
    'BillingAuditEventEntityType',
    'FunctionCallAuditEntityType',
    'FunctionConfigurationEntityType',
    'FunctionCostEntityType',
    'FunctionCredentialEntityType',
    'FunctionExecutionAuditEntityType',
    'FunctionExecutionEntityType',
    'IpFilterEntityEntityType',
    'MediaRetentionPolicyEntityType',
    'ModelCostEntityType',
    'ModelEntityType',
    'ModelProviderTypeAssociationEntityType',
    'ModelSeriesEntityType',
    'NotificationEntityType',
    'ProviderEntityType',
    'ProviderMetadataDriftItemEntityType',
    'ProviderMetadataSyncRunEntityType',
    'ProviderToolEntityType',
    'RequestLogEntityType',
    'VirtualKeyEntityType',
    'VirtualKeyGroupEntityType',
    'VirtualKeyGroupTransactionEntityType'
)

$marker = '        public static RuntimeEntityType Create('
$attributeStart = '        [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage('
$justification = 'EF Core 10 generates closed enum/array mapping setup through APIs annotated for arbitrary runtime types. Every concrete type is statically named and rooted by this compiled model. Owner: database/runtime maintainers; upstream: dotnet/efcore#29754; remove when the generator emits warning-free mappings.'
$changed = [Collections.Generic.List[string]]::new()

foreach ($entityType in $entityTypes) {
    $path = Join-Path $compiledModelRoot "$entityType.cs"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Expected generated entity type does not exist: $path"
    }

    $content = Get-Content -LiteralPath $path -Raw
    $markerCount = ([regex]::Matches($content, [regex]::Escape($marker))).Count
    if ($markerCount -ne 1) {
        throw "Expected exactly one generated Create method in '$path', found $markerCount."
    }

    $newLine = if ($content.Contains("`r`n", [StringComparison]::Ordinal)) { "`r`n" } else { "`n" }
    $attribute = @(
        $attributeStart
        '            "AOT",'
        '            "IL3050",'
        "            Justification = `"$justification`")]"
    ) -join $newLine
    if ($content.Contains($attributeStart, [StringComparison]::Ordinal)) {
        $attributeCount = ([regex]::Matches($content, [regex]::Escape($attributeStart))).Count
        if ($attributeCount -ne 1 -or -not $content.Contains("$attribute$newLine$marker", [StringComparison]::Ordinal)) {
            throw "Compiled-model exception must match the reviewed IL3050 attribute on Create exactly: $path"
        }
        if (-not $Check) {
            # EF emits these checked-in files as UTF-8 with BOM; preserve that format
            # so normalization does not create unrelated generated-file churn.
            [IO.File]::WriteAllText($path, $content, [Text.UTF8Encoding]::new($true))
        }
        continue
    }

    if ($Check) {
        $changed.Add($entityType)
        continue
    }

    $updated = $content.Replace($marker, "$attribute$newLine$marker", [StringComparison]::Ordinal)
    [IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($true))
    $changed.Add($entityType)
}

$unexpected = @(
    Get-ChildItem -LiteralPath $compiledModelRoot -Filter '*EntityType.cs' -File |
        Where-Object BaseName -notin $entityTypes |
        Where-Object { (Get-Content -LiteralPath $_.FullName -Raw).Contains($attributeStart, [StringComparison]::Ordinal) } |
        Select-Object -ExpandProperty BaseName
)
if ($unexpected.Count -gt 0) {
    throw "Unexpected compiled-model IL3050 exceptions: $($unexpected -join ', ')"
}

if ($Check -and $changed.Count -gt 0) {
    throw "Generated compiled model is not normalized: $($changed -join ', '). Run ./scripts/aot/normalize-compiled-model.ps1 after regenerating it."
}

if ($Check) {
    Write-Host "Verified $($entityTypes.Count) narrow EF compiled-model AOT exceptions."
}
else {
    Write-Host "Normalized $($changed.Count) generated compiled-model files."
}
