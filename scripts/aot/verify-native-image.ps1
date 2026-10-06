[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Image,

    [Parameter(Mandatory)]
    [ValidateSet('admin', 'http')]
    [string] $Service
)

$ErrorActionPreference = 'Stop'
$expectedExecutable = if ($Service -eq 'admin') { '/app/ConduitLLM.Admin' } else { '/app/ConduitLLM.Gateway' }
$inspect = docker image inspect $Image | ConvertFrom-Json | Select-Object -First 1
if ($LASTEXITCODE -ne 0 -or $null -eq $inspect) {
    throw "Unable to inspect native image '$Image'."
}

$errors = [System.Collections.Generic.List[string]]::new()
if ($inspect.Architecture -ne 'amd64') { $errors.Add("architecture is '$($inspect.Architecture)', expected 'amd64'") }
if ([string]::IsNullOrWhiteSpace($inspect.Config.User) -or $inspect.Config.User -in @('0', 'root')) {
    $errors.Add('runtime user is root or unspecified')
}
if (($inspect.Config.Entrypoint -join ' ') -ne $expectedExecutable) {
    $errors.Add("entrypoint is not the direct native executable $expectedExecutable")
}
if ($null -eq $inspect.Config.Healthcheck -or $inspect.Config.Healthcheck.Test.Count -eq 0) {
    $errors.Add('container health check is missing')
}
if ($inspect.Config.Labels.'io.conduit.runtime' -ne 'native-aot') { $errors.Add('native runtime label is missing') }
if ($inspect.Config.Labels.'io.conduit.promotion' -ne 'canary-only') { $errors.Add('canary-only promotion label is missing') }
if ($inspect.Config.Env -notcontains 'DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false') {
    $errors.Add('full globalization support is not enabled')
}

$container = docker create $Image
if ($LASTEXITCODE -ne 0) { throw "Unable to create a container from '$Image'." }
$archive = Join-Path ([System.IO.Path]::GetTempPath()) "conduit-native-image-$([guid]::NewGuid().ToString('N')).tar"
try {
    docker export --output $archive $container
    if ($LASTEXITCODE -ne 0) { throw "Unable to export container '$container'." }
    $paths = @(tar -tf $archive)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to list the exported native image.' }

    foreach ($required in @($expectedExecutable.TrimStart('/'), 'app/conduit-health-probe')) {
        if ($paths -notcontains $required) { $errors.Add("required runtime file '$required' is missing") }
    }

    $forbidden = @($paths | Where-Object {
        $_ -match '(^|/)dotnet$' -or
        $_ -match '(^|/)sdk/' -or
        $_ -match '(?i)(Roslyn|csc\.dll|Microsoft\.EntityFrameworkCore\.Design)' -or
        $_ -match '(?i)\.(pdb|dbg)$'
    })
    if ($forbidden.Count -gt 0) {
        $errors.Add("forbidden build/debug content found: $($forbidden -join ', ')")
    }
}
finally {
    docker rm --force $container | Out-Null
    Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
}

if ($errors.Count -gt 0) {
    throw "Native image verification failed:`n - $($errors -join "`n - ")"
}

Write-Host "Verified ${Image}: linux-x64, non-root, direct executable, health checked, and free of SDK/design/debug content."
