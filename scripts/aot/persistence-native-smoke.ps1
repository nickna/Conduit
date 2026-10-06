param(
    [string]$Runtime = "linux-x64",
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
if ([string]::IsNullOrWhiteSpace($env:DATABASE_URL)) {
    throw "DATABASE_URL is required for the native persistence smoke."
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts/native-aot/persistence"
}

$project = Join-Path $repoRoot "Tests/ConduitLLM.PersistenceAotTests/ConduitLLM.PersistenceAotTests.csproj"
dotnet publish $project -c Release -r $Runtime --self-contained true -o $OutputDirectory -p:PublishAot=true
if ($LASTEXITCODE -ne 0) { throw "Native persistence probe publish failed." }

$executable = Join-Path $OutputDirectory "ConduitLLM.PersistenceAotTests"
if ($Runtime.StartsWith("win-")) { $executable += ".exe" }
& $executable
if ($LASTEXITCODE -ne 0) { throw "Native persistence probe failed with exit code $LASTEXITCODE." }
