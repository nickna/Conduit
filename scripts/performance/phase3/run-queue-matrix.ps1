#!/usr/bin/env pwsh
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BenchmarkDll,
    [Parameter(Mandatory)][string]$AdminConnection,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$LatencyLabel = 'uncontrolled',
    [int[]]$Batches = @(10,25,50,100),
    [int[]]$HandlerCostMs = @(0,15),
    [double[]]$OfferedRates = @(0),
    [ValidateRange(1,20)][int]$Repeats = 5,
    [ValidateRange(2,100000)][int]$Messages = 800,
    [ValidateRange(1,1000)][int]$Warmup = 10,
    [ValidateRange(10,1800)][int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'queue-report.ps1')

# The caller owns PostgreSQL/Redis and any tc/netem setup. This driver does not
# start Docker, connect to implicit localhost services, drop databases, or tune policy.
$dll = (Resolve-Path -LiteralPath $BenchmarkDll).Path
$inputRoot = Split-Path -Parent $dll
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Use a fresh output directory; previous evidence is immutable.' }
if ($Batches.Count -eq 0 -or @($Batches | Where-Object { $_ -notin @(10,25,50,100) }).Count -gt 0 -or @($Batches | Select-Object -Unique).Count -ne $Batches.Count) { throw 'Use distinct receive batches from10,25,50,100.' }
if ($HandlerCostMs.Count -eq 0 -or @($HandlerCostMs | Where-Object { $_ -lt 0 -or $_ -gt 1000 }).Count -gt 0) { throw 'Invalid synthetic handler delay.' }
if ($OfferedRates.Count -eq 0 -or @($OfferedRates | Where-Object { -not [double]::IsFinite($_) -or $_ -lt 0 -or $_ -gt 1000000 }).Count -gt 0) { throw 'Invalid offered rate.' }
if ([string]::IsNullOrWhiteSpace($AdminConnection)) { throw 'An explicit private PostgreSQL maintenance connection is required.' }
New-Item -ItemType Directory -Path $destination -ErrorAction Stop | Out-Null
$dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
$invariant = [CultureInfo]::InvariantCulture
$runNonce = [guid]::NewGuid().ToString('N').Substring(0,12)

function Get-QueueInputManifest {
    $entries = @([IO.Directory]::EnumerateFiles($inputRoot, '*', [IO.SearchOption]::AllDirectories) | Sort-Object | ForEach-Object {
        [pscustomobject]@{ path = [IO.Path]::GetRelativePath($inputRoot,$_).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    if ($entries.Count -eq 0) { throw 'Empty published benchmark input folder.' }
    return $entries
}

function Invoke-QueueProcess([string[]]$Arguments, [string]$Prefix) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $dotnetPath
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add($dll)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $timedOut = $false
    $started = $false
    try {
        if (-not $process.Start()) { throw 'Could not start owned benchmark child.' }
        $started = $true
        $processId = $process.Id
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        # Two independently bounded phases, setup/SQL RTT, and a30-second host drain.
        $deadline = 2 * $TimeoutSeconds + 120
        while (-not $process.WaitForExit(200)) {
            if ($watch.Elapsed.TotalSeconds -ge $deadline) {
                $timedOut = $true
                $process.Kill($true)
                if (-not $process.WaitForExit(10000)) { throw 'Owned process tree did not exit after termination.' }
                break
            }
        }
        [IO.File]::WriteAllText($Prefix + '.stdout.log', $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText($Prefix + '.stderr.log', $stderr.GetAwaiter().GetResult())
        return [pscustomobject]@{ pid = $processId; exitCode = $process.ExitCode; timedOut = $timedOut; elapsedSeconds = $watch.Elapsed.TotalSeconds }
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(10000) }
        $process.Dispose()
    }
}

$before = @(Get-QueueInputManifest)
$rows = [Collections.Generic.List[object]]::new()
$receipt = [ordered]@{
    schemaVersion = 1
    scope = 'Synthetic spend handlers, real PostgreSQL transport/inbox settlement. No real ledger/Redis financial qualification or production tuning.'
    latencyLabel = $LatencyLabel
    latencyControlOwnedByCaller = $true
    latencyControlVerifiedHere = $false
    observedSqlRttRequired = $true
    concurrency = 1
    pollingMilliseconds = 250
    repeats = $Repeats
    messages = $Messages
    warmup = $Warmup
    alternatingBatchOrder = $true
    inputFilesBefore = $before
    inputFilesAfter = $null
    allInputBytesUnchanged = $false
    cases = $rows
    passed = $false
    financialQualification = $false
    policyChangeAdopted = $false
    error = $null
}
try {
    $caseNumber = 0
    for ($repeat = 0; $repeat -lt $Repeats; $repeat++) {
        $order = @($Batches)
        if ($repeat % 2 -eq 1) { [array]::Reverse($order) }
        foreach ($cost in $HandlerCostMs) {
            foreach ($rate in $OfferedRates) {
                foreach ($batch in $order) {
                    $caseNumber++
                    $stem = 'run-{0:D3}-r{1}-b{2}-cost{3}-rate{4}' -f $caseNumber,($repeat+1),$batch,$cost,$rate.ToString($invariant)
                    $prefix = Join-Path $destination $stem
                    $jsonPath = $prefix + '.json'
                    $database = 'conduit_phase3_queue_' + $runNonce + '_' + $caseNumber
                    $arguments = @('transport-throughput','--queue','spend','--messages',$Messages.ToString($invariant),
                        '--warmup',$Warmup.ToString($invariant),'--handler-cost-ms',$cost.ToString($invariant),
                        '--receive-batch',$batch.ToString($invariant),'--publish-parallelism','1',
                        '--rate',$rate.ToString($invariant),'--timeout-seconds',$TimeoutSeconds.ToString($invariant),
                        '--database',$database,'--output',$jsonPath,'--db-latency-label',$LatencyLabel,'--admin-connection',$AdminConnection)
                    $execution = Invoke-QueueProcess -Arguments $arguments -Prefix $prefix
                    $row = [ordered]@{ repeat = $repeat+1; batch = $batch; handlerCostMs = $cost; offeredRate = $rate;
                        database = $database; report = [IO.Path]::GetFileName($jsonPath); execution = $execution; validated = $false; summary = $null; reportSha256 = $null }
                    $rows.Add($row)
                    if ($execution.timedOut -or $execution.exitCode -ne 0) { throw "Queue case $stem failed or timed out; raw evidence retained." }
                    $row.summary = Assert-QueueCaseReport -Path $jsonPath -Batch $batch -Messages $Messages -HandlerCostMs $cost -OfferedRate $rate -Warmup $Warmup
                    $row.reportSha256 = (Get-FileHash -LiteralPath $jsonPath -Algorithm SHA256).Hash.ToLowerInvariant()
                    $row.validated = $true
                    Write-Host ("Queue {0}: {1:F2} SQL-observed settled/s; saturation observed={2}" -f $stem,$row.summary.SettledRate,$row.summary.SaturationObserved)
                }
            }
        }
    }
    $receipt.inputFilesAfter = @(Get-QueueInputManifest)
    $receipt.allInputBytesUnchanged = (($before | ConvertTo-Json -Depth 4 -Compress) -ceq ($receipt.inputFilesAfter | ConvertTo-Json -Depth 4 -Compress))
    if (-not $receipt.allInputBytesUnchanged) { throw 'Published benchmark input file set or bytes changed during measurement.' }
    $receipt.passed = $rows.Count -eq ($Repeats * $Batches.Count * $HandlerCostMs.Count * $OfferedRates.Count) -and @($rows | Where-Object { -not $_.validated }).Count -eq 0
}
catch { $receipt.error = $_.Exception.Message; throw }
finally {
    # One final publication; earlier case reports/logs are never overwritten.
    $temporary = Join-Path $destination ('matrix-' + [guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($temporary, ($receipt | ConvertTo-Json -Depth 20))
    [IO.File]::Move($temporary, (Join-Path $destination 'matrix.json'), $false)
}
