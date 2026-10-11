# Verify a locally inspected Linux native image without starting its container.
# Loading this file defines helpers only; it does not invoke Docker or write files.
. (Join-Path $PSScriptRoot 'native-image-runtime-archive.ps1')

function Read-NativeImageVerificationJson([string] $Text) {
    $document = $null
    try {
        if ([string]::IsNullOrWhiteSpace($Text) -or $Text.Length -gt 4194304) {
            throw 'Native image inspection JSON is invalid.'
        }
        $options = [Text.Json.JsonDocumentOptions]::new()
        $options.MaxDepth = 32
        $document = [Text.Json.JsonDocument]::Parse($Text, $options)
        return Get-NativeEvidenceObject (Read-NativeEvidenceJsonValue $document.RootElement)
    }
    catch { throw 'Native image inspection JSON is invalid.' }
    finally { if ($null -ne $document) { $document.Dispose() } }
}

function Resolve-NativeImageVerificationCommand([string] $CommandPath) {
    if ([string]::IsNullOrWhiteSpace($CommandPath) -or $CommandPath -match '[\x00-\x1f\x7f]') {
        throw 'Native image verification command is invalid.'
    }
    if ([IO.Path]::GetExtension($CommandPath) -ieq '.ps1') {
        $scriptPath = Get-NativeEvidenceFullPath $CommandPath
        Assert-NativeEvidenceRegularFile $scriptPath
        try {
            $application = Get-Command -Name 'pwsh' -CommandType Application -ErrorAction Stop |
                Select-Object -First 1
        }
        catch { throw 'Native image verification script runtime is unavailable.' }
        if ($null -eq $application -or [string]::IsNullOrWhiteSpace($application.Path)) {
            throw 'Native image verification script runtime is unavailable.'
        }
        return [ordered]@{ executable = $application.Path; prefix = @('-NoProfile', '-File', $scriptPath) }
    }
    try {
        $application = Get-Command -Name $CommandPath -CommandType Application -ErrorAction Stop |
            Select-Object -First 1
    }
    catch { throw 'Native image verification command is unavailable.' }
    if ($null -eq $application -or [string]::IsNullOrWhiteSpace($application.Path)) {
        throw 'Native image verification command is unavailable.'
    }
    return [ordered]@{ executable = $application.Path; prefix = @() }
}

function Invoke-NativeImageVerificationDocker {
    param(
        [Collections.IDictionary] $Command,
        [string[]] $Arguments,
        [double] $TimeoutSeconds,
        [double] $ReapTimeoutSeconds = 5
    )
    if ($TimeoutSeconds -le 0 -or $TimeoutSeconds -gt 120 -or
        $ReapTimeoutSeconds -le 0 -or $ReapTimeoutSeconds -gt 5) {
        throw 'Native image Docker deadline is invalid.'
    }
    $process = [Diagnostics.Process]::new()
    $started = $false
    $failed = $false
    $cleanupFailed = $false
    $result = $null
    $streams = @()
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TimeoutSeconds))
    try {
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $Command['executable']
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
        $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false, $true)
        foreach ($argument in $Command['prefix']) { $start.ArgumentList.Add($argument) }
        foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
        $process.StartInfo = $start
        if (-not $process.Start()) { throw 'Native image Docker command did not start.' }
        $started = $true
        foreach ($reader in @($process.StandardOutput, $process.StandardError)) {
            $streams += [pscustomobject]@{
                Reader = $reader; Buffer = [char[]]::new(4096); Text = [Text.StringBuilder]::new()
                Pending = $null; Ended = $false
            }
        }
        while ($true) {
            $deadline.Token.ThrowIfCancellationRequested()
            foreach ($stream in $streams) {
                if ($stream.Ended) { continue }
                for ($chunk = 0; $chunk -lt 64; $chunk++) {
                    $deadline.Token.ThrowIfCancellationRequested()
                    if ($null -eq $stream.Pending) {
                        $stream.Pending = $stream.Reader.ReadAsync([Memory[char]]::new($stream.Buffer),
                            $deadline.Token).AsTask()
                    }
                    if (-not $stream.Pending.IsCompleted) { break }
                    $length = $stream.Pending.GetAwaiter().GetResult()
                    $stream.Pending = $null
                    if ($length -eq 0) { $stream.Ended = $true; break }
                    if ($stream.Text.Length + $length -gt 4194304) {
                        throw 'Native image Docker output exceeded its bound.'
                    }
                    $null = $stream.Text.Append($stream.Buffer, 0, $length)
                }
            }
            $process.Refresh()
            if ($process.HasExited -and $streams[0].Ended -and $streams[1].Ended) { break }
            Start-Sleep -Milliseconds 10
        }
        $deadline.Token.ThrowIfCancellationRequested()
        $result = [ordered]@{
            exitCode = [int]$process.ExitCode
            stdout = $streams[0].Text.ToString()
            stderr = $streams[1].Text.ToString()
        }
    }
    catch { $failed = $true }
    finally {
        # The work token may already be cancelled. Use a fresh deadline for
        # killing/reaping the original child and joining captured reads.
        $cleanup = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($ReapTimeoutSeconds))
        $reaped = -not $started
        try {
            if ($started) {
                if (-not $process.HasExited) { $process.Kill($true) }
                $null = $process.WaitForExitAsync($cleanup.Token).GetAwaiter().GetResult()
                $reaped = $true
            }
        }
        catch { $cleanupFailed = $true }
        if ($started) {
            try { $process.StandardOutput.Dispose(); $process.StandardError.Dispose() }
            catch { $cleanupFailed = $true }
        }
        foreach ($stream in $streams) {
            if ($null -eq $stream.Pending) { continue }
            try { $null = $stream.Pending.WaitAsync($cleanup.Token).GetAwaiter().GetResult() }
            catch { if (-not $stream.Pending.IsCompleted) { $cleanupFailed = $true } }
        }
        if ($reaped) { $process.Dispose() }
        $cleanup.Dispose()
        $deadline.Dispose()
    }
    if ($failed -and $cleanupFailed) { throw 'Native image Docker command and child cleanup failed.' }
    if ($cleanupFailed) { throw 'Native image Docker child cleanup failed.' }
    if ($failed) { throw 'Native image Docker command failed.' }
    return $result
}

function Get-NativeImageVerificationContainer {
    param(
        [Collections.IDictionary] $Record,
        [Collections.IDictionary] $Command,
        [double] $TimeoutSeconds,
        [double] $ReapTimeoutSeconds = 5,
        [switch] $AllowMissing,
        [string] $ExpectedId
    )
    $identity = if ($null -ne $Record['containerId']) { $Record['containerId'] } else { $Record['name'] }
    $result = Invoke-NativeImageVerificationDocker -Command $Command -Arguments @(
        'container', 'inspect', '--format', '{{json .}}', $identity
    ) -TimeoutSeconds $TimeoutSeconds -ReapTimeoutSeconds $ReapTimeoutSeconds
    if ($result['exitCode'] -ne 0) {
        if ($AllowMissing -and [string]::IsNullOrWhiteSpace($result['stdout']) -and
            $result['stderr'].Trim() -cin @(
                "Error: No such object: $identity", "Error: No such container: $identity",
                "Error response from daemon: No such container: $identity")) {
            return $null
        }
        throw 'Native image owned container inspection failed.'
    }
    $container = Read-NativeImageVerificationJson $result['stdout']
    $config = Get-NativeEvidenceObject $container['Config']
    $labels = Get-NativeEvidenceObject $config['Labels']
    $hostConfig = Get-NativeEvidenceObject $container['HostConfig']
    $state = Get-NativeEvidenceObject $container['State']
    if ($container['Id'] -isnot [string] -or $container['Id'] -cnotmatch '\A[0-9a-f]{64}\z' -or
        $container['Name'] -isnot [string] -or $container['Name'] -cne ('/' + $Record['name']) -or
        ($null -ne $Record['containerId'] -and $container['Id'] -cne $Record['containerId']) -or
        (-not [string]::IsNullOrEmpty($ExpectedId) -and $container['Id'] -cne $ExpectedId) -or
        $container['Image'] -isnot [string] -or $container['Image'] -cne $Record['localImageId'] -or
        $labels['conduit.proof.owner'] -isnot [string] -or $labels['conduit.proof.owner'] -cne $Record['owner'] -or
        $labels['conduit.proof.service'] -isnot [string] -or $labels['conduit.proof.service'] -cne $Record['service'] -or
        $labels['conduit.epic'] -isnot [string] -or $labels['conduit.epic'] -cne '1368' -or
        $labels['conduit.fixture'] -isnot [string] -or $labels['conduit.fixture'] -cne 'native-image-verification' -or
        $hostConfig['NetworkMode'] -isnot [string] -or $hostConfig['NetworkMode'] -cne 'none' -or
        $state['Running'] -isnot [bool] -or $state['Running']) {
        throw 'Native image stopped container ownership was rejected.'
    }
    $Record['containerId'] = $container['Id']
    return $container
}

function Remove-NativeImageVerificationContainer {
    param(
        [Collections.IDictionary] $Record,
        [Collections.IDictionary] $Command,
        [int] $CleanupTimeoutSeconds,
        [int] $DockerCommandTimeoutSeconds
    )
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        # Reserve fresh process-reap time within the overall cleanup budget.
        # Short configured budgets remain bounded rather than implicitly adding
        # another five seconds after the container cleanup deadline expires.
        $remaining = $CleanupTimeoutSeconds - $watch.Elapsed.TotalSeconds
        if ($remaining -le 0.05) { throw 'Native image owned container cleanup deadline expired.' }
        $reap = [Math]::Min(5.0, $remaining / 2)
        $work = [Math]::Min([double]$DockerCommandTimeoutSeconds, $remaining - $reap)
        $container = Get-NativeImageVerificationContainer -Record $Record -Command $Command -AllowMissing -TimeoutSeconds $work -ReapTimeoutSeconds $reap
        if ($null -eq $container) { return }

        $remaining = $CleanupTimeoutSeconds - $watch.Elapsed.TotalSeconds
        if ($remaining -le 0.05) { throw 'Native image owned container cleanup deadline expired.' }
        $reap = [Math]::Min(5.0, $remaining / 2)
        $work = [Math]::Min([double]$DockerCommandTimeoutSeconds, $remaining - $reap)
        # Non-force removal refuses a container that starts after inspection.
        $removed = Invoke-NativeImageVerificationDocker -Command $Command -Arguments @(
            'container', 'rm', $Record['containerId']
        ) -TimeoutSeconds $work -ReapTimeoutSeconds $reap
        if ($removed['exitCode'] -ne 0 -or $removed['stdout'].Trim() -cne $Record['containerId']) {
            throw 'Native image owned stopped container removal failed.'
        }

        $remaining = $CleanupTimeoutSeconds - $watch.Elapsed.TotalSeconds
        if ($remaining -le 0.05) { throw 'Native image owned container cleanup deadline expired.' }
        $reap = [Math]::Min(5.0, $remaining / 2)
        $work = [Math]::Min([double]$DockerCommandTimeoutSeconds, $remaining - $reap)
        $remainingContainer = Get-NativeImageVerificationContainer -Record $Record -Command $Command -AllowMissing -TimeoutSeconds $work -ReapTimeoutSeconds $reap
        if ($null -ne $remainingContainer -or $watch.Elapsed.TotalSeconds -gt $CleanupTimeoutSeconds) {
            throw 'Native image owned container absence was not confirmed.'
        }
    }
    finally { $watch.Stop() }
}

function Get-NativeImagePackagingEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Image,
        [Parameter(Mandatory = $true)][string] $Service,
        [Parameter(Mandatory = $true)][string] $ArtifactDirectory,
        [string] $DockerCommandPath = 'docker',
        [ValidateRange(1, 120)][int] $DockerCommandTimeoutSeconds = 30,
        [ValidateRange(1, 30)][int] $CleanupTimeoutSeconds = 15
    )
    $ErrorActionPreference = 'Stop'
    if ($Service -cnotin @('admin', 'gateway') -or [string]::IsNullOrWhiteSpace($Image) -or
        $Image.Length -gt 4096 -or $Image -match '[\x00-\x1f\x7f]' -or $Image.StartsWith('-', [StringComparison]::Ordinal)) {
        throw 'Native image packaging request is invalid.'
    }
    $root = Get-NativeEvidenceFullPath $ArtifactDirectory
    Assert-NativeImageArchiveOutputEmpty $root
    $command = Resolve-NativeImageVerificationCommand $DockerCommandPath
    $record = [ordered]@{
        owner = [Guid]::NewGuid().ToString('N')
        service = $Service
        name = $null
        localImageId = $null
        containerId = $null
    }
    $record['name'] = 'conduit-native-image-verification-' + $Service + '-' + $record['owner']
    $claim = $null
    $createAttempted = $false
    $failure = $null
    $cleanupFailure = $null
    $report = $null
    $stage = 'artifact-claim'
    try {
        [IO.Directory]::CreateDirectory($root) | Out-Null
        Assert-NativeImageArchiveOutputEmpty $root
        $claimPath = [IO.Path]::Combine($root, 'native-image-verification.claim')
        Assert-NativeEvidencePathAncestry $claimPath
        $claim = [IO.FileStream]::new($claimPath, [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write, [IO.FileShare]::None)
        $claimBytes = [Text.UTF8Encoding]::new($false).GetBytes($record['owner'] + "`n")
        $claim.Write($claimBytes)
        $claim.Flush($true)
        foreach ($entry in ([IO.DirectoryInfo]::new($root)).EnumerateFileSystemInfos()) {
            if ($entry.Name -cne 'native-image-verification.claim' -or
                ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Native image artifact directory changed before inspection.'
            }
        }

        $stage = 'image-inspection'
        $inspection = Invoke-NativeImageVerificationDocker -Command $command -Arguments @(
            'image', 'inspect', '--format', '{{json .}}', $Image
        ) -TimeoutSeconds $DockerCommandTimeoutSeconds
        if ($inspection['exitCode'] -ne 0) { throw 'Native image local inspection failed.' }
        $identity = Read-NativeImageVerificationJson $inspection['stdout']
        $config = Get-NativeEvidenceObject $identity['Config']
        $labels = Get-NativeEvidenceObject $config['Labels']
        $health = Get-NativeEvidenceObject $config['Healthcheck']
        $names = Get-NativeImageExecutableNames $Service 'linux-x64'
        if ($identity['Id'] -isnot [string] -or $identity['Id'] -cnotmatch '\Asha256:[0-9a-f]{64}\z' -or
            $identity['Os'] -isnot [string] -or $identity['Os'] -cne 'linux' -or
            $identity['Architecture'] -isnot [string] -or $identity['Architecture'] -cne 'amd64' -or
            $config['User'] -isnot [string] -or $config['User'] -cnotin @('1654', '1654:1654') -or
            $config['Entrypoint'] -isnot [array] -or $config['Entrypoint'].Count -ne 1 -or
            $config['Entrypoint'][0] -isnot [string] -or $config['Entrypoint'][0] -cne ('/app/' + $names['application']) -or
            $health['Test'] -isnot [array] -or $health['Test'].Count -ne 3 -or
            $health['Test'][0] -isnot [string] -or $health['Test'][0] -cne 'CMD' -or
            $health['Test'][1] -isnot [string] -or $health['Test'][1] -cne '/app/conduit-health-probe' -or
            $health['Test'][2] -isnot [string] -or $health['Test'][2] -cne 'http://localhost:8080/health/live' -or
            $labels['io.conduit.runtime'] -isnot [string] -or $labels['io.conduit.runtime'] -cne 'native-aot' -or
            $labels['io.conduit.promotion'] -isnot [string] -or $labels['io.conduit.promotion'] -cne 'canary-only' -or
            $labels['org.opencontainers.image.source'] -isnot [string] -or
            $labels['org.opencontainers.image.source'] -cne 'https://github.com/nickna/Conduit') {
            throw 'Native image local identity or runtime configuration was rejected.'
        }
        if ($config['Env'] -isnot [array]) { throw 'Native image globalization environment was rejected.' }
        $globalization = @($config['Env'] | Where-Object {
            $_ -is [string] -and $_.StartsWith('DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=', [StringComparison]::Ordinal)
        })
        if ($globalization.Count -ne 1 -or $globalization[0] -cne 'DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false') {
            throw 'Native image globalization environment was rejected.'
        }
        if ($null -ne $config['Volumes']) {
            $volumes = Get-NativeEvidenceObject $config['Volumes']
            if ($volumes.Count -ne 0) { throw 'Native image declared volumes that can hide exported files.' }
        }
        foreach ($labelName in @('org.opencontainers.image.version', 'org.opencontainers.image.revision',
                'org.opencontainers.image.created')) {
            Assert-NativeEvidenceText $labels[$labelName]
        }
        # The local ID may be an OCI index digest in Docker's containerd store.
        # It pins this local export; it is not asserted to be a config digest.
        $record['localImageId'] = $identity['Id']

        $stage = 'container-creation'
        $createAttempted = $true
        $created = Invoke-NativeImageVerificationDocker -Command $command -Arguments @(
            'container', 'create', '--name', $record['name'], '--network', 'none',
            '--label', ('conduit.proof.owner=' + $record['owner']),
            '--label', ('conduit.proof.service=' + $Service), '--label', 'conduit.epic=1368',
            '--label', 'conduit.fixture=native-image-verification', $record['localImageId']
        ) -TimeoutSeconds $DockerCommandTimeoutSeconds
        if ($created['exitCode'] -ne 0 -or $created['stdout'].Trim() -cnotmatch '\A[0-9a-f]{64}\z') {
            throw 'Native image stopped container creation failed.'
        }
        # Store the ID only after inspecting the exact owned name. Failed or
        # malformed create responses remain recoverable by that name in finally.
        $null = Get-NativeImageVerificationContainer -Record $record -Command $command -TimeoutSeconds $DockerCommandTimeoutSeconds -ExpectedId ($created['stdout'].Trim())

        $stage = 'container-export'
        $archivePath = [IO.Path]::Combine($root, 'rootfs.tar')
        Assert-NativeEvidencePathAncestry $archivePath
        if ([IO.File]::Exists($archivePath) -or [IO.Directory]::Exists($archivePath)) {
            throw 'Native image export destination already exists.'
        }
        $exported = Invoke-NativeImageVerificationDocker -Command $command -Arguments @(
            'container', 'export', '--output', $archivePath, $record['containerId']
        ) -TimeoutSeconds $DockerCommandTimeoutSeconds
        if ($exported['exitCode'] -ne 0) { throw 'Native image owned container export failed.' }
        $null = Get-NativeImageVerificationContainer -Record $record -Command $command -TimeoutSeconds $DockerCommandTimeoutSeconds

        $stage = 'runtime-archive'
        $archiveInfo = Expand-NativeImageRuntimeArchive -ArchivePath $archivePath -OutputDirectory ([IO.Path]::Combine($root, 'extracted')) -Service $Service
        $stage = 'embedded-evidence'
        $summary = Assert-NativeImagePublishEvidenceMatchesPayload -EvidenceDirectory $archiveInfo['evidenceDirectory'] -PayloadDirectory $archiveInfo['payloadDirectory']
        if ($summary['service'] -cne $Service -or $summary['rid'] -cne 'linux-x64') {
            throw 'Native image embedded evidence service or RID was rejected.'
        }
        $metadata = Get-NativeEvidenceObject $summary['commitMetadata']
        foreach ($pair in @(@('version', 'version'), @('commitSha', 'revision'), @('buildTimestamp', 'created'))) {
            if ($metadata[$pair[0]] -cne $labels['org.opencontainers.image.' + $pair[1]]) {
                throw 'Native image embedded metadata disagreed with OCI labels.'
            }
        }
        $application = Read-NativeImageEvidenceDocument ([IO.Path]::Combine($archiveInfo['evidenceDirectory'], 'application.json'))
        $manifestHashes = [ordered]@{}
        foreach ($manifestName in @('application.json', 'health-probe.json', 'image-publish.json')) {
            $manifestFile = Get-Item -LiteralPath ([IO.Path]::Combine($archiveInfo['evidenceDirectory'], $manifestName))
            $manifestHashes[$manifestName] = (Get-NativeEvidenceFileHash $manifestFile)['sha256']
        }
        $report = [ordered]@{
            schemaVersion = 1
            kind = 'conduit-native-image-packaging'
            service = $Service
            requestedImage = $Image
            localImageId = $record['localImageId']
            containerId = $record['containerId']
            owner = $record['owner']
            buildId = $summary['buildId']
            rid = $summary['rid']
            commitMetadata = $metadata
            source = $application['source']
            manifestHashes = $manifestHashes
            runtimeFilesSha256 = $application['runtimeFilesSha256']
            metrics = $summary['metrics']
            ociLabels = [ordered]@{
                version = $labels['org.opencontainers.image.version']
                revision = $labels['org.opencontainers.image.revision']
                created = $labels['org.opencontainers.image.created']
                source = $labels['org.opencontainers.image.source']
                runtime = $labels['io.conduit.runtime']
                promotion = $labels['io.conduit.promotion']
            }
            archiveBytes = [long]$archiveInfo['archiveBytes']
            archiveEntries = [int]$archiveInfo['entries']
            archivedFilePermissions = $archiveInfo['files']
            extractionId = $archiveInfo['extractionId']
            verifiedAtUtc = [DateTime]::UtcNow.ToString('O')
            cleanupConfirmed = $false
            payloadVerified = $true
            registryDigestQualification = $false
        }
    }
    catch {
        $failure = [ordered]@{
            stage = $stage; type = $_.Exception.GetType().FullName; line = $_.InvocationInfo.ScriptLineNumber
        }
    }
    finally {
        if ($createAttempted) {
            try {
                Remove-NativeImageVerificationContainer -Record $record -Command $command -CleanupTimeoutSeconds $CleanupTimeoutSeconds -DockerCommandTimeoutSeconds $DockerCommandTimeoutSeconds
            }
            catch { $cleanupFailure = $_.Exception.GetType().FullName }
        }
        if ($null -ne $failure -or $null -ne $cleanupFailure) {
            if ($null -ne $claim) { $claim.Dispose(); $claim = $null }
        }
    }
    try {
        if ($null -ne $failure -and $null -ne $cleanupFailure) {
            throw ('Native image packaging and owned cleanup failed; stage=' + $failure['stage'] +
                '; type=' + $failure['type'] + '; line=' + $failure['line'] + '; cleanupType=' + $cleanupFailure + '.')
        }
        if ($null -ne $cleanupFailure) {
            throw ('Native image packaging owned cleanup failed; type=' + $cleanupFailure + '.')
        }
        if ($null -ne $failure) {
            throw ('Native image packaging failed; stage=' + $failure['stage'] + '; type=' + $failure['type'] +
                '; line=' + $failure['line'] + '.')
        }
        if ($null -eq $report) { throw 'Native image packaging did not produce validated evidence.' }
        $report['cleanupConfirmed'] = $true
        $report['verifiedAtUtc'] = [DateTime]::UtcNow.ToString('O')
        $reportPath = [IO.Path]::Combine($root, 'packaging-evidence.json')
        Assert-NativeEvidencePathAncestry $reportPath
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $report -Depth 30))
        $output = [IO.FileStream]::new($reportPath, [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $output.Write($bytes); $output.Flush($true) }
        finally { $output.Dispose() }
        return $report
    }
    finally { if ($null -ne $claim) { $claim.Dispose() } }
}
