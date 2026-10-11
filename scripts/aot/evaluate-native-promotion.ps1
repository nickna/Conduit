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

    [string] $NativePackagingDirectory,

    [switch] $ReportOnly
)

$Service = $Service.ToLowerInvariant()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-benchmark-image-evidence.ps1')
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
$evidence = Get-Content -LiteralPath $CanaryEvidencePath -Raw | ConvertFrom-Json
$failures = [System.Collections.Generic.List[string]]::new()
try {
    # Preserve integer and finite fractional metrics, while rejecting duplicate
    # JSON keys before PowerShell object conversion can discard them.
    $benchmarkDocument = Read-NativeImageOciEvidenceDocument -Path $BenchmarkPath
    $benchmarkValue = Get-NativeEvidenceObject $benchmarkDocument['value']
    $benchmark = $benchmarkValue | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json -DateKind String
}
catch {
    $benchmark = [pscustomobject]@{}
    $failures.Add('benchmark document is malformed or exceeds its bounded JSON contract')
}

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

function Get-RequiredPositiveInteger($Object, [string] $PropertyName, [string] $Description) {
    $value = Get-RequiredPositiveNumber $Object $PropertyName $Description
    if ($null -ne $value -and $value -ne [Math]::Floor($value)) {
        $failures.Add("$Description must be a whole number")
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
    $schemaVersion = Get-RequiredPositiveInteger $Candidate 'schemaVersion' "$Description schema version"
    if ($schemaVersion -ne 1) { $failures.Add("$Description schema version is not supported") }
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
$policySchema = Get-RequiredPositiveInteger $policy 'schemaVersion' 'promotion policy schema version'
if ($policySchema -ne 1) { $failures.Add('promotion policy schema version is not supported') }
if (@($policy.sequence).Count -ne 2 -or $policy.sequence[0] -ne 'admin' -or $policy.sequence[1] -ne 'gateway') {
    $failures.Add('promotion policy sequence must be Admin followed by Gateway')
}
$benchmarkSchema = Get-RequiredPositiveInteger $benchmark 'schemaVersion' 'benchmark schema version'
if ($benchmarkSchema -notin @(2, 3)) { $failures.Add('benchmark schema version 2 with authenticated workload samples is required') }
if ($benchmarkSchema -ne 3) { $failures.Add('benchmark schema version 3 with retained native image binding is required') }
elseif ($benchmark.schemaVersion.GetType() -notin @([sbyte], [byte], [int16], [uint16], [int32], [uint32], [int64], [uint64])) {
    $failures.Add('benchmark schema version 3 must be an integer')
}
if ($benchmark.service -ne $Service) { $failures.Add('benchmark service does not match the requested promotion') }
if ($benchmark.platform -ne 'linux-x64') { $failures.Add('benchmark platform is not linux-x64') }
$generatedAt = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse([string]$benchmark.generatedAt, [ref]$generatedAt)) { $failures.Add('benchmark generation time is invalid') }
elseif ($generatedAt -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) { $failures.Add('benchmark generation time is in the future') }

$nativeImageEvidence = $null
$boundNativeImage = $null
if ($benchmarkSchema -eq 3) {
    if ([string]::IsNullOrWhiteSpace($NativePackagingDirectory)) {
        $failures.Add('native image binding requires retained packaging evidence')
    }
    elseif ($benchmark.native.image -isnot [string]) {
        $failures.Add('native image binding requires a digest-pinned benchmark image')
    }
    else {
        try {
            $boundNativeImage = $benchmark.native.image
            $nativeImageEvidence = Read-NativeBenchmarkImageEvidence -Directory $NativePackagingDirectory -Service $Service -NativeImage $boundNativeImage
            Assert-NativeBenchmarkImageEvidenceEqual -Expected $nativeImageEvidence['identity'] -Actual $benchmark.nativeEvidence
            if ($evidence.candidateDigest -isnot [string] -or $evidence.candidateDigest -cne $nativeImageEvidence['identity']['requestedDigest']) {
                $failures.Add('native image binding digest does not match canary evidence')
            }
        }
        catch { $failures.Add('native image binding could not be verified from retained packaging evidence') }
    }
}

$canaryTimes = Test-CanaryEvidence $evidence $Service 'canary evidence'
if ($Service -eq 'gateway') {
    if ([string]::IsNullOrWhiteSpace($AdminCanaryEvidencePath)) { $failures.Add('Gateway promotion requires accepted Admin canary evidence') }
    else {
        $adminEvidence = Get-Content -LiteralPath $AdminCanaryEvidencePath -Raw | ConvertFrom-Json
        $adminTimes = Test-CanaryEvidence $adminEvidence 'admin' 'Admin canary evidence'
        if ($adminTimes.completedAt -ne [DateTimeOffset]::MinValue -and $canaryTimes.startedAt -ne [DateTimeOffset]::MinValue -and
            $adminTimes.completedAt -gt $canaryTimes.startedAt) { $failures.Add('Gateway canary started before the Admin canary completed') }
    }
}

function Get-RequiredNonNegativeInteger($Object, [string] $Name, [string] $Description) {
    $value = Get-RequiredNonNegativeNumber $Object $Name $Description
    if ($null -ne $value -and $value -ne [Math]::Floor($value)) { $failures.Add("$Description must be a whole number") }
    return $value
}
function Test-RequiredText($Object, [string] $Name, [string] $Description) {
    $value = if ($null -ne $Object) { $Object.$Name } else { $null }
    if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value) -or $value -eq 'replace-me') { $failures.Add("$Description is missing or invalid") }
}
function Test-NearlyEqual([double] $Actual, [double] $Expected, [double] $AbsoluteTolerance = 0.001001) {
    return [Math]::Abs($Actual - $Expected) -le [Math]::Max($AbsoluteTolerance, [Math]::Abs($Expected) * 0.000001)
}
$requiredIds = if ($Service -eq 'admin') { @('admin-setting-read') } else { @('gateway-chat-nonstream', 'gateway-chat-sse') }
$minimumRuns = Get-RequiredPositiveInteger $policy 'minimumBenchmarkRuns' 'minimum benchmark run count'
if ($null -ne $minimumRuns -and ($minimumRuns -lt 5 -or $minimumRuns -gt 21)) { $failures.Add('minimum benchmark run count must be between 5 and 21') }
foreach ($policyService in 'admin', 'gateway') {
    $expectedIds = if ($policyService -eq 'admin') { @('admin-setting-read') } else { @('gateway-chat-nonstream', 'gateway-chat-sse') }
    $configuredIds = $policy.requiredWorkloads.$policyService
    if ($configuredIds -isnot [array] -or @($configuredIds).Count -ne $expectedIds.Count -or
        (@($configuredIds | Sort-Object) -join '|') -cne (@($expectedIds | Sort-Object) -join '|')) {
        $failures.Add("$policyService policy required workloads must match the supported authenticated workload set")
    }
}
$pairedRuns = Get-RequiredPositiveInteger $benchmark 'pairedRuns' 'paired benchmark run count'
if ($null -ne $pairedRuns -and ($pairedRuns -lt 5 -or $pairedRuns -gt 21 -or $pairedRuns % 2 -ne 1)) { $failures.Add('paired benchmark run count must be an odd number between 5 and 21') }
if ($null -ne $pairedRuns -and $null -ne $minimumRuns -and $pairedRuns -lt $minimumRuns) { $failures.Add('paired benchmark run count is below the policy minimum') }
if ($benchmark.samples -isnot [array] -or @($benchmark.samples).Count -ne $pairedRuns) { $failures.Add('retained raw samples must contain every recorded benchmark pair') }

$environment = $benchmark.environment
$environmentSchema = Get-RequiredPositiveInteger $environment 'schemaVersion' 'benchmark environment evidence schema version'
if ($environmentSchema -ne 1) { $failures.Add('benchmark environment evidence schema version is not supported') }
Test-RequiredText $environment 'environmentId' 'benchmark environment identifier'
foreach ($name in 'os', 'architecture', 'cpu', 'dockerVersion', 'kernelVersion') { Test-RequiredText $environment.host $name "benchmark host $name" }
if ($environment.host.os -cne 'linux' -or $environment.host.architecture -cne 'x64') { $failures.Add('benchmark host must describe a Linux x64 environment') }
Get-RequiredPositiveInteger $environment.host 'logicalProcessors' 'benchmark host logical processor count' | Out-Null
Get-RequiredPositiveInteger $environment.host 'memoryBytes' 'benchmark host memory' | Out-Null
foreach ($name in 'postgresqlVersion', 'redisVersion', 'wolverineVersion', 'configurationId', 'providerFixtureId') { Test-RequiredText $environment.backingServices $name "benchmark backing services $name" }
if ($Service -eq 'gateway' -and $environment.backingServices.providerFixtureId -ieq 'not-applicable') {
    $failures.Add('Gateway benchmark provider fixture evidence must identify its deterministic provider')
}

$commonNames = @('imageBytes', 'pullBytes', 'coldReadinessMilliseconds', 'idleMemoryBytes', 'steadyStateMemoryBytes', 'peakMemoryBytes')
$workloadMetricNames = @('elapsedMilliseconds', 'throughputPerSecond', 'p50Milliseconds', 'p95Milliseconds', 'p99Milliseconds')
$identityNames = @('id', 'method', 'path', 'authenticated', 'inputFingerprint', 'requested', 'executed', 'completed', 'failures', 'warmupRequests', 'concurrency', 'timeoutSeconds')

function Get-WorkloadMap($Rows, [string] $Description, [bool] $RawSample) {
    $map = @{}
    if ($Rows -isnot [array]) { $failures.Add("$Description workloads must be an array with the exact required workload set"); return $map }
    if (@($Rows).Count -ne $requiredIds.Count) { $failures.Add("$Description workload set has missing, duplicate, or unexpected entries") }
    foreach ($row in $Rows) {
        $id = if ($null -ne $row) { [string]$row.id } else { '' }
        if ($id -cnotin $requiredIds) { $failures.Add("$Description workload set contains an unexpected workload"); continue }
        if ($map.ContainsKey($id)) { $failures.Add("$Description workload set contains duplicate '$id'"); continue }
        $method = if ($id -eq 'admin-setting-read') { 'GET' } else { 'POST' }
        $path = if ($id -eq 'admin-setting-read') { '/v1/admin/global-settings/by-key/{key}' } else { '/v1/chat/completions' }
        if ($row.method -cne $method -or $row.path -cne $path) { $failures.Add("$Description $id method or path does not match its authenticated workload") }
        Test-RequiredBoolean $row 'authenticated' "$Description $id authenticated workload"
        if ($row.inputFingerprint -isnot [string] -or $row.inputFingerprint -cnotmatch '^[0-9a-f]{64}$') { $failures.Add("$Description $id input fingerprint is missing or invalid") }
        $numbers = @{}
        foreach ($name in 'requested', 'executed', 'completed', 'concurrency', 'timeoutSeconds') { $numbers[$name] = Get-RequiredPositiveInteger $row $name "$Description $id $name" }
        foreach ($name in 'failures', 'warmupRequests') { $numbers[$name] = Get-RequiredNonNegativeInteger $row $name "$Description $id $name" }
        if ($numbers.failures -ne 0 -or $numbers.executed -ne $numbers.requested -or $numbers.completed -ne $numbers.requested) {
            $failures.Add("$Description $id must complete every requested request without failures")
        }
        if ($null -ne $numbers.requested -and $numbers.requested -gt 100000) { $failures.Add("$Description $id requested count exceeds the workload limit") }
        if ($null -ne $numbers.concurrency -and ($numbers.concurrency -gt 256 -or $numbers.concurrency -gt $numbers.requested)) { $failures.Add("$Description $id concurrency exceeds the workload limit") }
        if (($null -ne $numbers.warmupRequests -and $numbers.warmupRequests -gt 10000) -or
            ($null -ne $numbers.timeoutSeconds -and $numbers.timeoutSeconds -gt 120)) { $failures.Add("$Description $id warm-up or timeout exceeds the workload limit") }
        $metrics = @{}
        foreach ($name in 'elapsedMilliseconds', 'throughputPerSecond') { $metrics[$name] = Get-RequiredPositiveNumber $row $name "$Description $id $name" }
        foreach ($name in 'p50Milliseconds', 'p95Milliseconds', 'p99Milliseconds') { $metrics[$name] = Get-RequiredNonNegativeNumber $row $name "$Description $id $name" }
        if ($metrics.p50Milliseconds -gt $metrics.p95Milliseconds -or $metrics.p95Milliseconds -gt $metrics.p99Milliseconds) { $failures.Add("$Description $id latency percentiles must be ordered p50 <= p95 <= p99") }
        if ($RawSample -and $null -ne $metrics.elapsedMilliseconds -and $metrics.elapsedMilliseconds -gt 0 -and
            $null -ne $numbers.completed -and $null -ne $metrics.throughputPerSecond) {
            $expectedThroughput = $numbers.completed * 1000 / $metrics.elapsedMilliseconds
            if (-not (Test-NearlyEqual $metrics.throughputPerSecond $expectedThroughput 0.000001)) { $failures.Add("$Description $id throughput does not match completed requests and elapsed time") }
        }
        $map[$id] = [pscustomobject]@{ row = $row; metrics = $metrics }
    }
    foreach ($id in $requiredIds) { if (-not $map.ContainsKey($id)) { $failures.Add("$Description workload set is missing '$id'") } }
    return $map
}

function Get-Variant($Variant, [string] $Name, [string] $Description, [bool] $RawSample) {
    $expectedRuntime = if ($Name -eq 'jit') { 'jit' } else { 'native-aot' }
    if ($null -eq $Variant) { $failures.Add("$Description benchmark result is missing") }
    if ($Variant.runtime -cne $expectedRuntime) { $failures.Add("$Description benchmark runtime is invalid") }
    Test-RequiredText $Variant 'imageRevision' "$Description image revision"
    $image = [string]$Variant.image
    if ($image -notmatch '@(sha256:[0-9a-fA-F]{64})$') { $failures.Add("$Description benchmark image is not pinned by digest") }
    else {
        $recordedDigest = if ($Name -eq 'jit') { $evidence.correspondingJitDigest } else { $evidence.candidateDigest }
        if ($Matches[1] -ne $recordedDigest) { $failures.Add("$Description benchmark image digest does not match canary evidence") }
    }
    $metrics = @{}
    foreach ($metric in $commonNames) {
        $metrics[$metric] = if ($metric -eq 'coldReadinessMilliseconds') { Get-RequiredPositiveNumber $Variant $metric "$Description $metric" }
            else { Get-RequiredPositiveInteger $Variant $metric "$Description $metric" }
    }
    if ($Name -eq 'native' -and $benchmarkSchema -eq 3) {
        if ($RawSample -and ($Variant.nativeContainerId -isnot [string] -or $Variant.nativeContainerId -cnotmatch '\A[0-9a-f]{64}\z')) {
            $failures.Add("$Description native container binding is missing or invalid")
        }
        if ($null -ne $nativeImageEvidence) {
            try { Assert-NativeBenchmarkImageEvidenceEqual -Expected $nativeImageEvidence['binding'] -Actual $Variant.imageBinding }
            catch { $failures.Add("$Description native image binding differs from retained packaging evidence") }
            if ($Variant.image -isnot [string] -or $Variant.image -cne $boundNativeImage -or
                $Variant.imageRevision -isnot [string] -or $Variant.imageRevision -cne $nativeImageEvidence['identity']['source']['revision']) {
                $failures.Add("$Description native image binding has a different image or source revision")
            }
            if ($null -ne $metrics.pullBytes -and $metrics.pullBytes -ne $nativeImageEvidence['pullBytes']) {
                $failures.Add("$Description native image binding pull size differs from verified selected layers")
            }
        }
    }
    if ($metrics.peakMemoryBytes -lt $metrics.idleMemoryBytes -or $metrics.peakMemoryBytes -lt $metrics.steadyStateMemoryBytes) { $failures.Add("$Description peak memory is below its recorded idle or steady-state memory") }
    return [pscustomobject]@{ image = $image; imageRevision = $Variant.imageRevision; metrics = $metrics; workloads = Get-WorkloadMap $Variant.workloads $Description $RawSample }
}
$variants = @{}
foreach ($name in 'jit', 'native') { $variants[$name] = Get-Variant $benchmark.$name $name "$name median" $false }
if ($variants.jit.imageRevision -cne $variants.native.imageRevision) { $failures.Add('JIT and native image revisions must match') }
$rawVariants = @{ jit = [System.Collections.Generic.List[object]]::new(); native = [System.Collections.Generic.List[object]]::new() }
$nativeContainerIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$baseline = $null
$index = 0
foreach ($sample in @($benchmark.samples | Select-Object -First 21)) {
    $index++
    $pair = Get-RequiredPositiveInteger $sample 'pair' "sample $index pair number"
    if ($pair -ne $index) { $failures.Add('retained raw sample pair numbers must be complete, unique, and ordered') }
    $order = if ($index % 2 -eq 1) { @('jit', 'native-aot') } else { @('native-aot', 'jit') }
    if ($sample.order -isnot [array] -or @($sample.order).Count -ne 2 -or (@($sample.order) -join '|') -cne ($order -join '|')) { $failures.Add("sample $index runtime order must alternate across recorded pairs") }
    foreach ($name in 'jit', 'native') {
        $raw = Get-Variant $sample.$name $name "sample $index $name" $true
        if ($name -eq 'native' -and $benchmarkSchema -eq 3 -and $sample.native.nativeContainerId -is [string] -and
            $sample.native.nativeContainerId -cmatch '\A[0-9a-f]{64}\z' -and
            -not $nativeContainerIds.Add($sample.native.nativeContainerId)) {
            $failures.Add("sample $index native container binding repeats a recorded cold-start container")
        }
        $rawVariants[$name].Add($raw)
        if ($raw.image -cne $variants[$name].image) { $failures.Add("sample $index $name image differs from its median variant") }
        if ($raw.imageRevision -cne $variants[$name].imageRevision) { $failures.Add("sample $index $name image revision differs from its median variant") }
        if ($null -eq $baseline) { $baseline = $raw.workloads }
        foreach ($id in $requiredIds) {
            if ($raw.workloads.ContainsKey($id) -and $baseline.ContainsKey($id)) {
                foreach ($property in $identityNames) {
                    if ($raw.workloads[$id].row.$property -cne $baseline[$id].row.$property) { $failures.Add("sample $index $name $id workload metadata $property differs across JIT/native pairs") }
                }
            }
        }
    }
}
function Get-Median($Values, [string] $Description) {
    $ordered = @($Values | Where-Object { $null -ne $_ } | Sort-Object)
    if ($ordered.Count -eq 0) { $failures.Add("$Description cannot be recomputed without raw samples"); return $null }
    if ($ordered.Count % 2 -eq 0) { return ($ordered[$ordered.Count / 2 - 1] + $ordered[$ordered.Count / 2]) / 2 }
    return $ordered[[int][Math]::Floor($ordered.Count / 2)]
}
foreach ($name in 'jit', 'native') {
    foreach ($metric in $commonNames) {
        $median = Get-Median @($rawVariants[$name] | ForEach-Object { $_.metrics[$metric] }) "$name $metric median"
        $recorded = $variants[$name].metrics[$metric]
        if ($null -ne $median -and $null -ne $recorded -and -not (Test-NearlyEqual $recorded $median)) { $failures.Add("$name $metric median does not match retained raw samples") }
    }
    foreach ($id in $requiredIds) {
        if (-not $variants[$name].workloads.ContainsKey($id)) { continue }
        if ($null -ne $baseline -and $baseline.ContainsKey($id)) {
            foreach ($property in $identityNames) {
                if ($variants[$name].workloads[$id].row.$property -cne $baseline[$id].row.$property) { $failures.Add("$name median $id workload metadata $property differs from retained raw samples") }
            }
        }
        foreach ($metric in $workloadMetricNames) {
            $values = @($rawVariants[$name] | ForEach-Object { if ($_.workloads.ContainsKey($id)) { $_.workloads[$id].metrics[$metric] } })
            $median = Get-Median $values "$name $id $metric median"
            $recorded = $variants[$name].workloads[$id].metrics[$metric]
            if ($null -ne $median -and $null -ne $recorded -and -not (Test-NearlyEqual $recorded $median)) { $failures.Add("$name $id $metric median does not match retained raw samples") }
        }
    }
}

$nativePublish = $benchmark.nativePublish
if ($null -eq $nativePublish) { $failures.Add('native publish baseline is missing') }
else {
    $publishService = if ($Service -eq 'admin') { 'Admin' } else { 'Gateway' }
    if ($nativePublish.service -ne $publishService) { $failures.Add('native publish baseline service is invalid') }
    if ($nativePublish.rid -ne 'linux-x64') { $failures.Add('native publish baseline RID is not linux-x64') }
    Get-RequiredPositiveNumber $nativePublish 'publishDurationMilliseconds' 'native publish duration' | Out-Null
    Get-RequiredPositiveNumber $nativePublish 'executableBytes' 'native executable size' | Out-Null
    Get-RequiredPositiveNumber $nativePublish 'runtimeArtifactBytes' 'native runtime artifact size' | Out-Null
}
if ($benchmarkSchema -eq 3 -and $null -ne $nativeImageEvidence) {
    try { Assert-NativeBenchmarkImageEvidenceEqual -Expected $nativeImageEvidence['nativePublish'] -Actual $nativePublish }
    catch { $failures.Add('native publish binding differs from retained image packaging evidence') }
}
function Test-CalculatedComparison([string] $Description, $Object, [string] $PropertyName, $JitValue, $NativeValue, [bool] $IsIncrease) {
    $recorded = Get-RequiredNumber $Object $PropertyName $Description
    if ($null -eq $recorded -or $null -eq $JitValue -or $null -eq $NativeValue) { return $recorded }
    if ($JitValue -le 0) { $failures.Add("$Description cannot be recomputed from a nonpositive JIT measurement"); return $recorded }
    $expected = if ($IsIncrease) { (($NativeValue - $JitValue) / $JitValue) * 100 } else { (($JitValue - $NativeValue) / $JitValue) * 100 }
    if ([Math]::Abs($recorded - $expected) -gt 0.01) { $failures.Add("$Description does not match independently recomputed benchmark medians") }
    return $recorded
}
$comparisonNames = [ordered]@{
    imageBytes = 'imageSizeReductionPercent'; pullBytes = 'pullSizeReductionPercent'; coldReadinessMilliseconds = 'coldStartReductionPercent'
    idleMemoryBytes = 'idleMemoryReductionPercent'; steadyStateMemoryBytes = 'steadyStateMemoryReductionPercent'; peakMemoryBytes = 'peakMemoryReductionPercent'
}
$common = @{}
foreach ($metric in $comparisonNames.Keys) {
    $common[$metric] = Test-CalculatedComparison "$metric comparison" $benchmark.comparison.common $comparisonNames[$metric] ($variants.jit.metrics[$metric]) ($variants.native.metrics[$metric]) $false
}
$minimumImage = Get-RequiredNumber $policy.thresholds 'minimumImageSizeReductionPercent' 'minimum image-size reduction threshold'
$minimumCold = Get-RequiredNumber $policy.thresholds 'minimumColdStartReductionPercent' 'minimum cold-start reduction threshold'
$minimumIdle = Get-RequiredNumber $policy.thresholds 'minimumIdleMemoryReductionPercent' 'minimum idle-memory reduction threshold'
$maximumP99 = Get-RequiredNumber $policy.thresholds 'maximumP99LatencyRegressionPercent' 'maximum p99 latency regression threshold'
$minimumThroughput = Get-RequiredNumber $policy.thresholds 'minimumThroughputImprovementPercent' 'minimum throughput improvement threshold'
if ($null -ne $common.imageBytes -and $null -ne $minimumImage -and $common.imageBytes -lt $minimumImage) { $failures.Add('image-size improvement is below threshold') }
if ($null -ne $common.coldReadinessMilliseconds -and $null -ne $minimumCold -and $common.coldReadinessMilliseconds -lt $minimumCold) { $failures.Add('cold-start improvement is below threshold') }
if ($null -ne $common.idleMemoryBytes -and $null -ne $minimumIdle -and $common.idleMemoryBytes -lt $minimumIdle) { $failures.Add('idle-memory improvement is below threshold') }
$comparisons = @{}
if ($benchmark.comparison.workloads -isnot [array] -or @($benchmark.comparison.workloads).Count -ne $requiredIds.Count) { $failures.Add('workload comparisons must contain the exact required workload set') }
foreach ($row in @($benchmark.comparison.workloads)) {
    $id = [string]$row.id
    if ($id -cnotin $requiredIds -or $comparisons.ContainsKey($id)) { $failures.Add('workload comparisons contain a duplicate or unexpected workload'); continue }
    $comparisons[$id] = $row
}
foreach ($id in $requiredIds) {
    if (-not $comparisons.ContainsKey($id)) { $failures.Add("workload comparison is missing '$id'"); continue }
    if (-not $variants.jit.workloads.ContainsKey($id) -or -not $variants.native.workloads.ContainsKey($id)) { continue }
    $jitMetrics = $variants.jit.workloads[$id].metrics
    $nativeMetrics = $variants.native.workloads[$id].metrics
    $throughput = Test-CalculatedComparison "$id throughput comparison" $comparisons[$id] 'throughputImprovementPercent' $jitMetrics.throughputPerSecond $nativeMetrics.throughputPerSecond $true
    foreach ($metric in 'p50', 'p95', 'p99') {
        $regression = Test-CalculatedComparison "$id $metric latency comparison" $comparisons[$id] ($metric + 'LatencyRegressionPercent') $jitMetrics[($metric + 'Milliseconds')] $nativeMetrics[($metric + 'Milliseconds')] $true
        if ($metric -eq 'p99' -and $null -ne $regression -and $null -ne $maximumP99 -and $regression -gt $maximumP99) { $failures.Add("$id p99 latency regression exceeds threshold") }
    }
    if ($null -ne $throughput -and $null -ne $minimumThroughput -and $throughput -lt $minimumThroughput) { $failures.Add("$id throughput improvement is below threshold") }
}

if ($benchmarkSchema -eq 3 -and $null -ne $nativeImageEvidence) {
    try {
        $finalNativeImageEvidence = Read-NativeBenchmarkImageEvidence -Directory $NativePackagingDirectory -Service $Service -NativeImage $boundNativeImage
        foreach ($field in 'identity', 'binding', 'nativePublish', 'pullBytes') {
            Assert-NativeBenchmarkImageEvidenceEqual -Expected $nativeImageEvidence[$field] -Actual $finalNativeImageEvidence[$field]
        }
    }
    catch { $failures.Add('native image binding changed or could not be reverified after evaluation') }
}

$decision = if ($failures.Count -eq 0) { 'promote' } else { 'hold' }
[pscustomobject]@{
    service = $Service
    decision = $decision
    evaluatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    failures = @($failures)
} | ConvertTo-Json -Depth 5

if ($decision -ne 'promote' -and -not $ReportOnly) { exit 1 }
