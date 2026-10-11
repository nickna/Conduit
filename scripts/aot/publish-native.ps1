param(
    [string]$ArtifactDirectory,
    [ValidateSet("linux-x64", "win-x64")]
    [string]$Runtime = "linux-x64",
    [switch]$NoRestore,
    [string]$WarningBaselinePath,
    [switch]$UpdateWarningBaseline
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "native-publish-evidence.ps1")

function ConvertTo-MSBuildPropertyValue {
    param([string]$Value)
    # Escape MSBuild property-list separators, independently of shell quoting.
    return $Value.Replace('%', '%25').Replace(';', '%3B').Replace(',', '%2C')
}

function Get-NativePublishSource {
    param([string]$RepositoryRoot)
    $unavailable = [ordered]@{ status = "unavailable"; revision = $null; dirty = $null }
    if (!(Get-Command git -CommandType Application -ErrorAction SilentlyContinue)) { return $unavailable }
    $revisionLines = @(& git -C $RepositoryRoot rev-parse --verify HEAD 2>$null)
    $revisionExitCode = $LASTEXITCODE
    if ($revisionExitCode -ne 0 -or $revisionLines.Count -ne 1) { return $unavailable }
    $revision = ([string]$revisionLines[0]).Trim().ToLowerInvariant()
    if ($revision -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') { return $unavailable }
    $statusLines = @(& git -C $RepositoryRoot status --porcelain --untracked-files=normal 2>$null)
    $statusExitCode = $LASTEXITCODE
    if ($statusExitCode -ne 0) { return $unavailable }
    return [ordered]@{ status = "git"; revision = $revision; dirty = ($statusLines.Count -ne 0) }
}

function Assert-NativePublishRegularFile {
    param([IO.FileInfo]$File)
    if ($null -eq $File -or
        ($File.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Device)) -ne 0 -or
        ($IsLinux -and (Get-NativeEvidenceUnixFileType $File.FullName) -ne 0x8000)) {
        throw "Native publish capture and tool inputs must be regular files."
    }
}

function Read-NativeToolchainCapture {
    param([string]$Path)
    $names = @(
        "NETCoreSdkVersion", "RuntimeIdentifier", "PublishAot", "Configuration",
        "IlcHostPackagePath", "RuntimePackagePath", "IlcToolsPath", "ILCompilerTargetsPath",
        "CppCompilerAndLinker", "CppLinker", "LinkerFlavor", "ObjCopyName", "IlcUseEnvironmentalTools",
        "_hostOS", "_hostArchitecture", "_targetOS", "_targetArchitecture",
        "NativeIntermediateOutputPath", "NativeOutputPath", "NativeObject", "NativeBinary",
        "AssemblyName", "SelfContained", "StripSymbols", "ConduitAotAudit", "UseSharedCompilation",
        "Version", "InformationalVersion", "ConduitCommitSha", "ConduitBuildTimestamp"
    )
    $file = Get-Item -LiteralPath $Path
    if ($file.PSIsContainer -or $file.Length -gt 65536 -or
        ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Native toolchain capture is not a bounded regular file."
    }
    Assert-NativeEvidenceRegularFile $file.FullName
    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $captureText = $strictUtf8.GetString([IO.File]::ReadAllBytes($file.FullName)).TrimStart([char]0xFEFF)
    $values = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $expected = [Collections.Generic.HashSet[string]]::new([string[]]$names, [StringComparer]::Ordinal)
    foreach ($line in ($captureText -split '\r?\n')) {
        if ($line.Length -eq 0) { continue }
        $separator = $line.IndexOf('=')
        if ($separator -le 0) { throw "Native toolchain capture has invalid framing." }
        $name = $line.Substring(0, $separator)
        if (!$expected.Contains($name) -or $values.ContainsKey($name)) {
            throw "Native toolchain capture has an unexpected or repeated field."
        }
        $encoded = $line.Substring($separator + 1)
        $bytes = [Convert]::FromBase64String($encoded)
        if ([Convert]::ToBase64String($bytes) -cne $encoded) { throw "Native toolchain capture has invalid Base64." }
        $value = $strictUtf8.GetString($bytes)
        if ($value.IndexOfAny([char[]]@([char]0, [char]13, [char]10)) -ge 0) {
            throw "Native toolchain capture has an invalid scalar value."
        }
        $values.Add($name, $value)
    }
    if ($values.Count -ne $names.Count) { throw "Native toolchain capture is incomplete." }
    return $values
}

function Assert-NativePublishCompileOutputs {
    param($Capture, [string]$IntermediateDirectory, [string]$OutputDirectory,
        [string]$AssemblyName, [string]$ExecutableName, [string]$RuntimeExecutable)
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $expectedPaths = @{
        NativeIntermediateOutputPath = $IntermediateDirectory
        NativeOutputPath = $OutputDirectory
        NativeObject = Join-Path $IntermediateDirectory ($AssemblyName + $(if ($Runtime -eq "win-x64") { ".obj" } else { ".o" }))
        NativeBinary = Join-Path $OutputDirectory $ExecutableName
    }
    foreach ($name in $expectedPaths.Keys) {
        $capturedPath = $Capture[$name]
        if ([string]::IsNullOrWhiteSpace($capturedPath) -or ![IO.Path]::IsPathFullyQualified($capturedPath)) {
            throw "Native compilation evidence is missing an absolute owned path."
        }
        if ($name -in @("NativeIntermediateOutputPath", "NativeOutputPath") -and
            !$capturedPath.EndsWith([string][IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal) -and
            !$capturedPath.EndsWith([string][IO.Path]::AltDirectorySeparatorChar, [StringComparison]::Ordinal)) {
            throw "Native compilation evidence output roots must have a trailing separator."
        }
        $actual = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($capturedPath))
        $expected = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($expectedPaths[$name]))
        if (!$actual.Equals($expected, $comparison)) { throw "Native compilation evidence does not use this invocation's owned paths." }
        Assert-NativeEvidencePathAncestry $actual
    }
    foreach ($path in @($expectedPaths.NativeObject, $expectedPaths.NativeBinary, $RuntimeExecutable)) {
        Assert-NativeEvidenceRegularFile $path
        if ((Get-Item -LiteralPath $path).Length -le 0) { throw "Native compilation evidence output must be nonempty." }
    }
    $binary = Get-Item -LiteralPath $expectedPaths.NativeBinary
    $copied = Get-Item -LiteralPath $RuntimeExecutable
    if ($binary.Length -ne $copied.Length -or
        (Get-FileHash -LiteralPath $binary.FullName -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $copied.FullName -Algorithm SHA256).Hash) {
        throw "The published executable does not match this invocation's native binary."
    }
}

function Resolve-NativeToolExecutable {
    param([string]$Command)
    if ([string]::IsNullOrWhiteSpace($Command)) { throw "The resolved native tool command is missing." }
    if ([IO.Path]::IsPathRooted($Command)) { $toolPath = [IO.Path]::GetFullPath($Command) }
    else {
        $toolCommand = Get-Command $Command -CommandType Application -ErrorAction Stop | Select-Object -First 1
        $toolPath = [IO.Path]::GetFullPath($toolCommand.Source)
    }
    $tool = Get-Item -LiteralPath $toolPath
    if (($tool.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        $tool = $tool.ResolveLinkTarget($true)
        if ($null -eq $tool) { throw "The resolved native tool link has no target." }
    }
    if ($tool -isnot [IO.FileInfo] -or $tool.Length -le 0) { throw "The resolved native tool is not an executable file." }
    Assert-NativePublishRegularFile $tool
    return [ordered]@{
        path = $tool.FullName
        sha256 = (Get-FileHash -LiteralPath $tool.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = [long]$tool.Length
    }
}

function Get-NativeLinkerDriverVersion {
    param([string]$Path, [string]$HostOs)
    if ($HostOs -eq "windows") {
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileVersion
        if ([string]::IsNullOrWhiteSpace($version) -or $version.Length -gt 32768) {
            throw "The native linker driver version metadata is missing or oversized."
        }
        return $version.Trim()
    }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Path
    $start.ArgumentList.Add("--version")
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    $failed = $false
    $cleanupFailed = $false
    $stdout = $null
    $stderr = $null
    $version = $null
    try {
        $started = $process.Start()
        if (!$started) { throw "The native linker driver version process could not start." }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(10000)) { throw "The native linker driver version process timed out." }
        if ($process.ExitCode -ne 0) { throw "The native linker driver version process failed." }
        $drain = [Diagnostics.Stopwatch]::StartNew()
        try {
            $version = $stdout.WaitAsync([TimeSpan]::FromMilliseconds([Math]::Max(1, 5000 - [int]$drain.ElapsedMilliseconds))).GetAwaiter().GetResult()
            $errorVersion = $stderr.WaitAsync([TimeSpan]::FromMilliseconds([Math]::Max(1, 5000 - [int]$drain.ElapsedMilliseconds))).GetAwaiter().GetResult()
            if ($drain.ElapsedMilliseconds -gt 5000) { throw "The native linker driver output timed out." }
        }
        finally { $drain.Stop() }
        if ($version.Length + $errorVersion.Length -gt 32768) { throw "The native linker driver version is oversized." }
        $version = ($version + [Environment]::NewLine + $errorVersion).Replace([string][char]13 + [char]10, [string][char]10).Trim()
        if ([string]::IsNullOrWhiteSpace($version)) { throw "The native linker driver version is missing." }
    }
    catch { $failed = $true }
    finally {
        if ($started) {
            $cleanup = [Diagnostics.Stopwatch]::StartNew()
            $reaped = $false
            try {
                if (!$process.HasExited) { $process.Kill($true) }
                $remaining = [Math]::Max(1, 5000 - [int]$cleanup.ElapsedMilliseconds)
                $reaped = $process.WaitForExit($remaining)
                if (!$reaped) { $cleanupFailed = $true }
            }
            catch { $cleanupFailed = $true }
            foreach ($task in @($stdout, $stderr)) {
                if ($null -eq $task) { continue }
                try {
                    $remaining = [Math]::Max(1, 5000 - [int]$cleanup.ElapsedMilliseconds)
                    $unusedResult = $task.WaitAsync([TimeSpan]::FromMilliseconds($remaining)).GetAwaiter().GetResult()
                }
                catch {
                    # A completed faulted task is joined. An incomplete task
                    # still owns its pipe and must fail the cleanup contract.
                    if (!$task.IsCompleted) { $cleanupFailed = $true }
                }
            }
            if ($cleanup.ElapsedMilliseconds -gt 5000) { $cleanupFailed = $true }
            $cleanup.Stop()
            if ($reaped) { $process.Dispose() }
        }
        else { $process.Dispose() }
    }
    if ($failed -and $cleanupFailed) { throw "The native linker driver version probe and child cleanup failed." }
    if ($cleanupFailed) { throw "The native linker driver version child cleanup failed." }
    if ($failed) { throw "The native linker driver version probe failed." }
    return $version
}

function Get-NativePublishToolchain {
    param($Capture, [string]$ExpectedAssembly, [bool]$ExpectedAudit)
    $expectedValues = @{
        RuntimeIdentifier = $Runtime; AssemblyName = $ExpectedAssembly; Configuration = "Release"
        PublishAot = "true"; SelfContained = "true"; StripSymbols = "true"
        ConduitAotAudit = $ExpectedAudit.ToString().ToLowerInvariant(); UseSharedCompilation = "false"
    }
    foreach ($name in $expectedValues.Keys) {
        if (![string]::Equals($Capture[$name], $expectedValues[$name], [StringComparison]::OrdinalIgnoreCase)) {
            throw "Native toolchain capture does not match the requested publish properties."
        }
    }
    foreach ($name in @("NETCoreSdkVersion", "IlcHostPackagePath", "IlcToolsPath", "CppLinker", "_hostOS", "_hostArchitecture", "_targetOS", "_targetArchitecture")) {
        if ([string]::IsNullOrWhiteSpace($Capture[$name])) { throw "Native toolchain capture is missing a resolved identity." }
    }
    $hostOs = $Capture["_hostOS"].ToLowerInvariant()
    $targetOs = $Capture["_targetOS"].ToLowerInvariant()
    if ($hostOs -eq "win") { $hostOs = "windows" }
    if ($targetOs -eq "win") { $targetOs = "windows" }
    $expectedTargetOs = if ($Runtime -eq "win-x64") { "windows" } else { "linux" }
    if ($targetOs -cne $expectedTargetOs -or $Capture["_targetArchitecture"] -cne "x64") {
        throw "Native toolchain capture has an unexpected target platform."
    }
    if (![IO.Path]::IsPathRooted($Capture["IlcHostPackagePath"]) -or ![IO.Path]::IsPathRooted($Capture["IlcToolsPath"])) {
        throw "The selected ILCompiler package and tools paths must be absolute."
    }
    $packagePath = [IO.Path]::GetFullPath($Capture["IlcHostPackagePath"].Replace('\', [IO.Path]::DirectorySeparatorChar))
    $nuspecs = @(Get-ChildItem -LiteralPath $packagePath -Filter "*.nuspec" -File -Force)
    if ($nuspecs.Count -ne 1 -or $nuspecs[0].Length -gt 1048576) { throw "The selected ILCompiler package identity is missing or ambiguous." }
    Assert-NativeEvidenceRegularFile $nuspecs[0].FullName
    $xmlSettings = [Xml.XmlReaderSettings]::new()
    $xmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $xmlSettings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($nuspecs[0].FullName, $xmlSettings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
    }
    finally { $reader.Dispose() }
    $packageIdNodes = $document.SelectNodes('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="id"]')
    $packageVersionNodes = $document.SelectNodes('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="version"]')
    if ($packageIdNodes.Count -ne 1 -or $packageVersionNodes.Count -ne 1) { throw "The selected ILCompiler package identity is invalid." }
    $packageId = $packageIdNodes[0].InnerText
    $packageVersion = $packageVersionNodes[0].InnerText
    if ($packageId -notmatch '^runtime\..+\.Microsoft\.DotNet\.ILCompiler$' -or [string]::IsNullOrWhiteSpace($packageVersion)) {
        throw "The selected ILCompiler package identity is invalid."
    }
    $toolsPath = $Capture["IlcToolsPath"].Replace('\', [IO.Path]::DirectorySeparatorChar)
    $ilcName = if ($hostOs -eq "windows") { "ilc.exe" } else { "ilc" }
    $compiler = Resolve-NativeToolExecutable (Join-Path $toolsPath $ilcName)
    $linkDriver = Resolve-NativeToolExecutable $Capture["CppLinker"]
    $linkVersion = Get-NativeLinkerDriverVersion -Path $linkDriver.path -HostOs $hostOs
    return [ordered]@{
        sdkVersion = $Capture["NETCoreSdkVersion"]
        ilCompilerPackage = [ordered]@{ id = $packageId; version = $packageVersion; path = $packagePath }
        ilCompiler = $compiler
        nativeLinkerDriver = [ordered]@{
            command = $Capture["CppLinker"]; path = $linkDriver.path; sha256 = $linkDriver.sha256
            bytes = $linkDriver.bytes; version = $linkVersion
            flavor = $(if ([string]::IsNullOrWhiteSpace($Capture["LinkerFlavor"])) { $null } else { $Capture["LinkerFlavor"] })
        }
        hostOs = $hostOs; hostArchitecture = $Capture["_hostArchitecture"]
        targetOs = $targetOs; targetArchitecture = $Capture["_targetArchitecture"]
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
if ([string]::IsNullOrWhiteSpace($WarningBaselinePath)) { $WarningBaselinePath = Join-Path $PSScriptRoot "linker-warning-baseline.json" }
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) { $ArtifactDirectory = Join-Path $repoRoot "artifacts/native-aot" }
$artifactRoot = [IO.Path]::GetFullPath($ArtifactDirectory)
$runtimeRoot = Join-Path $artifactRoot "runtime"
$symbolsRoot = Join-Path $artifactRoot "symbols"
$reportsRoot = Join-Path $artifactRoot "reports"
$baselinePath = Join-Path $reportsRoot "native-baselines.json"
$claimPath = Join-Path $reportsRoot "native-publish.claim"
$warningBaselineFile = [IO.Path]::GetFullPath($WarningBaselinePath)
$source = Get-NativePublishSource -RepositoryRoot $repoRoot
$buildId = [Guid]::NewGuid().ToString("N")
$nativeIntermediateRoot = Join-Path $artifactRoot "native-intermediates"
$nativeOutputRoot = Join-Path $artifactRoot "native-outputs"
$nativeIntermediateBuildRoot = Join-Path $nativeIntermediateRoot $buildId
$nativeOutputBuildRoot = Join-Path $nativeOutputRoot $buildId
Assert-NativeEvidencePathAncestry $artifactRoot
Assert-NativeEvidencePathAncestry $runtimeRoot
Assert-NativeEvidencePathAncestry $reportsRoot
Assert-NativeEvidencePathAncestry $symbolsRoot
foreach ($nativeBuildRoot in @($nativeIntermediateBuildRoot, $nativeOutputBuildRoot)) {
    Assert-NativeEvidencePathAncestry $nativeBuildRoot
    if (Test-Path -LiteralPath $nativeBuildRoot) { throw "Native compilation evidence requires a fresh invocation output root." }
}
foreach ($reportLeaf in @("publish", "native-baselines.json", "native-publish.claim", "linker-diagnostics.json", "linker-summary.md")) {
    Assert-NativeEvidencePathAncestry (Join-Path $reportsRoot $reportLeaf)
}
foreach ($service in @("admin", "gateway", "probe")) {
    Assert-NativeEvidencePathAncestry (Join-Path $symbolsRoot $service)
    Assert-NativeEvidencePathAncestry (Join-Path $reportsRoot "$service-publish.log")
}
if (Test-Path -LiteralPath $baselinePath) { throw "Native publish baselines already exist; choose a fresh ArtifactDirectory." }
if (Test-Path -LiteralPath $claimPath) { throw "Native publish artifact directory is already claimed; choose a fresh ArtifactDirectory." }
Assert-NativePublishOutputDirectoriesEmpty -RuntimeRoot $runtimeRoot
New-Item -ItemType Directory -Force -Path $runtimeRoot, $symbolsRoot, $reportsRoot | Out-Null
# The fixed CreateNew claim is retained after success or failure; retries need a
# fresh directory. Report/log directories alone may already exist.
$claim = [IO.FileStream]::new($claimPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $claimBytes = [Text.UTF8Encoding]::new($false).GetBytes($buildId + [Environment]::NewLine)
    $claim.Write($claimBytes)
    $claim.Flush($true)
}
finally { $claim.Dispose() }
$publishEvidenceDirectory = Join-Path $reportsRoot "publish/$buildId"
New-Item -ItemType Directory -Path $publishEvidenceDirectory | Out-Null
$metadataTimestamp = [DateTime]::UtcNow.ToString("O", [Globalization.CultureInfo]::InvariantCulture)
$toolchainTargets = Join-Path $PSScriptRoot "native-publish-toolchain.targets"
$published = [Collections.Generic.List[object]]::new()

foreach ($definition in @(
    @{ service = "admin"; assembly = "ConduitLLM.Admin"; project = "Services/ConduitLLM.Admin/ConduitLLM.Admin.csproj"; audit = $true },
    @{ service = "gateway"; assembly = "ConduitLLM.Gateway"; project = "Services/ConduitLLM.Gateway/ConduitLLM.Gateway.csproj"; audit = $true },
    @{ service = "probe"; assembly = "ConduitLLM.GatewayNativeAotTests"; project = "Tests/ConduitLLM.GatewayNativeAotTests/ConduitLLM.GatewayNativeAotTests.csproj"; audit = $false }
)) {
    $service = $definition.service
    $publishDirectory = Join-Path $runtimeRoot $service
    $symbolDirectory = Join-Path $symbolsRoot $service
    $logPath = Join-Path $reportsRoot "$service-publish.log"
    $capturePath = Join-Path $publishEvidenceDirectory "$service-toolchain.b64"
    $nativeIntermediateDirectory = (Join-Path $nativeIntermediateBuildRoot $service) + [IO.Path]::DirectorySeparatorChar
    $nativeOutputDirectory = (Join-Path $nativeOutputBuildRoot $service) + [IO.Path]::DirectorySeparatorChar
    New-Item -ItemType Directory -Force -Path $publishDirectory, $symbolDirectory | Out-Null
    $arguments = @(
        "publish", (Join-Path $repoRoot $definition.project), "--configuration", "Release",
        "--runtime", $Runtime, "--self-contained", "true", "--output", $publishDirectory,
        "--nologo", "--tl:off", "--maxcpucount:1", "--verbosity", "minimal",
        "-p:PublishAot=true", "-p:ConduitAotAudit=$($definition.audit.ToString().ToLowerInvariant())",
        "-p:StripSymbols=true", "-p:UseSharedCompilation=false",
        "-p:NativeIntermediateOutputPath=$(ConvertTo-MSBuildPropertyValue $nativeIntermediateDirectory)",
        "-p:NativeOutputPath=$(ConvertTo-MSBuildPropertyValue $nativeOutputDirectory)",
        "-p:CustomAfterMicrosoftCommonTargets=$(ConvertTo-MSBuildPropertyValue $toolchainTargets)",
        "-p:ConduitNativeToolchainEvidencePath=$(ConvertTo-MSBuildPropertyValue $capturePath)",
        "-p:ConduitNativeToolchainEvidenceAssemblyName=$($definition.assembly)"
    )
    if ($source.status -eq "git") {
        $arguments += "-p:ConduitCommitSha=$($source.revision)"
        $arguments += "-p:ConduitBuildTimestamp=$metadataTimestamp"
    }
    if ($NoRestore) { $arguments += "--no-restore" }
    Write-Host "Publishing $($definition.assembly) for $Runtime NativeAOT..."
    $startedAt = [DateTime]::UtcNow
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    & dotnet @arguments 2>&1 | Tee-Object -FilePath $logPath
    $publishExitCode = $LASTEXITCODE
    $stopwatch.Stop()
    if ($publishExitCode -ne 0) { throw "$service NativeAOT publish failed with exit code $publishExitCode." }
    foreach ($symbol in @(Get-ChildItem -LiteralPath $publishDirectory -File -Force | Where-Object { $_.Extension -in @(".dbg", ".pdb") })) {
        $symbolDestination = Join-Path $symbolDirectory $symbol.Name
        Assert-NativeEvidencePathAncestry $symbolDestination
        Move-Item -LiteralPath $symbol.FullName -Destination $symbolDestination -Force
    }
    $completedAt = [DateTime]::UtcNow
    $executableName = $definition.assembly + $(if ($Runtime -eq "win-x64") { ".exe" } else { "" })
    if (!(Test-Path -LiteralPath (Join-Path $publishDirectory $executableName) -PathType Leaf)) {
        throw "$service native executable was not produced."
    }
    $capture = Read-NativeToolchainCapture -Path $capturePath
    Assert-NativePublishCompileOutputs -Capture $capture -IntermediateDirectory $nativeIntermediateDirectory -OutputDirectory $nativeOutputDirectory -AssemblyName $definition.assembly -ExecutableName $executableName -RuntimeExecutable (Join-Path $publishDirectory $executableName)
    if ($source.status -eq "git" -and ($capture["ConduitCommitSha"] -cne $source.revision -or $capture["ConduitBuildTimestamp"] -cne $metadataTimestamp)) {
        throw "Native assembly metadata does not match the captured source identity."
    }
    $toolchain = Get-NativePublishToolchain -Capture $capture -ExpectedAssembly $definition.assembly -ExpectedAudit $definition.audit
    $published.Add([ordered]@{
        service = $service; directory = $publishDirectory; executableName = $executableName
        startedAt = $startedAt; completedAt = $completedAt; duration = $stopwatch.ElapsedMilliseconds
        toolchain = $toolchain
        properties = [ordered]@{
            configuration = "Release"; publishAot = $true; selfContained = $true; stripSymbols = $true
            conduitAotAudit = $definition.audit; useSharedCompilation = $false
        }
    })
}

$linkerAuditArguments = @{ ReportDirectory = $reportsRoot; BaselinePath = $warningBaselineFile; Runtime = $Runtime }
if ($UpdateWarningBaseline) { $linkerAuditArguments.UpdateBaseline = $true }
& (Join-Path $PSScriptRoot "evaluate-native-linker-warnings.ps1") @linkerAuditArguments

# Write success evidence only after every publish, symbol split and linker check.
$metrics = [Collections.Generic.List[object]]::new()
$probeManifest = $null
foreach ($publication in $published) {
    $destination = Join-Path $publishEvidenceDirectory "$($publication.service).json"
    $identity = Get-NativeRuntimeIdentity -RuntimeDirectory $publication.directory -ExecutableName $publication.executableName
    $manifestArguments = @{
        DestinationPath = $destination; BuildId = $buildId; Service = $publication.service; Rid = $Runtime
        Source = $source; Toolchain = $publication.toolchain; PublishProperties = $publication.properties
        StartedAtUtc = $publication.startedAt; CompletedAtUtc = $publication.completedAt
        RuntimeDirectory = $publication.directory; ExecutableName = $publication.executableName
    }
    $manifest = Write-NativePublishManifest @manifestArguments
    $reference = [ordered]@{
        buildId = $manifest.buildId
        path = [IO.Path]::GetRelativePath($artifactRoot, $manifest.path).Replace('\', '/')
        sha256 = $manifest.sha256
    }
    if ($publication.service -eq "probe") { $probeManifest = $reference }
    else {
        $metrics.Add([ordered]@{
            service = $(if ($publication.service -eq "admin") { "Admin" } else { "Gateway" })
            rid = $Runtime; publishDurationMilliseconds = $publication.duration
            executableBytes = $identity.executable.bytes; runtimeArtifactBytes = $identity.runtimeArtifactBytes
            publishManifest = $reference
        })
    }
}
$baselineJson = [ordered]@{
    schemaVersion = 2; generatedAtUtc = [DateTime]::UtcNow.ToString("O")
    services = $metrics; probeManifest = $probeManifest
} | ConvertTo-Json -Depth 5
$baselineStagingPath = Join-Path $reportsRoot ".native-baselines.$buildId.tmp"
$baseline = [IO.FileStream]::new($baselineStagingPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $baselineBytes = [Text.UTF8Encoding]::new($false).GetBytes($baselineJson + [Environment]::NewLine)
    $baseline.Write($baselineBytes)
    $baseline.Flush($true)
}
finally { $baseline.Dispose() }
[IO.File]::Move($baselineStagingPath, $baselinePath, $false)
Write-Host "Native runtime artifacts: $runtimeRoot"
Write-Host "Native symbols: $symbolsRoot"
