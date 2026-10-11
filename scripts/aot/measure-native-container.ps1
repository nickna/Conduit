[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('admin', 'gateway')][string] $Service,
    [Parameter(Mandatory)][string] $JitImage,
    [Parameter(Mandatory)][string] $NativeImage,
    [Parameter(Mandatory)][string] $DatabaseUrl,
    [Parameter(Mandatory)][string] $RedisUrl,
    [Parameter(Mandatory)][string[]] $WorkloadConfigurationPaths,
    [Parameter(Mandatory)][string] $WorkloadProbePath,
    [Parameter(Mandatory)][string] $EnvironmentEvidencePath,
    [string] $ContainerEnvironmentPath,
    [ValidateRange(5, 21)][int] $PairedRuns = 5,
    [int] $Requests,
    [int] $Concurrency,
    [string] $NativeBaselinePath,
    [string] $NativePackagingDirectory,
    [string] $OutputPath,
    [ValidateRange(1024, 65534)][int] $Port = 18080,
    [string] $DockerCommandPath = 'docker',
    [string] $DotnetCommandPath = 'dotnet',
    [ValidateRange(1, 60)][int] $DockerCommandTimeoutSeconds = 30,
    [ValidateRange(1, 900)][int] $WorkloadTimeoutSeconds = 900,
    [ValidateRange(1, 300)][int] $ReadinessTimeoutSeconds = 120,
    [ValidateRange(1, 30)][int] $CleanupTimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'
$Service = $Service.ToLowerInvariant()
. (Join-Path $PSScriptRoot 'native-benchmark-image-evidence.ps1')
. (Join-Path $PSScriptRoot 'native-benchmark-attempt-evidence.ps1')
$privateRoot = $null
$collectionFailure = $null
$attempt = $null
$privateCleanupFailed = $false

function Get-SafeFailureMessage([Exception] $Exception, [string] $Fallback) {
    $allowed = @(
        'Private command failed.', 'Private command failed and its cleanup failed.', 'Private command cleanup failed.',
        'Benchmark variant failed.', 'Benchmark variant failed and owned container cleanup failed.',
        'Owned benchmark container cleanup failed.', 'Container ownership verification failed.',
        'Native benchmark image binding failed.', 'Native benchmark container image binding failed.',
        'Native benchmark image changed during collection.'
    )
    if ($Exception.Message -cin $allowed) { return $Exception.Message }
    return $Fallback
}

# Commands receive individual arguments and a private child environment. Their
# captured output is never printed by this collector, including on failure.
function Invoke-BoundedProcess(
    [string] $Command, [string[]] $Arguments, [int] $TimeoutSeconds,
    [System.Collections.IDictionary] $ChildEnvironment = @{}, [scriptblock] $PollAction
) {
    $process = [System.Diagnostics.Process]::new()
    $deadline = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TimeoutSeconds))
    $streams = @()
    $started = $false
    $failed = $false
    $cleanupFailed = $false
    $result = $null
    try {
        $start = [System.Diagnostics.ProcessStartInfo]::new()
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        if ([System.IO.Path]::GetExtension($Command) -ieq '.ps1') {
            $start.FileName = 'pwsh'
            foreach ($argument in @('-NoProfile', '-File', [System.IO.Path]::GetFullPath($Command))) {
                $start.ArgumentList.Add($argument)
            }
        }
        else { $start.FileName = $Command }
        foreach ($argument in $Arguments) { $start.ArgumentList.Add([string]$argument) }
        # Inherited benchmark credentials must not reach Docker or other commands.
        $start.Environment.Remove('CONDUIT_NATIVE_BENCHMARK_API_KEY') | Out-Null
        foreach ($name in $ChildEnvironment.Keys) {
            $start.Environment[[string]$name] = [string]$ChildEnvironment[$name]
        }
        $process.StartInfo = $start
        if (-not $process.Start()) { throw 'Private command failed.' }
        $started = $true
        foreach ($reader in @($process.StandardOutput, $process.StandardError)) {
            $streams += [pscustomobject]@{
                Reader = $reader; Buffer = [char[]]::new(4096); Text = [System.Text.StringBuilder]::new()
                Pending = $null; Ended = $false
            }
        }
        $sampleTimer = [System.Diagnostics.Stopwatch]::StartNew()
        $runTimer = [System.Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            $deadline.Token.ThrowIfCancellationRequested()
            foreach ($stream in $streams) {
                if ($stream.Ended) { continue }
                # Drain a bounded batch of ready chunks before yielding to the
                # other stream and process/memory checks. Small pipe reads must
                # not turn the output cap into a sleep-driven timeout.
                for ($chunk = 0; $chunk -lt 64; $chunk++) {
                    $deadline.Token.ThrowIfCancellationRequested()
                    if ($null -eq $stream.Pending) {
                        $stream.Pending = $stream.Reader.ReadAsync([System.Memory[char]]::new($stream.Buffer), $deadline.Token).AsTask()
                    }
                    if (-not $stream.Pending.IsCompleted) { break }
                    $length = $stream.Pending.GetAwaiter().GetResult()
                    $stream.Pending = $null
                    if ($length -eq 0) { $stream.Ended = $true; break }
                    if ($stream.Text.Length + $length -gt 4194304) { throw 'Private command output exceeded its limit.' }
                    $stream.Text.Append($stream.Buffer, 0, $length) | Out-Null
                }
            }
            $process.Refresh()
            if ($process.HasExited -and $streams[0].Ended -and $streams[1].Ended) { break }
            if (-not $process.HasExited -and $null -ne $PollAction -and $sampleTimer.ElapsedMilliseconds -ge 200) {
                $remaining = $TimeoutSeconds - $runTimer.Elapsed.TotalSeconds
                # Poll commands receive the remaining budget and have their own
                # bounded disposal; never start a sample with less than one second.
                if ($remaining -ge 1) { & $PollAction ([int][Math]::Floor($remaining)) | Out-Null }
                $sampleTimer.Restart()
            }
            Start-Sleep -Milliseconds 10
        }
        $deadline.Token.ThrowIfCancellationRequested()
        $result = [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $streams[0].Text.ToString(); Stderr = $streams[1].Text.ToString() }
    }
    catch { $failed = $true }
    finally {
        $reap = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(5))
        try {
            if ($started) {
                if (-not $process.HasExited) { $process.Kill($true) }
                $process.WaitForExitAsync($reap.Token).GetAwaiter().GetResult() | Out-Null
            }
        }
        catch { $cleanupFailed = $true }
        finally {
            if ($started) {
                try { $process.StandardOutput.Dispose(); $process.StandardError.Dispose() }
                catch { $cleanupFailed = $true }
            }
            foreach ($task in @($streams | ForEach-Object { $_.Pending })) {
                if ($null -eq $task) { continue }
                try { $task.WaitAsync($reap.Token).GetAwaiter().GetResult() | Out-Null }
                catch {
                    # Cancellation/reader disposal is expected after a command timeout.
                    if (-not $failed -or -not $task.IsCompleted) { $cleanupFailed = $true }
                }
            }
            $process.Dispose()
            $deadline.Dispose()
            $reap.Dispose()
        }
    }
    if ($failed -and $cleanupFailed) { throw 'Private command failed and its cleanup failed.' }
    if ($failed) { throw 'Private command failed.' }
    if ($cleanupFailed) { throw 'Private command cleanup failed.' }
    return $result
}

function Invoke-Docker([string[]] $Arguments, [int] $TimeoutSeconds = $DockerCommandTimeoutSeconds) {
    return Invoke-BoundedProcess $DockerCommandPath $Arguments $TimeoutSeconds
}

function Read-JsonDocument([string] $Path) {
    $stream = $null
    try {
        $stream = [System.IO.File]::OpenRead([System.IO.Path]::GetFullPath($Path))
        if ($stream.Length -lt 1 -or $stream.Length -gt 65536) { throw 'Invalid private configuration.' }
        $bytes = [byte[]]::new([int]$stream.Length)
        $stream.ReadExactly($bytes, 0, $bytes.Length)
        $options = [System.Text.Json.JsonDocumentOptions]::new()
        $options.MaxDepth = 16
        return [System.Text.Json.JsonDocument]::Parse([System.ReadOnlyMemory[byte]]::new($bytes), $options)
    }
    catch { throw 'Invalid private configuration.' }
    finally { if ($null -ne $stream) { $stream.Dispose() } }
}

function Get-JsonObjectMap([System.Text.Json.JsonElement] $Element, [string[]] $Names) {
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'Invalid private configuration.' }
    $map = [System.Collections.Generic.Dictionary[string, System.Text.Json.JsonElement]]::new([StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if (-not $map.TryAdd($property.Name, $property.Value)) { throw 'Invalid private configuration.' }
    }
    if ($map.Count -ne $Names.Count) { throw 'Invalid private configuration.' }
    foreach ($name in $Names) { if (-not $map.ContainsKey($name)) { throw 'Invalid private configuration.' } }
    return ,$map
}

function Read-JsonText($Map, [string] $Name, [switch] $AllowEmpty) {
    $value = $Map[$Name]
    if ($value.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { throw 'Invalid private configuration.' }
    $text = $value.GetString()
    if ($text.Length -gt 16384 -or (-not $AllowEmpty -and [string]::IsNullOrWhiteSpace($text))) {
        throw 'Invalid private configuration.'
    }
    return $text
}

function Read-JsonInteger($Map, [string] $Name, [long] $Minimum, [long] $Maximum) {
    $value = 0L
    if ($Map[$Name].ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        -not $Map[$Name].TryGetInt64([ref]$value) -or $value -lt $Minimum -or $value -gt $Maximum) {
        throw 'Invalid private configuration.'
    }
    return $value
}

function Read-WorkloadConfiguration([string] $Path) {
    $document = Read-JsonDocument $Path
    try {
        $root = $document.RootElement
        $kind = $root.GetProperty('workload')
        if ($kind.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { throw 'Invalid private configuration.' }
        $id = $kind.GetString()
        $common = @('workload', 'baseUrl', 'requests', 'concurrency', 'warmupRequests', 'timeoutSeconds')
        $extra = switch -CaseSensitive ($id) {
            'admin-setting-read' { @('settingKey', 'expectedValue') }
            'gateway-chat-nonstream' { @('model', 'prompt', 'expectedContent', 'expectedPromptTokens', 'expectedCompletionTokens') }
            'gateway-chat-sse' { @('model', 'prompt', 'expectedContent', 'expectedPromptTokens', 'expectedCompletionTokens') }
            default { throw 'Invalid private configuration.' }
        }
        $map = Get-JsonObjectMap $root ($common + $extra)
        $data = [ordered]@{
            workload = $id
            baseUrl = Read-JsonText $map 'baseUrl'
            requests = Read-JsonInteger $map 'requests' 1 100000
            concurrency = Read-JsonInteger $map 'concurrency' 1 256
            warmupRequests = Read-JsonInteger $map 'warmupRequests' 0 10000
            timeoutSeconds = Read-JsonInteger $map 'timeoutSeconds' 1 120
        }
        if ($data.concurrency -gt $data.requests) { throw 'Invalid private configuration.' }
        $uri = $null
        if (-not [Uri]::TryCreate($data.baseUrl, [UriKind]::Absolute, [ref]$uri) -or
            $uri.Scheme -cnotin @('http', 'https') -or $uri.AbsolutePath -ne '/' -or
            $uri.UserInfo.Length -ne 0 -or $uri.Query.Length -ne 0 -or $uri.Fragment.Length -ne 0) {
            throw 'Invalid private configuration.'
        }
        foreach ($name in $extra) {
            $data[$name] = if ($name -in @('expectedPromptTokens', 'expectedCompletionTokens')) {
                Read-JsonInteger $map $name 0 1000000000
            }
            else { Read-JsonText $map $name -AllowEmpty:($name -eq 'expectedValue') }
        }
        return [pscustomobject]@{ Id = $id; Data = $data }
    }
    catch { throw 'Invalid private configuration.' }
    finally { $document.Dispose() }
}

function Read-EnvironmentEvidence([string] $Path) {
    $document = Read-JsonDocument $Path
    try {
        $map = Get-JsonObjectMap $document.RootElement @('schemaVersion', 'environmentId', 'host', 'backingServices')
        if ((Read-JsonInteger $map 'schemaVersion' 1 1) -ne 1) { throw 'Invalid environment evidence.' }
        $hostMap = Get-JsonObjectMap $map['host'] @('os', 'architecture', 'cpu', 'logicalProcessors', 'memoryBytes', 'dockerVersion', 'kernelVersion')
        $serviceMap = Get-JsonObjectMap $map['backingServices'] @('postgresqlVersion', 'redisVersion', 'wolverineVersion', 'configurationId', 'providerFixtureId')
        $hostData = [ordered]@{}
        foreach ($name in @('os', 'architecture', 'cpu', 'dockerVersion', 'kernelVersion')) {
            $hostData[$name] = Read-JsonText $hostMap $name
        }
        $hostData.logicalProcessors = Read-JsonInteger $hostMap 'logicalProcessors' 1 2147483647
        $hostData.memoryBytes = Read-JsonInteger $hostMap 'memoryBytes' 1 ([long]::MaxValue)
        $services = [ordered]@{}
        foreach ($name in $serviceMap.Keys) { $services[$name] = Read-JsonText $serviceMap $name }
        $identifier = Read-JsonText $map 'environmentId'
        if ($hostData.os -cne 'linux' -or $hostData.architecture -cne 'x64' -or
            ($Service -eq 'gateway' -and $services.providerFixtureId -ceq 'not-applicable')) {
            throw 'Invalid environment evidence.'
        }
        foreach ($value in @($identifier) + @($hostData.Values) + @($services.Values)) {
            if ($value -is [string] -and ($value.Contains('://') -or $value -match '[\r\n\x00]')) {
                throw 'Invalid environment evidence.'
            }
        }
        return [ordered]@{ schemaVersion = 1; environmentId = $identifier; host = $hostData; backingServices = $services }
    }
    catch { throw 'Invalid environment evidence.' }
    finally { $document.Dispose() }
}

function Protect-PrivatePath([string] $Path, [switch] $Directory) {
    if ($IsWindows) {
        $owner = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl = Get-Acl -LiteralPath $Path
        $acl.SetOwner($owner)
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($rule in @($acl.Access)) { $acl.RemoveAccessRuleSpecific($rule) }
        $inheritance = if ($Directory) {
            [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
        }
        else { [System.Security.AccessControl.InheritanceFlags]::None }
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            $owner, [System.Security.AccessControl.FileSystemRights]::FullControl, $inheritance,
            [System.Security.AccessControl.PropagationFlags]::None, [System.Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
        Set-Acl -LiteralPath $Path -AclObject $acl
    }
    else {
        $mode = [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite
        if ($Directory) { $mode = $mode -bor [System.IO.UnixFileMode]::UserExecute }
        [System.IO.File]::SetUnixFileMode($Path, $mode)
    }
}

function ConvertTo-ContainerUrl([string] $Value, [string[]] $Schemes) {
    try {
        $uri = $null
        if ([string]::IsNullOrWhiteSpace($Value) -or $Value -match '[\r\n\x00]' -or
            -not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
            $uri.Scheme -cnotin $Schemes -or [string]::IsNullOrEmpty($uri.Host)) {
            throw 'Invalid backing-service configuration.'
        }
        if (-not $uri.IsLoopback) { return $uri.AbsoluteUri }

        # UriBuilder cannot round-trip a Redis password with an empty username.
        # Build only the replacement authority from parsed, escaped components.
        $userInfo = $uri.GetComponents([UriComponents]::UserInfo, [UriFormat]::UriEscaped)
        $authority = if ($userInfo.Length -gt 0) { $userInfo + '@host.docker.internal' }
            else { 'host.docker.internal' }
        if ($uri.Port -ge 0) { $authority += ':' + $uri.Port.ToString([Globalization.CultureInfo]::InvariantCulture) }
        $rewritten = $uri.Scheme + '://' + $authority + $uri.AbsolutePath + $uri.Query + $uri.Fragment
        $result = $null
        if (-not [Uri]::TryCreate($rewritten, [UriKind]::Absolute, [ref]$result) -or
            $result.Host -cne 'host.docker.internal' -or $result.Scheme -cne $uri.Scheme -or
            $result.Port -ne $uri.Port -or $result.AbsolutePath -cne $uri.AbsolutePath -or
            $result.Query -cne $uri.Query -or $result.Fragment -cne $uri.Fragment -or
            $result.GetComponents([UriComponents]::UserInfo, [UriFormat]::UriEscaped) -cne $userInfo) {
            throw 'Invalid backing-service configuration.'
        }
        return $result.AbsoluteUri
    }
    catch { throw 'Invalid backing-service configuration.' }
}

function Read-ContainerEnvironment([string] $Path) {
    $values = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    if ([string]::IsNullOrWhiteSpace($Path)) { return ,$values }
    $stream = $null
    try {
        $stream = [System.IO.File]::OpenRead([System.IO.Path]::GetFullPath($Path))
        if ($stream.Length -gt 65536) { throw 'Invalid private container environment.' }
        $bytes = [byte[]]::new([int]$stream.Length)
        $stream.ReadExactly($bytes, 0, $bytes.Length)
        $text = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes).TrimStart([char]0xfeff)
        foreach ($line in ($text -split '\r?\n')) {
            if ($line -match '[\x00-\x1f\x7f]') { throw 'Invalid private container environment.' }
            if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#', [StringComparison]::Ordinal)) { continue }
            if ($line -cnotmatch '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$' -or
                $Matches[1] -ieq 'CONDUIT_NATIVE_BENCHMARK_API_KEY' -or
                -not $values.TryAdd($Matches[1], $Matches[2])) { throw 'Invalid private container environment.' }
        }
        return ,$values
    }
    catch { throw 'Invalid private container environment.' }
    finally { if ($null -ne $stream) { $stream.Dispose() } }
}

function Set-ContainerEnvironmentValue($Values, [string] $Name, [string] $Value) {
    # Replace case variants with the canonical Linux environment name.
    $Values.Remove($Name) | Out-Null
    $Values.Add($Name, $Value)
}

function Get-ImageMetadata([string] $Image, $NativeEvidence = $null) {
    $inspect = Invoke-Docker @('image', 'inspect', '--format', '{{json .}}', $Image)
    if ($inspect.ExitCode -ne 0) { throw 'Image inspection failed.' }
    $imageData = Read-NativeImageVerificationJson $inspect.Stdout
    $revision = $imageData.Config.Labels['org.opencontainers.image.revision']
    if ($imageData.Os -cne 'linux' -or $imageData.Architecture -cne 'amd64' -or
        $imageData.Size -is [string] -or $imageData.Size -is [bool] -or $imageData.Size -le 0 -or
        -not [double]::IsFinite([double]$imageData.Size) -or
        [double]$imageData.Size -ne [Math]::Floor([double]$imageData.Size) -or [double]$imageData.Size -gt [long]::MaxValue -or
        $revision -isnot [string] -or [string]::IsNullOrWhiteSpace($revision) -or $revision -match '[\r\n\x00]') {
        throw 'Image metadata is invalid.'
    }
    $localId = $imageData['Id']
    if ($null -ne $NativeEvidence) {
        try {
            if ($localId -isnot [string] -or $localId -cne $NativeEvidence.binding.observedLocalImageId -or
                $revision -cne $NativeEvidence.binding.sourceRevision) { throw 'Native image observation differs.' }
            if ($null -ne $imageData['Descriptor']) {
                $descriptor = Get-NativeOciDescriptor $imageData['Descriptor']
                $expected = Get-NativeOciDescriptor $NativeEvidence.identity.rootDescriptor
                if (-not (Test-NativeOciDescriptorCore $expected $descriptor)) { throw 'Native image descriptor differs.' }
            }
        }
        catch { throw 'Native benchmark image binding failed.' }
        return [pscustomobject]@{ ImageBytes = [long]$imageData.Size; PullBytes = $NativeEvidence.pullBytes; Revision = $revision; LocalId = $localId }
    }
    if ($localId -isnot [string] -or $localId -cnotmatch '^sha256:[0-9a-f]{64}$') { throw 'Image local identity is invalid.' }
    $manifestResult = Invoke-Docker @('manifest', 'inspect', $Image)
    if ($manifestResult.ExitCode -ne 0) { throw 'Image manifest inspection failed.' }
    $manifest = $manifestResult.Stdout | ConvertFrom-Json -AsHashtable
    if ($manifest.ContainsKey('manifests')) {
        $matches = @($manifest.manifests | Where-Object {
            $_.platform.os -ceq 'linux' -and $_.platform.architecture -ceq 'amd64'
        })
        if ($matches.Count -ne 1 -or $matches[0].digest -cnotmatch '^sha256:[0-9a-f]{64}$') {
            throw 'Image platform manifest is invalid.'
        }
        $repository = $Image.Substring(0, $Image.LastIndexOf('@'))
        $manifestResult = Invoke-Docker @('manifest', 'inspect', ($repository + '@' + $matches[0].digest))
        if ($manifestResult.ExitCode -ne 0) { throw 'Image platform manifest inspection failed.' }
        $manifest = $manifestResult.Stdout | ConvertFrom-Json -AsHashtable
    }
    if ($manifest.layers -isnot [array] -or $manifest.layers.Count -eq 0) { throw 'Image layer manifest is invalid.' }
    $pullBytes = 0L
    foreach ($layer in $manifest.layers) {
        $size = $layer.size
        if ($null -eq $size -or $size -is [string] -or $size -is [bool] -or
            [double]$size -ne [Math]::Floor([double]$size) -or $size -le 0 -or
            [double]$size -gt ([long]::MaxValue - $pullBytes)) { throw 'Image layer size is invalid.' }
        $pullBytes += [long]$size
    }
    return [pscustomobject]@{ ImageBytes = [long]$imageData.Size; PullBytes = $pullBytes; Revision = $revision; LocalId = $localId }
}

function Get-OwnedContainer($Record, [switch] $AllowMissing, [switch] $ValidateNativeImage) {
    $identity = if ($null -ne $Record.Id) { $Record.Id } else { $Record.Name }
    $result = Invoke-Docker @('inspect', '--format', '{{json .}}', $identity) $CleanupTimeoutSeconds
    if ($result.ExitCode -ne 0) {
        $message = $result.Stderr.Trim()
        if ($AllowMissing -and $message -cin @("Error: No such object: $identity", "Error: No such container: $identity")) {
            return $null
        }
        throw 'Owned container inspection failed.'
    }
    $data = Read-NativeImageVerificationJson $result.Stdout
    if ($data.Id -cnotmatch '^[0-9a-f]{64}$' -or $data.Name -cne ('/' + $Record.Name) -or
        $data.Config.Labels['conduit.native-benchmark.owner'] -cne $Record.Owner -or
        $data.Config.Labels['conduit.native-benchmark.service'] -cne $Service -or
        $data.Config.Labels['conduit.native-benchmark.runtime'] -cne $Record.Runtime -or
        ($null -ne $Record.Id -and $Record.Id -cne $data.Id)) {
        throw 'Container ownership verification failed.'
    }
    $Record.Id = $data.Id
    if ($ValidateNativeImage -and $Record.Runtime -ceq 'native-aot') {
        try {
            if ($data['Image'] -isnot [string] -or $data['Image'] -cne $nativeEvidence.binding.observedLocalImageId) {
                throw 'Native container local identity differs.'
            }
            Assert-NativeImageRuntimeManifest -Record $nativeEvidence.packaging -Container $data
        }
        catch { throw 'Native benchmark container image binding failed.' }
    }
    return $data
}

function Remove-OwnedContainer($Record) {
    if ($null -eq (Get-OwnedContainer $Record -AllowMissing)) { return }
    $result = Invoke-Docker @('rm', '--force', $Record.Id) $CleanupTimeoutSeconds
    if ($result.ExitCode -ne 0) { throw 'Owned container cleanup failed.' }
    if ($null -ne (Get-OwnedContainer $Record -AllowMissing)) { throw 'Owned container still exists after cleanup.' }
}

function Get-ContainerMemory([string] $Id, [int] $RemainingSeconds = 5) {
    $result = Invoke-Docker @('stats', $Id, '--no-stream', '--format', '{{.MemUsage}}') ([Math]::Min($RemainingSeconds, [Math]::Min(5, $DockerCommandTimeoutSeconds)))
    if ($result.ExitCode -ne 0 -or $result.Stdout -notmatch '^\s*([0-9]+(?:\.[0-9]+)?)\s*(B|KB|KiB|MB|MiB|GB|GiB|TB|TiB)\s*/') {
        throw 'Container memory sampling failed.'
    }
    $value = [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
    $factor = switch -CaseSensitive ($Matches[2]) {
        'B' { 1 }; 'KB' { 1000 }; 'KiB' { 1024 }; 'MB' { 1000000 }; 'MiB' { 1048576 }
        'GB' { 1000000000 }; 'GiB' { 1073741824 }; 'TB' { 1000000000000 }; 'TiB' { 1099511627776 }
    }
    $bytes = $value * $factor
    if (-not [double]::IsFinite($bytes) -or $bytes -le 0 -or $bytes -gt [long]::MaxValue) { throw 'Invalid container memory sample.' }
    return [long][Math]::Round($bytes)
}

function Get-ResultNumber($Row, [string] $Name, [switch] $Integer, [switch] $Positive) {
    $property = $Row.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value -or $property.Value -is [string] -or $property.Value -is [bool]) {
        throw 'Workload report is invalid.'
    }
    $number = [double]$property.Value
    if (-not [double]::IsFinite($number) -or $number -lt 0 -or ($Positive -and $number -le 0) -or
        ($Integer -and $number -ne [Math]::Floor($number))) { throw 'Workload report is invalid.' }
    return $number
}

function Test-WorkloadResult($Report, $Configuration) {
    if ((Get-ResultNumber $Report 'schemaVersion' -Integer -Positive) -ne 2 -or
        $Report.service -isnot [string] -or $Report.service -cne $Service -or
        $Report.workloads -isnot [array] -or $Report.workloads.Count -ne 1) { throw 'Workload report is invalid.' }
    $row = $Report.workloads[0]
    $admin = $Configuration.Id -ceq 'admin-setting-read'
    $method = if ($admin) { 'GET' } else { 'POST' }
    $path = if ($admin) { '/v1/admin/global-settings/by-key/{key}' } else { '/v1/chat/completions' }
    if ($row.id -isnot [string] -or $row.method -isnot [string] -or $row.path -isnot [string] -or
        $row.id -cne $Configuration.Id -or $row.method -cne $method -or $row.path -cne $path -or
        $row.authenticated -isnot [bool] -or -not $row.authenticated -or
        $row.inputFingerprint -isnot [string] -or $row.inputFingerprint -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Workload report is invalid.'
    }
    foreach ($name in @('requested', 'executed', 'completed')) {
        if ((Get-ResultNumber $row $name -Integer -Positive) -ne $Configuration.Data.requests) { throw 'Workload report is invalid.' }
    }
    if ((Get-ResultNumber $row 'failures' -Integer) -ne 0) { throw 'Workload requests failed.' }
    foreach ($name in @('warmupRequests', 'concurrency', 'timeoutSeconds')) {
        if ((Get-ResultNumber $row $name -Integer) -ne $Configuration.Data[$name]) { throw 'Workload report is invalid.' }
    }
    $elapsed = Get-ResultNumber $row 'elapsedMilliseconds' -Positive
    $throughput = Get-ResultNumber $row 'throughputPerSecond' -Positive
    $latencies = @()
    foreach ($name in @('p50Milliseconds', 'p95Milliseconds', 'p99Milliseconds')) { $latencies += Get-ResultNumber $row $name }
    if ($latencies[0] -gt $latencies[1] -or $latencies[1] -gt $latencies[2] -or
        [Math]::Abs($throughput - ($row.completed * 1000 / $elapsed)) -gt [Math]::Max(0.000001, $throughput * 0.000001)) {
        throw 'Workload report is invalid.'
    }
    # Copy only the public report schema; never retain extra child output.
    $public = [ordered]@{}
    foreach ($name in @('id', 'method', 'path', 'authenticated', 'inputFingerprint', 'requested', 'executed', 'completed',
        'failures', 'warmupRequests', 'concurrency', 'timeoutSeconds', 'elapsedMilliseconds', 'throughputPerSecond',
        'p50Milliseconds', 'p95Milliseconds', 'p99Milliseconds')) { $public[$name] = $row.$name }
    return [pscustomobject]$public
}

function Measure-Variant([string] $Runtime, [string] $Image, $ImageMetadata, [int] $HostPort) {
    $owner = [guid]::NewGuid().ToString('N')
    $record = [pscustomobject]@{ Owner = $owner; Name = ('conduit-benchmark-' + $owner + '-' + $Runtime); Runtime = $Runtime; Id = $null }
    $attempted = $false
    $failed = $false
    $primaryMessage = 'Benchmark variant failed.'
    $cleanupFailed = $false
    $measurement = $null
    $samples = [System.Collections.Generic.List[long]]::new()
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $arguments = @('run', '--detach', '--platform', 'linux/amd64', '--name', $record.Name,
            '--label', ('conduit.native-benchmark.owner=' + $owner),
            '--label', ('conduit.native-benchmark.service=' + $Service),
            '--label', ('conduit.native-benchmark.runtime=' + $Runtime),
            '--add-host', 'host.docker.internal:host-gateway',
            '--publish', ('127.0.0.1:' + $HostPort + ':8080'), '--env-file', $environmentFile, $ImageMetadata.LocalId)
        $attempted = $true
        $run = Invoke-Docker $arguments
        if ($run.ExitCode -ne 0 -or $run.Stdout.Trim() -cnotmatch '^[0-9a-f]{64}$') { throw 'Container start failed.' }
        $record.Id = $run.Stdout.Trim()
        $container = Get-OwnedContainer $record -ValidateNativeImage
        if ($container.State.Running -isnot [bool] -or -not $container.State.Running) { throw 'Container start failed.' }
        $ready = $false
        $readyBudget = [System.Diagnostics.Stopwatch]::StartNew()
        while ($readyBudget.Elapsed.TotalSeconds -lt $ReadinessTimeoutSeconds) {
            try {
                $response = Invoke-WebRequest -Uri ('http://127.0.0.1:' + $HostPort + '/health/ready') -TimeoutSec 2 -MaximumRedirection 0 -SkipHttpErrorCheck -NoProxy
                if ($response.StatusCode -eq 200) { $ready = $true; break }
            }
            catch { }
            Start-Sleep -Milliseconds 200
        }
        if (-not $ready) { throw 'Container readiness deadline exceeded.' }
        $watch.Stop()
        $idle = Get-ContainerMemory $record.Id
        $samples.Add($idle)
        $rows = [System.Collections.Generic.List[object]]::new()
        foreach ($configuration in $configurations) {
            $privateConfiguration = [ordered]@{}
            foreach ($key in $configuration.Data.Keys) { $privateConfiguration[$key] = $configuration.Data[$key] }
            $privateConfiguration.baseUrl = 'http://127.0.0.1:' + $HostPort
            $configurationPath = Join-Path $privateRoot ([guid]::NewGuid().ToString('N') + '.json')
            try {
                [System.IO.File]::WriteAllText($configurationPath, ($privateConfiguration | ConvertTo-Json -Depth 4), [System.Text.UTF8Encoding]::new($false))
                Protect-PrivatePath $configurationPath
                $command = $WorkloadProbePath
                $probeArguments = @('--benchmark-workload', $configurationPath)
                if ([System.IO.Path]::GetExtension($WorkloadProbePath) -ieq '.dll') {
                    $command = $DotnetCommandPath
                    $probeArguments = @($WorkloadProbePath) + $probeArguments
                }
                $poll = { param([int] $remainingSeconds) $samples.Add((Get-ContainerMemory $record.Id $remainingSeconds)) }
                $probe = Invoke-BoundedProcess $command $probeArguments $WorkloadTimeoutSeconds @{
                    CONDUIT_NATIVE_BENCHMARK_API_KEY = $apiKey
                } $poll
                if ($probe.ExitCode -ne 0) { throw 'Workload probe failed.' }
                $report = $probe.Stdout | ConvertFrom-Json
                $rows.Add((Test-WorkloadResult $report $configuration))
                $samples.Add((Get-ContainerMemory $record.Id))
            }
            finally {
                if ([System.IO.File]::Exists($configurationPath)) { Remove-Item -LiteralPath $configurationPath -Force }
            }
        }
        $steady = Get-ContainerMemory $record.Id
        $samples.Add($steady)
        $null = Get-OwnedContainer $record -ValidateNativeImage
        $measurement = [pscustomobject]@{
            runtime = $Runtime; image = $Image; imageRevision = $ImageMetadata.Revision
            imageBytes = $ImageMetadata.ImageBytes; pullBytes = $ImageMetadata.PullBytes
            coldReadinessMilliseconds = $watch.Elapsed.TotalMilliseconds
            idleMemoryBytes = $idle; steadyStateMemoryBytes = $steady
            peakMemoryBytes = [long]($samples | Measure-Object -Maximum).Maximum
            memorySamplesBytes = @($samples.ToArray()); workloads = @($rows.ToArray())
        }
        if ($Runtime -ceq 'native-aot') {
            $measurement | Add-Member -NotePropertyName imageBinding -NotePropertyValue $nativeEvidence.binding
            $measurement | Add-Member -NotePropertyName nativeContainerId -NotePropertyValue $record.Id
        }
    }
    catch {
        $failed = $true
        $primaryMessage = Get-SafeFailureMessage $_.Exception 'Benchmark variant failed.'
    }
    finally {
        if ($attempted) {
            try { Remove-OwnedContainer $record }
            catch { $cleanupFailed = $true }
        }
    }
    if ($failed -and $cleanupFailed) { throw 'Benchmark variant failed and owned container cleanup failed.' }
    if ($failed) { throw $primaryMessage }
    if ($cleanupFailed) { throw 'Owned benchmark container cleanup failed.' }
    return $measurement
}

$commonNames = @('imageBytes', 'pullBytes', 'coldReadinessMilliseconds', 'idleMemoryBytes', 'steadyStateMemoryBytes', 'peakMemoryBytes')
$metricNames = @('elapsedMilliseconds', 'throughputPerSecond', 'p50Milliseconds', 'p95Milliseconds', 'p99Milliseconds')
$identityNames = @('id', 'method', 'path', 'authenticated', 'inputFingerprint', 'requested', 'executed', 'completed', 'failures', 'warmupRequests', 'concurrency', 'timeoutSeconds')

function Get-Median([object[]] $Values) {
    $ordered = @($Values | Sort-Object)
    if ($ordered.Count -ne $PairedRuns) { throw 'Incomplete benchmark samples.' }
    return $ordered[[int][Math]::Floor($ordered.Count / 2)]
}

function Get-MedianVariant($Samples, [string] $Name) {
    $raw = @($Samples | ForEach-Object { $_.$Name })
    $first = $raw[0]
    $result = [ordered]@{ runtime = $first.runtime; image = $first.image; imageRevision = $first.imageRevision }
    if ($Name -ceq 'native') {
        foreach ($sample in $raw) {
            Assert-NativeBenchmarkImageEvidenceEqual -Expected $nativeEvidence.binding -Actual $sample.imageBinding
        }
        $result.imageBinding = $nativeEvidence.binding
    }
    foreach ($name in $commonNames) { $result[$name] = Get-Median @($raw | ForEach-Object { $_.$name }) }
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($id in $requiredIds) {
        $rowSamples = @($raw | ForEach-Object { @($_.workloads | Where-Object id -CEQ $id)[0] })
        $row = [ordered]@{}
        foreach ($name in $identityNames) {
            $value = $rowSamples[0].$name
            foreach ($sample in $rowSamples) { if ($sample.$name -cne $value) { throw 'Workload inputs differ across benchmark samples.' } }
            $row[$name] = $value
        }
        foreach ($name in $metricNames) { $row[$name] = Get-Median @($rowSamples | ForEach-Object { $_.$name }) }
        $rows.Add([pscustomobject]$row)
    }
    $result.workloads = @($rows.ToArray())
    return [pscustomobject]$result
}

function Get-Percent([double] $Jit, [double] $Native, [switch] $Increase) {
    if ($Jit -le 0) { throw 'Benchmark comparison requires a positive JIT measurement.' }
    $delta = if ($Increase) { $Native - $Jit } else { $Jit - $Native }
    return [Math]::Round($delta / $Jit * 100, 6)
}

try {
    if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = "artifacts/native-benchmark/$Service.json" }
    $attempt = Start-NativeBenchmarkAttempt -OutputPath $OutputPath -Service $Service
    if ($PSBoundParameters.ContainsKey('Requests') -or $PSBoundParameters.ContainsKey('Concurrency') -or $PairedRuns % 2 -ne 1) {
        throw 'Use matching workload configurations and an odd paired run count.'
    }
    foreach ($image in @($JitImage, $NativeImage)) {
        if ($image -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9._:/-]*@sha256:[0-9a-f]{64}$') { throw 'Images must be pinned by digest.' }
    }
    if (($JitImage -split '@')[1] -ceq ($NativeImage -split '@')[1]) { throw 'JIT and native image digests must differ.' }
    if ($PSBoundParameters.ContainsKey('NativeBaselinePath') -or [string]::IsNullOrWhiteSpace($NativePackagingDirectory)) {
        throw 'Native benchmark image binding failed.'
    }
    try { $nativeEvidence = Read-NativeBenchmarkImageEvidence -Directory $NativePackagingDirectory -Service $Service -NativeImage $NativeImage }
    catch { throw 'Native benchmark image binding failed.' }
    $WorkloadProbePath = [System.IO.Path]::GetFullPath($WorkloadProbePath)
    if (-not [System.IO.File]::Exists($WorkloadProbePath)) { throw 'Workload probe is missing.' }
    $apiKey = [Environment]::GetEnvironmentVariable('CONDUIT_NATIVE_BENCHMARK_API_KEY')
    if ([string]::IsNullOrWhiteSpace($apiKey) -or $apiKey.Length -gt 8192 -or $apiKey -match '[\s\x00-\x1f\x7f]') {
        throw 'Benchmark credentials are missing or invalid.'
    }
    $environment = Read-EnvironmentEvidence $EnvironmentEvidencePath
    $containerEnvironment = Read-ContainerEnvironment $ContainerEnvironmentPath
    $requiredIds = if ($Service -eq 'admin') { @('admin-setting-read') } else { @('gateway-chat-nonstream', 'gateway-chat-sse') }
    if ($WorkloadConfigurationPaths.Count -ne $requiredIds.Count) { throw 'Required workload configurations are missing.' }
    $configurations = @($WorkloadConfigurationPaths | ForEach-Object { Read-WorkloadConfiguration $_ } | Sort-Object Id)
    if ((@($configurations.Id) -join '|') -cne (@($requiredIds | Sort-Object) -join '|')) { throw 'Workload configurations must match the service.' }
    $database = ConvertTo-ContainerUrl $DatabaseUrl @('postgres', 'postgresql')
    $redis = ConvertTo-ContainerUrl $RedisUrl @('redis', 'rediss')
    $jitMetadata = Get-ImageMetadata $JitImage
    $nativeMetadata = Get-ImageMetadata $NativeImage $nativeEvidence
    if ($jitMetadata.Revision -cne $nativeMetadata.Revision) { throw 'JIT and native image revisions must match.' }
    $nativePublish = $nativeEvidence.nativePublish
    $privateRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('conduit-benchmark-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $privateRoot | Out-Null
    Protect-PrivatePath $privateRoot -Directory
    $environmentFile = Join-Path $privateRoot 'container.env'
    Set-ContainerEnvironmentValue $containerEnvironment 'DATABASE_URL' $database
    Set-ContainerEnvironmentValue $containerEnvironment 'REDIS_URL' $redis
    Set-ContainerEnvironmentValue $containerEnvironment 'CONDUIT_MIGRATION_MODE' 'Wait'
    Set-ContainerEnvironmentValue $containerEnvironment 'ConduitLLM__Messaging__Wolverine__AutoProvision' 'false'
    Set-ContainerEnvironmentValue $containerEnvironment 'ASPNETCORE_URLS' 'http://+:8080'
    Set-ContainerEnvironmentValue $containerEnvironment 'ASPNETCORE_HTTP_PORTS' '8080'
    if ($Service -eq 'admin') {
        Set-ContainerEnvironmentValue $containerEnvironment 'CONDUIT_API_TO_API_BACKEND_AUTH_KEY' $apiKey
    }
    foreach ($name in @('CONDUIT_CORE_RATE_LIMIT_MAX_REQUESTS', 'CONDUIT_CORE_RATE_LIMIT_WINDOW_SECONDS')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($null -ne $value) {
            if ($value -notmatch '^[1-9][0-9]{0,8}$') { throw 'Invalid benchmark environment.' }
            Set-ContainerEnvironmentValue $containerEnvironment $name $value
        }
    }
    $environmentLines = [System.Collections.Generic.List[string]]::new()
    foreach ($name in $containerEnvironment.Keys) { $environmentLines.Add($name + '=' + $containerEnvironment[$name]) }
    [System.IO.File]::WriteAllLines($environmentFile, $environmentLines, [System.Text.UTF8Encoding]::new($false))
    Protect-PrivatePath $environmentFile
    # The first complete pair warms host/image caches and is never reported.
    Measure-Variant 'jit' $JitImage $jitMetadata $Port | Out-Null
    Measure-Variant 'native-aot' $NativeImage $nativeMetadata ($Port + 1) | Out-Null
    $samples = [System.Collections.Generic.List[object]]::new()
    for ($pair = 1; $pair -le $PairedRuns; $pair++) {
        $order = if ($pair % 2 -eq 1) { @('jit', 'native-aot') } else { @('native-aot', 'jit') }
        $variants = @{}
        foreach ($runtime in $order) {
            $variants[$runtime] = if ($runtime -eq 'jit') { Measure-Variant $runtime $JitImage $jitMetadata $Port }
                else { Measure-Variant $runtime $NativeImage $nativeMetadata ($Port + 1) }
        }
        $samples.Add([pscustomobject]@{ pair = $pair; order = $order; jit = $variants['jit']; native = $variants['native-aot'] })
    }
    $jit = Get-MedianVariant $samples.ToArray() 'jit'
    $native = Get-MedianVariant $samples.ToArray() 'native'
    for ($index = 0; $index -lt $requiredIds.Count; $index++) {
        foreach ($name in $identityNames) {
            if ($jit.workloads[$index].$name -cne $native.workloads[$index].$name) { throw 'JIT and native workload inputs differ.' }
        }
    }
    $comparisonCommon = [ordered]@{}
    $comparisonNames = [ordered]@{
        imageBytes = 'imageSizeReductionPercent'; pullBytes = 'pullSizeReductionPercent'; coldReadinessMilliseconds = 'coldStartReductionPercent'
        idleMemoryBytes = 'idleMemoryReductionPercent'; steadyStateMemoryBytes = 'steadyStateMemoryReductionPercent'; peakMemoryBytes = 'peakMemoryReductionPercent'
    }
    foreach ($name in $commonNames) { $comparisonCommon[$comparisonNames[$name]] = Get-Percent $jit.$name $native.$name }
    $workloadComparisons = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $requiredIds.Count; $index++) {
        $left = $jit.workloads[$index]
        $right = $native.workloads[$index]
        $row = [ordered]@{ id = $left.id; throughputImprovementPercent = Get-Percent $left.throughputPerSecond $right.throughputPerSecond -Increase }
        foreach ($percentile in @('p50', 'p95', 'p99')) {
            $name = $percentile + 'Milliseconds'
            $row[$percentile + 'LatencyRegressionPercent'] = Get-Percent $left.$name $right.$name -Increase
        }
        $workloadComparisons.Add([pscustomobject]$row)
    }
    try {
        $finalEvidence = Read-NativeBenchmarkImageEvidence -Directory $NativePackagingDirectory -Service $Service -NativeImage $NativeImage
        Assert-NativeBenchmarkImageEvidenceEqual -Expected $nativeEvidence.identity -Actual $finalEvidence.identity
    }
    catch { throw 'Native benchmark image changed during collection.' }
    $report = [pscustomobject]@{
        schemaVersion = 4; generatedAt = $null; collection = $null; service = $Service; platform = 'linux-x64'
        pairedRuns = $PairedRuns
        methodology = 'One unrecorded warm pair, then alternating cold JIT/native container pairs; authenticated workload latency includes complete JSON/SSE response validation. Memory is Docker container usage in bytes, sampled during workload execution.'
        environment = $environment; jit = $jit; native = $native; samples = @($samples.ToArray()); nativePublish = $nativePublish
        nativeEvidence = $nativeEvidence.identity
        comparison = [pscustomobject]@{ common = $comparisonCommon; workloads = @($workloadComparisons.ToArray()) }
    }
}
catch { $collectionFailure = Get-SafeFailureMessage $_.Exception 'Native benchmark collection failed.' }
finally {
    try {
        if ($null -ne $privateRoot -and [System.IO.Directory]::Exists($privateRoot)) {
            $resolved = [System.IO.Path]::GetFullPath($privateRoot)
            $expectedParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
            if ([System.IO.Path]::GetDirectoryName($resolved).TrimEnd([System.IO.Path]::DirectorySeparatorChar) -ne $expectedParent -or
                [System.IO.Path]::GetFileName($resolved) -cnotmatch '^conduit-benchmark-[0-9a-f]{32}$') {
                throw 'Private benchmark cleanup ownership could not be verified.'
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
    catch {
        $privateCleanupFailed = $true
        $collectionFailure = if ($null -ne $collectionFailure) {
            $collectionFailure + ' Private benchmark file cleanup also failed.'
        }
        else { 'Private benchmark file cleanup failed.' }
    }
}
try {
    if ($null -ne $collectionFailure) {
        if ($null -ne $attempt) {
            try {
                $failureCode = if ($privateCleanupFailed) { 'cleanup-failed' } else { 'collection-failed' }
                Set-NativeBenchmarkAttemptFailed -Context $attempt -FailureCode $failureCode
            }
            catch { $collectionFailure += ' Attempt failure status could not be recorded.' }
        }
        throw ($collectionFailure + ' No qualification was recorded.')
    }
    try { Complete-NativeBenchmarkAttempt -Context $attempt -Report $report }
    catch {
        try { Set-NativeBenchmarkAttemptFailed -Context $attempt -FailureCode 'publication-failed' }
        catch { throw 'Native benchmark report and failure status could not be recorded.' }
        throw 'Native benchmark report could not be recorded.'
    }
    Write-Host 'Wrote matched authenticated JIT versus NativeAOT benchmark.'
}
finally { if ($null -ne $attempt) { Close-NativeBenchmarkAttempt -Context $attempt } }
