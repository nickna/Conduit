# Shared verification for the assembled native image payload. No writes at load.
. (Join-Path $PSScriptRoot "native-publish-evidence.ps1")

function Read-NativeImageEvidenceDocument {
    param([string]$Path)
    Assert-NativeEvidencePathAncestry $Path
    Assert-NativeEvidenceRegularFile $Path
    $file = Get-Item -LiteralPath $Path
    if ($file.Length -le 0 -or $file.Length -gt 16777216) {
        throw "Native image publish evidence must be a bounded nonempty document."
    }
    $document = $null
    try {
        $utf8 = [Text.UTF8Encoding]::new($false, $true)
        $options = [Text.Json.JsonDocumentOptions]::new()
        $options.MaxDepth = 32
        $document = [Text.Json.JsonDocument]::Parse($utf8.GetString([IO.File]::ReadAllBytes($file.FullName)), $options)
        return Get-NativeEvidenceObject (Read-NativeEvidenceJsonValue $document.RootElement)
    }
    finally { if ($null -ne $document) { $document.Dispose() } }
}

function Get-NativeImageExecutableNames {
    param([string]$Service, [string]$Rid)
    if ($Service -cnotin @("admin", "gateway") -or $Rid -cnotin @("linux-x64", "win-x64")) {
        throw "Native image publish service or RID is invalid."
    }
    $extension = if ($Rid -ceq "win-x64") { ".exe" } else { "" }
    return [ordered]@{
        application = $(if ($Service -ceq "admin") { "ConduitLLM.Admin" } else { "ConduitLLM.Gateway" }) + $extension
        healthProbe = "conduit-health-probe" + $extension
        healthComponent = "ConduitLLM.HealthProbe" + $extension
    }
}

function Assert-NativeImagePublishEvidenceMatchesPayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
        [Parameter(Mandatory = $true)][string]$PayloadDirectory
    )
    $ErrorActionPreference = "Stop"
    $evidence = Get-NativeEvidenceFullPath $EvidenceDirectory
    $payload = Get-NativeEvidenceFullPath $PayloadDirectory
    $files = Get-NativeEvidenceFiles $evidence
    if ($files.Count -ne 3 -or !$files.ContainsKey("image-publish.json") -or
        !$files.ContainsKey("application.json") -or !$files.ContainsKey("health-probe.json")) {
        throw "Native image publish evidence requires exactly its three immutable documents."
    }
    $summary = Read-NativeImageEvidenceDocument (Join-Path $evidence "image-publish.json")
    Assert-NativeEvidenceKeys $summary @("schemaVersion", "kind", "buildId", "service", "rid",
        "commitMetadata", "application", "healthProbe", "metrics") -Exact
    Assert-NativeEvidenceInteger $summary["schemaVersion"] -Positive
    if ($summary["schemaVersion"] -ne 1 -or $summary["kind"] -isnot [string] -or
        $summary["kind"] -cne "conduit-native-image-publish" -or $summary["buildId"] -isnot [string] -or
        $summary["service"] -isnot [string] -or $summary["rid"] -isnot [string]) {
        throw "Native image publish summary identity is invalid."
    }
    Assert-NativeEvidenceEnvelopeIdentity $summary["buildId"] $summary["service"] $summary["rid"]
    $names = Get-NativeImageExecutableNames $summary["service"] $summary["rid"]
    $metadata = Get-NativeEvidenceObject $summary["commitMetadata"]
    Assert-NativeEvidenceKeys $metadata @("version", "commitSha", "buildTimestamp") -Exact
    foreach ($name in @("version", "commitSha", "buildTimestamp")) { Assert-NativeEvidenceText $metadata[$name] }
    $metrics = Get-NativeEvidenceObject $summary["metrics"]
    Assert-NativeEvidenceKeys $metrics @("publishDurationMilliseconds", "applicationPublishDurationMilliseconds",
        "healthProbePublishDurationMilliseconds", "executableBytes", "runtimeArtifactBytes") -Exact
    foreach ($name in $metrics.Keys) { Assert-NativeEvidenceInteger $metrics[$name] -Positive }
    if ([decimal]$metrics["applicationPublishDurationMilliseconds"] + [decimal]$metrics["healthProbePublishDurationMilliseconds"] -ne
        [decimal]$metrics["publishDurationMilliseconds"]) {
        throw "Native image publish durations do not identify both native compilations."
    }
    $manifests = @{}
    foreach ($role in @("application", "healthProbe")) {
        $reference = Get-NativeEvidenceObject $summary[$role]
        Assert-NativeEvidenceKeys $reference @("path", "sha256") -Exact
        $expectedFile = if ($role -ceq "application") { "application.json" } else { "health-probe.json" }
        if ($reference["path"] -isnot [string] -or $reference["path"] -cne $expectedFile) {
            throw "Native image publish reference has an unexpected role or path."
        }
        Assert-NativeEvidenceHash $reference["sha256"]
        $manifestPath = Join-Path $evidence $expectedFile
        $manifestHash = Get-NativeEvidenceFileHash (Get-Item -LiteralPath $manifestPath)
        if ($manifestHash.sha256 -cne $reference["sha256"]) {
            throw "Native image publish reference does not match its immutable document."
        }
        Assert-NativePublishManifestMatchesRuntime -ManifestPath $manifestPath -RuntimeDirectory $payload
        $manifest = Read-NativeImageEvidenceDocument $manifestPath
        $expectedService = if ($role -ceq "application") { $summary["service"] } else { "probe" }
        if ($manifest["buildId"] -cne $summary["buildId"] -or $manifest["service"] -cne $expectedService -or
            $manifest["rid"] -cne $summary["rid"] -or $manifest["executable"]["path"] -cne $names[$role] -or
            $manifest["publishProperties"]["conduitAotAudit"] -ne ($role -ceq "application")) {
            throw "Native image publish component has an unrelated build, service, RID or executable."
        }
        $afterHash = Get-NativeEvidenceFileHash (Get-Item -LiteralPath $manifestPath)
        if ($afterHash.sha256 -cne $reference["sha256"]) { throw "Native image publish document changed during verification." }
        $manifests[$role] = $manifest
    }
    $application = $manifests.application
    $healthProbe = $manifests.healthProbe
    foreach ($name in @("status", "revision", "dirty")) {
        if ($application["source"][$name] -cne $healthProbe["source"][$name]) {
            throw "Native image publish components have different source identity."
        }
    }
    if ($application["source"]["status"] -ceq "git" -and
        $application["source"]["revision"] -cne $metadata["commitSha"]) {
        throw "Native image publish assembly metadata does not match its actual Git source."
    }
    if ($application["runtimeFilesSha256"] -cne $healthProbe["runtimeFilesSha256"] -or
        $application["runtimeArtifactBytes"] -ne $healthProbe["runtimeArtifactBytes"] -or
        $application["executable"]["bytes"] -ne $metrics["executableBytes"] -or
        $application["runtimeArtifactBytes"] -ne $metrics["runtimeArtifactBytes"]) {
        throw "Native image publish metrics do not describe the assembled payload."
    }
    # These checks do not access the foreign producer's compiler paths. A source
    # marked unavailable is useful historical payload evidence, never qualification.
    return $summary
}
