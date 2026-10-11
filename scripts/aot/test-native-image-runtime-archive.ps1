#!/usr/bin/env pwsh
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot 'native-image-runtime-archive.ps1'
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$temporaryRoot = Join-Path $temporaryParent ('conduit-image-archive-contract-' + [Guid]::NewGuid().ToString('N'))
$cases = 0
$sequence = 0
$links = [Collections.Generic.List[string]]::new()
$failure = $null
$cleanupFailure = $false

function Assert-ArchiveContract([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function New-ArchiveCase {
    $script:sequence++
    $path = Join-Path $temporaryRoot ('case-' + $script:sequence.ToString('D3'))
    [IO.Directory]::CreateDirectory($path) | Out-Null
    return $path
}

function New-ArchiveEntry([string] $Name, [string] $Type = 'RegularFile',
    [byte[]] $Body = @(), [int] $Mode = 420, [int] $Uid = 1654, [int] $Gid = 1654,
    [string] $LinkName = '') {
    return [ordered]@{ Name = $Name; Type = $Type; Body = $Body; Mode = $Mode; Uid = $Uid; Gid = $Gid; LinkName = $LinkName }
}

function Get-ValidArchiveEntries([string] $Service = 'admin') {
    $main = if ($Service -ceq 'admin') { 'ConduitLLM.Admin' } else { 'ConduitLLM.Gateway' }
    return ,@(
        (New-ArchiveEntry 'app' 'Directory' -Mode 493),
        (New-ArchiveEntry 'conduit-evidence' 'Directory' -Mode 493),
        (New-ArchiveEntry ('app/' + $main) -Body ([byte[]]@(1, 2, 3, 4)) -Mode 493),
        (New-ArchiveEntry 'app/conduit-health-probe' -Body ([byte[]]@(5, 6, 7)) -Mode 493),
        (New-ArchiveEntry 'app/.hidden' -Body ([byte[]]@(8, 9))),
        (New-ArchiveEntry 'app/nested/café 雪/data.json' -Body ([Text.Encoding]::UTF8.GetBytes('{"value":"café 雪 🔎"}'))),
        (New-ArchiveEntry 'app/nested' 'Directory' -Mode 493),
        (New-ArchiveEntry 'app/nested/café 雪' 'Directory' -Mode 493),
        (New-ArchiveEntry 'conduit-evidence/image-publish.json' -Body ([Text.Encoding]::UTF8.GetBytes('{"kind":"synthetic-wrapper"}'))),
        (New-ArchiveEntry 'conduit-evidence/application.json' -Body ([Text.Encoding]::UTF8.GetBytes('{"kind":"synthetic-application"}'))),
        (New-ArchiveEntry 'conduit-evidence/health-probe.json' -Body ([Text.Encoding]::UTF8.GetBytes('{"kind":"synthetic-probe"}'))),
        (New-ArchiveEntry 'usr/lib/base-link' 'SymbolicLink' -LinkName '../../lib/base-target'),
        (New-ArchiveEntry 'usr/lib/base-target' -Body ([byte[]]@(10)))
    )
}

function Write-ContractArchive([string] $Path, [object[]] $Entries, [string] $Format = 'Pax') {
    $file = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $writer = $null
    try {
        $writer = [Formats.Tar.TarWriter]::new($file, [Enum]::Parse([Formats.Tar.TarEntryFormat], $Format), $true)
        foreach ($spec in $Entries) {
            $type = [Enum]::Parse([Formats.Tar.TarEntryType], $spec.Type)
            $entry = if ($Format -ceq 'Gnu') { [Formats.Tar.GnuTarEntry]::new($type, $spec.Name) }
                else { [Formats.Tar.PaxTarEntry]::new($type, $spec.Name) }
            $entry.Uid = $spec.Uid; $entry.Gid = $spec.Gid; $entry.Mode = [IO.UnixFileMode]$spec.Mode
            $body = $null
            try {
                if ($spec.Type -ceq 'RegularFile') {
                    $body = [IO.MemoryStream]::new([byte[]]$spec.Body, $false)
                    $entry.DataStream = $body
                }
                elseif ($spec.Type -cin @('SymbolicLink', 'HardLink')) { $entry.LinkName = $spec.LinkName }
                $writer.WriteEntry($entry)
            }
            finally { if ($null -ne $body) { $body.Dispose() } }
        }
    }
    finally { if ($null -ne $writer) { $writer.Dispose() }; $file.Dispose() }
}

function Get-ArchiveDataOffset([string] $Path, [string] $Name) {
    $file = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $reader = [Formats.Tar.TarReader]::new($file, $true)
    try {
        while ($null -ne ($entry = $reader.GetNextEntry($false))) {
            if ($entry.Name -ceq $Name) {
                Assert-ArchiveContract ($entry.DataOffset -ge 512) 'Synthetic tar data offset was unavailable.'
                return [long]$entry.DataOffset
            }
        }
        throw 'Synthetic tar entry was not found.'
    }
    finally { $reader.Dispose(); $file.Dispose() }
}

function Set-ArchiveEntryType([string] $Path, [string] $Name, [char] $TypeFlag) {
    # BCL constructors restrict FIFO/device creation to Unix. Patch only a GNU
    # regular-entry type/checksum so these input boundaries run on both hosts.
    $offset = (Get-ArchiveDataOffset $Path $Name) - 512
    $file = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $header = [byte[]]::new(512)
        $file.Position = $offset; $file.ReadExactly($header)
        $header[156] = [byte]$TypeFlag
        for ($index = 148; $index -lt 156; $index++) { $header[$index] = 32 }
        $sum = 0; foreach ($value in $header) { $sum += $value }
        $checksum = [Text.Encoding]::ASCII.GetBytes(([Convert]::ToString($sum, 8).PadLeft(6, '0') + [char]0 + ' '))
        [Array]::Copy($checksum, 0, $header, 148, 8)
        $file.Position = $offset; $file.Write($header); $file.Flush($true)
    }
    finally { $file.Dispose() }
}

function Get-ArchiveHash([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    try { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
    finally { $stream.Dispose() }
}

function Assert-RejectedArchive([string] $Archive, [string] $Output, [string] $Service = 'admin') {
    $before = Get-ArchiveHash $Archive
    $rejected = $false
    try { $unused = Expand-NativeImageRuntimeArchive -ArchivePath $Archive -OutputDirectory $Output -Service $Service }
    catch { $rejected = $true }
    Assert-ArchiveContract $rejected 'Invalid archive or extraction destination was accepted.'
    Assert-ArchiveContract ((Get-ArchiveHash $Archive) -ceq $before) 'Extraction changed the source archive.'
    $script:cases++
}

function New-ContractDirectoryLink([string] $Path, [string] $Target) {
    if ($IsWindows) { New-Item -ItemType Junction -Path $Path -Target $Target | Out-Null }
    else { [IO.Directory]::CreateSymbolicLink($Path, $Target) | Out-Null }
    $script:links.Add($Path)
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $loadResults = @(. $helper)
    Assert-ArchiveContract ($loadResults.Count -eq 0 -and [IO.Directory]::GetFileSystemEntries($temporaryRoot).Length -eq 0) 'Loading archive helpers produced output or changed the owned directory.'
    $cases++

    foreach ($positive in @(@{ Service = 'admin'; Format = 'Pax'; ExistingEmpty = $false },
        @{ Service = 'gateway'; Format = 'Gnu'; ExistingEmpty = $true })) {
        $case = New-ArchiveCase; $archive = Join-Path $case 'rootfs.tar'; $output = Join-Path $case 'extracted'
        $entries = Get-ValidArchiveEntries $positive.Service
        Write-ContractArchive $archive $entries $positive.Format
        if ($positive.ExistingEmpty) { [IO.Directory]::CreateDirectory($output) | Out-Null }
        $results = @(Expand-NativeImageRuntimeArchive -ArchivePath $archive -OutputDirectory $output -Service $positive.Service)
        Assert-ArchiveContract ($results.Count -eq 1 -and $results[0] -is [Collections.Specialized.OrderedDictionary]) 'Extraction must return one ordered result dictionary.'
        $result = $results[0]
        Assert-ArchiveContract (($result.Keys -join '|') -ceq 'archiveBytes|entries|selectedBytes|files|payloadDirectory|evidenceDirectory|extractionId') 'Extraction result properties differ.'
        Assert-ArchiveContract ($result.archiveBytes -is [long] -and $result.archiveBytes -eq ([IO.FileInfo]::new($archive)).Length -and
            $result.entries -is [int] -and $result.entries -eq $entries.Count -and $result.selectedBytes -is [long]) 'Archive length/count result differs.'
        Assert-ArchiveContract ($result.extractionId -is [string] -and $result.extractionId -cmatch '^[0-9a-f]{32}$' -and
            $result.payloadDirectory -ceq (Join-Path $output 'app') -and $result.evidenceDirectory -ceq (Join-Path $output 'conduit-evidence')) 'Extraction identity or absolute directories differ.'
        Assert-ArchiveContract ($result.files -is [Collections.Generic.Dictionary[string, object]] -and
            $result.files.Comparer.Equals('a', 'a') -and -not $result.files.Comparer.Equals('a', 'A')) 'Extracted file inventory is not an ordinal dictionary.'
        [long]$selectedBytes = 0; $selectedFiles = 0
        foreach ($spec in $entries) {
            if ($spec.Type -cne 'RegularFile' -or $spec.Name -cnotmatch '^(app|conduit-evidence)/') { continue }
            $selectedFiles++; $selectedBytes += $spec.Body.Length
            $metadata = $result.files[$spec.Name]
            Assert-ArchiveContract ($null -ne $metadata -and $metadata.uid -is [int] -and $metadata.uid -eq 1654 -and
                $metadata.gid -is [int] -and $metadata.gid -eq 1654 -and $metadata.mode -is [int] -and
                $metadata.mode -eq $spec.Mode -and $metadata.bytes -is [long] -and $metadata.bytes -eq $spec.Body.Length) 'Selected archive metadata differs.'
            $actual = [IO.File]::ReadAllBytes((Join-Path $output $spec.Name))
            Assert-ArchiveContract ([Linq.Enumerable]::SequenceEqual[byte]($actual, [byte[]]$spec.Body)) 'Selected archive file bytes differ.'
        }
        Assert-ArchiveContract ($result.selectedBytes -eq $selectedBytes -and $result.files.Count -eq $selectedFiles -and
            -not [IO.Directory]::Exists((Join-Path $output 'usr'))) 'Selected-byte inventory or ignored base filesystem differs.'
        $claim = Join-Path $output 'native-image-extraction.claim'
        Assert-ArchiveContract ([IO.File]::ReadAllText($claim) -ceq ($result.extractionId + "`n")) 'Successful extraction did not retain its exact ownership claim.'
        $cases++
        $claimHash = Get-ArchiveHash $claim
        Assert-RejectedArchive $archive $output $positive.Service
        Assert-ArchiveContract ((Get-ArchiveHash $claim) -ceq $claimHash) 'Repeated extraction changed the existing claim.'
    }

    $invalid = @(
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'app/link' 'SymbolicLink' -LinkName '../outside') },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'app/link' 'HardLink' -LinkName 'app/ConduitLLM.Admin') },
        @{ Kind = 'special'; Flag = [char]'6' }, @{ Kind = 'special'; Flag = [char]'3' }, @{ Kind = 'special'; Flag = [char]'4' },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'app/ConduitLLM.Admin' -Body ([byte[]]@(20))) },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry './app/ConduitLLM.Admin' -Body ([byte[]]@(20))) },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'app/.HIDDEN' -Body ([byte[]]@(20))) },
        @{ Kind = 'implicit-case' },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'app/../escape' -Body ([byte[]]@(20))) },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry '/app/rooted' -Body ([byte[]]@(20))) },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'C:/app/rooted' -Body ([byte[]]@(20))) },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'app\backslash' -Body ([byte[]]@(20))) },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'app/CON.txt' -Body ([byte[]]@(20))) },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'app//empty' -Body ([byte[]]@(20))) },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'app/./dot' -Body ([byte[]]@(20))) },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'app/trailing.' -Body ([byte[]]@(20))) },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'app/trailing ' -Body ([byte[]]@(20))) },
        @{ Kind = 'file-parent' }, @{ Kind = 'directory-data' },
        @{ Kind = 'remove'; Name = 'app' }, @{ Kind = 'remove'; Name = 'conduit-evidence' },
        @{ Kind = 'remove'; Name = 'app/ConduitLLM.Admin' }, @{ Kind = 'remove'; Name = 'app/conduit-health-probe' },
        @{ Kind = 'exe-only' },
        @{ Kind = 'property'; Name = 'app/ConduitLLM.Admin'; Property = 'Mode'; Value = 420 },
        @{ Kind = 'property'; Name = 'app/conduit-health-probe'; Property = 'Mode'; Value = 420 },
        @{ Kind = 'property'; Name = 'app/.hidden'; Property = 'Mode'; Value = 128 },
        @{ Kind = 'property'; Name = 'app/.hidden'; Property = 'Uid'; Value = 0 },
        @{ Kind = 'property'; Name = 'app/.hidden'; Property = 'Gid'; Value = 0 },
        @{ Kind = 'property'; Name = 'app/.hidden'; Property = 'Mode'; Value = 2468 },
        @{ Kind = 'property'; Name = 'app/.hidden'; Property = 'Mode'; Value = 1444 },
        @{ Kind = 'property'; Name = 'app/.hidden'; Property = 'Mode'; Value = 932 },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'usr/share/dotnet/runtime' -Body ([byte[]]@(20))) },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'opt/sdk/compiler' -Body ([byte[]]@(20))) },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'opt/symbols/debug' -Body ([byte[]]@(20))) },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'opt/Roslyn/compiler' -Body ([byte[]]@(20))) },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'opt/csc.dll' -Body ([byte[]]@(20))) },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'app/Microsoft.EntityFrameworkCore.Design.dll' -Body ([byte[]]@(20))) },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'var/unselected.pdb' -Body ([byte[]]@(20))) },
        @{ Kind = 'append'; Entry = (New-ArchiveEntry 'var/unselected.dbg' -Body ([byte[]]@(20))) },
        @{ Kind = 'evidence-limit' }, @{ Kind = 'truncate' }
    )
    foreach ($scenario in $invalid) {
        $case = New-ArchiveCase; $archive = Join-Path $case 'invalid.tar'; $output = Join-Path $case 'extracted'
        $entries = Get-ValidArchiveEntries
        switch ($scenario.Kind) {
            'append' { $entries += $scenario.Entry }
            'special' { $entries += New-ArchiveEntry 'app/special' }
            'implicit-case' { $entries += New-ArchiveEntry 'app/Foo/a' -Body ([byte[]]@(20)); $entries += New-ArchiveEntry 'app/foo/b' -Body ([byte[]]@(21)) }
            'file-parent' { $entries += New-ArchiveEntry 'app/conflict' -Body ([byte[]]@(20)); $entries += New-ArchiveEntry 'app/conflict/child' -Body ([byte[]]@(21)) }
            'directory-data' { $entries += New-ArchiveEntry 'app/data-directory' -Body ([byte[]]@(20)) }
            'remove' { $entries = @($entries | Where-Object { $_.Name -cne $scenario.Name }) }
            'exe-only' { foreach ($entry in $entries) { if ($entry.Name -cin @('app/ConduitLLM.Admin', 'app/conduit-health-probe')) { $entry.Name += '.exe' } } }
            'property' { $entry = @($entries | Where-Object { $_.Name -ceq $scenario.Name })[0]; $entry[$scenario.Property] = $scenario.Value }
            'evidence-limit' { $entry = @($entries | Where-Object { $_.Name -ceq 'conduit-evidence/image-publish.json' })[0]; $entry.Body = [byte[]]::new(16777217) }
            'truncate' { $entry = @($entries | Where-Object { $_.Name -ceq 'app/ConduitLLM.Admin' })[0]; $entry.Body = [byte[]]::new(1024) }
        }
        Write-ContractArchive $archive $entries 'Gnu'
        if ($scenario.Kind -ceq 'special') { Set-ArchiveEntryType $archive 'app/special' $scenario.Flag }
        if ($scenario.Kind -ceq 'directory-data') { Set-ArchiveEntryType $archive 'app/data-directory' ([char]'5') }
        if ($scenario.Kind -ceq 'truncate') {
            $length = (Get-ArchiveDataOffset $archive 'app/ConduitLLM.Admin') + 512
            $file = [IO.FileStream]::new($archive, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $file.SetLength($length); $file.Flush($true) } finally { $file.Dispose() }
        }
        Assert-RejectedArchive $archive $output
        $failedClaim = Join-Path $output 'native-image-extraction.claim'
        Assert-ArchiveContract ([IO.File]::Exists($failedClaim) -and
            [IO.File]::ReadAllText($failedClaim) -cmatch '^[0-9a-f]{32}\n$') 'Failed extraction discarded or changed its retained ownership claim.'
    }

    foreach ($name in @('visible-sentinel', '.hidden-sentinel', 'native-image-extraction.claim')) {
        $case = New-ArchiveCase; $archive = Join-Path $case 'rootfs.tar'; $output = Join-Path $case 'extracted'
        Write-ContractArchive $archive (Get-ValidArchiveEntries)
        [IO.Directory]::CreateDirectory($output) | Out-Null
        $sentinel = Join-Path $output $name
        [IO.File]::WriteAllText($sentinel, 'existing owner must survive')
        $heldClaim = $null
        try {
            if ($name -ceq 'native-image-extraction.claim') {
                $heldClaim = [IO.FileStream]::new($sentinel, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            }
            Assert-RejectedArchive $archive $output
        }
        finally { if ($null -ne $heldClaim) { $heldClaim.Dispose() } }
        Assert-ArchiveContract ([IO.File]::ReadAllText($sentinel) -ceq 'existing owner must survive' -and
            [IO.Directory]::GetFileSystemEntries($output).Length -eq 1) 'Freshness rejection changed existing extraction state.'
    }

    foreach ($kind in @('output-link', 'output-ancestor-link', 'archive-ancestor-link')) {
        $case = New-ArchiveCase; $archive = Join-Path $case 'rootfs.tar'; $output = Join-Path $case 'extracted'
        $target = Join-Path $case 'separate-target'; [IO.Directory]::CreateDirectory($target) | Out-Null
        $sentinel = Join-Path $target 'sentinel'; [IO.File]::WriteAllText($sentinel, 'link target must survive')
        if ($kind -ceq 'archive-ancestor-link') {
            Write-ContractArchive (Join-Path $target 'rootfs.tar') (Get-ValidArchiveEntries)
            $link = Join-Path $case 'linked-archive'; New-ContractDirectoryLink $link $target
            $archive = Join-Path $link 'rootfs.tar'
        }
        else {
            Write-ContractArchive $archive (Get-ValidArchiveEntries)
            if ($kind -ceq 'output-link') { New-ContractDirectoryLink $output $target }
            else { $link = Join-Path $case 'linked-parent'; New-ContractDirectoryLink $link $target; $output = Join-Path $link 'extracted' }
        }
        $before = @([IO.Directory]::GetFileSystemEntries($target) | Sort-Object) -join '|'
        Assert-RejectedArchive $archive $output
        Assert-ArchiveContract ([IO.File]::ReadAllText($sentinel) -ceq 'link target must survive' -and
            ((@([IO.Directory]::GetFileSystemEntries($target) | Sort-Object) -join '|') -ceq $before)) 'Linked-path rejection changed the separate target.'
    }

    $case = New-ArchiveCase; $archive = Join-Path $case 'empty.tar'; [IO.File]::WriteAllBytes($archive, [byte[]]@())
    Assert-RejectedArchive $archive (Join-Path $case 'extracted')
    $case = New-ArchiveCase; $archive = Join-Path $case 'rootfs.tar'; Write-ContractArchive $archive (Get-ValidArchiveEntries)
    Assert-RejectedArchive $archive (Join-Path $case 'extracted') 'unknown'
}
catch { $failure = $_ }
finally {
    try {
        $resolved = [IO.Path]::GetFullPath($temporaryRoot)
        Assert-ArchiveContract ([IO.Path]::GetDirectoryName($resolved) -ceq $temporaryParent -and
            [IO.Path]::GetFileName($resolved) -cmatch '^conduit-image-archive-contract-[0-9a-f]{32}$') 'Owned archive test cleanup target was rejected.'
        foreach ($link in $links) {
            Assert-ArchiveContract ([IO.Path]::GetFullPath($link).StartsWith($resolved + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::Ordinal)) 'Owned archive test link escaped the temporary root.'
            [IO.Directory]::Delete($link)
        }
        if ([IO.Directory]::Exists($resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
        Assert-ArchiveContract (-not [IO.Directory]::Exists($resolved)) 'Owned archive test directory remained after cleanup.'
    }
    catch { $cleanupFailure = $true }
}
if ($cleanupFailure) { throw 'Native image archive contracts could not clean their owned directory.' }
if ($null -ne $failure) { throw $failure }
Write-Output ('Verified native image runtime archive contracts; cases=' + $cases + '; skipped=0; cleanupConfirmed=true.')
