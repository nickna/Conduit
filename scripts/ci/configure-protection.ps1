[CmdletBinding()]
param([string]$Repository = 'nickna/Conduit', [switch]$Apply)
$ErrorActionPreference = 'Stop'
$policy = Join-Path $PSScriptRoot 'branch-protection.json'
foreach ($branch in @('master', 'dev')) {
    if ($Apply) {
        gh api --method PUT "repos/$Repository/branches/$branch/protection" --input $policy
        if ($LASTEXITCODE -ne 0) { throw "Cannot enforce protection on $branch" }
    }
    gh api "repos/$Repository/branches/$branch/protection"
    if ($LASTEXITCODE -ne 0) { throw "Cannot read protection on $branch" }
}
