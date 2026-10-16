# Shared helpers for immutable loose NativeAOT publish-directory evidence.
# Dot-sourcing this file does not publish, create directories, or change settings.

function Get-NativeEvidenceFullPath([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -match '[\x00-\x1f\x7f]') {
        throw 'Native publish evidence path is invalid.'
    }
    try { return [IO.Path]::GetFullPath($Path) }
    catch { throw 'Native publish evidence path is invalid.' }
}

function Assert-NativeEvidencePathAncestry([string] $Path) {
    $cursor = Get-NativeEvidenceFullPath $Path
    while (-not [string]::IsNullOrEmpty($cursor)) {
        try {
            $attributes = [IO.File]::GetAttributes($cursor)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Native publish evidence rejects linked paths.'
            }
        }
        catch [IO.FileNotFoundException] { }
        catch [IO.DirectoryNotFoundException] { }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -ceq $cursor) { break }
        $cursor = $parent
    }
}

function Assert-NativeEvidenceRelativePath([string] $Path, [switch] $Leaf) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -match '[\\\x00-\x1f\x7f:*?"<>|]' -or
        $Path.StartsWith('/', [StringComparison]::Ordinal) -or
        ($Leaf -and $Path.Contains('/'))) {
        throw 'Native publish evidence relative path is invalid.'
    }
    foreach ($segment in $Path.Split('/')) {
        if ([string]::IsNullOrEmpty($segment) -or $segment -cin @('.', '..') -or
            $segment.EndsWith('.', [StringComparison]::Ordinal) -or
            $segment.EndsWith(' ', [StringComparison]::Ordinal) -or
            $segment -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
            throw 'Native publish evidence relative path is invalid.'
        }
    }
}

function Get-NativeEvidenceStatxFileType([string] $Path) {
    if (-not $IsLinux) { return $null }
    if ($null -eq ('ConduitLLM.NativeEvidence.LinuxFileType' -as [type])) {
        # Cache only whether this runtime can compile the interop helper. Every
        # path observation below is a new no-follow lookup, never a cached type.
        if ((Get-Variable -Name NativeEvidenceStatxUnavailable -Scope Script -ValueOnly -ErrorAction SilentlyContinue) -eq $true) {
            return $null
        }
        try {
            Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ConduitLLM.NativeEvidence
{
    // Linux UAPI struct statx is 256 bytes on the supported 64-bit ABIs.
    // Preserve its entire writable buffer; only stx_mask and stx_mode are read.
    // https://github.com/torvalds/linux/blob/v6.12/include/uapi/linux/stat.h
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    public struct StatxBuffer
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    public static class LinuxFileType
    {
        [DllImport("libc", EntryPoint = "statx", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern int Statx(int directory, [In] byte[] pathname, int flags, uint mask, out StatxBuffer buffer);

        // Null means the independent bounded stat fallback must perform its own
        // live lookup. Missing or malformed successful metadata never falls back.
        public static int? ReadFileType(string path)
        {
            if (!OperatingSystem.IsLinux() ||
                (RuntimeInformation.ProcessArchitecture != Architecture.X64 &&
                 RuntimeInformation.ProcessArchitecture != Architecture.Arm64))
                return null;
            if (string.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0)
                throw new IOException("Native publish regular-file inspection failed.");

            // Explicit strict UTF-8 prevents replacement/truncation from changing
            // which filesystem entry is inspected. The native pathname is NUL-terminated.
            var pathname = new UTF8Encoding(false, true).GetBytes(path + "\0");
            try
            {
                // AT_FDCWD, AT_SYMLINK_NOFOLLOW | AT_NO_AUTOMOUNT, STATX_TYPE.
                // Default stat synchronization is retained; no DONT_SYNC/cache mode.
                var result = Statx(-100, pathname, 0x100 | 0x800, 0x00000001, out var buffer);
                var error = result == -1 ? Marshal.GetLastPInvokeError() : 0;
                if (result == -1 && (error == 1 || error == 38 || error == 95))
                    return null; // EPERM (e.g. seccomp), ENOSYS, EOPNOTSUPP.
                if (result != 0 || (buffer.Mask & 0x00000001) == 0)
                    throw new IOException("Native publish regular-file inspection failed.");
                var type = buffer.Mode & 0xF000;
                switch (type)
                {
                    case 0x1000: // FIFO
                    case 0x2000: // character device
                    case 0x4000: // directory
                    case 0x6000: // block device
                    case 0x8000: // regular file
                    case 0xA000: // symbolic link itself
                    case 0xC000: // socket
                        return type;
                    default:
                        throw new IOException("Native publish regular-file inspection failed.");
                }
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
            catch (BadImageFormatException) { return null; }
            catch (PlatformNotSupportedException) { return null; }
        }
    }
}
'@ | Out-Null
        }
        catch {
            $script:NativeEvidenceStatxUnavailable = $true
            return $null
        }
    }
    return [ConduitLLM.NativeEvidence.LinuxFileType]::ReadFileType($Path)
}

function Get-NativeEvidenceUnixFileType([string] $Path) {
    try { $nativeType = Get-NativeEvidenceStatxFileType $Path }
    catch { throw 'Native publish regular-file inspection failed.' }
    if ($null -ne $nativeType) { return $nativeType }
    return Get-NativeEvidenceUnixFileTypeViaStat $Path
}

function Get-NativeEvidenceUnixFileTypeViaStat([string] $Path) {
    # FileInfo also represents Unix sockets, FIFOs and devices. Check the actual
    # type before FileStream.Open, which could otherwise block on a FIFO.
    $process = $null
    $outputTask = $null
    $errorTask = $null
    $failed = $false
    $cleanupFailed = $false
    $started = $false
    $result = $null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $command = Get-Command -Name 'stat' -CommandType Application -ErrorAction Stop |
            Select-Object -First 1
        if ($null -eq $command -or [string]::IsNullOrWhiteSpace($command.Path) -or
            -not [IO.Path]::IsPathFullyQualified($command.Path)) {
            throw 'Native publish regular-file inspection tool is unavailable.'
        }
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $command.Path
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('--printf=%f', '--', $Path)) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $start
        if (-not $process.Start()) { throw 'Native publish regular-file inspection failed.' }
        $started = $true
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        $remaining = [Math]::Max(1, 5000 - [int]$timer.ElapsedMilliseconds)
        if (-not $process.WaitForExit($remaining)) { throw 'Native publish regular-file inspection timed out.' }
        $remaining = [Math]::Max(1, 5000 - [int]$timer.ElapsedMilliseconds)
        $output = $outputTask.WaitAsync([TimeSpan]::FromMilliseconds($remaining)).GetAwaiter().GetResult()
        $remaining = [Math]::Max(1, 5000 - [int]$timer.ElapsedMilliseconds)
        $unusedError = $errorTask.WaitAsync([TimeSpan]::FromMilliseconds($remaining)).GetAwaiter().GetResult()
        if ($timer.ElapsedMilliseconds -gt 5000 -or $process.ExitCode -ne 0 -or
            $output -cnotmatch '^[0-9a-fA-F]{1,8}$') {
            throw 'Native publish regular-file inspection failed.'
        }
        $result = [Convert]::ToUInt32($output, 16) -band 0xF000
        if ($result -notin @(0x1000, 0x2000, 0x4000, 0x6000, 0x8000, 0xA000, 0xC000)) {
            throw 'Native publish regular-file inspection failed.'
        }
    }
    catch { $failed = $true }
    finally {
        $timer.Stop()
        if ($null -ne $process -and $started) {
            $reaped = $false
            try {
                if (-not $process.HasExited) { $process.Kill($true) }
                $reaped = $process.WaitForExit(5000)
                if (-not $reaped) { $cleanupFailed = $true }
            }
            catch { $cleanupFailed = $true }
            $cleanupTimer = [Diagnostics.Stopwatch]::StartNew()
            foreach ($task in @($outputTask, $errorTask)) {
                if ($null -eq $task) { continue }
                try {
                    $remaining = [Math]::Max(1, 5000 - [int]$cleanupTimer.ElapsedMilliseconds)
                    $unusedResult = $task.WaitAsync([TimeSpan]::FromMilliseconds($remaining)).GetAwaiter().GetResult()
                }
                catch { if (-not $task.IsCompleted) { $cleanupFailed = $true } }
            }
            $cleanupTimer.Stop()
            if ($reaped) { $process.Dispose() }
        }
        elseif ($null -ne $process) { $process.Dispose() }
    }
    if ($failed -and $cleanupFailed) { throw 'Native publish regular-file inspection and child cleanup failed.' }
    if ($cleanupFailed) { throw 'Native publish regular-file inspection child cleanup failed.' }
    if ($failed) { throw 'Native publish regular-file inspection failed.' }
    return $result
}

function Assert-NativeEvidenceRegularFile([string] $Path) {
    $fullPath = Get-NativeEvidenceFullPath $Path
    Assert-NativeEvidencePathAncestry $fullPath
    if (-not [IO.File]::Exists($fullPath)) { throw 'Native publish evidence requires a regular file.' }
    $attributes = [IO.File]::GetAttributes($fullPath)
    if (($attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Device -bor
            [IO.FileAttributes]::Directory)) -ne 0 -or
        ($IsLinux -and (Get-NativeEvidenceUnixFileType $fullPath) -ne 0x8000)) {
        throw 'Native publish evidence requires a regular file.'
    }
}

function Get-NativeEvidenceFiles([string] $RuntimeDirectory) {
    $root = Get-NativeEvidenceFullPath $RuntimeDirectory
    Assert-NativeEvidencePathAncestry $root
    if (-not [IO.Directory]::Exists($root)) {
        throw 'Native publish runtime directory does not exist.'
    }
    if ($IsLinux -and (Get-NativeEvidenceUnixFileType $root) -ne 0x4000) {
        throw 'Native publish runtime root must be a regular directory.'
    }
    $entries = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($root)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        Assert-NativeEvidencePathAncestry $directory
        foreach ($entry in ([IO.DirectoryInfo]::new($directory)).EnumerateFileSystemInfos()) {
            $entry.Refresh()
            if (($entry.Attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Device)) -ne 0 -or
                $null -ne $entry.LinkTarget) {
                throw 'Native publish evidence rejects linked paths.'
            }
            $unixType = if ($IsLinux) { Get-NativeEvidenceUnixFileType $entry.FullName } else { $null }
            $relative = [IO.Path]::GetRelativePath($root, $entry.FullName)
            # A backslash is a real filename character on Unix; never turn it
            # into a separator and silently collide with another file.
            if ($IsWindows) { $relative = $relative.Replace('\', '/') }
            Assert-NativeEvidenceRelativePath $relative
            if (($entry.Attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                if ($IsLinux -and $unixType -ne 0x4000) {
                    throw 'Native publish evidence requires regular directories.'
                }
                $pending.Push($entry.FullName)
            }
            elseif ($entry -is [IO.FileInfo]) {
                if ($IsLinux -and $unixType -ne 0x8000) {
                    throw 'Native publish evidence requires regular files.'
                }
                if (-not $entries.TryAdd($relative, $entry)) {
                    throw 'Native publish evidence contains duplicate runtime paths.'
                }
            }
            else {
                throw 'Native publish evidence requires regular files.'
            }
        }
    }
    return $entries
}

function Get-NativeEvidenceFileHash([IO.FileInfo] $File) {
    Assert-NativeEvidencePathAncestry $File.FullName
    $File.Refresh()
    if (-not $File.Exists -or ($File.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Native publish runtime file changed during hashing.'
    }
    Assert-NativeEvidenceRegularFile $File.FullName
    $beforeLength = $File.Length
    $beforeWrite = $File.LastWriteTimeUtc.Ticks
    $stream = $null
    try {
        $stream = [IO.FileStream]::new($File.FullName, [IO.FileMode]::Open,
            [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($stream.Length -ne $beforeLength) {
            throw 'Native publish runtime file changed during hashing.'
        }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        if ($stream.Length -ne $beforeLength) {
            throw 'Native publish runtime file changed during hashing.'
        }
        $File.Refresh()
        Assert-NativeEvidencePathAncestry $File.FullName
        if (-not $File.Exists -or $File.Length -ne $beforeLength -or
            $File.LastWriteTimeUtc.Ticks -ne $beforeWrite -or
            ($File.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Native publish runtime file changed during hashing.'
        }
        return [ordered]@{ bytes = [long]$beforeLength; sha256 = $hash; writeTicks = $beforeWrite }
    }
    finally { if ($null -ne $stream) { $stream.Dispose() } }
}

function Get-NativeEvidenceObject($Value) {
    if ($Value -is [Collections.IDictionary]) {
        $result = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($key in $Value.Keys) {
            if ($key -isnot [string] -or [string]::IsNullOrWhiteSpace($key) -or
                -not $names.Add($key)) { throw 'Native publish evidence object is invalid.' }
            $result.Add($key, $Value[$key])
        }
        return $result
    }
    if ($Value -is [Management.Automation.PSCustomObject]) {
        $result = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($property in $Value.PSObject.Properties) {
            if (-not $names.Add($property.Name)) { throw 'Native publish evidence object is invalid.' }
            $result.Add($property.Name, $property.Value)
        }
        return $result
    }
    throw 'Native publish evidence requires an object.'
}

function Assert-NativeEvidenceKeys($Object, [string[]] $Required, [switch] $Exact) {
    foreach ($name in $Required) {
        if (-not $Object.ContainsKey($name)) { throw 'Native publish evidence object fields are invalid.' }
    }
    if ($Exact -and $Object.Count -ne $Required.Length) {
        throw 'Native publish evidence object fields are invalid.'
    }
}

function Assert-NativeEvidenceText($Value) {
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value) -or
        $Value -match '[\x00-\x1f\x7f]') { throw 'Native publish evidence text is invalid.' }
}

function Assert-NativeEvidenceDriverVersion($Value) {
    # clang --version is multiline. Only this bounded evidence field permits
    # line separators and tabs; paths and other identity text stay single-line.
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value) -or
        $Value.Length -gt 32768 -or $Value -match '[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]') {
        throw 'Native publish evidence driver version is invalid.'
    }
}

function Assert-NativeEvidenceInteger($Value, [switch] $Positive) {
    if (($Value -isnot [long] -and $Value -isnot [int] -and $Value -isnot [short] -and
            $Value -isnot [byte]) -or $Value -lt 0 -or ($Positive -and $Value -eq 0)) {
        throw 'Native publish evidence requires an integer.'
    }
}

function Assert-NativeEvidenceHash($Value) {
    if ($Value -isnot [string] -or $Value -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Native publish evidence hash is invalid.'
    }
}

function ConvertTo-NativeEvidenceUtc($Value) {
    $date = [DateTimeOffset]::MinValue
    if ($Value -is [DateTimeOffset]) {
        $date = $Value
    }
    elseif ($Value -is [DateTime]) {
        if ($Value.Kind -ne [DateTimeKind]::Utc) { throw 'Native publish evidence timestamp must be UTC.' }
        $date = [DateTimeOffset]::new($Value)
    }
    elseif ($Value -is [string] -and $Value -cmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$' -and
        [DateTimeOffset]::TryParse($Value, [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None, [ref]$date)) {
    }
    else { throw 'Native publish evidence timestamp must be UTC.' }
    if ($date.Offset -ne [TimeSpan]::Zero -or $date -eq [DateTimeOffset]::MinValue) {
        throw 'Native publish evidence timestamp must be UTC.'
    }
    return $date
}

function Get-NativeEvidenceMetadata($Source, $Toolchain, $PublishProperties, [string] $Rid) {
    $sourceObject = Get-NativeEvidenceObject $Source
    Assert-NativeEvidenceKeys $sourceObject @('status', 'revision', 'dirty') -Exact
    if ($sourceObject['status'] -isnot [string] -or
        $sourceObject['status'] -cnotin @('git', 'unavailable')) {
        throw 'Native publish evidence source is invalid.'
    }
    if ($sourceObject['status'] -ceq 'unavailable') {
        if ($null -ne $sourceObject['revision'] -or $null -ne $sourceObject['dirty']) {
            throw 'Native publish evidence unavailable source must remain unknown.'
        }
    }
    elseif ($sourceObject['revision'] -isnot [string] -or
        $sourceObject['revision'] -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$' -or
        $sourceObject['dirty'] -isnot [bool]) { throw 'Native publish evidence source is invalid.' }

    $toolchainObject = Get-NativeEvidenceObject $Toolchain
    Assert-NativeEvidenceKeys $toolchainObject @('sdkVersion', 'ilCompilerPackage', 'ilCompiler',
        'nativeLinkerDriver', 'hostOs', 'hostArchitecture', 'targetOs', 'targetArchitecture')
    foreach ($name in @('sdkVersion', 'hostOs', 'hostArchitecture', 'targetOs', 'targetArchitecture')) {
        Assert-NativeEvidenceText $toolchainObject[$name]
    }
    $targetOs = if ($Rid -ceq 'linux-x64') { 'linux' } else { 'windows' }
    if ($toolchainObject['targetOs'] -cne $targetOs -or $toolchainObject['targetArchitecture'] -cne 'x64') {
        throw 'Native publish evidence target does not match its RID.'
    }
    $package = Get-NativeEvidenceObject $toolchainObject['ilCompilerPackage']
    Assert-NativeEvidenceKeys $package @('id', 'version')
    if ($package.Count -ne 2 -and ($package.Count -ne 3 -or -not $package.ContainsKey('path'))) {
        throw 'Native publish evidence compiler package is invalid.'
    }
    Assert-NativeEvidenceText $package['id']
    Assert-NativeEvidenceText $package['version']
    if ($package.ContainsKey('path')) {
        Assert-NativeEvidenceText $package['path']
        if ($package['path'] -cnotmatch '^(?:/|[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+)') {
            throw 'Native publish evidence package path must be absolute.'
        }
    }
    foreach ($name in @('ilCompiler', 'nativeLinkerDriver')) {
        $executable = Get-NativeEvidenceObject $toolchainObject[$name]
        $required = if ($name -ceq 'ilCompiler') { @('path', 'sha256', 'bytes') }
            else { @('command', 'path', 'sha256', 'bytes', 'version', 'flavor') }
        Assert-NativeEvidenceKeys $executable $required -Exact
        Assert-NativeEvidenceText $executable['path']
        # Tool paths belong to the producing host. Validate their absolute shape
        # without accessing them when checking an artifact on another OS.
        if ($executable['path'] -cnotmatch '^(?:/|[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+)') {
            throw 'Native publish evidence tool path must be absolute.'
        }
        Assert-NativeEvidenceHash $executable['sha256']
        Assert-NativeEvidenceInteger $executable['bytes'] -Positive
        if ($name -ceq 'nativeLinkerDriver') {
            Assert-NativeEvidenceText $executable['command']
            Assert-NativeEvidenceDriverVersion $executable['version']
            if ($null -ne $executable['flavor']) { Assert-NativeEvidenceText $executable['flavor'] }
        }
    }
    $properties = Get-NativeEvidenceObject $PublishProperties
    Assert-NativeEvidenceKeys $properties @('configuration', 'publishAot', 'selfContained',
        'stripSymbols', 'conduitAotAudit', 'useSharedCompilation') -Exact
    if ($properties['configuration'] -isnot [string] -or $properties['configuration'] -cne 'Release') {
        throw 'Native publish evidence configuration is invalid.'
    }
    foreach ($name in @('publishAot', 'selfContained', 'stripSymbols')) {
        if ($properties[$name] -isnot [bool] -or -not $properties[$name]) {
            throw 'Native publish evidence publish properties are invalid.'
        }
    }
    if ($properties['conduitAotAudit'] -isnot [bool] -or
        $properties['useSharedCompilation'] -isnot [bool] -or $properties['useSharedCompilation']) {
        throw 'Native publish evidence publish properties are invalid.'
    }
    return [ordered]@{ source = $sourceObject; toolchain = $toolchainObject; publishProperties = $properties }
}

function Write-NativeEvidenceJsonValue([Text.Json.Utf8JsonWriter] $Writer, $Value, [int] $Depth = 0) {
    if ($Depth -gt 32) { throw 'Native publish evidence metadata is too deep.' }
    if ($null -eq $Value) { $Writer.WriteNullValue(); return }
    if ($Value -is [string]) { $Writer.WriteStringValue($Value); return }
    if ($Value -is [bool]) { $Writer.WriteBooleanValue($Value); return }
    if ($Value -is [long] -or $Value -is [int] -or $Value -is [short] -or $Value -is [byte]) {
        $Writer.WriteNumberValue([long]$Value); return
    }
    if ($Value -is [Collections.IDictionary] -or $Value -is [Management.Automation.PSCustomObject]) {
        $object = Get-NativeEvidenceObject $Value
        [string[]]$keys = @($object.Keys)
        [Array]::Sort($keys, [StringComparer]::Ordinal)
        $Writer.WriteStartObject()
        foreach ($key in $keys) {
            $Writer.WritePropertyName($key)
            Write-NativeEvidenceJsonValue $Writer $object[$key] ($Depth + 1)
        }
        $Writer.WriteEndObject()
        return
    }
    if ($Value -is [array] -or $Value -is [Collections.IList]) {
        $Writer.WriteStartArray()
        foreach ($element in $Value) { Write-NativeEvidenceJsonValue $Writer $element ($Depth + 1) }
        $Writer.WriteEndArray()
        return
    }
    throw 'Native publish evidence metadata has an unsupported value.'
}

function Get-NativeEvidenceFileListBytes($Files) {
    $memory = [IO.MemoryStream]::new()
    $writer = [Text.Json.Utf8JsonWriter]::new($memory)
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
        return ,$memory.ToArray()
    }
    finally { $writer.Dispose(); $memory.Dispose() }
}

function Assert-NativePublishOutputDirectoriesEmpty {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $RuntimeRoot)
    $ErrorActionPreference = 'Stop'
    $root = Get-NativeEvidenceFullPath $RuntimeRoot
    Assert-NativeEvidencePathAncestry $root
    if ([IO.File]::Exists($root)) { throw 'Native publish output root must be a directory.' }
    foreach ($service in @('admin', 'gateway', 'probe')) {
        $directory = Join-Path $root $service
        Assert-NativeEvidencePathAncestry $directory
        if ([IO.File]::Exists($directory)) { throw 'Native publish output must be an empty directory.' }
        if ([IO.Directory]::Exists($directory)) {
            $enumerator = ([IO.DirectoryInfo]::new($directory)).EnumerateFileSystemInfos().GetEnumerator()
            try {
                if ($enumerator.MoveNext()) { throw 'Native publish output directories must be empty.' }
            }
            finally { $enumerator.Dispose() }
        }
    }
}

function Get-NativeRuntimeIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $RuntimeDirectory,
        [Parameter(Mandatory = $true)][string] $ExecutableName
    )
    $ErrorActionPreference = 'Stop'
    Assert-NativeEvidenceRelativePath $ExecutableName -Leaf
    $entries = Get-NativeEvidenceFiles $RuntimeDirectory
    if (-not $entries.ContainsKey($ExecutableName)) {
        throw 'Native publish executable does not exist.'
    }
    [string[]]$paths = @($entries.Keys)
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $records = [Collections.Generic.List[object]]::new()
    $observations = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    [long]$total = 0
    foreach ($path in $paths) {
        $identity = Get-NativeEvidenceFileHash $entries[$path]
        $observations.Add($path, $identity)
        $records.Add([ordered]@{ path = $path; bytes = [long]$identity.bytes; sha256 = $identity.sha256 })
        $total = [long]([decimal]$total + [decimal]$identity.bytes)
    }
    # Check membership and file observations again after the complete walk.
    $after = Get-NativeEvidenceFiles $RuntimeDirectory
    if ($after.Count -ne $entries.Count) { throw 'Native publish runtime changed during hashing.' }
    foreach ($path in $paths) {
        if (-not $after.ContainsKey($path)) { throw 'Native publish runtime changed during hashing.' }
        $current = $after[$path]
        $current.Refresh()
        $observation = $observations[$path]
        if ($current.Length -ne $observation.bytes -or
            $current.LastWriteTimeUtc.Ticks -ne $observation.writeTicks) {
            throw 'Native publish runtime changed during hashing.'
        }
    }
    $files = $records.ToArray()
    $canonical = Get-NativeEvidenceFileListBytes $files
    $filesHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($canonical)).ToLowerInvariant()
    $executable = @($files | Where-Object { $_.path -ceq $ExecutableName })[0]
    if ($executable.bytes -le 0) { throw 'Native publish executable must not be empty.' }
    return [ordered]@{
        files = $files
        filesSha256 = $filesHash
        runtimeArtifactBytes = $total
        executable = $executable
    }
}

function Assert-NativeEvidenceEnvelopeIdentity([string] $BuildId, [string] $Service, [string] $Rid) {
    if ($BuildId -cnotmatch '^[0-9a-f]{32}$' -or $BuildId -ceq ('0' * 32)) {
        throw 'Native publish evidence build ID is invalid.'
    }
    if ($Service -cnotin @('admin', 'gateway', 'probe') -or $Rid -cnotin @('linux-x64', 'win-x64')) {
        throw 'Native publish evidence service or RID is unsupported.'
    }
}

function Write-NativeEvidenceFileRow([Text.Json.Utf8JsonWriter] $Writer, $File) {
    $Writer.WriteStartObject()
    $Writer.WriteString('path', [string]$File.path)
    $Writer.WriteNumber('bytes', [long]$File.bytes)
    $Writer.WriteString('sha256', [string]$File.sha256)
    $Writer.WriteEndObject()
}

function Write-NativePublishManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $DestinationPath,
        [Parameter(Mandatory = $true)][string] $BuildId,
        [Parameter(Mandatory = $true)][string] $Service,
        [Parameter(Mandatory = $true)][string] $Rid,
        [Parameter(Mandatory = $true)] $Source,
        [Parameter(Mandatory = $true)] $Toolchain,
        [Parameter(Mandatory = $true)] $PublishProperties,
        [Parameter(Mandatory = $true)] $StartedAtUtc,
        [Parameter(Mandatory = $true)] $CompletedAtUtc,
        [Parameter(Mandatory = $true)][string] $RuntimeDirectory,
        [Parameter(Mandatory = $true)][string] $ExecutableName
    )
    $ErrorActionPreference = 'Stop'
    Assert-NativeEvidenceEnvelopeIdentity $BuildId $Service $Rid
    $metadata = Get-NativeEvidenceMetadata $Source $Toolchain $PublishProperties $Rid
    $started = ConvertTo-NativeEvidenceUtc $StartedAtUtc
    $completed = ConvertTo-NativeEvidenceUtc $CompletedAtUtc
    if ($completed -lt $started) { throw 'Native publish evidence completion precedes its start.' }
    $destination = Get-NativeEvidenceFullPath $DestinationPath
    $runtime = Get-NativeEvidenceFullPath $RuntimeDirectory
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $runtimePrefix = $runtime.TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)) + [IO.Path]::DirectorySeparatorChar
    if ($destination.Equals($runtime, $comparison) -or $destination.StartsWith($runtimePrefix, $comparison)) {
        throw 'Native publish manifest must be outside its runtime directory.'
    }
    Assert-NativeEvidencePathAncestry $destination
    if ([IO.File]::Exists($destination) -or [IO.Directory]::Exists($destination)) {
        throw 'Native publish manifest already exists.'
    }
    $identity = Get-NativeRuntimeIdentity $runtime $ExecutableName
    $envelope = [ordered]@{
        schemaVersion = 1
        kind = 'conduit-native-publish'
        buildId = $BuildId
        service = $Service
        rid = $Rid
        source = $metadata.source
        toolchain = $metadata.toolchain
        publishProperties = $metadata.publishProperties
        startedAtUtc = $started.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
        completedAtUtc = $completed.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    }
    $memory = [IO.MemoryStream]::new()
    $writer = [Text.Json.Utf8JsonWriter]::new($memory)
    try {
        $writer.WriteStartObject()
        foreach ($name in $envelope.Keys) {
            $writer.WritePropertyName($name)
            Write-NativeEvidenceJsonValue $writer $envelope[$name]
        }
        $writer.WritePropertyName('executable')
        Write-NativeEvidenceFileRow $writer $identity.executable
        $writer.WriteNumber('runtimeArtifactBytes', [long]$identity.runtimeArtifactBytes)
        $writer.WriteString('runtimeFilesSha256', [string]$identity.filesSha256)
        $writer.WriteStartArray('files')
        foreach ($file in $identity.files) { Write-NativeEvidenceFileRow $writer $file }
        $writer.WriteEndArray()
        $writer.WriteEndObject()
        $writer.Flush()
        $bytes = $memory.ToArray()
    }
    finally { $writer.Dispose(); $memory.Dispose() }

    $parent = [IO.Path]::GetDirectoryName($destination)
    Assert-NativeEvidencePathAncestry $parent
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    Assert-NativeEvidencePathAncestry $destination
    $stream = $null
    try {
        $stream = [IO.FileStream]::new($destination, [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write, [IO.FileShare]::None)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally { if ($null -ne $stream) { $stream.Dispose() } }
    # Do not return a successful reference if the runtime changed while its
    # immutable manifest was being written.
    Assert-NativePublishManifestMatchesRuntime $destination $runtime
    return [ordered]@{
        buildId = $BuildId
        path = $destination
        sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    }
}

function Read-NativeEvidenceJsonValue([Text.Json.JsonElement] $Value, [int] $Depth = 0) {
    if ($Depth -gt 32) { throw 'Native publish manifest is too deep.' }
    switch ($Value.ValueKind) {
        ([Text.Json.JsonValueKind]::Object) {
            $object = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($property in $Value.EnumerateObject()) {
                if ([string]::IsNullOrWhiteSpace($property.Name) -or -not $names.Add($property.Name)) {
                    throw 'Native publish manifest contains duplicate object fields.'
                }
                $object.Add($property.Name, (Read-NativeEvidenceJsonValue $property.Value ($Depth + 1)))
            }
            return $object
        }
        ([Text.Json.JsonValueKind]::Array) {
            $items = [Collections.Generic.List[object]]::new()
            foreach ($item in $Value.EnumerateArray()) {
                $items.Add((Read-NativeEvidenceJsonValue $item ($Depth + 1)))
            }
            return ,$items.ToArray()
        }
        ([Text.Json.JsonValueKind]::String) { return $Value.GetString() }
        ([Text.Json.JsonValueKind]::Number) {
            [long]$number = 0
            if (-not $Value.TryGetInt64([ref]$number)) { throw 'Native publish manifest requires integer numbers.' }
            return $number
        }
        ([Text.Json.JsonValueKind]::True) { return $true }
        ([Text.Json.JsonValueKind]::False) { return $false }
        ([Text.Json.JsonValueKind]::Null) { return $null }
        default { throw 'Native publish manifest JSON is invalid.' }
    }
}

function Assert-NativePublishManifestMatchesRuntime {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $ManifestPath,
        [Parameter(Mandatory = $true)][string] $RuntimeDirectory
    )
    $ErrorActionPreference = 'Stop'
    $manifest = Get-NativeEvidenceFullPath $ManifestPath
    Assert-NativeEvidencePathAncestry $manifest
    if (-not [IO.File]::Exists($manifest)) { throw 'Native publish manifest does not exist.' }
    Assert-NativeEvidenceRegularFile $manifest
    $document = $null
    try {
        $utf8 = [Text.UTF8Encoding]::new($false, $true)
        $document = [Text.Json.JsonDocument]::Parse($utf8.GetString([IO.File]::ReadAllBytes($manifest)))
        $body = Read-NativeEvidenceJsonValue $document.RootElement
    }
    catch { throw 'Native publish manifest JSON is invalid.' }
    finally { if ($null -ne $document) { $document.Dispose() } }
    $body = Get-NativeEvidenceObject $body
    Assert-NativeEvidenceKeys $body @('schemaVersion', 'kind', 'buildId', 'service', 'rid',
        'source', 'toolchain', 'publishProperties', 'startedAtUtc', 'completedAtUtc',
        'executable', 'runtimeArtifactBytes', 'runtimeFilesSha256', 'files') -Exact
    Assert-NativeEvidenceInteger $body['schemaVersion'] -Positive
    if ($body['schemaVersion'] -ne 1 -or $body['kind'] -isnot [string] -or
        $body['kind'] -cne 'conduit-native-publish' -or $body['buildId'] -isnot [string] -or
        $body['service'] -isnot [string] -or $body['rid'] -isnot [string]) {
        throw 'Native publish manifest identity is invalid.'
    }
    Assert-NativeEvidenceEnvelopeIdentity $body['buildId'] $body['service'] $body['rid']
    $metadata = Get-NativeEvidenceMetadata $body['source'] $body['toolchain'] $body['publishProperties'] $body['rid']
    if ($body['startedAtUtc'] -isnot [string] -or $body['completedAtUtc'] -isnot [string]) {
        throw 'Native publish manifest timestamps are invalid.'
    }
    $started = ConvertTo-NativeEvidenceUtc $body['startedAtUtc']
    $completed = ConvertTo-NativeEvidenceUtc $body['completedAtUtc']
    if ($completed -lt $started) { throw 'Native publish manifest completion precedes its start.' }
    Assert-NativeEvidenceInteger $body['runtimeArtifactBytes'] -Positive
    Assert-NativeEvidenceHash $body['runtimeFilesSha256']
    $files = $body['files']
    if ($files -isnot [array] -or $files.Length -eq 0) {
        throw 'Native publish manifest files must be a nonempty array.'
    }
    [long]$sum = 0
    $previous = $null
    $fileMap = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($file in $files) {
        $row = Get-NativeEvidenceObject $file
        Assert-NativeEvidenceKeys $row @('path', 'bytes', 'sha256') -Exact
        if ($row['path'] -isnot [string]) { throw 'Native publish manifest file path is invalid.' }
        Assert-NativeEvidenceRelativePath $row['path']
        Assert-NativeEvidenceInteger $row['bytes']
        Assert-NativeEvidenceHash $row['sha256']
        if ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous, $row['path']) -ge 0) {
            throw 'Native publish manifest file paths must be unique and ordinally ordered.'
        }
        $previous = $row['path']
        $fileMap.Add($row['path'], $row)
        $sum = [long]([decimal]$sum + [decimal]$row['bytes'])
    }
    if ($sum -ne $body['runtimeArtifactBytes']) {
        throw 'Native publish manifest runtime size is inconsistent.'
    }
    $canonical = Get-NativeEvidenceFileListBytes $files
    $filesHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($canonical)).ToLowerInvariant()
    if ($filesHash -cne $body['runtimeFilesSha256']) {
        throw 'Native publish manifest file-list hash is inconsistent.'
    }
    $executable = Get-NativeEvidenceObject $body['executable']
    Assert-NativeEvidenceKeys $executable @('path', 'bytes', 'sha256') -Exact
    if ($executable['path'] -isnot [string]) { throw 'Native publish manifest executable path is invalid.' }
    Assert-NativeEvidenceRelativePath $executable['path'] -Leaf
    Assert-NativeEvidenceInteger $executable['bytes'] -Positive
    Assert-NativeEvidenceHash $executable['sha256']
    if (-not $fileMap.ContainsKey($executable['path']) -or
        $fileMap[$executable['path']]['bytes'] -ne $executable['bytes'] -or
        $fileMap[$executable['path']]['sha256'] -cne $executable['sha256']) {
        throw 'Native publish manifest executable identity is inconsistent.'
    }
    $runtime = Get-NativeEvidenceFullPath $RuntimeDirectory
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    $runtimePrefix = $runtime.TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)) + [IO.Path]::DirectorySeparatorChar
    if ($manifest.Equals($runtime, $comparison) -or $manifest.StartsWith($runtimePrefix, $comparison)) {
        throw 'Native publish manifest must be outside its runtime directory.'
    }
    $actual = Get-NativeRuntimeIdentity $runtime $executable['path']
    if ($actual.filesSha256 -cne $filesHash -or $actual.runtimeArtifactBytes -ne $sum -or
        $actual.executable.sha256 -cne $executable['sha256'] -or
        $actual.executable.bytes -ne $executable['bytes']) {
        throw 'Native publish manifest does not match its runtime directory.'
    }
}
