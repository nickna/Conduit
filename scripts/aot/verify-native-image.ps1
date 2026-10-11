[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Image,
    [Parameter(Mandatory)][ValidateSet('admin', 'http', 'gateway')][string] $Service,
    [string] $ArtifactDirectory
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-image-verification.ps1')

$packagingService = if ($Service -eq 'admin') { 'admin' } else { 'gateway' }
$smokeService = if ($packagingService -eq 'admin') { 'admin' } else { 'http' }
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path (Get-Location).Path (
        'artifacts/native-containers/' + $smokeService + '/packaging-' + [Guid]::NewGuid().ToString('N'))
}

$results = @(Get-NativeImagePackagingEvidence -Image $Image -Service $packagingService -ArtifactDirectory $ArtifactDirectory)
if ($results.Count -ne 1) { throw 'Native image packaging verification returned an invalid result.' }
$packaging = $results[0]
Write-Host ('Verified native image packaging; service=' + $packagingService +
    '; localImageId=' + $packaging.localImageId + '; cleanupConfirmed=true.')

node (Join-Path $PSScriptRoot 'native-container-smoke.mjs') $packaging.localImageId $smokeService
if ($LASTEXITCODE -ne 0) { throw 'Packaged native entrypoint smoke failed.' }
