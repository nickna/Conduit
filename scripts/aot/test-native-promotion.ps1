[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '../..')
$evaluator = Join-Path $PSScriptRoot 'evaluate-native-promotion.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "conduit-native-promotion-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$checks = 0

function Copy-Value($Value) { return $Value | ConvertTo-Json -Depth 30 | ConvertFrom-Json }
function Write-Fixture([string] $Name, $Value) {
    $path = Join-Path $temporaryRoot "$Name.json"
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path
    return $path
}
function Invoke-Gate([string] $Service, $Policy, $Benchmark, $Evidence, $AdminEvidence = $null) {
    $arguments = @{
        Service = $Service
        PolicyPath = Write-Fixture "policy-$([guid]::NewGuid().ToString('N'))" $Policy
        BenchmarkPath = Write-Fixture "benchmark-$([guid]::NewGuid().ToString('N'))" $Benchmark
        CanaryEvidencePath = Write-Fixture "evidence-$([guid]::NewGuid().ToString('N'))" $Evidence
        ReportOnly = $true
    }
    if ($null -ne $AdminEvidence) { $arguments.AdminCanaryEvidencePath = Write-Fixture "admin-$([guid]::NewGuid().ToString('N'))" $AdminEvidence }
    return ((& $evaluator @arguments) -join [Environment]::NewLine) | ConvertFrom-Json
}
function Assert-Decision($Result, [string] $Expected, [string] $Description) {
    $script:checks++
    if ($Result.decision -ne $Expected) { throw "$Description expected '$Expected' but received '$($Result.decision)': $($Result.failures -join '; ')" }
}
function Assert-Failure($Result, [string] $Pattern, [string] $Description) {
    if (-not (@($Result.failures) -match $Pattern)) { throw "$Description did not report '$Pattern': $($Result.failures -join '; ')" }
}
function New-Evidence([string] $Service, [char] $NativeDigest, [char] $JitDigest) {
    return [ordered]@{
        schemaVersion = 1; service = $Service
        candidateDigest = "sha256:$([string]$NativeDigest * 64)"
        correspondingJitDigest = "sha256:$([string]$JitDigest * 64)"
        startedAt = '2020-01-01T00:00:00Z'; completedAt = '2020-01-08T00:00:00Z'
        soakHours = 168; healthyWindows = 24; errorRate = 0
        contractsMatchedJit = $true; backingServicesMatchedJit = $true; fullFeatureMatrixPassed = $true
        dashboardAndAlertsVerified = $true; automaticRollbackTested = $true; manualRollbackTested = $true
        criticalVulnerabilitiesResolved = $true; sbomAndProvenanceVerified = $true
        dashboardUrl = 'https://monitoring.example.test/dashboard'; alertsUrl = 'https://monitoring.example.test/alerts'
    }
}
function New-Workload([string] $Id, [bool] $Native, [int] $Offset) {
    $stream = $Id -eq 'gateway-chat-sse'
    $throughput = if ($stream) { if ($Native) { 84 } else { 80 } } else { if ($Native) { 105 } else { 100 } }
    $throughput += $Offset
    $p50 = if ($Native) { 9 } else { 10 }
    $p95 = if ($Native) { 19 } else { 20 }
    $p99 = if ($stream) { if ($Native) { 41 } else { 40 } } else { if ($Native) { 31 } else { 30 } }
    $fingerprint = if ($Id -eq 'admin-setting-read') { 'a' } elseif ($stream) { 'f' } else { 'e' }
    return [ordered]@{
        id = $Id; method = if ($Id -eq 'admin-setting-read') { 'GET' } else { 'POST' }
        path = if ($Id -eq 'admin-setting-read') { '/v1/admin/global-settings/by-key/{key}' } else { '/v1/chat/completions' }
        authenticated = $true; inputFingerprint = ($fingerprint * 64)
        requested = 1000; executed = 1000; completed = 1000; failures = 0
        warmupRequests = 100; concurrency = 20; timeoutSeconds = 5
        elapsedMilliseconds = 1000000 / $throughput; throughputPerSecond = $throughput
        p50Milliseconds = $p50 + $Offset / 10
        p95Milliseconds = $p95 + $Offset / 10
        p99Milliseconds = $p99 + $Offset / 10
    }
}
function New-Variant([string] $Service, [bool] $Native, [char] $Digest, [int] $Offset) {
    $ids = if ($Service -eq 'admin') { @('admin-setting-read') } else { @('gateway-chat-nonstream', 'gateway-chat-sse') }
    return [ordered]@{
        runtime = if ($Native) { 'native-aot' } else { 'jit' }
        image = "registry.example.test/conduit-$Service@sha256:$([string]$Digest * 64)"
        imageRevision = ('1' * 40)
        imageBytes = if ($Native) { 800 } else { 1000 }
        pullBytes = if ($Native) { 600 } else { 800 }
        coldReadinessMilliseconds = $(if ($Native) { 70 } else { 100 }) + $Offset
        idleMemoryBytes = $(if ($Native) { 700 } else { 1000 }) + $Offset
        steadyStateMemoryBytes = $(if ($Native) { 700 } else { 1000 }) + $Offset
        peakMemoryBytes = $(if ($Native) { 900 } else { 1200 }) + $Offset
        workloads = @($ids | ForEach-Object { New-Workload $_ $Native $Offset })
    }
}
function Get-Increase([double] $Jit, [double] $Native) { return (($Native - $Jit) / $Jit) * 100 }
function Set-Comparisons($Benchmark) {
    $common = [ordered]@{}
    $names = [ordered]@{
        imageBytes = 'imageSizeReductionPercent'; pullBytes = 'pullSizeReductionPercent'; coldReadinessMilliseconds = 'coldStartReductionPercent'
        idleMemoryBytes = 'idleMemoryReductionPercent'; steadyStateMemoryBytes = 'steadyStateMemoryReductionPercent'; peakMemoryBytes = 'peakMemoryReductionPercent'
    }
    foreach ($name in $names.Keys) { $common[$names[$name]] = -(Get-Increase $Benchmark.jit.$name $Benchmark.native.$name) }
    $rows = @()
    foreach ($jitRow in $Benchmark.jit.workloads) {
        $nativeRow = $Benchmark.native.workloads | Where-Object id -EQ $jitRow.id | Select-Object -First 1
        $rows += [ordered]@{
            id = $jitRow.id
            throughputImprovementPercent = Get-Increase $jitRow.throughputPerSecond $nativeRow.throughputPerSecond
            p50LatencyRegressionPercent = Get-Increase $jitRow.p50Milliseconds $nativeRow.p50Milliseconds
            p95LatencyRegressionPercent = Get-Increase $jitRow.p95Milliseconds $nativeRow.p95Milliseconds
            p99LatencyRegressionPercent = Get-Increase $jitRow.p99Milliseconds $nativeRow.p99Milliseconds
        }
    }
    $Benchmark.comparison = [ordered]@{ common = $common; workloads = $rows }
}
function New-Benchmark([string] $Service, [char] $NativeDigest, [char] $JitDigest, [int] $Runs = 5) {
    $result = [ordered]@{
        schemaVersion = 2; generatedAt = '2020-01-01T00:00:00Z'; service = $Service; platform = 'linux-x64'
        pairedRuns = $Runs; methodology = 'Synthetic authenticated paired benchmark contract fixture.'
        environment = [ordered]@{
            schemaVersion = 1; environmentId = 'owned-test-environment'
            host = [ordered]@{ os = 'linux'; architecture = 'x64'; cpu = 'test CPU'; logicalProcessors = 8; memoryBytes = 16000000000; dockerVersion = '28'; kernelVersion = '6.8' }
            backingServices = [ordered]@{ postgresqlVersion = '17'; redisVersion = '7.4'; wolverineVersion = '6.48.3'; configurationId = 'fixture-v1'; providerFixtureId = 'owned-provider-v1' }
        }
        jit = New-Variant $Service $false $JitDigest 0
        native = New-Variant $Service $true $NativeDigest 0
        samples = @(for ($pair = 1; $pair -le $Runs; $pair++) {
            $offset = $pair - [int][Math]::Ceiling($Runs / 2)
            [ordered]@{
                pair = $pair
                order = if ($pair % 2 -eq 1) { @('jit', 'native-aot') } else { @('native-aot', 'jit') }
                jit = New-Variant $Service $false $JitDigest $offset
                native = New-Variant $Service $true $NativeDigest $offset
            }
        })
        nativePublish = [ordered]@{
            service = if ($Service -eq 'admin') { 'Admin' } else { 'Gateway' }
            rid = 'linux-x64'; publishDurationMilliseconds = 10000; executableBytes = 50000000; runtimeArtifactBytes = 60000000
        }
        comparison = $null
    }
    Set-Comparisons $result
    return $result
}
function Get-AllVariants($Benchmark) {
    return @($Benchmark.jit, $Benchmark.native) + @($Benchmark.samples | ForEach-Object { $_.jit; $_.native })
}
function Assert-BenchmarkHold([string] $Description, [string] $Pattern, [scriptblock] $Mutate) {
    $candidate = Copy-Value $gatewayBenchmark
    & $Mutate $candidate
    $result = Invoke-Gate 'gateway' $openPolicy $candidate $gatewayEvidence $adminEvidence
    Assert-Decision $result 'hold' $Description
    Assert-Failure $result $Pattern $Description
}

try {
    $blockedPolicy = Get-Content -LiteralPath (Join-Path $repoRoot 'deploy/native-canary/promotion-policy.json') -Raw | ConvertFrom-Json
    $openPolicy = Copy-Value $blockedPolicy
    $openPolicy.status = 'open'; $openPolicy.blockedBy = @()
    $adminEvidence = New-Evidence 'admin' 'a' 'b'
    $gatewayEvidence = New-Evidence 'gateway' 'c' 'd'
    $gatewayEvidence.startedAt = '2020-01-08T00:00:00Z'; $gatewayEvidence.completedAt = '2020-01-15T00:00:00Z'
    $adminBenchmark = New-Benchmark 'admin' 'a' 'b'
    $gatewayBenchmark = New-Benchmark 'gateway' 'c' 'd'

    Assert-Decision (Invoke-Gate 'admin' $openPolicy $adminBenchmark $adminEvidence) 'promote' 'Complete paired Admin evidence'
    $adminWithoutProvider = Copy-Value $adminBenchmark
    $adminWithoutProvider.environment.backingServices.providerFixtureId = 'not-applicable'
    Assert-Decision (Invoke-Gate 'admin' $openPolicy $adminWithoutProvider $adminEvidence) 'promote' 'Admin does not require a provider fixture'
    Assert-Decision (Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $gatewayEvidence $adminEvidence) 'promote' 'Complete ordered paired Gateway evidence'
    $unfilledTemplate = Get-Content -LiteralPath (Join-Path $repoRoot 'deploy/native-canary/benchmark.template.json') -Raw | ConvertFrom-Json
    $templateResult = Invoke-Gate 'gateway' $openPolicy $unfilledTemplate $gatewayEvidence $adminEvidence
    Assert-Decision $templateResult 'hold' 'Unfilled committed template'
    Assert-Failure $templateResult 'raw samples|workload set|image revision' 'Unfilled committed template'
    $maximumBenchmark = New-Benchmark 'gateway' 'c' 'd' 21
    Assert-Decision (Invoke-Gate 'gateway' $openPolicy $maximumBenchmark $gatewayEvidence $adminEvidence) 'promote' 'Twenty-one complete pairs'
    $sevenBenchmark = New-Benchmark 'admin' 'a' 'b' 7
    $sevenPolicy = Copy-Value $openPolicy; $sevenPolicy.minimumBenchmarkRuns = 7
    Assert-Decision (Invoke-Gate 'admin' $sevenPolicy $sevenBenchmark $adminEvidence) 'promote' 'Higher configured run minimum'

    $alternate = Copy-Value $gatewayBenchmark
    foreach ($variant in (Get-AllVariants $alternate)) {
        foreach ($row in $variant.workloads) {
            $row.requested = 2000; $row.executed = 2000; $row.completed = 2000; $row.concurrency = 40; $row.warmupRequests = 25; $row.timeoutSeconds = 10
            $row.elapsedMilliseconds = $row.completed * 1000 / $row.throughputPerSecond
        }
    }
    Assert-Decision (Invoke-Gate 'gateway' $openPolicy $alternate $gatewayEvidence $adminEvidence) 'promote' 'Comparable alternative load in every pair'

    Assert-BenchmarkHold 'Health-only schema one' 'schema version 2' { param($b) $b.schemaVersion = 1; $b | Add-Member -NotePropertyName endpoint -NotePropertyValue '/health/ready' }
    Assert-BenchmarkHold 'Health-only schema two' 'unexpected workload' { param($b) foreach ($v in (Get-AllVariants $b)) { foreach ($r in $v.workloads) { $r.id = 'health-ready' } } }
    Assert-BenchmarkHold 'Missing SSE median' "missing 'gateway-chat-sse'" { param($b) $b.native.workloads = @($b.native.workloads[0]) }
    Assert-BenchmarkHold 'Missing SSE raw pair' "missing 'gateway-chat-sse'" { param($b) $b.samples[2].jit.workloads = @($b.samples[2].jit.workloads[0]) }
    Assert-BenchmarkHold 'Duplicate workload' 'duplicate' { param($b) $b.jit.workloads = @($b.jit.workloads[0], $b.jit.workloads[0]) }
    Assert-BenchmarkHold 'Unexpected raw workload' 'unexpected workload' { param($b) $b.samples[0].native.workloads[1].id = 'provider-unproven' }
    Assert-BenchmarkHold 'Unauthenticated workload' 'authenticated workload was not verified' { param($b) $b.samples[0].jit.workloads[0].authenticated = $false }
    Assert-BenchmarkHold 'String authentication claim' 'authenticated workload was not verified' { param($b) $b.samples[0].jit.workloads[0].authenticated = 'true' }
    Assert-BenchmarkHold 'Wrong method' 'method or path' { param($b) $b.native.workloads[1].method = 'GET' }
    Assert-BenchmarkHold 'Health path substituted' 'method or path' { param($b) $b.samples[1].native.workloads[1].path = '/health/ready' }
    Assert-BenchmarkHold 'Invalid fingerprint' 'fingerprint is missing or invalid' { param($b) $b.native.workloads[0].inputFingerprint = 'unverified' }
    Assert-BenchmarkHold 'Different native input' 'inputFingerprint differs' { param($b) $b.samples[4].native.workloads[1].inputFingerprint = ('a' * 64) }
    foreach ($property in 'requested', 'concurrency', 'warmupRequests', 'timeoutSeconds') {
        $changedProperty = $property
        Assert-BenchmarkHold "Changed pair $property" "metadata $property differs" {
            param($b)
            $row = $b.samples[3].native.workloads[0]
            $row.$changedProperty += 1
            if ($changedProperty -eq 'requested') { $row.executed = $row.requested; $row.completed = $row.requested; $row.elapsedMilliseconds = $row.completed * 1000 / $row.throughputPerSecond }
        }
    }
    foreach ($property in 'requested', 'executed', 'completed', 'failures', 'warmupRequests', 'concurrency', 'timeoutSeconds') {
        $changedProperty = $property
        Assert-BenchmarkHold "Fractional $property" 'must be a whole number' { param($b) $b.samples[2].native.workloads[1].$changedProperty = 1.5 }
    }
    Assert-BenchmarkHold 'Partial requests' 'complete every requested request' { param($b) $b.samples[3].native.workloads[1].completed = 999 }
    Assert-BenchmarkHold 'Failed requests' 'complete every requested request' { param($b) $b.samples[3].native.workloads[1].failures = 1 }
    Assert-BenchmarkHold 'Forged raw throughput' 'throughput does not match' { param($b) $b.samples[0].native.workloads[0].throughputPerSecond *= 2 }
    Assert-BenchmarkHold 'String numeric throughput' 'missing or not finite' { param($b) $b.samples[0].native.workloads[0].throughputPerSecond = '100' }
    Assert-BenchmarkHold 'Nonfinite latency text' 'missing or not finite' { param($b) $b.samples[0].native.workloads[0].p99Milliseconds = 'NaN' }
    Assert-BenchmarkHold 'Unordered percentiles' 'percentiles must be ordered' { param($b) $b.samples[0].jit.workloads[0].p50Milliseconds = 100 }
    Assert-BenchmarkHold 'Negative elapsed duration' 'must be greater than zero' { param($b) $b.samples[0].jit.workloads[0].elapsedMilliseconds = -1 }
    Assert-BenchmarkHold 'Forged common median' 'imageBytes median does not match' { param($b) $b.native.imageBytes = 750 }
    Assert-BenchmarkHold 'Forged workload median' 'p95Milliseconds median does not match' { param($b) $b.native.workloads[1].p95Milliseconds += 5 }
    Assert-BenchmarkHold 'Forged comparison' 'does not match independently recomputed' { param($b) $b.comparison.workloads[1].p99LatencyRegressionPercent = -50 }
    Assert-BenchmarkHold 'Forged common comparison' 'does not match independently recomputed' { param($b) $b.comparison.common.imageSizeReductionPercent = 90 }
    Assert-BenchmarkHold 'Zero latency comparison denominator' 'cannot be recomputed from a nonpositive' {
        param($b)
        $b.jit.workloads[0].p50Milliseconds = 0
        foreach ($s in $b.samples) { $s.jit.workloads[0].p50Milliseconds = 0 }
    }
    Assert-BenchmarkHold 'Bad SSE p99 hidden by aggregate' 'gateway-chat-sse p99 latency regression exceeds' {
        param($b)
        $b.native.workloads[1].p99Milliseconds = 60
        foreach ($s in $b.samples) { $s.native.workloads[1].p99Milliseconds = 60 }
        Set-Comparisons $b
        $b.comparison | Add-Member -NotePropertyName p99LatencyRegressionPercent -NotePropertyValue -99
    }
    Assert-BenchmarkHold 'Bad SSE throughput hidden by aggregate' 'gateway-chat-sse throughput improvement is below' {
        param($b)
        $b.native.workloads[1].throughputPerSecond = 40; $b.native.workloads[1].elapsedMilliseconds = 25000
        foreach ($s in $b.samples) { $s.native.workloads[1].throughputPerSecond = 40; $s.native.workloads[1].elapsedMilliseconds = 25000 }
        Set-Comparisons $b
        $b.comparison | Add-Member -NotePropertyName throughputImprovementPercent -NotePropertyValue 100
    }
    Assert-BenchmarkHold 'Missing raw pair' 'every recorded benchmark pair' { param($b) $b.samples = @($b.samples[0..3]) }
    Assert-BenchmarkHold 'Even pair count' 'odd number' { param($b) $b.pairedRuns = 4; $b.samples = @($b.samples[0..3]) }
    Assert-BenchmarkHold 'Too few pairs' 'odd number|below the policy minimum' { param($b) $b.pairedRuns = 3; $b.samples = @($b.samples[0..2]) }
    Assert-BenchmarkHold 'Too many declared pairs' 'odd number between 5 and 21' { param($b) $b.pairedRuns = 23 }
    Assert-BenchmarkHold 'Fractional pair count' 'must be a whole number' { param($b) $b.pairedRuns = 5.5 }
    Assert-BenchmarkHold 'Duplicate pair number' 'pair numbers must be complete' { param($b) $b.samples[3].pair = 1 }
    Assert-BenchmarkHold 'Unpaired runtime order' 'runtime order must alternate' { param($b) $b.samples[1].order = @('jit', 'native-aot') }
    Assert-BenchmarkHold 'Missing raw runtime' 'benchmark result is missing' { param($b) $b.samples[1].native = $null }
    Assert-BenchmarkHold 'No raw samples' 'every recorded benchmark pair' { param($b) $b.samples = @() }
    Assert-BenchmarkHold 'Missing host metadata' 'host cpu is missing' { param($b) $b.environment.host.PSObject.Properties.Remove('cpu') }
    Assert-BenchmarkHold 'Fractional host processors' 'logical processor count must be a whole' { param($b) $b.environment.host.logicalProcessors = 1.5 }
    Assert-BenchmarkHold 'String host memory' 'host memory is missing or not finite' { param($b) $b.environment.host.memoryBytes = '16000000000' }
    Assert-BenchmarkHold 'Wrong host architecture' 'Linux x64' { param($b) $b.environment.host.architecture = 'arm64' }
    Assert-BenchmarkHold 'Missing provider fixture evidence' 'providerFixtureId is missing' { param($b) $b.environment.backingServices.PSObject.Properties.Remove('providerFixtureId') }
    Assert-BenchmarkHold 'Gateway without deterministic provider evidence' 'provider fixture evidence must identify' { param($b) $b.environment.backingServices.providerFixtureId = 'not-applicable' }
    Assert-BenchmarkHold 'Wrong native digest' 'image digest does not match' { param($b) $b.native.image = "registry.example.test/native@sha256:$('a' * 64)" }
    Assert-BenchmarkHold 'Wrong raw digest' 'image digest does not match' { param($b) $b.samples[0].native.image = "registry.example.test/native@sha256:$('a' * 64)" }
    Assert-BenchmarkHold 'Different revisions' 'image revisions must match' { param($b) $b.native.imageRevision = ('2' * 40) }
    Assert-BenchmarkHold 'Changed raw revision' 'image revision differs' { param($b) $b.samples[4].native.imageRevision = ('2' * 40) }
    Assert-BenchmarkHold 'Missing native publish evidence' 'native publish baseline is missing' { param($b) $b.nativePublish = $null }
    Assert-BenchmarkHold 'Wrong native runtime' 'benchmark runtime is invalid' { param($b) $b.native.runtime = 'jit' }
    Assert-BenchmarkHold 'Missing workload comparison' 'workload comparison is missing' { param($b) $b.comparison.workloads = @($b.comparison.workloads[0]) }
    Assert-BenchmarkHold 'Duplicate workload comparison' 'duplicate or unexpected' { param($b) $b.comparison.workloads = @($b.comparison.workloads[0], $b.comparison.workloads[0]) }

    foreach ($metric in 'imageBytes', 'coldReadinessMilliseconds', 'idleMemoryBytes') {
        $changedMetric = $metric
        Assert-BenchmarkHold "Insufficient common $metric benefit" 'improvement is below threshold' {
            param($b)
            $value = if ($changedMetric -eq 'imageBytes') { 950 } elseif ($changedMetric -eq 'coldReadinessMilliseconds') { 95 } else { 900 }
            $b.native.$changedMetric = $value
            foreach ($s in $b.samples) { $s.native.$changedMetric = $value }
            Set-Comparisons $b
        }
    }
    $result = Invoke-Gate 'admin' $blockedPolicy $adminBenchmark $adminEvidence
    Assert-Decision $result 'hold' 'Repository policy blocker'
    Assert-Failure $result '^promotion blocked:' 'Repository policy blocker'
    $badEvidence = Copy-Value $adminEvidence; $badEvidence.contractsMatchedJit = 'true'; $badEvidence.errorRate = '0'
    $result = Invoke-Gate 'admin' $openPolicy $adminBenchmark $badEvidence
    Assert-Decision $result 'hold' 'String-typed canary evidence'
    Assert-Failure $result 'JIT contract parity was not verified' 'String-typed canary evidence'
    Assert-Failure $result 'error rate is missing or not finite' 'String-typed canary evidence'
    $badEvidence = Copy-Value $adminEvidence; $badEvidence.automaticRollbackTested = $false
    $result = Invoke-Gate 'admin' $openPolicy $adminBenchmark $badEvidence
    Assert-Decision $result 'hold' 'Unverified rollback'
    Assert-Failure $result 'automatic rollback was not verified' 'Unverified rollback'
    $overlap = Copy-Value $gatewayEvidence; $overlap.startedAt = '2020-01-07T23:59:59Z'
    $result = Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $overlap $adminEvidence
    Assert-Decision $result 'hold' 'Overlapping Gateway canary'
    Assert-Failure $result 'Gateway canary started before the Admin canary completed' 'Overlapping Gateway canary'
    $result = Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $gatewayEvidence
    Assert-Decision $result 'hold' 'Gateway without accepted Admin canary'
    Assert-Failure $result 'requires accepted Admin' 'Gateway without accepted Admin canary'
    $badPolicy = Copy-Value $openPolicy; $badPolicy.minimumBenchmarkRuns = '5'
    $result = Invoke-Gate 'admin' $badPolicy $adminBenchmark $adminEvidence
    Assert-Decision $result 'hold' 'String policy run minimum'
    Assert-Failure $result 'minimum benchmark run count is missing or not finite' 'String policy run minimum'
    $badPolicy = Copy-Value $openPolicy; $badPolicy.requiredWorkloads.gateway = @('health-ready')
    $result = Invoke-Gate 'gateway' $badPolicy $gatewayBenchmark $gatewayEvidence $adminEvidence
    Assert-Decision $result 'hold' 'Health-only configured workload policy'
    Assert-Failure $result 'policy required workloads must match' 'Health-only configured workload policy'
    foreach ($target in 'policy', 'benchmark', 'environment', 'canary') {
        foreach ($type in 'string', 'boolean') {
            $candidatePolicy = Copy-Value $openPolicy
            $candidateBenchmark = Copy-Value $gatewayBenchmark
            $candidateEvidence = Copy-Value $gatewayEvidence
            $schema = if ($type -eq 'boolean') { $true } elseif ($target -eq 'benchmark') { '2' } else { '1' }
            switch ($target) {
                'policy' { $candidatePolicy.schemaVersion = $schema }
                'benchmark' { $candidateBenchmark.schemaVersion = $schema }
                'environment' { $candidateBenchmark.environment.schemaVersion = $schema }
                'canary' { $candidateEvidence.schemaVersion = $schema }
            }
            $result = Invoke-Gate 'gateway' $candidatePolicy $candidateBenchmark $candidateEvidence $adminEvidence
            Assert-Decision $result 'hold' "$type $target schema version"
            Assert-Failure $result 'schema version is missing or not finite' "$type $target schema version"
        }
    }

    Write-Host "Native promotion gate contract tests passed ($checks cases)."
}
finally {
    $cleanupPath = [IO.Path]::GetFullPath($temporaryRoot)
    $temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $cleanupPath.StartsWith($temporaryBase, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($cleanupPath) -notmatch '^conduit-native-promotion-[0-9a-f]{32}$') {
        throw 'Promotion test cleanup path is outside its owned temporary directory.'
    }
    Remove-Item -LiteralPath $cleanupPath -Recurse -Force
}
