[CmdletBinding()]
param(
    [string] $SourceRevision = 'HEAD',
    [string] $ArtifactDirectory,
    [ValidateSet('normal', 'native')][string[]] $Profiles = @('normal', 'native'),
    [ValidateSet('admin', 'gateway')][string[]] $Services = @('admin', 'gateway'),
    [ValidateSet('win-x64', 'linux-x64')][string] $NativeRuntime = $(if ($IsWindows) { 'win-x64' } else { 'linux-x64' }),
    [string] $AnalyzerAssemblyPath,
    [switch] $Resume,
    [ValidateRange(1, 900)][int] $CommandTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$utf8 = [Text.UTF8Encoding]::new($false, $true)
# SARIF has large diagnostic property strings. Use the SDK JSON parser rather
# than PowerShell's slower object adapter when reconciling every callsite.
Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
public static class ConduitEfInventoryJson
{
    public static object Parse(string text) { using var doc = JsonDocument.Parse(text); return Value(doc.RootElement); }
    public static string Serialize(object value) => JsonSerializer.Serialize(Plain(value), new JsonSerializerOptions { WriteIndented = true, MaxDepth = 80 });
    static object Plain(object value)
    {
        if (value == null) return null;
        var type = value.GetType();
        if (type.FullName == "System.Management.Automation.PSObject") {
            var unwrapped = type.GetProperty("BaseObject").GetValue(value);
            if (Object.ReferenceEquals(value, unwrapped)) throw new InvalidOperationException("Evidence contains an unsupported PowerShell wrapper.");
            return Plain(unwrapped);
        }
        if (value is string || type.IsPrimitive || value is decimal) return value;
        if (value is IDictionary map) {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in map) result.Add((string)entry.Key, Plain(entry.Value));
            return result;
        }
        if (value is IEnumerable array) {
            var result = new List<object>(); foreach (var item in array) result.Add(Plain(item)); return result.ToArray();
        }
        throw new InvalidOperationException("Evidence contains an unsupported scalar type.");
    }
    static object Value(JsonElement e)
    {
        switch (e.ValueKind) {
            case JsonValueKind.Object:
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject()) result.Add(p.Name, Value(p.Value));
                return result;
            case JsonValueKind.Array:
                var list = new List<object>(); foreach (var item in e.EnumerateArray()) list.Add(Value(item)); return list.ToArray();
            case JsonValueKind.String: return e.GetString();
            case JsonValueKind.Number: return e.TryGetInt64(out long n) ? (object)n : e.GetDouble();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            default: return null;
        }
    }
}
'@
$helperPath = Join-Path $PSScriptRoot 'measure-native-container.ps1'
$helperSha = 'c55d7c2750f2c5e6299eb35c659983b953c3c0c99c25d7f07b3508b59bf75501'
if ((Get-FileHash -LiteralPath $helperPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $helperSha) {
    throw 'The pinned bounded-process helper changed; review the collector derivation before collecting.'
}
# Import definitions only. The single derivation binds the actual child working
# directory, which Set-Location does not set for ProcessStartInfo.
$tokens = $null; $parseErrors = $null
$helperAst = [Management.Automation.Language.Parser]::ParseFile($helperPath, [ref]$tokens, [ref]$parseErrors)
if (@($parseErrors).Count -ne 0) { throw 'Bounded-process helper parse failed.' }
foreach ($name in @('Invoke-BoundedProcess', 'Protect-PrivatePath')) {
    $definitions = @($helperAst.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true))
    if ($definitions.Count -ne 1) { throw 'Bounded-process helper definition differs.' }
    $definition = $definitions[0].Extent.Text
    if ($name -ceq 'Invoke-BoundedProcess') {
        $anchor = '$start.UseShellExecute = $false'
        if ($definition.Split($anchor).Length -ne 2) { throw 'Bounded-process working-directory insertion is ambiguous.' }
        $definition = $definition.Replace($anchor, $anchor + "`n" + '        $start.WorkingDirectory = $script:inventoryWorkingDirectory')
        $derivedHelperSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($definition))).ToLowerInvariant()
    }
    . ([scriptblock]::Create($definition))
}
function Write-InventoryJson([string] $Path, $Value) {
    [IO.File]::WriteAllText($Path, [ConduitEfInventoryJson]::Serialize($Value), $utf8)
}
function Get-InventoryHash([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Escape-InventoryProperty([string] $Value) { $Value.Replace('%', '%25').Replace(';', '%3B').Replace(',', '%2C') }
function Assert-InventoryOwnedPath([string] $Path, [string] $Root) {
    $relative = [IO.Path]::GetRelativePath($Root, [IO.Path]::GetFullPath($Path))
    if ([IO.Path]::IsPathRooted($relative) -or $relative -eq '..' -or $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar)) {
        throw 'Inventory evidence path escapes its owned root.'
    }
    $cursor = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            if (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Inventory evidence rejects linked paths.'
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -ceq $cursor) { break }
        $cursor = $parent
    }
}
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $repository ('artifacts/ef-query-inventory/' + [guid]::NewGuid().ToString('N'))
}
$root = [IO.Path]::GetFullPath($ArtifactDirectory)
Assert-InventoryOwnedPath $root $root
$previousManifest = $null
if (Test-Path -LiteralPath $root) {
    if (-not $Resume) { throw 'Inventory collection requires a new artifact directory.' }
    $manifestPath = Join-Path $root 'inventory.json'
    $previousManifest = [ConduitEfInventoryJson]::Parse([IO.File]::ReadAllText($manifestPath, $utf8))
    if ($previousManifest.kind -cne 'conduit-ef-compiled-callsite-inventory' -or $previousManifest.schemaVersion -ne 1 -or $previousManifest.status -cnotin @('failed', 'captured')) { throw 'Resume requires a terminal collector receipt.' }
    if (@(Compare-Object $Profiles $previousManifest.requestedProfiles).Count -or @(Compare-Object $Services $previousManifest.requestedServices).Count -or $NativeRuntime -cne $previousManifest.nativeCompileRuntime) { throw 'Resume profiles must match the original request.' }
}
else {
    if ($Resume) { throw 'Resume artifact directory is missing.' }
    [void][IO.Directory]::CreateDirectory($root)
    Protect-PrivatePath $root -Directory
}
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$git = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$script:inventoryWorkingDirectory = $repository
$childEnvironment = @{
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_NOLOGO = '1'; DOTNET_CLI_UI_LANGUAGE = 'en'
    DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION = '0'; DOTNET_CLI_HOME = (Join-Path $root 'dotnet-home')
}
$commands = [Collections.Generic.List[object]]::new()
if ($previousManifest) { foreach ($command in $previousManifest.commands) { $commands.Add($command) } }
function Invoke-InventoryCommand([string] $Phase, [string] $Command, [string[]] $Arguments, [string] $WorkingDirectory) {
    $script:inventoryWorkingDirectory = $WorkingDirectory
    $capture = Join-Path $root ('commands/' + $Phase)
    [void][IO.Directory]::CreateDirectory($capture)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $receipt = [ordered]@{
        phase = $Phase; command = $Command; arguments = $Arguments; workingDirectory = $WorkingDirectory
        timeoutSeconds = $CommandTimeoutSeconds; helperSha256 = $helperSha; derivedHelperSha256 = $derivedHelperSha
        processJoined = $false; exitCode = $null; failure = $null
    }
    try {
        $results = @(Invoke-BoundedProcess $Command $Arguments $CommandTimeoutSeconds $childEnvironment)
        if ($results.Count -ne 1) { throw 'Inventory command did not return one joined child result.' }
        $result = $results[0]
        $receipt.processJoined = $true; $receipt.exitCode = $result.ExitCode
        foreach ($stream in @('Stdout', 'Stderr')) {
            $path = Join-Path $capture ($stream.ToLowerInvariant() + '.private.log')
            [IO.File]::WriteAllText($path, [string]$result.$stream, $utf8)
            $receipt[$stream.ToLowerInvariant()] = @{ path = $path; sha256 = Get-InventoryHash $path; bytes = (Get-Item -LiteralPath $path).Length }
        }
        return $result
    }
    catch { $receipt.failure = $_.Exception.Message; throw }
    finally {
        $watch.Stop(); $receipt.elapsedMilliseconds = $watch.ElapsedMilliseconds
        Write-InventoryJson (Join-Path $capture 'invocation.json') $receipt
        $commands.Add($receipt)
        Write-Host ('EF inventory command: ' + $Phase + '; exit=' + $receipt.exitCode + '; joined=' + $receipt.processJoined)
    }
}
function Read-InventoryInputs([string] $Path) {
    $values = @{}
    foreach ($line in ([IO.File]::ReadAllText($Path, $utf8).TrimStart([char]0xfeff) -split '\r?\n')) {
        if ($line.Length -eq 0) { continue }
        $separator = $line.IndexOf('=')
        if ($separator -lt 1) { throw 'Compiler input capture has invalid framing.' }
        $name = $line.Substring(0, $separator)
        if ($name -cnotin @('Project', 'Assembly', 'Sdk', 'Configuration', 'PublishAot', 'RuntimeIdentifier', 'Defines', 'Compile', 'Reference', 'ProjectReference')) {
            throw 'Compiler input capture contains an unexpected field.'
        }
        $encoded = $line.Substring($separator + 1)
        $bytes = [Convert]::FromBase64String($encoded)
        if ([Convert]::ToBase64String($bytes) -cne $encoded) { throw 'Compiler input capture has noncanonical Base64.' }
        $value = $utf8.GetString($bytes)
        if (-not $values.ContainsKey($name)) { $values[$name] = [Collections.Generic.List[string]]::new() }
        $values[$name].Add($value)
    }
    foreach ($name in @('Project', 'Assembly', 'Sdk', 'Configuration', 'PublishAot', 'RuntimeIdentifier', 'Defines')) {
        if (-not $values.ContainsKey($name) -or $values[$name].Count -ne 1) { throw 'Compiler input capture is incomplete or repeated.' }
    }
    return $values
}
function Get-InventoryFileEvidence([string] $Path) {
    if (-not [IO.File]::Exists($Path)) { throw 'Compiler input file is missing.' }
    return @{ path = [IO.Path]::GetFullPath($Path); sha256 = Get-InventoryHash $Path; bytes = (Get-Item -LiteralPath $Path).Length }
}
function Get-InventoryArchiveHashes([string] $Path) {
    $hashes = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $zip.Entries) {
            if (-not $entry.FullName.EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase)) { continue }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
            finally { $stream.Dispose() }
            if (-not $hashes.TryAdd($entry.FullName, $hash)) { throw 'Source archive contains ambiguous C# paths.' }
        }
    }
    finally { $zip.Dispose() }
    return ,$hashes
}
function Write-InventoryCaptureSeal([string] $Phase, [string] $CaptureRoot) {
    $files = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    $addFile = {
        param([string] $Path, [string] $Role)
        $fullPath = [IO.Path]::GetFullPath($Path)
        if ($Role -cne 'reference') { Assert-InventoryOwnedPath $fullPath $root }
        if (-not $files.ContainsKey($fullPath)) {
            $file = Get-InventoryFileEvidence $fullPath
            $file.role = $Role; $files.Add($fullPath, $file)
        }
    }
    $inputsFiles = @(Get-ChildItem -LiteralPath $CaptureRoot -Filter 'compiler-inputs.b64' -Recurse -File)
    if ($inputsFiles.Count -eq 0) { throw 'Joined graph has no original compiler inputs to seal.' }
    foreach ($inputsFile in $inputsFiles) {
        & $addFile $inputsFile.FullName 'compiler-inputs'
        & $addFile (Join-Path $inputsFile.DirectoryName 'diagnostics.sarif') 'sarif'
        $inputs = Read-InventoryInputs $inputsFile.FullName
        foreach ($path in $inputs.Compile) { & $addFile $path 'source' }
        foreach ($path in $inputs.Reference) { & $addFile $path 'reference' }
    }
    $commandRoot = Join-Path $root ('commands/' + $Phase)
    foreach ($leaf in @('invocation.json', 'stdout.private.log', 'stderr.private.log')) { & $addFile (Join-Path $commandRoot $leaf) 'command' }
    $seal = @{
        schemaVersion = 1; kind = 'conduit-ef-joined-compiler-capture-seal'; phase = $Phase
        sealedAtUtc = [DateTime]::UtcNow.ToString('o'); processJoined = $true
        files = @($files.Values)
    }
    $sealPath = Join-Path $CaptureRoot 'capture-seal.json'
    if (Test-Path -LiteralPath $sealPath) { throw 'Original joined compiler capture seal already exists.' }
    Write-InventoryJson $sealPath $seal
    return @{ phase = $Phase; file = Get-InventoryFileEvidence $sealPath }
}
function Assert-InventoryResumeEvidence($PreviousManifest, [string] $Root) {
    $sealsByPhase = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($sealEvidence in @($PreviousManifest.captureSeals | Where-Object { $null -ne $_ })) {
        $sealFile = $sealEvidence.file
        Assert-InventoryOwnedPath $sealFile.path $Root
        if (-not [IO.File]::Exists($sealFile.path) -or (Get-InventoryHash $sealFile.path) -cne $sealFile.sha256) { throw 'Resume joined capture seal hash differs.' }
        $seal = [ConduitEfInventoryJson]::Parse([IO.File]::ReadAllText($sealFile.path, $utf8))
        if ($seal.schemaVersion -ne 1 -or $seal.kind -cne 'conduit-ef-joined-compiler-capture-seal' -or $seal.phase -cne $sealEvidence.phase -or $seal.processJoined -ne $true -or -not $sealsByPhase.TryAdd($seal.phase, $seal)) { throw 'Resume joined capture seal is invalid.' }
        if (@($seal.files).Count -eq 0) { throw 'Resume joined capture seal contains no originals.' }
        foreach ($file in $seal.files) {
            if ($file.role -cnotin @('compiler-inputs', 'sarif', 'source', 'reference', 'command')) { throw 'Resume joined capture seal contains an invalid file role.' }
            if ($file.role -cne 'reference') { Assert-InventoryOwnedPath $file.path $Root }
            if (-not [IO.File]::Exists($file.path) -or (Get-Item -LiteralPath $file.path).Length -ne $file.bytes -or (Get-InventoryHash $file.path) -cne $file.sha256) { throw 'Resume original compiler evidence hash differs.' }
        }
    }
    foreach ($command in $PreviousManifest.commands) {
        if ($command.phase -cnotin @('normal-admin', 'normal-gateway', 'native-admin', 'native-gateway') -or $command.exitCode -ne 0 -or $command.processJoined -ne $true) { continue }
        $seal = $null
        if (-not $sealsByPhase.TryGetValue($command.phase, [ref]$seal)) { throw 'Resume successful compiler invocation has no joined capture seal; a new collection is required.' }
        foreach ($leaf in @('invocation.json', 'stdout.private.log', 'stderr.private.log')) {
            $path = Join-Path $Root ('commands/' + $command.phase + '/' + $leaf)
            $matches = @($seal.files | Where-Object { $_.role -ceq 'command' -and $_.path -ieq $path })
            if ($matches.Count -ne 1) { throw 'Resume compiler command originals are missing from the joined seal.' }
        }
    }
}
function Get-InventoryExcludedFiles([string] $Project, $TreePaths, [string] $Source) {
    $names = 'ToListAsync ToArrayAsync ToDictionaryAsync ToHashSetAsync LoadAsync FirstAsync FirstOrDefaultAsync SingleAsync SingleOrDefaultAsync LastAsync LastOrDefaultAsync ElementAtAsync ElementAtOrDefaultAsync AnyAsync AllAsync ContainsAsync CountAsync LongCountAsync MinAsync MaxAsync SumAsync AverageAsync AggregateAsync MinByAsync MaxByAsync ToList ToArray ToDictionary ToHashSet Load First FirstOrDefault Single SingleOrDefault Last LastOrDefault ElementAt ElementAtOrDefault Any All Contains Count LongCount Min Max Sum Average Aggregate MinBy MaxBy SequenceEqual ExecuteUpdate ExecuteUpdateAsync ExecuteDelete ExecuteDeleteAsync SaveChanges SaveChangesAsync Add AddAsync AddRange AddRangeAsync Attach AttachRange Update UpdateRange Remove RemoveRange FromSql FromSqlRaw FromSqlInterpolated SqlQuery SqlQueryRaw ExecuteSql ExecuteSqlAsync ExecuteSqlRaw ExecuteSqlRawAsync ExecuteSqlInterpolated ExecuteSqlInterpolatedAsync ExecuteReader ExecuteReaderAsync ExecuteScalar ExecuteScalarAsync ExecuteNonQuery ExecuteNonQueryAsync Include ThenInclude AsNoTracking AsTracking AsSplitQuery AsSingleQuery AsAsyncEnumerable Where Select SelectMany Find FindAsync Set' -split ' '
    $pattern = '\b(' + (($names | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\s*(?:<[^;\r\n]*?>\s*)?\('
    foreach ($file in Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($Project)) -Filter '*.cs' -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath([IO.Path]::GetDirectoryName($Project), $file.FullName).Replace('\', '/')
        if ($relative -match '(^|/)(obj|bin)/' -or $TreePaths.Contains($file.FullName)) { continue }
        Assert-InventoryOwnedPath $file.FullName $Source
        $matches = [regex]::Matches([IO.File]::ReadAllText($file.FullName, $utf8), $pattern)
        if ($matches.Count -eq 0) { continue }
        @{ file = $file.FullName; fileSha256 = Get-InventoryHash $file.FullName; candidateNames = @($matches | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique); reason = 'project-directory-source-excluded-from-compilation'; semanticResolution = 'UNKNOWN'; mayIncludeCommentsOrStrings = $true }
    }
}
function Read-InventoryGraph([string] $Profile, [string] $Service, [string] $Source, [string] $CaptureRoot) {
    $compilations = [Collections.Generic.List[object]]::new()
    $calls = [Collections.Generic.List[object]]::new()
    $lexical = [Collections.Generic.List[object]]::new()
    $projects = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $inputsFiles = @(Get-ChildItem -LiteralPath $CaptureRoot -Filter 'compiler-inputs.b64' -Recurse -File)
    if ($inputsFiles.Count -eq 0) { throw 'No actual compiler inputs were captured.' }
    foreach ($inputsFile in $inputsFiles) {
        $inputs = Read-InventoryInputs $inputsFile.FullName
        $project = $inputs.Project[0]
        Assert-InventoryOwnedPath $project $Source
        if (-not $projects.Add($project)) { throw 'A project produced ambiguous repeated compiler captures.' }
        if ($inputs.Configuration[0] -cne 'Release' -or $inputs.PublishAot[0] -cne $(if ($Profile -ceq 'native') { 'true' } else { 'false' })) {
            throw 'Actual compiler inputs disagree with the requested profile.'
        }
        $sarifPath = Join-Path $inputsFile.DirectoryName 'diagnostics.sarif'
        if (-not [IO.File]::Exists($sarifPath)) { throw 'A compiled project is missing its original SARIF.' }
        $sarif = [ConduitEfInventoryJson]::Parse([IO.File]::ReadAllText($sarifPath, $utf8))
        if ($sarif.version -cne '2.1.0' -or @($sarif.runs).Count -ne 1) { throw 'Compiler SARIF has an unsupported shape.' }
        $sentinels = [Collections.Generic.List[object]]::new()
        $suppressedDiagnostics = [Collections.Generic.List[object]]::new()
        foreach ($diagnostic in @($sarif.runs[0].results)) {
            $suppressed = $null -ne $diagnostic.suppressions -and @($diagnostic.suppressions).Count -gt 0
            if ($diagnostic.ruleId -ceq 'AD0001' -or ($diagnostic.level -ceq 'error' -and -not $suppressed)) { throw 'Compiler or analyzer errors make this graph incomplete.' }
            if ($suppressed) { $suppressedDiagnostics.Add(@{ ruleId = $diagnostic.ruleId; level = $diagnostic.level; suppressions = $diagnostic.suppressions }) }
            if ($diagnostic.ruleId -cnotin @('CEQ0000', 'CEQ0001', 'CEQ0002')) { continue }
            $properties = $diagnostic.properties
            if ($properties.customProperties) { $properties = $properties.customProperties }
            if ($properties.schemaVersion -cne '1' -or [string]::IsNullOrEmpty($properties.evidence)) { throw 'Analyzer evidence contract is missing.' }
            $evidence = [ConduitEfInventoryJson]::Parse($properties.evidence)
            switch ($diagnostic.ruleId) {
                'CEQ0000' { $sentinels.Add($evidence) }
                'CEQ0001' { $calls.Add(@{ profile = $Profile; serviceGraph = $Service; project = $project; classification = $properties.classification; evidence = $evidence }) }
                'CEQ0002' { $lexical.Add(@{ profile = $Profile; serviceGraph = $Service; project = $project; classification = $properties.classification; evidence = $evidence; semanticResolution = 'UNKNOWN' }) }
            }
        }
        if ($sentinels.Count -ne 1) { throw 'A compiled project is missing its unique analyzer compilation sentinel.' }
        $sentinel = $sentinels[0]
        if ($sentinel.compilation.projectPath -ine $project -or ($sentinel.compilation.assembly -split ',')[0] -cne $inputs.Assembly[0]) {
            throw 'Compilation sentinel does not bind to the captured project.'
        }
        $defineDifference = @(Compare-Object (@($inputs.Defines[0] -split ';' | Where-Object { $_ } | Sort-Object -Unique)) (@($sentinel.compilation.defines | Sort-Object -Unique)) -CaseSensitive)
        if ($defineDifference.Count -gt 0) { throw 'Compilation sentinel does not bind to the captured preprocessor defines.' }
        $sourceFiles = @($inputs.Compile | ForEach-Object { Assert-InventoryOwnedPath $_ $Source; Get-InventoryFileEvidence $_ })
        $treePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $treeMap = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($tree in @($sentinel.syntaxTrees)) {
            $treePath = if ([IO.File]::Exists($tree.file)) { [IO.Path]::GetFullPath($tree.file) } else { $tree.file }
            if (-not $treePaths.Add($treePath) -or -not $treeMap.TryAdd($treePath, $tree)) { throw 'Compilation contains ambiguous syntax-tree paths.' }
            if ([IO.File]::Exists($tree.file)) {
                Assert-InventoryOwnedPath $tree.file $Source
                $actual = Get-InventoryHash $tree.file
                if ($tree.checksum.algorithm -cne 'Sha256' -or $tree.checksum.value -ine $actual) { throw 'Syntax-tree checksum does not bind to its compiler source.' }
            }
            elseif (-not $tree.generated) { throw 'A nongenerated syntax tree does not bind to a source file.' }
        }
        foreach ($sourceFile in $sourceFiles) {
            if (-not $treePaths.Contains($sourceFile.path)) { throw 'A compiler source is missing from the analyzer syntax-tree ledger.' }
            $relativeSource = [IO.Path]::GetRelativePath($Source, $sourceFile.path).Replace('\', '/')
            $archiveHash = $null
            if ($script:inventoryArchiveHashes.TryGetValue($relativeSource, [ref]$archiveHash)) {
                if ($sourceFile.sha256 -cne $archiveHash) { throw 'A tracked compiler source differs from the retained Git tree archive.' }
                $sourceFile.sourceArchiveSha256 = $archiveHash
            }
            elseif (-not $treeMap[$sourceFile.path].generated) { throw 'A nongenerated compiler source is absent from the retained Git tree archive.' }
        }
        foreach ($call in @($calls | Where-Object project -IEQ $project)) {
            $callPath = if ([IO.File]::Exists($call.evidence.file)) { [IO.Path]::GetFullPath($call.evidence.file) } else { $call.evidence.file }
            $tree = $null
            if (-not $treeMap.TryGetValue($callPath, [ref]$tree) -or $call.evidence.sourceChecksum.value -cne $tree.checksum.value -or $call.evidence.sourceChecksum.algorithm -cne $tree.checksum.algorithm -or $call.evidence.compilation.projectPath -ine $project) {
                throw 'A callsite does not bind to its compilation syntax tree.'
            }
        }
        foreach ($excluded in @(Get-InventoryExcludedFiles $project $treePaths $Source)) {
            $lexical.Add(@{ profile = $Profile; serviceGraph = $Service; project = $project; classification = 'file-excluded'; evidence = $excluded; semanticResolution = 'UNKNOWN' })
        }
        $references = @($inputs.Reference | ForEach-Object { Get-InventoryFileEvidence $_ })
        if ($references.Count -eq 0 -or $references.Count -ne @($sentinel.references).Count) { throw 'Compiler reference input count does not bind to the compilation sentinel.' }
        $compilations.Add(@{
            project = $project; sourceRelativeProject = [IO.Path]::GetRelativePath($Source, $project).Replace('\', '/')
            sdk = $inputs.Sdk[0]; configuration = $inputs.Configuration[0]; publishAot = $inputs.PublishAot[0]
            runtimeIdentifier = $inputs.RuntimeIdentifier[0]; defines = $inputs.Defines[0] -split ';'
            sentinel = $sentinel; sources = $sourceFiles; referenceAssemblies = $references
            projectReferences = @($inputs.ProjectReference | Where-Object { $null -ne $_ }); compilerInputs = Get-InventoryFileEvidence $inputsFile.FullName
            originalSarif = Get-InventoryFileEvidence $sarifPath
            existingSuppressedDiagnostics = $suppressedDiagnostics.ToArray()
        })
    }
    foreach ($compilation in $compilations) {
        foreach ($reference in $compilation.projectReferences) {
            if ([IO.Path]::GetExtension($reference) -ieq '.csproj' -and -not $projects.Contains($reference)) { throw 'A referenced C# project is missing its compilation sentinel.' }
        }
    }
    $rootProject = Join-Path $Source ('Services/ConduitLLM.' + $(if ($Service -ceq 'admin') { 'Admin' } else { 'Gateway' }) + '/ConduitLLM.' + $(if ($Service -ceq 'admin') { 'Admin' } else { 'Gateway' }) + '.csproj')
    if (-not $projects.Contains($rootProject)) { throw 'The actual service root is missing its compilation sentinel.' }
    return @{ profile = $Profile; serviceGraph = $Service; sourceDirectory = $Source; complete = $true; compilations = $compilations.ToArray(); callsites = $calls.ToArray(); conditionalExcludedLexicalCandidates = $lexical.ToArray() }
}

if ($previousManifest) {
    Assert-InventoryResumeEvidence $previousManifest $root
    Copy-Item -LiteralPath (Join-Path $root 'inventory.json') -Destination (Join-Path $root ('inventory.before-resume-' + [guid]::NewGuid().ToString('N') + '.json'))
}
$manifest = [ordered]@{
    schemaVersion = 1; kind = 'conduit-ef-compiled-callsite-inventory'; status = 'collecting'; artifactDirectory = $root
    requestedProfiles = $Profiles; requestedServices = $Services; nativeCompileRuntime = $NativeRuntime
    source = $null; tool = $null; graphs = @(); commands = @(); captureSeals = @(); failure = $null
    countingUnit = 'resolved compiler callsite, not SQL query or runtime query shape'
    runtimeReachability = 'UNKNOWN'; completeRuntimeQueryScope = $false; nativePublication = $false; nativeRepositorySuiteQualified = $false
    unresolvedScope = @('caller reachability', 'helper return and parameter query roots', 'generic closures', 'runtime expressions', 'callbacks and delegate targets', 'conditional excluded lexical candidates')
}
$stage = 'source-identity'
try {
    if ($previousManifest) {
        $manifest.source = $previousManifest.source; $manifest.tool = $previousManifest.tool
        $manifest.captureSeals = @($previousManifest.captureSeals)
        $archive = $manifest.source.archive.path; $AnalyzerAssemblyPath = $manifest.tool.assembly.path; $targetsPath = $manifest.tool.injectionTargets.path
        foreach ($fileEvidence in @($manifest.source.archive, $manifest.tool.assembly, $manifest.tool.injectionTargets) + @($manifest.tool.sources)) {
            Assert-InventoryOwnedPath $fileEvidence.path $root
            if ((Get-InventoryHash $fileEvidence.path) -cne $fileEvidence.sha256) { throw 'Resume immutable input hash differs.' }
        }
        $manifest.resumedFrom = 'retained terminal collector receipt; successful compiler invocations reused after full reconciliation'
        $manifest.tool.reconciler = Get-InventoryFileEvidence $PSCommandPath
    }
    else {
    $revisionResult = Invoke-InventoryCommand 'git-revision' $git @('-C', $repository, 'rev-parse', '--verify', ($SourceRevision + '^{commit}')) $repository
    if ($revisionResult.ExitCode -ne 0 -or $revisionResult.Stdout.Trim() -cnotmatch '^[0-9a-f]{40}$') { throw 'Source revision cannot be resolved to a commit.' }
    $revision = $revisionResult.Stdout.Trim()
    $treeResult = Invoke-InventoryCommand 'git-tree' $git @('-C', $repository, 'rev-parse', ($revision + '^{tree}')) $repository
    if ($treeResult.ExitCode -ne 0 -or $treeResult.Stdout.Trim() -cnotmatch '^[0-9a-f]{40}$') { throw 'Source tree cannot be resolved.' }
    $archive = Join-Path $root 'source.zip'
    $archiveResult = Invoke-InventoryCommand 'git-archive' $git @('-C', $repository, 'archive', '--format=zip', ('--output=' + $archive), $revision) $repository
    if ($archiveResult.ExitCode -ne 0) { throw 'Source archive failed.' }
    $manifest.source = @{ revision = $revision; tree = $treeResult.Stdout.Trim(); archive = Get-InventoryFileEvidence $archive; excludesWorkingTreeEdits = $true }
    $stage = 'tool-build'
    if ([string]::IsNullOrWhiteSpace($AnalyzerAssemblyPath)) {
        $toolSource = Join-Path $root 'tool-source'
        [void][IO.Directory]::CreateDirectory($toolSource)
        $toolRepositoryPath = Join-Path $repository 'tools/ConduitLLM.EfQueryInventory'
        $toolFiles = @(Get-ChildItem -LiteralPath $toolRepositoryPath -File | Where-Object Extension -in @('.cs', '.csproj'))
        foreach ($toolFile in $toolFiles) { Copy-Item -LiteralPath $toolFile.FullName -Destination (Join-Path $toolSource $toolFile.Name) }
        Copy-Item -LiteralPath (Join-Path $repository 'global.json') -Destination (Join-Path $toolSource 'global.json')
        $toolBuild = Invoke-InventoryCommand 'tool-build' $dotnet @('build', (Join-Path $toolSource 'ConduitLLM.EfQueryInventory.csproj'), '-c', 'Release', '-o', (Join-Path $root 'tool'), '--nologo', '--tl:off', '-m:1', '-nr:false', '-p:UseSharedCompilation=false') $toolSource
        if ($toolBuild.ExitCode -ne 0) { throw 'Developer-only analyzer build failed.' }
        $AnalyzerAssemblyPath = Join-Path $root 'tool/ConduitLLM.EfQueryInventory.dll'
        $manifest.tool = @{ provenance = 'copied developer-only source'; sources = @(Get-ChildItem -LiteralPath $toolSource -File | ForEach-Object { Get-InventoryFileEvidence $_.FullName }) }
    }
    else {
        $externalReference = Get-InventoryFileEvidence ([IO.Path]::GetFullPath($AnalyzerAssemblyPath))
        $ownedTool = Join-Path $root 'tool'
        [void][IO.Directory]::CreateDirectory($ownedTool)
        $ownedAssembly = Join-Path $ownedTool 'ConduitLLM.EfQueryInventory.dll'
        Copy-Item -LiteralPath $externalReference.path -Destination $ownedAssembly
        if ((Get-InventoryHash $ownedAssembly) -cne $externalReference.sha256) { throw 'Supplied analyzer copy differs from its source reference.' }
        $AnalyzerAssemblyPath = $ownedAssembly
        $manifest.tool = @{ provenance = 'externally supplied assembly copied into owned capture'; sourceIdentity = 'UNKNOWN'; externalSourceReference = $externalReference }
    }
    $AnalyzerAssemblyPath = [IO.Path]::GetFullPath($AnalyzerAssemblyPath)
    $manifest.tool.assembly = Get-InventoryFileEvidence $AnalyzerAssemblyPath
    $targetsPath = Join-Path $root 'ef-query-inventory.targets'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ef-query-inventory.targets') -Destination $targetsPath
    $manifest.tool.injectionTargets = Get-InventoryFileEvidence $targetsPath
    $manifest.tool.collector = Get-InventoryFileEvidence $PSCommandPath
    $sdkResult = Invoke-InventoryCommand 'sdk-info' $dotnet @('--info') $repository
    if ($sdkResult.ExitCode -ne 0) { throw 'SDK identity capture failed.' }
    $manifest.tool.sdkInfoCommand = 'commands/sdk-info/invocation.json'
    }
    $schemaPath = Join-Path $root 'ef-query-inventory.schema.json'
    if (-not (Test-Path -LiteralPath $schemaPath)) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ef-query-inventory.schema.json') -Destination $schemaPath }
    $manifest.schema = Get-InventoryFileEvidence $schemaPath
    $script:inventoryArchiveHashes = Get-InventoryArchiveHashes $archive
    $manifest.source.compilerSourceTreeBinding = 'tracked C# compiler inputs checked against retained Git archive entry hashes'
    # Fixed ordering captures ordinary Release graphs before any native graph.
    foreach ($profile in @('normal', 'native') | Where-Object { $_ -cin $Profiles }) {
        foreach ($service in @('admin', 'gateway') | Where-Object { $_ -cin $Services }) {
            $stage = $profile + '-' + $service
            $source = Join-Path $root ('sources/' + $stage)
            $captureRoot = Join-Path $root ('graphs/' + $stage)
            $priorBuild = @($commands | Where-Object { $_.phase -ceq $stage -and $_.exitCode -eq 0 -and $_.processJoined -eq $true })
            if ($priorBuild.Count -gt 1) { throw 'Resume has ambiguous prior graph commands.' }
            if ($priorBuild.Count -eq 0) {
                if ((Test-Path -LiteralPath $source) -or (Test-Path -LiteralPath $captureRoot)) { throw 'A failed graph build requires a new artifact directory; resume only reuses successful builds.' }
                [void][IO.Directory]::CreateDirectory($source)
                [IO.Compression.ZipFile]::ExtractToDirectory($archive, $source)
                [void][IO.Directory]::CreateDirectory($captureRoot)
            }
            $name = $(if ($service -ceq 'admin') { 'Admin' } else { 'Gateway' })
            $project = Join-Path $source ('Services/ConduitLLM.' + $name + '/ConduitLLM.' + $name + '.csproj')
            $arguments = @('build', $project, '-c', 'Release', '--nologo', '--tl:off', '-m:1', '-nr:false', '-v:minimal', '-p:UseSharedCompilation=false', ('-p:PublishAot=' + $(if ($profile -ceq 'native') { 'true' } else { 'false' })), ('-p:CustomAfterMicrosoftCommonTargets=' + (Escape-InventoryProperty $targetsPath)), ('-p:ConduitEfQueryInventoryDirectory=' + (Escape-InventoryProperty $captureRoot)), ('-p:ConduitEfQueryInventoryAnalyzer=' + (Escape-InventoryProperty $AnalyzerAssemblyPath)))
            if ($profile -ceq 'native') { $arguments += @('--runtime', $NativeRuntime) }
            if ($priorBuild.Count -eq 1) {
                if ($priorBuild[0].workingDirectory -ine $source -or ($priorBuild[0].arguments -join "`n") -cne ($arguments -join "`n")) { throw 'Resume command profile differs from the retained successful compiler invocation.' }
                Write-Host ('EF inventory command reused: ' + $stage + '; exit=0; joined=True')
            }
            else {
                $build = Invoke-InventoryCommand $stage $dotnet $arguments $source
                if ($build.ExitCode -ne 0) { throw 'The actual service graph build failed; original private diagnostics are retained.' }
                $manifest.captureSeals += Write-InventoryCaptureSeal $stage $captureRoot
                $manifest.commands = $commands.ToArray()
                Write-InventoryJson (Join-Path $root 'inventory.json') $manifest
            }
            $graph = Read-InventoryGraph $profile $service $source $captureRoot
            $manifest.graphs += $graph
            Write-InventoryJson (Join-Path $captureRoot 'inventory.json') $graph
            Write-Host ('EF inventory graph: ' + $stage + '; compilations=' + $graph.compilations.Count + '; resolvedCallsites=' + $graph.callsites.Count)
        }
    }
    $manifest.status = 'captured'
}
catch {
    $manifest.status = 'failed'; $manifest.failure = @{ stage = $stage; message = $_.Exception.Message }
    throw 'EF query inventory collection failed; inspect inventory.json and the retained private command/SARIF captures.'
}
finally {
    $manifest.commands = $commands.ToArray()
    $manifest.completedAtUtc = [DateTime]::UtcNow.ToString('o')
    Write-InventoryJson (Join-Path $root 'inventory.json') $manifest
    Write-Host ('EF inventory artifact: ' + $root + '; status=' + $manifest.status + '; runtimeScope=UNKNOWN')
}
