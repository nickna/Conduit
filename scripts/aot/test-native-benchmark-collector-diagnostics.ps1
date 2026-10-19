#!/usr/bin/env pwsh
[CmdletBinding()]
param([switch] $ProjectionOnly)

$ErrorActionPreference = 'Stop'

# The collector suite intentionally dot-sources its helpers and changes script
# variables. Keep both its inner invocation and this proof in isolated scopes.
& {
    param([string] $ContractDirectory, [bool] $OnlyProjection)

    function Assert-ProjectionContract([bool] $Condition, [string] $Message) {
        if (-not $Condition) { throw $Message }
    }

    $suitePath = Join-Path $ContractDirectory 'test-native-benchmark-collector.ps1'
    . (Join-Path $ContractDirectory 'native-benchmark-attempt-evidence.ps1')
    $tokens = $null; $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($suitePath, [ref]$tokens, [ref]$parseErrors)
    Assert-ProjectionContract ($parseErrors.Count -eq 0) 'Collector diagnostic contract syntax is invalid.'
    $definitions = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq 'Get-CaseAttemptFailureProjection'
    }, $true))
    Assert-ProjectionContract ($definitions.Count -eq 1) 'Collector diagnostic projection helper is ambiguous.'
    Invoke-Expression $definitions[0].Extent.Text

    $projectionLeaf = 'conduit-collector-diagnostics-' + [guid]::NewGuid().ToString('N')
    $projectionRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) $projectionLeaf))
    $projectionCases = 0
    $context = $null
    try {
        [IO.Directory]::CreateDirectory($projectionRoot) | Out-Null
        $outputPath = Join-Path $projectionRoot 'report.json'
        $context = Start-NativeBenchmarkAttempt -OutputPath $outputPath -Service 'gateway'
        Set-NativeBenchmarkAttemptFailed -Context $context -FailureCode 'collection-failed'
        $inputDiagnostic = [ordered]@{
            phase = 'workload'; reason = 'probe-exit'; runtime = 'jit'; sampleKind = 'warmup'; pair = $null
            ownedContainerCleanup = @{ status = 'confirmed'; reason = 'none' }
            privateFileCleanup = @{ status = 'confirmed'; reason = 'none' }
            workloadAggregate = [ordered]@{
                phase = 'measured'; id = 'gateway-chat-nonstream'; requested = 4; executed = 4; completed = 3
                failures = 1; warmupRequests = 2; concurrency = 2; timeoutSeconds = 5
            }
        }
        Set-NativeBenchmarkAttemptFailureDiagnostic -Context $context -Diagnostic $inputDiagnostic
        $diagnosticPath = Join-Path $context.attemptDirectory 'diagnostics.json'
        $original = @{}
        foreach ($entry in @(@('latest', $context.latestPath), @('attempt', $context.attemptPath), @('diagnostic', $diagnosticPath))) {
            $original[$entry[0]] = [ordered]@{ path = $entry[1]; bytes = [IO.File]::ReadAllBytes($entry[1]) }
        }
        Close-NativeBenchmarkAttempt -Context $context
        $context = $null
        $privateSentinel = 'private-projection-' + [guid]::NewGuid().ToString('N')

        function Restore-ProjectionFixture {
            foreach ($entry in $original.Values) { [IO.File]::WriteAllBytes($entry.path, $entry.bytes) }
        }

        function New-ProjectionFixture {
            $fixture = @{}
            foreach ($name in $original.Keys) { $fixture[$name] = Read-NativeOciJson $original[$name].bytes }
            $fixture.private = $privateSentinel
            return ,$fixture
        }

        function Write-ProjectionFixture($Fixture) {
            foreach ($name in $original.Keys) {
                [IO.File]::WriteAllText($original[$name].path,
                    (ConvertTo-Json -InputObject $Fixture[$name] -Depth 16 -Compress), [Text.UTF8Encoding]::new($false))
            }
        }

        $expectedFields = 'phase|reason|runtime|sampleKind|pair|ownedContainerCleanup|privateFileCleanup|workloadAggregate'
        foreach ($version in @('aggregate', 'null', 'legacy', 'recorded')) {
            $fixture = New-ProjectionFixture
            if ($version -cin @('null', 'legacy')) { $fixture.diagnostic.workloadAggregate = $null }
            if ($version -ceq 'legacy') {
                $fixture.diagnostic.schemaVersion = 1
                $fixture.diagnostic.Remove('workloadAggregate') | Out-Null
            }
            if ($version -ceq 'recorded') {
                $fixture.diagnostic.runtime = 'native-aot'; $fixture.diagnostic.sampleKind = 'recorded'; $fixture.diagnostic.pair = 21
                $fixture.diagnostic.ownedContainerCleanup.reason = 'shutdown-failed'
                $fixture.diagnostic.privateFileCleanup.reason = 'cleanup-failed'
            }
            Write-ProjectionFixture $fixture
            $result = @(Get-CaseAttemptFailureProjection $outputPath 'gateway')
            Assert-ProjectionContract ($result.Count -eq 1 -and $result[0] -is [string] -and
                $result[0] -cne 'unavailable' -and [Text.Encoding]::UTF8.GetByteCount($result[0]) -le 4096) 'Valid diagnostics did not produce one bounded projection.'
            $projection = $result[0] | ConvertFrom-Json -AsHashtable -DateKind String
            Assert-ProjectionContract (($projection.Keys -join '|') -ceq $expectedFields -and
                $projection.phase -ceq 'workload' -and $projection.reason -ceq 'probe-exit' -and
                $projection.runtime -ceq $fixture.diagnostic.runtime -and $projection.sampleKind -ceq $fixture.diagnostic.sampleKind -and
                $projection.pair -ceq $fixture.diagnostic.pair -and
                $projection.ownedContainerCleanup.reason -ceq $fixture.diagnostic.ownedContainerCleanup.reason -and
                $projection.privateFileCleanup.reason -ceq $fixture.diagnostic.privateFileCleanup.reason) 'Diagnostic projection changed closed fields or retained document identity.'
            Assert-ProjectionContract (($null -eq $projection.workloadAggregate) -eq ($version -cin @('null', 'legacy'))) 'Diagnostic projection changed aggregate availability.'
            if ($null -ne $projection.workloadAggregate) {
                Assert-ProjectionContract ((ConvertTo-Json -InputObject $projection.workloadAggregate -Compress) -ceq
                    (ConvertTo-Json -InputObject $inputDiagnostic.workloadAggregate -Compress)) 'Diagnostic projection changed validated workload counts.'
            }
            $projectionCases++
        }

        $mutations = @(
            { param($f) $f.diagnostic.reason = $f.private },
            { param($f) $f.diagnostic.phase = 'Workload' },
            { param($f) $f.diagnostic.runtime = $f.private },
            { param($f) $f.diagnostic.sampleKind = $f.private },
            { param($f) $f.diagnostic.pair = 1 },
            { param($f) $f.diagnostic.ownedContainerCleanup.reason = $f.private },
            { param($f) $f.diagnostic.privateFileCleanup.status = $f.private },
            { param($f) $f.diagnostic.privateConfiguration = $f.private },
            { param($f) $f.diagnostic.workloadAggregate.completed = '3' },
            { param($f) $f.diagnostic.workloadAggregate.completed = 5 },
            { param($f) $f.diagnostic.workloadAggregate.failures = 0 },
            { param($f) $f.diagnostic.workloadAggregate.requested = 100001 },
            { param($f) $f.diagnostic.workloadAggregate.privateRequest = $f.private },
            { param($f) $f.diagnostic.workloadAggregate.id = 'admin-setting-read' },
            { param($f) $f.diagnostic.service = 'admin' },
            { param($f) $f.attempt.service = 'admin' },
            { param($f) $f.latest.service = 'admin' },
            { param($f) $f.diagnostic.attemptId = 'f' * 32 },
            { param($f) $f.attempt.attemptId = 'f' * 32 },
            { param($f) $f.latest.attemptId = 'f' * 32 },
            { param($f) $f.latest.attemptId = '../' + $f.private },
            { param($f) $f.latest.privateArguments = $f.private },
            { param($f) $f.attempt.privateOutput = $f.private },
            { param($f) $f.attempt.status = 'succeeded'; $f.attempt.failureCode = $null; $f.attempt.reportSha256 = 'f' * 64 },
            { param($f) $f.attempt.status = 'running'; $f.attempt.failureCode = $null; $f.attempt.completedAt = $null }
        )
        foreach ($mutation in $mutations) {
            $fixture = New-ProjectionFixture
            & $mutation $fixture
            Write-ProjectionFixture $fixture
            $result = @(Get-CaseAttemptFailureProjection $outputPath 'gateway')
            Assert-ProjectionContract ($result.Count -eq 1 -and $result[0] -ceq 'unavailable') 'Invalid, mismatched or private diagnostics escaped the fixed projection fallback.'
            $projectionCases++
        }
        foreach ($name in @('latest', 'attempt', 'diagnostic')) {
            foreach ($invalid in @('missing', 'malformed', 'duplicate', 'oversized')) {
                Restore-ProjectionFixture
                $path = $original[$name].path
                switch ($invalid) {
                    'missing' { Remove-Item -LiteralPath $path -Force }
                    'malformed' { [IO.File]::WriteAllText($path, ('{' + $privateSentinel)) }
                    'duplicate' {
                        $text = [Text.Encoding]::UTF8.GetString($original[$name].bytes)
                        $text = $text.Replace('"schemaVersion":', '"schemaVersion":1,"schemaVersion":')
                        [IO.File]::WriteAllText($path, $text)
                    }
                    'oversized' {
                        $padding = if ($name -ceq 'diagnostic') { 4097 } else { 65537 }
                        [IO.File]::WriteAllText($path, ((' ' * $padding) + [Text.Encoding]::UTF8.GetString($original[$name].bytes)))
                    }
                }
                $result = @(Get-CaseAttemptFailureProjection $outputPath 'gateway')
                Assert-ProjectionContract ($result.Count -eq 1 -and $result[0] -ceq 'unavailable') 'Unavailable or malformed evidence did not use the fixed projection fallback.'
                $projectionCases++
            }
        }
        Restore-ProjectionFixture
        foreach ($service in @('admin', $privateSentinel)) {
            Assert-ProjectionContract ((Get-CaseAttemptFailureProjection $outputPath $service) -ceq 'unavailable') 'Projection accepted a mismatched requested service.'
            $projectionCases++
        }
        foreach ($entry in $original.Values) {
            Assert-ProjectionContract ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($entry.path))) -ceq
                [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entry.bytes))) 'Diagnostic projection changed evidence bytes.'
        }
        # Execute the actual outer catch independently of its positive assertion.
        # An arbitrary private error must never become its diagnostic output.
        $suiteBoundaries = @($ast.EndBlock.Statements | Where-Object {
            $_ -is [Management.Automation.Language.TryStatementAst] -and $_.CatchClauses.Count -eq 1 -and
            $_.CatchClauses[0].Body.Extent.Text.Contains('Collector case attempt diagnostic ')
        })
        Assert-ProjectionContract ($suiteBoundaries.Count -eq 1) 'Collector suite diagnostic emission boundary is ambiguous.'
        $suiteCatchText = $suiteBoundaries[0].CatchClauses[0].Body.Extent.Text
        $suiteCatch = [scriptblock]::Create($suiteCatchText.Substring(1, $suiteCatchText.Length - 2))
        foreach ($snapshot in @((Get-CaseAttemptFailureProjection $outputPath 'gateway'), 'unavailable')) {
            $script:lastCaseFailureProjection = $snapshot
            $catchRecords = @()
            try { throw [InvalidOperationException]::new($privateSentinel) }
            catch { $catchRecords = @(. $suiteCatch 6>&1) }
            Assert-ProjectionContract ($catchRecords.Count -eq 1 -and
                [string]$catchRecords[0] -ceq ('Collector case attempt diagnostic ' + $snapshot) -and
                -not ([string]$catchRecords[0]).Contains($privateSentinel)) 'Collector suite catch exposed private error text or lost its independently retained projection.'
            $projectionCases++
        }
        $script:lastCaseFailureProjection = 'unavailable'
    }
    finally {
        if ($null -ne $context) { Close-NativeBenchmarkAttempt -Context $context }
        if ([IO.Directory]::Exists($projectionRoot)) {
            $resolved = [IO.Path]::GetFullPath($projectionRoot)
            $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
            Assert-ProjectionContract ([IO.Path]::GetDirectoryName($resolved).TrimEnd([IO.Path]::DirectorySeparatorChar) -ceq $expectedParent -and
                [IO.Path]::GetFileName($resolved) -cmatch '^conduit-collector-diagnostics-[0-9a-f]{32}$') 'Diagnostic projection fixture cleanup ownership could not be verified.'
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }

    if (-not $OnlyProjection) {
        & {
            param([string] $CollectorSuitePath)
            $unexpectedPositiveFailure = $null
            $regressionPublicRecords = [Collections.Generic.List[object]]::new()
            try { . $CollectorSuitePath -UnexpectedPositiveFailureOnly *>&1 | ForEach-Object { $regressionPublicRecords.Add($_) } }
            catch { $unexpectedPositiveFailure = $_ }
            Assert-ProjectionContract ($null -ne $unexpectedPositiveFailure -and
                $unexpectedPositiveFailure.Exception.Message.StartsWith('Successful fake collection failed;', [StringComparison]::Ordinal)) 'Controlled unexpected collection did not reach the real positive assertion.'
            Assert-ProjectionContract (-not [IO.Directory]::Exists($temporaryRoot) -and $cleanupFailures.Count -eq 0 -and
                $ownedReadinessServers.Count -eq 2 -and
                @($ownedReadinessServers | Where-Object { -not $_.Reaped -or -not $_.Disposed }).Count -eq 0) 'Unexpected-positive regression did not complete owned fixture cleanup.'
            foreach ($name in $environmentNames) {
                Assert-ProjectionContract ([Environment]::GetEnvironmentVariable($name) -ceq $oldEnvironment[$name]) ('Unexpected-positive regression did not restore parent environment variable ' + $name + '.')
            }
            $publicText = $unexpectedPositiveFailure | Out-String
            $publicText += $regressionPublicRecords | Out-String
            foreach ($privateText in @($privateKey, $privateDatabasePassword, $privateRedisPassword, $temporaryRoot,
                'private-child-diagnostic', 'private-cleanup-sentinel', 'conduit-benchmark-', 'configurationPath',
                'postgresql://', 'redis://', 'native benchmark café 雪', 'fixture response', 'native-aot-model')) {
                Assert-ProjectionContract (-not $publicText.Contains($privateText)) 'Unexpected-positive failure exposed private output, configuration, request content or fixture paths.'
            }
            $message = $unexpectedPositiveFailure.Exception.Message
            $projectionMatch = [regex]::Match($message, '(?<projection>\{"phase":.*\})')
            Assert-ProjectionContract ($projectionMatch.Success -and [Text.Encoding]::UTF8.GetByteCount($projectionMatch.Value) -le 4096) 'Unexpected-positive failure lost its bounded diagnostic projection after fixture deletion.'
            Assert-ProjectionContract (@($regressionPublicRecords | Where-Object {
                [string]$_ -ceq ('Collector case attempt diagnostic ' + $projectionMatch.Value)
            }).Count -eq 1) 'Unexpected-positive failure did not emit the same retained projection before fixture cleanup.'
            $projection = Read-NativeOciJson ([Text.Encoding]::UTF8.GetBytes($projectionMatch.Value))
            Assert-ProjectionContract (($projection.Keys -join '|') -ceq $expectedFields -and
                $projection.phase -ceq 'workload' -and $projection.reason -ceq 'probe-exit' -and
                $projection.runtime -ceq 'jit' -and $projection.sampleKind -ceq 'warmup' -and $null -eq $projection.pair -and
                $projection.ownedContainerCleanup.status -ceq 'confirmed' -and $projection.ownedContainerCleanup.reason -ceq 'none' -and
                $projection.privateFileCleanup.status -ceq 'confirmed' -and $projection.privateFileCleanup.reason -ceq 'none') 'Unexpected-positive failure changed primary or independent cleanup codes.'
            $aggregate = $projection.workloadAggregate
            Assert-ProjectionContract ($aggregate.Count -eq 9 -and $aggregate.phase -ceq 'measured' -and
                $aggregate.id -ceq 'gateway-chat-nonstream' -and $aggregate.requested -eq 4 -and
                $aggregate.executed -eq 4 -and $aggregate.completed -eq 3 -and $aggregate.failures -eq 1 -and
                $aggregate.warmupRequests -eq 2 -and $aggregate.concurrency -eq 2 -and $aggregate.timeoutSeconds -eq 5) 'Unexpected-positive failure discarded validated measured counts.'
        } $suitePath
        $projectionCases++
    }
    Write-Host ('PASS: ' + $projectionCases + ' collector diagnostic projection contracts; closed fields, validated counts, private evidence rejection and owned cleanup.')
} $PSScriptRoot ([bool]$ProjectionOnly)
