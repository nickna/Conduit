#!/usr/bin/env pwsh
<# Starts published hosts against owned missing/prior schemas and upgrades them in place. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeRoot,
    [Parameter(Mandatory)][string]$FixtureDll,
    [Parameter(Mandatory)][string]$MigratorDll,
    [Parameter(Mandatory)][string]$DatabaseUrl,
    [Parameter(Mandatory)][string]$ManagementConnectionString,
    [Parameter(Mandatory)][string]$LogRoot,
    [int]$GatewayPort = 15100,
    [int]$SecondaryGatewayPort = 15101,
    [int]$AdminPort = 15102,
    [int]$TimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'
$originalEnvironment = @{}
function Set-StartupEnvironment([string]$name, $value) {
    if (-not $originalEnvironment.ContainsKey($name)) {
        $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    $environmentValue = if ($null -eq $value) { [NullString]::Value } else { [string]$value }
    [Environment]::SetEnvironmentVariable($name, $environmentValue)
}

function Start-StartupHost([string]$scenario, [string]$name, [string]$service, [int]$port) {
    $executableName = "ConduitLLM.$service" + $(if ($IsWindows) { '.exe' } else { '' })
    $executable = Join-Path $RuntimeRoot $service.ToLowerInvariant() $executableName
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Published $service executable is missing." }
    Set-StartupEnvironment 'ASPNETCORE_URLS' "http://127.0.0.1:$port"
    $start = @{
        FilePath = $executable
        WorkingDirectory = (Split-Path $executable)
        RedirectStandardOutput = (Join-Path $LogRoot "startup-$scenario-$name.stdout.log")
        RedirectStandardError = (Join-Path $LogRoot "startup-$scenario-$name.stderr.log")
        PassThru = $true
    }
    if ($IsWindows) { $start.WindowStyle = 'Hidden' } else { $start.NoNewWindow = $true }
    $process = Start-Process @start
    Write-Host "Started $scenario $name startup fixture (pid $($process.Id))"
    return [pscustomobject]@{ Process = $process; Name = $name; Url = "http://127.0.0.1:$port" }
}

function Read-StartupHealth([string]$url) {
    try {
        return Invoke-WebRequest $url -SkipHttpErrorCheck -TimeoutSec 5
    } catch [Net.Http.HttpRequestException] {
        # A newly launched listener may not have bound yet. This is never
        # counted as a successful unhealthy/readiness response.
        return $null
    } catch [Threading.Tasks.TaskCanceledException] {
        return $null
    }
}

function Assert-StartupHealth($hosts, [bool]$afterMigration) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $latest = 'No health responses received.'
    while ([DateTime]::UtcNow -lt $deadline) {
        $complete = $true
        $observations = [Collections.Generic.List[string]]::new()
        foreach ($hostEntry in $hosts) {
            if ($hostEntry.Process.HasExited) { throw "Startup fixture $($hostEntry.Name) exited before recovery." }
            $live = Read-StartupHealth "$($hostEntry.Url)/health/live"
            if ($null -eq $live) {
                $complete = $false
                $observations.Add("$($hostEntry.Name): liveness listener unavailable or timed out")
                continue
            }
            if ($live.StatusCode -ne 200) {
                throw "Unexpected startup liveness response for $($hostEntry.Name): $($live.StatusCode)"
            }
            $ready = Read-StartupHealth "$($hostEntry.Url)/health/ready"
            if ($null -eq $ready) {
                $complete = $false
                $observations.Add("$($hostEntry.Name): live=200, readiness listener unavailable or timed out")
                continue
            }
            $observations.Add("$($hostEntry.Name): live=$($live.StatusCode), ready=$($ready.StatusCode)")
            if ($ready.StatusCode -notin @(200, 503)) {
                throw "Unexpected startup health response: $($observations -join '; ')"
            }
            if (-not $afterMigration -and $ready.StatusCode -eq 200) {
                throw "Startup fixture $($hostEntry.Name) became ready before the release migrator ran."
            }
            $complete = $complete -and ($ready.StatusCode -eq $(if ($afterMigration) { 200 } else { 503 }))
        }
        $latest = $observations -join '; '
        if ($complete) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out checking schema startup health: $latest"
}

foreach ($file in @($FixtureDll, $MigratorDll)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Startup test tool is missing: $file" }
}
$originalUri = [Uri]::new($DatabaseUrl, [UriKind]::Absolute)
if ($originalUri.Scheme -notin @('postgres', 'postgresql')) { throw 'Schema startup hosts require a PostgreSQL URI.' }
New-Item -ItemType Directory -Force -Path $LogRoot | Out-Null

try {
    Set-StartupEnvironment 'CONDUIT_NATIVE_STARTUP_MANAGEMENT_CONNECTION_STRING' $ManagementConnectionString
    Set-StartupEnvironment 'CONDUIT_MIGRATION_MODE' 'Wait'
    Set-StartupEnvironment 'CONDUIT_MIGRATION_WAIT_TIMEOUT_SECONDS' '0'
    Set-StartupEnvironment 'ConduitLLM__Messaging__Backend' 'Wolverine'
    Set-StartupEnvironment 'ConduitLLM__Messaging__Wolverine__Transport' 'Postgresql'
    Set-StartupEnvironment 'ConduitLLM__Messaging__Wolverine__AutoProvision' 'false'
    Set-StartupEnvironment 'ConduitLLM__Messaging__Wolverine__SchemaName' $null
    # Reprobe state in Redis belongs to the application's normal database, not
    # this disposable empty database. Keep the worker out of startup fixtures.
    Set-StartupEnvironment 'ProviderKeyReprobe__Enabled' 'false'
    foreach ($scenario in @('missing', 'stale')) {
        $ownedHosts = [Collections.Generic.List[object]]::new()
        $fixtureName = $null
        $assertionFailure = $null
        try {
            # stdout contains credentials: capture privately and never write it
            # to the diagnostic artifact or console.
            $fixtureJson = & dotnet $FixtureDll create $scenario
            if ($LASTEXITCODE -ne 0) { throw "Could not create the $scenario startup database." }
            $fixture = $fixtureJson | ConvertFrom-Json
            if ($fixture.name -notmatch '^conduit_native_startup_[0-9a-f]{32}$') { throw 'Invalid generated startup database identity.' }
            $fixtureName = $fixture.name
            $hostUri = [UriBuilder]::new($originalUri)
            $hostUri.Path = $fixtureName
            Set-StartupEnvironment 'DATABASE_URL' $hostUri.Uri.AbsoluteUri
            $ownedHosts.Add((Start-StartupHost $scenario 'admin' 'Admin' $AdminPort))
            $ownedHosts.Add((Start-StartupHost $scenario 'gateway-primary' 'Gateway' $GatewayPort))
            $ownedHosts.Add((Start-StartupHost $scenario 'gateway-secondary' 'Gateway' $SecondaryGatewayPort))
            Assert-StartupHealth $ownedHosts $false
            # Observe a second complete snapshot after at least one runtime
            # preflight interval, with every original process still alive.
            Start-Sleep -Seconds 6
            Assert-StartupHealth $ownedHosts $false
            Set-StartupEnvironment 'DATABASE_URL' $fixture.connectionString
            & dotnet $MigratorDll *> (Join-Path $LogRoot "startup-$scenario-migrator.log")
            if ($LASTEXITCODE -ne 0) { throw "Release migration failed for the $scenario startup database." }
            Assert-StartupHealth $ownedHosts $true
            foreach ($hostEntry in $ownedHosts) {
                if ($hostEntry.Process.HasExited) { throw "Startup fixture $($hostEntry.Name) exited during schema recovery." }
            }
            Write-Host "  PASS  native $scenario schema: liveness200/readiness503 -> same-process readiness200"
        } catch {
            $assertionFailure = $_.Exception
            throw
        } finally {
            $cleanupFailures = [Collections.Generic.List[Exception]]::new()
            foreach ($hostEntry in $ownedHosts) {
                try {
                    if (-not $hostEntry.Process.HasExited) {
                        $hostEntry.Process.Kill()
                        if (-not $hostEntry.Process.WaitForExit(5000)) { throw "Could not stop owned startup process $($hostEntry.Process.Id)." }
                    }
                    $hostEntry.Process.Dispose()
                } catch { $cleanupFailures.Add($_.Exception) }
            }
            if ($fixtureName) {
                try {
                    & dotnet $FixtureDll drop $fixtureName
                    if ($LASTEXITCODE -ne 0) { throw 'Owned startup database cleanup failed.' }
                } catch { $cleanupFailures.Add($_.Exception) }
            }
            if ($cleanupFailures.Count -gt 0) {
                if ($assertionFailure) { $cleanupFailures.Insert(0, $assertionFailure) }
                throw [AggregateException]::new('Startup fixture assertion or cleanup failed.', $cleanupFailures)
            }
        }
    }
} finally {
    foreach ($name in $originalEnvironment.Keys) { Set-StartupEnvironment $name $originalEnvironment[$name] }
}
