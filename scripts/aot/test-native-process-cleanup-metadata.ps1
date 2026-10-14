#!/usr/bin/env pwsh
[CmdletBinding()]
param()
# Deterministic metadata races using the repository helper's actual nested functions.
# Process parameters become fake objects; proc IO/lookup and pauses use in-memory seams.
# Real Process/SafeHandle behavior, OS scheduling, signals and cleanup timing require
# the real collector contracts; this fixture does not read /proc or launch a process.
$ErrorActionPreference='Stop';$utf8=[Text.UTF8Encoding]::new($false,$true)
$collectorPath=Join-Path $PSScriptRoot 'measure-native-container.ps1'
$source=[IO.File]::ReadAllText($collectorPath,$utf8);$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors)
$owner=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-BoundedProcess'},$true))
if(@($errors).Count -or $owner.Count -ne 1){throw 'Exact source extraction closure differs'}
$functions=@($owner[0].Body.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true))
if(($functions.Name -join '|') -cne 'Test-LinuxMissingMetadata|Read-LinuxOwnedProcess|Capture-LinuxOwnedDescendants'){throw 'Exact nested source functions differ'}
$closure=($functions.Extent.Text -join "`r`n`r`n")
$seams=[Collections.Generic.List[object]]::new()
foreach($parameter in @('OwnedProcess','RootProcess')) {
    $oldParameter='[Diagnostics.Process] $'+$parameter;$newParameter='[object] $'+$parameter
    if($closure.Split($oldParameter,[StringSplitOptions]::None).Length -ne 2){throw 'Exact fake Process parameter seam differs'}
    $closure=$closure.Replace($oldParameter,$newParameter)
    $seams.Add([ordered]@{kind='parameter-type';before=$oldParameter;after=$newParameter})
}
$methodTargets=@{'IO.File.ReadAllText'='Read-FixtureFile';'IO.Directory.EnumerateDirectories'='Get-FixtureDirectories';'IO.File.GetAttributes'='Get-FixtureAttributes';'Diagnostics.Process.GetProcessById'='Get-FixtureProcess'}
$expectedCounts=@{'IO.File.ReadAllText'=3;'IO.Directory.EnumerateDirectories'=1;'IO.File.GetAttributes'=2;'Diagnostics.Process.GetProcessById'=1}
$closureTokens=$null;$closureErrors=$null;$closureAst=[Management.Automation.Language.Parser]::ParseInput($closure,[ref]$closureTokens,[ref]$closureErrors)
if(@($closureErrors).Count){throw 'Extracted parameter-seamed closure syntax differs'}
$invocations=@($closureAst.FindAll({param($n)$n -is [Management.Automation.Language.InvokeMemberExpressionAst] -and $n.Static -and $n.Expression -is [Management.Automation.Language.TypeExpressionAst]},$true))
$replacements=[Collections.Generic.List[object]]::new()
foreach($invocation in $invocations) {
    $key=$invocation.Expression.TypeName.FullName+'.'+$invocation.Member.Value
    if(-not $methodTargets.ContainsKey($key)){continue}
    if($invocation.Arguments.Count -ne 1){throw 'Each metadata method seam must have one exact source argument'}
    $replacement='('+$methodTargets[$key]+' ('+$invocation.Arguments[0].Extent.Text+'))'
    $replacements.Add([ordered]@{kind='metadata-method';key=$key;start=$invocation.Extent.StartOffset;length=$invocation.Extent.EndOffset-$invocation.Extent.StartOffset;before=$invocation.Extent.Text;after=$replacement})
}
foreach($key in $expectedCounts.Keys){if(@($replacements|Where-Object {$_.key -ceq $key}).Count -ne $expectedCounts[$key]){throw 'Exact metadata method seam cardinality differs'}}
$descending=@($replacements|Sort-Object { [int]$_.start } -Descending)
if($descending.Count -ne 7){throw 'Seven exact metadata substitutions required'}
for($i=1;$i -lt $descending.Count;$i++){if([int]$descending[$i-1].start -le [int]$descending[$i].start){throw 'Metadata replacement offsets must be strictly descending'}}
foreach($replacement in $descending){
    if($closure.Substring([int]$replacement.start,[int]$replacement.length) -cne $replacement.before){throw 'Original source span differs before its exact metadata substitution'}
    $closure=$closure.Substring(0,$replacement.start)+$replacement.after+$closure.Substring($replacement.start+$replacement.length)
    $seams.Add($replacement)
}
$waitLoops=@($owner[0].Body.FindAll({param($n)$n -is [Management.Automation.Language.ForEachStatementAst] -and $n.Extent.Text.Contains('while ($true)') -and $n.Extent.Text.StartsWith('foreach ($record in $linuxDescendants)',[StringComparison]::Ordinal)},$true))
if($waitLoops.Count -ne 1){throw 'One exact retained-record completion loop required'}
$waitText=$waitLoops[0].Extent.Text
if($waitText.Split('Start-Sleep -Milliseconds 10',[StringSplitOptions]::None).Length -ne 2){throw 'One fixture pause seam required'}
$waitFixture=[scriptblock]::Create($waitText.Replace('Start-Sleep -Milliseconds 10','Invoke-FixturePause'))
$fixtureTokens=$null;$fixtureErrors=$null;[void][Management.Automation.Language.Parser]::ParseInput($closure,[ref]$fixtureTokens,[ref]$fixtureErrors)
if(@($fixtureErrors).Count){throw 'Source-derived fixture syntax differs'}

# All proc reads, directory checks and process lookup below operate on in-memory tables.
# Fake Process parameter substitutions do not test real Diagnostics.Process constraints,
# Unix SafeHandle behavior, OS scheduling, process termination or the five-second duration.
function Reset-Fixture {
    $script:fixture=@{Files=@{};Directories=@{};Attributes=@{};Processes=@{};Reads=@{};Events=[Collections.Generic.List[string]]::new();PauseCount=0;OnPause=$null}
}
function New-FixtureProcess([int]$Id,[bool[]]$RefreshSequence=@()) {
    $p=[pscustomobject]@{Id=$Id;HasExited=$false;SafeHandle='fake managed wait wrapper';Disposed=$false;RefreshSequence=[Collections.Generic.Queue[bool]]::new()}
    foreach($value in $RefreshSequence){$p.RefreshSequence.Enqueue($value)}
    $p|Add-Member ScriptMethod Refresh {if($this.RefreshSequence.Count -gt 0){$this.HasExited=$this.RefreshSequence.Dequeue()}}
    $p|Add-Member ScriptMethod Dispose {$this.Disposed=$true}
    return $p
}
function New-FixtureStat([int]$Id,[int]$ParentId,[ulong]$StartToken,[string]$State='R') {
    $fields=[string[]]::new(20);for($i=0;$i -lt $fields.Count;$i++){$fields[$i]='0'};$fields[0]=$State;$fields[1]=[string]$ParentId;$fields[19]=[string]$StartToken
    return ([string]$Id+' (worker (nested) name) '+($fields -join ' '))
}
function Read-FixtureFile([string]$Path) {
    $Path=$Path.Replace([char]92,[char]47)
    $fixture.Events.Add('read:'+ $Path)
    if(-not $fixture.Files.ContainsKey($Path)){throw [IO.FileNotFoundException]::new('Owned fixture path absent.',$Path)}
    $count=if($fixture.Reads.ContainsKey($Path)){$fixture.Reads[$Path]}else{0};$fixture.Reads[$Path]=$count+1
    $value=$fixture.Files[$Path];if($value -is [object[]]){$value=$value[[Math]::Min($count,$value.Count-1)]}
    if($value -is [scriptblock]){$value=& $value};if($value -is [Exception]){throw $value};return [string]$value
}
function Get-FixtureDirectories([string]$Path){$fixture.Events.Add('enumerate:'+ $Path);if(-not $fixture.Directories.ContainsKey($Path)){throw [IO.DirectoryNotFoundException]::new('Owned fixture directory absent.')} ;return $fixture.Directories[$Path]}
function Get-FixtureAttributes([string]$Path){$fixture.Events.Add('attributes:'+ $Path);if(-not $fixture.Attributes.ContainsKey($Path)){throw [IO.FileNotFoundException]::new('Exact fixture path absent.',$Path)};$value=$fixture.Attributes[$Path];if($value -is [scriptblock]){$value=& $value};if($value -is [Exception]){throw $value};return $value}
function Get-FixtureProcess([int]$Id){$fixture.Events.Add('get-process:'+ $Id);if(-not $fixture.Processes.ContainsKey($Id)){throw [ArgumentException]::new('Fixture process no longer running.')};$value=$fixture.Processes[$Id];if($value -is [Exception]){throw $value};return $value}
function Invoke-FixturePause{$fixture.PauseCount++;if($null -ne $fixture.OnPause){& $fixture.OnPause}}
. ([scriptblock]::Create($closure))
function Require([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Expect-Rejection([scriptblock]$Action){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};Require $rejected 'Expected this metadata timeline to be rejected.'}
function Setup-Tree([string]$Children='20') {
    Reset-Fixture;$script:root=New-FixtureProcess 10;$script:records=[Collections.Generic.List[object]]::new()
    $fixture.Files['/proc/10/stat']=New-FixtureStat 10 1 100
    $fixture.Directories['/proc/10/task']=@('/proc/10/task/10')
    $fixture.Attributes['/proc/10/task/10']=[IO.FileAttributes]::Directory;$fixture.Files['/proc/10/task/10/children']=$Children
}
function Run-Completion([object]$Record,[Threading.CancellationToken]$Token=[Threading.CancellationToken]::None) {
    $linuxDescendants=[Collections.Generic.List[object]]::new();$linuxDescendants.Add($Record)
    $reap=[pscustomobject]@{Token=$Token};$cleanupFailed=$false
    . $waitFixture
    return $cleanupFailed
}
$rows=[Collections.Generic.List[object]]::new()
function Case([string]$Name,[scriptblock]$Action){try{& $Action|Out-Null;$rows.Add([ordered]@{name=$Name;passed=$true;events=@($fixture.Events)})}catch{$rows.Add([ordered]@{name=$Name;passed=$false;exceptionType=$_.Exception.GetType().FullName;message=$_.Exception.Message;events=@($fixture.Events)})}}
Case 'thread disappears after enumeration while retained parent remains live' {
    Setup-Tree;$fixture.Files.Remove('/proc/10/task/10/children');$fixture.Attributes['/proc/10/task/10']=[IO.FileNotFoundException]::new('Exact fixture task path absent.','/proc/10/task/10')
    $emitted=@(Capture-LinuxOwnedDescendants $root $records ([Threading.CancellationToken]::None));Require ($records.Count -eq 0 -and $emitted.Count -eq 0) 'Unacquired task exit should emit no record or success output.'
}
Case 'missing children file in still present exact task is rejected' {Setup-Tree;$fixture.Files.Remove('/proc/10/task/10/children');Expect-Rejection {Capture-LinuxOwnedDescendants $root $records ([Threading.CancellationToken]::None)}}
Case 'permission failure is not treated as thread disappearance' {Setup-Tree;$fixture.Files['/proc/10/task/10/children']=[UnauthorizedAccessException]::new('Fixture permission denied.');$fixture.Attributes['/proc/10/task/10']=[IO.FileNotFoundException]::new('Exact fixture task path absent.','/proc/10/task/10');Expect-Rejection {Capture-LinuxOwnedDescendants $root $records ([Threading.CancellationToken]::None)}}
Case 'parent PID start changes during task disappearance fallback' {Setup-Tree;$fixture.Files.Remove('/proc/10/task/10/children');$fixture.Attributes['/proc/10/task/10']=[IO.FileNotFoundException]::new('Exact fixture task path absent.','/proc/10/task/10');$fixture.Files['/proc/10/stat']=@((New-FixtureStat 10 1 100),(New-FixtureStat 10 1 100),(New-FixtureStat 10 1 300));Expect-Rejection {Capture-LinuxOwnedDescendants $root $records ([Threading.CancellationToken]::None)}}
Case 'retained parent positively reports exit during exact missing task fallback' {Setup-Tree;$script:root=New-FixtureProcess 10 @($false,$false,$true);$fixture.Files.Remove('/proc/10/task/10/children');$fixture.Attributes['/proc/10/task/10']=[IO.FileNotFoundException]::new('Exact fixture task path absent.','/proc/10/task/10');Capture-LinuxOwnedDescendants $root $records ([Threading.CancellationToken]::None);Require ($records.Count -eq 0 -and $root.HasExited) 'Parent exit must be positive retained-record evidence.'}
Case 'child exits before Process record acquisition with exact stat absent' {Setup-Tree;Capture-LinuxOwnedDescendants $root $records ([Threading.CancellationToken]::None);Require ($records.Count -eq 0) 'Unacquired gone child must not manufacture a completion record.'}
Case 'missing process lookup but exact child stat now present is rejected' {Setup-Tree;$fixture.Files['/proc/20/stat']=New-FixtureStat 20 10 300;Expect-Rejection {Capture-LinuxOwnedDescendants $root $records ([Threading.CancellationToken]::None)}}
Case 'missing process lookup followed by permission failure is rejected' {Setup-Tree;$fixture.Files['/proc/20/stat']=[UnauthorizedAccessException]::new('Fixture permission denied.');Expect-Rejection {Capture-LinuxOwnedDescendants $root $records ([Threading.CancellationToken]::None)}}
Case 'acquired child start changes across managed wait-wrapper acquisition' {Setup-Tree;$child=New-FixtureProcess 20;$fixture.Processes[20]=$child;$fixture.Files['/proc/20/stat']=@((New-FixtureStat 20 10 200),(New-FixtureStat 20 10 300));Expect-Rejection {Capture-LinuxOwnedDescendants $root $records ([Threading.CancellationToken]::None)};Require ($records.Count -eq 1 -and $records[0].Process -eq $child) 'Partial acquired record must remain caller owned.'}
Case 'acquired child ancestry differs despite matching child start' {Setup-Tree;$fixture.Processes[20]=New-FixtureProcess 20;$fixture.Files['/proc/20/stat']=New-FixtureStat 20 99 200;Expect-Rejection {Capture-LinuxOwnedDescendants $root $records ([Threading.CancellationToken]::None)};Require ($records.Count -eq 1) 'Caller must retain a partial record after ancestry failure.'}
Case 'malformed state cannot be accepted as owned live process metadata' {Reset-Fixture;$p=New-FixtureProcess 20;$fixture.Files['/proc/20/stat']=New-FixtureStat 20 10 200 '?';Expect-Rejection {Read-LinuxOwnedProcess $p}}
Case 'retained child stat absent but exact PID directory present rejects stale handle completion' {Reset-Fixture;$p=New-FixtureProcess 20;$fixture.Attributes['/proc/20']=[IO.FileAttributes]::Directory;Require (Run-Completion ([pscustomobject]@{Process=$p;Identity=[pscustomobject]@{Id=20;ParentId=10;StartToken=200;Zombie=$false}})) 'A present PID directory must reject missing-stat completion.';Require ($fixture.Events.Contains('attributes:/proc/20')) 'Expected the exact retained PID directory probe.'}
Case 'retained child missing metadata with refreshed HasExited completes' {Reset-Fixture;$p=New-FixtureProcess 20 @($false,$true);Require (-not (Run-Completion ([pscustomobject]@{Process=$p;Identity=[pscustomobject]@{Id=20;ParentId=10;StartToken=200;Zombie=$false}}))) 'Confirmed retained-record exit should complete.'}
Case 'matching zombie after reparenting completes without a child signal' {Reset-Fixture;$p=New-FixtureProcess 20;$fixture.Files['/proc/20/stat']=New-FixtureStat 20 1 200 'Z';Require (-not (Run-Completion ([pscustomobject]@{Process=$p;Identity=[pscustomobject]@{Id=20;ParentId=10;StartToken=200;Zombie=$false}}))) 'Matching zombie should complete without requiring original PPID after kill.'}
Case 'reused PID zombie with different start cannot complete original record' {Reset-Fixture;$p=New-FixtureProcess 20;$fixture.Files['/proc/20/stat']=New-FixtureStat 20 1 300 'Z';Require (Run-Completion ([pscustomobject]@{Process=$p;Identity=[pscustomobject]@{Id=20;ParentId=10;StartToken=200;Zombie=$false}})) 'Zombie alone must not bypass start identity.'}
Case 'live retained child completing after one fixture scheduling turn is awaited' {Reset-Fixture;$script:pending=New-FixtureProcess 20;$fixture.Files['/proc/20/stat']=New-FixtureStat 20 1 200;$fixture.OnPause={$script:pending.HasExited=$true};Require (-not (Run-Completion ([pscustomobject]@{Process=$pending;Identity=[pscustomobject]@{Id=20;ParentId=10;StartToken=200;Zombie=$false}}))) 'Retained live child should be checked until it completes.';Require ($fixture.PauseCount -eq 1) 'Expected one simulated scheduling turn.'}
Case 'shared token cancellation rejects completion before metadata reads' {Reset-Fixture;$p=New-FixtureProcess 20;$cts=[Threading.CancellationTokenSource]::new();try{$cts.Cancel();Require (Run-Completion ([pscustomobject]@{Process=$p;Identity=[pscustomobject]@{Id=20;ParentId=10;StartToken=200;Zombie=$false}}) $cts.Token) 'Canceled shared budget must fail completion.';Require ($fixture.Events.Count -eq 0) 'Canceled budget must stop metadata reads.'}finally{$cts.Dispose()}}
Case 'missing task children followed by attribute permission failure is rejected' {Setup-Tree;$fixture.Files.Remove('/proc/10/task/10/children');$fixture.Attributes['/proc/10/task/10']=[UnauthorizedAccessException]::new('Fixture attribute access denied.');Expect-Rejection {Capture-LinuxOwnedDescendants $root $records ([Threading.CancellationToken]::None)}}
Case 'shared token cancellation during typed task disappearance prevents further probes' {Setup-Tree;$script:raceCts=[Threading.CancellationTokenSource]::new();try{$fixture.Files['/proc/10/task/10/children']={$script:raceCts.Cancel();throw [IO.FileNotFoundException]::new('Fixture task children disappeared.','/proc/10/task/10/children')};Expect-Rejection {Capture-LinuxOwnedDescendants $root $records $raceCts.Token};Require (-not $fixture.Events.Contains('attributes:/proc/10/task/10')) 'Canceled fallback must not continue to its exact-path probe.'}finally{$raceCts.Dispose()}}
Case 'matching two-level descendant ancestry is captured with no pipeline success output' {
    Setup-Tree;$child=New-FixtureProcess 20;$grandchild=New-FixtureProcess 30
    $fixture.Processes[20]=$child;$fixture.Processes[30]=$grandchild
    $fixture.Files['/proc/20/stat']=New-FixtureStat 20 10 200;$fixture.Files['/proc/30/stat']=New-FixtureStat 30 20 300
    $fixture.Directories['/proc/20/task']=@('/proc/20/task/20');$fixture.Files['/proc/20/task/20/children']='30'
    $fixture.Directories['/proc/30/task']=@()
    $emitted=@(Capture-LinuxOwnedDescendants $root $records ([Threading.CancellationToken]::None))
    Require ($records.Count -eq 2 -and $emitted.Count -eq 0) 'Matching hierarchy must retain both records without success pipeline output.'
    Require ($records[0].Process -eq $child -and $records[0].Identity.StartToken -eq 200 -and $records[1].Process -eq $grandchild -and $records[1].Identity.StartToken -eq 300) 'Each captured record must carry its observed matching start identity.'
}
Case 'latched PID start plus typed missing stat and directory completes stale HasExited false' {
    Reset-Fixture;$p=New-FixtureProcess 20
    Require (-not (Run-Completion ([pscustomobject]@{Process=$p;Identity=[pscustomobject]@{Id=20;ParentId=10;StartToken=200;Zombie=$false}}))) 'A reaped captured process must complete despite a stale managed wrapper.'
    Require (-not $p.HasExited -and $fixture.Events.Contains('read:/proc/20/stat') -and $fixture.Events.Contains('attributes:/proc/20') -and $fixture.PauseCount -eq 0) 'Require the actual stale-false/two-typed-absence timeline with no settling pause.'
}
Case 'latched missing stat followed by exact directory permission failure rejects completion' {Reset-Fixture;$p=New-FixtureProcess 20;$fixture.Attributes['/proc/20']=[UnauthorizedAccessException]::new('Fixture PID-directory permission denied.');Require (Run-Completion ([pscustomobject]@{Process=$p;Identity=[pscustomobject]@{Id=20;ParentId=10;StartToken=200;Zombie=$false}})) 'Permission must not be converted into proven PID absence.'}
Case 'latched missing stat followed by unexpected directory IO failure rejects completion' {Reset-Fixture;$p=New-FixtureProcess 20;$fixture.Attributes['/proc/20']=[IO.IOException]::new('Fixture unexpected PID-directory IO failure.');Require (Run-Completion ([pscustomobject]@{Process=$p;Identity=[pscustomobject]@{Id=20;ParentId=10;StartToken=200;Zombie=$false}})) 'Unexpected IO must not be converted into proven PID absence.'}
Case 'unlatched missing stat rejects completion and performs no PID directory probe' {Reset-Fixture;$p=New-FixtureProcess 20;Require (Run-Completion ([pscustomobject]@{Process=$p;Identity=$null})) 'Unlatched record must not use the proven-absence completion rule.';Require (-not $fixture.Events.Contains('attributes:/proc/20')) 'Unlatched lookup must not enter record-aware fallback.'}
Case 'mismatched captured PID rejects missing-stat fallback before directory probe' {Reset-Fixture;$p=New-FixtureProcess 20;Require (Run-Completion ([pscustomobject]@{Process=$p;Identity=[pscustomobject]@{Id=21;ParentId=10;StartToken=200;Zombie=$false}})) 'Captured PID must equal the retained managed record PID.';Require (-not $fixture.Events.Contains('attributes:/proc/20')) 'Invalid identity must not enter the absence probe.'}
Case 'malformed captured start rejects missing-stat fallback before directory probe' {Reset-Fixture;$p=New-FixtureProcess 20;Require (Run-Completion ([pscustomobject]@{Process=$p;Identity=[pscustomobject]@{Id=20;ParentId=10;StartToken='not-a-start';Zombie=$false}})) 'Captured start must parse as positive UInt64.';Require (-not $fixture.Events.Contains('attributes:/proc/20')) 'Malformed identity must not enter the absence probe.'}
Case 'zero captured start rejects missing-stat fallback before directory probe' {Reset-Fixture;$p=New-FixtureProcess 20;Require (Run-Completion ([pscustomobject]@{Process=$p;Identity=[pscustomobject]@{Id=20;ParentId=10;StartToken=0;Zombie=$false}})) 'Zero start is not a valid captured identity.';Require (-not $fixture.Events.Contains('attributes:/proc/20')) 'Zero identity must not enter the absence probe.'}
Case 'malformed present stat does not enter record-aware missing-directory fallback' {Reset-Fixture;$p=New-FixtureProcess 20;$fixture.Files['/proc/20/stat']=New-FixtureStat 20 10 200 '?';Require (Run-Completion ([pscustomobject]@{Process=$p;Identity=[pscustomobject]@{Id=20;ParentId=10;StartToken=200;Zombie=$false}})) 'Malformed stat is not typed absence.';Require (-not $fixture.Events.Contains('attributes:/proc/20')) 'Malformed present stat must not enter the absence probe.'}
Case 'cancellation after typed exact PID-directory absence still rejects completion' {
    Reset-Fixture;$p=New-FixtureProcess 20;$script:goneCts=[Threading.CancellationTokenSource]::new()
    try{$fixture.Attributes['/proc/20']={$script:goneCts.Cancel();throw [IO.FileNotFoundException]::new('Fixture PID disappeared.','/proc/20')};Require (Run-Completion ([pscustomobject]@{Process=$p;Identity=[pscustomobject]@{Id=20;ParentId=10;StartToken=200;Zombie=$false}}) $goneCts.Token) 'Expired shared budget must not be accepted after its absence probe.';Require ($fixture.PauseCount -eq 0) 'Canceled absence fallback must not add a settling pause.'}finally{$goneCts.Dispose()}
}
Case 'cancellation during missing-stat read rejects before the PID-directory fallback' {
    Reset-Fixture;$p=New-FixtureProcess 20;$script:statCts=[Threading.CancellationTokenSource]::new()
    try{$fixture.Files['/proc/20/stat']={$script:statCts.Cancel();throw [IO.FileNotFoundException]::new('Fixture stat disappeared.','/proc/20/stat')};Require (Run-Completion ([pscustomobject]@{Process=$p;Identity=[pscustomobject]@{Id=20;ParentId=10;StartToken=200;Zombie=$false}}) $statCts.Token) 'Canceled shared budget must reject before entering the directory fallback.';Require ($fixture.Events.Contains('read:/proc/20/stat') -and -not $fixture.Events.Contains('attributes:/proc/20') -and $fixture.PauseCount -eq 0) 'Require the missing-stat cancellation timeline and no later probe or pause.'}finally{$statCts.Dispose()}
}
Case 'retained stat permission failure rejects without probing an absent PID directory' {
    Reset-Fixture;$p=New-FixtureProcess 20;$fixture.Files['/proc/20/stat']=[UnauthorizedAccessException]::new('Fixture stat permission denied.')
    Require (Run-Completion ([pscustomobject]@{Process=$p;Identity=[pscustomobject]@{Id=20;ParentId=10;StartToken=200;Zombie=$false}})) 'Stat permission failure is not typed absence.'
    Require ($fixture.Events.Contains('read:/proc/20/stat') -and -not $fixture.Events.Contains('attributes:/proc/20') -and $fixture.PauseCount -eq 0) 'Permission failure must not enter fallback or add a pause.'
}
$passed=@($rows|Where-Object {$_.passed -eq $true}).Count
if($rows.Count -ne 31 -or $passed -ne $rows.Count){
    foreach($row in $rows){if(-not $row.passed){Write-Error ($row.name+': '+$row.message) -ErrorAction Continue}}
    throw ('Metadata cleanup boundary contracts failed: '+$passed+'/'+$rows.Count+'.')
}
Write-Host ('PASS: '+$passed+' metadata cleanup boundaries; in-memory Process, IO and scheduling seams.')
