#!/usr/bin/env pwsh
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$helperPath = Join-Path $PSScriptRoot 'native-publish-evidence.ps1'
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('conduit-file-types-' + [Guid]::NewGuid().ToString('N'))))
$rows = [Collections.Generic.List[object]]::new()
$ownedLinks = [Collections.Generic.List[string]]::new()
$ownedSockets = [Collections.Generic.List[IDisposable]]::new()
$proofFailure = $null
$cleanupFailure = $null
$initializationMilliseconds = $null

function Require([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Require-Rejection([scriptblock] $Action, [string] $Pattern = '') {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_ }
    Require ($null -ne $failure) 'Expected this file-type observation to fail closed.'
    if ($Pattern) { Require ($failure.Exception.Message -match $Pattern) 'Rejection came from a different boundary.' }
}
function Reset-Fake {
    [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Calls = 0
    [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Result = 0
    [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Error = 0
    [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::ReturnedMask = 1
    [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::ReturnedMode = 0x81A4
    [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Failure = $null
    [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::FixtureIsLinux = $true
    [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::FixtureArchitecture = [Runtime.InteropServices.Architecture]::X64
    $script:fallbackCalls = 0
    $script:fallbackType = 0x8000
    $script:fallbackFailure = $false
    $script:fallbackDriver = $null
    $script:statDiscoveryCalls = 0
    $script:statMissing = $false
}
function Case([string] $Name, [scriptblock] $Action) {
    Reset-Fake
    try { & $Action | Out-Null; $rows.Add([ordered]@{ name = $Name; passed = $true }) }
    catch { $rows.Add([ordered]@{ name = $Name; passed = $false; message = $_.Exception.Message }) }
}
function Get-FixtureStatxFileType([string] $Path) {
    return [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::ReadFileType($Path)
}
function Invoke-FixtureStat([string] $Path) {
    $script:fallbackCalls++
    if ($script:fallbackFailure) { throw 'Independent fixture stat lookup failed.' }
    if ($null -ne $script:fallbackDriver) { return & $script:fallbackDriver $Path }
    return $script:fallbackType
}
function Get-FixtureStatCommand {
    $script:statDiscoveryCalls++
    if ($script:statMissing) { throw 'Owned fixture stat tool is unavailable.' }
    return [pscustomobject]@{ Path = (Get-Process -Id $PID).Path }
}
function New-FixtureDirectory([string] $Name) {
    $path = Join-Path $temporaryRoot $Name
    [IO.Directory]::CreateDirectory($path) | Out-Null
    return $path
}
function Assert-StatChildExited([string] $Marker) {
    $identity = [IO.File]::ReadAllText($Marker) | ConvertFrom-Json -AsHashtable
    $child = $null
    try { $child = [Diagnostics.Process]::GetProcessById([int]$identity.pid) }
    catch [ArgumentException] { return }
    $primary = $null
    $cleanupErrors = [Collections.Generic.List[Exception]]::new()
    try {
        # A reused PID is not the owned child and must never be signaled.
        if ($child.StartTime.ToUniversalTime().Ticks -eq [long]$identity.startTimeUtcTicks) {
            if (-not $child.HasExited) {
                $primary = [InvalidOperationException]::new('Owned stat child survived fallback return or rejection.')
                try { $child.Kill($true) }
                catch { $cleanupErrors.Add($_.Exception) }
            }
            try { Require ($child.WaitForExit(5000)) 'Surviving owned stat child could not be reaped.' }
            catch { $cleanupErrors.Add($_.Exception) }
        }
    }
    catch { $cleanupErrors.Add($_.Exception) }
    finally {
        try { $child.Dispose() }
        catch { $cleanupErrors.Add($_.Exception) }
    }
    if ($null -ne $primary -and $cleanupErrors.Count -gt 0) {
        throw [AggregateException]::new('Owned stat child survived and its cleanup failed.', [Exception[]](@($primary) + $cleanupErrors.ToArray()))
    }
    if ($null -ne $primary) { throw $primary }
    if ($cleanupErrors.Count -gt 0) { throw [AggregateException]::new('Owned stat child identity or cleanup failed.', $cleanupErrors.ToArray()) }
}
function Invoke-BoundedHashRejection([string] $Path, [switch] $ReplaceWithFifo) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Process -Id $PID).Path
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', $script:hashChild, '-Helper', $helperPath, '-Path', $Path)) {
        $start.ArgumentList.Add($argument)
    }
    if ($ReplaceWithFifo) { $start.ArgumentList.Add('-ReplaceWithFifo') }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $null
    $stderr = $null
    $primary = $null
    $cleanupErrors = [Collections.Generic.List[Exception]]::new()
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) {
            throw 'Owned hash child blocked or exceeded its deadline.'
        }
        Require ([Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000)) 'Owned hash child pipes did not close.'
        Require ($process.ExitCode -eq 0 -and $stdout.Result.Trim() -ceq 'REJECTED_BEFORE_OPEN') 'The actual hash guard did not reject this path before opening it.'
    }
    catch { $primary = $_ }
    finally {
        try { if (-not $process.HasExited) { $process.Kill($true) } }
        catch { $cleanupErrors.Add($_.Exception) }
        $reaped = $false
        try {
            $reaped = $process.WaitForExit(5000)
            Require $reaped 'Owned hash child could not be reaped.'
        }
        catch { $cleanupErrors.Add($_.Exception) }
        foreach ($task in @($stdout, $stderr)) {
            if ($null -eq $task) { continue }
            try { $task.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() | Out-Null }
            catch { $cleanupErrors.Add($_.Exception) }
        }
        if ($reaped) {
            try { $process.Dispose() }
            catch { $cleanupErrors.Add($_.Exception) }
        }
    }
    if ($null -ne $primary -and $cleanupErrors.Count -gt 0) {
        throw [AggregateException]::new('Owned hash work and child cleanup failed.', [Exception[]](@($primary.Exception) + $cleanupErrors.ToArray()))
    }
    if ($null -ne $primary) { throw $primary }
    if ($cleanupErrors.Count -gt 0) { throw [AggregateException]::new('Owned hash child cleanup failed.', $cleanupErrors.ToArray()) }
}

try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    . $helperPath
    $regular = Join-Path $temporaryRoot 'regular.txt'
    $unicode = Join-Path $temporaryRoot 'é雪😀.txt'
    [IO.File]::WriteAllText($regular, 'regular fixture', $utf8)
    [IO.File]::WriteAllText($unicode, 'UTF-8 fixture café 雪', $utf8)

    # Extract the actual interop implementation. Only namespace, syscall, last
    # errno and platform discovery are substituted; validation is production code.
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($helperPath, [ref]$tokens, [ref]$errors)
    Require (@($errors).Count -eq 0) 'Evidence helper syntax is invalid.'
    $literals = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.StringConstantExpressionAst] -and
        $node.Value.Contains('public static class LinuxFileType')
    }, $true))
    Require ($literals.Count -eq 1) 'Expected one exact managed file-type implementation literal.'
    $managed = $literals[0].Value
    if ($IsLinux) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        Require ((Get-NativeEvidenceUnixFileType $regular) -eq 0x8000) 'Initial live regular-file lookup failed.'
        $timer.Stop()
        $initializationMilliseconds = $timer.Elapsed.TotalMilliseconds
    }
    elseif ($null -eq ('ConduitLLM.NativeEvidence.LinuxFileType' -as [type])) {
        # Compile-only on Windows: the real libc import is never invoked here.
        Add-Type -TypeDefinition $managed | Out-Null
    }

    $nativeType = 'ConduitLLM.NativeEvidence.LinuxFileType' -as [type]
    Require ($null -ne $nativeType) 'Managed file-type helper did not compile.'
    $nativeMethod = $nativeType.GetMethod('Statx', [Reflection.BindingFlags]'Static,NonPublic')
    $bufferType = $nativeMethod.GetParameters()[4].ParameterType.GetElementType()

    Require ($managed.Split('namespace ConduitLLM.NativeEvidence', [StringSplitOptions]::None).Length -eq 2) 'Managed namespace seam differs.'
    $fixtureSource = $managed.Replace('namespace ConduitLLM.NativeEvidence', 'namespace ConduitLLM.NativeEvidenceFileTypeFixture')
    $syscall = [regex]::new('(?s)\[DllImport\("libc",[^\]]+\)\]\s*private static extern int Statx\([^;]+;')
    Require ($syscall.Matches($fixtureSource).Count -eq 1) 'Expected one exact libc statx declaration seam.'
    $fixtureSource = $syscall.Replace($fixtureSource, @'
private static int Statx(int directory, byte[] pathname, int flags, uint mask, out StatxBuffer buffer)
        {
            Calls++;
            LastDirectory = directory;
            LastPath = (byte[])pathname.Clone();
            LastFlags = flags;
            LastMask = mask;
            buffer = new StatxBuffer { Mask = ReturnedMask, Mode = ReturnedMode };
            if (Failure != null) throw Failure;
            return Result;
        }
'@, 1)
    foreach ($replacement in @(
        @('Marshal.GetLastPInvokeError()', 'Error', 1),
        @('OperatingSystem.IsLinux()', 'FixtureIsLinux', 1),
        @('RuntimeInformation.ProcessArchitecture', 'FixtureArchitecture', 2)
    )) {
        Require ($fixtureSource.Split([string]$replacement[0], [StringSplitOptions]::None).Length -eq ([int]$replacement[2] + 1)) 'Exact syscall metadata seam cardinality differs.'
        $fixtureSource = $fixtureSource.Replace([string]$replacement[0], [string]$replacement[1])
    }
    $classOpening = [regex]::new('public static class LinuxFileType\s*\{')
    Require ($classOpening.Matches($fixtureSource).Count -eq 1) 'Managed fixture class seam differs.'
    $fixtureSource = $classOpening.Replace($fixtureSource, @'
public static class LinuxFileType
    {
        public static int Calls, Result, Error, LastDirectory, LastFlags;
        public static uint ReturnedMask, LastMask;
        public static ushort ReturnedMode;
        public static byte[] LastPath;
        public static Exception Failure;
        public static bool FixtureIsLinux;
        public static Architecture FixtureArchitecture;
'@, 1)
    Add-Type -TypeDefinition $fixtureSource | Out-Null

    $wrappers = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-NativeEvidenceUnixFileType'
    }, $true))
    Require ($wrappers.Count -eq 1) 'Expected one exact native/fallback dispatcher.'
    $wrapper = $wrappers[0].Extent.Text
    foreach ($replacement in @(
        @('function Get-NativeEvidenceUnixFileType(', 'function Get-FixtureUnixFileType('),
        @('Get-NativeEvidenceStatxFileType $Path', 'Get-FixtureStatxFileType $Path'),
        @('Get-NativeEvidenceUnixFileTypeViaStat $Path', 'Invoke-FixtureStat $Path')
    )) {
        Require ($wrapper.Split([string]$replacement[0], [StringSplitOptions]::None).Length -eq 2) 'Exact dispatcher seam cardinality differs.'
        $wrapper = $wrapper.Replace([string]$replacement[0], [string]$replacement[1])
    }
    . ([scriptblock]::Create($wrapper))

    # Keep the real bounded process/pipe/exit/kill implementation. Substitute
    # only command discovery and argv to run an owned portable fake stat child.
    $fallbacks = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-NativeEvidenceUnixFileTypeViaStat'
    }, $true))
    Require ($fallbacks.Count -eq 1) 'Expected one exact bounded stat fallback.'
    $fallbackSource = $fallbacks[0].Extent.Text
    foreach ($replacement in @(
        @('function Get-NativeEvidenceUnixFileTypeViaStat(', 'function Get-FixtureUnixFileTypeViaProcess('),
        @('Get-Command -Name ''stat'' -CommandType Application -ErrorAction Stop', 'Get-FixtureStatCommand'),
        @('@(''--printf=%f'', ''--'', $Path)', '@(''-NoProfile'', ''-File'', $script:fakeStatScript, $script:statScenario, $script:statMarker)')
    )) {
        Require ($fallbackSource.Split([string]$replacement[0], [StringSplitOptions]::None).Length -eq 2) 'Exact stat command seam cardinality differs.'
        $fallbackSource = $fallbackSource.Replace([string]$replacement[0], [string]$replacement[1])
    }
    . ([scriptblock]::Create($fallbackSource))
    $statDirectory = New-FixtureDirectory 'stat-process'
    $script:fakeStatScript = Join-Path $statDirectory 'owned-stat.ps1'
    [IO.File]::WriteAllText($fakeStatScript, @'
param([string] $Scenario, [string] $Marker)
$ErrorActionPreference = 'Stop'
$identity = @{ pid = $PID; startTimeUtcTicks = (Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks }
[IO.File]::WriteAllText($Marker, ($identity | ConvertTo-Json -Compress))
switch ($Scenario) {
    'success' { [Console]::Out.Write('81a4') }
    'nonzero' { [Console]::Out.Write('81a4'); exit 7 }
    'malformed' { [Console]::Out.Write('not-hex') }
    'unknown-mode' { [Console]::Out.Write('3000') }
    'timeout' { [Threading.Thread]::Sleep(30000) }
    default { throw 'Unknown owned stat scenario.' }
}
'@, $utf8)

    Case 'real Linux ABI buffer layout and libc declaration' {
        $size = [Runtime.InteropServices.Marshal].GetMethod('SizeOf', [type[]]@([type])).Invoke($null, [object[]]@($bufferType))
        Require ($size -eq 256) 'statx buffer must include all 256 writable bytes.'
        Require ([Runtime.InteropServices.Marshal]::OffsetOf($bufferType, 'Mask').ToInt32() -eq 0) 'statx mask offset differs.'
        Require ([Runtime.InteropServices.Marshal]::OffsetOf($bufferType, 'Mode').ToInt32() -eq 28) 'statx mode offset differs.'
        Require ($bufferType.GetField('Mask').FieldType -eq [uint32] -and $bufferType.GetField('Mode').FieldType -eq [uint16]) 'statx scalar widths differ.'
        $import = $nativeMethod.GetCustomAttributes([Runtime.InteropServices.DllImportAttribute], $false)[0]
        Require ($import.Value -ceq 'libc' -and $import.EntryPoint -ceq 'statx' -and $import.SetLastError -and
            $import.CallingConvention -eq [Runtime.InteropServices.CallingConvention]::Cdecl) 'statx calling convention or error capture differs.'
    }
    Case 'native success uses exact UTF8 NUL terminated path and no-follow flags without fallback' {
        Require ((Get-FixtureUnixFileType $unicode) -eq 0x8000) 'Native regular type was not returned.'
        $expected = $utf8.GetBytes($unicode + [char]0)
        $observed = [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::LastPath
        Require ([Convert]::ToHexString($observed) -ceq [Convert]::ToHexString($expected)) 'Native path was not exact UTF8 plus one terminator.'
        Require ([ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::LastDirectory -eq -100 -and
            [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::LastFlags -eq (0x100 -bor 0x800) -and
            [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::LastMask -eq 1) 'statx must request TYPE using AT_FDCWD, NOFOLLOW and NO_AUTOMOUNT.'
        Require ($fallbackCalls -eq 0) 'Native success launched fallback inspection.'
    }
    foreach ($mode in @(0x1000, 0x2000, 0x4000, 0x6000, 0x8000, 0xA000, 0xC000)) {
        Case ('recognized native type ' + $mode.ToString('X4')) {
            [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::ReturnedMode = [uint16]($mode -bor 0x1A4)
            Require ((Get-FixtureUnixFileType $regular) -eq $mode -and $fallbackCalls -eq 0) 'Native file type was replaced or caused fallback.'
        }
    }
    foreach ($errno in @(1, 38, 95)) {
        Case ('permitted unavailable or denied syscall fallback errno ' + $errno) {
            [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Result = -1
            [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Error = $errno
            $script:fallbackType = 0x1000
            Require ((Get-FixtureUnixFileType $regular) -eq 0x1000 -and $fallbackCalls -eq 1) 'Unavailable syscall did not use independent fallback result.'
        }
    }
    foreach ($exception in @([DllNotFoundException]::new(), [EntryPointNotFoundException]::new(),
        [BadImageFormatException]::new(), [PlatformNotSupportedException]::new())) {
        Case ('interop unavailable fallback ' + $exception.GetType().Name) {
            [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Failure = $exception
            Require ((Get-FixtureUnixFileType $regular) -eq 0x8000 -and $fallbackCalls -eq 1) 'Unavailable interop did not require fallback.'
        }
    }
    foreach ($errno in @(2, 5, 13, 22)) {
        Case ('native metadata error fails closed errno ' + $errno) {
            [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Result = -1
            [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Error = $errno
            Require-Rejection { Get-FixtureUnixFileType $regular }
            Require ($fallbackCalls -eq 0) 'A metadata error was converted to fallback.'
        }
    }
    foreach ($mask in @(0, 2)) {
        Case ('successful reply missing TYPE mask ' + $mask) {
            [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::ReturnedMask = $mask
            Require-Rejection { Get-FixtureUnixFileType $regular }
            Require ($fallbackCalls -eq 0) 'Missing TYPE metadata was hidden by fallback.'
        }
    }
    foreach ($mode in @(0, 0x3000)) {
        Case ('successful reply invalid mode ' + $mode) {
            [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::ReturnedMode = $mode
            Require-Rejection { Get-FixtureUnixFileType $regular }
            Require ($fallbackCalls -eq 0) 'Invalid file mode was hidden by fallback.'
        }
    }
    Case 'unexpected positive native status fails closed' {
        [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Result = 1
        [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Error = 38
        Require-Rejection { Get-FixtureUnixFileType $regular }
        Require ($fallbackCalls -eq 0) 'Unexpected status was treated as an unavailable syscall.'
    }
    Case 'unsupported OS falls back without calling statx' {
        [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::FixtureIsLinux = $false
        Require ((Get-FixtureUnixFileType $regular) -eq 0x8000 -and $fallbackCalls -eq 1 -and
            [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Calls -eq 0) 'Unsupported OS entered native ABI.'
    }
    Case 'unsupported architecture falls back without calling statx' {
        [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::FixtureArchitecture = [Runtime.InteropServices.Architecture]::X86
        Require ((Get-FixtureUnixFileType $regular) -eq 0x8000 -and $fallbackCalls -eq 1 -and
            [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Calls -eq 0) 'Unsupported architecture entered native ABI.'
    }
    Case 'supported Arm64 follows the native ABI path' {
        [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::FixtureArchitecture = [Runtime.InteropServices.Architecture]::Arm64
        Require ((Get-FixtureUnixFileType $regular) -eq 0x8000 -and $fallbackCalls -eq 0 -and
            [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Calls -eq 1) 'Arm64 did not enter native ABI.'
    }
    Case 'embedded NUL path is rejected before syscall' {
        Require-Rejection { Get-FixtureUnixFileType ($regular + [char]0 + 'suffix') }
        Require ($fallbackCalls -eq 0 -and [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Calls -eq 0) 'NUL path reached filesystem inspection.'
    }
    Case 'invalid UTF16 path is rejected before syscall' {
        Require-Rejection { Get-FixtureUnixFileType ($regular + [char]0xD800) }
        Require ($fallbackCalls -eq 0 -and [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Calls -eq 0) 'Invalid UTF16 was replaced or reached filesystem inspection.'
    }
    Case 'fallback failure cannot manufacture a regular-file result' {
        [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Result = -1
        [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Error = 38
        $script:fallbackFailure = $true
        Require-Rejection { Get-FixtureUnixFileType $regular }
        Require ($fallbackCalls -eq 1) 'Independent fallback was not attempted exactly once.'
    }
    foreach ($scenario in @('success', 'nonzero', 'malformed', 'unknown-mode', 'timeout', 'missing-tool')) {
        Case ('allowed syscall fallback uses actual bounded stat process: ' + $scenario) {
            [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Result = -1
            [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Error = 38
            $script:statScenario = $scenario
            $script:statMarker = Join-Path $statDirectory ($scenario + '.pid')
            $script:statMissing = $scenario -ceq 'missing-tool'
            $script:fallbackDriver = { param($Path) Get-FixtureUnixFileTypeViaProcess $Path }
            $elapsed = [Diagnostics.Stopwatch]::StartNew()
            $actionFailure = $null
            $childFailure = $null
            try {
                if ($scenario -ceq 'success') {
                    Require ((Get-FixtureUnixFileType $regular) -eq 0x8000) 'Independent successful stat did not validate the regular file.'
                }
                else { Require-Rejection { Get-FixtureUnixFileType $regular } }
            }
            catch { $actionFailure = $_ }
            finally {
                $elapsed.Stop()
                if ([IO.File]::Exists($statMarker)) {
                    try { Assert-StatChildExited $statMarker }
                    catch { $childFailure = $_ }
                }
            }
            if ($null -ne $actionFailure -and $null -ne $childFailure) {
                throw [AggregateException]::new('Stat fallback proof and owned child cleanup failed.', [Exception[]]@($actionFailure.Exception, $childFailure.Exception))
            }
            if ($null -ne $actionFailure) { throw $actionFailure }
            if ($null -ne $childFailure) { throw $childFailure }
            if ($scenario -ceq 'timeout') {
                Require ($elapsed.Elapsed -lt [TimeSpan]::FromSeconds(20)) 'Fallback waited for the full mock sleep instead of enforcing its deadline.'
            }
            Require ($fallbackCalls -eq 1 -and $statDiscoveryCalls -eq 1) 'Allowed native fallback did not independently discover stat exactly once.'
            if ($statMissing) { Require (-not [IO.File]::Exists($statMarker)) 'Unavailable stat unexpectedly launched a process.' }
            else {
                Require ([IO.File]::Exists($statMarker)) 'The owned stat process was not actually invoked.'
            }
        }
    }
    Case 'native file types are never cached across calls' {
        Require ((Get-FixtureUnixFileType $regular) -eq 0x8000) 'Initial source-derived lookup failed.'
        [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::ReturnedMode = 0x1000
        Require ((Get-FixtureUnixFileType $regular) -eq 0x1000 -and
            [ConduitLLM.NativeEvidenceFileTypeFixture.LinuxFileType]::Calls -eq 2) 'A stale regular-file result was reused.'
    }
    Case 'real UTF8 regular file hashes unchanged' {
        $hash = Get-NativeEvidenceFileHash ([IO.FileInfo]::new($unicode))
        $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($unicode))).ToLowerInvariant()
        Require ($hash.sha256 -ceq $expected) 'Unicode regular file contents or identity changed.'
    }

    if ($IsLinux) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
namespace ConduitLLM.NativeEvidenceFileTypeFixture
{
    public static class Posix
    {
        [DllImport("libc", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, uint mode);
    }
}
'@ | Out-Null
        $script:hashChild = Join-Path $temporaryRoot 'guard-child.ps1'
        [IO.File]::WriteAllText($hashChild, @'
param([string] $Helper, [string] $Path, [switch] $ReplaceWithFifo)
$ErrorActionPreference = 'Stop'
. $Helper
$file = [IO.FileInfo]::new($Path)
if ($ReplaceWithFifo) {
    Get-NativeEvidenceFileHash $file | Out-Null
    Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public static class FifoFixture { [DllImport("libc", SetLastError=true)] public static extern int mkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode); }'
    [IO.File]::Delete($Path)
    if ([FifoFixture]::mkfifo($Path, 384) -ne 0) { throw 'Owned FIFO mutation setup failed.' }
}
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($Helper, [ref]$tokens, [ref]$errors)
$hashFunctions = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Get-NativeEvidenceFileHash' }, $true))
if (@($errors).Count -ne 0 -or $hashFunctions.Count -ne 1) { throw 'Expected exact production hash source.' }
$opens = @($hashFunctions[0].Body.FindAll({ param($n)
    $n -is [Management.Automation.Language.InvokeMemberExpressionAst] -and $n.Static -and
    $n.Expression -is [Management.Automation.Language.TypeExpressionAst] -and
    $n.Expression.TypeName.FullName -cin @('IO.FileStream', 'System.IO.FileStream') -and $n.Member.Value -ceq 'new'
}, $true))
if ($opens.Count -ne 1) { throw 'Expected exactly one production stream-open seam.' }
$hashSource = $hashFunctions[0].Extent.Text.Replace('function Get-NativeEvidenceFileHash(', 'function Get-FixtureEvidenceFileHash(')
if ($hashSource.Split($opens[0].Extent.Text, [StringSplitOptions]::None).Length -ne 2) { throw 'Exact stream-open span differs.' }
$hashSource = $hashSource.Replace($opens[0].Extent.Text, '(Open-FixtureStream)')
function Open-FixtureStream { throw 'OPEN_REACHED' }
. ([scriptblock]::Create($hashSource))
# The source-derived stream seam proves rejection precedes the open. Then run
# the actual unmodified hash, bounded by its parent's owned process deadline.
foreach ($action in @({ Get-FixtureEvidenceFileHash $file }, { Get-NativeEvidenceFileHash $file })) {
    $rejected = $false
    try { & $action | Out-Null }
    catch {
        if ($_.Exception.Message -cnotin @('Native publish evidence requires a regular file.', 'Native publish evidence rejects linked paths.')) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'A special or linked path reached stream opening.' }
}
[Console]::Out.WriteLine('REJECTED_BEFORE_OPEN')
'@, $utf8)
        Case 'real statx and stat fallback preserve regular UTF8 file and directory types' {
            foreach ($path in @($regular, $unicode)) {
                Require ((Get-NativeEvidenceStatxFileType $path) -eq 0x8000) 'Live native UTF8 regular type differs.'
                Require ((Get-NativeEvidenceUnixFileTypeViaStat $path) -eq 0x8000) 'Live independent fallback regular type differs.'
            }
            Require ((Get-NativeEvidenceStatxFileType $temporaryRoot) -eq 0x4000) 'Live native directory type differs.'
        }
        $fifo = Join-Path $temporaryRoot 'fifo'
        Require ([ConduitLLM.NativeEvidenceFileTypeFixture.Posix]::mkfifo($fifo, 384) -eq 0) 'Owned FIFO setup failed.'
        $socketPath = Join-Path $temporaryRoot 's'
        $socket = [Net.Sockets.Socket]::new([Net.Sockets.AddressFamily]::Unix, [Net.Sockets.SocketType]::Stream, [Net.Sockets.ProtocolType]::Unspecified)
        $ownedSockets.Add($socket)
        $socket.Bind([Net.Sockets.UnixDomainSocketEndPoint]::new($socketPath))
        $symlink = Join-Path $temporaryRoot 'linked-file'
        [IO.File]::CreateSymbolicLink($symlink, $regular) | Out-Null
        $ownedLinks.Add($symlink)
        foreach ($special in @(
            @{ name = 'FIFO'; path = $fifo; type = 0x1000 },
            @{ name = 'socket'; path = $socketPath; type = 0xC000 },
            @{ name = 'character device'; path = '/dev/null'; type = 0x2000 },
            @{ name = 'symlink itself'; path = $symlink; type = 0xA000 }
        )) {
            Case ('live ' + $special.name + ' is typed without following or opening it') {
                Require ((Get-NativeEvidenceStatxFileType $special.path) -eq $special.type) 'Live no-follow native type differs.'
                Require ((Get-NativeEvidenceUnixFileTypeViaStat $special.path) -eq $special.type) 'Live fallback type differs.'
                Invoke-BoundedHashRejection $special.path
            }
        }
        Case 'live linked ancestor is rejected before hashing its regular child' {
            $target = New-FixtureDirectory 'target'
            [IO.File]::WriteAllText((Join-Path $target 'child'), 'target sentinel', $utf8)
            $link = Join-Path $temporaryRoot 'linked-directory'
            [IO.Directory]::CreateSymbolicLink($link, $target) | Out-Null
            $ownedLinks.Add($link)
            Invoke-BoundedHashRejection (Join-Path $link 'child')
            Require ([IO.File]::ReadAllText((Join-Path $target 'child')) -ceq 'target sentinel') 'Linked target was changed.'
        }
        Case 'live regular file replaced by FIFO is rechecked without cached acceptance' {
            $late = Join-Path $temporaryRoot 'late-file'
            [IO.File]::WriteAllText($late, 'initial regular file', $utf8)
            Invoke-BoundedHashRejection $late -ReplaceWithFifo
            Require ((Get-NativeEvidenceUnixFileType $late) -eq 0x1000) 'Late FIFO mutation was not observed.'
        }
        Case 'live ancestor replaced by symlink is rechecked without cached acceptance' {
            $directory = New-FixtureDirectory 'late-directory'
            $file = [IO.FileInfo]::new((Join-Path $directory 'child'))
            [IO.File]::WriteAllText($file.FullName, 'original regular file', $utf8)
            Get-NativeEvidenceFileHash $file | Out-Null
            $moved = Join-Path $temporaryRoot 'retained-directory'
            [IO.Directory]::Move($directory, $moved)
            [IO.Directory]::CreateSymbolicLink($directory, $moved) | Out-Null
            $ownedLinks.Add($directory)
            Require-Rejection { Get-NativeEvidenceFileHash $file } 'rejects linked paths'
            Require ([IO.File]::ReadAllText((Join-Path $moved 'child')) -ceq 'original regular file') 'Late linked target was changed.'
        }

        # Informational only: identical live metadata observations, no speed
        # threshold. This emits hosted-runner evidence when CI runs the contract.
        try {
            $calls = 500
            Get-NativeEvidenceUnixFileType $regular | Out-Null
            Get-NativeEvidenceUnixFileTypeViaStat $regular | Out-Null
            $native = [Diagnostics.Stopwatch]::StartNew()
            for ($index = 0; $index -lt $calls; $index++) {
                Require ((Get-NativeEvidenceUnixFileType $regular) -eq 0x8000) 'Timed native lookup differed.'
            }
            $native.Stop()
            $fallback = [Diagnostics.Stopwatch]::StartNew()
            for ($index = 0; $index -lt $calls; $index++) {
                Require ((Get-NativeEvidenceUnixFileTypeViaStat $regular) -eq 0x8000) 'Timed stat lookup differed.'
            }
            $fallback.Stop()
            $timing = [ordered]@{ status = 'measured'; calls = $calls; initializationMilliseconds = $initializationMilliseconds;
                nativeMilliseconds = $native.Elapsed.TotalMilliseconds; statMilliseconds = $fallback.Elapsed.TotalMilliseconds;
                runtime = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription;
                powerShellVersion = $PSVersionTable.PSVersion.ToString();
                osDescription = [Runtime.InteropServices.RuntimeInformation]::OSDescription;
                processArchitecture = [string][Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture }
        }
        catch { $timing = [ordered]@{ status = 'unavailable'; exceptionType = $_.Exception.GetType().FullName } }
        Write-Host ('NATIVE_FILE_TYPE_TIMING ' + ($timing | ConvertTo-Json -Compress))
    }
    else { Write-Host 'SKIP: live Linux syscall and special-file checks; source-derived ABI/error/fallback contracts still ran.' }

    $failed = @($rows | Where-Object { -not $_.passed })
    if ($failed.Count -gt 0) {
        foreach ($row in $failed) { Write-Error ($row.name + ': ' + $row.message) -ErrorAction Continue }
        throw ('File-type contracts failed: ' + ($rows.Count - $failed.Count) + '/' + $rows.Count + '.')
    }
    Write-Host ('PASS: ' + $rows.Count + ' file-type contracts; syscall/platform seams use source-derived validation, live cases use actual paths.')
}
catch { $proofFailure = $_ }
finally {
    $cleanupErrors = [Collections.Generic.List[Exception]]::new()
    foreach ($socket in $ownedSockets) {
        try { $socket.Dispose() }
        catch { $cleanupErrors.Add($_.Exception) }
    }
    foreach ($link in $ownedLinks) {
        try {
            if ([IO.File]::GetAttributes($link) -band [IO.FileAttributes]::Directory) { [IO.Directory]::Delete($link) }
            else { [IO.File]::Delete($link) }
        }
        catch { $cleanupErrors.Add($_.Exception) }
    }
    try {
        $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        Require ($temporaryRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($temporaryRoot) -cmatch '^conduit-file-types-[0-9a-f]{32}$') 'Refusing cleanup outside the exact owned temporary root.'
        if ([IO.Directory]::Exists($temporaryRoot)) { [IO.Directory]::Delete($temporaryRoot, $true) }
    }
    catch { $cleanupErrors.Add($_.Exception) }
    if ($cleanupErrors.Count -gt 0) { $cleanupFailure = [AggregateException]::new('Owned file-type fixture cleanup failed.', $cleanupErrors.ToArray()) }
}
if ($null -ne $proofFailure -and $null -ne $cleanupFailure) {
    throw [AggregateException]::new('File-type contract work and owned cleanup failed.', [Exception[]]@($proofFailure.Exception, $cleanupFailure))
}
if ($null -ne $proofFailure) { throw $proofFailure }
if ($null -ne $cleanupFailure) { throw $cleanupFailure }
