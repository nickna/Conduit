[CmdletBinding()]
param([ValidateSet('prepare','measure')][string]$Stage='prepare', [ValidateSet('gateway','admin')][string]$Service='gateway', [int]$Runs=5, [int]$Requests=1000)
$ErrorActionPreference='Stop'
$root='/workspace'
$evidence="/evidence/$Service"
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$probe="$root/Tests/ConduitLLM.GatewayNativeAotTests/bin/Release/net10.0/ConduitLLM.GatewayNativeAotTests.dll"
$serviceDll=if($Service -eq 'gateway'){"$root/Services/ConduitLLM.Gateway/bin/Release/net10.0/ConduitLLM.Gateway.dll"}else{"$root/Services/ConduitLLM.Admin/bin/Release/net10.0/ConduitLLM.Admin.dll"}
$migrator="$root/tools/ConduitLLM.Migrator/bin/Release/net10.0/ConduitLLM.Migrator.dll"
$env:DATABASE_URL='postgresql://phase0:phase0-isolated@phase0-primary-postgres:5432/phase0_primary'
$env:REDIS_URL='redis://phase0-primary-redis:6379'
$env:CONDUIT_NATIVE_PROVIDER_URL='http://127.0.0.1:18083'
$env:CONDUIT_MASTER_KEY='phase0-fixture-master-key-32-bytes-only'
$env:CONDUIT_API_TO_API_BACKEND_AUTH_KEY='phase0-fixture-backend-key-32-bytes-only'
$env:ASPNETCORE_ENVIRONMENT='Production'
$serviceUrl=if($Service -eq 'gateway'){'http://127.0.0.1:18080'}else{'http://127.0.0.1:18081'}
$env:ASPNETCORE_URLS=$serviceUrl
$env:CONDUIT_MIGRATION_MODE='Wait'
$env:CONDUIT_MIGRATION_WAIT_TIMEOUT_SECONDS='30'
$env:ConduitLLM__Messaging__Backend='Wolverine'
$env:ConduitLLM__Messaging__Wolverine__AutoProvision='false'
$env:CONDUIT_MEDIA_STORAGE_TYPE='InMemory'
$env:MediaLifecycle__Enabled='false'
$env:CONDUIT_CORE_RATE_LIMIT_MAX_REQUESTS='100000'
$env:CONDUIT_CORE_RATE_LIMIT_WINDOW_SECONDS='60'
$env:Logging__LogLevel__Default='Warning'
$env:Logging__LogLevel__Microsoft='Warning'
$env:Logging__LogLevel__ConduitLLM='Warning'
$env:OTEL_SDK_DISABLED='true'
$env:CONDUIT_NATIVE_BENCHMARK_API_KEY=if($Service -eq 'gateway'){'condt_native_aot_parity'}else{$env:CONDUIT_API_TO_API_BACKEND_AUTH_KEY}

[pscustomobject]@{
 timestampUtc=[DateTimeOffset]::UtcNow.ToString('o');stage=$Stage;service=$Service
 osRelease=@(Get-Content /etc/os-release);cpuFlags=(Get-Content /proc/cpuinfo|Where-Object{$_ -match '^flags\s*:'}|Select-Object -First 1)
 architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString();sdks=@(& dotnet --list-sdks);runtimes=@(& dotnet --list-runtimes)
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$evidence/environment-$Stage.json" -Encoding utf8NoBOM

function Start-Owned([string]$Name,[string[]]$Arguments){
 Start-Process -FilePath 'dotnet' -ArgumentList $Arguments -WorkingDirectory $root -NoNewWindow -PassThru -RedirectStandardOutput "$evidence/$Name.stdout.log" -RedirectStandardError "$evidence/$Name.stderr.log"
}
function Stop-Owned($Process,[string]$Name){
 if(-not $Process.HasExited){
  & /bin/kill -TERM -- ([string]$Process.Id)
  if($LASTEXITCODE -ne 0){throw "SIGTERM failed for $Name"}
  if(-not $Process.WaitForExit(60000)){$Process.Kill($true);[void]$Process.WaitForExit(5000);throw "Graceful shutdown timed out for $Name"}
 }
 if($Process.ExitCode -ne 0){throw "Owned $Name exited with $($Process.ExitCode)"}
}
function Invoke-Owned([string]$Name,[string[]]$Arguments,[int]$TimeoutSeconds=180){
 $process=Start-Owned $Name $Arguments
 try{
  if(-not $process.WaitForExit($TimeoutSeconds*1000)){$process.Kill($true);[void]$process.WaitForExit(5000);throw "Owned $Name timed out"}
  if($process.ExitCode -ne 0){throw "Owned $Name failed with $($process.ExitCode); inspect retained logs"}
 }finally{$process.Dispose()}
}
if($Stage -eq 'prepare'){
 Invoke-Owned 'migrator' @($migrator) 240
 Invoke-Owned 'seed' @($probe,'--seed') 60
 Write-Output 'PREPARE PASS: migrated and seeded task-owned phase0_primary database.'
 exit 0
}
$provider=$null
$hostProcess=$null
$rows=[Collections.Generic.List[object]]::new()
$handler=[Net.Http.SocketsHttpHandler]::new();$handler.UseProxy=$false
$client=[Net.Http.HttpClient]::new($handler);$client.Timeout=[TimeSpan]::FromSeconds(2)
try{
 if($Service -eq 'gateway'){
  $provider=Start-Owned 'provider' @($probe,'--mock-provider')
  $clock=[Diagnostics.Stopwatch]::StartNew()
  while($true){
   try{if($client.GetAsync('http://127.0.0.1:18083/health').GetAwaiter().GetResult().IsSuccessStatusCode){break}}catch{}
   if($provider.HasExited -or $clock.Elapsed.TotalSeconds -gt 30){throw 'Provider readiness failed'}
   Start-Sleep -Milliseconds 100
  }
 }
 for($run=1;$run -le $Runs;$run++){
  $clock=[Diagnostics.Stopwatch]::StartNew();$hostProcess=Start-Owned "$Service-$run" @($serviceDll)
  while($true){
   try{if($client.GetAsync("$serviceUrl/health/ready").GetAwaiter().GetResult().IsSuccessStatusCode){break}}catch{}
   if($hostProcess.HasExited -or $clock.Elapsed.TotalSeconds -gt 120){throw "$Service readiness failed for run $run"}
   Start-Sleep -Milliseconds 100
  }
  $readinessMs=$clock.Elapsed.TotalMilliseconds
  $order=if($Service -eq 'admin'){@('admin-setting-read')}elseif($run%2 -eq 1){@('gateway-chat-nonstream','gateway-chat-sse')}else{@('gateway-chat-sse','gateway-chat-nonstream')}
  foreach($workload in $order){
   $configuration=[ordered]@{workload=$workload;baseUrl=$serviceUrl;requests=$Requests;concurrency=8;warmupRequests=100;timeoutSeconds=30}
   if($Service -eq 'admin'){$configuration.settingKey='IpFilter:Enabled';$configuration.expectedValue='true'}
   else{
    $configuration.model='native-aot-model';$configuration.prompt='phase0 baseline Unicode snow 雪'
    $configuration.expectedContent=if($workload -eq 'gateway-chat-sse'){'native stream response'}else{'native provider response'}
    $configuration.expectedPromptTokens=10;$configuration.expectedCompletionTokens=5
   }
   $prefix="run-$run-$workload";$configurationPath="$evidence/$prefix.configuration.json"
   $configuration|ConvertTo-Json|Set-Content -LiteralPath $configurationPath -Encoding utf8NoBOM
   $invocationStarted=[DateTimeOffset]::UtcNow
   $probeProcess=Start-Owned $prefix @($probe,'--benchmark-workload',$configurationPath)
   try{
    if(-not $probeProcess.WaitForExit(180000)){$probeProcess.Kill($true);[void]$probeProcess.WaitForExit(5000);throw "Workload $prefix exceeded deadline"}
    if($probeProcess.ExitCode -ne 0){throw "Workload $prefix failed with $($probeProcess.ExitCode); inspect retained logs"}
    if($hostProcess.HasExited){throw "$Service exited during measurement"}
    $report=Get-Content -Raw -LiteralPath "$evidence/$prefix.stdout.log"|ConvertFrom-Json
    $result=@($report.workloads)
    if($report.schemaVersion -ne 2 -or $result.Count -ne 1 -or $result[0].id -cne $workload -or $result[0].authenticated -ne $true -or $result[0].requested -ne $Requests -or $result[0].executed -ne $Requests -or $result[0].completed -ne $Requests -or $result[0].failures -ne 0 -or $result[0].warmupRequests -ne 100 -or $result[0].concurrency -ne 8 -or $result[0].timeoutSeconds -ne 30 -or $result[0].inputFingerprint -cnotmatch '\A[0-9a-f]{64}\z'){throw 'Incomplete or invalid validated response report'}
    $rows.Add([pscustomobject]@{run=$run;workload=$workload;coldProcessReadinessMs=$readinessMs;invocationStartedUtc=$invocationStarted.ToString('o');invocationCompletedUtc=[DateTimeOffset]::UtcNow.ToString('o');probeArguments=@($probe,'--benchmark-workload',$configurationPath);clientDiagnostics=$false;serverProcessSampling=$false;result=$result[0]})
    $rows|ConvertTo-Json -Depth 12|Set-Content -LiteralPath "$evidence/results.json" -Encoding utf8NoBOM
    Write-Output "PASS run=$run workload=$workload complete=$($result[0].completed) failures=$($result[0].failures)"
   }finally{if(-not $probeProcess.HasExited){$probeProcess.Kill($true);[void]$probeProcess.WaitForExit(5000)};$probeProcess.Dispose()}
  }
  Stop-Owned $hostProcess "$Service run $run";$hostProcess.Dispose();$hostProcess=$null
  Write-Output "PASS run=$run graceful $Service SIGTERM shutdown"
 }
 if($Service -eq 'gateway'){$client.GetStringAsync('http://127.0.0.1:18083/probe/state').GetAwaiter().GetResult()|Set-Content -LiteralPath "$evidence/provider-state.json" -Encoding utf8NoBOM}
 [pscustomobject]@{
  schemaVersion=1;sourceRevision='f68cb5fae1130bbe1ef4fc30aca7a3266094dbaa';compilationSdk='10.0.403';containerSdk=(& dotnet --version);availableRuntimes=@(& dotnet --list-runtimes);framework='net10.0'
  publishing='JIT framework-dependent, unchanged Windows-built portable Release binaries in an isolated Linux SDK fixture';service=$Service;runs=$Runs;requestsPerWorkload=$Requests;warmupRequestsPerWorkload=100;concurrency=8;clientDiagnostics=$false;serverProcessSampling=$false;expectedAccountingRequests=$(if($Service -eq 'gateway'){$Runs*2*($Requests+100)}else{0})
  limitations=@('Baseline only; no .NET11 comparison.','SDK fixture is not the production service image.','p99 from1000 measured requests per run is exploratory.','No server CPU/allocation/GC or sampled memory metrics are collected in this primary arm.','Readiness polls run before workloads; response timing includes complete EOF and strict validation.','Native-named fixture/probe executes managed JIT assemblies.','This arm and the earlier diagnostics-enabled arm are separate blocks, not randomized pairs; their difference does not isolate diagnostic overhead.')
 }|ConvertTo-Json -Depth 8|Set-Content -LiteralPath "$evidence/manifest.json" -Encoding utf8NoBOM
}finally{
 if($null -ne $hostProcess){if(-not $hostProcess.HasExited){$hostProcess.Kill($true);[void]$hostProcess.WaitForExit(5000)};$hostProcess.Dispose()}
 if($null -ne $provider){
  if(-not $provider.HasExited){
   & /bin/kill -TERM -- ([string]$provider.Id)
   if(-not $provider.WaitForExit(10000)){$provider.Kill($true);[void]$provider.WaitForExit(5000)}
  }
  [pscustomobject]@{exitCode=$provider.ExitCode;termination='Owned plain-console provider terminated after captures; SIGTERM exit143 is expected. No graceful hosted-service shutdown claim.'}|ConvertTo-Json|Set-Content -LiteralPath "$evidence/provider-shutdown.json" -Encoding utf8NoBOM
  $provider.Dispose()
 }
 $client.Dispose();$handler.Dispose()
}
