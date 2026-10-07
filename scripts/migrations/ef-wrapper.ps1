#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Wrapper for EF Core commands with enhanced error handling and debugging.

.DESCRIPTION
    Ensures consistent environment setup and provides better error messages
    for Entity Framework Core migration commands.

.PARAMETER Command
    The EF Core command to run (e.g., "migrations list", "migrations add Name").

.EXAMPLE
    ./scripts/migrations/ef-wrapper.ps1 migrations list

.EXAMPLE
    ./scripts/migrations/ef-wrapper.ps1 migrations add MigrationName

.EXAMPLE
    ./scripts/migrations/ef-wrapper.ps1 -Command @('migrations', 'script', '-o', 'output.sql')
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments)]
    [ValidateNotNullOrEmpty()]
    [string[]]$Command
)

$ErrorActionPreference = 'Stop'

# Import common utilities
$scriptDir = $PSScriptRoot
$devLibPath = Join-Path $scriptDir '..' 'dev' 'lib' 'Common.psm1'
if (Test-Path $devLibPath) {
    Import-Module $devLibPath -Force
}

# Helper functions for colored output
function Write-EfStatus {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('error', 'success', 'warning', 'info')]
        [string]$Status,

        [Parameter(Mandatory)]
        [string]$Message
    )

    switch ($Status) {
        'error'   { Write-Host "X ERROR: $Message" -ForegroundColor Red }
        'success' { Write-Host "[OK] $Message" -ForegroundColor Green }
        'warning' { Write-Host "[!] $Message" -ForegroundColor Yellow }
        'info'    { Write-Host "[i] $Message" -ForegroundColor Blue }
    }
}

function Test-Environment {
    Write-EfStatus 'info' "Validating environment..."

    $hasError = $false

    # Check DATABASE_URL
    $databaseUrl = $env:DATABASE_URL
    if ([string]::IsNullOrWhiteSpace($databaseUrl)) {
        Write-EfStatus 'error' "DATABASE_URL environment variable is not set"
        Write-Host "  Set DATABASE_URL to a valid PostgreSQL connection string:"
        Write-Host "  Example: postgresql://user:password@localhost:5432/conduitdb"
        $hasError = $true
    } else {
        Write-EfStatus 'success' "DATABASE_URL is set"
        # Validate format (basic check)
        if ($databaseUrl -notmatch '^(postgresql|postgres)://' -and $databaseUrl -notmatch 'Host=') {
            Write-EfStatus 'warning' "DATABASE_URL format may be invalid"
            Write-Host "  Expected format: postgresql://user:password@host:port/database"
            Write-Host "  Or: Host=host;Port=port;Database=database;Username=user;Password=password"
        }
    }

    # Check if we're in the correct directory
    if (-not (Test-Path 'ConduitLLM.Configuration.csproj')) {
        Write-EfStatus 'error' "Not in ConduitLLM.Configuration directory"
        Write-Host "  Please run this script from the ConduitLLM.Configuration directory"
        $hasError = $true
    } else {
        Write-EfStatus 'success' "In correct project directory"
    }

    # Check if EF Core tools are installed
    try {
        $efVersion = dotnet ef --version 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "EF tools not working"
        }
        Write-EfStatus 'success' "EF Core tools installed (version: $efVersion)"
    } catch {
        Write-EfStatus 'error' "EF Core tools not installed"
        Write-Host "  Install with: dotnet tool install --global dotnet-ef"
        $hasError = $true
    }

    # Check if project is built
    if (-not (Test-Path 'bin') -or -not (Test-Path 'obj')) {
        Write-EfStatus 'warning' "Project may not be built"
        Write-Host "  Run: dotnet build"
    }

    return -not $hasError
}

function Test-DatabaseConnection {
    Write-EfStatus 'info' "Testing database connection..."

    $databaseUrl = $env:DATABASE_URL
    if ([string]::IsNullOrWhiteSpace($databaseUrl)) {
        return $false
    }

    # Extract connection details from DATABASE_URL
    if ($databaseUrl -match '^(postgresql|postgres)://([^:]+):([^@]+)@([^:]+):(\d+)/(.+)$') {
        $databaseHost = $Matches[4]
        $port = [int]$Matches[5]

        # Test if PostgreSQL is reachable
        try {
            $tcpClient = New-Object System.Net.Sockets.TcpClient
            $connectResult = $tcpClient.BeginConnect($databaseHost, $port, $null, $null)
            $success = $connectResult.AsyncWaitHandle.WaitOne(5000, $false)
            if ($success) { $tcpClient.EndConnect($connectResult) }

            if ($success) {
                Write-EfStatus 'success' "PostgreSQL server is reachable at ${databaseHost}:${port}"
                return $true
            } else {
                Write-EfStatus 'error' "Cannot connect to PostgreSQL at ${databaseHost}:${port}"
                Write-Host "  Ensure PostgreSQL is running and accessible"
                return $false
            }
        } catch {
            Write-EfStatus 'error' "Cannot connect to PostgreSQL at ${databaseHost}:${port}"
            Write-Host "  Error: $_"
            return $false
        } finally {
            if ($tcpClient) { $tcpClient.Dispose() }
        }
    }

    return $true
}

function Invoke-EfCommand {
    param(
        [Parameter(Mandatory)]
        [string[]]$CommandArgs
    )

    $commandString = $CommandArgs -join ' '
    Write-EfStatus 'info' "Running: dotnet ef $commandString"

    # Start-Process joins ArgumentList into a command line, which loses boundaries
    # for paths with spaces. ProcessStartInfo.ArgumentList passes each EF argument.
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.WorkingDirectory = (Get-Location).Path
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in (@('ef') + $CommandArgs)) { $startInfo.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $stdout.GetAwaiter().GetResult()
        $errorOutput = $stderr.GetAwaiter().GetResult()

        if ($output) { Write-Host $output }
        if ($errorOutput) { Write-Host $errorOutput -ForegroundColor Red }

        if ($process.ExitCode -eq 0) {
            Write-EfStatus 'success' "Command completed successfully"
        } else {
            # Analyze common error patterns
            $combinedOutput = "$output`n$errorOutput"

            if ($combinedOutput -match "Unable to create a 'DbContext'") {
                Write-EfStatus 'error' "Failed to create DbContext"
                Write-Host "  This usually means the database connection failed"
                Write-Host "  Check your DATABASE_URL and ensure PostgreSQL is running"
            } elseif ($combinedOutput -match "No project was found") {
                Write-EfStatus 'error' "No project found"
                Write-Host "  Ensure you're in the correct directory with a .csproj file"
            } elseif ($combinedOutput -match "Build failed") {
                Write-EfStatus 'error' "Build failed"
                Write-Host "  Run: dotnet build"
            }

            Write-EfStatus 'error' "Command failed with exit code: $($process.ExitCode)"
        }

        return $process.ExitCode
    } finally {
        $process.Dispose()
    }
}

# Dot-sourcing exposes helpers for the offline regression suite without running EF.
if ($MyInvocation.InvocationName -eq '.') { return }

# Main execution
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host "EF Core Command Wrapper" -ForegroundColor Cyan
Write-Host "==============================================" -ForegroundColor Cyan

# Validate environment first
if (-not (Test-Environment)) {
    Write-Host ""
    Write-EfStatus 'error' "Environment validation failed"
    exit 1
}

# Test database connection
if (-not (Test-DatabaseConnection)) {
    Write-Host ""
    Write-EfStatus 'warning' "Database connection test failed"
    Write-Host "  Continuing anyway - some commands may work without a live database"
}

Write-Host ""
Write-Host "Running EF Core command..."
Write-Host "------------------------------"

# Run the actual command
$exitCode = Invoke-EfCommand -CommandArgs $Command

Write-Host "------------------------------"

if ($exitCode -eq 0) {
    Write-EfStatus 'success' "Operation completed successfully"
} else {
    Write-EfStatus 'error' "Operation failed"
}

exit $exitCode
