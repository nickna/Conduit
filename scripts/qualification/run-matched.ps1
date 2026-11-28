[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConfigurationPath,
      [Parameter(Mandatory)][string]$OutputDirectory,
      [ValidateSet('prepare','measure')][string]$Stage='measure')
$ErrorActionPreference='Stop'
$configuration=Get-Content -Raw -LiteralPath $ConfigurationPath | ConvertFrom-Json
. (Join-Path $PSScriptRoot 'diagnostic-policy.ps1')
$diagnostic=$configuration.diagnostic -eq $true -and $configuration.profile -ceq 'pressure8'
if($configuration.diagnostic -and !$diagnostic){throw 'Diagnostic exception is restricted to pressure8'}
$extendedPressure = $configuration.profile -ceq 'pressure64' -and
 $configuration.interleavedControl -and $configuration.cpuPlacement -and $configuration.runs -eq 12
$replicateMixed = $configuration.profile -cin @('phase0', 'core') -and
 $configuration.interleavedControl -and $configuration.cpuPlacement -and $configuration.runs -eq 12
if($configuration.schemaVersion -ne 1 -or $(if($diagnostic){$configuration.runs -ne 1}else{
 !$extendedPressure -and !$replicateMixed -and ($configuration.runs -lt 3 -or $configuration.runs -gt 6)
})){throw 'Invalid qualification configuration'}
if(!$IsLinux){throw 'Matched measurements require native Linux'}
if((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count){throw 'Use a fresh output directory; previous evidence must remain unchanged'}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$out=(Resolve-Path $OutputDirectory).Path
Copy-Item -LiteralPath $PSCommandPath -Destination "$out/run-matched.source.ps1"
Copy-Item -LiteralPath $ConfigurationPath -Destination "$out/configuration.source.json"
$cohorts=@($configuration.cohorts)
if($Stage -eq 'measure' -and !(Test-Path -LiteralPath $configuration.drainProbe -PathType Leaf)){throw 'Require the fixed read-only financial drain probe'}
if($(if($diagnostic){$cohorts.Count -lt 1}else{$cohorts.Count -lt 2}) -or @($cohorts.id|Sort-Object -Unique).Count -ne $cohorts.Count){throw 'Require distinct matched cohorts; singleton is pressure8diagnostic only'}
if($diagnostic -and $Stage -eq 'measure' -and $cohorts.Count -ne 1){throw 'Measure exactly one diagnostic source at a time'}
foreach($cohort in $cohorts){
 if($cohort.id -notmatch '^[a-z0-9-]{1,16}$' -or $cohort.sourceSha -notmatch '^[0-9a-f]{40}$'){throw 'Invalid cohort identity'}
 foreach($path in @($cohort.gateway,$cohort.dotnet,$cohort.migrator,$cohort.seedProbe)){if(!(Test-Path -LiteralPath $path -PathType Leaf)){throw "Missing cohort input: $path"}}
}
$catalog=Get-Content -Raw -LiteralPath $configuration.catalog | ConvertFrom-Json
$cases=@($catalog.cases)
Copy-Item -LiteralPath $configuration.catalog -Destination "$out/catalog.source.json"
if(!$cases.Count -or @($cases.id|Sort-Object -Unique).Count -ne $cases.Count){throw 'Invalid case catalog'}
foreach($case in $cases){if($case.id -notmatch '^[A-Za-z0-9_-]{1,40}$'){throw 'Invalid case identifier'}}
if($diagnostic){
 $case=$cases[0]
 if($cases.Count -ne 1 -or $case.id -cne 'pressure-standard-fast' -or $case.endpoint -cne 'standard-sse' -or $case.requests -ne 1 -or $case.warmupRequests -ne 0 -or $case.chunks -ne 10000 -or $case.chunkCharacters -ne 1024 -or $case.receiveBufferBytes -ne 8192 -or $case.timeoutSeconds -ne 120 -or $case.slowReadDelayMilliseconds -ne 0 -or $case.slowReadBytes -ne 16384 -or $case.cancel -cne 'none'){throw 'Require unchanged first original8KiBcase for diagnostics'}
 $ssExecutable=(Get-Command ss -CommandType Application -ErrorAction Stop|Select-Object -First 1).Source
}
if($configuration.profile -and $configuration.profile -notmatch '^[a-z0-9-]{1,10}$'){throw 'Invalid measurement profile'}
$cpuPlacement = $configuration.cpuPlacement
if ($cpuPlacement) {
 . (Join-Path $PSScriptRoot 'gateway-cpu-placement.ps1')
 $taskset = (Get-Command taskset -CommandType Application | Select-Object -First 1).Source
 if ((Get-LinuxProcessCpus $PID) -cne $cpuPlacement.fixture) { throw 'Fixture process CPU affinity differs from the configured placement' }
}
$bindings=[ordered]@{schemaVersion=1;stage=$Stage;configurationSha256=(Get-FileHash $ConfigurationPath -Algorithm SHA256).Hash.ToLowerInvariant();
 nativeArchitecture=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString();processorCount=[Environment]::ProcessorCount;cpuTopology=(& lscpu);osRelease=@(Get-Content /etc/os-release);
 tcpConfiguration=@(foreach($name in @('net.ipv4.tcp_rmem','net.ipv4.tcp_wmem','net.ipv4.tcp_adv_win_scale','net.ipv4.tcp_moderate_rcvbuf','net.ipv4.tcp_congestion_control','net.ipv4.tcp_autocorking','net.ipv4.tcp_rto_min_us','net.core.rmem_max','net.core.wmem_max')){$path='/proc/sys/'+$name.Replace('.','/');[ordered]@{name=$name;available=(Test-Path $path);value=$(if(Test-Path $path){(Get-Content $path)}else{$null})}});
 kernel=(& uname -a);glibc=(& ldd --version|Select-Object -First 1);cpu=@(Get-Content /proc/cpuinfo|Select-Object -First 35);
 cgroup=@(foreach($file in @('/sys/fs/cgroup/cpu.max','/sys/fs/cgroup/memory.max','/sys/fs/cgroup/cpuset.cpus.effective')){if(Test-Path $file){[ordered]@{path=$file;value=(Get-Content $file)}}});
 files=@(foreach($path in @($configuration.workload,$configuration.provider,$configuration.observer,$configuration.catalog,$PSCommandPath)+@($cohorts|ForEach-Object{$_.gateway})+@($configuration.drainProbe|Where-Object{$_})){[ordered]@{path=$path;sha256=(Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()}});
  cohorts=@($cohorts|ForEach-Object{[ordered]@{id=$_.id;sourceSha=$_.sourceSha;sourceSdk=$_.sourceSdk;expectedRuntime=$_.expectedRuntime;dotnet=$_.dotnet}});
 gcEnvironment=@(Get-ChildItem Env:|Where-Object{$_.Name -match '^(DOTNET|COMPlus)_(GC|Heap|ServerGC|ConcurrentGC)' }|ForEach-Object{[ordered]@{name=$_.Name;value=$_.Value}});
 scope='Matched JIT diagnostic fixture; provider/client fixed on .NET 10 with a fresh provider per cohort/run and identical logical payload IDs across source pairs; same Linux kernel/monotonic clock and infrastructure; no production capacity or deployment claim'}
if($bindings.nativeArchitecture -cne $configuration.architecture){throw 'Native architecture does not match requested qualification'}
if ($cpuPlacement) { $bindings.cpuPlacement = $cpuPlacement }
$bindings|ConvertTo-Json -Depth 8|Set-Content "$out/bindings.json" -Encoding utf8NoBOM
if($bindings.gcEnvironment.Count){throw 'Default-GC qualification requires no external GC environment overrides; retained bindings identify the conflicting settings'}
Remove-Item Env:DOTNET_STARTUP_HOOKS -ErrorAction SilentlyContinue
$env:ASPNETCORE_ENVIRONMENT='Production';$env:ASPNETCORE_URLS='http://127.0.0.1:18080'
$env:CONDUIT_MASTER_KEY='phase2-fixture-master-key-32-bytes-only'
$env:CONDUIT_API_TO_API_BACKEND_AUTH_KEY='phase2-fixture-backend-key-32-bytes-only'
$env:CONDUIT_NATIVE_PROVIDER_URL='http://127.0.0.1:18083'
$env:CONDUIT_MIGRATION_MODE='Wait';$env:CONDUIT_MIGRATION_WAIT_TIMEOUT_SECONDS='30'
$env:ConduitLLM__Messaging__Backend='Wolverine';$env:ConduitLLM__Messaging__Wolverine__AutoProvision='false'
$env:CONDUIT_MEDIA_STORAGE_TYPE='InMemory';$env:MediaLifecycle__Enabled='false'
$env:CONDUIT_CORE_RATE_LIMIT_MAX_REQUESTS='1000000';$env:CONDUIT_CORE_RATE_LIMIT_WINDOW_SECONDS='60'
$env:Logging__LogLevel__Default='Warning';$env:Logging__LogLevel__Microsoft='Warning';$env:Logging__LogLevel__ConduitLLM='Warning'
$env:OTEL_SDK_DISABLED='true';$env:CONDUIT_EXT_BENCHMARK_API_KEY='condt_native_aot_parity'
$observerToken=[Guid]::NewGuid().ToString('N')
function Select-Cohort($cohort){
 $env:DATABASE_URL=$cohort.databaseUrl;$env:REDIS_URL=$cohort.redisUrl
 $env:CONDUIT_ENVIRONMENT="phase2-1766-$($cohort.id.PadRight(16,[char]'-'))"
}
. (Join-Path $PSScriptRoot 'owned-process.ps1')
if($Stage -eq 'prepare'){
 try{
 foreach($cohort in $cohorts){Select-Cohort $cohort;Invoke-Owned "$($cohort.id)-migrator" $cohort.dotnet @($cohort.migrator);Invoke-Owned "$($cohort.id)-seed" $configuration.clientDotnet @($cohort.seedProbe,'--seed')}
 [ordered]@{stage='prepare';passed=$true;next='Apply fixture-controls.sql to each task-owned database before measure; preparation alone does not prove financial outcomes'}|ConvertTo-Json|Set-Content "$out/result.json" -Encoding utf8NoBOM
 }finally{
  $prepareCleanup=@(Clear-OwnedProcesses)
  $prepareCleanup|ConvertTo-Json -AsArray|Set-Content "$out/process-cleanup.json" -Encoding utf8NoBOM
  if($prepareCleanup.Count){throw ($prepareCleanup -join '; ')}
 }
 exit 0
}
$isolatedStandard = $configuration.profile -ceq 'sse-isol'
$threeArms = $isolatedStandard -or $configuration.interleavedControl
if ($threeArms -and ($cohorts.Count -ne 3 -or ($configuration.runs -ne 6 -and !$extendedPressure -and !$replicateMixed))) {
 throw 'Invalid interleaved cohort or repetition inventory'
}
if ($isolatedStandard -and ($cases.Count -ne 1 -or $cases[0].id -cnotin @('standard-100','standard-1000','standard-10000') -or
  $cases[0].endpoint -cne 'standard-sse')) {
 throw 'Expected three cohorts, six repetitions and one standard SSE case'
}
if($configuration.infrastructurePrepared -ne $true){throw 'Require explicit fixture preparation receipt before measurement'}
$handler=[Net.Http.SocketsHttpHandler]::new();$handler.UseProxy=$false
$http=[Net.Http.HttpClient]::new($handler);$http.Timeout=[TimeSpan]::FromSeconds(5)
function Read-Json([string]$url,[string]$path,[bool]$post=$false){
 $message=[Net.Http.HttpRequestMessage]::new($(if($post){[Net.Http.HttpMethod]::Post}else{[Net.Http.HttpMethod]::Get}),$url);$response=$null
 if($url.StartsWith('http://127.0.0.1:18084')){[void]$message.Headers.TryAddWithoutValidation('X-Conduit-Observer-Token',$observerToken)}
 try{$response=$http.SendAsync($message).GetAwaiter().GetResult();$value=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult();if(!$response.IsSuccessStatusCode){throw "HTTP $([int]$response.StatusCode): $value"};$value|Set-Content $path -Encoding utf8NoBOM;return ($value|ConvertFrom-Json)}finally{$message.Dispose();if($response){$response.Dispose()}}
}
function Save-Metrics([string]$path){
 $clock=[Diagnostics.Stopwatch]::StartNew()
 $recordedActiveScrape=$false
 while($true){
  $message=[Net.Http.HttpRequestMessage]::new('GET','http://127.0.0.1:18080/metrics')
  $response=$null
  [void]$message.Headers.TryAddWithoutValidation('Authorization','Bearer condt_native_aot_parity')
  try{
   $response=$http.SendAsync($message).GetAwaiter().GetResult()
   $body=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
   if(!$response.IsSuccessStatusCode){throw "Metrics HTTP $([int]$response.StatusCode)"}
  }finally{
   $message.Dispose()
   if($response){$response.Dispose()}
  }
  $body|Set-Content -LiteralPath $path -Encoding utf8NoBOM
  if(!$configuration.requireStreamQuiescence){return}

  # The client can finish a cancellation before Gateway accounting and terminal
  # metrics finish. Wait outside the measured window in this owned fixture.
  $activeSamples=[regex]::Matches($body,'(?m)^conduitllm_gateway_streaming_conduit_streams_active ([^\s]+)(?: [^\r\n]+)?\r?$')
  if($activeSamples.Count -eq 0){return} # The gauge is absent before the first stream.
  if($activeSamples.Count -ne 1){throw "Duplicate active-stream gauge samples in $path"}
  $active=[decimal]::Parse($activeSamples[0].Groups[1].Value,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture)
  if($active -eq 0){return}
  if($active -lt 0 -or [decimal]::Truncate($active) -ne $active){throw "Invalid active-stream gauge in $path"}
  if(!$recordedActiveScrape){
   $body|Set-Content -LiteralPath "$path.active" -Encoding utf8NoBOM
   $recordedActiveScrape=$true
  }
  if($clock.Elapsed.TotalSeconds -ge 30){throw "Gateway streams did not finish before metrics capture in $path"}
  Start-Sleep -Milliseconds 100
 }
}
function Await-Ready([string]$url,$owned,[int]$timeout){
 $clock=[Diagnostics.Stopwatch]::StartNew()
 while($true){$response=$null;try{$response=$http.GetAsync($url).GetAwaiter().GetResult();if($response.IsSuccessStatusCode){return $clock.Elapsed.TotalMilliseconds}}catch{}finally{if($response){$response.Dispose()}}
  if($owned.Process.HasExited -or $clock.Elapsed.TotalSeconds -gt $timeout){throw "Readiness failed: $($owned.Name)"};Start-Sleep -Milliseconds 100}
}
$audioIo = $env:PR1778_STORAGE_START -cin @('normal','memory')
if ($audioIo -and (!$replicateMixed -or $cases.Count -ne 5)) { throw 'Storage comparison scope changed' }
$provider=$null;$hostOwned=$null;$rows=[Collections.Generic.List[object]]::new();$failure=$null
$expectedLogs=@{}
foreach($cohort in $cohorts){
 $initial=0
 if($configuration.initialLogCounts -and $configuration.initialLogCounts.PSObject.Properties.Name -contains $cohort.id){$initial=[int]$configuration.initialLogCounts.($cohort.id)}
 if($initial -lt 0){throw 'Invalid initial request-log count'}
 $expectedLogs[$cohort.id]=$initial
}
try{
 for($run=1;$run -le $configuration.runs;$run++){
  # Keep each original workload history together on a fresh Gateway in both blocks.
  $mirror = $replicateMixed -and $run -gt 6
  $orderRun = if ($mirror) { 13 - $run } else { $run }
  if ($audioIo) {
   $orderRun = 1 + [int][Math]::Floor(($run - 1) / 2)
   $mirror = $false
   $memory = (($run % 4) -in @(2,3)) -xor ($env:PR1778_STORAGE_START -ceq 'memory')
   $storage = if ($memory) { 'memory' } else { 'normal' }
  }
  $order=if($run%2 -eq 1){@($cohorts)}else{@($cohorts[($cohorts.Count-1)..0])}
  if ($threeArms) {
   # Each block of six repetitions places each cohort in each position twice.
   $first = [int][Math]::Floor(($orderRun - 1) / 2)
   $direction = if ($orderRun % 2 -eq 1) { 1 } else { 2 }
   $order = @(0..2 | ForEach-Object { $cohorts[($first + $direction * $_) % 3] })
   if ($mirror) { [Array]::Reverse($order) }
  }
  foreach($cohort in $order){
   # Cohort prefixes identify artifacts/control windows only. User/provider
   # payloads use the shared logical ID defined below.
   Select-Cohort $cohort;$prefix="$($cohort.id.PadRight(16,[char]'-'))-r$run"
   if($configuration.profile){$prefix="$($configuration.profile)-$prefix"}
   $profileName=if($configuration.profile){$configuration.profile}else{'phase0'}
   # Provider observation IDs are scoped to this fresh process. Logical payload
   # IDs match exactly across runtimes; artifact paths remain cohort-specific.
   # Equal-width but different cohort words change BPE admission estimates.
   if ($audioIo -and $memory) {
    $provider=Start-Owned "$prefix-provider" '/usr/bin/env' @("ASPNETCORE_TEMP=$env:PR1778_PROVIDER_TMP",$configuration.clientDotnet,$configuration.provider,'--provider','18083')
   } else {
    $provider=Start-Owned "$prefix-provider" $configuration.clientDotnet @($configuration.provider,'--provider','18083')
   }
   [void](Await-Ready 'http://127.0.0.1:18083/health' $provider 30)
   $providerIdentity=Read-Json 'http://127.0.0.1:18083/identity' "$out/$prefix-provider-identity.json"
   $providerHash=(Get-FileHash -LiteralPath $configuration.provider -Algorithm SHA256).Hash.ToLowerInvariant()
   if($configuration.providerObservationVersion -ne 2 -or $providerIdentity.providerObservationVersion -ne 2 -or $providerIdentity.processId -ne $provider.Process.Id -or $providerIdentity.actualRuntime -cne '10.0.15' -or $providerIdentity.architecture -cne $configuration.architecture -or $providerIdentity.sourceSdk -cne '10.0.404' -or $providerIdentity.assemblySha256 -cne $providerHash){throw 'Fixed ProviderV2 identity/runtime/compiler/binary differs'}
   if ($cpuPlacement) {
    $hostOwned = Start-Owned "$prefix-gateway" $taskset @('--cpu-list', $cpuPlacement.gateway, $cohort.dotnet, $cohort.gateway) $true
   } else {
    $hostOwned=Start-Owned "$prefix-gateway" $cohort.dotnet @($cohort.gateway) $true
   }
   $ready=Await-Ready 'http://127.0.0.1:18080/health/ready' $hostOwned 120
   if ($cpuPlacement) {
    $gatewayCpus = Get-LinuxProcessCpus $hostOwned.Process.Id
    $providerCpus = Get-LinuxProcessCpus $provider.Process.Id
    [ordered]@{gateway=$gatewayCpus;provider=$providerCpus} |
     ConvertTo-Json | Set-Content "$out/$prefix-cpu-affinity.json" -Encoding utf8NoBOM
    if ($gatewayCpus -cne $cpuPlacement.gateway -or $providerCpus -cne $cpuPlacement.fixture) {
     throw 'Gateway or provider CPU affinity differs from the configured placement'
    }
   }
   $identity=Read-Json 'http://127.0.0.1:18084/snapshot' "$out/$prefix-identity.json"
   if($identity.snapshot.runtimeVersion -cne $cohort.expectedRuntime -or $identity.snapshot.architecture -cne $configuration.architecture){throw 'Actual Gateway runtime/architecture differs from requested cohort'}
   [void](Read-Json 'http://127.0.0.1:18084/snapshot?collect=true' "$out/$prefix-before-idle-gc.json")
   [void](Read-Json "http://127.0.0.1:18084/begin?id=$prefix-idle" "$out/$prefix-idle-begin.json" $true)
   Start-Sleep -Seconds 3
   [void](Read-Json "http://127.0.0.1:18084/end?id=$prefix-idle" "$out/$prefix-idle-end.json" $true)
   for($index=0;$index -lt $cases.Count;$index++){
    $case=$cases[($index+$orderRun-1)%$cases.Count];$id="$prefix-$($case.id)"
    $logicalCaseId="$profileName-r$run-$($case.id)"
    if ($audioIo) {
     $case = $cases[$index]
     $id = "$prefix-$($case.id)"
     $logicalCaseId = "$profileName-r$($run.ToString('D2'))-$($case.id)"
    }
    $config=[ordered]@{caseId=$logicalCaseId;baseUrl='http://127.0.0.1:18080';providerUrl='http://127.0.0.1:18083';model='native-aot-model'}
    foreach($property in $case.PSObject.Properties){if($property.Name -ne 'id'){$config[$property.Name]=$property.Value}}
    $warmups=[int]$config.warmupRequests;$config.warmupRequests=0
    $legacyDiagnostic=$cohort.knownResponsesFailure -eq $true -and $case.id -eq 'responses-json'
    if($legacyDiagnostic){$config.requests=1;$config.concurrency=1;$warmups=0}
    if($warmups -gt 0){
     $warm=[ordered]@{};foreach($key in $config.Keys){$warm[$key]=$config[$key]};$warm.caseId="$logicalCaseId-warm";$warm.requests=$warmups;$warm.concurrency=[Math]::Min([int]$warm.concurrency,$warmups)
     $warm|ConvertTo-Json -Depth 8|Set-Content "$out/$id-warm.configuration.json" -Encoding utf8NoBOM
     Invoke-Owned "$id-warm" $configuration.clientDotnet @($configuration.workload,'--run',"$out/$id-warm.configuration.json")
    }
    $config|ConvertTo-Json -Depth 8|Set-Content "$out/$id.configuration.json" -Encoding utf8NoBOM
    $tcpSampler=$null;$tcpStop="$out/$id-tcp.stop"
    if($diagnostic){
     $sampleScript='i=0; while [ ! -f "$1" ] && [ "$i" -lt 180 ]; do date -u +%Y-%m-%dT%H:%M:%S.%NZ; "$2" -tinm "( sport = :18080 or dport = :18080 )" || exit 92; i=$((i+1)); sleep 1; done'
     $tcpSampler=Start-Owned "$id-tcp" '/bin/sh' @('-c',$sampleScript,'pressure-tcp',$tcpStop,$ssExecutable)
    }
    Save-Metrics "$out/$id-before.prometheus"
    [void](Read-Json "http://127.0.0.1:18084/begin?id=$id" "$out/$id-server-begin.json" $true)
    $client=Start-Owned $id $configuration.clientDotnet @($configuration.workload,'--run',"$out/$id.configuration.json");$clientFailure=$null
    $ioSampler=$null
    if ($audioIo) {
     $ioSampler=Start-Owned "$id-io" '/usr/bin/python3' @((Join-Path $PSScriptRoot 'pr1778-audio-io.py'),"$out/$id-io.json",$storage,$hostOwned.Process.Id,$provider.Process.Id,$client.Process.Id)
    }
    try{Finish-Owned $client 600}catch{$clientFailure=$_}finally{
     try{[void](Read-Json "http://127.0.0.1:18084/end?id=$id" "$out/$id-server-end.json" $true)}finally{
      if($ioSampler){Finish-Owned $ioSampler 20}
      if($tcpSampler){[IO.File]::WriteAllText($tcpStop,'stop');Finish-Owned $tcpSampler 10}
     }
    }
    $report=Get-Content -Raw "$out/$id.stdout.log"|ConvertFrom-Json
    $known=$false;$diagnosticDeadline=$false
    if($clientFailure){
     $failed=@($report.measured.requests)
     $known=$legacyDiagnostic -and $failed.Count -eq 1 -and $failed[0].status -eq 502 -and $failed[0].errorResponseBody -match 'cannot be represented by the text-only Responses subset' -and $failed[0].provider.completed
     $diagnosticDeadline=Test-PressureDiagnosticDeadline $report $diagnostic
     if(!$known -and !$diagnosticDeadline){throw $clientFailure}
    }
    [void](Read-Json "http://127.0.0.1:18083/observations?prefix=$logicalCaseId" "$out/$id-provider.json")
    Start-Sleep -Milliseconds 250;Save-Metrics "$out/$id-after.prometheus"
    [void](Read-Json 'http://127.0.0.1:18084/snapshot?collect=true' "$out/$id-retained-gc.json")
    $rows.Add([ordered]@{cohort=$cohort.id;case=$case.id;caseId=$id;logicalCaseId=$logicalCaseId;providerInstanceId=$prefix;providerProcessId=$provider.Process.Id;run=$run;sourceSha=$cohort.sourceSha;requests=$config.requests;warmups=$warmups;
     diagnostic=$diagnostic;diagnosticDeadlineFailure=$diagnosticDeadline;gatewayProcessId=$hostOwned.Process.Id;readyMilliseconds=$ready;knownProductionFinding=$known;passed=$report.passed;result="$id.stdout.log";
     serverWindowIncludes='Measured client startup, provider-observation/finalization wait, runtime/application background services and observer; excludes warmup; no idle subtraction'})
    $expectedLogs[$cohort.id]+=$warmups
    if(!$known){$expectedLogs[$cohort.id]+=[int]$config.requests}
    $rows.ToArray()|ConvertTo-Json -Depth 8|Set-Content "$out/manifest.json" -Encoding utf8NoBOM
    Write-Output "$(if($diagnosticDeadline){'DIAGNOSTIC DEADLINE FAILURE'}elseif($known){'KNOWN LEGACY FAILURE'}else{'PASS'}) $id"
   }
   # RequestLog batching and durable fallback SpendUpdateRequested events can
   # outlive response EOF. Drain known persisted spend while the consumer is
   # alive, outside every observer window; retain the entire read-only receipt.
   if(!$diagnostic){Invoke-Owned "$prefix-financial-drain" $configuration.clientDotnet @($configuration.drainProbe,
    '--expected-logs',([string]$expectedLogs[$cohort.id]),'--timeout-seconds','180','--output',"$out/$prefix-financial-drain.json") 200}
   else{[ordered]@{executed=$false;financialQualification=$false;scope='Diagnostic only: state captured after shutdown; no live financial-drain success claim'}|ConvertTo-Json|Set-Content "$out/$prefix-financial-drain-not-executed.json" -Encoding utf8NoBOM}
   $shutdown=[Diagnostics.Stopwatch]::StartNew();Finish-Owned $hostOwned 60 $true;$hostOwned=$null
   [ordered]@{cohort=$cohort.id;run=$run;shutdownMilliseconds=$shutdown.Elapsed.TotalMilliseconds;exitCode=0}|ConvertTo-Json|Set-Content "$out/$prefix-shutdown.json" -Encoding utf8NoBOM
   Finish-Owned $provider 60 $true;$provider=$null
  }
 }
}catch{$failure=$_.ToString();throw}finally{
 $cleanupFailures=[Collections.Generic.List[string]]::new()
 foreach($owned in @($hostOwned,$provider)){if($owned){try{Finish-Owned $owned 60 $true}catch{$cleanupFailures.Add($_.ToString())}}}
 foreach($errorText in @(Clear-OwnedProcesses)){$cleanupFailures.Add($errorText)}
 $http.Dispose();$handler.Dispose()
 [ordered]@{schemaVersion=1;executionCompleted=($null -eq $failure -and $cleanupFailures.Count -eq 0);rows=$rows.Count;
 expectedRows=([int]$configuration.runs*$cohorts.Count*$cases.Count);failure=$failure;cleanupFailures=$cleanupFailures.ToArray();
 diagnostic=$diagnostic;performanceAcceptanceGranted=$false;
 financialQualification='Requires separate read-only PostgreSQL and Redis reconciliation; HTTP success does not prove settlement'}|ConvertTo-Json -Depth 8|Set-Content "$out/execution.json" -Encoding utf8NoBOM
 if(!$failure -and $cleanupFailures.Count){throw ($cleanupFailures -join '; ')}
}
