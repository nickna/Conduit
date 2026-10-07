#!/usr/bin/env pwsh
#Requires -Version 7.0
<#
.SYNOPSIS
    Validate EF Core migrations for CI/CD pipeline.

.DESCRIPTION
    Validates migration files, checks for duplicates, and detects pending changes.

.PARAMETER CheckPending
    Fail if pending model changes are detected.

.PARAMETER GenerateScript
    Generate a SQL migration script.

.EXAMPLE
    ./scripts/migrations/validate-migrations.ps1

.EXAMPLE
    ./scripts/migrations/validate-migrations.ps1 -CheckPending

.EXAMPLE
    ./scripts/migrations/validate-migrations.ps1 -GenerateScript
#>

[CmdletBinding()]
param(
    [Parameter()]
    [switch]$CheckPending,

    [Parameter()]
    [switch]$GenerateScript,

    [ValidateRange(1, 600)]
    [int]$PendingTimeoutSeconds = 30,

    [ValidateRange(1, 600)]
    [int]$InventoryTimeoutSeconds = 30
)

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

$configurationProject = Join-Path $projectRoot 'Shared' 'ConduitLLM.Configuration'
. (Join-Path $PSScriptRoot 'BoundedCommand.ps1')
. (Join-Path $PSScriptRoot 'MigrationInventory.ps1')

Write-Host "==============================================" -ForegroundColor Cyan
Write-Host "EF Core Migration Validation" -ForegroundColor Cyan
Write-Host "==============================================" -ForegroundColor Cyan

Push-Location $configurationProject

try {
    # Step 1: Check if EF Core tools are installed
    Write-Host ""
    Write-Host "Step 1: Checking EF Core tools..." -ForegroundColor Yellow

    try {
        $efVersion = dotnet ef --version 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "EF tools check failed"
        }
        Write-Host "[OK] EF Core tools installed: $efVersion" -ForegroundColor Green
    } catch {
        Write-Host "ERROR: EF Core tools not installed" -ForegroundColor Red
        # Surface the underlying dotnet error - without this the real cause is invisible in CI
        Write-Host "dotnet ef --version output: $efVersion"
        Write-Host "Restore the repository tool manifest with: dotnet tool restore"
        exit 1
    }

    # Step 2: List all migrations
    Write-Host ""
    Write-Host "Step 2: Listing migrations..." -ForegroundColor Yellow

    # EF metadata is authoritative in every mode. A timeout must never change the
    # inventory to timestamped files that EF would not apply to deployed databases.
    $listing = Invoke-BoundedCommand -FilePath 'dotnet' -WorkingDirectory $configurationProject `
        -Arguments @('ef', 'migrations', 'list', '--no-build', '--no-connect') -TimeoutSeconds $InventoryTimeoutSeconds
    $diagnosticDirectory = Join-Path $projectRoot 'artifacts/migrations'
    New-Item -ItemType Directory -Force $diagnosticDirectory | Out-Null
    $listing.Output | Set-Content (Join-Path $diagnosticDirectory 'inventory.log')
    $migrations = @(Get-VerifiedMigrationInventory -Result $listing)
    Write-Host 'Migrations from EF tool:'

    $migrations | ForEach-Object { Write-Host "  $_" }

    $migrationCount = $migrations.Count
    Write-Host ""
    Write-Host "Total migrations: $migrationCount" -ForegroundColor Cyan

    # Step 3: Check for duplicate migration names
    Write-Host ""
    Write-Host "Step 3: Checking for duplicate migration names..." -ForegroundColor Yellow

    $duplicates = $migrations | Group-Object | Where-Object { $_.Count -gt 1 } | Select-Object -ExpandProperty Name
    if ($duplicates) {
        Write-Host "ERROR: Duplicate migration names found:" -ForegroundColor Red
        $duplicates | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        exit 1
    } else {
        Write-Host "[OK] No duplicate migration names" -ForegroundColor Green
    }

    # Step 4: Validate migration files exist
    Write-Host ""
    Write-Host "Step 4: Validating migration files..." -ForegroundColor Yellow

    $migrationsPath = Join-Path $configurationProject 'Migrations'
    Assert-MigrationSourceInventory -Migrations $migrations -MigrationsPath $migrationsPath
    Write-Host '[OK] All migration sources match EF inventory and have designers' -ForegroundColor Green

    # Step 5: Check for pending model changes
    Write-Host ""
    Write-Host "Step 5: Checking for pending model changes..." -ForegroundColor Yellow

    $pendingResult = Invoke-BoundedCommand -FilePath 'dotnet' -WorkingDirectory $configurationProject `
        -Arguments @('ef', 'migrations', 'has-pending-model-changes', '--no-build') -TimeoutSeconds $PendingTimeoutSeconds
    $diagnosticDirectory = Join-Path $projectRoot 'artifacts/migrations'
    New-Item -ItemType Directory -Force $diagnosticDirectory | Out-Null
    $pendingResult.Output | Set-Content (Join-Path $diagnosticDirectory 'pending-model.log')
    Write-Host $pendingResult.Output
    if ($CheckPending) {
        Assert-PendingModelVerification $pendingResult
    } elseif ($pendingResult.TimedOut -or $pendingResult.ExitCode -ne 0) {
        Write-Warning 'Pending-model verification did not succeed; use -CheckPending for mandatory verification.'
    }

    # Step 6: Generate migration script (optional)
    if ($GenerateScript) {
        Write-Host ""
        Write-Host "Step 6: Generating migration script..." -ForegroundColor Yellow

        $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $outputFile = Join-Path $projectRoot "migration-script-$timestamp.sql"

        $efWrapperPath = Join-Path $scriptDir 'ef-wrapper.ps1'
        if (Test-Path $efWrapperPath) {
            & $efWrapperPath -Command @('migrations', 'script', '--no-build', '-o', $outputFile)
        } else {
            dotnet ef migrations script --no-build -o $outputFile
        }

        if ($LASTEXITCODE -eq 0 -and (Test-Path $outputFile)) {
            Write-Host "[OK] Migration script generated: $outputFile" -ForegroundColor Green
        } else {
            Write-Host "ERROR: Failed to generate migration script" -ForegroundColor Red
            exit 1
        }
    }

    # Step 7: Check migration snapshot
    Write-Host ""
    Write-Host "Step 7: Validating migration snapshot..." -ForegroundColor Yellow

    $snapshotFile = Get-ChildItem -Path $migrationsPath -Filter '*ModelSnapshot.cs' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $snapshotFile) {
        Write-Host "ERROR: Migration snapshot file missing" -ForegroundColor Red
        exit 1
    } else {
        Write-Host "[OK] Migration snapshot present: $($snapshotFile.Name)" -ForegroundColor Green
    }

    # Step 8: Summary
    Write-Host ""
    Write-Host "==============================================" -ForegroundColor Cyan
    Write-Host "Validation Summary" -ForegroundColor Cyan
    Write-Host "==============================================" -ForegroundColor Cyan
    Write-Host "[OK] EF Core tools installed" -ForegroundColor Green
    Write-Host "[OK] $migrationCount migrations found" -ForegroundColor Green
    Write-Host "[OK] No duplicate migrations" -ForegroundColor Green
    Write-Host "[OK] All migration files present" -ForegroundColor Green
    Write-Host "[OK] Migration snapshot valid" -ForegroundColor Green

    if ($CheckPending) {
        Write-Host "[OK] No pending model changes" -ForegroundColor Green
    }

    if ($GenerateScript) {
        Write-Host "[OK] Migration script generated" -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "Migration validation completed successfully!" -ForegroundColor Green
    exit 0
} finally {
    Pop-Location
}
