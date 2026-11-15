[CmdletBinding()]
param(
    [ValidateRange(1, 10)][int] $Runs = 5,
    [string] $EvidenceDirectory = (Join-Path $PSScriptRoot 'collector-measurements'),
    [ValidateRange(10, 600)][int] $ProcessTimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $taskRoot) { throw 'Fresh evidence directory required.' }
New-Item -ItemType Directory -Path $taskRoot | Out-Null
$taskDll = Join-Path $PSScriptRoot 'collector/bin/Release/net10.0/Conduit.CollectorMeasurements.dll'
$taskManifest = [ordered]@{
    schemaVersion = 1
    sourceRevision = 'f68cb5fae1130bbe1ef4fc30aca7a3266094dbaa'
    sdk = (& dotnet --version | Out-String).Trim()
    runtimes = (& dotnet --list-runtimes | Out-String).Trim()
    runs = $Runs
    startedAtUtc = [DateTime]::UtcNow.ToString('O')
    productionSource = 'Shared/ConduitLLM.Core/Services/StreamingMetricsCollector.cs'
    callPatternSource = 'Services/ConduitLLM.Gateway/Endpoints/ChatEndpoints.Streaming.cs:550'
    workload = 'Content chunks10,100,1000,10000; snapshot each chunk versus same actual collector with records and a single final snapshot.1warmup+3measured streams per size/mode per process.'
    timing = 'Synchronous burst collector-only microbenchmark. Constructor, record/snapshot loop and final snapshot; no HTTP, SSE writer, cadence, periodic emissions or server telemetry.'
    allocation = 'GC.GetAllocatedBytesForCurrentThread, same managed thread verified before/after each stream.'
    control = 'Mechanistic record-plus-final-snapshot control is not deployed endpoint behavior or an implemented constant-state optimization.'
    sourceSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'collector/Program.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
    binarySha256 = (Get-FileHash -LiteralPath $taskDll -Algorithm SHA256).Hash.ToLowerInvariant()
    runnerSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    invocations = @()
}
for ($taskRun = 1; $taskRun -le $Runs; $taskRun++) {
    $taskOutput = Join-Path $taskRoot "run-$taskRun.json"
    $taskInfo = [Diagnostics.ProcessStartInfo]::new()
    $taskInfo.FileName = (Get-Command dotnet).Source
    $taskInfo.UseShellExecute = $false
    $taskInfo.CreateNoWindow = $true
    $taskInfo.RedirectStandardOutput = $true
    $taskInfo.RedirectStandardError = $true
    $taskInfo.WorkingDirectory = $PSScriptRoot
    $taskInfo.ArgumentList.Add($taskDll)
    $taskInfo.ArgumentList.Add($taskOutput)
    $taskProcess = [Diagnostics.Process]::new()
    $taskProcess.StartInfo = $taskInfo
    $taskWatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (-not $taskProcess.Start()) { throw 'Collector process failed to start.' }
        $taskOut = $taskProcess.StandardOutput.ReadToEndAsync()
        $taskErr = $taskProcess.StandardError.ReadToEndAsync()
        if (-not $taskProcess.WaitForExit($ProcessTimeoutSeconds * 1000)) { $taskProcess.Kill($true); $taskProcess.WaitForExit(); throw 'Collector deadline exceeded.' }
        $taskWatch.Stop()
        $taskOut.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $taskRoot "run-$taskRun.stdout.log") -Encoding utf8
        $taskErr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $taskRoot "run-$taskRun.stderr.log") -Encoding utf8
        $taskManifest.invocations += [ordered]@{ run = $taskRun; processId = $taskProcess.Id; exitCode = $taskProcess.ExitCode; elapsedMilliseconds = $taskWatch.Elapsed.TotalMilliseconds; report = "run-$taskRun.json" }
        $taskManifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $taskRoot 'manifest.json') -Encoding utf8
        if ($taskProcess.ExitCode -ne 0) { throw "Collector run$taskRun failed; retain logs." }
        $taskReport = Get-Content -LiteralPath $taskOutput -Raw | ConvertFrom-Json
        if (-not $taskReport.Succeeded -or $taskReport.Rows.Count -ne 8 -or @($taskReport.Rows | Where-Object { $_.Samples.Count -ne 3 }).Count -ne 0) { throw 'Collector inventory validation failed.' }
        Write-Output "PASS collector run$taskRun :8rows,24measured streams"
    }
    finally { $taskProcess.Dispose() }
}
function Get-Median($taskValues) { $taskSorted = @($taskValues | Sort-Object); $taskSorted[[Math]::Floor($taskSorted.Count / 2)] }
$taskRows = foreach ($taskEntry in $taskManifest.invocations) {
    $taskReport = Get-Content -LiteralPath (Join-Path $taskRoot $taskEntry.report) -Raw | ConvertFrom-Json
    foreach ($taskRow in $taskReport.Rows) {
        [pscustomobject]@{ chunks = $taskRow.ContentChunks; mode = $taskRow.Mode; medianLatencyMicroseconds = Get-Median $taskRow.Samples.Microseconds; medianThreadLocalAllocatedBytes = Get-Median $taskRow.Samples.ThreadLocalAllocatedBytes }
    }
}
$taskSummaryRows = foreach ($taskGroup in ($taskRows | Group-Object chunks, mode)) {
    [ordered]@{ chunks = $taskGroup.Group[0].chunks; mode = $taskGroup.Group[0].mode; independentProcesses = $taskGroup.Count; measuredStreams = 3 * $taskGroup.Count; medianOfPerProcessMedianLatencyMicroseconds = Get-Median $taskGroup.Group.medianLatencyMicroseconds; medianOfPerProcessMedianThreadLocalAllocatedBytes = Get-Median $taskGroup.Group.medianThreadLocalAllocatedBytes; perProcessMedians = @($taskGroup.Group) }
}
[ordered]@{ schemaVersion = 1; succeeded = $true; sourceRevision = $taskManifest.sourceRevision; rows = @($taskSummaryRows); limitation = 'Small burst microbenchmark, fixed mode order, one warmup and3measurements per mode/size/process. Mechanism/allocation evidence, not HTTP throughput or statistical significance.' } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $taskRoot 'summary.json') -Encoding utf8
$taskManifest['completedAtUtc'] = [DateTime]::UtcNow.ToString('O')
$taskManifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $taskRoot 'manifest.json') -Encoding utf8
Write-Output "PASS collector inventory: $Runs independent processes"
