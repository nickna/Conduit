[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '../..')
$evaluator = Join-Path $PSScriptRoot 'evaluate-native-promotion.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "conduit-native-promotion-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

function Copy-Value($Value) {
    return $Value | ConvertTo-Json -Depth 20 | ConvertFrom-Json
}

function Write-Fixture([string] $Name, $Value) {
    $path = Join-Path $temporaryRoot "$Name.json"
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path
    return $path
}

function Invoke-Gate(
    [string] $Service,
    $Policy,
    $Benchmark,
    $Evidence,
    $AdminEvidence = $null
) {
    $arguments = @{
        Service = $Service
        PolicyPath = Write-Fixture "$Service-policy-$([guid]::NewGuid().ToString('N'))" $Policy
        BenchmarkPath = Write-Fixture "$Service-benchmark-$([guid]::NewGuid().ToString('N'))" $Benchmark
        CanaryEvidencePath = Write-Fixture "$Service-evidence-$([guid]::NewGuid().ToString('N'))" $Evidence
        ReportOnly = $true
    }
    if ($null -ne $AdminEvidence) {
        $arguments.AdminCanaryEvidencePath = Write-Fixture "admin-evidence-$([guid]::NewGuid().ToString('N'))" $AdminEvidence
    }

    $json = & $evaluator @arguments
    return ($json -join "`n") | ConvertFrom-Json
}

function Assert-Decision($Result, [string] $Expected, [string] $Description) {
    if ($Result.decision -ne $Expected) {
        throw "$Description expected '$Expected' but received '$($Result.decision)': $($Result.failures -join '; ')"
    }
}

function Assert-Failure($Result, [string] $Pattern, [string] $Description) {
    if (-not (@($Result.failures) -match $Pattern)) {
        throw "$Description did not report '$Pattern': $($Result.failures -join '; ')"
    }
}

function New-Evidence([string] $Service, [char] $NativeDigestCharacter, [char] $JitDigestCharacter) {
    return [ordered]@{
        schemaVersion = 1
        service = $Service
        candidateDigest = "sha256:$([string]$NativeDigestCharacter * 64)"
        correspondingJitDigest = "sha256:$([string]$JitDigestCharacter * 64)"
        startedAt = '2020-01-01T00:00:00Z'
        completedAt = '2020-01-08T00:00:00Z'
        soakHours = 168
        healthyWindows = 24
        errorRate = 0
        contractsMatchedJit = $true
        backingServicesMatchedJit = $true
        fullFeatureMatrixPassed = $true
        dashboardAndAlertsVerified = $true
        automaticRollbackTested = $true
        manualRollbackTested = $true
        criticalVulnerabilitiesResolved = $true
        sbomAndProvenanceVerified = $true
        dashboardUrl = 'https://monitoring.example.test/dashboard'
        alertsUrl = 'https://monitoring.example.test/alerts'
    }
}

function New-Benchmark([string] $Service, [char] $NativeDigestCharacter, [char] $JitDigestCharacter) {
    $publishService = if ($Service -eq 'admin') { 'Admin' } else { 'Gateway' }
    return [ordered]@{
        schemaVersion = 1
        generatedAt = '2020-01-01T00:00:00Z'
        service = $Service
        platform = 'linux-x64'
        endpoint = '/health/ready'
        methodology = 'Synthetic promotion-gate contract fixture.'
        jit = [ordered]@{
            runtime = 'jit'
            image = "registry.example.test/conduit-$Service@sha256:$([string]$JitDigestCharacter * 64)"
            imageBytes = 1000
            pullBytes = 800
            coldReadinessMilliseconds = 100
            idleMemoryBytes = 1000
            steadyStateMemoryBytes = 1000
            peakMemoryBytes = 1200
            requests = 1000
            concurrency = 20
            failures = 0
            throughputPerSecond = 100
            p50Milliseconds = 10
            p95Milliseconds = 20
            p99Milliseconds = 30
        }
        native = [ordered]@{
            runtime = 'native-aot'
            image = "registry.example.test/conduit-$Service-native@sha256:$([string]$NativeDigestCharacter * 64)"
            imageBytes = 800
            pullBytes = 600
            coldReadinessMilliseconds = 70
            idleMemoryBytes = 700
            steadyStateMemoryBytes = 700
            peakMemoryBytes = 900
            requests = 1000
            concurrency = 20
            failures = 0
            throughputPerSecond = 105
            p50Milliseconds = 9
            p95Milliseconds = 19
            p99Milliseconds = 31
        }
        nativePublish = [ordered]@{
            service = $publishService
            rid = 'linux-x64'
            publishDurationMilliseconds = 10000
            executableBytes = 50000000
            runtimeArtifactBytes = 60000000
        }
        comparison = [ordered]@{
            imageSizeReductionPercent = 20
            pullSizeReductionPercent = 25
            coldStartReductionPercent = 30
            idleMemoryReductionPercent = 30
            steadyStateMemoryReductionPercent = 30
            peakMemoryReductionPercent = 25
            throughputImprovementPercent = 5
            p50LatencyRegressionPercent = -10
            p95LatencyRegressionPercent = -5
            p99LatencyRegressionPercent = 3.333
        }
    }
}

try {
    $blockedPolicy = Get-Content -LiteralPath (Join-Path $repoRoot 'deploy/native-canary/promotion-policy.json') -Raw | ConvertFrom-Json
    $openPolicy = Copy-Value $blockedPolicy
    $openPolicy.status = 'open'
    $openPolicy.blockedBy = @()

    $adminEvidence = New-Evidence 'admin' 'a' 'b'
    $adminBenchmark = New-Benchmark 'admin' 'a' 'b'

    $result = Invoke-Gate 'admin' $openPolicy $adminBenchmark $adminEvidence
    Assert-Decision $result 'promote' 'Complete Admin evidence'

    $result = Invoke-Gate 'admin' $blockedPolicy $adminBenchmark $adminEvidence
    Assert-Decision $result 'hold' 'Repository policy blocker'
    Assert-Failure $result '^promotion blocked:' 'Repository policy blocker'

    $incompleteBenchmark = Copy-Value $adminBenchmark
    $incompleteBenchmark.native.PSObject.Properties.Remove('pullBytes')
    $result = Invoke-Gate 'admin' $openPolicy $incompleteBenchmark $adminEvidence
    Assert-Decision $result 'hold' 'Incomplete benchmark'
    Assert-Failure $result 'native compressed pull size is missing' 'Incomplete benchmark'

    $inconsistentBenchmark = Copy-Value $adminBenchmark
    $inconsistentBenchmark.native.imageBytes = 900
    $result = Invoke-Gate 'admin' $openPolicy $inconsistentBenchmark $adminEvidence
    Assert-Decision $result 'hold' 'Inconsistent benchmark comparison'
    Assert-Failure $result 'image-size comparison does not match' 'Inconsistent benchmark comparison'

    $wrongDigestBenchmark = Copy-Value $adminBenchmark
    $wrongDigestBenchmark.native.image = "registry.example.test/conduit-admin-native@sha256:$('e' * 64)"
    $result = Invoke-Gate 'admin' $openPolicy $wrongDigestBenchmark $adminEvidence
    Assert-Decision $result 'hold' 'Mismatched image digest'
    Assert-Failure $result 'native benchmark image digest does not match' 'Mismatched image digest'

    $wrongTypesEvidence = Copy-Value $adminEvidence
    $wrongTypesEvidence.contractsMatchedJit = 'true'
    $wrongTypesEvidence.errorRate = '0'
    $result = Invoke-Gate 'admin' $openPolicy $adminBenchmark $wrongTypesEvidence
    Assert-Decision $result 'hold' 'String-typed evidence'
    Assert-Failure $result 'JIT contract parity was not verified' 'String-typed evidence'
    Assert-Failure $result 'error rate is missing or not finite' 'String-typed evidence'

    $gatewayEvidence = New-Evidence 'gateway' 'c' 'd'
    $gatewayEvidence.startedAt = '2020-01-08T00:00:00Z'
    $gatewayEvidence.completedAt = '2020-01-15T00:00:00Z'
    $gatewayBenchmark = New-Benchmark 'gateway' 'c' 'd'
    $result = Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $gatewayEvidence $adminEvidence
    Assert-Decision $result 'promote' 'Ordered Gateway evidence'

    $overlappingGatewayEvidence = Copy-Value $gatewayEvidence
    $overlappingGatewayEvidence.startedAt = '2020-01-07T23:59:59Z'
    $result = Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $overlappingGatewayEvidence $adminEvidence
    Assert-Decision $result 'hold' 'Overlapping Gateway canary'
    Assert-Failure $result 'Gateway canary started before the Admin canary completed' 'Overlapping Gateway canary'

    Write-Host 'Native promotion gate contract tests passed.'
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}
