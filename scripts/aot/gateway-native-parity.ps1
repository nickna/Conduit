#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the Phase 5 Gateway protocol/infrastructure matrix against native processes.

.DESCRIPTION
    Starts a native Admin and two native Gateway executables against real PostgreSQL
    and Redis, then runs the native JSON SignalR/HTTP supported-boundary probe.
#>
[CmdletBinding()]
param(
    [string]$ArtifactDirectory,
    [string]$DatabaseUrl = $env:DATABASE_URL,
    [string]$RedisUrl = $env:REDIS_URL,
    [string]$MasterKey = 'native-aot-parity-master-key-32-bytes',
    [int]$GatewayPort = 15100,
    [int]$SecondaryGatewayPort = 15101,
    [int]$AdminPort = 15102,
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
$logRoot = Join-Path ([IO.Path]::GetTempPath()) "gateway-native-parity-$PID"
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null

if (-not $DatabaseUrl) { throw 'DatabaseUrl not set.' }
if (-not $RedisUrl) { throw 'RedisUrl not set.' }

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
    if ($urls) { $env:ASPNETCORE_URLS = $urls }
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
}

$env:DATABASE_URL = $DatabaseUrl
$env:REDIS_URL = $RedisUrl
$env:CONDUIT_MASTER_KEY = $MasterKey
$env:CONDUIT_MIGRATION_MODE = 'Skip'
$env:ConduitLLM__Messaging__Backend = 'Wolverine'
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:CONDUIT_ENABLE_HTTPS_REDIRECTION = 'false'
$env:CONDUIT_TRUSTED_PROXY_ENABLED = 'true'
$env:Telemetry__TracingEnabled = 'true'
$env:Telemetry__OtlpEndpoint = 'http://127.0.0.1:4317'
$env:DOTNET_EnableDiagnostics = '0'
if ($IsWindows) {
    # Native smoke processes run without an elevated Windows service identity.
    # Prevent the default EventLog provider from masking the real application log
    # with an access-denied AggregateException on warning/error events.
    $env:Logging__EventLog__LogLevel__Default = 'None'
}

Write-Host 'Applying migrations before native process startup...'
dotnet (Join-Path $migratorProject 'bin/Release/net10.0/ConduitLLM.Migrator.dll')
if ($LASTEXITCODE -ne 0) { throw 'Standalone migrator failed.' }

$processes = [Collections.Generic.List[Diagnostics.Process]]::new()
try {
    $adminExecutable = Get-NativeExecutable 'Admin'
    $admin = Start-LoggedProcess 'admin' $adminExecutable @() (Split-Path $adminExecutable) "http://127.0.0.1:$AdminPort"
    $processes.Add($admin)
    Wait-ForHttp "http://127.0.0.1:$AdminPort/health/live" 'native Admin liveness'

    $gatewayExecutable = Get-NativeExecutable 'Gateway'
    $gateway = Start-LoggedProcess 'gateway-primary' $gatewayExecutable @() (Split-Path $gatewayExecutable) "http://127.0.0.1:$GatewayPort"
    $processes.Add($gateway)
    Wait-ForHttp "http://127.0.0.1:$GatewayPort/health/runtime-capabilities" 'primary native Gateway'

    $secondary = Start-LoggedProcess 'gateway-secondary' $gatewayExecutable @() (Split-Path $gatewayExecutable) "http://127.0.0.1:$SecondaryGatewayPort"
    $processes.Add($secondary)
    Wait-ForHttp "http://127.0.0.1:$SecondaryGatewayPort/health/runtime-capabilities" 'secondary native Gateway'

    $env:CONDUIT_NATIVE_ADMIN_URL = "http://127.0.0.1:$AdminPort"
    $env:CONDUIT_NATIVE_GATEWAY_URL = "http://127.0.0.1:$GatewayPort"
    $env:CONDUIT_NATIVE_GATEWAY_SECONDARY_URL = "http://127.0.0.1:$SecondaryGatewayPort"
    $probeName = if ($IsWindows) { 'ConduitLLM.GatewayNativeAotTests.exe' } else { 'ConduitLLM.GatewayNativeAotTests' }
    $nativeProbe = Join-Path $runtimeRoot 'probe' $probeName
    if (Test-Path -LiteralPath $nativeProbe -PathType Leaf) {
        & $nativeProbe
    } else {
        dotnet (Join-Path $probeProject 'bin/Release/net10.0/ConduitLLM.GatewayNativeAotTests.dll')
    }
    if ($LASTEXITCODE -ne 0) { throw "Gateway native parity probe exited with $LASTEXITCODE." }
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
}
