#!/usr/bin/env pwsh
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$temporaryRoot = Join-Path $temporaryParent ('conduit-oci-association-contract-' + [Guid]::NewGuid().ToString('N'))
$helper = Join-Path $PSScriptRoot 'native-image-oci-association.ps1'
$indexMediaType = 'application/vnd.oci.image.index.v1+json'
$manifestMediaType = 'application/vnd.oci.image.manifest.v1+json'
$configMediaType = 'application/vnd.oci.image.config.v1+json'
$layerMediaType = 'application/vnd.oci.image.layer.v1.tar'
$gzipLayerMediaType = 'application/vnd.oci.image.layer.v1.tar+gzip'
$cases = 0; $sequence = 0; $failure = $null; $cleanupFailure = $false

function Assert-OciContract([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Get-OciHash([byte[]] $Bytes) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Get-OciFileHash([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    try { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
    finally { $stream.Dispose() }
}

function ConvertTo-OciBytes($Value) {
    return ,[Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Value -Depth 64 -Compress))
}

function Add-OciBlob([Collections.IDictionary] $Blobs, [string] $MediaType, [byte[]] $Bytes, $Platform = $null) {
    $digest = 'sha256:' + (Get-OciHash $Bytes)
    $Blobs[$digest] = $Bytes
    $descriptor = [ordered]@{ mediaType = $MediaType; digest = $digest; size = [long]$Bytes.Length }
    if ($null -ne $Platform) { $descriptor['platform'] = $Platform }
    return $descriptor
}

function Copy-OciObject($Value) {
    return (ConvertTo-Json -InputObject $Value -Depth 64 -Compress) | ConvertFrom-Json -AsHashtable -DateKind String
}

function New-OciLayerBytes([string] $Content = 'original raw layer café 雪 🔎', [string] $Name = 'app/native-fixture') {
    $memory = [IO.MemoryStream]::new()
    $writer = [Formats.Tar.TarWriter]::new($memory, [Formats.Tar.TarEntryFormat]::Pax, $true)
    try {
        $entry = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, $Name)
        $entry.Uid = 1654; $entry.Gid = 1654; $entry.Mode = [IO.UnixFileMode]493
        $body = [IO.MemoryStream]::new([Text.UTF8Encoding]::new($false).GetBytes($Content), $false)
        try { $entry.DataStream = $body; $writer.WriteEntry($entry) }
        finally { $body.Dispose() }
    }
    finally { $writer.Dispose() }
    try { return ,$memory.ToArray() }
    finally { $memory.Dispose() }
}

function Compress-OciLayer([byte[]] $Bytes) {
    $memory = [IO.MemoryStream]::new()
    $gzip = [IO.Compression.GZipStream]::new($memory, [IO.Compression.CompressionLevel]::SmallestSize, $true)
    try { $gzip.Write($Bytes) }
    finally { $gzip.Dispose() }
    try { return ,$memory.ToArray() }
    finally { $memory.Dispose() }
}

function Write-OciArchive([string] $Path, [Collections.IEnumerable] $Entries) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $writer = [Formats.Tar.TarWriter]::new($stream, [Formats.Tar.TarEntryFormat]::Pax, $true)
    try {
        foreach ($item in $Entries) {
            $type = if ($item.Contains('type')) { $item['type'] } else { [Formats.Tar.TarEntryType]::RegularFile }
            $entry = [Formats.Tar.PaxTarEntry]::new($type, $item['path'])
            $entry.Uid = 0; $entry.Gid = 0; $entry.Mode = [IO.UnixFileMode]420
            if ($type -eq [Formats.Tar.TarEntryType]::SymbolicLink -or $type -eq [Formats.Tar.TarEntryType]::HardLink) {
                $entry.LinkName = $item['link']
                $writer.WriteEntry($entry)
            }
            else {
                $body = [IO.MemoryStream]::new([byte[]]$item['bytes'], $false)
                try { $entry.DataStream = $body; $writer.WriteEntry($entry) }
                finally { $body.Dispose() }
            }
        }
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}

function New-OciFixture([string] $Shape, [string] $Scenario) {
    $script:sequence++
    $root = Join-Path $temporaryRoot ('case-' + $script:sequence.ToString('D3') + ' café 雪')
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $blobs = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $platform = [ordered]@{ os = 'linux'; architecture = 'amd64' }
    $rawBytes = New-OciLayerBytes
    $compressedTarBytes = New-OciLayerBytes 'different compressed layer café 雪 🔎' 'app/native-compressed-fixture'
    $compressedBytes = Compress-OciLayer $compressedTarBytes
    $raw = Add-OciBlob $blobs $layerMediaType $rawBytes
    $compressed = Add-OciBlob $blobs $gzipLayerMediaType $compressedBytes
    $configuration = [ordered]@{ architecture = 'amd64'; os = 'linux'; config = @{ Labels = @{ 'org.opencontainers.image.revision' = 'historical-unqualified-fixture' } }
        rootfs = @{ type = 'layers'; diff_ids = @($raw.digest, ('sha256:' + (Get-OciHash $compressedTarBytes))) } }
    if ($Scenario -ceq 'config-platform') { $configuration.architecture = 'arm64' }
    if ($Scenario -ceq 'rootfs-type-array') { $configuration.rootfs.type = @('layers') }
    $config = Add-OciBlob $blobs $configMediaType (ConvertTo-OciBytes $configuration)
    $manifest = [ordered]@{ schemaVersion = 2; mediaType = $manifestMediaType; config = $config; layers = @($raw, $compressed) }
    if ($Scenario -ceq 'layer-count') { $manifest.layers = @($raw) }
    if ($Scenario -ceq 'manifest-media-array') { $manifest.mediaType = @($manifestMediaType) }
    $selected = Add-OciBlob $blobs $manifestMediaType (ConvertTo-OciBytes $manifest) $platform
    $attestationConfig = Add-OciBlob $blobs $configMediaType (ConvertTo-OciBytes @{ os = 'unknown'; architecture = 'unknown'; config = @{}; rootfs = @{ type = 'layers'; diff_ids = @() } })
    $attestation = Add-OciBlob $blobs $manifestMediaType (ConvertTo-OciBytes @{ schemaVersion = 2; mediaType = $manifestMediaType; config = $attestationConfig; layers = @() }) @{ os = 'unknown'; architecture = 'unknown' }
    $attestation['annotations'] = @{ 'vnd.docker.reference.type' = 'attestation-manifest'; 'vnd.docker.reference.digest' = $selected.digest }
    $candidates = @($selected, $attestation)
    if ($Scenario -ceq 'ambiguous') {
        $alternateConfig = Copy-OciObject $configuration
        $alternateConfig.config.Labels['org.opencontainers.image.revision'] = 'other-historical-fixture'
        $alternate = Add-OciBlob $blobs $configMediaType (ConvertTo-OciBytes $alternateConfig)
        $alternateManifest = Add-OciBlob $blobs $manifestMediaType (ConvertTo-OciBytes @{ schemaVersion = 2; mediaType = $manifestMediaType; config = $alternate; layers = @($raw, $compressed) }) $platform
        $candidates += $alternateManifest
    }
    if ($Scenario -ceq 'descriptor-platform') { $selected.platform.architecture = 'arm64' }
    $rootDescriptor = $selected
    if ($Shape -cne 'direct') {
        $indexDocument = @{ schemaVersion = 2; mediaType = $indexMediaType; manifests = $candidates }
        if ($Scenario -ceq 'index-media-array') { $indexDocument.mediaType = @($indexMediaType) }
        $rootDescriptor = Add-OciBlob $blobs $indexMediaType (ConvertTo-OciBytes $indexDocument)
        if ($Shape -ceq 'nested') {
            $rootDescriptor = Add-OciBlob $blobs $indexMediaType (ConvertTo-OciBytes @{ schemaVersion = 2; mediaType = $indexMediaType; manifests = @($rootDescriptor) })
        }
        if ($Scenario -ceq 'depth') {
            for ($depth = 0; $depth -lt 35; $depth++) {
                $rootDescriptor = Add-OciBlob $blobs $indexMediaType (ConvertTo-OciBytes @{ schemaVersion = 2; mediaType = $indexMediaType; manifests = @($rootDescriptor) })
            }
        }
    }
    $wrapper = [ordered]@{ schemaVersion = 2; mediaType = $indexMediaType; manifests = @($rootDescriptor) }
    if ($Scenario -ceq 'wrapper-media-array') { $wrapper.mediaType = @($indexMediaType) }
    $wrapperBytes = ConvertTo-OciBytes $wrapper
    if ($Scenario -ceq 'duplicate-json') {
        $wrapperBytes = [Text.UTF8Encoding]::new($false).GetBytes(([Text.UTF8Encoding]::new($false).GetString($wrapperBytes)).Replace('"schemaVersion":2', '"schemaVersion":2,"schemaVersion":2'))
    }
    if ($Scenario -ceq 'metadata-size') {
        # Valid JSON padded one byte beyond the real 4 MiB metadata bound.
        $wrapperBytes = [Text.UTF8Encoding]::new($false).GetBytes([Text.UTF8Encoding]::new($false).GetString($wrapperBytes) +
            [string]::new([char]32, 4194305 - $wrapperBytes.Length))
    }
    $image = [ordered]@{ id = $rootDescriptor.digest; descriptor = Copy-OciObject $rootDescriptor }
    $container = [ordered]@{ id = 'c' * 64; image = $image.id; imageManifestDescriptor = Copy-OciObject $selected }
    $requested = 'registry.example.invalid/conduit-fixture:historical'
    switch ($Scenario) {
        'requested-root' { $requested = 'registry.example.invalid/conduit-fixture@' + $rootDescriptor.digest }
        'requested-selected' { $requested = 'registry.example.invalid/conduit-fixture@' + $selected.digest }
        'local-ref' { $requested = $rootDescriptor.digest }
        'requested-mismatch' { $requested = 'registry.example.invalid/conduit-fixture@sha256:' + ('d' * 64) }
        'local-mismatch' { $image.id = 'sha256:' + ('d' * 64); $container.image = $image.id }
        'container-image-mismatch' { $container.image = 'sha256:' + ('d' * 64) }
        'container-id' { $container.id = 'not-a-container-id' }
        'image-descriptor-size' { $image.descriptor.size++ }
        'container-descriptor-size' { $container.imageManifestDescriptor.size++ }
        'container-descriptor-digest' { $container.imageManifestDescriptor.digest = 'sha256:' + ('d' * 64) }
        'inspection-platform' { $container.imageManifestDescriptor.platform.architecture = 'arm64' }
        'no-image-descriptor' { $image.descriptor = $null }
        'no-container-descriptor' { $container.imageManifestDescriptor = $null }
        'classic' { $image.id = $config.digest; $container.image = $config.digest; $image.descriptor = $null; $container.imageManifestDescriptor = $null }
    }
    $entries = [Collections.Generic.List[object]]::new()
    $layoutDocument = @{ imageLayoutVersion = '1.0.0' }
    if ($Scenario -ceq 'layout-version-array') { $layoutDocument.imageLayoutVersion = @('1.0.0') }
    $entries.Add(@{ path = 'oci-layout'; bytes = ConvertTo-OciBytes $layoutDocument })
    $entries.Add(@{ path = 'index.json'; bytes = $wrapperBytes })
    foreach ($digest in @($blobs.Keys | Sort-Object)) {
        if ($Scenario -ceq 'missing-layer' -and $digest -ceq $compressed.digest) { continue }
        if ($Scenario -ceq 'missing-config' -and $digest -ceq $config.digest) { continue }
        $bytes = [byte[]]$blobs[$digest]
        if ($Scenario -ceq 'blob-hash' -and $digest -ceq $compressed.digest) { $bytes = [byte[]]$bytes.Clone(); $bytes[0] = $bytes[0] -bxor 1 }
        if ($Scenario -ceq 'blob-size' -and $digest -ceq $raw.digest) { $bytes = [byte[]]$bytes[0..($bytes.Length - 2)] }
        $entries.Add(@{ path = 'blobs/sha256/' + $digest.Substring(7); bytes = $bytes })
    }
    switch ($Scenario) {
        'classic' { $entries.Clear(); $entries.Add(@{ path = 'manifest.json'; bytes = [Text.UTF8Encoding]::new($false).GetBytes('[]') }) }
        'duplicate-path' { $entries.Add(@{ path = 'index.json'; bytes = $wrapperBytes }) }
        'alias-path' { $entries.Add(@{ path = './index.json'; bytes = $wrapperBytes }) }
        'link-path' { $entries.Add(@{ path = 'unselected-link'; type = [Formats.Tar.TarEntryType]::SymbolicLink; link = 'index.json' }) }
        'traversal-path' { $entries.Add(@{ path = '../outside'; bytes = [byte[]]@(1) }) }
        'one-terminator-after-zero-data' { $entries.Add(@{ path = 'unreferenced-padding.bin'; bytes = [byte[]]::new(512) }) }
    }
    $archive = Join-Path $root 'image.tar'
    Write-OciArchive $archive $entries
    if ($Scenario -ceq 'one-terminator-after-zero-data') {
        $paddingEnd = 0L
        $probeFile = [IO.File]::OpenRead($archive)
        $probeReader = [Formats.Tar.TarReader]::new($probeFile, $true)
        try {
            while ($null -ne ($probeEntry = $probeReader.GetNextEntry())) {
                if ($probeEntry.Name -ceq 'unreferenced-padding.bin') { $paddingEnd = $probeEntry.DataOffset + $probeEntry.Length }
            }
            Assert-OciContract ($paddingEnd -gt 0 -and $probeFile.Length -eq $paddingEnd + 1024) 'One-terminator fixture did not begin with exactly two TAR terminator records.'
        }
        finally { $probeReader.Dispose(); $probeFile.Dispose() }
        $truncate = [IO.File]::Open($archive, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $truncate.SetLength($truncate.Length - 512) }
        finally { $truncate.Dispose() }
        $tail = [IO.File]::ReadAllBytes($archive)
        Assert-OciContract ($tail.Length % 512 -eq 0 -and @($tail[($tail.Length - 1024)..($tail.Length - 1)] | Where-Object { $_ -ne 0 }).Count -eq 0) 'One-terminator fixture did not retain aligned final1024 zero bytes.'
    }
    if ($Scenario -cin @('archive-alignment', 'nonzero-after-terminator')) {
        $append = [IO.File]::Open($archive, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            if ($Scenario -ceq 'archive-alignment') { $append.WriteByte(1) }
            else { $append.Write([Text.Encoding]::ASCII.GetBytes([string]::new([char]120, 512))) }
        }
        finally { $append.Dispose() }
    }
    $roles = @((Get-OciHash $wrapperBytes), $rootDescriptor.digest.Substring(7), $selected.digest.Substring(7), $config.digest.Substring(7), $raw.digest.Substring(7), $compressed.digest.Substring(7))
    if ($Shape -cne 'direct') { Assert-OciContract (@($roles | Sort-Object -Unique).Count -eq 6) 'Synthetic OCI digest roles were not distinct.' }
    return [ordered]@{ root = $root; archive = $archive; image = $image; container = $container; requested = $requested; shape = $Shape; scenario = $Scenario
        wrapperBytes = $wrapperBytes; rootDescriptor = $rootDescriptor; selected = $selected; config = $config; layers = @($raw, $compressed) }
}

function Invoke-OciCase([string] $Shape, [string] $Scenario, [string] $Expected) {
    $fixture = New-OciFixture $Shape $Scenario
    $archiveHash = Get-OciFileHash $fixture.archive
    $imageBefore = ConvertTo-Json -InputObject $fixture.image -Depth 64 -Compress
    $containerBefore = ConvertTo-Json -InputObject $fixture.container -Depth 64 -Compress
    $caught = $null; $results = @()
    try { $results = @(Get-NativeImageOciArchiveAssociation -ArchivePath $fixture.archive -RequestedImage $fixture.requested -ImageObservation $fixture.image -ContainerObservation $fixture.container) }
    catch { $caught = $_ }
    Assert-OciContract ((Get-OciFileHash $fixture.archive) -ceq $archiveHash -and [IO.Directory]::GetFileSystemEntries($fixture.root).Length -eq 1 -and
        (ConvertTo-Json -InputObject $fixture.image -Depth 64 -Compress) -ceq $imageBefore -and
        (ConvertTo-Json -InputObject $fixture.container -Depth 64 -Compress) -ceq $containerBefore) 'Pure OCI association modified its inputs or wrote artifacts.'
    if ($Expected -ceq 'reject') {
        Assert-OciContract ($null -ne $caught -and $results.Count -eq 0) ('Invalid OCI association was accepted: ' + $Scenario)
    }
    else {
        Assert-OciContract ($null -eq $caught -and $results.Count -eq 1 -and $results[0] -is [Collections.Specialized.OrderedDictionary]) ('OCI association did not return its one expected report: ' + $Scenario)
        $report = $results[0]
        $keys = @('schemaVersion', 'kind', 'associationStatus', 'requestedImage', 'requestedDigest', 'requestedDigestKind', 'observedLocalImageId', 'localIdKind', 'containerId', 'rootDescriptor',
            'selectedManifestDescriptor', 'configDigest', 'layoutIndexSha256', 'archiveSha256', 'archiveBytes', 'selectedLayers', 'descriptorChainVerified', 'holdReasons', 'registryDigestQualification')
        Assert-OciContract ($report.Count -eq 19 -and ((@($report.Keys | Sort-Object) -join '|') -ceq (@($keys | Sort-Object) -join '|')) -and
            $report.schemaVersion -is [int] -and $report.schemaVersion -eq 1 -and $report.kind -ceq 'conduit-native-image-oci-association' -and
            $report.associationStatus -ceq $Expected -and $report.requestedImage -ceq $fixture.requested -and $report.observedLocalImageId -ceq $fixture.image.id -and
            $report.containerId -ceq $fixture.container.id -and $report.archiveSha256 -ceq $archiveHash -and
            $report.archiveBytes -is [long] -and $report.archiveBytes -eq ([IO.FileInfo]::new($fixture.archive)).Length -and
            $report.registryDigestQualification -is [bool] -and -not $report.registryDigestQualification) 'OCI report shape, identity or archive hash differs.'
        Assert-OciContract ($report.descriptorChainVerified -is [bool] -and $report.holdReasons -is [array] -and $report.selectedLayers -is [array]) 'OCI report lost typed chain, hold or layer fields.'
        if ($Expected -ceq 'verified') {
            $expectedKind = if ($Shape -ceq 'direct') { 'manifest' } else { 'index' }
            $expectedRequestedDigest = switch ($Scenario) { 'requested-root' { $fixture.rootDescriptor.digest }; 'requested-selected' { $fixture.selected.digest }; default { $null } }
            $expectedRequestedKind = switch ($Scenario) { 'requested-root' { $expectedKind }; 'requested-selected' { 'manifest' }; default { $null } }
            Assert-OciContract ($report.descriptorChainVerified -and $report.holdReasons.Count -eq 0 -and
                $report.localIdKind -ceq $expectedKind -and $report.requestedDigest -ceq $expectedRequestedDigest -and $report.requestedDigestKind -ceq $expectedRequestedKind -and
                $report.rootDescriptor.digest -ceq $fixture.rootDescriptor.digest -and $report.rootDescriptor.size -eq $fixture.rootDescriptor.size -and $report.rootDescriptor.mediaType -ceq $fixture.rootDescriptor.mediaType -and
                $report.selectedManifestDescriptor.digest -ceq $fixture.selected.digest -and $report.selectedManifestDescriptor.size -eq $fixture.selected.size -and $report.selectedManifestDescriptor.mediaType -ceq $manifestMediaType -and
                $report.configDigest -ceq $fixture.config.digest -and $report.layoutIndexSha256 -ceq (Get-OciHash $fixture.wrapperBytes) -and
                $report.selectedLayers.Count -eq 2) 'Verified OCI report mixed wrapper, index, manifest, config or layer identities.'
            for ($index = 0; $index -lt 2; $index++) {
                Assert-OciContract ($report.selectedLayers[$index].digest -ceq $fixture.layers[$index].digest -and
                    $report.selectedLayers[$index].size -eq $fixture.layers[$index].size -and $report.selectedLayers[$index].mediaType -ceq $fixture.layers[$index].mediaType) 'Verified OCI layer descriptor changed original raw/compressed bytes or order.'
            }
        }
        else {
            $closedReasons = @('missing-image-descriptor', 'missing-container-manifest-descriptor', 'classic-save-layout', 'missing-oci-layout', 'missing-layout-index', 'missing-root-descriptor', 'missing-selected-content')
            $requiredReason = switch ($Scenario) { 'no-image-descriptor' { 'missing-image-descriptor' }; 'no-container-descriptor' { 'missing-container-manifest-descriptor' }; 'classic' { 'classic-save-layout' }; default { 'missing-selected-content' } }
            Assert-OciContract (-not $report.descriptorChainVerified -and $report.selectedLayers.Count -eq 0 -and $report.holdReasons.Count -gt 0 -and
                @($report.holdReasons | Sort-Object -Unique).Count -eq $report.holdReasons.Count -and
                @($report.holdReasons | Where-Object { $_ -isnot [string] -or $_ -cnotin $closedReasons }).Count -eq 0 -and
                $requiredReason -cin $report.holdReasons) 'Incomplete OCI evidence invented a chain or lost its closed explicit HOLD reason.'
            if ($Scenario -ceq 'classic') {
                Assert-OciContract ($null -eq $report.rootDescriptor -and $null -eq $report.selectedManifestDescriptor -and $null -eq $report.configDigest -and
                    $null -eq $report.layoutIndexSha256 -and $null -eq $report.localIdKind -and $null -eq $report.requestedDigestKind) 'Classic config-only evidence invented original index or manifest identities.'
            }
        }
    }
    $script:cases++
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $loadResults = @(. $helper)
    Assert-OciContract ($loadResults.Count -eq 0 -and [IO.Directory]::GetFileSystemEntries($temporaryRoot).Length -eq 0) 'OCI helper load emitted output or wrote artifacts.'
    $cases++
    foreach ($entry in @(@('direct', 'valid'), @('index', 'valid'), @('nested', 'valid'), @('index', 'requested-root'), @('nested', 'requested-selected'), @('index', 'local-ref'))) {
        Invoke-OciCase $entry[0] $entry[1] 'verified'
    }
    foreach ($scenario in @('no-image-descriptor', 'no-container-descriptor', 'classic', 'missing-layer', 'missing-config')) { Invoke-OciCase 'index' $scenario 'hold' }
    foreach ($scenario in @('requested-mismatch', 'local-mismatch', 'container-image-mismatch', 'container-id', 'image-descriptor-size', 'container-descriptor-size',
        'container-descriptor-digest', 'inspection-platform', 'config-platform', 'descriptor-platform', 'layer-count', 'ambiguous', 'depth',
        'duplicate-json', 'metadata-size', 'archive-alignment', 'blob-hash', 'blob-size', 'duplicate-path', 'alias-path', 'link-path', 'traversal-path',
        'layout-version-array', 'rootfs-type-array', 'manifest-media-array', 'index-media-array', 'wrapper-media-array', 'nonzero-after-terminator', 'one-terminator-after-zero-data')) {
        Invoke-OciCase 'index' $scenario 'reject'
    }
}
catch { $failure = $_ }
finally {
    try {
        $resolved = [IO.Path]::GetFullPath($temporaryRoot)
        Assert-OciContract ([IO.Path]::GetDirectoryName($resolved) -ceq $temporaryParent -and
            [IO.Path]::GetFileName($resolved) -cmatch '^conduit-oci-association-contract-[0-9a-f]{32}$') 'OCI fixture cleanup target was rejected.'
        if ([IO.Directory]::Exists($resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
        Assert-OciContract (-not [IO.Directory]::Exists($resolved)) 'OCI fixture owned directory remained after cleanup.'
    }
    catch { $cleanupFailure = $true }
}
if ($cleanupFailure) { throw 'Native image OCI contracts could not clean their owned directory.' }
if ($null -ne $failure) { throw $failure }
Write-Output ('Verified native image OCI association contracts; cases=' + $cases + '; skipped=0; cleanupConfirmed=true.')
