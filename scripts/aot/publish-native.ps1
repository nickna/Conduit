param(
    [string]$ArtifactDirectory,
    [ValidateSet("linux-x64", "win-x64")]
    [string]$Runtime = "linux-x64",
    [switch]$NoRestore,
    [string]$WarningBaselinePath,
    [switch]$UpdateWarningBaseline
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "native-publish-build.ps1")

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
if ([string]::IsNullOrWhiteSpace($WarningBaselinePath)) { $WarningBaselinePath = Join-Path $PSScriptRoot "linker-warning-baseline.json" }
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) { $ArtifactDirectory = Join-Path $repoRoot "artifacts/native-aot" }
$artifactRoot = [IO.Path]::GetFullPath($ArtifactDirectory)
$runtimeRoot = Join-Path $artifactRoot "runtime"
$symbolsRoot = Join-Path $artifactRoot "symbols"
$reportsRoot = Join-Path $artifactRoot "reports"
$baselinePath = Join-Path $reportsRoot "native-baselines.json"
$claimPath = Join-Path $reportsRoot "native-publish.claim"
$warningBaselineFile = [IO.Path]::GetFullPath($WarningBaselinePath)
$source = Get-NativePublishSource -RepositoryRoot $repoRoot
$buildId = [Guid]::NewGuid().ToString("N")
$nativeIntermediateRoot = Join-Path $artifactRoot "native-intermediates"
$nativeOutputRoot = Join-Path $artifactRoot "native-outputs"
$nativeIntermediateBuildRoot = Join-Path $nativeIntermediateRoot $buildId
$nativeOutputBuildRoot = Join-Path $nativeOutputRoot $buildId
Assert-NativeEvidencePathAncestry $artifactRoot
Assert-NativeEvidencePathAncestry $runtimeRoot
Assert-NativeEvidencePathAncestry $reportsRoot
Assert-NativeEvidencePathAncestry $symbolsRoot
foreach ($nativeBuildRoot in @($nativeIntermediateBuildRoot, $nativeOutputBuildRoot)) {
    Assert-NativeEvidencePathAncestry $nativeBuildRoot
    if (Test-Path -LiteralPath $nativeBuildRoot) { throw "Native compilation evidence requires a fresh invocation output root." }
}
foreach ($reportLeaf in @("publish", "native-baselines.json", "native-publish.claim", "linker-diagnostics.json", "linker-summary.md")) {
    Assert-NativeEvidencePathAncestry (Join-Path $reportsRoot $reportLeaf)
}
foreach ($service in @("admin", "gateway", "probe")) {
    Assert-NativeEvidencePathAncestry (Join-Path $symbolsRoot $service)
    Assert-NativeEvidencePathAncestry (Join-Path $reportsRoot "$service-publish.log")
}
if (Test-Path -LiteralPath $baselinePath) { throw "Native publish baselines already exist; choose a fresh ArtifactDirectory." }
if (Test-Path -LiteralPath $claimPath) { throw "Native publish artifact directory is already claimed; choose a fresh ArtifactDirectory." }
Assert-NativePublishOutputDirectoriesEmpty -RuntimeRoot $runtimeRoot
New-Item -ItemType Directory -Force -Path $runtimeRoot, $symbolsRoot, $reportsRoot | Out-Null
# The fixed CreateNew claim is retained after success or failure; retries need a
# fresh directory. Report/log directories alone may already exist.
$claim = [IO.FileStream]::new($claimPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $claimBytes = [Text.UTF8Encoding]::new($false).GetBytes($buildId + [Environment]::NewLine)
    $claim.Write($claimBytes)
    $claim.Flush($true)
}
finally { $claim.Dispose() }
$publishEvidenceDirectory = Join-Path $reportsRoot "publish/$buildId"
New-Item -ItemType Directory -Path $publishEvidenceDirectory | Out-Null
$metadataTimestamp = [DateTime]::UtcNow.ToString("O", [Globalization.CultureInfo]::InvariantCulture)
$toolchainTargets = Join-Path $PSScriptRoot "native-publish-toolchain.targets"
$published = [Collections.Generic.List[object]]::new()

foreach ($definition in @(
    @{ service = "admin"; assembly = "ConduitLLM.Admin"; project = "Services/ConduitLLM.Admin/ConduitLLM.Admin.csproj"; audit = $true },
    @{ service = "gateway"; assembly = "ConduitLLM.Gateway"; project = "Services/ConduitLLM.Gateway/ConduitLLM.Gateway.csproj"; audit = $true },
    @{ service = "probe"; assembly = "ConduitLLM.GatewayNativeAotTests"; project = "Tests/ConduitLLM.GatewayNativeAotTests/ConduitLLM.GatewayNativeAotTests.csproj"; audit = $false }
)) {
    $service = $definition.service
    $publishDirectory = Join-Path $runtimeRoot $service
    $symbolDirectory = Join-Path $symbolsRoot $service
    $logPath = Join-Path $reportsRoot "$service-publish.log"
    $capturePath = Join-Path $publishEvidenceDirectory "$service-toolchain.b64"
    $nativeIntermediateDirectory = (Join-Path $nativeIntermediateBuildRoot $service) + [IO.Path]::DirectorySeparatorChar
    $nativeOutputDirectory = (Join-Path $nativeOutputBuildRoot $service) + [IO.Path]::DirectorySeparatorChar
    New-Item -ItemType Directory -Force -Path $publishDirectory, $symbolDirectory | Out-Null
    $arguments = @(
        "publish", (Join-Path $repoRoot $definition.project), "--configuration", "Release",
        "--runtime", $Runtime, "--self-contained", "true", "--output", $publishDirectory,
        "--nologo", "--tl:off", "--maxcpucount:1", "--verbosity", "minimal",
        "-p:PublishAot=true", "-p:ConduitAotAudit=$($definition.audit.ToString().ToLowerInvariant())",
        "-p:StripSymbols=true", "-p:UseSharedCompilation=false",
        "-p:NativeIntermediateOutputPath=$(ConvertTo-MSBuildPropertyValue $nativeIntermediateDirectory)",
        "-p:NativeOutputPath=$(ConvertTo-MSBuildPropertyValue $nativeOutputDirectory)",
        "-p:CustomAfterMicrosoftCommonTargets=$(ConvertTo-MSBuildPropertyValue $toolchainTargets)",
        "-p:ConduitNativeToolchainEvidencePath=$(ConvertTo-MSBuildPropertyValue $capturePath)",
        "-p:ConduitNativeToolchainEvidenceAssemblyName=$($definition.assembly)"
    )
    if ($source.status -eq "git") {
        $arguments += "-p:ConduitCommitSha=$($source.revision)"
        $arguments += "-p:ConduitBuildTimestamp=$metadataTimestamp"
    }
    if ($NoRestore) { $arguments += "--no-restore" }
    Write-Host "Publishing $($definition.assembly) for $Runtime NativeAOT..."
    $startedAt = [DateTime]::UtcNow
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    & dotnet @arguments 2>&1 | Tee-Object -FilePath $logPath
    $publishExitCode = $LASTEXITCODE
    $stopwatch.Stop()
    if ($publishExitCode -ne 0) { throw "$service NativeAOT publish failed with exit code $publishExitCode." }
    foreach ($symbol in @(Get-ChildItem -LiteralPath $publishDirectory -File -Force | Where-Object { $_.Extension -in @(".dbg", ".pdb") })) {
        $symbolDestination = Join-Path $symbolDirectory $symbol.Name
        Assert-NativeEvidencePathAncestry $symbolDestination
        Move-Item -LiteralPath $symbol.FullName -Destination $symbolDestination -Force
    }
    $completedAt = [DateTime]::UtcNow
    $executableName = $definition.assembly + $(if ($Runtime -eq "win-x64") { ".exe" } else { "" })
    if (!(Test-Path -LiteralPath (Join-Path $publishDirectory $executableName) -PathType Leaf)) {
        throw "$service native executable was not produced."
    }
    $capture = Read-NativeToolchainCapture -Path $capturePath
    Assert-NativePublishCompileOutputs -Capture $capture -IntermediateDirectory $nativeIntermediateDirectory -OutputDirectory $nativeOutputDirectory -AssemblyName $definition.assembly -ExecutableName $executableName -RuntimeExecutable (Join-Path $publishDirectory $executableName) -Runtime $Runtime
    if ($source.status -eq "git" -and ($capture["ConduitCommitSha"] -cne $source.revision -or $capture["ConduitBuildTimestamp"] -cne $metadataTimestamp)) {
        throw "Native assembly metadata does not match the captured source identity."
    }
    $toolchain = Get-NativePublishToolchain -Capture $capture -ExpectedAssembly $definition.assembly -ExpectedAudit $definition.audit -Runtime $Runtime
    $published.Add([ordered]@{
        service = $service; directory = $publishDirectory; executableName = $executableName
        startedAt = $startedAt; completedAt = $completedAt; duration = $stopwatch.ElapsedMilliseconds
        toolchain = $toolchain
        properties = [ordered]@{
            configuration = "Release"; publishAot = $true; selfContained = $true; stripSymbols = $true
            conduitAotAudit = $definition.audit; useSharedCompilation = $false
        }
    })
}

$linkerAuditArguments = @{ ReportDirectory = $reportsRoot; BaselinePath = $warningBaselineFile; Runtime = $Runtime }
if ($UpdateWarningBaseline) { $linkerAuditArguments.UpdateBaseline = $true }
& (Join-Path $PSScriptRoot "evaluate-native-linker-warnings.ps1") @linkerAuditArguments

# Write success evidence only after every publish, symbol split and linker check.
$metrics = [Collections.Generic.List[object]]::new()
$probeManifest = $null
foreach ($publication in $published) {
    $destination = Join-Path $publishEvidenceDirectory "$($publication.service).json"
    $identity = Get-NativeRuntimeIdentity -RuntimeDirectory $publication.directory -ExecutableName $publication.executableName
    $manifestArguments = @{
        DestinationPath = $destination; BuildId = $buildId; Service = $publication.service; Rid = $Runtime
        Source = $source; Toolchain = $publication.toolchain; PublishProperties = $publication.properties
        StartedAtUtc = $publication.startedAt; CompletedAtUtc = $publication.completedAt
        RuntimeDirectory = $publication.directory; ExecutableName = $publication.executableName
    }
    $manifest = Write-NativePublishManifest @manifestArguments
    $reference = [ordered]@{
        buildId = $manifest.buildId
        path = [IO.Path]::GetRelativePath($artifactRoot, $manifest.path).Replace('\', '/')
        sha256 = $manifest.sha256
    }
    if ($publication.service -eq "probe") { $probeManifest = $reference }
    else {
        $metrics.Add([ordered]@{
            service = $(if ($publication.service -eq "admin") { "Admin" } else { "Gateway" })
            rid = $Runtime; publishDurationMilliseconds = $publication.duration
            executableBytes = $identity.executable.bytes; runtimeArtifactBytes = $identity.runtimeArtifactBytes
            publishManifest = $reference
        })
    }
}
$baselineJson = [ordered]@{
    schemaVersion = 2; generatedAtUtc = [DateTime]::UtcNow.ToString("O")
    services = $metrics; probeManifest = $probeManifest
} | ConvertTo-Json -Depth 5
$baselineStagingPath = Join-Path $reportsRoot ".native-baselines.$buildId.tmp"
$baseline = [IO.FileStream]::new($baselineStagingPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $baselineBytes = [Text.UTF8Encoding]::new($false).GetBytes($baselineJson + [Environment]::NewLine)
    $baseline.Write($baselineBytes)
    $baseline.Flush($true)
}
finally { $baseline.Dispose() }
[IO.File]::Move($baselineStagingPath, $baselinePath, $false)
Write-Host "Native runtime artifacts: $runtimeRoot"
Write-Host "Native symbols: $symbolsRoot"
