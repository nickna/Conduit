#!/usr/bin/env pwsh
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('conduit-publish-contract-' + [Guid]::NewGuid().ToString('N'))))
$publishScript = Join-Path $PSScriptRoot 'publish-native.ps1'
$smokeScript = Join-Path $PSScriptRoot 'native-smoke.ps1'
$cases = 0
$skipped = [Collections.Generic.List[string]]::new()
$ownedLinks = [Collections.Generic.List[string]]::new()
$proofFailure = $null
$cleanupFailure = $null
$originalCulture = [Globalization.CultureInfo]::CurrentCulture
$originalUICulture = [Globalization.CultureInfo]::CurrentUICulture

function Assert-Contract([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Rejected([scriptblock] $Action, [string] $Message) {
    $rejected = $false
    try { & $Action | Out-Null }
    catch { $rejected = $true }
    Assert-Contract $rejected $Message
    $script:cases++
}

function Get-IndependentHash([byte[]] $Bytes) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Get-FileHashValue([string] $Path) {
    return Get-IndependentHash ([IO.File]::ReadAllBytes($Path))
}

function Write-Json([string] $Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
}

function Read-Json([string] $Path) {
    return [IO.File]::ReadAllText($Path) | ConvertFrom-Json -AsHashtable
}

function New-OwnedDirectory([string] $Parent = $temporaryRoot) {
    $path = Join-Path $Parent ([Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($path) | Out-Null
    return $path
}

function Get-ExecutableName([string] $Service) {
    $name = if ($Service -ceq 'probe') { 'ConduitLLM.GatewayNativeAotTests' }
        elseif ($Service -ceq 'admin') { 'ConduitLLM.Admin' } else { 'ConduitLLM.Gateway' }
    if ($IsWindows) { $name += '.exe' }
    return $name
}

function New-RuntimeFixture {
    param([switch] $ReverseCreation)
    $path = New-OwnedDirectory
    $unicodeDirectory = Join-Path $path 'a'
    [IO.Directory]::CreateDirectory($unicodeDirectory) | Out-Null
    $executableName = Get-ExecutableName 'admin'
    $files = @(
        @{ Name = $executableName; Bytes = [Text.Encoding]::UTF8.GetBytes('fixture-application') }
        @{ Name = '.hidden'; Bytes = [byte[]]@(0,1,2) }
        @{ Name = 'a/é雪😀.txt'; Bytes = [Text.Encoding]::UTF8.GetBytes('café 雪') }
        @{ Name = 'z-last.dat'; Bytes = [byte[]]@(255,13,10,0) }
        @{ Name = 'empty.dat'; Bytes = [byte[]]@() }
    )
    if ($ReverseCreation) { [Array]::Reverse($files) }
    foreach ($file in $files) { [IO.File]::WriteAllBytes((Join-Path $path $file.Name), [byte[]]$file.Bytes) }
    if ($IsWindows) {
        [IO.File]::SetAttributes((Join-Path $path '.hidden'), [IO.FileAttributes]::Hidden)
    }
    return [pscustomobject]@{ Path = $path; ExecutableName = $executableName }
}

function New-ManifestArguments($Runtime) {
    $now = [DateTime]::UtcNow
    return @{
        DestinationPath = Join-Path (New-OwnedDirectory) 'manifest.json'
        BuildId = [Guid]::NewGuid().ToString('N')
        Service = 'admin'
        Rid = $(if ($IsWindows) { 'win-x64' } else { 'linux-x64' })
        Source = @{ status = 'unavailable'; revision = $null; dirty = $null }
        Toolchain = @{
            sdkVersion = '10.0.401'
            ilCompilerPackage = @{ id = 'runtime.' + $(if ($IsWindows) { 'win-x64' } else { 'linux-x64' }) + '.Microsoft.DotNet.ILCompiler'; version = '10.0.12' }
            ilCompiler = @{ path = $script:compilerPath; sha256 = Get-FileHashValue $script:compilerPath; bytes = [long](Get-Item -LiteralPath $script:compilerPath).Length }
            nativeLinkerDriver = @{ command = $script:pwshPath; path = $script:pwshPath; sha256 = Get-FileHashValue $script:pwshPath; bytes = [long](Get-Item -LiteralPath $script:pwshPath).Length; version = 'owned fixture driver'; flavor = $null }
            hostOs = $(if ($IsWindows) { 'windows' } else { 'linux' })
            hostArchitecture = 'x64'
            targetOs = $(if ($IsWindows) { 'windows' } else { 'linux' })
            targetArchitecture = 'x64'
        }
        PublishProperties = @{ configuration = 'Release'; publishAot = $true; selfContained = $true; stripSymbols = $true; conduitAotAudit = $true; useSharedCompilation = $false }
        StartedAtUtc = $now.AddSeconds(-1)
        CompletedAtUtc = $now
        RuntimeDirectory = $Runtime.Path
        ExecutableName = $Runtime.ExecutableName
    }
}

function Get-CanonicalFileBytes($Files) {
    # An independent protocol writer is used only to construct deliberately
    # malformed manifests whose aggregate digest is otherwise internally valid.
    $stream = [IO.MemoryStream]::new()
    $writer = [Text.Json.Utf8JsonWriter]::new($stream)
    try {
        $writer.WriteStartArray()
        foreach ($file in $Files) {
            $writer.WriteStartObject()
            $writer.WriteString('path', [string]$file.path)
            $writer.WriteNumber('bytes', [long]$file.bytes)
            $writer.WriteString('sha256', [string]$file.sha256)
            $writer.WriteEndObject()
        }
        $writer.WriteEndArray()
        $writer.Flush()
        return ,$stream.ToArray()
    }
    finally { $writer.Dispose(); $stream.Dispose() }
}

function Invoke-PrivateChild([string] $Script, [string[]] $Arguments = @(), [hashtable] $Environment = @{}, [int] $TimeoutSeconds = 45) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $script:pwshPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in @('-NoProfile', '-File', $Script) + $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($name in $Environment.Keys) { $start.Environment[$name] = [string]$Environment[$name] }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            Assert-Contract ($process.WaitForExit(5000)) 'Owned publisher contract child did not reap.'
            throw 'Owned publisher contract child exceeded its deadline.'
        }
        Assert-Contract ([Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000)) 'Owned publisher contract pipes did not close.'
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout.Result; Stderr = $stderr.Result }
    }
    finally {
        if (-not $process.HasExited) {
            $process.Kill($true)
            Assert-Contract ($process.WaitForExit(5000)) 'Owned publisher contract child cleanup did not reap.'
        }
        $process.Dispose()
    }
}

function Get-CommandRecords([string] $CaseRoot) {
    $path = Join-Path $CaseRoot 'commands'
    if (-not [IO.Directory]::Exists($path)) { return ,@() }
    return ,@(Get-ChildItem -LiteralPath $path -File | ForEach-Object { Read-Json $_.FullName })
}

function Get-SafePublisherDiagnostic($Result) {
    $captured = @([string]$Result.Stdout, [string]$Result.Stderr) -join [Environment]::NewLine
    $safe = @($captured -split '\r?\n' | Where-Object {
        $_ -cmatch '^Owned publisher fixture failed; type=[A-Za-z0-9_.+]+; line=[0-9]+\.$' -or
        $_ -cmatch '^Owned fake dotnet failed; type=[A-Za-z0-9_.+]+; line=[0-9]+\.$' -or
        $_ -ceq 'Owned publisher fixture failed.' -or $_ -ceq 'Owned publish failure.' -or $_ -ceq 'Owned fake dotnet failed.'
    })
    if ($safe.Count -eq 0) { return 'no controlled child diagnostic' }
    return $safe -join ' | '
}

function Invoke-PublisherCase([string] $Scenario, [bool] $ExpectSuccess, [string] $NonemptyService = '') {
    $caseRoot = New-OwnedDirectory
    $artifactRoot = Join-Path $caseRoot 'artifact'
    [IO.Directory]::CreateDirectory((Join-Path $caseRoot 'commands')) | Out-Null
    $linkedTarget = $null
    if ($Scenario -cin @('linked-output', 'linked-publish-directory', 'linked-symbol-directory', 'linked-log-leaf',
        'linked-symbol-leaf', 'linked-native-intermediates', 'linked-native-outputs')) {
        $linkedTarget = Join-Path $caseRoot 'external-output-sentinel'
        [IO.Directory]::CreateDirectory($linkedTarget) | Out-Null
        [IO.File]::WriteAllText((Join-Path $linkedTarget 'sentinel'), 'do not mutate external output')
        $linkedEntry = switch ($Scenario) {
            'linked-output' { $artifactRoot }
            'linked-publish-directory' { Join-Path $artifactRoot 'reports/publish' }
            'linked-symbol-directory' { Join-Path $artifactRoot 'symbols/admin' }
            'linked-log-leaf' { Join-Path $artifactRoot 'reports/admin-publish.log' }
            'linked-symbol-leaf' { Join-Path $artifactRoot 'symbols/admin/ConduitLLM.Admin.dbg' }
            'linked-native-intermediates' { Join-Path $artifactRoot 'native-intermediates' }
            'linked-native-outputs' { Join-Path $artifactRoot 'native-outputs' }
        }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($linkedEntry)) | Out-Null
        $fileLink = $Scenario -cin @('linked-log-leaf', 'linked-symbol-leaf')
        try {
            if ($fileLink) { [IO.File]::CreateSymbolicLink($linkedEntry, (Join-Path $linkedTarget 'sentinel')) | Out-Null }
            elseif ($IsWindows) { New-Item -ItemType Junction -Path $linkedEntry -Target $linkedTarget | Out-Null }
            else { [IO.Directory]::CreateSymbolicLink($linkedEntry, $linkedTarget) | Out-Null }
        }
        catch {
            if ($IsWindows -and $fileLink) {
                $script:skipped.Add('Windows file symbolic-link setup unavailable; publisher ' + $Scenario + ' rejection not claimed.')
                return
            }
            throw 'Mandatory owned publisher link fixture could not be created.'
        }
        $script:ownedLinks.Add($linkedEntry)
    }
    if (-not [string]::IsNullOrEmpty($NonemptyService)) {
        $runtime = Join-Path $artifactRoot ('runtime/' + $NonemptyService)
        [IO.Directory]::CreateDirectory($runtime) | Out-Null
        [IO.File]::WriteAllText((Join-Path $runtime 'stale.dat'), 'must not overwrite')
    }
    if ($Scenario -ceq 'prior-baseline') {
        [IO.Directory]::CreateDirectory((Join-Path $artifactRoot 'reports')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $artifactRoot 'reports/native-baselines.json'), 'old baseline sentinel')
    }
    if ($Scenario -ceq 'existing-claim') {
        [IO.Directory]::CreateDirectory((Join-Path $artifactRoot 'reports')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $artifactRoot 'reports/native-publish.claim'), 'existing claim sentinel')
    }
    $environment = @{
        CONDUIT_PUBLISH_EVIDENCE_TEST_ROOT = $caseRoot
        CONDUIT_PUBLISH_EVIDENCE_TEST_SCENARIO = $Scenario
        CONDUIT_PUBLISH_EVIDENCE_TEST_DOTNET = $script:fakeDotnetPath
        CONDUIT_PUBLISH_EVIDENCE_TEST_PUBLISHER = $publishScript
        CONDUIT_PUBLISH_EVIDENCE_TEST_TOOLROOT = $script:toolRoot
        CONDUIT_PUBLISH_EVIDENCE_TEST_DRIVER = $script:pwshPath
    }
    $result = Invoke-PrivateChild $script:publisherWrapper @('-ArtifactDirectory', $artifactRoot,
        '-Runtime', $(if ($IsWindows) { 'win-x64' } else { 'linux-x64' }), '-WarningBaselinePath', $script:warningBaselinePath) $environment
    $records = Get-CommandRecords $caseRoot
    $baselinePath = Join-Path $artifactRoot 'reports/native-baselines.json'
    $manifestRoot = Join-Path $artifactRoot 'reports/publish'
    $manifests = if ([IO.Directory]::Exists($manifestRoot)) { @(Get-ChildItem -LiteralPath $manifestRoot -Recurse -File -Filter '*.json') } else { @() }
    if ($ExpectSuccess) {
        $successFailureMessage = 'Successful fake publisher did not emit a baseline; scenario=' + $Scenario + '; exit=' + $result.ExitCode + '; child=' + (Get-SafePublisherDiagnostic $result)
        Assert-Contract ($result.ExitCode -eq 0 -and [IO.File]::Exists($baselinePath)) $successFailureMessage
        Assert-Contract ($manifests.Count -eq 3) 'Successful fake publisher did not create exactly three immutable manifests.'
        Assert-Contract (@($records | Where-Object { $_.arguments[0] -ceq 'publish' }).Count -eq 3) 'Successful fake publisher did not execute all three publishes.'
        $baseline = Read-Json $baselinePath
        Assert-Contract ($baseline.schemaVersion -is [long] -or $baseline.schemaVersion -is [int]) 'Publish baseline schema is not numeric.'
        Assert-Contract ($baseline.schemaVersion -eq 2 -and $baseline.services.Count -eq 2) 'Publish baseline schema/services are invalid.'
        Assert-Contract ([IO.File]::Exists((Join-Path $artifactRoot 'reports/native-publish.claim'))) 'Successful publisher did not retain its exclusive claim.'
        $references = @($baseline.services[0].publishManifest, $baseline.services[1].publishManifest, $baseline.probeManifest)
        $buildIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $intermediateDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $nativeOutputDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($reference in $references) {
            Assert-Contract ($reference.Keys.Count -eq 3 -and $reference.path -is [string] -and
                $reference.path -cmatch '^reports/publish/[0-9a-f]{32}/(?:admin|gateway|probe)\.json$') 'Publish baseline reference is not shallow or safely relative.'
            $manifestPath = Join-Path $artifactRoot $reference.path
            Assert-Contract ([IO.File]::Exists($manifestPath)) 'Publish baseline reference does not exist.'
            Assert-Contract ($reference.sha256 -ceq (Get-FileHashValue $manifestPath)) 'Publish baseline reference does not match immutable manifest bytes.'
            $manifest = Read-Json $manifestPath
            Assert-Contract ($reference.buildId -ceq $manifest.buildId) 'Publish baseline reference build identity differs.'
            $buildIds.Add([string]$manifest.buildId) | Out-Null
            Assert-Contract ([IO.Path]::GetDirectoryName($manifestPath) -ceq (Join-Path $artifactRoot ('reports/publish/' + $manifest.buildId))) 'Manifest is outside its unique publish directory.'
            $service = [string]$manifest.service
            $runtimeDirectory = Join-Path $artifactRoot ('runtime/' + $service)
            Assert-NativePublishManifestMatchesRuntime -ManifestPath $manifestPath -RuntimeDirectory $runtimeDirectory | Out-Null
            $nativeRecord = @($records | Where-Object { $_.service -ceq $service })
            Assert-Contract ($nativeRecord.Count -eq 1 -and $nativeRecord[0].nativePaths.Count -eq 4) 'Fake publisher did not record the four selected native compile/link paths.'
            $nativePaths = $nativeRecord[0].nativePaths
            $trimSeparators = [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
            $expectedIntermediate = Join-Path $artifactRoot ('native-intermediates/' + $manifest.buildId + '/' + $service)
            $expectedNativeOutput = Join-Path $artifactRoot ('native-outputs/' + $manifest.buildId + '/' + $service)
            Assert-Contract ([IO.Path]::GetFullPath($nativePaths.NativeIntermediateOutputPath).TrimEnd($trimSeparators) -ceq $expectedIntermediate -and
                [IO.Path]::GetFullPath($nativePaths.NativeOutputPath).TrimEnd($trimSeparators) -ceq $expectedNativeOutput) 'Selected native service directories do not belong to the invocation build GUID.'
            $assemblyName = if ($service -ceq 'probe') { 'ConduitLLM.GatewayNativeAotTests' } elseif ($service -ceq 'admin') { 'ConduitLLM.Admin' } else { 'ConduitLLM.Gateway' }
            $expectedObject = Join-Path $expectedIntermediate ($assemblyName + $(if ($IsWindows) { '.obj' } else { '.o' }))
            $expectedBinary = Join-Path $expectedNativeOutput (Get-ExecutableName $service)
            Assert-Contract ($nativePaths.NativeObject -ceq $expectedObject -and $nativePaths.NativeBinary -ceq $expectedBinary -and
                [IO.File]::Exists($expectedObject) -and (Get-Item -LiteralPath $expectedObject).Length -gt 0 -and
                (Get-FileHashValue $expectedBinary) -ceq (Get-FileHashValue (Join-Path $runtimeDirectory (Get-ExecutableName $service)))) 'Fresh native object/binary paths or copied runtime bytes are not independently verifiable.'
            $intermediateDirectories.Add($expectedIntermediate) | Out-Null
            $nativeOutputDirectories.Add($expectedNativeOutput) | Out-Null
            Assert-Contract ($manifest.toolchain.ilCompiler.sha256 -ceq (Get-FileHashValue $script:compilerPath)) 'Publisher did not record the actual selected ILC bytes.'
            Assert-Contract ($manifest.toolchain.ilCompilerPackage.version -ceq '10.0.12') 'Publisher did not record the selected nuspec version.'
            Assert-Contract ($manifest.toolchain.nativeLinkerDriver.sha256 -ceq (Get-FileHashValue $script:pwshPath)) 'Publisher did not record the actual selected driver bytes.'
            Assert-Contract ($manifest.publishProperties.conduitAotAudit -eq ($service -cne 'probe')) 'Publisher audit property differs from actual invocation.'
            Assert-Contract ($manifest.source.status -cin @('git', 'unavailable')) 'Publisher invented a source identity status.'
            if ($manifest.source.status -ceq 'unavailable') {
                Assert-Contract ($null -eq $manifest.source.revision -and $null -eq $manifest.source.dirty) 'Unavailable source identity contains fabricated values.'
            }
        }
        Assert-Contract ($buildIds.Count -eq 1 -and $intermediateDirectories.Count -eq 3 -and $nativeOutputDirectories.Count -eq 3) 'Three publishes do not use distinct service directories within one build identity.'
        $claim = [IO.File]::ReadAllText((Join-Path $artifactRoot 'reports/native-publish.claim')).Trim()
        Assert-Contract ($buildIds.Contains($claim)) 'Exclusive claim does not identify the three completed publishes.'
    }
    else {
        Assert-Contract ($result.ExitCode -ne 0) ('Invalid fake publisher case was accepted: ' + $Scenario)
        Assert-Contract ($manifests.Count -eq 0) ('Failed publisher left a success manifest: ' + $Scenario)
        if ($Scenario -ceq 'prior-baseline') {
            Assert-Contract ([IO.File]::ReadAllText($baselinePath) -ceq 'old baseline sentinel') 'Publisher changed an existing baseline.'
        }
        else { Assert-Contract (-not [IO.File]::Exists($baselinePath)) ('Failed publisher left a success baseline: ' + $Scenario) }
        if (-not [string]::IsNullOrEmpty($NonemptyService) -or $Scenario -cin @('prior-baseline', 'existing-claim',
            'linked-output', 'linked-publish-directory', 'linked-symbol-directory', 'linked-log-leaf',
            'linked-native-intermediates', 'linked-native-outputs')) {
            Assert-Contract ($records.Count -eq 0) 'Publisher preflight ran dotnet before rejecting prior evidence/runtime files.'
            if (-not [string]::IsNullOrEmpty($NonemptyService)) {
                Assert-Contract ([IO.File]::ReadAllText((Join-Path $artifactRoot ('runtime/' + $NonemptyService + '/stale.dat'))) -ceq 'must not overwrite') 'Publisher changed a nonempty runtime before rejection.'
            }
        }
        else {
            $expectedPublishes = switch ($Scenario) {
                'fail-admin' { 1 } 'fail-gateway' { 2 } 'fail-probe' { 3 }
                'duplicate-capture' { 1 } 'invalid-base64' { 1 } 'invalid-utf8' { 1 } 'unexpected-capture' { 1 }
                'linked-symbol-leaf' { 1 }
                { $_ -clike 'native-*' } { 1 }
                default { 3 }
            }
            $actualPublishes = @($records | Where-Object { $_.arguments[0] -ceq 'publish' }).Count
            $operationFailureMessage = 'Publisher failure did not reach the intended operation; scenario=' + $Scenario + '; actual=' + $actualPublishes + '; expected=' + $expectedPublishes + '; child=' + (Get-SafePublisherDiagnostic $result)
            Assert-Contract ($actualPublishes -eq $expectedPublishes) $operationFailureMessage
            Assert-Contract ([IO.File]::Exists((Join-Path $artifactRoot 'reports/native-publish.claim'))) 'Failed publisher discarded its exclusive claim.'
            if ($Scenario -ceq 'linked-symbol-leaf') {
                Assert-Contract ($result.Stdout.Contains('Owned fake native publish complete.', [StringComparison]::Ordinal) -and
                    [IO.File]::Exists((Join-Path $artifactRoot ('runtime/admin/' + (Get-ExecutableName 'admin')))) -and
                    [IO.File]::Exists((Join-Path $artifactRoot 'runtime/admin/ConduitLLM.Admin.dbg'))) 'Symbol-destination fixture did not complete admin publish before rejecting the move.'
            }
            if ($Scenario -clike 'native-*') {
                Assert-Contract ($result.Stdout.Contains('Owned fake native publish complete.', [StringComparison]::Ordinal) -and
                    $records[0].nativePaths.Count -eq 4) 'Native freshness rejection did not follow a completed fake compile/link operation.'
            }
        }
        if ($null -ne $linkedTarget) {
            $targetEntries = @(([IO.DirectoryInfo]::new($linkedTarget)).EnumerateFileSystemInfos())
            Assert-Contract ($targetEntries.Count -eq 1 -and $targetEntries[0].Name -ceq 'sentinel' -and
                [IO.File]::ReadAllText((Join-Path $linkedTarget 'sentinel')) -ceq 'do not mutate external output') 'Publisher changed a linked output target before rejecting it.'
        }
    }
    $script:cases++
    return [pscustomobject]@{ CaseRoot = $caseRoot; ArtifactRoot = $artifactRoot; Result = $result; Records = $records }
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $script:pwshPath = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
    $script:toolRoot = New-OwnedDirectory
    $tools = Join-Path $script:toolRoot 'tools'
    [IO.Directory]::CreateDirectory($tools) | Out-Null
    $script:compilerPath = Join-Path $tools $(if ($IsWindows) { 'ilc.exe' } else { 'ilc' })
    [IO.File]::WriteAllText($script:compilerPath, 'owned synthetic ILC fixture', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $script:toolRoot 'compiler.nuspec'),
        '<package><metadata><id>runtime.' + $(if ($IsWindows) { 'win-x64' } else { 'linux-x64' }) + '.Microsoft.DotNet.ILCompiler</id><version>10.0.12</version></metadata></package>')
    . (Join-Path $PSScriptRoot 'native-publish-evidence.ps1')

    $runtime = New-RuntimeFixture
    $identity = Get-NativeRuntimeIdentity -RuntimeDirectory $runtime.Path -ExecutableName $runtime.ExecutableName
    $exeBytes = [Text.Encoding]::UTF8.GetBytes('fixture-application')
    $textBytes = [Text.Encoding]::UTF8.GetBytes('café 雪')
    # Literal expected path order, escaping and property order are independent
    # of filesystem enumeration, culture and the production canonicalizer.
    $golden = '[{"path":".hidden","bytes":3,"sha256":"' + (Get-IndependentHash ([byte[]]@(0,1,2))) + '"},' +
        '{"path":"' + $runtime.ExecutableName + '","bytes":' + $exeBytes.Length + ',"sha256":"' + (Get-IndependentHash $exeBytes) + '"},' +
        '{"path":"a/\u00E9\u96EA\uD83D\uDE00.txt","bytes":' + $textBytes.Length + ',"sha256":"' + (Get-IndependentHash $textBytes) + '"},' +
        '{"path":"empty.dat","bytes":0,"sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"},' +
        '{"path":"z-last.dat","bytes":4,"sha256":"' + (Get-IndependentHash ([byte[]]@(255,13,10,0))) + '"}]'
    $expectedDigest = Get-IndependentHash ([Text.Encoding]::UTF8.GetBytes($golden))
    Assert-Contract ($identity.filesSha256 -ceq $expectedDigest -and $identity.files.Count -eq 5) 'Runtime inventory differs from independent canonical UTF8/digest golden.'
    Assert-Contract ($identity.runtimeArtifactBytes -eq (3 + $exeBytes.Length + $textBytes.Length + 4)) 'Runtime inventory omits hidden or nested bytes.'
    Assert-Contract ($identity.executable.path -ceq $runtime.ExecutableName -and $identity.executable.sha256 -ceq (Get-IndependentHash $exeBytes)) 'Executable identity is not anchored to actual bytes.'
    $cases++
    foreach ($culture in @('tr-TR', 'sv-SE')) {
        [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo($culture)
        [Globalization.CultureInfo]::CurrentUICulture = [Globalization.CultureInfo]::GetCultureInfo($culture)
        $repeated = Get-NativeRuntimeIdentity -RuntimeDirectory $runtime.Path -ExecutableName $runtime.ExecutableName
        Assert-Contract ($repeated.filesSha256 -ceq $expectedDigest) 'Runtime inventory is culture dependent.'
        $cases++
    }
    [Globalization.CultureInfo]::CurrentCulture = $originalCulture
    [Globalization.CultureInfo]::CurrentUICulture = $originalUICulture
    $secondRuntime = New-RuntimeFixture -ReverseCreation
    $secondIdentity = Get-NativeRuntimeIdentity -RuntimeDirectory $secondRuntime.Path -ExecutableName $secondRuntime.ExecutableName
    Assert-Contract ($secondIdentity.filesSha256 -ceq $expectedDigest) 'Runtime digest depends on absolute root or reverse file creation order.'
    $cases++

    $manifestArguments = New-ManifestArguments $runtime
    $reference = Write-NativePublishManifest @manifestArguments
    $manifestBytes = [IO.File]::ReadAllBytes($reference.path)
    Assert-Contract (-not ($manifestBytes.Length -ge 3 -and $manifestBytes[0] -eq 239 -and $manifestBytes[1] -eq 187 -and $manifestBytes[2] -eq 191)) 'Manifest has a UTF8 BOM.'
    Assert-Contract ($reference.sha256 -ceq (Get-IndependentHash $manifestBytes) -and $reference.buildId -ceq $manifestArguments.BuildId) 'Manifest reference is not bound to exact bytes/build.'
    $manifest = Read-Json $reference.path
    Assert-Contract ($manifest.schemaVersion -eq 1 -and $manifest.kind -ceq 'conduit-native-publish' -and $manifest.runtimeFilesSha256 -ceq $expectedDigest) 'Manifest envelope/digest is invalid.'
    Assert-NativePublishManifestMatchesRuntime -ManifestPath $reference.path -RuntimeDirectory $runtime.Path | Out-Null
    $cases++
    $multilineVersion = "owned clang version 19.1.7`nTarget: x86_64-unknown-linux-gnu`nInstalledDir: /owned/toolchain"
    $multilineArguments = New-ManifestArguments $runtime
    $multilineArguments.Toolchain.nativeLinkerDriver.version = $multilineVersion
    $multilineReference = Write-NativePublishManifest @multilineArguments
    $multilineManifest = Read-Json $multilineReference.path
    Assert-Contract ($multilineManifest.toolchain.nativeLinkerDriver.version -ceq $multilineVersion) 'Multiline resolved linker version was normalized or truncated.'
    Assert-NativePublishManifestMatchesRuntime -ManifestPath $multilineReference.path -RuntimeDirectory $runtime.Path | Out-Null
    $cases++
    Assert-Rejected { Write-NativePublishManifest @manifestArguments } 'Manifest CreateNew overwrote existing immutable evidence.'
    Assert-Contract ((Get-FileHashValue $reference.path) -ceq $reference.sha256) 'Rejected overwrite changed manifest bytes.'
    $insideArguments = New-ManifestArguments $runtime
    $insideArguments.DestinationPath = Join-Path $runtime.Path 'forbidden-manifest.json'
    Assert-Rejected { Write-NativePublishManifest @insideArguments } 'Manifest was accepted inside the runtime tree.'
    Assert-Contract (-not [IO.File]::Exists($insideArguments.DestinationPath)) 'Rejected in-runtime manifest left a new file.'

    foreach ($mutation in @('byte-edit', 'rename', 'add', 'remove')) {
        $mutated = New-RuntimeFixture
        $arguments = New-ManifestArguments $mutated
        $original = Write-NativePublishManifest @arguments
        $path = Join-Path $mutated.Path 'z-last.dat'
        $originalWriteTime = [IO.File]::GetLastWriteTimeUtc($path)
        switch ($mutation) {
            'byte-edit' {
                [IO.File]::WriteAllBytes($path, [byte[]]@(255,13,10,1))
                [IO.File]::SetLastWriteTimeUtc($path, $originalWriteTime)
                Assert-Contract ([IO.File]::GetLastWriteTimeUtc($path) -eq $originalWriteTime) 'Same-length byte mutation did not restore original file timestamp.'
            }
            'rename' { [IO.File]::Move($path, (Join-Path $mutated.Path 'renamed.dat')) }
            'add' { [IO.File]::WriteAllText((Join-Path $mutated.Path '.new-hidden'), 'new') }
            'remove' { [IO.File]::Delete($path) }
        }
        Assert-Rejected { Assert-NativePublishManifestMatchesRuntime -ManifestPath $original.path -RuntimeDirectory $mutated.Path } ('Runtime tamper was accepted: ' + $mutation)
        Assert-Contract ((Get-FileHashValue $original.path) -ceq $original.sha256) 'Runtime validation modified immutable evidence.'
    }

    foreach ($corruption in @('duplicate-path', 'duplicate-property', 'case-duplicate-property', 'escaping-parent', 'absolute-path',
        'backslash-path', 'wrong-file-bytes', 'wrong-files-digest', 'inconsistent-executable', 'string-schema', 'wrong-kind', 'malformed-json')) {
        $path = Join-Path (New-OwnedDirectory) 'corrupt.json'
        $copy = Read-Json $reference.path
        switch ($corruption) {
            'duplicate-path' { $copy.files = @($copy.files) + @($copy.files[0]) }
            'escaping-parent' { $copy.files[0].path = '../external-sentinel.txt' }
            'absolute-path' { $copy.files[0].path = Join-Path $temporaryRoot 'external-sentinel.txt' }
            'backslash-path' { $copy.files[0].path = 'a\outside.txt' }
            'wrong-file-bytes' {
                $copy.files[0].bytes = [long]$copy.files[0].bytes + 1
                $copy.runtimeArtifactBytes = [long]$copy.runtimeArtifactBytes + 1
            }
            'wrong-files-digest' { $copy.runtimeFilesSha256 = '0' * 64 }
            'inconsistent-executable' { $copy.executable.sha256 = '0' * 64 }
            'string-schema' { $copy.schemaVersion = '1' }
            'wrong-kind' { $copy.kind = 'other' }
        }
        if ($corruption -cin @('duplicate-path', 'escaping-parent', 'absolute-path', 'backslash-path', 'wrong-file-bytes')) {
            $copy.runtimeFilesSha256 = Get-IndependentHash (Get-CanonicalFileBytes $copy.files)
        }
        if ($corruption -cin @('duplicate-property', 'case-duplicate-property')) {
            $raw = [IO.File]::ReadAllText($reference.path)
            $replacement = if ($corruption -ceq 'duplicate-property') { '"schemaVersion":1,"schemaVersion":1' }
                else { '"schemaVersion":1,"SchemaVersion":1' }
            $duplicate = $raw.Replace('"schemaVersion":1', $replacement)
            Assert-Contract ($raw -cne $duplicate) 'Duplicate-field fixture did not alter the original JSON.'
            [IO.File]::WriteAllText($path, $duplicate, [Text.UTF8Encoding]::new($false))
        }
        elseif ($corruption -ceq 'malformed-json') { [IO.File]::WriteAllText($path, '{not-json') }
        else { Write-Json $path $copy }
        Assert-Rejected { Assert-NativePublishManifestMatchesRuntime -ManifestPath $path -RuntimeDirectory $runtime.Path } ('Malformed manifest was accepted: ' + $corruption)
    }

    foreach ($invalid in @('source-unavailable-values', 'string-tool-bytes', 'nul-driver-version', 'extra-publish-property', 'reversed-timestamps', 'uppercase-build-id')) {
        $arguments = New-ManifestArguments $runtime
        switch ($invalid) {
            'source-unavailable-values' { $arguments.Source.dirty = $false }
            'string-tool-bytes' { $arguments.Toolchain.ilCompiler.bytes = '1' }
            'nul-driver-version' { $arguments.Toolchain.nativeLinkerDriver.version = 'owned' + [char]0 + 'version' }
            'extra-publish-property' { $arguments.PublishProperties.unrecordedOverride = $true }
            'reversed-timestamps' { $arguments.CompletedAtUtc = $arguments.StartedAtUtc.AddSeconds(-1) }
            'uppercase-build-id' { $arguments.BuildId = 'ABCDEF0123456789ABCDEF0123456789' }
        }
        Assert-Rejected { Write-NativePublishManifest @arguments } ('Invalid manifest input was accepted: ' + $invalid)
        Assert-Contract (-not [IO.File]::Exists($arguments.DestinationPath)) 'Rejected manifest input created an evidence file.'
    }

    $sentinelRoot = New-OwnedDirectory
    $sentinel = Join-Path $sentinelRoot 'external-sentinel.txt'
    [IO.File]::WriteAllText($sentinel, 'external sentinel must survive', [Text.UTF8Encoding]::new($false))
    $sentinelHash = Get-FileHashValue $sentinel
    foreach ($linkKind in @('file', 'directory')) {
        $linked = New-RuntimeFixture
        $linkPath = Join-Path $linked.Path ('linked-' + $linkKind)
        $available = $true
        try {
            if ($linkKind -ceq 'file') { [IO.File]::CreateSymbolicLink($linkPath, $sentinel) | Out-Null }
            elseif ($IsWindows) { New-Item -ItemType Junction -Path $linkPath -Target $sentinelRoot | Out-Null }
            else { [IO.Directory]::CreateSymbolicLink($linkPath, $sentinelRoot) | Out-Null }
        }
        catch {
            if ($IsWindows -and $linkKind -ceq 'file') { $available = $false; $skipped.Add('Windows file symbolic-link setup unavailable; rejection not claimed.') }
            else { throw 'Mandatory owned link fixture could not be created.' }
        }
        if ($available) {
            $ownedLinks.Add($linkPath)
            Assert-Rejected { Get-NativeRuntimeIdentity -RuntimeDirectory $linked.Path -ExecutableName $linked.ExecutableName } ('Runtime link was accepted: ' + $linkKind)
        }
    }
    if ($IsLinux) {
        $collision = New-RuntimeFixture
        $literalPath = [IO.Path]::Combine($collision.Path, 'a\collision.dat')
        $nestedPath = [IO.Path]::Combine($collision.Path, 'a', 'collision.dat')
        [IO.File]::WriteAllText($literalPath, 'literal backslash')
        [IO.File]::WriteAllText($nestedPath, 'nested path')
        Assert-Contract ($literalPath -cne $nestedPath -and [IO.File]::Exists($literalPath) -and [IO.File]::Exists($nestedPath) -and
            [IO.File]::ReadAllText($literalPath) -ceq 'literal backslash' -and [IO.File]::ReadAllText($nestedPath) -ceq 'nested path') 'Linux collision fixture did not create two distinct literal/nested files.'
        Assert-Rejected { Get-NativeRuntimeIdentity -RuntimeDirectory $collision.Path -ExecutableName $collision.ExecutableName } 'Linux literal-backslash path collision was accepted.'
    }
    Assert-Contract ((Get-FileHashValue $sentinel) -ceq $sentinelHash) 'Link validation changed the external sentinel.'

    # Files with FIFO/socket modes must be rejected before an open/read. Run the
    # actual helper in an owned bounded process so a broken implementation cannot
    # hang this suite on the FIFO's first hash read.
    if ($IsLinux) {
        $specialReader = Join-Path $temporaryRoot 'reject-special-file.ps1'
        [IO.File]::WriteAllText($specialReader, @'
param([string]$Helper, [string]$RuntimeDirectory, [string]$ExecutableName)
$ErrorActionPreference = 'Stop'
. $Helper
try { Get-NativeRuntimeIdentity -RuntimeDirectory $RuntimeDirectory -ExecutableName $ExecutableName | Out-Null }
catch { [Console]::Out.WriteLine('SPECIAL_REJECTED'); exit 0 }
exit 9
'@, [Text.UTF8Encoding]::new($false))
        foreach ($specialKind in @('fifo', 'socket')) {
            $special = New-RuntimeFixture
            # Keep the Unix socket pathname below sockaddr_un's small limit.
            $specialPath = Join-Path $special.Path $(if ($specialKind -ceq 'socket') { 's' } else { 'owned-fifo' })
            $socket = $null
            try {
                if ($specialKind -ceq 'fifo') {
                    $mkfifo = (Get-Command mkfifo -CommandType Application | Select-Object -First 1).Source
                    $make = [Diagnostics.ProcessStartInfo]::new()
                    $make.FileName = $mkfifo; $make.UseShellExecute = $false; $make.CreateNoWindow = $true
                    $make.ArgumentList.Add('--'); $make.ArgumentList.Add($specialPath)
                    $maker = [Diagnostics.Process]::Start($make)
                    try {
                        if (-not $maker.WaitForExit(5000)) {
                            $maker.Kill($true)
                            Assert-Contract ($maker.WaitForExit(5000)) 'Owned FIFO setup did not reap.'
                            throw 'Owned FIFO setup exceeded its deadline.'
                        }
                        Assert-Contract ($maker.ExitCode -eq 0) 'Mandatory owned FIFO fixture could not be created.'
                    }
                    finally { $maker.Dispose() }
                }
                else {
                    $socket = [Net.Sockets.Socket]::new([Net.Sockets.AddressFamily]::Unix,
                        [Net.Sockets.SocketType]::Stream, [Net.Sockets.ProtocolType]::Unspecified)
                    $socket.Bind([Net.Sockets.UnixDomainSocketEndPoint]::new($specialPath))
                }
                $specialResult = Invoke-PrivateChild $specialReader @('-Helper', (Join-Path $PSScriptRoot 'native-publish-evidence.ps1'),
                    '-RuntimeDirectory', $special.Path, '-ExecutableName', $special.ExecutableName) @{} 12
                Assert-Contract ($specialResult.ExitCode -eq 0 -and $specialResult.Stdout.Trim() -ceq 'SPECIAL_REJECTED') ('Special Linux file was accepted: ' + $specialKind)
                $cases++
            }
            finally {
                if ($null -ne $socket) { $socket.Dispose() }
                # Unlink only this known owned entry; never read it during cleanup.
                if ([IO.File]::Exists($specialPath)) { [IO.File]::Delete($specialPath) }
            }
        }
    }

    $script:warningBaselinePath = Join-Path $temporaryRoot 'warning-baseline.json'
    Write-Json $script:warningBaselinePath @{ schemaVersion = 1; groups = @() }
    $script:publisherWrapper = Join-Path $temporaryRoot 'invoke-owned-publisher.ps1'
    $script:fakeDotnetPath = Join-Path $temporaryRoot 'fake-dotnet.ps1'
    [IO.File]::WriteAllText($script:publisherWrapper, @'
param([string]$ArtifactDirectory, [string]$Runtime, [string]$WarningBaselinePath)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
# This wrapper is confined to the owned child. The actual publisher retains its
# production dotnet invocation; every publish below executes a real subprocess.
function dotnet {
    $argumentJson = ConvertTo-Json -InputObject ([string[]]@($args)) -Compress
    $encodedArguments = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($argumentJson))
    & $env:CONDUIT_PUBLISH_EVIDENCE_TEST_DRIVER -NoProfile -File $env:CONDUIT_PUBLISH_EVIDENCE_TEST_DOTNET -EncodedArguments $encodedArguments
    $global:LASTEXITCODE = $LASTEXITCODE
}
if ($env:CONDUIT_PUBLISH_EVIDENCE_TEST_SCENARIO -ceq 'source-unavailable') {
    function git { $global:LASTEXITCODE = 128 }
}
try {
    & $env:CONDUIT_PUBLISH_EVIDENCE_TEST_PUBLISHER -ArtifactDirectory $ArtifactDirectory -Runtime $Runtime -WarningBaselinePath $WarningBaselinePath
    exit 0
}
catch {
    [Console]::Error.WriteLine('Owned publisher fixture failed; type=' + $_.Exception.GetType().FullName + '; line=' + [int]$_.InvocationInfo.ScriptLineNumber + '.')
    exit 1
}
'@, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($script:fakeDotnetPath, @'
param([Parameter(Mandatory = $true)][string]$EncodedArguments)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
try {
    $argumentJson = [Text.UTF8Encoding]::new($false, $true).GetString([Convert]::FromBase64String($EncodedArguments))
    [string[]]$arguments = @($argumentJson | ConvertFrom-Json)
    $root = $env:CONDUIT_PUBLISH_EVIDENCE_TEST_ROOT
    $scenario = $env:CONDUIT_PUBLISH_EVIDENCE_TEST_SCENARIO
    $record = [ordered]@{ arguments = $arguments }
    $recordPath = Join-Path $root ('commands/' + [Guid]::NewGuid().ToString('N') + '.json')
    [IO.File]::WriteAllText($recordPath,
        ($record | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    if ($arguments.Count -lt 3 -or $arguments[0] -cne 'publish') { throw 'Invalid fixture operation.' }
    function Get-Argument([string]$Name) {
        $index = [Array]::IndexOf($arguments, $Name)
        if ($index -lt 0 -or $index + 1 -ge $arguments.Length) { throw 'Fixture argument missing.' }
        return $arguments[$index + 1]
    }
    $properties = @{}
    foreach ($argument in $arguments) {
        if ($argument -cmatch '^-p:([^=]+)=(.*)$') {
            $properties[$Matches[1]] = $Matches[2].Replace('%3B',';').Replace('%3b',';').Replace('%2C',',').Replace('%2c',',').Replace('%25','%')
        }
    }
    $assembly = [string]$properties.ConduitNativeToolchainEvidenceAssemblyName
    $service = switch ($assembly) {
        'ConduitLLM.Admin' { 'admin' } 'ConduitLLM.Gateway' { 'gateway' }
        'ConduitLLM.GatewayNativeAotTests' { 'probe' } default { throw 'Unknown fixture assembly.' }
    }
    if ($scenario -ceq ('fail-' + $service)) { [Console]::Error.WriteLine('Owned publish failure.'); exit 7 }
    $output = Get-Argument '--output'
    $rid = Get-Argument '--runtime'
    $executableName = $assembly + $(if ($rid -ceq 'win-x64') { '.exe' } else { '' })
    $nativeIntermediate = [string]$properties.NativeIntermediateOutputPath
    $nativeOutput = [string]$properties.NativeOutputPath
    if ([string]::IsNullOrEmpty($nativeIntermediate) -or [string]::IsNullOrEmpty($nativeOutput)) { throw 'Selected native fixture directories missing.' }
    [IO.Directory]::CreateDirectory($nativeIntermediate) | Out-Null
    [IO.Directory]::CreateDirectory($nativeOutput) | Out-Null
    $nativeObject = Join-Path $nativeIntermediate ($assembly + $(if ($rid -ceq 'win-x64') { '.obj' } else { '.o' }))
    $nativeBinary = Join-Path $nativeOutput $executableName
    [IO.File]::WriteAllText($nativeObject, 'owned compiled object ' + $service, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($nativeBinary, 'owned fresh native link ' + $service, [Text.UTF8Encoding]::new($false))
    [IO.Directory]::CreateDirectory((Join-Path $output 'nested')) | Out-Null
    [IO.File]::Copy($nativeBinary, (Join-Path $output $executableName))
    switch ($scenario) {
        'native-object-missing' { [IO.File]::Delete($nativeObject) }
        'native-object-empty' { [IO.File]::WriteAllBytes($nativeObject, [byte[]]@()) }
        'native-object-wrong-path' {
            $wrongObject = Join-Path $nativeIntermediate ('wrong' + $(if ($rid -ceq 'win-x64') { '.obj' } else { '.o' }))
            [IO.File]::Copy($nativeObject, $wrongObject)
            $nativeObject = $wrongObject
        }
        'native-binary-missing' { [IO.File]::Delete($nativeBinary) }
        'native-binary-wrong-path' {
            $wrongBinary = Join-Path $nativeOutput ('wrong' + $(if ($rid -ceq 'win-x64') { '.exe' } else { '' }))
            [IO.File]::Copy($nativeBinary, $wrongBinary)
            $nativeBinary = $wrongBinary
        }
        'native-binary-mismatch' { [IO.File]::WriteAllText((Join-Path $output $executableName), 'different runtime bytes', [Text.UTF8Encoding]::new($false)) }
        'native-intermediate-wrong-path' {
            $nativeIntermediate = (Join-Path $nativeIntermediate 'unexpected') + [IO.Path]::DirectorySeparatorChar
            [IO.Directory]::CreateDirectory($nativeIntermediate) | Out-Null
        }
        'native-output-wrong-path' {
            $nativeOutput = (Join-Path $nativeOutput 'unexpected') + [IO.Path]::DirectorySeparatorChar
            [IO.Directory]::CreateDirectory($nativeOutput) | Out-Null
        }
    }
    [IO.File]::WriteAllBytes((Join-Path $output '.hidden-runtime'), [byte[]]@(0,1,2,255))
    [IO.File]::WriteAllText((Join-Path $output 'nested/café-雪.json'), '{"value":"雪 😀"}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $output ($assembly + '.dbg')), 'debug symbols must be split')
    if ($IsWindows) { [IO.File]::SetAttributes((Join-Path $output '.hidden-runtime'), [IO.FileAttributes]::Hidden) }
    $toolRoot = $env:CONDUIT_PUBLISH_EVIDENCE_TEST_TOOLROOT
    $driver = $env:CONDUIT_PUBLISH_EVIDENCE_TEST_DRIVER
    $capture = [ordered]@{
        NETCoreSdkVersion = '10.0.401'; RuntimeIdentifier = $rid; PublishAot = 'true'; Configuration = 'Release'
        IlcHostPackagePath = $toolRoot; RuntimePackagePath = (Join-Path $toolRoot 'target-runtime')
        IlcToolsPath = (Join-Path $toolRoot 'tools'); ILCompilerTargetsPath = (Join-Path $toolRoot 'actual-selected.targets')
        CppCompilerAndLinker = $driver; CppLinker = $driver; LinkerFlavor = ''; ObjCopyName = ''; IlcUseEnvironmentalTools = ''
        _hostOS = $(if ($IsWindows) { 'win' } else { 'linux' }); _hostArchitecture = 'x64'
        _targetOS = $(if ($rid -ceq 'win-x64') { 'win' } else { 'linux' }); _targetArchitecture = 'x64'
        NativeIntermediateOutputPath = $nativeIntermediate; NativeOutputPath = $nativeOutput
        NativeObject = $nativeObject; NativeBinary = $nativeBinary; AssemblyName = $assembly
        SelfContained = 'true'; StripSymbols = 'true'; ConduitAotAudit = [string]$properties.ConduitAotAudit
        UseSharedCompilation = 'false'; Version = '1.0.0'; InformationalVersion = 'owned-fixture'
        ConduitCommitSha = $(if ($properties.ContainsKey('ConduitCommitSha')) { $properties.ConduitCommitSha } else { '' })
        ConduitBuildTimestamp = $(if ($properties.ContainsKey('ConduitBuildTimestamp')) { $properties.ConduitBuildTimestamp } else { '' })
    }
    $record['service'] = $service
    $record['nativePaths'] = [ordered]@{
        NativeIntermediateOutputPath = $nativeIntermediate; NativeOutputPath = $nativeOutput
        NativeObject = $nativeObject; NativeBinary = $nativeBinary
    }
    [IO.File]::WriteAllText($recordPath, ($record | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($name in $capture.Keys) {
        $lines.Add($name + '=' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$capture[$name])))
    }
    switch ($scenario) {
        'duplicate-capture' { $lines.Add($lines[0]) }
        'unexpected-capture' { $lines.Add('UNEXPECTED=' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('fixture'))) }
        'invalid-base64' { $lines[0] = 'NETCoreSdkVersion=MT AuMA==' }
        'invalid-utf8' { $lines[0] = 'NETCoreSdkVersion=' + [Convert]::ToBase64String([byte[]]@(0xc3,0x28)) }
    }
    [IO.File]::WriteAllLines([string]$properties.ConduitNativeToolchainEvidencePath, $lines, [Text.UTF8Encoding]::new($false))
    if ($scenario -ceq 'linker-failure' -and $service -ceq 'gateway') {
        [Console]::Out.WriteLine('ILC : AOT analysis warning IL3050: ConduitLLM.OwnedFixture.Unsupported() [fixture.csproj]')
    }
    [Console]::Out.WriteLine('Owned fake native publish complete.')
    exit 0
}
catch {
    [Console]::Error.WriteLine('Owned fake dotnet failed; type=' + $_.Exception.GetType().FullName + '; line=' + [int]$_.InvocationInfo.ScriptLineNumber + '.')
    exit 8
}
'@, [Text.UTF8Encoding]::new($false))

    $trackedEnvironmentNames = @('CONDUIT_PUBLISH_EVIDENCE_TEST_ROOT', 'CONDUIT_PUBLISH_EVIDENCE_TEST_SCENARIO',
        'CONDUIT_PUBLISH_EVIDENCE_TEST_DOTNET', 'CONDUIT_PUBLISH_EVIDENCE_TEST_PUBLISHER',
        'CONDUIT_PUBLISH_EVIDENCE_TEST_TOOLROOT', 'CONDUIT_PUBLISH_EVIDENCE_TEST_DRIVER')
    $parentEnvironment = @{}
    foreach ($name in $trackedEnvironmentNames) { $parentEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }

    foreach ($service in @('admin', 'gateway', 'probe')) {
        Invoke-PublisherCase 'nonempty-runtime' $false $service | Out-Null
    }
    foreach ($scenario in @('prior-baseline', 'existing-claim', 'linked-output', 'linked-publish-directory',
        'linked-symbol-directory', 'linked-log-leaf', 'linked-symbol-leaf', 'linked-native-intermediates', 'linked-native-outputs',
        'fail-admin', 'fail-gateway', 'fail-probe', 'linker-failure', 'duplicate-capture', 'unexpected-capture', 'invalid-base64', 'invalid-utf8',
        'native-object-missing', 'native-object-empty', 'native-object-wrong-path', 'native-binary-missing',
        'native-binary-wrong-path', 'native-binary-mismatch', 'native-intermediate-wrong-path', 'native-output-wrong-path')) {
        Invoke-PublisherCase $scenario $false | Out-Null
    }
    $success = Invoke-PublisherCase 'success' $true
    $unknownSource = Invoke-PublisherCase 'source-unavailable' $true
    $unknownBaseline = Read-Json (Join-Path $unknownSource.ArtifactRoot 'reports/native-baselines.json')
    $knownBaseline = Read-Json (Join-Path $success.ArtifactRoot 'reports/native-baselines.json')
    Assert-Contract ($knownBaseline.probeManifest.buildId -cne $unknownBaseline.probeManifest.buildId) 'Separate successful publisher invocations reused one native compile/link build GUID.'
    $knownDirectories = @($success.Records | ForEach-Object { $_.nativePaths.NativeIntermediateOutputPath; $_.nativePaths.NativeOutputPath })
    foreach ($record in $unknownSource.Records) {
        Assert-Contract ($record.nativePaths.NativeIntermediateOutputPath -cnotin $knownDirectories -and
            $record.nativePaths.NativeOutputPath -cnotin $knownDirectories) 'Separate publisher invocation reused a selected native compile/link directory.'
    }
    foreach ($ref in @($unknownBaseline.services[0].publishManifest, $unknownBaseline.services[1].publishManifest, $unknownBaseline.probeManifest)) {
        $unknownManifest = Read-Json (Join-Path $unknownSource.ArtifactRoot $ref.path)
        Assert-Contract ($unknownManifest.source.status -ceq 'unavailable' -and $null -eq $unknownManifest.source.revision -and
            $null -eq $unknownManifest.source.dirty) 'Unknown-source fake publish invented git metadata.'
    }

    # Re-running the actual publisher must retain the original manifests and
    # baseline, and reject before any further fake dotnet invocation.
    $successBaselinePath = Join-Path $success.ArtifactRoot 'reports/native-baselines.json'
    $baselineBefore = Get-FileHashValue $successBaselinePath
    $evidenceBefore = @{}
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $success.ArtifactRoot 'reports/publish') -Recurse -File -Filter '*.json') {
        $evidenceBefore[$file.FullName] = Get-FileHashValue $file.FullName
    }
    $retryEnvironment = @{
        CONDUIT_PUBLISH_EVIDENCE_TEST_ROOT = $success.CaseRoot; CONDUIT_PUBLISH_EVIDENCE_TEST_SCENARIO = 'success'
        CONDUIT_PUBLISH_EVIDENCE_TEST_DOTNET = $script:fakeDotnetPath; CONDUIT_PUBLISH_EVIDENCE_TEST_PUBLISHER = $publishScript
        CONDUIT_PUBLISH_EVIDENCE_TEST_TOOLROOT = $script:toolRoot; CONDUIT_PUBLISH_EVIDENCE_TEST_DRIVER = $script:pwshPath
    }
    $retry = Invoke-PrivateChild $script:publisherWrapper @('-ArtifactDirectory', $success.ArtifactRoot,
        '-Runtime', $(if ($IsWindows) { 'win-x64' } else { 'linux-x64' }), '-WarningBaselinePath', $script:warningBaselinePath) $retryEnvironment
    Assert-Contract ($retry.ExitCode -ne 0 -and (Get-CommandRecords $success.CaseRoot).Count -eq 3 -and
        (Get-FileHashValue $successBaselinePath) -ceq $baselineBefore) 'Publisher retry overwrote prior evidence or invoked dotnet.'
    foreach ($path in $evidenceBefore.Keys) { Assert-Contract ((Get-FileHashValue $path) -ceq $evidenceBefore[$path]) 'Publisher retry rewrote an immutable manifest.' }
    $cases++

    # Both fake applications are deliberately nonexecutable fixture bytes. Remove
    # them before native-smoke so its real missing-executable failure branch runs
    # without starting an invented application or altering immutable evidence.
    $beforeSmoke = Read-Json $successBaselinePath
    foreach ($service in @('admin', 'gateway')) { [IO.File]::Delete((Join-Path $success.ArtifactRoot ('runtime/' + $service + '/' + (Get-ExecutableName $service)))) }
    $smoke = Invoke-PrivateChild $smokeScript @('-ArtifactDirectory', $success.ArtifactRoot, '-TimeoutSeconds', '2') @{}
    Assert-Contract ($smoke.ExitCode -ne 0) 'Missing-executable native smoke unexpectedly succeeded.'
    $afterSmoke = Read-Json $successBaselinePath
    Assert-Contract ($afterSmoke.schemaVersion -eq 2 -and $afterSmoke.services.Count -eq 2) 'Smoke rewrite lost schema2 baseline structure.'
    for ($index = 0; $index -lt 2; $index++) {
        $before = $beforeSmoke.services[$index]; $after = $afterSmoke.services[$index]
        Assert-Contract ($after.smokeSucceeded -is [bool] -and -not $after.smokeSucceeded -and
            $after.publishManifest.Count -eq 3 -and $after.publishManifest.path -ceq $before.publishManifest.path -and
            $after.publishManifest.buildId -ceq $before.publishManifest.buildId -and $after.publishManifest.sha256 -ceq $before.publishManifest.sha256) 'Smoke failure rewrite lost a shallow service evidence reference.'
    }
    Assert-Contract ($afterSmoke.probeManifest.Count -eq 3 -and $afterSmoke.probeManifest.path -ceq $beforeSmoke.probeManifest.path -and
        $afterSmoke.probeManifest.buildId -ceq $beforeSmoke.probeManifest.buildId -and $afterSmoke.probeManifest.sha256 -ceq $beforeSmoke.probeManifest.sha256) 'Smoke failure rewrite lost probe evidence.'
    foreach ($path in $evidenceBefore.Keys) { Assert-Contract ((Get-FileHashValue $path) -ceq $evidenceBefore[$path]) 'Smoke changed immutable publish manifest bytes.' }
    $cases++

    foreach ($name in $trackedEnvironmentNames) {
        Assert-Contract ([Environment]::GetEnvironmentVariable($name) -ceq $parentEnvironment[$name]) 'Private publisher child changed a parent environment variable.'
    }
    Assert-Contract ((Get-FileHashValue $sentinel) -ceq $sentinelHash) 'Publish/smoke contracts changed the external sentinel.'
}
catch { $proofFailure = $_.Exception }
finally {
    [Globalization.CultureInfo]::CurrentCulture = $originalCulture
    [Globalization.CultureInfo]::CurrentUICulture = $originalUICulture
    $linkCleanupSucceeded = $true
    foreach ($link in $ownedLinks) {
        try { if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force } }
        catch { $linkCleanupSucceeded = $false; $cleanupFailure = $_.Exception.GetType().FullName }
    }
    try {
        # Keep the entire owned root if any link could not be unlinked, so a
        # recursive cleanup can never follow an unresolved external target.
        Assert-Contract $linkCleanupSucceeded 'Owned link cleanup failed.'
        if ([IO.Directory]::Exists($temporaryRoot)) {
            $resolved = [IO.Path]::GetFullPath($temporaryRoot)
            $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
            Assert-Contract ([IO.Path]::GetDirectoryName($resolved).TrimEnd([IO.Path]::DirectorySeparatorChar) -ceq $parent -and
                [IO.Path]::GetFileName($resolved) -cmatch '^conduit-publish-contract-[0-9a-f]{32}$') 'Publish-contract cleanup ownership was rejected.'
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
    catch { $cleanupFailure = $_.Exception.GetType().FullName }
}
if ($null -ne $cleanupFailure) { throw 'Native publish evidence contract fixture cleanup failed.' }
if ($null -ne $proofFailure) { throw $proofFailure }
foreach ($message in $skipped) { Write-Host ('SKIP: ' + $message) }
Write-Host ('PASS: ' + $cases + ' native publish evidence contracts; immutable file identity and owned fake publisher cases.')
