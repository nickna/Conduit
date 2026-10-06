# EF Core Migration Tools

This directory contains tools for validating and managing Entity Framework Core migrations in the Conduit project.

## Problem Statement

The EF Core migration validation was failing in GitHub Actions because:
1. The `DATABASE_URL` environment variable wasn't being passed to the `dotnet ef migrations script` command
2. Error messages weren't clear about what was wrong
3. Environment setup was inconsistent across workflow steps

## Solution

We've created a robust, maintainable solution using the existing .NET toolchain:

### 1. **GitHub Actions Workflow Fix** (`migration-validation.yml`)
- Moved `DATABASE_URL` to job-level environment variables for consistency
- Added pre-flight validation step to catch missing configuration early
- Removed duplicate environment variable declarations from individual steps

### 2. **EF Wrapper Script** (`ef-wrapper.ps1`)
- Validates environment before running EF commands
- Provides clear, colored output with detailed error messages
- Tests database connectivity
- Analyzes common error patterns and suggests fixes

### 3. **Enhanced Validation Script** (`validate-migrations.ps1`)
- Already worked well, now integrates with ef-wrapper for better error handling
- Validates migration files, checks for duplicates, and detects pending changes
- Falls back gracefully when database is unavailable

### 4. **Comprehensive Test Suite** (`test-migration-tools.ps1`)
- Tests all components in various scenarios
- Validates error handling and edge cases
- Ensures scripts provide helpful feedback

## Usage

### Running Migration Validation
```powershell
# Basic validation
./scripts/migrations/validate-migrations.ps1

# Check for pending model changes (CI mode)
./scripts/migrations/validate-migrations.ps1 -CheckPending

# Generate migration script
./scripts/migrations/validate-migrations.ps1 -GenerateScript
```

### Using the EF Wrapper
```powershell
cd Shared/ConduitLLM.Configuration

# List migrations with enhanced error handling
../scripts/migrations/ef-wrapper.ps1 migrations list

# Generate migration script
../scripts/migrations/ef-wrapper.ps1 migrations script -o output.sql

# Add a new migration
../scripts/migrations/ef-wrapper.ps1 migrations add MigrationName
```

### Testing the Tools
```powershell
# Run comprehensive test suite
./scripts/migrations/test-migration-tools.ps1
```

## Environment Requirements

- `DATABASE_URL`: PostgreSQL connection string (required)
  - Format: `postgresql://user:password@host:port/database`
- .NET 10.0 SDK
- EF Core tools: `dotnet tool install --global dotnet-ef`
- PowerShell Core 7+ (cross-platform)

### Opting into EF build tooling

EF Design and MSBuild task assemblies are excluded from normal service restores and
production publishes. Enable them only for the shell that runs migration tooling:

```powershell
$env:ConduitEfTooling = 'true'
dotnet restore Conduit.slnx
dotnet ef migrations list --project Shared/ConduitLLM.Configuration
```

Remove the environment variable after finishing with `Remove-Item Env:ConduitEfTooling`.

## Why Not Python?

When challenged to think critically about the solution, we determined that Python would be overengineering because:

1. **Existing Tools Work Well**: The PowerShell scripts and dotnet-ef tools are sufficient
2. **Root Cause Was Simple**: Missing environment variable in one workflow step
3. **Stay in Ecosystem**: Adding Python introduces unnecessary complexity to a .NET project
4. **Better Error Handling**: We can enhance existing tools rather than rewrite them

## Key Features

1. **Consistent Environment Handling**: Job-level variables in GitHub Actions
2. **Clear Error Messages**: Wrapper script provides context and solutions
3. **Graceful Degradation**: Scripts work even when database is unavailable
4. **Comprehensive Testing**: Test suite validates all components
5. **No Over-Engineering**: Simple, maintainable solution using existing tools
6. **Cross-Platform**: PowerShell Core works on Windows, Linux, and macOS

## Troubleshooting

### "DATABASE_URL environment variable is not set"
Set the DATABASE_URL:
```powershell
$env:DATABASE_URL = "postgresql://user:password@localhost:5432/conduitdb"
```

### "Not in ConduitLLM.Configuration directory"
Navigate to the correct directory:
```powershell
cd Shared/ConduitLLM.Configuration
```

### "EF Core tools not installed"
Install the tools:
```powershell
dotnet tool install --global dotnet-ef
```

## Maintenance

The solution is designed to be maintainable:
- Scripts use clear variable names and comments
- Error messages guide users to solutions
- Test suite ensures changes don't break functionality
- No external dependencies beyond .NET ecosystem
