# Selectively extract the Linux native image runtime for later evidence checks.
# Dot-sourcing this helper does not create files or change process settings.
. (Join-Path $PSScriptRoot 'native-image-publish-evidence.ps1')

function Assert-NativeImageArchiveOutputEmpty([string] $Path) {
    Assert-NativeEvidencePathAncestry $Path
    if ([IO.File]::Exists($Path)) {
        throw 'Native image archive extraction requires an empty directory.'
    }
    if ([IO.Directory]::Exists($Path)) {
        if ($IsLinux -and (Get-NativeEvidenceUnixFileType $Path) -ne 0x4000) {
            throw 'Native image archive extraction requires a regular directory.'
        }
        $enumerator = ([IO.DirectoryInfo]::new($Path)).EnumerateFileSystemInfos().GetEnumerator()
        try {
            if ($enumerator.MoveNext()) {
                throw 'Native image archive extraction requires an empty directory.'
            }
        }
        finally { $enumerator.Dispose() }
    }
}

function Register-NativeImageArchiveSelectedPath {
    param(
        [Collections.Generic.Dictionary[string, object]] $Paths,
        [string] $Path,
        [bool] $IsDirectory
    )
    # Register implicit ancestors too: app/Foo/a and app/foo/b must not merge
    # when the inspection host has a case-insensitive filesystem.
    $segments = $Path.Split('/')
    $prefix = ''
    for ($index = 0; $index -lt $segments.Length; $index++) {
        $prefix = if ($index -eq 0) { $segments[$index] } else { $prefix + '/' + $segments[$index] }
        $directory = $index -lt $segments.Length - 1 -or $IsDirectory
        $existing = $null
        if ($Paths.TryGetValue($prefix, [ref]$existing)) {
            if ($existing['path'] -cne $prefix -or $existing['directory'] -ne $directory) {
                throw 'Selected native image paths contain aliases or file conflicts.'
            }
        }
        else {
            $Paths.Add($prefix, [ordered]@{ path = $prefix; directory = $directory })
        }
    }
}

function Expand-NativeImageRuntimeArchive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $ArchivePath,
        [Parameter(Mandatory = $true)][string] $OutputDirectory,
        [Parameter(Mandatory = $true)][string] $Service
    )
    $ErrorActionPreference = 'Stop'
    if ($Service -cnotin @('admin', 'gateway')) {
        throw 'Native image archive service is invalid.'
    }

    $archive = $null
    $reader = $null
    $claim = $null
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(90))
    try {
        $archiveFile = Get-NativeEvidenceFullPath $ArchivePath
        $outputRoot = Get-NativeEvidenceFullPath $OutputDirectory
        Assert-NativeEvidenceRegularFile $archiveFile
        Assert-NativeImageArchiveOutputEmpty $outputRoot
        $deadline.Token.ThrowIfCancellationRequested()

        $archive = [IO.FileStream]::new($archiveFile, [IO.FileMode]::Open,
            [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $archiveBytes = [long]$archive.Length
        if ($archiveBytes -lt 1024 -or $archiveBytes -gt 4294967296L -or $archiveBytes % 512 -ne 0) {
            throw 'Native image archive size or alignment is invalid.'
        }

        [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
        # A concurrent invocation may have populated the directory since the
        # first check. CreateNew plus the retained, exclusively held claim also
        # prevents a second invocation from reusing a completed or failed run.
        Assert-NativeImageArchiveOutputEmpty $outputRoot
        $extractionId = [Guid]::NewGuid().ToString('N')
        $claimPath = [IO.Path]::Combine($outputRoot, 'native-image-extraction.claim')
        Assert-NativeEvidencePathAncestry $claimPath
        $claim = [IO.FileStream]::new($claimPath, [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write, [IO.FileShare]::None)
        $claimBytes = [Text.UTF8Encoding]::new($false).GetBytes($extractionId + "`n")
        $null = $claim.WriteAsync([ReadOnlyMemory[byte]]::new($claimBytes), $deadline.Token).
            AsTask().GetAwaiter().GetResult()
        $claim.Flush($true)
        Assert-NativeEvidencePathAncestry $outputRoot
        foreach ($initial in ([IO.DirectoryInfo]::new($outputRoot)).EnumerateFileSystemInfos()) {
            if ($initial.Name -cne 'native-image-extraction.claim' -or
                ($initial.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Native image archive destination changed before extraction.'
            }
        }

        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $selectedPaths = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        $files = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
        $roots = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $selectedBytes = 0L
        $entries = 0
        $buffer = [byte[]]::new(81920)
        $separator = [IO.Path]::DirectorySeparatorChar
        $outputPrefix = $outputRoot.TrimEnd($separator) + $separator
        $pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        $reader = [System.Formats.Tar.TarReader]::new($archive, $true)
        while ($true) {
            $deadline.Token.ThrowIfCancellationRequested()
            $entry = $reader.GetNextEntryAsync($false, $deadline.Token).GetAwaiter().GetResult()
            if ($null -eq $entry) { break }
            $entries++
            if ($entries -gt 200000) { throw 'Native image archive entry count exceeds its bound.' }

            $name = $entry.Name
            if ($name -isnot [string] -or $name.Length -gt 4096 -or
                $name -match '[\\\x00-\x1f\x7f]' -or $name.StartsWith('/', [StringComparison]::Ordinal) -or
                $name -match '^[A-Za-z]:' -or [IO.Path]::IsPathRooted($name)) {
                throw 'Native image archive path is invalid.'
            }
            if ($name.StartsWith('./', [StringComparison]::Ordinal)) { $name = $name.Substring(2) }
            $isDirectory = $entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory
            if ($name.EndsWith('/', [StringComparison]::Ordinal)) {
                if (-not $isDirectory) { throw 'Native image archive file path ends in a separator.' }
                $name = $name.Substring(0, $name.Length - 1)
            }
            if ($isDirectory -and $name -cin @('', '.')) {
                if ($entry.Length -ne 0 -or -not $seen.Add('.')) {
                    throw 'Native image archive root directory is invalid or duplicated.'
                }
                continue
            }
            if ([string]::IsNullOrWhiteSpace($name)) { throw 'Native image archive path is empty.' }
            foreach ($segment in $name.Split('/')) {
                if ([string]::IsNullOrEmpty($segment) -or $segment -cin @('.', '..')) {
                    throw 'Native image archive contains a noncanonical path.'
                }
            }
            if (-not $seen.Add($name)) { throw 'Native image archive contains duplicate paths.' }
            if ($name -match '(?i)(^|/)(dotnet|sdk|symbols)(/|$)|Roslyn|csc\.dll|Microsoft\.EntityFrameworkCore\.Design|\.(pdb|dbg)$') {
                throw 'Native image archive contains SDK, design or symbol content.'
            }
            if ($entry.Length -lt 0 -or ($entry.Length -gt 0 -and
                    ($entry.DataOffset -lt 0 -or $entry.DataOffset -gt $archiveBytes -or
                     $entry.Length -gt $archiveBytes - $entry.DataOffset))) {
                throw 'Native image archive entry data exceeds its archive.'
            }

            $root = $name.Split('/')[0]
            if ($root -cnotin @('app', 'conduit-evidence')) { continue }
            Assert-NativeEvidenceRelativePath $name
            if (-not $isDirectory -and $entry.EntryType -notin @(
                    [System.Formats.Tar.TarEntryType]::RegularFile,
                    [System.Formats.Tar.TarEntryType]::V7RegularFile)) {
                throw 'Selected native image payload contains a link or special file.'
            }
            Register-NativeImageArchiveSelectedPath $selectedPaths $name $isDirectory
            $destination = [IO.Path]::GetFullPath([IO.Path]::Combine($outputRoot, $name.Replace('/', $separator)))
            if (-not $destination.StartsWith($outputPrefix, $pathComparison)) {
                throw 'Selected native image path escaped its destination.'
            }
            Assert-NativeEvidencePathAncestry $destination
            if ($isDirectory) {
                if ($entry.Length -ne 0 -or [IO.File]::Exists($destination)) {
                    throw 'Selected native image directory conflicts with a file.'
                }
                [IO.Directory]::CreateDirectory($destination) | Out-Null
                Assert-NativeEvidencePathAncestry $destination
                if ($name -ceq $root) { $null = $roots.Add($root) }
                continue
            }
            if ($name -ceq $root -or $entry.Length -gt 536870912L -or
                $entry.DataOffset -lt 0 -or $entry.DataOffset -gt $archiveBytes -or
                $entry.Length -gt $archiveBytes - $entry.DataOffset -or
                ($entry.Length -gt 0 -and $null -eq $entry.DataStream)) {
                throw 'Selected native image file length or data is invalid.'
            }
            if ($root -ceq 'conduit-evidence' -and ($entry.Length -le 0 -or $entry.Length -gt 16777216L)) {
                throw 'Selected native image evidence file exceeds its bound.'
            }
            if ($files.Count -ge 10000 -or $entry.Length -gt 1073741824L - $selectedBytes) {
                throw 'Selected native image payload exceeds its aggregate bound.'
            }
            $mode = [int]$entry.Mode
            if ($entry.Uid -ne 1654 -or $entry.Gid -ne 1654 -or
                ($mode -band [int][IO.UnixFileMode]::UserRead) -eq 0 -or
                ($mode -band 3584) -ne 0 -or ($mode -band -4096) -ne 0) {
                throw 'Selected native image file ownership or mode is invalid.'
            }
            $selectedBytes += [long]$entry.Length
            $files.Add($name, [ordered]@{
                uid = [int]$entry.Uid; gid = [int]$entry.Gid; mode = $mode; bytes = [long]$entry.Length
            })
            $parent = [IO.Path]::GetDirectoryName($destination)
            [IO.Directory]::CreateDirectory($parent) | Out-Null
            Assert-NativeEvidencePathAncestry $parent
            Assert-NativeEvidencePathAncestry $destination
            $output = [IO.FileStream]::new($destination, [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $remaining = [long]$entry.Length
                while ($remaining -gt 0) {
                    $count = [int][Math]::Min($remaining, $buffer.Length)
                    $read = $entry.DataStream.ReadAsync([Memory[byte]]::new($buffer, 0, $count),
                        $deadline.Token).AsTask().GetAwaiter().GetResult()
                    if ($read -le 0 -or $read -gt $count) { throw 'Selected native image file data is truncated.' }
                    $null = $output.WriteAsync([ReadOnlyMemory[byte]]::new($buffer, 0, $read),
                        $deadline.Token).AsTask().GetAwaiter().GetResult()
                    $remaining -= $read
                }
                $deadline.Token.ThrowIfCancellationRequested()
                $output.Flush($true)
                if ($output.Length -ne $entry.Length) { throw 'Selected native image file extraction changed its length.' }
            }
            finally { $output.Dispose() }
        }
        $deadline.Token.ThrowIfCancellationRequested()
        if ($archive.Length -ne $archiveBytes -or -not $roots.Contains('app') -or -not $roots.Contains('conduit-evidence')) {
            throw 'Native image archive changed or omitted its required root directories.'
        }
        $executables = Get-NativeImageExecutableNames $Service 'linux-x64'
        foreach ($executable in @($executables['application'], $executables['healthProbe'])) {
            $relative = 'app/' + $executable
            if (-not $files.ContainsKey($relative) -or $files[$relative]['bytes'] -le 0 -or
                ($files[$relative]['mode'] -band [int][IO.UnixFileMode]::UserExecute) -eq 0) {
                throw 'Native image archive omitted a readable executable with its required execute mode.'
            }
        }
        $orderedFiles = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
        $orderedPaths = [string[]]@($files.Keys)
        [Array]::Sort($orderedPaths, [StringComparer]::Ordinal)
        foreach ($path in $orderedPaths) { $orderedFiles.Add($path, $files[$path]) }
        return [ordered]@{
            archiveBytes = $archiveBytes
            entries = [int]$entries
            selectedBytes = [long]$selectedBytes
            files = $orderedFiles
            payloadDirectory = [IO.Path]::Combine($outputRoot, 'app')
            evidenceDirectory = [IO.Path]::Combine($outputRoot, 'conduit-evidence')
            extractionId = $extractionId
        }
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        if ($null -ne $archive) { $archive.Dispose() }
        if ($null -ne $claim) { $claim.Dispose() }
        $deadline.Dispose()
    }
}
