function Invoke-BoundedCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [ValidateRange(1, 600)][int]$TimeoutSeconds = 30
    )
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FilePath
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $remainingMilliseconds = { [Math]::Max(0, $TimeoutSeconds * 1000 - [int]$clock.ElapsedMilliseconds) }
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit((& $remainingMilliseconds))
        if ($timedOut) {
            if (-not $process.HasExited) { $process.Kill($true) }
            [void]$process.WaitForExit(2000) # bounded process-tree cleanup grace
        }
        # An exited parent can leave descendants holding the redirected pipes.
        # Process exit and both reads share one deadline; never await an open pipe.
        if (-not [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]@($stdout, $stderr), (& $remainingMilliseconds))) {
            $timedOut = $true
        }
        $output = if ($stdout.IsCompletedSuccessfully) { $stdout.GetAwaiter().GetResult() } else { '[stdout did not close before the command deadline]' }
        $output += if ($stderr.IsCompletedSuccessfully) { $stderr.GetAwaiter().GetResult() } else { '[stderr did not close before the command deadline]' }
        [pscustomobject]@{
            ExitCode = if ($process.HasExited) { $process.ExitCode } else { -1 }
            TimedOut = $timedOut
            Output = $output
        }
    } finally { $process.Dispose() }
}

function Assert-PendingModelVerification {
    param([Parameter(Mandatory)]$Result)
    if ($Result.TimedOut) { throw 'Pending-model verification timed out; verification is mandatory.' }
    if ($Result.ExitCode -ne 0) {
        throw "Pending-model verification failed (exit $($Result.ExitCode)): pending changes or EF tooling failure."
    }
}
