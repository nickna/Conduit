[CmdletBinding()]
param([string]$ActorDll,[string]$OutputDirectory,
    [string]$Collector=(Join-Path $PSScriptRoot 'measure-native-container.ps1'))
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if(-not $IsLinux){throw 'Process cleanup lifecycle requires Linux.'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot ('../../artifacts/native-aot/reports/process-cleanup-lifecycle-'+[Guid]::NewGuid().ToString('N'))}
function Require([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Hash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Write-NewJson([string]$Path,$Value){
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Depth 22));$stream=[IO.File]::Open($Path,'CreateNew','Write','Read')
    try{$stream.Write($bytes);$stream.Flush($true)}finally{$stream.Dispose()}
}
function Build-Actor([string]$PrivateRoot,[string]$EvidenceDirectory){
    $source=Join-Path $PSScriptRoot 'fixtures/ProcessCleanupFixture';$work=Join-Path $PrivateRoot 'build';$null=[IO.Directory]::CreateDirectory($work)
    foreach($file in @('ProcessCleanupFixture.csproj','Program.cs')){Copy-Item -LiteralPath (Join-Path $source $file) -Destination $work;$script:report.actorSources[$file]=Hash (Join-Path $source $file)}
    # Copying only the two BCL fixture files isolates all obj/bin and build policy.
    $binary=Join-Path $work 'bin';$dotnet=(Get-Command dotnet -CommandType Application|Select-Object -First 1).Source
    $stdout=Join-Path $EvidenceDirectory 'actor-build.stdout.log';$stderr=Join-Path $EvidenceDirectory 'actor-build.stderr.log'
    $process=[Diagnostics.Process]::new();$started=$false;$joined=$false;$outputTask=$null;$errorTask=$null;$outputFile=$null;$errorFile=$null
    $failures=[Collections.Generic.List[string]]::new();$exitCode=$null;$watch=[Diagnostics.Stopwatch]::StartNew();$workBudget=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(90))
    try{
        $outputFile=[IO.File]::Open($stdout,'CreateNew','Write','Read');$errorFile=[IO.File]::Open($stderr,'CreateNew','Write','Read')
        $start=[Diagnostics.ProcessStartInfo]::new($dotnet);$start.WorkingDirectory=$work;$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
        foreach($argument in @('build','ProcessCleanupFixture.csproj','-c','Release','--output',$binary,'--artifacts-path',(Join-Path $work 'artifacts'),'--tl:off','-m:1','-p:UseSharedCompilation=false','-p:UseAppHost=false')){$start.ArgumentList.Add($argument)}
        foreach($name in @('DATABASE_URL','REDIS_URL','CONDUIT_NATIVE_BENCHMARK_API_KEY','ISSUE1674_PRIVATE_SENTINEL')){$null=$start.Environment.Remove($name)}
        $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT']='1';$start.Environment['DOTNET_NOLOGO']='1';$start.Environment['MSBUILDDISABLENODEREUSE']='1';$start.Environment['DOTNET_CLI_USE_MSBUILD_SERVER']='0'
        $process.StartInfo=$start;Require ($process.Start()) 'Fixture build did not start.';$started=$true
        $outputTask=$process.StandardOutput.BaseStream.CopyToAsync($outputFile);$errorTask=$process.StandardError.BaseStream.CopyToAsync($errorFile)
        $null=$process.WaitForExitAsync($workBudget.Token).GetAwaiter().GetResult();$joined=$true;$exitCode=$process.ExitCode
    }catch{$failures.Add('fixture-build-process')}
    finally{
        $timedOut=$workBudget.IsCancellationRequested;$reap=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(10))
        if($started){
            try{if(-not $process.HasExited){$process.Kill($true)};$null=$process.WaitForExitAsync($reap.Token).GetAwaiter().GetResult();$joined=$true;$exitCode=$process.ExitCode}catch{$failures.Add('fixture-build-reap')}
            foreach($pending in @($outputTask,$errorTask)){if($null -ne $pending){try{$null=$pending.WaitAsync($reap.Token).GetAwaiter().GetResult()}catch{$failures.Add('fixture-build-pipe-join')}}}
        }
        foreach($stream in @($outputFile,$errorFile)){if($null -ne $stream){try{$stream.Flush($true)}catch{$failures.Add('fixture-build-stream-flush')};try{$stream.Dispose()}catch{$failures.Add('fixture-build-stream-dispose')}}}
        if(-not $started -or $joined){$process.Dispose()};$reap.Dispose();$workBudget.Dispose();$watch.Stop()
    }
    $script:report.actorBuild=[ordered]@{workBudgetSeconds=90;reapBudgetSeconds=10;exitCode=$exitCode;elapsedMilliseconds=$watch.Elapsed.TotalMilliseconds;timedOut=$timedOut;childJoined=$joined;
        streamsJoined=($null -ne $outputTask -and $null -ne $errorTask -and $outputTask.IsCompletedSuccessfully -and $errorTask.IsCompletedSuccessfully);failures=$failures.ToArray();stdoutSha256=(Hash $stdout);stderrSha256=(Hash $stderr)}
    Require ($exitCode -eq 0 -and $joined -and $script:report.actorBuild.streamsJoined -and $failures.Count -eq 0) 'Process cleanup lifecycle fixture build failed.'
    foreach($file in $script:report.actorSources.Keys){Require ((Hash (Join-Path $source $file)) -ceq $script:report.actorSources[$file]) 'Fixture source changed during build.'}
    return Join-Path $binary 'ProcessCleanupFixture.dll'
}
function Is-Missing([Exception]$ErrorValue){while($null -ne $ErrorValue){if($ErrorValue -is [IO.FileNotFoundException] -or $ErrorValue -is [IO.DirectoryNotFoundException]){return $true};$ErrorValue=$ErrorValue.InnerException};return $false}
function Read-Identity([int]$Id){
    try{$stat=[IO.File]::ReadAllText('/proc/'+$Id+'/stat')}catch{if(Is-Missing $_.Exception){
        try{$null=[IO.File]::GetAttributes('/proc/'+$Id)}catch{if(Is-Missing $_.Exception){return $null};throw};throw 'PID directory remains without stat metadata.'};throw}
    $open=$stat.IndexOf(' (',[StringComparison]::Ordinal);$close=$stat.LastIndexOf(') ',[StringComparison]::Ordinal)
    [int]$statId=0;[int]$parent=0;[ulong]$start=0
    Require ($open -gt 0 -and $close -gt $open -and [int]::TryParse($stat.Substring(0,$open),[ref]$statId) -and $statId -eq $Id) 'Invalid observed PID identity.'
    $fields=$stat.Substring($close+2).Split(' ',[StringSplitOptions]::RemoveEmptyEntries)
    Require ($fields.Count -gt 19 -and $fields[0] -cin @('R','S','D','Z','T','t','X','x','K','W','P','I') -and [int]::TryParse($fields[1],[ref]$parent) -and $parent -ge 0 -and [ulong]::TryParse($fields[19],[ref]$start) -and $start -gt 0) 'Invalid observed process fields.'
    $result=[ordered]@{id=$Id;parentId=$parent;startToken=$start.ToString();state=$fields[0];stdout=$null;stderr=$null}
    if($fields[0] -cne 'Z'){$result.stdout=[IO.FileInfo]::new('/proc/'+$Id+'/fd/1').LinkTarget;$result.stderr=[IO.FileInfo]::new('/proc/'+$Id+'/fd/2').LinkTarget}
    return $result
}
function Observe($Expected){
    $now=Read-Identity ([int]$Expected.id)
    if($null -eq $now){return [ordered]@{id=$Expected.id;startToken=$Expected.startToken;completion='typed-absence';identity=$null}}
    Require ($now.startToken -ceq [string]$Expected.startToken) 'Observed identity was reused; refusing ownership.'
    return [ordered]@{id=$Expected.id;startToken=$Expected.startToken;completion=$(if($now.state -ceq 'Z'){'matching-zombie'}else{'live'});identity=$now}
}
function Wait-Completion($Expected,[Threading.CancellationToken]$Token){
    while($true){$Token.ThrowIfCancellationRequested();$observed=Observe $Expected;if($observed.completion -cne 'live'){return $observed};Start-Sleep -Milliseconds 10}
}
function Read-Ready([string]$Role){
    $path=Join-Path $script:scenario.directory ($Role+'.json')
    if(-not [IO.File]::Exists($path)){return $null}
    $info=Get-Content $path -Raw|ConvertFrom-Json -AsHashtable -DateKind String
    Require ($info.kind -ceq 'issue1674-actor-ready' -and $info.role -ceq $Role -and $info.privateOverridePresent -and -not $info.benchmarkKeyPresent) 'Actor identity/private environment contract differs.'
    $observed=Observe $info.identity;Require ($observed.completion -ceq 'live') 'Actor exited before identity latch.'
    Require ($observed.identity.stdout -ceq $info.identity.stdout -and $observed.identity.stderr -ceq $info.identity.stderr -and $observed.identity.stdout -cmatch '^pipe:\[[0-9]+\]$' -and $observed.identity.stderr -cmatch '^pipe:\[[0-9]+\]$') 'Actor inherited pipe identity differs.'
    return $info
}
function Command-Actor([string]$Role,[string]$Command,[Threading.CancellationToken]$Token){
    $Token.ThrowIfCancellationRequested();$socket=[Net.Sockets.Socket]::new([Net.Sockets.AddressFamily]::Unix,[Net.Sockets.SocketType]::Stream,[Net.Sockets.ProtocolType]::Unspecified)
    $stream=$null;$reader=$null;$writer=$null;$primary=$null;$disposeFailures=[Collections.Generic.List[string]]::new()
    try{
        $endpoint=[Net.Sockets.UnixDomainSocketEndPoint]::new((Join-Path $script:scenario.directory ($Role+'.sock')))
        $null=$socket.ConnectAsync($endpoint,$Token).AsTask().GetAwaiter().GetResult()
        $stream=[Net.Sockets.NetworkStream]::new($socket,$false)
        $reader=[IO.StreamReader]::new($stream,[Text.UTF8Encoding]::new($false,$true),$false,256,$true)
        $writer=[IO.StreamWriter]::new($stream,[Text.UTF8Encoding]::new($false,$true),256,$true);$writer.AutoFlush=$true
        $null=$writer.WriteLineAsync([ReadOnlyMemory[char]]::new($Command.ToCharArray()),$Token).GetAwaiter().GetResult()
        $answer=$reader.ReadLineAsync($Token).AsTask().GetAwaiter().GetResult()
        $expected=switch($Command){'ping'{'ready'};'fork'{'forked'};'exit'{'exiting'};default{throw 'Unknown actor control command.'}}
        Require ($answer -ceq $expected) 'Actor handshake did not acknowledge the exact command.'
    }catch{$primary=$_}
    finally{foreach($resource in @($writer,$reader,$stream,$socket)){if($null -ne $resource){try{$resource.Dispose()}catch{$disposeFailures.Add($_.Exception.GetType().FullName)}}}}
    if($null -ne $primary){throw $primary};if($disposeFailures.Count -gt 0){throw 'Actor control resource cleanup failed.'}
}
function Latch-Initial([Threading.CancellationToken]$Token){
    $root=Read-Ready 'root';$child=Read-Ready 'child';if($null -eq $root -or $null -eq $child){return $false}
    Require ($root.identity.parentId -eq $PID -and $child.identity.parentId -eq $root.identity.id -and [ulong]$child.identity.startToken -ge [ulong]$root.identity.startToken) 'Initial child ancestry not exact.'
    Require ($root.identity.stdout -ceq $child.identity.stdout -and $root.identity.stderr -ceq $child.identity.stderr) 'Child does not hold both root output pipes.'
    Command-Actor 'root' 'ping' $Token;Command-Actor 'child' 'ping' $Token
    $script:scenario.identities['root']=$root.identity;$script:scenario.identities['child']=$child.identity
    $script:scenario.events.Add([ordered]@{stage='child-identity-latched';ticks=[Diagnostics.Stopwatch]::GetTimestamp();root=$root.identity;child=$child.identity})
    return $true
}
function Invoke-Issue1674SnapshotLatch([Diagnostics.Process]$RootProcess,[Collections.Generic.List[object]]$Records,[Threading.CancellationToken]$Token){
    Require ($script:scenario.name -ceq 'late-fork-after-snapshot' -and $script:scenario.armed -and -not $script:scenario.snapshotSeen) 'Unexpected or duplicate snapshot seam.'
    Require ($RootProcess.Id -eq $script:scenario.identities.root.id -and $Records.Count -eq 1 -and $null -ne $Records[0].Identity -and $Records[0].Identity.Id -eq $script:scenario.identities.child.id -and [string]$Records[0].Identity.StartToken -ceq $script:scenario.identities.child.startToken) 'Actual helper snapshot did not retain exactly the latched child.'
    $script:scenario.snapshotSeen=$true
    $script:scenario.events.Add([ordered]@{stage='actual-helper-descendant-snapshot-complete';ticks=[Diagnostics.Stopwatch]::GetTimestamp();captured=@($Records|ForEach-Object {[ordered]@{id=$_.Identity.Id;parentId=$_.Identity.ParentId;startToken=[string]$_.Identity.StartToken}})})
    Command-Actor 'child' 'fork' $Token
    $grandchild=$null
    while($null -eq $grandchild){$Token.ThrowIfCancellationRequested();$grandchild=Read-Ready 'grandchild';if($null -eq $grandchild){Start-Sleep -Milliseconds 10}}
    Require ($grandchild.identity.parentId -eq $script:scenario.identities.child.id -and [ulong]$grandchild.identity.startToken -ge [ulong]$script:scenario.identities.child.startToken -and $grandchild.identity.stdout -ceq $script:scenario.identities.root.stdout -and $grandchild.identity.stderr -ceq $script:scenario.identities.root.stderr) 'Late grandchild ancestry/held pipes differ.'
    Command-Actor 'grandchild' 'ping' $Token;$script:scenario.identities['grandchild']=$grandchild.identity
    $script:scenario.events.Add([ordered]@{stage='post-snapshot-grandchild-latched';ticks=[Diagnostics.Stopwatch]::GetTimestamp();grandchild=$grandchild.identity})
    Command-Actor 'child' 'exit' $Token;$childCompletion=Wait-Completion $script:scenario.identities.child $Token
    $reparented=Observe $grandchild.identity
    Require ($reparented.completion -ceq 'live' -and $reparented.identity.parentId -ne $script:scenario.identities.child.id -and $reparented.identity.parentId -ne $script:scenario.identities.root.id) 'Late grandchild was not reparented outside the root snapshot.'
    $script:scenario.events.Add([ordered]@{stage='late-grandchild-reparented-before-helper-termination';ticks=[Diagnostics.Stopwatch]::GetTimestamp();child=$childCompletion;grandchild=$reparented})
    $script:scenario.lateTimelineEstablished=$true
}
$source=[IO.File]::ReadAllText($Collector,[Text.UTF8Encoding]::new($false,$true));$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors)
$helpers=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Invoke-BoundedProcess'},$true))
Require (@($errors).Count -eq 0 -and $helpers.Count -eq 1) 'Exact helper extraction failed.'
$original=$helpers[0].Extent.Text
$anchor='Capture-LinuxOwnedDescendants $process $linuxDescendants $reap.Token'
$calls=@($helpers[0].Body.FindAll({param($node)$node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Capture-LinuxOwnedDescendants'},$true))
Require ($calls.Count -eq 1 -and $calls[0].Extent.Text -ceq $anchor -and $original.Split($anchor,[StringSplitOptions]::None).Length -eq 2) 'Exactly one actual cleanup snapshot AST call is required.'
$late=$original.Replace($anchor,($anchor+'; Invoke-Issue1674SnapshotLatch $process $linuxDescendants $reap.Token'))
$output=[IO.Directory]::CreateDirectory($OutputDirectory).FullName
$script:report=[ordered]@{kind='native-process-cleanup-lifecycle';collectorSha256=(Hash $Collector);harnessSha256=(Hash $PSCommandPath);actorDllSha256=$null;actorSources=[ordered]@{};actorBuild=$null;setupFailure=$null;privateDirectoryAbsent=$false;
    osDescription=[Runtime.InteropServices.RuntimeInformation]::OSDescription;runtime=[Runtime.InteropServices.RuntimeInformation]::FrameworkDescription;powerShellVersion=$PSVersionTable.PSVersion.ToString();kernel=[IO.File]::ReadAllText('/proc/sys/kernel/osrelease').Trim();
    commandBudgetSeconds=5;sharedCleanupBudgetSeconds=5;privateOutputPrinted=$false;arbitraryDelayGate=$false;
    sourceSeam=[ordered]@{rootExit='Unmodified actual helper; existing PollAction used only to synchronize actors.';lateFork='Exactly one after-real-snapshot callback inserted into extracted helper; actual capture, Kill, deadlines, reads, disposal and classification remain unchanged.';anchor=$anchor;astStart=$calls[0].Extent.StartOffset;astEnd=$calls[0].Extent.EndOffset;injected=$anchor+'; Invoke-Issue1674SnapshotLatch $process $linuxDescendants $reap.Token'};
    cases=@();cleanupFailures=@();originalHelperSha256=$null;lateFixtureHelperSha256=$null;sourceStable=$false}
Write-NewJson (Join-Path $output 'source-seam.json') $report.sourceSeam
[IO.File]::WriteAllText((Join-Path $output 'original-helper.ps1'),$original,[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $output 'late-fixture-helper.ps1'),$late,[Text.UTF8Encoding]::new($false))
$report.originalHelperSha256=Hash (Join-Path $output 'original-helper.ps1');$report.lateFixtureHelperSha256=Hash (Join-Path $output 'late-fixture-helper.ps1')
$privateTop='/tmp/issue1674-'+[Guid]::NewGuid().ToString('N');$null=[IO.Directory]::CreateDirectory($privateTop);[IO.File]::SetUnixFileMode($privateTop,[IO.UnixFileMode]'UserRead,UserWrite,UserExecute')
$inheritedKey='inherited-secret-'+[Guid]::NewGuid().ToString('N');$privateValue='fixture-output-'+[Guid]::NewGuid().ToString('N')
$previousKey=[Environment]::GetEnvironmentVariable('CONDUIT_NATIVE_BENCHMARK_API_KEY');$env:CONDUIT_NATIVE_BENCHMARK_API_KEY=$inheritedKey
try{
    if(-not $ActorDll){$ActorDll=Build-Actor $privateTop $output}
    $report.actorDllSha256=Hash $ActorDll
    foreach($name in @('root-exits-after-child-latch','late-fork-after-snapshot')){
        $caseDirectory=Join-Path $privateTop $name;$null=[IO.Directory]::CreateDirectory($caseDirectory);[IO.File]::SetUnixFileMode($caseDirectory,[IO.UnixFileMode]'UserRead,UserWrite,UserExecute')
        $script:scenario=[ordered]@{name=$name;directory=$caseDirectory;armed=$false;snapshotSeen=$false;lateTimelineEstablished=$false;identities=[ordered]@{};events=[Collections.Generic.List[object]]::new()}
        $row=[ordered]@{name=$name;helperFailure=$null;helperFailureType=$null;helperElapsedMilliseconds=$null;events=$null;beforeOuterCleanup=@();foreignBefore=$null;foreignAfter=$null;completionOrClosedFailure=$false;timelineEstablished=$false;cleanup=@();fixtureFailure=$null}
        $report.cases+=,$row;$foreign=[Diagnostics.Process]::new();$foreignStarted=$false;$foreignOut=$null;$foreignErr=$null;$foreignIdentity=$null
        $cleanup=[Collections.Generic.List[object]]::new();$watch=[Diagnostics.Stopwatch]::new()
        try{
            $start=[Diagnostics.ProcessStartInfo]::new('dotnet');$start.UseShellExecute=$false;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
            foreach($arg in @($ActorDll,'foreign',$caseDirectory)){$start.ArgumentList.Add($arg)}
            $null=$start.Environment.Remove('CONDUIT_NATIVE_BENCHMARK_API_KEY');$start.Environment['ISSUE1674_PRIVATE_SENTINEL']=$privateValue
            $foreign.StartInfo=$start;Require ($foreign.Start()) 'Foreign control actor did not start.';$foreignStarted=$true
            $foreignOut=$foreign.StandardOutput.ReadToEndAsync();$foreignErr=$foreign.StandardError.ReadToEndAsync()
            $readyBudget=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(3))
            try{while($null -eq $foreignIdentity){$readyBudget.Token.ThrowIfCancellationRequested();$ready=Read-Ready 'foreign';if($null -ne $ready){$foreignIdentity=$ready.identity}else{Start-Sleep -Milliseconds 10}};Command-Actor 'foreign' 'ping' $readyBudget.Token}finally{$readyBudget.Dispose()}
            $row.foreignBefore=$foreignIdentity
            Require ($foreignIdentity.id -eq $foreign.Id -and $foreignIdentity.parentId -eq $PID) 'Foreign control actor was not the direct owned fixture child.'
            if($name -ceq 'root-exits-after-child-latch'){. ([scriptblock]::Create($original))}else{. ([scriptblock]::Create($late))}
            $poll={param([int]$Remaining)
                if($script:scenario.armed){return}
                if(-not (Latch-Initial $deadline.Token)){return}
                $script:scenario.armed=$true
                if($script:scenario.name -ceq 'root-exits-after-child-latch'){
                    Command-Actor 'root' 'exit' $deadline.Token;$rootDone=Wait-Completion $script:scenario.identities.root $deadline.Token
                    $childNow=Observe $script:scenario.identities.child;Require ($childNow.completion -ceq 'live') 'Child did not remain live after root exit.'
                    $script:scenario.events.Add([ordered]@{stage='root-exited-with-child-holding-both-output-pipes';ticks=[Diagnostics.Stopwatch]::GetTimestamp();root=$rootDone;child=$childNow})
                }
            }
            $watch.Start()
            try{$unexpected=Invoke-BoundedProcess 'dotnet' @($ActorDll,'root',$caseDirectory) 5 @{ISSUE1674_PRIVATE_SENTINEL=$privateValue} $poll;$row.fixtureFailure='Helper unexpectedly returned a result despite held output pipes.'}
            catch{
                $row.helperFailureType=$_.Exception.GetType().FullName
                if($_.Exception.Message -cin @('Private command failed. [stage=command-deadline]','Private command failed and its cleanup failed. [stage=command-deadline]')){$row.helperFailure=$_.Exception.Message}
                else{$row.fixtureFailure=[ordered]@{reason='unexpected-helper-classification';exceptionType=$_.Exception.GetType().FullName;line=$_.InvocationInfo.ScriptLineNumber}}
            }
            finally{$watch.Stop();$row.helperElapsedMilliseconds=$watch.Elapsed.TotalMilliseconds}
            Require ($script:scenario.armed -and ($name -cne 'late-fork-after-snapshot' -or ($script:scenario.snapshotSeen -and $script:scenario.lateTimelineEstablished))) 'Required controlled timeline was not reached.'
            Require ($watch.Elapsed.TotalSeconds -le 12) 'Helper exceeded five-second work plus five-second cleanup and two-second observation allowance.'
            $row.timelineEstablished=$true
            foreach($role in $script:scenario.identities.Keys){$row.beforeOuterCleanup+=,(Observe $script:scenario.identities[$role])}
            $allDone=@($row.beforeOuterCleanup|Where-Object completion -eq 'live').Count -eq 0
            $closed=$row.helperFailure -ceq 'Private command failed and its cleanup failed. [stage=command-deadline]'
            Require ($row.helperFailure -cin @('Private command failed. [stage=command-deadline]','Private command failed and its cleanup failed. [stage=command-deadline]')) 'Unexpected public failure classification.'
            $row.completionOrClosedFailure=$allDone -or $closed
            $row.foreignAfter=Observe $foreignIdentity;Require ($row.foreignAfter.completion -ceq 'live') 'Foreign control actor was terminated.'
            $foreignPing=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(1));try{Command-Actor 'foreign' 'ping' $foreignPing.Token}finally{$foreignPing.Dispose()}
        }catch{if($null -eq $row.fixtureFailure){$row.fixtureFailure=[ordered]@{reason='controlled-fixture-validation-failed';exceptionType=$_.Exception.GetType().FullName;line=$_.InvocationInfo.ScriptLineNumber}}}
        finally{
            # Each private control socket is cooperatively exited independently. No PID is signaled.
            foreach($role in @('grandchild','child','root','foreign')){
                $budget=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(2))
                try{
                    $identity=$null
                    if($role -ceq 'foreign'){$identity=$foreignIdentity}elseif($script:scenario.identities.Contains($role)){$identity=$script:scenario.identities[$role]}
                    if($null -eq $identity -and [IO.File]::Exists((Join-Path $caseDirectory ($role+'.json')))){$metadata=Get-Content (Join-Path $caseDirectory ($role+'.json')) -Raw|ConvertFrom-Json -AsHashtable -DateKind String;$identity=$metadata.identity}
                    if($null -ne $identity){$observed=Observe $identity;if($observed.completion -ceq 'live'){Command-Actor $role 'exit' $budget.Token};$done=Wait-Completion $identity $budget.Token;$cleanup.Add([ordered]@{role=$role;completed=$true;result=$done})}
                }catch{$cleanup.Add([ordered]@{role=$role;completed=$false;failureType=$_.Exception.GetType().FullName})}finally{$budget.Dispose()}
            }
            if($foreignStarted){
                $join=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(2))
                try{$null=$foreign.WaitForExitAsync($join.Token).GetAwaiter().GetResult();Require ($foreign.ExitCode -eq 0) 'Foreign actor did not exit normally.'}catch{$cleanup.Add([ordered]@{role='foreign-process-join';completed=$false;failureType=$_.Exception.GetType().FullName})}
                foreach($pending in @($foreignOut,$foreignErr)){if($null -ne $pending){try{$privateText=$pending.WaitAsync($join.Token).GetAwaiter().GetResult();$privateText=$null}catch{$cleanup.Add([ordered]@{role='foreign-private-pipe-join';completed=$false;failureType=$_.Exception.GetType().FullName})}}}
                $join.Dispose()
            }
            $foreign.Dispose();$row.events=$script:scenario.events.ToArray();$row.cleanup=$cleanup.ToArray()
            if(@($cleanup|Where-Object completed -eq $false).Count -gt 0){$report.cleanupFailures+=,$name}
        }
    }
}catch{$report.setupFailure=[ordered]@{reason='lifecycle-fixture-setup-failed';exceptionType=$_.Exception.GetType().FullName;line=$_.InvocationInfo.ScriptLineNumber}}
finally{
    [Environment]::SetEnvironmentVariable('CONDUIT_NATIVE_BENCHMARK_API_KEY',$previousKey)
    $report.sourceStable=(Hash $Collector) -ceq $report.collectorSha256
    # Actor identity/control files are private. Retain only sanitized observations above.
    try{
        Require ([IO.Path]::GetFullPath($privateTop) -ceq $privateTop -and $privateTop -cmatch '^/tmp/issue1674-[a-f0-9]{32}$' -and ([IO.File]::GetAttributes($privateTop) -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Private fixture cleanup target differs.'
        Remove-Item -LiteralPath $privateTop -Recurse -Force
        try{$null=[IO.File]::GetAttributes($privateTop);throw 'Private fixture directory remains.'}catch{if(-not (Is-Missing $_.Exception)){throw};$report.privateDirectoryAbsent=$true}
    }catch{$report.cleanupFailures+=,'private-directory-removal'}
    $publicText=$report|ConvertTo-Json -Depth 22
    Require (-not $publicText.Contains($privateValue) -and -not $publicText.Contains($inheritedKey)) 'Private fixture sentinel reached public evidence.'
    Write-NewJson (Join-Path $output 'timeline-results.json') $report
}
$valid=@($report.cases|Where-Object {$_.timelineEstablished -and $null -eq $_.fixtureFailure}).Count -eq 2 -and $report.cleanupFailures.Count -eq 0 -and $report.sourceStable -and $report.privateDirectoryAbsent -and $null -eq $report.setupFailure
$accepted=@($report.cases|Where-Object completionOrClosedFailure -eq $true).Count -eq 2
if(-not $valid -or -not $accepted){throw 'Process cleanup lifecycle contract failed.'}
Write-Output 'PASS: 2 process cleanup lifecycle contracts; synchronized root exit and late fork, private output, foreign identity and bounded cooperative teardown.'
