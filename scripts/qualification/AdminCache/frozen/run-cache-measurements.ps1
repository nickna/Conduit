[CmdletBinding()]
param(
    [ValidateRange(1, 10)][int] $Runs = 5,
    [string] $Redis = '127.0.0.1:16468',
    [ValidateRange(1, 10000)][int] $Warmups = 100,
    [ValidateRange(1, 10000)][int] $Samples = 1000,
    [string] $EvidenceDirectory = (Join-Path $PSScriptRoot 'measurements'),
    [ValidateRange(10, 600)][int] $ProcessTimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$taskEvidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $taskEvidenceRoot) { throw 'Use a fresh evidence directory. Prior captures must remain immutable.' }
New-Item -ItemType Directory -Path $taskEvidenceRoot | Out-Null
$taskProject = Join-Path $PSScriptRoot 'project/Conduit.CacheMeasurements.csproj'
$taskDll = Join-Path $PSScriptRoot 'project/bin/Release/net10.0/Conduit.CacheMeasurements.dll'
if (-not (Test-Path -LiteralPath $taskDll)) { throw 'Build the artifact project in Release before requesting a quiet measurement window.' }
$taskRuntime = (& dotnet --list-runtimes | Out-String).Trim()
$taskPackageAssets = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'project/obj/project.assets.json') -Raw | ConvertFrom-Json
$taskManifest = [ordered]@{
    schemaVersion = 1
    sourceRevision = 'f68cb5fae1130bbe1ef4fc30aca7a3266094dbaa'
    startedAtUtc = [DateTime]::UtcNow.ToString('O')
    sdk = (& dotnet --version | Out-String).Trim()
    installedRuntimes = $taskRuntime
    warmupsPerStatePerProcess = $Warmups
    measuredPerStatePerProcess = $Samples
    pairedWriterReaderInvocations = $Runs
    instrumentation = 'Process-wide GC allocation deltas per cache operation and Stopwatch timings. Per-operation deep payload/ownership validation is outside the interval. No server/HTTP tracing or PostgreSQL operation is measured.'
    lifetime = 'Each invocation starts a writer process and waits for its successful exit before starting a fresh reader process with the same unique environment; all payload cache entries must be reconstructed from Redis with forbidden business loaders.'
    ttlPolicy = 'Production TTLs retained. Writer measures cold then immediate L1 on each unique key. Every measured cold-start to L1-end interval must be below80ms, conservatively below the100ms mapping/pricing L1 cap.'
    healthyRedisTimeout = '00:00:01, documented supported healthy-read ceiling, explicit fixture configuration'
    processTimeoutSeconds = $ProcessTimeoutSeconds
    command = 'dotnet Conduit.CacheMeasurements.dll write|read (environment contract in command-contract.json)'
    compilerFlags = 'Release; net10.0; reflection JSON disabled; -m:1 -nr:false -p:BuildProjectReferences=false'
    files = @(
        [ordered]@{ path = 'project/Program.cs'; sha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'project/Program.cs') -Algorithm SHA256).Hash.ToLowerInvariant() },
        [ordered]@{ path = 'project/Conduit.CacheMeasurements.csproj'; sha256 = (Get-FileHash -LiteralPath $taskProject -Algorithm SHA256).Hash.ToLowerInvariant() },
        [ordered]@{ path = 'project/bin/Release/net10.0/Conduit.CacheMeasurements.dll'; sha256 = (Get-FileHash -LiteralPath $taskDll -Algorithm SHA256).Hash.ToLowerInvariant() },
        [ordered]@{ path = 'run-cache-measurements.ps1'; sha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant() }
    )
    packages = @($taskPackageAssets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | Sort-Object Name | ForEach-Object { $_.Name })
    invocations = @()
}
$taskManifestPath = Join-Path $taskEvidenceRoot 'manifest.json'
$taskManifest | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $taskManifestPath -Encoding utf8

for ($taskRun = 1; $taskRun -le $Runs; $taskRun++) {
    $taskEnvironment = 'cache-ext-' + [Guid]::NewGuid().ToString('N')
    foreach ($taskMode in @('write', 'read')) {
        $taskPrefix = "run-$taskRun-$taskMode"
        $taskOutput = Join-Path $taskEvidenceRoot "$taskPrefix.json"
        $taskInfo = [Diagnostics.ProcessStartInfo]::new()
        $taskInfo.FileName = (Get-Command dotnet).Source
        $taskInfo.UseShellExecute = $false
        $taskInfo.CreateNoWindow = $true
        $taskInfo.RedirectStandardOutput = $true
        $taskInfo.RedirectStandardError = $true
        $taskInfo.WorkingDirectory = $PSScriptRoot
        $taskInfo.ArgumentList.Add($taskDll)
        $taskInfo.ArgumentList.Add($taskMode)
        $taskInfo.Environment['CONDUIT_EXTENDED_CACHE_REDIS'] = $Redis
        $taskInfo.Environment['CONDUIT_EXTENDED_CACHE_ENVIRONMENT'] = $taskEnvironment
        $taskInfo.Environment['CONDUIT_EXTENDED_CACHE_OUTPUT'] = $taskOutput
        $taskInfo.Environment['CONDUIT_EXTENDED_CACHE_WARMUPS'] = [string]$Warmups
        $taskInfo.Environment['CONDUIT_EXTENDED_CACHE_SAMPLES'] = [string]$Samples
        $taskProcess = [Diagnostics.Process]::new()
        $taskProcess.StartInfo = $taskInfo
        $taskWatch = [Diagnostics.Stopwatch]::StartNew()
        try {
            if (-not $taskProcess.Start()) { throw 'Cache measurement process did not start.' }
            $taskStdout = $taskProcess.StandardOutput.ReadToEndAsync()
            $taskStderr = $taskProcess.StandardError.ReadToEndAsync()
            if (-not $taskProcess.WaitForExit($ProcessTimeoutSeconds * 1000)) {
                $taskProcess.Kill($true)
                $taskProcess.WaitForExit()
                throw 'Cache measurement process deadline exceeded; retain failed capture.'
            }
            $taskWatch.Stop()
            $taskStdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $taskEvidenceRoot "$taskPrefix.stdout.log") -Encoding utf8
            $taskStderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $taskEvidenceRoot "$taskPrefix.stderr.log") -Encoding utf8
            $taskEntry = [ordered]@{ run = $taskRun; mode = $taskMode; environment = $taskEnvironment; processId = $taskProcess.Id; exitCode = $taskProcess.ExitCode; elapsedMilliseconds = $taskWatch.Elapsed.TotalMilliseconds; report = "$taskPrefix.json" }
            $taskManifest.invocations += $taskEntry
            $taskManifest | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $taskManifestPath -Encoding utf8
            if ($taskProcess.ExitCode -ne 0) { throw "Cache $taskPrefix failed; inspect retained report/logs." }
            $taskReport = Get-Content -LiteralPath $taskOutput -Raw | ConvertFrom-Json
            if (-not $taskReport.Succeeded -or $taskReport.MeasuredPerState -ne $Samples -or $taskReport.Warmups -ne $Warmups) { throw 'Cache report/count validation failed.' }
            if ($taskReport.Rows.Count -ne $(if ($taskMode -eq 'write') { 6 } else { 3 })) { throw 'Cache state inventory incomplete.' }
            foreach ($taskRow in $taskReport.Rows) {
                if ($taskRow.Samples.Count -ne $Samples -or $taskRow.Samples.Where({$null -eq $_}).Count -ne 0) { throw 'Missing cache samples.' }
            }
            Write-Output "PASS ${taskPrefix}: $($taskReport.Rows.Count) states, $Samples measured/state, $Warmups warmups/state"
        }
        finally { $taskProcess.Dispose() }
    }
}
$taskRows = foreach ($taskEntry in $taskManifest.invocations) { (Get-Content -LiteralPath (Join-Path $taskEvidenceRoot $taskEntry.report) -Raw | ConvertFrom-Json).Rows }
function Get-Median($taskValues) {
    $taskSorted = @($taskValues | Sort-Object)
    return $taskSorted[[Math]::Floor($taskSorted.Count / 2)]
}
$taskSummaryRows = foreach ($taskGroup in ($taskRows | Group-Object Domain, State)) {
    $taskFirst = $taskGroup.Group[0]
    [ordered]@{
        domain = $taskFirst.Domain
        state = $taskFirst.State
        runs = $taskGroup.Count
        measuredSamples = ($taskGroup.Group.Samples.Count | Measure-Object -Sum).Sum
        loaderCallsMeasured = ($taskGroup.Group.BusinessLoaderCalls | Measure-Object -Sum).Sum
        medianOfPerRunLatencyP50Us = Get-Median $taskGroup.Group.LatencyMicroseconds.P50
        medianOfPerRunLatencyP95Us = Get-Median $taskGroup.Group.LatencyMicroseconds.P95
        medianOfPerRunLatencyP99Us = Get-Median $taskGroup.Group.LatencyMicroseconds.P99
        medianOfPerRunAllocatedBytesP50 = Get-Median $taskGroup.Group.ProcessWideAllocatedBytes.P50
        medianOfPerRunAllocatedBytesP95 = Get-Median $taskGroup.Group.ProcessWideAllocatedBytes.P95
        medianOfPerRunAllocatedBytesP99 = Get-Median $taskGroup.Group.ProcessWideAllocatedBytes.P99
        maxColdToL1EndMilliseconds = ($taskGroup.Group.MaximumColdToL1EndMilliseconds | Measure-Object -Maximum).Maximum
    }
}
$taskSummary = [ordered]@{ schemaVersion = 1; sourceRevision = $taskManifest.sourceRevision; succeeded = $true; runs = $Runs; samplesPerStatePerRun = $Samples; warmupsPerStatePerRun = $Warmups; quantiles = 'Nearest-rank per-run percentiles; summary is median of run-percentiles, not pooled quantiles'; allocation = 'Process-wide total allocation deltas, includes background work/runtime noise; excludes explicit expected-fixture validation performed outside interval'; rows = @($taskSummaryRows) }
$taskSummary | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $taskEvidenceRoot 'summary.json') -Encoding utf8
$taskManifest['completedAtUtc'] = [DateTime]::UtcNow.ToString('O')
$taskManifest | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $taskManifestPath -Encoding utf8
Write-Output "PASS complete-cache measurement inventory: $Runs independent writer/reader pairs"
