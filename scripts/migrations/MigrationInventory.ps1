function Get-VerifiedMigrationInventory {
    param([Parameter(Mandatory)]$Result)

    if ($Result.TimedOut -or $Result.ExitCode -ne 0) {
        throw "EF migration inventory failed (timeout=$($Result.TimedOut), exit=$($Result.ExitCode)). Rebuild with ConduitEfTooling=true and retry; filesystem sources are not an authoritative inventory."
    }

    $migrations = @($Result.Output -split "`r?`n" | ForEach-Object { $_.Trim() } |
        Where-Object { $_ -match '^\d{14}_' } |
        ForEach-Object { $_ -replace ' \((Pending|Applied)\)$', '' })
    if ($migrations.Count -eq 0) {
        throw 'EF discovered no migrations; validation cannot substitute filesystem sources.'
    }
    return $migrations
}

function Assert-MigrationSourceInventory {
    param(
        [Parameter(Mandatory)][string[]]$Migrations,
        [Parameter(Mandatory)][string]$MigrationsPath
    )

    $sources = @(Get-ChildItem -LiteralPath $MigrationsPath -Filter '*.cs' -File |
        Where-Object { $_.Name -match '^\d{14}_' -and $_.Name -notmatch '\.Designer\.cs$' })
    $undiscovered = @($sources | Where-Object { $_.BaseName -notin $Migrations })
    if ($undiscovered.Count -gt 0) {
        throw "Migration sources are not discoverable by EF: $($undiscovered.BaseName -join ', '). Reconcile drafts deliberately; do not add historical migration metadata to deployed databases."
    }
    foreach ($migration in $Migrations) {
        foreach ($suffix in @('.cs', '.Designer.cs')) {
            if (-not (Test-Path -LiteralPath (Join-Path $MigrationsPath "$migration$suffix") -PathType Leaf)) {
                throw "Missing migration file: $migration$suffix"
            }
        }
    }
}
