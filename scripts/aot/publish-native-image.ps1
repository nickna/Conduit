[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet("admin", "gateway")][string]$Service,
    [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
    [ValidateSet("linux-x64", "win-x64")][string]$Runtime = "linux-x64",
    [switch]$NoRestore,
    [string]$Version = "3.0.0",
    [string]$CommitSha = "dev",
    [string]$BuildTimestamp = "unknown",
    [string]$WarningBaselinePath,
    [ValidateSet("local", "require-clean-git")][string]$SourcePolicy = "local"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "native-publish-build.ps1")
. (Join-Path $PSScriptRoot "native-image-publish-evidence.ps1")
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
foreach ($value in @($Version, $CommitSha, $BuildTimestamp)) { Assert-NativeEvidenceText $value }
$source = Get-NativePublishSource -RepositoryRoot $repositoryRoot
if ($SourcePolicy -eq "require-clean-git") {
    if ($CommitSha -cnotmatch '\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z' -or
        $source.status -cne "git" -or $source.dirty -isnot [bool] -or $source.dirty -or
        $source.revision -cne $CommitSha) {
        throw "Native image publish requires clean Git source matching the explicit full commit metadata."
    }
}
if ($source.status -ceq "git") {
    if ($CommitSha -ceq "dev") { $CommitSha = $source.revision }
    if ($CommitSha -cne $source.revision) { throw "Native image publish commit metadata differs from actual Git source." }
}
if ([string]::IsNullOrWhiteSpace($WarningBaselinePath)) { $WarningBaselinePath = Join-Path $PSScriptRoot "linker-warning-baseline.json" }
Assert-NativeEvidenceRegularFile ([IO.Path]::GetFullPath($WarningBaselinePath))
$artifactRoot = Get-NativeEvidenceFullPath $ArtifactDirectory
Assert-NativeEvidencePathAncestry $artifactRoot
if (Test-Path -LiteralPath $artifactRoot) {
    $existing = Get-Item -LiteralPath $artifactRoot
    if (!$existing.PSIsContainer -or @(Get-ChildItem -LiteralPath $artifactRoot -Force).Count -ne 0) {
        throw "Native image publish requires a fresh empty ArtifactDirectory."
    }
}
$buildId = [Guid]::NewGuid().ToString("N")
$names = Get-NativeImageExecutableNames $Service $Runtime
$payload = Join-Path $artifactRoot "payload"
$evidence = Join-Path $artifactRoot "evidence"
$reports = Join-Path $artifactRoot "reports"
$claimPath = Join-Path $artifactRoot "native-image-publish.claim"
foreach ($path in @($payload, $evidence, $reports, $claimPath)) { Assert-NativeEvidencePathAncestry $path }
[IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
$claim = [IO.FileStream]::new($claimPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($buildId + [Environment]::NewLine)
    $claim.Write($bytes)
    $claim.Flush($true)
}
finally { $claim.Dispose() }
foreach ($path in @($payload, $evidence, $reports)) { [IO.Directory]::CreateDirectory($path) | Out-Null }
$assembly = if ($Service -ceq "admin") { "ConduitLLM.Admin" } else { "ConduitLLM.Gateway" }
$definitions = @(
    @{ role = "application"; assembly = $assembly; project = "Services/$assembly/$assembly.csproj"; audit = $true; executable = $names.application },
    @{ role = "health-probe"; assembly = "ConduitLLM.HealthProbe"; project = "tools/ConduitLLM.HealthProbe/ConduitLLM.HealthProbe.csproj"; audit = $false; executable = $names.healthComponent }
)
$publications = @{}
foreach ($definition in $definitions) {
    $role = $definition.role
    $component = Join-Path $artifactRoot "components/$role"
    $symbols = Join-Path $artifactRoot "symbols/$role"
    $intermediate = (Join-Path $artifactRoot "native-intermediates/$buildId/$role") + [IO.Path]::DirectorySeparatorChar
    $nativeOutput = (Join-Path $artifactRoot "native-outputs/$buildId/$role") + [IO.Path]::DirectorySeparatorChar
    $capturePath = Join-Path $reports "$role-toolchain.b64"
    $logPath = Join-Path $reports "$role-publish.log"
    foreach ($path in @($component, $symbols, $intermediate, $nativeOutput, $capturePath, $logPath)) {
        Assert-NativeEvidencePathAncestry $path
    }
    [IO.Directory]::CreateDirectory($component) | Out-Null
    [IO.Directory]::CreateDirectory($symbols) | Out-Null
    $arguments = @(
        "publish", (Join-Path $repositoryRoot $definition.project), "--configuration", "Release",
        "--runtime", $Runtime, "--self-contained", "true", "--output", $component,
        "--nologo", "--tl:off", "--maxcpucount:1", "--verbosity", "minimal",
        "-p:PublishAot=true", "-p:ConduitAotAudit=$($definition.audit.ToString().ToLowerInvariant())",
        "-p:StripSymbols=true", "-p:UseSharedCompilation=false",
        "-p:Version=$(ConvertTo-MSBuildPropertyValue $Version)", "-p:InformationalVersion=$(ConvertTo-MSBuildPropertyValue $Version)",
        "-p:ConduitCommitSha=$(ConvertTo-MSBuildPropertyValue $CommitSha)",
        "-p:ConduitBuildTimestamp=$(ConvertTo-MSBuildPropertyValue $BuildTimestamp)",
        "-p:NativeIntermediateOutputPath=$(ConvertTo-MSBuildPropertyValue $intermediate)",
        "-p:NativeOutputPath=$(ConvertTo-MSBuildPropertyValue $nativeOutput)",
        "-p:CustomAfterMicrosoftCommonTargets=$(ConvertTo-MSBuildPropertyValue (Join-Path $PSScriptRoot 'native-publish-toolchain.targets'))",
        "-p:ConduitNativeToolchainEvidencePath=$(ConvertTo-MSBuildPropertyValue $capturePath)",
        "-p:ConduitNativeToolchainEvidenceAssemblyName=$($definition.assembly)"
    )
    if ($NoRestore) { $arguments += "--no-restore" }
    $startedAt = [DateTime]::UtcNow
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & dotnet @arguments 2>&1 | Tee-Object -FilePath $logPath
    $publishExit = $LASTEXITCODE
    $watch.Stop()
    if ($publishExit -ne 0) { throw "Native image $role publish failed with exit code $publishExit." }
    # Preserve relative symbol paths, including nested files. Runtime hashing
    # and exact leaf destinations reject links before any move or copy.
    $componentFiles = Get-NativeEvidenceFiles $component
    foreach ($relative in @($componentFiles.Keys)) {
        if ([IO.Path]::GetExtension($relative) -in @(".dbg", ".pdb")) {
            $destination = Join-Path $symbols $relative
            Assert-NativeEvidencePathAncestry $destination
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            Move-Item -LiteralPath $componentFiles[$relative].FullName -Destination $destination
        }
    }
    $completedAt = [DateTime]::UtcNow
    $capture = Read-NativeToolchainCapture -Path $capturePath
    Assert-NativePublishCompileOutputs -Capture $capture -IntermediateDirectory $intermediate -OutputDirectory $nativeOutput `
        -AssemblyName $definition.assembly -ExecutableName $definition.executable `
        -RuntimeExecutable (Join-Path $component $definition.executable) -Runtime $Runtime
    foreach ($entry in @{ Version = $Version; InformationalVersion = $Version; ConduitCommitSha = $CommitSha; ConduitBuildTimestamp = $BuildTimestamp }.GetEnumerator()) {
        if ($capture[$entry.Key] -cne $entry.Value) { throw "Native image compiler metadata differs from the actual invocation." }
    }
    $toolchain = Get-NativePublishToolchain -Capture $capture -ExpectedAssembly $definition.assembly -ExpectedAudit $definition.audit -Runtime $Runtime
    $publications[$role] = [ordered]@{
        directory = $component; startedAt = $startedAt; completedAt = $completedAt
        duration = [long]$watch.ElapsedMilliseconds; toolchain = $toolchain
        properties = [ordered]@{ configuration = "Release"; publishAot = $true; selfContained = $true
            stripSymbols = $true; conduitAotAudit = $definition.audit; useSharedCompilation = $false }
    }
}
$applicationFiles = Get-NativeEvidenceFiles $publications.application.directory
if ($applicationFiles.ContainsKey($names.healthProbe)) { throw "Native image application collides with the packaged health probe." }
$healthFiles = Get-NativeEvidenceFiles $publications['health-probe'].directory
if ($healthFiles.Count -ne 1 -or !$healthFiles.ContainsKey($names.healthComponent)) {
    throw "Native image health probe has unexpected runtime dependencies."
}
foreach ($relative in $applicationFiles.Keys) {
    $destination = Join-Path $payload $relative
    Assert-NativeEvidencePathAncestry $destination
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::Copy($applicationFiles[$relative].FullName, $destination, $false)
}
[IO.File]::Copy($healthFiles[$names.healthComponent].FullName, (Join-Path $payload $names.healthProbe), $false)
$payloadIdentity = Get-NativeRuntimeIdentity $payload $names.application
$healthIdentity = Get-NativeRuntimeIdentity $payload $names.healthProbe
foreach ($pair in @(@($applicationFiles[$names.application].FullName, $payloadIdentity.executable),
    @($healthFiles[$names.healthComponent].FullName, $healthIdentity.executable))) {
    $original = Get-NativeEvidenceFileHash (Get-Item -LiteralPath $pair[0])
    if ($original.sha256 -cne $pair[1].sha256 -or $original.bytes -ne $pair[1].bytes) {
        throw "Native image assembly changed a freshly linked native executable."
    }
}
$linkerReports = Join-Path $reports "linker"
Assert-NativeEvidencePathAncestry $linkerReports
[IO.Directory]::CreateDirectory($linkerReports) | Out-Null
$combinedLog = Join-Path $linkerReports "$Service-publish.log"
$logText = [IO.File]::ReadAllText((Join-Path $reports "application-publish.log")) + [Environment]::NewLine +
    [IO.File]::ReadAllText((Join-Path $reports "health-probe-publish.log"))
[IO.File]::WriteAllText($combinedLog, $logText, [Text.UTF8Encoding]::new($false))
$owner = if ($Service -ceq "admin") { "Admin" } else { "Gateway" }
& (Join-Path $PSScriptRoot "evaluate-native-linker-warnings.ps1") -ReportDirectory $linkerReports `
    -BaselinePath $WarningBaselinePath -Runtime $Runtime -Services @($owner)

if ($SourcePolicy -eq "require-clean-git") {
    $completedSource = Get-NativePublishSource -RepositoryRoot $repositoryRoot
    if ($completedSource.status -cne "git" -or $completedSource.dirty -isnot [bool] -or
        $completedSource.dirty -or $completedSource.revision -cne $CommitSha) {
        throw "Native image Git source changed before completed publish evidence was written."
    }
}
$references = @{}
foreach ($role in @("application", "healthProbe")) {
    $publication = if ($role -ceq "application") { $publications.application } else { $publications['health-probe'] }
    $manifestName = if ($role -ceq "application") { "application.json" } else { "health-probe.json" }
    $manifestService = if ($role -ceq "application") { $Service } else { "probe" }
    $manifest = Write-NativePublishManifest -DestinationPath (Join-Path $evidence $manifestName) `
        -BuildId $buildId -Service $manifestService -Rid $Runtime -Source $source -Toolchain $publication.toolchain `
        -PublishProperties $publication.properties -StartedAtUtc $publication.startedAt -CompletedAtUtc $publication.completedAt `
        -RuntimeDirectory $payload -ExecutableName $names[$role]
    $references[$role] = [ordered]@{ path = $manifestName; sha256 = $manifest.sha256 }
}
$summary = [ordered]@{
    schemaVersion = 1; kind = "conduit-native-image-publish"; buildId = $buildId; service = $Service; rid = $Runtime
    commitMetadata = [ordered]@{ version = $Version; commitSha = $CommitSha; buildTimestamp = $BuildTimestamp }
    application = $references.application; healthProbe = $references.healthProbe
    metrics = [ordered]@{
        publishDurationMilliseconds = [long]([decimal]$publications.application.duration + [decimal]$publications['health-probe'].duration)
        applicationPublishDurationMilliseconds = $publications.application.duration
        healthProbePublishDurationMilliseconds = $publications['health-probe'].duration
        executableBytes = $payloadIdentity.executable.bytes; runtimeArtifactBytes = $payloadIdentity.runtimeArtifactBytes
    }
}
$memory = [IO.MemoryStream]::new()
$writer = [Text.Json.Utf8JsonWriter]::new($memory)
try { Write-NativeEvidenceJsonValue $writer $summary; $writer.Flush(); $summaryBytes = $memory.ToArray() }
finally { $writer.Dispose(); $memory.Dispose() }
$stagingPath = Join-Path $artifactRoot ".image-publish.$buildId.tmp"
$stream = [IO.FileStream]::new($stagingPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($summaryBytes); $stream.Flush($true) }
finally { $stream.Dispose() }
[IO.File]::Move($stagingPath, (Join-Path $evidence "image-publish.json"), $false)
Assert-NativeImagePublishEvidenceMatchesPayload -EvidenceDirectory $evidence -PayloadDirectory $payload | Out-Null
Write-Host "Native image publish payload: $payload"
Write-Host "Immutable native image publish evidence: $evidence"
