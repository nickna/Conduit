# Associate original OCI-save bytes with local Docker inspection observations.
# This helper reads retained evidence only; dot-sourcing performs no I/O.
. (Join-Path $PSScriptRoot 'native-image-runtime-archive.ps1')

function Get-NativeOciObject($Value, [string[]] $AllowedKeys = @(), [string[]] $RequiredKeys = @()) {
    if ($Value -isnot [Collections.IDictionary]) { throw 'OCI evidence requires an object.' }
    $result = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($key in $Value.Keys) {
        if ($key -isnot [string] -or -not $result.TryAdd($key, $Value[$key])) {
            throw 'OCI evidence object keys are invalid.'
        }
        if ($AllowedKeys.Count -gt 0 -and $key -cnotin $AllowedKeys) {
            throw 'OCI inspection observation contains an unknown field.'
        }
    }
    foreach ($key in $RequiredKeys) {
        if (-not $result.ContainsKey($key)) { throw 'OCI evidence object is missing a required field.' }
    }
    return ,$result
}

function ConvertFrom-NativeOciJsonElement([Text.Json.JsonElement] $Element, [int] $Depth = 0) {
    if ($Depth -gt 64) { throw 'OCI JSON depth exceeds its bound.' }
    switch ($Element.ValueKind) {
        Object {
            # OCI annotation names are case-sensitive. Reject exact duplicates,
            # while retaining legitimate case-distinct annotation keys.
            $object = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
            foreach ($property in $Element.EnumerateObject()) {
                if ($object.ContainsKey($property.Name)) { throw 'OCI JSON contains duplicate keys.' }
                $object.Add($property.Name, (ConvertFrom-NativeOciJsonElement $property.Value ($Depth + 1)))
            }
            return ,$object
        }
        Array {
            $items = [Collections.Generic.List[object]]::new()
            foreach ($item in $Element.EnumerateArray()) {
                $items.Add((ConvertFrom-NativeOciJsonElement $item ($Depth + 1)))
            }
            return ,$items.ToArray()
        }
        String { return $Element.GetString() }
        Number {
            $integer = 0L
            if ($Element.TryGetInt64([ref]$integer)) { return $integer }
            $number = $Element.GetDouble()
            if (-not [double]::IsFinite($number)) { throw 'OCI JSON number is invalid.' }
            return $number
        }
        True { return $true }
        False { return $false }
        Null { return $null }
        default { throw 'OCI JSON value is invalid.' }
    }
}

function Read-NativeOciJson([byte[]] $Bytes) {
    if ($Bytes.Length -eq 0 -or $Bytes.Length -gt 4194304) { throw 'OCI metadata size is invalid.' }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = 64
    $options.CommentHandling = [Text.Json.JsonCommentHandling]::Disallow
    $options.AllowTrailingCommas = $false
    $document = [Text.Json.JsonDocument]::Parse($text, $options)
    try { return ,(ConvertFrom-NativeOciJsonElement $document.RootElement) }
    finally { $document.Dispose() }
}

function Assert-NativeOciInteger($Value, [bool] $Positive = $false) {
    if ($Value -isnot [long] -and $Value -isnot [int] -and $Value -isnot [short] -and $Value -isnot [byte]) {
        throw 'OCI descriptor integer is invalid.'
    }
    if ($Value -lt 0 -or ($Positive -and $Value -eq 0)) { throw 'OCI descriptor integer is invalid.' }
}

function Get-NativeOciMediaKind([string] $MediaType) {
    switch -CaseSensitive ($MediaType) {
        'application/vnd.oci.image.index.v1+json' { return 'index' }
        'application/vnd.docker.distribution.manifest.list.v2+json' { return 'index' }
        'application/vnd.oci.image.manifest.v1+json' { return 'manifest' }
        'application/vnd.docker.distribution.manifest.v2+json' { return 'manifest' }
        'application/vnd.oci.image.config.v1+json' { return 'config' }
        'application/vnd.docker.container.image.v1+json' { return 'config' }
        default { return $null }
    }
}

function Get-NativeOciDescriptor($Value, [bool] $Sha256Only = $true) {
    $object = Get-NativeOciObject $Value @() @('mediaType', 'digest', 'size')
    if ($object['mediaType'] -isnot [string] -or [string]::IsNullOrWhiteSpace($object['mediaType']) -or
        $object['mediaType'].Length -gt 256 -or $object['mediaType'] -match '[\x00-\x20\x7f]') {
        throw 'OCI descriptor media type is invalid.'
    }
    if ($object['digest'] -isnot [string] -or
        ($Sha256Only -and $object['digest'] -cnotmatch '\Asha256:[0-9a-f]{64}\z') -or
        (-not $Sha256Only -and $object['digest'] -cnotmatch '\A[a-z][a-z0-9_+.-]*:[A-Za-z0-9=_-]+\z')) {
        throw 'OCI descriptor digest is invalid.'
    }
    Assert-NativeOciInteger $object['size']
    $descriptor = [ordered]@{
        mediaType = $object['mediaType']; digest = $object['digest']; size = [long]$object['size']
    }
    if ($object.ContainsKey('platform')) {
        $platform = Get-NativeOciObject $object['platform'] @() @('os', 'architecture')
        foreach ($key in @('os', 'architecture')) {
            if ($platform[$key] -isnot [string] -or [string]::IsNullOrWhiteSpace($platform[$key]) -or
                $platform[$key].Length -gt 128 -or $platform[$key] -match '[\x00-\x20\x7f]') {
                throw 'OCI descriptor platform is invalid.'
            }
        }
        foreach ($key in @('variant', 'os.version')) {
            if ($platform.ContainsKey($key) -and ($platform[$key] -isnot [string] -or
                    [string]::IsNullOrEmpty($platform[$key]) -or $platform[$key] -match '[\x00-\x1f\x7f]')) {
                throw 'OCI descriptor platform is invalid.'
            }
        }
        if ($platform.ContainsKey('os.features')) {
            if ($platform['os.features'] -isnot [array]) { throw 'OCI descriptor platform features are invalid.' }
            foreach ($feature in $platform['os.features']) {
                if ($feature -isnot [string] -or [string]::IsNullOrEmpty($feature) -or
                    $feature -match '[\x00-\x1f\x7f]') { throw 'OCI descriptor platform features are invalid.' }
            }
        }
        $descriptor['platform'] = $platform
    }
    return ,$descriptor
}

function Test-NativeOciDescriptorCore($Left, $Right) {
    return $Left['mediaType'] -ceq $Right['mediaType'] -and $Left['digest'] -ceq $Right['digest'] -and
        $Left['size'] -eq $Right['size']
}

function Get-NativeOciRangeHash([IO.Stream] $Stream, [long] $Offset, [long] $Length,
    [Threading.CancellationToken] $CancellationToken) {
    if ($Offset -lt 0 -or $Length -lt 0 -or $Offset -gt $Stream.Length -or $Length -gt $Stream.Length - $Offset) {
        throw 'OCI content range exceeds its archive.'
    }
    $Stream.Position = $Offset
    $buffer = [byte[]]::new(81920)
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $remaining = $Length
        while ($remaining -gt 0) {
            $CancellationToken.ThrowIfCancellationRequested()
            $count = [int][Math]::Min($remaining, $buffer.Length)
            $read = $Stream.ReadAsync([Memory[byte]]::new($buffer, 0, $count), $CancellationToken).
                AsTask().GetAwaiter().GetResult()
            if ($read -le 0) { throw 'OCI archive content is truncated.' }
            $hash.AppendData($buffer, 0, $read)
            $remaining -= $read
        }
        return [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
    }
    finally { $hash.Dispose() }
}

function Get-NativeOciRangeBytes($Context, $Entry) {
    if ($Entry['length'] -gt 4194304) { throw 'OCI metadata size exceeds its bound.' }
    $bytes = [byte[]]::new([int]$Entry['length'])
    $Context['archive'].Position = $Entry['offset']
    $offset = 0
    while ($offset -lt $bytes.Length) {
        $Context['token'].ThrowIfCancellationRequested()
        $read = $Context['archive'].ReadAsync([Memory[byte]]::new($bytes, $offset, $bytes.Length - $offset),
            $Context['token']).AsTask().GetAwaiter().GetResult()
        if ($read -le 0) { throw 'OCI archive metadata is truncated.' }
        $offset += $read
    }
    return ,$bytes
}

function Assert-NativeOciZeroRange([IO.Stream] $Stream, [long] $Offset, [long] $Length,
    [Threading.CancellationToken] $CancellationToken) {
    if ($Offset -lt 0 -or $Length -lt 0 -or $Offset -gt $Stream.Length -or $Length -gt $Stream.Length - $Offset) {
        throw 'OCI archive terminator exceeds its archive.'
    }
    $Stream.Position = $Offset
    $buffer = [byte[]]::new(81920)
    $remaining = $Length
    while ($remaining -gt 0) {
        $CancellationToken.ThrowIfCancellationRequested()
        $count = [int][Math]::Min($remaining, $buffer.Length)
        $read = $Stream.ReadAsync([Memory[byte]]::new($buffer, 0, $count), $CancellationToken).
            AsTask().GetAwaiter().GetResult()
        if ($read -le 0) { throw 'OCI archive terminator is truncated.' }
        if ([Convert]::ToHexString($buffer, 0, $read).Trim([char]'0').Length -ne 0) {
            throw 'OCI archive contains nonzero content after its terminator.'
        }
        $remaining -= $read
    }
}

function Get-NativeOciBlob($Context, $Descriptor, [bool] $Metadata = $true) {
    if ($Descriptor['digest'] -cnotmatch '\Asha256:[0-9a-f]{64}\z') {
        throw 'Selected OCI content must use SHA-256.'
    }
    $limit = if ($Metadata) { 4194304L } else { 536870912L }
    if ($Descriptor['size'] -gt $limit -or ($Metadata -and $Descriptor['size'] -eq 0)) {
        throw 'Selected OCI content exceeds its bound or is empty metadata.'
    }
    $path = 'blobs/sha256/' + $Descriptor['digest'].Substring(7)
    $entry = $null
    if (-not $Context['files'].TryGetValue($path, [ref]$entry)) {
        $Context['missingContent'] = $true
        return $null
    }
    if ($entry['length'] -ne $Descriptor['size']) { throw 'OCI content size does not match its descriptor.' }
    $digest = Get-NativeOciRangeHash $Context['archive'] $entry['offset'] $entry['length'] $Context['token']
    if ('sha256:' + $digest -cne $Descriptor['digest']) { throw 'OCI content digest does not match its descriptor.' }
    if ($Metadata) { return ,(Read-NativeOciJson (Get-NativeOciRangeBytes $Context $entry)) }
    return $true
}

function Assert-NativeOciDocument($Document, $Descriptor, [string] $Kind) {
    $object = Get-NativeOciObject $Document @() @('schemaVersion')
    Assert-NativeOciInteger $object['schemaVersion'] $true
    if ($object['schemaVersion'] -ne 2) { throw 'OCI schema version is invalid.' }
    if ($object.ContainsKey('mediaType') -and ($object['mediaType'] -isnot [string] -or
            $object['mediaType'] -cne $Descriptor['mediaType'])) {
        throw 'OCI document media type does not match its descriptor.'
    }
    if ((Get-NativeOciMediaKind $Descriptor['mediaType']) -cne $Kind) { throw 'OCI document role is invalid.' }
    return ,$object
}

function Assert-NativeOciPlatformMatches($Platform, $Config) {
    if ($Platform['os'] -cne $Config['os'] -or $Platform['architecture'] -cne $Config['architecture']) {
        throw 'OCI descriptor platform conflicts with its configuration.'
    }
    foreach ($key in @('variant', 'os.version')) {
        if ($Platform.ContainsKey($key) -and (-not $Config.ContainsKey($key) -or
                $Config[$key] -isnot [string] -or $Platform[$key] -cne $Config[$key])) {
            throw 'OCI descriptor platform conflicts with its configuration.'
        }
    }
    if ($Platform.ContainsKey('os.features')) {
        if (-not $Config.ContainsKey('os.features') -or $Config['os.features'] -isnot [array]) {
            throw 'OCI descriptor platform conflicts with its configuration.'
        }
        $left = @($Platform['os.features']); $right = @($Config['os.features'])
        if ($left.Count -ne $right.Count) { throw 'OCI descriptor platform conflicts with its configuration.' }
        for ($index = 0; $index -lt $left.Count; $index++) {
            if ($right[$index] -isnot [string] -or $left[$index] -cne $right[$index]) {
                throw 'OCI descriptor platform conflicts with its configuration.'
            }
        }
    }
}

function Find-NativeOciExecutableManifests($Context, $Descriptor,
    [Collections.Generic.HashSet[string]] $Ancestors, [object[]] $Platforms, [int] $Depth = 0) {
    $Context['token'].ThrowIfCancellationRequested()
    $Context['nodes']++
    if ($Depth -gt 16 -or $Context['nodes'] -gt 4096) { throw 'OCI descriptor graph exceeds its bound.' }
    if (-not $Ancestors.Add($Descriptor['digest'])) { throw 'OCI descriptor graph contains a cycle.' }
    try {
        $kind = Get-NativeOciMediaKind $Descriptor['mediaType']
        if ($kind -cnotin @('index', 'manifest')) { return }
        $platformChain = $Platforms
        if ($Descriptor.Contains('platform')) { $platformChain = @($Platforms) + @($Descriptor['platform']) }
        if ($kind -ceq 'manifest' -and $Descriptor.Contains('platform') -and
            ($Descriptor['platform']['os'] -cne 'linux' -or $Descriptor['platform']['architecture'] -cne 'amd64')) {
            if ($Descriptor['digest'] -ceq $Context['containerDescriptor']['digest']) {
                throw 'Selected OCI manifest platform conflicts with Linux/amd64.'
            }
            return
        }
        $document = Get-NativeOciBlob $Context $Descriptor
        if ($null -eq $document) { return }
        $document = Assert-NativeOciDocument $document $Descriptor $kind
        if ($kind -ceq 'index') {
            if (-not $document.ContainsKey('manifests') -or $document['manifests'] -isnot [array] -or
                $document['manifests'].Count -eq 0 -or $document['manifests'].Count -gt 1024) {
                throw 'OCI index manifests are invalid.'
            }
            foreach ($value in $document['manifests']) {
                $child = Get-NativeOciDescriptor $value $false
                if ((Get-NativeOciMediaKind $child['mediaType']) -cin @('index', 'manifest')) {
                    Find-NativeOciExecutableManifests $Context $child $Ancestors $platformChain ($Depth + 1)
                }
            }
            return
        }
        if (-not $document.ContainsKey('config') -or -not $document.ContainsKey('layers') -or
            $document['layers'] -isnot [array] -or $document['layers'].Count -gt 10000) {
            throw 'OCI executable manifest is invalid.'
        }
        $configDescriptor = Get-NativeOciDescriptor $document['config'] $false
        if ((Get-NativeOciMediaKind $configDescriptor['mediaType']) -cne 'config' -or
            ($document.ContainsKey('artifactType') -and $null -ne $document['artifactType'])) {
            if ($Descriptor['digest'] -ceq $Context['containerDescriptor']['digest']) {
                throw 'Selected OCI manifest is not an executable image.'
            }
            return
        }
        $config = Get-NativeOciBlob $Context $configDescriptor
        if ($null -eq $config) { return }
        $config = Get-NativeOciObject $config @() @('os', 'architecture', 'rootfs')
        foreach ($key in @('os', 'architecture')) {
            if ($config[$key] -isnot [string] -or [string]::IsNullOrWhiteSpace($config[$key])) {
                throw 'OCI image configuration platform is invalid.'
            }
        }
        foreach ($platform in $platformChain) { Assert-NativeOciPlatformMatches $platform $config }
        if ($configDescriptor.Contains('platform')) {
            Assert-NativeOciPlatformMatches $configDescriptor['platform'] $config
        }
        if ($Descriptor['digest'] -ceq $Context['containerDescriptor']['digest']) {
            if (-not (Test-NativeOciDescriptorCore $Descriptor $Context['containerDescriptor'])) {
                throw 'OCI selected manifest conflicts with its inspection descriptor.'
            }
            if ($Context['containerDescriptor'].Contains('platform')) {
                Assert-NativeOciPlatformMatches $Context['containerDescriptor']['platform'] $config
            }
        }
        if ($config['os'] -cne 'linux' -or $config['architecture'] -cne 'amd64') {
            if ($Descriptor['digest'] -ceq $Context['containerDescriptor']['digest']) {
                throw 'Selected OCI configuration is not Linux/amd64.'
            }
            return
        }
        $rootfs = Get-NativeOciObject $config['rootfs'] @() @('type', 'diff_ids')
        if ($rootfs['type'] -isnot [string] -or $rootfs['type'] -cne 'layers' -or $rootfs['diff_ids'] -isnot [array] -or
            $rootfs['diff_ids'].Count -ne $document['layers'].Count) {
            throw 'OCI configuration rootfs layer count is invalid.'
        }
        foreach ($diffId in $rootfs['diff_ids']) {
            if ($diffId -isnot [string] -or $diffId -cnotmatch '\Asha256:[0-9a-f]{64}\z') {
                throw 'OCI configuration diff ID is invalid.'
            }
        }
        $layers = [Collections.Generic.List[object]]::new()
        foreach ($value in $document['layers']) {
            $layer = Get-NativeOciDescriptor $value
            if ($layer['mediaType'] -cnotin @(
                'application/vnd.oci.image.layer.v1.tar', 'application/vnd.oci.image.layer.v1.tar+gzip',
                'application/vnd.oci.image.layer.v1.tar+zstd', 'application/vnd.oci.image.layer.nondistributable.v1.tar',
                'application/vnd.oci.image.layer.nondistributable.v1.tar+gzip',
                'application/vnd.oci.image.layer.nondistributable.v1.tar+zstd',
                'application/vnd.docker.image.rootfs.diff.tar', 'application/vnd.docker.image.rootfs.diff.tar.gzip',
                'application/vnd.docker.image.rootfs.foreign.diff.tar.gzip')) {
                throw 'Selected OCI layer media type is unsupported.'
            }
            if ($layer.Contains('platform')) { Assert-NativeOciPlatformMatches $layer['platform'] $config }
            $layers.Add($layer)
        }
        $Context['candidates'].Add([ordered]@{ descriptor = $Descriptor; config = $configDescriptor; layers = $layers.ToArray() })
    }
    finally { $null = $Ancestors.Remove($Descriptor['digest']) }
}

function Get-NativeImageOciArchiveAssociation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $ArchivePath,
        [Parameter(Mandatory = $true)][string] $RequestedImage,
        [Parameter(Mandatory = $true)] $ImageObservation,
        [Parameter(Mandatory = $true)] $ContainerObservation
    )
    $ErrorActionPreference = 'Stop'
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(90))
    $archive = $null; $reader = $null
    try {
        if ([string]::IsNullOrWhiteSpace($RequestedImage) -or $RequestedImage.Length -gt 4096 -or
            $RequestedImage -match '[\x00-\x20\x7f]' -or $RequestedImage.StartsWith('-', [StringComparison]::Ordinal)) {
            throw 'OCI requested image reference is invalid.'
        }
        $image = Get-NativeOciObject $ImageObservation @('id', 'descriptor') @('id')
        $container = Get-NativeOciObject $ContainerObservation @('id', 'image', 'imageManifestDescriptor') @('id', 'image')
        if ($image['id'] -isnot [string] -or $image['id'] -cnotmatch '\Asha256:[0-9a-f]{64}\z' -or
            $container['id'] -isnot [string] -or $container['id'] -cnotmatch '\A[0-9a-f]{64}\z' -or
            $container['image'] -isnot [string] -or $container['image'] -cne $image['id']) {
            throw 'OCI local image and container identities disagree.'
        }
        $requestedDigest = $null; $requestedKind = $null
        $at = $RequestedImage.IndexOf('@', [StringComparison]::Ordinal)
        if ($at -ge 0) {
            if ($at -eq 0 -or $RequestedImage.LastIndexOf('@', [StringComparison]::Ordinal) -ne $at) {
                throw 'OCI requested image digest reference is invalid.'
            }
            $requestedDigest = $RequestedImage.Substring($at + 1)
            if ($requestedDigest -cnotmatch '\Asha256:[0-9a-f]{64}\z') { throw 'OCI requested image digest is invalid.' }
        }
        elseif ($RequestedImage.StartsWith('sha256:', [StringComparison]::Ordinal)) {
            if ($RequestedImage -cne $image['id']) { throw 'OCI local digest reference differs from the observed image.' }
        }
        $holdReasons = [Collections.Generic.List[string]]::new()
        $root = $null; $selectedObservation = $null; $localKind = $null
        if (-not $image.ContainsKey('descriptor') -or $null -eq $image['descriptor']) {
            $holdReasons.Add('missing-image-descriptor')
        }
        else {
            $root = Get-NativeOciDescriptor $image['descriptor']
            $localKind = Get-NativeOciMediaKind $root['mediaType']
            if ($localKind -cnotin @('index', 'manifest') -or $root['digest'] -cne $image['id']) {
                throw 'OCI inspected root descriptor differs from the local image identity.'
            }
        }
        if (-not $container.ContainsKey('imageManifestDescriptor') -or $null -eq $container['imageManifestDescriptor']) {
            $holdReasons.Add('missing-container-manifest-descriptor')
        }
        else {
            $selectedObservation = Get-NativeOciDescriptor $container['imageManifestDescriptor']
            if ((Get-NativeOciMediaKind $selectedObservation['mediaType']) -cne 'manifest') {
                throw 'OCI container inspection descriptor is not a manifest.'
            }
        }
        if ($null -ne $requestedDigest -and $null -ne $root -and $requestedDigest -ceq $root['digest']) {
            $requestedKind = $localKind
        }
        elseif ($null -ne $requestedDigest -and $null -ne $selectedObservation -and
                $requestedDigest -ceq $selectedObservation['digest']) { $requestedKind = 'manifest' }
        elseif ($null -ne $requestedDigest -and $null -ne $selectedObservation -and
                $requestedDigest -cne $image['id']) {
            throw 'OCI requested registry digest has no observed root or manifest role.'
        }

        $archiveFile = Get-NativeEvidenceFullPath $ArchivePath
        Assert-NativeEvidenceRegularFile $archiveFile
        $archive = [IO.FileStream]::new($archiveFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $archiveBytes = [long]$archive.Length
        if ($archiveBytes -lt 1024 -or $archiveBytes -gt 4294967296L -or $archiveBytes % 512 -ne 0) {
            throw 'OCI archive size or alignment is invalid.'
        }
        $archiveHash = Get-NativeOciRangeHash $archive 0 $archiveBytes $deadline.Token
        $archive.Position = 0
        $files = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $paths = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        $entries = 0
        $reader = [System.Formats.Tar.TarReader]::new($archive, $true)
        while ($true) {
            $deadline.Token.ThrowIfCancellationRequested()
            $entry = $reader.GetNextEntryAsync($false, $deadline.Token).GetAwaiter().GetResult()
            if ($null -eq $entry) { break }
            $entries++
            if ($entries -gt 200000) { throw 'OCI archive entry count exceeds its bound.' }
            $name = $entry.Name
            if ($name -isnot [string] -or $name.Length -gt 4096 -or $name -match '[\\\x00-\x1f\x7f]' -or
                $name.StartsWith('/', [StringComparison]::Ordinal) -or $name -match '\A[A-Za-z]:' -or
                [IO.Path]::IsPathRooted($name)) { throw 'OCI archive path is invalid.' }
            if ($name.StartsWith('./', [StringComparison]::Ordinal)) { $name = $name.Substring(2) }
            $isDirectory = $entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory
            if ($name.EndsWith('/', [StringComparison]::Ordinal)) {
                if (-not $isDirectory) { throw 'OCI archive file path ends in a separator.' }
                $name = $name.Substring(0, $name.Length - 1)
            }
            if ($isDirectory -and $name -cin @('', '.')) {
                if ($entry.Length -ne 0 -or -not $seen.Add('.')) { throw 'OCI archive root path is invalid or duplicated.' }
                continue
            }
            Assert-NativeEvidenceRelativePath $name
            if (-not $seen.Add($name)) { throw 'OCI archive contains duplicate paths.' }
            Register-NativeImageArchiveSelectedPath $paths $name $isDirectory
            if ($isDirectory) {
                if ($entry.Length -ne 0) { throw 'OCI archive directory contains data.' }
            }
            elseif ($entry.EntryType -cin @([System.Formats.Tar.TarEntryType]::RegularFile,
                    [System.Formats.Tar.TarEntryType]::V7RegularFile)) {
                if ($entry.Length -lt 0 -or $entry.DataOffset -lt 0 -or $entry.DataOffset -gt $archiveBytes -or
                    $entry.Length -gt $archiveBytes - $entry.DataOffset) { throw 'OCI archive content exceeds its archive.' }
                $files.Add($name, [ordered]@{ offset = [long]$entry.DataOffset; length = [long]$entry.Length })
            }
            else { throw 'OCI archive contains a link or special entry.' }
        }
        $endOffset = [long]$archive.Position
        $reader.Dispose(); $reader = $null
        # TarReader ends at the first terminator. Do not permit another archive
        # or hidden nonzero entries after that point, and require two complete
        # zero records at the end of the retained tar.
        if ($archiveBytes - $endOffset -lt 512) { throw 'OCI archive has an incomplete terminator.' }
        Assert-NativeOciZeroRange $archive $endOffset ($archiveBytes - $endOffset) $deadline.Token
        Assert-NativeOciZeroRange $archive ($archiveBytes - 1024) 1024 $deadline.Token
        $context = @{
            archive = $archive; token = $deadline.Token; files = $files; missingContent = $false; nodes = 0
            candidates = [Collections.Generic.List[object]]::new(); containerDescriptor = $selectedObservation
        }
        $layoutHash = $null; $configDigest = $null; $selected = $null; $layers = @()
        $layoutEntry = $null; $indexEntry = $null
        $hasLayout = $files.TryGetValue('oci-layout', [ref]$layoutEntry)
        $hasIndex = $files.TryGetValue('index.json', [ref]$indexEntry)
        if (-not $hasLayout -and -not $hasIndex -and $files.ContainsKey('manifest.json')) {
            $holdReasons.Add('classic-save-layout')
        }
        else {
            if (-not $hasLayout) { $holdReasons.Add('missing-oci-layout') }
            else {
                $layout = Get-NativeOciObject (Read-NativeOciJson (Get-NativeOciRangeBytes $context $layoutEntry)) @() @('imageLayoutVersion')
                if ($layout['imageLayoutVersion'] -isnot [string] -or $layout['imageLayoutVersion'] -cne '1.0.0') {
                    throw 'OCI layout version is invalid.'
                }
            }
            if (-not $hasIndex) { $holdReasons.Add('missing-layout-index') }
            else {
                $layoutHash = Get-NativeOciRangeHash $archive $indexEntry['offset'] $indexEntry['length'] $deadline.Token
                $index = Get-NativeOciObject (Read-NativeOciJson (Get-NativeOciRangeBytes $context $indexEntry)) @() @('schemaVersion', 'manifests')
                Assert-NativeOciInteger $index['schemaVersion'] $true
                if ($index['schemaVersion'] -ne 2 -or $index['manifests'] -isnot [array] -or
                    $index['manifests'].Count -eq 0 -or $index['manifests'].Count -gt 1024 -or
                    ($index.ContainsKey('mediaType') -and ($index['mediaType'] -isnot [string] -or
                        (Get-NativeOciMediaKind $index['mediaType']) -cne 'index'))) {
                    throw 'OCI transport index is invalid.'
                }
                $rootMatches = [Collections.Generic.List[object]]::new()
                foreach ($value in $index['manifests']) {
                    $candidate = Get-NativeOciDescriptor $value $false
                    if ($null -ne $root -and $candidate['digest'] -ceq $root['digest']) {
                        if (-not (Test-NativeOciDescriptorCore $candidate $root)) {
                            throw 'OCI transport root conflicts with the inspected root descriptor.'
                        }
                        $rootMatches.Add($candidate)
                    }
                }
                if ($rootMatches.Count -gt 1) { throw 'OCI transport index has ambiguous inspected roots.' }
                if ($null -ne $root -and $rootMatches.Count -eq 0) { $holdReasons.Add('missing-root-descriptor') }
                if ($null -ne $root -and $null -ne $selectedObservation -and $rootMatches.Count -eq 1) {
                    $rootPlatforms = @()
                    if ($root.Contains('platform')) { $rootPlatforms += @($root['platform']) }
                    $ancestors = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                    Find-NativeOciExecutableManifests $context $rootMatches[0] $ancestors $rootPlatforms
                    if ($context['candidates'].Count -gt 1) { throw 'OCI selected graph has ambiguous Linux/amd64 executable manifests.' }
                    if ($context['candidates'].Count -eq 1) {
                        $choice = $context['candidates'][0]
                        if (-not (Test-NativeOciDescriptorCore $choice['descriptor'] $selectedObservation)) {
                            throw 'OCI selected executable manifest differs from the container observation.'
                        }
                        $selected = $choice['descriptor']; $configDigest = $choice['config']['digest']
                        $selectedBytes = 0L
                        foreach ($layer in $choice['layers']) {
                            $selectedBytes += $layer['size']
                            if ($selectedBytes -gt 1073741824L) { throw 'Selected OCI layers exceed their aggregate bound.' }
                            $null = Get-NativeOciBlob $context $layer $false
                        }
                        if (-not $context['missingContent']) {
                            $layers = @($choice['layers'] | ForEach-Object {
                                [ordered]@{ mediaType = $_['mediaType']; digest = $_['digest']; size = $_['size'] }
                            })
                        }
                    }
                    elseif (-not $context['missingContent']) { throw 'OCI selected graph contains no Linux/amd64 executable manifest.' }
                    if ($context['missingContent']) { $holdReasons.Add('missing-selected-content') }
                }
            }
        }
        if ($archive.Length -ne $archiveBytes -or (Get-NativeOciRangeHash $archive 0 $archiveBytes $deadline.Token) -cne $archiveHash) {
            throw 'OCI archive changed during verification.'
        }
        # Reopen the retained pathname too: on Unix, a renamed replacement can
        # otherwise differ from the still-held original file descriptor.
        Assert-NativeEvidenceRegularFile $archiveFile
        $retained = [IO.FileStream]::new($archiveFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            if ($retained.Length -ne $archiveBytes -or
                (Get-NativeOciRangeHash $retained 0 $archiveBytes $deadline.Token) -cne $archiveHash) {
                throw 'Retained OCI archive differs from the verified bytes.'
            }
        }
        finally { $retained.Dispose() }
        $verified = $holdReasons.Count -eq 0
        if (-not $verified) { $layers = @() }
        return ,[ordered]@{
            schemaVersion = 1; kind = 'conduit-native-image-oci-association'
            associationStatus = if ($verified) { 'verified' } else { 'hold' }
            requestedImage = $RequestedImage; requestedDigest = $requestedDigest; requestedDigestKind = $requestedKind
            observedLocalImageId = $image['id']; localIdKind = $localKind; containerId = $container['id']
            rootDescriptor = $root; selectedManifestDescriptor = $selected; configDigest = $configDigest
            layoutIndexSha256 = $layoutHash; archiveSha256 = $archiveHash; archiveBytes = $archiveBytes
            selectedLayers = $layers; descriptorChainVerified = $verified; holdReasons = $holdReasons.ToArray()
            registryDigestQualification = $false
        }
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        if ($null -ne $archive) { $archive.Dispose() }
        $deadline.Dispose()
    }
}
