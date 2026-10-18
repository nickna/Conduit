#Requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'Repeat-GeneratedCompilation.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'The generated-compilation runner has invalid PowerShell syntax.' }
$definitions = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-CompilationResult'
}, $true))
if ($definitions.Count -ne 1) { throw 'Expected one compilation-result classifier.' }
# Load only the real classifier. Loading the runner itself would restore tools and invoke EF.
. ([scriptblock]::Create($definitions[0].Extent.Text))

$sealedDiagnostic = "Generated/ReadPredicate.EFInterceptors.cs(116,18): error CS0030: Cannot convert type 'EfQueryPrecompilerRepro.VirtualKey' to 'Microsoft.EntityFrameworkCore.Internal.IInjectableService'"
$sealedGenerated = @'
if (instance is IInjectableService)
{
    ((IInjectableService)instance).Injected(context, instance, trackingBehavior, entityType);
}
'@
$discoveryDiagnostic = @'
Generated/DiscoveryIncludeOnly.EFInterceptors.DiscoveryContext.cs(276,42): error CS1002: ; expected
Generated/DiscoveryIncludeOnly.EFInterceptors.DiscoveryContext.cs(276,42): error CS1003: Syntax error, ',' expected
'@
$discoveryGenerated = @'
    var efQueryPrecompilerRepro.DiscoveryMappingPrimaryKeyProperties = new[] { "Id" };
'@
$cases = @(
    @{ name = 'successful generated compilation'; exit = 0; diagnostics = 'Build succeeded.'; generated = $sealedGenerated; expected = 'CompilationSuccess' },
    @{ name = 'sealed entity cast'; exit = 1; diagnostics = $sealedDiagnostic; generated = $sealedGenerated; expected = 'KnownFailure' },
    @{ name = 'repeated compiler diagnostic and retained warning'; exit = 1; diagnostics = ($sealedDiagnostic + "`nwarning CS0184: The given expression is never of the provided type.`n" + $sealedDiagnostic); generated = $sealedGenerated; expected = 'KnownFailure' },
    @{ name = 'unrelated entity cast'; exit = 1; diagnostics = $sealedDiagnostic.Replace('VirtualKey', 'OtherEntity'); generated = $sealedGenerated; expected = 'UnexpectedFailure' },
    @{ name = 'unrelated interface cast'; exit = 1; diagnostics = $sealedDiagnostic.Replace('IInjectableService', 'IDisposable'); generated = $sealedGenerated; expected = 'UnexpectedFailure' },
    @{ name = 'missing generated cast'; exit = 1; diagnostics = $sealedDiagnostic; generated = 'if (instance is IInjectableService) { }'; expected = 'UnexpectedFailure' },
    @{ name = 'mixed MSBuild and compiler errors'; exit = 1; diagnostics = ($sealedDiagnostic + "`nerror MSB4018: The task failed unexpectedly."); generated = $sealedGenerated; expected = 'UnexpectedFailure' },
    @{ name = 'failed build without expected diagnostic'; exit = 1; diagnostics = 'Build FAILED.'; generated = $sealedGenerated; expected = 'UnexpectedFailure' },
    @{ name = 'successful discovery generated compilation'; issue = 1696; exit = 0; diagnostics = 'Build succeeded.'; generated = $discoveryGenerated; expected = 'CompilationSuccess' },
    @{ name = 'discovery dotted local identifier'; issue = 1696; exit = 1; diagnostics = $discoveryDiagnostic; generated = $discoveryGenerated; expected = 'KnownFailure' },
    @{ name = 'repeated discovery diagnostics and retained warning'; issue = 1696; exit = 1; diagnostics = ($discoveryDiagnostic + "`nwarning CS0168: The variable is declared but never used.`n" + $discoveryDiagnostic); generated = $discoveryGenerated; expected = 'KnownFailure' },
    @{ name = 'discovery missing syntax diagnostics'; issue = 1696; exit = 1; diagnostics = 'Build FAILED.'; generated = $discoveryGenerated; expected = 'UnexpectedFailure' },
    @{ name = 'discovery only semicolon diagnostic'; issue = 1696; exit = 1; diagnostics = ($discoveryDiagnostic -split "`n")[0]; generated = $discoveryGenerated; expected = 'UnexpectedFailure' },
    @{ name = 'discovery only comma diagnostic'; issue = 1696; exit = 1; diagnostics = ($discoveryDiagnostic -split "`n")[1]; generated = $discoveryGenerated; expected = 'UnexpectedFailure' },
    @{ name = 'discovery other syntax error'; issue = 1696; exit = 1; diagnostics = ($discoveryDiagnostic + "`nerror CS1513: } expected"); generated = $discoveryGenerated; expected = 'UnexpectedFailure' },
    @{ name = 'discovery unrelated generated file'; issue = 1696; exit = 1; diagnostics = $discoveryDiagnostic.Replace('DiscoveryIncludeOnly.EFInterceptors.DiscoveryContext.cs', 'OtherQuery.EFInterceptors.DiscoveryContext.cs'); generated = $discoveryGenerated; expected = 'UnexpectedFailure' },
    @{ name = 'discovery missing dotted local'; issue = 1696; exit = 1; diagnostics = $discoveryDiagnostic; generated = 'var properties = new[] { "Id" };'; expected = 'UnexpectedFailure' },
    @{ name = 'discovery identifier dot must be literal'; issue = 1696; exit = 1; diagnostics = $discoveryDiagnostic; generated = $discoveryGenerated.Replace('efQueryPrecompilerRepro.DiscoveryMappingPrimaryKeyProperties', 'efQueryPrecompilerReproXDiscoveryMappingPrimaryKeyProperties'); expected = 'UnexpectedFailure' },
    @{ name = 'discovery mixed MSBuild and compiler errors'; issue = 1696; exit = 1; diagnostics = ($discoveryDiagnostic + "`nerror MSB4018: The task failed unexpectedly."); generated = $discoveryGenerated; expected = 'UnexpectedFailure' },
    @{ name = 'sealed entity failure under discovery issue'; issue = 1696; exit = 1; diagnostics = $sealedDiagnostic; generated = $sealedGenerated; expected = 'UnexpectedFailure' },
    @{ name = 'discovery failure under sealed entity issue'; exit = 1; diagnostics = $discoveryDiagnostic; generated = $discoveryGenerated; expected = 'UnexpectedFailure' }
)
foreach ($case in $cases) {
    $issue = if ($case.ContainsKey('issue')) { $case.issue } else { 1695 }
    $actual = Get-CompilationResult -ExitCode $case.exit -Diagnostics $case.diagnostics -GeneratedText $case.generated -Issue $issue
    if ($actual -cne $case.expected) { throw "Classification failed for $($case.name): expected $($case.expected), observed $actual." }
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('conduit-generated-compilation-' + [Guid]::NewGuid().ToString('N'))
$sentinel = Join-Path $temporaryRoot 'sentinel.txt'
$previousVariant = $env:ReproVariant
$originalLocation = (Get-Location).Path
try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    [IO.File]::WriteAllText($sentinel, 'Preserve the existing directory.')
    $sentinelHash = (Get-FileHash -LiteralPath $sentinel).Hash
    $callerVariant = 'caller-' + [Guid]::NewGuid().ToString('N')
    $env:ReproVariant = $callerVariant
    $script:unexpectedDotnetCalls = 0
    function dotnet {
        $script:unexpectedDotnetCalls++
        throw 'Existing-directory rejection must occur before invoking dotnet.'
    }
    foreach ($issue in @(1695, 1696)) {
        $failure = $null
        try { & $runner -Issue $issue -OutputDirectory $temporaryRoot } catch { $failure = $_ }
        if ($null -eq $failure -or $failure.Exception.Message -cne "Use a fresh output directory: $temporaryRoot") {
            throw "The issue-$issue runner did not reject the existing output directory."
        }
        if ($script:unexpectedDotnetCalls -ne 0) { throw "Issue-$issue directory rejection invoked dotnet." }
        if ((Get-FileHash -LiteralPath $sentinel).Hash -cne $sentinelHash -or
            @(Get-ChildItem -LiteralPath $temporaryRoot -Force).Count -ne 1) {
            throw "Issue-$issue directory rejection changed existing files."
        }
        if ($env:ReproVariant -cne $callerVariant -or (Get-Location).Path -cne $originalLocation) {
            throw "Issue-$issue directory rejection changed the caller environment or location."
        }
    }
} finally {
    $env:ReproVariant = $previousVariant
    if (Test-Path -LiteralPath $sentinel) { Remove-Item -LiteralPath $sentinel -Force }
    # The fixture owns only this file and empty directory; preserve unexpected entries for inspection.
    if ((Test-Path -LiteralPath $temporaryRoot) -and @(Get-ChildItem -LiteralPath $temporaryRoot -Force).Count -eq 0) {
        Remove-Item -LiteralPath $temporaryRoot
    }
}

Write-Host "PASS: $($cases.Count + 2) generated-compilation contracts; no EF invocation."
