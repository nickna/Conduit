[CmdletBinding()]
param([string]$ArtifactDirectory)

$ErrorActionPreference = 'Stop'
if (-not $IsLinux) { throw 'The persisted converter spike requires a Linux x64 NativeAOT runner.' }
if ([string]::IsNullOrWhiteSpace($env:DATABASE_URL)) { throw 'DATABASE_URL is required for the converter proof.' }
. (Join-Path $PSScriptRoot 'native-publish-build.ps1')
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $repository 'artifacts/native-aot'
}
$root = [IO.Path]::GetFullPath($ArtifactDirectory)
$runtime = Join-Path $root 'runtime/persistence-converters'
$symbols = Join-Path $root 'symbols/persistence-converters'
$reports = Join-Path $root 'reports/persistence-converters'
foreach ($path in @($runtime, $symbols, $reports)) {
    Assert-NativeEvidencePathAncestry $path
    if (Test-Path -LiteralPath $path) { throw 'Converter evidence requires fresh output directories.' }
}
$buildId = [Guid]::NewGuid().ToString('N')
$assembly = 'ConduitLLM.PersistenceConverterAotTests'
$intermediate = (Join-Path $root "native-intermediates/$buildId/persistence-converters") + [IO.Path]::DirectorySeparatorChar
$nativeOutput = (Join-Path $root "native-outputs/$buildId/persistence-converters") + [IO.Path]::DirectorySeparatorChar
foreach ($path in @($intermediate, $nativeOutput)) { Assert-NativeEvidencePathAncestry $path }
foreach ($path in @($runtime, $symbols, $reports)) { [void][IO.Directory]::CreateDirectory($path) }
function Write-ConverterOriginal([string]$Path, [string]$Text) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write([Text.UTF8Encoding]::new($false).GetBytes($Text)); $stream.Flush($true) }
    finally { $stream.Dispose() }
}
Write-ConverterOriginal (Join-Path $reports 'publish.claim') $buildId
$source = Get-NativePublishSource -RepositoryRoot $repository
$started = [DateTime]::UtcNow
$timestamp = $started.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
$capturePath = Join-Path $reports 'toolchain.b64'
$targets = Join-Path $PSScriptRoot 'native-publish-toolchain.targets'
$arguments = @(
    'publish', (Join-Path $repository "Tests/$assembly/$assembly.csproj"),
    '--configuration', 'Release', '--runtime', 'linux-x64', '--self-contained', 'true',
    '--output', $runtime, '--nologo', '--tl:off', '--maxcpucount:1', '--verbosity', 'minimal',
    '-p:PublishAot=true', '-p:ConduitAotAudit=true', '-p:StripSymbols=true', '-p:UseSharedCompilation=false', '-nr:false',
    "-p:NativeIntermediateOutputPath=$(ConvertTo-MSBuildPropertyValue $intermediate)",
    "-p:NativeOutputPath=$(ConvertTo-MSBuildPropertyValue $nativeOutput)",
    "-p:CustomAfterMicrosoftCommonTargets=$(ConvertTo-MSBuildPropertyValue $targets)",
    "-p:ConduitNativeToolchainEvidencePath=$(ConvertTo-MSBuildPropertyValue $capturePath)",
    "-p:ConduitNativeToolchainEvidenceAssemblyName=$assembly"
)
if ($source.status -eq 'git') {
    $arguments += "-p:ConduitCommitSha=$($source.revision)"
    $arguments += "-p:ConduitBuildTimestamp=$timestamp"
}
$timer = [Diagnostics.Stopwatch]::StartNew()
& dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $reports 'persistenceconverters-publish.log')
$publishExit = $LASTEXITCODE
$timer.Stop()
if ($publishExit -ne 0) { throw 'Native converter publish failed; the original diagnostics are retained.' }
$completed = [DateTime]::UtcNow
$executable = Join-Path $runtime $assembly
$capture = Read-NativeToolchainCapture -Path $capturePath
Assert-NativePublishCompileOutputs -Capture $capture -IntermediateDirectory $intermediate -OutputDirectory $nativeOutput `
    -AssemblyName $assembly -ExecutableName $assembly -RuntimeExecutable $executable -Runtime 'linux-x64'
$toolchain = Get-NativePublishToolchain -Capture $capture -ExpectedAssembly $assembly -ExpectedAudit $true -Runtime 'linux-x64'
if ($source.status -eq 'git' -and ($capture.ConduitCommitSha -cne $source.revision -or
    $capture.ConduitBuildTimestamp -cne $timestamp)) { throw 'Converter assembly metadata differs from its source.' }
# This spike always uses an empty diagnostic allowance. It cannot acquire an
# exception by updating either production service's warning baseline.
$emptyBaseline = Join-Path $reports 'zero-warning-baseline.json'
Write-ConverterOriginal $emptyBaseline '{"schemaVersion":1,"groups":[]}'
& (Join-Path $PSScriptRoot 'evaluate-native-linker-warnings.ps1') -ReportDirectory $reports `
    -BaselinePath $emptyBaseline -Runtime 'linux-x64' -Services PersistenceConverters
foreach ($symbol in @(Get-ChildItem -LiteralPath $runtime -File -Force | Where-Object Extension -in @('.dbg', '.pdb'))) {
    Assert-NativeEvidenceRegularFile $symbol.FullName
    Move-Item -LiteralPath $symbol.FullName -Destination (Join-Path $symbols $symbol.Name)
}
$identity = Get-NativeRuntimeIdentity -RuntimeDirectory $runtime -ExecutableName $assembly
$header = [byte[]]::new(20)
$file = [IO.File]::OpenRead($executable)
try { $file.ReadExactly($header, 0, $header.Length) } finally { $file.Dispose() }
if ($header[0] -ne 0x7f -or $header[1] -ne 0x45 -or
    $header[2] -ne 0x4c -or $header[3] -ne 0x46 -or $header[4] -ne 2 -or
    $header[5] -ne 1 -or $header[18] -ne 0x3e -or $header[19] -ne 0) {
    throw 'The converter executable is not a Linux x64 ELF.'
}
$manifest = Write-NativePublishManifest -DestinationPath (Join-Path $reports 'publish.json') `
    -BuildId $buildId -Service 'probe' -Rid 'linux-x64' -Source $source -Toolchain $toolchain `
    -PublishProperties @{configuration='Release';publishAot=$true;selfContained=$true;stripSymbols=$true;conduitAotAudit=$true;useSharedCompilation=$false} `
    -StartedAtUtc $started -CompletedAtUtc $completed -RuntimeDirectory $runtime -ExecutableName $assembly
$runStarted = [DateTimeOffset]::UtcNow
# The probe bounds database operations and verifies removal with a fresh cleanup
# token before printing its success marker. No managed fallback is accepted.
& $executable 1> (Join-Path $reports 'probe.stdout.log') 2> (Join-Path $reports 'probe.stderr.log')
$runExit = $LASTEXITCODE
$stdout = [IO.File]::ReadAllText((Join-Path $reports 'probe.stdout.log'))
$nativeFlags = $stdout -cmatch '(?m)^PASS: 31 persisted converter properties; model=compiled; dynamicCodeSupported=false; dynamicCodeCompiled=false;'
$markers = [regex]::Matches($stdout, '(?m)^PASS: 31 persisted converter properties; model=compiled; dynamicCodeSupported=false; dynamicCodeCompiled=false; PostgreSQLChecks=126; dictionaryComparers=2; ownedSchemaRemoved=true; EFQueryExecution=false\.\r?$')
$pass = $runExit -eq 0 -and $nativeFlags -and $markers.Count -eq 1
$execution = [ordered]@{
    schemaVersion=1;kind='native-persisted-converter-proof';result=$(if($pass){'PASS'}else{'FAIL'})
    source=$source;buildId=$buildId;rid='linux-x64';converterProperties=31
    actualNativeRuntimeFlags=$nativeFlags;processExitCode=$runExit;processJoined=$true
    postgreSQLChecks=$(if($markers.Count -eq 1){126}else{$null})
    dictionaryComparers=$(if($markers.Count -eq 1){2}else{$null});ownedSchemaRemoved=$pass
    startedAtUtc=$runStarted.ToString('O');completedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')
    publishDurationMilliseconds=$timer.ElapsedMilliseconds;publishManifestSha256=$manifest.sha256
    executable=$identity.executable;stdoutSha256=(Get-FileHash -LiteralPath (Join-Path $reports 'probe.stdout.log')).Hash.ToLowerInvariant()
    stderrSha256=(Get-FileHash -LiteralPath (Join-Path $reports 'probe.stderr.log')).Hash.ToLowerInvariant()
    successMarker=$(if($markers.Count -eq 1){$markers[0].Value.TrimEnd()}else{$null})
    nativeEfQuerySupport=$false;productionQualification=$false
}
Write-ConverterOriginal (Join-Path $reports 'execution.json') ($execution | ConvertTo-Json -Depth 8)
if (-not $pass) { throw 'Native converter proof failed; original execution diagnostics are retained.' }
Write-Host $execution.successMarker
