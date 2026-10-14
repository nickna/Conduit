# Accepted benchmark timelines and explicitly approved archived collection attempts.
# Dot-sourcing defines read-only helpers; it does not acquire locks or change files.
. (Join-Path $PSScriptRoot 'native-benchmark-attempt-evidence.ps1')

function Assert-NativeBenchmarkQualificationHash($Value) {
    if ($Value -isnot [string] -or $Value -cnotmatch '\A[0-9a-f]{64}\z') {
        throw 'Native benchmark qualification hash is invalid.'
    }
}

function Get-NativeBenchmarkQualificationApproval($Value, [string] $Service) {
    $approval = Get-NativeEvidenceObject $Value
    Assert-NativeEvidenceKeys $approval @('selection', 'attemptId', 'reportSha256', 'canaryEvidenceSha256',
        'latestAttemptId', 'latestAttemptSha256', 'acceptedAt', 'acceptedBy') -Exact
    if ($approval['selection'] -isnot [string] -or $approval['selection'] -cnotin @('latest', 'historical')) {
        throw 'Native benchmark qualification selection is invalid.'
    }
    Assert-NativeBenchmarkAttemptIdentity $approval['attemptId'] $Service
    Assert-NativeBenchmarkAttemptIdentity $approval['latestAttemptId'] $Service
    foreach ($field in 'reportSha256', 'canaryEvidenceSha256', 'latestAttemptSha256') {
        Assert-NativeBenchmarkQualificationHash $approval[$field]
    }
    $unusedAccepted = Get-NativeBenchmarkAttemptUtc $approval['acceptedAt']
    Assert-NativeEvidenceText $approval['acceptedBy']
    if ($approval['acceptedBy'].Length -gt 256 -or
        $approval['acceptedBy'].Trim().ToLowerInvariant() -in @('replace-me', 'replace me', 'pending', 'unknown', 'not-applicable', 'todo')) {
        throw 'Native benchmark qualification reviewer is invalid.'
    }
    if (($approval['selection'] -ceq 'latest' -and $approval['attemptId'] -cne $approval['latestAttemptId']) -or
        ($approval['selection'] -ceq 'historical' -and $approval['attemptId'] -ceq $approval['latestAttemptId'])) {
        throw 'Native benchmark qualification attempt selection differs.'
    }
    return ,$approval
}

function Get-NativeBenchmarkQualificationValue($Value, [string] $Service) {
    $qualification = Get-NativeEvidenceObject $Value
    Assert-NativeEvidenceKeys $qualification @('schemaVersion', 'kind', 'qualificationId', 'status',
        'windowStartedAt', 'windowEndsAt', 'services') -Exact
    Assert-NativeEvidenceInteger $qualification['schemaVersion'] -Positive
    if ($qualification['schemaVersion'] -ne 1 -or $qualification['kind'] -isnot [string] -or
        $qualification['kind'] -cne 'conduit-native-benchmark-qualification' -or
        $qualification['status'] -isnot [string] -or $qualification['status'] -cne 'accepted') {
        throw 'Native benchmark qualification has not been accepted.'
    }
    Assert-NativeBenchmarkAttemptIdentity $qualification['qualificationId'] $Service
    $windowStarted = Get-NativeBenchmarkAttemptUtc $qualification['windowStartedAt']
    $windowEnds = Get-NativeBenchmarkAttemptUtc $qualification['windowEndsAt']
    if ($windowEnds -le $windowStarted) { throw 'Native benchmark qualification window is invalid.' }
    $services = Get-NativeEvidenceObject $qualification['services']
    Assert-NativeEvidenceKeys $services @('admin', 'gateway') -Exact
    foreach ($providedService in 'admin', 'gateway') {
        if ($null -ne $services[$providedService]) {
            $approval = Get-NativeBenchmarkQualificationApproval $services[$providedService] $providedService
            $accepted = Get-NativeBenchmarkAttemptUtc $approval['acceptedAt']
            if ($accepted -lt $windowStarted -or $accepted -gt $windowEnds) {
                throw 'Native benchmark approval lies outside its qualification window.'
            }
        }
    }
    if ($null -eq $services[$Service]) { throw 'Native benchmark service qualification is missing.' }
    return ,$qualification
}

function Get-NativeBenchmarkQualificationAttemptReport($Value, $Attempt, [string] $Service) {
    $report = Get-NativeEvidenceObject $Value
    Assert-NativeEvidenceKeys $report @('schemaVersion', 'service', 'generatedAt', 'collection')
    Assert-NativeEvidenceInteger $report['schemaVersion'] -Positive
    if ($report['schemaVersion'] -ne 4 -or $report['service'] -isnot [string] -or $report['service'] -cne $Service -or
        $report['generatedAt'] -isnot [string] -or $report['generatedAt'] -cne $Attempt['completedAt']) {
        throw 'Native benchmark qualified report attempt metadata is invalid.'
    }
    $collection = Get-NativeEvidenceObject $report['collection']
    Assert-NativeEvidenceKeys $collection @('attemptId', 'startedAt', 'completedAt') -Exact
    Assert-NativeBenchmarkAttemptIdentity $collection['attemptId'] $Service
    $unusedStarted = Get-NativeBenchmarkAttemptUtc $collection['startedAt']
    $unusedCompleted = Get-NativeBenchmarkAttemptUtc $collection['completedAt']
    if ($collection['attemptId'] -cne $Attempt['attemptId'] -or $collection['startedAt'] -cne $Attempt['startedAt'] -or
        $collection['completedAt'] -cne $Attempt['completedAt']) {
        throw 'Native benchmark qualified report and attempt timestamps differ.'
    }
    return ,$report
}

function Read-NativeBenchmarkQualificationEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][ValidateSet('admin', 'gateway')][string] $Service
    )
    $ErrorActionPreference = 'Stop'
    $Service = $Service.ToLowerInvariant()
    try {
        $fullPath = Get-NativeEvidenceFullPath $Path
        # Reuse the strict original-byte reader with the 64 KiB metadata limit.
        $document = Read-NativeBenchmarkAttemptDocument $fullPath
        $qualification = Get-NativeBenchmarkQualificationValue $document['value'] $Service
        $services = Get-NativeEvidenceObject $qualification['services']
        $approval = Get-NativeBenchmarkQualificationApproval $services[$Service] $Service
        return ,[ordered]@{ qualification = $qualification; approval = $approval; sha256 = $document['sha256'] }
    }
    catch { throw 'Native benchmark accepted qualification could not be verified.' }
}

function Read-NativeBenchmarkAttemptArchiveEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $OutputPath,
        [Parameter(Mandatory)][ValidateSet('admin', 'gateway')][string] $Service,
        [Parameter(Mandatory)][string] $AttemptId
    )
    $ErrorActionPreference = 'Stop'
    $Service = $Service.ToLowerInvariant()
    $lock = $null
    try {
        Assert-NativeBenchmarkAttemptIdentity $AttemptId $Service
        # OutputPath is only the journal locator; historical reads never open the alias.
        $output = Get-NativeEvidenceFullPath $OutputPath
        $root = $output + '.attempts'
        Assert-NativeBenchmarkAttemptDirectoryPath $root -Required
        $lockPath = [IO.Path]::Combine($root, 'writer.lock')
        Assert-NativeBenchmarkAttemptFilePath $lockPath -Required
        $lock = [IO.FileStream]::new($lockPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        $latestPath = [IO.Path]::Combine($root, 'latest.json')
        $latestDocument = Read-NativeBenchmarkAttemptDocument $latestPath
        $latest = Get-NativeBenchmarkAttemptLatest $latestDocument['value']
        if ($latest['service'] -cne $Service -or $latest['attemptId'] -ceq $AttemptId) {
            throw 'Native benchmark historical selection is not a distinct service attempt.'
        }
        $latestDirectory = [IO.Path]::Combine($root, $latest['attemptId'])
        Assert-NativeBenchmarkAttemptDirectoryPath $latestDirectory -Required
        $latestAttemptPath = [IO.Path]::Combine($latestDirectory, 'attempt.json')
        $latestAttemptDocument = Read-NativeBenchmarkAttemptDocument $latestAttemptPath
        $latestAttempt = Get-NativeBenchmarkAttemptRecord $latestAttemptDocument['value']
        if ($latestAttempt['attemptId'] -cne $latest['attemptId'] -or $latestAttempt['service'] -cne $Service -or
            $latestAttempt['status'] -cne 'failed') {
            throw 'Native benchmark historical selection requires a terminal failed latest attempt.'
        }
        $directory = [IO.Path]::Combine($root, $AttemptId)
        Assert-NativeBenchmarkAttemptDirectoryPath $directory -Required
        $attemptPath = [IO.Path]::Combine($directory, 'attempt.json')
        $attemptDocument = Read-NativeBenchmarkAttemptDocument $attemptPath
        $attempt = Get-NativeBenchmarkAttemptRecord $attemptDocument['value']
        if ($attempt['attemptId'] -cne $AttemptId -or $attempt['service'] -cne $Service -or $attempt['status'] -cne 'succeeded') {
            throw 'Native benchmark archived attempt has not succeeded.'
        }
        if ((Get-NativeBenchmarkAttemptUtc $attempt['completedAt']) -gt
            (Get-NativeBenchmarkAttemptUtc $latestAttempt['startedAt'])) {
            throw 'Native benchmark archived attempt follows its failed latest attempt.'
        }
        $archivePath = [IO.Path]::Combine($directory, 'benchmark.json')
        $archiveDocument = Read-NativeBenchmarkAttemptDocument $archivePath -Report
        if ($archiveDocument['sha256'] -cne $attempt['reportSha256']) {
            throw 'Native benchmark archived report does not match its successful attempt.'
        }
        $report = Get-NativeBenchmarkQualificationAttemptReport $archiveDocument['value'] $attempt $Service
        $finalLatest = Read-NativeBenchmarkAttemptDocument $latestPath
        $finalLatestAttempt = Read-NativeBenchmarkAttemptDocument $latestAttemptPath
        $finalAttempt = Read-NativeBenchmarkAttemptDocument $attemptPath
        $finalArchive = Read-NativeBenchmarkAttemptDocument $archivePath -Report
        if ($finalLatest['sha256'] -cne $latestDocument['sha256'] -or
            $finalLatestAttempt['sha256'] -cne $latestAttemptDocument['sha256'] -or
            $finalAttempt['sha256'] -cne $attemptDocument['sha256'] -or
            $finalArchive['sha256'] -cne $archiveDocument['sha256']) {
            throw 'Native benchmark archived attempt evidence changed while reading.'
        }
        return ,[ordered]@{
            latest = $latest; latestAttempt = $latestAttempt; attempt = $attempt; report = $report
            reportSha256 = $archiveDocument['sha256']; latestSha256 = $latestDocument['sha256']
            latestAttemptSha256 = $latestAttemptDocument['sha256']; attemptSha256 = $attemptDocument['sha256']; reportPath = $archivePath
        }
    }
    catch { throw 'Native benchmark approved historical attempt could not be verified.' }
    finally { if ($null -ne $lock) { $lock.Dispose() } }
}

function Assert-NativeBenchmarkQualificationTimeline {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $QualificationEvidence,
        [Parameter(Mandatory)] $AttemptEvidence,
        [Parameter(Mandatory)] $CanaryDocument,
        [Parameter(Mandatory)] $HistoricalEvidencePolicy,
        [Parameter(Mandatory)][DateTimeOffset] $Now
    )
    $ErrorActionPreference = 'Stop'
    try {
        if ($HistoricalEvidencePolicy -isnot [string] -or $HistoricalEvidencePolicy -cnotin @('latest-only', 'approved-archive')) {
            throw 'Native benchmark historical evidence policy is invalid.'
        }
        $nowUtc = ConvertTo-NativeEvidenceUtc $Now
        $attemptEvidenceObject = Get-NativeEvidenceObject $AttemptEvidence
        Assert-NativeEvidenceKeys $attemptEvidenceObject @('latest', 'attempt', 'report', 'reportSha256',
            'latestSha256', 'attemptSha256', 'reportPath')
        $attempt = Get-NativeBenchmarkAttemptRecord $attemptEvidenceObject['attempt']
        $service = $attempt['service']
        if ($attempt['status'] -cne 'succeeded') { throw 'Native benchmark selected attempt has not succeeded.' }
        $qualificationEvidenceObject = Get-NativeEvidenceObject $QualificationEvidence
        Assert-NativeEvidenceKeys $qualificationEvidenceObject @('qualification', 'approval', 'sha256') -Exact
        Assert-NativeBenchmarkQualificationHash $qualificationEvidenceObject['sha256']
        $qualification = Get-NativeBenchmarkQualificationValue $qualificationEvidenceObject['qualification'] $service
        $services = Get-NativeEvidenceObject $qualification['services']
        $approval = Get-NativeBenchmarkQualificationApproval $services[$service] $service
        $suppliedApproval = Get-NativeBenchmarkQualificationApproval $qualificationEvidenceObject['approval'] $service
        if (-not (Test-NativeImageOciEvidenceValueEqual $approval $suppliedApproval)) {
            throw 'Native benchmark qualification selected service approval differs.'
        }
        $latest = Get-NativeBenchmarkAttemptLatest $attemptEvidenceObject['latest']
        if ($latest['service'] -cne $service -or $latest['attemptId'] -cne $approval['latestAttemptId'] -or
            $attempt['attemptId'] -cne $approval['attemptId']) {
            throw 'Native benchmark qualification attempt identities differ.'
        }
        foreach ($field in 'reportSha256', 'latestSha256', 'attemptSha256') {
            Assert-NativeBenchmarkQualificationHash $attemptEvidenceObject[$field]
        }
        if ($attemptEvidenceObject['reportSha256'] -cne $approval['reportSha256'] -or
            $attempt['reportSha256'] -cne $attemptEvidenceObject['reportSha256']) {
            throw 'Native benchmark qualification report hash differs.'
        }
        $report = Get-NativeBenchmarkQualificationAttemptReport $attemptEvidenceObject['report'] $attempt $service
        $canaryDocumentObject = Get-NativeEvidenceObject $CanaryDocument
        Assert-NativeEvidenceKeys $canaryDocumentObject @('value', 'sha256', 'bytes') -Exact
        Assert-NativeBenchmarkQualificationHash $canaryDocumentObject['sha256']
        Assert-NativeEvidenceInteger $canaryDocumentObject['bytes'] -Positive
        if ($canaryDocumentObject['sha256'] -cne $approval['canaryEvidenceSha256']) {
            throw 'Native benchmark qualification canary original bytes differ.'
        }
        $canary = Get-NativeEvidenceObject $canaryDocumentObject['value']
        Assert-NativeEvidenceKeys $canary @('service', 'startedAt', 'completedAt')
        if ($canary['service'] -isnot [string] -or $canary['service'] -cne $service) {
            throw 'Native benchmark qualification canary service differs.'
        }
        $windowStarted = Get-NativeBenchmarkAttemptUtc $qualification['windowStartedAt']
        $windowEnds = Get-NativeBenchmarkAttemptUtc $qualification['windowEndsAt']
        $accepted = Get-NativeBenchmarkAttemptUtc $approval['acceptedAt']
        $collectionStarted = Get-NativeBenchmarkAttemptUtc $attempt['startedAt']
        $collectionCompleted = Get-NativeBenchmarkAttemptUtc $attempt['completedAt']
        $canaryStarted = Get-NativeBenchmarkAttemptUtc $canary['startedAt']
        $canaryCompleted = Get-NativeBenchmarkAttemptUtc $canary['completedAt']
        if ($nowUtc -lt $windowStarted -or $nowUtc -gt $windowEnds -or
            $collectionStarted -lt $windowStarted -or $collectionCompleted -gt $windowEnds -or
            $canaryStarted -lt $windowStarted -or $canaryCompleted -gt $windowEnds -or $canaryCompleted -le $canaryStarted -or
            $accepted -lt $collectionCompleted -or $accepted -lt $canaryCompleted -or $accepted -gt $nowUtc) {
            throw 'Native benchmark qualification timeline lies outside its accepted window.'
        }
        if ($approval['selection'] -ceq 'latest') {
            Assert-NativeEvidenceKeys $attemptEvidenceObject @('latest', 'attempt', 'report', 'reportSha256',
                'latestSha256', 'attemptSha256', 'reportPath') -Exact
            if ($latest['attemptId'] -cne $attempt['attemptId'] -or
                $attemptEvidenceObject['attemptSha256'] -cne $approval['latestAttemptSha256']) {
                throw 'Native benchmark qualification latest attempt differs.'
            }
        }
        else {
            Assert-NativeEvidenceKeys $attemptEvidenceObject @('latest', 'latestAttempt', 'attempt', 'report',
                'reportSha256', 'latestSha256', 'latestAttemptSha256', 'attemptSha256', 'reportPath') -Exact
            if ($HistoricalEvidencePolicy -cne 'approved-archive') {
                throw 'Native benchmark historical selection is not allowed by policy.'
            }
            Assert-NativeBenchmarkQualificationHash $attemptEvidenceObject['latestAttemptSha256']
            $latestAttempt = Get-NativeBenchmarkAttemptRecord $attemptEvidenceObject['latestAttempt']
            if ($latestAttempt['attemptId'] -cne $latest['attemptId'] -or $latestAttempt['service'] -cne $service -or
                $latestAttempt['status'] -cne 'failed' -or $latestAttempt['attemptId'] -ceq $attempt['attemptId'] -or
                $attemptEvidenceObject['latestAttemptSha256'] -cne $approval['latestAttemptSha256']) {
                throw 'Native benchmark qualification failed latest attempt differs.'
            }
            $latestStarted = Get-NativeBenchmarkAttemptUtc $latestAttempt['startedAt']
            $latestCompleted = Get-NativeBenchmarkAttemptUtc $latestAttempt['completedAt']
            if ($latestStarted -lt $windowStarted -or $latestCompleted -gt $windowEnds -or
                $collectionCompleted -gt $latestStarted -or $accepted -lt $latestCompleted) {
                throw 'Native benchmark acknowledged failure lies outside its accepted timeline.'
            }
        }
    }
    catch { throw 'Native benchmark qualification timeline could not be verified.' }
}
