# Isolated synthetic PostgreSQL transport sweep. No application ledger or production capacity claim.
[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$BenchmarkDll,
 [Parameter(Mandatory)][string]$OutputDirectory,
 [Parameter(Mandatory)][ValidateSet('X64','Arm64')][string]$Architecture,
 [ValidateRange(3,6)][int]$Repeats=5
)
$ErrorActionPreference='Stop'
if(!$IsLinux -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() -cne $Architecture){throw 'Require matching native Linux hardware'}
$BenchmarkDll=(Resolve-Path -LiteralPath $BenchmarkDll).Path
if((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count){throw 'Require a fresh evidence directory'}
New-Item -ItemType Directory -Force -Path $OutputDirectory|Out-Null
$out=(Resolve-Path -LiteralPath $OutputDirectory).Path
$task="conduit-phase3-queue-$([Guid]::NewGuid().ToString('N').Substring(0,12))"
$container="$task-postgres"
$containerId=$null;$networkId=$null;$failure=$null
$latencyEvidence=[Collections.Generic.List[object]]::new()
$script:QueueCommandNumber=0
$commands=[Collections.Generic.List[object]]::new()
$image='postgres:16.15@sha256:f9a7b9ebf6f56201f949db745e47bc423c0f0f8294bafe991cc10bf74b029657'
function Invoke-Checked([string]$Program,[string[]]$Arguments){
 $script:QueueCommandNumber++
 $stem='command-{0:D4}' -f $script:QueueCommandNumber
 $stderrPath=Join-Path $out "$stem.stderr.log"
 # Pull progress and CLI diagnostics must never become a Docker object ID/JSON.
 $value=(& $Program @Arguments 2> $stderrPath) -join "`n"
 $code=$LASTEXITCODE
 $value|Set-Content (Join-Path $out "$stem.stdout.log") -Encoding utf8NoBOM
 $commands.Add([ordered]@{stem=$stem;program=$Program;arguments=$Arguments;exitCode=$code})
 if($code -ne 0){throw "Command failed: $Program $($Arguments[0]); inspect $stem.stderr.log"}
 return $value
}
function Get-OwnedContainer {
 $identity=@((Invoke-Checked docker @('inspect',$containerId))|ConvertFrom-Json)[0]
 if($identity.Id -cne $containerId -or $identity.Name -cne "/$container" -or
    $identity.Config.Labels.'conduit.task' -cne $task){throw 'Fixture ownership changed'}
 return $identity
}
try {
 # State.Pid is a host PID only for a local daemon. Never enter a local namespace
 # using an ID supplied by a remote Docker endpoint or a forwarded VM context.
 if(($env:DOCKER_HOST -and $env:DOCKER_HOST -cne 'unix:///var/run/docker.sock') -or
    ($env:DOCKER_CONTEXT -and $env:DOCKER_CONTEXT -cne 'default')){throw 'Require the local default Docker daemon'}
 if((Invoke-Checked docker @('context','show')) -cne 'default'){throw 'Require the local default Docker context'}
 $context=@((Invoke-Checked docker @('context','inspect','default'))|ConvertFrom-Json)[0]
 if($context.Endpoints.docker.Host -cne 'unix:///var/run/docker.sock'){throw 'Require the local Docker Unix socket'}
 $server=(Invoke-Checked docker @('info','--format','{{json .}}'))|ConvertFrom-Json
 if($server.Name -cne [Environment]::MachineName){throw 'Docker daemon and qualification host differ'}
 $hostNamespace=Invoke-Checked sudo @('-n','readlink','/proc/self/ns/net')
 $networkId=Invoke-Checked docker @('network','create','--label',"conduit.task=$task",$task)
 if($networkId -cnotmatch '^[a-f0-9]{64}$'){throw 'Invalid owned network ID'}
 $containerId=Invoke-Checked docker @('create','--name',$container,'--label',"conduit.task=$task",'--network',$task,
  '--publish','127.0.0.1:15486:5432','--env','POSTGRES_USER=phase3','--env','POSTGRES_PASSWORD=isolated-phase3-fixture','--env','POSTGRES_DB=postgres',$image)
 if($containerId -cnotmatch '^[a-f0-9]{64}$'){throw 'Invalid owned container ID'}
 [void](Invoke-Checked docker @('start',$containerId))
 $deadline=[DateTime]::UtcNow.AddSeconds(60)
 while($true){
  & docker exec $containerId pg_isready -h 127.0.0.1 -U phase3 -d postgres *> $null
  if($LASTEXITCODE -eq 0){break}
  if([DateTime]::UtcNow -gt $deadline){throw 'Private PostgreSQL did not become ready'}
  Start-Sleep -Milliseconds 500
 }
 $identity=Get-OwnedContainer
 $identity|ConvertTo-Json -Depth 30|Set-Content "$out/postgres-container.json" -Encoding utf8NoBOM
 Invoke-Checked docker @('exec',$containerId,'psql','-X','-U','phase3','-d','postgres','-Atc','SELECT version()')|Set-Content "$out/postgres-version.txt" -Encoding utf8NoBOM
 # Prove the actual durable transport/Handled witness on a tiny fresh database
 # before attempting the full capacity/latency matrix. This is not timing data.
 & (Join-Path $PSScriptRoot 'run-queue-matrix.ps1') -BenchmarkDll $BenchmarkDll `
  -AdminConnection 'Host=127.0.0.1;Port=15486;Username=phase3;Password=isolated-phase3-fixture;Database=postgres;Pooling=true' `
  -OutputDirectory "$out/preflight" -LatencyLabel 'functional preflight, no injected latency' `
  -Batches @(10) -HandlerCostMs @(0) -Repeats 1 -Messages 2 -Warmup 2 -TimeoutSeconds 30
 if($LASTEXITCODE -ne 0){throw 'Real PostgreSQL transport preflight failed'}
 # Enter only this labelled container's network namespace. The host filesystem
 # supplies tc; no host network interface or existing container is reconfigured.
 [void](Invoke-Checked sudo @('-n','tc','-V'))
 [void](Invoke-Checked sudo @('-n','nsenter','--version'))
 foreach($delayMs in @(0,2,10)){
  $identity=Get-OwnedContainer
  $fixturePid=[int]$identity.State.Pid
  if(!$identity.State.Running -or $fixturePid -le 1){throw 'Private PostgreSQL is not running'}
  $namespace="/proc/$fixturePid/ns/net"
  $before=Invoke-Checked sudo @('-n','readlink',$namespace)
  if($before -cnotmatch '^net:\[[0-9]+\]$' -or $before -ceq $hostNamespace){throw 'Require a distinct owned container network namespace'}
  if($delayMs -gt 0){
   [void](Invoke-Checked sudo @('-n','nsenter',"--net=$namespace",'tc','qdisc','replace','dev','eth0','root','netem','delay',"${delayMs}ms"))
  }
  $qdisc=Invoke-Checked sudo @('-n','nsenter',"--net=$namespace",'tc','-s','-j','qdisc','show','dev','eth0')
  if($delayMs -gt 0 -and @($qdisc|ConvertFrom-Json|Where-Object{$_.kind -ceq 'netem'}).Count -ne 1){throw 'Owned PostgreSQL netem was not installed'}
  if((Get-OwnedContainer).State.Pid -ne $fixturePid -or (Invoke-Checked sudo @('-n','readlink',$namespace)) -cne $before){throw 'Fixture network namespace changed'}
  $qdisc|Set-Content "$out/netem-${delayMs}ms-before.json" -Encoding utf8NoBOM
  & (Join-Path $PSScriptRoot 'run-queue-matrix.ps1') -BenchmarkDll $BenchmarkDll `
   -AdminConnection 'Host=127.0.0.1;Port=15486;Username=phase3;Password=isolated-phase3-fixture;Database=postgres;Pooling=true' `
   -OutputDirectory "$out/delay-${delayMs}ms" -LatencyLabel "synthetic PostgreSQL egress delay ${delayMs}ms; actual SELECT1 RTT retained" -Repeats $Repeats
  if($LASTEXITCODE -ne 0){throw 'Synthetic queue matrix failed'}
  if((Get-OwnedContainer).State.Pid -ne $fixturePid){throw 'Fixture restarted during queue measurements'}
  $afterQdisc=Invoke-Checked sudo @('-n','nsenter',"--net=$namespace",'tc','-s','-j','qdisc','show','dev','eth0')
  $afterQdisc|Set-Content "$out/netem-${delayMs}ms-after.json" -Encoding utf8NoBOM
  $matrix=Get-Content -LiteralPath "$out/delay-${delayMs}ms/matrix.json" -Raw|ConvertFrom-Json
  if($matrix.passed -cne $true -or !$matrix.cases.Count){throw 'Queue matrix did not pass'}
  $rttMedians=@($matrix.cases|ForEach-Object{[double]$_.summary.SqlRttMedianMs})
  if(@($rttMedians|Where-Object{![double]::IsFinite($_) -or $_ -le 0 -or ($delayMs -gt 0 -and $_ -lt $delayMs*0.8)}).Count){throw 'Actual SQL RTT does not support the injected delay label'}
  if($delayMs -gt 0){
   $netem=@($afterQdisc|ConvertFrom-Json|Where-Object{$_.kind -ceq 'netem'})
   if($netem.Count -ne 1 -or [long]$netem[0].packets -le 0){throw 'No PostgreSQL traffic observed through owned netem qdisc'}
  }
  $latencyEvidence.Add([ordered]@{postgresEgressDelayMs=$delayMs;sqlRttMediansMs=$rttMedians;matrixPassed=$matrix.passed;netemTrafficVerified=($delayMs -gt 0)})
 }
}catch{$failure=$_.ToString();throw}finally{
 $cleanupErrors=[Collections.Generic.List[string]]::new()
 if($containerId){try{
  [void](Get-OwnedContainer)
  Invoke-Checked docker @('logs',$containerId)|Set-Content "$out/postgres.log" -Encoding utf8NoBOM
  [void](Invoke-Checked docker @('rm','--force','--volumes',$containerId))
 }catch{$cleanupErrors.Add($_.ToString())}}
 if($networkId){try{
  $network=@((Invoke-Checked docker @('network','inspect',$networkId))|ConvertFrom-Json)[0]
  if($network.Id -cne $networkId -or $network.Name -cne $task -or $network.Labels.'conduit.task' -cne $task){throw 'Network ownership changed'}
  [void](Invoke-Checked docker @('network','rm',$networkId))
 }catch{$cleanupErrors.Add($_.ToString())}}
 [ordered]@{schemaVersion=1;failure=$failure;cleanupErrors=$cleanupErrors.ToArray();architecture=$Architecture;postgresImage=$image;
  benchmarkDll=$BenchmarkDll;benchmarkSha256=(Get-FileHash -LiteralPath $BenchmarkDll -Algorithm SHA256).Hash.ToLowerInvariant();
  latencyEvidence=$latencyEvidence.ToArray();
  commands=$commands.ToArray();
  scope='Synthetic transport/inbox only. Netem changes the owned PostgreSQL namespace egress; retain actual RTT and qdisc evidence. Handler delays are simulated work. No real ledger debit, production topology, batching-policy or concurrency change.';
  performanceAcceptanceGranted=$false;productionPolicyChanged=$false}|ConvertTo-Json -Depth 5|Set-Content "$out/queue-method.json" -Encoding utf8NoBOM
 if(!$failure -and $cleanupErrors.Count){throw ($cleanupErrors -join '; ')}
}
