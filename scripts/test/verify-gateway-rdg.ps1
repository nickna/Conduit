param(
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$project = Join-Path $repoRoot "Services/ConduitLLM.Gateway/ConduitLLM.Gateway.csproj"
$generatedRoot = Join-Path `
    $repoRoot `
    "artifacts/conduit-gateway-rdg-$PID-$([Guid]::NewGuid().ToString('N'))"

try {
    $arguments = @(
        "build"
        $project
        "--configuration", "Release"
        "-t:Rebuild"
        "-p:EmitCompilerGeneratedFiles=true"
        "-p:CompilerGeneratedFilesOutputPath=$generatedRoot"
        "-p:UseSharedCompilation=false"
    )
    if ($NoRestore) {
        $arguments += "--no-restore"
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Gateway Release build failed with exit code $LASTEXITCODE."
    }

    $routeSources = @(
        Get-ChildItem `
            -Path $generatedRoot `
            -Recurse `
            -Filter "GeneratedRouteBuilderExtensions.g.cs" `
            -File
    )
    if ($routeSources.Count -eq 0) {
        throw "RequestDelegateGenerator did not emit GeneratedRouteBuilderExtensions.g.cs."
    }

    $routeSource = ($routeSources | ForEach-Object { Get-Content $_.FullName -Raw }) `
        -join [Environment]::NewLine
    $gatewayHandlers = [Regex]::Matches(
        $routeSource,
        "var handler = Cast\(del,[^\r\n]*ConduitLLM\.Gateway\.Endpoints\.")

    if ($gatewayHandlers.Count -ne 33) {
        throw "Expected 33 generated Gateway lambda endpoint handlers; found $($gatewayHandlers.Count)."
    }

    Write-Host "Gateway RDG guard passed: all 33 lambda endpoint handlers were generated at compile time."
}
finally {
    if (Test-Path -LiteralPath $generatedRoot) {
        Remove-Item -LiteralPath $generatedRoot -Recurse -Force
    }
}
