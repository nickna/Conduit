# Build-time NativeAOT producer helpers. Dot-sourcing defines functions only.
# Published applications never import this script or its toolchain capture target.
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
        [string]$AssemblyName, [string]$ExecutableName, [string]$RuntimeExecutable,
        [Parameter(Mandatory = $true)][ValidateSet("linux-x64", "win-x64")][string]$Runtime)
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
    param($Capture, [string]$ExpectedAssembly, [bool]$ExpectedAudit,
        [Parameter(Mandatory = $true)][ValidateSet("linux-x64", "win-x64")][string]$Runtime)
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

