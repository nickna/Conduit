#!/usr/bin/env pwsh
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$collector = Join-Path $repositoryRoot 'scripts/aot/measure-native-container.ps1'
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

function Get-OwnedProcessIdentityToken([Diagnostics.Process] $Process) {
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
        return 'linux:' + $start.ToString([Globalization.CultureInfo]::InvariantCulture)
    }
    return 'windows:' + $Process.StartTime.ToUniversalTime().Ticks.ToString([Globalization.CultureInfo]::InvariantCulture)
}

function Test-OwnedProcessStopped([string] $IdentityPath, [switch] $Stop) {
    if (-not [IO.File]::Exists($IdentityPath)) { return }
    $identity = ([IO.File]::ReadAllText($IdentityPath)).Split('|')
    Assert-Contract ($identity.Count -eq 2) 'Fixture process identity is malformed.'
    $ownedProcess = Get-Process -Id ([int]$identity[0]) -ErrorAction SilentlyContinue
    if ($null -eq $ownedProcess) { return }
    try {
        Assert-Contract ((Get-OwnedProcessIdentityToken $ownedProcess) -ceq $identity[1]) 'Fixture cleanup rejected a reused PID.'
        $statPath = '/proc/' + $ownedProcess.Id + '/stat'
        $zombie = $IsLinux -and [IO.File]::Exists($statPath) -and
            [IO.File]::ReadAllText($statPath) -match '^\d+ \(.+\) Z '
        if ($Stop -and -not $ownedProcess.HasExited -and -not $zombie) {
            $ownedProcess.Kill($true)
            Assert-Contract ($ownedProcess.WaitForExit(5000)) 'Owned fixture process did not terminate.'
        }
        elseif (-not $ownedProcess.HasExited -and -not $zombie) {
            throw 'Collector left a live owned descendant.'
        }
    }
    finally { $ownedProcess.Dispose() }
}

function Stop-OwnedReadinessServer($Record) {
    $failures = [Collections.Generic.List[string]]::new()
    $stage = 'identity-file'
    try {
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
        if (-not $Record.Process.HasExited) {
            Assert-Contract ((Get-OwnedProcessIdentityToken $Record.Process) -ceq $Record.StartToken) 'Original owned readiness handle was rejected.'
            $Record.Process.Kill($true)
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

function New-CaseRoot([string] $Scenario) {
    $caseRoot = Join-Path $temporaryRoot ([Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($caseRoot) | Out-Null
    foreach ($name in @('commands', 'drivers', 'created', 'active', 'processes')) {
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
    foreach ($allowed in @('Private command failed.', 'Private command failed and its cleanup failed.',
        'Private command cleanup failed.', 'Benchmark variant failed.',
        'Benchmark variant failed and owned container cleanup failed.',
        'Owned benchmark container cleanup failed.', 'Container ownership verification failed.')) {
        if ($Failure -ceq ($allowed + ' No qualification was recorded.')) { $safeFailure = $allowed }
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

function Invoke-CollectorCase([string] $Service, [string] $Scenario, [string] $ExpectedFailure) {
    $caseRoot = New-CaseRoot $Scenario
    [IO.File]::WriteAllText((Join-Path $caseRoot 'expected-admin.key'), $privateKey)
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
        JitImage = 'example.invalid/conduit-jit@sha256:' + ('1' * 64)
        NativeImage = 'example.invalid/conduit-native@sha256:' + ('2' * 64)
        DatabaseUrl = 'postgresql://fixture:' + $privateDatabasePassword + '@127.0.0.1:5432/fixture'
        RedisUrl = 'redis://:' + $privateRedisPassword + '@127.0.0.1:6379/0'
        WorkloadConfigurationPaths = $configurationPaths
        WorkloadProbePath = $probePath
        EnvironmentEvidencePath = $environmentPath
        ContainerEnvironmentPath = $caseEnvironmentPath
        PairedRuns = 5
        OutputPath = Join-Path $caseRoot 'report.json'
        Port = (Get-TestPort)
        DockerCommandPath = $dockerCommand
        DotnetCommandPath = $dotnetCommand
        DockerCommandTimeoutSeconds = 5
        WorkloadTimeoutSeconds = 10
        ReadinessTimeoutSeconds = 10
        CleanupTimeoutSeconds = 5
    }
    if ($Scenario -eq 'driver-timeout') { $parameters.WorkloadTimeoutSeconds = 5 }
    if ($Scenario -eq 'even-pairs') { $parameters.PairedRuns = 6 }
    $caseServers = [Collections.Generic.List[object]]::new()
    $casePrimaryFailure = $null
    $caseStage = 'readiness-setup'
    $failureDiagnostic = $null
    try {
    if ($Scenario -cnotin @('even-pairs', 'case-duplicate-environment', 'revision-mismatch')) {
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
            $serverRecord = [pscustomobject]@{ Process = $serverProcess; IdentityPath = $identityPath
                AssociationPath = $associationPath; Id = $serverProcess.Id
                StartToken = Get-OwnedProcessIdentityToken $serverProcess; Reaped = $false; Disposed = $false
                CleanupDiagnostics = @() }
            $caseServers.Add($serverRecord)
            $ownedReadinessServers.Add($serverRecord)
            [IO.File]::WriteAllText($identityPath,
                ([string]$serverRecord.Id + '|' + $serverRecord.StartToken))
            [IO.File]::WriteAllText($associationPath, $identityPath)
        }
    }
    $before = @{}
    foreach ($name in $environmentNames) { $before[$name] = [Environment]::GetEnvironmentVariable($name) }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $failure = $null
    $publicOutput = ''
    $caseStage = 'collector-invocation'
    try { $publicOutput = (& $collector @parameters 6>&1 | Out-String) }
    catch { $failure = $_.Exception.Message }
    $caseStage = 'privacy-assertions'
    foreach ($name in $environmentNames) {
        Assert-Contract ([Environment]::GetEnvironmentVariable($name) -ceq $before[$name]) ('Collector mutated parent environment: ' + $name)
    }
    foreach ($secret in @($privateKey, $privateDatabasePassword, $privateRedisPassword, 'private-child-diagnostic')) {
        Assert-Contract (-not $publicOutput.Contains($secret) -and ($null -eq $failure -or -not $failure.Contains($secret))) 'Collector exposed private command configuration.'
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
            Assert-Contract (-not [IO.File]::Exists($environmentFile) -and
                -not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($environmentFile))) 'Collector retained its private container environment.'
        }
    }
    foreach ($driver in $drivers) {
        Assert-Contract $driver.benchmarkCredentialPresent 'Driver did not receive its private credential.'
        Assert-Contract (-not [IO.File]::Exists($driver.configurationPath)) 'Collector retained a private workload configuration.'
    }
    $caseStage = 'failure-oracle'
    if (-not [string]::IsNullOrEmpty($ExpectedFailure)) {
        Assert-Contract ($null -ne $failure -and $failure.Contains($ExpectedFailure)) ('Expected controlled collector failure was absent: ' + $Scenario)
        Assert-Contract (-not [IO.File]::Exists($parameters.OutputPath)) 'Failed collector recorded a qualification.'
        if ($Scenario -in @('even-pairs', 'case-duplicate-environment')) {
            Assert-Contract ($commands.Count -eq 0 -and $drivers.Count -eq 0) 'Even paired runs reached external commands.'
        }
        elseif ($Scenario -eq 'foreign-label') {
            Assert-Contract (@($commands | Where-Object { $_.arguments[0] -ceq 'rm' }).Count -eq 0) 'Collector removed a container with a foreign label.'
            Assert-Contract ((Read-Records (Join-Path $caseRoot 'active')).Count -eq 1) 'Foreign-label rejection did not leave the fake container intact.'
        }
        elseif ($Scenario -eq 'cleanup-failure') {
            Assert-Contract ($failure.Contains('cleanup failed')) 'Collector lost combined primary and cleanup failure.'
        }
        else {
            Assert-Contract ((Read-Records (Join-Path $caseRoot 'active')).Count -eq 0) 'Failed collector left an owned fake container.'
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
            Assert-Contract ($failureDiagnostic.Classification -ceq 'Private command failed.') 'Timeout fixture failed for a reason other than the bounded command deadline.'
        }
        $script:cases++
        return
    }
    $caseStage = 'positive-oracle'
    Assert-Contract ($null -eq $failure) ('Successful fake collection failed; ' + $failureDiagnostic.Text)
    $reportText = [IO.File]::ReadAllText($parameters.OutputPath)
    foreach ($secret in @($privateKey, $privateDatabasePassword, $privateRedisPassword, 'private-child-diagnostic')) {
        Assert-Contract (-not $reportText.Contains($secret)) 'Collector retained private command configuration in its report.'
    }
    $report = $reportText | ConvertFrom-Json -AsHashtable
    Assert-Contract ($report.schemaVersion -eq 2 -and $report.service -ceq $Service -and $report.pairedRuns -eq 5 -and $report.samples.Count -eq 5) 'Collector report identity or paired sample count is wrong.'
    Assert-Contract ($created.Count -eq 12 -and (Read-Records (Join-Path $caseRoot 'active')).Count -eq 0) 'Collector did not remove all twelve warm and measured containers.'
    $orderedCreated = @($created | Sort-Object ordinal)
    $expectedOrder = @('jit', 'native-aot', 'jit', 'native-aot', 'native-aot', 'jit',
        'jit', 'native-aot', 'native-aot', 'jit', 'jit', 'native-aot')
    Assert-Contract ((@($orderedCreated.runtime) -join '|') -ceq ($expectedOrder -join '|')) 'Collector did not alternate recorded pair order after the warm pair.'
    $expectedDriverCount = if ($Service -eq 'admin') { 12 } else { 24 }
    Assert-Contract ($drivers.Count -eq $expectedDriverCount) 'Collector omitted an authenticated workload from a variant.'
    Assert-Contract (@($drivers | Where-Object dotnetRoute).Count -eq $(if ($Service -eq 'gateway') { 24 } else { 0 })) 'DLL and direct native workload command routing differ.'
    $removals = @($commands | Where-Object { $_.arguments[0] -ceq 'rm' })
    Assert-Contract ($removals.Count -eq 12) 'Collector did not issue one verified removal per container.'
    foreach ($removal in $removals) {
        Assert-Contract ($removal.arguments.Count -eq 3 -and $removal.arguments[1] -ceq '--force' -and
            $removal.arguments[2] -cmatch '^[0-9a-f]{64}$' -and
            @($created | Where-Object id -CEQ $removal.arguments[2]).Count -eq 1) 'Collector cleanup used an unverified container identity.'
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
    }
    Assert-Contract ($report.jit.imageBytes -eq 1000 -and $report.native.imageBytes -eq 800 -and
        $report.jit.pullBytes -eq 700 -and $report.native.pullBytes -eq 500) 'Collector mixed image and compressed pull sizes.'
    $script:cases++
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
        if ($caseCleanupFailures.Count -gt 0) {
            $primaryType = if ($null -ne $casePrimaryFailure) { $casePrimaryFailure.GetType().FullName } else { 'none' }
            $primaryClassification = if ($null -ne $failureDiagnostic) { $failureDiagnostic.Classification } else { 'unclassified' }
            throw ('Collector case readiness cleanup failed; primary stage ' + $caseStage +
                ', type ' + $primaryType + ', classification ' + $primaryClassification +
                '; cleanup stages ' + ($caseCleanupFailures -join '|') + '.')
        }
    }
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    [Environment]::SetEnvironmentVariable('CONDUIT_NATIVE_BENCHMARK_API_KEY', $privateKey)
    [Environment]::SetEnvironmentVariable('CONDUIT_COLLECTOR_TEST_PWSH', (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source)
    [Environment]::SetEnvironmentVariable('CONDUIT_CORE_RATE_LIMIT_MAX_REQUESTS', '1000')
    [Environment]::SetEnvironmentVariable('CONDUIT_CORE_RATE_LIMIT_WINDOW_SECONDS', '60')
    $processIdentityHelper = Join-Path $temporaryRoot 'process-identity.ps1'
    [IO.File]::WriteAllText($processIdentityHelper,
        ('function Get-OwnedProcessIdentityToken {' + [Environment]::NewLine +
            (Get-Item Function:\Get-OwnedProcessIdentityToken).ScriptBlock.ToString() + [Environment]::NewLine + '}'))
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
            if ((Get-OwnedProcessIdentityToken $owned) -cne $identity[1]) { throw 'Reused owned fixture PID.' }
            if (-not $owned.HasExited) { $owned.Kill($true); if (-not $owned.WaitForExit(5000)) { throw 'Fixture reap failed.' } }
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
                $response = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK" + [char]13 + [char]10 +
                    "Content-Type: application/json" + [char]13 + [char]10 + "Content-Length: 2" + [char]13 +
                    [char]10 + "Connection: close" + [char]13 + [char]10 + [char]13 + [char]10 + "{}")
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
    if ($CommandArguments[0] -eq 'image') {
        $native = $CommandArguments[-1].Contains('native')
        $revision = if ($scenario -eq 'revision-mismatch' -and $native) { 'b' * 40 } else { 'a' * 40 }
        @{ Os = 'linux'; Architecture = 'amd64'; Size = $(if ($native) { 800 } else { 1000 });
            Config = @{ Labels = @{ 'org.opencontainers.image.revision' = $revision } } } | ConvertTo-Json -Depth 5 -Compress
        exit 0
    }
    if ($CommandArguments[0] -eq 'manifest') {
        $native = $CommandArguments[-1].Contains('native')
        @{ schemaVersion = 2; layers = @(@{ size = $(if ($native) { 500 } else { 700 }) }) } | ConvertTo-Json -Depth 5 -Compress
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
        for ($index = 1; $index -lt $CommandArguments.Count - 1; $index++) {
            if ($CommandArguments[$index] -eq '--name') { $name = $CommandArguments[++$index] }
            elseif ($CommandArguments[$index] -eq '--label') {
                $label = $CommandArguments[++$index].Split('=', 2); $labels[$label[0]] = $label[1]
            }
            elseif ($CommandArguments[$index] -eq '--publish') { $port = [int]$CommandArguments[++$index].Split(':')[1] }
            elseif ($CommandArguments[$index] -eq '--env-file') { $privateEnvironment = $CommandArguments[++$index] }
        }
        Write-RunTrace 'arguments-parsed'
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
            canonicalEnvironment = $true; backendCredentialCorrect = $true }
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
        if ($matches.Count -eq 0) { [Console]::Error.WriteLine('Error: No such object: ' + $identity); exit 1 }
        if ($matches.Count -ne 1) { throw 'Ambiguous fake identity.' }
        $record = $matches[0]
        $labels = $record.labels
        if ($scenario -eq 'foreign-label' -and @(Get-ChildItem -LiteralPath (Join-Path $root 'drivers') -File).Count -gt 0) {
            $labels['conduit.native-benchmark.owner'] = 'foreign-owner'
        }
        @{ Id = $record.id; Name = '/' + $record.name; Config = @{ Labels = $labels }; State = @{ Running = $true } } |
            ConvertTo-Json -Depth 8 -Compress
        exit 0
    }
    if ($CommandArguments[0] -eq 'stats') {
        $record = @(Read-Active | Where-Object id -CEQ $CommandArguments[1])[0]
        $memory = if ($record.ordinal -le 2) { '99 GiB' } elseif ($record.runtime -ceq 'jit') { '1 MiB' } else { '512 KiB' }
        [Console]::Out.WriteLine($memory + ' / 128 GiB')
        exit 0
    }
    if ($CommandArguments[0] -eq 'rm') {
        if ($scenario -eq 'cleanup-failure') { [Console]::Error.WriteLine('private-child-diagnostic'); exit 18 }
        $matches = @(Read-Active | Where-Object id -CEQ $CommandArguments[-1])
        if ($matches.Count -ne 1) { throw 'Cleanup did not use one exact fake ID.' }
        # Containers are synthetic records. The parent owns both reusable
        # readiness listeners and joins them after this complete case.
        Remove-Item -LiteralPath (Join-Path $root ('active/' + $matches[0].id + '.json')) -Force
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
    $childSchemaVersion = if ($scenario -eq 'child-string-schema') { '2' } else { 2 }
    @{ schemaVersion = $childSchemaVersion; service = $(if ($admin) { 'admin' } else { 'gateway' }); workloads = @($row) } |
        ConvertTo-Json -Depth 8 -Compress
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
    foreach ($functionName in @('Invoke-BoundedProcess', 'ConvertTo-ContainerUrl', 'Get-ResultNumber', 'Test-WorkloadResult')) {
        $definition = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $functionName
        }.GetNewClosure(), $true)
        Assert-Contract ($null -ne $definition) ('Collector helper is missing: ' + $functionName)
        Invoke-Expression $definition.Extent.Text
    }
    # These are public fixture credentials, not a connection to any service.
    # Assert URI components independently so host translation cannot silently
    # remove an empty Redis username's password or re-escape existing userinfo.
    $uriCases = @(
        @{ input = 'redis://:fixture-pass@127.0.0.1:6379/2'; schemes = @('redis', 'rediss');
            scheme = 'redis'; host = 'host.docker.internal'; port = 6379; path = '/2'; userInfo = ':fixture-pass' },
        @{ input = 'rediss://fixture:pass%40word%3Avalue@localhost:6380/3'; schemes = @('redis', 'rediss');
            scheme = 'rediss'; host = 'host.docker.internal'; port = 6380; path = '/3'; userInfo = 'fixture:pass%40word%3Avalue' },
        @{ input = 'postgresql://fixture:fixture-pass@[::1]:5434/owned'; schemes = @('postgres', 'postgresql');
            scheme = 'postgresql'; host = 'host.docker.internal'; port = 5434; path = '/owned'; userInfo = 'fixture:fixture-pass' },
        @{ input = 'postgres://fixture:pass%40word@localhost:5433/owned'; schemes = @('postgres', 'postgresql');
            scheme = 'postgres'; host = 'host.docker.internal'; port = 5433; path = '/owned'; userInfo = 'fixture:pass%40word' },
        @{ input = 'redis://:fixture-pass@redis.example.invalid:6381/4'; schemes = @('redis', 'rediss');
            scheme = 'redis'; host = 'redis.example.invalid'; port = 6381; path = '/4'; userInfo = ':fixture-pass' }
    )
    foreach ($uriCase in $uriCases) {
        $translatedText = ConvertTo-ContainerUrl $uriCase.input $uriCase.schemes
        $translated = $null
        Assert-Contract ([Uri]::TryCreate($translatedText, [UriKind]::Absolute, [ref]$translated)) 'Translated backing-service URI is malformed.'
        Assert-Contract ($translated.Scheme -ceq $uriCase.scheme -and $translated.Host -ceq $uriCase.host -and
            $translated.Port -eq $uriCase.port -and $translated.AbsolutePath -ceq $uriCase.path -and
            $translated.UserInfo -ceq $uriCase.userInfo -and $translated.Query.Length -eq 0 -and
            $translated.Fragment.Length -eq 0) 'Backing-service host translation changed URI credentials or other components.'
        $cases++
    }
    foreach ($invalidUri in @('http://127.0.0.1:6379/0', ('redis://127.0.0.1:6379/0' + [char]10))) {
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
    $result = Invoke-BoundedProcess $helperChild @($argumentValue) 5 $helperEnvironment
    Assert-Contract ($result.ExitCode -eq 0) ('Private helper exit code was ' + $result.ExitCode + '.')
    $stdoutLines = @($result.Stdout -split '\r?\n')
    Assert-Contract ($stdoutLines.Count -ge 3) ('Private helper returned ' + $stdoutLines.Count + ' stdout lines.')
    Assert-Contract ($stdoutLines[0] -ceq $argumentValue) ('Private helper literal argument differs; actual UTF16 length ' + $stdoutLines[0].Length + ', expected ' + $argumentValue.Length + '.')
    Assert-Contract ($stdoutLines[1] -ceq 'child-only') 'Private helper child environment value differs.'
    Assert-Contract ($stdoutLines[2] -ceq 'True') 'Private helper inherited the benchmark credential.'
    Assert-Contract ($result.Stderr.Trim() -ceq 'private-child-diagnostic') 'Private helper stderr capture differs.'
    Assert-Contract ([Environment]::GetEnvironmentVariable('CONDUIT_COLLECTOR_HELPER_VALUE') -ceq $oldEnvironment.CONDUIT_COLLECTOR_HELPER_VALUE) 'Private helper changed parent environment.'
    $cases++
    $helperEnvironment.CONDUIT_COLLECTOR_HELPER_VALUE = 'nonzero'
    $result = Invoke-BoundedProcess $helperChild @($argumentValue) 5 $helperEnvironment
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
    Assert-Contract ($failure -ceq 'Private command failed.' -and $watch.Elapsed.TotalSeconds -lt 10 -and
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

    Invoke-CollectorCase 'admin' 'success' $null
    Invoke-CollectorCase 'gateway' 'success' $null
    Invoke-CollectorCase 'admin' 'even-pairs' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'case-duplicate-environment' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'revision-mismatch' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'driver-nonzero' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'driver-timeout' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'unknown-create' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'foreign-label' 'cleanup failed'
    Invoke-CollectorCase 'admin' 'cleanup-failure' 'cleanup failed'
    Invoke-CollectorCase 'admin' 'incomplete-report' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'child-string-schema' 'No qualification was recorded.'
    Invoke-CollectorCase 'admin' 'fingerprint-mismatch' 'No qualification was recorded.'
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
Write-Host ('PASS: ' + $cases + ' collector contracts; owned mocked Docker/driver processes, authenticated workload pairs, warm exclusion, private deadlines and strict cleanup.')
