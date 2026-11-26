[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('main', 'pressure', 'isolated-standard')][string]$Profile,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [switch]$InterleavedControl
)

$ErrorActionPreference = 'Stop'
$hasControl = $Profile -ne 'pressure' -or $InterleavedControl
$pairCount = if ($hasControl) { 6 } else { 5 }
$expectedPairs = @('fixed->candidate')
if ($hasControl) {
    $expectedPairs += 'fixed->legacy', 'legacy->fixed', 'legacy->candidate'
}

$reports = switch ($Profile) {
    'main' { 'summary.json'; 'core-summary.json' }
    'pressure' { 'summary.json' }
    'isolated-standard' {
        'standard-100/summary.json'
        'standard-1000/summary.json'
        'standard-10000/summary.json'
    }
}

$controlBreaches = [Collections.Generic.List[string]]::new()
$candidateBreaches = [Collections.Generic.List[string]]::new()
foreach ($name in $reports) {
    $report = Get-Content -Raw -LiteralPath (Join-Path $EvidenceDirectory $name) | ConvertFrom-Json
    if (!$report.collectionValidated -or !$report.allWorkloadsPassed -or !$report.comparisons.Count) {
        throw "Incomplete performance evidence in $name"
    }

    $byCase = @($report.comparisons | Group-Object case)
    if ($Profile -eq 'isolated-standard' -and ($byCase.Count -ne 1 -or $byCase[0].Name -cne (Split-Path -Parent $name))) {
        throw "Unexpected isolated workload inventory in $name"
    }

    foreach ($workload in $byCase) {
        $pairs = @($workload.Group | ForEach-Object { "$($_.baseline)->$($_.candidate)" })
        if ($pairs.Count -ne $expectedPairs.Count -or (Compare-Object $pairs $expectedPairs -CaseSensitive)) {
            throw "Incomplete comparison inventory for $($workload.Name) in $name"
        }

        foreach ($comparison in $workload.Group) {
            $cpu = $comparison.metrics.serverWindowCpuMillisecondsPerValidated
            if ($cpu.pairs -ne $pairCount -or $cpu.medianPairedRatio -le 0 -or
                $cpu.status -notin @('within-working-threshold', 'outside-working-threshold')) {
                throw "Incomplete paired CPU evidence for $($workload.Name) in $name"
            }

            foreach ($metric in $comparison.metrics.PSObject.Properties) {
                if ($metric.Value.status -eq 'outside-working-threshold') {
                    $detail = "$($report.profile) / $($workload.Name) / $($comparison.baseline)->$($comparison.candidate) / $($metric.Name): $($metric.Value.medianPairedRatio)"
                    if ($comparison.candidate -ceq 'candidate') {
                        $candidateBreaches.Add($detail)
                    } else {
                        $controlBreaches.Add($detail)
                    }
                }
            }
        }
    }
}

if ($controlBreaches.Count) {
    $controlBreaches | ForEach-Object { Write-Host "Control: $_" }
    $candidateBreaches | ForEach-Object { Write-Host "Candidate: $_" }
    throw 'Inconclusive performance evidence: an identical-binary control exceeded a working budget.'
}
if ($candidateBreaches.Count) {
    throw ("Candidate exceeded a working budget:" + [Environment]::NewLine + ($candidateBreaches -join [Environment]::NewLine))
}
Write-Output 'Gateway performance comparisons satisfy the working budgets.'
