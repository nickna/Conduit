[CmdletBinding()]
param(
    [string] $CollectorPath = (Join-Path $PSScriptRoot '../../scripts/aot/collect-ef-query-inventory.ps1'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot ('bin/resume-contracts-' + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
$CollectorPath = [IO.Path]::GetFullPath($CollectorPath)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Resume contracts require a fresh output directory.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$results = [Collections.Generic.List[object]]::new()

function Assert-ResumeContract([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}
function Write-ResumeFixtureJson([string] $Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 80), $utf8)
}
function Read-ResumeFixtureJson([string] $Path) {
    [IO.File]::ReadAllText($Path, $utf8) | ConvertFrom-Json -AsHashtable
}
function Get-ResumeFixtureFile([string] $Path) {
    @{ path = [IO.Path]::GetFullPath($Path); sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = (Get-Item -LiteralPath $Path).Length }
}
function Invoke-ResumeFixtureContract([string] $Name, [scriptblock] $Assertion) {
    try {
        & $Assertion
        $results.Add(@{ test = $Name; passed = $true })
        Write-Host ('PASS ' + $Name)
    }
    catch {
        $results.Add(@{ test = $Name; passed = $false; failure = $_.Exception.Message })
        Write-Host ('FAIL ' + $Name + ': ' + $_.Exception.Message)
    }
}

# Load only production readers and validators. No collector entry point, archive,
# Git operation, compiler invocation, or service build executes during this import.
$tokens = $null; $parseErrors = $null
$collectorAst = [Management.Automation.Language.Parser]::ParseFile($CollectorPath, [ref]$tokens, [ref]$parseErrors)
Assert-ResumeContract (@($parseErrors).Count -eq 0) 'Collector source did not parse.'
$jsonDefinitions = @($collectorAst.FindAll({ param($node)
    $node -is [Management.Automation.Language.StringConstantExpressionAst] -and
    $node.Value.Contains('public static class ConduitEfInventoryJson')
}, $true))
Assert-ResumeContract ($jsonDefinitions.Count -eq 1) 'Missing unique production JSON parser.'
Add-Type -TypeDefinition $jsonDefinitions[0].Value
foreach ($name in @('Get-InventoryHash', 'Get-InventoryFileEvidence', 'Assert-InventoryOwnedPath', 'Assert-InventoryResumeEvidence')) {
    $functions = @($collectorAst.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true))
    Assert-ResumeContract ($functions.Count -eq 1) ('Missing unique production validator: ' + $name)
    . ([scriptblock]::Create($functions[0].Extent.Text))
}

function New-ResumeFixture([string] $Name) {
    $caseRoot = Join-Path $OutputDirectory $Name
    [void][IO.Directory]::CreateDirectory($caseRoot)
    foreach ($directory in @('commands/normal-admin', 'graphs/normal-admin', 'tool', 'sources/normal-admin/Services/ConduitLLM.Admin')) {
        [void][IO.Directory]::CreateDirectory((Join-Path $caseRoot $directory))
    }
    $sourceDirectory = Join-Path $caseRoot 'sources/normal-admin'
    $projectPath = Join-Path $sourceDirectory 'Services/ConduitLLM.Admin/ConduitLLM.Admin.csproj'
    $sourcePath = Join-Path $sourceDirectory 'Services/ConduitLLM.Admin/Fixture.cs'
    [IO.File]::WriteAllText($sourcePath, 'internal sealed class ResumeFixture {}', $utf8)
    [IO.File]::WriteAllText($projectPath, '<Project Sdk="Microsoft.NET.Sdk" />', $utf8)
    $archivePath = Join-Path $caseRoot 'source.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($sourceDirectory, $archivePath)

    $analyzerOriginal = Join-Path $PSScriptRoot 'bin/Debug/net10.0/ConduitLLM.EfQueryInventory.dll'
    Assert-ResumeContract (Test-Path -LiteralPath $analyzerOriginal) 'Build the semantic runner once before resume contracts.'
    $analyzerPath = Join-Path $caseRoot 'tool/ConduitLLM.EfQueryInventory.dll'
    Copy-Item -LiteralPath $analyzerOriginal -Destination $analyzerPath
    $referencePath = Join-Path $caseRoot 'graphs/normal-admin/reference.dll'
    Copy-Item -LiteralPath $analyzerOriginal -Destination $referencePath
    $targetsPath = Join-Path $caseRoot 'ef-query-inventory.targets'
    Copy-Item -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($CollectorPath)) 'ef-query-inventory.targets') -Destination $targetsPath
    $sarifPath = Join-Path $caseRoot 'graphs/normal-admin/diagnostics.sarif'
    Write-ResumeFixtureJson $sarifPath @{ version = '2.1.0'; runs = @(@{ results = @(
        @{ ruleId = 'CEQ0000'; level = 'note'; properties = @{ evidence = '{"fixture":"compilation"}' } },
        @{ ruleId = 'CEQ0001'; level = 'note'; properties = @{ evidence = '{"fixture":"callsite"}' } }
    ) }) }
    $inputsPath = Join-Path $caseRoot 'graphs/normal-admin/compiler-inputs.b64'
    [IO.File]::WriteAllText($inputsPath, ('Project=' + [Convert]::ToBase64String($utf8.GetBytes($projectPath))), $utf8)
    $stdoutPath = Join-Path $caseRoot 'commands/normal-admin/stdout.private.log'
    $stderrPath = Join-Path $caseRoot 'commands/normal-admin/stderr.private.log'
    [IO.File]::WriteAllText($stdoutPath, 'retained successful compiler stdout', $utf8)
    [IO.File]::WriteAllText($stderrPath, '', $utf8)
    $invocationPath = Join-Path $caseRoot 'commands/normal-admin/invocation.json'
    # This deliberate argument mismatch is an independent guard against builds
    # if a validator regresses. Resume must reject these fixtures before reaching
    # that later profile check; a native compiler child is never needed here.
    $command = @{ phase = 'normal-admin'; command = 'dotnet'; arguments = @('fixture-no-build-guard');
        workingDirectory = $sourceDirectory; processJoined = $true; exitCode = 0;
        stdout = Get-ResumeFixtureFile $stdoutPath; stderr = Get-ResumeFixtureFile $stderrPath }
    Write-ResumeFixtureJson $invocationPath $command
    $files = @(
        @{ path = $sarifPath; role = 'sarif' }, @{ path = $inputsPath; role = 'compiler-inputs' },
        @{ path = $sourcePath; role = 'source' }, @{ path = $referencePath; role = 'reference' },
        @{ path = $stdoutPath; role = 'command' }, @{ path = $stderrPath; role = 'command' },
        @{ path = $invocationPath; role = 'command' }
    ) | ForEach-Object { $evidence = Get-ResumeFixtureFile $_.path; $evidence.role = $_.role; $evidence }
    $sealPath = Join-Path $caseRoot 'graphs/normal-admin/capture-seal.json'
    Write-ResumeFixtureJson $sealPath @{ schemaVersion = 1; kind = 'conduit-ef-joined-compiler-capture-seal';
        phase = 'normal-admin'; sealedAtUtc = [DateTime]::UtcNow.ToString('o'); processJoined = $true; files = @($files) }
    $manifest = @{ schemaVersion = 1; kind = 'conduit-ef-compiled-callsite-inventory'; status = 'captured';
        requestedProfiles = @('normal'); requestedServices = @('admin'); nativeCompileRuntime = $(if ($IsWindows) { 'win-x64' } else { 'linux-x64' });
        source = @{ archive = Get-ResumeFixtureFile $archivePath };
        tool = @{ sourceIdentity = 'UNKNOWN'; provenance = 'externally supplied assembly copied into owned artifact';
            sourceReference = Get-ResumeFixtureFile $analyzerOriginal; assembly = Get-ResumeFixtureFile $analyzerPath;
            injectionTargets = Get-ResumeFixtureFile $targetsPath; sources = @() };
        commands = @($command); captureSeals = @(@{ phase = 'normal-admin'; file = Get-ResumeFixtureFile $sealPath }); graphs = @(); fixtureOnly = $true }
    Write-ResumeFixtureJson (Join-Path $caseRoot 'inventory.json') $manifest
    @{ root = $caseRoot; manifest = $manifest; sarif = $sarifPath; inputs = $inputsPath; reference = $referencePath;
        stdout = $stdoutPath; seal = $sealPath }
}

function Assert-ResumeRejectedBeforeChild($Fixture, [string] $ExpectedMessage) {
    $manifestPath = Join-Path $Fixture.root 'inventory.json'
    $beforeManifest = Get-ResumeFixtureFile $manifestPath
    $beforeCommands = @(Get-ChildItem -LiteralPath (Join-Path $Fixture.root 'commands') -File -Recurse | ForEach-Object FullName)
    $caught = $null
    try { Assert-InventoryResumeEvidence $Fixture.manifest $Fixture.root }
    catch { $caught = $_.Exception.Message }
    Assert-ResumeContract ($null -ne $caught -and $caught -match $ExpectedMessage) ('Production preflight did not reject the changed evidence: ' + $caught)
    $pwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
    $childOutput = @(& $pwsh -NoProfile -File $CollectorPath -Resume -ArtifactDirectory $Fixture.root -Profiles normal -Services admin -CommandTimeoutSeconds 1 2>&1 | ForEach-Object ToString)
    $exitCode = $LASTEXITCODE
    [IO.File]::WriteAllText((Join-Path $Fixture.root 'resume.private.log'), ($childOutput -join "`n"), $utf8)
    Assert-ResumeContract ($exitCode -ne 0 -and ($childOutput -join "`n") -match $ExpectedMessage) 'Collector entry point did not reject at resume preflight.'
    Assert-ResumeContract ((Get-ResumeFixtureFile $manifestPath).sha256 -ceq $beforeManifest.sha256) 'Rejected resume changed the retained terminal manifest.'
    $afterCommands = @(Get-ChildItem -LiteralPath (Join-Path $Fixture.root 'commands') -File -Recurse | ForEach-Object FullName)
    Assert-ResumeContract (@(Compare-Object $beforeCommands $afterCommands).Count -eq 0) 'Rejected resume created a command capture.'
    Assert-ResumeContract (-not (Test-Path -LiteralPath (Join-Path $Fixture.root 'dotnet-home'))) 'Rejected resume initialized compiler child state.'
}

$baseline = New-ResumeFixture 'baseline'
Invoke-ResumeFixtureContract 'unchanged sealed fixture validates with an owned supplied DLL and external provenance' {
    Assert-InventoryResumeEvidence $baseline.manifest $baseline.root
}

foreach ($scenario in @('removed-callsite', 'altered-stream', 'replaced-reference', 'changed-inputs', 'changed-seal', 'missing-seal')) {
    $fixture = New-ResumeFixture $scenario
    switch ($scenario) {
        'removed-callsite' {
            $sarif = Read-ResumeFixtureJson $fixture.sarif
            $sarif.runs[0].results = @($sarif.runs[0].results | Where-Object ruleId -CNE 'CEQ0001')
            Write-ResumeFixtureJson $fixture.sarif $sarif
        }
        'altered-stream' { [IO.File]::AppendAllText($fixture.stdout, 'changed after compiler exit', $utf8) }
        'replaced-reference' { [IO.File]::WriteAllBytes($fixture.reference, @(1, 2, 3, 4)) }
        'changed-inputs' { [IO.File]::AppendAllText($fixture.inputs, "`nReference=Y2hhbmdlZA==", $utf8) }
        'changed-seal' { [IO.File]::AppendAllText($fixture.seal, "`n", $utf8) }
        'missing-seal' {
            $fixture.manifest.captureSeals = @()
            Write-ResumeFixtureJson (Join-Path $fixture.root 'inventory.json') $fixture.manifest
        }
    }
    Invoke-ResumeFixtureContract ($scenario + ' rejects before collector child setup') {
        $expected = switch ($scenario) {
            'changed-seal' { 'Resume joined capture seal hash differs.' }
            'missing-seal' { 'Resume successful compiler invocation has no joined capture seal' }
            default { 'Resume original compiler evidence hash differs.' }
        }
        Assert-ResumeRejectedBeforeChild $fixture ([regex]::Escape($expected))
    }
}

$failed = @($results | Where-Object passed -EQ $false).Count
Write-ResumeFixtureJson (Join-Path $OutputDirectory 'resume-contract-results.json') @{
    passed = $results.Count - $failed; failed = $failed; collector = Get-ResumeFixtureFile $CollectorPath; results = $results.ToArray()
}
Write-Host ('Resume contracts: ' + ($results.Count - $failed) + ' passed, ' + $failed + ' failed; ' + $OutputDirectory)
if ($failed -ne 0) { exit 1 }
