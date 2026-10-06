param(
    [Parameter(Mandatory = $true)]
    [string]$ReportDirectory,
    [Parameter(Mandatory = $true)]
    [string]$BaselinePath,
    [string]$Runtime = "linux-x64",
    [switch]$UpdateBaseline
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$reportsRoot = [IO.Path]::GetFullPath($ReportDirectory)
$warningBaselineFile = [IO.Path]::GetFullPath($BaselinePath)

# Analyzer builds are intentionally fast, but the native linker can discover
# additional closed-generic and expression-tree warnings. Inventory those real
# publish diagnostics separately and ratchet them by service, code, and source
# file so the slow lane cannot silently regress while the baseline burns down.
$sourceWarningPattern =
    '^(?<file>.+?)\((?<line>\d+)(?:,\d+)?\): (?:Trim|AOT) analysis warning (?<code>IL(?:2026|3050|207\d|209\d)): (?<message>.+?) \[(?<project>.+?\.csproj)\]$'
$ilcWarningPattern =
    '^ILC : (?:Trim|AOT) analysis warning (?<code>IL(?:2026|3050|207\d|209\d)): (?<message>ConduitLLM\..+?) \[(?<project>.+?\.csproj)\]$'
$diagnostics = foreach ($service in @("Admin", "Gateway")) {
    $serviceSlug = $service.ToLowerInvariant()
    $logPath = Join-Path $reportsRoot "$serviceSlug-publish.log"
    if (!(Test-Path -LiteralPath $logPath -PathType Leaf)) {
        throw "Native-link publish log does not exist: $logPath"
    }

    foreach ($logLine in Get-Content -LiteralPath $logPath) {
        if ($logLine -match $sourceWarningPattern) {
            $sourcePath = [IO.Path]::GetFullPath($Matches.file)
            if (!$sourcePath.StartsWith($repoRoot.Path, [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            [pscustomobject]@{
                service = $service
                code = $Matches.code
                project = [IO.Path]::GetFileNameWithoutExtension($Matches.project)
                file = $sourcePath.Substring($repoRoot.Path.Length).
                    TrimStart([char[]]@('\', '/')).Replace('\', '/')
                line = [int]$Matches.line
                message = $Matches.message
            }
        }
        elseif ($logLine -match $ilcWarningPattern) {
            [pscustomobject]@{
                service = $service
                code = $Matches.code
                project = [IO.Path]::GetFileNameWithoutExtension($Matches.project)
                file = "ILC/$serviceSlug"
                line = 0
                message = $Matches.message
            }
        }
    }
}

$diagnostics = @(
    $diagnostics |
        Sort-Object service, code, project, file, line, message -Unique
)
$groups = @(
    $diagnostics |
        Group-Object service, project, code, file |
        ForEach-Object {
            $first = $_.Group[0]
            [pscustomobject]@{
                service = $first.service
                project = $first.project
                code = $first.code
                file = $first.file
                count = $_.Count
            }
        } |
        Sort-Object service, project, code, file
)

[ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTime]::UtcNow.ToString("O")
    rid = $Runtime
    total = $diagnostics.Count
    diagnostics = $diagnostics
} | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $reportsRoot "linker-diagnostics.json") -Encoding utf8

$summaryLines = [Collections.Generic.List[string]]::new()
$summaryLines.Add("# NativeAOT linker diagnostics")
$summaryLines.Add("")
$summaryLines.Add("First-party diagnostics: **$($diagnostics.Count)**")
$summaryLines.Add("")
$summaryLines.Add("| Service | Project | Code | Count |")
$summaryLines.Add("|---|---|---:|---:|")
foreach ($summaryGroup in $diagnostics | Group-Object service, project, code | Sort-Object Name) {
    $first = $summaryGroup.Group[0]
    $summaryLines.Add("| $($first.service) | $($first.project) | $($first.code) | $($summaryGroup.Count) |")
}
$summaryLines | Set-Content -LiteralPath (Join-Path $reportsRoot "linker-summary.md") -Encoding utf8

if ($UpdateBaseline) {
    [ordered]@{
        schemaVersion = 1
        description = "Maximum first-party native-link diagnostics by service, project, warning code, and source file. Counts may decrease without updating this file."
        groups = $groups
    } | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $warningBaselineFile -Encoding utf8
    Write-Host "Updated native-link warning baseline: $warningBaselineFile"
}
elseif (!(Test-Path -LiteralPath $warningBaselineFile)) {
    throw "Native-link warning baseline does not exist: $warningBaselineFile"
}
else {
    $baseline = Get-Content -LiteralPath $warningBaselineFile -Raw | ConvertFrom-Json
    if ($baseline.schemaVersion -ne 1) {
        throw "Unsupported native-link warning baseline schema: $($baseline.schemaVersion)"
    }

    $allowed = @{}
    foreach ($group in $baseline.groups) {
        $key = "$($group.service)|$($group.project)|$($group.code)|$($group.file)"
        $allowed[$key] = [int]$group.count
    }

    $regressions = @(
        foreach ($group in $groups) {
            $key = "$($group.service)|$($group.project)|$($group.code)|$($group.file)"
            $maximum = if ($allowed.ContainsKey($key)) { $allowed[$key] } else { 0 }
            if ($group.count -gt $maximum) {
                [pscustomobject]@{
                    service = $group.service
                    project = $group.project
                    code = $group.code
                    file = $group.file
                    baseline = $maximum
                    current = $group.count
                }
            }
        }
    )
    if ($regressions.Count -gt 0) {
        Write-Host "New first-party native-link diagnostics exceed the baseline:" -ForegroundColor Red
        $regressions | Format-Table -AutoSize | Out-String | Write-Host
        throw "Native-link warning baseline regression detected."
    }
}

Write-Host "Native linker diagnostics: $($diagnostics.Count) first-party warnings"
