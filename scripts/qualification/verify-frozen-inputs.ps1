[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
$manifestPath=Join-Path $PSScriptRoot 'frozen-inputs.json'
$manifest=Get-Content -Raw -LiteralPath $manifestPath|ConvertFrom-Json
if($manifest.schemaVersion -ne 1 -or !$manifest.files.Count){throw 'Invalid frozen-source inventory'}
$results=@(foreach($entry in $manifest.files){
 $path=Join-Path $PSScriptRoot $entry.path
 $exists=Test-Path -LiteralPath $path -PathType Leaf
 $bytes=if($exists){(Get-Item -LiteralPath $path).Length}else{$null}
 $hash=if($exists){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}else{$null}
 [ordered]@{path=$entry.path;expectedBytes=$entry.bytes;actualBytes=$bytes;expectedSha256=$entry.sha256;actualSha256=$hash;passed=($exists -and $bytes -eq $entry.bytes -and $hash -ceq $entry.sha256)}
})
$passed=@($results|Where-Object{!$_.passed}).Count -eq 0
New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($OutputPath))) -Force|Out-Null
[ordered]@{schemaVersion=1;passed=$passed;checkedFiles=$results.Count;manifestSha256=(Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant();files=$results}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
if(!$passed){throw 'Frozen-source byte identity check failed; retain the receipt and investigate'}
