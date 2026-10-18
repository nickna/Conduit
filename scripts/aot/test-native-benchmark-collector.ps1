#!/usr/bin/env pwsh
[CmdletBinding()]
param([switch] $DiagnosticSchemaOnly, [switch] $StrictCallerOnly, [switch] $FailedWorkloadOnly,
    [switch] $NetworkBindingOnly, [switch] $DockerNetworkOnly)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$collector = Join-Path $repositoryRoot 'scripts/aot/measure-native-container.ps1'
. (Join-Path $PSScriptRoot 'native-benchmark-test-fixtures.ps1')
. (Join-Path $PSScriptRoot 'native-benchmark-image-evidence.ps1')
. (Join-Path $PSScriptRoot 'native-benchmark-attempt-evidence.ps1')
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('conduit-collector-contract-' + [Guid]::NewGuid().ToString('N'))))
$environmentNames = @('CONDUIT_COLLECTOR_TEST_ROOT', 'CONDUIT_COLLECTOR_TEST_SCENARIO', 'CONDUIT_COLLECTOR_TEST_PWSH',
    'CONDUIT_NATIVE_BENCHMARK_API_KEY', 'CONDUIT_CORE_RATE_LIMIT_MAX_REQUESTS', 'CONDUIT_CORE_RATE_LIMIT_WINDOW_SECONDS',
    'CONDUIT_COLLECTOR_HELPER_VALUE')
$oldEnvironment = @{}
foreach ($name in $environmentNames) { $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
$privateKey = 'private-collector-key-' + [Guid]::NewGuid().ToString('N')
$privateDatabasePassword = 'private-database-' + [Guid]::NewGuid().ToString('N')
$privateRedisPassword = 'private-redis-' + [Guid]::NewGuid().ToString('N')
$cases = 0
$proofFailure = $null
$cleanupFailures = [Collections.Generic.List[string]]::new()
$ownedReadinessServers = [Collections.Generic.List[object]]::new()

function Assert-Contract([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Read-Records([string] $Path) {
    if (-not [IO.Directory]::Exists($Path)) { return ,@() }
    return ,@(Get-ChildItem -LiteralPath $Path -File | Sort-Object Name | ForEach-Object {
        [IO.File]::ReadAllText($_.FullName) | ConvertFrom-Json -AsHashtable
    })
}

function Read-AttemptContractJson([string] $Path) {
    return ,([IO.File]::ReadAllText($Path) | ConvertFrom-Json -AsHashtable -DateKind String)
}

function Get-AttemptContractHash([string] $Path) {
    return [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($Path)))
}

function Assert-AttemptContractPrivacy([string] $OutputPath) {
    foreach ($file in Get-ChildItem -LiteralPath ($OutputPath + '.attempts') -File -Recurse -Filter '*.json') {
        $text = [IO.File]::ReadAllText($file.FullName)
        foreach ($secret in @($privateKey, $privateDatabasePassword, $privateRedisPassword, 'private-child-diagnostic', 'private-cleanup-sentinel')) {
            Assert-Contract (-not $text.Contains($secret)) 'Benchmark attempt evidence retained private configuration.'
        }
    }
}

function Assert-DiagnosticContractRejection([scriptblock] $Action, [string] $ExpectedMessage) {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_ }
    Assert-Contract ($null -ne $failure -and $failure.Exception.Message -ceq $ExpectedMessage -and
        -not (($failure | Out-String).Contains($privateKey))) 'Diagnostic rejection exposed private input or lost its fixed message.'
}

function New-DiagnosticContractInput {
    return ,([ordered]@{ phase = 'unknown'; reason = 'unknown'; runtime = 'unknown'; sampleKind = 'none'; pair = $null;
        ownedContainerCleanup = @{ status = 'not-attempted'; reason = 'none' };
        privateFileCleanup = @{ status = 'not-attempted'; reason = 'none' }; workloadAggregate = $null })
}

function Invoke-NetworkBindingContracts {
    # These contracts execute the real collector helpers with an in-memory
    # Docker boundary. They never start Docker, readiness listeners or probes.
    $tokens = $null; $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($collector, [ref]$tokens, [ref]$parseErrors)
    Assert-Contract ($parseErrors.Count -eq 0) 'Collector syntax is invalid.'
    foreach ($functionName in @('Read-BenchmarkFixtureNetwork', 'Assert-BenchmarkFixtureNetworkAttachment')) {
        $definition = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $functionName
        }.GetNewClosure(), $true)
        Assert-Contract ($null -ne $definition) 'Collector network helper is missing.'
        Invoke-Expression $definition.Extent.Text
    }
    $networkName = 'owned-fixture-bridge'
    $networkId = 'a' * 64
    $networkExit = 0; $networkThrows = $false
    $networkText = ConvertTo-Json -InputObject @{ Id = $networkId; Name = $networkName; Driver = 'bridge'; Scope = 'local' } -Compress
    $networkCalls = [Collections.Generic.List[object]]::new()
    function Invoke-Docker([string[]] $Arguments) {
        $networkCalls.Add(@($Arguments))
        if ($networkThrows) { throw ('private-child-diagnostic:' + $privateKey) }
        return @{ ExitCode = $networkExit; Stdout = $networkText; Stderr = 'private-child-diagnostic:' + $privateKey }
    }
    $pin = Read-BenchmarkFixtureNetwork $networkName
    Assert-Contract ($pin.Id -ceq $networkId -and $pin.Name -ceq $networkName -and $pin.Count -eq 2 -and
        $networkCalls.Count -eq 1 -and ($networkCalls[0] -join '|') -ceq ('network|inspect|--format|{{json .}}|' + $networkName)) 'Network inspection did not pin the exact existing bridge.'
    $script:cases++
    foreach ($invalidName in @('', ' ', '-leading', 'with/slash', 'with:colon', 'nonascii-雪', ('x' * 129), ($privateKey + "`n"))) {
        $networkCalls.Clear()
        Assert-DiagnosticContractRejection { Read-BenchmarkFixtureNetwork $invalidName } 'Benchmark fixture network binding failed.'
        Assert-Contract ($networkCalls.Count -eq 0) 'Invalid network name reached Docker.'
        $script:cases++
    }
    $networkName = 'x' * 128
    $networkText = ConvertTo-Json -InputObject @{ Id = $networkId; Name = $networkName; Driver = 'bridge'; Scope = 'local' } -Compress
    Assert-Contract ((Read-BenchmarkFixtureNetwork $networkName).Name -ceq $networkName) 'Maximum bounded ASCII network name was rejected.'
    $script:cases++
    $networkName = $pin.Name
    foreach ($mutation in @(
        { param($d) $d.Id = 'A' * 64 }, { param($d) $d.Id = 'a' * 63 }, { param($d) $d.Id = 42 },
        { param($d) $d.Name = 'foreign-fixture' }, { param($d) $d.Name = @($networkName) },
        { param($d) $d.Driver = 'host' }, { param($d) $d.Driver = 'null' }, { param($d) $d.Driver = 'overlay' },
        { param($d) $d.Scope = 'swarm' }, { param($d) $d.Remove('Scope') | Out-Null })) {
        $data = @{ Id = $networkId; Name = $networkName; Driver = 'bridge'; Scope = 'local' }
        & $mutation $data
        $networkText = ConvertTo-Json -InputObject $data -Compress
        Assert-DiagnosticContractRejection { Read-BenchmarkFixtureNetwork $networkName } 'Benchmark fixture network binding failed.'
        $script:cases++
    }
    foreach ($invalidJson in @('[]', 'null', '{', ('{"Id":"' + $networkId + '","Id":"' + $networkId + '","Name":"' + $networkName + '","Driver":"bridge","Scope":"local"}'),
        ('{"Id":"' + $networkId + '","Name":"' + $networkName + '","Driver":"bridge","Scope":"local","private":{"key":1,"key":2}}'))) {
        $networkText = $invalidJson
        Assert-DiagnosticContractRejection { Read-BenchmarkFixtureNetwork $networkName } 'Benchmark fixture network binding failed.'
        $script:cases++
    }
    $networkExit = 17
    Assert-DiagnosticContractRejection { Read-BenchmarkFixtureNetwork $networkName } 'Benchmark fixture network binding failed.'
    $networkThrows = $true
    Assert-DiagnosticContractRejection { Read-BenchmarkFixtureNetwork $networkName } 'Benchmark fixture network binding failed.'
    $script:cases += 2
    $valid = @{ HostConfig = @{ NetworkMode = $networkId }; State = @{ Running = $true };
        NetworkSettings = @{ Networks = @{ $networkName = @{ NetworkID = $networkId; IPAddress = '172.29.0.8' } } } }
    $validText = ConvertTo-Json -InputObject $valid -Depth 8 -Compress
    Assert-Contract (@(Assert-BenchmarkFixtureNetworkAttachment $valid $pin).Count -eq 0) 'Valid actual bridge attachment emitted output.'
    Assert-Contract ((ConvertTo-Json -InputObject $valid -Depth 8 -Compress) -ceq $validText) 'Network validation mutated Docker inspection data.'
    Assert-Contract (@(Assert-BenchmarkFixtureNetworkAttachment $null $null).Count -eq 0) 'Default transport unexpectedly required network metadata.'
    $script:cases += 2
    foreach ($mutation in @(
        { param($d) $d.HostConfig.NetworkMode = $networkName }, { param($d) $d.HostConfig.NetworkMode = 'b' * 64 },
        { param($d) $d.Remove('HostConfig') | Out-Null }, { param($d) $d.State.Running = $false }, { param($d) $d.State.Running = 'true' },
        { param($d) $d.NetworkSettings.Networks[$networkName].NetworkID = 'b' * 64 },
        { param($d) $d.NetworkSettings.Networks['foreign-network'] = $d.NetworkSettings.Networks[$networkName]; $d.NetworkSettings.Networks.Remove($networkName) | Out-Null },
        { param($d) $d.NetworkSettings.Networks['second-network'] = @{ NetworkID = 'b' * 64; IPAddress = '172.30.0.8' } },
        { param($d) $d.NetworkSettings.Networks = @{} })) {
        $data = Read-NativeImageVerificationJson $validText
        & $mutation $data
        Assert-DiagnosticContractRejection { Assert-BenchmarkFixtureNetworkAttachment $data $pin } 'Benchmark fixture network binding failed.'
        $script:cases++
    }
    foreach ($address in @('', '::1', '172.29.0.256', '172.29.0', '172.029.0.8', '0.0.0.0', '127.0.0.1', '224.0.0.1', '255.255.255.255', 42, $null, ($privateKey + "`n"))) {
        $data = Read-NativeImageVerificationJson $validText
        $data.NetworkSettings.Networks[$networkName].IPAddress = $address
        Assert-DiagnosticContractRejection { Assert-BenchmarkFixtureNetworkAttachment $data $pin } 'Benchmark fixture network binding failed.'
        $script:cases++
    }
}

function Invoke-FailedWorkloadAggregateContracts {
    # Execute the collector's real validators without launching a workload.
    $tokens = $null; $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($collector, [ref]$tokens, [ref]$parseErrors)
    Assert-Contract ($parseErrors.Count -eq 0) 'Collector syntax is invalid.'
    foreach ($functionName in @('Get-JsonObjectMap', 'Read-JsonText', 'Read-JsonInteger', 'Get-ResultNumber', 'Test-WorkloadResult', 'Get-FailedWorkloadAggregate')) {
        $definition = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $functionName
        }.GetNewClosure(), $true)
        Assert-Contract ($null -ne $definition) ('Collector helper is missing: ' + $functionName)
        Invoke-Expression $definition.Extent.Text
    }
    foreach ($id in @('admin-setting-read', 'gateway-chat-nonstream', 'gateway-chat-sse')) {
        $Service = if ($id -ceq 'admin-setting-read') { 'admin' } else { 'gateway' }
        $configuration = [pscustomobject]@{ Id = $id; Data = @{ requests = 4; warmupRequests = 2; concurrency = 2; timeoutSeconds = 5 } }
        $report = [ordered]@{
            schemaVersion = 2; service = $Service; generatedAt = '2026-10-17T00:00:00.0000000+00:00'
            workloads = @([ordered]@{
                id = $id; method = $(if ($Service -ceq 'admin') { 'GET' } else { 'POST' })
                path = $(if ($Service -ceq 'admin') { '/v1/admin/global-settings/by-key/{key}' } else { '/v1/chat/completions' })
                authenticated = $true; inputFingerprint = 'c' * 64
                requested = 4; executed = 4; completed = 3; failures = 1
                warmupRequests = 2; concurrency = 2; timeoutSeconds = 5
                elapsedMilliseconds = 100.25; throughputPerSecond = 3000 / 100.25
                p50Milliseconds = 1.125; p95Milliseconds = 2.25; p99Milliseconds = 3.5
            })
        }
        foreach ($completed in @(0, 3, 4)) {
            $report.workloads[0].completed = $completed
            $report.workloads[0].failures = 4 - $completed
            $report.workloads[0].throughputPerSecond = $completed * 1000 / 100.25
            $stdout = $report | ConvertTo-Json -Depth 8 -Compress
            $aggregate = Get-FailedWorkloadAggregate $stdout $configuration
            Assert-Contract ($null -ne $aggregate -and $aggregate.Count -eq 9 -and $aggregate.phase -ceq 'measured' -and
                $aggregate.id -ceq $id -and $aggregate.requested -eq 4 -and $aggregate.executed -eq 4 -and
                $aggregate.completed -eq $completed -and $aggregate.failures -eq (4 - $completed) -and
                $aggregate.warmupRequests -eq 2) 'A valid measured failure projection lost its counts or invented warm-up completion.'
            $successFailure = $null
            try { $unusedSuccess = Test-WorkloadResult ($stdout | ConvertFrom-Json) $configuration }
            catch { $successFailure = $_.Exception.Message }
            Assert-Contract (($completed -eq 4 -and $null -eq $successFailure) -or
                ($completed -ne 4 -and $successFailure -ceq 'Workload report is invalid.')) 'Failure evidence relaxed the strict success gate.'
            $script:cases++
        }
        $report.workloads[0].completed = 3; $report.workloads[0].failures = 1; $report.workloads[0].throughputPerSecond = 3000 / 100.25
        foreach ($mutate in @(
            { param($r) $r.schemaVersion = '2' },
            { param($r) $r.service = 'other' },
            { param($r) $r.generatedAt = $privateKey },
            { param($r) $r.extra = $privateKey },
            { param($r) $r.workloads = @($r.workloads[0], $r.workloads[0]) },
            { param($r) $r.workloads[0].id = $privateKey },
            { param($r) $r.workloads[0].method = 'DELETE' },
            { param($r) $r.workloads[0].path = $privateKey },
            { param($r) $r.workloads[0].authenticated = $false },
            { param($r) $r.workloads[0].inputFingerprint = $privateKey },
            { param($r) $r.workloads[0].extra = $privateKey },
            { param($r) $r.workloads[0].Remove('failures') },
            { param($r) $r.workloads[0].requested = 5; $r.workloads[0].executed = 5; $r.workloads[0].failures = 2 },
            { param($r) $r.workloads[0].executed = 3 },
            { param($r) $r.workloads[0].completed = '3' },
            { param($r) $r.workloads[0].completed = $true },
            { param($r) $r.workloads[0].completed = 3.5 },
            { param($r) $r.workloads[0].completed = -1 },
            { param($r) $r.workloads[0].failures = 0 },
            { param($r) $r.workloads[0].warmupRequests = 1 },
            { param($r) $r.workloads[0].concurrency = 1 },
            { param($r) $r.workloads[0].timeoutSeconds = 4 },
            { param($r) $r.workloads[0].elapsedMilliseconds = 0 },
            { param($r) $r.workloads[0].elapsedMilliseconds = [double]::PositiveInfinity },
            { param($r) $r.workloads[0].throughputPerSecond = 31 },
            { param($r) $r.workloads[0].p50Milliseconds = [double]::NaN },
            { param($r) $r.workloads[0].p95Milliseconds = -1 },
            { param($r) $r.workloads[0].p95Milliseconds = 4 }
        )) {
            $invalid = $report | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
            & $mutate $invalid | Out-Null
            $aggregate = Get-FailedWorkloadAggregate ($invalid | ConvertTo-Json -Depth 8 -Compress) $configuration
            Assert-Contract ($null -eq $aggregate) 'Malformed, mismatched or private failed stdout produced retained evidence.'
            $script:cases++
        }
        $stdout = $report | ConvertTo-Json -Depth 8 -Compress
        foreach ($invalid in @(
            ('private-child-diagnostic ' + $privateKey),
            ($stdout.Replace('"schemaVersion":2', '"schemaVersion":2,"schemaVersion":2')),
            ($stdout.Replace('"p50Milliseconds":1.125', '"p50Milliseconds":1e999')),
            ((' ' * 16385) + $stdout)
        )) {
            Assert-Contract ($null -eq (Get-FailedWorkloadAggregate $invalid $configuration)) 'Unparseable, duplicate, nonfinite or oversized failed stdout was retained.'
            $script:cases++
        }
    }
}

function Invoke-FailureDiagnosticSchemaContracts {
    $output = Join-Path $temporaryRoot 'diagnostic-schema.json'
    $context = $null
    $writeError = 'Native benchmark failure diagnostic could not be recorded.'
    $validationError = 'Native benchmark failure diagnostic is invalid.'
    try {
        $context = Start-NativeBenchmarkAttempt -OutputPath $output -Service 'admin'
        $runningHash = Get-AttemptContractHash $context.attemptPath
        Assert-DiagnosticContractRejection { Set-NativeBenchmarkAttemptFailureDiagnostic -Context $context -Diagnostic (New-DiagnosticContractInput) } $writeError
        Assert-Contract ((Get-AttemptContractHash $context.attemptPath) -ceq $runningHash -and
            -not [IO.File]::Exists((Join-Path $context.attemptDirectory 'diagnostics.json'))) 'Rejected running diagnostic changed the attempt.'
        $script:cases++
        # A minimal public helper report establishes an actual succeeded state;
        # this is a schema/writer fixture, not a measured benchmark qualification.
        Complete-NativeBenchmarkAttempt -Context $context -Report @{ service = 'admin' }
        $archive = Join-Path $context.attemptDirectory 'benchmark.json'
        $aliasHash = Get-AttemptContractHash $output
        $archiveHash = Get-AttemptContractHash $archive
        $succeededHash = Get-AttemptContractHash $context.attemptPath
        Assert-DiagnosticContractRejection { Set-NativeBenchmarkAttemptFailureDiagnostic -Context $context -Diagnostic (New-DiagnosticContractInput) } $writeError
        Assert-Contract ((Get-AttemptContractHash $context.attemptPath) -ceq $succeededHash -and
            (Get-AttemptContractHash $output) -ceq $aliasHash -and (Get-AttemptContractHash $archive) -ceq $archiveHash) 'Rejected succeeded diagnostic changed original bytes.'
        $script:cases++
    }
    finally { if ($null -ne $context) { Close-NativeBenchmarkAttempt -Context $context } }

    $context = $null
    try {
        $context = Start-NativeBenchmarkAttempt -OutputPath $output -Service 'admin'
        Set-NativeBenchmarkAttemptFailed -Context $context -FailureCode 'collection-failed'
        $attemptHash = Get-AttemptContractHash $context.attemptPath
        $latestHash = Get-AttemptContractHash $context.latestPath
        $diagnosticPath = Join-Path $context.attemptDirectory 'diagnostics.json'
        $invalid = New-DiagnosticContractInput
        $invalid.reason = $privateKey
        Assert-DiagnosticContractRejection { Set-NativeBenchmarkAttemptFailureDiagnostic -Context $context -Diagnostic $invalid } $writeError
        Assert-Contract (-not [IO.File]::Exists($diagnosticPath)) 'Invalid diagnostic created a sidecar.'
        $script:cases++
        Assert-Contract (@(Set-NativeBenchmarkAttemptFailureDiagnostic -Context $context -Diagnostic (New-DiagnosticContractInput)).Count -eq 0) 'Diagnostic writer emitted incidental output.'
        $full = Read-AttemptContractJson $diagnosticPath
        Assert-Contract ($full.Count -eq 12 -and $full.schemaVersion -eq 2 -and $null -eq $full.workloadAggregate -and
            $full.kind -ceq 'conduit-native-benchmark-failure-diagnostic' -and $full.attemptId -ceq $context.attemptId -and
            $full.service -ceq 'admin' -and $full.phase -ceq 'unknown' -and $full.reason -ceq 'unknown' -and
            [IO.FileInfo]::new($diagnosticPath).Length -le 4096) 'Unknown fallback diagnostic is not a bounded closed record.'
        $diagnosticHash = Get-AttemptContractHash $diagnosticPath
        Assert-DiagnosticContractRejection { Set-NativeBenchmarkAttemptFailureDiagnostic -Context $context -Diagnostic (New-DiagnosticContractInput) } $writeError
        Assert-Contract ((Get-AttemptContractHash $diagnosticPath) -ceq $diagnosticHash -and
            (Get-AttemptContractHash $context.attemptPath) -ceq $attemptHash -and (Get-AttemptContractHash $context.latestPath) -ceq $latestHash -and
            (Get-AttemptContractHash $output) -ceq $aliasHash -and (Get-AttemptContractHash $archive) -ceq $archiveHash) 'Immutable diagnostic writer changed a sidecar, attempt, alias or archive.'
        $script:cases++

        $legacy = $full | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
        $legacy.schemaVersion = 1
        $legacy.Remove('workloadAggregate')
        Assert-Contract (@(Get-NativeBenchmarkAttemptFailureDiagnostic $legacy).Count -eq 1) 'Original schema-1 failure evidence is no longer readable.'
        $legacy.workloadAggregate = $null
        Assert-DiagnosticContractRejection { Get-NativeBenchmarkAttemptFailureDiagnostic $legacy } $validationError
        $script:cases++

        $networkDiagnostic = $full | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
        $networkDiagnostic.phase = 'configuration'; $networkDiagnostic.reason = 'network-binding'
        Assert-Contract (@(Get-NativeBenchmarkAttemptFailureDiagnostic $networkDiagnostic).Count -eq 1 -and
            $null -eq $networkDiagnostic.workloadAggregate) 'Schema-2 network binding diagnostics did not retain the closed reason and null aggregate.'
        $script:cases++
        $legacyNetworkDiagnostic = $networkDiagnostic | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
        $legacyNetworkDiagnostic.schemaVersion = 1; $legacyNetworkDiagnostic.Remove('workloadAggregate')
        Assert-Contract (@(Get-NativeBenchmarkAttemptFailureDiagnostic $legacyNetworkDiagnostic).Count -eq 1) 'Schema-1 diagnostics did not accept the closed network binding reason.'
        $script:cases++
        foreach ($invalidReason in @('Network-binding', 'network-binding-unknown', ('network-binding:' + $privateKey), ("network-binding`n" + $privateKey))) {
            $modified = $networkDiagnostic | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
            $modified.reason = $invalidReason
            Assert-DiagnosticContractRejection { Get-NativeBenchmarkAttemptFailureDiagnostic $modified } $validationError
            $script:cases++
        }

        $mutations = @(
            { param($d) $d.schemaVersion = '1' },
            { param($d) $d.schemaVersion = 3 },
            { param($d) $d.kind = 'Conduit-native-benchmark-failure-diagnostic' },
            { param($d) $d.attemptId = '0' * 32 },
            { param($d) $d.service = 'Admin' },
            { param($d) $d.phase = $privateKey },
            { param($d) $d.phase = 'Readiness' },
            { param($d) $d.reason = $privateKey },
            { param($d) $d.reason = @('unknown') },
            { param($d) $d.runtime = 'native' },
            { param($d) $d.sampleKind = 'Warmup' },
            { param($d) $d.extra = $privateKey },
            { param($d) $value = $d.phase; $d.Remove('phase'); $d['Phase'] = $value },
            { param($d) $d.sampleKind = 'recorded'; $d.pair = $null },
            { param($d) $d.sampleKind = 'recorded'; $d.pair = 0 },
            { param($d) $d.sampleKind = 'recorded'; $d.pair = 22 },
            { param($d) $d.sampleKind = 'recorded'; $d.pair = '1' },
            { param($d) $d.sampleKind = 'recorded'; $d.pair = $true },
            { param($d) $d.sampleKind = 'warmup'; $d.pair = 1 },
            { param($d) $d.pair = 1 },
            { param($d) $d.ownedContainerCleanup.status = 'confirmed-leak' },
            { param($d) $d.ownedContainerCleanup.status = 'not-attempted'; $d.ownedContainerCleanup.reason = 'shutdown-failed' },
            { param($d) $d.ownedContainerCleanup.status = 'verification-failed'; $d.ownedContainerCleanup.reason = 'none' },
            { param($d) $d.ownedContainerCleanup.reason = $privateKey },
            { param($d) $d.privateFileCleanup.status = 'not-attempted'; $d.privateFileCleanup.reason = 'cleanup-failed' },
            { param($d) $d.privateFileCleanup.status = 'verification-failed'; $d.privateFileCleanup.reason = 'none' },
            { param($d) $d.privateFileCleanup.reason = 'verification-failed' },
            { param($d) $d.privateFileCleanup.extra = $privateKey }
        )
        foreach ($mutate in $mutations) {
            $modified = $full | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
            & $mutate $modified | Out-Null
            Assert-DiagnosticContractRejection { Get-NativeBenchmarkAttemptFailureDiagnostic $modified } $validationError
            Assert-Contract ((Get-AttemptContractHash $diagnosticPath) -ceq $diagnosticHash) 'Validator changed retained diagnostic bytes.'
            $script:cases++
        }
        foreach ($pair in @(1, 21)) {
            $valid = $full | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
            $valid.runtime = 'native-aot'; $valid.sampleKind = 'recorded'; $valid.pair = $pair
            $valid.ownedContainerCleanup = @{ status = 'confirmed'; reason = 'shutdown-failed' }
            $valid.privateFileCleanup = @{ status = 'confirmed'; reason = 'cleanup-failed' }
            Assert-Contract (@(Get-NativeBenchmarkAttemptFailureDiagnostic $valid).Count -eq 1) 'Valid recorded pair or confirmed cleanup reason was rejected.'
            $script:cases++
        }
        $withAggregate = $full | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
        $withAggregate.phase = 'workload'; $withAggregate.reason = 'probe-exit'
        $withAggregate.runtime = 'jit'; $withAggregate.sampleKind = 'warmup'
        $withAggregate.workloadAggregate = @{ phase = 'measured'; id = 'admin-setting-read'; requested = 4;
            executed = 4; completed = 0; failures = 4; warmupRequests = 2; concurrency = 2; timeoutSeconds = 5 }
        Assert-Contract (@(Get-NativeBenchmarkAttemptFailureDiagnostic $withAggregate).Count -eq 1) 'A fully failed measured aggregate was rejected.'
        $script:cases++
        foreach ($mutate in @(
            { param($d) $d.phase = 'workload-result' },
            { param($d) $d.reason = 'unknown' },
            { param($d) $d.runtime = 'unknown' },
            { param($d) $d.sampleKind = 'none' },
            { param($d) $d.workloadAggregate.phase = 'warmup' },
            { param($d) $d.workloadAggregate.id = 'gateway-chat-sse' },
            { param($d) $d.workloadAggregate.id = $privateKey },
            { param($d) $d.workloadAggregate.requested = 0 },
            { param($d) $d.workloadAggregate.requested = 100001 },
            { param($d) $d.workloadAggregate.executed = 3 },
            { param($d) $d.workloadAggregate.completed = 5 },
            { param($d) $d.workloadAggregate.failures = 3 },
            { param($d) $d.workloadAggregate.completed = '0' },
            { param($d) $d.workloadAggregate.completed = $false },
            { param($d) $d.workloadAggregate.completed = [double]::NaN },
            { param($d) $d.workloadAggregate.warmupRequests = 10001 },
            { param($d) $d.workloadAggregate.concurrency = 0 },
            { param($d) $d.workloadAggregate.concurrency = 257 },
            { param($d) $d.workloadAggregate.timeoutSeconds = 121 },
            { param($d) $d.workloadAggregate.extra = $privateKey },
            { param($d) $d.workloadAggregate.Remove('phase') }
        )) {
            $modified = $withAggregate | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
            & $mutate $modified | Out-Null
            Assert-DiagnosticContractRejection { Get-NativeBenchmarkAttemptFailureDiagnostic $modified } $validationError
            $script:cases++
        }
    }
    finally { if ($null -ne $context) { Close-NativeBenchmarkAttempt -Context $context } }
    Assert-AttemptContractReaderRejected $output 'admin'
    Assert-AttemptContractPrivacy $output
    $script:cases++

    foreach ($kind in @('directory', 'symlink')) {
        $context = $null
        $path = $null
        try {
            $context = Start-NativeBenchmarkAttempt -OutputPath (Join-Path $temporaryRoot ('diagnostic-' + $kind + '.json')) -Service 'admin'
            Set-NativeBenchmarkAttemptFailed -Context $context -FailureCode 'collection-failed'
            $path = Join-Path $context.attemptDirectory 'diagnostics.json'
            $target = Join-Path $context.attemptDirectory 'sentinel.json'
            [IO.File]::WriteAllText($target, 'owned diagnostic sentinel')
            $targetHash = Get-AttemptContractHash $target
            if ($kind -ceq 'directory') { [IO.Directory]::CreateDirectory($path) | Out-Null }
            else {
                try { [IO.File]::CreateSymbolicLink($path, $target) | Out-Null }
                catch {
                    if ($IsWindows) {
                        Write-Host 'SKIP: Windows diagnostic file symbolic-link setup unavailable; rejection not claimed.'
                        continue
                    }
                    throw 'Mandatory diagnostic file symbolic-link fixture could not be created.'
                }
            }
            Assert-DiagnosticContractRejection { Set-NativeBenchmarkAttemptFailureDiagnostic -Context $context -Diagnostic (New-DiagnosticContractInput) } $writeError
            Assert-Contract ((Get-AttemptContractHash $target) -ceq $targetHash) 'Rejected diagnostic path changed its sentinel target.'
            $script:cases++
        }
        finally {
            if ($null -ne $context) { Close-NativeBenchmarkAttempt -Context $context }
            if ($null -ne $path) {
                if ($kind -ceq 'directory' -and [IO.Directory]::Exists($path)) { [IO.Directory]::Delete($path) }
                elseif ($kind -ceq 'symlink' -and [IO.File]::Exists($path)) { [IO.File]::Delete($path) }
            }
        }
    }
}

function Get-AttemptContractMetadata([string] $OutputPath, [string] $Service, [string] $Status) {
    $latestPath = Join-Path ($OutputPath + '.attempts') 'latest.json'
    Assert-Contract ([IO.File]::Exists($latestPath)) 'Collector did not retain its latest attempt.'
    $latest = Read-AttemptContractJson $latestPath
    Assert-Contract ($latest.Count -eq 4 -and ($latest.schemaVersion -is [long] -or $latest.schemaVersion -is [int]) -and $latest.schemaVersion -eq 1 -and
        $latest.kind -ceq 'conduit-native-benchmark-latest-attempt' -and $latest.service -ceq $Service -and
        $latest.attemptId -is [string] -and $latest.attemptId -cmatch '\A[0-9a-f]{32}\z') 'Latest attempt identity is invalid.'
    $attemptPath = Join-Path (Join-Path ($OutputPath + '.attempts') $latest.attemptId) 'attempt.json'
    $attempt = Read-AttemptContractJson $attemptPath
    Assert-Contract ($attempt.Count -eq 9 -and ($attempt.schemaVersion -is [long] -or $attempt.schemaVersion -is [int]) -and $attempt.schemaVersion -eq 1 -and
        $attempt.kind -ceq 'conduit-native-benchmark-attempt' -and $attempt.attemptId -ceq $latest.attemptId -and
        $attempt.service -ceq $Service -and $attempt.status -ceq $Status) 'Retained attempt state is invalid.'
    [DateTimeOffset]$started = [DateTimeOffset]::MinValue
    Assert-Contract ($attempt.startedAt -is [string] -and
        [DateTimeOffset]::TryParse($attempt.startedAt, [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind, [ref]$started) -and $started.Offset -eq [TimeSpan]::Zero) 'Attempt start time is invalid.'
    if ($Status -ceq 'running') {
        Assert-Contract ($null -eq $attempt.completedAt -and $null -eq $attempt.reportSha256 -and
            $null -eq $attempt.failureCode) 'Running attempt contains a terminal result.'
    }
    else {
        [DateTimeOffset]$completed = [DateTimeOffset]::MinValue
        Assert-Contract ($attempt.completedAt -is [string] -and
            [DateTimeOffset]::TryParse($attempt.completedAt, [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind, [ref]$completed) -and
            $completed.Offset -eq [TimeSpan]::Zero -and $completed -ge $started) 'Attempt completion time is invalid.'
        if ($Status -ceq 'failed') {
            Assert-Contract ($attempt.failureCode -is [string] -and
                $attempt.failureCode -cin @('collection-failed', 'cleanup-failed', 'publication-failed') -and
                $null -eq $attempt.reportSha256) 'Failed attempt did not retain only a controlled failure code.'
        }
    }
    Assert-AttemptContractPrivacy $OutputPath
    return ,[ordered]@{ latest = $latest; attempt = $attempt; latestPath = $latestPath; attemptPath = $attemptPath }
}

function Assert-AttemptContractReaderRejected([string] $OutputPath, [string] $Service) {
    $rejected = $false
    try { $unexpected = Read-NativeBenchmarkAttemptEvidence -OutputPath $OutputPath -Service $Service }
    catch { $rejected = $true }
    Assert-Contract $rejected 'Attempt reader accepted incomplete or mismatched latest evidence.'
}

function Assert-AttemptContractSuccess([string] $OutputPath, [string] $Service) {
    $metadata = Get-AttemptContractMetadata $OutputPath $Service 'succeeded'
    $results = @(Read-NativeBenchmarkAttemptEvidence -OutputPath $OutputPath -Service $Service)
    Assert-Contract ($results.Count -eq 1 -and $results[0] -is [Collections.IDictionary] -and
        $results[0].Count -eq 7) 'Attempt reader did not return exactly one closed evidence object.'
    $evidence = $results[0]
    $expectedArchive = Join-Path (Join-Path ($OutputPath + '.attempts') $metadata.latest.attemptId) 'benchmark.json'
    Assert-Contract ($evidence.reportPath -ceq $OutputPath -and [IO.File]::Exists($expectedArchive) -and
        (Get-AttemptContractHash $OutputPath) -ceq (Get-AttemptContractHash $expectedArchive) -and
        $evidence.reportSha256 -ceq (Get-AttemptContractHash $expectedArchive) -and
        $metadata.attempt.reportSha256 -ceq $evidence.reportSha256 -and
        $evidence.latestSha256 -ceq (Get-AttemptContractHash $metadata.latestPath) -and
        $evidence.attemptSha256 -ceq (Get-AttemptContractHash $metadata.attemptPath)) 'Attempt hashes or archived alias differ from original bytes.'
    $report = $evidence.report
    Assert-Contract ($report.schemaVersion -eq 4 -and $report.service -ceq $Service -and
        $report.collection.Count -eq 3 -and $report.collection.attemptId -ceq $metadata.latest.attemptId -and
        $report.collection.startedAt -ceq $metadata.attempt.startedAt -and
        $report.collection.completedAt -ceq $metadata.attempt.completedAt -and
        $report.generatedAt -ceq $metadata.attempt.completedAt -and $null -eq $metadata.attempt.failureCode) 'Benchmark collection identity is not joined to its succeeded latest attempt.'
    return ,$evidence
}

function Get-OwnedProcessIdentityToken([Diagnostics.Process] $Process, [switch] $IncludeState) {
    if ($IsLinux) {
        # /proc field 22 has no separately calculated wall-clock boot origin.
        $stat = [IO.File]::ReadAllText('/proc/' + $Process.Id + '/stat')
        $separator = $stat.LastIndexOf(') ', [StringComparison]::Ordinal)
        if ($separator -lt 0) { throw 'Owned process stat framing is invalid.' }
        $fields = $stat.Substring($separator + 2).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)
        [ulong]$start = 0
        if ($fields.Count -le 19 -or
            -not [ulong]::TryParse($fields[19], [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$start) -or $start -eq 0) {
            throw 'Owned process start token is invalid.'
        }
        $token = 'linux:' + $start.ToString([Globalization.CultureInfo]::InvariantCulture)
        if ($IncludeState) {
            return [pscustomobject]@{ Token = $token; Zombie = ($fields[0] -ceq 'Z') }
        }
        return $token
    }
    $token = 'windows:' + $Process.StartTime.ToUniversalTime().Ticks.ToString([Globalization.CultureInfo]::InvariantCulture)
    if ($IncludeState) { return [pscustomobject]@{ Token = $token; Zombie = $false } }
    return $token
}

function Get-OwnedProcessCleanupInspection([Diagnostics.Process] $Process) {
    if ($Process.HasExited) { return }
    try { return Get-OwnedProcessIdentityToken $Process -IncludeState }
    catch {
        # A child may exit and be reaped after HasExited/lookup but before the
        # single /proc read. Missing metadata is never a matching identity.
        $exception = $_.Exception
        $missing = $false
        while ($null -ne $exception) {
            if ($exception -is [IO.FileNotFoundException] -or $exception -is [IO.DirectoryNotFoundException]) {
                $missing = $true
                break
            }
            $exception = $exception.InnerException
        }
        if (-not $IsLinux -or -not $missing) { throw }
        $Process.Refresh()
        if ($Process.HasExited) { return }
        throw
    }
}

function Test-OwnedProcessStopped([string] $IdentityPath, [switch] $Stop) {
    if (-not [IO.File]::Exists($IdentityPath)) { return }
    $identity = ([IO.File]::ReadAllText($IdentityPath)).Split('|')
    Assert-Contract ($identity.Count -eq 2) 'Fixture process identity is malformed.'
    $ownedProcess = Get-Process -Id ([int]$identity[0]) -ErrorAction SilentlyContinue
    if ($null -eq $ownedProcess) { return }
    try {
        $inspection = Get-OwnedProcessCleanupInspection $ownedProcess
        if ($null -eq $inspection) { return }
        Assert-Contract ($inspection.Token -ceq $identity[1]) 'Fixture cleanup rejected a reused PID.'
        if ($Stop -and -not $ownedProcess.HasExited -and -not $inspection.Zombie) {
            $ownedProcess.Kill($true)
            Assert-Contract ($ownedProcess.WaitForExit(5000)) 'Owned fixture process did not terminate.'
        }
        elseif (-not $ownedProcess.HasExited -and -not $inspection.Zombie) {
            throw 'Collector left a live owned descendant.'
        }
    }
    finally { $ownedProcess.Dispose() }
}

function Stop-OwnedReadinessServer($Record) {
    $failures = [Collections.Generic.List[string]]::new()
    $stage = 'identity-file'
    try {
        Assert-Contract $Record.IdentityCaptured 'Owned readiness startup identity was not captured.'
        $expectedIdentity = [string]$Record.Id + '|' + $Record.StartToken
        Assert-Contract ([IO.File]::Exists($Record.IdentityPath) -and
            [IO.File]::ReadAllText($Record.IdentityPath) -ceq $expectedIdentity) 'Owned readiness identity file was rejected.'
        $stage = 'identity-process-reap'
        Test-OwnedProcessStopped $Record.IdentityPath -Stop
    }
    catch { $failures.Add($stage + ':' + $_.Exception.GetType().FullName) }
    # An identity-file error must never discard the still-live original handle.
    # Verify this original Process against its captured identity before fallback.
    $stage = 'original-handle'
    try {
        Assert-Contract ($Record.Process.Id -eq $Record.Id) 'Original owned readiness handle was rejected.'
        if (-not $Record.IdentityCaptured) {
            # This directly started original handle was retained before token
            # capture. Reap it on failed startup; never find a replacement by PID.
            if (-not $Record.Process.HasExited) { $Record.Process.Kill($true) }
        }
        else {
            $inspection = Get-OwnedProcessCleanupInspection $Record.Process
            if ($null -ne $inspection) {
                Assert-Contract ($inspection.Token -ceq $Record.StartToken) 'Original owned readiness handle was rejected.'
                if (-not $Record.Process.HasExited -and -not $inspection.Zombie) {
                    $Record.Process.Kill($true)
                }
            }
        }
        $stage = 'original-process-reap'
        Assert-Contract ($Record.Process.WaitForExit(5000)) 'Original owned readiness child did not reap.'
        $Record.Reaped = $true
    }
    catch { $failures.Add($stage + ':' + $_.Exception.GetType().FullName) }
    finally {
        if ($Record.Reaped) {
            $Record.Process.Dispose()
            $Record.Disposed = $true
            foreach ($path in @($Record.IdentityPath, $Record.AssociationPath)) {
                try { if ([IO.File]::Exists($path)) { Remove-Item -LiteralPath $path -Force } }
                catch { $failures.Add('identity-file-removal:' + $_.Exception.GetType().FullName) }
            }
        }
    }
    $Record.CleanupDiagnostics = @($failures.ToArray())
    if ($failures.Count -gt 0) { throw 'Owned readiness cleanup failed.' }
}

function Register-OwnedReadinessServer([Diagnostics.Process] $Process, [string] $IdentityPath,
    [string] $AssociationPath, [Collections.Generic.List[object]] $CaseServers) {
    $record = [pscustomobject]@{ Process = $Process; IdentityPath = $IdentityPath
        AssociationPath = $AssociationPath; Id = $Process.Id; StartToken = $null
        IdentityCaptured = $false; Reaped = $false; Disposed = $false; CleanupDiagnostics = @() }
    $CaseServers.Add($record)
    $ownedReadinessServers.Add($record)
    Assert-Contract (-not $Process.HasExited) 'Owned readiness child exited before identity capture.'
    $record.StartToken = Get-OwnedProcessIdentityToken $Process
    Assert-Contract ($record.StartToken -is [string] -and
        $record.StartToken -cmatch '\A(?:linux|windows):[1-9][0-9]*\z' -and -not $Process.HasExited) 'Owned readiness startup identity is invalid.'
    $record.IdentityCaptured = $true
    [IO.File]::WriteAllText($IdentityPath, ([string]$record.Id + '|' + $record.StartToken))
    [IO.File]::WriteAllText($AssociationPath, $IdentityPath)
    return $record
}

function Invoke-OwnedProcessCleanupContracts {
    $contractRoot = Join-Path $temporaryRoot ('process-lifecycle-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($contractRoot) | Out-Null
    $childScript = Join-Path $contractRoot 'owned-child.ps1'
    [IO.File]::WriteAllText($childScript, @'
param([string] $ExitPath, [string] $ReadyPath)
[IO.File]::WriteAllText($ReadyPath, 'ready')
while (-not [IO.File]::Exists($ExitPath)) { Start-Sleep -Milliseconds 20 }
exit 0
'@, [Text.UTF8Encoding]::new($false))
    $caseServers = [Collections.Generic.List[object]]::new()
    $startedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
    $breakpoints = [Collections.Generic.List[object]]::new()
    $contractFailures = [Collections.Generic.List[string]]::new()

    function New-OwnedProcessContractChild([string] $Name) {
        $exitPath = Join-Path $contractRoot ($Name + '.exit')
        $readyPath = Join-Path $contractRoot ($Name + '.ready')
        $start = [Diagnostics.ProcessStartInfo]::new($env:CONDUIT_COLLECTOR_TEST_PWSH)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('-NoProfile', '-File', $childScript, '-ExitPath', $exitPath, '-ReadyPath', $readyPath)) {
            $start.ArgumentList.Add($argument)
        }
        $child = [Diagnostics.Process]::Start($start)
        $startedProcesses.Add($child)
        return [pscustomobject]@{ Process = $child; ExitPath = $exitPath; ReadyPath = $readyPath
            IdentityPath = (Join-Path $contractRoot ($Name + '.identity'))
            AssociationPath = (Join-Path $contractRoot ($Name + '.association')) }
    }

    function Wait-OwnedProcessContractReady($Child) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (-not [IO.File]::Exists($Child.ReadyPath) -and -not $Child.Process.HasExited -and
            $watch.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 20 }
        Assert-Contract ([IO.File]::Exists($Child.ReadyPath) -and -not $Child.Process.HasExited) 'Owned process lifecycle child did not become ready.'
    }

    function New-OwnedProcessReadBoundary($Child) {
        $tokens = $null; $parseErrors = $null
        $sourceAst = [Management.Automation.Language.Parser]::ParseFile($PSCommandPath, [ref]$tokens, [ref]$parseErrors)
        Assert-Contract ($parseErrors.Count -eq 0) 'Owned process regression source did not parse.'
        $tokenDefinition = @($sourceAst.FindAll({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -ceq 'Get-OwnedProcessIdentityToken'
        }, $true))
        Assert-Contract ($tokenDefinition.Count -eq 1) 'Owned process token definition is ambiguous.'
        $read = @($tokenDefinition[0].Body.FindAll({ param($node)
            $node -is [Management.Automation.Language.InvokeMemberExpressionAst] -and
                $node.Member.Value -ceq 'ReadAllText'
        }, $true))
        Assert-Contract ($read.Count -eq 1) 'Owned process token read boundary is ambiguous.'
        $boundary = [pscustomobject]@{ TargetId = $Child.Process.Id; Handle = $Child.Process
            ExitPath = $Child.ExitPath; Hits = 0; Joined = $false; ProcAbsent = $false; Failure = $false }
        $action = {
            # Debugger frames expose automatic variables, including the actual
            # bound arguments, while user-defined parameter locals may be absent.
            # Restrict the read boundary to this directly started child.
            $frames = @(Get-PSCallStack | Where-Object { $_.FunctionName -ceq 'Get-OwnedProcessIdentityToken' })
            if ($frames.Count -ne 1) { return }
            $variables = $frames[0].GetFrameVariables()
            if (-not $variables.ContainsKey('PSBoundParameters')) { return }
            $boundParameters = $variables['PSBoundParameters'].Value
            if ($boundParameters -isnot [Collections.IDictionary] -or -not $boundParameters.ContainsKey('Process')) { return }
            $readProcess = $boundParameters['Process']
            if ($readProcess -isnot [Diagnostics.Process] -or $readProcess.Id -ne $boundary.TargetId) { return }
            $boundary.Hits++
            try {
                [IO.File]::WriteAllText($boundary.ExitPath, 'exit')
                $boundary.Joined = $boundary.Handle.WaitForExit(5000) -and $boundary.Handle.ExitCode -eq 0
                $boundary.ProcAbsent = -not [IO.Directory]::Exists('/proc/' + $boundary.TargetId)
                if (-not $boundary.Joined -or -not $boundary.ProcAbsent) { $boundary.Failure = $true }
            }
            catch { $boundary.Failure = $true }
            # Resume the real ReadAllText. Startup must throw; cleanup must only
            # accept the missing file after confirming the original exit.
        }.GetNewClosure()
        $breakpoint = Set-PSBreakpoint -Script $PSCommandPath -Line $read[0].Extent.StartLineNumber -Action $action
        $breakpoints.Add($breakpoint)
        return [pscustomobject]@{ State = $boundary; Breakpoint = $breakpoint }
    }

    function Assert-OwnedProcessReadBoundary($Boundary) {
        Assert-Contract ($Boundary.State.Hits -eq 1 -and $Boundary.State.Joined -and
            $Boundary.State.ProcAbsent -and -not $Boundary.State.Failure) 'Owned process did not exit at the actual Linux stat-read boundary.'
        Remove-PSBreakpoint -Id $Boundary.Breakpoint.Id
        $null = $breakpoints.Remove($Boundary.Breakpoint)
    }

    try {
        # Strict startup must not turn absent /proc metadata into a valid token.
        # The original handle is already retained when this read fails.
        $child = New-OwnedProcessContractChild 'startup'
        Wait-OwnedProcessContractReady $child
        $boundary = $null
        if ($IsLinux) { $boundary = New-OwnedProcessReadBoundary $child }
        else {
            [IO.File]::WriteAllText($child.ExitPath, 'exit')
            Assert-Contract ($child.Process.WaitForExit(5000) -and $child.Process.ExitCode -eq 0) 'Owned startup exit fixture did not join.'
        }
        $startupFailure = $null
        try { $unused = Register-OwnedReadinessServer $child.Process $child.IdentityPath $child.AssociationPath $caseServers }
        catch { $startupFailure = $_.Exception }
        Assert-Contract ($null -ne $startupFailure -and $caseServers.Count -eq 1) 'Missing startup identity was accepted or its original handle was lost.'
        $startupRecord = $caseServers[0]
        Assert-Contract (-not $startupRecord.IdentityCaptured -and $null -eq $startupRecord.StartToken -and
            -not [IO.File]::Exists($child.IdentityPath) -and -not [IO.File]::Exists($child.AssociationPath)) 'Failed startup retained a claimed identity.'
        if ($IsLinux) {
            $missing = $false; $exception = $startupFailure
            while ($null -ne $exception) {
                if ($exception -is [IO.FileNotFoundException] -or $exception -is [IO.DirectoryNotFoundException]) { $missing = $true; break }
                $exception = $exception.InnerException
            }
            Assert-Contract $missing 'The strict original stat read did not reject the reaped startup child.'
            Assert-OwnedProcessReadBoundary $boundary
        }
        $startupCleanupFailed = $false
        try { Stop-OwnedReadinessServer $startupRecord }
        catch { $startupCleanupFailed = $_.Exception.Message -ceq 'Owned readiness cleanup failed.' }
        Assert-Contract ($startupCleanupFailed -and $startupRecord.Reaped -and $startupRecord.Disposed -and
            $startupRecord.CleanupDiagnostics.Count -eq 1 -and
            $startupRecord.CleanupDiagnostics[0].StartsWith('identity-file:', [StringComparison]::Ordinal)) 'Failed startup did not retain failure while reaping its original handle.'
        $script:cases++

        # Both the PID lookup path and the retained original-handle fallback
        # encounter disappearance after their initial live-process check.
        foreach ($cleanupPath in @('identity', 'fallback')) {
            $child = New-OwnedProcessContractChild ('cleanup-' + $cleanupPath)
            $record = Register-OwnedReadinessServer $child.Process $child.IdentityPath $child.AssociationPath $caseServers
            Wait-OwnedProcessContractReady $child
            if ($cleanupPath -ceq 'fallback') { Remove-Item -LiteralPath $child.IdentityPath }
            $boundary = $null
            if ($IsLinux) { $boundary = New-OwnedProcessReadBoundary $child }
            else {
                [IO.File]::WriteAllText($child.ExitPath, 'exit')
                Assert-Contract ($child.Process.WaitForExit(5000) -and $child.Process.ExitCode -eq 0) 'Owned cleanup exit fixture did not join.'
            }
            if ($cleanupPath -ceq 'identity') {
                Test-OwnedProcessStopped $child.IdentityPath -Stop
                if ($IsLinux) { Assert-OwnedProcessReadBoundary $boundary }
                Stop-OwnedReadinessServer $record
            }
            else {
                $fallbackFailed = $false
                try { Stop-OwnedReadinessServer $record }
                catch { $fallbackFailed = $_.Exception.Message -ceq 'Owned readiness cleanup failed.' }
                if ($IsLinux) { Assert-OwnedProcessReadBoundary $boundary }
                Assert-Contract ($fallbackFailed -and $record.CleanupDiagnostics.Count -eq 1 -and
                    $record.CleanupDiagnostics[0].StartsWith('identity-file:', [StringComparison]::Ordinal)) 'Original-handle fallback discarded the identity-file failure.'
            }
            Assert-Contract ($record.Reaped -and $record.Disposed) 'Stat-read disappearance left an unreaped original handle.'
        }
        $script:cases++

        $child = New-OwnedProcessContractChild 'wrong-token'
        $record = Register-OwnedReadinessServer $child.Process $child.IdentityPath $child.AssociationPath $caseServers
        Wait-OwnedProcessContractReady $child
        $correctIdentity = [string]$record.Id + '|' + $record.StartToken
        $parts = $record.StartToken.Split(':')
        $wrongToken = $parts[0] + ':' + ([ulong]::Parse($parts[1], [Globalization.CultureInfo]::InvariantCulture) + 1).ToString([Globalization.CultureInfo]::InvariantCulture)
        [IO.File]::WriteAllText($child.IdentityPath, ([string]$record.Id + '|' + $wrongToken))
        $wrongTokenRejected = $false
        try { Test-OwnedProcessStopped $child.IdentityPath -Stop }
        catch { $wrongTokenRejected = $_.Exception.Message -ceq 'Fixture cleanup rejected a reused PID.' }
        finally { [IO.File]::WriteAllText($child.IdentityPath, $correctIdentity) }
        Assert-Contract ($wrongTokenRejected -and -not $child.Process.HasExited -and
            (Get-OwnedProcessIdentityToken $child.Process) -ceq $record.StartToken) 'A live wrong start token was signalled or accepted.'
        Stop-OwnedReadinessServer $record
        Assert-Contract ($record.Reaped -and $record.Disposed) 'The rejected wrong-token child was not subsequently reaped through its exact identity.'
        $script:cases++

        $child = New-OwnedProcessContractChild 'normal-stop'
        $record = Register-OwnedReadinessServer $child.Process $child.IdentityPath $child.AssociationPath $caseServers
        Wait-OwnedProcessContractReady $child
        Test-OwnedProcessStopped $child.IdentityPath -Stop
        Assert-Contract ($child.Process.WaitForExit(5000) -and $child.Process.HasExited) 'Exact-token stop did not join the original child.'
        Stop-OwnedReadinessServer $record
        Assert-Contract ($record.Reaped -and $record.Disposed -and
            -not [IO.File]::Exists($child.IdentityPath) -and -not [IO.File]::Exists($child.AssociationPath)) 'Normal owned-process cleanup did not reap and remove its identity files.'
        $script:cases++
    }
    finally {
        foreach ($breakpoint in $breakpoints) {
            try { Remove-PSBreakpoint -Id $breakpoint.Id }
            catch { $contractFailures.Add('breakpoint-removal') }
        }
        foreach ($record in $caseServers) {
            if (-not $record.Disposed) {
                try { Stop-OwnedReadinessServer $record }
                catch { $contractFailures.Add('registered-process-cleanup') }
            }
        }
        foreach ($child in $startedProcesses) {
            if (@($caseServers | Where-Object { [object]::ReferenceEquals($_.Process, $child) }).Count -eq 0) {
                try {
                    if (-not $child.HasExited) { $child.Kill($true) }
                    Assert-Contract ($child.WaitForExit(5000)) 'Unregistered original lifecycle child did not reap.'
                    $child.Dispose()
                }
                catch { $contractFailures.Add('unregistered-process-cleanup') }
            }
        }
        Assert-Contract ($contractFailures.Count -eq 0) 'Owned process lifecycle contract cleanup failed.'
    }
}

function New-CaseRoot([string] $Scenario) {
    $caseRoot = Join-Path $temporaryRoot ([Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($caseRoot) | Out-Null
    foreach ($name in @('commands', 'drivers', 'created', 'active', 'processes', 'network-checks')) {
        [IO.Directory]::CreateDirectory((Join-Path $caseRoot $name)) | Out-Null
    }
    [Environment]::SetEnvironmentVariable('CONDUIT_COLLECTOR_TEST_ROOT', $caseRoot)
    [Environment]::SetEnvironmentVariable('CONDUIT_COLLECTOR_TEST_SCENARIO', $Scenario)
    return $caseRoot
}

function Get-TestPort {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $adjacent = $null
        $listener.Start()
        try {
            $port = $listener.LocalEndpoint.Port
            if ($port -gt 65534) { continue }
            $adjacent = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port + 1)
            $adjacent.Start()
            return $port
        }
        catch [Net.Sockets.SocketException] { }
        finally { if ($null -ne $adjacent) { $adjacent.Stop() }; $listener.Stop() }
    }
    throw 'Could not reserve adjacent owned fixture ports.'
}

function Get-CaseFailureDiagnostic([string] $CaseRoot, [object[]] $Commands, [object[]] $Drivers,
    [object[]] $Created, [string] $Failure) {
    $operations = @($Commands | ForEach-Object {
        if ($null -ne $_ -and $null -ne $_.arguments -and $_.arguments.Count -gt 0) {
            [string]$_.arguments[0] + $(if ($_.arguments[0] -cin @('image', 'manifest')) { ':' + $_.arguments[1] } else { '' })
        }
    })
    $safeFailure = 'unclassified'
    # Preserve the old console-classification oracle while accepting only the
    # new closed diagnostic suffix. The full sidecar is validated separately.
    $classificationFailure = $Failure -creplace (' \[phase=[a-z-]+;reason=[a-z-]+;runtime=(?:unknown|jit|native-aot);' +
        'sample=(?:none|warmup|recorded);pair=(?:none|[1-9]|1[0-9]|2[01]);' +
        'owned-cleanup=(?:not-attempted|confirmed|verification-failed);owned-cleanup-reason=(?:none|shutdown-failed|verification-failed);' +
        'private-cleanup=(?:not-attempted|confirmed|verification-failed);private-cleanup-reason=(?:none|cleanup-failed)\]'), ''
    foreach ($allowed in @('Private command failed.', 'Private command failed and its cleanup failed.',
        'Private command cleanup failed.', 'Benchmark variant failed.',
        'Benchmark variant failed and owned container cleanup failed.',
        'Owned benchmark container cleanup failed.', 'Container ownership verification failed.', 'Benchmark fixture network binding failed.')) {
        if ($classificationFailure -ceq ($allowed + ' No qualification was recorded.')) { $safeFailure = $allowed }
    }
    foreach ($tag in @('command-deadline', 'poll-command-deadline', 'poll-callback', 'poll-memory-sample', 'poll-memory-value')) {
        foreach ($prefix in @('Private command failed.', 'Private command failed and its cleanup failed.')) {
            $allowed = $prefix + ' [stage=' + $tag + ']'
            if ($classificationFailure -ceq ($allowed + ' No qualification was recorded.')) { $safeFailure = $allowed }
        }
    }
    $traces = @{}
    foreach ($entry in @(@('driver', 'driver-timeout-trace.json'), @('run', 'docker-run-trace.json'))) {
        $path = Join-Path $CaseRoot ('processes/' + $entry[1])
        $traces[$entry[0]] = if ([IO.File]::Exists($path)) { [IO.File]::ReadAllText($path) } else { 'absent' }
    }
    return [pscustomobject]@{
        Classification = $safeFailure
        Text = 'driver records ' + $Drivers.Count + ', created records ' + $Created.Count +
            ', operations ' + ($operations -join '|') + ', controlled failure ' + $safeFailure +
            ', safe driver trace ' + $traces.driver + ', safe run trace ' + $traces.run + '.'
    }
}

function Assert-CollectorFailureDiagnostic($Metadata, [string] $Scenario, [string] $Failure) {
    $path = Join-Path ([IO.Path]::GetDirectoryName($Metadata.attemptPath)) 'diagnostics.json'
    Assert-Contract ([IO.File]::Exists($path) -and [IO.FileInfo]::new($path).Length -le 4096) 'Failed collection did not retain bounded diagnostics.'
    $diagnostic = Get-NativeBenchmarkAttemptFailureDiagnostic (Read-AttemptContractJson $path)
    Assert-Contract ($diagnostic.attemptId -ceq $Metadata.attempt.attemptId -and $diagnostic.service -ceq $Metadata.attempt.service -and
        $null -eq $Metadata.attempt.reportSha256 -and -not [IO.File]::Exists((Join-Path ([IO.Path]::GetDirectoryName($Metadata.attemptPath)) 'benchmark.json'))) 'Failure diagnostic changed failed eligibility or created a report archive.'
    $phase = 'readiness'; $reason = 'readiness-deadline'; $runtime = 'jit'; $sample = 'warmup'; $pair = $null
    $ownedStatus = 'confirmed'; $ownedReason = 'none'; $privateStatus = 'confirmed'; $privateReason = 'none'
    switch ($Scenario) {
        'diagnostic-readiness-cleanup' { $runtime = 'native-aot'; $sample = 'recorded'; $pair = 1; $ownedStatus = 'verification-failed'; $ownedReason = 'verification-failed' }
        'diagnostic-private-cleanup' { $phase = 'private-cleanup'; $reason = 'private-cleanup-failed'; $runtime = 'unknown'; $sample = 'none'; $privateStatus = 'verification-failed'; $privateReason = 'cleanup-failed' }
        'diagnostic-readiness-private-cleanup' { $privateStatus = 'verification-failed'; $privateReason = 'cleanup-failed' }
        'diagnostic-private-child' { $phase = 'workload-result'; $reason = 'unknown' }
        'diagnostic-private-config' { $phase = 'workload-result'; $reason = 'unknown'; $privateReason = 'cleanup-failed' }
        'diagnostic-poll-memory' { $phase = 'workload'; $reason = 'poll-memory-sample' }
        { $_.StartsWith('diagnostic-probe-', [StringComparison]::Ordinal) } { $phase = 'workload'; $reason = 'probe-exit' }
    }
    Assert-Contract ($diagnostic.phase -ceq $phase -and $diagnostic.reason -ceq $reason -and $diagnostic.runtime -ceq $runtime -and
        $diagnostic.sampleKind -ceq $sample -and $diagnostic.pair -ceq $pair -and
        $diagnostic.ownedContainerCleanup.status -ceq $ownedStatus -and $diagnostic.ownedContainerCleanup.reason -ceq $ownedReason -and
        $diagnostic.privateFileCleanup.status -ceq $privateStatus -and $diagnostic.privateFileCleanup.reason -ceq $privateReason) (
        'Persisted diagnostic differs for ' + $Scenario + ': phase=' + $diagnostic.phase + ';reason=' + $diagnostic.reason +
        ';runtime=' + $diagnostic.runtime + ';sample=' + $diagnostic.sampleKind + ';owned=' + $diagnostic.ownedContainerCleanup.status +
        '/' + $diagnostic.ownedContainerCleanup.reason + ';private=' + $diagnostic.privateFileCleanup.status + '/' + $diagnostic.privateFileCleanup.reason + '.')
    $pairText = if ($null -eq $pair) { 'none' } else { [string]$pair }
    $expectedSuffix = '[phase=' + $phase + ';reason=' + $reason + ';runtime=' + $runtime + ';sample=' + $sample + ';pair=' + $pairText +
        ';owned-cleanup=' + $ownedStatus + ';owned-cleanup-reason=' + $ownedReason + ';private-cleanup=' + $privateStatus + ';private-cleanup-reason=' + $privateReason + ']'
    Assert-Contract ($Failure.Contains($expectedSuffix)) 'Public summary lost the same closed primary and independent cleanup codes.'
    $expectedAggregate = $Scenario -cin @('diagnostic-probe-partial', 'diagnostic-probe-all-failed')
    Assert-Contract ($diagnostic.schemaVersion -eq 2 -and ($null -ne $diagnostic.workloadAggregate) -eq $expectedAggregate) (
        'Failed stdout aggregate availability differs for ' + $Scenario + '; expected ' + $expectedAggregate + ', observed ' + ($null -ne $diagnostic.workloadAggregate) + '.')
    if ($expectedAggregate) {
        $aggregate = $diagnostic.workloadAggregate
        $completed = if ($Scenario -ceq 'diagnostic-probe-all-failed') { 0 } else { 3 }
        $id = if ($Metadata.attempt.service -ceq 'admin') { 'admin-setting-read' } else { 'gateway-chat-nonstream' }
        Assert-Contract ($aggregate.Count -eq 9 -and $aggregate.phase -ceq 'measured' -and $aggregate.id -ceq $id -and
            $aggregate.requested -eq 4 -and $aggregate.executed -eq 4 -and $aggregate.completed -eq $completed -and
            $aggregate.failures -eq (4 - $completed) -and $aggregate.warmupRequests -eq 2 -and
            $aggregate.concurrency -eq 2 -and $aggregate.timeoutSeconds -eq 5) 'Failed client completion counts were not retained exactly.'
    }
}

function Invoke-CollectorCase([string] $Service, [string] $Scenario, [string] $ExpectedFailure,
    [string] $ExistingOutputPath = '', [switch] $CaptureResult, [switch] $StrictCaller) {
    $caseRoot = New-CaseRoot $Scenario
    $networkCase = $Scenario.StartsWith('network-', [StringComparison]::Ordinal)
    $fixtureNetworkName = 'owned-private-fixture-' + [Guid]::NewGuid().ToString('N')
    $fixtureNetworkId = '9' * 64
    if ($networkCase) {
        # Network records are independent of the active-container oracle.
        [IO.File]::WriteAllText((Join-Path $caseRoot 'fixture-network.json'), (ConvertTo-Json -InputObject @{
            Id = $fixtureNetworkId; Name = $fixtureNetworkName; Driver = 'bridge'; Scope = 'local' } -Compress))
    }
    $indexedJit = $Scenario -cin @('jit-index', 'jit-index-ambiguous', 'jit-index-missing', 'jit-index-malformed')
    $jitRootImage = 'example.invalid/conduit-jit@sha256:' + $(if ($indexedJit) { '4' * 64 } else { '1' * 64 })
    $jitSelectedImage = 'example.invalid/conduit-jit@sha256:' + ('2' * 64)
    $expectedJitPullBytes = if ($indexedJit) { 518 } else { 700 }
    $packageFixture = $script:packages[$Service]
    $selectedFixture = switch ($Scenario) {
        'package-wrong-service' { $script:packages['gateway'] }
        'package-cross-build' { $script:packages['admin-other'] }
        'package-dirty' { $script:packages['admin-dirty'] }
        'package-unavailable' { $script:packages['admin-unavailable'] }
        'package-hold' { $script:packages['admin-hold'] }
        default { $packageFixture }
    }
    $packageDirectory = Join-Path $caseRoot 'package'
    Copy-NativeBenchmarkPackagingFixture $selectedFixture $packageDirectory
    [IO.File]::WriteAllText((Join-Path $caseRoot 'native-image.json'), (ConvertTo-Json -InputObject $packageFixture.image -Depth 32))
    [IO.File]::WriteAllText((Join-Path $caseRoot 'native-selected.json'), (ConvertTo-Json -InputObject $packageFixture.oci.selectedDescriptor -Depth 32))
    [IO.File]::WriteAllText((Join-Path $caseRoot 'expected-admin.key'), $privateKey)
    [IO.File]::WriteAllText((Join-Path $caseRoot 'expected-redis.url'),
        ('redis://:' + $privateRedisPassword + '@host.docker.internal:6379'))
    $caseEnvironmentPath = Join-Path $caseRoot 'input.env'
    $caseEnvironmentLines = @('database_url=postgresql://ignored:ignored@localhost:1/ignored',
        'redis_url=redis://localhost:1/0', 'conduit_migration_mode=Skip',
        'conduitllm__messaging__wolverine__autoprovision=true',
        'aspnetcore_urls=http://127.0.0.1:9999', 'aspnetcore_http_ports=9999', 'CONDUIT_LOG_LEVEL=Warning')
    $caseEnvironmentLines += if ($Service -eq 'admin') { 'conduit_api_to_api_backend_auth_key=wrong-admin-fixture-key' }
        else { 'CONDUIT_API_TO_API_BACKEND_AUTH_KEY=gateway-master-fixture-key' }
    if ($Scenario -eq 'case-duplicate-environment') { $caseEnvironmentLines += 'DATABASE_URL=postgresql://duplicate.invalid/ignored' }
    [IO.File]::WriteAllLines($caseEnvironmentPath, $caseEnvironmentLines)
    $configurationPaths = if ($Service -eq 'admin') { @($adminConfiguration) }
        else { @($nonstreamConfiguration, $streamConfiguration) }
    $probePath = if ($Service -eq 'admin') { $nativeDriver } else { $managedProbe }
    $parameters = @{
        Service = $Service
        JitImage = $jitRootImage
        NativeImage = if ($Scenario -cin @('package-dirty', 'package-unavailable', 'package-hold')) { $selectedFixture.requestedImage } else { $packageFixture.requestedImage }
        NativePackagingDirectory = $packageDirectory
        DatabaseUrl = 'postgresql://fixture:' + $privateDatabasePassword + '@127.0.0.1:5432/fixture'
        RedisUrl = 'redis://:' + $privateRedisPassword + '@127.0.0.1:6379'
        WorkloadConfigurationPaths = $configurationPaths
        WorkloadProbePath = $probePath
        EnvironmentEvidencePath = $environmentPath
        ContainerEnvironmentPath = $caseEnvironmentPath
        PairedRuns = 5
        OutputPath = if ([string]::IsNullOrEmpty($ExistingOutputPath)) { Join-Path $caseRoot 'report.json' } else { $ExistingOutputPath }
        Port = (Get-TestPort)
        DockerCommandPath = $dockerCommand
        DotnetCommandPath = $dotnetCommand
        DockerCommandTimeoutSeconds = 5
        WorkloadTimeoutSeconds = 10
        ReadinessTimeoutSeconds = 10
        ShutdownTimeoutSeconds = 1
        CleanupTimeoutSeconds = 5
    }
    if ($Scenario -eq 'package-absent') { $parameters.Remove('NativePackagingDirectory') }
    if ($Scenario -eq 'legacy-baseline') { $parameters.NativeBaselinePath = $environmentPath }
    if ($Scenario -eq 'candidate-config') { $parameters.NativeImage = 'example.invalid/conduit-' + $Service + '@' + $packageFixture.oci.configDigest }
    if ($Scenario -eq 'candidate-selected-swap') { $parameters.NativeImage = 'example.invalid/conduit-' + $Service + '@' + $packageFixture.oci.selectedDescriptor.digest }
    if ($Scenario -eq 'driver-timeout') { $parameters.WorkloadTimeoutSeconds = 5 }
    if ($Scenario -cin @('diagnostic-readiness', 'diagnostic-readiness-cleanup', 'diagnostic-readiness-private-cleanup')) {
        $parameters.ReadinessTimeoutSeconds = 1
    }
    if ($Scenario -eq 'even-pairs') { $parameters.PairedRuns = 6 }
    if ($networkCase) { $parameters.DockerNetwork = $fixtureNetworkName }
    if ($Scenario -ceq 'network-invalid-name') { $parameters.DockerNetwork = '-' + $privateKey }
    $priorAliasHash = if ([IO.File]::Exists($parameters.OutputPath)) { Get-AttemptContractHash $parameters.OutputPath } else { $null }
    $priorEvidence = $null
    $priorLatestId = $null
    if ($null -ne $priorAliasHash) {
        # Historical alias inspection is only the test's preservation oracle.
        # The public reader must still reject a later failed or running attempt.
        $priorReport = Read-AttemptContractJson $parameters.OutputPath
        $priorArchive = Join-Path (Join-Path ($parameters.OutputPath + '.attempts') $priorReport.collection.attemptId) 'benchmark.json'
        $priorEvidence = [ordered]@{ reportPath = $priorArchive; reportSha256 = Get-AttemptContractHash $priorArchive }
        $priorLatestId = (Read-AttemptContractJson (Join-Path ($parameters.OutputPath + '.attempts') 'latest.json')).attemptId
    }
    $caseServers = [Collections.Generic.List[object]]::new()
    $casePrimaryFailure = $null
    $caseStage = 'readiness-setup'
    $failureDiagnostic = $null
    $collectorToInvoke = $collector
    $privateCleanupMarker = Join-Path $caseRoot 'injected-private-cleanup.path'
    if ($Scenario -cin @('diagnostic-private-cleanup', 'diagnostic-readiness-private-cleanup', 'diagnostic-private-config')) {
        $copyRoot = Join-Path $caseRoot 'collector-source'
        [IO.Directory]::CreateDirectory($copyRoot) | Out-Null
        foreach ($sourceFile in Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter '*.ps1') {
            Copy-Item -LiteralPath $sourceFile.FullName -Destination (Join-Path $copyRoot $sourceFile.Name)
        }
        $collectorToInvoke = Join-Path $copyRoot 'measure-native-container.ps1'
        $copy = [IO.File]::ReadAllText($collectorToInvoke)
        $delete = if ($Scenario -ceq 'diagnostic-private-config') { 'Remove-Item -LiteralPath $configurationPath -Force' }
            else { 'Remove-Item -LiteralPath $resolved -Recurse -Force' }
        Assert-Contract ($copy.Split($delete, [StringSplitOptions]::None).Length -eq 2) 'Exact private cleanup fault seam differs.'
        $markerValue = if ($Scenario -ceq 'diagnostic-private-config') { '$configurationPath' } else { '$resolved' }
        $fault = '[IO.File]::WriteAllText((Join-Path $env:CONDUIT_COLLECTOR_TEST_ROOT ''injected-private-cleanup.path''), ' + $markerValue +
            '); throw (''private-cleanup-sentinel:'' + $env:CONDUIT_NATIVE_BENCHMARK_API_KEY)'
        [IO.File]::WriteAllText($collectorToInvoke, $copy.Replace($delete, $fault), [Text.UTF8Encoding]::new($false))
    }
    try {
    if ($Scenario -cnotin @('even-pairs', 'case-duplicate-environment', 'revision-mismatch', 'package-absent', 'legacy-baseline',
        'package-wrong-service', 'package-cross-build', 'package-dirty', 'package-unavailable', 'package-hold', 'candidate-config', 'candidate-selected-swap')) {
        # Readiness listeners belong to this test process. Spawning a persistent
        # listener inside captured Docker would retain inherited pipe handles on
        # Windows, preventing that short-lived command from reaching EOF.
        foreach ($healthPort in @($parameters.Port, ($parameters.Port + 1))) {
            $serverNonce = [Guid]::NewGuid().ToString('N')
            $identityPath = Join-Path $caseRoot ('processes/health-' + $healthPort + '-' + $serverNonce + '.identity')
            $associationPath = Join-Path $caseRoot ('processes/health-' + $healthPort + '.association')
            Assert-Contract (-not [IO.File]::Exists($associationPath)) 'Duplicate readiness port association was rejected.'
            $start = [Diagnostics.ProcessStartInfo]::new($env:CONDUIT_COLLECTOR_TEST_PWSH)
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $childArguments = ConvertTo-Json -InputObject @([string]$healthPort, $identityPath) -Compress
            foreach ($argument in @('-NoProfile', '-File', $fixture, '-Mode', 'health', '-ChildArgumentsJson', $childArguments)) {
                $start.ArgumentList.Add($argument)
            }
            $serverProcess = [Diagnostics.Process]::Start($start)
            $unusedServer = Register-OwnedReadinessServer $serverProcess $identityPath $associationPath $caseServers
        }
    }
    $before = @{}
    foreach ($name in $environmentNames) { $before[$name] = [Environment]::GetEnvironmentVariable($name) }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $failure = $null
    $publicOutput = ''
    $publicError = ''
    $publicRecords = [Collections.Generic.List[object]]::new()
    $caseStage = 'collector-invocation'
    try {
        # Inherit StrictMode only at the real collector boundary. Fixture setup,
        # child processes and the independent result oracles retain their scope.
        & {
            if ($StrictCaller) { Set-StrictMode -Version Latest }
            & $collectorToInvoke @parameters
        } *>&1 | ForEach-Object { $publicRecords.Add($_) }
    }
    catch { $failure = $_.Exception.Message; $publicError = $_ | Out-String }
    $publicOutput = $publicRecords | Out-String
    $caseStage = 'privacy-assertions'
    foreach ($name in $environmentNames) {
        Assert-Contract ([Environment]::GetEnvironmentVariable($name) -ceq $before[$name]) ('Collector mutated parent environment: ' + $name)
    }
    foreach ($secret in @($privateKey, $privateDatabasePassword, $privateRedisPassword, 'private-child-diagnostic', 'private-cleanup-sentinel')) {
        Assert-Contract (-not $publicOutput.Contains($secret) -and -not $publicError.Contains($secret) -and
            ($null -eq $failure -or -not $failure.Contains($secret))) 'Collector exposed private command configuration.'
    }
    if ($networkCase) {
        Assert-Contract (-not $publicOutput.Contains($fixtureNetworkName) -and -not $publicError.Contains($fixtureNetworkName) -and
            ($null -eq $failure -or -not $failure.Contains($fixtureNetworkName))) 'Public diagnostics exposed a private fixture network name.'
    }
    $commands = Read-Records (Join-Path $caseRoot 'commands')
    $drivers = Read-Records (Join-Path $caseRoot 'drivers')
    $created = Read-Records (Join-Path $caseRoot 'created')
    $failureDiagnostic = Get-CaseFailureDiagnostic $caseRoot $commands $drivers $created $failure
    $caseStage = 'fixture-record-assertions'
    foreach ($container in $created) {
        Assert-Contract ($container.canonicalEnvironment -and $container.backendCredentialCorrect) 'Mandatory environment canonicalization or independent backend credential preservation failed.'
    }
    foreach ($command in $commands) {
        Assert-Contract (-not $command.benchmarkCredentialPresent) 'Benchmark credential reached Docker.'
        if ($command.arguments[0] -ceq 'run') {
            $environmentIndex = [Array]::IndexOf([object[]]$command.arguments, '--env-file')
            Assert-Contract ($environmentIndex -ge 0) 'Docker did not receive a private environment file.'
            $environmentFile = [string]$command.arguments[$environmentIndex + 1]
            if ($Scenario -cin @('diagnostic-private-cleanup', 'diagnostic-readiness-private-cleanup')) {
                Assert-Contract ([IO.File]::Exists($environmentFile)) 'Injected private cleanup failure was not reached.'
            }
            else {
                Assert-Contract (-not [IO.File]::Exists($environmentFile) -and
                    -not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($environmentFile))) 'Collector retained its private container environment.'
            }
            Assert-Contract (-not $publicOutput.Contains($environmentFile) -and -not $publicError.Contains($environmentFile) -and
                ($null -eq $failure -or -not $failure.Contains($environmentFile))) 'Public diagnostics exposed a private environment path.'
        }
    }
    foreach ($driver in $drivers) {
        Assert-Contract $driver.benchmarkCredentialPresent 'Driver did not receive its private credential.'
        Assert-Contract (-not [IO.File]::Exists($driver.configurationPath)) 'Collector retained a private workload configuration.'
        Assert-Contract (-not $publicOutput.Contains($driver.configurationPath) -and -not $publicError.Contains($driver.configurationPath) -and
            ($null -eq $failure -or -not $failure.Contains($driver.configurationPath))) 'Public diagnostics exposed a private workload configuration path.'
    }
    $caseStage = 'failure-oracle'
    $networkCommands = @($commands | Where-Object { $_.arguments[0] -ceq 'network' })
    if ($networkCase) {
        $expectedInspections = if ($Scenario -ceq 'network-invalid-name') { 0 } else { 1 }
        Assert-Contract ($networkCommands.Count -eq $expectedInspections -and
            @($networkCommands | Where-Object { $_.arguments.Count -ne 5 -or $_.arguments[1] -cne 'inspect' -or
                $_.arguments[2] -cne '--format' -or $_.arguments[3] -cne '{{json .}}' -or $_.arguments[4] -cne $fixtureNetworkName }).Count -eq 0) 'Collector mutated a caller-managed network or did not inspect its exact name once.'
    }
    else {
        Assert-Contract ($networkCommands.Count -eq 0 -and
            @($commands | Where-Object { $_.arguments[0] -ceq 'run' -and $_.arguments -ccontains '--network' }).Count -eq 0) 'Default transport acquired a network dependency.'
    }
    if (-not [string]::IsNullOrEmpty($ExpectedFailure)) {
        Assert-Contract ($null -ne $failure -and $failure.Contains($ExpectedFailure)) ('Expected controlled collector failure was absent: ' + $Scenario)
        if ($null -eq $priorAliasHash) {
            Assert-Contract (-not [IO.File]::Exists($parameters.OutputPath)) 'Failed collector recorded a qualification.'
        }
        else {
            Assert-Contract ((Get-AttemptContractHash $parameters.OutputPath) -ceq $priorAliasHash -and
                (Get-AttemptContractHash $priorEvidence.reportPath) -ceq $priorEvidence.reportSha256) 'Failed rerun changed a prior successful alias or archive.'
        }
        $failedAttempt = Get-AttemptContractMetadata $parameters.OutputPath $Service 'failed'
        Assert-AttemptContractReaderRejected $parameters.OutputPath $Service
        if ($networkCase) {
            $diagnosticPath = Join-Path ([IO.Path]::GetDirectoryName($failedAttempt.attemptPath)) 'diagnostics.json'
            $diagnostic = Get-NativeBenchmarkAttemptFailureDiagnostic (Read-AttemptContractJson $diagnosticPath)
            $beforeStart = $Scenario -cin @('network-invalid-name', 'network-malformed', 'network-foreign-name', 'network-nonbridge')
            $finalAttachment = $Scenario -ceq 'network-id-change'
            Assert-Contract ($diagnostic.reason -ceq 'network-binding' -and
                $diagnostic.phase -ceq $(if ($beforeStart) { 'configuration' } elseif ($finalAttachment) { 'collection-validation' } else { 'container-start' }) -and
                $diagnostic.runtime -ceq $(if ($beforeStart) { 'unknown' } else { 'jit' }) -and
                $created.Count -eq $(if ($beforeStart) { 0 } else { 1 }) -and
                $drivers.Count -eq $(if ($finalAttachment) { if ($Service -ceq 'gateway') { 2 } else { 1 } } else { 0 }) -and
                (Read-Records (Join-Path $caseRoot 'active')).Count -eq 0) 'Network binding rejection occurred after an unexpected workload or lost its closed diagnostic.'
            foreach ($file in Get-ChildItem -LiteralPath ($parameters.OutputPath + '.attempts') -File -Recurse -Filter '*.json') {
                Assert-Contract (-not [IO.File]::ReadAllText($file.FullName).Contains($fixtureNetworkName)) 'Retained diagnostics exposed a private fixture network.'
            }
        }
        if ($Scenario.StartsWith('diagnostic-', [StringComparison]::Ordinal)) {
            Assert-CollectorFailureDiagnostic $failedAttempt $Scenario $failure
            Assert-Contract ((Read-Records (Join-Path $caseRoot 'active')).Count -eq 0) 'A diagnostic case left an owned fake container.'
            if ($Scenario -cin @('diagnostic-readiness', 'diagnostic-readiness-private-cleanup')) {
                Assert-Contract ($created.Count -eq 1 -and $drivers.Count -eq 0 -and
                    [IO.File]::Exists((Join-Path $caseRoot 'readiness-failure-observed'))) 'Readiness failure did not occur before its first workload.'
            }
            elseif ($Scenario -ceq 'diagnostic-readiness-cleanup') {
                Assert-Contract ($created.Count -eq 4 -and $drivers.Count -eq 3 -and
                    [IO.File]::Exists((Join-Path $caseRoot 'cleanup-verification-injected'))) 'Recorded native readiness or post-removal verification failure was not reached.'
            }
            elseif ($Scenario -ceq 'diagnostic-private-cleanup') {
                Assert-Contract ($created.Count -eq 12 -and $drivers.Count -eq 12) 'Private-only cleanup fault occurred before complete successful workload collection.'
            }
            elseif ($Scenario -ceq 'diagnostic-poll-memory') {
                foreach ($name in @('diagnostic-workload.identity', 'diagnostic-descendant.identity')) {
                    $identity = Join-Path $caseRoot ('processes/' + $name)
                    Assert-Contract ([IO.File]::Exists($identity)) 'Nested poll did not reach a latched sleeping workload and descendant.'
                    Test-OwnedProcessStopped $identity
                }
                Assert-Contract ($watch.Elapsed.TotalSeconds -lt 25 -and $created.Count -eq 1 -and $drivers.Count -eq 1) 'Nested poll failure did not remain bounded in its first workload.'
            }
            if ($Scenario -cin @('diagnostic-private-cleanup', 'diagnostic-readiness-private-cleanup', 'diagnostic-private-config')) {
                Assert-Contract ([IO.File]::Exists($privateCleanupMarker)) 'Exact private cleanup fault was not invoked.'
            }
            foreach ($file in Get-ChildItem -LiteralPath ($parameters.OutputPath + '.attempts') -File -Recurse -Filter '*.json') {
                $text = [IO.File]::ReadAllText($file.FullName)
                foreach ($driver in $drivers) { Assert-Contract (-not $text.Contains($driver.configurationPath)) 'Retained diagnostics exposed a private workload path.' }
                foreach ($command in $commands | Where-Object { $_.arguments[0] -ceq 'run' }) {
                    $index = [Array]::IndexOf([object[]]$command.arguments, '--env-file')
                    Assert-Contract (-not $text.Contains([string]$command.arguments[$index + 1])) 'Retained diagnostics exposed a private environment path.'
                }
            }
        }
        if ($null -ne $priorEvidence) {
            Assert-Contract ($failedAttempt.latest.attemptId -cne $priorLatestId) 'Failed rerun did not supersede the prior latest attempt.'
        }
        if ($Scenario -in @('even-pairs', 'case-duplicate-environment')) {
            Assert-Contract ($commands.Count -eq 0 -and $drivers.Count -eq 0) 'Even paired runs reached external commands.'
        }
        elseif ($Scenario -cin @('foreign-label', 'shutdown-foreign-label')) {
            Assert-Contract (@($commands | Where-Object { $_.arguments[0] -ceq 'rm' }).Count -eq 0) 'Collector removed a container with a foreign label.'
            if ($Scenario -ceq 'foreign-label') {
                Assert-Contract (@($commands | Where-Object { $_.arguments[0] -ceq 'stop' }).Count -eq 0) 'Collector stopped a container with a foreign label.'
            }
            Assert-Contract ((Read-Records (Join-Path $caseRoot 'active')).Count -eq 1) 'Foreign-label rejection did not leave the fake container intact.'
        }
        elseif ($Scenario -eq 'cleanup-failure') {
            Assert-Contract ($failure.Contains('cleanup failed')) 'Collector lost combined primary and cleanup failure.'
        }
        else {
            Assert-Contract ((Read-Records (Join-Path $caseRoot 'active')).Count -eq 0) 'Failed collector left an owned fake container.'
        }
        if ($Scenario -cin @('shutdown-command-failure', 'shutdown-forced-exit', 'shutdown-still-running', 'shutdown-invalid-exit')) {
            $stops = @($commands | Where-Object { $_.arguments[0] -ceq 'stop' })
            $removals = @($commands | Where-Object { $_.arguments[0] -ceq 'rm' })
            Assert-Contract ($stops.Count -eq 1 -and $removals.Count -eq 1 -and
                $removals[0].arguments.Count -eq 3 -and $removals[0].arguments[1] -ceq '--force' -and
                $stops[0].ordinal -lt $removals[0].ordinal -and
                $stops[0].arguments[-1] -ceq $removals[0].arguments[-1]) 'Failed shutdown was not rejected and reaped by its verified exact ID.'
        }
        if ($Scenario -eq 'child-string-schema') {
            Assert-Contract ($created.Count -eq 1 -and $drivers.Count -eq 1 -and
                $failureDiagnostic.Classification -ceq 'Benchmark variant failed.') 'String child schema was not rejected after an actual authenticated driver/container run.'
        }
        if ($Scenario -eq 'unknown-create') {
            $nameInspection = @($commands | Where-Object { $_.arguments[0] -ceq 'inspect' -and $_.arguments[-1] -ceq $created[0].name })
            Assert-Contract ($nameInspection.Count -ge 1) 'Unknown create outcome was not recovered by exact owned name.'
            Assert-Contract (@($commands | Where-Object { $_.arguments[0] -ceq 'rm' -and $_.arguments[-1] -ceq $created[0].id }).Count -eq 1) 'Unknown create outcome did not remove the verified exact ID.'
        }
        if ($Scenario -eq 'driver-timeout') {
            Assert-Contract ($watch.Elapsed.TotalSeconds -lt 20) 'Collector workload timeout exceeded its bounded cleanup budget.'
            $identityPath = Join-Path $caseRoot 'processes/driver-descendant.identity'
            Assert-Contract ([IO.File]::Exists($identityPath)) ('Timeout driver did not start its latched descendant; ' + $failureDiagnostic.Text)
            Test-OwnedProcessStopped $identityPath
            Assert-Contract ($failureDiagnostic.Classification -ceq 'Private command failed. [stage=command-deadline]') 'Timeout fixture failed for a reason other than the bounded command deadline.'
        }
        if ($Scenario -cin @('package-absent', 'legacy-baseline', 'package-wrong-service', 'package-cross-build',
            'package-dirty', 'package-unavailable', 'package-hold', 'candidate-config', 'candidate-selected-swap')) {
            Assert-Contract ($created.Count -eq 0 -and $drivers.Count -eq 0) 'Publication rejection reached a measured container or workload.'
        }
        if ($indexedJit) {
            $manifestCommands = @($commands | Where-Object { $_.arguments[0] -ceq 'manifest' })
            Assert-Contract ($manifestCommands.Count -eq 1 -and $manifestCommands[0].arguments.Count -eq 3 -and
                $manifestCommands[0].arguments[1] -ceq 'inspect' -and $manifestCommands[0].arguments[2] -ceq $jitRootImage -and
                $created.Count -eq 0 -and $drivers.Count -eq 0) 'Invalid JIT index reached a selected manifest, container or workload.'
        }
        if ($Scenario -eq 'native-selected-mismatch') {
            Assert-Contract ($created.Count -eq 2 -and $drivers.Count -eq $(if ($Service -eq 'gateway') { 2 } else { 1 }) -and
                (Read-Records (Join-Path $caseRoot 'active')).Count -eq 0 -and
                @($commands | Where-Object { $_.arguments[0] -ceq 'rm' }).Count -eq 2) 'Selected-manifest rejection did not clean both independently owned warm containers.'
        }
        if ($Scenario -eq 'package-late-mutation') {
            Assert-Contract ($created.Count -eq 12 -and $drivers.Count -eq $(if ($Service -eq 'gateway') { 24 } else { 12 }) -and
                [IO.File]::Exists((Join-Path $caseRoot 'package-mutated')) -and
                (Read-Records (Join-Path $caseRoot 'active')).Count -eq 0) 'Late package mutation was not rejected after all complete pairs and cleanup.'
        }
        $script:cases++
        if ($CaptureResult) { return ,[ordered]@{ outputPath = $parameters.OutputPath; metadata = $failedAttempt } }
        return
    }
    $caseStage = 'positive-oracle'
    Assert-Contract ($null -eq $failure) ('Successful fake collection failed; ' + $failureDiagnostic.Text)
    $reportText = [IO.File]::ReadAllText($parameters.OutputPath)
    foreach ($secret in @($privateKey, $privateDatabasePassword, $privateRedisPassword, 'private-child-diagnostic')) {
        Assert-Contract (-not $reportText.Contains($secret)) 'Collector retained private command configuration in its report.'
    }
    $attemptEvidence = Assert-AttemptContractSuccess $parameters.OutputPath $Service
    $report = $attemptEvidence.report
    Assert-Contract ($report.methodology.Contains('Caller-managed direct-bridge transport was requested;') -eq $networkCase) 'Methodology did not truthfully describe requested transport.'
    if ($networkCase) {
        Assert-Contract (-not $reportText.Contains($fixtureNetworkName) -and -not $reportText.Contains($fixtureNetworkId)) 'Collector retained private network identity in the report.'
        $networkRuns = @($commands | Where-Object { $_.arguments[0] -ceq 'run' })
        Assert-Contract ($networkRuns.Count -eq 12 -and @($networkRuns | Where-Object {
            $index = [Array]::IndexOf([object[]]$_.arguments, '--network')
            $index -lt 0 -or $_.arguments[$index + 1] -cne $fixtureNetworkId
        }).Count -eq 0 -and @($created | Where-Object networkMode -CNE $fixtureNetworkId).Count -eq 0) 'All twelve warm/measured variants did not use the same pinned network.'
        $networkChecks = Read-Records (Join-Path $caseRoot 'network-checks')
        foreach ($container in $created) {
            Assert-Contract (@($networkChecks | Where-Object { $_.id -ceq $container.id -and $_.running }).Count -ge 2) 'A measured variant omitted initial or final running attachment verification.'
        }
    }
    Assert-Contract ($report.schemaVersion -eq 4 -and $report.service -ceq $Service -and $report.pairedRuns -eq 5 -and $report.samples.Count -eq 5) 'Collector report identity or paired sample count is wrong.'
    Assert-NativeBenchmarkFixtureIdentity $report.nativeEvidence $packageFixture
    Assert-NativeBenchmarkFixtureBinding $report.nativePublish.imageBinding $packageFixture
    Assert-Contract ($report.nativePublish.Count -eq 8 -and $report.nativePublish.service -ceq $(if ($Service -eq 'admin') { 'Admin' } else { 'Gateway' }) -and
        $report.nativePublish.rid -ceq 'linux-x64') 'Collector publication role or complete metrics shape is wrong.'
    foreach ($metric in @('publishDurationMilliseconds', 'applicationPublishDurationMilliseconds', 'healthProbePublishDurationMilliseconds', 'executableBytes', 'runtimeArtifactBytes')) {
        Assert-Contract ($report.nativePublish[$metric] -eq $packageFixture.metrics[$metric]) 'Collector publication metric differs from the sealed complete image.'
    }
    Assert-NativeBenchmarkFixtureBinding $report.native.imageBinding $packageFixture
    Assert-Contract ($created.Count -eq 12 -and (Read-Records (Join-Path $caseRoot 'active')).Count -eq 0) 'Collector did not remove all twelve warm and measured containers.'
    $orderedCreated = @($created | Sort-Object ordinal)
    $expectedOrder = @('jit', 'native-aot', 'jit', 'native-aot', 'native-aot', 'jit',
        'jit', 'native-aot', 'native-aot', 'jit', 'jit', 'native-aot')
    Assert-Contract ((@($orderedCreated.runtime) -join '|') -ceq ($expectedOrder -join '|')) 'Collector did not alternate recorded pair order after the warm pair.'
    $expectedDriverCount = if ($Service -eq 'admin') { 12 } else { 24 }
    Assert-Contract ($drivers.Count -eq $expectedDriverCount) 'Collector omitted an authenticated workload from a variant.'
    Assert-Contract (@($drivers | Where-Object dotnetRoute).Count -eq $(if ($Service -eq 'gateway') { 24 } else { 0 })) 'DLL and direct native workload command routing differ.'
    $removals = @($commands | Where-Object { $_.arguments[0] -ceq 'rm' })
    $stops = @($commands | Where-Object { $_.arguments[0] -ceq 'stop' })
    Assert-Contract ($removals.Count -eq 12) 'Collector did not issue one verified removal per container.'
    Assert-Contract ($stops.Count -eq 12) 'Collector did not stop each warm and measured host.'
    foreach ($removal in $removals) {
        $identity = $removal.arguments[-1]
        $stop = @($stops | Where-Object { $_.arguments[-1] -ceq $identity })
        Assert-Contract ($removal.arguments.Count -eq 2 -and
            $identity -cmatch '^[0-9a-f]{64}$' -and
            @($created | Where-Object id -CEQ $identity).Count -eq 1 -and
            $stop.Count -eq 1 -and $stop[0].ordinal -lt $removal.ordinal -and
            $stop[0].arguments.Count -eq 4 -and $stop[0].arguments[1] -ceq '--timeout' -and
            $stop[0].arguments[2] -ceq '1') 'Collector did not gracefully stop and remove one verified exact container.'
    }
    Assert-Contract ($report.environment.environmentId -ceq 'collector-contract-host' -and
        $report.environment.backingServices.configurationId -ceq 'owned-fixture-settings' -and
        $report.environment.host.memoryBytes -eq 8589934592) 'Collector omitted matched environment evidence.'
    foreach ($variantName in @('jit', 'native')) {
        $variant = $report[$variantName]
        Assert-Contract ($variant.imageRevision -ceq ('a' * 40) -and
            $variant.workloads.Count -eq $configurationPaths.Count) 'Collector median lost image revision or workload identity.'
        $expectedMemory = if ($variantName -eq 'jit') { 1048576 } else { 524288 }
        foreach ($name in @('idleMemoryBytes', 'steadyStateMemoryBytes', 'peakMemoryBytes')) {
            Assert-Contract ($variant[$name] -eq $expectedMemory) 'Unrecorded warm memory contaminated reported medians.'
        }
        foreach ($workload in $variant.workloads) {
            Assert-Contract ($workload.authenticated -and $workload.completed -eq 4 -and $workload.executed -eq 4 -and
                $workload.requested -eq 4 -and $workload.failures -eq 0 -and $workload.warmupRequests -eq 2 -and
                $workload.concurrency -eq 2 -and $workload.p99Milliseconds -eq 3) 'Warm pair or incomplete workload contaminated reported medians.'
            $raw = @($report.samples | ForEach-Object {
                @($_[$variantName].workloads | Where-Object id -CEQ $workload.id)[0]
            })
            foreach ($metric in @('elapsedMilliseconds', 'throughputPerSecond', 'p50Milliseconds', 'p95Milliseconds', 'p99Milliseconds')) {
                $median = @($raw | ForEach-Object { $_[$metric] } | Sort-Object)[2]
                Assert-Contract ($workload[$metric] -eq $median) 'Collector median differs from independent raw-sample recomputation.'
            }
            foreach ($row in $raw) {
                Assert-Contract ($row.inputFingerprint -ceq $workload.inputFingerprint) 'Collector changed input identity across retained samples.'
            }
        }
    }
    foreach ($sample in $report.samples) {
        $expectedPairOrder = if ($sample.pair % 2 -eq 1) { 'jit|native-aot' } else { 'native-aot|jit' }
        Assert-Contract (($sample.order -join '|') -ceq $expectedPairOrder) 'Raw sample pair order is incorrect.'
        Assert-Contract ($sample.jit.pullBytes -eq $expectedJitPullBytes) 'Raw JIT sample lost selected-manifest pull bytes.'
        Assert-NativeBenchmarkFixtureBinding $sample.native.imageBinding $packageFixture
        Assert-Contract ($sample.native.nativeContainerId -is [string] -and $sample.native.nativeContainerId -cmatch '\A[0-9a-f]{64}\z' -and
            @($created | Where-Object { $_.id -ceq $sample.native.nativeContainerId -and $_.runtime -ceq 'native-aot' }).Count -eq 1) 'Raw native sample omitted its actual owned container identity.'
    }
    Assert-Contract ($report.jit.imageBytes -eq 1000 -and $report.native.imageBytes -eq 800 -and
        $report.jit.pullBytes -eq $expectedJitPullBytes -and $report.native.pullBytes -eq $packageFixture.oci.layer.size) 'Collector mixed image and original selected-layer pull sizes.'
    if ($indexedJit) {
        $manifestCommands = @($commands | Where-Object { $_.arguments[0] -ceq 'manifest' } | Sort-Object ordinal)
        Assert-Contract ($manifestCommands.Count -eq 2 -and $manifestCommands[0].arguments.Count -eq 3 -and
            $manifestCommands[1].arguments.Count -eq 3 -and $manifestCommands[0].arguments[1] -ceq 'inspect' -and
            $manifestCommands[1].arguments[1] -ceq 'inspect' -and $manifestCommands[0].arguments[2] -ceq $jitRootImage -and
            $manifestCommands[1].arguments[2] -ceq $jitSelectedImage) 'Collector did not inspect the original JIT index and exact selected manifest in order.'
        $jitRuns = @($commands | Where-Object { $_.arguments[0] -ceq 'run' -and $_.arguments -ccontains 'conduit.native-benchmark.runtime=jit' })
        Assert-Contract ($jitRuns.Count -eq 6 -and
            @($jitRuns | Where-Object { $_.arguments[-1] -cne ('sha256:' + ('1' * 64)) }).Count -eq 0) 'Indexed JIT collection did not launch every warm/measured pair by the observed immutable local image ID.'
    }
    $script:cases++
    if ($CaptureResult) { return ,[ordered]@{ outputPath = $parameters.OutputPath; evidence = $attemptEvidence } }
    }
    catch {
        $casePrimaryFailure = $_.Exception
        throw
    }
    finally {
        $caseCleanupFailures = [Collections.Generic.List[string]]::new()
        foreach ($server in $caseServers) {
            try { Stop-OwnedReadinessServer $server }
            catch {
                $safeDetails = if ($server.CleanupDiagnostics.Count -gt 0) {
                    $server.CleanupDiagnostics -join ','
                }
                else { 'cleanup-unclassified:' + $_.Exception.GetType().FullName }
                $caseCleanupFailures.Add($safeDetails)
            }
        }
        if ([IO.File]::Exists($privateCleanupMarker)) {
            try {
                $privatePath = [IO.Path]::GetFullPath([IO.File]::ReadAllText($privateCleanupMarker))
                $privateDirectory = if ($Scenario -ceq 'diagnostic-private-config') { [IO.Path]::GetDirectoryName($privatePath) } else { $privatePath }
                $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
                Assert-Contract ([IO.Path]::GetDirectoryName($privateDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar) -ceq $expectedParent -and
                    [IO.Path]::GetFileName($privateDirectory) -cmatch '^conduit-benchmark-[0-9a-f]{32}$') 'Injected cleanup fixture refused an unowned private directory.'
                if ([IO.Directory]::Exists($privateDirectory)) { Remove-Item -LiteralPath $privateDirectory -Recurse -Force }
            }
            catch { $caseCleanupFailures.Add('Injected owned private directory cleanup failed.') }
        }
        if ($caseCleanupFailures.Count -gt 0) {
            $primaryType = if ($null -ne $casePrimaryFailure) { $casePrimaryFailure.GetType().FullName } else { 'none' }
            $primaryClassification = if ($null -ne $failureDiagnostic) { $failureDiagnostic.Classification } else { 'unclassified' }
            throw ('Collector case readiness cleanup failed; primary stage ' + $caseStage +
                ', type ' + $primaryType + ', classification ' + $primaryClassification +
                '; cleanup stages ' + ($caseCleanupFailures -join '|') + '.')
        }
    }
}

function New-AttemptContractReport($Source) {
    return ,($Source | ConvertTo-Json -Depth 64 | ConvertFrom-Json -AsHashtable -DateKind String)
}

function Invoke-AttemptLifecycleContracts($SuccessfulCase) {
    $outputPath = Join-Path $temporaryRoot 'attempt-lifecycle.json'
    $sourceReport = $SuccessfulCase.evidence.report
    $first = $null
    $second = $null
    try {
        $startResults = @(Start-NativeBenchmarkAttempt -OutputPath $outputPath -Service 'admin')
        Assert-Contract ($startResults.Count -eq 1 -and $startResults[0] -is [Collections.IDictionary]) 'Attempt start emitted more than its context.'
        $first = $startResults[0]
        $report = New-AttemptContractReport $sourceReport
        Assert-Contract (@(Complete-NativeBenchmarkAttempt -Context $first -Report $report).Count -eq 0) 'Attempt completion emitted incidental output.'
    }
    finally { if ($null -ne $first) { Close-NativeBenchmarkAttempt -Context $first } }
    $firstEvidence = Assert-AttemptContractSuccess $outputPath 'admin'
    $firstArchivePath = Join-Path (Join-Path ($outputPath + '.attempts') $firstEvidence.latest.attemptId) 'benchmark.json'
    $firstArchiveHash = Get-AttemptContractHash $firstArchivePath
    try {
        $second = Start-NativeBenchmarkAttempt -OutputPath $outputPath -Service 'admin'
        $running = Get-AttemptContractMetadata $outputPath 'admin' 'running'
        Assert-Contract ($running.latest.attemptId -cne $firstEvidence.latest.attemptId -and
            (Get-AttemptContractHash $outputPath) -ceq $firstEvidence.reportSha256) 'Starting a rerun changed the prior alias or reused an attempt ID.'
        Assert-AttemptContractReaderRejected $outputPath 'admin'
        $secondReport = New-AttemptContractReport $sourceReport
        Assert-Contract (@(Complete-NativeBenchmarkAttempt -Context $second -Report $secondReport).Count -eq 0) 'Second attempt completion emitted incidental output.'
    }
    finally { if ($null -ne $second) { Close-NativeBenchmarkAttempt -Context $second } }
    $secondEvidence = Assert-AttemptContractSuccess $outputPath 'admin'
    Assert-Contract ($secondEvidence.latest.attemptId -cne $firstEvidence.latest.attemptId -and
        [IO.File]::Exists($firstArchivePath) -and
        (Get-AttemptContractHash $firstArchivePath) -ceq $firstArchiveHash -and
        (Get-AttemptContractHash $outputPath) -ceq $secondEvidence.reportSha256 -and
        @(Get-ChildItem -LiteralPath ($outputPath + '.attempts') -Directory | Where-Object { $_.Name -cmatch '\A[0-9a-f]{32}\z' }).Count -eq 2) 'Successful replacement did not retain both immutable report archives.'
    $script:cases++

    $legacyPath = Join-Path $temporaryRoot 'attempt-legacy-schema3.json'
    $legacyReport = New-AttemptContractReport $sourceReport
    $legacyReport.schemaVersion = 3
    $legacyReport.Remove('collection') | Out-Null
    [IO.File]::WriteAllText($legacyPath, (ConvertTo-Json -InputObject $legacyReport -Depth 64), [Text.UTF8Encoding]::new($false))
    $legacyHash = Get-AttemptContractHash $legacyPath
    Assert-AttemptContractReaderRejected $legacyPath 'admin'
    $legacyContext = $null
    try {
        $legacyContext = Start-NativeBenchmarkAttempt -OutputPath $legacyPath -Service 'admin'
        Complete-NativeBenchmarkAttempt -Context $legacyContext -Report (New-AttemptContractReport $sourceReport)
    }
    finally { if ($null -ne $legacyContext) { Close-NativeBenchmarkAttempt -Context $legacyContext } }
    $legacyEvidence = Assert-AttemptContractSuccess $legacyPath 'admin'
    $retainedLegacyPath = Join-Path (Join-Path ($legacyPath + '.attempts') 'legacy') ($legacyHash + '.json')
    Assert-Contract ([IO.File]::Exists($retainedLegacyPath) -and
        (Get-AttemptContractHash $retainedLegacyPath) -ceq $legacyHash -and
        (Read-AttemptContractJson $retainedLegacyPath).schemaVersion -eq 3 -and
        -not [IO.File]::Exists((Join-Path ([IO.Path]::GetDirectoryName($retainedLegacyPath)) 'attempt.json')) -and
        $legacyEvidence.report.schemaVersion -eq 4) 'Replacing a legacy report did not preserve original bytes without invented successful attempt metadata.'
    Assert-AttemptContractReaderRejected $retainedLegacyPath 'admin'
    $script:cases++

    $lockPath = Join-Path ($outputPath + '.attempts') 'writer.lock'
    $oldLockAttributes = [IO.File]::GetAttributes($lockPath)
    $oldLockMode = if ($IsLinux) { [IO.File]::GetUnixFileMode($lockPath) } else { $null }
    $oldLockHash = Get-AttemptContractHash $lockPath
    $oldLatestHash = Get-AttemptContractHash (Join-Path ($outputPath + '.attempts') 'latest.json')
    try {
        [IO.File]::SetAttributes($lockPath, ($oldLockAttributes -bor [IO.FileAttributes]::ReadOnly))
        if ($IsLinux) {
            $writeBits = [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::GroupWrite -bor [IO.UnixFileMode]::OtherWrite
            [IO.File]::SetUnixFileMode($lockPath, ($oldLockMode -band (-bnot $writeBits)))
            Assert-Contract (([IO.File]::GetUnixFileMode($lockPath) -band $writeBits) -eq 0) 'Retained lock still has Linux write permission bits.'
            # A privileged Linux host may bypass DAC; Windows below independently
            # proves that the read-only attribute rejects a write-access handle.
        }
        if ($IsWindows) {
            $writeRejected = $false
            $unexpectedWriteHandle = $null
            try { $unexpectedWriteHandle = [IO.FileStream]::new($lockPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
            catch [UnauthorizedAccessException] { $writeRejected = $true }
            finally { if ($null -ne $unexpectedWriteHandle) { $unexpectedWriteHandle.Dispose() } }
            Assert-Contract $writeRejected 'Windows retained lock fixture did not enforce read-only write-access rejection.'
        }
        $readOnlyEvidence = Assert-AttemptContractSuccess $outputPath 'admin'
        Assert-Contract ($readOnlyEvidence.reportSha256 -ceq $secondEvidence.reportSha256 -and
            (Get-AttemptContractHash $lockPath) -ceq $oldLockHash -and
            (Get-AttemptContractHash (Join-Path ($outputPath + '.attempts') 'latest.json')) -ceq $oldLatestHash) 'Read-only verification mutated retained attempt evidence.'
    }
    finally {
        [IO.File]::SetAttributes($lockPath, $oldLockAttributes)
        if ($IsLinux) { [IO.File]::SetUnixFileMode($lockPath, $oldLockMode) }
    }
    $script:cases++

    $context = $null
    try {
        $context = Start-NativeBenchmarkAttempt -OutputPath $outputPath -Service 'admin'
        $running = Get-AttemptContractMetadata $outputPath 'admin' 'running'
        $latestHash = Get-AttemptContractHash $running.latestPath
        $concurrentRejected = $false
        $unexpectedContext = $null
        try { $unexpectedContext = Start-NativeBenchmarkAttempt -OutputPath $outputPath -Service 'admin' }
        catch { $concurrentRejected = $true }
        finally { if ($null -ne $unexpectedContext) { Close-NativeBenchmarkAttempt -Context $unexpectedContext } }
        Assert-Contract ($concurrentRejected -and (Get-AttemptContractHash $running.latestPath) -ceq $latestHash -and
            (Get-AttemptContractHash $outputPath) -ceq $secondEvidence.reportSha256) 'Concurrent start did not refuse the held writer lock without mutation.'
        Assert-AttemptContractReaderRejected $outputPath 'admin'
        Assert-Contract (@(Set-NativeBenchmarkAttemptFailed -Context $context -FailureCode 'collection-failed').Count -eq 0) 'Failed attempt publication emitted incidental output.'
    }
    finally { if ($null -ne $context) { Close-NativeBenchmarkAttempt -Context $context } }
    $failed = Get-AttemptContractMetadata $outputPath 'admin' 'failed'
    Assert-Contract ($failed.attempt.failureCode -ceq 'collection-failed' -and
        (Get-AttemptContractHash $outputPath) -ceq $secondEvidence.reportSha256) 'A failed latest attempt altered the previous successful alias.'
    Assert-AttemptContractReaderRejected $outputPath 'admin'
    $script:cases++

    $abandonedPath = Join-Path $temporaryRoot 'attempt-abandoned.json'
    $abandoned = $null
    try { $abandoned = Start-NativeBenchmarkAttempt -OutputPath $abandonedPath -Service 'admin' }
    finally { if ($null -ne $abandoned) { Close-NativeBenchmarkAttempt -Context $abandoned } }
    $unused = Get-AttemptContractMetadata $abandonedPath 'admin' 'running'
    Assert-AttemptContractReaderRejected $abandonedPath 'admin'
    $script:cases++

    $crashPath = Join-Path $temporaryRoot 'attempt-child-exit.json'
    $crashChild = Join-Path $temporaryRoot 'attempt-child-exit.ps1'
    @'
param([string] $HelperPath, [string] $OutputPath)
$ErrorActionPreference = 'Stop'
. $HelperPath
$context = Start-NativeBenchmarkAttempt -OutputPath $OutputPath -Service 'admin'
# A real child process exits with its lock still held; the OS releases the handle,
# while retained running metadata must continue to disqualify its latest result.
[Environment]::Exit(0)
'@ | Set-Content -LiteralPath $crashChild -Encoding utf8NoBOM
    $child = Invoke-BoundedProcess $crashChild @((Join-Path $PSScriptRoot 'native-benchmark-attempt-evidence.ps1'), $crashPath) 10
    Assert-Contract ($child.ExitCode -eq 0 -and [string]::IsNullOrWhiteSpace($child.Stdout) -and
        [string]::IsNullOrWhiteSpace($child.Stderr)) 'Owned crashed-attempt fixture did not exit cleanly without diagnostic output.'
    $unused = Get-AttemptContractMetadata $crashPath 'admin' 'running'
    Assert-AttemptContractReaderRejected $crashPath 'admin'
    $script:cases++

    $publicationPath = Join-Path $temporaryRoot 'attempt-publication-obstruction.json'
    $publication = $null
    try {
        $publication = Start-NativeBenchmarkAttempt -OutputPath $publicationPath -Service 'admin'
        # This is a real file-system conflict at the final atomic alias target.
        [IO.Directory]::CreateDirectory($publicationPath) | Out-Null
        $publicationRejected = $false
        try { Complete-NativeBenchmarkAttempt -Context $publication -Report (New-AttemptContractReport $sourceReport) }
        catch { $publicationRejected = $true }
        Assert-Contract $publicationRejected 'A blocked atomic alias publication was accepted.'
        Set-NativeBenchmarkAttemptFailed -Context $publication -FailureCode 'publication-failed'
    }
    finally { if ($null -ne $publication) { Close-NativeBenchmarkAttempt -Context $publication } }
    $failed = Get-AttemptContractMetadata $publicationPath 'admin' 'failed'
    Assert-Contract ($failed.attempt.failureCode -ceq 'publication-failed' -and -not [IO.File]::Exists($publicationPath)) 'Publication failure did not leave failed latest metadata without a success alias.'
    Assert-AttemptContractReaderRejected $publicationPath 'admin'
    $script:cases++

    $wrongCasePath = Join-Path $temporaryRoot 'attempt-wrong-property-case.json'
    $wrongCaseContext = $null
    try {
        $wrongCaseContext = Start-NativeBenchmarkAttempt -OutputPath $wrongCasePath -Service 'admin'
        $wrongCaseReport = New-AttemptContractReport $sourceReport
        $wrongCaseReport.Remove('schemaVersion') | Out-Null
        $wrongCaseReport.Add('SchemaVersion', 4)
        $wrongCaseFailure = $null
        try { Complete-NativeBenchmarkAttempt -Context $wrongCaseContext -Report $wrongCaseReport }
        catch { $wrongCaseFailure = $_.Exception.Message }
        Assert-Contract ($wrongCaseFailure -ceq 'Native benchmark attempt publication failed.') 'Wrong-cased report identity did not fail with controlled publication refusal.'
        Set-NativeBenchmarkAttemptFailed -Context $wrongCaseContext -FailureCode 'publication-failed'
    }
    finally { if ($null -ne $wrongCaseContext) { Close-NativeBenchmarkAttempt -Context $wrongCaseContext } }
    $failed = Get-AttemptContractMetadata $wrongCasePath 'admin' 'failed'
    Assert-Contract ($failed.attempt.failureCode -ceq 'publication-failed' -and
        -not [IO.File]::Exists($wrongCasePath)) 'Wrong-cased identity published a success alias or lost its failure status.'
    Assert-AttemptContractReaderRejected $wrongCasePath 'admin'
    $script:cases++

    # Start a clean succeeded lineage for independent retained-byte and ID guards.
    $tamperPath = Join-Path $temporaryRoot 'attempt-tamper.json'
    $tamperContext = $null
    try {
        $tamperContext = Start-NativeBenchmarkAttempt -OutputPath $tamperPath -Service 'admin'
        Complete-NativeBenchmarkAttempt -Context $tamperContext -Report (New-AttemptContractReport $sourceReport)
    }
    finally { if ($null -ne $tamperContext) { Close-NativeBenchmarkAttempt -Context $tamperContext } }
    $trusted = Assert-AttemptContractSuccess $tamperPath 'admin'
    $trustedArchive = Join-Path (Join-Path ($tamperPath + '.attempts') $trusted.latest.attemptId) 'benchmark.json'
    Assert-AttemptContractReaderRejected $tamperPath 'gateway'
    $script:cases++
    $metadata = Get-AttemptContractMetadata $tamperPath 'admin' 'succeeded'
    $originalBytes = @{}
    foreach ($path in @($tamperPath, $trustedArchive, $metadata.attemptPath, $metadata.latestPath)) {
        $originalBytes[$path] = [IO.File]::ReadAllBytes($path)
    }
    foreach ($scenario in @('archived-bytes', 'alias-bytes', 'attempt-report-hash', 'latest-id', 'attempt-schema-type', 'joined-report-id', 'completion-before-start')) {
        try {
            switch ($scenario) {
                'archived-bytes' { [IO.File]::AppendAllText($trustedArchive, "`n", [Text.UTF8Encoding]::new($false)) }
                'alias-bytes' { [IO.File]::AppendAllText($tamperPath, "`n", [Text.UTF8Encoding]::new($false)) }
                'attempt-report-hash' {
                    $modified = Read-AttemptContractJson $metadata.attemptPath
                    $modified.reportSha256 = '0' * 64
                    [IO.File]::WriteAllText($metadata.attemptPath, (ConvertTo-Json -InputObject $modified -Depth 8), [Text.UTF8Encoding]::new($false))
                }
                'latest-id' {
                    $modified = Read-AttemptContractJson $metadata.latestPath
                    $modified.attemptId = [Guid]::NewGuid().ToString('N')
                    [IO.File]::WriteAllText($metadata.latestPath, (ConvertTo-Json -InputObject $modified -Depth 8), [Text.UTF8Encoding]::new($false))
                }
                'attempt-schema-type' {
                    $modified = Read-AttemptContractJson $metadata.attemptPath
                    $modified.schemaVersion = '1'
                    [IO.File]::WriteAllText($metadata.attemptPath, (ConvertTo-Json -InputObject $modified -Depth 8), [Text.UTF8Encoding]::new($false))
                }
                { $_ -cin @('joined-report-id', 'completion-before-start') } {
                    $modifiedReport = Read-AttemptContractJson $trustedArchive
                    $modifiedAttempt = Read-AttemptContractJson $metadata.attemptPath
                    if ($scenario -ceq 'joined-report-id') { $modifiedReport.collection.attemptId = [Guid]::NewGuid().ToString('N') }
                    else {
                        $invalidTime = '1999-01-01T00:00:00.0000000Z'
                        $modifiedReport.collection.completedAt = $invalidTime
                        $modifiedReport.generatedAt = $invalidTime
                        $modifiedAttempt.completedAt = $invalidTime
                    }
                    $modifiedText = ConvertTo-Json -InputObject $modifiedReport -Depth 64
                    [IO.File]::WriteAllText($trustedArchive, $modifiedText, [Text.UTF8Encoding]::new($false))
                    [IO.File]::WriteAllText($tamperPath, $modifiedText, [Text.UTF8Encoding]::new($false))
                    $modifiedAttempt.reportSha256 = Get-AttemptContractHash $trustedArchive
                    [IO.File]::WriteAllText($metadata.attemptPath, (ConvertTo-Json -InputObject $modifiedAttempt -Depth 8), [Text.UTF8Encoding]::new($false))
                }
            }
            Assert-AttemptContractReaderRejected $tamperPath 'admin'
            $script:cases++
        }
        finally {
            foreach ($path in $originalBytes.Keys) { [IO.File]::WriteAllBytes($path, $originalBytes[$path]) }
        }
        $unused = Assert-AttemptContractSuccess $tamperPath 'admin'
    }
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    if ($NetworkBindingOnly) {
        Invoke-NetworkBindingContracts
    }
    elseif ($DiagnosticSchemaOnly) {
        Invoke-FailedWorkloadAggregateContracts
        Invoke-FailureDiagnosticSchemaContracts
    }
    else {
    [Environment]::SetEnvironmentVariable('CONDUIT_NATIVE_BENCHMARK_API_KEY', $privateKey)
    [Environment]::SetEnvironmentVariable('CONDUIT_COLLECTOR_TEST_PWSH', (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source)
    [Environment]::SetEnvironmentVariable('CONDUIT_CORE_RATE_LIMIT_MAX_REQUESTS', '1000')
    [Environment]::SetEnvironmentVariable('CONDUIT_CORE_RATE_LIMIT_WINDOW_SECONDS', '60')
    $processIdentityHelper = Join-Path $temporaryRoot 'process-identity.ps1'
    $identityDefinitions = foreach ($name in @('Get-OwnedProcessIdentityToken', 'Get-OwnedProcessCleanupInspection')) {
        'function ' + $name + ' {' + [Environment]::NewLine +
            (Get-Item ('Function:\' + $name)).ScriptBlock.ToString() + [Environment]::NewLine + '}'
    }
    [IO.File]::WriteAllText($processIdentityHelper, ($identityDefinitions -join [Environment]::NewLine))
    $fixture = Join-Path $temporaryRoot 'fixture.ps1'
    @'
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Mode, [string[]] $CommandArguments = @(), [string] $ChildArgumentsJson)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
. (Join-Path $PSScriptRoot 'process-identity.ps1')
if (-not [string]::IsNullOrEmpty($ChildArgumentsJson)) {
    $CommandArguments = @($ChildArgumentsJson | ConvertFrom-Json)
}
$root = $env:CONDUIT_COLLECTOR_TEST_ROOT
$scenario = $env:CONDUIT_COLLECTOR_TEST_SCENARIO
function Save-Record([string] $Directory, $Value) {
    if ($Directory -ceq 'commands') {
        $Value['ordinal'] = @(Get-ChildItem -LiteralPath (Join-Path $root $Directory) -File).Count + 1
    }
    $path = Join-Path (Join-Path $root $Directory) ([Guid]::NewGuid().ToString('N') + '.json')
    [IO.File]::WriteAllText($path, ($Value | ConvertTo-Json -Depth 20))
}
function Read-Active {
    return @(Get-ChildItem -LiteralPath (Join-Path $root 'active') -File | ForEach-Object {
        [IO.File]::ReadAllText($_.FullName) | ConvertFrom-Json -AsHashtable
    })
}
function Stop-Identity([string] $Path) {
    $identity = ([IO.File]::ReadAllText($Path)).Split('|')
    $owned = Get-Process -Id ([int]$identity[0]) -ErrorAction SilentlyContinue
    if ($null -ne $owned) {
        try {
            $inspection = Get-OwnedProcessCleanupInspection $owned
            if ($null -eq $inspection) { return }
            if ($inspection.Token -cne $identity[1]) { throw 'Reused owned fixture PID.' }
            if (-not $owned.HasExited -and -not $inspection.Zombie) {
                $owned.Kill($true)
                if (-not $owned.WaitForExit(5000)) { throw 'Fixture reap failed.' }
            }
        }
        finally { $owned.Dispose() }
    }
}
function Start-Child([string] $ChildMode, [string[]] $ChildArguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($env:CONDUIT_COLLECTOR_TEST_PWSH)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    # Persistent health children must own different pipes from their short-lived
    # Docker launcher, so the launcher's bounded reader can observe EOF.
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $childArgumentText = ConvertTo-Json -InputObject @($ChildArguments) -Compress
    foreach ($argument in @('-NoProfile', '-File', $PSCommandPath, '-Mode', $ChildMode, '-ChildArgumentsJson', $childArgumentText)) {
        $start.ArgumentList.Add($argument)
    }
    $child = [Diagnostics.Process]::Start($start)
    return $child
}
if ($Mode -eq 'sleep') { Start-Sleep -Seconds 60; exit 0 }
if ($Mode -eq 'health') {
    $port = [int]$CommandArguments[0]
    # The parent alone writes the readiness identity before associating the port.
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
    $listener.Start()
    try {
        while ($true) {
            $client = $listener.AcceptTcpClient()
            try {
                $client.ReceiveTimeout = 3000
                $stream = $client.GetStream()
                $previous = [Collections.Generic.Queue[byte]]::new()
                for ($index = 0; $index -lt 8192; $index++) {
                    $byte = $stream.ReadByte()
                    if ($byte -lt 0) { break }
                    $previous.Enqueue([byte]$byte)
                    if ($previous.Count -gt 4) { $null = $previous.Dequeue() }
                    if ($previous.Count -eq 4 -and (($previous.ToArray() -join ',') -eq '13,10,13,10')) { break }
                }
                $active = @(Read-Active | Where-Object port -EQ $port)
                $rejectReadiness = $active.Count -eq 1 -and (
                    ($scenario -cin @('diagnostic-readiness', 'diagnostic-readiness-private-cleanup') -and $active[0].ordinal -eq 1) -or
                    ($scenario -ceq 'diagnostic-readiness-cleanup' -and $active[0].ordinal -eq 4))
                $status = if ($rejectReadiness) { '503 Service Unavailable' } else { '200 OK' }
                $body = '{}'
                if ($rejectReadiness) {
                    [IO.File]::WriteAllText((Join-Path $root 'readiness-failure-observed'), [string]$active[0].ordinal)
                    $body = 'private-child-diagnostic:' + $env:CONDUIT_NATIVE_BENCHMARK_API_KEY +
                        [IO.File]::ReadAllText((Join-Path $root 'expected-redis.url'))
                }
                $response = [Text.Encoding]::ASCII.GetBytes(('HTTP/1.1 ' + $status) + [char]13 + [char]10 +
                    "Content-Type: application/json" + [char]13 + [char]10 + ('Content-Length: ' + $body.Length) + [char]13 +
                    [char]10 + "Connection: close" + [char]13 + [char]10 + [char]13 + [char]10 + $body)
                $stream.Write($response)
            }
            catch { }
            finally { $client.Dispose() }
        }
    }
    finally { $listener.Stop() }
}
if ($Mode -eq 'docker') {
    Save-Record 'commands' @{ arguments = $CommandArguments; benchmarkCredentialPresent = -not [string]::IsNullOrEmpty($env:CONDUIT_NATIVE_BENCHMARK_API_KEY) }
    $nativeImage = [IO.File]::ReadAllText((Join-Path $root 'native-image.json')) | ConvertFrom-Json -AsHashtable -DateKind String
    if ($CommandArguments[0] -ceq 'network') {
        if ($CommandArguments.Count -ne 5 -or $CommandArguments[1] -cne 'inspect' -or
            $CommandArguments[2] -cne '--format' -or $CommandArguments[3] -cne '{{json .}}') { throw 'Unexpected fake network mutation.' }
        $network = [IO.File]::ReadAllText((Join-Path $root 'fixture-network.json')) | ConvertFrom-Json -AsHashtable
        if ($CommandArguments[-1] -cne $network.Name) { throw 'Network inspection did not use its exact caller-managed name.' }
        switch ($scenario) {
            'network-malformed' { [Console]::Out.WriteLine('{"Id":1,"Id":"private-child-diagnostic"}'); exit 0 }
            'network-foreign-name' { $network.Name = 'private-child-diagnostic' }
            'network-nonbridge' { $network.Driver = 'overlay' }
        }
        $network | ConvertTo-Json -Compress
        exit 0
    }
    if ($CommandArguments[0] -eq 'image') {
        $native = -not ($CommandArguments[-1].StartsWith('example.invalid/conduit-jit@', [StringComparison]::Ordinal) -or $CommandArguments[-1] -ceq ('sha256:' + ('1' * 64)))
        $revision = if ($scenario -eq 'revision-mismatch' -and $native) { 'b' * 40 } else { 'a' * 40 }
        if ($native) {
            $nativeImage['Size'] = 800; $nativeImage.Config.Labels['org.opencontainers.image.revision'] = $revision
            $nativeImage | ConvertTo-Json -Depth 32 -Compress
        } else {
            @{ Id = 'sha256:' + ('1' * 64); Os = 'linux'; Architecture = 'amd64'; Size = 1000;
                Config = @{ Labels = @{ 'org.opencontainers.image.revision' = $revision } } } | ConvertTo-Json -Depth 5 -Compress
        }
        exit 0
    }
    if ($CommandArguments[0] -eq 'manifest') {
        if (-not $CommandArguments[-1].StartsWith('example.invalid/conduit-jit@', [StringComparison]::Ordinal)) { throw 'Native metadata consulted an unrelated registry manifest.' }
        if ($scenario -cin @('jit-index', 'jit-index-ambiguous', 'jit-index-missing', 'jit-index-malformed')) {
            if ($CommandArguments.Count -ne 3 -or $CommandArguments[1] -cne 'inspect') { throw 'JIT manifest command shape differs.' }
            if ($CommandArguments[-1] -ceq ('example.invalid/conduit-jit@sha256:' + ('4' * 64))) {
                $amd64 = @{ digest = 'sha256:' + ('2' * 64); platform = @{ os = 'linux'; architecture = 'amd64' } }
                $arm64 = @{ digest = 'sha256:' + ('3' * 64); platform = @{ os = 'linux'; architecture = 'arm64' } }
                $platforms = switch ($scenario) {
                    'jit-index-ambiguous' { @($amd64, $amd64, $arm64) }
                    'jit-index-missing' { @($arm64) }
                    'jit-index-malformed' { $amd64.digest = 'sha256:invalid'; @($amd64, $arm64) }
                    default { @($amd64, $arm64) }
                }
                @{ schemaVersion = 2; manifests = @($platforms) } | ConvertTo-Json -Depth 5 -Compress
                exit 0
            }
            if ($scenario -ceq 'jit-index' -and
                $CommandArguments[-1] -ceq ('example.invalid/conduit-jit@sha256:' + ('2' * 64))) {
                @{ schemaVersion = 2; layers = @(@{ size = 211 }, @{ size = 307 }) } | ConvertTo-Json -Depth 5 -Compress
                exit 0
            }
            throw 'JIT metadata consulted an unexpected selected manifest.'
        }
        @{ schemaVersion = 2; layers = @(@{ size = 700 }) } | ConvertTo-Json -Depth 5 -Compress
        exit 0
    }
    if ($CommandArguments[0] -eq 'run') {
        $runTracePath = Join-Path $root 'processes/docker-run-trace.json'
        $runTrace = [Collections.Generic.List[object]]::new()
        function Write-RunTrace([string] $Stage, [string] $ExceptionType, [string] $ErrorId) {
            $entry = @{ stage = $Stage }
            if (-not [string]::IsNullOrEmpty($ExceptionType)) { $entry.exceptionType = $ExceptionType }
            if (-not [string]::IsNullOrEmpty($ErrorId)) { $entry.fullyQualifiedErrorId = $ErrorId }
            $runTrace.Add($entry)
            [IO.File]::WriteAllText($runTracePath, (ConvertTo-Json -InputObject @($runTrace.ToArray()) -Compress))
        }
        Write-RunTrace 'run-entered'
        try {
        $name = $null
        $labels = @{}
        $port = 0
        $privateEnvironment = $null
        $networkMode = $null
        for ($index = 1; $index -lt $CommandArguments.Count - 1; $index++) {
            if ($CommandArguments[$index] -eq '--name') { $name = $CommandArguments[++$index] }
            elseif ($CommandArguments[$index] -eq '--label') {
                $label = $CommandArguments[++$index].Split('=', 2); $labels[$label[0]] = $label[1]
            }
            elseif ($CommandArguments[$index] -eq '--publish') { $port = [int]$CommandArguments[++$index].Split(':')[1] }
            elseif ($CommandArguments[$index] -eq '--env-file') { $privateEnvironment = $CommandArguments[++$index] }
            elseif ($CommandArguments[$index] -eq '--network') { $networkMode = $CommandArguments[++$index] }
        }
        Write-RunTrace 'arguments-parsed'
        $platformIndex = [Array]::IndexOf($CommandArguments, '--platform')
        if ($platformIndex -lt 0 -or $CommandArguments[$platformIndex + 1] -cne 'linux/amd64') { throw 'Measured image platform was not explicit.' }
        $hostIndex = [Array]::IndexOf($CommandArguments, '--add-host')
        $publishIndex = [Array]::IndexOf($CommandArguments, '--publish')
        if ($hostIndex -lt 0 -or $CommandArguments[$hostIndex + 1] -cne 'host.docker.internal:host-gateway' -or
            $publishIndex -lt 0 -or $CommandArguments[$publishIndex + 1] -cne ('127.0.0.1:' + $port + ':8080')) { throw 'Host mapping or published-port contract changed.' }
        if ($scenario.StartsWith('network-', [StringComparison]::Ordinal)) {
            $network = [IO.File]::ReadAllText((Join-Path $root 'fixture-network.json')) | ConvertFrom-Json -AsHashtable
            if ($networkMode -cne $network.Id) { throw 'Measured host did not use the pinned network ID.' }
        }
        elseif ($null -ne $networkMode) { throw 'Default transport received an unexpected network.' }
        $native = $labels['conduit.native-benchmark.runtime'] -ceq 'native-aot'
        $expectedImage = if ($native) { $nativeImage.Id } else { 'sha256:' + ('1' * 64) }
        if ($CommandArguments[-1] -cne $expectedImage) { throw 'Measured image was not the immutable observed local ID.' }
        $ordinal = @(Get-ChildItem -LiteralPath (Join-Path $root 'created') -File).Count + 1
        $identity = ([long]$ordinal).ToString('x64')
        $associationPath = Join-Path $root ('processes/health-' + $port + '.association')
        if (-not [IO.File]::Exists($associationPath)) { throw 'Owned readiness association is missing.' }
        $identityPath = [IO.File]::ReadAllText($associationPath)
        $expectedIdentityParent = [IO.Path]::GetFullPath((Join-Path $root 'processes'))
        if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($identityPath)) -cne $expectedIdentityParent -or
            [IO.Path]::GetFileName($identityPath) -cnotmatch ('^health-' + $port + '-[0-9a-f]{32}\.identity$')) {
            throw 'Owned readiness association was rejected.'
        }
        if (-not [IO.File]::Exists($identityPath)) { throw 'Owned readiness fixture identity is missing.' }
        $healthIdentity = [IO.File]::ReadAllText($identityPath).Split('|')
        $health = Get-Process -Id ([int]$healthIdentity[0]) -ErrorAction Stop
        try {
            if ($health.HasExited -or (Get-OwnedProcessIdentityToken $health) -cne $healthIdentity[1]) {
                throw 'Owned readiness fixture identity does not match.'
            }
        }
        finally { $health.Dispose() }
        Write-RunTrace 'owned-health-associated'
        $environmentText = [IO.File]::ReadAllText($privateEnvironment)
        Write-RunTrace 'environment-read'
        $environmentChecks = @{
            stage = 'environment-framing'
            databasePrefixMatches = $environmentText -match 'DATABASE_URL=postgresql://fixture:private-database-'
            redisPrefixMatches = $environmentText -match 'REDIS_URL=redis://:private-redis-'
            translatedHostPresent = $environmentText -match 'host.docker.internal'
            forbiddenBenchmarkCredentialPresent = $environmentText -match '(?m)^CONDUIT_NATIVE_BENCHMARK_API_KEY='
        }
        $runTrace.Add($environmentChecks)
        [IO.File]::WriteAllText($runTracePath, (ConvertTo-Json -InputObject @($runTrace.ToArray()) -Compress))
        if (-not $environmentChecks.databasePrefixMatches -or -not $environmentChecks.redisPrefixMatches -or
            -not $environmentChecks.translatedHostPresent -or $environmentChecks.forbiddenBenchmarkCredentialPresent) {
            throw 'Private container environment is invalid.'
        }
        $environmentValues = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
        foreach ($line in ($environmentText -split '\r?\n')) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $pair = $line.Split('=', 2)
            if ($pair.Count -ne 2 -or -not $environmentValues.TryAdd($pair[0], $pair[1])) { throw 'Invalid canonical fixture environment.' }
        }
        $mandatoryNames = @('DATABASE_URL', 'REDIS_URL', 'CONDUIT_MIGRATION_MODE',
            'ConduitLLM__Messaging__Wolverine__AutoProvision', 'ASPNETCORE_URLS', 'ASPNETCORE_HTTP_PORTS')
        foreach ($mandatoryName in $mandatoryNames) {
            if (-not $environmentValues.ContainsKey($mandatoryName) -or $environmentValues.ContainsKey($mandatoryName.ToLowerInvariant())) {
                throw 'Mandatory environment name was not canonicalized.'
            }
        }
        if ($environmentValues['REDIS_URL'] -cne [IO.File]::ReadAllText((Join-Path $root 'expected-redis.url'))) {
            throw 'Backing-service URL changed beyond its loopback hostname.'
        }
        if ($environmentValues['CONDUIT_MIGRATION_MODE'] -cne 'Wait' -or
            $environmentValues['ConduitLLM__Messaging__Wolverine__AutoProvision'] -cne 'false' -or
            $environmentValues['ASPNETCORE_URLS'] -cne 'http://+:8080' -or
            $environmentValues['ASPNETCORE_HTTP_PORTS'] -cne '8080') { throw 'Mandatory environment value was not overridden.' }
        $admin = $labels['conduit.native-benchmark.service'] -ceq 'admin'
        $expectedBackend = if ($admin) { [IO.File]::ReadAllText((Join-Path $root 'expected-admin.key')) }
            else { 'gateway-master-fixture-key' }
        if (-not $environmentValues.ContainsKey('CONDUIT_API_TO_API_BACKEND_AUTH_KEY') -or
            $environmentValues['CONDUIT_API_TO_API_BACKEND_AUTH_KEY'] -cne $expectedBackend -or
            $environmentValues.ContainsKey('conduit_api_to_api_backend_auth_key')) { throw 'Backend credential override or preservation failed.' }
        Write-RunTrace 'environment-validated'
        $record = @{ id = $identity; name = $name; labels = $labels; port = $port; ordinal = $ordinal;
            runtime = $labels['conduit.native-benchmark.runtime']; processIdentity = $identityPath;
            canonicalEnvironment = $true; backendCredentialCorrect = $true; image = $expectedImage;
            running = $true; exitCode = 0; networkMode = $networkMode; attachmentChecks = 0 }
        if ($native) {
            $record['selectedDescriptor'] = [IO.File]::ReadAllText((Join-Path $root 'native-selected.json')) | ConvertFrom-Json -AsHashtable
            if ($scenario -ceq 'native-selected-mismatch') { $record.selectedDescriptor.digest = 'sha256:' + ('d' * 64) }
        }
        [IO.File]::WriteAllText((Join-Path $root ('active/' + $identity + '.json')), ($record | ConvertTo-Json -Depth 8))
        Write-RunTrace 'active-written'
        Save-Record 'created' $record
        Write-RunTrace 'created-written'
        if ($scenario -eq 'unknown-create') { [Console]::Error.WriteLine('private-child-diagnostic'); exit 17 }
        [Console]::Out.WriteLine($identity)
        exit 0
        }
        catch {
            Write-RunTrace 'error' $_.Exception.GetType().FullName $_.FullyQualifiedErrorId
            throw
        }
    }
    if ($CommandArguments[0] -eq 'inspect') {
        $identity = $CommandArguments[-1]
        $matches = @(Read-Active | Where-Object { $_.id -ceq $identity -or $_.name -ceq $identity })
        if ($matches.Count -eq 0) {
            $missingMessage = switch ($scenario) {
                'lowercase-missing-object' { 'error: no such object: ' + $identity }
                'lowercase-missing-container' { 'error: no such container: ' + $identity }
                'foreign-missing-identity' { 'error: no such object: foreign-container' }
                default { 'Error: No such object: ' + $identity }
            }
            $missingFault = Join-Path $root 'cleanup-verification-injected'
            if ($scenario -ceq 'diagnostic-readiness-cleanup' -and [IO.File]::Exists($missingFault) -and
                [IO.File]::ReadAllText($missingFault) -ceq $identity) {
                $missingMessage = 'private-child-diagnostic: missing verification denied'
            }
            if ($scenario -ceq 'missing-with-payload') { [Console]::Out.WriteLine('{"Id":"foreign-container"}') }
            [Console]::Error.WriteLine($missingMessage)
            exit 1
        }
        if ($matches.Count -ne 1) { throw 'Ambiguous fake identity.' }
        $record = $matches[0]
        $labels = $record.labels
        if ($scenario -eq 'foreign-label' -and @(Get-ChildItem -LiteralPath (Join-Path $root 'drivers') -File).Count -gt 0) {
            $labels['conduit.native-benchmark.owner'] = 'foreign-owner'
        }
        if ($scenario -ceq 'shutdown-foreign-label' -and -not $record.running) {
            $labels['conduit.native-benchmark.owner'] = 'foreign-owner'
        }
        $attachments = @{}
        if ($null -ne $record.networkMode) {
            $network = [IO.File]::ReadAllText((Join-Path $root 'fixture-network.json')) | ConvertFrom-Json -AsHashtable
            $record.attachmentChecks++
            [IO.File]::WriteAllText((Join-Path $root ('active/' + $record.id + '.json')), ($record | ConvertTo-Json -Depth 8))
            Save-Record 'network-checks' @{ id = $record.id; running = $record.running; check = $record.attachmentChecks }
            $attachmentId = if ($scenario -ceq 'network-forged-attachment' -or
                ($scenario -ceq 'network-id-change' -and $record.attachmentChecks -gt 1)) { '8' * 64 } else { $network.Id }
            $attachments[$network.Name] = @{ NetworkID = $attachmentId; IPAddress = '172.29.0.8' }
        }
        @{ Id = $record.id; Name = '/' + $record.name; Image = $record.image; ImageManifestDescriptor = $record.selectedDescriptor;
            Config = @{ Labels = $labels }; State = @{ Running = $record.running; ExitCode = $record.exitCode };
            HostConfig = @{ NetworkMode = $record.networkMode }; NetworkSettings = @{ Networks = $attachments } } |
            ConvertTo-Json -Depth 8 -Compress
        exit 0
    }
    if ($CommandArguments[0] -eq 'stats') {
        $record = @(Read-Active | Where-Object id -CEQ $CommandArguments[1])[0]
        if ($scenario -ceq 'diagnostic-poll-memory' -and
            [IO.File]::Exists((Join-Path $root 'processes/diagnostic-workload.identity')) -and
            [IO.File]::Exists((Join-Path $root 'processes/diagnostic-descendant.identity'))) {
            [Console]::Out.WriteLine('private-child-diagnostic: invalid memory ' + [IO.File]::ReadAllText((Join-Path $root 'expected-redis.url')))
            exit 0
        }
        $memory = if ($record.ordinal -le 2) { '99 GiB' } elseif ($record.runtime -ceq 'jit') { '1 MiB' } else { '512 KiB' }
        [Console]::Out.WriteLine($memory + ' / 128 GiB')
        exit 0
    }
    if ($CommandArguments[0] -eq 'stop') {
        if ($CommandArguments.Count -ne 4 -or $CommandArguments[1] -cne '--timeout' -or
            $CommandArguments[2] -cne '1') { throw 'Shutdown deadline was not explicit.' }
        $matches = @(Read-Active | Where-Object id -CEQ $CommandArguments[-1])
        if ($matches.Count -ne 1 -or -not $matches[0].running) { throw 'Shutdown did not use one running exact fake ID.' }
        if ($scenario -ceq 'shutdown-command-failure') { [Console]::Error.WriteLine('private-child-diagnostic'); exit 18 }
        $record = $matches[0]
        $record.running = $scenario -ceq 'shutdown-still-running'
        $record.exitCode = if ($scenario -ceq 'shutdown-forced-exit') { 137 }
            elseif ($scenario -ceq 'shutdown-invalid-exit') { '0' } else { 0 }
        [IO.File]::WriteAllText((Join-Path $root ('active/' + $record.id + '.json')), ($record | ConvertTo-Json -Depth 8))
        exit 0
    }
    if ($CommandArguments[0] -eq 'rm') {
        if ($scenario -eq 'cleanup-failure') { [Console]::Error.WriteLine('private-child-diagnostic'); exit 18 }
        $matches = @(Read-Active | Where-Object id -CEQ $CommandArguments[-1])
        if ($matches.Count -ne 1) { throw 'Cleanup did not use one exact fake ID.' }
        if ($CommandArguments.Count -eq 2 -and $matches[0].running) { throw 'Non-force removal reached a running fake container.' }
        # Containers are synthetic records. The parent owns both reusable
        # readiness listeners and joins them after this complete case.
        Remove-Item -LiteralPath (Join-Path $root ('active/' + $matches[0].id + '.json')) -Force
        if ($scenario -ceq 'diagnostic-readiness-cleanup' -and $matches[0].ordinal -eq 4) {
            [IO.File]::WriteAllText((Join-Path $root 'cleanup-verification-injected'), [string]$matches[0].id)
        }
        exit 0
    }
    throw 'Unexpected fake Docker command.'
}
if ($Mode -eq 'driver' -or $Mode -eq 'dotnet') {
    $tracePath = Join-Path $root 'processes/driver-timeout-trace.json'
    $trace = [Collections.Generic.List[object]]::new()
    $trace.Add(@{ stage = 'driver-entered' })
    [IO.File]::WriteAllText($tracePath, (ConvertTo-Json -InputObject @($trace.ToArray()) -Compress))
    $dotnetRoute = $Mode -eq 'dotnet'
    $marker = [Array]::IndexOf($CommandArguments, '--benchmark-workload')
    if ($marker -lt 0 -or $marker + 1 -ge $CommandArguments.Count) { throw 'Missing workload command.' }
    $configuration = [IO.File]::ReadAllText($CommandArguments[$marker + 1]) | ConvertFrom-Json -AsHashtable
    $trace.Add(@{ stage = 'configuration-read' })
    [IO.File]::WriteAllText($tracePath, (ConvertTo-Json -InputObject @($trace.ToArray()) -Compress))
    $port = ([Uri]$configuration.baseUrl).Port
    $matches = @(Read-Active | Where-Object port -EQ $port)
    $trace.Add(@{ stage = 'owned-port-read'; matches = $matches.Count })
    [IO.File]::WriteAllText($tracePath, (ConvertTo-Json -InputObject @($trace.ToArray()) -Compress))
    if ($matches.Count -ne 1) { throw 'Driver did not target one owned container.' }
    $record = $matches[0]
    Save-Record 'drivers' @{ dotnetRoute = $dotnetRoute; ordinal = $record.ordinal; runtime = $record.runtime;
        workload = $configuration.workload; configurationPath = $CommandArguments[$marker + 1];
        benchmarkCredentialPresent = -not [string]::IsNullOrEmpty($env:CONDUIT_NATIVE_BENCHMARK_API_KEY) }
    if ($scenario -cin @('diagnostic-private-child', 'diagnostic-private-config')) {
        [Console]::Out.WriteLine('private-child-diagnostic: not JSON ' + $env:CONDUIT_NATIVE_BENCHMARK_API_KEY +
            [IO.File]::ReadAllText((Join-Path $root 'expected-redis.url')) + ' ' + $CommandArguments[$marker + 1])
        [Console]::Error.WriteLine('private-child-diagnostic: ' + $env:CONDUIT_NATIVE_BENCHMARK_API_KEY)
        exit 0
    }
    if ($scenario -ceq 'diagnostic-probe-malformed') {
        [Console]::Out.WriteLine('private-child-diagnostic ' + $env:CONDUIT_NATIVE_BENCHMARK_API_KEY)
        [Console]::Error.WriteLine('private-child-diagnostic ' + $env:CONDUIT_NATIVE_BENCHMARK_API_KEY)
        exit 1
    }
    if ($scenario -ceq 'diagnostic-poll-memory') {
        $descendant = Start-Child 'sleep' @()
        try {
            [IO.File]::WriteAllText((Join-Path $root 'processes/diagnostic-descendant.identity'),
                ($descendant.Id.ToString() + '|' + (Get-OwnedProcessIdentityToken $descendant)))
        }
        finally { $descendant.Dispose() }
        $self = [Diagnostics.Process]::GetCurrentProcess()
        try {
            [IO.File]::WriteAllText((Join-Path $root 'processes/diagnostic-workload.identity'),
                ($self.Id.ToString() + '|' + (Get-OwnedProcessIdentityToken $self)))
        }
        finally { $self.Dispose() }
        Start-Sleep -Seconds 60
    }
    if ($scenario -ceq 'package-late-mutation' -and $record.ordinal -eq 12 -and
        -not [IO.File]::Exists((Join-Path $root 'package-mutated'))) {
        [IO.File]::AppendAllText((Join-Path $root 'package/extracted/conduit-evidence/application.json'), ' ')
        [IO.File]::WriteAllText((Join-Path $root 'package-mutated'), 'Owned retained package changed during workload')
    }
    if ($scenario -in @('driver-nonzero', 'foreign-label', 'cleanup-failure')) {
        [Console]::Error.WriteLine('private-child-diagnostic'); exit 17
    }
    if ($scenario -eq 'driver-timeout') {
        $trace.Add(@{ stage = 'before-start' })
        [IO.File]::WriteAllText($tracePath, (ConvertTo-Json -InputObject @($trace.ToArray()) -Compress))
        try {
            $descendant = Start-Child 'sleep' @()
            $trace.Add(@{ stage = 'after-start'; resultType = $descendant.GetType().FullName })
            [IO.File]::WriteAllText($tracePath, (ConvertTo-Json -InputObject @($trace.ToArray()) -Compress))
            try {
                [IO.File]::WriteAllText((Join-Path $root 'processes/driver-descendant.identity'),
                    ($descendant.Id.ToString() + '|' + (Get-OwnedProcessIdentityToken $descendant)))
                $trace.Add(@{ stage = 'identity-written' })
                [IO.File]::WriteAllText($tracePath, (ConvertTo-Json -InputObject @($trace.ToArray()) -Compress))
            }
            finally { $descendant.Dispose() }
            Start-Sleep -Seconds 60
        }
        catch {
            $trace.Add(@{ stage = 'error'; exceptionType = $_.Exception.GetType().FullName;
                fullyQualifiedErrorId = $_.FullyQualifiedErrorId })
            [IO.File]::WriteAllText($tracePath, (ConvertTo-Json -InputObject @($trace.ToArray()) -Compress))
            throw
        }
    }
    $admin = $configuration.workload -ceq 'admin-setting-read'
    $path = if ($admin) { '/v1/admin/global-settings/by-key/{key}' } else { '/v1/chat/completions' }
    $method = if ($admin) { 'GET' } else { 'POST' }
    $fingerprint = if ($configuration.workload -ceq 'gateway-chat-sse') { 'd' * 64 } else { 'c' * 64 }
    if ($scenario -eq 'fingerprint-mismatch' -and $record.runtime -ceq 'native-aot') { $fingerprint = 'e' * 64 }
    $elapsed = if ($record.ordinal -le 2) { 100000 } else { 100 + $record.ordinal }
    $completed = if ($scenario -eq 'incomplete-report') { $configuration.requests - 1 } else { $configuration.requests }
    $row = [ordered]@{
        id = $configuration.workload; method = $method; path = $path; authenticated = $true; inputFingerprint = $fingerprint
        requested = $configuration.requests; executed = $configuration.requests; completed = $completed; failures = 0
        warmupRequests = $configuration.warmupRequests; concurrency = $configuration.concurrency; timeoutSeconds = $configuration.timeoutSeconds
        elapsedMilliseconds = $elapsed; throughputPerSecond = $completed * 1000.0 / $elapsed
        p50Milliseconds = $(if ($record.ordinal -le 2) { 100 } else { 1 })
        p95Milliseconds = $(if ($record.ordinal -le 2) { 200 } else { 2 })
        p99Milliseconds = $(if ($record.ordinal -le 2) { 300 } else { 3 })
    }
    if ($scenario.StartsWith('diagnostic-probe-', [StringComparison]::Ordinal)) {
        $row.completed = if ($scenario -ceq 'diagnostic-probe-all-failed') { 0 } else { $configuration.requests - 1 }
        $row.failures = $configuration.requests - $row.completed
        $row.throughputPerSecond = $row.completed * 1000.0 / $elapsed
        if ($scenario -ceq 'diagnostic-probe-private') { $row.extra = $env:CONDUIT_NATIVE_BENCHMARK_API_KEY }
    }
    $childSchemaVersion = if ($scenario -eq 'child-string-schema') { '2' } else { 2 }
    @{ schemaVersion = $childSchemaVersion; service = $(if ($admin) { 'admin' } else { 'gateway' });
        generatedAt = [DateTimeOffset]::UtcNow.ToString('O'); workloads = @($row) } |
        ConvertTo-Json -Depth 8 -Compress
    if ($scenario.StartsWith('diagnostic-probe-', [StringComparison]::Ordinal)) {
        [Console]::Error.WriteLine('private-child-diagnostic ' + $env:CONDUIT_NATIVE_BENCHMARK_API_KEY)
        exit 1
    }
    exit 0
}
throw 'Unknown fixture mode.'
'@ | Set-Content -LiteralPath $fixture -Encoding utf8NoBOM

    $dockerCommand = Join-Path $temporaryRoot 'docker.ps1'
    $dotnetCommand = Join-Path $temporaryRoot 'dotnet.ps1'
    $nativeDriver = Join-Path $temporaryRoot 'native-driver.ps1'
    foreach ($entry in @(@($dockerCommand, 'docker'), @($dotnetCommand, 'dotnet'), @($nativeDriver, 'driver'))) {
        $wrapper = "param()" + [Environment]::NewLine +
            "& (Join-Path " + '$PSScriptRoot' + " 'fixture.ps1') -Mode '" + $entry[1] + "' -CommandArguments " + '$args' +
            [Environment]::NewLine + 'exit $LASTEXITCODE'
        [IO.File]::WriteAllText($entry[0], $wrapper)
    }
    $managedProbe = Join-Path $temporaryRoot 'managed-probe.dll'
    [IO.File]::WriteAllText($managedProbe, 'This command-routing fixture is intentionally not a .NET assembly.')
    $script:packages = @{}
    $script:packages['admin'] = New-NativeBenchmarkPackagingFixture $temporaryRoot 'admin'
    $script:packages['gateway'] = New-NativeBenchmarkPackagingFixture $temporaryRoot 'gateway'
    if (-not $StrictCallerOnly -and -not $FailedWorkloadOnly -and -not $DockerNetworkOnly) {
    $script:packages['admin-other'] = New-NativeBenchmarkPackagingFixture $temporaryRoot 'admin'
    $script:packages['admin-dirty'] = New-NativeBenchmarkPackagingFixture $temporaryRoot 'admin' -SourceDirty $true
    $script:packages['admin-unavailable'] = New-NativeBenchmarkPackagingFixture $temporaryRoot 'admin' -UnavailableSource
    $script:packages['admin-hold'] = New-NativeBenchmarkPackagingFixture $temporaryRoot 'admin' -Scenario 'oci-hold-no-descriptors'
    }
    $environmentPath = Join-Path $temporaryRoot 'environment.json'
    @{
        schemaVersion = 1; environmentId = 'collector-contract-host'
        host = @{ os = 'linux'; architecture = 'x64'; cpu = 'owned test CPU'; logicalProcessors = 4; memoryBytes = 8589934592
            dockerVersion = 'fixture-1'; kernelVersion = 'fixture-kernel' }
        backingServices = @{ postgresqlVersion = '17-fixture'; redisVersion = '7-fixture'; wolverineVersion = '5-fixture'
            configurationId = 'owned-fixture-settings'; providerFixtureId = 'owned-provider-fixture' }
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $environmentPath -Encoding utf8NoBOM
    $adminConfiguration = Join-Path $temporaryRoot 'admin.json'
    $nonstreamConfiguration = Join-Path $temporaryRoot 'nonstream.json'
    $streamConfiguration = Join-Path $temporaryRoot 'stream.json'
    foreach ($entry in @(@($adminConfiguration, 'admin-setting-read'), @($nonstreamConfiguration, 'gateway-chat-nonstream'),
        @($streamConfiguration, 'gateway-chat-sse'))) {
        $configuration = [ordered]@{ workload = $entry[1]; baseUrl = 'http://127.0.0.1:18080'; requests = 4
            concurrency = 2; warmupRequests = 2; timeoutSeconds = 5 }
        if ($entry[1] -ceq 'admin-setting-read') {
            $configuration.settingKey = 'IpFilter:Enabled'; $configuration.expectedValue = 'true'
        }
        else {
            $configuration.model = 'native-aot-model'; $configuration.prompt = 'native benchmark café 雪'
            $configuration.expectedContent = 'fixture response'; $configuration.expectedPromptTokens = 10
            $configuration.expectedCompletionTokens = 5
        }
        $configuration | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $entry[0] -Encoding utf8NoBOM
    }

    # Extract and execute the real helper, including its cancellation and reap
    # behavior, without executing the collector's orchestration at this point.
    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($collector, [ref]$tokens, [ref]$parseErrors)
    Assert-Contract ($parseErrors.Count -eq 0) 'Collector syntax is invalid.'
    foreach ($functionName in @('Get-SafeFailureMessage', 'Invoke-BoundedProcess', 'Invoke-Docker', 'Get-ContainerMemory', 'ConvertTo-ContainerUrl', 'Get-ResultNumber', 'Test-WorkloadResult')) {
        $definition = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $functionName
        }.GetNewClosure(), $true)
        Assert-Contract ($null -ne $definition) ('Collector helper is missing: ' + $functionName)
        Invoke-Expression $definition.Extent.Text
    }
    if ($DockerNetworkOnly) {
        Invoke-NetworkBindingContracts
        $successfulNetwork = Invoke-CollectorCase 'gateway' 'network-success' $null -CaptureResult -StrictCaller
        foreach ($scenario in @('network-invalid-name', 'network-malformed', 'network-foreign-name', 'network-nonbridge', 'network-forged-attachment')) {
            Invoke-CollectorCase 'admin' $scenario 'Benchmark fixture network binding failed.' -StrictCaller
        }
        Invoke-CollectorCase 'gateway' 'network-id-change' 'Benchmark fixture network binding failed.' -ExistingOutputPath $successfulNetwork.outputPath -StrictCaller
    }
    elseif ($FailedWorkloadOnly) {
        $successfulAdmin = Invoke-CollectorCase 'admin' 'success' $null -CaptureResult -StrictCaller
        Invoke-CollectorCase 'admin' 'diagnostic-probe-partial' 'No qualification was recorded.' -ExistingOutputPath $successfulAdmin.outputPath
        Invoke-CollectorCase 'gateway' 'diagnostic-probe-all-failed' 'No qualification was recorded.'
        Invoke-CollectorCase 'gateway' 'diagnostic-probe-private' 'No qualification was recorded.'
        Invoke-CollectorCase 'admin' 'diagnostic-probe-malformed' 'No qualification was recorded.'
        Invoke-FailedWorkloadAggregateContracts
        Invoke-AttemptLifecycleContracts $successfulAdmin
        Invoke-FailureDiagnosticSchemaContracts
    }
    else {
    $successfulAdmin = Invoke-CollectorCase 'admin' 'success' $null -CaptureResult -StrictCaller
    Invoke-CollectorCase 'gateway' 'success' $null -StrictCaller
    Invoke-CollectorCase 'admin' 'case-duplicate-environment' 'No qualification was recorded.' -StrictCaller
    if (-not $StrictCallerOnly) {
    Invoke-NetworkBindingContracts
    $successfulNetwork = Invoke-CollectorCase 'gateway' 'network-success' $null -CaptureResult -StrictCaller
    foreach ($scenario in @('network-invalid-name', 'network-malformed', 'network-foreign-name', 'network-nonbridge', 'network-forged-attachment')) {
        Invoke-CollectorCase 'admin' $scenario 'Benchmark fixture network binding failed.' -StrictCaller
    }
    Invoke-CollectorCase 'gateway' 'network-id-change' 'Benchmark fixture network binding failed.' -ExistingOutputPath $successfulNetwork.outputPath -StrictCaller
    & (Join-Path $PSScriptRoot 'test-native-process-cleanup-metadata.ps1')
    # These are public fixture credentials, not a connection to any service.
    # Assert exact strings as well as parsed components: an invented slash on a
    # bare Redis endpoint looks equivalent to System.Uri but breaks startup.
    $uriCases = @(
        @{ input = 'redis://:fixture-pass@127.0.0.1:6379/2'; schemes = @('redis', 'rediss');
            expected = 'redis://:fixture-pass@host.docker.internal:6379/2';
            scheme = 'redis'; host = 'host.docker.internal'; port = 6379; path = '/2'; userInfo = ':fixture-pass' },
        @{ input = 'rediss://fixture:pass%40word%3Avalue@localhost:6380/3'; schemes = @('redis', 'rediss');
            expected = 'rediss://fixture:pass%40word%3Avalue@host.docker.internal:6380/3';
            scheme = 'rediss'; host = 'host.docker.internal'; port = 6380; path = '/3'; userInfo = 'fixture:pass%40word%3Avalue' },
        @{ input = 'postgresql://fixture:fixture-pass@[::1]:5434/owned'; schemes = @('postgres', 'postgresql');
            expected = 'postgresql://fixture:fixture-pass@host.docker.internal:5434/owned';
            scheme = 'postgresql'; host = 'host.docker.internal'; port = 5434; path = '/owned'; userInfo = 'fixture:fixture-pass' },
        @{ input = 'postgres://fixture:pass%40word@localhost:5433/owned'; schemes = @('postgres', 'postgresql');
            expected = 'postgres://fixture:pass%40word@host.docker.internal:5433/owned';
            scheme = 'postgres'; host = 'host.docker.internal'; port = 5433; path = '/owned'; userInfo = 'fixture:pass%40word' },
        @{ input = 'redis://:fixture-pass@redis.example.invalid:6381/4'; schemes = @('redis', 'rediss');
            expected = 'redis://:fixture-pass@redis.example.invalid:6381/4';
            scheme = 'redis'; host = 'redis.example.invalid'; port = 6381; path = '/4'; userInfo = ':fixture-pass' },
        @{ input = 'redis://172.17.0.3:6379'; schemes = @('redis', 'rediss');
            expected = 'redis://172.17.0.3:6379';
            scheme = 'redis'; host = '172.17.0.3'; port = 6379; path = '/'; userInfo = '' },
        @{ input = 'redis://localhost:6379'; schemes = @('redis', 'rediss');
            expected = 'redis://host.docker.internal:6379';
            scheme = 'redis'; host = 'host.docker.internal'; port = 6379; path = '/'; userInfo = '' },
        @{ input = 'redis://:fixture-pass@127.0.0.1:6379'; schemes = @('redis', 'rediss');
            expected = 'redis://:fixture-pass@host.docker.internal:6379';
            scheme = 'redis'; host = 'host.docker.internal'; port = 6379; path = '/'; userInfo = ':fixture-pass' },
        @{ input = 'redis://localhost:6379/'; schemes = @('redis', 'rediss');
            expected = 'redis://host.docker.internal:6379/';
            scheme = 'redis'; host = 'host.docker.internal'; port = 6379; path = '/'; userInfo = '' },
        @{ input = 'redis://[::1]:6379'; schemes = @('redis', 'rediss');
            expected = 'redis://host.docker.internal:6379';
            scheme = 'redis'; host = 'host.docker.internal'; port = 6379; path = '/'; userInfo = '' },
        @{ input = 'rediss://fixture:pass%40word%3Avalue@redis.example.invalid:6380/2?mode=%2F#part'; schemes = @('redis', 'rediss');
            expected = 'rediss://fixture:pass%40word%3Avalue@redis.example.invalid:6380/2?mode=%2F#part';
            scheme = 'rediss'; host = 'redis.example.invalid'; port = 6380; path = '/2'; userInfo = 'fixture:pass%40word%3Avalue';
            query = '?mode=%2F'; fragment = '#part' },
        @{ input = 'redis://localhost:06379'; schemes = @('redis', 'rediss');
            expected = 'redis://host.docker.internal:06379';
            scheme = 'redis'; host = 'host.docker.internal'; port = 6379; path = '/'; userInfo = '' },
        @{ input = 'rediss://fixture:pass%40word%3Avalue@localhost:6380/2?mode=%2F#part'; schemes = @('redis', 'rediss');
            expected = 'rediss://fixture:pass%40word%3Avalue@host.docker.internal:6380/2?mode=%2F#part';
            scheme = 'rediss'; host = 'host.docker.internal'; port = 6380; path = '/2'; userInfo = 'fixture:pass%40word%3Avalue';
            query = '?mode=%2F'; fragment = '#part' }
    )
    foreach ($uriCase in $uriCases) {
        $translatedText = ConvertTo-ContainerUrl $uriCase.input $uriCase.schemes
        Assert-Contract ($translatedText -ceq $uriCase.expected) 'Backing-service host translation changed the exact URI beyond its hostname.'
        $translated = $null
        Assert-Contract ([Uri]::TryCreate($translatedText, [UriKind]::Absolute, [ref]$translated)) 'Translated backing-service URI is malformed.'
        Assert-Contract ($translated.Scheme -ceq $uriCase.scheme -and $translated.Host -ceq $uriCase.host -and
            $translated.Port -eq $uriCase.port -and $translated.AbsolutePath -ceq $uriCase.path -and
            $translated.UserInfo -ceq $uriCase.userInfo -and $translated.Query -ceq [string]$uriCase.query -and
            $translated.Fragment -ceq [string]$uriCase.fragment) 'Backing-service host translation changed URI credentials or other components.'
        $cases++
    }
    foreach ($invalidUri in @('http://127.0.0.1:6379/0', ('redis://127.0.0.1:6379/0' + [char]10),
        ' redis://localhost:6379', 'redis://localhost:6379 ',
        ([char]9 + 'redis://localhost:6379'), ('redis://localhost:6379' + [char]9),
        ' redis://172.17.0.3:6379', 'redis://172.17.0.3:6379 ',
        ([char]9 + 'redis://172.17.0.3:6379'), ('redis://172.17.0.3:6379' + [char]9),
        ('redis://local' + [char]9 + 'host:6379'), ('redis://localhost:6379' + [char]127))) {
        $uriFailure = $null
        try { $unexpectedUri = ConvertTo-ContainerUrl $invalidUri @('redis', 'rediss') }
        catch { $uriFailure = $_.Exception.Message }
        Assert-Contract ($uriFailure -ceq 'Invalid backing-service configuration.') 'Invalid backing-service URI was not rejected privately.'
        $cases++
    }
    $Service = 'admin'
    $reportConfiguration = [pscustomobject]@{
        Id = 'admin-setting-read'
        Data = @{ requests = 4; warmupRequests = 2; concurrency = 2; timeoutSeconds = 5 }
    }
    $validChildReport = [pscustomobject]@{
        schemaVersion = 2
        service = 'admin'
        workloads = @([pscustomobject]@{
            id = 'admin-setting-read'; method = 'GET'; path = '/v1/admin/global-settings/by-key/{key}'
            authenticated = $true; inputFingerprint = 'c' * 64
            requested = 4; executed = 4; completed = 4; failures = 0
            warmupRequests = 2; concurrency = 2; timeoutSeconds = 5
            elapsedMilliseconds = 100; throughputPerSecond = 40
            p50Milliseconds = 1; p95Milliseconds = 2; p99Milliseconds = 3
        })
    }
    $validatedChildRow = Test-WorkloadResult $validChildReport $reportConfiguration
    Assert-Contract ($validatedChildRow.id -ceq 'admin-setting-read' -and $validatedChildRow.completed -eq 4) 'Valid direct child-report contract was rejected.'
    $cases++
    foreach ($invalidShape in @('string-schema', 'bool-schema', 'array-service', 'scalar-workloads', 'array-id', 'array-method', 'array-path')) {
        $invalidReport = $validChildReport | ConvertTo-Json -Depth 8 | ConvertFrom-Json
        switch ($invalidShape) {
            'string-schema' { $invalidReport.schemaVersion = '2' }
            'bool-schema' { $invalidReport.schemaVersion = $true }
            'array-service' { $invalidReport.service = @('admin') }
            'scalar-workloads' { $invalidReport.workloads = $invalidReport.workloads[0] }
            'array-id' { $invalidReport.workloads[0].id = @('admin-setting-read') }
            'array-method' { $invalidReport.workloads[0].method = @('GET') }
            'array-path' { $invalidReport.workloads[0].path = @('/v1/admin/global-settings/by-key/{key}') }
        }
        $childFailure = $null
        try { $unexpectedRow = Test-WorkloadResult $invalidReport $reportConfiguration }
        catch { $childFailure = $_.Exception.Message }
        Assert-Contract ($childFailure -ceq 'Workload report is invalid.') ('Invalid direct child-report shape was accepted: ' + $invalidShape)
        $cases++
    }
    $helperChild = Join-Path $temporaryRoot 'helper-child.ps1'
    @'
param([string] $Value)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
. (Join-Path $PSScriptRoot 'process-identity.ps1')
[Console]::Out.WriteLine($Value)
[Console]::Out.WriteLine($env:CONDUIT_COLLECTOR_HELPER_VALUE)
[Console]::Out.WriteLine([string]::IsNullOrEmpty($env:CONDUIT_NATIVE_BENCHMARK_API_KEY))
[Console]::Error.WriteLine('private-child-diagnostic')
if ($env:CONDUIT_COLLECTOR_HELPER_VALUE -eq 'nonzero') { exit 17 }
if ($env:CONDUIT_COLLECTOR_HELPER_VALUE -eq 'overflow') {
    [Console]::Out.Write([string]::new([char]120, 4194305))
    Start-Sleep -Seconds 60
}
if ($env:CONDUIT_COLLECTOR_HELPER_VALUE -eq 'timeout') {
    $start = [Diagnostics.ProcessStartInfo]::new($env:CONDUIT_COLLECTOR_TEST_PWSH)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in @('-NoProfile', '-File', $env:CONDUIT_COLLECTOR_HELPER_SLEEPER)) { $start.ArgumentList.Add($argument) }
    $child = [Diagnostics.Process]::Start($start)
    try { [IO.File]::WriteAllText($env:CONDUIT_COLLECTOR_HELPER_IDENTITY,
        ($child.Id.ToString() + '|' + (Get-OwnedProcessIdentityToken $child))) }
    finally { $child.Dispose() }
    Start-Sleep -Seconds 60
}
'@ | Set-Content -LiteralPath $helperChild -Encoding utf8NoBOM
    $sleeper = Join-Path $temporaryRoot 'sleeper.ps1'
    'Start-Sleep -Seconds 60' | Set-Content -LiteralPath $sleeper -Encoding utf8NoBOM
    $helperEnvironment = @{ CONDUIT_COLLECTOR_HELPER_VALUE = 'child-only' }
    $argumentValue = 'literal space café 雪 "$()'
    $results = @(Invoke-BoundedProcess $helperChild @($argumentValue) 5 $helperEnvironment)
    Assert-Contract ($results.Count -eq 1 -and $results[0] -is [pscustomobject]) 'Private helper success must return exactly one result object.'
    $result = $results[0]
    $resultProperties = @($result.PSObject.Properties.Name | Sort-Object)
    Assert-Contract ($resultProperties.Count -eq 3 -and ($resultProperties -join '|') -ceq 'ExitCode|Stderr|Stdout' -and
        $result.ExitCode -is [int] -and $result.Stdout -is [string] -and $result.Stderr -is [string]) 'Private helper success result shape differs.'
    Assert-Contract ($result.ExitCode -eq 0) ('Private helper exit code was ' + $result.ExitCode + '.')
    $stdoutLines = @($result.Stdout -split '\r?\n')
    Assert-Contract ($stdoutLines.Count -ge 3) ('Private helper returned ' + $stdoutLines.Count + ' stdout lines.')
    Assert-Contract ($stdoutLines[0] -ceq $argumentValue) ('Private helper literal argument differs; actual UTF16 length ' + $stdoutLines[0].Length + ', expected ' + $argumentValue.Length + '.')
    Assert-Contract ($stdoutLines[1] -ceq 'child-only') 'Private helper child environment value differs.'
    Assert-Contract ($stdoutLines[2] -ceq 'True') 'Private helper inherited the benchmark credential.'
    Assert-Contract ($result.Stderr.Trim() -ceq 'private-child-diagnostic') 'Private helper stderr capture differs.'
    Assert-Contract ([Environment]::GetEnvironmentVariable('CONDUIT_COLLECTOR_HELPER_VALUE') -ceq $oldEnvironment.CONDUIT_COLLECTOR_HELPER_VALUE) 'Private helper changed parent environment.'
    $cases++

    $utf8HelperChild = Join-Path $temporaryRoot 'utf8-helper-child.ps1'
    $utf8HelperIdentity = Join-Path $temporaryRoot 'utf8-helper-child.identity'
    @'
param([string] $Value, [string] $IdentityPath)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
. (Join-Path $PSScriptRoot 'process-identity.ps1')
$self = [Diagnostics.Process]::GetCurrentProcess()
try { [IO.File]::WriteAllText($IdentityPath, ($self.Id.ToString() + '|' + (Get-OwnedProcessIdentityToken $self))) }
finally { $self.Dispose() }
[Console]::Out.WriteLine($Value)
[Console]::Error.WriteLine($Value)
'@ | Set-Content -LiteralPath $utf8HelperChild -Encoding utf8NoBOM
    $originalConsoleOutputEncoding = [Console]::OutputEncoding
    try {
        $legacyEncoding = [Text.CodePagesEncodingProvider]::Instance.GetEncoding(437)
        Assert-Contract ($null -ne $legacyEncoding -and $legacyEncoding.CodePage -eq 437) 'The portable CP437 fixture encoding is unavailable.'
        [Console]::OutputEncoding = $legacyEncoding
        Assert-Contract ([Console]::OutputEncoding.CodePage -eq 437 -and $argumentValue.Length -eq 25) 'The UTF8 decoding fixture did not establish its CP437 parent and exact literal.'
        $results = @(Invoke-BoundedProcess $utf8HelperChild @($argumentValue, $utf8HelperIdentity) 5)
        Assert-Contract ($results.Count -eq 1 -and $results[0] -is [pscustomobject]) 'The UTF8 decoding helper must return exactly one result object.'
        $result = $results[0]
        $resultProperties = @($result.PSObject.Properties.Name | Sort-Object)
        Assert-Contract ($resultProperties.Count -eq 3 -and ($resultProperties -join '|') -ceq 'ExitCode|Stderr|Stdout' -and
            $result.ExitCode -is [int] -and $result.Stdout -is [string] -and $result.Stderr -is [string]) 'The UTF8 decoding helper result shape differs.'
        $expectedOutput = $argumentValue + [Environment]::NewLine
        Assert-Contract ($result.ExitCode -eq 0 -and $result.Stdout -ceq $expectedOutput -and $result.Stderr -ceq $expectedOutput) 'The CP437 parent corrupted UTF8 child stdout or stderr.'
        Assert-Contract ([IO.File]::Exists($utf8HelperIdentity)) 'The UTF8 decoding child did not capture its owned identity.'
        Test-OwnedProcessStopped $utf8HelperIdentity
    }
    finally { [Console]::OutputEncoding = $originalConsoleOutputEncoding }
    Assert-Contract ([Console]::OutputEncoding.Equals($originalConsoleOutputEncoding)) 'The UTF8 decoding fixture did not restore the parent output encoding.'
    $cases++

    $helperEnvironment.CONDUIT_COLLECTOR_HELPER_VALUE = 'nonzero'
    $results = @(Invoke-BoundedProcess $helperChild @($argumentValue) 5 $helperEnvironment)
    Assert-Contract ($results.Count -eq 1 -and $results[0] -is [pscustomobject]) 'Private helper nonzero exit must return exactly one result object.'
    $result = $results[0]
    $resultProperties = @($result.PSObject.Properties.Name | Sort-Object)
    Assert-Contract ($resultProperties.Count -eq 3 -and ($resultProperties -join '|') -ceq 'ExitCode|Stderr|Stdout' -and
        $result.ExitCode -is [int] -and $result.Stdout -is [string] -and $result.Stderr -is [string]) 'Private helper nonzero result shape differs.'
    Assert-Contract ($result.ExitCode -eq 17 -and $result.Stderr.Trim() -ceq 'private-child-diagnostic') 'Private helper lost nonzero exit/captured output.'
    $cases++
    $helperIdentity = Join-Path $temporaryRoot 'helper-descendant.identity'
    $helperEnvironment.CONDUIT_COLLECTOR_HELPER_VALUE = 'timeout'
    $helperEnvironment.CONDUIT_COLLECTOR_HELPER_SLEEPER = $sleeper
    $helperEnvironment.CONDUIT_COLLECTOR_HELPER_IDENTITY = $helperIdentity
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $failure = $null
    $remainingBudgets = [Collections.Generic.List[int]]::new()
    $poll = { param([int] $RemainingSeconds); $remainingBudgets.Add($RemainingSeconds) }
    try { $unused = Invoke-BoundedProcess $helperChild @($argumentValue) 3 $helperEnvironment $poll }
    catch { $failure = $_.Exception.Message }
    Assert-Contract ($failure -ceq 'Private command failed. [stage=command-deadline]' -and $watch.Elapsed.TotalSeconds -lt 10 -and
        [IO.File]::Exists($helperIdentity)) 'Private helper timeout did not terminate within its cleanup deadline.'
    Test-OwnedProcessStopped $helperIdentity
    Assert-Contract ($remainingBudgets.Count -gt 0 -and @($remainingBudgets | Where-Object { $_ -lt 1 -or $_ -gt 2 }).Count -eq 0) 'Private helper did not bound polling by its remaining deadline.'
    $cases++
    $helperEnvironment.CONDUIT_COLLECTOR_HELPER_VALUE = 'overflow'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $failure = $null
    try { $unused = Invoke-BoundedProcess $helperChild @($argumentValue) 30 $helperEnvironment }
    catch { $failure = $_.Exception.Message }
    $matchedFailure = $failure -ceq 'Private command failed.'
    Assert-Contract ($matchedFailure -and $watch.Elapsed.TotalSeconds -lt 25) ('Private output cap did not terminate before timeout; elapsed seconds ' +
        [Math]::Round($watch.Elapsed.TotalSeconds, 3) + ', controlled failure matched ' + $matchedFailure + '.')
    $cases++

    # Real nested processes model the memory-poll boundary without adding Docker
    # containers or HTTP workloads. Every outer descendant and inner sleeper
    # must latch its identity before the error can satisfy the case.
    $privatePollSentinel = 'private-poll-' + [Guid]::NewGuid().ToString('N')
    foreach ($pollScenario in @('deadline', 'invalid-memory', 'zero-memory', 'private-callback')) {
        $outerIdentity = Join-Path $temporaryRoot ('helper-poll-' + $pollScenario + '-outer.identity')
        $innerIdentity = Join-Path $temporaryRoot ('helper-poll-' + $pollScenario + '-inner.identity')
        $statsScript = Join-Path $temporaryRoot ('helper-poll-' + $pollScenario + '-stats.ps1')
        $statsSource = @'
param([Parameter(ValueFromRemainingArguments)][string[]] $CommandArguments)
. (Join-Path $PSScriptRoot 'process-identity.ps1')
$mode = '__MODE__'
if ($mode -ceq 'deadline') {
    $self = [Diagnostics.Process]::GetCurrentProcess()
    try { [IO.File]::WriteAllText((Join-Path $PSScriptRoot '__IDENTITY__'),
        ($self.Id.ToString() + '|' + (Get-OwnedProcessIdentityToken $self))) }
    finally { $self.Dispose() }
    Start-Sleep -Seconds 60
}
if ($mode -ceq 'invalid-memory') { [Console]::Out.WriteLine('not-a-memory-value __PRIVATE__') }
if ($mode -ceq 'zero-memory') { [Console]::Out.WriteLine('0 B / 128 GiB') }
exit 0
'@
        $statsSource = $statsSource.Replace('__MODE__', $pollScenario).Replace('__IDENTITY__', [IO.Path]::GetFileName($innerIdentity)).Replace('__PRIVATE__', $privatePollSentinel)
        [IO.File]::WriteAllText($statsScript, $statsSource, [Text.UTF8Encoding]::new($false))
        $outerEnvironment = @{
            CONDUIT_COLLECTOR_HELPER_VALUE = 'timeout'
            CONDUIT_COLLECTOR_HELPER_SLEEPER = $sleeper
            CONDUIT_COLLECTOR_HELPER_IDENTITY = $outerIdentity
        }
        $poll = {
            param([int] $RemainingSeconds)
            if (-not [IO.File]::Exists($outerIdentity)) { return }
            if ($pollScenario -ceq 'private-callback') { throw ($privatePollSentinel + $privateKey + $privateDatabasePassword) }
            $unusedMemory = Get-ContainerMemory 'owned-stats-target' $RemainingSeconds
        }
        $hadDockerPath = $null -ne (Get-Variable DockerCommandPath -ErrorAction SilentlyContinue)
        $hadDockerTimeout = $null -ne (Get-Variable DockerCommandTimeoutSeconds -ErrorAction SilentlyContinue)
        $oldDockerPath = $DockerCommandPath; $oldDockerTimeout = $DockerCommandTimeoutSeconds
        $DockerCommandPath = $statsScript; $DockerCommandTimeoutSeconds = 5
        $watch = [Diagnostics.Stopwatch]::StartNew(); $failure = $null
        try { $unused = Invoke-BoundedProcess $helperChild @($argumentValue) 20 $outerEnvironment $poll }
        catch { $failure = $_.Exception.Message }
        finally {
            $watch.Stop()
            if ($hadDockerPath) { $DockerCommandPath = $oldDockerPath } else { Remove-Variable DockerCommandPath }
            if ($hadDockerTimeout) { $DockerCommandTimeoutSeconds = $oldDockerTimeout } else { Remove-Variable DockerCommandTimeoutSeconds }
        }
        $tag = switch ($pollScenario) {
            'deadline' { 'poll-command-deadline' }
            'invalid-memory' { 'poll-memory-sample' }
            'zero-memory' { 'poll-memory-value' }
            'private-callback' { 'poll-callback' }
        }
        $expected = 'Private command failed. [stage=' + $tag + ']'
        Assert-Contract ($failure -ceq $expected -and $watch.Elapsed.TotalSeconds -lt 30 -and [IO.File]::Exists($outerIdentity)) 'The nested poll fixture did not reach its latched controlled failure within bounded cleanup.'
        Test-OwnedProcessStopped $outerIdentity
        if ($pollScenario -ceq 'deadline') {
            Assert-Contract ([IO.File]::Exists($innerIdentity)) 'The inner deadline fixture did not start its owned process.'
            Test-OwnedProcessStopped $innerIdentity
        }
        Assert-Contract (-not $failure.Contains($privatePollSentinel) -and -not $failure.Contains($privateKey) -and
            -not $failure.Contains($privateDatabasePassword)) 'The controlled poll failure exposed private text.'
        $safe = Get-SafeFailureMessage ([InvalidOperationException]::new($failure)) 'fixed-fallback'
        Assert-Contract ($safe -ceq $expected) 'The collection boundary discarded its closed poll classification.'
        $unknown = Get-SafeFailureMessage ([InvalidOperationException]::new($privatePollSentinel)) 'fixed-fallback'
        Assert-Contract ($unknown -ceq 'fixed-fallback') 'Unknown private failures escaped the fixed fallback.'
        $cases++
    }
    Invoke-OwnedProcessCleanupContracts
    Invoke-CollectorCase 'admin' 'lowercase-missing-object' $null
    Invoke-CollectorCase 'admin' 'lowercase-missing-container' $null
    Invoke-CollectorCase 'admin' 'foreign-missing-identity' 'cleanup failed'
    Invoke-CollectorCase 'admin' 'missing-with-payload' 'cleanup failed'
    Invoke-CollectorCase 'admin' 'jit-index' $null
    foreach ($scenario in @('jit-index-ambiguous', 'jit-index-missing', 'jit-index-malformed')) {
        Invoke-CollectorCase 'admin' $scenario 'No qualification was recorded.'
    }
    Invoke-CollectorCase 'admin' 'even-pairs' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'revision-mismatch' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'driver-nonzero' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'driver-timeout' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'unknown-create' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'foreign-label' 'cleanup failed'
    Invoke-CollectorCase 'admin' 'cleanup-failure' 'cleanup failed'
    foreach ($scenario in @('shutdown-command-failure', 'shutdown-forced-exit', 'shutdown-still-running',
        'shutdown-invalid-exit', 'shutdown-foreign-label')) {
        Invoke-CollectorCase 'gateway' $scenario 'cleanup failed'
    }
    Invoke-CollectorCase 'admin' 'incomplete-report' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'child-string-schema' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'fingerprint-mismatch' 'No qualification was recorded.'
    foreach ($scenario in @('package-absent', 'legacy-baseline', 'package-wrong-service', 'package-cross-build',
        'package-dirty', 'package-unavailable', 'package-hold', 'candidate-config', 'candidate-selected-swap')) {
        Invoke-CollectorCase 'admin' $scenario 'No qualification was recorded.'
    }
    Invoke-CollectorCase 'gateway' 'native-selected-mismatch' 'Native benchmark container image binding failed.'
    Invoke-CollectorCase 'gateway' 'package-late-mutation' 'Native benchmark image changed during collection.'
    # A previous successful alias is deliberately present for both real reruns.
    # Readers must reject the newly failed latest attempt even though A survives.
    Invoke-CollectorCase 'admin' 'even-pairs' 'No qualification was recorded.' -ExistingOutputPath $successfulAdmin.outputPath
    Invoke-CollectorCase 'admin' 'driver-nonzero' 'No qualification was recorded.' -ExistingOutputPath $successfulAdmin.outputPath
    Invoke-CollectorCase 'admin' 'diagnostic-readiness' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'diagnostic-readiness-cleanup' 'No qualification was recorded.' -ExistingOutputPath $successfulAdmin.outputPath
    Invoke-CollectorCase 'admin' 'diagnostic-private-cleanup' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'diagnostic-readiness-private-cleanup' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'diagnostic-private-child' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'diagnostic-private-config' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'diagnostic-poll-memory' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'diagnostic-probe-partial' 'No qualification was recorded.' -ExistingOutputPath $successfulAdmin.outputPath
    Invoke-CollectorCase 'gateway' 'diagnostic-probe-all-failed' 'No qualification was recorded.'
    Invoke-CollectorCase 'gateway' 'diagnostic-probe-private' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'diagnostic-probe-malformed' 'No qualification was recorded.'
    Invoke-FailedWorkloadAggregateContracts
    Invoke-AttemptLifecycleContracts $successfulAdmin
    Invoke-FailureDiagnosticSchemaContracts
    }
    }
    }
}
catch { $proofFailure = $_.Exception }
finally {
    $processCleanupConfirmed = $true
    try {
        foreach ($server in $ownedReadinessServers) {
            if ($server.Disposed) { continue }
            try { Stop-OwnedReadinessServer $server }
            catch {
                $processCleanupConfirmed = $false
                $cleanupFailures.Add('Original owned readiness cleanup failed.')
            }
        }
        if ([IO.Directory]::Exists($temporaryRoot)) {
            # The collector deliberately refuses foreign labels. The test still
            # owns its readiness listeners and stops only verified PID identities.
            foreach ($identityFile in Get-ChildItem -LiteralPath $temporaryRoot -File -Recurse -Filter '*.identity') {
                try { Test-OwnedProcessStopped $identityFile.FullName -Stop }
                catch {
                    $processCleanupConfirmed = $false
                    $cleanupFailures.Add('Owned fixture process cleanup failed.')
                }
            }
        }
    }
    catch {
        $processCleanupConfirmed = $false
        $cleanupFailures.Add('Owned fixture identity enumeration failed.')
    }
    finally {
        foreach ($name in $environmentNames) {
            try { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name]) }
            catch { $cleanupFailures.Add('Parent environment restoration failed.') }
        }
        try {
            if ([IO.Directory]::Exists($temporaryRoot)) {
                Assert-Contract ($processCleanupConfirmed -and
                    @($ownedReadinessServers | Where-Object { -not $_.Reaped }).Count -eq 0) 'Fixture directory retained because process cleanup was not confirmed.'
                $resolved = [IO.Path]::GetFullPath($temporaryRoot)
                $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
                $leaf = [IO.Path]::GetFileName($resolved)
                Assert-Contract ($resolved -ceq $temporaryRoot -and
                    [IO.Path]::GetDirectoryName($resolved).TrimEnd([IO.Path]::DirectorySeparatorChar) -ceq $expectedParent -and
                    $leaf -cmatch '^conduit-collector-contract-[0-9a-f]{32}$') 'Collector contract cleanup ownership could not be verified.'
                Remove-Item -LiteralPath $resolved -Recurse -Force
            }
        }
        catch { $cleanupFailures.Add('Owned fixture directory cleanup failed.') }
    }
}
if ($cleanupFailures.Count -gt 0) {
    if ($null -ne $proofFailure) { throw ('Collector contract proof and fixture cleanup failed; cleanup failures ' + $cleanupFailures.Count + '.') }
    throw ('Collector contract fixture cleanup failed; cleanup failures ' + $cleanupFailures.Count + '.')
}
if ($null -ne $proofFailure) { throw $proofFailure }
if ($NetworkBindingOnly) {
    Write-Host ('PASS: ' + $cases + ' pure fixture network contracts; strict private names/JSON, immutable bridge pin and actual running attachment validation; no Docker/driver processes ran.')
}
elseif ($DockerNetworkOnly) {
    Write-Host ('PASS: ' + $cases + ' fixture network contracts; all twelve alternating Gateway variants, strict pre-workload/final attachment rejection, prior-alias preservation and owned cleanup.')
}
elseif ($DiagnosticSchemaOnly) {
    Write-Host ('PASS: ' + $cases + ' failure diagnostic schema/writer and failed-workload projection contracts; strict codes, immutable sidecars, privacy and failed eligibility; no Docker/driver workload cases ran.')
}
elseif ($StrictCallerOnly) {
    Write-Host ('PASS: ' + $cases + ' strict-caller collector contracts; complete authenticated Admin/Gateway workload pairs, private configuration rejection, owned cleanup and latest-attempt evidence.')
}
elseif ($FailedWorkloadOnly) {
    Write-Host ('PASS: ' + $cases + ' failed-workload collector contracts; one successful setup, four failed process cases, prior-alias preservation, projections, lifecycle, schema and owned cleanup.')
}
else {
    Write-Host ('PASS: ' + $cases + ' collector contracts; owned mocked Docker/driver processes, authenticated workload pairs, warm exclusion, private deadlines, strict cleanup and latest-attempt evidence.')
}
