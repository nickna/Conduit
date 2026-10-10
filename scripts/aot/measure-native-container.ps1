[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('admin', 'gateway')]
    [string] $Service,

    [Parameter(Mandatory)]
    [string] $JitImage,

    [Parameter(Mandatory)]
    [string] $NativeImage,

    [Parameter(Mandatory)]
    [string] $DatabaseUrl,

    [Parameter(Mandatory)]
    [string] $RedisUrl,

    [int] $Requests = 1000,
    [int] $Concurrency = 20,
    [string] $NativeBaselinePath,
    [string] $OutputPath,
    [int] $Port = 18080
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = "artifacts/native-benchmark/$Service.json"
}

function ConvertTo-ContainerUrl([string] $value) {
    return $value -replace '(?i)(localhost|127\.0\.0\.1)', 'host.docker.internal'
}

function ConvertTo-Bytes([string] $value) {
    if ($value -notmatch '^\s*([0-9.]+)\s*([KMGT]?i?B)') { return 0L }
    $factor = switch ($Matches[2].ToUpperInvariant()) {
        'KB' { 1KB }; 'KIB' { 1KB }; 'MB' { 1MB }; 'MIB' { 1MB }
        'GB' { 1GB }; 'GIB' { 1GB }; 'TB' { 1TB }; 'TIB' { 1TB }
        default { 1 }
    }
    return [long]([double]$Matches[1] * $factor)
}

function Get-ContainerMemory([string] $name) {
    $usage = docker stats $name --no-stream --format '{{.MemUsage}}'
    if ($LASTEXITCODE -ne 0) { throw "Unable to sample memory for '$name'." }
    return ConvertTo-Bytes (($usage -split '/')[0])
}

function Get-ImageSize([string] $image) {
    $value = docker image inspect $image --format '{{.Size}}'
    if ($LASTEXITCODE -ne 0) { throw "Unable to inspect '$image'." }
    return [long]$value
}

function Get-PullSize([string] $image) {
    $manifestText = docker manifest inspect $image 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    $manifest = $manifestText | ConvertFrom-Json
    if ($null -eq $manifest.layers) { return $null }
    return [long](($manifest.layers | Measure-Object -Property size -Sum).Sum)
}

function Invoke-Probe([string] $url, [int] $requestCount, [int] $workerCount, [string] $sampleContainer) {
    $stdout = Join-Path ([System.IO.Path]::GetTempPath()) "conduit-probe-$([guid]::NewGuid().ToString('N')).json"
    $stderr = "$stdout.err"
    try {
        $arguments = @(
            'run', '--project', 'tools/ConduitLLM.HealthProbe', '--configuration', 'Release', '--no-build', '--',
            $url, '--requests', $requestCount, '--concurrency', $workerCount, '--timeout-seconds', '30'
        )
        $process = Start-Process dotnet -ArgumentList $arguments -NoNewWindow -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $samples = [System.Collections.Generic.List[long]]::new()
        while (-not $process.HasExited) {
            $samples.Add((Get-ContainerMemory $sampleContainer))
            Start-Sleep -Milliseconds 200
            $process.Refresh()
        }
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "Load probe failed: $(Get-Content -LiteralPath $stderr -Raw)"
        }
        $lastLine = Get-Content -LiteralPath $stdout | Select-Object -Last 1
        return [pscustomobject]@{
            result = $lastLine | ConvertFrom-Json
            memorySamples = @($samples)
        }
    }
    finally {
        Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
    }
}

function Measure-Variant([string] $runtime, [string] $image, [int] $hostPort) {
    $name = "conduit-$Service-$runtime-benchmark-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    # The port is explicitly bound to IPv4 below. Using localhost can resolve to ::1
    # first on Windows and make a healthy container appear to time out.
    $healthUrl = "http://127.0.0.1:$hostPort/health/ready"
    $started = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        docker run --detach --name $name --add-host host.docker.internal:host-gateway `
            --publish "127.0.0.1:${hostPort}:8080" `
            --env "DATABASE_URL=$(ConvertTo-ContainerUrl $DatabaseUrl)" `
            --env "REDIS_URL=$(ConvertTo-ContainerUrl $RedisUrl)" `
            --env 'CONDUIT_MIGRATION_MODE=Skip' `
            --env 'ConduitLLM__Messaging__Wolverine__AutoProvision=false' `
            $image | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Unable to start '$image'." }

        $ready = $false
        while ($started.Elapsed -lt [TimeSpan]::FromMinutes(2)) {
            try {
                $response = Invoke-WebRequest -Uri $healthUrl -TimeoutSec 2 -SkipHttpErrorCheck
                if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) { $ready = $true; break }
            }
            catch { }
            Start-Sleep -Milliseconds 200
        }
        if (-not $ready) {
            $logs = docker logs $name 2>&1 | Select-Object -Last 40
            throw "'$image' did not become ready.`n$($logs -join "`n")"
        }
        $started.Stop()

        # Warm connections before recording load latency and memory.
        Invoke-Probe $healthUrl ([Math]::Min(100, $Requests)) ([Math]::Min(10, $Concurrency)) $name | Out-Null
        $idle = Get-ContainerMemory $name
        $load = Invoke-Probe $healthUrl $Requests $Concurrency $name
        $steady = Get-ContainerMemory $name
        $allMemory = @($idle, $steady) + @($load.memorySamples)

        return [pscustomobject]@{
            runtime = $runtime
            image = $image
            imageBytes = Get-ImageSize $image
            pullBytes = Get-PullSize $image
            coldReadinessMilliseconds = [Math]::Round($started.Elapsed.TotalMilliseconds, 3)
            idleMemoryBytes = $idle
            steadyStateMemoryBytes = $steady
            peakMemoryBytes = [long](($allMemory | Measure-Object -Maximum).Maximum)
            requests = $load.result.requests
            concurrency = $load.result.concurrency
            failures = $load.result.failures
            throughputPerSecond = $load.result.throughputPerSecond
            p50Milliseconds = $load.result.p50Milliseconds
            p95Milliseconds = $load.result.p95Milliseconds
            p99Milliseconds = $load.result.p99Milliseconds
        }
    }
    finally {
        docker rm --force $name 2>$null | Out-Null
    }
}

function Get-Reduction([double] $jit, [double] $native) {
    if ($jit -eq 0) { return $null }
    return [Math]::Round((($jit - $native) / $jit) * 100, 3)
}
function Get-Increase([double] $jit, [double] $native) {
    if ($jit -eq 0) { return $null }
    return [Math]::Round((($native - $jit) / $jit) * 100, 3)
}

dotnet build tools/ConduitLLM.HealthProbe --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Health/load probe failed to build.' }

$jit = Measure-Variant 'jit' $JitImage $Port
$native = Measure-Variant 'native-aot' $NativeImage ($Port + 1)
$nativePublish = $null
if (-not [string]::IsNullOrWhiteSpace($NativeBaselinePath)) {
    $baselines = Get-Content -LiteralPath $NativeBaselinePath -Raw | ConvertFrom-Json
    $baselineName = if ($Service -eq 'admin') { 'Admin' } else { 'Gateway' }
    $nativePublish = $baselines.services | Where-Object service -eq $baselineName | Select-Object -First 1
}

$report = [pscustomobject]@{
    schemaVersion = 1
    generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    service = $Service
    platform = 'linux-x64'
    endpoint = '/health/ready'
    methodology = 'Sequential cold containers; warmed HTTP connections; memory sampled during measured load.'
    jit = $jit
    native = $native
    nativePublish = $nativePublish
    comparison = [pscustomobject]@{
        imageSizeReductionPercent = Get-Reduction $jit.imageBytes $native.imageBytes
        pullSizeReductionPercent = if ($null -ne $jit.pullBytes -and $null -ne $native.pullBytes) { Get-Reduction $jit.pullBytes $native.pullBytes } else { $null }
        coldStartReductionPercent = Get-Reduction $jit.coldReadinessMilliseconds $native.coldReadinessMilliseconds
        idleMemoryReductionPercent = Get-Reduction $jit.idleMemoryBytes $native.idleMemoryBytes
        steadyStateMemoryReductionPercent = Get-Reduction $jit.steadyStateMemoryBytes $native.steadyStateMemoryBytes
        peakMemoryReductionPercent = Get-Reduction $jit.peakMemoryBytes $native.peakMemoryBytes
        throughputImprovementPercent = Get-Increase $jit.throughputPerSecond $native.throughputPerSecond
        p50LatencyRegressionPercent = Get-Increase $jit.p50Milliseconds $native.p50Milliseconds
        p95LatencyRegressionPercent = Get-Increase $jit.p95Milliseconds $native.p95Milliseconds
        p99LatencyRegressionPercent = Get-Increase $jit.p99Milliseconds $native.p99Milliseconds
    }
}

$parent = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath
Write-Host "Wrote JIT versus NativeAOT benchmark to $OutputPath"
