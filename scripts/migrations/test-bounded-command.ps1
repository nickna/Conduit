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
