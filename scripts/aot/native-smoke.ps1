param(
    [string]$ArtifactDirectory,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $repoRoot "artifacts/native-aot"
}
$artifactRoot = [IO.Path]::GetFullPath($ArtifactDirectory)
$runtimeRoot = Join-Path $artifactRoot "runtime"
$reportsRoot = Join-Path $artifactRoot "reports"
$metricsPath = Join-Path $reportsRoot "native-baselines.json"
New-Item -ItemType Directory -Force -Path $reportsRoot | Out-Null

if (!(Test-Path -LiteralPath $metricsPath)) {
    throw "Native publish metrics do not exist: $metricsPath"
}
$metrics = Get-Content -LiteralPath $metricsPath -Raw | ConvertFrom-Json
$failures = [Collections.Generic.List[string]]::new()

foreach ($service in @("Admin", "Gateway")) {
    $serviceSlug = $service.ToLowerInvariant()
    $openApiOutput = Join-Path $reportsRoot "$serviceSlug-openapi.json"
    $stdoutPath = Join-Path $reportsRoot "$serviceSlug-smoke.stdout.log"
    $stderrPath = Join-Path $reportsRoot "$serviceSlug-smoke.stderr.log"
    $result = $metrics.services | Where-Object service -eq $service
    if ($null -eq $result) {
        throw "Native publish metrics are missing $service."
    }
    $executableName = "ConduitLLM.$service"
    if ($result.rid.StartsWith("win-", [StringComparison]::OrdinalIgnoreCase)) {
        $executableName += ".exe"
    }
    $executable = Join-Path $runtimeRoot "$serviceSlug/$executableName"

    $process = $null
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $peakWorkingSetBytes = 0L
    try {
        if (!(Test-Path -LiteralPath $executable -PathType Leaf)) {
            throw "$service native executable does not exist: $executable"
        }

        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $executable
        $startInfo.WorkingDirectory = Split-Path -Parent $executable
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production"
        $startInfo.Environment["CONDUIT_OPENAPI_GENERATION"] = "true"
        $startInfo.Environment["CONDUIT_OPENAPI_OUTPUT"] = $openApiOutput
        $startInfo.Environment["CONDUIT_ENABLE_HTTPS_REDIRECTION"] = "false"
        $startInfo.Environment["DOTNET_EnableDiagnostics"] = "0"
        if ($IsWindows) {
            $startInfo.Environment["Logging__EventLog__LogLevel__Default"] = "None"
        }

        Write-Host "Launching $service native OpenAPI smoke..."
        $process = [Diagnostics.Process]::Start($startInfo)
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        while (!$process.WaitForExit(50)) {
            $process.Refresh()
            $peakWorkingSetBytes = [Math]::Max($peakWorkingSetBytes, $process.WorkingSet64)
            if ($stopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                $process.Kill($true)
                throw "$service native smoke exceeded $TimeoutSeconds seconds."
            }
        }
        $process.Refresh()
        $peakWorkingSetBytes = [Math]::Max($peakWorkingSetBytes, $process.PeakWorkingSet64)
        $stdoutTask.GetAwaiter().GetResult() |
            Set-Content -LiteralPath $stdoutPath -Encoding utf8
        $stderrTask.GetAwaiter().GetResult() |
            Set-Content -LiteralPath $stderrPath -Encoding utf8

        if ($process.ExitCode -ne 0) {
            throw "$service native smoke exited with code $($process.ExitCode). See $stderrPath."
        }
        if (!(Test-Path -LiteralPath $openApiOutput -PathType Leaf)) {
            throw "$service native smoke did not produce an OpenAPI document."
        }
        $document = Get-Content -LiteralPath $openApiOutput -Raw | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace($document.openapi) -or
            $null -eq $document.paths -or
            @($document.paths.PSObject.Properties).Count -eq 0) {
            throw "$service native smoke produced an invalid OpenAPI document."
        }

        $result | Add-Member -NotePropertyName smokeSucceeded -NotePropertyValue $true -Force
        $result | Add-Member -NotePropertyName openApiReadyMilliseconds -NotePropertyValue $stopwatch.ElapsedMilliseconds -Force
        Write-Host "$service native OpenAPI smoke passed."
    }
    catch {
        $failures.Add("$service`: $($_.Exception.Message)")
        $result | Add-Member -NotePropertyName smokeSucceeded -NotePropertyValue $false -Force
        $result | Add-Member -NotePropertyName smokeFailure -NotePropertyValue $_.Exception.Message -Force
        Write-Warning $_.Exception.Message
    }
    finally {
        $stopwatch.Stop()
        if ($null -ne $process -and !$process.HasExited) {
            $process.Kill($true)
        }
        $result | Add-Member -NotePropertyName processDurationMilliseconds -NotePropertyValue $stopwatch.ElapsedMilliseconds -Force
        $result | Add-Member -NotePropertyName peakWorkingSetBytes -NotePropertyValue $peakWorkingSetBytes -Force
    }
}

$metrics.generatedAtUtc = [DateTime]::UtcNow.ToString("O")
$metrics | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath $metricsPath -Encoding utf8

if ($failures.Count -gt 0) {
    throw "Native smoke failures: $($failures -join '; ')"
}
