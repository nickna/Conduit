[CmdletBinding()]
param([string]$NpmCli)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$directory = Join-Path $root "artifacts/npm-override-repro/$([Guid]::NewGuid().ToString('N'))"
$executable = (Get-Command npm).Source
$prefixArguments = @()
if ($NpmCli) {
    $prefixArguments = @((Resolve-Path -LiteralPath $NpmCli).Path)
    $executable = (Get-Command node).Source
}
function Invoke-ReproNpm([string[]]$Arguments) {
    & $executable @prefixArguments @Arguments
    if ($LASTEXITCODE -ne 0) { throw "npm failed: $Arguments" }
}
New-Item -ItemType Directory -Path $directory | Out-Null
Push-Location $directory
try {
    $version = Invoke-ReproNpm @('--version')
    $manifest = [ordered]@{
        name = 'npm-scoped-override-repro'
        private = $true
        devDependencies = [ordered]@{ '@istanbuljs/load-nyc-config' = '1.1.0'; 'js-yaml' = '4.3.2' }
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath package.json -Encoding utf8NoBOM
    $install = @('install', '--package-lock-only', '--ignore-scripts', '--no-audit', '--no-fund')
    Invoke-ReproNpm $install | Set-Content -LiteralPath before.log
    Copy-Item -LiteralPath package-lock.json -Destination before-lock.json
    $manifest.overrides = @{ '@istanbuljs/load-nyc-config' = @{ 'js-yaml' = '4.3.2' } }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath package.json -Encoding utf8NoBOM
    Invoke-ReproNpm $install | Set-Content -LiteralPath after.log
    if (Test-Path -LiteralPath node_modules) { throw 'Reproduction must not contain node_modules' }
    $lock = Get-Content package-lock.json -Raw | ConvertFrom-Json -AsHashtable
    $parser = $lock.packages['node_modules/@istanbuljs/load-nyc-config/node_modules/js-yaml'].version
    $report = [ordered]@{
        node = (& node --version)
        npm = "$version"
        directory = $directory
        retainedParser = $parser
        retainedSprintf = $lock.packages['node_modules/sprintf-js'].version
        reproduced = ($parser -eq '3.15.2')
    }
    $report | ConvertTo-Json | Set-Content -LiteralPath reproduction.json -Encoding utf8NoBOM
    $report | ConvertTo-Json
} finally { Pop-Location }
