# Pure retained-image qualification for benchmark collection and promotion.
# Loading this file performs no writes or Docker operations.
. (Join-Path $PSScriptRoot 'native-image-runtime-evidence.ps1')

function Assert-NativeBenchmarkImageEvidenceValueEqual($Expected, $Actual, [int] $Depth = 0) {
    if ($Depth -gt 64) { throw 'Native benchmark image binding exceeds its depth bound.' }
    if ($null -eq $Expected) {
        if ($null -ne $Actual) { throw 'Native benchmark image binding differs.' }
        return
    }
    if ($Expected -is [Collections.IDictionary] -or $Expected -is [Management.Automation.PSCustomObject]) {
        $expectedObject = Get-NativeEvidenceObject $Expected
        $actualObject = Get-NativeEvidenceObject $Actual
        if ($expectedObject.Count -ne $actualObject.Count) { throw 'Native benchmark image binding fields differ.' }
        foreach ($key in $expectedObject.Keys) {
            if (-not $actualObject.ContainsKey($key)) { throw 'Native benchmark image binding fields differ.' }
            Assert-NativeBenchmarkImageEvidenceValueEqual $expectedObject[$key] $actualObject[$key] ($Depth + 1)
        }
        return
    }
    if ($Expected -is [array]) {
        if ($Actual -isnot [array] -or $Expected.Count -ne $Actual.Count) { throw 'Native benchmark image binding array differs.' }
        for ($index = 0; $index -lt $Expected.Count; $index++) {
            Assert-NativeBenchmarkImageEvidenceValueEqual $Expected[$index] $Actual[$index] ($Depth + 1)
        }
        return
    }
    if ($Expected -is [string]) {
        if ($Actual -isnot [string] -or -not [string]::Equals($Expected, $Actual, [StringComparison]::Ordinal)) {
            throw 'Native benchmark image binding text differs.'
        }
        return
    }
    if ($Expected -is [bool]) {
        if ($Actual -isnot [bool] -or $Expected -ne $Actual) { throw 'Native benchmark image binding boolean differs.' }
        return
    }
    $integerTypes = @([sbyte], [byte], [int16], [uint16], [int32], [uint32], [int64], [uint64])
    if ($Expected.GetType() -in $integerTypes) {
        if ($null -eq $Actual -or $Actual.GetType() -notin $integerTypes -or [decimal]$Expected -ne [decimal]$Actual) {
            throw 'Native benchmark image binding integer differs.'
        }
        return
    }
    throw 'Native benchmark image binding value type is invalid.'
}

function Assert-NativeBenchmarkImageEvidenceEqual {
    param([Parameter(Mandatory)][AllowNull()]$Expected, [Parameter(Mandatory)][AllowNull()]$Actual)
    try { Assert-NativeBenchmarkImageEvidenceValueEqual $Expected $Actual }
    catch { throw 'Native benchmark image binding differs from retained evidence.' }
}

function Read-NativeBenchmarkImageEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][ValidateSet('admin', 'gateway')][string]$Service,
        [Parameter(Mandatory)][string]$NativeImage
    )
    $Service = $Service.ToLowerInvariant()
    $ErrorActionPreference = 'Stop'
    try {
        $reference = [regex]::Match($NativeImage,
            '\A(?:[a-z0-9]+(?:[.-][a-z0-9]+)*(?::[0-9]+)?/)?[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*@(?<digest>sha256:[0-9a-f]{64})\z')
        if (-not $reference.Success) { throw 'Native benchmark image reference is invalid.' }
        $packaging = Get-NativeImageRuntimePackaging $Directory
        $package = $packaging['report']; $association = $packaging['ociAssociation']
        if ($package['schemaVersion'] -ne 2 -or $package['service'] -cne $Service -or $package['rid'] -cne 'linux-x64' -or
            $package['requestedImage'] -cne $NativeImage -or $association['requestedImage'] -cne $NativeImage -or
            $association['associationStatus'] -cne 'verified' -or $association['descriptorChainVerified'] -isnot [bool] -or
            -not $association['descriptorChainVerified'] -or $association['holdReasons'].Count -ne 0 -or
            $association['requestedDigest'] -cne $reference.Groups['digest'].Value -or
            $association['requestedDigestKind'] -cnotin @('index', 'manifest')) {
            throw 'Native benchmark image has no verified candidate binding.'
        }
        $source = $package['source']
        if ($source['status'] -cne 'git' -or $source['dirty'] -isnot [bool] -or $source['dirty'] -or
            $source['revision'] -isnot [string] -or $source['revision'] -cnotmatch '\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z' -or
            $source['revision'] -cne $package['commitMetadata']['commitSha'] -or
            $source['revision'] -cne $package['ociLabels']['revision']) {
            throw 'Native benchmark image source is not a clean Git revision.'
        }
        $directoryRoot = $packaging['directory']
        $evidence = [IO.Path]::Combine($directoryRoot, 'extracted', 'conduit-evidence')
        $application = Read-NativeImageEvidenceDocument ([IO.Path]::Combine($evidence, 'application.json'))
        $health = Read-NativeImageEvidenceDocument ([IO.Path]::Combine($evidence, 'health-probe.json'))
        $packagingReference = [ordered]@{ path = 'packaging-evidence.json'; sha256 = $packaging['sha256'] }
        $identity = [ordered]@{
            schemaVersion = 1; kind = 'conduit-native-benchmark-image'; service = $Service; rid = 'linux-x64'
            requestedImage = $NativeImage; requestedDigest = $association['requestedDigest']; requestedDigestKind = $association['requestedDigestKind']
            localImageId = $package['localImageId']; rootDescriptor = $association['rootDescriptor']
            selectedManifestDescriptor = $association['selectedManifestDescriptor']; configDigest = $association['configDigest']
            buildId = $package['buildId']; source = $source; packagingEvidence = $packagingReference
            runtimeFilesSha256 = $package['runtimeFilesSha256']; manifestHashes = $package['manifestHashes']
            application = [ordered]@{ executable = $application['executable']; toolchain = $application['toolchain'] }
            healthProbe = [ordered]@{ executable = $health['executable']; toolchain = $health['toolchain'] }
        }
        $binding = [ordered]@{
            schemaVersion = 1; kind = 'conduit-native-benchmark-image-binding'; packagingEvidence = $packagingReference
            buildId = $package['buildId']; sourceRevision = $source['revision']; requestedImage = $NativeImage
            requestedDigest = $association['requestedDigest']; requestedDigestKind = $association['requestedDigestKind']
            observedLocalImageId = $package['localImageId']; selectedManifestDescriptor = $association['selectedManifestDescriptor']
        }
        $metrics = $package['metrics']
        $nativePublish = [ordered]@{
            service = if ($Service -eq 'admin') { 'Admin' } else { 'Gateway' }; rid = 'linux-x64'
            publishDurationMilliseconds = $metrics['publishDurationMilliseconds']
            applicationPublishDurationMilliseconds = $metrics['applicationPublishDurationMilliseconds']
            healthProbePublishDurationMilliseconds = $metrics['healthProbePublishDurationMilliseconds']
            executableBytes = $metrics['executableBytes']; runtimeArtifactBytes = $metrics['runtimeArtifactBytes']; imageBinding = $binding
        }
        $pullBytes = 0L
        foreach ($layer in $association['selectedLayers']) { $pullBytes += [long]$layer['size'] }
        if ($pullBytes -le 0) { throw 'Native benchmark image selected layer size is invalid.' }
        # Seal the extra manifest reads too, without reading historical tool
        # executable paths on this inspection host.
        foreach ($name in @('application.json', 'health-probe.json')) {
            if ((Get-NativeEvidenceFileHash (Get-Item -LiteralPath ([IO.Path]::Combine($evidence, $name))))['sha256'] -cne $package['manifestHashes'][$name]) {
                throw 'Native benchmark image manifest changed while reading.'
            }
        }
        if ((Get-NativeEvidenceFileHash (Get-Item -LiteralPath $packaging['path']))['sha256'] -cne $packaging['sha256']) {
            throw 'Native benchmark image packaging changed while reading.'
        }
        return ,[ordered]@{ packaging = $packaging; identity = $identity; binding = $binding; nativePublish = $nativePublish; pullBytes = $pullBytes }
    }
    catch { throw 'Native benchmark image binding could not be verified from retained evidence.' }
}
