#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Test the migration validation tools in various scenarios.

.DESCRIPTION
    Comprehensive test suite for EF Core migration tools.

.EXAMPLE
    ./scripts/migrations/test-migration-tools.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Import common utilities
$scriptDir = $PSScriptRoot
$devLibPath = Join-Path $scriptDir '..' 'dev' 'lib' 'Common.psm1'
if (Test-Path $devLibPath) {
    Import-Module $devLibPath -Force
    $projectRoot = Get-ProjectRoot -FromPath $scriptDir
} else {
    $projectRoot = Split-Path $scriptDir -Parent | Split-Path -Parent
}

# Test results
$script:TestsPassed = 0
$script:TestsFailed = 0

function Write-TestHeader {
    param([string]$TestName)
    Write-Host ""
    Write-Host "TEST: $TestName" -ForegroundColor Blue
    Write-Host "------------------------------"
}

function Write-TestResult {
    param(
        [ValidateSet('PASS', 'FAIL')]
        [string]$Status,
        [string]$Message
    )

    if ($Status -eq 'PASS') {
        Write-Host "[OK] PASS: $Message" -ForegroundColor Green
        $script:TestsPassed++
    } else {
        Write-Host "[X] FAIL: $Message" -ForegroundColor Red
        $script:TestsFailed++
    }
}

# Test 1: Test ef-wrapper without DATABASE_URL
function Test-WrapperNoDatabaseUrl {
    Write-TestHeader "EF Wrapper - No DATABASE_URL"

    $configPath = Join-Path $projectRoot 'Shared' 'ConduitLLM.Configuration'
    Push-Location $configPath

    try {
        # Save and unset DATABASE_URL
        $savedDbUrl = $env:DATABASE_URL
        $env:DATABASE_URL = $null

        # Run wrapper and expect it to fail gracefully
        $output = & "$scriptDir/ef-wrapper.ps1" -Command @('migrations', 'list', '--no-build', '--no-connect') *>&1 | Out-String

        if ($output -match 'DATABASE_URL environment variable is not set') {
            Write-TestResult 'PASS' "Wrapper correctly detected missing DATABASE_URL"
        } else {
            Write-TestResult 'FAIL' "Wrapper did not detect missing DATABASE_URL"
        }

        # Restore DATABASE_URL
        $env:DATABASE_URL = $savedDbUrl
    } finally {
        Pop-Location
    }
}

# Test 2: Test ef-wrapper with invalid DATABASE_URL format
function Test-WrapperInvalidDatabaseUrl {
    Write-TestHeader "EF Wrapper - Invalid DATABASE_URL Format"

    $configPath = Join-Path $projectRoot 'Shared' 'ConduitLLM.Configuration'
    Push-Location $configPath

    try {
        # Save and set invalid DATABASE_URL
        $savedDbUrl = $env:DATABASE_URL
        $env:DATABASE_URL = 'invalid-connection-string'

        # Run wrapper and check for warning
        $output = & "$scriptDir/ef-wrapper.ps1" -Command @('migrations', 'list', '--no-build', '--no-connect') *>&1 | Out-String

        if ($output -match 'DATABASE_URL format may be invalid') {
            Write-TestResult 'PASS' "Wrapper warned about invalid DATABASE_URL format"
        } else {
            Write-TestResult 'FAIL' "Wrapper did not warn about invalid DATABASE_URL format"
        }

        # Restore DATABASE_URL
        $env:DATABASE_URL = $savedDbUrl
    } finally {
        Pop-Location
    }
}

# Test 3: Test ef-wrapper from wrong directory
function Test-WrapperWrongDirectory {
    Write-TestHeader "EF Wrapper - Wrong Directory"

    Push-Location $projectRoot

    try {
        # Run wrapper from wrong directory
        $output = & "$scriptDir/ef-wrapper.ps1" -Command @('migrations', 'list', '--no-build', '--no-connect') *>&1 | Out-String

        if ($output -match 'Not in ConduitLLM.Configuration directory') {
            Write-TestResult 'PASS' "Wrapper detected wrong directory"
        } else {
            Write-TestResult 'FAIL' "Wrapper did not detect wrong directory"
        }
    } finally {
        Pop-Location
    }
}

# Test 4: Test validate-migrations.ps1 basic functionality
function Test-ValidateMigrationsBasic {
    Write-TestHeader "Validate Migrations - Basic Run"

    $configPath = Join-Path $projectRoot 'Shared' 'ConduitLLM.Configuration'
    Push-Location $configPath

    try {
        # Run validation script and capture output
        $output = & "$scriptDir/validate-migrations.ps1" *>&1 | Out-String
        $exitCode = $LASTEXITCODE

        if ($exitCode -eq 0 -and $output -match 'Migration validation completed successfully!') {
            Write-TestResult 'PASS' "Validation script completed successfully"
        } else {
            Write-TestResult 'FAIL' "Validation script failed; prepare EF tooling and inspect the diagnostics"
            Write-Host $output
        }
    } finally {
        Pop-Location
    }
}

# Test 5: Test GitHub Actions workflow syntax
function Test-GitHubActionsSyntax {
    Write-TestHeader "GitHub Actions Workflow - Syntax Check"

    $workflowFile = Join-Path $projectRoot '.github' 'workflows' 'migration-validation.yml'

    if (-not (Test-Path $workflowFile)) {
        Write-Host "  Workflow file not found, skipping test" -ForegroundColor Yellow
        return
    }

    $workflowContent = Get-Content $workflowFile -Raw

    # Check job-level env is defined
    if ($workflowContent -match 'env:' -and $workflowContent -match 'validate-migrations:[\s\S]*?DATABASE_URL:') {
        Write-TestResult 'PASS' "Workflow has job-level DATABASE_URL defined"
    } else {
        Write-TestResult 'FAIL' "Workflow missing job-level DATABASE_URL"
    }

    # Check no duplicate env declarations in steps
    $databaseUrlMatches = [regex]::Matches($workflowContent, 'DATABASE_URL:')
    if ($databaseUrlMatches.Count -eq 1) {
        Write-TestResult 'PASS' "No duplicate DATABASE_URL declarations"
    } else {
        Write-TestResult 'FAIL' "Found $($databaseUrlMatches.Count) DATABASE_URL declarations (expected 1)"
    }
}

# Test 6: Test if all required scripts exist
function Test-ScriptsExist {
    Write-TestHeader "Script Files Exist"

    $scripts = @(
        (Join-Path $scriptDir 'validate-migrations.ps1'),
        (Join-Path $scriptDir 'ef-wrapper.ps1'),
        (Join-Path $scriptDir 'test-migration-tools.ps1')
    )

    $allExist = $true
    foreach ($script in $scripts) {
        if (Test-Path $script) {
            Write-Host "  [OK] $script exists" -ForegroundColor Green
        } else {
            Write-Host "  [X] $script does NOT exist" -ForegroundColor Red
            $allExist = $false
        }
    }

    if ($allExist) {
        Write-TestResult 'PASS' "All scripts exist"
    } else {
        Write-TestResult 'FAIL' "Some scripts are missing"
    }
}

# Test 7: Test that ef-wrapper provides better error messages
function Test-WrapperErrorMessages {
    Write-TestHeader "EF Wrapper - Enhanced Error Messages"

    $configPath = Join-Path $projectRoot 'Shared' 'ConduitLLM.Configuration'
    Push-Location $configPath

    try {
        # Use a read-only EF command. This test suite must never create a migration.
        $output = & "$scriptDir/ef-wrapper.ps1" -Command @('migrations', 'list', '--no-build', '--no-connect') *>&1 | Out-String

        # Check if wrapper provides helpful context
        if ($output -match 'Validating environment' -and $output -match 'EF Core Command Wrapper') {
            Write-TestResult 'PASS' "Wrapper provides structured output with validation"
        } else {
            Write-TestResult 'FAIL' "Wrapper output lacks structure or validation info"
        }
    } finally {
        Pop-Location
    }
}

# Main execution
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host "Migration Tools Test Suite" -ForegroundColor Cyan
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host "Running comprehensive tests..."

# Save current DATABASE_URL
$originalDatabaseUrl = $env:DATABASE_URL

# Run all tests
Test-WrapperNoDatabaseUrl
Test-WrapperInvalidDatabaseUrl
Test-WrapperWrongDirectory
Test-ValidateMigrationsBasic
Test-GitHubActionsSyntax
Test-ScriptsExist
Test-WrapperErrorMessages

# Restore DATABASE_URL
$env:DATABASE_URL = $originalDatabaseUrl

# Summary
Write-Host ""
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host "Test Summary" -ForegroundColor Cyan
Write-Host "==============================================" -ForegroundColor Cyan
Write-Host "Passed: $($script:TestsPassed)" -ForegroundColor Green
Write-Host "Failed: $($script:TestsFailed)" -ForegroundColor Red
Write-Host "Total:  $($script:TestsPassed + $script:TestsFailed)"

if ($script:TestsFailed -eq 0) {
    Write-Host ""
    Write-Host "[OK] All tests passed!" -ForegroundColor Green
    exit 0
} else {
    Write-Host ""
    Write-Host "[X] Some tests failed" -ForegroundColor Red
    exit 1
}
