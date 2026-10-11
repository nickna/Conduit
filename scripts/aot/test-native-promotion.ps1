[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '../..')
$evaluator = Join-Path $PSScriptRoot 'evaluate-native-promotion.ps1'
. (Join-Path $PSScriptRoot 'native-benchmark-test-fixtures.ps1')
. (Join-Path $PSScriptRoot 'native-benchmark-image-evidence.ps1')
. (Join-Path $PSScriptRoot 'native-benchmark-attempt-evidence.ps1')
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "conduit-native-promotion-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$checks = 0

function Copy-Value($Value) { return $Value | ConvertTo-Json -Depth 64 | ConvertFrom-Json -DateKind String }
function Write-Fixture([string] $Name, $Value) {
    $path = Join-Path $temporaryRoot "$Name.json"
    $Value | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $path
    return $path
}
function Invoke-Gate([string] $Service, $Policy, $Benchmark, $Evidence, $AdminEvidence = $null,
    [string]$PackagingDirectory = '', [switch]$WithoutPackaging, [string]$RawBenchmarkJson = '',
    [string]$ExistingBenchmarkPath = '', [switch]$WithoutAttempt,
    [scriptblock]$DuringFinalOciRead = $null, $MutationState = $null) {
    $benchmarkPath = if ([string]::IsNullOrEmpty($ExistingBenchmarkPath)) {
        Join-Path $temporaryRoot "benchmark-$([guid]::NewGuid().ToString('N')).json"
    } else { $ExistingBenchmarkPath }
    if ([string]::IsNullOrEmpty($ExistingBenchmarkPath)) {
        $canonicalService = $Service.ToLowerInvariant()
        $schema = $Benchmark.schemaVersion
        $integerTypes = @([sbyte], [byte], [int16], [uint16], [int32], [uint32], [int64], [uint64])
        $bindAttempt = -not $WithoutAttempt -and $null -ne $schema -and $schema.GetType() -in $integerTypes -and
            $schema -eq 4 -and $Benchmark.service -is [string] -and $Benchmark.service -ceq $canonicalService
        if ($bindAttempt) {
            $context = $null
            try {
                $context = Start-NativeBenchmarkAttempt -OutputPath $benchmarkPath -Service $canonicalService
                Complete-NativeBenchmarkAttempt -Context $context -Report (Copy-Value $Benchmark)
            }
            finally { if ($null -ne $context) { Close-NativeBenchmarkAttempt -Context $context } }
        }
        else {
            # Deliberate old schemas, wrong types and mismatched services retain
            # their original fixture bytes; publication must not repair them.
            $Benchmark | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $benchmarkPath
        }
    }
    $arguments = @{
        Service = $Service
        PolicyPath = Write-Fixture "policy-$([guid]::NewGuid().ToString('N'))" $Policy
        BenchmarkPath = $benchmarkPath
        CanaryEvidencePath = Write-Fixture "evidence-$([guid]::NewGuid().ToString('N'))" $Evidence
        ReportOnly = $true
    }
    if (-not $WithoutPackaging) {
        $arguments.NativePackagingDirectory = if ([string]::IsNullOrEmpty($PackagingDirectory)) {
            $script:packages[$Service.ToLowerInvariant()].artifact
        } else { $PackagingDirectory }
    }
    if (-not [string]::IsNullOrEmpty($RawBenchmarkJson)) {
        [IO.File]::WriteAllText($arguments.BenchmarkPath, $RawBenchmarkJson, [Text.UTF8Encoding]::new($false))
    }
    if ($null -ne $AdminEvidence) { $arguments.AdminCanaryEvidencePath = Write-Fixture "admin-$([guid]::NewGuid().ToString('N'))" $AdminEvidence }
    $breakpoint = $null
    try {
        if ($null -ne $DuringFinalOciRead) {
            $callback = $DuringFinalOciRead
            $state = $MutationState
            $action = {
                $state.readCalls++
                if ($state.readCalls -eq 2) {
                    & $callback $state
                    $state.mutated = $true
                }
            }.GetNewClosure()
            $breakpoint = Set-PSBreakpoint -Command 'Read-NativeBenchmarkImageEvidence' -Action $action
        }
        return ((& $evaluator @arguments) -join [Environment]::NewLine) | ConvertFrom-Json -DateKind String
    }
    finally { if ($null -ne $breakpoint) { Remove-PSBreakpoint -Id $breakpoint.Id } }
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
        candidateDigest = $script:packageEvidence[$Service].binding.requestedDigest
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
        image = if ($Native) { $script:packages[$Service].requestedImage } else { "registry.example.test/conduit-$Service@sha256:$([string]$Digest * 64)" }
        imageRevision = ('1' * 40)
        imageBytes = if ($Native) { 800 } else { 1000 }
        pullBytes = if ($Native) { $script:packageEvidence[$Service].pullBytes } else { 800 }
        coldReadinessMilliseconds = $(if ($Native) { 70 } else { 100 }) + $Offset
        idleMemoryBytes = $(if ($Native) { 700 } else { 1000 }) + $Offset
        steadyStateMemoryBytes = $(if ($Native) { 700 } else { 1000 }) + $Offset
        peakMemoryBytes = $(if ($Native) { 900 } else { 1200 }) + $Offset
        imageBinding = if ($Native) { $script:packageEvidence[$Service].binding } else { $null }
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
        schemaVersion = 4; generatedAt = '2020-01-01T00:00:00Z'; service = $Service; platform = 'linux-x64'
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
            $nativeSample = New-Variant $Service $true $NativeDigest $offset
            $nativeSample.nativeContainerId = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
            [ordered]@{
                pair = $pair
                order = if ($pair % 2 -eq 1) { @('jit', 'native-aot') } else { @('native-aot', 'jit') }
                jit = New-Variant $Service $false $JitDigest $offset
                native = $nativeSample
            }
        })
        nativeEvidence = $script:packageEvidence[$Service].identity
        nativePublish = $script:packageEvidence[$Service].nativePublish
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

function Read-PromotionAttemptJson([string] $Path) {
    return ,([IO.File]::ReadAllText($Path) | ConvertFrom-Json -AsHashtable -DateKind String)
}
function Write-PromotionAttemptJson([string] $Path, $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 64 -Compress), [Text.UTF8Encoding]::new($false))
}
function Get-PromotionAttemptHash([string] $Path) {
    return [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($Path)))
}
function Publish-PromotionAttempt([string] $Path, $Benchmark) {
    $context = $null
    try {
        $context = Start-NativeBenchmarkAttempt -OutputPath $Path -Service 'gateway'
        Complete-NativeBenchmarkAttempt -Context $context -Report (Copy-Value $Benchmark)
    }
    finally { if ($null -ne $context) { Close-NativeBenchmarkAttempt -Context $context } }
    return ,(Read-NativeBenchmarkAttemptEvidence -OutputPath $Path -Service 'gateway')
}

function Invoke-PromotionAttemptContracts {
    Assert-BenchmarkHold 'Historical schema three cannot qualify' 'schema version 4' { param($b) $b.schemaVersion = 3 }
    $withoutJournal = Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $gatewayEvidence $adminEvidence -WithoutAttempt
    Assert-Decision $withoutJournal 'hold' 'Schema four without collection journal'
    Assert-Failure $withoutJournal 'latest.*attempt|collection attempt' 'Schema four without collection journal'
    $wrongService = Copy-Value $gatewayBenchmark
    $wrongService.service = 'admin'
    $result = Invoke-Gate 'gateway' $openPolicy $wrongService $gatewayEvidence $adminEvidence
    Assert-Decision $result 'hold' 'Mismatched benchmark service remains unbound'
    Assert-Failure $result 'service does not match' 'Mismatched benchmark service remains unbound'

    $lineagePath = Join-Path $temporaryRoot ('attempt-lineage-' + [Guid]::NewGuid().ToString('N') + '.json')
    $first = Publish-PromotionAttempt $lineagePath $gatewayBenchmark
    $firstArchive = Join-Path (Join-Path ($lineagePath + '.attempts') $first.latest.attemptId) 'benchmark.json'
    $firstHash = Get-PromotionAttemptHash $lineagePath
    Assert-Decision (Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $gatewayEvidence $adminEvidence -ExistingBenchmarkPath $lineagePath) 'promote' 'Latest succeeded A qualifies with valid canary and OCI'
    $failed = $null
    try {
        $failed = Start-NativeBenchmarkAttempt -OutputPath $lineagePath -Service 'gateway'
        Set-NativeBenchmarkAttemptFailed -Context $failed -FailureCode 'collection-failed'
    }
    finally { if ($null -ne $failed) { Close-NativeBenchmarkAttempt -Context $failed } }
    if ((Get-PromotionAttemptHash $lineagePath) -cne $firstHash -or
        (Get-PromotionAttemptHash $firstArchive) -cne $firstHash) { throw 'Failed B changed successful A retained bytes.' }
    $result = Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $gatewayEvidence $adminEvidence -ExistingBenchmarkPath $lineagePath
    Assert-Decision $result 'hold' 'Failed B disqualifies unchanged successful A alias'
    Assert-Failure $result 'latest.*attempt|collection attempt' 'Failed B disqualifies unchanged successful A alias'

    $running = $null
    try {
        $running = Start-NativeBenchmarkAttempt -OutputPath $lineagePath -Service 'gateway'
        $result = Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $gatewayEvidence $adminEvidence -ExistingBenchmarkPath $lineagePath
        Assert-Decision $result 'hold' 'Held running attempt disqualifies previous success'
        Assert-Failure $result 'latest.*attempt|collection attempt' 'Held running attempt disqualifies previous success'
    }
    finally { if ($null -ne $running) { Close-NativeBenchmarkAttempt -Context $running } }
    $result = Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $gatewayEvidence $adminEvidence -ExistingBenchmarkPath $lineagePath
    Assert-Decision $result 'hold' 'Closed crashed-running attempt remains disqualified'
    Assert-Failure $result 'latest.*attempt|collection attempt' 'Closed crashed-running attempt remains disqualified'
    $second = Publish-PromotionAttempt $lineagePath $gatewayBenchmark
    if ($second.latest.attemptId -ceq $first.latest.attemptId -or
        (Get-PromotionAttemptHash $firstArchive) -cne $firstHash -or
        $second.reportSha256 -ceq $first.reportSha256) { throw 'Successful rerun reused A identity or changed its immutable archive.' }
    Assert-Decision (Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $gatewayEvidence $adminEvidence -ExistingBenchmarkPath $lineagePath) 'promote' 'Current successful B replaces older failures'

    $latestPath = Join-Path ($lineagePath + '.attempts') 'latest.json'
    $attemptPath = Join-Path (Join-Path ($lineagePath + '.attempts') $second.latest.attemptId) 'attempt.json'
    $archivePath = Join-Path (Join-Path ($lineagePath + '.attempts') $second.latest.attemptId) 'benchmark.json'
    $originalBytes = @{}
    foreach ($path in @($lineagePath, $latestPath, $attemptPath, $archivePath)) { $originalBytes[$path] = [IO.File]::ReadAllBytes($path) }
    foreach ($mutation in @('alias-bytes', 'archive-bytes', 'attempt-hash', 'attempt-id', 'latest-id', 'generated-at', 'collection-id', 'collection-time')) {
        try {
            switch ($mutation) {
                'alias-bytes' { [IO.File]::AppendAllText($lineagePath, "`n", [Text.UTF8Encoding]::new($false)) }
                'archive-bytes' { [IO.File]::AppendAllText($archivePath, "`n", [Text.UTF8Encoding]::new($false)) }
                'attempt-hash' {
                    $record = Read-PromotionAttemptJson $attemptPath
                    $record.reportSha256 = 'f' * 64
                    Write-PromotionAttemptJson $attemptPath $record
                }
                'attempt-id' {
                    $record = Read-PromotionAttemptJson $attemptPath
                    $record.attemptId = [Guid]::NewGuid().ToString('N')
                    Write-PromotionAttemptJson $attemptPath $record
                }
                'latest-id' {
                    $record = Read-PromotionAttemptJson $latestPath
                    $record.attemptId = $first.latest.attemptId
                    Write-PromotionAttemptJson $latestPath $record
                }
                { $_ -cin @('generated-at', 'collection-id', 'collection-time') } {
                    $report = Read-PromotionAttemptJson $lineagePath
                    switch ($mutation) {
                        'generated-at' { $report.generatedAt = '1999-01-01T00:00:00Z' }
                        'collection-id' { $report.collection.attemptId = [Guid]::NewGuid().ToString('N') }
                        'collection-time' { $report.collection.completedAt = '1999-01-01T00:00:00Z' }
                    }
                    # Keep both report hashes coherent so the ID/time join, rather
                    # than a trivial byte mismatch, must reject this corruption.
                    Write-PromotionAttemptJson $lineagePath $report
                    [IO.File]::WriteAllBytes($archivePath, [IO.File]::ReadAllBytes($lineagePath))
                    $record = Read-PromotionAttemptJson $attemptPath
                    $record.reportSha256 = Get-PromotionAttemptHash $lineagePath
                    Write-PromotionAttemptJson $attemptPath $record
                }
            }
            $result = Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $gatewayEvidence $adminEvidence -ExistingBenchmarkPath $lineagePath
            Assert-Decision $result 'hold' "Post-publication $mutation disqualifies latest collection"
            Assert-Failure $result 'latest.*attempt|collection attempt' "Post-publication $mutation disqualifies latest collection"
        }
        finally { foreach ($path in $originalBytes.Keys) { [IO.File]::WriteAllBytes($path, $originalBytes[$path]) } }
    }

    foreach ($mutation in @('latest-bytes', 'attempt-bytes', 'report-rehashed', 'new-failed', 'new-running')) {
        $latePath = Join-Path $temporaryRoot ('attempt-late-' + [Guid]::NewGuid().ToString('N') + '.json')
        $lateEvidence = Publish-PromotionAttempt $latePath $gatewayBenchmark
        $lateDirectory = Join-Path ($latePath + '.attempts') $lateEvidence.latest.attemptId
        $state = [ordered]@{
            readCalls = 0; mutated = $false; mutation = $mutation; outputPath = $latePath
            latestPath = Join-Path ($latePath + '.attempts') 'latest.json'
            attemptPath = Join-Path $lateDirectory 'attempt.json'; archivePath = Join-Path $lateDirectory 'benchmark.json'
            activeContext = $null
        }
        $mutate = {
            param($State)
            switch ($State.mutation) {
                'latest-bytes' { [IO.File]::AppendAllText($State.latestPath, "`n", [Text.UTF8Encoding]::new($false)) }
                'attempt-bytes' { [IO.File]::AppendAllText($State.attemptPath, "`n", [Text.UTF8Encoding]::new($false)) }
                'report-rehashed' {
                    $report = [IO.File]::ReadAllText($State.outputPath) | ConvertFrom-Json -AsHashtable -DateKind String
                    $report.methodology += ' Changed during final OCI replay.'
                    $json = ConvertTo-Json -InputObject $report -Depth 64 -Compress
                    [IO.File]::WriteAllText($State.outputPath, $json, [Text.UTF8Encoding]::new($false))
                    [IO.File]::WriteAllText($State.archivePath, $json, [Text.UTF8Encoding]::new($false))
                    $record = [IO.File]::ReadAllText($State.attemptPath) | ConvertFrom-Json -AsHashtable -DateKind String
                    $record.reportSha256 = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($State.outputPath)))
                    [IO.File]::WriteAllText($State.attemptPath, (ConvertTo-Json -InputObject $record -Depth 8 -Compress), [Text.UTF8Encoding]::new($false))
                }
                { $_ -cin @('new-failed', 'new-running') } {
                    $context = Start-NativeBenchmarkAttempt -OutputPath $State.outputPath -Service 'gateway'
                    if ($State.mutation -ceq 'new-running') { $State.activeContext = $context }
                    else {
                        try { Set-NativeBenchmarkAttemptFailed -Context $context -FailureCode 'collection-failed' }
                        finally { Close-NativeBenchmarkAttempt -Context $context }
                    }
                }
            }
        }
        try {
            $result = Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $gatewayEvidence $adminEvidence -ExistingBenchmarkPath $latePath -DuringFinalOciRead $mutate -MutationState $state
            if ($state.readCalls -ne 2 -or -not $state.mutated) { throw "Late $mutation did not occur during the real evaluator's second OCI read." }
            Assert-Decision $result 'hold' "Late $mutation changes current collection identity"
            Assert-Failure $result '^benchmark latest successful collection attempt changed or could not be reverified after evaluation$' "Late $mutation changes current collection identity"
        }
        finally { if ($null -ne $state.activeContext) { Close-NativeBenchmarkAttempt -Context $state.activeContext } }
    }
}

try {
    $script:packages = @{}
    $script:packageEvidence = @{}
    foreach ($service in @('admin', 'gateway')) {
        $script:packages[$service] = New-NativeBenchmarkPackagingFixture -ParentDirectory $temporaryRoot -Service $service -Revision ('1' * 40)
        $script:packageEvidence[$service] = Read-NativeBenchmarkImageEvidence -Directory $script:packages[$service].artifact -Service $service -NativeImage $script:packages[$service].requestedImage
        Assert-NativeBenchmarkFixtureIdentity $script:packageEvidence[$service].identity $script:packages[$service]
        Assert-NativeBenchmarkFixtureBinding $script:packageEvidence[$service].binding $script:packages[$service]
    }
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

    Assert-BenchmarkHold 'Health-only schema one' 'schema version 4' { param($b) $b.schemaVersion = 1; $b | Add-Member -NotePropertyName endpoint -NotePropertyValue '/health/ready' }
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

    # The arithmetic remains synthetic, but every native identity below comes
    # from a completed original-byte OCI package with independent role hashes.
    Assert-Decision (Invoke-Gate 'Gateway' $openPolicy $gatewayBenchmark $gatewayEvidence $adminEvidence) 'promote' 'Case-insensitive service retains canonical image identity'
    $upperHelper = @(Read-NativeBenchmarkImageEvidence -Directory $script:packages.admin.artifact -Service 'ADMIN' -NativeImage $script:packages.admin.requestedImage)
    if ($upperHelper.Count -ne 1 -or $upperHelper[0].identity.service -cne 'admin') { throw 'Uppercase helper did not return one canonical identity.' }
    Assert-BenchmarkHold 'Historical schema two cannot qualify' 'schema version 4' { param($b) $b.schemaVersion = 2 }
    $result = Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $gatewayEvidence $adminEvidence -WithoutPackaging
    Assert-Decision $result 'hold' 'Packaging directory is required'
    Assert-Failure $result 'requires retained packaging evidence' 'Packaging directory is required'
    Assert-BenchmarkHold 'Missing native top identity' 'native image binding could not be verified' { param($b) $b.nativeEvidence = $null }
    Assert-BenchmarkHold 'Extra native top identity key' 'native image binding could not be verified' { param($b) $b.nativeEvidence | Add-Member -NotePropertyName unverified -NotePropertyValue $true }
    Assert-BenchmarkHold 'Extra image binding key' 'native image binding differs' { param($b) $b.native.imageBinding | Add-Member -NotePropertyName unverified -NotePropertyValue $true }
    Assert-BenchmarkHold 'Missing image binding key' 'native image binding differs' { param($b) $b.samples[2].native.imageBinding.PSObject.Properties.Remove('buildId') }
    Assert-BenchmarkHold 'String binding schema' 'native image binding differs' { param($b) $b.samples[2].native.imageBinding.schemaVersion = '1' }
    Assert-BenchmarkHold 'Boolean binding schema' 'native image binding differs' { param($b) $b.samples[2].native.imageBinding.schemaVersion = $true }
    Assert-BenchmarkHold 'Raw image singleton array' 'different image or source revision' { param($b) $b.samples[1].native.image = @($b.samples[1].native.image) }
    Assert-BenchmarkHold 'Raw revision singleton array' 'different image or source revision' { param($b) $b.samples[1].native.imageRevision = @($b.samples[1].native.imageRevision) }
    Assert-BenchmarkHold 'Missing cold-start container identity' 'native container binding is missing or invalid' { param($b) $b.samples[0].native.PSObject.Properties.Remove('nativeContainerId') }
    Assert-BenchmarkHold 'Array cold-start container identity' 'native container binding is missing or invalid' { param($b) $b.samples[0].native.nativeContainerId = @($b.samples[0].native.nativeContainerId) }
    Assert-BenchmarkHold 'Uppercase cold-start container identity' 'native container binding is missing or invalid' { param($b) $b.samples[0].native.nativeContainerId = 'A' * 64 }
    Assert-BenchmarkHold 'Repeated recorded cold-start container' 'repeats a recorded cold-start container' { param($b) $b.samples[4].native.nativeContainerId = $b.samples[0].native.nativeContainerId }
    for ($index = 0; $index -lt 5; $index++) {
        $sampleIndex = $index
        Assert-BenchmarkHold "Forged native binding in pair $($index + 1)" 'native image binding differs' {
            param($b) $b.samples[$sampleIndex].native.imageBinding.packagingEvidence.sha256 = 'f' * 64
        }
    }
    foreach ($metric in @('publishDurationMilliseconds', 'applicationPublishDurationMilliseconds', 'healthProbePublishDurationMilliseconds', 'executableBytes', 'runtimeArtifactBytes')) {
        $publishMetric = $metric
        Assert-BenchmarkHold "Forged sealed publish metric $metric" 'native publish binding differs' { param($b) $b.nativePublish.$publishMetric += 1 }
    }
    Assert-BenchmarkHold 'Publish binding belongs to another build' 'native publish binding differs' { param($b) $b.nativePublish.imageBinding.buildId = 'f' * 32 }
    Assert-BenchmarkHold 'Forged verified layer pull total' 'pull size differs from verified selected layers' {
        param($b)
        $b.native.pullBytes += 1
        foreach ($sample in $b.samples) { $sample.native.pullBytes += 1 }
        Set-Comparisons $b
    }
    foreach ($role in @('buildId', 'source', 'rootDescriptor', 'selectedManifestDescriptor', 'configDigest', 'runtimeFilesSha256', 'manifestHashes', 'application', 'healthProbe')) {
        $identityRole = $role
        Assert-BenchmarkHold "Forged independently sealed identity $role" 'native image binding could not be verified' {
            param($b)
            switch ($identityRole) {
                'source' { $b.nativeEvidence.source.revision = 'f' * 40 }
                'rootDescriptor' { $b.nativeEvidence.rootDescriptor.digest = $b.nativeEvidence.configDigest }
                'selectedManifestDescriptor' { $b.nativeEvidence.selectedManifestDescriptor.digest = $b.nativeEvidence.rootDescriptor.digest }
                'manifestHashes' { $b.nativeEvidence.manifestHashes.'application.json' = 'f' * 64 }
                'application' { $b.nativeEvidence.application.executable.sha256 = 'f' * 64 }
                'healthProbe' { $b.nativeEvidence.healthProbe.toolchain.ilCompiler.sha256 = 'f' * 64 }
                'buildId' { $b.nativeEvidence.buildId = 'f' * 32 }
                'configDigest' { $b.nativeEvidence.configDigest = $b.nativeEvidence.rootDescriptor.digest }
                'runtimeFilesSha256' { $b.nativeEvidence.runtimeFilesSha256 = 'f' * 64 }
            }
        }
    }
    foreach ($role in @('config', 'manifest', 'wrapper')) {
        $candidateEvidence = Copy-Value $gatewayEvidence
        $candidateEvidence.candidateDigest = switch ($role) {
            'config' { $script:packages.gateway.oci.configDigest }
            'manifest' { $script:packages.gateway.oci.selectedDescriptor.digest }
            'wrapper' { 'sha256:' + $script:packages.gateway.oci.wrapperSha256 }
        }
        $result = Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $candidateEvidence $adminEvidence
        Assert-Decision $result 'hold' "Requested index cannot be substituted by $role"
        Assert-Failure $result 'binding digest does not match canary evidence' "Requested index cannot be substituted by $role"
    }
    $raw = ConvertTo-Json -InputObject $gatewayBenchmark -Depth 64 -Compress
    $duplicate = [Text.RegularExpressions.Regex]::new('"nativeContainerId"\s*:').Replace($raw, '"nativeContainerId":"' + ('f' * 64) + '","nativeContainerId":', 1)
    if ($duplicate -ceq $raw) { throw 'Duplicate JSON fixture did not contain a real duplicate native identity key.' }
    $result = Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $gatewayEvidence $adminEvidence -RawBenchmarkJson $duplicate
    Assert-Decision $result 'hold' 'Actual duplicate raw native identity JSON'
    Assert-Failure $result 'benchmark document is malformed' 'Actual duplicate raw native identity JSON'

    $otherBuild = New-NativeBenchmarkPackagingFixture -ParentDirectory $temporaryRoot -Service 'gateway' -Revision ('1' * 40)
    if ($otherBuild.buildId -ceq $script:packages.gateway.buildId -or $otherBuild.runtimeFilesSha256 -cne $script:packages.gateway.runtimeFilesSha256 -or
        $otherBuild.source.revision -cne $script:packages.gateway.source.revision -or $otherBuild.metrics.executableBytes -ne $script:packages.gateway.metrics.executableBytes) {
        throw 'Cross-build fixture must have identical source/payload/metrics and a distinct sealed build.'
    }
    $result = Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $gatewayEvidence $adminEvidence -PackagingDirectory $otherBuild.artifact
    Assert-Decision $result 'hold' 'Different build with same source and payload cannot replace package'
    Assert-Failure $result 'native image binding could not be verified' 'Different build with same source and payload cannot replace package'
    $otherImageEvidence = Read-NativeBenchmarkImageEvidence -Directory $otherBuild.artifact -Service 'gateway' -NativeImage $otherBuild.requestedImage
    $crossBuildBenchmark = Copy-Value $gatewayBenchmark
    $crossBuildBenchmark.nativeEvidence = Copy-Value $otherImageEvidence.identity
    $crossBuildBenchmark.nativePublish = Copy-Value $otherImageEvidence.nativePublish
    foreach ($variant in @($crossBuildBenchmark.native) + @($crossBuildBenchmark.samples | ForEach-Object { $_.native })) {
        $variant.image = $otherBuild.requestedImage
        $variant.imageBinding = Copy-Value $otherImageEvidence.binding
    }
    $crossBuildBenchmark.samples[2].native.imageBinding = Copy-Value $script:packageEvidence.gateway.binding
    $otherCanary = Copy-Value $gatewayEvidence; $otherCanary.candidateDigest = $otherImageEvidence.binding.requestedDigest
    $result = Invoke-Gate 'gateway' $openPolicy $crossBuildBenchmark $otherCanary $adminEvidence -PackagingDirectory $otherBuild.artifact
    Assert-Decision $result 'hold' 'One same-source cold sample belongs to another build'
    Assert-Failure $result 'sample 3 .*image binding differs' 'One same-source cold sample belongs to another build'
    foreach ($badSource in @('dirty', 'unavailable', 'hold')) {
        $options = @{ ParentDirectory = $temporaryRoot; Service = 'gateway'; Revision = ('1' * 40) }
        switch ($badSource) {
            'dirty' { $options.SourceDirty = $true }
            'unavailable' { $options.UnavailableSource = $true }
            'hold' { $options.Scenario = 'oci-hold-no-descriptors' }
        }
        $fixture = New-NativeBenchmarkPackagingFixture @options
        $badBenchmark = Copy-Value $gatewayBenchmark
        foreach ($variant in @($badBenchmark.native) + @($badBenchmark.samples | ForEach-Object { $_.native })) { $variant.image = $fixture.requestedImage }
        $badCanary = Copy-Value $gatewayEvidence; $badCanary.candidateDigest = $fixture.requestedImage.Substring($fixture.requestedImage.LastIndexOf('@') + 1)
        $result = Invoke-Gate 'gateway' $openPolicy $badBenchmark $badCanary $adminEvidence -PackagingDirectory $fixture.artifact
        Assert-Decision $result 'hold' "$badSource retained package cannot qualify"
        Assert-Failure $result 'native image binding could not be verified' "$badSource retained package cannot qualify"
    }
    foreach ($mutation in @('package', 'manifest', 'payload', 'oci')) {
        $clone = Join-Path $temporaryRoot ([Guid]::NewGuid().ToString('N'))
        Copy-NativeBenchmarkPackagingFixture $script:packages.gateway $clone
        $path = switch ($mutation) {
            'package' { Join-Path $clone 'packaging-evidence.json' }
            'manifest' { Join-Path $clone 'extracted/conduit-evidence/health-probe.json' }
            'payload' { Join-Path $clone ('extracted/app/' + $script:packages.gateway.main) }
            'oci' { Join-Path $clone 'image-save.tar' }
        }
        [IO.File]::AppendAllText($path, ' ')
        $result = Invoke-Gate 'gateway' $openPolicy $gatewayBenchmark $gatewayEvidence $adminEvidence -PackagingDirectory $clone
        Assert-Decision $result 'hold' "Mutated retained $mutation is rejected"
        Assert-Failure $result 'native image binding could not be verified' "Mutated retained $mutation is rejected"
    }
    $selectedFixture = New-NativeBenchmarkPackagingFixture -ParentDirectory $temporaryRoot -Service 'gateway' -Revision ('1' * 40) -SelectedManifestRequest
    $originalFixture = $script:packages.gateway; $originalEvidence = $script:packageEvidence.gateway
    try {
        $script:packages.gateway = $selectedFixture
        $script:packageEvidence.gateway = Read-NativeBenchmarkImageEvidence -Directory $selectedFixture.artifact -Service 'gateway' -NativeImage $selectedFixture.requestedImage
        Assert-NativeBenchmarkFixtureBinding $script:packageEvidence.gateway.binding $selectedFixture
        Assert-NativeBenchmarkFixtureIdentity $script:packageEvidence.gateway.identity $selectedFixture
        $selectedBenchmark = New-Benchmark 'gateway' 'c' 'd'
        $selectedCanary = New-Evidence 'gateway' 'c' 'd'; $selectedCanary.startedAt = $gatewayEvidence.startedAt; $selectedCanary.completedAt = $gatewayEvidence.completedAt
        Assert-Decision (Invoke-Gate 'gateway' $openPolicy $selectedBenchmark $selectedCanary $adminEvidence) 'promote' 'Selected manifest candidate binds to distinct observed local index'
    }
    finally { $script:packages.gateway = $originalFixture; $script:packageEvidence.gateway = $originalEvidence }

    Invoke-PromotionAttemptContracts

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
