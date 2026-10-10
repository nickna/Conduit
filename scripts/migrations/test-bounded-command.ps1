$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BoundedCommand.ps1')
$executable = (Get-Process -Id $PID).Path
$workingDirectory = (Get-Location).Path
foreach ($scenario in @(
    @{ Command = "Write-Output 'No changes'; exit 0"; Timeout = 10; Expected = $true },
    @{ Command = "Write-Error 'Pending model changes'; exit 1"; Timeout = 10; Expected = $false },
    @{ Command = "Write-Error 'EF tool failure'; exit 42"; Timeout = 10; Expected = $false },
    @{ Command = "Write-Output 'before timeout'; Start-Sleep -Seconds 20"; Timeout = 1; Expected = $false }
)) {
    $result = Invoke-BoundedCommand -FilePath $executable -Arguments @('-NoProfile', '-Command', $scenario.Command) -WorkingDirectory $workingDirectory -TimeoutSeconds $scenario.Timeout
    $accepted = $true
    try { Assert-PendingModelVerification $result } catch { $accepted = $false }
    if ($accepted -ne $scenario.Expected) { throw "Unexpected verification result: $($scenario.Command)" }
    if (-not $result.Output) { throw 'Tool diagnostics were lost' }
    Write-Host "Verified exit=$($result.ExitCode) timeout=$($result.TimedOut) accepted=$accepted"
}
$missingToolRejected = $false
try { Invoke-BoundedCommand -FilePath 'conduit-nonexistent-ef-tool' -WorkingDirectory $workingDirectory } catch { $missingToolRejected = $true }
if (-not $missingToolRejected) { throw 'Missing tool was accepted' }
Write-Host 'Missing tooling fails closed.'

if ($IsWindows) {
    Write-Host 'Inherited POSIX pipe regression runs on Linux (required CI or the SDK test container).'
    return
}
$childIdPath = [System.IO.Path]::GetTempFileName()
$inheritedPipeFixture = 'sleep 20 & echo $! > "$1"; echo "Parent exited while child retains stdout and stderr."'
try {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $result = Invoke-BoundedCommand -FilePath '/bin/sh' -Arguments @('-c', $inheritedPipeFixture, 'fixture', $childIdPath) -WorkingDirectory $workingDirectory -TimeoutSeconds 2
    if (-not $result.TimedOut -or $result.ExitCode -ne 0 -or $clock.Elapsed.TotalSeconds -gt 8) {
        throw 'An inherited redirected pipe did not fail within the command deadline and bounded cleanup grace.'
    }
    Write-Host 'An exited parent with inherited open pipes fails within the deadline.'
} finally {
    $childIdText = Get-Content -LiteralPath $childIdPath -Raw
    if ($childIdText -match '^\d+$') { Stop-Process -Id ([int]$childIdText) -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $childIdPath -Force
}
