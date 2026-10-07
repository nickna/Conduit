[CmdletBinding()]
param([string]$Actionlint)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Push-Location $root
try {
    $configuration = Get-Content scripts/ci/actionlint-config.json -Raw | ConvertFrom-Json
    $version = $configuration.version
    if (!$Actionlint) {
        $directory = Join-Path $root 'artifacts/tools/actionlint'
        if ($IsWindows) {
            $asset = "actionlint_${version}_windows_amd64.zip"
            $checksum = '6e7241b51e6817ea6a047693d8e6fed13b31819c9a0dd6c5a726e1592d22f6e9'
            $Actionlint = Join-Path $directory 'bin/actionlint.exe'
        } elseif ($IsLinux -and [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq 'X64') {
            $asset = "actionlint_${version}_linux_amd64.tar.gz"
            $checksum = '8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8'
            $Actionlint = Join-Path $directory 'bin/actionlint'
        } else { throw 'Pass -Actionlint with a verified executable on this platform.' }
        New-Item -ItemType Directory -Force "$directory/bin" | Out-Null
        $archive = Join-Path $directory $asset
        if (!(Test-Path -LiteralPath $archive)) {
            Invoke-WebRequest "https://github.com/rhysd/actionlint/releases/download/v$version/$asset" -OutFile $archive -TimeoutSec 60
        }
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $checksum) { throw 'actionlint archive checksum mismatch' }
        if ($IsWindows) { Expand-Archive -LiteralPath $archive -DestinationPath "$directory/bin" -Force }
        else {
            & tar -xzf $archive -C "$directory/bin" actionlint
            if ($LASTEXITCODE -ne 0) { throw 'Could not extract verified actionlint' }
        }
    }
    $toolVersion = @(& $Actionlint -version)
    if ($LASTEXITCODE -ne 0 -or !$toolVersion.Count) { throw 'Could not identify actionlint executable' }
    $env:ACTIONLINT = $Actionlint
    $workflows = Get-ChildItem .github/workflows -File | Where-Object Extension -In '.yml', '.yaml' | ForEach-Object FullName
    # Actionlint validates YAML, Actions schemas, expressions and job/step references.
    # Shellcheck/pyflakes have separate ownership; don't depend on optional local installs.
    # v1.7.12 predates GitHub's documented concurrency.queue syntax (upstream PR #654).
    # Ignore only that exact schema diagnostic; parsed policy validates queue values
    # and cancellation compatibility for every workflow/job, including fixtures.
    $arguments = @('-shellcheck=', '-pyflakes=')
    if ($configuration.queueCompatibilityDiagnostic) {
        $arguments += @('-ignore', $configuration.queueCompatibilityDiagnostic)
    }
    & $Actionlint @arguments @workflows
    if ($LASTEXITCODE -ne 0) { throw 'Actions syntax/expression validation failed' }
    & node scripts/ci/workflow-check.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Workflow gating policy failed' }
    # Exercise the selected executable, including -Actionlint overrides. A newer
    # validator must not silently keep an obsolete compatibility suppression.
    & node --test scripts/ci/workflow-policy.test.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Workflow/queue compatibility regressions failed' }
    Write-Host "Validated $($workflows.Count) active workflows with $($toolVersion[0]) and parsed gating policy."
} finally { Pop-Location }
