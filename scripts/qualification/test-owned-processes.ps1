[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(!$IsLinux){throw 'Exercise process-tree ownership on Linux'}
if(Test-Path $OutputDirectory){throw 'Use a fresh test output directory'}
New-Item -ItemType Directory -Path $OutputDirectory|Out-Null
$out=(Resolve-Path $OutputDirectory).Path
$configuration=@{workingDirectory=$out}
$observerToken='unused-process-lifecycle-test'
. (Join-Path $PSScriptRoot 'owned-process.ps1')
$checks=[Collections.Generic.List[object]]::new()
try{
 $normal=Start-Owned 'normal' '/bin/sh' @('-c','printf ready')
 Finish-Owned $normal 10
 if(!$normal.Closed -or (Get-Content -Raw "$out/normal.stdout.log") -cne 'ready'){throw 'Normal exit/output capture failed'}
 $checks.Add(@{case='normal-exit';passed=$true})
 $failed=Start-Owned 'failed' '/bin/sh' @('-c','exit 7')
 $rejected=$false
 try{Finish-Owned $failed 10}catch{if($_.ToString() -notmatch 'Exit 7: failed'){throw};$rejected=$true}
 if(!$rejected -or !$failed.Closed -or !$failed.Process.HasExited){throw 'Nonzero exit was accepted or left unjoined'}
 $checks.Add(@{case='nonzero-rejected-and-joined';passed=$true})
 $timeout=Start-Owned 'timeout' '/bin/sh' @('-c','sleep 90 & child=$!; printf "%s" "$child"; wait')
 $rejected=$false
 try{Finish-Owned $timeout 1}catch{if($_.ToString() -notmatch 'Timeout required forced termination'){throw};$rejected=$true}
 $receipt=Get-Content -Raw "$out/timeout.exit.json"|ConvertFrom-Json
 if(!$rejected -or !$timeout.Closed -or !$timeout.Process.HasExited -or !$receipt.timedOut -or !$receipt.exitConfirmed -or !$receipt.outputCaptureCompleted){throw 'Timeout was accepted or not fully joined/captured'}
 $childId=[int](Get-Content -Raw "$out/timeout.stdout.log")
 $childStat="/proc/$childId/stat"
 if((Test-Path $childStat) -and (Get-Content -Raw $childStat) -notmatch '^\d+ \(.+\) Z '){throw 'A live owned descendant survived timeout cleanup'}
 $checks.Add(@{case='timeout-rejected-process-tree-killed-and-output-joined';passed=$true})
}finally{
 $cleanup=@(Clear-OwnedProcesses)
 [ordered]@{passed=($checks.Count -eq 3 -and !$cleanup.Count);checks=$checks.ToArray();cleanupFailures=$cleanup;ownedProcessIds=@($ownedProcesses|ForEach-Object{$_.Process.Id})}|ConvertTo-Json -Depth 5|Set-Content "$out/result.json" -Encoding utf8NoBOM
 if($cleanup.Count){throw ($cleanup -join '; ')}
}
