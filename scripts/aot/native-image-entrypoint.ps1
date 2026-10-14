[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Image,
    [Parameter(Mandatory = $true)][ValidateSet('admin', 'http', 'gateway')][string] $Service,
    [string] $ArtifactDirectory
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'native-image-runtime-evidence.ps1')
$claim = $null
try {
    $selectedService = if ($Service -ieq 'http') { 'gateway' } else { $Service.ToLowerInvariant() }
    if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
        $repository = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '..', '..'))
        $ArtifactDirectory = [IO.Path]::Combine($repository, 'artifacts', 'native-containers', $selectedService,
            [Guid]::NewGuid().ToString('N'))
    }
    $root = Get-NativeEvidenceFullPath $ArtifactDirectory
    Assert-NativeImageArchiveOutputEmpty $root
    [IO.Directory]::CreateDirectory($root) | Out-Null
    Assert-NativeImageArchiveOutputEmpty $root
    $claimPath = [IO.Path]::Combine($root, 'native-image-entrypoint.claim')
    Assert-NativeEvidencePathAncestry $claimPath
    $claim = [IO.FileStream]::new($claimPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $claim.Write([Text.UTF8Encoding]::new($false).GetBytes([Guid]::NewGuid().ToString('N') + "`n"))
    $claim.Flush($true)
    foreach ($entry in ([IO.DirectoryInfo]::new($root)).EnumerateFileSystemInfos()) {
        if ($entry.Name -cne 'native-image-entrypoint.claim' -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Native image entrypoint parent artifact directory changed.'
        }
    }
    $packagingDirectory = [IO.Path]::Combine($root, 'packaging')
    $runtimeDirectory = [IO.Path]::Combine($root, 'runtime')
    $packaging = Get-NativeImagePackagingEvidence -Image $Image -Service $selectedService -ArtifactDirectory $packagingDirectory
    $runtime = Get-NativeImageRuntimeEvidence -PackagingDirectory $packagingDirectory -ArtifactDirectory $runtimeDirectory
    if ($packaging['localImageId'] -cne $runtime['id'] -or -not $runtime['cleanupConfirmed'] -or -not $runtime['succeeded']) {
        throw 'Native image entrypoint evidence did not retain its packaged identity.'
    }
    Write-Output ('Verified packaged native entrypoint; service=' + $selectedService + '; localImageId=' + $runtime['id'] +
        '; paths=' + $runtime['paths'] + '; cleanupConfirmed=true; evidence=' + [IO.Path]::Combine($runtimeDirectory, 'evidence.json') + '.')
}
catch {
    [Console]::Error.WriteLine('Native image entrypoint verification failed; type=' + $_.Exception.GetType().FullName +
        '; line=' + $_.InvocationInfo.ScriptLineNumber + '.')
    exit 1
}
finally { if ($null -ne $claim) { $claim.Dispose() } }
