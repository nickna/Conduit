[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$Suite,
    [string]$Filter,
    [string]$Configuration = 'Release',
    [switch]$NoBuild,
    [switch]$Coverage,
    [int]$Minimum = 1,
    [int]$MaximumSkips = 0
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$results = Join-Path $root "artifacts/tests/$Suite"
New-Item -ItemType Directory -Force $results | Out-Null
$arguments = @('test', $Project, '--configuration', $Configuration, '--verbosity', 'normal',
    '--logger', "trx;LogFileName=$Suite.trx", '--results-directory', $results)
if ($Filter) { $arguments += @('--filter', $Filter) }
if ($NoBuild) { $arguments += '--no-build' }
if ($Coverage) { $arguments += @('--collect', 'XPlat Code Coverage', '--settings', (Join-Path $root '.runsettings')) }
& dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $results 'execution.log')
$testExit = $LASTEXITCODE
python (Join-Path $PSScriptRoot 'test-evidence.py') (Join-Path $results "$Suite.trx") --minimum $Minimum --maximum-skips $MaximumSkips
if ($LASTEXITCODE -ne 0 -or $testExit -ne 0) { throw "$Suite validation failed (dotnet exit $testExit)" }
