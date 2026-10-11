# Execute an immutable local image already bound to retained packaging evidence.
# Loading this helper does not invoke Docker or create artifacts.
. (Join-Path $PSScriptRoot 'native-image-verification.ps1')

function Read-NativeImageRuntimeClaim([string] $Path, [string] $Owner) {
    Assert-NativeEvidenceRegularFile $Path
    $file = Get-Item -LiteralPath $Path
    if ($file.Length -ne 33 -or [Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($file.FullName)) -cne ($Owner + "`n")) {
        throw 'Native image evidence claim does not match its owner.'
    }
}

function Get-NativeImageRuntimePackaging {
    param([string] $Directory)
    $root = Get-NativeEvidenceFullPath $Directory
    Assert-NativeEvidencePathAncestry $root
    if (-not [IO.Directory]::Exists($root) -or ($IsLinux -and (Get-NativeEvidenceUnixFileType $root) -ne 0x4000)) {
        throw 'Completed native image packaging directory is invalid.'
    }
    $path = [IO.Path]::Combine($root, 'packaging-evidence.json')
    $before = Get-NativeEvidenceFileHash (Get-Item -LiteralPath $path)
    $report = Read-NativeImageEvidenceDocument $path
    Assert-NativeEvidenceKeys $report @('schemaVersion', 'kind', 'service', 'requestedImage', 'localImageId',
        'containerId', 'owner', 'buildId', 'rid', 'commitMetadata', 'source', 'manifestHashes',
        'runtimeFilesSha256', 'metrics', 'ociLabels', 'archiveBytes', 'archiveEntries', 'archivedFilePermissions',
        'extractionId', 'verifiedAtUtc', 'cleanupConfirmed', 'payloadVerified', 'registryDigestQualification') -Exact
    Assert-NativeEvidenceInteger $report['schemaVersion'] -Positive
    if ($report['schemaVersion'] -ne 1 -or $report['kind'] -isnot [string] -or
        $report['kind'] -cne 'conduit-native-image-packaging' -or $report['service'] -isnot [string] -or
        $report['service'] -cnotin @('admin', 'gateway') -or $report['rid'] -isnot [string] -or
        $report['rid'] -cne 'linux-x64' -or $report['localImageId'] -isnot [string] -or
        $report['localImageId'] -cnotmatch '\Asha256:[0-9a-f]{64}\z' -or
        $report['containerId'] -isnot [string] -or $report['containerId'] -cnotmatch '\A[0-9a-f]{64}\z' -or
        $report['owner'] -isnot [string] -or $report['owner'] -cnotmatch '\A[0-9a-f]{32}\z' -or
        $report['extractionId'] -isnot [string] -or $report['extractionId'] -cnotmatch '\A[0-9a-f]{32}\z' -or
        $report['buildId'] -isnot [string] -or $report['buildId'] -cnotmatch '\A[0-9a-f]{32}\z' -or
        $report['owner'] -ceq ('0' * 32) -or $report['extractionId'] -ceq ('0' * 32) -or
        $report['cleanupConfirmed'] -isnot [bool] -or -not $report['cleanupConfirmed'] -or
        $report['payloadVerified'] -isnot [bool] -or -not $report['payloadVerified'] -or
        $report['registryDigestQualification'] -isnot [bool] -or $report['registryDigestQualification']) {
        throw 'Completed native image packaging report is invalid.'
    }
    Assert-NativeEvidenceText $report['requestedImage']
    Assert-NativeEvidenceEnvelopeIdentity $report['buildId'] $report['service'] $report['rid']
    Assert-NativeEvidenceText $report['verifiedAtUtc']
    $null = ConvertTo-NativeEvidenceUtc $report['verifiedAtUtc']
    Read-NativeImageRuntimeClaim ([IO.Path]::Combine($root, 'native-image-verification.claim')) $report['owner']
    $extracted = [IO.Path]::Combine($root, 'extracted')
    Read-NativeImageRuntimeClaim ([IO.Path]::Combine($extracted, 'native-image-extraction.claim')) $report['extractionId']
    $archive = [IO.Path]::Combine($root, 'rootfs.tar')
    Assert-NativeEvidenceRegularFile $archive
    Assert-NativeEvidenceInteger $report['archiveBytes'] -Positive
    Assert-NativeEvidenceInteger $report['archiveEntries'] -Positive
    if ($report['archiveBytes'] -lt 1024 -or $report['archiveBytes'] -gt 4294967296L -or
        $report['archiveBytes'] % 512 -ne 0 -or $report['archiveEntries'] -gt 200000 -or
        (Get-Item -LiteralPath $archive).Length -ne $report['archiveBytes']) {
        throw 'Completed native image archive metadata is invalid.'
    }
    $payload = [IO.Path]::Combine($extracted, 'app')
    $evidence = [IO.Path]::Combine($extracted, 'conduit-evidence')
    $summary = Assert-NativeImagePublishEvidenceMatchesPayload -EvidenceDirectory $evidence -PayloadDirectory $payload
    $application = Read-NativeImageEvidenceDocument ([IO.Path]::Combine($evidence, 'application.json'))
    if ($report['buildId'] -cne $summary['buildId'] -or $report['service'] -cne $summary['service'] -or
        $report['rid'] -cne $summary['rid'] -or $report['runtimeFilesSha256'] -isnot [string] -or
        $report['runtimeFilesSha256'] -cnotmatch '\A[0-9a-f]{64}\z' -or
        $report['runtimeFilesSha256'] -cne $application['runtimeFilesSha256']) {
        throw 'Completed native image packaging identity differs from its embedded evidence.'
    }
    $metadata = Get-NativeEvidenceObject $report['commitMetadata']
    Assert-NativeEvidenceKeys $metadata @('version', 'commitSha', 'buildTimestamp') -Exact
    foreach ($field in $metadata.Keys) {
        Assert-NativeEvidenceText $metadata[$field]
        if ($metadata[$field] -cne $summary['commitMetadata'][$field]) {
            throw 'Completed native image declared metadata differs from its embedded evidence.'
        }
    }
    $source = Get-NativeEvidenceObject $report['source']
    Assert-NativeEvidenceKeys $source @('status', 'revision', 'dirty') -Exact
    if ($source['status'] -isnot [string] -or $source['status'] -cnotin @('git', 'unavailable') -or
        ($source['status'] -ceq 'unavailable' -and ($null -ne $source['revision'] -or $null -ne $source['dirty'])) -or
        ($source['status'] -ceq 'git' -and ($source['revision'] -isnot [string] -or
            $source['revision'] -cnotmatch '\A(?:[0-9a-f]{40}|[0-9a-f]{64})\z' -or $source['dirty'] -isnot [bool]))) {
        throw 'Completed native image source metadata is invalid.'
    }
    foreach ($field in $source.Keys) {
        if ($source[$field] -cne $application['source'][$field]) {
            throw 'Completed native image source differs from its embedded evidence.'
        }
    }
    $metrics = Get-NativeEvidenceObject $report['metrics']
    Assert-NativeEvidenceKeys $metrics @('publishDurationMilliseconds', 'applicationPublishDurationMilliseconds',
        'healthProbePublishDurationMilliseconds', 'executableBytes', 'runtimeArtifactBytes') -Exact
    foreach ($field in $metrics.Keys) {
        Assert-NativeEvidenceInteger $metrics[$field] -Positive
        if ($metrics[$field] -ne $summary['metrics'][$field]) {
            throw 'Completed native image metrics differ from its embedded evidence.'
        }
    }
    $ociLabels = Get-NativeEvidenceObject $report['ociLabels']
    Assert-NativeEvidenceKeys $ociLabels @('version', 'revision', 'created', 'source', 'runtime', 'promotion') -Exact
    foreach ($field in $ociLabels.Keys) { Assert-NativeEvidenceText $ociLabels[$field] }
    if ($ociLabels['version'] -cne $metadata['version'] -or $ociLabels['revision'] -cne $metadata['commitSha'] -or
        $ociLabels['created'] -cne $metadata['buildTimestamp'] -or $ociLabels['runtime'] -cne 'native-aot' -or
        $ociLabels['promotion'] -cne 'canary-only' -or $ociLabels['source'] -cne 'https://github.com/nickna/Conduit') {
        throw 'Completed native image OCI labels differ from its embedded evidence.'
    }
    $manifestHashes = Get-NativeEvidenceObject $report['manifestHashes']
    Assert-NativeEvidenceKeys $manifestHashes @('application.json', 'health-probe.json', 'image-publish.json') -Exact
    foreach ($field in $manifestHashes.Keys) {
        if ($manifestHashes[$field] -isnot [string] -or $manifestHashes[$field] -cnotmatch '\A[0-9a-f]{64}\z' -or
            $manifestHashes[$field] -cne (Get-NativeEvidenceFileHash (Get-Item -LiteralPath ([IO.Path]::Combine($evidence, $field))))['sha256']) {
            throw 'Completed native image manifest hashes differ from their retained files.'
        }
    }
    $permissions = Get-NativeEvidenceObject $report['archivedFilePermissions']
    $expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($file in $application['files']) { $expected.Add('app/' + $file['path'], $file['bytes']) }
    foreach ($field in $manifestHashes.Keys) {
        $expected.Add('conduit-evidence/' + $field, [long](Get-Item -LiteralPath ([IO.Path]::Combine($evidence, $field))).Length)
    }
    if ($permissions.Count -ne $expected.Count) { throw 'Completed native image permissions omit or add files.' }
    $names = Get-NativeImageExecutableNames $report['service'] 'linux-x64'
    foreach ($field in $permissions.Keys) {
        Assert-NativeEvidenceRelativePath $field
        if (-not $expected.ContainsKey($field)) { throw 'Completed native image permission path is invalid.' }
        $permission = Get-NativeEvidenceObject $permissions[$field]
        Assert-NativeEvidenceKeys $permission @('uid', 'gid', 'mode', 'bytes') -Exact
        foreach ($scalar in $permission.Keys) { Assert-NativeEvidenceInteger $permission[$scalar] }
        if ($permission['uid'] -ne 1654 -or $permission['gid'] -ne 1654 -or
            $permission['bytes'] -ne $expected[$field] -or
            ($permission['mode'] -band [int][IO.UnixFileMode]::UserRead) -eq 0 -or
            ($permission['mode'] -band 3584) -ne 0 -or ($permission['mode'] -band -4096) -ne 0 -or
            ($field -cin @(('app/' + $names['application']), ('app/' + $names['healthProbe'])) -and
                ($permission['mode'] -band [int][IO.UnixFileMode]::UserExecute) -eq 0)) {
            throw 'Completed native image file permissions are invalid.'
        }
    }
    if ((Get-NativeEvidenceFileHash (Get-Item -LiteralPath $path))['sha256'] -cne $before['sha256']) {
        throw 'Completed native image packaging report changed during verification.'
    }
    return [ordered]@{ directory = $root; path = $path; sha256 = $before['sha256']; report = $report }
}

function Get-NativeImageRuntimeContainer {
    param(
        [Collections.IDictionary] $Record,
        [Collections.IDictionary] $Command,
        [double] $TimeoutSeconds,
        [double] $ReapTimeoutSeconds = 5,
        [switch] $AllowMissing,
        [switch] $AllowRunning,
        [string] $ExpectedId
    )
    $identity = if ($null -ne $Record['containerId']) { $Record['containerId'] } else { $Record['name'] }
    $result = Invoke-NativeImageVerificationDocker -Command $Command -Arguments @(
        'container', 'inspect', '--format', '{{json .}}', $identity
    ) -TimeoutSeconds $TimeoutSeconds -ReapTimeoutSeconds $ReapTimeoutSeconds
    if ($result['exitCode'] -ne 0) {
        if ($AllowMissing -and [string]::IsNullOrWhiteSpace($result['stdout']) -and $result['stderr'].Trim() -cin @(
            "Error: No such object: $identity", "Error: No such container: $identity",
            "Error response from daemon: No such container: $identity")) { return $null }
        throw 'Native image runtime container inspection failed.'
    }
    $container = Read-NativeImageVerificationJson $result['stdout']
    $config = Get-NativeEvidenceObject $container['Config']
    $labels = Get-NativeEvidenceObject $config['Labels']
    $hostConfig = Get-NativeEvidenceObject $container['HostConfig']
    $state = Get-NativeEvidenceObject $container['State']
    $names = Get-NativeImageExecutableNames $Record['service'] 'linux-x64'
    if ($container['Id'] -isnot [string] -or $container['Id'] -cnotmatch '\A[0-9a-f]{64}\z' -or
        $container['Name'] -isnot [string] -or $container['Name'] -cne ('/' + $Record['name']) -or
        ($null -ne $Record['containerId'] -and $container['Id'] -cne $Record['containerId']) -or
        (-not [string]::IsNullOrEmpty($ExpectedId) -and $container['Id'] -cne $ExpectedId) -or
        $container['Image'] -isnot [string] -or $container['Image'] -cne $Record['packaging']['localImageId'] -or
        $labels['conduit.proof.owner'] -isnot [string] -or $labels['conduit.proof.owner'] -cne $Record['owner'] -or
        $labels['conduit.proof.service'] -isnot [string] -or $labels['conduit.proof.service'] -cne $Record['service'] -or
        $labels['conduit.epic'] -isnot [string] -or $labels['conduit.epic'] -cne '1368' -or
        $labels['conduit.fixture'] -isnot [string] -or $labels['conduit.fixture'] -cne 'native-image-entrypoint' -or
        $hostConfig['NetworkMode'] -isnot [string] -or $hostConfig['NetworkMode'] -cne 'none' -or
        $state['Running'] -isnot [bool] -or (-not $AllowRunning -and $state['Running']) -or
        $config['User'] -isnot [string] -or $config['User'] -cnotin @('1654', '1654:1654') -or
        $config['Entrypoint'] -isnot [array] -or $config['Entrypoint'].Count -ne 1 -or
        $config['Entrypoint'][0] -isnot [string] -or $config['Entrypoint'][0] -cne ('/app/' + $names['application'])) {
        throw 'Native image runtime container ownership or configuration was rejected.'
    }
    foreach ($pair in @(@('version', 'version'), @('revision', 'revision'), @('created', 'created'),
            @('source', 'source'), @('runtime', 'io.conduit.runtime'), @('promotion', 'io.conduit.promotion'))) {
        $labelName = if ($pair[0] -cin @('runtime', 'promotion')) { $pair[1] } else { 'org.opencontainers.image.' + $pair[1] }
        if ($labels[$labelName] -isnot [string] -or $labels[$labelName] -cne $Record['packaging']['ociLabels'][$pair[0]]) {
            throw 'Native image runtime labels differ from its packaged identity.'
        }
    }
    if ($config['Env'] -isnot [array]) { throw 'Native image runtime environment is invalid.' }
    foreach ($expected in @('CONDUIT_OPENAPI_GENERATION=true', 'CONDUIT_OPENAPI_OUTPUT=/tmp/packaged-openapi.json',
            'DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false')) {
        $prefix = $expected.Substring(0, $expected.IndexOf('=') + 1)
        $values = @($config['Env'] | Where-Object { $_ -is [string] -and $_.StartsWith($prefix, [StringComparison]::Ordinal) })
        if ($values.Count -ne 1 -or $values[0] -cne $expected) { throw 'Native image runtime environment is invalid.' }
    }
    $Record['containerId'] = $container['Id']
    return $container
}

function Get-NativeImageRuntimeBudget([Diagnostics.Stopwatch] $Watch, [int] $Seconds, [int] $CommandSeconds) {
    $remaining = $Seconds - $Watch.Elapsed.TotalSeconds
    if ($remaining -le 0.05) { throw 'Native image runtime operation deadline expired.' }
    $reap = [Math]::Min(5.0, $remaining / 2)
    return [ordered]@{ work = [Math]::Min([double]$CommandSeconds, $remaining - $reap); reap = $reap }
}

function Remove-NativeImageRuntimeContainer {
    param([Collections.IDictionary] $Record, [Collections.IDictionary] $Command,
        [int] $CleanupTimeoutSeconds, [int] $DockerCommandTimeoutSeconds)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $budget = Get-NativeImageRuntimeBudget $watch $CleanupTimeoutSeconds $DockerCommandTimeoutSeconds
        $container = Get-NativeImageRuntimeContainer -Record $Record -Command $Command -AllowMissing -AllowRunning -TimeoutSeconds $budget['work'] -ReapTimeoutSeconds $budget['reap']
        if ($null -eq $container) { return }
        if ($container['State']['Running']) {
            $budget = Get-NativeImageRuntimeBudget $watch $CleanupTimeoutSeconds $DockerCommandTimeoutSeconds
            $grace = [int][Math]::Max(0, [Math]::Min(3, [Math]::Floor($budget['work'] / 2)))
            $stopped = Invoke-NativeImageVerificationDocker -Command $Command -Arguments @('container', 'stop', '--time', $grace.ToString([Globalization.CultureInfo]::InvariantCulture), $Record['containerId']) -TimeoutSeconds $budget['work'] -ReapTimeoutSeconds $budget['reap']
            if ($stopped['exitCode'] -ne 0 -or $stopped['stdout'].Trim() -cne $Record['containerId']) {
                throw 'Native image owned runtime container stop failed.'
            }
            $budget = Get-NativeImageRuntimeBudget $watch $CleanupTimeoutSeconds $DockerCommandTimeoutSeconds
            $null = Get-NativeImageRuntimeContainer -Record $Record -Command $Command -TimeoutSeconds $budget['work'] -ReapTimeoutSeconds $budget['reap']
        }
        $budget = Get-NativeImageRuntimeBudget $watch $CleanupTimeoutSeconds $DockerCommandTimeoutSeconds
        $removed = Invoke-NativeImageVerificationDocker -Command $Command -Arguments @('container', 'rm', $Record['containerId']) -TimeoutSeconds $budget['work'] -ReapTimeoutSeconds $budget['reap']
        if ($removed['exitCode'] -ne 0 -or $removed['stdout'].Trim() -cne $Record['containerId']) {
            throw 'Native image owned runtime container removal failed.'
        }
        $budget = Get-NativeImageRuntimeBudget $watch $CleanupTimeoutSeconds $DockerCommandTimeoutSeconds
        $container = Get-NativeImageRuntimeContainer -Record $Record -Command $Command -AllowMissing -TimeoutSeconds $budget['work'] -ReapTimeoutSeconds $budget['reap']
        if ($null -ne $container -or $watch.Elapsed.TotalSeconds -gt $CleanupTimeoutSeconds) {
            throw 'Native image runtime container absence was not confirmed.'
        }
    }
    finally { $watch.Stop() }
}

function Assert-NativeImageOpenApiJsonValue([Text.Json.JsonElement] $Value, [int] $Depth = 0) {
    if ($Depth -gt 128) { throw 'Native image OpenAPI document exceeds its depth bound.' }
    if ($Value.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Value.EnumerateObject()) {
            if (-not $names.Add($property.Name)) { throw 'Native image OpenAPI document has duplicate fields.' }
            Assert-NativeImageOpenApiJsonValue $property.Value ($Depth + 1)
        }
    }
    elseif ($Value.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        foreach ($item in $Value.EnumerateArray()) { Assert-NativeImageOpenApiJsonValue $item ($Depth + 1) }
    }
}

function Get-NativeImageOpenApiIdentity([string] $Path) {
    Assert-NativeEvidenceRegularFile $Path
    $file = Get-Item -LiteralPath $Path
    if ($file.Length -le 0 -or $file.Length -gt 16777216) { throw 'Native image OpenAPI document size is invalid.' }
    $before = Get-NativeEvidenceFileHash $file
    $document = $null
    try {
        $options = [Text.Json.JsonDocumentOptions]::new()
        $options.MaxDepth = 128
        $document = [Text.Json.JsonDocument]::Parse([Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($file.FullName)), $options)
        Assert-NativeImageOpenApiJsonValue $document.RootElement
        $version = [Text.Json.JsonElement]::new()
        $paths = [Text.Json.JsonElement]::new()
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object -or
            -not $document.RootElement.TryGetProperty('openapi', [ref]$version) -or
            $version.ValueKind -ne [Text.Json.JsonValueKind]::String -or [string]::IsNullOrWhiteSpace($version.GetString()) -or
            -not $document.RootElement.TryGetProperty('paths', [ref]$paths) -or $paths.ValueKind -ne [Text.Json.JsonValueKind]::Object) {
            throw 'Native image entrypoint did not produce an OpenAPI contract.'
        }
        $count = 0
        foreach ($property in $paths.EnumerateObject()) { $count++ }
        if ($count -le 0) { throw 'Native image entrypoint produced no OpenAPI paths.' }
    }
    catch { throw 'Native image entrypoint OpenAPI document is invalid.' }
    finally { if ($null -ne $document) { $document.Dispose() } }
    $after = Get-NativeEvidenceFileHash (Get-Item -LiteralPath $Path)
    if ($before['sha256'] -cne $after['sha256'] -or $before['bytes'] -ne $after['bytes']) {
        throw 'Native image OpenAPI document changed during verification.'
    }
    return [ordered]@{ paths = $count; bytes = $after['bytes']; sha256 = $after['sha256'] }
}

function Get-NativeImageRuntimeEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $PackagingDirectory,
        [Parameter(Mandatory = $true)][string] $ArtifactDirectory,
        [string] $DockerCommandPath = 'docker',
        [ValidateRange(1, 120)][int] $DockerCommandTimeoutSeconds = 30,
        [ValidateRange(1, 300)][int] $ExecutionTimeoutSeconds = 120,
        [ValidateRange(1, 30)][int] $CleanupTimeoutSeconds = 15
    )
    $ErrorActionPreference = 'Stop'
    $root = Get-NativeEvidenceFullPath $ArtifactDirectory
    $packageRoot = Get-NativeEvidenceFullPath $PackagingDirectory
    Assert-NativeImageArchiveOutputEmpty $root
    Assert-NativeEvidencePathAncestry $packageRoot
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $separator = [IO.Path]::DirectorySeparatorChar
    if ($root.Equals($packageRoot, $comparison) -or
        $root.StartsWith($packageRoot.TrimEnd($separator) + $separator, $comparison) -or
        $packageRoot.StartsWith($root.TrimEnd($separator) + $separator, $comparison)) {
        throw 'Native image runtime and packaging artifact roots must be disjoint.'
    }
    $packaging = Get-NativeImageRuntimePackaging $packageRoot
    $package = $packaging['report']
    $command = Resolve-NativeImageVerificationCommand $DockerCommandPath
    $record = [ordered]@{
        owner = [Guid]::NewGuid().ToString('N'); service = $package['service']; name = $null
        containerId = $null; packaging = $package
    }
    $record['name'] = 'conduit-native-image-entrypoint-' + $record['service'] + '-' + $record['owner']
    $claim = $null
    $createAttempted = $false
    $failure = $null
    $cleanupFailure = $null
    $report = $null
    $stage = 'runtime-claim'
    try {
        [IO.Directory]::CreateDirectory($root) | Out-Null
        Assert-NativeImageArchiveOutputEmpty $root
        $claimPath = [IO.Path]::Combine($root, 'native-image-runtime.claim')
        Assert-NativeEvidencePathAncestry $claimPath
        $claim = [IO.FileStream]::new($claimPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $claim.Write([Text.UTF8Encoding]::new($false).GetBytes($record['owner'] + "`n"))
        $claim.Flush($true)
        foreach ($entry in ([IO.DirectoryInfo]::new($root)).EnumerateFileSystemInfos()) {
            if ($entry.Name -cne 'native-image-runtime.claim' -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Native image runtime artifact directory changed before execution.'
            }
        }
        $stage = 'runtime-create'
        $createAttempted = $true
        $created = Invoke-NativeImageVerificationDocker -Command $command -Arguments @(
            'container', 'create', '--name', $record['name'], '--network', 'none', '--platform', 'linux/amd64',
            '--label', ('conduit.proof.owner=' + $record['owner']), '--label', ('conduit.proof.service=' + $record['service']),
            '--label', 'conduit.epic=1368', '--label', 'conduit.fixture=native-image-entrypoint',
            '--env', 'CONDUIT_OPENAPI_GENERATION=true', '--env', 'CONDUIT_OPENAPI_OUTPUT=/tmp/packaged-openapi.json', $package['localImageId']
        ) -TimeoutSeconds $DockerCommandTimeoutSeconds
        if ($created['exitCode'] -ne 0 -or $created['stdout'].Trim() -cnotmatch '\A[0-9a-f]{64}\z') {
            throw 'Native image runtime container creation failed.'
        }
        $container = Get-NativeImageRuntimeContainer -Record $record -Command $command -TimeoutSeconds $DockerCommandTimeoutSeconds -ExpectedId ($created['stdout'].Trim())
        $user = $container['Config']['User']
        $stage = 'runtime-start'
        $started = Invoke-NativeImageVerificationDocker -Command $command -Arguments @('container', 'start', $record['containerId']) -TimeoutSeconds $DockerCommandTimeoutSeconds
        if ($started['exitCode'] -ne 0 -or $started['stdout'].Trim() -cne $record['containerId']) {
            throw 'Native image runtime container start failed.'
        }
        $stage = 'runtime-execution'
        $execution = [Diagnostics.Stopwatch]::StartNew()
        try {
            while ($true) {
                $budget = Get-NativeImageRuntimeBudget $execution $ExecutionTimeoutSeconds $DockerCommandTimeoutSeconds
                $container = Get-NativeImageRuntimeContainer -Record $record -Command $command -AllowRunning -TimeoutSeconds $budget['work'] -ReapTimeoutSeconds $budget['reap']
                if (-not $container['State']['Running']) { break }
                if ($execution.Elapsed.TotalSeconds -ge $ExecutionTimeoutSeconds) { throw 'Native image runtime execution timed out.' }
                Start-Sleep -Milliseconds 100
            }
            $budget = Get-NativeImageRuntimeBudget $execution $ExecutionTimeoutSeconds $DockerCommandTimeoutSeconds
            $waited = Invoke-NativeImageVerificationDocker -Command $command -Arguments @('container', 'wait', $record['containerId']) -TimeoutSeconds $budget['work'] -ReapTimeoutSeconds $budget['reap']
            if ($waited['exitCode'] -ne 0 -or $waited['stdout'].Trim() -cne '0') { throw 'Native image runtime wait did not report success.' }
            $budget = Get-NativeImageRuntimeBudget $execution $ExecutionTimeoutSeconds $DockerCommandTimeoutSeconds
            $container = Get-NativeImageRuntimeContainer -Record $record -Command $command -TimeoutSeconds $budget['work'] -ReapTimeoutSeconds $budget['reap']
            Assert-NativeEvidenceInteger $container['State']['ExitCode']
            if ($container['State']['ExitCode'] -ne 0 -or $container['State']['Status'] -isnot [string] -or
                $container['State']['Status'] -cne 'exited' -or $execution.Elapsed.TotalSeconds -gt $ExecutionTimeoutSeconds) {
                throw 'Native image runtime actual exit state did not report success.'
            }
        }
        finally { $execution.Stop() }
        $stage = 'runtime-openapi-copy'
        $openApiPath = [IO.Path]::Combine($root, 'openapi.json')
        Assert-NativeEvidencePathAncestry $openApiPath
        if ([IO.File]::Exists($openApiPath) -or [IO.Directory]::Exists($openApiPath)) { throw 'Native image OpenAPI destination already exists.' }
        $copied = Invoke-NativeImageVerificationDocker -Command $command -Arguments @('container', 'cp', ($record['containerId'] + ':/tmp/packaged-openapi.json'), $openApiPath) -TimeoutSeconds $DockerCommandTimeoutSeconds
        if ($copied['exitCode'] -ne 0) { throw 'Native image OpenAPI copy failed.' }
        $stage = 'runtime-openapi-validation'
        $openApi = Get-NativeImageOpenApiIdentity $openApiPath
        $report = [ordered]@{
            image = $package['requestedImage']; id = $package['localImageId']; user = $user
            paths = [int]$openApi['paths']; succeeded = $true; schemaVersion = 1
            kind = 'conduit-native-image-execution'; service = $package['service']; owner = $record['owner']
            containerId = $record['containerId']; buildId = $package['buildId']; rid = $package['rid']; source = $package['source']
            packagingEvidence = [ordered]@{ path = $packaging['path']; sha256 = $packaging['sha256'] }
            openApiSha256 = $openApi['sha256']; openApiBytes = [long]$openApi['bytes']; verifiedAtUtc = [DateTime]::UtcNow.ToString('O')
            cleanupConfirmed = $false; registryDigestQualification = $false
        }
    }
    catch { $failure = [ordered]@{ stage = $stage; type = $_.Exception.GetType().FullName; line = $_.InvocationInfo.ScriptLineNumber } }
    finally {
        if ($createAttempted) {
            if ($null -ne $record['containerId']) {
                try {
                    $logs = Invoke-NativeImageVerificationDocker -Command $command -Arguments @('container', 'logs', $record['containerId']) -TimeoutSeconds $DockerCommandTimeoutSeconds
                    if ($logs['exitCode'] -ne 0) { throw 'Native image runtime logs failed.' }
                    $logPath = [IO.Path]::Combine($root, 'container.log')
                    Assert-NativeEvidencePathAncestry $logPath
                    $log = [IO.FileStream]::new($logPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    try { $log.Write([Text.UTF8Encoding]::new($false).GetBytes($logs['stdout'] + $logs['stderr'])); $log.Flush($true) }
                    finally { $log.Dispose() }
                }
                catch {
                    if ($null -eq $failure) { $failure = [ordered]@{ stage = 'runtime-logs'; type = $_.Exception.GetType().FullName; line = $_.InvocationInfo.ScriptLineNumber } }
                }
            }
            try { Remove-NativeImageRuntimeContainer -Record $record -Command $command -CleanupTimeoutSeconds $CleanupTimeoutSeconds -DockerCommandTimeoutSeconds $DockerCommandTimeoutSeconds }
            catch { $cleanupFailure = $_.Exception.GetType().FullName }
        }
    }
    try {
        if ($null -ne $failure -and $null -ne $cleanupFailure) {
            throw ('Native image execution and owned cleanup failed; stage=' + $failure['stage'] + '; type=' + $failure['type'] +
                '; line=' + $failure['line'] + '; cleanupType=' + $cleanupFailure + '.')
        }
        if ($null -ne $cleanupFailure) { throw ('Native image execution owned cleanup failed; type=' + $cleanupFailure + '.') }
        if ($null -ne $failure) { throw ('Native image execution failed; stage=' + $failure['stage'] + '; type=' + $failure['type'] + '; line=' + $failure['line'] + '.') }
        if ($null -eq $report) { throw 'Native image execution did not produce validated evidence.' }
        $retainedPackaging = Get-NativeImageRuntimePackaging $packaging['directory']
        if ($retainedPackaging['sha256'] -cne $packaging['sha256']) {
            throw 'Completed native image packaging report changed during execution.'
        }
        $retainedOpenApi = Get-NativeEvidenceFileHash (Get-Item -LiteralPath ([IO.Path]::Combine($root, 'openapi.json')))
        if ($retainedOpenApi['sha256'] -cne $report['openApiSha256'] -or $retainedOpenApi['bytes'] -ne $report['openApiBytes']) {
            throw 'Native image OpenAPI document changed before its execution evidence was written.'
        }
        $report['cleanupConfirmed'] = $true
        $report['verifiedAtUtc'] = [DateTime]::UtcNow.ToString('O')
        $reportPath = [IO.Path]::Combine($root, 'evidence.json')
        Assert-NativeEvidencePathAncestry $reportPath
        $output = [IO.FileStream]::new($reportPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $output.Write([Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $report -Depth 30))); $output.Flush($true) }
        finally { $output.Dispose() }
        return $report
    }
    finally { if ($null -ne $claim) { $claim.Dispose() } }
}
