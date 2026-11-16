#!/usr/bin/env pwsh
[CmdletBinding()]
param([string]$OutputPath = ('artifacts/queue-report-tests/' + [guid]::NewGuid().ToString('N') + '.json'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'queue-report.ps1')
$destination = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $destination) { throw 'Refuse to overwrite previous policy-test evidence.' }
$directory = [IO.Path]::GetDirectoryName($destination)
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$fixtures = Join-Path $directory ('queue-policy-fixtures-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtures | Out-Null
$architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()

function New-QueueTestPhase {
    $records = @(0..1 | ForEach-Object {
        @{ eventId = [guid]::NewGuid().ToString(); sequence = $_; messageId = [guid]::NewGuid().ToString();
            publishedTicks = 100 + $_*100; startedTicks = 110 + $_*100; finishedTicks = 120 + $_*100;
            settlementObservedTicks = 300; syntheticAmount = [decimal]0.01; error = $null }
    })
    return @{ phaseId = [guid]::NewGuid().ToString('N'); expected = 2; registered = 2; started = 2; completed = 2; settled = 2;
        inFlight = 0; maxConcurrency = 1; duplicateAttempts = 0; orderViolations = 0; errors = @(); deliveries = $records;
        complete = $true; registeredSyntheticAmount = [decimal]0.02; settledSyntheticAmount = [decimal]0.02;
        storeSamples = @(@{ observedTicks = 300; ready = 0; scheduled = 0; incoming = 0; outgoing = 0; deadLetters = 0; newlyObservedHandled = 2 }) }
}
$valid = @{ schemaVersion = 2; passed = $true; error = $null; runtime = '.NET 11.0.0'; runtimeVersion = '11.0.0';
    architecture = $architecture; processId = 123; serverGc = $false; gcConfiguration = @{ ServerGC = 'False' };
    gcConfigurationTypes = @{ ServerGC = 'System.Boolean' }; coreLibrarySha256 = ('a'*64);
    results = @(@{ queue = 'spend'; handlerCostMs = 0; offeredRate = 0; receiveBatch = 10; complete = $true; warmupComplete = $true;
        publishRate = 100; handlerCompletionRate = 90; settlementObservedRate = 80; elapsedSeconds = 1;
        latencyP50Ms = 10; latencyP95Ms = 20; latencyP99Ms = 30; saturationObserved = $false;
        databaseRttMs = @((1..20) | ForEach-Object { [double]0.1 }); warmup = New-QueueTestPhase; measurement = New-QueueTestPhase; error = $null;
        setup = @{ ready = $true; polls = 1; elapsedMilliseconds = 1; error = $null;
            requiredRelations = @('wolverine_queues.wolverine_queue_spend_update_events','wolverine_queues.wolverine_queue_spend_update_events_scheduled','wolverine_conduit_bench.wolverine_incoming_envelopes','wolverine_conduit_bench.wolverine_outgoing_envelopes','wolverine_conduit_bench.wolverine_dead_letters');
            presentRelations = @('wolverine_queues.wolverine_queue_spend_update_events','wolverine_queues.wolverine_queue_spend_update_events_scheduled','wolverine_conduit_bench.wolverine_incoming_envelopes','wolverine_conduit_bench.wolverine_outgoing_envelopes','wolverine_conduit_bench.wolverine_dead_letters') } }) }
$baseline = $valid | ConvertTo-Json -Depth 30 -Compress
$checks = [Collections.Generic.List[object]]::new()
function Test-QueueMutation([string]$Name, [scriptblock]$Mutation, [bool]$ShouldPass = $false) {
    $copy = $baseline | ConvertFrom-Json -AsHashtable
    & $Mutation $copy
    $path = Join-Path $fixtures ($Name + '.json')
    [IO.File]::WriteAllText($path, ($copy | ConvertTo-Json -Depth 30 -Compress))
    Test-QueueRaw $Name $path $ShouldPass
}
function Test-QueueRaw([string]$Name, [string]$Path, [bool]$ShouldPass = $false) {
    $accepted = $false
    $message = $null
    try { $null = Assert-QueueCaseReport -Path $Path -Batch 10 -Messages 2 -HandlerCostMs 0 -Warmup 2 -ExpectedArchitecture $architecture; $accepted = $true }
    catch { $message = $_.Exception.Message }
    $checks.Add([pscustomobject]@{ name = $Name; passed = ($accepted -eq $ShouldPass); accepted = $accepted; expectedAccepted = $ShouldPass; error = $message })
    if ($accepted -ne $ShouldPass) { throw "Queue policy test failed: $Name; $message" }
}
$passed = $false
try {
    Test-QueueMutation 'valid-shallow-complete-not-capacity' { param($d) } $true
    Test-QueueMutation 'even-sample-median' { param($d) $d.results[0].databaseRttMs = @(1..20) } $true
    $median = Assert-QueueCaseReport -Path (Join-Path $fixtures 'even-sample-median.json') -Batch 10 -Messages 2 -HandlerCostMs 0 -Warmup 2 -ExpectedArchitecture $architecture
    if ($median.SqlRttMedianMs -ne 10.5) { throw 'Even-sized RTT samples require the mean of the middle pair.' }
    Test-QueueMutation 'failed-report' { param($d) $d.passed = $false }
    Test-QueueMutation 'coerced-true' { param($d) $d.passed = 'true' }
    Test-QueueMutation 'failed-warmup' { param($d) $d.results[0].warmupComplete = $false }
    Test-QueueMutation 'incomplete' { param($d) $d.results[0].complete = $false }
    Test-QueueMutation 'missing-completion' { param($d) $d.results[0].measurement.completed = 1 }
    Test-QueueMutation 'duplicates' { param($d) $d.results[0].measurement.duplicateAttempts = 1 }
    Test-QueueMutation 'ordering' { param($d) $d.results[0].measurement.orderViolations = 1 }
    Test-QueueMutation 'concurrent' { param($d) $d.results[0].measurement.maxConcurrency = 2 }
    Test-QueueMutation 'duplicate-envelope' { param($d) $d.results[0].measurement.deliveries[1].messageId = $d.results[0].measurement.deliveries[0].messageId }
    Test-QueueMutation 'duplicate-event' { param($d) $d.results[0].measurement.deliveries[1].eventId = $d.results[0].measurement.deliveries[0].eventId }
    Test-QueueMutation 'missing-envelope' { param($d) $d.results[0].measurement.deliveries[0].messageId = $null }
    Test-QueueMutation 'early-settlement' { param($d) $d.results[0].measurement.deliveries[0].settlementObservedTicks = 119 }
    Test-QueueMutation 'no-finish' { param($d) $d.results[0].measurement.deliveries[0].finishedTicks = 0 }
    Test-QueueMutation 'queued-spend' { param($d) $d.results[0].measurement.storeSamples[0].ready = 1 }
    Test-QueueMutation 'pending-outbox' { param($d) $d.results[0].measurement.storeSamples[0].outgoing = 1 }
    Test-QueueMutation 'dead-letter' { param($d) $d.results[0].measurement.storeSamples[0].deadLetters = 1 }
    Test-QueueMutation 'no-store-observer' { param($d) $d.results[0].measurement.storeSamples = @() }
    Test-QueueMutation 'incomplete-observed-ids' { param($d) $d.results[0].measurement.storeSamples[0].newlyObservedHandled = 1 }
    Test-QueueMutation 'wrong-batch' { param($d) $d.results[0].receiveBatch = 50 }
    Test-QueueMutation 'coerced-count' { param($d) $d.results[0].measurement.completed = '2' }
    Test-QueueMutation 'wrong-patch' { param($d) $d.runtime = '.NET 11.0.1'; $d.runtimeVersion = '11.0.1' }
    Test-QueueMutation 'preview-runtime' { param($d) $d.runtime = '.NET 11.0.0-preview.1'; $d.runtimeVersion = '11.0.0-preview.1' }
    Test-QueueMutation 'wrong-architecture' { param($d) $d.architecture = if ($architecture -eq 'X64') { 'Arm64' } else { 'X64' } }
    Test-QueueMutation 'coerced-gc' { param($d) $d.serverGc = 'false' }
    Test-QueueMutation 'missing-gc-config' { param($d) $d.gcConfiguration = @{} }
    Test-QueueMutation 'missing-rtt' { param($d) $d.results[0].databaseRttMs = @() }
    Test-QueueMutation 'schema-not-ready' { param($d) $d.results[0].setup.ready = $false }
    Test-QueueMutation 'schema-ready-coercion' { param($d) $d.results[0].setup.ready = 'true' }
    Test-QueueMutation 'schema-never-probed' { param($d) $d.results[0].setup.polls = 0 }
    Test-QueueMutation 'schema-query-failure' { param($d) $d.results[0].setup.error = 'query failed' }
    Test-QueueMutation 'schema-missing-relation' { param($d) $d.results[0].setup.presentRelations = @($d.results[0].setup.presentRelations)[0..3] }
    Test-QueueMutation 'schema-wrong-required-relation' { param($d) $d.results[0].setup.requiredRelations[0] = 'other.queue' }
    Test-QueueMutation 'schema-duplicate-relation' { param($d) $d.results[0].setup.presentRelations[1] = $d.results[0].setup.presentRelations[0] }
    Test-QueueMutation 'schema-missing-setup' { param($d) $d.results[0].setup = $null }
    Test-QueueMutation 'money-string' { param($d) $d.results[0].measurement.deliveries[0].syntheticAmount = '0.01' }
    $precision = Join-Path $fixtures 'money-precision.json'
    [IO.File]::WriteAllText($precision, $baseline.Replace('"syntheticAmount":0.01', '"syntheticAmount":0.010000000000000001'))
    Test-QueueRaw 'money-precision' $precision
    $malformed = Join-Path $fixtures 'malformed.json'
    [IO.File]::WriteAllText($malformed, '{malformed')
    Test-QueueRaw 'malformed-json' $malformed
    $passed = $true
}
finally {
    [IO.File]::WriteAllText($destination, ([ordered]@{ schemaVersion = 1; scope = 'Pure synthetic report-policy fixtures, not a transport/performance/database qualification.'; passed = $passed; total = $checks.Count; checks = $checks } | ConvertTo-Json -Depth 10))
}
Write-Host "Queue report policy: $($checks.Count) checks passed."
