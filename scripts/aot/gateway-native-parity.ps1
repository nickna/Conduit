#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the Phase 5 Gateway protocol/infrastructure matrix against native processes.

.DESCRIPTION
    Starts a native Admin and two native Gateway executables against real PostgreSQL,
    Redis, and S3-compatible storage, then runs the native supported-boundary probe.
    Use a disposable parity database: fixture seeding changes application data and
    the readiness proof temporarily adds its own migration-history row.
#>
[CmdletBinding()]
param(
    [string]$ArtifactDirectory,
    [string]$DatabaseUrl = $env:DATABASE_URL,
    [string]$RedisUrl = $env:REDIS_URL,
    [string]$S3Endpoint = $env:CONDUIT_S3_ENDPOINT,
    [string]$S3HealthPath = '/minio/health/live',
    [string]$S3AccessKeyId = $env:CONDUIT_S3_ACCESS_KEY_ID,
    [string]$S3SecretAccessKey = $env:CONDUIT_S3_SECRET_ACCESS_KEY,
    [string]$S3BucketName = $env:CONDUIT_S3_BUCKET_NAME,
    [string]$S3Region = 'us-east-1',
    [string]$MasterKey = 'native-aot-parity-master-key-32-bytes',
    [int]$GatewayPort = 15100,
    [int]$SecondaryGatewayPort = 15101,
    [int]$AdminPort = 15102,
    [int]$ProviderPort = 15103,
    [int]$DependencyControlPort = 15104,
    [int]$PostgresProxyPort = 15105,
    [int]$RedisProxyPort = 15106,
    [int]$TimeoutSeconds = 240,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '../..')
if (-not $ArtifactDirectory) { $ArtifactDirectory = Join-Path $repoRoot 'artifacts/native-aot' }
$artifactRoot = [IO.Path]::GetFullPath($ArtifactDirectory)
$runtimeRoot = Join-Path $artifactRoot 'runtime'
$migratorProject = Join-Path $repoRoot 'tools/ConduitLLM.Migrator'
$probeProject = Join-Path $repoRoot 'Tests/ConduitLLM.GatewayNativeAotTests'
$publisherProject = Join-Path $repoRoot 'tools/WolverineSmokePublisher'
$startupFixtureProject = Join-Path $repoRoot 'Tests/ConduitLLM.SchemaStartupFixtures'
$startupFixtureDll = Join-Path $startupFixtureProject 'bin/Release/net10.0/ConduitLLM.SchemaStartupFixtures.dll'
$publisherDll = Join-Path $publisherProject 'bin/Release/net10.0/WolverineSmokePublisher.dll'
$logRoot = Join-Path $artifactRoot "reports/gateway-parity-$PID"
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null

if (-not $DatabaseUrl) { throw 'DatabaseUrl not set.' }
if (-not $RedisUrl) { throw 'RedisUrl not set.' }
if (-not $S3Endpoint) { throw 'S3Endpoint not set.' }
if (-not $S3AccessKeyId) { throw 'S3AccessKeyId not set.' }
if (-not $S3SecretAccessKey) { throw 'S3SecretAccessKey not set.' }
if (-not $S3BucketName) { throw 'S3BucketName not set.' }
if (-not $IsLinux) { throw 'The native BillingAudit process lifecycle proof requires Linux SIGTERM.' }

function Get-NativeExecutable([string]$service) {
    $name = "ConduitLLM.$service"
    if ($IsWindows) { $name += '.exe' }
    $path = Join-Path $runtimeRoot $service.ToLowerInvariant() $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Native $service executable not found: $path"
    }
    return $path
}

function Start-LoggedProcess(
    [string]$name,
    [string]$filePath,
    [string[]]$argumentList,
    [string]$workingDirectory,
    [string]$urls,
    [hashtable]$childEnvironment = @{}
) {
    if ($urls) { Set-GateEnvironment 'ASPNETCORE_URLS' $urls }
    $arguments = @{
        FilePath = $filePath
        ArgumentList = $argumentList
        WorkingDirectory = $workingDirectory
        RedirectStandardOutput = (Join-Path $logRoot "$name.stdout.log")
        RedirectStandardError = (Join-Path $logRoot "$name.stderr.log")
        PassThru = $true
    }
    if ($IsWindows) { $arguments.WindowStyle = 'Hidden' } else { $arguments.NoNewWindow = $true }
    if ($childEnvironment.Count) { $arguments.Environment = $childEnvironment }
    $process = Start-Process @arguments
    Write-Host "Started $name (pid $($process.Id))"
    return $process
}

$originalEnvironment = @{}
function Set-GateEnvironment([string]$name, $value) {
    if (-not $originalEnvironment.ContainsKey($name)) {
        $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    # PowerShell converts $null to an empty string for a .NET string parameter.
    # Preserve absence explicitly; an empty REDIS_URL masks the fallback route.
    $environmentValue = if ($null -eq $value) { [NullString]::Value } else { [string]$value }
    [Environment]::SetEnvironmentVariable($name, $environmentValue)
}

function Invoke-PrivateFixtureProcess(
    [string]$filePath, [string[]]$arguments, [hashtable]$childEnvironment, [string]$operation,
    [ValidateRange(100, 30000)][int]$timeoutMilliseconds = 30000
) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new($filePath)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $arguments) { $startInfo.ArgumentList.Add($argument) }
    foreach ($name in $childEnvironment.Keys) { $startInfo.Environment[$name] = $childEnvironment[$name] }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    $cleanup = $null
    try {
        $started = $process.Start()
        if (-not $started) { throw "$operation could not start." }
        # Both streams stay private; never include their contents in gate diagnostics.
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $streams = [Threading.Tasks.Task[]]@($stdout, $stderr)
        if (-not $process.WaitForExit($timeoutMilliseconds)) {
            $cleanup = [Diagnostics.Stopwatch]::StartNew()
            try { $process.Kill($true) } catch {
                if (-not $process.HasExited) { throw "$operation termination failed." }
            }
            $remaining = [Math]::Max(0, 5000 - [int]$cleanup.ElapsedMilliseconds)
            if (-not $process.WaitForExit($remaining)) { throw "$operation could not be reaped." }
            $remaining = [Math]::Max(0, 5000 - [int]$cleanup.ElapsedMilliseconds)
            if (-not [Threading.Tasks.Task]::WaitAll($streams, $remaining)) {
                throw "$operation output did not close."
            }
            throw "$operation timed out."
        }
        if (-not [Threading.Tasks.Task]::WaitAll($streams, 5000)) {
            throw "$operation output did not close."
        }
        if ($process.ExitCode -ne 0) { throw "$operation failed." }
        return $stdout.GetAwaiter().GetResult()
    } finally {
        try {
            if ($started -and -not $process.HasExited) {
                if ($null -eq $cleanup) { $cleanup = [Diagnostics.Stopwatch]::StartNew() }
                $process.Kill($true)
                $remaining = [Math]::Max(0, 5000 - [int]$cleanup.ElapsedMilliseconds)
                if (-not $process.WaitForExit($remaining)) { throw "$operation could not be reaped." }
            }
        } finally {
            $process.Dispose()
        }
    }
}

function Invoke-FunctionCredentialFixture([string]$fixtureDll, [string]$redisConnectionString, [string]$plaintext) {
        $output = Invoke-PrivateFixtureProcess (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source @($fixtureDll, 'protect-function-key') @{
            CONDUIT_NATIVE_FUNCTION_PLAINTEXT_KEY = $plaintext
            CONDUIT_NATIVE_FUNCTION_REDIS_CONNECTION_STRING = $redisConnectionString
        } 'Function credential fixture'
        if ($output.Length -gt 2002 -or $output -cnotmatch '\Aenc:v1:[A-Za-z0-9_-]+(?:\r?\n)?\z') {
            throw 'Function credential fixture returned invalid ciphertext.'
        }
        $ciphertext = $output.TrimEnd("`r", "`n")
        if ($ciphertext.Length -gt 2000 -or $ciphertext -ceq $plaintext) {
            throw 'Function credential fixture did not protect its input.'
        }
        return $ciphertext
}

function Stop-OwnedParityProcesses([Collections.Generic.List[Diagnostics.Process]]$ownedProcesses) {
    $failures = [Collections.Generic.List[Exception]]::new()
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    foreach ($process in $ownedProcesses) {
        try {
            if (-not $process.HasExited) { $process.Kill($true) }
            $remaining = [Math]::Max(0, 30000 - [int]$deadline.ElapsedMilliseconds)
            if (-not $process.WaitForExit($remaining)) {
                throw "Owned parity process $($process.Id) could not be reaped."
            }
        } catch {
            $failures.Add($_.Exception)
        } finally {
            $process.Dispose()
        }
    }
    if ($failures.Count) { throw [AggregateException]::new('Owned parity hosts could not all be stopped.', $failures) }
}

function Get-OwnedLinuxStartToken([Diagnostics.Process]$process) {
    if (-not $IsLinux -or $process.HasExited) { throw 'An owned live Linux host is required.' }
    $stat = [IO.File]::ReadAllText("/proc/$($process.Id)/stat")
    $boundary = $stat.LastIndexOf(') ', [StringComparison]::Ordinal)
    if ($boundary -lt 0) { throw 'Owned host identity is unavailable.' }
    $fields = $stat.Substring($boundary + 2).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)
    [ulong]$start = 0
    if ($fields.Length -le 19 -or -not [ulong]::TryParse($fields[19], [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$start) -or $start -eq 0) {
        throw 'Owned host identity is invalid.'
    }
    return "linux:$start"
}

function Send-OwnedLinuxSigTerm([Diagnostics.Process]$process, [string]$identity) {
    if ((Get-OwnedLinuxStartToken $process) -cne $identity) { throw 'Owned host identity changed before graceful shutdown.' }
    $startInfo = [Diagnostics.ProcessStartInfo]::new('/bin/kill')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add('-TERM')
    $startInfo.ArgumentList.Add('--')
    $startInfo.ArgumentList.Add([string]$process.Id)
    $signal = [Diagnostics.Process]::new()
    $signal.StartInfo = $startInfo
    $started = $false
    try {
        $started = $signal.Start()
        if (-not $started) { throw 'Owned host graceful signal could not start.' }
        # Retain the original handle through the outer cleanup even if a signal
        # helper cannot be reaped within its local bound.
        $processes.Add($signal)
        $stdout = $signal.StandardOutput.ReadToEndAsync()
        $stderr = $signal.StandardError.ReadToEndAsync()
        if (-not $signal.WaitForExit(5000)) {
            $signal.Kill($true)
            if (-not $signal.WaitForExit(5000)) { throw 'Owned host graceful signal could not be reaped.' }
            throw 'Owned host graceful signal timed out.'
        }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000) -or $signal.ExitCode -ne 0) {
            throw 'Owned host graceful signal failed.'
        }
    } finally { if (-not $started) { $signal.Dispose() } }
}

function Read-LogTail([string]$path, [long]$offset) {
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        if ($stream.Length -lt $offset) { throw 'Owned host lifecycle log was truncated.' }
        [void]$stream.Seek($offset, [IO.SeekOrigin]::Begin)
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true, 4096, $true)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}

function Invoke-BillingAuditLifecycle([string]$argument, [string]$expected, [string]$hostName = '', [switch]$timerWitness) {
    $arguments = @($argument)
    if ($hostName) { $arguments += $hostName }
    if (Test-Path -LiteralPath $nativeProbe -PathType Leaf) {
        $output = Invoke-PrivateFixtureProcess $nativeProbe $arguments @{} 'Native BillingAudit lifecycle'
    } else {
        $output = Invoke-PrivateFixtureProcess (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source (@($probeDll) + $arguments) @{} 'Native BillingAudit lifecycle'
    }
    if ($timerWitness) {
        $witness = [regex]::Match($output, '\A' + [regex]::Escape($expected) + ' ([1-9][0-9]*) ([1-9][0-9]*)(?:\r?\n)?\z')
        [long]$timestamp = 0
        [long]$frequency = 0
        if (-not $witness.Success -or -not [long]::TryParse($witness.Groups[1].Value, [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$timestamp) -or
            -not [long]::TryParse($witness.Groups[2].Value, [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$frequency) -or
            $frequency -ne [Diagnostics.Stopwatch]::Frequency) {
            throw 'Native BillingAudit timer clock witness is invalid.'
        }
        Write-Host "  PASS  $expected"
        return $timestamp
    }
    if ($output -cnotmatch ('\A' + [regex]::Escape($expected) + '(?:\r?\n)?\z')) {
        throw 'Native BillingAudit lifecycle returned an invalid result.'
    }
    Write-Host "  PASS  $expected"
}

function Assert-LifecycleTimerWindow([long]$timestamp) {
    $elapsed = [Diagnostics.Stopwatch]::GetElapsedTime($timestamp).TotalMilliseconds
    if ($timestamp -le 0 -or $elapsed -lt 0 -or $elapsed -ge 3000) {
        throw 'Owned native Gateway missed the fresh timer shutdown window.'
    }
}

function Wait-ForHttp([string]$url, [string]$name) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -eq 200) {
                Write-Host "  PASS  $name"
                return
            }
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    throw "Timed out waiting for $name at $url"
}

if (-not $NoBuild) {
    dotnet build $migratorProject -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Migrator build failed.' }
    dotnet build $probeProject -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Native parity probe build failed.' }
    dotnet build $publisherProject -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Wolverine media publisher build failed.' }
    dotnet build $startupFixtureProject -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Schema startup fixture build failed.' }
}
if (-not (Test-Path -LiteralPath $publisherDll -PathType Leaf)) {
    throw "Wolverine media publisher not found: $publisherDll"
}
if (-not (Test-Path -LiteralPath $startupFixtureDll -PathType Leaf)) { throw "Schema startup fixture tool not found: $startupFixtureDll" }

$processes = [Collections.Generic.List[Diagnostics.Process]]::new()
$functionSeedAttempted = $false
$mcpSeedAttempted = $false
$adminSettingsAttempted = $false
$adminIpAttempted = $false
$billingAuditAttempted = $false
$parityFailure = $null
try {
    Set-GateEnvironment 'DATABASE_URL' $DatabaseUrl
    Set-GateEnvironment 'CONDUIT_NATIVE_MESSAGE_PUBLISHER_PATH' $publisherDll
    Set-GateEnvironment 'REDIS_URL' $RedisUrl
    Set-GateEnvironment 'CONDUIT_MEDIA_STORAGE_TYPE' 'S3'
    Set-GateEnvironment 'CONDUIT_S3_ENDPOINT' $S3Endpoint
    Set-GateEnvironment 'CONDUIT_S3_ACCESS_KEY_ID' $S3AccessKeyId
    Set-GateEnvironment 'CONDUIT_S3_SECRET_ACCESS_KEY' $S3SecretAccessKey
    Set-GateEnvironment 'CONDUIT_S3_BUCKET_NAME' $S3BucketName
    Set-GateEnvironment 'CONDUIT_S3_REGION' $S3Region
    Set-GateEnvironment 'CONDUIT_MASTER_KEY' $MasterKey
    Set-GateEnvironment 'CONDUIT_CUSTOMER_MODE' 'External'
    # The complete protocol/function suite shares one loopback IP. Set the
    # declared Gateway limit explicitly so both base and Gateway option views
    # agree, while retaining IP limiting and the separate per-key limits.
    Set-GateEnvironment 'CONDUIT_CORE_RATE_LIMIT_MAX_REQUESTS' '1000'
    Set-GateEnvironment 'CONDUIT_NATIVE_PROVIDER_URL' "http://127.0.0.1:$ProviderPort"
    Set-GateEnvironment 'CONDUIT_MIGRATION_MODE' 'Wait'
    Set-GateEnvironment 'CONDUIT_MIGRATION_WAIT_TIMEOUT_SECONDS' '0'
    Set-GateEnvironment 'ConduitLLM__Messaging__Backend' 'Wolverine'
    Set-GateEnvironment 'BatchSpending__FlushIntervalSeconds' '1'
    Set-GateEnvironment 'SignalR__ConnectionLimits__MaxConnectionsPerVirtualKey' '2'
    Set-GateEnvironment 'ASPNETCORE_ENVIRONMENT' 'Production'
    # The final-flush witness distinguishes StopAsync from a racing timer flush.
    Set-GateEnvironment 'Logging__LogLevel__ConduitLLM.Configuration.Services.BillingAuditService' 'Debug'
    Set-GateEnvironment 'CONDUIT_ENABLE_HTTPS_REDIRECTION' 'false'
    Set-GateEnvironment 'CONDUIT_TRUSTED_PROXY_ENABLED' 'true'
    Set-GateEnvironment 'Telemetry__TracingEnabled' 'true'
    Set-GateEnvironment 'Telemetry__OtlpEndpoint' 'http://127.0.0.1:4317'
    Set-GateEnvironment 'DOTNET_EnableDiagnostics' '0'
    Set-GateEnvironment 'CONDUIT_NATIVE_DIRECT_DATABASE_CONNECTION_STRING' $null
    Set-GateEnvironment 'CONDUIT_NATIVE_DIRECT_REDIS_CONNECTION_STRING' $null
    if ($IsWindows) {
        # Native smoke processes run without an elevated Windows service identity.
        # Prevent the default EventLog provider from masking the real application log
        # with an access-denied AggregateException on warning/error events.
        Set-GateEnvironment 'Logging__EventLog__LogLevel__Default' 'None'
    }

    Wait-ForHttp "$($S3Endpoint.TrimEnd('/'))/$($S3HealthPath.TrimStart('/'))" 'S3-compatible media storage'

    $probeName = if ($IsWindows) { 'ConduitLLM.GatewayNativeAotTests.exe' } else { 'ConduitLLM.GatewayNativeAotTests' }
    $nativeProbe = Join-Path $runtimeRoot 'probe' $probeName
    $probeDll = Join-Path $probeProject 'bin/Release/net10.0/ConduitLLM.GatewayNativeAotTests.dll'
    Set-GateEnvironment 'CONDUIT_NATIVE_PROXY_TOKEN' ([Guid]::NewGuid().ToString('N'))
    Set-GateEnvironment 'CONDUIT_NATIVE_PROXY_CONTROL_URL' "http://127.0.0.1:$DependencyControlPort"
    Set-GateEnvironment 'CONDUIT_NATIVE_PROXY_POSTGRES_PORT' ([string]$PostgresProxyPort)
    Set-GateEnvironment 'CONDUIT_NATIVE_PROXY_REDIS_PORT' ([string]$RedisProxyPort)
    # Validate/capture direct routes before any owned startup database is made.
    if (Test-Path -LiteralPath $nativeProbe -PathType Leaf) {
        $routesJson = & $nativeProbe --proxy-routes
    } else {
        $routesJson = dotnet $probeDll --proxy-routes
    }
    if ($LASTEXITCODE -ne 0) { throw 'Dependency proxy routing configuration failed.' }
    $routes = $routesJson | ConvertFrom-Json
    & (Join-Path $PSScriptRoot 'schema-startup-native-parity.ps1') -RuntimeRoot $runtimeRoot `
        -FixtureDll $startupFixtureDll -MigratorDll (Join-Path $migratorProject 'bin/Release/net10.0/ConduitLLM.Migrator.dll') `
        -DatabaseUrl $DatabaseUrl -ManagementConnectionString $routes.directDatabase -LogRoot $logRoot `
        -GatewayPort $GatewayPort -SecondaryGatewayPort $SecondaryGatewayPort -AdminPort $AdminPort -TimeoutSeconds $TimeoutSeconds

    Write-Host 'Applying migrations before native process startup...'
    dotnet (Join-Path $migratorProject 'bin/Release/net10.0/ConduitLLM.Migrator.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Standalone migrator failed.' }

    $protectedFunctionKey = Invoke-FunctionCredentialFixture $startupFixtureDll $routes.directRedis 'native-aot-function-key-32-bytes'
    Set-GateEnvironment 'CONDUIT_NATIVE_FUNCTION_PROTECTED_KEY' $protectedFunctionKey
    Set-GateEnvironment 'CONDUIT_NATIVE_FUNCTION_FIXTURE_OWNER' ([Guid]::NewGuid().ToString('N'))
    Set-GateEnvironment 'CONDUIT_NATIVE_MCP_FIXTURE_OWNER' ([Guid]::NewGuid().ToString('N'))
    Set-GateEnvironment 'CONDUIT_NATIVE_ADMIN_SETTINGS_OWNER' ([Guid]::NewGuid().ToString('N'))
    Set-GateEnvironment 'CONDUIT_NATIVE_ADMIN_IP_OWNER' ([Guid]::NewGuid().ToString('N'))
    Set-GateEnvironment 'CONDUIT_NATIVE_BILLING_AUDIT_FIXTURE_OWNER' ([Guid]::NewGuid().ToString('N'))
    Set-GateEnvironment 'CONDUIT_API_TO_API_BACKEND_AUTH_KEY' $MasterKey

    Write-Host 'Seeding typed-store authentication and IP-filter fixtures...'
    # Set this before the call: a seed that committed but lost its acknowledgement
    # still requires owner-checked, idempotent cleanup.
    $functionSeedAttempted = $true
    if (Test-Path -LiteralPath $nativeProbe -PathType Leaf) {
        & $nativeProbe --seed
    } else {
        dotnet (Join-Path $probeProject 'bin/Release/net10.0/ConduitLLM.GatewayNativeAotTests.dll') --seed
    }
    if ($LASTEXITCODE -ne 0) { throw 'Native Gateway parity fixture seed failed.' }

    $dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    # Capture credential-bearing configuration privately, never into report logs.
    if (Test-Path -LiteralPath $nativeProbe -PathType Leaf) {
        $proxies = Start-LoggedProcess 'dependency-proxies' $nativeProbe @('--dependency-proxies') (Split-Path $nativeProbe) $null
    } else {
        $proxies = Start-LoggedProcess 'dependency-proxies' $dotnet @($probeDll, '--dependency-proxies') $probeProject $null
    }
    $processes.Add($proxies)
    Wait-ForHttp "http://127.0.0.1:$DependencyControlPort/health" 'owned dependency proxies'
    Set-GateEnvironment 'DATABASE_URL' $routes.database
    Set-GateEnvironment 'REDIS_URL' $null
    Set-GateEnvironment 'CONDUIT_REDIS_CONNECTION_STRING' $routes.redis
    Set-GateEnvironment 'ConnectionStrings__RedisSignalR' $routes.redis
    Set-GateEnvironment 'CONDUIT_NATIVE_DIRECT_DATABASE_CONNECTION_STRING' $routes.directDatabase
    Set-GateEnvironment 'CONDUIT_NATIVE_DIRECT_REDIS_CONNECTION_STRING' $routes.directRedis
    if (Test-Path -LiteralPath $nativeProbe -PathType Leaf) {
        $provider = Start-LoggedProcess 'provider' $nativeProbe @('--mock-provider') (Split-Path $nativeProbe) $null
    } else {
        $dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
        $probeDll = Join-Path $probeProject 'bin/Release/net10.0/ConduitLLM.GatewayNativeAotTests.dll'
        $provider = Start-LoggedProcess 'provider' $dotnet @($probeDll, '--mock-provider') $probeProject $null
    }
    $processes.Add($provider)
    Wait-ForHttp "http://127.0.0.1:$ProviderPort/health" 'OpenAI-compatible provider stub'

    $adminExecutable = Get-NativeExecutable 'Admin'
    # Apply security overrides only to this owned Admin child. The alternate
    # source-IP proof must reach the database rules without a private-IP bypass.
    $adminSecurity = @{
        CONDUIT_ADMIN_IP_FILTERING_ENABLED = 'true'
        CONDUIT_ADMIN_IP_FILTER_ALLOW_PRIVATE = 'false'
        CONDUIT_ADMIN_IP_FILTER_MODE = 'permissive'
        CONDUIT_ADMIN_IP_FILTER_WHITELIST = ''
        CONDUIT_ADMIN_IP_FILTER_BLACKLIST = ''
    }
    $admin = Start-LoggedProcess 'admin' $adminExecutable @() (Split-Path $adminExecutable) "http://127.0.0.1:$AdminPort" $adminSecurity
    $processes.Add($admin)
    Wait-ForHttp "http://127.0.0.1:$AdminPort/health/live" 'native Admin liveness'
    Wait-ForHttp "http://127.0.0.1:$AdminPort/health/ready" 'native Admin readiness'

    $gatewayExecutable = Get-NativeExecutable 'Gateway'
    $gateway = Start-LoggedProcess 'gateway-primary' $gatewayExecutable @() (Split-Path $gatewayExecutable) "http://127.0.0.1:$GatewayPort"
    $processes.Add($gateway)
    $gatewayIdentity = Get-OwnedLinuxStartToken $gateway
    Wait-ForHttp "http://127.0.0.1:$GatewayPort/health/runtime-capabilities" 'primary native Gateway'
    Wait-ForHttp "http://127.0.0.1:$GatewayPort/health/ready" 'primary native Gateway readiness'

    $secondary = Start-LoggedProcess 'gateway-secondary' $gatewayExecutable @() (Split-Path $gatewayExecutable) "http://127.0.0.1:$SecondaryGatewayPort"
    $processes.Add($secondary)
    $secondaryIdentity = Get-OwnedLinuxStartToken $secondary
    Wait-ForHttp "http://127.0.0.1:$SecondaryGatewayPort/health/runtime-capabilities" 'secondary native Gateway'
    Wait-ForHttp "http://127.0.0.1:$SecondaryGatewayPort/health/ready" 'secondary native Gateway readiness'

    # Children keep their proxy routes. The probe and message publisher use the
    # original routes, including bounded cleanup while host dependencies are down.
    Set-GateEnvironment 'DATABASE_URL' $DatabaseUrl
    Set-GateEnvironment 'REDIS_URL' $RedisUrl
    Set-GateEnvironment 'CONDUIT_REDIS_CONNECTION_STRING' $routes.directRedis
    Set-GateEnvironment 'CONDUIT_NATIVE_ADMIN_URL' "http://127.0.0.1:$AdminPort"
    Set-GateEnvironment 'CONDUIT_NATIVE_GATEWAY_URL' "http://127.0.0.1:$GatewayPort"
    Set-GateEnvironment 'CONDUIT_NATIVE_GATEWAY_SECONDARY_URL' "http://127.0.0.1:$SecondaryGatewayPort"
    # This quiet phase must precede every protocol/provider/benchmark request.
    # Mark the owner before creation to cover an unknown committed outcome.
    $billingAuditAttempted = $true
    Invoke-BillingAuditLifecycle '--billing-audit-timer' 'Native BillingAudit quiet timer: PASS'
    # The in-process MCP server binds port zero and its fixture is created by the
    # probe. Track the attempt before invocation even if its acknowledgement is lost.
    $mcpSeedAttempted = $true
    $adminSettingsAttempted = $true
    if (Test-Path -LiteralPath $nativeProbe -PathType Leaf) {
        & $nativeProbe
    } else {
        dotnet (Join-Path $probeProject 'bin/Release/net10.0/ConduitLLM.GatewayNativeAotTests.dll')
    }
    if ($LASTEXITCODE -ne 0) { throw "Gateway native parity probe exited with $LASTEXITCODE." }

    # The owner is known before POST and cleanup is repeated after producers are
    # reaped, covering an unknown committed HTTP outcome or a late request.
    $adminIpAttempted = $true
    if (Test-Path -LiteralPath $nativeProbe -PathType Leaf) {
        $adminIpOutput = Invoke-PrivateFixtureProcess $nativeProbe @('--admin-ip-filters') @{} 'Native Admin IP-filter HTTP/security proof'
    } else {
        $adminIpOutput = Invoke-PrivateFixtureProcess $dotnet @($probeDll, '--admin-ip-filters') @{} 'Native Admin IP-filter HTTP/security proof'
    }
    if ($adminIpOutput -cnotmatch '\ANative Admin IP filters HTTP/security: PASS(?:\r?\n)?\z') {
        throw 'Native Admin IP-filter proof returned an invalid result.'
    }
    Write-Host '  PASS  Native Admin IP filters HTTP/security'

    # Run the same bounded, authenticated workload driver used by the benchmark
    # collector. These small correctness samples are not promotion measurements.
    # The transport probe accepts four completed requests and one canceled stream.
    $shutdownMeasuredRequests = 5
    foreach ($workload in @('admin-setting-read', 'gateway-chat-nonstream', 'gateway-chat-sse')) {
        $configuration = [ordered]@{
            workload = $workload
            baseUrl = if ($workload -eq 'admin-setting-read') { "http://127.0.0.1:$AdminPort" } else { "http://127.0.0.1:$GatewayPort" }
            requests = 4
            concurrency = 2
            warmupRequests = 2
            timeoutSeconds = 5
        }
        if ($workload -eq 'admin-setting-read') {
            $configuration.settingKey = 'IpFilter:Enabled'
            $configuration.expectedValue = 'true'
            $benchmarkKey = $MasterKey
        } else {
            $configuration.model = 'native-aot-model'
            $configuration.prompt = 'native benchmark workload'
            $configuration.expectedContent = if ($workload -eq 'gateway-chat-sse') { 'native stream response' } else { 'native provider response' }
            $configuration.expectedPromptTokens = 10
            $configuration.expectedCompletionTokens = 5
            $benchmarkKey = 'condt_native_aot_parity'
        }
        $configurationPath = Join-Path $logRoot "$workload.configuration.json"
        $configuration | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configurationPath -Encoding utf8
        $benchmarkArguments = @('--benchmark-workload', $configurationPath)
        $benchmarkFile = $nativeProbe
        if (-not (Test-Path -LiteralPath $nativeProbe -PathType Leaf)) {
            $benchmarkFile = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
            $benchmarkArguments = @($probeDll) + $benchmarkArguments
        }
        $benchmarkOutput = Invoke-PrivateFixtureProcess $benchmarkFile $benchmarkArguments @{
            CONDUIT_NATIVE_BENCHMARK_API_KEY = $benchmarkKey
        } 'Authenticated benchmark workload'
        try { $benchmarkReport = $benchmarkOutput | ConvertFrom-Json }
        catch { throw 'Authenticated benchmark workload returned invalid JSON.' }
        $result = @($benchmarkReport.workloads)
        if ($benchmarkReport.schemaVersion -ne 2 -or $result.Count -ne 1 -or
            $result[0].id -cne $workload -or $result[0].authenticated -ne $true -or
            $result[0].requested -ne 4 -or $result[0].executed -ne 4 -or
            $result[0].completed -ne 4 -or $result[0].failures -ne 0 -or
            $result[0].warmupRequests -ne 2 -or $result[0].concurrency -ne 2 -or
            $result[0].timeoutSeconds -ne 5 -or
            $result[0].inputFingerprint -cnotmatch '\A[0-9a-f]{64}\z') {
            throw 'Authenticated benchmark workload did not complete its configured requests.'
        }
        $benchmarkOutput | Set-Content -LiteralPath (Join-Path $logRoot "$workload.benchmark.json") -Encoding utf8
        Write-Host "  PASS  Authenticated benchmark workload $workload"
        if ($workload -ne 'admin-setting-read') {
            $shutdownMeasuredRequests += $configuration.requests + $configuration.warmupRequests
        }
    }
    foreach ($hostProcess in @($admin, $gateway, $secondary)) {
        if ($hostProcess.HasExited) { throw "Native host $($hostProcess.Id) exited during dependency recovery." }
    }
    Invoke-BillingAuditLifecycle '--billing-audit-drain' 'Native BillingAudit completed timer wave: PASS'
    # Each host gets a fresh observed timer cycle before its own tail is armed.
    # The unchanged ten-second timer remains active during other hosted-service
    # shutdown, so a normal flush of the tail must still make the proof fail.
    $lifecycleLogs = @(
        [pscustomobject]@{ Name = 'gateway-primary'; HostName = 'primary'; Process = $gateway; Identity = $gatewayIdentity },
        [pscustomobject]@{ Name = 'gateway-secondary'; HostName = 'secondary'; Process = $secondary; Identity = $secondaryIdentity }
    )
    foreach ($hostEntry in $lifecycleLogs) {
        $prepareOffsets = @{}
        foreach ($stream in @('stdout', 'stderr')) {
            $path = Join-Path $logRoot "$($hostEntry.Name).$stream.log"
            $prepareOffsets[$path] = (Get-Item -LiteralPath $path).Length
        }
        $timerTimestamp = Invoke-BillingAuditLifecycle '--billing-audit-prepare' `
            "Native BillingAudit prepared shutdown timer $($hostEntry.HostName): PASS" $hostEntry.HostName -timerWitness
        # Observe copying of the prepare marker's normal flush before capturing
        # the exclusive shutdown range. All waiting consumes the same 3s bound.
        while ($true) {
            Assert-LifecycleTimerWindow $timerTimestamp
            $prepareText = ''
            foreach ($stream in @('stdout', 'stderr')) {
                $path = Join-Path $logRoot "$($hostEntry.Name).$stream.log"
                $prepareText += Read-LogTail $path $prepareOffsets[$path]
            }
            $prepareCount = [regex]::Matches($prepareText, '\bFlushed 1 Billing audit events to database').Count
            if ($prepareCount -gt 1) { throw 'Owned native Gateway has ambiguous prepare timer witnesses.' }
            if ($prepareCount -eq 1) { break }
            Start-Sleep -Milliseconds 25
        }
        $offsets = @{}
        foreach ($stream in @('stdout', 'stderr')) {
            $path = Join-Path $logRoot "$($hostEntry.Name).$stream.log"
            $offsets[$path] = (Get-Item -LiteralPath $path).Length
        }
        Invoke-BillingAuditLifecycle '--billing-audit-tail' `
            "Native BillingAudit pending shutdown tail $($hostEntry.HostName): PASS" $hostEntry.HostName
        Assert-LifecycleTimerWindow $timerTimestamp
        Send-OwnedLinuxSigTerm $hostEntry.Process $hostEntry.Identity
        Assert-LifecycleTimerWindow $timerTimestamp
        if (-not $hostEntry.Process.WaitForExit(30000) -or $hostEntry.Process.ExitCode -ne 0) {
            throw 'Owned native Gateway did not exit gracefully.'
        }
        # A timed process wait can precede completion of redirected log copying.
        # Poll only the owned files under a fresh monotonic five-second bound.
        $logDrain = [Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            $text = ''
            foreach ($stream in @('stdout', 'stderr')) {
                $path = Join-Path $logRoot "$($hostEntry.Name).$stream.log"
                $text += Read-LogTail $path $offsets[$path]
            }
            $finalCount = [regex]::Matches($text, 'Final flush of 1 Billing audit events to database').Count
            if ($finalCount -gt 1 -or [regex]::IsMatch($text, '\bFlushed [1-9][0-9]* Billing audit events to database')) {
                throw 'Owned native Gateway lacks an exclusive one-event shutdown-flush witness.'
            }
            if ($finalCount -eq 1) { break }
            $remaining = 5000 - [int]$logDrain.ElapsedMilliseconds
            if ($remaining -le 0) { throw 'Owned native Gateway shutdown-flush log did not drain within five seconds.' }
            Start-Sleep -Milliseconds ([Math]::Min(50, $remaining))
        }
        Invoke-BillingAuditLifecycle '--billing-audit-verify-host' `
            "Native BillingAudit graceful shutdown tail $($hostEntry.HostName): PASS" $hostEntry.HostName
        foreach ($stream in @('stdout', 'stderr')) {
            $path = Join-Path $logRoot "$($hostEntry.Name).$stream.log"
            $content = Get-Content -LiteralPath $path -Raw
            if ($content -match 'SafeWaitHandle|ObjectDisposedException|Error flushing pending updates during async disposal|Error stopping inner service SpendNotificationService') {
                throw "$($hostEntry.Name) logged a shutdown lifecycle failure. See $path."
            }
        }
        Write-Host "  PASS  $($hostEntry.Name) graceful traced shutdown"
    }
    Invoke-BillingAuditLifecycle '--billing-audit-verify' 'Native BillingAudit graceful shutdown tails: PASS'
    if (Test-Path -LiteralPath $nativeProbe -PathType Leaf) {
        & $nativeProbe --verify-shutdown $shutdownMeasuredRequests
    } else {
        dotnet $probeDll --verify-shutdown $shutdownMeasuredRequests
    }
    if ($LASTEXITCODE -ne 0) { throw 'Accounting verification after native shutdown failed.' }
}
catch {
    $parityFailure = $_
    Write-Host "Native parity logs: $logRoot" -ForegroundColor Yellow
    foreach ($file in Get-ChildItem -LiteralPath $logRoot -File) {
        Write-Host "===== $($file.Name) ====="
        Get-Content -LiteralPath $file.FullName -Tail 100
    }
    throw
}
finally {
    try {
        # Stop/reap every owned producer before removing its SQL or Redis state.
        Stop-OwnedParityProcesses $processes
        $fixtureCleanupFailures = [Collections.Generic.List[Exception]]::new()
        foreach ($cleanup in @(
            [pscustomobject]@{ Attempted = $adminIpAttempted; Argument = '--cleanup-admin-ip-filters'; Label = 'Owned Admin IP-filter fixture cleanup' },
            [pscustomobject]@{ Attempted = $billingAuditAttempted; Argument = '--cleanup-billing-audit-fixture'; Label = 'Owned BillingAudit lifecycle fixture cleanup' },
            [pscustomobject]@{ Attempted = $adminSettingsAttempted; Argument = '--cleanup-admin-global-settings'; Label = 'Owned Admin global-setting fixture cleanup' },
            [pscustomobject]@{ Attempted = $mcpSeedAttempted; Argument = '--cleanup-mcp-function-fixture'; Label = 'Owned MCP function fixture cleanup' },
            [pscustomobject]@{ Attempted = $functionSeedAttempted; Argument = '--cleanup-function-fixture'; Label = 'Owned function fixture cleanup' }
        )) {
            if (-not $cleanup.Attempted) { continue }
            try {
                if (Test-Path -LiteralPath $nativeProbe -PathType Leaf) {
                    $cleanupOutput = Invoke-PrivateFixtureProcess $nativeProbe @($cleanup.Argument) @{} $cleanup.Label
                } else {
                    $cleanupOutput = Invoke-PrivateFixtureProcess (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source @($probeDll, $cleanup.Argument) @{} $cleanup.Label
                }
                $expected = '\A' + [regex]::Escape($cleanup.Label + ': PASS') + '(?:\r?\n)?\z'
                if ($cleanupOutput -cnotmatch $expected) { throw ($cleanup.Label + ' returned an invalid result.') }
                Write-Host ('  PASS  ' + $cleanup.Label)
            } catch {
                # A failure for one owner must not prevent cleanup of the other.
                $fixtureCleanupFailures.Add($_.Exception)
            }
        }
        if ($fixtureCleanupFailures.Count) {
            throw [AggregateException]::new('Owned function fixtures could not all be cleaned.', $fixtureCleanupFailures)
        }
    } catch {
        if ($parityFailure) {
            throw [AggregateException]::new('Native parity and owned fixture cleanup both failed.',
                [Exception[]]@($parityFailure.Exception, $_.Exception))
        }
        throw
    } finally {
        foreach ($name in $originalEnvironment.Keys) {
            Set-GateEnvironment $name $originalEnvironment[$name]
        }
    }
}
