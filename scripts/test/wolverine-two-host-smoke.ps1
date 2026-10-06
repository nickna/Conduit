#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Two-host Wolverine listener-assignment smoke (#961).

.DESCRIPTION
    Boots the real Admin API and Gateway API hosts against a real PostgreSQL database
    with ConduitLLM:Messaging:Backend=Wolverine and asserts the multi-node agent
    topology that the in-memory dual-backend tests (#928) cannot see:

      1. Each service forms its OWN single-node agent cluster (per-service durability
         schema: wolverine_conduit_gateway / wolverine_conduit_admin). A shared cluster
         is defect W1 (#929): when the Admin node wins leadership it cannot assign the
         Gateway-only exclusive listeners and the spend/image queues go dead.
      2. The Gateway's exclusive strict-ordered listener agents
         (wolverine-listener://postgresql/spend-update-events and
         .../image-generation-events) are actually assigned and started.
      3. The Admin node is never assigned a queue listener agent it does not declare.
      4. The Gateway starts message listening on all five queues (gateway-events,
         webhook-delivery, video-generation-events, spend-update-events,
         image-generation-events).
      5. Representative spend, batch, image, and video messages reach the committed
         static handler adapters through the real PostgreSQL queues. In the native
         supported-boundary run, spend stops at the explicitly excluded EF query data
         plane after the adapter has invoked it.
      6. Neither host logs agent-assignment, service-location, or static-code-loading
         failures (including a fallback handler scan).

    The Admin host is started FIRST deliberately — that recreates the W1 leadership
    scenario (the Admin winning the election) which is only dangerous if the durability
    schemas ever regress to being shared.

.PARAMETER DatabaseUrl
    PostgreSQL URL for both hosts. Defaults to $env:DATABASE_URL.

.PARAMETER RedisUrl
    Redis URL for both hosts. Defaults to $env:REDIS_URL.

.PARAMETER PsqlCommand
    Command used to reach psql for the SQL assertions. Defaults to `psql <DatabaseUrl>`
    (works on GitHub runners, where the postgres client is preinstalled). For a local
    dev run against a Docker postgres, pass e.g.:
        -PsqlCommand 'docker exec my-postgres psql -U conduit -d conduitdb'
    NOTE: tokens are split on spaces, so paths/arguments with spaces are not supported.

.PARAMETER NoBuild
    Skip `dotnet build` of the two service projects (CI builds them beforehand).

.PARAMETER Configuration
    Build configuration of the service outputs (default Debug).

.PARAMETER NativeArtifactDirectory
    Root produced by scripts/aot/publish-native.ps1. When set, launches the
    published native Admin and Gateway executables instead of framework-dependent DLLs.

.EXAMPLE
    # CI (after `dotnet build` of both services):
    ./scripts/test/wolverine-two-host-smoke.ps1 -NoBuild

.EXAMPLE
    # Local, against throwaway containers:
    docker run -d --name pg961 -e POSTGRES_USER=conduit -e POSTGRES_PASSWORD=conduitpass \
        -e POSTGRES_DB=conduitdb -p 15432:5432 postgres:16
    docker run -d --name redis961 -p 16379:6379 redis:7-alpine
    ./scripts/test/wolverine-two-host-smoke.ps1 `
        -DatabaseUrl 'postgresql://conduit:conduitpass@localhost:15432/conduitdb' `
        -RedisUrl 'redis://localhost:16379' `
        -PsqlCommand 'docker exec pg961 psql -U conduit -d conduitdb'
#>
[CmdletBinding()]
param(
    [string]$DatabaseUrl = $env:DATABASE_URL,
    [string]$RedisUrl = $env:REDIS_URL,
    [string]$PsqlCommand,
    [switch]$NoBuild,
    [string]$Configuration = 'Debug',
    [string]$NativeArtifactDirectory,
    [int]$AdminPort = 15002,
    [int]$GatewayPort = 15000,
    [int]$TimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'

if (-not $DatabaseUrl) { Write-Error 'DatabaseUrl not set (pass -DatabaseUrl or set DATABASE_URL).' }
if ($NativeArtifactDirectory -and -not $RedisUrl) {
    Write-Error 'RedisUrl not set (pass -RedisUrl or set REDIS_URL) for the native two-host smoke.'
}
if (-not $PsqlCommand) { $PsqlCommand = "psql $DatabaseUrl" }

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..' '..')
$adminProject = Join-Path $repoRoot 'Services' 'ConduitLLM.Admin'
$gatewayProject = Join-Path $repoRoot 'Services' 'ConduitLLM.Gateway'
$publisherProject = Join-Path $repoRoot 'tools' 'WolverineSmokePublisher'
$migratorProject = Join-Path $repoRoot 'tools' 'ConduitLLM.Migrator'
$logDir = Join-Path ([System.IO.Path]::GetTempPath()) "wolverine-two-host-$PID"
New-Item -ItemType Directory -Force $logDir | Out-Null

$script:failures = [System.Collections.Generic.List[string]]::new()

function Invoke-Sql([string]$Sql) {
    $tokens = $PsqlCommand -split ' '
    $rest = if ($tokens.Length -gt 1) { $tokens[1..($tokens.Length - 1)] } else { @() }
    $result = & $tokens[0] @($rest) -tAc $Sql 2>&1
    if ($LASTEXITCODE -ne 0) { throw "psql failed for [$Sql]: $result" }
    return ($result | Out-String).Trim()
}

function Assert([bool]$Condition, [string]$Name) {
    if ($Condition) {
        Write-Host "  PASS  $Name" -ForegroundColor Green
    } else {
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        $script:failures.Add($Name)
    }
}

function Wait-ForSql([string]$Sql, [string]$Expected, [string]$What) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            if ((Invoke-Sql $Sql) -eq $Expected) { return $true }
        } catch {
            # schema/tables may not exist yet while the host is still provisioning
        }
        Start-Sleep -Seconds 3
    }
    Write-Host "  timed out waiting for: $What" -ForegroundColor Yellow
    return $false
}

function Wait-ForDeliveryProbes([string]$Name, [System.Collections.IDictionary]$Patterns) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $log = Get-HostLog $Name
        $missing = @($Patterns.GetEnumerator() | Where-Object { $log -notmatch $_.Value })
        if ($missing.Count -eq 0) { return $log }
        Start-Sleep -Milliseconds 500
    }

    $missingNames = @($Patterns.GetEnumerator() |
        Where-Object { $log -notmatch $_.Value } |
        ForEach-Object Key)
    Write-Host "  timed out waiting for delivery probes: $($missingNames -join ', ')" -ForegroundColor Yellow
    return $log
}

function Start-ServiceHost([string]$Project, [string]$Name, [int]$Port) {
    if ($NativeArtifactDirectory) {
        $serviceSlug = $Name.Replace('ConduitLLM.', '').ToLowerInvariant()
        $executableName = if ($IsWindows) { "$Name.exe" } else { $Name }
        $hostPath = Join-Path $NativeArtifactDirectory 'runtime' $serviceSlug $executableName
        $hostArguments = @()
    } else {
        $hostPath = Join-Path $Project 'bin' $Configuration 'net10.0' "$Name.dll"
        $hostArguments = @($hostPath)
    }
    if (-not (Test-Path $hostPath)) { throw "Host output not found: $hostPath (build/publish first)" }
    $out = Join-Path $logDir "$Name.out.log"
    $err = Join-Path $logDir "$Name.err.log"
    $env:ASPNETCORE_URLS = "http://localhost:$Port"
    $startArgs = @{
        FilePath = if ($NativeArtifactDirectory) { $hostPath } else { 'dotnet' }
        ArgumentList = $hostArguments
        WorkingDirectory = (Split-Path $hostPath)
        RedirectStandardOutput = $out
        RedirectStandardError = $err
        PassThru = $true
    }
    if ($IsWindows) {
        $startArgs.WindowStyle = 'Hidden'
    } else {
        $startArgs.NoNewWindow = $true
    }
    $proc = Start-Process @startArgs
    Write-Host "Started $Name (pid $($proc.Id)) on port $Port; logs: $out"
    return $proc
}

function Get-HostLog([string]$Name) {
    $text = ''
    foreach ($suffix in 'out', 'err') {
        $file = Join-Path $logDir "$Name.$suffix.log"
        if (Test-Path $file) { $text += (Get-Content -Raw $file) }
    }
    return $text
}

# Environment shared by both hosts (ASPNETCORE_URLS is set per host).
$env:DATABASE_URL = $DatabaseUrl
if ($RedisUrl) { $env:REDIS_URL = $RedisUrl }
$env:ConduitLLM__Messaging__Backend = 'Wolverine'
$env:CONDUIT_MIGRATION_MODE = 'Skip'   # migrations are applied by the standalone executable below
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:CONDUIT_ENABLE_HTTPS_REDIRECTION = 'false'
if ($IsWindows) { $env:Logging__EventLog__LogLevel__Default = 'None' }

if (-not $NoBuild) {
    if (-not $NativeArtifactDirectory) {
        Write-Host '== Building Gateway + Admin =='
        dotnet build $gatewayProject -c $Configuration
        if ($LASTEXITCODE -ne 0) { exit 1 }
        dotnet build $adminProject -c $Configuration
        if ($LASTEXITCODE -ne 0) { exit 1 }
    }
    dotnet build $publisherProject -c $Configuration
    if ($LASTEXITCODE -ne 0) { exit 1 }
    dotnet build $migratorProject -c $Configuration
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

Write-Host '== Applying EF migrations (standalone migrator) =='
dotnet (Join-Path $migratorProject 'bin' $Configuration 'net10.0' 'ConduitLLM.Migrator.dll')
if ($LASTEXITCODE -ne 0) { Write-Error 'standalone migrator failed' }

# Wolverine retains node records briefly after an ungraceful shutdown and may prune
# them during the next startup. Use the database clock to identify this run's nodes
# without depending on either cleanup timing or the workstation clock.
$nodeRegistrationStartedAt = (Invoke-Sql 'SELECT clock_timestamp()').Replace("'", "''")
$adminNodeCountSql = "SELECT count(*) FROM wolverine_conduit_admin.wolverine_nodes WHERE started >= '$nodeRegistrationStartedAt'::timestamptz"
$gatewayNodeCountSql = "SELECT count(*) FROM wolverine_conduit_gateway.wolverine_nodes WHERE started >= '$nodeRegistrationStartedAt'::timestamptz"

$admin = $null
$gateway = $null
$exitCode = 1
try {
    # Admin FIRST: recreates the W1 leadership scenario (see .DESCRIPTION).
    Write-Host '== Booting Admin API (Wolverine backend) =='
    $admin = Start-ServiceHost $adminProject 'ConduitLLM.Admin' $AdminPort
    $adminUp = Wait-ForSql $adminNodeCountSql '1' 'Admin node registration'

    Write-Host '== Booting Gateway API (Wolverine backend) =='
    $gateway = Start-ServiceHost $gatewayProject 'ConduitLLM.Gateway' $GatewayPort
    $gatewayUp = Wait-ForSql @'
SELECT CASE WHEN count(*) >= 2 THEN 1 ELSE 0 END FROM wolverine_conduit_gateway.wolverine_node_assignments
 WHERE id IN ('wolverine-listener://postgresql/spend-update-events',
              'wolverine-listener://postgresql/image-generation-events')
   AND started IS NOT NULL
'@ '1' 'Gateway exclusive listener assignments'

    Write-Host '== Publishing representative delivery probes =='
    $probeId = [Guid]::NewGuid().ToString('N')
    dotnet (Join-Path $publisherProject 'bin' $Configuration 'net10.0' 'WolverineSmokePublisher.dll') $probeId
    if ($LASTEXITCODE -ne 0) { Write-Error 'Wolverine delivery-probe publisher failed' }

    $spendPattern = if ($NativeArtifactDirectory) {
        'SpendUpdateRequestedHandler1733208880\.<HandleAsync>'
    } else {
        'Spend update request for non-existent virtual key 2147483647'
    }
    $deliveryPatterns = [ordered]@{
        Spend = $spendPattern
        Batch = "Processing batch spend flush request static-codegen-batch-$probeId"
        Image = "Received cancellation request for image generation task static-codegen-image-$probeId"
        Video = "Received cancellation request for video generation task static-codegen-video-$probeId"
    }
    $deliveryLog = Wait-ForDeliveryProbes 'ConduitLLM.Gateway' $deliveryPatterns
    $spendDelivered = $deliveryLog -match $deliveryPatterns.Spend
    $batchDelivered = $deliveryLog -match $deliveryPatterns.Batch
    $imageDelivered = $deliveryLog -match $deliveryPatterns.Image
    $videoDelivered = $deliveryLog -match $deliveryPatterns.Video

    Write-Host '== Assertions =='
    Assert $adminUp 'Admin registers a Wolverine node'
    Assert $gatewayUp 'Gateway exclusive listeners (spend-update-events, image-generation-events) assigned and started'

    # (1) Separate single-node clusters — the W1 guard.
    Assert ((Invoke-Sql $adminNodeCountSql) -eq '1') `
        'Admin durability schema holds exactly its own node (no shared cluster)'
    Assert ((Invoke-Sql $gatewayNodeCountSql) -eq '1') `
        'Gateway durability schema holds exactly its own node (no shared cluster)'

    # (3) The Admin cluster must never be assigned queue listener agents.
    Assert ((Invoke-Sql "SELECT count(*) FROM wolverine_conduit_admin.wolverine_node_assignments WHERE id LIKE 'wolverine-listener://%'") -eq '0') `
        'Admin node has no queue listener agents assigned'

    # (4) Gateway listens on all five queues (log wording from Wolverine.Transports.ListeningAgent).
    $gatewayLog = Get-HostLog 'ConduitLLM.Gateway'
    foreach ($queue in 'gateway_events', 'webhook_delivery', 'video_generation_events', 'spend_update_events', 'image_generation_events') {
        Assert ($gatewayLog -match [regex]::Escape("Started message listening at postgresql://$queue/")) `
            "Gateway listening on $queue"
    }

    # (5) Representative messages traverse each generated handler path.
    $spendAssertion = if ($NativeArtifactDirectory) {
        'SpendUpdateRequested reached its static handler adapter before the excluded EF query plane'
    } else {
        'SpendUpdateRequested delivered through its static handler adapter'
    }
    Assert $spendDelivered $spendAssertion
    Assert $batchDelivered 'BatchSpendFlushRequestedEvent delivered through its static handler adapter'
    Assert $imageDelivered 'ImageGenerationCancelled delivered through its static handler adapter'
    Assert $videoDelivered 'VideoGenerationCancelled delivered through its static handler adapter'

    # (6) No agent-assignment, service-location, or static-code-loading failures.
    $adminLog = Get-HostLog 'ConduitLLM.Admin'
    foreach ($bad in 'InvalidAgentException',
                      'InvalidServiceLocationException',
                      'ExpectedTypeMissingException',
                      'falling back to a runtime assembly scan') {
        Assert (-not ($gatewayLog -match $bad)) "Gateway log free of $bad"
        Assert (-not ($adminLog -match $bad)) "Admin log free of $bad"
    }

    if ($script:failures.Count -eq 0) {
        Write-Host "`nWolverine two-host smoke: PASS" -ForegroundColor Green
        $exitCode = 0
    } else {
        Write-Host "`nWolverine two-host smoke: FAIL ($($script:failures.Count) assertion(s))" -ForegroundColor Red
        Write-Host "`n===== Gateway log ====="
        Get-HostLog 'ConduitLLM.Gateway' | Write-Host
        Write-Host "`n===== Admin log ====="
        Get-HostLog 'ConduitLLM.Admin' | Write-Host
    }
}
finally {
    foreach ($proc in $gateway, $admin) {
        if ($proc -and -not $proc.HasExited) {
            try { Stop-Process -Id $proc.Id -Force -Confirm:$false } catch {}
        }
    }
}

exit $exitCode
