function Files([string]$Directory){
    @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Force|Sort-Object FullName|ForEach-Object{
        [ordered]@{path=[IO.Path]::GetRelativePath($Directory,$_.FullName).Replace([char]92,[char]47);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
}
function Source-Files([string]$Directory){@(Files $Directory|Where-Object{$_.path -notmatch '(^|/)(bin|obj)/'})}
