# Test-only original-byte OCI fixtures. Dot-sourcing defines functions only.
# Synthetic source metadata exercises binding contracts; these are not builds.
function New-NativeBenchmarkPackagingFixture {
    param([Parameter(Mandatory)][string] $ParentDirectory,
        [Parameter(Mandatory)][ValidateSet('admin', 'gateway')][string] $Service,
        [string] $Revision = ('a' * 40), [bool] $SourceDirty = $false,
        [switch] $UnavailableSource, [switch] $SelectedManifestRequest,
        [ValidateSet('success', 'oci-hold-classic', 'oci-hold-no-descriptors')][string] $Scenario = 'success')
    $temporaryRoot = [IO.Path]::Combine($ParentDirectory, 'sealed-package-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    . (Join-Path $PSScriptRoot 'native-image-verification.ps1')
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'test-native-image-verification.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) { throw 'Benchmark packaging fixture definitions did not parse.' }
    foreach ($name in @('Assert-VerificationContract', 'Write-VerificationJson', 'Read-VerificationJson', 'Get-VerificationHash',
        'Write-VerificationArchive', 'Write-VerificationOciArchive', 'New-VerificationFixture')) {
        $definitions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true))
        if ($definitions.Count -ne 1) { throw 'Benchmark packaging fixture definition is ambiguous.' }
        . ([ScriptBlock]::Create($definitions[0].Extent.Text))
    }
    $fixture = New-VerificationFixture $Service $Scenario $(if ($UnavailableSource) { '' } else { $Revision }) $SourceDirty ('example.invalid/conduit-' + $Service)
    if ($SelectedManifestRequest) { $fixture.requestedImage = 'example.invalid/conduit-' + $Service + '@' + $fixture.oci.selectedDescriptor.digest }
    $docker = Join-Path $fixture.root 'package Docker café 雪.ps1'
    $encodedRoot = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($fixture.root))
    $prefix = "param([Parameter(ValueFromRemainingArguments = `$true)][string[]] `$DockerArguments)`n" +
        "`$root = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$encodedRoot'))`n"
    $body = @'
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
function Read-FixtureJson([string]$Path) { return [IO.File]::ReadAllText($Path) | ConvertFrom-Json -AsHashtable -DateKind String }
function Write-FixtureJson([string]$Path, $Value) { [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 32), [Text.UTF8Encoding]::new($false)) }
$ordinal = @(Get-ChildItem -LiteralPath (Join-Path $root 'commands') -File).Count + 1
Write-FixtureJson (Join-Path $root ('commands/' + $ordinal.ToString('D3') + '.json')) @{
    arguments = @($DockerArguments); reportPresent = [IO.File]::Exists((Join-Path $root 'artifact/packaging-evidence.json'))
    benchmarkCredentialPresent = -not [string]::IsNullOrEmpty($env:CONDUIT_NATIVE_BENCHMARK_API_KEY)
    benchmarkCredentialExists = $null -ne [Environment]::GetEnvironmentVariable('CONDUIT_NATIVE_BENCHMARK_API_KEY')
}
$image = Read-FixtureJson (Join-Path $root 'image.json')
$active = Join-Path $root 'active.json'
if ($DockerArguments[0] -ceq 'image' -and $DockerArguments[1] -ceq 'inspect') {
    if ($DockerArguments.Count -ne 5) { throw 'Unexpected packaging image inspection.' }
    [Console]::Out.WriteLine((ConvertTo-Json -InputObject $image -Depth 32 -Compress)); exit 0
}
if ($DockerArguments[0] -ceq 'container' -and $DockerArguments[1] -ceq 'create') {
    $networkIndex = [Array]::IndexOf($DockerArguments, '--network')
    if ($DockerArguments[-1] -cne $image.Id -or $DockerArguments[2] -cne '--platform' -or $DockerArguments[3] -cne 'linux/amd64' -or
        $networkIndex -lt 0 -or $DockerArguments[$networkIndex + 1] -cne 'none') { throw 'Packaging did not pin the local image and isolated network.' }
    $name = $DockerArguments[[Array]::IndexOf($DockerArguments, '--name') + 1]
    $labels = @{} + $image.Config.Labels
    for ($index = 0; $index -lt $DockerArguments.Count; $index++) {
        if ($DockerArguments[$index] -ceq '--label') { $pair = $DockerArguments[++$index].Split('=', 2); $labels[$pair[0]] = $pair[1] }
    }
    $config = $image.Config; $config.Labels = $labels
    $container = @{ Id = ('c' * 64); Name = '/' + $name; Image = $image.Id; Config = $config
        HostConfig = @{ NetworkMode = 'none' }; State = @{ Running = $false }
        ImageManifestDescriptor = Read-FixtureJson (Join-Path $root 'selected-descriptor.json') }
    Write-FixtureJson $active $container; [Console]::Out.WriteLine($container.Id); exit 0
}
if ($DockerArguments[0] -ceq 'container' -and $DockerArguments[1] -ceq 'inspect') {
    if ($DockerArguments.Count -ne 5 -or $DockerArguments[2] -cne '--format' -or $DockerArguments[3] -cne '{{json .}}') { throw 'Unexpected packaging container inspection.' }
    if (-not [IO.File]::Exists($active)) { [Console]::Error.WriteLine('Error: No such object: ' + $DockerArguments[-1]); exit 1 }
    $container = Read-FixtureJson $active
    if ($DockerArguments[-1] -cnotin @($container.Id, $container.Name.Substring(1))) { throw 'Packaging inspected an unrelated container.' }
    [Console]::Out.WriteLine((ConvertTo-Json -InputObject $container -Depth 32 -Compress)); exit 0
}
if ($DockerArguments[0] -ceq 'image' -and $DockerArguments[1] -ceq 'save') {
    if ($DockerArguments.Count -ne 5 -or $DockerArguments[2] -cne '--output' -or $DockerArguments[-1] -cne $image.Id) { throw 'Packaging changed the original OCI save root.' }
    [IO.File]::Copy((Join-Path $root 'fixture-save.tar'), $DockerArguments[3], $false); exit 0
}
if ($DockerArguments[0] -ceq 'container' -and $DockerArguments[1] -ceq 'export') {
    if ($DockerArguments.Count -ne 5 -or $DockerArguments[2] -cne '--output' -or $DockerArguments[-1] -cne ('c' * 64)) { throw 'Packaging exported an unrelated container.' }
    [IO.File]::Copy((Join-Path $root 'fixture.tar'), $DockerArguments[3], $false); exit 0
}
if ($DockerArguments[0] -ceq 'container' -and $DockerArguments[1] -ceq 'rm') {
    if ($DockerArguments.Count -ne 3 -or $DockerArguments[2] -cne ('c' * 64) -or -not [IO.File]::Exists($active)) { throw 'Packaging removed an unrelated container.' }
    [IO.File]::Delete($active); [Console]::Out.WriteLine('c' * 64); exit 0
}
throw 'Unexpected packaging fixture command.'
'@
    [IO.File]::WriteAllText($docker, $prefix + $body, [Text.UTF8Encoding]::new($false))
    $oldCredential = [Environment]::GetEnvironmentVariable('CONDUIT_NATIVE_BENCHMARK_API_KEY')
    try {
        [Environment]::SetEnvironmentVariable('CONDUIT_NATIVE_BENCHMARK_API_KEY', [NullString]::Value)
        $results = @(Get-NativeImagePackagingEvidence -Image $fixture.requestedImage -Service $Service -ArtifactDirectory $fixture.artifact `
            -DockerCommandPath $docker -DockerCommandTimeoutSeconds 10 -CleanupTimeoutSeconds 15)
    }
    finally {
        if ($null -eq $oldCredential) { [Environment]::SetEnvironmentVariable('CONDUIT_NATIVE_BENCHMARK_API_KEY', [NullString]::Value) }
        else { [Environment]::SetEnvironmentVariable('CONDUIT_NATIVE_BENCHMARK_API_KEY', [string]$oldCredential) }
        if ([Environment]::GetEnvironmentVariable('CONDUIT_NATIVE_BENCHMARK_API_KEY') -cne $oldCredential) {
            throw 'Benchmark package fixture did not restore the exact parent credential state.'
        }
    }
    if ($results.Count -ne 1 -or -not $results[0].cleanupConfirmed -or [IO.File]::Exists((Join-Path $fixture.root 'active.json'))) {
        throw 'Benchmark fixture did not complete real packaging verification and owned cleanup.'
    }
    foreach ($recordPath in [IO.Directory]::EnumerateFiles((Join-Path $fixture.root 'commands'))) {
        $record = Read-VerificationJson $recordPath
        if ($record.benchmarkCredentialExists -or $record.benchmarkCredentialPresent -or $record.reportPresent) { throw 'Benchmark package fixture exposed credentials or success before cleanup.' }
    }
    $fixture['packaging'] = $results[0]
    $fixture['packageHash'] = Get-VerificationHash (Join-Path $fixture.artifact 'packaging-evidence.json')
    $fixture['image'] = Read-VerificationJson (Join-Path $fixture.root 'image.json')
    return ,$fixture
}

function Assert-NativeBenchmarkFixtureIdentity {
    param($Identity, $Fixture)
    $requestedDigest = $Fixture.requestedImage.Substring($Fixture.requestedImage.LastIndexOf('@') + 1)
    $requestedKind = if ($requestedDigest -ceq $Fixture.oci.rootDescriptor.digest) { 'index' } else { 'manifest' }
    if ($Identity.Count -ne 18 -or $Identity.schemaVersion -ne 1 -or $Identity.kind -cne 'conduit-native-benchmark-image' -or
        $Identity.service -cne $Fixture.service -or $Identity.rid -cne 'linux-x64' -or
        $Identity.buildId -cne $Fixture.buildId -or $Identity.source.status -cne 'git' -or
        $Identity.source.revision -cne $Fixture.source.revision -or $Identity.source.dirty -isnot [bool] -or $Identity.source.dirty -or
        $Identity.localImageId -cne $Fixture.localImageId -or $Identity.requestedImage -cne $Fixture.requestedImage -or
        $Identity.requestedDigest -cne $requestedDigest -or $Identity.requestedDigestKind -cne $requestedKind -or
        $Identity.rootDescriptor.digest -cne $Fixture.oci.rootDescriptor.digest -or
        $Identity.rootDescriptor.mediaType -cne $Fixture.oci.rootDescriptor.mediaType -or
        $Identity.rootDescriptor.size -ne $Fixture.oci.rootDescriptor.size -or
        $Identity.selectedManifestDescriptor.digest -cne $Fixture.oci.selectedDescriptor.digest -or
        $Identity.selectedManifestDescriptor.mediaType -cne $Fixture.oci.selectedDescriptor.mediaType -or
        $Identity.selectedManifestDescriptor.size -ne $Fixture.oci.selectedDescriptor.size -or
        $Identity.selectedManifestDescriptor.platform.os -cne 'linux' -or
        $Identity.selectedManifestDescriptor.platform.architecture -cne 'amd64' -or
        $Identity.configDigest -cne $Fixture.oci.configDigest -or $Identity.runtimeFilesSha256 -cne $Fixture.runtimeFilesSha256 -or
        $Identity.packagingEvidence.path -cne 'packaging-evidence.json' -or $Identity.packagingEvidence.sha256 -cne $Fixture.packageHash) {
        throw 'Benchmark native evidence changed independently constructed source, build, package or OCI roles.'
    }
    if ($Identity.manifestHashes.Count -ne 3) { throw 'Benchmark evidence omitted an executable manifest.' }
    foreach ($name in @('application.json', 'health-probe.json', 'image-publish.json')) {
        if ($Identity.manifestHashes[$name] -cne $Fixture.hashes[$name]) { throw 'Benchmark evidence changed original embedded manifest bytes.' }
    }
    foreach ($role in @('application', 'healthProbe')) {
        $name = if ($role -ceq 'application') { $Fixture.main } else { 'conduit-health-probe' }
        $file = Join-Path $Fixture.payload $name
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($file))).ToLowerInvariant()
        $tool = $Identity[$role].toolchain; $expected = $Fixture.toolchain
        if ($Identity[$role].executable.path -cne $name -or $Identity[$role].executable.bytes -ne ([IO.FileInfo]::new($file)).Length -or
            $Identity[$role].executable.sha256 -cne $hash -or $tool.sdkVersion -cne $expected.sdkVersion -or
            $tool.ilCompilerPackage.id -cne $expected.ilCompilerPackage.id -or $tool.ilCompilerPackage.version -cne $expected.ilCompilerPackage.version -or
            $tool.ilCompiler.path -cne $expected.ilCompiler.path -or $tool.ilCompiler.sha256 -cne $expected.ilCompiler.sha256 -or
            $tool.ilCompiler.bytes -ne $expected.ilCompiler.bytes -or $tool.nativeLinkerDriver.path -cne $expected.nativeLinkerDriver.path -or
            $tool.nativeLinkerDriver.sha256 -cne $expected.nativeLinkerDriver.sha256 -or $tool.nativeLinkerDriver.bytes -ne $expected.nativeLinkerDriver.bytes -or
            $tool.nativeLinkerDriver.version -cne $expected.nativeLinkerDriver.version -or $tool.targetOs -cne 'linux' -or $tool.targetArchitecture -cne 'x64') {
            throw 'Benchmark native evidence changed independently constructed executable or selected toolchain identity.'
        }
    }
}

function Copy-NativeBenchmarkPackagingFixture {
    param([Parameter(Mandatory)]$Fixture, [Parameter(Mandatory)][string]$Directory)
    . (Join-Path $PSScriptRoot 'native-publish-evidence.ps1')
    Assert-NativeEvidencePathAncestry $Directory
    if ([IO.Directory]::Exists($Directory) -or [IO.File]::Exists($Directory)) { throw 'Benchmark fixture copy destination is not fresh.' }
    [IO.Directory]::CreateDirectory($Directory) | Out-Null
    foreach ($file in ([IO.DirectoryInfo]::new($Fixture.artifact)).EnumerateFiles('*', [IO.SearchOption]::AllDirectories)) {
        Assert-NativeEvidenceRegularFile $file.FullName
        $destination = Join-Path $Directory ([IO.Path]::GetRelativePath($Fixture.artifact, $file.FullName))
        Assert-NativeEvidencePathAncestry $destination
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($file.FullName, $destination, $false)
    }
}

function Assert-NativeBenchmarkFixtureBinding {
    param($Binding, $Fixture)
    $requestedDigest = $Fixture.requestedImage.Substring($Fixture.requestedImage.LastIndexOf('@') + 1)
    $requestedKind = if ($requestedDigest -ceq $Fixture.oci.rootDescriptor.digest) { 'index' } else { 'manifest' }
    if ($Binding.schemaVersion -isnot [int] -and $Binding.schemaVersion -isnot [long]) { throw 'Benchmark binding schema is not numeric.' }
    if ($Binding.Count -ne 10 -or $Binding.schemaVersion -ne 1 -or $Binding.kind -cne 'conduit-native-benchmark-image-binding' -or
        $Binding.packagingEvidence.Count -ne 2 -or $Binding.packagingEvidence.path -cne 'packaging-evidence.json' -or
        $Binding.packagingEvidence.sha256 -cne $Fixture.packageHash -or $Binding.buildId -cne $Fixture.buildId -or
        $Binding.sourceRevision -cne $Fixture.source.revision -or $Binding.requestedImage -cne $Fixture.requestedImage -or
        $Binding.requestedDigest -cne $requestedDigest -or $Binding.requestedDigestKind -cne $requestedKind -or
        $Binding.observedLocalImageId -cne $Fixture.localImageId) { throw 'Benchmark binding changed independent package, source, build or requested image identity.' }
    $selected = $Binding.selectedManifestDescriptor; $expected = $Fixture.oci.selectedDescriptor
    if ($selected.mediaType -cne $expected.mediaType -or $selected.digest -cne $expected.digest -or $selected.size -ne $expected.size -or
        $selected.platform.os -cne 'linux' -or $selected.platform.architecture -cne 'amd64') { throw 'Benchmark binding changed the original selected manifest.' }
    if ($Fixture.oci.rootDescriptor.digest -ceq $Fixture.oci.selectedDescriptor.digest -or
        $Fixture.oci.configDigest -cin @($Fixture.oci.rootDescriptor.digest, $Fixture.oci.selectedDescriptor.digest) -or
        ('sha256:' + $Fixture.oci.wrapperSha256) -cin @($Fixture.oci.rootDescriptor.digest, $Fixture.oci.selectedDescriptor.digest, $Fixture.oci.configDigest)) {
        throw 'Benchmark fixture did not distinguish original index, selected manifest, config and wrapper bytes.'
    }
}
