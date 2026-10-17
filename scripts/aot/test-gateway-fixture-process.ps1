#!/usr/bin/env pwsh
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$harness = Join-Path $PSScriptRoot 'gateway-native-parity.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($harness, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Gateway parity harness syntax failed.' }
foreach ($name in @('Invoke-PrivateFixtureProcess', 'Stop-OwnedParityProcesses')) {
    $definition = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }.GetNewClosure(), $true)
    if ($null -eq $definition) { throw 'Required process helper is missing.' }
    Invoke-Expression $definition.Extent.Text
}

$temporaryRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) "conduit-gateway-process-$([Guid]::NewGuid().ToString('N'))"))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$descendant = $null
$pidPath = Join-Path $temporaryRoot 'descendant.pid'
$pwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
$parentValue = [Environment]::GetEnvironmentVariable('CONDUIT_PROCESS_TEST_SECRET')
try {
    $child = Join-Path $temporaryRoot 'child.ps1'
    @'
[Console]::Out.WriteLine($env:CONDUIT_PROCESS_TEST_RESULT)
[Console]::Error.WriteLine($env:CONDUIT_PROCESS_TEST_SECRET)
if ($env:CONDUIT_PROCESS_TEST_MODE -eq 'fail') { exit 17 }
if ($env:CONDUIT_PROCESS_TEST_MODE -eq 'timeout') {
    $start = [Diagnostics.ProcessStartInfo]::new($env:CONDUIT_PROCESS_TEST_PWSH)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in @('-NoLogo', '-NoProfile', '-File', $env:CONDUIT_PROCESS_TEST_SLEEPER)) {
        $start.ArgumentList.Add($argument)
    }
    $descendant = [Diagnostics.Process]::Start($start)
    [IO.File]::WriteAllText($env:CONDUIT_PROCESS_TEST_PID,
        "$($descendant.Id)|$($descendant.StartTime.ToUniversalTime().Ticks)")
    $descendant.Dispose()
    Start-Sleep -Seconds 60
}
'@ | Set-Content -LiteralPath $child
    $sleeper = Join-Path $temporaryRoot 'sleeper.ps1'
    'Start-Sleep -Seconds 60' | Set-Content -LiteralPath $sleeper
    $arguments = @('-NoLogo', '-NoProfile', '-File', $child)
    $environment = @{
        CONDUIT_PROCESS_TEST_RESULT = 'owned-result'
        CONDUIT_PROCESS_TEST_SECRET = 'private-fixture-stderr'
        CONDUIT_PROCESS_TEST_MODE = 'success'
    }
    $result = Invoke-PrivateFixtureProcess $pwsh $arguments $environment 'Process fixture'
    if ($result.TrimEnd("`r", "`n") -cne 'owned-result') { throw 'Private process success contract failed.' }
    $environment.CONDUIT_PROCESS_TEST_MODE = 'fail'
    $failure = $null
    try { $unexpected = Invoke-PrivateFixtureProcess $pwsh $arguments $environment 'Process fixture' }
    catch { $failure = $_.Exception.Message }
    if ($failure -cne 'Process fixture failed.') { throw 'Nonzero process failure did not stay private.' }

    $environment.CONDUIT_PROCESS_TEST_MODE = 'timeout'
    $environment.CONDUIT_PROCESS_TEST_PWSH = $pwsh
    $environment.CONDUIT_PROCESS_TEST_SLEEPER = $sleeper
    $environment.CONDUIT_PROCESS_TEST_PID = $pidPath
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $failure = $null
    try { $unexpected = Invoke-PrivateFixtureProcess $pwsh $arguments $environment 'Process fixture' 3000 }
    catch { $failure = $_.Exception.Message }
    if ($failure -cne 'Process fixture timed out.' -or $watch.Elapsed.TotalSeconds -gt 10) {
        throw 'Process timeout did not terminate and reap within its deadline.'
    }
    if (-not (Test-Path -LiteralPath $pidPath -PathType Leaf)) { throw 'Timeout fixture did not start its descendant.' }
    $identity = (Get-Content -LiteralPath $pidPath -Raw).Split('|')
    $descendantId = [int]$identity[0]
    $descendant = Get-Process -Id $descendantId -ErrorAction SilentlyContinue
    if ($descendant -and $descendant.StartTime.ToUniversalTime().Ticks -ne [long]$identity[1]) {
        throw 'Timeout descendant identity no longer belongs to the fixture.'
    }
    if ($null -ne $descendant -and -not $descendant.HasExited) {
        # On Linux, a terminated descendant can remain a zombie until its new
        # parent reaps it. A zombie is stopped and cannot produce fixture state.
        $statPath = "/proc/$descendantId/stat"
        if (-not $IsLinux -or -not (Test-Path -LiteralPath $statPath) -or
            (Get-Content -LiteralPath $statPath -Raw) -notmatch '^\d+ \(.+\) Z ') {
            throw 'Timeout left a live descendant.'
        }
    }

    $start = [Diagnostics.ProcessStartInfo]::new($pwsh)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in @('-NoLogo', '-NoProfile', '-File', $sleeper)) { $start.ArgumentList.Add($argument) }
    $hostProcess = [Diagnostics.Process]::Start($start)
    $ownedId = $hostProcess.Id
    $ownedProcesses.Add($hostProcess)
    Stop-OwnedParityProcesses $ownedProcesses
    $ownedProcesses.Clear()
    if (Get-Process -Id $ownedId -ErrorAction SilentlyContinue) { throw 'Owned host was not reaped.' }
    if ([Environment]::GetEnvironmentVariable('CONDUIT_PROCESS_TEST_SECRET') -cne $parentValue) {
        throw 'Child configuration changed the parent environment.'
    }
    Write-Host 'PASS: private process output, generic nonzero failure, bounded tree termination, owned host reap, unchanged parent environment.'
} finally {
    try {
        if ($ownedProcesses.Count) { Stop-OwnedParityProcesses $ownedProcesses }
    } finally {
        # A regression in the helper's tree termination must not leave the test's
        # descendant running. PID plus start time prevents stopping a reused PID.
        if (Test-Path -LiteralPath $pidPath -PathType Leaf) {
            $identity = (Get-Content -LiteralPath $pidPath -Raw).Split('|')
            $remainingChild = Get-Process -Id ([int]$identity[0]) -ErrorAction SilentlyContinue
            if ($remainingChild) {
                try {
                    if ($remainingChild.StartTime.ToUniversalTime().Ticks -ne [long]$identity[1]) {
                        throw 'Process cleanup rejected a reused descendant PID.'
                    }
                    $statPath = "/proc/$($remainingChild.Id)/stat"
                    $isZombie = $IsLinux -and (Test-Path -LiteralPath $statPath) -and
                        (Get-Content -LiteralPath $statPath -Raw) -match '^\d+ \(.+\) Z '
                    if (-not $remainingChild.HasExited -and -not $isZombie) {
                        $remainingChild.Kill($true)
                        if (-not $remainingChild.WaitForExit(5000)) { throw 'Owned timeout descendant could not be reaped.' }
                    }
                } finally { $remainingChild.Dispose() }
            }
        }
        if ($descendant) { $descendant.Dispose() }
        $resolved = [IO.Path]::GetFullPath($temporaryRoot)
        $leaf = [IO.Path]::GetFileName($resolved)
        $parsedOwner = [Guid]::Empty
        if ($resolved -ne $temporaryRoot -or -not $leaf.StartsWith('conduit-gateway-process-') -or
            -not [Guid]::TryParseExact($leaf.Substring('conduit-gateway-process-'.Length), 'N', [ref]$parsedOwner)) {
            throw 'Process test cleanup requires its owned directory.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
