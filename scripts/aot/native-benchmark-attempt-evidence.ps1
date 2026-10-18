# Versioned benchmark collection attempts and atomic successful report publication.
# Dot-sourcing defines helpers only; it does not create files or acquire locks.
. (Join-Path $PSScriptRoot 'native-image-oci-evidence.ps1')

function Assert-NativeBenchmarkAttemptFilePath([string] $Path, [switch] $Required) {
    Assert-NativeEvidencePathAncestry $Path
    $exists = $true
    try { $unusedAttributes = [IO.File]::GetAttributes($Path) }
    catch [IO.FileNotFoundException] { $exists = $false }
    catch [IO.DirectoryNotFoundException] { $exists = $false }
    if ($exists) { Assert-NativeEvidenceRegularFile $Path }
    elseif ($Required) { throw 'Native benchmark attempt file is missing.' }
}

function Assert-NativeBenchmarkAttemptDirectoryPath([string] $Path, [switch] $Required) {
    Assert-NativeEvidencePathAncestry $Path
    $exists = $true
    $attributes = $null
    try { $attributes = [IO.File]::GetAttributes($Path) }
    catch [IO.FileNotFoundException] { $exists = $false }
    catch [IO.DirectoryNotFoundException] { $exists = $false }
    if ($exists) {
        if (($attributes -band [IO.FileAttributes]::Directory) -eq 0 -or
            ($attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Device)) -ne 0 -or
            ($IsLinux -and (Get-NativeEvidenceUnixFileType $Path) -ne 0x4000)) {
            throw 'Native benchmark attempt directory is invalid.'
        }
    }
    elseif ($Required) { throw 'Native benchmark attempt directory is missing.' }
}

function Get-NativeBenchmarkAttemptUtc($Value) {
    if ($Value -isnot [string] -or
        $Value -cnotmatch '\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)\z') {
        throw 'Native benchmark attempt timestamp is invalid.'
    }
    return (ConvertTo-NativeEvidenceUtc $Value)
}

function Assert-NativeBenchmarkAttemptIdentity($AttemptId, $Service) {
    if ($AttemptId -isnot [string] -or $AttemptId -cnotmatch '\A[0-9a-f]{32}\z' -or
        $AttemptId -ceq ('0' * 32) -or $Service -isnot [string] -or $Service -cnotin @('admin', 'gateway')) {
        throw 'Native benchmark attempt identity is invalid.'
    }
}

function Get-NativeBenchmarkAttemptLatest($Value) {
    $valueObject = Get-NativeEvidenceObject $Value
    Assert-NativeEvidenceKeys $valueObject @('schemaVersion', 'kind', 'attemptId', 'service') -Exact
    Assert-NativeEvidenceInteger $valueObject['schemaVersion'] -Positive
    if ($valueObject['schemaVersion'] -ne 1 -or $valueObject['kind'] -isnot [string] -or
        $valueObject['kind'] -cne 'conduit-native-benchmark-latest-attempt') {
        throw 'Native benchmark latest attempt is invalid.'
    }
    Assert-NativeBenchmarkAttemptIdentity $valueObject['attemptId'] $valueObject['service']
    return ,$valueObject
}

function Get-NativeBenchmarkAttemptRecord($Value) {
    $valueObject = Get-NativeEvidenceObject $Value
    Assert-NativeEvidenceKeys $valueObject @('schemaVersion', 'kind', 'attemptId', 'service', 'status',
        'startedAt', 'completedAt', 'reportSha256', 'failureCode') -Exact
    Assert-NativeEvidenceInteger $valueObject['schemaVersion'] -Positive
    if ($valueObject['schemaVersion'] -ne 1 -or $valueObject['kind'] -isnot [string] -or
        $valueObject['kind'] -cne 'conduit-native-benchmark-attempt' -or $valueObject['status'] -isnot [string] -or
        $valueObject['status'] -cnotin @('running', 'succeeded', 'failed')) {
        throw 'Native benchmark attempt record is invalid.'
    }
    Assert-NativeBenchmarkAttemptIdentity $valueObject['attemptId'] $valueObject['service']
    $started = Get-NativeBenchmarkAttemptUtc $valueObject['startedAt']
    if ($valueObject['status'] -ceq 'running') {
        if ($null -ne $valueObject['completedAt'] -or $null -ne $valueObject['reportSha256'] -or
            $null -ne $valueObject['failureCode']) { throw 'Native benchmark running attempt is invalid.' }
    }
    else {
        $completed = Get-NativeBenchmarkAttemptUtc $valueObject['completedAt']
        if ($completed -lt $started) { throw 'Native benchmark attempt time range is invalid.' }
        if ($valueObject['status'] -ceq 'succeeded') {
            if ($valueObject['reportSha256'] -isnot [string] -or
                $valueObject['reportSha256'] -cnotmatch '\A[0-9a-f]{64}\z' -or
                $null -ne $valueObject['failureCode']) { throw 'Native benchmark successful attempt is invalid.' }
        }
        elseif ($null -ne $valueObject['reportSha256'] -or $valueObject['failureCode'] -isnot [string] -or
            $valueObject['failureCode'] -cnotin @('collection-failed', 'cleanup-failed', 'publication-failed')) {
            throw 'Native benchmark failed attempt is invalid.'
        }
    }
    return ,$valueObject
}

function Read-NativeBenchmarkAttemptDocument([string] $Path, [switch] $Report) {
    Assert-NativeBenchmarkAttemptFilePath $Path -Required
    $maximumBytes = if ($Report) { 4194304 } else { 65536 }
    if ([IO.FileInfo]::new($Path).Length -gt $maximumBytes) { throw 'Native benchmark attempt document exceeds its bound.' }
    $document = Read-NativeImageOciEvidenceDocument $Path
    if ($document['bytes'] -gt $maximumBytes) { throw 'Native benchmark attempt document exceeds its bound.' }
    return ,$document
}

function ConvertTo-NativeBenchmarkAttemptJsonBytes($Value, [switch] $Report) {
    $json = ConvertTo-Json -InputObject $Value -Depth 64 -Compress -WarningAction Stop
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($json)
    $maximumBytes = if ($Report) { 4194304 } else { 65536 }
    if ($bytes.Length -eq 0 -or $bytes.Length -gt $maximumBytes) {
        throw 'Native benchmark attempt document exceeds its bound.'
    }
    # Validate original serialized bytes, including finite numbers and depth.
    $unusedValue = Read-NativeOciJson $bytes
    return ,$bytes
}

function Write-NativeBenchmarkAttemptNewFile([string] $Path, [byte[]] $Bytes) {
    Assert-NativeBenchmarkAttemptFilePath $Path
    Assert-NativeBenchmarkAttemptDirectoryPath ([IO.Path]::GetDirectoryName($Path)) -Required
    $stream = $null
    try {
        $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $stream.Write($Bytes, 0, $Bytes.Length)
        $stream.Flush($true)
    }
    finally { if ($null -ne $stream) { $stream.Dispose() } }
}

function Write-NativeBenchmarkAttemptAtomicFile([string] $Path, [byte[]] $Bytes) {
    $failed = $false
    $cleanupFailed = $false
    $temporaryOwned = $false
    $temporary = $null
    try {
        Assert-NativeBenchmarkAttemptFilePath $Path
        $parent = [IO.Path]::GetDirectoryName($Path)
        Assert-NativeBenchmarkAttemptDirectoryPath $parent -Required
        $temporary = [IO.Path]::Combine($parent, '.native-benchmark-' + [guid]::NewGuid().ToString('N') + '.tmp')
        Assert-NativeBenchmarkAttemptFilePath $temporary
        $stream = $null
        try {
            $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $temporaryOwned = $true
            $stream.Write($Bytes, 0, $Bytes.Length)
            $stream.Flush($true)
        }
        finally { if ($null -ne $stream) { $stream.Dispose() } }
        Assert-NativeBenchmarkAttemptFilePath $temporary -Required
        Assert-NativeBenchmarkAttemptFilePath $Path
        [IO.File]::Move($temporary, $Path, $true)
    }
    catch { $failed = $true }
    finally {
        if ($temporaryOwned) {
            try {
                $exists = $true
                try { $unusedAttributes = [IO.File]::GetAttributes($temporary) }
                catch [IO.FileNotFoundException] { $exists = $false }
                catch [IO.DirectoryNotFoundException] { $exists = $false }
                if ($exists) {
                    Assert-NativeBenchmarkAttemptFilePath $temporary -Required
                    [IO.File]::Delete($temporary)
                }
            }
            catch { $cleanupFailed = $true }
        }
    }
    if ($failed -and $cleanupFailed) { throw 'Native benchmark publication and owned temporary cleanup failed.' }
    if ($cleanupFailed) { throw 'Native benchmark owned temporary cleanup failed.' }
    if ($failed) { throw 'Native benchmark publication failed.' }
}

function Get-NativeBenchmarkAttemptContext($Context) {
    if ($Context -isnot [Collections.IDictionary]) { throw 'Native benchmark attempt context is invalid.' }
    $contextObject = Get-NativeEvidenceObject $Context
    Assert-NativeEvidenceKeys $contextObject @('lock', 'attemptId', 'service', 'startedAt', 'outputPath', 'attemptRoot',
        'attemptDirectory', 'latestPath', 'attemptPath', 'status')
    Assert-NativeBenchmarkAttemptIdentity $contextObject['attemptId'] $contextObject['service']
    $unusedStarted = Get-NativeBenchmarkAttemptUtc $contextObject['startedAt']
    foreach ($key in @('outputPath', 'attemptRoot', 'attemptDirectory', 'latestPath', 'attemptPath')) {
        if ($contextObject[$key] -isnot [string] -or
            $contextObject[$key] -cne (Get-NativeEvidenceFullPath $contextObject[$key])) {
            throw 'Native benchmark attempt context path is invalid.'
        }
    }
    $root = $contextObject['outputPath'] + '.attempts'
    $directory = [IO.Path]::Combine($root, $contextObject['attemptId'])
    $lockPath = [IO.Path]::Combine($root, 'writer.lock')
    if ($contextObject['attemptRoot'] -cne $root -or $contextObject['attemptDirectory'] -cne $directory -or
        $contextObject['latestPath'] -cne [IO.Path]::Combine($root, 'latest.json') -or
        $contextObject['attemptPath'] -cne [IO.Path]::Combine($directory, 'attempt.json') -or
        $contextObject['status'] -isnot [string] -or $contextObject['status'] -cnotin @('running', 'succeeded', 'failed') -or
        $contextObject['lock'] -isnot [IO.FileStream] -or -not $contextObject['lock'].CanWrite -or
        -not $contextObject['lock'].CanRead -or $contextObject['lock'].Name -cne $lockPath) {
        throw 'Native benchmark attempt context ownership is invalid.'
    }
    Assert-NativeBenchmarkAttemptDirectoryPath $root -Required
    Assert-NativeBenchmarkAttemptDirectoryPath $directory -Required
    Assert-NativeBenchmarkAttemptFilePath $lockPath -Required
    return ,$contextObject
}

function Get-NativeBenchmarkAttemptLiveRecord($Context) {
    $contextObject = Get-NativeBenchmarkAttemptContext $Context
    $latestDocument = Read-NativeBenchmarkAttemptDocument $contextObject['latestPath']
    $attemptDocument = Read-NativeBenchmarkAttemptDocument $contextObject['attemptPath']
    $latest = Get-NativeBenchmarkAttemptLatest $latestDocument['value']
    $attempt = Get-NativeBenchmarkAttemptRecord $attemptDocument['value']
    if ($latest['attemptId'] -cne $contextObject['attemptId'] -or $latest['service'] -cne $contextObject['service'] -or
        $attempt['attemptId'] -cne $contextObject['attemptId'] -or $attempt['service'] -cne $contextObject['service'] -or
        $attempt['startedAt'] -cne $contextObject['startedAt'] -or $attempt['status'] -cne $contextObject['status']) {
        throw 'Native benchmark attempt context no longer owns the latest record.'
    }
    return ,$attempt
}

function Start-NativeBenchmarkAttempt {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $OutputPath,
        [Parameter(Mandatory)][ValidateSet('admin', 'gateway')][string] $Service
    )
    $ErrorActionPreference = 'Stop'
    $Service = $Service.ToLowerInvariant()
    $lock = $null
    try {
        $output = Get-NativeEvidenceFullPath $OutputPath
        $root = $output + '.attempts'
        $latestPath = [IO.Path]::Combine($root, 'latest.json')
        $lockPath = [IO.Path]::Combine($root, 'writer.lock')
        Assert-NativeBenchmarkAttemptFilePath $output
        Assert-NativeBenchmarkAttemptDirectoryPath ([IO.Path]::GetDirectoryName($output))
        Assert-NativeBenchmarkAttemptDirectoryPath $root
        Assert-NativeBenchmarkAttemptFilePath $latestPath
        Assert-NativeBenchmarkAttemptFilePath $lockPath
        [IO.Directory]::CreateDirectory($root) | Out-Null
        Assert-NativeBenchmarkAttemptDirectoryPath $root -Required
        $lock = [IO.FileStream]::new($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        # Recheck paths after acquiring the lock, before changing any pointer.
        Assert-NativeBenchmarkAttemptFilePath $output
        Assert-NativeBenchmarkAttemptFilePath $latestPath
        Assert-NativeBenchmarkAttemptFilePath $lockPath -Required
        $attemptId = [guid]::NewGuid().ToString('N')
        $directory = [IO.Path]::Combine($root, $attemptId)
        Assert-NativeBenchmarkAttemptDirectoryPath $directory
        if ([IO.Directory]::Exists($directory) -or [IO.File]::Exists($directory)) {
            throw 'Native benchmark attempt directory already exists.'
        }
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $started = [DateTimeOffset]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
        $attemptPath = [IO.Path]::Combine($directory, 'attempt.json')
        $record = [ordered]@{
            schemaVersion = 1; kind = 'conduit-native-benchmark-attempt'; attemptId = $attemptId; service = $Service
            status = 'running'; startedAt = $started; completedAt = $null; reportSha256 = $null; failureCode = $null
        }
        Write-NativeBenchmarkAttemptNewFile $attemptPath (ConvertTo-NativeBenchmarkAttemptJsonBytes $record)
        $latest = [ordered]@{ schemaVersion = 1; kind = 'conduit-native-benchmark-latest-attempt'; attemptId = $attemptId; service = $Service }
        Write-NativeBenchmarkAttemptAtomicFile $latestPath (ConvertTo-NativeBenchmarkAttemptJsonBytes $latest)
        $context = [ordered]@{
            lock = $lock; attemptId = $attemptId; service = $Service; startedAt = $started; outputPath = $output
            attemptRoot = $root; attemptDirectory = $directory; latestPath = $latestPath; attemptPath = $attemptPath; status = 'running'
        }
        $lock = $null
        return ,$context
    }
    catch { throw 'Native benchmark attempt could not be started.' }
    finally { if ($null -ne $lock) { $lock.Dispose() } }
}

function Set-NativeBenchmarkAttemptReportProperty($Report, [string] $Name, $Value) {
    if ($Report -is [Collections.IDictionary]) {
        foreach ($key in $Report.Keys) {
            if ($key -is [string] -and [string]::Equals($key, $Name, [StringComparison]::OrdinalIgnoreCase) -and $key -cne $Name) {
                throw 'Native benchmark report field casing is invalid.'
            }
        }
        $Report[$Name] = $Value
        return
    }
    if ($Report -isnot [Management.Automation.PSCustomObject]) { throw 'Native benchmark report requires an object.' }
    $property = $Report.PSObject.Properties[$Name]
    if ($null -eq $property) { $Report.PSObject.Properties.Add([Management.Automation.PSNoteProperty]::new($Name, $Value)) }
    elseif ($property.Name -cne $Name) { throw 'Native benchmark report field casing is invalid.' }
    else { $property.Value = $Value }
}

function Read-NativeBenchmarkAttemptReportBytes([string] $Path) {
    Assert-NativeBenchmarkAttemptFilePath $Path -Required
    $stream = $null
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(90))
    try {
        $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($stream.Length -le 0 -or $stream.Length -gt 4194304) {
            throw 'Native benchmark previous report exceeds its bound.'
        }
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $deadline.Token.ThrowIfCancellationRequested()
            $read = $stream.ReadAsync([Memory[byte]]::new($bytes, $offset, $bytes.Length - $offset),
                $deadline.Token).AsTask().GetAwaiter().GetResult()
            if ($read -le 0) { throw 'Native benchmark previous report is truncated.' }
            $offset += $read
        }
        if ($stream.Length -ne $bytes.Length) { throw 'Native benchmark previous report changed while reading.' }
        return ,$bytes
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
        $deadline.Dispose()
    }
}

function Preserve-NativeBenchmarkPreviousReport($Context) {
    $output = $Context['outputPath']
    if (-not [IO.File]::Exists($output)) { return }
    $previous = Read-NativeBenchmarkAttemptDocument $output -Report
    $legacy = [IO.Path]::Combine($Context['attemptRoot'], 'legacy')
    Assert-NativeBenchmarkAttemptDirectoryPath $legacy
    $destination = [IO.Path]::Combine($legacy, $previous['sha256'] + '.json')
    Assert-NativeBenchmarkAttemptFilePath $destination
    [IO.Directory]::CreateDirectory($legacy) | Out-Null
    if ([IO.File]::Exists($destination)) {
        $retained = Read-NativeBenchmarkAttemptDocument $destination -Report
        if ($retained['sha256'] -cne $previous['sha256']) { throw 'Native benchmark historical report changed.' }
        return
    }
    # This copy preserves bytes only; it does not invent successful attempt metadata.
    $bytes = Read-NativeBenchmarkAttemptReportBytes $output
    if ($bytes.Length -ne $previous['bytes'] -or
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne $previous['sha256']) {
        throw 'Native benchmark previous report changed during preservation.'
    }
    Write-NativeBenchmarkAttemptNewFile $destination $bytes
}

function Complete-NativeBenchmarkAttempt {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Context, [Parameter(Mandatory)] $Report)
    $ErrorActionPreference = 'Stop'
    try {
        $contextObject = Get-NativeBenchmarkAttemptContext $Context
        $record = Get-NativeBenchmarkAttemptLiveRecord $Context
        if ($record['status'] -cne 'running') { throw 'Native benchmark attempt is already terminal.' }
        $reportObject = Get-NativeEvidenceObject $Report
        if (-not $reportObject.ContainsKey('service') -or $reportObject['service'] -isnot [string] -or
            $reportObject['service'] -cne $contextObject['service']) { throw 'Native benchmark report service differs.' }
        $completed = [DateTimeOffset]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
        if ((Get-NativeBenchmarkAttemptUtc $completed) -lt (Get-NativeBenchmarkAttemptUtc $contextObject['startedAt'])) {
            throw 'Native benchmark attempt clock moved before its start.'
        }
        $collection = [ordered]@{ attemptId = $contextObject['attemptId']; startedAt = $contextObject['startedAt']; completedAt = $completed }
        Set-NativeBenchmarkAttemptReportProperty $Report 'schemaVersion' 4
        Set-NativeBenchmarkAttemptReportProperty $Report 'collection' $collection
        Set-NativeBenchmarkAttemptReportProperty $Report 'generatedAt' $completed
        $bytes = ConvertTo-NativeBenchmarkAttemptJsonBytes $Report -Report
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        $archive = [IO.Path]::Combine($contextObject['attemptDirectory'], 'benchmark.json')
        Write-NativeBenchmarkAttemptNewFile $archive $bytes
        Preserve-NativeBenchmarkPreviousReport $contextObject
        $unusedRecord = Get-NativeBenchmarkAttemptLiveRecord $Context
        Write-NativeBenchmarkAttemptAtomicFile $contextObject['outputPath'] $bytes
        $record['status'] = 'succeeded'; $record['completedAt'] = $completed; $record['reportSha256'] = $hash
        Write-NativeBenchmarkAttemptAtomicFile $contextObject['attemptPath'] (ConvertTo-NativeBenchmarkAttemptJsonBytes $record)
        $Context['status'] = 'succeeded'
    }
    catch { throw 'Native benchmark attempt publication failed.' }
}

function Set-NativeBenchmarkAttemptFailed {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)][ValidateSet('collection-failed', 'cleanup-failed', 'publication-failed')][string] $FailureCode
    )
    $ErrorActionPreference = 'Stop'
    try {
        $contextObject = Get-NativeBenchmarkAttemptContext $Context
        $record = Get-NativeBenchmarkAttemptLiveRecord $Context
        if ($record['status'] -ceq 'succeeded') { throw 'Native benchmark successful attempt cannot be marked failed.' }
        $completed = [DateTimeOffset]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
        if ((Get-NativeBenchmarkAttemptUtc $completed) -lt (Get-NativeBenchmarkAttemptUtc $contextObject['startedAt'])) {
            throw 'Native benchmark attempt clock moved before its start.'
        }
        $record['status'] = 'failed'; $record['completedAt'] = $completed; $record['reportSha256'] = $null
        $record['failureCode'] = $FailureCode.ToLowerInvariant()
        Write-NativeBenchmarkAttemptAtomicFile $contextObject['attemptPath'] (ConvertTo-NativeBenchmarkAttemptJsonBytes $record)
        $Context['status'] = 'failed'
    }
    catch { throw 'Native benchmark failed attempt could not be recorded.' }
}

function Get-NativeBenchmarkFailureWorkloadAggregate($Value, [string] $Service) {
    $aggregate = Get-NativeEvidenceObject $Value
    Assert-NativeEvidenceKeys $aggregate @('phase', 'id', 'requested', 'executed', 'completed', 'failures',
        'warmupRequests', 'concurrency', 'timeoutSeconds') -Exact
    $ids = if ($Service -ceq 'admin') { @('admin-setting-read') }
        elseif ($Service -ceq 'gateway') { @('gateway-chat-nonstream', 'gateway-chat-sse') }
        else { throw 'Invalid failure workload service.' }
    if ($aggregate['phase'] -isnot [string] -or $aggregate['phase'] -cne 'measured' -or
        $aggregate['id'] -isnot [string] -or $aggregate['id'] -cnotin $ids) {
        throw 'Invalid failure workload identity.'
    }
    foreach ($field in @('requested', 'executed', 'completed', 'failures', 'warmupRequests', 'concurrency', 'timeoutSeconds')) {
        Assert-NativeEvidenceInteger $aggregate[$field]
    }
    if ($aggregate['requested'] -lt 1 -or $aggregate['requested'] -gt 100000 -or
        $aggregate['executed'] -ne $aggregate['requested'] -or
        $aggregate['completed'] -gt $aggregate['executed'] -or
        $aggregate['failures'] -ne ($aggregate['executed'] - $aggregate['completed']) -or
        $aggregate['warmupRequests'] -gt 10000 -or $aggregate['concurrency'] -lt 1 -or
        $aggregate['concurrency'] -gt 256 -or $aggregate['concurrency'] -gt $aggregate['requested'] -or
        $aggregate['timeoutSeconds'] -lt 1 -or $aggregate['timeoutSeconds'] -gt 120) {
        throw 'Invalid failure workload counts.'
    }
    return ,$aggregate
}

function Get-NativeBenchmarkAttemptFailureDiagnostic($Value) {
    # This is a closed public record, not an exception/output serialization
    # boundary. Reject unrecognized text rather than echoing it in validation.
    try {
        $diagnostic = Get-NativeEvidenceObject $Value
        Assert-NativeEvidenceInteger $diagnostic['schemaVersion'] -Positive
        $fields = @('schemaVersion', 'kind', 'attemptId', 'service', 'phase', 'reason', 'runtime',
            'sampleKind', 'pair', 'ownedContainerCleanup', 'privateFileCleanup')
        if ($diagnostic['schemaVersion'] -eq 2) { $fields += 'workloadAggregate' }
        Assert-NativeEvidenceKeys $diagnostic $fields -Exact
        if ($diagnostic['schemaVersion'] -notin @(1, 2) -or $diagnostic['kind'] -isnot [string] -or
            $diagnostic['kind'] -cne 'conduit-native-benchmark-failure-diagnostic') {
            throw 'Invalid failure diagnostic version.'
        }
        Assert-NativeBenchmarkAttemptIdentity $diagnostic['attemptId'] $diagnostic['service']
        $phases = @('configuration', 'image-metadata', 'container-start', 'readiness', 'memory-sample',
            'workload', 'workload-result', 'collection-validation', 'owned-cleanup', 'private-cleanup',
            'publication', 'unknown')
        $reasons = @('unknown', 'readiness-deadline', 'command-deadline', 'poll-command-deadline',
            'poll-callback', 'poll-memory-sample', 'poll-memory-value', 'memory-sample', 'memory-value',
            'probe-exit', 'invalid-result', 'request-failed', 'image-binding', 'network-binding', 'container-start',
            'ownership-verification', 'shutdown-failed', 'cleanup-verification-failed',
            'private-cleanup-failed', 'publication-failed')
        foreach ($field in @('phase', 'reason', 'runtime', 'sampleKind')) {
            if ($diagnostic[$field] -isnot [string]) { throw 'Invalid failure diagnostic code.' }
        }
        if ($diagnostic['phase'] -cnotin $phases -or $diagnostic['reason'] -cnotin $reasons -or
            $diagnostic['runtime'] -cnotin @('unknown', 'jit', 'native-aot') -or
            $diagnostic['sampleKind'] -cnotin @('none', 'warmup', 'recorded')) {
            throw 'Invalid failure diagnostic code.'
        }
        if ($diagnostic['sampleKind'] -ceq 'recorded') {
            Assert-NativeEvidenceInteger $diagnostic['pair'] -Positive
            if ($diagnostic['pair'] -gt 21) { throw 'Invalid failure diagnostic pair.' }
        }
        elseif ($null -ne $diagnostic['pair']) { throw 'Invalid failure diagnostic pair.' }

        if ($diagnostic['schemaVersion'] -eq 2 -and $null -ne $diagnostic['workloadAggregate']) {
            if ($diagnostic['phase'] -cne 'workload' -or $diagnostic['reason'] -cne 'probe-exit' -or
                $diagnostic['runtime'] -ceq 'unknown' -or $diagnostic['sampleKind'] -ceq 'none') {
                throw 'Invalid failure workload context.'
            }
            $diagnostic['workloadAggregate'] = Get-NativeBenchmarkFailureWorkloadAggregate $diagnostic['workloadAggregate'] $diagnostic['service']
        }

        foreach ($field in @('ownedContainerCleanup', 'privateFileCleanup')) {
            $cleanup = Get-NativeEvidenceObject $diagnostic[$field]
            Assert-NativeEvidenceKeys $cleanup @('status', 'reason') -Exact
            $reasons = if ($field -ceq 'ownedContainerCleanup') {
                @('none', 'shutdown-failed', 'verification-failed')
            } else { @('none', 'cleanup-failed') }
            if ($cleanup['status'] -isnot [string] -or
                $cleanup['status'] -cnotin @('not-attempted', 'confirmed', 'verification-failed') -or
                $cleanup['reason'] -isnot [string] -or $cleanup['reason'] -cnotin $reasons -or
                ($cleanup['status'] -ceq 'not-attempted' -and $cleanup['reason'] -cne 'none') -or
                ($cleanup['status'] -ceq 'verification-failed' -and $cleanup['reason'] -ceq 'none')) {
                throw 'Invalid failure diagnostic cleanup outcome.'
            }
            # A confirmed absence may follow an earlier shutdown/file-delete
            # failure. Keep that reason without claiming the resource leaked.
            $diagnostic[$field] = $cleanup
        }
        $bytes = ConvertTo-NativeBenchmarkAttemptJsonBytes $diagnostic
        if ($bytes.Length -gt 4096) { throw 'Failure diagnostic exceeds its bound.' }
        return ,$diagnostic
    }
    catch { throw 'Native benchmark failure diagnostic is invalid.' }
}

function Set-NativeBenchmarkAttemptFailureDiagnostic {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Context, [Parameter(Mandatory)] $Diagnostic)
    $ErrorActionPreference = 'Stop'
    try {
        $contextObject = Get-NativeBenchmarkAttemptContext $Context
        $record = Get-NativeBenchmarkAttemptLiveRecord $Context
        if ($record['status'] -cne 'failed') { throw 'Failure diagnostics require a failed attempt.' }
        $provided = Get-NativeEvidenceObject $Diagnostic
        $fields = @('phase', 'reason', 'runtime', 'sampleKind', 'pair', 'ownedContainerCleanup', 'privateFileCleanup', 'workloadAggregate')
        Assert-NativeEvidenceKeys $provided $fields -Exact
        $value = [ordered]@{
            schemaVersion = 2; kind = 'conduit-native-benchmark-failure-diagnostic'
            attemptId = $contextObject['attemptId']; service = $contextObject['service']
        }
        foreach ($field in $fields) { $value[$field] = $provided[$field] }
        $validated = Get-NativeBenchmarkAttemptFailureDiagnostic $value
        $bytes = ConvertTo-NativeBenchmarkAttemptJsonBytes $validated
        $unusedRecord = Get-NativeBenchmarkAttemptLiveRecord $Context
        # Sidecars are immutable and cannot replace an original diagnostic or
        # alter attempt.json, historical reports, or the successful alias.
        $path = [IO.Path]::Combine($contextObject['attemptDirectory'], 'diagnostics.json')
        Write-NativeBenchmarkAttemptNewFile $path $bytes
    }
    catch { throw 'Native benchmark failure diagnostic could not be recorded.' }
}

function Close-NativeBenchmarkAttempt {
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Context)
    $ErrorActionPreference = 'Stop'
    try {
        if ($Context -isnot [Collections.IDictionary]) { throw 'Native benchmark attempt context is invalid.' }
        $contextObject = Get-NativeEvidenceObject $Context
        if (-not $contextObject.ContainsKey('lock') -or $contextObject['lock'] -isnot [IO.FileStream]) {
            throw 'Native benchmark attempt context is invalid.'
        }
        $contextObject['lock'].Dispose()
    }
    catch { throw 'Native benchmark attempt lock could not be closed.' }
}

function Read-NativeBenchmarkAttemptEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $OutputPath,
        [Parameter(Mandatory)][ValidateSet('admin', 'gateway')][string] $Service
    )
    $ErrorActionPreference = 'Stop'
    $Service = $Service.ToLowerInvariant()
    $lock = $null
    try {
        $output = Get-NativeEvidenceFullPath $OutputPath
        $root = $output + '.attempts'
        Assert-NativeBenchmarkAttemptDirectoryPath $root -Required
        $lockPath = [IO.Path]::Combine($root, 'writer.lock')
        Assert-NativeBenchmarkAttemptFilePath $lockPath -Required
        $lock = [IO.FileStream]::new($lockPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        $latestPath = [IO.Path]::Combine($root, 'latest.json')
        $latestDocument = Read-NativeBenchmarkAttemptDocument $latestPath
        $latest = Get-NativeBenchmarkAttemptLatest $latestDocument['value']
        if ($latest['service'] -cne $Service) { throw 'Native benchmark latest attempt service differs.' }
        $directory = [IO.Path]::Combine($root, $latest['attemptId'])
        Assert-NativeBenchmarkAttemptDirectoryPath $directory -Required
        $attemptPath = [IO.Path]::Combine($directory, 'attempt.json')
        $attemptDocument = Read-NativeBenchmarkAttemptDocument $attemptPath
        $attempt = Get-NativeBenchmarkAttemptRecord $attemptDocument['value']
        if ($attempt['attemptId'] -cne $latest['attemptId'] -or $attempt['service'] -cne $Service -or
            $attempt['status'] -cne 'succeeded') { throw 'Native benchmark latest attempt has not succeeded.' }
        $archivePath = [IO.Path]::Combine($directory, 'benchmark.json')
        $archiveDocument = Read-NativeBenchmarkAttemptDocument $archivePath -Report
        $reportDocument = Read-NativeBenchmarkAttemptDocument $output -Report
        if ($archiveDocument['sha256'] -cne $attempt['reportSha256'] -or
            $reportDocument['sha256'] -cne $attempt['reportSha256']) { throw 'Native benchmark report does not match its latest attempt.' }
        $report = Get-NativeEvidenceObject $reportDocument['value']
        Assert-NativeEvidenceKeys $report @('schemaVersion', 'service', 'generatedAt', 'collection')
        Assert-NativeEvidenceInteger $report['schemaVersion'] -Positive
        if ($report['schemaVersion'] -ne 4 -or $report['service'] -isnot [string] -or $report['service'] -cne $Service -or
            $report['generatedAt'] -isnot [string] -or $report['generatedAt'] -cne $attempt['completedAt']) {
            throw 'Native benchmark report attempt metadata is invalid.'
        }
        $collection = Get-NativeEvidenceObject $report['collection']
        Assert-NativeEvidenceKeys $collection @('attemptId', 'startedAt', 'completedAt') -Exact
        Assert-NativeBenchmarkAttemptIdentity $collection['attemptId'] $Service
        $unusedStarted = Get-NativeBenchmarkAttemptUtc $collection['startedAt']
        $unusedCompleted = Get-NativeBenchmarkAttemptUtc $collection['completedAt']
        if ($collection['attemptId'] -cne $attempt['attemptId'] -or $collection['startedAt'] -cne $attempt['startedAt'] -or
            $collection['completedAt'] -cne $attempt['completedAt']) { throw 'Native benchmark report and attempt timestamps differ.' }
        $finalLatest = Read-NativeBenchmarkAttemptDocument $latestPath
        $finalAttempt = Read-NativeBenchmarkAttemptDocument $attemptPath
        $finalArchive = Read-NativeBenchmarkAttemptDocument $archivePath -Report
        $finalReport = Read-NativeBenchmarkAttemptDocument $output -Report
        if ($finalLatest['sha256'] -cne $latestDocument['sha256'] -or $finalAttempt['sha256'] -cne $attemptDocument['sha256'] -or
            $finalArchive['sha256'] -cne $archiveDocument['sha256'] -or $finalReport['sha256'] -cne $reportDocument['sha256']) {
            throw 'Native benchmark attempt evidence changed while reading.'
        }
        return ,[ordered]@{
            latest = $latest; attempt = $attempt; report = $report; reportSha256 = $reportDocument['sha256']
            latestSha256 = $latestDocument['sha256']; attemptSha256 = $attemptDocument['sha256']; reportPath = $output
        }
    }
    catch { throw 'Native benchmark latest successful attempt could not be verified.' }
    finally { if ($null -ne $lock) { $lock.Dispose() } }
}
