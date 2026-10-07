#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'MigrationInventory.ps1')

$first = '20260101000000_First'
$second = '20260102000000_Second'
$result = [pscustomobject]@{
    ExitCode = 0
    TimedOut = $false
    Output = "Build skipped.`n$first (Applied)`n$second (Pending)`n"
}
$inventory = @(Get-VerifiedMigrationInventory -Result $result)
if (($inventory -join ',') -ne "$first,$second") { throw 'EF migration IDs were not preserved.' }
foreach ($scenario in @(
    [pscustomobject]@{ ExitCode = 0; TimedOut = $true; Output = $result.Output },
    [pscustomobject]@{ ExitCode = 42; TimedOut = $false; Output = $result.Output },
    [pscustomobject]@{ ExitCode = 0; TimedOut = $false; Output = 'No migrations found.' }
)) {
    $rejected = $false
    try { Get-VerifiedMigrationInventory -Result $scenario | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'An incomplete or failed EF inventory was accepted.' }
}
Write-Host 'EF failure, timeout (even with partial IDs), and empty inventories fail closed.'

$fixture = Join-Path ([IO.Path]::GetTempPath()) "conduit-migration-inventory-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    foreach ($migration in $inventory) {
        Set-Content -LiteralPath (Join-Path $fixture "$migration.cs") -Value '// fixture'
        Set-Content -LiteralPath (Join-Path $fixture "$migration.Designer.cs") -Value '// fixture'
    }
    $archive = New-Item -ItemType Directory -Path (Join-Path $fixture 'Archive')
    Set-Content -LiteralPath (Join-Path $archive.FullName '20250101000000_Archived.cs.txt') -Value '// never discoverable'
    Assert-MigrationSourceInventory -Migrations $inventory -MigrationsPath $fixture
    $orphan = Join-Path $fixture '20250101000000_Orphan.cs'
    Set-Content -LiteralPath $orphan -Value '// missing EF metadata'
    $rejected = $false
    try { Assert-MigrationSourceInventory -Migrations $inventory -MigrationsPath $fixture } catch { $rejected = $true }
    if (-not $rejected) { throw 'An undiscoverable migration source was accepted.' }
    Remove-Item -LiteralPath $orphan
    Remove-Item -LiteralPath (Join-Path $fixture "$first.Designer.cs")
    $rejected = $false
    try { Assert-MigrationSourceInventory -Migrations $inventory -MigrationsPath $fixture } catch { $rejected = $true }
    if (-not $rejected) { throw 'A migration with a missing designer was accepted.' }
    Write-Host 'Active sources must match EF; archived drafts do not enter the inventory.'
} finally {
    # Delete only the GUID-named fixture created above, within the temp directory.
    $resolvedFixture = (Resolve-Path -LiteralPath $fixture).Path
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $resolvedFixture.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar) -or
        (Split-Path $resolvedFixture -Leaf) -notlike 'conduit-migration-inventory-*') { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
