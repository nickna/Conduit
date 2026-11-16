[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a fresh inventory-test output directory'}
New-Item -ItemType Directory -Path $OutputDirectory|Out-Null
$testRoot=(Resolve-Path -LiteralPath $OutputDirectory).Path
$fixture=Join-Path $testRoot 'fixture'
New-Item -ItemType Directory -Path $fixture|Out-Null
. (Join-Path $PSScriptRoot 'micro-inventory.ps1')
$expected=@('visible.cs','.gitignore','.config/dotnet-tools.json','visible/.hidden.cs','hidden-directory/inner.cs','bin/generated.dll','obj/generated.cs')
foreach($relative in $expected){
    $path=Join-Path $fixture $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent)|Out-Null
    [IO.File]::WriteAllText($path,"fixture:$relative",[Text.UTF8Encoding]::new($false))
}
if($IsWindows){
    foreach($relative in @('.gitignore','.config','visible/.hidden.cs','hidden-directory')){
        $path=Join-Path $fixture $relative
        [IO.File]::SetAttributes($path,([IO.File]::GetAttributes($path) -bor [IO.FileAttributes]::Hidden))
    }
}
$all=@(Files $fixture)
$source=@(Source-Files $fixture)
if($all.Count -ne $expected.Count -or (@($all.path|Sort-Object)-join "`n") -cne (@($expected|Sort-Object)-join "`n")){throw 'Complete inventory lost hidden files or directories'}
$expectedSource=@($expected|Where-Object{$_ -notmatch '^(bin|obj)/'})
if($source.Count -ne $expectedSource.Count -or (@($source.path|Sort-Object)-join "`n") -cne (@($expectedSource|Sort-Object)-join "`n")){throw 'Source inventory must include hidden paths while excluding generated bin/obj only'}
foreach($file in $all){
    $path=Join-Path $fixture $file.path
    if($file.bytes -ne ([IO.File]::ReadAllBytes($path)).Length -or $file.sha256 -cne (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()){throw 'Inventory byte/hash mismatch'}
}
[ordered]@{schemaVersion=1;passed=$true;allFiles=$all;sourceFiles=$source;scope='Real production inventory helper over visible, dot-prefixed, hidden-directory and generated-output fixtures; no source dataset modified'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $testRoot 'receipt.json') -Encoding utf8NoBOM
Write-Output 'PASS hidden inventory:7 complete files/5 source files; full byte/hash validation.'
