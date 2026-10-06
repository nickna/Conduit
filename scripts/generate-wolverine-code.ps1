[CmdletBinding()]
param(
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$generatedPaths = @(
    'Services/ConduitLLM.Gateway/Internal/Generated',
    'Services/ConduitLLM.Admin/Internal/Generated'
)

$defaults = @{
    'ConduitLLM__Messaging__Backend' = 'Wolverine'
    'ConduitLLM__Messaging__Wolverine__Transport' = 'Postgresql'
    'CONDUIT_MIGRATION_MODE' = 'Skip'
    'ASPNETCORE_ENVIRONMENT' = 'Production'
    'DATABASE_URL' = 'postgresql://conduit:conduitpass@localhost:5432/conduitdb'
    'REDIS_URL' = 'redis://localhost:6379'
}

foreach ($entry in $defaults.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($entry.Key))) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
    }
}

# The Windows Event Log provider may be unavailable to an unprivileged developer.
if ($IsWindows) {
    [Environment]::SetEnvironmentVariable('Logging__EventLog__LogLevel__Default', 'None')
}

$projects = @(
    'Services/ConduitLLM.Gateway/ConduitLLM.Gateway.csproj',
    'Services/ConduitLLM.Admin/ConduitLLM.Admin.csproj'
)
$artifactRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'conduit-wolverine-codegen'

Push-Location $repoRoot
try {
    foreach ($project in $projects) {
        Write-Host "Generating Wolverine adapters for $project"

        # Keep the generation-only RuntimeCompilation dependency out of the
        # ordinary obj/bin trees used by builds and production publishes.
        # Build the shared project graph serially before launching the generator.
        # Parallel rebuilds can race while cleaning shared outputs on newer SDKs,
        # producing a silent build failure before JasperFx starts.
        & dotnet build `
            $project `
            --configuration Release `
            --nologo `
            --tl:off `
            --verbosity minimal `
            --artifacts-path $artifactRoot `
            --maxcpucount:1 `
            -p:ConduitWolverineCodegen=true `
            -p:UseSharedCompilation=false

        if ($LASTEXITCODE -ne 0) {
            throw "Wolverine code generation build failed for $project."
        }

        & dotnet run `
            --project $project `
            --configuration Release `
            --no-build `
            --no-launch-profile `
            --artifacts-path $artifactRoot `
            -p:ConduitWolverineCodegen=true `
            -- codegen write --log-level Warning

        if ($LASTEXITCODE -ne 0) {
            throw "Wolverine code generation failed for $project."
        }
    }

    # JasperFx emits indentation on otherwise blank lines. Normalize only that
    # generator noise so the committed source also passes Git's whitespace checks.
    foreach ($generatedPath in $generatedPaths) {
        Get-ChildItem $generatedPath -Recurse -Filter '*.cs' | ForEach-Object {
            $lines = [System.Collections.Generic.List[string]]::new()
            foreach ($line in [System.IO.File]::ReadAllLines($_.FullName)) {
                $lines.Add($line.TrimEnd())
            }
            while ($lines.Count -gt 0 -and $lines[$lines.Count - 1].Length -eq 0) {
                $lines.RemoveAt($lines.Count - 1)
            }
            [System.IO.File]::WriteAllLines(
                $_.FullName,
                $lines,
                [System.Text.UTF8Encoding]::new($false))
        }
    }

    if ($Verify) {
        $status = @(& git status --short --untracked-files=all -- $generatedPaths)
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to inspect generated Wolverine files with git status.'
        }

        if ($status.Count -gt 0) {
            Write-Host 'Committed Wolverine adapters are stale:'
            $status | ForEach-Object { Write-Host $_ }
            throw "Run './scripts/generate-wolverine-code.ps1' and commit the generated files."
        }

        Write-Host 'Wolverine generated-code drift check passed.'
    }
}
finally {
    Pop-Location
}
