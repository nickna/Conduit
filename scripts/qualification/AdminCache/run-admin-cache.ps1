[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConfigurationPath,
      [Parameter(Mandatory)][string]$OutputDirectory,
      [ValidateSet('prepare','measure')][string]$Stage='measure')
$ErrorActionPreference='Stop'
$configuration=Get-Content -Raw -LiteralPath $ConfigurationPath|ConvertFrom-Json
if(!$IsLinux -or $configuration.schemaVersion -ne 1 -or $configuration.profile -cne 'admin-cache'){throw 'Require Linux admin-cache configuration'}
if($configuration.runs -lt 1 -or $configuration.runs -gt 6 -or (!$configuration.functionalSmoke -and $configuration.runs -lt 3)){throw 'Invalid repeated-run inventory'}
if([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() -cne $configuration.architecture){throw 'Native architecture mismatch'}
if((Test-Path $OutputDirectory) -and @(Get-ChildItem $OutputDirectory -Force).Count){throw 'Fresh evidence directory required'}
New-Item -ItemType Directory -Force -Path $OutputDirectory|Out-Null
$out=(Resolve-Path $OutputDirectory).Path
$gcOverrides=@(Get-ChildItem Env:|Where-Object{$_.Name -match '^(DOTNET|COMPlus)_(GC|gc|Heap|heap)'})
if($gcOverrides.Count){$gcOverrides|Select-Object Name,Value|ConvertTo-Json -AsArray|Set-Content "$out/external-gc-overrides.json" -Encoding utf8NoBOM;throw 'External GC overrides are not permitted'}
$cohorts=@($configuration.cohorts)
if($cohorts.Count -ne 3 -or (@($cohorts.id|Sort-Object)-join ',') -cne 'candidate,fixed,legacy'){throw 'Require all three explicit source cohorts'}
foreach($cohort in $cohorts){
 foreach($path in @($cohort.admin,$cohort.dotnet,$cohort.migrator,$cohort.seedProbe,$cohort.cache,$cohort.collector)){if(!(Test-Path $path -PathType Leaf)){throw "Missing input: $path"}}
}
Copy-Item $PSCommandPath "$out/run-admin-cache.source.ps1"
Copy-Item $ConfigurationPath "$out/configuration.source.json"
$bindings=[ordered]@{schemaVersion=1;stage=$Stage;nativeArchitecture=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString();
 osRelease=@(Get-Content /etc/os-release);kernel=(& uname -a);glibc=(& ldd --version|Select-Object -First 1);
 cpu=@(Get-Content /proc/cpuinfo|Select-Object -First 35);processorCount=[Environment]::ProcessorCount;lscpu=@(& lscpu);cgroup=@(Get-Content /proc/self/cgroup);cgroupCpuMax=$(if(Test-Path /sys/fs/cgroup/cpu.max){Get-Content /sys/fs/cgroup/cpu.max});cgroupMemoryMax=$(if(Test-Path /sys/fs/cgroup/memory.max){Get-Content /sys/fs/cgroup/memory.max});externalGcOverrides=@();scope='Same native VM, fixed .NET10 workload client/server observer/micro observer; host-published managed applications, not production image capacity.';
 files=@(foreach($path in @($configuration.observer,$configuration.microObserver,$configuration.clientDotnet,$PSCommandPath)+@($cohorts|ForEach-Object{@($_.admin,$_.cache,$_.collector,$_.seedProbe)})){
  [ordered]@{path=$path;sha256=(Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()}})}
$bindings|ConvertTo-Json -Depth 8|Set-Content "$out/bindings.json" -Encoding utf8NoBOM
Remove-Item Env:DOTNET_STARTUP_HOOKS -ErrorAction SilentlyContinue
$env:ASPNETCORE_ENVIRONMENT='Production';$env:ASPNETCORE_URLS='http://127.0.0.1:18081'
$env:CONDUIT_MASTER_KEY='phase2-fixture-master-key-32-bytes-only'
$env:CONDUIT_API_TO_API_BACKEND_AUTH_KEY='phase2-fixture-backend-key-32-bytes-only'
$env:CONDUIT_NATIVE_BENCHMARK_API_KEY=$env:CONDUIT_API_TO_API_BACKEND_AUTH_KEY
$env:CONDUIT_NATIVE_PROVIDER_URL='http://127.0.0.1:18083'
$env:CONDUIT_MIGRATION_MODE='Wait';$env:CONDUIT_MIGRATION_WAIT_TIMEOUT_SECONDS='30'
$env:ConduitLLM__Messaging__Backend='Wolverine';$env:ConduitLLM__Messaging__Wolverine__AutoProvision='false'
$env:CONDUIT_MEDIA_STORAGE_TYPE='InMemory';$env:MediaLifecycle__Enabled='false'
$env:Logging__LogLevel__Default='Warning';$env:Logging__LogLevel__Microsoft='Warning';$env:Logging__LogLevel__ConduitLLM='Warning'
$env:OTEL_SDK_DISABLED='true'
$observerToken=[Guid]::NewGuid().ToString('N')
$ownedProcesses=[Collections.Generic.List[object]]::new()
function Select-Cohort($cohort){$env:DATABASE_URL=$cohort.databaseUrl;$env:REDIS_URL=$cohort.redisUrl}
function Start-Owned([string]$Name,[string]$Program,[string[]]$Arguments,[ValidateSet('none','admin','micro','client')][string]$Observed='none'){
 $psi=[Diagnostics.ProcessStartInfo]::new($Program);$psi.WorkingDirectory=$configuration.workingDirectory
 $psi.UseShellExecute=$false;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
 foreach($argument in $Arguments){[void]$psi.ArgumentList.Add($argument)}
 if($Observed -eq 'admin'){$psi.Environment['DOTNET_STARTUP_HOOKS']=$configuration.observer+[IO.Path]::PathSeparator+$configuration.microObserver;$psi.Environment['CONDUIT_OBSERVER_PORT']='18084';$psi.Environment['CONDUIT_OBSERVER_TOKEN']=$observerToken;$psi.Environment['CONDUIT_OBSERVER_SAMPLE_MS']='100';$psi.Environment['CONDUIT_MICRO_OBSERVER_OUTPUT']="$out/$Name.process.json";$psi.Environment['CONDUIT_MICRO_OBSERVER_SAMPLE_MS']='0'}
 if($Observed -eq 'micro'){$psi.Environment['DOTNET_STARTUP_HOOKS']=$configuration.microObserver;$psi.Environment['CONDUIT_MICRO_OBSERVER_OUTPUT']="$out/$Name.process.json";$psi.Environment['CONDUIT_MICRO_OBSERVER_SAMPLE_MS']='100'}
 if($Observed -eq 'client'){$psi.Environment['DOTNET_STARTUP_HOOKS']=$configuration.microObserver;$psi.Environment['CONDUIT_MICRO_OBSERVER_OUTPUT']="$out/$Name.process.json";$psi.Environment['CONDUIT_MICRO_OBSERVER_SAMPLE_MS']='0'}
 $stdout=[IO.File]::Create("$out/$Name.stdout.log");$stderr=[IO.File]::Create("$out/$Name.stderr.log")
 try{$p=[Diagnostics.Process]::Start($psi)}catch{$stdout.Dispose();$stderr.Dispose();throw}
 $owned=[pscustomobject]@{Process=$p;Out=$stdout;Err=$stderr;OutTask=$p.StandardOutput.BaseStream.CopyToAsync($stdout);ErrTask=$p.StandardError.BaseStream.CopyToAsync($stderr);Name=$Name;Closed=$false}
 $ownedProcesses.Add($owned);return $owned
}
function Finish-Owned($owned,[int]$TimeoutSeconds=300,[bool]$Graceful=$false){
 if($owned.Closed){return}
 $timedOut=$false
 try{
  if($Graceful -and !$owned.Process.HasExited){& /bin/kill -TERM -- ([string]$owned.Process.Id);if($LASTEXITCODE -ne 0){throw 'SIGTERM failed'}}
  if(!$owned.Process.WaitForExit($TimeoutSeconds*1000)){$timedOut=$true;$owned.Process.Kill($true);if(!$owned.Process.WaitForExit(10000)){throw "Owned process could not be joined: $($owned.Name)"}}
  if(![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($owned.OutTask,$owned.ErrTask),10000)){throw "Output capture did not finish: $($owned.Name)"}
  [void]$owned.OutTask.GetAwaiter().GetResult();[void]$owned.ErrTask.GetAwaiter().GetResult()
  if($timedOut){throw "Timeout required forced termination: $($owned.Name)"}
  if($owned.Process.ExitCode -ne 0){throw "Owned exit $($owned.Process.ExitCode): $($owned.Name)"}
 }finally{
  $exited=$owned.Process.HasExited
  [ordered]@{processId=$owned.Process.Id;exitCode=$(if($exited){$owned.Process.ExitCode}else{$null});graceful=$Graceful;timedOut=$timedOut;exitConfirmed=$exited;outputCaptureCompleted=($owned.OutTask.IsCompletedSuccessfully -and $owned.ErrTask.IsCompletedSuccessfully)}|ConvertTo-Json|Set-Content "$out/$($owned.Name).exit.json" -Encoding utf8NoBOM
  if($exited -and $owned.OutTask.IsCompleted -and $owned.ErrTask.IsCompleted){$owned.Out.Dispose();$owned.Err.Dispose();$owned.Closed=$true}
 }
}
function Invoke-Owned([string]$Name,[string]$Program,[string[]]$Arguments,[string]$Observed='none'){
 $owned=Start-Owned $Name $Program $Arguments $Observed
 Finish-Owned $owned;return $owned.Process.Id
}
function Clear-OwnedProcesses{
 $errors=[Collections.Generic.List[string]]::new()
 foreach($owned in $ownedProcesses){if(!$owned.Closed){try{if(!$owned.Process.HasExited){$owned.Process.Kill($true)};Finish-Owned $owned 10}catch{$errors.Add($_.ToString())};if(!$owned.Closed){$errors.Add("Owned process or output capture remains unjoined: $($owned.Name)")}};if($owned.Closed){$owned.Process.Dispose()}}
 return $errors.ToArray()
}
if($Stage -eq 'prepare'){
 try{
 foreach($cohort in $cohorts){Select-Cohort $cohort;[void](Invoke-Owned "$($cohort.id)-migrator" $cohort.dotnet @($cohort.migrator));[void](Invoke-Owned "$($cohort.id)-seed" $configuration.clientDotnet @($cohort.seedProbe,'--seed'))}
 [ordered]@{stage='prepare';passed=$true;setting='IpFilter:Enabled';expectedValue='true';scope='Task-owned fixture migration and fixed f68 seed; no workload or financial acceptance claim'}|ConvertTo-Json|Set-Content "$out/result.json" -Encoding utf8NoBOM
 }finally{$cleanup=@(Clear-OwnedProcesses);[ordered]@{errors=$cleanup;allJoined=($cleanup.Count -eq 0)}|ConvertTo-Json -Depth 5|Set-Content "$out/process-cleanup.json" -Encoding utf8NoBOM;if($cleanup.Count){throw 'Preparation process cleanup failed'}}
 exit 0
}
if($configuration.infrastructurePrepared -ne $true){throw 'Require preparation receipt'}
$handler=[Net.Http.SocketsHttpHandler]::new();$handler.UseProxy=$false
$http=[Net.Http.HttpClient]::new($handler);$http.Timeout=[TimeSpan]::FromSeconds(3)
function Read-Observer([string]$Route,[string]$Name,[bool]$Post=$false){
 $message=[Net.Http.HttpRequestMessage]::new($(if($Post){[Net.Http.HttpMethod]::Post}else{[Net.Http.HttpMethod]::Get}),"http://127.0.0.1:18084$Route")
 [void]$message.Headers.TryAddWithoutValidation('X-Conduit-Observer-Token',$observerToken);$response=$null
 try{$response=$http.SendAsync($message).GetAwaiter().GetResult();$body=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult();if(!$response.IsSuccessStatusCode){throw "Observer HTTP $([int]$response.StatusCode)"};$body|Set-Content "$out/$Name.json" -Encoding utf8NoBOM;return ($body|ConvertFrom-Json)}finally{$message.Dispose();if($response){$response.Dispose()}}
}
function Await-Ready([string]$Url,$owned,[int]$Seconds=120,[bool]$Observer=$false){
 $clock=[Diagnostics.Stopwatch]::StartNew()
 while($true){$message=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,$Url);$response=$null
  if($Observer){[void]$message.Headers.TryAddWithoutValidation('X-Conduit-Observer-Token',$observerToken)}
  try{$response=$http.SendAsync($message).GetAwaiter().GetResult();if($response.IsSuccessStatusCode){return $clock.Elapsed.TotalMilliseconds}}catch{}finally{$message.Dispose();if($response){$response.Dispose()}}
  if($owned.Process.HasExited -or $clock.Elapsed.TotalSeconds -gt $Seconds){throw "Readiness timeout: $($owned.Name)"};Start-Sleep -Milliseconds 100
 }
}
function Run-AdminRead($cohort,[string]$Name,[int]$Requests,[int]$Warmups,[int]$Concurrency){
 $request=[ordered]@{workload='admin-setting-read';baseUrl='http://127.0.0.1:18081';requests=$Requests;warmupRequests=$Warmups;concurrency=$Concurrency;timeoutSeconds=30;settingKey='IpFilter:Enabled';expectedValue='true'}
 $request|ConvertTo-Json|Set-Content "$out/$Name.configuration.json" -Encoding utf8NoBOM
 [void](Invoke-Owned $Name $configuration.clientDotnet @($cohort.seedProbe,'--benchmark-workload',"$out/$Name.configuration.json") 'client')
 $raw=Get-Content -Raw "$out/$Name.stdout.log"|ConvertFrom-Json;$values=@($raw.workloads)
 if($raw.schemaVersion -ne 2 -or $raw.service -cne 'admin' -or $values.Count -ne 1){throw 'Invalid fixed Admin client report'}
 $value=$values[0]
 $metadata=Get-Content -Raw "$out/$Name.process.json"|ConvertFrom-Json
 if($metadata.framework -cne '.NET 10.0.15' -or $metadata.architecture -cne $configuration.architecture -or $metadata.samplingIntervalMilliseconds -ne 0){throw 'Fixed Admin client actual runtime/architecture differs'}
 if($value.id -cne 'admin-setting-read' -or !$value.authenticated -or $value.requested -ne $Requests -or $value.executed -ne $Requests -or $value.completed -ne $Requests -or $value.failures -ne 0 -or $value.warmupRequests -ne $Warmups -or $value.concurrency -ne $Concurrency -or $value.inputFingerprint -cnotmatch '^[a-f0-9]{64}$'){throw 'Admin responses not completely validated'}
 return $value
}
$rows=[Collections.Generic.List[object]]::new();$ownedHost=$null
try{
 for($run=1;$run -le $configuration.runs;$run++){
  $order=if($run%2 -eq 1){@($cohorts)}else{@($cohorts[($cohorts.Count-1)..0])}
  foreach($cohort in $order){
   Select-Cohort $cohort;$prefix="$($cohort.id)-r$run";$clock=[Diagnostics.Stopwatch]::StartNew()
   $ownedHost=Start-Owned "$prefix-admin" $cohort.dotnet @($cohort.admin) 'admin'
   [void](Await-Ready 'http://127.0.0.1:18084/health' $ownedHost 30 $true)
   [void](Read-Observer "/begin?id=$prefix-startup" "$prefix-startup-begin" $true)
   [void](Await-Ready 'http://127.0.0.1:18081/health/ready' $ownedHost)
   $ready=$clock.Elapsed.TotalMilliseconds
   $startup=Read-Observer "/end?id=$prefix-startup" "$prefix-startup-end" $true
   if($startup.snapshot.processId -ne $ownedHost.Process.Id -or $startup.snapshot.runtimeVersion -cne $cohort.expectedRuntime -or $startup.snapshot.architecture -cne $configuration.architecture){throw 'Admin actual runtime/PID/architecture differs'}
   [void](Read-Observer "/begin?id=$prefix-first" "$prefix-first-begin" $true)
   $first=Run-AdminRead $cohort "$prefix-first" 1 0 1
   $firstWindow=Read-Observer "/end?id=$prefix-first" "$prefix-first-end" $true
   [void](Read-Observer "/begin?id=$prefix-steady" "$prefix-steady-begin" $true)
   $steady=Run-AdminRead $cohort "$prefix-steady" $configuration.adminRequests $configuration.adminWarmups 8
   $steadyWindow=Read-Observer "/end?id=$prefix-steady" "$prefix-steady-end" $true
   [void](Read-Observer '/snapshot?collect=true' "$prefix-retained-outside-window")
   Finish-Owned $ownedHost 75 $true;$ownedHost=$null
   $rows.Add([ordered]@{kind='admin';cohort=$cohort.id;run=$run;sourceSha=$cohort.sourceSha;actualRuntime=$startup.snapshot.runtimeVersion;
    readyMilliseconds=$ready;runtimeMetadata="$prefix-admin.process.json";startup="$prefix-startup-end.json";first="$prefix-first.stdout.log";firstWindow="$prefix-first-end.json";steady="$prefix-steady.stdout.log";steadyWindow="$prefix-steady-end.json";
    validatedWindowCalls=($configuration.adminRequests+$configuration.adminWarmups);serverWindowIncludes='Internal fixed-client warmups plus measured calls, client startup/EOF validation and server background/observer; no idle subtraction. Different from Gateway measured-only windows; do not compare cross-service CPU.'})
   $env:CONDUIT_EXTENDED_CACHE_REDIS=$cohort.redisEndpoint
   $env:CONDUIT_EXTENDED_CACHE_ENVIRONMENT="phase2-cache-r$run"
   $env:CONDUIT_EXTENDED_CACHE_WARMUPS=[string]$configuration.cacheWarmups;$env:CONDUIT_EXTENDED_CACHE_SAMPLES=[string]$configuration.cacheSamples
   foreach($mode in @('write','read')){
    $name="$prefix-cache-$mode";$env:CONDUIT_EXTENDED_CACHE_OUTPUT="$out/$name.report.json"
    $processId=Invoke-Owned $name $cohort.dotnet @($cohort.cache,$mode) 'micro'
    $raw=Get-Content -Raw "$out/$name.report.json"|ConvertFrom-Json;$sidecar=Get-Content -Raw "$out/$name.process.json"|ConvertFrom-Json
    if(!$raw.Succeeded -or $raw.ProcessId -ne $processId -or $sidecar.processId -ne $processId -or $raw.Warmups -ne $configuration.cacheWarmups -or $raw.MeasuredPerState -ne $configuration.cacheSamples){throw 'Cache raw counts/PID failed'}
    if($raw.Rows.Count -ne $(if($mode -eq 'write'){6}else{3})){throw 'Cache state inventory missing'}
    if($raw.ProcessArchitecture -cne $configuration.architecture -or $sidecar.architecture -cne $configuration.architecture -or $raw.Framework -cne ".NET $($cohort.expectedRuntime)"){throw 'Cache runtime/architecture failed'}
    foreach($row in $raw.Rows){if($row.Samples.Count -ne $configuration.cacheSamples){throw 'Cache sample inventory failed'}}
    $expectedLoads=if($mode -eq 'write'){$configuration.cacheWarmups+$configuration.cacheSamples}else{0}
    foreach($domain in @('mapping','discovery','pricing')){if($raw.FactoryCalls.$domain -ne $expectedLoads){throw 'Cache loader count failed'}}
    $rows.Add([ordered]@{kind='cache';cohort=$cohort.id;run=$run;mode=$mode;sourceSha=$cohort.sourceSha;report="$name.report.json";process="$name.process.json";
     totalCacheLookups=(6*($configuration.cacheWarmups+$configuration.cacheSamples));lookupScope=$(if($mode -eq 'write'){'Cold+immediateL1, all3domains, warmups included'}else{'L2+untimed ownership reread, all3domains, warmups included'});
     factoryMisses=$expectedLoads*3;explicitQueueOrDrainCounter='Not applicable: these services are caches; factory counts and strict state validation establish logical cold/fill/L1/L2 states.'})
   }
   $name="$prefix-collector";$processId=Invoke-Owned $name $cohort.dotnet @($cohort.collector,"$out/$name.report.json") 'micro'
   $raw=Get-Content -Raw "$out/$name.report.json"|ConvertFrom-Json;$sidecar=Get-Content -Raw "$out/$name.process.json"|ConvertFrom-Json
   if(!$raw.Succeeded -or $raw.ProcessId -ne $processId -or $sidecar.processId -ne $processId -or $raw.Rows.Count -ne 8 -or @($raw.Rows|Where-Object{$_.Samples.Count -ne 3}).Count){throw 'Collector inventory failed'}
   if($raw.Architecture -cne $configuration.architecture -or $raw.Framework -cne ".NET $($cohort.expectedRuntime)"){throw 'Collector runtime/architecture failed'}
   $rows.Add([ordered]@{kind='collector';cohort=$cohort.id;run=$run;sourceSha=$cohort.sourceSha;report="$name.report.json";process="$name.process.json";
    measuredStreams=24;warmupStreams=8;allStreams=32;allContentChunks=88880;queueOrDrain='None: actual StreamingMetricsCollector retains a List of timestamps; validated recordedchunks/finalmetrics, not message-delivery capacity.'})
   $rows.ToArray()|ConvertTo-Json -Depth 8|Set-Content "$out/manifest.json" -Encoding utf8NoBOM
   Write-Output "PASS admin/cache/collector cohort=$($cohort.id) run=$run"
  }
 }
 [ordered]@{passed=$true;rows=$rows.Count;performanceAcceptanceGranted=$false;financialQualification=$false}|ConvertTo-Json|Set-Content "$out/result.json" -Encoding utf8NoBOM
}finally{
 $cleanup=@(Clear-OwnedProcesses);[ordered]@{errors=$cleanup;allJoined=($cleanup.Count -eq 0)}|ConvertTo-Json -Depth 5|Set-Content "$out/process-cleanup.json" -Encoding utf8NoBOM
 $http.Dispose();$handler.Dispose()
 if($cleanup.Count){throw 'Measurement process cleanup failed'}
}
