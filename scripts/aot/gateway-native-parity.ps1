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
$publisherDll = Join-Path $publisherProject 'bin/Release/net10.0/WolverineSmokePublisher.dll'
$logRoot = Join-Path $artifactRoot "reports/gateway-parity-$PID"
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null

if (-not $DatabaseUrl) { throw 'DatabaseUrl not set.' }
if (-not $RedisUrl) { throw 'RedisUrl not set.' }
if (-not $S3Endpoint) { throw 'S3Endpoint not set.' }
if (-not $S3AccessKeyId) { throw 'S3AccessKeyId not set.' }
if (-not $S3SecretAccessKey) { throw 'S3SecretAccessKey not set.' }
if (-not $S3BucketName) { throw 'S3BucketName not set.' }

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
    [string]$urls
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
}
if (-not (Test-Path -LiteralPath $publisherDll -PathType Leaf)) {
    throw "Wolverine media publisher not found: $publisherDll"
}

$processes = [Collections.Generic.List[Diagnostics.Process]]::new()
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
    Set-GateEnvironment 'CONDUIT_NATIVE_PROVIDER_URL' "http://127.0.0.1:$ProviderPort"
    Set-GateEnvironment 'CONDUIT_MIGRATION_MODE' 'Wait'
    Set-GateEnvironment 'CONDUIT_MIGRATION_WAIT_TIMEOUT_SECONDS' '0'
    Set-GateEnvironment 'ConduitLLM__Messaging__Backend' 'Wolverine'
    Set-GateEnvironment 'BatchSpending__FlushIntervalSeconds' '1'
    Set-GateEnvironment 'SignalR__ConnectionLimits__MaxConnectionsPerVirtualKey' '2'
    Set-GateEnvironment 'ASPNETCORE_ENVIRONMENT' 'Production'
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

    Write-Host 'Applying migrations before native process startup...'
    dotnet (Join-Path $migratorProject 'bin/Release/net10.0/ConduitLLM.Migrator.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Standalone migrator failed.' }

    Write-Host 'Seeding typed-store authentication and IP-filter fixtures...'
    $probeName = if ($IsWindows) { 'ConduitLLM.GatewayNativeAotTests.exe' } else { 'ConduitLLM.GatewayNativeAotTests' }
    $nativeProbe = Join-Path $runtimeRoot 'probe' $probeName
    if (Test-Path -LiteralPath $nativeProbe -PathType Leaf) {
        & $nativeProbe --seed
    } else {
        dotnet (Join-Path $probeProject 'bin/Release/net10.0/ConduitLLM.GatewayNativeAotTests.dll') --seed
    }
    if ($LASTEXITCODE -ne 0) { throw 'Native Gateway parity fixture seed failed.' }

    Set-GateEnvironment 'CONDUIT_NATIVE_PROXY_TOKEN' ([Guid]::NewGuid().ToString('N'))
    Set-GateEnvironment 'CONDUIT_NATIVE_PROXY_CONTROL_URL' "http://127.0.0.1:$DependencyControlPort"
    Set-GateEnvironment 'CONDUIT_NATIVE_PROXY_POSTGRES_PORT' ([string]$PostgresProxyPort)
    Set-GateEnvironment 'CONDUIT_NATIVE_PROXY_REDIS_PORT' ([string]$RedisProxyPort)
    $probeDll = Join-Path $probeProject 'bin/Release/net10.0/ConduitLLM.GatewayNativeAotTests.dll'
    $dotnet = (Get-Command dotnet).Source
    # Capture credential-bearing configuration privately, never into report logs.
    if (Test-Path -LiteralPath $nativeProbe -PathType Leaf) {
        $routesJson = & $nativeProbe --proxy-routes
        if ($LASTEXITCODE -ne 0) { throw 'Dependency proxy routing configuration failed.' }
        $proxies = Start-LoggedProcess 'dependency-proxies' $nativeProbe @('--dependency-proxies') (Split-Path $nativeProbe) $null
    } else {
        $routesJson = dotnet $probeDll --proxy-routes
        if ($LASTEXITCODE -ne 0) { throw 'Dependency proxy routing configuration failed.' }
        $proxies = Start-LoggedProcess 'dependency-proxies' $dotnet @($probeDll, '--dependency-proxies') $probeProject $null
    }
    $processes.Add($proxies)
    $routes = $routesJson | ConvertFrom-Json
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
        $dotnet = (Get-Command dotnet).Source
        $probeDll = Join-Path $probeProject 'bin/Release/net10.0/ConduitLLM.GatewayNativeAotTests.dll'
        $provider = Start-LoggedProcess 'provider' $dotnet @($probeDll, '--mock-provider') $probeProject $null
    }
    $processes.Add($provider)
    Wait-ForHttp "http://127.0.0.1:$ProviderPort/health" 'OpenAI-compatible provider stub'

    $adminExecutable = Get-NativeExecutable 'Admin'
    $admin = Start-LoggedProcess 'admin' $adminExecutable @() (Split-Path $adminExecutable) "http://127.0.0.1:$AdminPort"
    $processes.Add($admin)
    Wait-ForHttp "http://127.0.0.1:$AdminPort/health/live" 'native Admin liveness'
    Wait-ForHttp "http://127.0.0.1:$AdminPort/health/ready" 'native Admin readiness'

    $gatewayExecutable = Get-NativeExecutable 'Gateway'
    $gateway = Start-LoggedProcess 'gateway-primary' $gatewayExecutable @() (Split-Path $gatewayExecutable) "http://127.0.0.1:$GatewayPort"
    $processes.Add($gateway)
    Wait-ForHttp "http://127.0.0.1:$GatewayPort/health/runtime-capabilities" 'primary native Gateway'
    Wait-ForHttp "http://127.0.0.1:$GatewayPort/health/ready" 'primary native Gateway readiness'

    $secondary = Start-LoggedProcess 'gateway-secondary' $gatewayExecutable @() (Split-Path $gatewayExecutable) "http://127.0.0.1:$SecondaryGatewayPort"
    $processes.Add($secondary)
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
    if (Test-Path -LiteralPath $nativeProbe -PathType Leaf) {
        & $nativeProbe
    } else {
        dotnet (Join-Path $probeProject 'bin/Release/net10.0/ConduitLLM.GatewayNativeAotTests.dll')
    }
    if ($LASTEXITCODE -ne 0) { throw "Gateway native parity probe exited with $LASTEXITCODE." }
    foreach ($hostProcess in @($admin, $gateway, $secondary)) {
        if ($hostProcess.HasExited) { throw "Native host $($hostProcess.Id) exited during dependency recovery." }
    }
}
catch {
    Write-Host "Native parity logs: $logRoot" -ForegroundColor Yellow
    foreach ($file in Get-ChildItem -LiteralPath $logRoot -File) {
        Write-Host "===== $($file.Name) ====="
        Get-Content -LiteralPath $file.FullName -Tail 100
    }
    throw
}
finally {
    foreach ($process in $processes) {
        if ($process -and -not $process.HasExited) {
            try { Stop-Process -Id $process.Id -Force -Confirm:$false } catch {}
        }
    }
    foreach ($name in $originalEnvironment.Keys) {
        Set-GateEnvironment $name $originalEnvironment[$name]
    }
}
