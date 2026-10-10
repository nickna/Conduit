#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$wrapperPath = Join-Path $PSScriptRoot 'ef-wrapper.ps1'
. $wrapperPath

# Exercise the public script binder: the EF -o switch must remain an array value,
# even when the destination contains spaces. Missing environment stops before EF.
$originalUrl = $env:DATABASE_URL
try {
    $env:DATABASE_URL = $null
    $output = & $wrapperPath -Command @('migrations', 'script', '--no-build', '-o', 'output with spaces.sql') *>&1 | Out-String
    if ($LASTEXITCODE -ne 1 -or $output -notmatch 'DATABASE_URL environment variable is not set') {
        throw 'Explicit EF command-array binding did not reach environment validation.'
    }
    Write-Host 'The wrapper accepts EF -o and a path with spaces through -Command.'

    # A successful TCP handshake proves the PostgreSQL URL path does not assign
    # PowerShell's read-only $Host. A closed port must not be reported reachable.
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $databasePort = $listener.LocalEndpoint.Port
    try {
        $env:DATABASE_URL = "postgresql://fixture:fixture@127.0.0.1:$databasePort/fixture"
        if (-not (Test-DatabaseConnection)) { throw 'The reachable PostgreSQL-URL probe failed.' }
    } finally { $listener.Stop() }
    if (Test-DatabaseConnection) { throw 'A refused PostgreSQL TCP connection was reported reachable.' }
    Write-Host 'PostgreSQL URL probes handle reachable and refused connections without overwriting Host.'
} finally { $env:DATABASE_URL = $originalUrl }
exit 0
