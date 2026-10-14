[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Image,
    [Parameter(Mandatory)][ValidateSet('admin', 'http', 'gateway')][string] $Service,
    [string] $ArtifactDirectory
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'native-image-entrypoint.ps1') -Image $Image -Service $Service -ArtifactDirectory $ArtifactDirectory
$driverSucceeded = $?
if (-not $driverSucceeded) { throw 'Native image entrypoint verification failed.' }
