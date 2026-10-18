#Requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet(1695, 1696)]
    [int]$Issue = 1695,
    [ValidateSet('KnownFailure', 'CompilationSuccess')]
    [string]$Expect = 'KnownFailure',
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$EfVersion = '10.0.12',
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$ProviderVersion = '10.0.3',
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$SdkVersion = '10.0.401',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot "artifacts/ef-query-repro/issue-$Issue/$([Guid]::NewGuid().ToString('N'))"
}
$runRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $runRoot) { throw "Use a fresh output directory: $runRoot" }
New-Item -ItemType Directory -Path $runRoot | Out-Null

$variant = if ($Issue -eq 1695) { 'ReadPredicate' } else { 'DiscoveryIncludeOnly' }
$context = if ($Issue -eq 1695) { 'EfQueryPrecompilerRepro.ReproContext' } else { 'EfQueryPrecompilerRepro.DiscoveryContext' }
$project = 'EfQueryPrecompilerRepro.csproj'
$inputs = @($project, 'Directory.Packages.props', 'Program.cs', 'ReproContext.cs', "Variants/$variant.cs")
foreach ($relative in $inputs) {
    $destination = Join-Path $runRoot $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $relative) -Destination $destination
}

# All version overrides and tool restoration are confined to the fresh copy.
$packagesPath = Join-Path $runRoot 'Directory.Packages.props'
[xml]$packages = Get-Content -LiteralPath $packagesPath -Raw
foreach ($package in $packages.Project.ItemGroup.PackageVersion) {
    if ($package.Include -like 'Microsoft.EntityFrameworkCore*') { $package.Version = $EfVersion }
    if ($package.Include -like 'Npgsql*') { $package.Version = $ProviderVersion }
}
$packages.Save($packagesPath)
@{ sdk = @{ version = $SdkVersion; rollForward = 'disable'; allowPrerelease = $false } } |
    ConvertTo-Json -Depth 4 | Set-Content (Join-Path $runRoot 'global.json')
# Prevent repository or machine ancestor build props from altering the isolated control.
'<Project />' | Set-Content (Join-Path $runRoot 'Directory.Build.props')
'<Project />' | Set-Content (Join-Path $runRoot 'Directory.Build.targets')
Copy-Item -LiteralPath (Join-Path $repoRoot 'NuGet.Config') -Destination $runRoot
New-Item -ItemType Directory -Path (Join-Path $runRoot '.config') | Out-Null
@{ version = 1; isRoot = $true; tools = @{ 'dotnet-ef' = @{
    version = $EfVersion; commands = @('dotnet-ef'); rollForward = $false
} } } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $runRoot '.config/dotnet-tools.json')
$inputs += @('global.json', 'Directory.Build.props', 'Directory.Build.targets', 'NuGet.Config', '.config/dotnet-tools.json')

function Get-InputHashes {
    @(foreach ($relative in $inputs) {
        [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath (Join-Path $runRoot $relative)).Hash.ToLowerInvariant() }
    })
}

function Get-GeneratedHashes {
    @(Get-ChildItem -LiteralPath (Join-Path $runRoot 'Generated') -Filter '*.cs' -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($runRoot, $_.FullName).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
    })
}

function Get-CompilationResult([int]$ExitCode, [string]$Diagnostics, [string]$GeneratedText, [int]$Issue = 1695) {
    if ($ExitCode -eq 0) { return 'CompilationSuccess' }
    $errorCodes = @([regex]::Matches($Diagnostics, 'error ([A-Z]+\d+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    if ($Issue -eq 1695 -and $errorCodes.Count -eq 1 -and $errorCodes[0] -eq 'CS0030' -and
        $Diagnostics -match 'VirtualKey.*IInjectableService' -and $GeneratedText -match '\(\(IInjectableService\)instance\)') {
        return 'KnownFailure'
    }
    if ($Issue -eq 1696 -and $errorCodes.Count -eq 2 -and $errorCodes -contains 'CS1002' -and $errorCodes -contains 'CS1003' -and
        $Diagnostics -match 'DiscoveryIncludeOnly\.EFInterceptors\.DiscoveryContext\.cs\(\d+,\d+\): error CS100[23]' -and
        $GeneratedText -match '(?m)^\s*var efQueryPrecompilerRepro\.DiscoveryMappingPrimaryKeyProperties\s*=') {
        return 'KnownFailure'
    }
    return 'UnexpectedFailure'
}

$report = [ordered]@{
    schemaVersion = 1
    issue = $Issue
    upstream = if ($Issue -eq 1695) { 'https://github.com/dotnet/efcore/issues/37957' } else { 'https://github.com/dotnet/efcore/issues/37972' }
    variant = $variant
    context = $context
    startedAtUtc = [DateTime]::UtcNow.ToString('o')
    platform = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    sdkVersion = $SdkVersion
    efVersion = $EfVersion
    providerVersion = $ProviderVersion
    expected = $Expect
    result = 'Incomplete'
    stages = [Collections.Generic.List[object]]::new()
    inputsBefore = @(Get-InputHashes)
    generatedSources = @()
    packageAssetsSha256 = $null
    error = $null
}

function Invoke-ReproStage([string]$Name, [string[]]$Arguments) {
    $logName = "$Name.log"
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $runRoot $logName) | Out-Host
    $stageExit = $LASTEXITCODE
    $report.stages.Add([ordered]@{
        name = $Name; executable = 'dotnet'; arguments = $Arguments; workingDirectory = $runRoot
        exitCode = $stageExit; log = $logName
        logSha256 = (Get-FileHash -LiteralPath (Join-Path $runRoot $logName)).Hash.ToLowerInvariant()
    })
    return $stageExit
}

$previousVariant = $env:ReproVariant
Push-Location $runRoot
try {
    # EF's MSBuildWorkspace also needs the variant, not just the initial build.
    $env:ReproVariant = $variant
    if ((Invoke-ReproStage 'sdk' @('--info')) -ne 0) { throw 'Pinned SDK is unavailable.' }
    if ((Invoke-ReproStage 'tool-restore' @('tool', 'restore', '--tool-manifest', '.config/dotnet-tools.json')) -ne 0) {
        throw 'Aligned EF tool restore failed.'
    }
    if ((Invoke-ReproStage 'initial-build' @('build', $project, '-c', 'Release', '-m:1', '--tl:off')) -ne 0) {
        throw 'Initial build failed before query generation.'
    }
    $generation = Invoke-ReproStage 'generation' @('ef', 'dbcontext', 'optimize', '--project', $project,
        '--startup-project', $project, '--context', $context, '--configuration', 'Release',
        '--output-dir', 'Generated', '--namespace', 'EfQueryPrecompilerRepro.Generated',
        '--no-scaffold', '--precompile-queries', '--nativeaot', '--no-build')
    if ($generation -ne 0) { throw 'Query generation failed before generated-code compilation.' }
    $generated = @(Get-ChildItem -LiteralPath (Join-Path $runRoot 'Generated') -Filter '*.cs' -Recurse)
    if ($generated.Count -eq 0) { throw 'Generation reported success without any generated C# sources.' }
    $report.generatedSources = @(Get-GeneratedHashes)
    $compilation = Invoke-ReproStage 'generated-build' @('build', $project, '-c', 'Release', '--no-restore', '-m:1', '--tl:off')
    $diagnostics = Get-Content -LiteralPath (Join-Path $runRoot 'generated-build.log') -Raw
    $generatedText = ($generated | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    $report.result = Get-CompilationResult $compilation $diagnostics $generatedText $Issue
    if ($report.result -ne $Expect) { throw "Expected $Expect; observed $($report.result). Inspect the retained stage logs." }
} catch {
    $report.error = $_.Exception.Message
    throw
} finally {
    try {
        $report.inputsAfter = @(Get-InputHashes)
        $report.inputsUnchanged = ($report.inputsBefore | ConvertTo-Json -Compress) -ceq ($report.inputsAfter | ConvertTo-Json -Compress)
        if ($report.generatedSources.Count -gt 0) {
            $report.generatedSourcesAfter = @(Get-GeneratedHashes)
            $report.generatedSourcesUnchanged = ($report.generatedSources | ConvertTo-Json -Compress) -ceq ($report.generatedSourcesAfter | ConvertTo-Json -Compress)
        }
        if (-not $report.inputsUnchanged -or $report.generatedSourcesUnchanged -eq $false) {
            $report.result = 'UnexpectedFailure'
            $report.error = 'An isolated input or generated source changed during the run.'
        }
        $assets = Join-Path $runRoot 'obj/project.assets.json'
        if (Test-Path -LiteralPath $assets) { $report.packageAssetsSha256 = (Get-FileHash -LiteralPath $assets).Hash.ToLowerInvariant() }
        $report.completedAtUtc = [DateTime]::UtcNow.ToString('o')
        $report | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $runRoot 'result.json')
        Write-Host "Evidence: $(Join-Path $runRoot 'result.json')"
    } finally {
        Pop-Location
        $env:ReproVariant = $previousVariant
    }
}
if ($report.result -ne $Expect) { throw $report.error }
