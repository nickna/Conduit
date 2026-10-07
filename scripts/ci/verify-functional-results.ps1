[CmdletBinding()]
param([Parameter(Mandatory)][string]$Results)
$ErrorActionPreference = 'Stop'
[xml]$trx = Get-Content -LiteralPath $Results -Raw
$inventory = Get-Content (Join-Path $PSScriptRoot 'functional-inventory.json') -Raw | ConvertFrom-Json -AsHashtable
$executed = @($trx.SelectNodes("//*[local-name()='UnitTestResult']") | Where-Object { $_.outcome -in @('Passed', 'Failed') })
$definitions = @{}
foreach ($definition in $trx.SelectNodes("//*[local-name()='UnitTest']")) {
    $definitions[$definition.id] = $definition.TestMethod
}
foreach ($suite in $inventory.Keys) {
    $methods = @($executed | ForEach-Object { $definitions[$_.testId] } |
        Where-Object { $_.className -eq $suite } | Select-Object -ExpandProperty name -Unique)
    if ($methods.Count -lt $inventory[$suite]) {
        throw "$suite promised $($inventory[$suite]) executed methods, found $($methods.Count)"
    }
    Write-Host "$suite : $($methods.Count) methods executed"
}
