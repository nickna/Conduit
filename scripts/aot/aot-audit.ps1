param(
    [string]$OutputDirectory,
    [string]$BaselinePath,
    [switch]$NoRestore,
    [switch]$UpdateBaseline
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts/aot-audit"
}
if ([string]::IsNullOrWhiteSpace($BaselinePath)) {
    $BaselinePath = Join-Path $PSScriptRoot "warning-baseline.json"
}

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$baselineFile = [IO.Path]::GetFullPath($BaselinePath)
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$services = @("Admin", "Gateway")
$buildFailures = @()

foreach ($service in $services) {
    $project = Join-Path $repoRoot "Services/ConduitLLM.$service/ConduitLLM.$service.csproj"
    $logPath = Join-Path $outputRoot "$($service.ToLowerInvariant()).log"
    $arguments = @(
        "build"
        $project
        "--configuration", "Release"
        "--nologo"
        "--tl:off"
        # Rebuild cleans and builds shared project references. Running those targets
        # concurrently can race under newer MSBuild versions and fail without a
        # diagnostic, so keep each service graph deterministic.
        "--maxcpucount:1"
        "--verbosity", "minimal"
        "-t:Rebuild"
        "-p:ConduitAotAudit=true"
        "-p:GenerateFullPaths=true"
        "-p:UseSharedCompilation=false"
    )
    if ($NoRestore) {
        $arguments += "--no-restore"
    }

    Write-Host "Running $service AOT analyzer build..."
    & dotnet @arguments 2>&1 | Tee-Object -FilePath $logPath
    if ($LASTEXITCODE -ne 0) {
        $buildFailures += $service
    }
}

# MSBuild repeats warnings in its final summary, and shared-project warnings appear
# in both service builds. Retain one canonical source diagnostic for the inventory.
$warningPattern =
    '^(?<file>.+?)\((?<line>\d+),(?<column>\d+)\): warning (?<code>IL[23]\d{3}): (?<message>.+?) \[(?<project>.+?\.csproj)\]$'
$diagnostics = foreach ($log in Get-ChildItem -LiteralPath $outputRoot -Filter "*.log" -File) {
    foreach ($logLine in Get-Content -LiteralPath $log.FullName) {
        if ($logLine -notmatch $warningPattern) {
            continue
        }

        $sourcePath = [IO.Path]::GetFullPath($Matches.file)
        if (!$sourcePath.StartsWith($repoRoot.Path, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        [pscustomobject]@{
            code = $Matches.code
            project = [IO.Path]::GetFileNameWithoutExtension($Matches.project)
            file = $sourcePath.Substring($repoRoot.Path.Length).
                TrimStart([char[]]@('\', '/')).Replace('\', '/')
            line = [int]$Matches.line
            column = [int]$Matches.column
            message = $Matches.message
        }
    }
}

$diagnostics = @(
    $diagnostics |
        Sort-Object code, project, file, line, column, message -Unique
)
$groups = @(
    $diagnostics |
        Group-Object project, code, file |
        ForEach-Object {
            $first = $_.Group[0]
            [pscustomobject]@{
                project = $first.project
                code = $first.code
                file = $first.file
                count = $_.Count
            }
        } |
        Sort-Object project, code, file
)

$inventory = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTime]::UtcNow.ToString("O")
    total = $diagnostics.Count
    diagnostics = $diagnostics
}
$inventory | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $outputRoot "diagnostics.json") -Encoding utf8

$summaryLines = [Collections.Generic.List[string]]::new()
$summaryLines.Add("# NativeAOT analyzer diagnostics")
$summaryLines.Add("")
$summaryLines.Add("First-party diagnostics: **$($diagnostics.Count)**")
$summaryLines.Add("")
$summaryLines.Add("| Project | Code | Count |")
$summaryLines.Add("|---|---:|---:|")
foreach ($summaryGroup in $diagnostics | Group-Object project, code | Sort-Object Name) {
    $first = $summaryGroup.Group[0]
    $summaryLines.Add("| $($first.project) | $($first.code) | $($summaryGroup.Count) |")
}
$summaryLines | Set-Content -LiteralPath (Join-Path $outputRoot "summary.md") -Encoding utf8

if ($UpdateBaseline) {
    $baseline = [ordered]@{
        schemaVersion = 1
        description = "Maximum first-party analyzer diagnostics by project, warning code, and source file. Counts may decrease without updating this file."
        groups = $groups
    }
    $baseline | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $baselineFile -Encoding utf8
    Write-Host "Updated AOT warning baseline: $baselineFile"
}
elseif (!(Test-Path -LiteralPath $baselineFile)) {
    throw "AOT warning baseline does not exist: $baselineFile"
}
else {
    $baseline = Get-Content -LiteralPath $baselineFile -Raw | ConvertFrom-Json
    if ($baseline.schemaVersion -ne 1) {
        throw "Unsupported AOT warning baseline schema: $($baseline.schemaVersion)"
    }

    $allowed = @{}
    foreach ($group in $baseline.groups) {
        $key = "$($group.project)|$($group.code)|$($group.file)"
        $allowed[$key] = [int]$group.count
    }

    $regressions = @(
        foreach ($group in $groups) {
            $key = "$($group.project)|$($group.code)|$($group.file)"
            $maximum = if ($allowed.ContainsKey($key)) { $allowed[$key] } else { 0 }
            if ($group.count -gt $maximum) {
                [pscustomobject]@{
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
        Write-Host "New first-party AOT/trim diagnostics exceed the baseline:" -ForegroundColor Red
        $regressions | Format-Table -AutoSize | Out-String | Write-Host
        throw "AOT warning baseline regression detected."
    }
}

if ($buildFailures.Count -gt 0) {
    throw "AOT analyzer build failed for: $($buildFailures -join ', ')."
}

Write-Host "AOT audit passed with $($diagnostics.Count) first-party diagnostics."
Write-Host "Inventory: $(Join-Path $outputRoot 'diagnostics.json')"
