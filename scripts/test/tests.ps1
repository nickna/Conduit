#!/usr/bin/env pwsh
#Requires -Version 7.0

<#
.SYNOPSIS
    Run unit tests by default, or explicitly select integration tests.

.DESCRIPTION
    Runs Tests/ConduitLLM.Tests by default. Use -Integration to run
    Tests/ConduitLLM.IntegrationTests instead.
    Active infrastructure tests live in Tests/ConduitLLM.IntegrationTests.
    Use scripts/ci/run-tests.ps1 with the documented Component traits and isolated
    Docker/PostgreSQL/Redis infrastructure for the same execution evidence as CI.

.PARAMETER Filter
    Optional test filter to run specific tests in the selected project.

.PARAMETER Integration
    Run the integration test project instead of the unit test project.
    Integration tests require their documented infrastructure and configuration.

.EXAMPLE
    ./scripts/test/tests.ps1

.EXAMPLE
    ./scripts/test/tests.ps1 -Filter "FullyQualifiedName~MyTest"

.EXAMPLE
    ./scripts/test/tests.ps1 -Integration

.EXAMPLE
    ./scripts/test/tests.ps1 -Integration -Filter "Component=SignalR"
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Filter,

    [switch]$Integration
)

$ErrorActionPreference = 'Stop'

# Import common utilities
$scriptDir = $PSScriptRoot
$devLibPath = Join-Path $scriptDir '..' 'dev' 'lib' 'Common.psm1'
if (Test-Path $devLibPath) {
    Import-Module $devLibPath -Force
}

# Determine if running in terminal (for colored output)
$isInteractive = -not [Console]::IsOutputRedirected -and -not $env:CI

# Colors for output
function Write-TestStatus {
    param(
        [ValidateSet('info', 'success', 'error')]
        [string]$Status,
        [string]$Message
    )

    if ($isInteractive) {
        switch ($Status) {
            'info'    { Write-Host "==> $Message" -ForegroundColor Blue }
            'success' { Write-Host "[OK] $Message" -ForegroundColor Green }
            'error'   { Write-Host "[X] $Message" -ForegroundColor Red }
        }
    } else {
        switch ($Status) {
            'info'    { Write-Host "==> $Message" }
            'success' { Write-Host "[OK] $Message" }
            'error'   { Write-Host "[X] $Message" }
        }
    }
}

$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$testProjectName = if ($Integration) { 'ConduitLLM.IntegrationTests' } else { 'ConduitLLM.Tests' }
$testProject = Join-Path $projectRoot "Tests/$testProjectName/$testProjectName.csproj"
$testSuite = if ($Integration) { 'integration' } else { 'unit' }

Write-TestStatus 'info' "Running $testSuite tests..."
Write-Host ""

# Build test command
$testArgs = @(
    'test'
    $testProject
    '--configuration', 'Debug'
    '--logger', 'console;verbosity=normal'
)

# Add test filter if provided
if ($Filter) {
    $testArgs += '--filter'
    $testArgs += $Filter
    Write-TestStatus 'info' "Using test filter: $Filter"
}

# Show command
$cmdDisplay = "dotnet $($testArgs -join ' ')"
Write-TestStatus 'info' "Running: $cmdDisplay"
Write-Host ""

# Run tests
& dotnet @testArgs
$exitCode = $LASTEXITCODE

Write-Host ""

if ($exitCode -eq 0) {
    Write-TestStatus 'success' "All tests passed!"
} else {
    Write-TestStatus 'error' "Some tests failed"
    exit 1
}
