#!/usr/bin/env pwsh
[CmdletBinding()]
param()
# Exercise the lifecycle harness's actual ownership, polling and diagnostic helpers.
# Only identity IO and scheduling are in-memory seams; no process is launched or signaled.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$harness = Join-Path $PSScriptRoot 'test-native-process-cleanup-lifecycle.ps1'
$source = [IO.File]::ReadAllText($harness, [Text.UTF8Encoding]::new($false, $true))
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
Assert (@($errors).Count -eq 0) 'Lifecycle harness must parse before helper extraction.'
$snapshotAssertionLine = $null
foreach ($name in @('Require', 'New-FixtureFailure', 'Observe', 'Wait-Reparenting', 'Invoke-Issue1674SnapshotLatch')) {
    $definitions = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $true))
    Assert ($definitions.Count -eq 1) ('Exactly one lifecycle helper is required: ' + $name)
    if ($name -ceq 'Invoke-Issue1674SnapshotLatch') {
        $assertions = @($definitions[0].Body.FindAll({ param($node)
            $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Require'
        }, $true))
        Assert ($assertions.Count -gt 0) 'Snapshot latch must retain its controlled timeline assertions.'
        $snapshotAssertionLine = $assertions[0].Extent.StartLineNumber - $definitions[0].Extent.StartLineNumber + 1
    }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
function Reset-Fixture {
    $script:fixture = @{
        observations = [Collections.Generic.Queue[object]]::new()
        reads = 0; pauses = 0; onPause = $null
        expected = [ordered]@{ id = 30; parentId = 20; startToken = '300' }
        evidence = [ordered]@{ attempts = 0; lastObservation = $null; established = $false }
    }
}
function New-Identity([int]$ParentId = 20, [string]$State = 'S', [string]$StartToken = '300') {
    return [ordered]@{
        id = 30; parentId = $ParentId; startToken = $StartToken; state = $State
        stdout = 'pipe:[101]'; stderr = 'pipe:[102]'
    }
}
function Read-Identity([int]$Id) {
    Assert ($Id -eq $fixture.expected.id) 'Every observation must read the latched PID.'
    $fixture.reads++
    Assert ($fixture.observations.Count -gt 0) 'Polling performed an unexpected extra identity read.'
    $value = $fixture.observations.Dequeue()
    if ($value -is [scriptblock]) { $value = & $value }
    if ($value -is [Exception]) { throw $value }
    return $value
}
function Start-Sleep([int]$Milliseconds) {
    Assert ($Milliseconds -eq 10) 'Polling must use the harness scheduling interval.'
    $fixture.pauses++
    if ($null -ne $fixture.onPause) { & $fixture.onPause }
}
function Run-Poll([Threading.CancellationToken]$Token = [Threading.CancellationToken]::None) {
    Wait-Reparenting $fixture.expected 20 10 $Token $fixture.evidence | Out-Null
}
function Expect-Rejection([scriptblock]$Action) {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_ }
    Assert ($null -ne $failure) 'Expected this lifecycle transition to be rejected.'
    return $failure
}
$rows = [Collections.Generic.List[object]]::new()
function Case([string]$Name, [scriptblock]$Action) {
    Reset-Fixture
    try { & $Action | Out-Null; $rows.Add([ordered]@{ name = $Name; passed = $true; message = $null }) }
    catch { $rows.Add([ordered]@{ name = $Name; passed = $false; message = $_.Exception.Message }) }
}
Case 'already reparented live identity establishes the timeline without a delay' {
    $fixture.observations.Enqueue((New-Identity 1))
    Run-Poll
    Assert ($fixture.evidence.established -and $fixture.evidence.attempts -eq 1 -and $fixture.reads -eq 1 -and $fixture.pauses -eq 0) 'An established identity should be accepted on its first observation.'
    Assert ($fixture.evidence.lastObservation.identity.parentId -eq 1 -and $fixture.evidence.lastObservation.startToken -ceq '300') 'Evidence must preserve the accepted matching identity.'
}
Case 'child completion does not bypass delayed grandchild reparenting' {
    $fixture.observations.Enqueue((New-Identity 20)); $fixture.observations.Enqueue((New-Identity 20)); $fixture.observations.Enqueue((New-Identity 1))
    Run-Poll
    Assert ($fixture.evidence.established -and $fixture.evidence.attempts -eq 3 -and $fixture.reads -eq 3 -and $fixture.pauses -eq 2) 'Retained child ancestry must be polled until the grandchild reparents.'
}
Case 'both child and root ancestry remain inside the captured tree' {
    $fixture.observations.Enqueue((New-Identity 10)); $fixture.observations.Enqueue((New-Identity 20)); $fixture.observations.Enqueue((New-Identity 1))
    Run-Poll
    Assert ($fixture.evidence.established -and $fixture.pauses -eq 2 -and $fixture.evidence.lastObservation.identity.parentId -eq 1) 'Root ancestry must not be accepted as an external reparenting.'
}
Case 'matching zombie cannot establish the required live grandchild timeline' {
    $fixture.observations.Enqueue((New-Identity 1 'Z'))
    $null = Expect-Rejection { Run-Poll }
    Assert (-not $fixture.evidence.established -and $fixture.pauses -eq 0 -and $fixture.evidence.lastObservation.completion -ceq 'matching-zombie') 'Zombie evidence must fail immediately without a retry.'
}
Case 'typed absence cannot establish the required live grandchild timeline' {
    $fixture.observations.Enqueue($null)
    $null = Expect-Rejection { Run-Poll }
    Assert (-not $fixture.evidence.established -and $fixture.pauses -eq 0 -and $fixture.evidence.lastObservation.completion -ceq 'typed-absence') 'An absent grandchild must fail immediately without a retry.'
}
Case 'reused live PID is rejected by the actual ownership check' {
    $fixture.observations.Enqueue((New-Identity 1 'S' '301'))
    $failure = Expect-Rejection { Run-Poll }
    Assert (-not $fixture.evidence.established -and $fixture.reads -eq 1 -and $fixture.pauses -eq 0) 'A reused start token must not become successful reparenting evidence.'
    $diagnostic = New-FixtureFailure 'fixture-test' $failure
    Assert ($diagnostic.assertion -ceq 'Observed identity was reused; refusing ownership.') 'The actual identity assertion must remain attributable.'
}
Case 'reused zombie PID cannot bypass ownership validation' {
    $fixture.observations.Enqueue((New-Identity 1 'Z' '301'))
    $null = Expect-Rejection { Run-Poll }
    Assert (-not $fixture.evidence.established -and $fixture.pauses -eq 0) 'A zombie with another start token must fail ownership validation.'
}
Case 'identity permission failures propagate without retries' {
    $fixture.observations.Enqueue([UnauthorizedAccessException]::new('Synthetic private metadata text.'))
    $failure = Expect-Rejection { Run-Poll }
    Assert ($failure.Exception -is [UnauthorizedAccessException] -and -not $fixture.evidence.established -and $fixture.pauses -eq 0) 'Permission failure must not be converted into a retry or completion.'
}
Case 'unexpected metadata failures propagate without retries' {
    $fixture.observations.Enqueue([IO.InvalidDataException]::new('Synthetic malformed identity.'))
    $failure = Expect-Rejection { Run-Poll }
    Assert ($failure.Exception -is [IO.InvalidDataException] -and -not $fixture.evidence.established -and $fixture.pauses -eq 0) 'Malformed metadata must not be converted into a retry or completion.'
}
Case 'an already canceled shared budget prevents any observation' {
    $budget = [Threading.CancellationTokenSource]::new()
    try {
        $budget.Cancel()
        $null = Expect-Rejection { Run-Poll $budget.Token }
        Assert ($fixture.reads -eq 0 -and $fixture.pauses -eq 0 -and -not $fixture.evidence.established) 'An expired cleanup token must prevent further work.'
    } finally { $budget.Dispose() }
}
Case 'cancellation between polls prevents accepting the next external parent' {
    $script:budget = [Threading.CancellationTokenSource]::new()
    try {
        $fixture.observations.Enqueue((New-Identity 20)); $fixture.observations.Enqueue((New-Identity 1))
        $fixture.onPause = { $script:budget.Cancel() }
        $null = Expect-Rejection { Run-Poll $script:budget.Token }
        Assert ($fixture.reads -eq 1 -and $fixture.pauses -eq 1 -and -not $fixture.evidence.established) 'Polling must retain the existing shared cleanup deadline.'
        Assert ($fixture.evidence.lastObservation.identity.parentId -eq 20) 'Timeout evidence must retain the last observed ancestry.'
    } finally { $script:budget.Dispose() }
}
Case 'cancellation during a successful identity read prevents establishing the timeline' {
    $script:budget = [Threading.CancellationTokenSource]::new()
    try {
        $fixture.observations.Enqueue({ $script:budget.Cancel(); New-Identity 1 })
        $null = Expect-Rejection { Run-Poll $script:budget.Token }
        Assert ($fixture.reads -eq 1 -and $fixture.pauses -eq 0 -and -not $fixture.evidence.established) 'A matching observation after token cancellation must not establish the timeline.'
    } finally { $script:budget.Dispose() }
}
Case 'fixture assertions retain their actual caller line' {
    $failure = $null
    try {
        $assertionLine = (Get-PSCallStack)[0].ScriptLineNumber + 1
        Require $false 'Synthetic safe lifecycle assertion.'
    } catch { $failure = $_ }
    Assert ($null -ne $failure) 'Synthetic assertion must fail.'
    $diagnostic = New-FixtureFailure 'fixture-test' $failure
    Assert ($diagnostic.reason -ceq 'fixture-test' -and $diagnostic.assertion -ceq 'Synthetic safe lifecycle assertion.' -and $diagnostic.assertionLine -eq $assertionLine) 'Failure evidence must identify the assertion and its caller line.'
}
Case 'snapshot callback retains its specific assertion when the helper swallows its failure' {
    $script:scenario = [ordered]@{
        name = 'late-fork-after-snapshot'; armed = $false; snapshotSeen = $false; timelineFailure = $null
    }
    $callbackFailed = $false
    try { Invoke-Issue1674SnapshotLatch $null ([Collections.Generic.List[object]]::new()) ([Threading.CancellationToken]::None) }
    catch { $callbackFailed = $true } # The real helper consumes this callback exception during cleanup.
    Assert ($callbackFailed) 'The unexpected snapshot seam must still throw to preserve helper fail-closed behavior.'
    $diagnostic = $script:scenario.timelineFailure
    Assert ($null -ne $diagnostic -and $diagnostic.reason -ceq 'snapshot-latch-failed' -and $diagnostic.assertion -ceq 'Unexpected or duplicate snapshot seam.') 'The callback must preserve its cause before helper classification replaces the exception.'
    Assert ($diagnostic.assertionLine -eq $snapshotAssertionLine) 'Callback evidence must retain the exact assertion caller line in the extracted latch.'
}
Case 'ordinary exception messages cannot reach public failure evidence' {
    $sentinel = 'synthetic-private-sentinel-' + [Guid]::NewGuid().ToString('N')
    $failure = Expect-Rejection { throw [InvalidOperationException]::new($sentinel) }
    $diagnostic = New-FixtureFailure 'fixture-test' $failure
    Assert ($null -eq $diagnostic.assertion -and $null -eq $diagnostic.assertionLine) 'Ordinary exceptions must not fabricate fixture assertions.'
    Assert (-not ($diagnostic | ConvertTo-Json -Depth 8).Contains($sentinel)) 'Private exception text must remain absent from public failure evidence.'
}
$passed = @($rows | Where-Object passed -eq $true).Count
if ($rows.Count -ne 15 -or $passed -ne $rows.Count) {
    foreach ($row in $rows) { if (-not $row.passed) { Write-Error ($row.name + ': ' + $row.message) -ErrorAction Continue } }
    throw ('Lifecycle transition contracts failed: ' + $passed + '/' + $rows.Count + '.')
}
Write-Host ('PASS: ' + $passed + ' lifecycle transition contracts; source-derived ownership, bounded reparenting and sanitized assertion evidence.')
