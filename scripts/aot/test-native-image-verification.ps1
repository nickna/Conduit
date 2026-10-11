#!/usr/bin/env pwsh
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot 'native-image-verification.ps1'
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$temporaryRoot = Join-Path $temporaryParent ('conduit-image-verification-contract-' + [Guid]::NewGuid().ToString('N'))
$environmentNames = @('CONDUIT_IMAGE_VERIFICATION_TEST_ROOT', 'CONDUIT_IMAGE_VERIFICATION_TEST_SCENARIO', 'CONDUIT_IMAGE_VERIFICATION_TEST_DIAGNOSTIC')
$oldEnvironment = @{}
foreach ($name in $environmentNames) { $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
$cases = 0; $sequence = 0; $failure = $null; $cleanupFailure = $false
$otherImageId = 'sha256:' + ('b' * 64)
$containerId = 'c' * 64
$privateDiagnostic = 'private-image-verification-' + [Guid]::NewGuid().ToString('N')

function Assert-VerificationContract([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Write-VerificationJson([string] $Path, $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 32), [Text.UTF8Encoding]::new($false))
}

function Read-VerificationJson([string] $Path) {
    return [IO.File]::ReadAllText($Path) | ConvertFrom-Json -AsHashtable -DateKind String
}

function Get-VerificationHash([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    try { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
    finally { $stream.Dispose() }
}

function Get-VerificationRecords([string] $Root) {
    return ,@(Get-ChildItem -LiteralPath (Join-Path $Root 'commands') -File |
        ForEach-Object { Read-VerificationJson $_.FullName } | Sort-Object ordinal)
}

function Get-VerificationProcessToken([Diagnostics.Process] $Process) {
    if ($IsLinux) {
        $stat = [IO.File]::ReadAllText('/proc/' + $Process.Id + '/stat')
        $separator = $stat.LastIndexOf(') ', [StringComparison]::Ordinal)
        Assert-VerificationContract ($separator -ge 0) 'Owned fixture process identity was malformed.'
        $fields = $stat.Substring($separator + 2).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)
        [ulong]$start = 0
        Assert-VerificationContract ($fields.Count -gt 19 -and [ulong]::TryParse($fields[19],
            [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$start) -and $start -gt 0) 'Owned fixture process start identity was malformed.'
        return 'linux:' + $start.ToString([Globalization.CultureInfo]::InvariantCulture)
    }
    return 'windows:' + $Process.StartTime.ToUniversalTime().Ticks.ToString([Globalization.CultureInfo]::InvariantCulture)
}

function Assert-VerificationPausedProcessReaped([string] $Root, [switch] $Stop) {
    $identityPath = Join-Path $Root 'paused-process.json'
    if (-not [IO.File]::Exists($identityPath)) { return }
    $identity = Read-VerificationJson $identityPath
    $process = Get-Process -Id $identity.id -ErrorAction SilentlyContinue
    if ($null -eq $process) { return }
    try {
        if ((Get-VerificationProcessToken $process) -cne $identity.token) { return }
        $zombie = $IsLinux -and [IO.File]::Exists('/proc/' + $process.Id + '/stat') -and
            [IO.File]::ReadAllText('/proc/' + $process.Id + '/stat') -match '^\d+ \(.+\) Z '
        if ($Stop -and -not $process.HasExited -and -not $zombie) {
            $process.Kill($true)
            Assert-VerificationContract ($process.WaitForExit(5000)) 'Owned paused fixture process could not be reaped.'
        }
        else { Assert-VerificationContract ($process.HasExited -or $zombie) 'Verification left its own timed-out command alive.' }
    }
    finally { $process.Dispose() }
}

function Write-VerificationArchive([string] $Root, [string] $Payload, [string] $Evidence) {
    $archive = Join-Path $Root 'fixture.tar'
    $file = [IO.FileStream]::new($archive, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $writer = [Formats.Tar.TarWriter]::new($file, [Formats.Tar.TarEntryFormat]::Pax, $true)
    try {
        foreach ($directory in @('app', 'conduit-evidence')) {
            $entry = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::Directory, $directory)
            $entry.Uid = 1654; $entry.Gid = 1654; $entry.Mode = [IO.UnixFileMode]493
            $writer.WriteEntry($entry)
        }
        foreach ($part in @(@{ Root = $Payload; Prefix = 'app' }, @{ Root = $Evidence; Prefix = 'conduit-evidence' })) {
            foreach ($source in @(([IO.DirectoryInfo]::new($part.Root)).EnumerateFiles('*', [IO.SearchOption]::AllDirectories) | Sort-Object FullName)) {
                $relative = [IO.Path]::GetRelativePath($part.Root, $source.FullName).Replace('\', '/')
                $entry = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, ($part.Prefix + '/' + $relative))
                $entry.Uid = 1654; $entry.Gid = 1654
                $entry.Mode = [IO.UnixFileMode]$(if ($relative -cin @('ConduitLLM.Admin', 'ConduitLLM.Gateway', 'conduit-health-probe')) { 493 } else { 420 })
                $body = [IO.File]::OpenRead($source.FullName)
                try { $entry.DataStream = $body; $writer.WriteEntry($entry) } finally { $body.Dispose() }
            }
        }
        $entry = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::SymbolicLink, 'usr/lib/ignored-base-link')
        $entry.LinkName = '../../base-target'; $writer.WriteEntry($entry)
    }
    finally { $writer.Dispose(); $file.Dispose() }
    return $archive
}

function Write-VerificationOciArchive([string] $Root, $Image, [string] $RootfsArchive, [string] $Scenario) {
    $blobs = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    function ConvertTo-FixtureOciBytes($Value) { return ,[Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Value -Depth 32 -Compress)) }
    function Add-FixtureOciBlob([string] $MediaType, [byte[]] $Bytes) {
        $digest = 'sha256:' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
        $blobs[$digest] = $Bytes
        return [ordered]@{ mediaType = $MediaType; digest = $digest; size = [long]$Bytes.Length }
    }
    $layerBytes = [IO.File]::ReadAllBytes($RootfsArchive)
    $layer = Add-FixtureOciBlob 'application/vnd.oci.image.layer.v1.tar' $layerBytes
    $configDocument = [ordered]@{ os = 'linux'; architecture = 'amd64'; config = $Image.Config; rootfs = @{ type = 'layers'; diff_ids = @($layer.digest) } }
    $configBytes = ConvertTo-FixtureOciBytes $configDocument
    $config = Add-FixtureOciBlob 'application/vnd.oci.image.config.v1+json' $configBytes
    $manifest = Add-FixtureOciBlob 'application/vnd.oci.image.manifest.v1+json' (ConvertTo-FixtureOciBytes @{
        schemaVersion = 2; mediaType = 'application/vnd.oci.image.manifest.v1+json'; config = $config; layers = @($layer)
    })
    $manifest['platform'] = @{ os = 'linux'; architecture = 'amd64' }
    $rootDescriptor = Add-FixtureOciBlob 'application/vnd.oci.image.index.v1+json' (ConvertTo-FixtureOciBytes @{
        schemaVersion = 2; mediaType = 'application/vnd.oci.image.index.v1+json'; manifests = @($manifest)
    })
    $wrapperBytes = ConvertTo-FixtureOciBytes @{
        schemaVersion = 2; mediaType = 'application/vnd.oci.image.index.v1+json'; manifests = @($rootDescriptor)
    }
    $Image.Id = $rootDescriptor.digest
    $Image['Descriptor'] = $rootDescriptor
    $entries = [Collections.Generic.List[object]]::new()
    $entries.Add(@{ path = 'oci-layout'; bytes = ConvertTo-FixtureOciBytes @{ imageLayoutVersion = '1.0.0' } })
    $entries.Add(@{ path = 'index.json'; bytes = $wrapperBytes })
    foreach ($digest in @($blobs.Keys | Sort-Object)) {
        $bytes = [byte[]]$blobs[$digest]
        if ($Scenario -ceq 'oci-blob-corrupt' -and $digest -ceq $layer.digest) { $bytes = [byte[]]$bytes.Clone(); $bytes[0] = $bytes[0] -bxor 1 }
        $entries.Add(@{ path = 'blobs/sha256/' + $digest.Substring(7); bytes = $bytes })
    }
    if ($Scenario -ceq 'oci-hold-classic') {
        $Image.Id = $config.digest; $Image.Descriptor = $null
        $entries.Clear()
        $entries.Add(@{ path = 'manifest.json'; bytes = ConvertTo-FixtureOciBytes @(@{ Config = $config.digest.Substring(7) + '.json'; RepoTags = @('historical-fixture:local'); Layers = @('layer.tar') }) })
        $entries.Add(@{ path = $config.digest.Substring(7) + '.json'; bytes = $configBytes })
        $entries.Add(@{ path = 'layer.tar'; bytes = $layerBytes })
    }
    if ($Scenario -ceq 'oci-hold-no-descriptors') { $Image.Descriptor = $null }
    $archive = Join-Path $Root 'fixture-save.tar'
    $file = [IO.FileStream]::new($archive, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $writer = [Formats.Tar.TarWriter]::new($file, [Formats.Tar.TarEntryFormat]::Pax, $true)
    try {
        foreach ($item in $entries) {
            $entry = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, $item.path)
            $entry.Uid = 0; $entry.Gid = 0; $entry.Mode = [IO.UnixFileMode]420
            $body = [IO.MemoryStream]::new([byte[]]$item.bytes, $false)
            try { $entry.DataStream = $body; $writer.WriteEntry($entry) }
            finally { $body.Dispose() }
        }
    }
    finally { $writer.Dispose(); $file.Dispose() }
    $selectedObservation = if ($Scenario -cin @('oci-hold-classic', 'oci-hold-no-descriptors')) { $null } else { $manifest }
    Write-VerificationJson (Join-Path $Root 'selected-descriptor.json') $selectedObservation
    return [ordered]@{ archive = $archive; localImageId = $Image.Id; rootDescriptor = $rootDescriptor; selectedDescriptor = $manifest; configDigest = $config.digest; layer = $layer
        wrapperSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($wrapperBytes)).ToLowerInvariant() }
}

function New-VerificationFixture([string] $Service, [string] $Scenario) {
    $script:sequence++
    $root = Join-Path $temporaryRoot ('case-' + $script:sequence.ToString('D3') + ' café 雪')
    [IO.Directory]::CreateDirectory($root) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $root 'commands')) | Out-Null
    $payload = Join-Path $root 'payload'; $evidence = Join-Path $root 'evidence'
    [IO.Directory]::CreateDirectory($payload) | Out-Null; [IO.Directory]::CreateDirectory($evidence) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $payload 'nested')) | Out-Null
    $main = if ($Service -ceq 'admin') { 'ConduitLLM.Admin' } else { 'ConduitLLM.Gateway' }
    [IO.File]::WriteAllBytes((Join-Path $payload $main), [Text.Encoding]::UTF8.GetBytes('synthetic-native-' + $Service))
    [IO.File]::WriteAllBytes((Join-Path $payload 'conduit-health-probe'), [byte[]]@(1, 2, 3, 4, 5))
    [IO.File]::WriteAllBytes((Join-Path $payload '.hidden'), [byte[]]@(6, 7, 8))
    [IO.File]::WriteAllText((Join-Path $payload 'nested/café-雪.json'), '{"unicode":"café 雪 🔎"}', [Text.UTF8Encoding]::new($false))
    $tool = Join-Path $root 'synthetic-tool'; [IO.File]::WriteAllBytes($tool, [byte[]]@(9, 10, 11))
    $toolchain = [ordered]@{
        sdkVersion = '10.0.401'; ilCompilerPackage = @{ id = 'runtime.linux-x64.Microsoft.DotNet.ILCompiler'; version = '10.0.12' }
        ilCompiler = @{ path = $tool; sha256 = Get-VerificationHash $tool; bytes = 3L }
        nativeLinkerDriver = @{ command = $tool; path = $tool; sha256 = Get-VerificationHash $tool; bytes = 3L; version = 'synthetic fixture driver'; flavor = $null }
        hostOs = $(if ($IsWindows) { 'windows' } else { 'linux' }); hostArchitecture = 'x64'; targetOs = 'linux'; targetArchitecture = 'x64'
    }
    $properties = @{ configuration = 'Release'; publishAot = $true; selfContained = $true; stripSymbols = $true; conduitAotAudit = $true; useSharedCompilation = $false }
    $source = [ordered]@{ status = 'unavailable'; revision = $null; dirty = $null }
    $buildId = [Guid]::NewGuid().ToString('N')
    $start = [DateTime]::SpecifyKind([DateTime]'2026-10-11T12:34:56', [DateTimeKind]::Utc)
    $application = Write-NativePublishManifest -DestinationPath (Join-Path $evidence 'application.json') -BuildId $buildId -Service $Service -Rid 'linux-x64' -Source $source -Toolchain $toolchain -PublishProperties $properties -StartedAtUtc $start -CompletedAtUtc $start.AddMilliseconds(200) -RuntimeDirectory $payload -ExecutableName $main
    $properties.conduitAotAudit = $false
    $health = Write-NativePublishManifest -DestinationPath (Join-Path $evidence 'health-probe.json') -BuildId $buildId -Service 'probe' -Rid 'linux-x64' -Source $source -Toolchain $toolchain -PublishProperties $properties -StartedAtUtc $start -CompletedAtUtc $start.AddMilliseconds(100) -RuntimeDirectory $payload -ExecutableName 'conduit-health-probe'
    $identity = Get-NativeRuntimeIdentity $payload $main
    $metadata = [ordered]@{ version = '8.1.0'; commitSha = 'dev'; buildTimestamp = '2026-10-11T12:34:56.0000000Z' }
    $metrics = [ordered]@{ publishDurationMilliseconds = 300L; applicationPublishDurationMilliseconds = 200L; healthProbePublishDurationMilliseconds = 100L; executableBytes = $identity.executable.bytes; runtimeArtifactBytes = $identity.runtimeArtifactBytes }
    $wrapper = [ordered]@{ schemaVersion = 1; kind = 'conduit-native-image-publish'; buildId = $buildId; service = $Service; rid = 'linux-x64'; commitMetadata = $metadata
        application = @{ path = 'application.json'; sha256 = $application.sha256 }; healthProbe = @{ path = 'health-probe.json'; sha256 = $health.sha256 }; metrics = $metrics }
    Write-VerificationJson (Join-Path $evidence 'image-publish.json') $wrapper
    Assert-NativeImagePublishEvidenceMatchesPayload -EvidenceDirectory $evidence -PayloadDirectory $payload | Out-Null
    $expectedHashes = @{}
    foreach ($name in @('image-publish.json', 'application.json', 'health-probe.json')) { $expectedHashes[$name] = Get-VerificationHash (Join-Path $evidence $name) }
    $labels = [ordered]@{ 'org.opencontainers.image.version' = $metadata.version; 'org.opencontainers.image.revision' = $metadata.commitSha
        'org.opencontainers.image.created' = $metadata.buildTimestamp; 'org.opencontainers.image.source' = 'https://github.com/nickna/Conduit'
        'io.conduit.runtime' = 'native-aot'; 'io.conduit.promotion' = 'canary-only' }
    if ($Scenario -ceq 'oci-mismatch') { $labels['org.opencontainers.image.version'] = 'unrelated-version' }
    $image = [ordered]@{ Id = $null; Os = 'linux'; Architecture = 'amd64'; Config = @{
        User = '1654'; Entrypoint = @('/app/' + $main); Cmd = $null; Volumes = $null; Labels = $labels
        Env = @('DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false', 'ASPNETCORE_HTTP_PORTS=8080')
        Healthcheck = @{ Test = @('CMD', '/app/conduit-health-probe', 'http://localhost:8080/health/live') }
    } }
    if ($Scenario -ceq 'manifest-mismatch') { [IO.File]::AppendAllText((Join-Path $evidence 'application.json'), ' ') }
    if ($Scenario -ceq 'payload-mismatch') { [IO.File]::AppendAllText((Join-Path $payload $main), 'changed') }
    $archive = Write-VerificationArchive $root $payload $evidence
    $oci = Write-VerificationOciArchive $root $image $archive $Scenario
    Write-VerificationJson (Join-Path $root 'image.json') $image
    return [ordered]@{ root = $root; artifact = Join-Path $root 'artifact'; archive = $archive; service = $Service; scenario = $Scenario
        buildId = $buildId; requestedImage = 'owned-image-verification-fixture:' + $Service; hashes = $expectedHashes
        metadata = $metadata; metrics = $metrics; runtimeFilesSha256 = $identity.filesSha256; main = $main; oci = $oci; localImageId = $oci.localImageId }
}

function Invoke-VerificationCase([string] $Service, [string] $Scenario, [bool] $Success) {
    $fixture = New-VerificationFixture $Service $Scenario
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $(switch ($name) {
            'CONDUIT_IMAGE_VERIFICATION_TEST_ROOT' { $fixture.root }
            'CONDUIT_IMAGE_VERIFICATION_TEST_SCENARIO' { $Scenario }
            'CONDUIT_IMAGE_VERIFICATION_TEST_DIAGNOSTIC' { $privateDiagnostic }
        }))
    }
    $parameters = @{ Image = $fixture.requestedImage; Service = $Service; ArtifactDirectory = $fixture.artifact
        DockerCommandPath = $script:fakeDocker; DockerCommandTimeoutSeconds = 5; CleanupTimeoutSeconds = 15 }
    $held = $null; $sentinel = $null
    if ($Scenario -cin @('fresh-visible', 'fresh-hidden', 'fresh-held-claim')) {
        [IO.Directory]::CreateDirectory($fixture.artifact) | Out-Null
        $name = switch ($Scenario) { 'fresh-visible' { 'sentinel' }; 'fresh-hidden' { '.hidden' }; default { 'native-image-verification.claim' } }
        $sentinel = Join-Path $fixture.artifact $name; [IO.File]::WriteAllText($sentinel, 'existing owner state')
        if ($Scenario -ceq 'fresh-held-claim') { $held = [IO.FileStream]::new($sentinel, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    }
    if ($Scenario -ceq 'tag-flip') { [IO.Directory]::CreateDirectory($fixture.artifact) | Out-Null }
    switch ($Scenario) {
        'invalid-image' { $parameters.Image = ' ' }
        'invalid-service' { $parameters.Service = 'unknown' }
        'invalid-docker-timeout' { $parameters.DockerCommandTimeoutSeconds = 0 }
        'invalid-cleanup-timeout' { $parameters.CleanupTimeoutSeconds = 0 }
        'invalid-docker-path' { $parameters.DockerCommandPath = Join-Path $fixture.root 'missing.ps1' }
    }
    $watch = [Diagnostics.Stopwatch]::StartNew(); $caught = $null; $results = @()
    try { $results = @(Get-NativeImagePackagingEvidence @parameters) }
    catch { $caught = $_ }
    finally { if ($null -ne $held) { $held.Dispose() } }
    $records = Get-VerificationRecords $fixture.root
    $reportPath = Join-Path $fixture.artifact 'packaging-evidence.json'
    $created = @($records | Where-Object { $_.arguments[0] -ceq 'container' -and $_.arguments[1] -ceq 'create' })
    $removed = @($records | Where-Object { $_.arguments[0] -ceq 'container' -and $_.arguments[1] -ceq 'rm' })
    Assert-VerificationContract ($null -eq $caught -or -not $caught.Exception.Message.Contains($privateDiagnostic, [StringComparison]::Ordinal)) 'Verification leaked captured private child diagnostics.'
    if ($Success) {
        Assert-VerificationContract ($null -eq $caught -and $results.Count -eq 1 -and $results[0] -is [Collections.Specialized.OrderedDictionary]) ('Valid packaging verification failed: ' + $Scenario)
        $report = $results[0]; $persisted = Read-VerificationJson $reportPath
        $expectedKeys = @('schemaVersion', 'kind', 'service', 'requestedImage', 'localImageId', 'containerId', 'owner', 'buildId', 'rid', 'commitMetadata', 'source', 'manifestHashes', 'runtimeFilesSha256', 'metrics', 'ociLabels', 'archiveBytes', 'archiveEntries', 'archivedFilePermissions', 'extractionId', 'verifiedAtUtc', 'cleanupConfirmed', 'payloadVerified', 'registryDigestQualification', 'ociAssociation')
        Assert-VerificationContract ($report.Count -eq $expectedKeys.Count -and
            ((@($report.Keys | Sort-Object) -join '|') -ceq (@($expectedKeys | Sort-Object) -join '|'))) 'Packaging report properties differ.'
        Assert-VerificationContract ($report.schemaVersion -is [int] -and $report.schemaVersion -eq 2 -and $report.kind -ceq 'conduit-native-image-packaging' -and
            $report.service -ceq $Service -and $report.requestedImage -ceq $fixture.requestedImage -and
            $report.localImageId -ceq $fixture.localImageId -and $report.containerId -ceq $containerId -and
            $report.owner -cmatch '^[0-9a-f]{32}$' -and $report.buildId -ceq $fixture.buildId -and $report.rid -ceq 'linux-x64') 'Packaging report image/build ownership differs.'
        Assert-VerificationContract ($report.source.status -ceq 'unavailable' -and $null -eq $report.source.revision -and
            $null -eq $report.source.dirty -and $report.commitMetadata.commitSha -ceq 'dev' -and
            $report.registryDigestQualification -is [bool] -and -not $report.registryDigestQualification -and
            $report.cleanupConfirmed -is [bool] -and $report.cleanupConfirmed -and $report.payloadVerified -is [bool] -and $report.payloadVerified) 'Packaging evidence invented source or registry qualification.'
        Assert-VerificationContract ($report.archiveBytes -is [long] -and $report.archiveBytes -eq ([IO.FileInfo]::new($fixture.archive)).Length -and
            $report.archiveEntries -is [int] -and $report.archiveEntries -eq 10 -and $report.archivedFilePermissions.Count -eq 7 -and
            $report.extractionId -cmatch '^[0-9a-f]{32}$' -and $report.runtimeFilesSha256 -ceq $fixture.runtimeFilesSha256) 'Packaging archive/payload inventory differs.'
        Assert-VerificationContract ($report.manifestHashes.Count -eq 3 -and
            ((@($report.manifestHashes.Values | Sort-Object) -join '|') -ceq (@($fixture.hashes.Values | Sort-Object) -join '|'))) 'Packaging evidence hashes do not identify all three exact documents.'
        foreach ($name in $fixture.metrics.Keys) { Assert-VerificationContract ($report.metrics[$name] -eq $fixture.metrics[$name]) 'Packaging publish metrics differ.' }
        $reference = $report.ociAssociation
        Assert-VerificationContract ($reference.Count -eq 3 -and $reference.report.Count -eq 2 -and $reference.observations.Count -eq 2 -and $reference.archive.Count -eq 3 -and
            $reference.report.path -ceq 'oci-association.json' -and $reference.observations.path -ceq 'image-observations.json' -and $reference.archive.path -ceq 'image-save.tar') 'Packaging OCI sidecar references differ.'
        foreach ($part in @($reference.report, $reference.observations, $reference.archive)) {
            Assert-VerificationContract ($part.sha256 -ceq (Get-VerificationHash (Join-Path $fixture.artifact $part.path))) 'Packaging OCI reference is not bound to original retained bytes.'
        }
        Assert-VerificationContract ($reference.archive.bytes -eq ([IO.FileInfo]::new($fixture.oci.archive)).Length -and
            $reference.archive.sha256 -ceq (Get-VerificationHash $fixture.oci.archive)) 'Packaging did not retain the exact original OCI-save archive.'
        $association = Read-VerificationJson (Join-Path $fixture.artifact $reference.report.path)
        $observations = Read-VerificationJson (Join-Path $fixture.artifact $reference.observations.path)
        Assert-VerificationContract ($association.Count -eq 19 -and -not $association.registryDigestQualification -and $association.observedLocalImageId -ceq $fixture.localImageId -and
            $association.archiveSha256 -ceq $reference.archive.sha256 -and $observations.Count -eq 5 -and $observations.schemaVersion -eq 1 -and
            $observations.kind -ceq 'conduit-native-image-observations' -and $observations.requestedImage -ceq $fixture.requestedImage -and
            $observations.image.id -ceq $fixture.localImageId -and $observations.container.image -ceq $fixture.localImageId -and $observations.container.id -ceq $containerId) 'Packaging OCI association changed its image/container observation or invented qualification.'
        if ($Scenario -cin @('oci-hold-no-descriptors', 'oci-hold-classic')) {
            $requiredReason = if ($Scenario -ceq 'oci-hold-classic') { 'classic-save-layout' } else { 'missing-image-descriptor' }
            Assert-VerificationContract ($association.associationStatus -ceq 'hold' -and -not $association.descriptorChainVerified -and
                $requiredReason -cin $association.holdReasons -and $association.selectedLayers.Count -eq 0 -and $null -eq $association.selectedManifestDescriptor) 'Local-only packaging HOLD invented a selected manifest chain.'
        } else {
            Assert-VerificationContract ($association.associationStatus -ceq 'verified' -and $association.descriptorChainVerified -and $association.holdReasons.Count -eq 0 -and
                $association.rootDescriptor.digest -ceq $fixture.oci.rootDescriptor.digest -and $association.selectedManifestDescriptor.digest -ceq $fixture.oci.selectedDescriptor.digest -and
                $association.configDigest -ceq $fixture.oci.configDigest -and $association.layoutIndexSha256 -ceq $fixture.oci.wrapperSha256 -and
                $association.selectedLayers.Count -eq 1 -and $association.selectedLayers[0].digest -ceq $fixture.oci.layer.digest -and $association.selectedLayers[0].size -eq $fixture.oci.layer.size) 'Packaging OCI graph mixed original root, selected manifest, config, wrapper or layer identities.'
        }
        $mainPermissions = $report.archivedFilePermissions['app/' + $fixture.main]
        $healthPermissions = $report.archivedFilePermissions['app/conduit-health-probe']
        Assert-VerificationContract ($mainPermissions.uid -eq 1654 -and $mainPermissions.gid -eq 1654 -and $mainPermissions.mode -eq 493 -and
            $mainPermissions.bytes -eq $fixture.metrics.executableBytes -and $healthPermissions.mode -eq 493 -and $healthPermissions.bytes -eq 5 -and
            $report.ociLabels.Count -eq 6 -and $report.ociLabels.version -ceq $fixture.metadata.version -and
            $report.ociLabels.revision -ceq 'dev' -and $report.ociLabels.created -ceq $fixture.metadata.buildTimestamp -and
            $report.ociLabels.runtime -ceq 'native-aot' -and $report.ociLabels.promotion -ceq 'canary-only') 'Packaging permission or OCI-label report differs.'
        [DateTimeOffset]$verifiedAt = [DateTimeOffset]::MinValue
        Assert-VerificationContract ($report.verifiedAtUtc -is [string] -and $report.verifiedAtUtc.EndsWith('Z', [StringComparison]::Ordinal) -and
            [DateTimeOffset]::TryParse($report.verifiedAtUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$verifiedAt) -and
            $verifiedAt.Offset -eq [TimeSpan]::Zero) 'Packaging verification timestamp was not UTC.'
        Assert-VerificationContract ($persisted.localImageId -ceq $report.localImageId -and $persisted.owner -ceq $report.owner -and
            $persisted.cleanupConfirmed -is [bool] -and $persisted.cleanupConfirmed -and -not $persisted.registryDigestQualification) 'Persisted packaging evidence differs from the returned report.'
        $imageInspects = @($records | Where-Object { $_.arguments[0] -ceq 'image' -and $_.arguments[1] -ceq 'inspect' })
        $imageSaves = @($records | Where-Object { $_.arguments[0] -ceq 'image' -and $_.arguments[1] -ceq 'save' })
        Assert-VerificationContract ($imageInspects.Count -eq 1 -and $imageInspects[0].arguments[-1] -ceq $fixture.requestedImage -and
            $imageSaves.Count -eq 1 -and $imageSaves[0].arguments.Count -eq 5 -and ($imageSaves[0].arguments[0..2] -join '|') -ceq 'image|save|--output' -and $imageSaves[0].arguments[-1] -ceq $fixture.localImageId -and
            $created.Count -eq 1 -and $created[0].arguments[-1] -ceq $fixture.localImageId -and $created[0].arguments[[Array]::IndexOf([object[]]$created[0].arguments, '--platform') + 1] -ceq 'linux/amd64' -and $removed.Count -eq 1 -and
            ($removed[0].arguments -join '|') -ceq ('container|rm|' + $containerId)) 'Verification used a mutable reference or unverified/forced deletion.'
        Assert-VerificationContract (-not [IO.File]::Exists((Join-Path $fixture.root 'active.json')) -and
            @($records | Where-Object reportPresent).Count -eq 0 -and
            [IO.File]::ReadAllText((Join-Path $fixture.artifact 'native-image-verification.claim')) -ceq ($report.owner + "`n")) 'Packaging evidence preceded confirmed cleanup or lost its retained owner claim.'
    }
    else {
        Assert-VerificationContract ($null -ne $caught -and -not [IO.File]::Exists($reportPath)) ('Invalid packaging verification was accepted or wrote success evidence: ' + $Scenario)
        if ($Scenario.StartsWith('fresh-', [StringComparison]::Ordinal) -or $Scenario.StartsWith('invalid-', [StringComparison]::Ordinal)) {
            Assert-VerificationContract ($records.Count -eq 0) 'Verification preflight reached Docker.'
            if ($null -ne $sentinel) { Assert-VerificationContract ([IO.File]::ReadAllText($sentinel) -ceq 'existing owner state' -and [IO.Directory]::GetFileSystemEntries($fixture.artifact).Length -eq 1) 'Verification preflight changed existing owner state.' }
        }
        elseif ($Scenario -cin @('wrong-owner', 'wrong-name', 'wrong-image', 'running')) {
            Assert-VerificationContract ($created.Count -eq 1 -and $removed.Count -eq 0 -and [IO.File]::Exists((Join-Path $fixture.root 'active.json'))) 'Verification removed an unassociated or running container.'
        }
        elseif ($Scenario -cin @('rm-failure', 'combined-failure')) {
            Assert-VerificationContract ($removed.Count -eq 1 -and [IO.File]::Exists((Join-Path $fixture.root 'active.json'))) 'Removal failure did not remain fail-closed.'
        }
        elseif ($created.Count -gt 0) {
            Assert-VerificationContract ($removed.Count -eq 1 -and -not [IO.File]::Exists((Join-Path $fixture.root 'active.json'))) 'Failed verification did not clean its associated stopped container.'
        }
        if ($Scenario -cin @('create-nonzero', 'create-malformed', 'create-timeout')) {
            $createdName = $created[0].arguments[[Array]::IndexOf([object[]]$created[0].arguments, '--name') + 1]
            Assert-VerificationContract (@($records | Where-Object { $_.arguments[0] -ceq 'container' -and $_.arguments[1] -ceq 'inspect' -and $_.arguments[-1] -ceq $createdName }).Count -gt 0) 'Unknown create outcome was not recovered by its exact owned name.'
        }
        if ($Scenario -ceq 'combined-failure') {
            Assert-VerificationContract ($caught.Exception.Message.StartsWith('Native image packaging and owned cleanup failed;', [StringComparison]::Ordinal)) 'Combined primary and cleanup failure was lost.'
        }
        if ($Scenario -ceq 'oci-save-late-mutation') {
            Assert-VerificationContract (([IO.FileInfo]::new((Join-Path $fixture.artifact 'image-save.tar'))).Length -eq ([IO.FileInfo]::new($fixture.oci.archive)).Length + 1) 'Late OCI mutation fixture did not alter its retained archive.'
        }
        if ($Scenario -cin @('image-timeout', 'image-overflow', 'create-timeout', 'export-timeout', 'oci-save-timeout')) {
            Assert-VerificationContract ([IO.File]::Exists((Join-Path $fixture.root 'paused-process.json')) -and $watch.Elapsed.TotalSeconds -lt 30) 'Bounded command fixture did not reach its latched failure or deadline.'
            Assert-VerificationPausedProcessReaped $fixture.root
        }
    }
    $script:cases++
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $loadResults = @(. $helper)
    Assert-VerificationContract ($loadResults.Count -eq 0 -and [IO.Directory]::GetFileSystemEntries($temporaryRoot).Length -eq 0) 'Loading image verification changed the owned directory or produced output.'
    $cases++
    $script:fakeDocker = Join-Path $temporaryRoot 'fake Docker café 雪.ps1'
    @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]] $DockerArguments)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$root = $env:CONDUIT_IMAGE_VERIFICATION_TEST_ROOT; $scenario = $env:CONDUIT_IMAGE_VERIFICATION_TEST_SCENARIO
$privateDiagnostic = $env:CONDUIT_IMAGE_VERIFICATION_TEST_DIAGNOSTIC
$otherImageId = 'sha256:' + ('b' * 64); $id = 'c' * 64
$activePath = Join-Path $root 'active.json'; $removedPath = Join-Path $root 'removed.json'
function Read-Json([string] $Path) { return [IO.File]::ReadAllText($Path) | ConvertFrom-Json -AsHashtable -DateKind String }
function Write-Json([string] $Path, $Value) { [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 32), [Text.UTF8Encoding]::new($false)) }
$imageId = (Read-Json (Join-Path $root 'image.json')).Id
function Pause-Command {
    $process = [Diagnostics.Process]::GetCurrentProcess()
    try {
        if ($IsLinux) {
            $stat = [IO.File]::ReadAllText('/proc/' + $process.Id + '/stat'); $separator = $stat.LastIndexOf(') ', [StringComparison]::Ordinal)
            $fields = $stat.Substring($separator + 2).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)
            $token = 'linux:' + $fields[19]
        }
        else { $token = 'windows:' + $process.StartTime.ToUniversalTime().Ticks.ToString([Globalization.CultureInfo]::InvariantCulture) }
        Write-Json (Join-Path $root 'paused-process.json') @{ id = $process.Id; token = $token }
    }
    finally { $process.Dispose() }
}
function Missing-Container([string] $Lookup) {
    if ($scenario -ceq 'missing-diagnostic') { [Console]::Error.WriteLine('Unrecognized missing container response.') }
    else { [Console]::Error.WriteLine('Error response from daemon: No such container: ' + $Lookup) }
    exit 1
}
$ordinal = [IO.Directory]::GetFiles((Join-Path $root 'commands')).Length + 1
Write-Json (Join-Path $root ('commands/' + $ordinal.ToString('D3') + '.json')) @{ ordinal = $ordinal; arguments = @($DockerArguments); reportPresent = [IO.File]::Exists((Join-Path $root 'artifact/packaging-evidence.json')) }
if ($DockerArguments[0] -ceq 'image' -and $DockerArguments[1] -ceq 'inspect') {
    if (($DockerArguments[2..3] -join '|') -cne '--format|{{json .}}' -or $DockerArguments.Count -ne 5) { throw 'Invalid fixture image inspect arguments.' }
    if ($scenario -ceq 'image-nonzero') { [Console]::Error.WriteLine($privateDiagnostic); exit 17 }
    if ($scenario -ceq 'image-malformed') { [Console]::Out.WriteLine('{'); exit 0 }
    if ($scenario -cin @('image-timeout', 'image-overflow')) {
        Pause-Command
        if ($scenario -ceq 'image-overflow') { [Console]::Out.Write([string]::new([char]120, 4194305)) }
        Start-Sleep -Seconds 60; exit 0
    }
    $image = Read-Json (Join-Path $root 'image.json')
    if ($scenario -ceq 'tag-flip' -and $ordinal -gt 1) { $image.Id = $otherImageId }
    [Console]::Out.WriteLine((ConvertTo-Json -InputObject $image -Depth 32 -Compress)); exit 0
}
if ($DockerArguments[0] -ceq 'image' -and $DockerArguments[1] -ceq 'save') {
    if ($DockerArguments.Count -ne 5 -or $DockerArguments[2] -cne '--output' -or $DockerArguments[-1] -cne $imageId) { throw 'OCI save did not use the exact immutable local ID without platform filtering.' }
    if ($scenario -ceq 'oci-save-nonzero') { [Console]::Error.WriteLine($privateDiagnostic); exit 17 }
    if ($scenario -ceq 'oci-save-timeout') { Pause-Command; Start-Sleep -Seconds 60; exit 0 }
    [IO.File]::Copy((Join-Path $root 'fixture-save.tar'), $DockerArguments[3], $false); exit 0
}
if ($DockerArguments[0] -cne 'container') { throw 'Invalid fixture Docker command.' }
switch ($DockerArguments[1]) {
    'create' {
        $nameIndex = [Array]::IndexOf($DockerArguments, '--name'); $networkIndex = [Array]::IndexOf($DockerArguments, '--network')
        $platformIndex = [Array]::IndexOf($DockerArguments, '--platform')
        if ($nameIndex -lt 0 -or $networkIndex -lt 0 -or $platformIndex -lt 0 -or $DockerArguments[$platformIndex + 1] -cne 'linux/amd64' -or $DockerArguments[$networkIndex + 1] -cne 'none' -or $DockerArguments[-1] -cne $imageId) { throw 'Invalid fixture container create arguments.' }
        $name = $DockerArguments[$nameIndex + 1]; $labels = @{}
        for ($index = 0; $index -lt $DockerArguments.Length; $index++) {
            if ($DockerArguments[$index] -ceq '--label') { $parts = $DockerArguments[$index + 1].Split('=', 2); $labels[$parts[0]] = $parts[1] }
        }
        $container = @{ Id = $id; Name = '/' + $name; Image = $imageId; ImageManifestDescriptor = Read-Json (Join-Path $root 'selected-descriptor.json'); Config = @{ Labels = $labels }
            HostConfig = @{ NetworkMode = 'none'; PortBindings = $null }; State = @{ Running = $false; Status = 'created' } }
        switch ($scenario) {
            'wrong-owner' { $container.Config.Labels['conduit.proof.owner'] = 'd' * 32 }
            'wrong-name' { $container.Name = '/foreign-container' }
            'wrong-image' { $container.Image = $otherImageId }
            'running' { $container.State.Running = $true; $container.State.Status = 'running' }
            'oci-selected-mismatch' { $container.ImageManifestDescriptor.digest = 'sha256:' + ('d' * 64) }
        }
        Write-Json $activePath @{ lookupName = $name; container = $container }
        if ($scenario -ceq 'create-nonzero') { [Console]::Error.WriteLine($privateDiagnostic); exit 17 }
        if ($scenario -ceq 'create-malformed') { [Console]::Out.WriteLine('malformed-container-id'); exit 0 }
        if ($scenario -ceq 'create-timeout') { Pause-Command; Start-Sleep -Seconds 60; exit 0 }
        [Console]::Out.WriteLine($id); exit 0
    }
    'inspect' {
        if (($DockerArguments[2..3] -join '|') -cne '--format|{{json .}}' -or $DockerArguments.Count -ne 5) { throw 'Invalid fixture container inspect arguments.' }
        $lookup = $DockerArguments[-1]
        if ($scenario -ceq 'container-malformed-first' -and -not [IO.File]::Exists((Join-Path $root 'malformed-inspection-seen'))) {
            [IO.File]::WriteAllText((Join-Path $root 'malformed-inspection-seen'), 'seen'); [Console]::Out.WriteLine('{'); exit 0
        }
        $state = if ([IO.File]::Exists($activePath)) { Read-Json $activePath }
            elseif ($scenario -ceq 'absence-failure' -and [IO.File]::Exists($removedPath)) { Read-Json $removedPath }
            else { Missing-Container $lookup }
        if ($lookup -cne $id -and $lookup -cne $state.lookupName) { Missing-Container $lookup }
        [Console]::Out.WriteLine((ConvertTo-Json -InputObject $state.container -Depth 32 -Compress)); exit 0
    }
    'export' {
        if ($DockerArguments.Count -ne 5 -or $DockerArguments[2] -cne '--output' -or $DockerArguments[-1] -cne $id) { throw 'Invalid fixture export arguments.' }
        if ($scenario -cin @('export-nonzero', 'combined-failure')) { [Console]::Error.WriteLine($privateDiagnostic); exit 17 }
        if ($scenario -ceq 'export-timeout') { Pause-Command; Start-Sleep -Seconds 60; exit 0 }
        [IO.File]::Copy((Join-Path $root 'fixture.tar'), $DockerArguments[3], $false); exit 0
    }
    'rm' {
        if ($DockerArguments.Count -ne 3 -or $DockerArguments[-1] -cne $id) { throw 'Invalid or forced fixture removal arguments.' }
        if ($scenario -cin @('rm-failure', 'combined-failure')) { [Console]::Error.WriteLine($privateDiagnostic); exit 17 }
        [IO.File]::Copy($activePath, $removedPath, $false); [IO.File]::Delete($activePath)
        if ($scenario -ceq 'oci-save-late-mutation') {
            $append = [IO.File]::Open((Join-Path $root 'artifact/image-save.tar'), [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $append.WriteByte(1) }
            finally { $append.Dispose() }
        }
        [Console]::Out.WriteLine($(if ($scenario -ceq 'rm-output') { 'wrong-removed-id' } else { $id })); exit 0
    }
    default { throw 'Unexpected fixture Docker operation.' }
}
'@ | Set-Content -LiteralPath $script:fakeDocker -Encoding utf8NoBOM

    foreach ($positive in @(@{ Service = 'admin'; Scenario = 'success' }, @{ Service = 'gateway'; Scenario = 'success' }, @{ Service = 'admin'; Scenario = 'tag-flip' },
        @{ Service = 'admin'; Scenario = 'oci-hold-no-descriptors' }, @{ Service = 'admin'; Scenario = 'oci-hold-classic' })) {
        Invoke-VerificationCase $positive.Service $positive.Scenario $true
    }
    foreach ($scenario in @('image-nonzero', 'image-malformed', 'image-timeout', 'image-overflow',
        'create-nonzero', 'create-malformed', 'create-timeout', 'export-nonzero', 'export-timeout',
        'manifest-mismatch', 'payload-mismatch', 'oci-mismatch', 'container-malformed-first',
        'wrong-owner', 'wrong-name', 'wrong-image', 'running', 'rm-failure', 'rm-output',
        'absence-failure', 'missing-diagnostic', 'combined-failure',
        'fresh-visible', 'fresh-hidden', 'fresh-held-claim', 'invalid-image', 'invalid-service',
        'invalid-docker-timeout', 'invalid-cleanup-timeout', 'invalid-docker-path',
        'oci-save-nonzero', 'oci-save-timeout', 'oci-blob-corrupt', 'oci-selected-mismatch', 'oci-save-late-mutation')) {
        Invoke-VerificationCase 'admin' $scenario $false
    }
}
catch { $failure = $_ }
finally {
    foreach ($name in $environmentNames) {
        try {
            if ($null -eq $oldEnvironment[$name]) {
                [Environment]::SetEnvironmentVariable($name, [NullString]::Value)
            }
            else { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name]) }
            $restored = [Environment]::GetEnvironmentVariable($name)
            $restoredExactly = if ($null -eq $oldEnvironment[$name]) { $null -eq $restored }
                else { [string]::Equals($restored, $oldEnvironment[$name], [StringComparison]::Ordinal) }
            Assert-VerificationContract $restoredExactly 'Verification fixture environment was not restored.'
        }
        catch { $cleanupFailure = $true }
    }
    try {
        $resolved = [IO.Path]::GetFullPath($temporaryRoot)
        Assert-VerificationContract ([IO.Path]::GetDirectoryName($resolved) -ceq $temporaryParent -and
            [IO.Path]::GetFileName($resolved) -cmatch '^conduit-image-verification-contract-[0-9a-f]{32}$') 'Verification fixture cleanup target was rejected.'
        foreach ($directory in @(([IO.DirectoryInfo]::new($resolved)).EnumerateDirectories('case-*'))) {
            Assert-VerificationPausedProcessReaped $directory.FullName -Stop
        }
        if ([IO.Directory]::Exists($resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
        Assert-VerificationContract (-not [IO.Directory]::Exists($resolved)) 'Verification fixture directory remained after cleanup.'
    }
    catch { $cleanupFailure = $true }
}
if ($cleanupFailure) { throw 'Native image verification contracts could not clean their owned resources.' }
if ($null -ne $failure) { throw $failure }
Write-Output ('Verified native image verification contracts; cases=' + $cases + '; skipped=0; cleanupConfirmed=true.')
