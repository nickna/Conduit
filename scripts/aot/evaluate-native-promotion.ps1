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
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [bool]) { return $false }
    try {
        $number = [double]$Value
        return -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number)
    }
    catch { return $false }
}

function Get-RequiredNumber($Object, [string] $PropertyName, [string] $Description) {
    if ($null -eq $Object) {
        $failures.Add("$Description is missing or not finite")
        return $null
    }
    $property = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $property -or -not (Test-FiniteNumber $property.Value)) {
        $failures.Add("$Description is missing or not finite")
        return $null
    }
    return [double]$property.Value
}

function Get-RequiredPositiveNumber($Object, [string] $PropertyName, [string] $Description) {
    $value = Get-RequiredNumber $Object $PropertyName $Description
    if ($null -ne $value -and $value -le 0) {
        $failures.Add("$Description must be greater than zero")
    }
    return $value
}

function Get-RequiredNonNegativeNumber($Object, [string] $PropertyName, [string] $Description) {
    $value = Get-RequiredNumber $Object $PropertyName $Description
    if ($null -ne $value -and $value -lt 0) {
        $failures.Add("$Description must not be negative")
    }
    return $value
}

function Test-RequiredBoolean($Object, [string] $PropertyName, [string] $Description) {
    if ($null -eq $Object) {
        $failures.Add("$Description was not verified")
        return
    }
    $property = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $property -or $property.Value -isnot [bool] -or $property.Value -ne $true) {
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
    if ($completedAt -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
        $failures.Add("$Description completion time is in the future")
    }

    $soakHours = Get-RequiredNonNegativeNumber $Candidate 'soakHours' "$Description soak duration"
    $minimumSoakHours = Get-RequiredPositiveNumber $policy.minimumSoakHours $ExpectedService "$ExpectedService minimum soak duration"
    if ($null -ne $soakHours -and $null -ne $minimumSoakHours -and $soakHours -lt $minimumSoakHours) {
        $failures.Add("$Description minimum soak duration was not met")
    }
    if ($null -ne $soakHours -and
        $startedAt -ne [DateTimeOffset]::MinValue -and
        $completedAt -ne [DateTimeOffset]::MinValue -and
        $soakHours -gt (($completedAt - $startedAt).TotalHours + 0.01)) {
        $failures.Add("$Description claimed soak duration exceeds its recorded time range")
    }

    $healthyWindows = Get-RequiredNonNegativeNumber $Candidate 'healthyWindows' "$Description healthy-window count"
    $requiredHealthyWindows = Get-RequiredPositiveNumber $policy 'requiredConsecutiveHealthyWindows' 'required consecutive healthy-window count'
    if ($null -ne $healthyWindows -and $null -ne $requiredHealthyWindows -and $healthyWindows -lt $requiredHealthyWindows) {
        $failures.Add("$Description required consecutive healthy windows were not met")
    }
    $errorRate = Get-RequiredNonNegativeNumber $Candidate 'errorRate' "$Description error rate"
    $maximumErrorRate = Get-RequiredNonNegativeNumber $policy.thresholds 'maximumErrorRate' 'maximum error-rate threshold'
    if ($null -ne $errorRate -and $null -ne $maximumErrorRate -and $errorRate -gt $maximumErrorRate) {
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

if ($policy.status -notin @('open', 'blocked')) {
    $failures.Add('promotion policy status must be open or blocked')
}
if ($policy.status -ne 'open') {
    foreach ($blocker in $policy.blockedBy) { $failures.Add("promotion blocked: $blocker") }
    if (@($policy.blockedBy).Count -eq 0) { $failures.Add('promotion is blocked without a recorded reason') }
}
elseif (@($policy.blockedBy).Count -ne 0) {
    $failures.Add('promotion policy is open but still records blockers')
}
if ($policy.schemaVersion -ne 1) { $failures.Add('promotion policy schema version is not supported') }
if (@($policy.sequence).Count -ne 2 -or $policy.sequence[0] -ne 'admin' -or $policy.sequence[1] -ne 'gateway') {
    $failures.Add('promotion policy sequence must be Admin followed by Gateway')
}
if ($benchmark.schemaVersion -ne 1) { $failures.Add('benchmark schema version is not supported') }
if ($benchmark.service -ne $Service) { $failures.Add('benchmark service does not match the requested promotion') }
if ($benchmark.platform -ne 'linux-x64') { $failures.Add('benchmark platform is not linux-x64') }
if ($benchmark.endpoint -ne '/health/ready') { $failures.Add('benchmark endpoint is not /health/ready') }
$benchmarkGeneratedAt = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse([string]$benchmark.generatedAt, [ref]$benchmarkGeneratedAt)) {
    $failures.Add('benchmark generation time is invalid')
}
elseif ($benchmarkGeneratedAt -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
    $failures.Add('benchmark generation time is in the future')
}

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

$variantMetrics = @{}
foreach ($variantName in 'jit', 'native') {
    $variant = $benchmark.$variantName
    $expectedRuntime = if ($variantName -eq 'jit') { 'jit' } else { 'native-aot' }
    if ($null -eq $variant) {
        $failures.Add("$variantName benchmark result is missing")
        continue
    }
    if ($variant.runtime -ne $expectedRuntime) { $failures.Add("$variantName benchmark runtime is invalid") }
    $requests = Get-RequiredPositiveNumber $variant 'requests' "$variantName benchmark request count"
    $concurrency = Get-RequiredPositiveNumber $variant 'concurrency' "$variantName benchmark concurrency"
    $benchmarkFailures = Get-RequiredNonNegativeNumber $variant 'failures' "$variantName benchmark failure count"
    if ($null -ne $benchmarkFailures -and $benchmarkFailures -ne 0) { $failures.Add("$variantName benchmark contains failed requests") }

    $variantMetrics[$variantName] = [pscustomobject]@{
        imageBytes = Get-RequiredPositiveNumber $variant 'imageBytes' "$variantName image size"
        pullBytes = Get-RequiredPositiveNumber $variant 'pullBytes' "$variantName compressed pull size"
        coldReadinessMilliseconds = Get-RequiredPositiveNumber $variant 'coldReadinessMilliseconds' "$variantName cold-readiness duration"
        idleMemoryBytes = Get-RequiredPositiveNumber $variant 'idleMemoryBytes' "$variantName idle memory"
        steadyStateMemoryBytes = Get-RequiredPositiveNumber $variant 'steadyStateMemoryBytes' "$variantName steady-state memory"
        peakMemoryBytes = Get-RequiredPositiveNumber $variant 'peakMemoryBytes' "$variantName peak memory"
        throughputPerSecond = Get-RequiredPositiveNumber $variant 'throughputPerSecond' "$variantName throughput"
        p50Milliseconds = Get-RequiredNonNegativeNumber $variant 'p50Milliseconds' "$variantName p50 latency"
        p95Milliseconds = Get-RequiredNonNegativeNumber $variant 'p95Milliseconds' "$variantName p95 latency"
        p99Milliseconds = Get-RequiredNonNegativeNumber $variant 'p99Milliseconds' "$variantName p99 latency"
    }

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

$nativePublish = $benchmark.nativePublish
if ($null -eq $nativePublish) {
    $failures.Add('native publish baseline is missing')
}
else {
    $expectedPublishService = if ($Service -eq 'admin') { 'Admin' } else { 'Gateway' }
    if ($nativePublish.service -ne $expectedPublishService) { $failures.Add('native publish baseline service is invalid') }
    if ($nativePublish.rid -ne 'linux-x64') { $failures.Add('native publish baseline RID is not linux-x64') }
    Get-RequiredPositiveNumber $nativePublish 'publishDurationMilliseconds' 'native publish duration' | Out-Null
    Get-RequiredPositiveNumber $nativePublish 'executableBytes' 'native executable size' | Out-Null
    Get-RequiredPositiveNumber $nativePublish 'runtimeArtifactBytes' 'native runtime artifact size' | Out-Null
}

$comparison = $benchmark.comparison
$imageReduction = Get-RequiredNumber $comparison 'imageSizeReductionPercent' 'image-size comparison'
$pullReduction = Get-RequiredNumber $comparison 'pullSizeReductionPercent' 'pull-size comparison'
$coldStartReduction = Get-RequiredNumber $comparison 'coldStartReductionPercent' 'cold-start comparison'
$idleMemoryReduction = Get-RequiredNumber $comparison 'idleMemoryReductionPercent' 'idle-memory comparison'
$steadyStateMemoryReduction = Get-RequiredNumber $comparison 'steadyStateMemoryReductionPercent' 'steady-state-memory comparison'
$peakMemoryReduction = Get-RequiredNumber $comparison 'peakMemoryReductionPercent' 'peak-memory comparison'
$p50Regression = Get-RequiredNumber $comparison 'p50LatencyRegressionPercent' 'p50 latency comparison'
$p95Regression = Get-RequiredNumber $comparison 'p95LatencyRegressionPercent' 'p95 latency comparison'
$p99Regression = Get-RequiredNumber $comparison 'p99LatencyRegressionPercent' 'p99 latency comparison'
$throughputImprovement = Get-RequiredNumber $comparison 'throughputImprovementPercent' 'throughput comparison'
$minimumImageReduction = Get-RequiredNumber $policy.thresholds 'minimumImageSizeReductionPercent' 'minimum image-size reduction threshold'
$minimumColdStartReduction = Get-RequiredNumber $policy.thresholds 'minimumColdStartReductionPercent' 'minimum cold-start reduction threshold'
$minimumIdleMemoryReduction = Get-RequiredNumber $policy.thresholds 'minimumIdleMemoryReductionPercent' 'minimum idle-memory reduction threshold'
$maximumP99Regression = Get-RequiredNumber $policy.thresholds 'maximumP99LatencyRegressionPercent' 'maximum p99 latency regression threshold'
$minimumThroughputImprovement = Get-RequiredNumber $policy.thresholds 'minimumThroughputImprovementPercent' 'minimum throughput improvement threshold'
if ($null -ne $imageReduction -and $null -ne $minimumImageReduction -and $imageReduction -lt $minimumImageReduction) { $failures.Add('image-size improvement is below threshold') }
if ($null -ne $coldStartReduction -and $null -ne $minimumColdStartReduction -and $coldStartReduction -lt $minimumColdStartReduction) { $failures.Add('cold-start improvement is below threshold') }
if ($null -ne $idleMemoryReduction -and $null -ne $minimumIdleMemoryReduction -and $idleMemoryReduction -lt $minimumIdleMemoryReduction) { $failures.Add('idle-memory improvement is below threshold') }
if ($null -ne $p99Regression -and $null -ne $maximumP99Regression -and $p99Regression -gt $maximumP99Regression) { $failures.Add('p99 latency regression exceeds threshold') }
if ($null -ne $throughputImprovement -and $null -ne $minimumThroughputImprovement -and $throughputImprovement -lt $minimumThroughputImprovement) { $failures.Add('throughput improvement is below threshold') }

function Test-CalculatedComparison(
    [string] $PropertyName,
    [Nullable[double]] $Recorded,
    [Nullable[double]] $JitValue,
    [Nullable[double]] $NativeValue,
    [bool] $IsIncrease
) {
    if ($null -eq $Recorded -or $null -eq $JitValue -or $null -eq $NativeValue -or $JitValue -eq 0) { return }
    $expected = if ($IsIncrease) {
        (($NativeValue - $JitValue) / $JitValue) * 100
    }
    else {
        (($JitValue - $NativeValue) / $JitValue) * 100
    }
    if ([Math]::Abs($Recorded - $expected) -gt 0.01) {
        $failures.Add("$PropertyName does not match the raw benchmark measurements")
    }
}

$jitMetrics = $variantMetrics.jit
$nativeMetrics = $variantMetrics.native
Test-CalculatedComparison 'image-size comparison' $imageReduction $jitMetrics.imageBytes $nativeMetrics.imageBytes $false
Test-CalculatedComparison 'pull-size comparison' $pullReduction $jitMetrics.pullBytes $nativeMetrics.pullBytes $false
Test-CalculatedComparison 'cold-start comparison' $coldStartReduction $jitMetrics.coldReadinessMilliseconds $nativeMetrics.coldReadinessMilliseconds $false
Test-CalculatedComparison 'idle-memory comparison' $idleMemoryReduction $jitMetrics.idleMemoryBytes $nativeMetrics.idleMemoryBytes $false
Test-CalculatedComparison 'steady-state-memory comparison' $steadyStateMemoryReduction $jitMetrics.steadyStateMemoryBytes $nativeMetrics.steadyStateMemoryBytes $false
Test-CalculatedComparison 'peak-memory comparison' $peakMemoryReduction $jitMetrics.peakMemoryBytes $nativeMetrics.peakMemoryBytes $false
Test-CalculatedComparison 'throughput comparison' $throughputImprovement $jitMetrics.throughputPerSecond $nativeMetrics.throughputPerSecond $true
Test-CalculatedComparison 'p50 latency comparison' $p50Regression $jitMetrics.p50Milliseconds $nativeMetrics.p50Milliseconds $true
Test-CalculatedComparison 'p95 latency comparison' $p95Regression $jitMetrics.p95Milliseconds $nativeMetrics.p95Milliseconds $true
Test-CalculatedComparison 'p99 latency comparison' $p99Regression $jitMetrics.p99Milliseconds $nativeMetrics.p99Milliseconds $true

$decision = if ($failures.Count -eq 0) { 'promote' } else { 'hold' }
[pscustomobject]@{
    service = $Service
    decision = $decision
    evaluatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    failures = @($failures)
} | ConvertTo-Json -Depth 5

if ($decision -ne 'promote' -and -not $ReportOnly) { exit 1 }
