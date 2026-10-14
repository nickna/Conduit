#!/usr/bin/env pwsh
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot 'native-image-runtime-evidence.ps1'
$fixtureSource = Join-Path $PSScriptRoot 'test-native-image-verification.ps1'
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$temporaryRoot = Join-Path $temporaryParent ('conduit-image-runtime-contract-' + [Guid]::NewGuid().ToString('N'))
$environmentNames = @('CONDUIT_IMAGE_RUNTIME_TEST_ROOT', 'CONDUIT_IMAGE_RUNTIME_TEST_SCENARIO', 'CONDUIT_IMAGE_RUNTIME_TEST_PHASE', 'CONDUIT_IMAGE_RUNTIME_TEST_DIAGNOSTIC')
$oldEnvironment = @{}
foreach ($name in $environmentNames) { $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
$otherImageId = 'sha256:' + ('b' * 64); $containerId = 'c' * 64
$privateDiagnostic = 'private-runtime-fixture-' + [Guid]::NewGuid().ToString('N')
$cases = 0; $sequence = 0; $failure = $null; $cleanupFailure = $false
$goldens = @{}

function Assert-RuntimeContract([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Set-RuntimeFixtureEnvironment([string] $Root, [string] $Scenario, [string] $Phase) {
    [Environment]::SetEnvironmentVariable('CONDUIT_IMAGE_RUNTIME_TEST_ROOT', $Root)
    [Environment]::SetEnvironmentVariable('CONDUIT_IMAGE_RUNTIME_TEST_SCENARIO', $Scenario)
    [Environment]::SetEnvironmentVariable('CONDUIT_IMAGE_RUNTIME_TEST_PHASE', $Phase)
    [Environment]::SetEnvironmentVariable('CONDUIT_IMAGE_RUNTIME_TEST_DIAGNOSTIC', $privateDiagnostic)
}

function Get-RuntimeRecords([string] $Root) {
    return ,@(Get-ChildItem -LiteralPath (Join-Path $Root 'commands') -File |
        ForEach-Object { Read-VerificationJson $_.FullName } | Sort-Object ordinal)
}

function Copy-RuntimeFixtureDirectory([string] $Source, [string] $Destination) {
    Assert-NativeEvidencePathAncestry $Source
    Assert-NativeEvidencePathAncestry $Destination
    Assert-RuntimeContract (-not [IO.Directory]::Exists($Destination) -and -not [IO.File]::Exists($Destination)) 'Fixture copy destination was not fresh.'
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($directory in @(([IO.DirectoryInfo]::new($Source)).EnumerateDirectories('*', [IO.SearchOption]::AllDirectories))) {
        Assert-RuntimeContract (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Fixture copy rejected a linked directory.'
        [IO.Directory]::CreateDirectory((Join-Path $Destination ([IO.Path]::GetRelativePath($Source, $directory.FullName)))) | Out-Null
    }
    foreach ($file in @(([IO.DirectoryInfo]::new($Source)).EnumerateFiles('*', [IO.SearchOption]::AllDirectories))) {
        Assert-NativeEvidenceRegularFile $file.FullName
        [IO.File]::Copy($file.FullName, (Join-Path $Destination ([IO.Path]::GetRelativePath($Source, $file.FullName))), $false)
    }
}

function Get-RuntimeFixtureFingerprint([string] $Root) {
    $rows = [Collections.Generic.List[string]]::new()
    foreach ($file in @(([IO.DirectoryInfo]::new($Root)).EnumerateFiles('*', [IO.SearchOption]::AllDirectories) | Sort-Object FullName)) {
        $rows.Add([IO.Path]::GetRelativePath($Root, $file.FullName) + ':' + (Get-VerificationHash $file.FullName))
    }
    return $rows -join '|'
}

function Get-RuntimeProcessToken([Diagnostics.Process] $Process) {
    if ($IsLinux) {
        $stat = [IO.File]::ReadAllText('/proc/' + $Process.Id + '/stat')
        $separator = $stat.LastIndexOf(') ', [StringComparison]::Ordinal)
        Assert-RuntimeContract ($separator -ge 0) 'Owned runtime command stat was malformed.'
        $fields = $stat.Substring($separator + 2).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)
        [ulong]$start = 0
        Assert-RuntimeContract ($fields.Count -gt 19 -and [ulong]::TryParse($fields[19], [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$start) -and $start -gt 0) 'Owned runtime command start identity was malformed.'
        return 'linux:' + $start.ToString([Globalization.CultureInfo]::InvariantCulture)
    }
    return 'windows:' + $Process.StartTime.ToUniversalTime().Ticks.ToString([Globalization.CultureInfo]::InvariantCulture)
}

function Assert-RuntimeCommandReaped([string] $Root, [switch] $Stop) {
    $path = Join-Path $Root 'paused-process.json'
    if (-not [IO.File]::Exists($path)) { return }
    $identity = Read-VerificationJson $path
    $process = Get-Process -Id $identity.id -ErrorAction SilentlyContinue
    if ($null -eq $process) { return }
    try {
        if ((Get-RuntimeProcessToken $process) -cne $identity.token) { return }
        $zombie = $IsLinux -and [IO.File]::Exists('/proc/' + $process.Id + '/stat') -and
            [IO.File]::ReadAllText('/proc/' + $process.Id + '/stat') -match '^\d+ \(.+\) Z '
        if ($Stop -and -not $process.HasExited -and -not $zombie) {
            $process.Kill($true)
            Assert-RuntimeContract ($process.WaitForExit(5000)) 'Owned runtime fixture command did not reap.'
        }
        else { Assert-RuntimeContract ($process.HasExited -or $zombie) 'Runtime verification left its timed-out command alive.' }
    }
    finally { $process.Dispose() }
}

function Invoke-RuntimeCase([string] $Service, [string] $Scenario, [bool] $Success) {
    $script:sequence++
    $root = Join-Path $temporaryRoot ('case-' + $script:sequence.ToString('D3') + ' café 雪')
    [IO.Directory]::CreateDirectory($root) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $root 'commands')) | Out-Null
    $package = Join-Path $root 'packaging'; $artifact = Join-Path $root 'runtime'
    $goldenKey = if ($Scenario -cin @('oci-hold-no-descriptors', 'oci-hold-classic')) { $Service + '-' + $Scenario } else { $Service }
    Copy-RuntimeFixtureDirectory $goldens[$goldenKey].artifact $package
    [IO.File]::Copy((Join-Path $goldens[$goldenKey].root 'image.json'), (Join-Path $root 'image.json'), $false)
    [IO.File]::Copy((Join-Path $goldens[$goldenKey].root 'selected-descriptor.json'), (Join-Path $root 'selected-descriptor.json'), $false)
    $localImageId = (Read-VerificationJson (Join-Path $root 'image.json')).Id
    $packageReportPath = Join-Path $package 'packaging-evidence.json'
    $originalPackage = Read-VerificationJson $packageReportPath
    switch ($Scenario) {
        'historical-schema1' {
            $changed = Read-VerificationJson $packageReportPath; $changed.schemaVersion = 1; $changed.Remove('ociAssociation') | Out-Null
            Write-VerificationJson $packageReportPath $changed
            foreach ($name in @('oci-association.json', 'image-observations.json', 'image-save.tar')) { [IO.File]::Delete((Join-Path $package $name)) }
        }
        'oci-report-tamper' {
            $path = Join-Path $package 'oci-association.json'; $changed = Read-VerificationJson $path
            $changed.descriptorChainVerified = $false; Write-VerificationJson $path $changed
            $changedPackage = Read-VerificationJson $packageReportPath; $changedPackage.ociAssociation.report.sha256 = Get-VerificationHash $path
            Write-VerificationJson $packageReportPath $changedPackage
        }
        'oci-observations-tamper' {
            $path = Join-Path $package 'image-observations.json'; $changed = Read-VerificationJson $path
            $changed.container.imageManifestDescriptor.digest = 'sha256:' + ('d' * 64); Write-VerificationJson $path $changed
            $changedPackage = Read-VerificationJson $packageReportPath; $changedPackage.ociAssociation.observations.sha256 = Get-VerificationHash $path
            Write-VerificationJson $packageReportPath $changedPackage
        }
        'oci-archive-tamper' {
            $path = Join-Path $package 'image-save.tar'; $stream = [IO.File]::Open($path, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $tail = [byte[]]::new(512); [Array]::Fill[byte]($tail, [byte]1)
            try { $stream.Write($tail, 0, $tail.Length) } finally { $stream.Dispose() }
            $changedPackage = Read-VerificationJson $packageReportPath; $changedPackage.ociAssociation.archive.sha256 = Get-VerificationHash $path
            $changedPackage.ociAssociation.archive.bytes = ([IO.FileInfo]::new($path)).Length
            Write-VerificationJson $packageReportPath $changedPackage
        }
        'oci-reference-path' {
            $changed = Read-VerificationJson $packageReportPath; $changed.ociAssociation.report.path = '../outside'
            Write-VerificationJson $packageReportPath $changed
        }
        'package-report-tamper' { $changed = Read-VerificationJson $packageReportPath; $changed.cleanupConfirmed = $false; Write-VerificationJson $packageReportPath $changed }
        'manifest-tamper' { [IO.File]::AppendAllText((Join-Path $package 'extracted/conduit-evidence/application.json'), ' ') }
        'payload-tamper' {
            $main = if ($Service -ceq 'admin') { 'ConduitLLM.Admin' } else { 'ConduitLLM.Gateway' }
            $path = Join-Path $package ('extracted/app/' + $main); $bytes = [IO.File]::ReadAllBytes($path)
            $bytes[0] = $bytes[0] -bxor 1; [IO.File]::WriteAllBytes($path, $bytes)
        }
    }
    $sourceFingerprint = Get-RuntimeFixtureFingerprint $package
    $expectedSourceFingerprint = $sourceFingerprint
    if ($Scenario -cin @('runtime-manifest-tamper', 'runtime-payload-tamper', 'runtime-oci-report-tamper', 'runtime-oci-archive-tamper')) {
        $relative = switch ($Scenario) {
            'runtime-manifest-tamper' { Join-Path 'extracted/conduit-evidence' 'application.json' }
            'runtime-payload-tamper' { Join-Path 'extracted/app' $(if ($Service -ceq 'admin') { 'ConduitLLM.Admin' } else { 'ConduitLLM.Gateway' }) }
            'runtime-oci-report-tamper' { 'oci-association.json' }
            'runtime-oci-archive-tamper' { 'image-save.tar' }
        }
        $changedPath = Join-Path $package $relative
        $expectedBytes = [IO.File]::ReadAllBytes($changedPath)
        if ($Scenario -cin @('runtime-manifest-tamper', 'runtime-oci-report-tamper')) { $expectedBytes = [byte[]]($expectedBytes + [byte]32) }
        elseif ($Scenario -ceq 'runtime-oci-archive-tamper') {
            $tail = [byte[]]::new(512); [Array]::Fill[byte]($tail, [byte]1)
            $expectedBytes = [byte[]]($expectedBytes + $tail)
        }
        else { $expectedBytes[0] = $expectedBytes[0] -bxor 1 }
        $expectedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($expectedBytes)).ToLowerInvariant()
        $entry = [IO.Path]::GetRelativePath($package, $changedPath) + ':'
        $expectedSourceFingerprint = $sourceFingerprint.Replace($entry + (Get-VerificationHash $changedPath), $entry + $expectedHash)
        Assert-RuntimeContract ($expectedSourceFingerprint -cne $sourceFingerprint) 'Runtime mutation fixture did not plan a distinct source hash.'
    }
    Set-RuntimeFixtureEnvironment $root $Scenario 'runtime'
    $parameters = @{ PackagingDirectory = $package; ArtifactDirectory = $artifact; DockerCommandPath = $script:fakeDocker
        DockerCommandTimeoutSeconds = 5; ExecutionTimeoutSeconds = 10; CleanupTimeoutSeconds = 15 }
    $sentinel = $null; $held = $null
    if ($Scenario -cin @('fresh-visible', 'fresh-hidden', 'fresh-claim')) {
        [IO.Directory]::CreateDirectory($artifact) | Out-Null
        $name = switch ($Scenario) { 'fresh-visible' { 'sentinel' }; 'fresh-hidden' { '.hidden' }; default { 'native-image-runtime.claim' } }
        $sentinel = Join-Path $artifact $name; [IO.File]::WriteAllText($sentinel, 'existing runtime owner')
        if ($Scenario -ceq 'fresh-claim') { $held = [IO.FileStream]::new($sentinel, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    }
    switch ($Scenario) {
        'invalid-execution-timeout' { $parameters.ExecutionTimeoutSeconds = 0 }
        'invalid-cleanup-timeout' { $parameters.CleanupTimeoutSeconds = 0 }
        'invalid-docker-timeout' { $parameters.DockerCommandTimeoutSeconds = 0 }
        'wait-timeout' { $parameters.ExecutionTimeoutSeconds = 5 }
        'nested-artifact' { $parameters.ArtifactDirectory = Join-Path $package 'nested-runtime'; $artifact = $parameters.ArtifactDirectory }
    }
    $watch = [Diagnostics.Stopwatch]::StartNew(); $caught = $null; $results = @()
    try { $results = @(Get-NativeImageRuntimeEvidence @parameters) }
    catch { $caught = $_ }
    finally {
        $watch.Stop()
        if ($null -ne $held) { $held.Dispose() }
    }
    $records = Get-RuntimeRecords $root
    $created = @($records | Where-Object { $_.arguments[0] -ceq 'container' -and $_.arguments[1] -ceq 'create' })
    $stopped = @($records | Where-Object { $_.arguments[0] -ceq 'container' -and $_.arguments[1] -ceq 'stop' })
    $removed = @($records | Where-Object { $_.arguments[0] -ceq 'container' -and $_.arguments[1] -ceq 'rm' })
    $reportPath = Join-Path $artifact 'evidence.json'
    Assert-RuntimeContract ((Get-RuntimeFixtureFingerprint $package) -ceq $expectedSourceFingerprint) 'Runtime source mutation differed from its controlled fixture change.'
    Assert-RuntimeContract ($null -eq $caught -or -not $caught.Exception.Message.Contains($privateDiagnostic, [StringComparison]::Ordinal)) 'Runtime verification leaked private child diagnostics.'
    if ($Success) {
        Assert-RuntimeContract ($null -eq $caught -and $results.Count -eq 1 -and $results[0] -is [Collections.Specialized.OrderedDictionary]) ('Valid runtime evidence failed: ' + $Service)
        $report = $results[0]; $persisted = Read-VerificationJson $reportPath
        $keys = @('image', 'id', 'user', 'paths', 'succeeded', 'schemaVersion', 'kind', 'service', 'owner', 'containerId', 'buildId', 'rid', 'source', 'packagingEvidence', 'openApiSha256', 'openApiBytes', 'verifiedAtUtc', 'cleanupConfirmed', 'registryDigestQualification')
        Assert-RuntimeContract ($report.Count -eq 19 -and ((@($report.Keys | Sort-Object) -join '|') -ceq (@($keys | Sort-Object) -join '|'))) 'Runtime evidence properties differ.'
        Assert-RuntimeContract ($report.schemaVersion -is [int] -and $report.schemaVersion -eq 1 -and
            $report.kind -ceq 'conduit-native-image-execution' -and $report.service -ceq $Service -and
            $report.image -ceq $originalPackage.requestedImage -and $report.id -ceq $localImageId -and $report.user -ceq '1654' -and
            $report.containerId -ceq $containerId -and $report.owner -cmatch '^[0-9a-f]{32}$' -and
            $report.buildId -ceq $originalPackage.buildId -and $report.rid -ceq 'linux-x64' -and
            $report.paths -is [int] -and $report.paths -eq 2) 'Runtime identity/build/path count differs.'
        Assert-RuntimeContract ($report.succeeded -is [bool] -and $report.succeeded -and $report.cleanupConfirmed -is [bool] -and
            $report.cleanupConfirmed -and $report.registryDigestQualification -is [bool] -and -not $report.registryDigestQualification -and
            $report.source.status -ceq 'unavailable' -and $null -eq $report.source.revision -and $null -eq $report.source.dirty) 'Runtime evidence invented success, source or digest qualification.'
        $openApi = Join-Path $artifact 'openapi.json'
        Assert-RuntimeContract ($report.packagingEvidence.Count -eq 2 -and $report.packagingEvidence.path -ceq $packageReportPath -and
            $report.packagingEvidence.sha256 -ceq (Get-VerificationHash $packageReportPath) -and $report.openApiSha256 -ceq (Get-VerificationHash $openApi) -and
            $report.openApiBytes -is [long] -and $report.openApiBytes -eq ([IO.FileInfo]::new($openApi)).Length) 'Runtime evidence is not bound to exact package/OpenAPI bytes.'
        $document = Read-VerificationJson $openApi
        Assert-RuntimeContract ($document.components.schemas.Price.minimum -eq 0.125 -and
            $document.components.schemas.Price.maximum -eq 1.5 -and $document.components.schemas.Price.example.nested[0] -eq 0.25 -and
            $document.components.schemas.Price.description -ceq 'café 雪 🔎') 'Valid float-bearing recursive OpenAPI content was changed.'
        Assert-RuntimeContract ($created.Count -eq 1 -and $created[0].arguments[-1] -ceq $localImageId -and
            $stopped.Count -eq 0 -and $removed.Count -eq 1 -and ($removed[0].arguments -join '|') -ceq ('container|rm|' + $containerId) -and
            -not [IO.File]::Exists((Join-Path $root 'active.json')) -and @($records | Where-Object reportPresent).Count -eq 0) 'Runtime success preceded cleanup or used an unpinned/forced container operation.'
        $inspections = @($records | Where-Object { $_.arguments[1] -ceq 'inspect' })
        $createName = $created[0].arguments[[Array]::IndexOf([object[]]$created[0].arguments, '--name') + 1]
        Assert-RuntimeContract ($inspections.Count -gt 1 -and $inspections[0].arguments[-1] -ceq $createName -and
            @($inspections | Select-Object -Skip 1 | Where-Object { $_.arguments[-1] -cne $containerId }).Count -eq 0 -and
            @($records | Where-Object { $_.arguments[0] -ceq 'image' }).Count -eq 0) 'Runtime execution re-resolved the mutable image or lost its associated container ID.'
        Assert-RuntimeContract ($persisted.id -ceq $report.id -and $persisted.owner -ceq $report.owner -and $persisted.openApiSha256 -ceq $report.openApiSha256 -and
            $persisted.cleanupConfirmed -and -not $persisted.registryDigestQualification -and
            [IO.File]::ReadAllText((Join-Path $artifact 'native-image-runtime.claim')) -ceq ($report.owner + "`n")) 'Persisted runtime result or owner claim differs.'
    }
    else {
        Assert-RuntimeContract ($null -ne $caught -and -not [IO.File]::Exists($reportPath)) ('Invalid runtime was accepted or wrote success evidence: ' + $Scenario)
        if ($Scenario -cin @('package-report-tamper', 'manifest-tamper', 'payload-tamper', 'nested-artifact',
            'oci-report-tamper', 'oci-observations-tamper', 'oci-archive-tamper', 'oci-reference-path') -or
            $Scenario.StartsWith('fresh-', [StringComparison]::Ordinal) -or $Scenario.StartsWith('invalid-', [StringComparison]::Ordinal)) {
            Assert-RuntimeContract ($records.Count -eq 0) 'Invalid source/output/deadline reached Docker.'
            if ($null -ne $sentinel) { Assert-RuntimeContract ([IO.File]::ReadAllText($sentinel) -ceq 'existing runtime owner' -and [IO.Directory]::GetFileSystemEntries($artifact).Length -eq 1) 'Runtime freshness rejection changed existing state.' }
        }
        elseif ($Scenario -cin @('wrong-owner', 'wrong-name', 'wrong-image', 'foreign-running')) {
            Assert-RuntimeContract ($created.Count -eq 1 -and $stopped.Count -eq 0 -and $removed.Count -eq 0 -and [IO.File]::Exists((Join-Path $root 'active.json'))) 'Runtime cleanup acted on foreign/unassociated state.'
        }
        elseif ($Scenario -cin @('stop-failure', 'stop-still-running')) {
            Assert-RuntimeContract ($stopped.Count -eq 1 -and $removed.Count -eq 0 -and [IO.File]::Exists((Join-Path $root 'active.json'))) 'Runtime stop failure was followed by unsafe removal.'
        }
        elseif ($Scenario -ceq 'rm-failure') {
            Assert-RuntimeContract ($removed.Count -eq 1 -and [IO.File]::Exists((Join-Path $root 'active.json'))) 'Runtime removal failure did not remain fail-closed.'
        }
        elseif ($created.Count -gt 0) {
            Assert-RuntimeContract ($removed.Count -eq 1 -and -not [IO.File]::Exists((Join-Path $root 'active.json'))) 'Failed runtime did not remove its verified stopped container.'
        }
        if ($Scenario -cin @('start-nonzero', 'wait-timeout', 'running-after-wait')) {
            Assert-RuntimeContract ($stopped.Count -eq 1 -and $removed.Count -eq 1 -and $stopped[0].arguments[-1] -ceq $containerId) 'Owned running failure did not stop then nonforce-remove its exact ID.'
            $stopOrdinal = $stopped[0].ordinal
            Assert-RuntimeContract (@($records | Where-Object { $_.ordinal -gt $stopOrdinal -and $_.ordinal -lt $removed[0].ordinal -and $_.arguments[1] -ceq 'inspect' }).Count -gt 0) 'Removal did not re-inspect after stopping the runtime.'
        }
        if ($Scenario -cin @('create-nonzero', 'create-malformed', 'create-timeout')) {
            $name = $created[0].arguments[[Array]::IndexOf([object[]]$created[0].arguments, '--name') + 1]
            Assert-RuntimeContract (@($records | Where-Object { $_.arguments[1] -ceq 'inspect' -and $_.arguments[-1] -ceq $name }).Count -gt 0) 'Unknown runtime create was not recovered by exact owned name.'
        }
        if ($Scenario -cin @('create-timeout', 'cp-timeout', 'logs-overflow')) {
            $expectedStage = switch ($Scenario) {
                'create-timeout' { 'runtime-create' }
                'cp-timeout' { 'runtime-openapi-copy' }
                'logs-overflow' { 'runtime-logs' }
            }
            $targetRecords = switch ($Scenario) {
                'create-timeout' {
                    @($records | Where-Object {
                        $_.arguments[0] -ceq 'container' -and $_.arguments[1] -ceq 'create' -and
                        $_.arguments[-1] -ceq $localImageId -and
                        [regex]::IsMatch($_.arguments[[Array]::IndexOf([object[]]$_.arguments, '--name') + 1], '\Aconduit-native-image-entrypoint-admin-[0-9a-f]{32}\z')
                    })
                }
                'cp-timeout' {
                    @($records | Where-Object {
                        $_.arguments.Count -eq 4 -and ($_.arguments[0..1] -join '|') -ceq 'container|cp' -and
                        $_.arguments[2] -ceq ($containerId + ':/tmp/packaged-openapi.json') -and
                        $_.arguments[3] -ceq (Join-Path $artifact 'openapi.json')
                    })
                }
                'logs-overflow' {
                    @($records | Where-Object {
                        $_.arguments.Count -eq 3 -and ($_.arguments -join '|') -ceq ('container|logs|' + $containerId)
                    })
                }
            }
            $targetRecords = @($targetRecords)
            $stageMatch = [regex]::Match($caught.Exception.Message,
                '\ANative image execution failed; stage=(?<stage>[a-z-]+); type=[A-Za-z0-9_.+`]+; line=[0-9]+\.\z')
            $controlledStage = if ($stageMatch.Success) { $stageMatch.Groups['stage'].Value } else { 'unclassified' }
            $latched = [IO.File]::Exists((Join-Path $root 'paused-process.json'))
            # Include the bounded OCI preflight replay, each reached command's
            # work and fresh reap budget, the shared execution deadline, and
            # independent owned cleanup. Assertion bookkeeping is not timed.
            $sourceReplaySeconds = 90
            $freshReapSeconds = 5
            $commandCount = if ($Scenario -ceq 'create-timeout') { 1 } else { 5 }
            $executionSeconds = if ($Scenario -ceq 'create-timeout') { 0 } else { $parameters.ExecutionTimeoutSeconds }
            $invocationBound = $sourceReplaySeconds +
                $commandCount * ($parameters.DockerCommandTimeoutSeconds + $freshReapSeconds) +
                $executionSeconds + $parameters.CleanupTimeoutSeconds
            $diagnostic = 'Runtime command fixture mismatch; scenario=' + $Scenario +
                '; elapsedSeconds=' + $watch.Elapsed.TotalSeconds.ToString('F3', [Globalization.CultureInfo]::InvariantCulture) +
                '; maximumSeconds=' + $invocationBound + '; latch=' + $latched +
                '; targetCommandCount=' + $targetRecords.Count + '; expectedStage=' + $expectedStage +
                '; controlledFailureStage=' + $controlledStage + '.'
            Assert-RuntimeContract ($latched -and $targetRecords.Count -eq 1 -and $controlledStage -ceq $expectedStage -and
                $watch.Elapsed.TotalSeconds -lt $invocationBound) $diagnostic
            Assert-RuntimeCommandReaped $root
        }
        if ($Scenario -ceq 'wait-timeout') {
            $stageMatch = [regex]::Match($caught.Exception.Message,
                '\ANative image execution failed; stage=(?<stage>[a-z-]+); type=[A-Za-z0-9_.+`]+; line=[0-9]+\.\z')
            $controlledStage = if ($stageMatch.Success) { $stageMatch.Groups['stage'].Value } else { 'unclassified' }
            # OCI replay, create/inspect/start, the explicit five-second
            # execution deadline, logs and independent cleanup are additive.
            $invocationBound = 90 + 3 * ($parameters.DockerCommandTimeoutSeconds + 5) +
                $parameters.ExecutionTimeoutSeconds + ($parameters.DockerCommandTimeoutSeconds + 5) +
                $parameters.CleanupTimeoutSeconds
            $diagnostic = 'Runtime execution deadline mismatch; scenario=' + $Scenario +
                '; elapsedSeconds=' + $watch.Elapsed.TotalSeconds.ToString('F3', [Globalization.CultureInfo]::InvariantCulture) +
                '; maximumSeconds=' + $invocationBound + '; expectedStage=runtime-execution' +
                '; controlledFailureStage=' + $controlledStage + '.'
            Assert-RuntimeContract ($watch.Elapsed.TotalSeconds -ge 4 -and $watch.Elapsed.TotalSeconds -lt $invocationBound -and
                $controlledStage -ceq 'runtime-execution') $diagnostic
        }
    }
    $script:cases++
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $loadResults = @(. $helper)
    Assert-RuntimeContract ($loadResults.Count -eq 0 -and [IO.Directory]::GetFileSystemEntries($temporaryRoot).Length -eq 0) 'Runtime helper load changed settings or wrote output.'
    $cases++
    $invalidWrapperRoot = Join-Path $temporaryRoot 'invalid-wrapper'
    $wrapperCommand = Resolve-NativeImageVerificationCommand (Join-Path $PSScriptRoot 'verify-native-image.ps1')
    $wrapperResults = @(Invoke-NativeImageVerificationDocker -Command $wrapperCommand -Arguments @(
        '-Image', '-', '-Service', 'admin', '-ArtifactDirectory', $invalidWrapperRoot
    ) -TimeoutSeconds 10)
    $wrapperEvidence = if ([IO.Directory]::Exists($invalidWrapperRoot)) {
        @(([IO.DirectoryInfo]::new($invalidWrapperRoot)).EnumerateFiles('evidence.json', [IO.SearchOption]::AllDirectories))
    } else { @() }
    Assert-RuntimeContract ($wrapperResults.Count -eq 1 -and $wrapperResults[0].exitCode -ne 0 -and
        [string]::IsNullOrWhiteSpace($wrapperResults[0].stdout) -and $wrapperEvidence.Count -eq 0) 'Invalid outer wrapper input emitted successful runtime evidence or exited successfully.'
    $cases++
    # Reuse only trusted, committed fixture definitions; never execute test85's main.
    $tokens = $null; $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($fixtureSource, [ref]$tokens, [ref]$parseErrors)
    Assert-RuntimeContract ($parseErrors.Count -eq 0) 'Committed packaging fixture source did not parse.'
    foreach ($name in @('Assert-VerificationContract', 'Write-VerificationJson', 'Read-VerificationJson', 'Get-VerificationHash', 'Write-VerificationArchive', 'Write-VerificationOciArchive', 'New-VerificationFixture')) {
        $definitions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true))
        Assert-RuntimeContract ($definitions.Count -eq 1) 'Committed packaging fixture definition was ambiguous.'
        . ([ScriptBlock]::Create($definitions[0].Extent.Text))
    }
    $script:fakeDocker = Join-Path $temporaryRoot 'fake runtime Docker café 雪.ps1'
    @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]] $DockerArguments)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$root = $env:CONDUIT_IMAGE_RUNTIME_TEST_ROOT; $scenario = $env:CONDUIT_IMAGE_RUNTIME_TEST_SCENARIO; $phase = $env:CONDUIT_IMAGE_RUNTIME_TEST_PHASE
$diagnostic = $env:CONDUIT_IMAGE_RUNTIME_TEST_DIAGNOSTIC
$otherImageId = 'sha256:' + ('b' * 64); $id = 'c' * 64
$active = Join-Path $root 'active.json'; $removed = Join-Path $root 'removed.json'
function Read-Json([string] $Path) { return [IO.File]::ReadAllText($Path) | ConvertFrom-Json -AsHashtable -DateKind String }
function Write-Json([string] $Path, $Value) { [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 32), [Text.UTF8Encoding]::new($false)) }
$imageId = (Read-Json (Join-Path $root 'image.json')).Id
function Pause-Command {
    $process = [Diagnostics.Process]::GetCurrentProcess()
    try {
        if ($IsLinux) { $stat = [IO.File]::ReadAllText('/proc/' + $process.Id + '/stat'); $position = $stat.LastIndexOf(') ', [StringComparison]::Ordinal); $fields = $stat.Substring($position + 2).Split(' ', [StringSplitOptions]::RemoveEmptyEntries); $token = 'linux:' + $fields[19] }
        else { $token = 'windows:' + $process.StartTime.ToUniversalTime().Ticks.ToString([Globalization.CultureInfo]::InvariantCulture) }
        Write-Json (Join-Path $root 'paused-process.json') @{ id = $process.Id; token = $token }
    }
    finally { $process.Dispose() }
}
function Missing([string] $Identity) { [Console]::Error.WriteLine('Error response from daemon: No such container: ' + $Identity); exit 1 }
$ordinal = [IO.Directory]::GetFiles((Join-Path $root 'commands')).Length + 1
Write-Json (Join-Path $root ('commands/' + $ordinal.ToString('D3') + '.json')) @{ ordinal = $ordinal; arguments = @($DockerArguments); reportPresent = [IO.File]::Exists((Join-Path $root 'runtime/evidence.json')) }
if ($DockerArguments[0] -ceq 'image' -and $DockerArguments[1] -ceq 'inspect' -and $phase -ceq 'packaging') {
    if ($DockerArguments.Count -ne 5 -or ($DockerArguments[2..3] -join '|') -cne '--format|{{json .}}') { throw 'Invalid packaging image inspect.' }
    [Console]::Out.WriteLine((ConvertTo-Json -InputObject (Read-Json (Join-Path $root 'image.json')) -Depth 32 -Compress)); exit 0
}
if ($DockerArguments[0] -ceq 'image' -and $DockerArguments[1] -ceq 'save' -and $phase -ceq 'packaging') {
    if ($DockerArguments.Count -ne 5 -or $DockerArguments[2] -cne '--output' -or $DockerArguments[-1] -cne $imageId) { throw 'Golden OCI save did not pin the immutable ID without platform filtering.' }
    [IO.File]::Copy((Join-Path $root 'fixture-save.tar'), $DockerArguments[3], $false); exit 0
}
if ($DockerArguments[0] -cne 'container') { throw 'Unexpected runtime fixture command.' }
switch ($DockerArguments[1]) {
    'create' {
        $nameIndex = [Array]::IndexOf($DockerArguments, '--name'); $networkIndex = [Array]::IndexOf($DockerArguments, '--network')
        $platform = [Array]::IndexOf($DockerArguments, '--platform')
        if ($nameIndex -lt 0 -or $networkIndex -lt 0 -or $platform -lt 0 -or $DockerArguments[$platform + 1] -cne 'linux/amd64' -or
            $DockerArguments[$networkIndex + 1] -cne 'none' -or $DockerArguments[-1] -cne $imageId) { throw 'Runtime create did not pin private immutable identity and platform.' }
        $name = $DockerArguments[$nameIndex + 1]; $labels = @{}; $environment = @()
        for ($index = 0; $index -lt $DockerArguments.Length; $index++) {
            if ($DockerArguments[$index] -ceq '--label') { $parts = $DockerArguments[$index + 1].Split('=', 2); $labels[$parts[0]] = $parts[1] }
            if ($DockerArguments[$index] -ceq '--env') { $environment += $DockerArguments[$index + 1] }
        }
        if ($phase -ceq 'runtime') {
            if ((@($environment | Sort-Object) -join '|') -cne 'CONDUIT_OPENAPI_GENERATION=true|CONDUIT_OPENAPI_OUTPUT=/tmp/packaged-openapi.json' -or
                $labels['conduit.fixture'] -cne 'native-image-entrypoint' -or $name -cnotmatch '\Aconduit-native-image-entrypoint-(admin|gateway)-[0-9a-f]{32}\z') { throw 'Invalid runtime platform, flags or fixture identity.' }
        }
        else { if ($labels['conduit.fixture'] -cne 'native-image-verification') { throw 'Invalid packaging fixture identity.' } }
        $configuration = (Read-Json (Join-Path $root 'image.json')).Config
        foreach ($label in $labels.Keys) { $configuration.Labels[$label] = $labels[$label] }
        $configuration.Env = @($configuration.Env) + @($environment)
        $state = @{ lookupName = $name; started = $false; waited = $false; container = @{ Id = $id; Name = '/' + $name; Image = $imageId
            ImageManifestDescriptor = Read-Json (Join-Path $root 'selected-descriptor.json')
            Config = $configuration; HostConfig = @{ NetworkMode = 'none'; PortBindings = $null }
            State = @{ Running = $false; Status = 'created'; ExitCode = 0 } } }
        switch ($scenario) {
            'wrong-owner' { $state.container.Config.Labels['conduit.proof.owner'] = 'd' * 32 }
            'wrong-name' { $state.container.Name = '/foreign-runtime' }
            'wrong-image' { $state.container.Image = $otherImageId }
            'foreign-running' { $state.container.Config.Labels['conduit.proof.owner'] = 'd' * 32; $state.container.State.Running = $true; $state.container.State.Status = 'running' }
            'runtime-wrong-manifest' { $state.container.ImageManifestDescriptor.digest = 'sha256:' + ('d' * 64) }
        }
        Write-Json $active $state
        if ($scenario -ceq 'create-nonzero') { [Console]::Error.WriteLine($diagnostic); exit 17 }
        if ($scenario -ceq 'create-malformed') { [Console]::Out.WriteLine('malformed-runtime-id'); exit 0 }
        if ($scenario -ceq 'create-timeout') { Pause-Command; Start-Sleep -Seconds 60; exit 0 }
        [Console]::Out.WriteLine($id); exit 0
    }
    'inspect' {
        if ($DockerArguments.Count -ne 5 -or ($DockerArguments[2..3] -join '|') -cne '--format|{{json .}}') { throw 'Invalid owned inspect arguments.' }
        $lookup = $DockerArguments[-1]
        $state = if ([IO.File]::Exists($active)) { Read-Json $active } elseif ($scenario -ceq 'absence-failure' -and [IO.File]::Exists($removed)) { Read-Json $removed } else { Missing $lookup }
        if ($lookup -cne $id -and $lookup -cne $state.lookupName) { Missing $lookup }
        if ($phase -ceq 'runtime' -and $state.started -and $state.container.State.Running -and
            $scenario -cnotin @('wait-timeout', 'start-nonzero', 'stop-failure', 'stop-still-running') -and
            -not ($scenario -ceq 'running-after-wait' -and $state.waited)) {
            $state.container.State.Running = $false; $state.container.State.Status = 'exited'; Write-Json $active $state
        }
        [Console]::Out.WriteLine((ConvertTo-Json -InputObject $state.container -Depth 32 -Compress)); exit 0
    }
    'export' {
        if ($phase -cne 'packaging' -or $DockerArguments.Count -ne 5 -or $DockerArguments[2] -cne '--output' -or $DockerArguments[-1] -cne $id) { throw 'Invalid golden packaging export.' }
        [IO.File]::Copy((Join-Path $root 'fixture.tar'), $DockerArguments[3], $false); exit 0
    }
    'start' {
        if ($DockerArguments.Count -ne 3 -or $DockerArguments[-1] -cne $id) { throw 'Invalid runtime start identity.' }
        $state = Read-Json $active; $state.started = $true; $state.container.State.Running = $true; $state.container.State.Status = 'running'; Write-Json $active $state
        if ($scenario -cin @('start-nonzero', 'stop-failure', 'stop-still-running')) { [Console]::Error.WriteLine($diagnostic); exit 17 }
        [Console]::Out.WriteLine($id); exit 0
    }
    'wait' {
        if ($DockerArguments.Count -ne 3 -or $DockerArguments[-1] -cne $id) { throw 'Invalid runtime wait identity.' }
        $state = Read-Json $active; $state.waited = $true
        if ($scenario -ceq 'running-after-wait') { $state.container.State.Running = $true; $state.container.State.Status = 'running' }
        else { $state.container.State.Running = $false; $state.container.State.Status = 'exited' }
        $state.container.State.ExitCode = $(if ($scenario -ceq 'exit-mismatch') { 9 } elseif ($scenario -ceq 'wait-nonzero') { 4 } else { 0 })
        Write-Json $active $state; [Console]::Out.WriteLine($(if ($scenario -ceq 'wait-nonzero') { '4' } else { '0' })); exit 0
    }
    'cp' {
        if ($DockerArguments.Count -ne 4 -or $DockerArguments[2] -cne ($id + ':/tmp/packaged-openapi.json')) { throw 'Invalid runtime copied resource identity.' }
        if ($scenario -ceq 'cp-nonzero') { [Console]::Error.WriteLine($diagnostic); exit 17 }
        if ($scenario -ceq 'cp-timeout') { Pause-Command; Start-Sleep -Seconds 60; exit 0 }
        if ($scenario -ceq 'cp-directory') { [IO.Directory]::CreateDirectory($DockerArguments[3]) | Out-Null; exit 0 }
        if ($scenario -ceq 'runtime-manifest-tamper') {
            [IO.File]::AppendAllText((Join-Path $root 'packaging/extracted/conduit-evidence/application.json'), ' ')
        }
        if ($scenario -ceq 'runtime-payload-tamper') {
            $state = Read-Json $active; $main = [IO.Path]::GetFileName($state.container.Config.Entrypoint[0])
            $path = Join-Path $root ('packaging/extracted/app/' + $main)
            $bytes = [IO.File]::ReadAllBytes($path); $bytes[0] = $bytes[0] -bxor 1; [IO.File]::WriteAllBytes($path, $bytes)
        }
        if ($scenario -ceq 'runtime-oci-report-tamper') { [IO.File]::AppendAllText((Join-Path $root 'packaging/oci-association.json'), ' ') }
        if ($scenario -ceq 'runtime-oci-archive-tamper') {
            $path = Join-Path $root 'packaging/image-save.tar'; $stream = [IO.File]::Open($path, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $tail = [byte[]]::new(512); [Array]::Fill[byte]($tail, [byte]1)
            try { $stream.Write($tail, 0, $tail.Length) } finally { $stream.Dispose() }
        }
        $text = '{"openapi":"3.0.1","info":{"title":"Owned native runtime","version":"8.1.0"},"paths":{"/health/live":{"get":{"responses":{"200":{"description":"ok"}}}},"/proof":{"get":{"responses":{"200":{"description":"café 雪 🔎"}}}}},"components":{"schemas":{"Price":{"type":"number","minimum":0.125,"maximum":1.5,"description":"café 雪 🔎","example":{"nested":[0.25,{"amount":2.75}]}}}}}'
        if ($scenario -ceq 'openapi-nonjson') { $text = '{' }
        if ($scenario -ceq 'openapi-duplicate') { $text = $text.Replace('"minimum":0.125', '"minimum":0.125,"minimum":0.5') }
        [IO.File]::WriteAllText($DockerArguments[3], $text, [Text.UTF8Encoding]::new($false)); exit 0
    }
    'logs' {
        if ($DockerArguments.Count -ne 3 -or $DockerArguments[-1] -cne $id) { throw 'Invalid runtime logs identity.' }
        if ($scenario -ceq 'logs-overflow') { Pause-Command; [Console]::Out.Write([string]::new([char]120, 4194305)); Start-Sleep -Seconds 60; exit 0 }
        [Console]::Out.WriteLine('Owned native runtime completed.'); exit 0
    }
    'stop' {
        if ($DockerArguments.Count -ne 5 -or $DockerArguments[2] -cne '--timeout' -or $DockerArguments[-1] -cne $id -or [int]$DockerArguments[3] -lt 0 -or [int]$DockerArguments[3] -gt 3) { throw 'Invalid bounded runtime stop identity.' }
        if ($scenario -ceq 'stop-failure') { [Console]::Error.WriteLine($diagnostic); exit 17 }
        $state = Read-Json $active
        if ($scenario -cne 'stop-still-running') { $state.container.State.Running = $false; $state.container.State.Status = 'exited'; $state.container.State.ExitCode = 137; Write-Json $active $state }
        [Console]::Out.WriteLine($id); exit 0
    }
    'rm' {
        if ($DockerArguments.Count -ne 3 -or $DockerArguments[-1] -cne $id) { throw 'Invalid or forced runtime removal.' }
        $state = Read-Json $active
        if ($state.container.State.Running) { throw 'Fixture rejected removal of a running container.' }
        if ($scenario -ceq 'rm-failure') { [Console]::Error.WriteLine($diagnostic); exit 17 }
        [IO.File]::Copy($active, $removed, $false); [IO.File]::Delete($active); [Console]::Out.WriteLine($id); exit 0
    }
    default { throw 'Unexpected runtime fixture operation.' }
}
'@ | Set-Content -LiteralPath $script:fakeDocker -Encoding utf8NoBOM

    foreach ($golden in @(@{ service = 'admin'; scenario = 'success' }, @{ service = 'gateway'; scenario = 'success' },
        @{ service = 'admin'; scenario = 'oci-hold-no-descriptors' }, @{ service = 'admin'; scenario = 'oci-hold-classic' })) {
        $fixture = New-VerificationFixture $golden.service $golden.scenario
        Set-RuntimeFixtureEnvironment $fixture.root $golden.scenario 'packaging'
        $packaged = @(Get-NativeImagePackagingEvidence -Image $fixture.requestedImage -Service $golden.service -ArtifactDirectory $fixture.artifact -DockerCommandPath $script:fakeDocker -DockerCommandTimeoutSeconds 5 -CleanupTimeoutSeconds 15)
        Assert-RuntimeContract ($packaged.Count -eq 1 -and $packaged[0].cleanupConfirmed -and -not [IO.File]::Exists((Join-Path $fixture.root 'active.json'))) 'Standalone completed packaging fixture failed.'
        $key = if ($golden.scenario -ceq 'success') { $golden.service } else { $golden.service + '-' + $golden.scenario }
        $goldens[$key] = $fixture
    }
    Invoke-RuntimeCase 'admin' 'success' $true
    Invoke-RuntimeCase 'gateway' 'success' $true
    Invoke-RuntimeCase 'admin' 'historical-schema1' $true
    Invoke-RuntimeCase 'admin' 'oci-hold-no-descriptors' $true
    Invoke-RuntimeCase 'admin' 'oci-hold-classic' $true
    foreach ($scenario in @('package-report-tamper', 'manifest-tamper', 'payload-tamper', 'runtime-manifest-tamper', 'runtime-payload-tamper',
        'create-nonzero', 'create-malformed', 'create-timeout', 'start-nonzero', 'wait-nonzero',
        'exit-mismatch', 'running-after-wait', 'wait-timeout', 'cp-nonzero', 'cp-timeout', 'cp-directory',
        'openapi-nonjson', 'openapi-duplicate', 'logs-overflow', 'wrong-owner', 'wrong-name', 'wrong-image',
        'foreign-running', 'stop-failure', 'stop-still-running', 'rm-failure', 'absence-failure',
        'fresh-visible', 'fresh-hidden', 'fresh-claim', 'invalid-execution-timeout', 'invalid-cleanup-timeout',
        'invalid-docker-timeout', 'nested-artifact', 'oci-report-tamper', 'oci-observations-tamper', 'oci-archive-tamper',
        'oci-reference-path', 'runtime-wrong-manifest', 'runtime-oci-report-tamper', 'runtime-oci-archive-tamper')) {
        Invoke-RuntimeCase 'admin' $scenario $false
    }
}
catch { $failure = $_ }
finally {
    foreach ($name in $environmentNames) {
        if ($null -eq $oldEnvironment[$name]) { [Environment]::SetEnvironmentVariable($name, [NullString]::Value) }
        else { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name]) }
        if ([Environment]::GetEnvironmentVariable($name) -cne $oldEnvironment[$name]) { $cleanupFailure = $true }
    }
    try {
        $resolved = [IO.Path]::GetFullPath($temporaryRoot)
        Assert-RuntimeContract ([IO.Path]::GetDirectoryName($resolved) -ceq $temporaryParent -and
            [IO.Path]::GetFileName($resolved) -cmatch '^conduit-image-runtime-contract-[0-9a-f]{32}$') 'Runtime fixture cleanup target was rejected.'
        foreach ($directory in @(([IO.DirectoryInfo]::new($resolved)).EnumerateDirectories('case-*'))) { Assert-RuntimeCommandReaped $directory.FullName -Stop }
        if ([IO.Directory]::Exists($resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
        Assert-RuntimeContract (-not [IO.Directory]::Exists($resolved)) 'Runtime fixture owned directory remained after cleanup.'
    }
    catch { $cleanupFailure = $true }
}
if ($cleanupFailure) { throw 'Native image runtime contracts could not restore their environment or clean owned resources.' }
if ($null -ne $failure) { throw $failure }
Write-Output ('Verified native image runtime evidence contracts; cases=' + $cases + '; skipped=0; cleanupConfirmed=true.')
