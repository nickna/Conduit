# Read and replay retained original OCI archive association evidence.
# Loading this module defines pure readers only; it performs no I/O.
. (Join-Path $PSScriptRoot 'native-image-oci-association.ps1')

function Test-NativeImageOciEvidenceValueEqual {
    param($Left, $Right, [int] $Depth = 0)
    if ($Depth -gt 64) { throw 'Native image OCI evidence comparison exceeds its depth bound.' }
    if ($null -eq $Left -or $null -eq $Right) { return $null -eq $Left -and $null -eq $Right }
    if ($Left -is [Collections.IDictionary] -or $Right -is [Collections.IDictionary]) {
        if ($Left -isnot [Collections.IDictionary] -or $Right -isnot [Collections.IDictionary] -or
            $Left.Count -ne $Right.Count) { return $false }
        $leftObject = Get-NativeOciObject $Left
        $rightObject = Get-NativeOciObject $Right
        foreach ($key in $leftObject.Keys) {
            if (-not $rightObject.ContainsKey($key) -or
                -not (Test-NativeImageOciEvidenceValueEqual $leftObject[$key] $rightObject[$key] ($Depth + 1))) {
                return $false
            }
        }
        return $true
    }
    if ($Left -is [array] -or $Right -is [array]) {
        if ($Left -isnot [array] -or $Right -isnot [array] -or $Left.Count -ne $Right.Count) { return $false }
        for ($index = 0; $index -lt $Left.Count; $index++) {
            if (-not (Test-NativeImageOciEvidenceValueEqual $Left[$index] $Right[$index] ($Depth + 1))) {
                return $false
            }
        }
        return $true
    }
    if ($Left -is [string] -or $Right -is [string]) {
        return $Left -is [string] -and $Right -is [string] -and $Left -ceq $Right
    }
    if ($Left -is [bool] -or $Right -is [bool]) {
        return $Left -is [bool] -and $Right -is [bool] -and $Left -eq $Right
    }
    $numericTypes = @([byte], [short], [int], [long], [double], [decimal])
    if ($Left.GetType() -in $numericTypes -and $Right.GetType() -in $numericTypes) { return $Left -eq $Right }
    return $false
}

function Read-NativeImageOciEvidenceDocument([string] $Path) {
    Assert-NativeEvidenceRegularFile $Path
    $file = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(90))
    try {
        if ($file.Length -le 0 -or $file.Length -gt 4194304) {
            throw 'Native image OCI evidence document exceeds its bound.'
        }
        $bytes = [byte[]]::new([int]$file.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $deadline.Token.ThrowIfCancellationRequested()
            $read = $file.ReadAsync([Memory[byte]]::new($bytes, $offset, $bytes.Length - $offset),
                $deadline.Token).AsTask().GetAwaiter().GetResult()
            if ($read -le 0) { throw 'Native image OCI evidence document is truncated.' }
            $offset += $read
        }
        if ($file.Length -ne $bytes.Length) { throw 'Native image OCI evidence document changed while reading.' }
        return ,[ordered]@{
            value = Read-NativeOciJson $bytes
            sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
            bytes = [long]$bytes.Length
        }
    }
    finally { $deadline.Dispose(); $file.Dispose() }
}

function Get-NativeImageOciEvidenceReference($Value, [string] $ExpectedPath, [bool] $Archive = $false) {
    $keys = if ($Archive) { @('path', 'sha256', 'bytes') } else { @('path', 'sha256') }
    $reference = Get-NativeOciObject $Value $keys $keys
    if ($reference['path'] -isnot [string] -or $reference['path'] -cne $ExpectedPath -or
        $reference['sha256'] -isnot [string] -or $reference['sha256'] -cnotmatch '\A[0-9a-f]{64}\z') {
        throw 'Native image OCI evidence reference is invalid.'
    }
    if ($Archive) {
        Assert-NativeOciInteger $reference['bytes'] $true
        if ($reference['bytes'] -gt 4294967296L) { throw 'Native image OCI evidence archive exceeds its bound.' }
    }
    return ,$reference
}

function Assert-NativeImageOciEvidenceReport($Value) {
    $keys = @('schemaVersion', 'kind', 'associationStatus', 'requestedImage', 'requestedDigest', 'requestedDigestKind',
        'observedLocalImageId', 'localIdKind', 'containerId', 'rootDescriptor', 'selectedManifestDescriptor', 'configDigest',
        'layoutIndexSha256', 'archiveSha256', 'archiveBytes', 'selectedLayers', 'descriptorChainVerified', 'holdReasons',
        'registryDigestQualification')
    $report = Get-NativeOciObject $Value $keys $keys
    Assert-NativeOciInteger $report['schemaVersion'] $true
    Assert-NativeOciInteger $report['archiveBytes'] $true
    if ($report['schemaVersion'] -ne 1 -or $report['kind'] -isnot [string] -or
        $report['kind'] -cne 'conduit-native-image-oci-association' -or
        $report['associationStatus'] -isnot [string] -or $report['associationStatus'] -cnotin @('verified', 'hold') -or
        $report['requestedImage'] -isnot [string] -or $report['observedLocalImageId'] -isnot [string] -or
        $report['observedLocalImageId'] -cnotmatch '\Asha256:[0-9a-f]{64}\z' -or
        $report['containerId'] -isnot [string] -or $report['containerId'] -cnotmatch '\A[0-9a-f]{64}\z' -or
        $report['archiveSha256'] -isnot [string] -or $report['archiveSha256'] -cnotmatch '\A[0-9a-f]{64}\z' -or
        $report['descriptorChainVerified'] -isnot [bool] -or $report['registryDigestQualification'] -isnot [bool] -or
        $report['registryDigestQualification'] -or $report['selectedLayers'] -isnot [array] -or
        $report['holdReasons'] -isnot [array]) { throw 'Native image OCI association report shape is invalid.' }
    foreach ($key in @('requestedDigest', 'configDigest')) {
        if ($null -ne $report[$key] -and ($report[$key] -isnot [string] -or
                $report[$key] -cnotmatch '\Asha256:[0-9a-f]{64}\z')) {
            throw 'Native image OCI association report digest is invalid.'
        }
    }
    if ($null -ne $report['layoutIndexSha256'] -and ($report['layoutIndexSha256'] -isnot [string] -or
            $report['layoutIndexSha256'] -cnotmatch '\A[0-9a-f]{64}\z')) {
        throw 'Native image OCI association wrapper hash is invalid.'
    }
    foreach ($key in @('requestedDigestKind', 'localIdKind')) {
        if ($null -ne $report[$key] -and ($report[$key] -isnot [string] -or $report[$key] -cnotin @('index', 'manifest'))) {
            throw 'Native image OCI association digest role is invalid.'
        }
    }
    foreach ($key in @('rootDescriptor', 'selectedManifestDescriptor')) {
        if ($null -ne $report[$key]) { $null = Get-NativeOciDescriptor $report[$key] }
    }
    foreach ($layer in $report['selectedLayers']) { $null = Get-NativeOciDescriptor $layer }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($reason in $report['holdReasons']) {
        if ($reason -isnot [string] -or $reason -cnotin @('missing-image-descriptor', 'missing-container-manifest-descriptor',
                'classic-save-layout', 'missing-oci-layout', 'missing-layout-index', 'missing-root-descriptor',
                'missing-selected-content') -or -not $seen.Add($reason)) {
            throw 'Native image OCI association HOLD reasons are invalid.'
        }
    }
    return ,$report
}

function Read-NativeImageOciEvidenceAssociation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Directory,
        [Parameter(Mandatory = $true)] $Reference,
        [Parameter(Mandatory = $true)][string] $RequestedImage,
        [Parameter(Mandatory = $true)][string] $LocalImageId,
        [Parameter(Mandatory = $true)][string] $ContainerId
    )
    $ErrorActionPreference = 'Stop'
    $root = Get-NativeEvidenceFullPath $Directory
    Assert-NativeEvidencePathAncestry $root
    if (-not [IO.Directory]::Exists($root) -or
        ($IsLinux -and (Get-NativeEvidenceUnixFileType $root) -ne 0x4000) -or
        $LocalImageId -cnotmatch '\Asha256:[0-9a-f]{64}\z' -or $ContainerId -cnotmatch '\A[0-9a-f]{64}\z') {
        throw 'Native image OCI evidence directory or identity is invalid.'
    }
    $envelope = Get-NativeOciObject $Reference @('report', 'observations', 'archive') @('report', 'observations', 'archive')
    $reportReference = Get-NativeImageOciEvidenceReference $envelope['report'] 'oci-association.json'
    $observationsReference = Get-NativeImageOciEvidenceReference $envelope['observations'] 'image-observations.json'
    $archiveReference = Get-NativeImageOciEvidenceReference $envelope['archive'] 'image-save.tar' $true
    $reportPath = [IO.Path]::Combine($root, 'oci-association.json')
    $observationsPath = [IO.Path]::Combine($root, 'image-observations.json')
    $archivePath = [IO.Path]::Combine($root, 'image-save.tar')
    $retainedReport = Read-NativeImageOciEvidenceDocument $reportPath
    $retainedObservations = Read-NativeImageOciEvidenceDocument $observationsPath
    if ($retainedReport['sha256'] -cne $reportReference['sha256'] -or
        $retainedObservations['sha256'] -cne $observationsReference['sha256']) {
        throw 'Native image OCI evidence document does not match its reference.'
    }
    $observationKeys = @('schemaVersion', 'kind', 'requestedImage', 'image', 'container')
    $observations = Get-NativeOciObject $retainedObservations['value'] $observationKeys $observationKeys
    Assert-NativeOciInteger $observations['schemaVersion'] $true
    $image = Get-NativeOciObject $observations['image'] @('id', 'descriptor') @('id', 'descriptor')
    $containerKeys = @('id', 'image', 'imageManifestDescriptor')
    $container = Get-NativeOciObject $observations['container'] $containerKeys $containerKeys
    foreach ($descriptor in @($image['descriptor'], $container['imageManifestDescriptor'])) {
        if ($null -ne $descriptor) {
            $null = Get-NativeOciObject $descriptor @('mediaType', 'digest', 'size', 'platform') @('mediaType', 'digest', 'size')
        }
    }
    if ($observations['schemaVersion'] -ne 1 -or $observations['kind'] -isnot [string] -or
        $observations['kind'] -cne 'conduit-native-image-observations' -or
        $observations['requestedImage'] -isnot [string] -or $observations['requestedImage'] -cne $RequestedImage -or
        $image['id'] -isnot [string] -or $image['id'] -cne $LocalImageId -or
        $container['id'] -isnot [string] -or $container['id'] -cne $ContainerId -or
        $container['image'] -isnot [string] -or $container['image'] -cne $LocalImageId) {
        throw 'Native image OCI evidence observations disagree with packaging identity.'
    }
    $report = Assert-NativeImageOciEvidenceReport $retainedReport['value']
    Assert-NativeEvidenceRegularFile $archivePath
    if ([IO.FileInfo]::new($archivePath).Length -ne $archiveReference['bytes']) {
        throw 'Native image OCI evidence archive bytes disagree with its reference.'
    }
    $replayArguments = @{
        ArchivePath = $archivePath; RequestedImage = $RequestedImage
        ImageObservation = $image; ContainerObservation = $container
    }
    $replayed = Get-NativeImageOciArchiveAssociation @replayArguments
    if ($replayed['archiveSha256'] -cne $archiveReference['sha256'] -or
        $replayed['archiveBytes'] -ne $archiveReference['bytes'] -or
        -not (Test-NativeImageOciEvidenceValueEqual $report $replayed)) {
        throw 'Native image OCI association does not replay from the retained original bytes.'
    }
    # Reject replacement of either small document during the archive replay.
    $finalReport = Read-NativeImageOciEvidenceDocument $reportPath
    $finalObservations = Read-NativeImageOciEvidenceDocument $observationsPath
    if ($finalReport['sha256'] -cne $reportReference['sha256'] -or
        $finalObservations['sha256'] -cne $observationsReference['sha256']) {
        throw 'Native image OCI evidence documents changed during replay.'
    }
    return ,$replayed
}
