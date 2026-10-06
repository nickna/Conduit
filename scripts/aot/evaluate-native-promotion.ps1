[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('admin', 'gateway')]
    [string] $Service,

    [Parameter(Mandatory)]
    [string] $BenchmarkPath,

    [Parameter(Mandatory)]
    [string] $CanaryEvidencePath,

    [string] $PolicyPath = 'deploy/native-canary/promotion-policy.json',

    [string] $AdminCanaryEvidencePath,

    [switch] $ReportOnly
)

$ErrorActionPreference = 'Stop'
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
$benchmark = Get-Content -LiteralPath $BenchmarkPath -Raw | ConvertFrom-Json
$evidence = Get-Content -LiteralPath $CanaryEvidencePath -Raw | ConvertFrom-Json
$failures = [System.Collections.Generic.List[string]]::new()

function Test-FiniteNumber($Value) {
    if ($null -eq $Value) { return $false }
    try {
        $number = [double]$Value
        return -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number)
    }
    catch { return $false }
}

function Get-RequiredNumber($Object, [string] $PropertyName, [string] $Description) {
    $property = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $property -or -not (Test-FiniteNumber $property.Value)) {
        $failures.Add("$Description is missing or not finite")
        return $null
    }
    return [double]$property.Value
}

function Test-RequiredBoolean($Object, [string] $PropertyName, [string] $Description) {
    $property = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $property -or $property.Value -ne $true) {
        $failures.Add("$Description was not verified")
    }
}

function Test-AbsoluteUrl([string] $Value) {
    $uri = $null
    return -not [string]::IsNullOrWhiteSpace($Value) -and
        $Value -ne 'replace-me' -and
        [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -and
        $uri.Scheme -in @('https', 'http')
}

function Test-CanaryEvidence($Candidate, [string] $ExpectedService, [string] $Description) {
    if ($Candidate.schemaVersion -ne 1) { $failures.Add("$Description schema version is not supported") }
    if ($Candidate.service -ne $ExpectedService) { $failures.Add("$Description service is not '$ExpectedService'") }

    foreach ($digestProperty in 'candidateDigest', 'correspondingJitDigest') {
        $digest = [string]$Candidate.$digestProperty
        if ($digest -notmatch '^sha256:[0-9a-fA-F]{64}$') {
            $failures.Add("$Description $digestProperty is not an immutable sha256 digest")
        }
    }
    if ($Candidate.candidateDigest -eq $Candidate.correspondingJitDigest) {
        $failures.Add("$Description native and JIT digests are identical")
    }

    $startedAt = [DateTimeOffset]::MinValue
    $completedAt = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse([string]$Candidate.startedAt, [ref]$startedAt)) {
        $failures.Add("$Description start time is invalid")
    }
    if (-not [DateTimeOffset]::TryParse([string]$Candidate.completedAt, [ref]$completedAt)) {
        $failures.Add("$Description completion time is invalid")
    }
    if ($startedAt -ne [DateTimeOffset]::MinValue -and
        $completedAt -ne [DateTimeOffset]::MinValue -and
        $completedAt -le $startedAt) {
        $failures.Add("$Description completion time is not after its start time")
    }

    $soakHours = Get-RequiredNumber $Candidate 'soakHours' "$Description soak duration"
    $minimumSoakHours = [double]$policy.minimumSoakHours.PSObject.Properties[$ExpectedService].Value
    if ($null -ne $soakHours -and $soakHours -lt $minimumSoakHours) {
        $failures.Add("$Description minimum soak duration was not met")
    }
    if ($null -ne $soakHours -and
        $startedAt -ne [DateTimeOffset]::MinValue -and
        $completedAt -ne [DateTimeOffset]::MinValue -and
        $soakHours -gt (($completedAt - $startedAt).TotalHours + 0.01)) {
        $failures.Add("$Description claimed soak duration exceeds its recorded time range")
    }

    $healthyWindows = Get-RequiredNumber $Candidate 'healthyWindows' "$Description healthy-window count"
    if ($null -ne $healthyWindows -and $healthyWindows -lt $policy.requiredConsecutiveHealthyWindows) {
        $failures.Add("$Description required consecutive healthy windows were not met")
    }
    $errorRate = Get-RequiredNumber $Candidate 'errorRate' "$Description error rate"
    if ($null -ne $errorRate -and $errorRate -gt $policy.thresholds.maximumErrorRate) {
        $failures.Add("$Description error rate exceeds the threshold")
    }

    foreach ($requirement in @(
        @('contractsMatchedJit', 'JIT contract parity'),
        @('backingServicesMatchedJit', 'JIT backing-service parity'),
        @('fullFeatureMatrixPassed', 'full supported feature matrix'),
        @('dashboardAndAlertsVerified', 'dashboard and alert coverage'),
        @('automaticRollbackTested', 'automatic rollback'),
        @('manualRollbackTested', 'manual rollback'),
        @('criticalVulnerabilitiesResolved', 'critical-vulnerability review'),
        @('sbomAndProvenanceVerified', 'SBOM and provenance attestations')
    )) {
        Test-RequiredBoolean $Candidate $requirement[0] "$Description $($requirement[1])"
    }

    if (-not (Test-AbsoluteUrl ([string]$Candidate.dashboardUrl))) {
        $failures.Add("$Description dashboard URL is missing or invalid")
    }
    if (-not (Test-AbsoluteUrl ([string]$Candidate.alertsUrl))) {
        $failures.Add("$Description alerts URL is missing or invalid")
    }

    return [pscustomobject]@{ startedAt = $startedAt; completedAt = $completedAt }
}

if ($policy.status -ne 'open') {
    foreach ($blocker in $policy.blockedBy) { $failures.Add("promotion blocked: $blocker") }
}
if ($policy.schemaVersion -ne 1) { $failures.Add('promotion policy schema version is not supported') }
if ($benchmark.schemaVersion -ne 1) { $failures.Add('benchmark schema version is not supported') }
if ($benchmark.service -ne $Service) { $failures.Add('benchmark service does not match the requested promotion') }
if ($benchmark.platform -ne 'linux-x64') { $failures.Add('benchmark platform is not linux-x64') }

$canaryTimes = Test-CanaryEvidence $evidence $Service 'canary evidence'
if ($Service -eq 'gateway') {
    if ([string]::IsNullOrWhiteSpace($AdminCanaryEvidencePath)) {
        $failures.Add('Gateway promotion requires accepted Admin canary evidence')
    }
    else {
        $adminEvidence = Get-Content -LiteralPath $AdminCanaryEvidencePath -Raw | ConvertFrom-Json
        $adminTimes = Test-CanaryEvidence $adminEvidence 'admin' 'Admin canary evidence'
        if ($adminTimes.completedAt -ne [DateTimeOffset]::MinValue -and
            $canaryTimes.startedAt -ne [DateTimeOffset]::MinValue -and
            $adminTimes.completedAt -gt $canaryTimes.startedAt) {
            $failures.Add('Gateway canary started before the Admin canary completed')
        }
    }
}

foreach ($variantName in 'jit', 'native') {
    $variant = $benchmark.$variantName
    $expectedRuntime = if ($variantName -eq 'jit') { 'jit' } else { 'native-aot' }
    if ($null -eq $variant) {
        $failures.Add("$variantName benchmark result is missing")
        continue
    }
    if ($variant.runtime -ne $expectedRuntime) { $failures.Add("$variantName benchmark runtime is invalid") }
    $requests = Get-RequiredNumber $variant 'requests' "$variantName benchmark request count"
    $benchmarkFailures = Get-RequiredNumber $variant 'failures' "$variantName benchmark failure count"
    if ($null -ne $requests -and $requests -le 0) { $failures.Add("$variantName benchmark made no requests") }
    if ($null -ne $benchmarkFailures -and $benchmarkFailures -ne 0) { $failures.Add("$variantName benchmark contains failed requests") }

    $image = [string]$variant.image
    if ($image -notmatch '@(sha256:[0-9a-fA-F]{64})$') {
        $failures.Add("$variantName benchmark image is not pinned by digest")
    }
    else {
        $recordedDigest = if ($variantName -eq 'jit') { $evidence.correspondingJitDigest } else { $evidence.candidateDigest }
        if ($Matches[1] -ne $recordedDigest) {
            $failures.Add("$variantName benchmark image digest does not match canary evidence")
        }
    }
}

$comparison = $benchmark.comparison
$imageReduction = Get-RequiredNumber $comparison 'imageSizeReductionPercent' 'image-size comparison'
$coldStartReduction = Get-RequiredNumber $comparison 'coldStartReductionPercent' 'cold-start comparison'
$idleMemoryReduction = Get-RequiredNumber $comparison 'idleMemoryReductionPercent' 'idle-memory comparison'
$p99Regression = Get-RequiredNumber $comparison 'p99LatencyRegressionPercent' 'p99 latency comparison'
$throughputImprovement = Get-RequiredNumber $comparison 'throughputImprovementPercent' 'throughput comparison'
if ($null -ne $imageReduction -and $imageReduction -lt $policy.thresholds.minimumImageSizeReductionPercent) { $failures.Add('image-size improvement is below threshold') }
if ($null -ne $coldStartReduction -and $coldStartReduction -lt $policy.thresholds.minimumColdStartReductionPercent) { $failures.Add('cold-start improvement is below threshold') }
if ($null -ne $idleMemoryReduction -and $idleMemoryReduction -lt $policy.thresholds.minimumIdleMemoryReductionPercent) { $failures.Add('idle-memory improvement is below threshold') }
if ($null -ne $p99Regression -and $p99Regression -gt $policy.thresholds.maximumP99LatencyRegressionPercent) { $failures.Add('p99 latency regression exceeds threshold') }
if ($null -ne $throughputImprovement -and $throughputImprovement -lt $policy.thresholds.minimumThroughputImprovementPercent) { $failures.Add('throughput improvement is below threshold') }

$decision = if ($failures.Count -eq 0) { 'promote' } else { 'hold' }
[pscustomobject]@{
    service = $Service
    decision = $decision
    evaluatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    failures = @($failures)
} | ConvertTo-Json -Depth 5

if ($decision -ne 'promote' -and -not $ReportOnly) { exit 1 }
