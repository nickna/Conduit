param([string]$BaselineRoot = "artifacts/webhook-baseline", [string]$OutputDirectory = "artifacts/webhook-benchmark")
$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
Set-Location $repoRoot
$baselinePath = [IO.Path]::GetFullPath((Join-Path $repoRoot $BaselineRoot))
$outputPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
if (!(Test-Path (Join-Path $baselinePath "Conduit.slnx"))) { throw "Extract baseline 7b3a471d into $baselinePath first (git archive)." }
$toolRelative = "tools/ConduitLLM.WebhookBenchmark"
New-Item -ItemType Directory -Force -Path (Join-Path $baselinePath $toolRelative) | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot "$toolRelative/Program.cs"), (Join-Path $repoRoot "$toolRelative/ConduitLLM.WebhookBenchmark.csproj") -Destination (Join-Path $baselinePath $toolRelative)
& dotnet build "$baselinePath/$toolRelative/ConduitLLM.WebhookBenchmark.csproj" -c Release -p:WebhookBenchmarkBaseline=true -m:2
if ($LASTEXITCODE) { throw "Baseline build failed" }
& dotnet build "$repoRoot/$toolRelative/ConduitLLM.WebhookBenchmark.csproj" -c Release -m:2
if ($LASTEXITCODE) { throw "Current build failed" }
$containerName = "conduit-webhook-bench-" + [Guid]::NewGuid().ToString("N")
$oldConnection = $env:CONDUIT_WEBHOOK_BENCH_POSTGRES
try {
    & docker run -d --name $containerName --cpus 2 --memory 2g -e POSTGRES_DB=conduit_webhook_bench -e POSTGRES_USER=conduit -e POSTGRES_PASSWORD=benchmark-only -p 127.0.0.1::5432 postgres:17-alpine -c shared_preload_libraries=pg_stat_statements -c track_io_timing=on
    if ($LASTEXITCODE) { throw "Benchmark PostgreSQL failed to start" }
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        & docker exec $containerName pg_isready -U conduit -d conduit_webhook_bench *> $null
        if (!$LASTEXITCODE) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (!$ready) { throw "PostgreSQL readiness timeout" }
    & docker exec $containerName psql -U conduit -d conduit_webhook_bench -c 'CREATE EXTENSION pg_stat_statements'
    if ($LASTEXITCODE) { throw "pg_stat_statements setup failed" }
    $port = (& docker inspect --format '{{(index (index .NetworkSettings.Ports "5432/tcp") 0).HostPort}}' $containerName).Trim()
    $env:CONDUIT_WEBHOOK_BENCH_POSTGRES = "Host=127.0.0.1;Port=$port;Database=conduit_webhook_bench;Username=conduit;Password=benchmark-only;Application Name=webhook-benchmark"
    [ordered]@{ utc = [DateTime]::UtcNow.ToString("O"); os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription;
        logicalProcessors = [Environment]::ProcessorCount; dotnet = (& dotnet --version); databaseImage = "postgres:17-alpine";
        databaseCpuLimit = 2; databaseMemoryGiB = 2; payloadPaddingBytes = 1024; offeredEvents = 200; warmupEvents = 20;
        observationSeconds = 10; queueConcurrencyPerHost = 75; scheduledPollingMilliseconds = 250; redis = "absent";
        hostModel = "one or two Wolverine hosts in one process; shared receiver; same machine" } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputPath "environment.json") -Encoding utf8
    & dotnet "$baselinePath/$toolRelative/bin/Release/net10.0/ConduitLLM.WebhookBenchmark.dll" (Join-Path $outputPath "baseline.json")
    if ($LASTEXITCODE) { throw "Baseline benchmark failed" }
    & dotnet "$repoRoot/$toolRelative/bin/Release/net10.0/ConduitLLM.WebhookBenchmark.dll" (Join-Path $outputPath "current.json")
    if ($LASTEXITCODE) { throw "Current benchmark failed" }
    $measurements = Get-Content -LiteralPath (Join-Path $outputPath 'current.json') -Raw | ConvertFrom-Json
    foreach ($case in $measurements) {
        $targetCount = if ($case.mixed) { 40 } else { 200 }
        if ($case.delivered -ne $targetCount -or $null -eq $case.healthyDrainMilliseconds -or $case.healthyDrainMilliseconds -gt 2000) {
            throw "Healthy completion gate failed (instances=$($case.instances), mixed=$($case.mixed)). Inspect current.json."
        }
        if (!$case.mixed -and ($case.posts -ne 200 -or $case.usefulPerSecond -lt 19.5 -or $case.endToEndMs.p95 -gt 1500)) {
            throw "Healthy throughput/latency/amplification gate failed. Inspect current.json."
        }
        if ($case.mixed -and $case.posts -gt 100) { throw "Mixed-outage POST bound failed. Inspect current.json." }
    }
} finally {
    $env:CONDUIT_WEBHOOK_BENCH_POSTGRES = $oldConnection
    # Remove only the disposable container named by this invocation.
    & docker rm -f $containerName *> $null
}
