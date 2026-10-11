#!/usr/bin/env pwsh
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('conduit-image-contract-' + [Guid]::NewGuid().ToString('N'))))
$producer = Join-Path $PSScriptRoot 'publish-native-image.ps1'
$cases = 0
$skipped = [Collections.Generic.List[string]]::new()
$ownedLinks = [Collections.Generic.List[string]]::new()
$proofFailure = $null
$cleanupFailure = $null
$fixtureVersion = '8.1.0'
$fixtureTimestamp = '2026-10-11T12:34:56.0000000Z'

function Assert-Contract([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function New-OwnedDirectory([string] $Parent = $temporaryRoot) {
    $path = Join-Path $Parent ([Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($path) | Out-Null
    return $path
}

function Get-FileDigest([string] $Path) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($Path))).ToLowerInvariant()
}

function Read-Json([string] $Path) {
    return [IO.File]::ReadAllText($Path) | ConvertFrom-Json -AsHashtable -DateKind String
}

function Write-Json([string] $Path, $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 30), [Text.UTF8Encoding]::new($false))
}

function Get-ExecutableName([string] $Service, [switch] $Health) {
    $name = if ($Health) { 'conduit-health-probe' } elseif ($Service -ceq 'admin') { 'ConduitLLM.Admin' } else { 'ConduitLLM.Gateway' }
    if ($IsWindows) { $name += '.exe' }
    return $name
}

function Get-Records([string] $CaseRoot) {
    return ,@(Get-ChildItem -LiteralPath (Join-Path $CaseRoot 'commands') -File | ForEach-Object { Read-Json $_.FullName })
}

function Get-SafeChildDiagnostic($Result) {
    $captured = @([string]$Result.Stdout, [string]$Result.Stderr) -join [Environment]::NewLine
    $safe = @($captured -split '\r?\n' | Where-Object {
        $_ -cmatch '^Owned image (?:publisher|dotnet) failed; type=[A-Za-z0-9_.+]+; line=[0-9]+\.$' -or
        $_ -ceq 'Owned image publish failure.'
    })
    if ($safe.Count -eq 0) { return 'no controlled child diagnostic' }
    return $safe -join ' | '
}

function Invoke-OwnedChild([string] $Script, [string[]] $Arguments, [hashtable] $Environment, [int] $TimeoutSeconds = 90) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $script:pwshPath
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in @('-NoProfile', '-File', $Script) + $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($name in $Environment.Keys) { $start.Environment[$name] = [string]$Environment[$name] }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            Assert-Contract ($process.WaitForExit(5000)) 'Owned image contract child did not reap after its deadline.'
            throw 'Owned image contract child exceeded its deadline.'
        }
        Assert-Contract ([Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000)) 'Owned image contract child streams did not close.'
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout.Result; Stderr = $stderr.Result }
    }
    finally {
        if (-not $process.HasExited) {
            $process.Kill($true)
            Assert-Contract ($process.WaitForExit(5000)) 'Owned image contract child did not reap during cleanup.'
        }
        $process.Dispose()
    }
}

function Get-ChildEnvironment([string] $CaseRoot, [string] $Scenario) {
    return @{
        CONDUIT_IMAGE_TEST_ROOT = $CaseRoot; CONDUIT_IMAGE_TEST_SCENARIO = $Scenario
        CONDUIT_IMAGE_TEST_PUBLISHER = $producer; CONDUIT_IMAGE_TEST_DOTNET = $script:fakeDotnet
        CONDUIT_IMAGE_TEST_DRIVER = $script:pwshPath; CONDUIT_IMAGE_TEST_TOOLROOT = $script:toolRoot
    }
}

function Invoke-ImageCase([string] $Service, [string] $Scenario, [bool] $Success, [int] $ExpectedPublishes) {
    $caseRoot = New-OwnedDirectory
    [IO.Directory]::CreateDirectory((Join-Path $caseRoot 'commands')) | Out-Null
    $artifact = Join-Path $caseRoot 'artifact'
    $foreign = $null
    if ($Scenario -ceq 'linked-output') {
        $foreign = Join-Path $caseRoot 'external-sentinel'
        [IO.Directory]::CreateDirectory($foreign) | Out-Null
        [IO.File]::WriteAllText((Join-Path $foreign 'sentinel'), 'external image sentinel')
        if ($IsWindows) { New-Item -ItemType Junction -Path $artifact -Target $foreign | Out-Null }
        else { [IO.Directory]::CreateSymbolicLink($artifact, $foreign) | Out-Null }
        $script:ownedLinks.Add($artifact)
    }
    if ($Scenario -ceq 'nonempty-output') {
        [IO.Directory]::CreateDirectory($artifact) | Out-Null
        [IO.File]::WriteAllText((Join-Path $artifact 'sentinel'), 'existing image output')
    }
    $commit = if ($Scenario -ceq 'commit-mismatch') { '0000000000000000000000000000000000000000' } else { 'dev' }
    $result = Invoke-OwnedChild $script:wrapper @('-Service', $Service, '-ArtifactDirectory', $artifact,
        '-Runtime', $script:rid, '-Version', $fixtureVersion, '-CommitSha', $commit, '-BuildTimestamp', $fixtureTimestamp,
        '-WarningBaselinePath', $script:warningBaseline) (Get-ChildEnvironment $caseRoot $Scenario)
    $records = Get-Records $caseRoot
    $evidence = Join-Path $artifact 'evidence'
    $jsons = if ([IO.Directory]::Exists($evidence)) { @(Get-ChildItem -LiteralPath $evidence -File -Filter '*.json') } else { @() }
    $diagnostic = 'scenario=' + $Scenario + '; service=' + $Service + '; exit=' + $result.ExitCode + '; actual=' + $records.Count + '; expected=' + $ExpectedPublishes + '; child=' + (Get-SafeChildDiagnostic $result)
    Assert-Contract ($records.Count -eq $ExpectedPublishes) ('Image case did not reach the intended publish stage; ' + $diagnostic)
    if (-not $Success) {
        Assert-Contract ($result.ExitCode -ne 0 -and $jsons.Count -eq 0) ('Failed image publish left success evidence; ' + $diagnostic)
        if ($ExpectedPublishes -gt 0) { Assert-Contract ([IO.File]::Exists((Join-Path $artifact 'native-image-publish.claim'))) 'Failed image producer discarded its claim.' }
        if ($Scenario -cin @('health-sidecar', 'payload-collision', 'copied-binary-mismatch', 'capture-root-mismatch', 'health-linker-warning')) {
            Assert-Contract ($result.Stdout.Contains('Owned image native publish complete.', [StringComparison]::Ordinal)) 'Intended image rejection preceded all fake native output.'
        }
        if ($null -ne $foreign) {
            $entries = @(([IO.DirectoryInfo]::new($foreign)).EnumerateFileSystemInfos())
            Assert-Contract ($entries.Count -eq 1 -and $entries[0].Name -ceq 'sentinel' -and
                [IO.File]::ReadAllText((Join-Path $foreign 'sentinel')) -ceq 'external image sentinel') 'Image preflight modified a linked external sentinel directory.'
        }
        if ($Scenario -ceq 'nonempty-output') {
            Assert-Contract ([IO.File]::ReadAllText((Join-Path $artifact 'sentinel')) -ceq 'existing image output') 'Image preflight overwrote existing output.'
        }
    }
    else {
        Assert-Contract ($result.ExitCode -eq 0 -and $jsons.Count -eq 3) ('Successful image producer did not return exactly three evidence documents; ' + $diagnostic)
        $payload = Join-Path $artifact 'payload'
        Assert-NativeImagePublishEvidenceMatchesPayload -EvidenceDirectory $evidence -PayloadDirectory $payload | Out-Null
        $image = Read-Json (Join-Path $evidence 'image-publish.json')
        $application = Read-Json (Join-Path $evidence 'application.json')
        $health = Read-Json (Join-Path $evidence 'health-probe.json')
        Assert-Contract ($image.schemaVersion -is [long] -or $image.schemaVersion -is [int]) 'Image wrapper schema is not numeric.'
        Assert-Contract ($image.schemaVersion -eq 1 -and $image.kind -ceq 'conduit-native-image-publish' -and
            $image.service -ceq $Service -and $image.rid -ceq $script:rid -and $image.buildId -cmatch '^[0-9a-f]{32}$') 'Image wrapper role/platform identity is invalid.'
        Assert-Contract ($application.service -ceq $Service -and $health.service -ceq 'probe' -and
            $application.buildId -ceq $image.buildId -and $health.buildId -ceq $image.buildId -and
            $application.executable.path -ceq (Get-ExecutableName $Service) -and $health.executable.path -ceq (Get-ExecutableName $Service -Health)) 'Application and health manifest roles are not independently bound.'
        Assert-Contract ($image.application.Count -eq 2 -and $image.application.path -ceq 'application.json' -and
            $image.application.sha256 -ceq (Get-FileDigest (Join-Path $evidence 'application.json')) -and
            $image.healthProbe.Count -eq 2 -and $image.healthProbe.path -ceq 'health-probe.json' -and
            $image.healthProbe.sha256 -ceq (Get-FileDigest (Join-Path $evidence 'health-probe.json'))) 'Image wrapper is not bound to both exact child bytes.'
        Assert-Contract ($application.runtimeFilesSha256 -ceq $health.runtimeFilesSha256 -and
            (ConvertTo-Json -InputObject $application.files -Depth 8 -Compress) -ceq (ConvertTo-Json -InputObject $health.files -Depth 8 -Compress)) 'Each executable does not cover the exact same complete assembled payload.'
        $payloadFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -File -Force)
        Assert-Contract ($payloadFiles.Count -eq 5 -and $application.files.Count -eq 5 -and
            [IO.File]::ReadAllText((Join-Path $payload 'nested/café-雪.json')) -ceq '{"unicode":"雪 😀"}' -and
            [IO.File]::ReadAllBytes((Join-Path $payload '.host-hidden')).Length -eq 4 -and
            -not [IO.File]::Exists((Join-Path $payload ('ConduitLLM.HealthProbe' + $(if ($IsWindows) { '.exe' } else { '' })))) -and
            @($payloadFiles | Where-Object { $_.Extension -cin @('.dbg', '.pdb') }).Count -eq 0) 'Image assembly omitted hidden/nested host files, health rename, or recursive symbol split.'
        $healthComponentFiles = @(Get-ChildItem -LiteralPath (Join-Path $artifact 'components/health-probe') -Recurse -File -Force)
        Assert-Contract ($healthComponentFiles.Count -eq 1) 'Health component retained a sidecar after symbol splitting.'
        foreach ($role in @('application', 'health-probe')) {
            $roleRecords = @($records | Where-Object { $_.role -ceq $role })
            Assert-Contract ($roleRecords.Count -eq 1 -and $roleRecords[0].capture.Count -eq 30) 'Image producer did not capture both complete selected toolchains.'
            $capture = $roleRecords[0].capture
            $expectedIntermediate = Join-Path $artifact ('native-intermediates/' + $image.buildId + '/' + $role)
            $expectedOutput = Join-Path $artifact ('native-outputs/' + $image.buildId + '/' + $role)
            $trim = [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
            Assert-Contract ([IO.Path]::GetFullPath($capture.NativeIntermediateOutputPath).TrimEnd($trim) -ceq $expectedIntermediate -and
                [IO.Path]::GetFullPath($capture.NativeOutputPath).TrimEnd($trim) -ceq $expectedOutput -and
                [IO.File]::Exists($capture.NativeObject) -and (Get-Item -LiteralPath $capture.NativeObject).Length -gt 0) 'Image producer reused or omitted a fresh native object directory.'
            $manifest = if ($role -ceq 'application') { $application } else { $health }
            $payloadExecutable = Join-Path $payload $manifest.executable.path
            Assert-Contract ((Get-FileDigest $capture.NativeBinary) -ceq (Get-FileDigest $payloadExecutable) -and
                $manifest.toolchain.ilCompiler.sha256 -ceq (Get-FileDigest $script:compiler) -and
                $manifest.toolchain.nativeLinkerDriver.sha256 -ceq (Get-FileDigest $script:pwshPath) -and
                $manifest.publishProperties.conduitAotAudit -eq ($role -ceq 'application')) 'Image executable/toolchain/audit identity differs from selected native outputs.'
            Assert-Contract ($capture.Version -ceq $fixtureVersion -and $capture.ConduitBuildTimestamp -ceq $fixtureTimestamp -and
                $capture.ConduitCommitSha -ceq $image.commitMetadata.commitSha) 'Image components were published with different declared commit metadata.'
            Assert-Contract (@(Get-ChildItem -LiteralPath (Join-Path $artifact ('symbols/' + $role)) -Recurse -File -Force).Count -eq 2) 'Nested/root debug symbols were not both split.'
        }
        Assert-Contract (-not [string]::IsNullOrWhiteSpace($records[0].capture.InformationalVersion) -and
            $records[0].capture.InformationalVersion -ceq $records[1].capture.InformationalVersion -and
            $image.commitMetadata.Count -eq 3 -and $image.commitMetadata.version -ceq $fixtureVersion -and
            $image.commitMetadata.buildTimestamp -ceq $fixtureTimestamp) 'Image component informational versions or wrapper metadata diverge.'
        if ($Scenario -ceq 'source-unavailable') {
            foreach ($manifest in @($application, $health)) {
                Assert-Contract ($manifest.source.status -ceq 'unavailable' -and $null -eq $manifest.source.revision -and
                    $null -eq $manifest.source.dirty) 'Unavailable image source contains invented git identity.'
            }
            Assert-Contract ($image.commitMetadata.commitSha -ceq 'dev') 'Unavailable source was confused with declared commit metadata.'
        }
        elseif ($application.source.status -ceq 'git') {
            Assert-Contract ($application.source.revision -ceq $image.commitMetadata.commitSha -and
                $health.source.revision -ceq $application.source.revision) 'Actual source identity is not the requested image revision.'
        }
        [long]$sum = 0
        foreach ($file in $payloadFiles) { $sum += $file.Length }
        Assert-Contract ($image.metrics.runtimeArtifactBytes -eq $sum -and $image.metrics.executableBytes -eq
            (Get-Item -LiteralPath (Join-Path $payload (Get-ExecutableName $Service))).Length -and
            $image.metrics.publishDurationMilliseconds -ge 0 -and $image.metrics.applicationPublishDurationMilliseconds -ge 0 -and
            $image.metrics.healthProbePublishDurationMilliseconds -ge 0) 'Image metrics are not based on the assembled runtime bytes.'
        Assert-Contract ([IO.File]::ReadAllText((Join-Path $artifact 'native-image-publish.claim')).Trim() -ceq $image.buildId) 'Image publish claim does not identify the wrapper build.'
    }
    $script:cases++
    return [pscustomobject]@{ Root = $caseRoot; Artifact = $artifact; Result = $result; Records = $records }
}

function Copy-Evidence($Fixture) {
    $directory = New-OwnedDirectory
    foreach ($name in @('application.json', 'health-probe.json', 'image-publish.json')) {
        [IO.File]::Copy((Join-Path $Fixture.Artifact ('evidence/' + $name)), (Join-Path $directory $name))
    }
    return $directory
}

function Assert-EvidenceRejected([string] $Evidence, [string] $Payload, [string] $Label) {
    $rejected = $false
    try { Assert-NativeImagePublishEvidenceMatchesPayload -EvidenceDirectory $Evidence -PayloadDirectory $Payload | Out-Null }
    catch { $rejected = $true }
    Assert-Contract $rejected ('Invalid image evidence/payload was accepted: ' + $Label)
    $script:cases++
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $script:pwshPath = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
    $script:rid = if ($IsWindows) { 'win-x64' } else { 'linux-x64' }
    $script:toolRoot = New-OwnedDirectory
    [IO.Directory]::CreateDirectory((Join-Path $script:toolRoot 'tools')) | Out-Null
    $script:compiler = Join-Path $script:toolRoot ('tools/' + $(if ($IsWindows) { 'ilc.exe' } else { 'ilc' }))
    [IO.File]::WriteAllText($script:compiler, 'owned image ILCompiler bytes')
    [IO.File]::WriteAllText((Join-Path $script:toolRoot 'compiler.nuspec'),
        '<package><metadata><id>runtime.' + $script:rid + '.Microsoft.DotNet.ILCompiler</id><version>10.0.12</version></metadata></package>')
    $script:warningBaseline = Join-Path $temporaryRoot 'warning-baseline.json'
    Write-Json $script:warningBaseline @{ schemaVersion = 1; groups = @() }
    $script:wrapper = Join-Path $temporaryRoot 'invoke-image-publisher.ps1'
    $script:fakeDotnet = Join-Path $temporaryRoot 'fake-image-dotnet.ps1'
    [IO.File]::WriteAllText($script:wrapper, @'
param([string]$Service,[string]$ArtifactDirectory,[string]$Runtime,[string]$Version,[string]$CommitSha,[string]$BuildTimestamp,[string]$WarningBaselinePath)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
function dotnet {
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject ([string[]]@($args)) -Compress)))
    & $env:CONDUIT_IMAGE_TEST_DRIVER -NoProfile -File $env:CONDUIT_IMAGE_TEST_DOTNET -EncodedArguments $encoded
    $global:LASTEXITCODE = $LASTEXITCODE
}
if ($env:CONDUIT_IMAGE_TEST_SCENARIO -ceq 'source-unavailable') { function git { $global:LASTEXITCODE = 128 } }
try {
    & $env:CONDUIT_IMAGE_TEST_PUBLISHER -Service $Service -ArtifactDirectory $ArtifactDirectory -Runtime $Runtime -Version $Version -CommitSha $CommitSha -BuildTimestamp $BuildTimestamp -WarningBaselinePath $WarningBaselinePath
    exit 0
}
catch { [Console]::Error.WriteLine('Owned image publisher failed; type=' + $_.Exception.GetType().FullName + '; line=' + [int]$_.InvocationInfo.ScriptLineNumber + '.'); exit 1 }
'@, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($script:fakeDotnet, @'
param([Parameter(Mandatory=$true)][string]$EncodedArguments)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
try {
    [string[]]$arguments = @([Text.UTF8Encoding]::new($false,$true).GetString([Convert]::FromBase64String($EncodedArguments)) | ConvertFrom-Json)
    $root = $env:CONDUIT_IMAGE_TEST_ROOT; $scenario = $env:CONDUIT_IMAGE_TEST_SCENARIO
    $recordPath = Join-Path $root ('commands/' + [Guid]::NewGuid().ToString('N') + '.json')
    $record = [ordered]@{ arguments = $arguments }
    [IO.File]::WriteAllText($recordPath, (ConvertTo-Json -InputObject $record -Depth 8), [Text.UTF8Encoding]::new($false))
    if ($arguments[0] -cne 'publish') { throw 'Unexpected owned operation.' }
    function Get-Argument([string]$Name) {
        $index = [Array]::IndexOf($arguments,$Name)
        if ($index -lt 0 -or $index+1 -ge $arguments.Length) { throw 'Owned argument missing.' }
        return $arguments[$index+1]
    }
    $properties = @{}
    foreach ($argument in $arguments) {
        if ($argument -cmatch '^-p:([^=]+)=(.*)$') { $properties[$Matches[1]] = $Matches[2].Replace('%3B',';').Replace('%3b',';').Replace('%2C',',').Replace('%2c',',').Replace('%25','%') }
    }
    $assembly = [string]$properties.ConduitNativeToolchainEvidenceAssemblyName
    $role = if ($assembly -ceq 'ConduitLLM.HealthProbe') { 'health-probe' } elseif ($assembly -cin @('ConduitLLM.Admin','ConduitLLM.Gateway')) { 'application' } else { throw 'Unexpected owned assembly.' }
    if (($scenario -ceq 'fail-main' -and $role -ceq 'application') -or ($scenario -ceq 'fail-health' -and $role -ceq 'health-probe')) {
        [Console]::Error.WriteLine('Owned image publish failure.'); exit 7
    }
    $output = Get-Argument '--output'; $rid = Get-Argument '--runtime'
    $executableName = $assembly + $(if ($rid -ceq 'win-x64') { '.exe' } else { '' })
    $intermediate = [string]$properties.NativeIntermediateOutputPath; $nativeOutput = [string]$properties.NativeOutputPath
    [IO.Directory]::CreateDirectory($intermediate) | Out-Null
    [IO.Directory]::CreateDirectory($nativeOutput) | Out-Null
    $object = Join-Path $intermediate ($assembly + $(if ($rid -ceq 'win-x64') { '.obj' } else { '.o' }))
    $binary = Join-Path $nativeOutput $executableName
    [IO.File]::WriteAllText($object, 'owned compiled object ' + $assembly)
    [IO.File]::WriteAllText($binary, 'owned image native binary ' + $assembly)
    [IO.File]::Copy($binary, (Join-Path $output $executableName))
    [IO.Directory]::CreateDirectory((Join-Path $output 'nested')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $output ($assembly + '.dbg')), 'root symbols')
    [IO.File]::WriteAllText((Join-Path $output ('nested/' + $assembly + '.pdb')), 'nested symbols')
    if ($role -ceq 'application') {
        [IO.File]::WriteAllText((Join-Path $output 'appsettings.fixture.json'), '{"owned":true}', [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $output 'nested/café-雪.json'), '{"unicode":"雪 😀"}', [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllBytes((Join-Path $output '.host-hidden'), [byte[]]@(0,1,2,255))
        if ($IsWindows) { [IO.File]::SetAttributes((Join-Path $output '.host-hidden'), [IO.FileAttributes]::Hidden) }
        if ($scenario -ceq 'payload-collision') {
            [IO.File]::WriteAllText((Join-Path $output ('conduit-health-probe' + $(if ($rid -ceq 'win-x64') { '.exe' } else { '' }))), 'must not overwrite app health collision')
        }
        if ($scenario -ceq 'copied-binary-mismatch') { [IO.File]::WriteAllText((Join-Path $output $executableName), 'different copied native executable') }
    }
    elseif ($scenario -ceq 'health-sidecar') { [IO.File]::WriteAllText((Join-Path $output 'unexpected.runtimeconfig.json'), '{}') }
    $capturedIntermediate = $intermediate
    if ($scenario -ceq 'capture-root-mismatch') {
        $capturedIntermediate = (Join-Path $intermediate 'unexpected') + [IO.Path]::DirectorySeparatorChar
        [IO.Directory]::CreateDirectory($capturedIntermediate) | Out-Null
    }
    $toolRoot = $env:CONDUIT_IMAGE_TEST_TOOLROOT; $driver = $env:CONDUIT_IMAGE_TEST_DRIVER
    $capture = [ordered]@{
        NETCoreSdkVersion='10.0.401'; RuntimeIdentifier=$rid; PublishAot='true'; Configuration='Release'
        IlcHostPackagePath=$toolRoot; RuntimePackagePath=(Join-Path $toolRoot 'runtime'); IlcToolsPath=(Join-Path $toolRoot 'tools'); ILCompilerTargetsPath=(Join-Path $toolRoot 'selected.targets')
        CppCompilerAndLinker=$driver; CppLinker=$driver; LinkerFlavor=''; ObjCopyName=''; IlcUseEnvironmentalTools=''
        _hostOS=$(if($IsWindows){'win'}else{'linux'}); _hostArchitecture='x64'; _targetOS=$(if($rid -ceq 'win-x64'){'win'}else{'linux'}); _targetArchitecture='x64'
        NativeIntermediateOutputPath=$capturedIntermediate; NativeOutputPath=$nativeOutput; NativeObject=$object; NativeBinary=$binary; AssemblyName=$assembly
        SelfContained='true'; StripSymbols='true'; ConduitAotAudit=[string]$properties.ConduitAotAudit; UseSharedCompilation='false'
        Version=[string]$properties.Version; InformationalVersion=[string]$properties.InformationalVersion; ConduitCommitSha=[string]$properties.ConduitCommitSha; ConduitBuildTimestamp=[string]$properties.ConduitBuildTimestamp
    }
    $record['role']=$role; $record['capture']=$capture
    [IO.File]::WriteAllText($recordPath, (ConvertTo-Json -InputObject $record -Depth 8), [Text.UTF8Encoding]::new($false))
    $lines = [Collections.Generic.List[string]]::new()
    foreach($name in $capture.Keys){$lines.Add($name+'='+[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$capture[$name])))}
    [IO.File]::WriteAllLines([string]$properties.ConduitNativeToolchainEvidencePath,$lines,[Text.UTF8Encoding]::new($false))
    if($scenario -ceq 'health-linker-warning' -and $role -ceq 'health-probe'){
        [Console]::Out.WriteLine('ILC : AOT analysis warning IL3050: ConduitLLM.HealthProbe.OwnedFixture.Unsupported() [fixture.csproj]')
    }
    [Console]::Out.WriteLine('Owned image native publish complete.')
    exit 0
}
catch{[Console]::Error.WriteLine('Owned image dotnet failed; type='+$_.Exception.GetType().FullName+'; line='+[int]$_.InvocationInfo.ScriptLineNumber+'.');exit 8}
'@, [Text.UTF8Encoding]::new($false))

    . (Join-Path $PSScriptRoot 'native-image-publish-evidence.ps1')
    $names = @('CONDUIT_IMAGE_TEST_ROOT','CONDUIT_IMAGE_TEST_SCENARIO','CONDUIT_IMAGE_TEST_PUBLISHER',
        'CONDUIT_IMAGE_TEST_DOTNET','CONDUIT_IMAGE_TEST_DRIVER','CONDUIT_IMAGE_TEST_TOOLROOT')
    $parentEnvironment = @{}
    foreach ($name in $names) { $parentEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }

    $admin = Invoke-ImageCase 'admin' 'success' $true 2
    $gateway = Invoke-ImageCase 'gateway' 'success' $true 2
    $unknown = Invoke-ImageCase 'gateway' 'source-unavailable' $true 2
    $adminImage = Read-Json (Join-Path $admin.Artifact 'evidence/image-publish.json')
    $gatewayImage = Read-Json (Join-Path $gateway.Artifact 'evidence/image-publish.json')
    $unknownImage = Read-Json (Join-Path $unknown.Artifact 'evidence/image-publish.json')
    Assert-Contract ($adminImage.buildId -cne $gatewayImage.buildId -and $gatewayImage.buildId -cne $unknownImage.buildId -and
        $adminImage.buildId -cne $unknownImage.buildId) 'Separate image invocations reused a build GUID.'
    $allDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($fixture in @($admin,$gateway,$unknown)) {
        foreach ($record in $fixture.Records) {
            Assert-Contract ($allDirectories.Add([string]$record.capture.NativeIntermediateOutputPath) -and
                $allDirectories.Add([string]$record.capture.NativeOutputPath)) 'Separate image invocation reused native compile/link directories.'
        }
    }
    $cases++

    foreach ($case in @(
        @{Scenario='fail-main';Count=1}, @{Scenario='fail-health';Count=2}, @{Scenario='health-sidecar';Count=2},
        @{Scenario='payload-collision';Count=2}, @{Scenario='copied-binary-mismatch';Count=1},
        @{Scenario='capture-root-mismatch';Count=1}, @{Scenario='health-linker-warning';Count=2},
        @{Scenario='linked-output';Count=0}, @{Scenario='nonempty-output';Count=0}
    )) { Invoke-ImageCase 'gateway' $case.Scenario $false $case.Count | Out-Null }
    $application = Read-Json (Join-Path $gateway.Artifact 'evidence/application.json')
    if ($application.source.status -ceq 'git') { Invoke-ImageCase 'gateway' 'commit-mismatch' $false 0 | Out-Null }
    else { $skipped.Add('Git unavailable; prepublish declared-commit mismatch rejection not claimed.') }

    $evidenceHashes = @{}
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $gateway.Artifact 'evidence') -File -Filter '*.json') { $evidenceHashes[$file.FullName] = Get-FileDigest $file.FullName }
    $rerun = Invoke-OwnedChild $script:wrapper @('-Service','gateway','-ArtifactDirectory',$gateway.Artifact,
        '-Runtime',$script:rid,'-Version',$fixtureVersion,'-CommitSha','dev','-BuildTimestamp',$fixtureTimestamp,
        '-WarningBaselinePath',$script:warningBaseline) (Get-ChildEnvironment $gateway.Root 'success')
    Assert-Contract ($rerun.ExitCode -ne 0 -and (Get-Records $gateway.Root).Count -eq 2 -and
        [IO.File]::ReadAllText((Join-Path $gateway.Artifact 'native-image-publish.claim')).Trim() -ceq $gatewayImage.buildId) 'Image rerun rewrote its original claim or invoked dotnet.'
    foreach ($path in $evidenceHashes.Keys) { Assert-Contract ((Get-FileDigest $path) -ceq $evidenceHashes[$path]) 'Image rerun rewrote immutable evidence.' }
    $cases++

    foreach ($health in @($false,$true)) {
        $executable = Join-Path $gateway.Artifact ('payload/' + (Get-ExecutableName 'gateway' -Health:$health))
        $bytes = [IO.File]::ReadAllBytes($executable); $mtime = [IO.File]::GetLastWriteTimeUtc($executable)
        $changed = [byte[]]$bytes.Clone(); $changed[$changed.Length-1] = $changed[$changed.Length-1] -bxor 1
        try {
            [IO.File]::WriteAllBytes($executable,$changed); [IO.File]::SetLastWriteTimeUtc($executable,$mtime)
            Assert-Contract ([IO.File]::GetLastWriteTimeUtc($executable) -eq $mtime) 'Image same-length payload mutation did not restore its original timestamp.'
            Assert-EvidenceRejected (Join-Path $gateway.Artifact 'evidence') (Join-Path $gateway.Artifact 'payload') $(if($health){'health executable bytes'}else{'application executable bytes'})
        }
        finally { [IO.File]::WriteAllBytes($executable,$bytes); [IO.File]::SetLastWriteTimeUtc($executable,$mtime) }
    }

    $externalManifest = Join-Path $temporaryRoot 'external-sentinel'
    [IO.File]::Copy((Join-Path $gateway.Artifact 'evidence/application.json'), $externalManifest)
    $externalDigest = Get-FileDigest $externalManifest
    foreach ($mutation in @('cross-build','service','rid','reference-hash','escaping-reference','health-role')) {
        $copy = Copy-Evidence $gateway
        $imagePath = Join-Path $copy 'image-publish.json'
        $image = Read-Json $imagePath
        switch ($mutation) {
            'cross-build' {
                $otherApplication = Read-Json (Join-Path $unknown.Artifact 'evidence/application.json')
                Assert-Contract ($otherApplication.runtimeFilesSha256 -ceq $application.runtimeFilesSha256) 'Cross-build fixture does not have identical payload bytes.'
                [IO.File]::Copy((Join-Path $unknown.Artifact 'evidence/application.json'),(Join-Path $copy 'application.json'),$true)
                $image.application.sha256 = Get-FileDigest (Join-Path $copy 'application.json')
            }
            'service' { $image.service = 'admin' }
            'rid' { $image.rid = $(if($script:rid -ceq 'linux-x64'){'win-x64'}else{'linux-x64'}) }
            'reference-hash' { $image.healthProbe.sha256 = '0' * 64 }
            'escaping-reference' {
                $image.application.path = '../external-sentinel'
                $image.application.sha256 = $externalDigest
            }
            'health-role' {
                $healthPath = Join-Path $copy 'health-probe.json'
                $healthManifest = Read-Json $healthPath; $healthManifest.service = 'gateway'; Write-Json $healthPath $healthManifest
                $image.healthProbe.sha256 = Get-FileDigest $healthPath
            }
        }
        Write-Json $imagePath $image
        Assert-EvidenceRejected $copy (Join-Path $gateway.Artifact 'payload') $mutation
        Assert-Contract ((Get-FileDigest $externalManifest) -ceq $externalDigest) 'Image validation changed the external valid child-manifest sentinel.'
    }
    foreach ($path in $evidenceHashes.Keys) { Assert-Contract ((Get-FileDigest $path) -ceq $evidenceHashes[$path]) 'Payload/evidence validation modified original immutable evidence.' }
    $singleServiceAudit = New-OwnedDirectory
    $singleServiceReports = Join-Path $singleServiceAudit 'reports'
    [IO.Directory]::CreateDirectory($singleServiceReports) | Out-Null
    [IO.File]::WriteAllText((Join-Path $singleServiceReports 'admin-publish.log'), 'owned clean publish')
    $singleServiceBaseline = Join-Path $singleServiceAudit 'baseline.json'
    Write-Json $singleServiceBaseline @{ schemaVersion = 1; description = 'preserve owned baseline sentinel'; groups = @(
        @{ service = 'Gateway'; project = 'ConduitLLM.Gateway'; code = 'IL3050'; file = 'ILC/gateway'; count = 7 }
    ) }
    $singleServiceDigest = Get-FileDigest $singleServiceBaseline
    $singleServiceResult = Invoke-OwnedChild (Join-Path $PSScriptRoot 'evaluate-native-linker-warnings.ps1') @(
        '-ReportDirectory', $singleServiceReports, '-BaselinePath', $singleServiceBaseline, '-Runtime', $script:rid,
        '-Services', 'Admin', '-UpdateBaseline') @{}
    Assert-Contract ($singleServiceResult.ExitCode -ne 0 -and (Get-FileDigest $singleServiceBaseline) -ceq $singleServiceDigest -and
        @(([IO.DirectoryInfo]::new($singleServiceReports)).EnumerateFileSystemInfos()).Count -eq 1) 'Single-service linker evaluation overwrote the combined baseline or wrote reports before rejecting UpdateBaseline.'
    $cases++
    foreach ($name in $names) { Assert-Contract ([Environment]::GetEnvironmentVariable($name) -ceq $parentEnvironment[$name]) 'Private image child altered parent environment.' }
}
catch { $proofFailure = $_.Exception }
finally {
    $linksRemoved = $true
    foreach ($link in $ownedLinks) {
        try { if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force } }
        catch { $linksRemoved = $false; $cleanupFailure = $_.Exception.GetType().FullName }
    }
    try {
        Assert-Contract $linksRemoved 'Image contract link cleanup failed.'
        if ([IO.Directory]::Exists($temporaryRoot)) {
            $resolved = [IO.Path]::GetFullPath($temporaryRoot)
            $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
            Assert-Contract ([IO.Path]::GetDirectoryName($resolved).TrimEnd([IO.Path]::DirectorySeparatorChar) -ceq $parent -and
                [IO.Path]::GetFileName($resolved) -cmatch '^conduit-image-contract-[0-9a-f]{32}$') 'Image contract cleanup ownership was rejected.'
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
    catch { $cleanupFailure = $_.Exception.GetType().FullName }
}
if ($null -ne $cleanupFailure) { throw 'Native image contract fixture cleanup failed.' }
if ($null -ne $proofFailure) { throw $proofFailure }
foreach ($message in $skipped) { Write-Host ('SKIP: ' + $message) }
Write-Host ('PASS: ' + $cases + ' assembled native image publisher contracts; both services and independently bound complete payload evidence.')
