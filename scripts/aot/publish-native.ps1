param(
    [string]$ArtifactDirectory,
    [string]$Runtime = "linux-x64",
    [switch]$NoRestore,
    [string]$WarningBaselinePath,
    [switch]$UpdateWarningBaseline
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
if ([string]::IsNullOrWhiteSpace($WarningBaselinePath)) {
    $WarningBaselinePath = Join-Path $PSScriptRoot "linker-warning-baseline.json"
}
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $repoRoot "artifacts/native-aot"
}
$artifactRoot = [IO.Path]::GetFullPath($ArtifactDirectory)
$runtimeRoot = Join-Path $artifactRoot "runtime"
$symbolsRoot = Join-Path $artifactRoot "symbols"
$reportsRoot = Join-Path $artifactRoot "reports"
$warningBaselineFile = [IO.Path]::GetFullPath($WarningBaselinePath)
New-Item -ItemType Directory -Force -Path $runtimeRoot, $symbolsRoot, $reportsRoot | Out-Null

$metrics = [Collections.Generic.List[object]]::new()
foreach ($service in @("Admin", "Gateway")) {
    $serviceSlug = $service.ToLowerInvariant()
    $project = Join-Path $repoRoot "Services/ConduitLLM.$service/ConduitLLM.$service.csproj"
    $publishDirectory = Join-Path $runtimeRoot $serviceSlug
    $symbolDirectory = Join-Path $symbolsRoot $serviceSlug
    $logPath = Join-Path $reportsRoot "$serviceSlug-publish.log"
    New-Item -ItemType Directory -Force -Path $publishDirectory, $symbolDirectory | Out-Null

    $arguments = @(
        "publish"
        $project
        "--configuration", "Release"
        "--runtime", $Runtime
        "--self-contained", "true"
        "--output", $publishDirectory
        "--nologo"
        "--tl:off"
        "--maxcpucount:1"
        "--verbosity", "minimal"
        "-p:PublishAot=true"
        "-p:ConduitAotAudit=true"
        "-p:StripSymbols=true"
        "-p:UseSharedCompilation=false"
    )
    if ($NoRestore) {
        $arguments += "--no-restore"
    }

    Write-Host "Publishing ConduitLLM.$service for $Runtime NativeAOT..."
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    & dotnet @arguments 2>&1 | Tee-Object -FilePath $logPath
    $publishExitCode = $LASTEXITCODE
    $stopwatch.Stop()
    if ($publishExitCode -ne 0) {
        throw "$service NativeAOT publish failed with exit code $publishExitCode."
    }

    Get-ChildItem -LiteralPath $publishDirectory -File |
        Where-Object { $_.Extension -in @(".dbg", ".pdb") } |
        Move-Item -Destination $symbolDirectory

    $executableName = "ConduitLLM.$service"
    if ($Runtime.StartsWith("win-", [StringComparison]::OrdinalIgnoreCase)) {
        $executableName += ".exe"
    }
    $executable = Join-Path $publishDirectory $executableName
    if (!(Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "$service native executable was not produced: $executable"
    }

    $runtimeBytes = (
        Get-ChildItem -LiteralPath $publishDirectory -Recurse -File |
            Measure-Object -Property Length -Sum
    ).Sum
    $metrics.Add([ordered]@{
        service = $service
        rid = $Runtime
        publishDurationMilliseconds = $stopwatch.ElapsedMilliseconds
        executableBytes = (Get-Item -LiteralPath $executable).Length
        runtimeArtifactBytes = [long]$runtimeBytes
    })
}

# Publish the JSON-only SignalR/HTTP parity client as native code too. This proves
# the client-side protocol path used by the process harness is statically compiled.
$probeProject = Join-Path $repoRoot "Tests/ConduitLLM.GatewayNativeAotTests/ConduitLLM.GatewayNativeAotTests.csproj"
$probeDirectory = Join-Path $runtimeRoot "probe"
$probeSymbolDirectory = Join-Path $symbolsRoot "probe"
$probeLogPath = Join-Path $reportsRoot "probe-publish.log"
New-Item -ItemType Directory -Force -Path $probeDirectory, $probeSymbolDirectory | Out-Null
$probeArguments = @(
    "publish"
    $probeProject
    "--configuration", "Release"
    "--runtime", $Runtime
    "--self-contained", "true"
    "--output", $probeDirectory
    "--nologo"
    "--tl:off"
    "--maxcpucount:1"
    "--verbosity", "minimal"
    "-p:PublishAot=true"
    "-p:StripSymbols=true"
    "-p:UseSharedCompilation=false"
)
if ($NoRestore) {
    $probeArguments += "--no-restore"
}
Write-Host "Publishing the Gateway NativeAOT parity client for $Runtime..."
& dotnet @probeArguments 2>&1 | Tee-Object -FilePath $probeLogPath
if ($LASTEXITCODE -ne 0) {
    throw "Gateway parity client NativeAOT publish failed with exit code $LASTEXITCODE."
}

Get-ChildItem -LiteralPath $probeDirectory -File |
    Where-Object { $_.Extension -in @(".dbg", ".pdb") } |
    Move-Item -Destination $probeSymbolDirectory -Force

$linkerAuditArguments = @{
    ReportDirectory = $reportsRoot
    BaselinePath = $warningBaselineFile
    Runtime = $Runtime
}
if ($UpdateWarningBaseline) {
    $linkerAuditArguments.UpdateBaseline = $true
}
& (Join-Path $PSScriptRoot "evaluate-native-linker-warnings.ps1") @linkerAuditArguments

[ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTime]::UtcNow.ToString("O")
    services = $metrics
} | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $reportsRoot "native-baselines.json") -Encoding utf8

Write-Host "Native runtime artifacts: $runtimeRoot"
Write-Host "Native symbols: $symbolsRoot"
