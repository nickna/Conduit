[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$CandidateRevision,
      [Parameter(Mandatory)][ValidateSet('X64','Arm64')][string]$Architecture,
      [Parameter(Mandatory)][string]$OutputDirectory,
      [ValidateSet('main','pressure')][string]$Profile='main',
      [ValidateRange(3,6)][int]$Runs=5)
$ErrorActionPreference='Stop'
if(!$IsLinux -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() -cne $Architecture){throw 'Require matching native Linux hardware'}
$repository=(& git rev-parse --show-toplevel).Trim()
if($LASTEXITCODE -ne 0){throw 'Require a Git checkout'}
& git merge-base --is-ancestor $CandidateRevision HEAD
if($LASTEXITCODE -ne 0){throw 'Candidate must be an ancestor of the qualification checkout'}
if((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count){throw 'Use a fresh output directory; never overwrite previous qualification evidence'}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$out=(Resolve-Path $OutputDirectory).Path
$harnessSource=Join-Path $out 'harness-source';New-Item -ItemType Directory -Path $harnessSource|Out-Null
$sourceFiles=@(Get-ChildItem $PSScriptRoot -Recurse -File|Where-Object{
 $_.FullName -notmatch '[\\/](bin|obj|published|__pycache__)[\\/]' -and $_.Extension -in @('.ps1','.py','.cs','.csproj','.props','.targets','.json','.sql','.md')
})
$harnessBindings=@(foreach($file in $sourceFiles){
 $relative=[IO.Path]::GetRelativePath($PSScriptRoot,$file.FullName);$copy=Join-Path $harnessSource $relative
 New-Item -ItemType Directory -Path (Split-Path $copy) -Force|Out-Null
 Copy-Item -LiteralPath $file.FullName -Destination $copy
 [ordered]@{path=$relative;bytes=$file.Length;sha256=(Get-FileHash $copy -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$harnessBindings|ConvertTo-Json -Depth 4|Set-Content "$out/harness-source-bindings.json" -Encoding utf8NoBOM
$work=Join-Path $out 'work';New-Item -ItemType Directory -Path $work | Out-Null
$task="conduit-phase2-runtime-$([Guid]::NewGuid().ToString('N').Substring(0,12))"
$containers=[Collections.Generic.List[string]]::new();$failure=$null;$prepared=$false
$cohortDefinitions=@(
 @{id='legacy';sha='f68cb5fae1130bbe1ef4fc30aca7a3266094dbaa';sdk='10.0.404';runtime='10.0.15';db=2;known=$true},
 @{id='fixed';sha='911d7b6bbe54b4a6b77058b75ee2a86bc2d59097';sdk='10.0.404';runtime='10.0.15';db=3;known=$false},
 @{id='candidate';sha=$CandidateRevision;sdk='11.0.100';runtime='11.0.0';db=4;known=$false})
$sourceBindings=[Collections.Generic.List[object]]::new();$cohorts=[Collections.Generic.List[object]]::new()
function Invoke-Checked([string]$Program,[string[]]$Arguments,[string]$Log){
 & $Program @Arguments *> $Log
 if($LASTEXITCODE -ne 0){throw "Command failed; inspect $Log"}
}
function Invoke-Docker([string[]]$Arguments){
 $value=(& docker @Arguments) -join "`n"
 if($LASTEXITCODE -ne 0){throw "Docker command failed: $($Arguments[0])"}
 return $value
}
function Publish-Project([string]$Source,[string]$Project,[string]$Destination,[string]$Log,[string]$ExpectedSdk,[string]$Revision=''){
 $toolchain=Join-Path $work "toolchains/$ExpectedSdk"
 New-Item -ItemType Directory -Force -Path $toolchain|Out-Null
 [ordered]@{sdk=[ordered]@{version=$ExpectedSdk;rollForward='disable';allowPrerelease=$false}}|ConvertTo-Json|Set-Content "$toolchain/global.json" -Encoding utf8NoBOM
 $projectPath=Join-Path $Source $Project
 Push-Location $toolchain
 try{
  $selected=(& dotnet --version)-join "`n"
  if($LASTEXITCODE -ne 0 -or $selected.Trim() -cne $ExpectedSdk){throw "Unexpected build SDK; expected $ExpectedSdk, observed $selected"}
  Invoke-Checked 'dotnet' @('--info') "$Log.toolchain.txt"
  $arguments=@('publish',$projectPath,'-c','Release','-o',$Destination,'-m:1','-nr:false','-p:UseSharedCompilation=false')
  if($Revision){$arguments+="-p:ConduitCommitSha=$Revision"}
  Invoke-Checked 'dotnet' $arguments $Log
 }finally{Pop-Location}
}
foreach($definition in $cohortDefinitions){
 $source=Join-Path $work $definition.id;New-Item -ItemType Directory -Path $source | Out-Null
 $archive=Join-Path $work "$($definition.id).tar"
 Invoke-Checked 'git' @('archive','--format=tar',"--output=$archive",$definition.sha) "$out/$($definition.id)-archive.log"
 Invoke-Checked 'tar' @('-xf',$archive,'-C',$source) "$out/$($definition.id)-extract.log"
 $gateway=Join-Path $work "$($definition.id)-gateway";$migrator=Join-Path $work "$($definition.id)-migrator"
 Publish-Project $source 'Services/ConduitLLM.Gateway/ConduitLLM.Gateway.csproj' $gateway "$out/$($definition.id)-gateway-build.log" $definition.sdk $definition.sha
 Publish-Project $source 'tools/ConduitLLM.Migrator/ConduitLLM.Migrator.csproj' $migrator "$out/$($definition.id)-migrator-build.log" $definition.sdk $definition.sha
 $sourceBindings.Add([ordered]@{id=$definition.id;sourceSha=$definition.sha;sourceSdk=$definition.sdk;sourceTree=(& git rev-parse "$($definition.sha)^{tree}").Trim();
  archiveSha256=(Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant();
  files=@(foreach($directory in @($gateway,$migrator)){
   Get-ChildItem $directory -Recurse -File|ForEach-Object{
    [ordered]@{path=$_.FullName.Substring($work.Length+1);sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant();bytes=$_.Length}
   }
  })})
 $cohorts.Add([ordered]@{id=$definition.id;sourceSha=$definition.sha;sourceSdk=$definition.sdk;expectedRuntime=$definition.runtime;dotnet=(Get-Command dotnet).Source;
  gateway=(Join-Path $gateway 'ConduitLLM.Gateway.dll');migrator=(Join-Path $migrator 'ConduitLLM.Migrator.dll');
  seedProbe=(Join-Path $work 'seed/ConduitLLM.GatewayNativeAotTests.dll');databaseUrl="postgresql://phase2:isolated-phase2-fixture@127.0.0.1:15476/phase2_$($definition.id)";
  redisUrl="redis://127.0.0.1:16476/$($definition.db)";knownResponsesFailure=$definition.known})
}
$sourceBindings.ToArray()|ConvertTo-Json -Depth 8|Set-Content "$out/source-bindings.json" -Encoding utf8NoBOM
Publish-Project (Join-Path $work 'legacy') 'Tests/ConduitLLM.GatewayNativeAotTests/ConduitLLM.GatewayNativeAotTests.csproj' (Join-Path $work 'seed') "$out/seed-build.log" '10.0.404' 'f68cb5fae1130bbe1ef4fc30aca7a3266094dbaa'
$clientProject=if($Profile -eq 'pressure'){'Pressure'}else{'Workload'}
Publish-Project (Join-Path $PSScriptRoot $clientProject) "$clientProject.csproj" (Join-Path $work 'client') "$out/client-build.log" '10.0.404'
Publish-Project (Join-Path $PSScriptRoot 'ObserverV2') 'Conduit.ServerObserver.csproj' (Join-Path $work 'observer') "$out/observer-build.log" '10.0.404'
Publish-Project (Join-Path $PSScriptRoot 'Drain') 'DrainProbe.csproj' (Join-Path $work 'drain') "$out/drain-build.log" '10.0.404'
$sourceBindings.Add([ordered]@{id='fixed-tools';sourceSdk='10.0.404';files=@(
 foreach($name in @('seed','client','observer','drain')){
  Get-ChildItem (Join-Path $work $name) -Recurse -File|ForEach-Object{
   [ordered]@{path=[IO.Path]::GetRelativePath($work,$_.FullName);sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant();bytes=$_.Length}
  }
 }
)})
$sourceBindings.ToArray()|ConvertTo-Json -Depth 8|Set-Content "$out/source-bindings.json" -Encoding utf8NoBOM
$primaryCatalog=if($Profile -eq 'pressure'){Join-Path $PSScriptRoot 'Pressure/catalog.json'}else{Join-Path $PSScriptRoot 'phase0-catalog.json'}
$config=[ordered]@{schemaVersion=1;profile=$(if($Profile -eq 'pressure'){'pressure'}else{'phase0'});runs=$Runs;architecture=$Architecture;workingDirectory=$repository;infrastructurePrepared=$false;
 scope='Native Ubuntu host-published JIT processes on one runner; not packaged Resolute image performance; source/compiler/runtime migration effects remain attributable';
 clientDotnet=(Get-Command dotnet).Source;workload=(Join-Path $work "client/Conduit.Phase0.$clientProject.dll");
 observer=(Join-Path $work 'observer/Conduit.ServerObserver.dll');drainProbe=(Join-Path $work 'drain/DrainProbe.dll');catalog=$primaryCatalog;cohorts=$cohorts.ToArray()}
$configPath=Join-Path $out 'configuration.json';$config|ConvertTo-Json -Depth 8|Set-Content $configPath -Encoding utf8NoBOM
$before=Invoke-Docker @('ps','-aq');$before|Set-Content "$out/outside-containers-before.txt" -Encoding utf8NoBOM
try{
 [void](Invoke-Docker @('network','create','--label','conduit.task=dotnet11-phase2',$task))
 $pg="$task-postgres";$redis="$task-redis"
 [void](Invoke-Docker @('run','--detach','--name',$pg,'--label','conduit.task=dotnet11-phase2','--network',$task,'--publish','127.0.0.1:15476:5432',
  '--env','POSTGRES_USER=phase2','--env','POSTGRES_PASSWORD=isolated-phase2-fixture','--env','POSTGRES_DB=phase2_legacy',
  'postgres:16.15@sha256:f9a7b9ebf6f56201f949db745e47bc423c0f0f8294bafe991cc10bf74b029657'));$containers.Add($pg)
 [void](Invoke-Docker @('run','--detach','--name',$redis,'--label','conduit.task=dotnet11-phase2','--network',$task,'--publish','127.0.0.1:16476:6379',
  'redis:7.4.2-alpine@sha256:02419de7eddf55aa5bcf49efb74e88fa8d931b4d77c07eff8a6b2144472b6952'));$containers.Add($redis)
 $deadline=[DateTime]::UtcNow.AddSeconds(60)
 while($true){& docker exec $pg pg_isready -h 127.0.0.1 -U phase2 -d phase2_legacy *> $null;if($LASTEXITCODE -eq 0){break};if([DateTime]::UtcNow -gt $deadline){throw 'PostgreSQL readiness failed'};Start-Sleep -Milliseconds 500}
 foreach($database in @('phase2_fixed','phase2_candidate')){[void](Invoke-Docker @('exec',$pg,'createdb','-U','phase2',$database))}
 Invoke-Docker (@('inspect')+$containers.ToArray())|Set-Content "$out/infrastructure.json" -Encoding utf8NoBOM
 & (Join-Path $PSScriptRoot 'run-matched.ps1') -ConfigurationPath $configPath -OutputDirectory (Join-Path $out 'prepare') -Stage prepare
 foreach($database in @('phase2_legacy','phase2_fixed','phase2_candidate')){
  Get-Content -Raw (Join-Path $PSScriptRoot 'fixture-controls.sql')|& docker exec -i $pg psql -X -v ON_ERROR_STOP=1 -U phase2 -d $database *> "$out/$database-controls.log"
  if($LASTEXITCODE -ne 0){throw 'Fixture controls failed'}
 }
 $config.infrastructurePrepared=$true;$prepared=$true;$config|ConvertTo-Json -Depth 8|Set-Content $configPath -Encoding utf8NoBOM
 & (Join-Path $PSScriptRoot 'run-matched.ps1') -ConfigurationPath $configPath -OutputDirectory (Join-Path $out 'measure') -Stage measure
 if($Profile -eq 'main'){
 # Expand ordinary protocol workloads before evaluating p99 point estimates.
 # Long streams, pressure, cancellation and paced readers remain separately
 # labelled diagnostic distributions; no small sample becomes a release p99 SLO.
 $coreCases=@((Get-Content -Raw (Join-Path $PSScriptRoot 'phase0-catalog.json')|ConvertFrom-Json).cases|Where-Object{
  $_.id -in 'chat-json','standard-short','enhanced-short','standard-tools','enhanced-tools','responses-json','responses-sse'
 })
 foreach($case in $coreCases){$case.requests=1000;$case.warmupRequests=100}
 $coreCatalog=Join-Path $out 'core-catalog.json'
 [ordered]@{schemaVersion=1;profile='core';scope='1000 measured requests and100 warmups per run; per-run quantiles, not pooled';cases=$coreCases}|ConvertTo-Json -Depth 8|Set-Content $coreCatalog -Encoding utf8NoBOM
 $coreConfig=[ordered]@{};foreach($key in $config.Keys){$coreConfig[$key]=$config[$key]}
 $coreConfig.profile='core';$coreConfig.catalog=$coreCatalog;$coreConfig.cohorts=@($cohorts.ToArray()|Where-Object{$_.id -ne 'legacy'})
 $coreConfig.initialLogCounts=[ordered]@{fixed=0;candidate=0}
 $coreConfig.initialLogCountsSourceManifestSha256=(Get-FileHash (Join-Path $out 'measure/manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
 foreach($row in @(Get-Content -Raw (Join-Path $out 'measure/manifest.json')|ConvertFrom-Json)){
  if($row.cohort -eq 'legacy'){continue}
  $coreConfig.initialLogCounts[$row.cohort]+=[int]$row.warmups
  if(!$row.knownProductionFinding){$coreConfig.initialLogCounts[$row.cohort]+=[int]$row.requests}
 }
 $coreConfigPath=Join-Path $out 'core-configuration.json';$coreConfig|ConvertTo-Json -Depth 8|Set-Content $coreConfigPath -Encoding utf8NoBOM
 & (Join-Path $PSScriptRoot 'run-matched.ps1') -ConfigurationPath $coreConfigPath -OutputDirectory (Join-Path $out 'core-measure') -Stage measure
 }
}catch{$failure=$_.ToString();throw}finally{
 $cleanupFailures=[Collections.Generic.List[string]]::new()
 $evidenceFailures=[Collections.Generic.List[string]]::new()
 # Preserve financial evidence on unexpected workload failure as well as success,
 # before removing any owned infrastructure. No dump is represented as atomic.
 if($prepared){foreach($definition in $cohortDefinitions){
  try{
   & (Join-Path $PSScriptRoot 'capture-accounting.ps1') -Label $definition.id -Database "phase2_$($definition.id)" -RedisDatabase $definition.db -PostgresContainer $pg -RedisContainer $redis -OutputDirectory (Join-Path $out "accounting/$($definition.id)")
   & docker exec $pg pg_dump -U phase2 -d "phase2_$($definition.id)" 2> "$out/$($definition.id)-dump.stderr.log"|Set-Content "$out/$($definition.id)-database.sql" -Encoding utf8NoBOM
   if($LASTEXITCODE -ne 0){throw 'Owned database evidence dump failed'}
  }catch{$evidenceFailures.Add($_.ToString())}
 }}
 if($prepared){try{
  [void](Invoke-Docker @('exec',$redis,'redis-cli','SAVE'))
  [void](Invoke-Docker @('cp',"${redis}:/data/dump.rdb",(Join-Path $out 'redis.rdb')))
  Invoke-Checked 'docker' @('exec',$redis,'redis-check-rdb','/data/dump.rdb') "$out/redis-rdb-check.log"
 }catch{$evidenceFailures.Add($_.ToString())}}
 foreach($container in $containers){
  try{
   $identity=Invoke-Docker @('inspect',$container)|ConvertFrom-Json
   if($identity[0].Name -cne "/$container" -or !$container.StartsWith("$task-") -or $identity[0].Config.Labels.'conduit.task' -cne 'dotnet11-phase2'){throw 'Fixture ownership changed; refuse removal'}
   [void](Invoke-Docker @('rm','--force','--volumes',$identity[0].Id))
  }catch{$cleanupFailures.Add($_.ToString())}
 }
 & docker network inspect $task *> $null
 if($LASTEXITCODE -eq 0){try{
  $networkIdentity=@(Invoke-Docker @('network','inspect',$task)|ConvertFrom-Json)[0]
  if($networkIdentity.Name -cne $task -or $networkIdentity.Labels.'conduit.task' -cne 'dotnet11-phase2'){throw 'Network ownership changed; refuse removal'}
  [void](Invoke-Docker @('network','rm',$networkIdentity.Id))
 }catch{$cleanupFailures.Add($_.ToString())}}
 $after=Invoke-Docker @('ps','-aq');$after|Set-Content "$out/outside-containers-after.txt" -Encoding utf8NoBOM
 [ordered]@{failure=$failure;evidenceFailures=$evidenceFailures.ToArray();cleanupFailures=$cleanupFailures.ToArray();outsideContainerInventoryUnchanged=($before -ceq $after);scope='Only explicitly labelled task-owned fixtures removed; source/build/evidence retained'}|ConvertTo-Json -Depth 5|Set-Content "$out/cleanup.json" -Encoding utf8NoBOM
 if(!$failure -and ($cleanupFailures.Count -or $evidenceFailures.Count)){throw (($cleanupFailures.ToArray()+$evidenceFailures.ToArray()) -join '; ')}
}

# Interpret complete retained cohorts only after fixtures have been snapshotted
# and removed. A historical failed control is never promoted into a passing
# protocol/financial result, and a green collection job never accepts a budget.
Invoke-Checked 'python' @((Join-Path $PSScriptRoot 'summarize.py'),'--directory',(Join-Path $out 'measure'),
 '--configuration',$configPath,'--catalog',(Join-Path $out 'measure/catalog.source.json'),'--output',(Join-Path $out 'summary.json')) "$out/summarize.log"
$summary=Get-Content -Raw "$out/summary.json"|ConvertFrom-Json
$coreSummary=$null
if($Profile -eq 'main'){
 Invoke-Checked 'python' @((Join-Path $PSScriptRoot 'summarize.py'),'--directory',(Join-Path $out 'core-measure'),
  '--configuration',$coreConfigPath,'--catalog',(Join-Path $out 'core-measure/catalog.source.json'),'--output',(Join-Path $out 'core-summary.json')) "$out/core-summarize.log"
 $coreSummary=Get-Content -Raw "$out/core-summary.json"|ConvertFrom-Json
 if(!$coreSummary.collectionValidated -or !$coreSummary.allWorkloadsPassed){throw 'Candidate core protocol collection is incomplete or failed'}
}
if(!$summary.collectionValidated){throw 'Primary protocol collection is incomplete'}
$legacyFailures=@($summary.runs|Where-Object{$_.kind -eq 'known-legacy-failure'})
$expectedLegacyFailures=if($Profile -eq 'main'){$Runs}else{0}
if($legacyFailures.Count -ne $expectedLegacyFailures -or @($summary.runs|Where-Object{$_.kind -eq 'failed'}).Count){throw 'Historical protocol failure inventory changed'}
$financialResults=[Collections.Generic.List[object]]::new()
foreach($definition in $cohortDefinitions){
 $directories=@((Join-Path $out 'measure'))
 if($Profile -eq 'main' -and $definition.id -ne 'legacy'){$directories+=(Join-Path $out 'core-measure')}
 $expectation=[ordered]@{schemaVersion=1;cohort=$definition.id;postgresDatabase="phase2_$($definition.id)";redisDatabase=$definition.db;
  seedBalance='100.00000000';seedSpent='0.00000000';seedCredits='100.00000000';chatCost='0.00007500';audioCost='0.00125000';
  beforeUsageCancellationState='settled-bounded-estimate';knownResponsesFailure=$definition.known;sourceSha=$definition.sha;
  profileSources=@($directories|ForEach-Object{(Get-FileHash (Join-Path $_ 'manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()})}
 $expectationPath=Join-Path $out "$($definition.id)-financial-expectation.json"
 $expectation|ConvertTo-Json -Depth 6|Set-Content $expectationPath -Encoding utf8NoBOM
 $financialPath=Join-Path $out "$($definition.id)-financial.json"
 $arguments=@((Join-Path $PSScriptRoot 'reconcile.py'),'--snapshot',(Join-Path $out "accounting/$($definition.id)/snapshot.json"),
  '--expectation',$expectationPath,'--output',$financialPath)
 foreach($directory in $directories){$arguments+=@('--directory',$directory)}
 & python @arguments *> "$out/$($definition.id)-financial.log"
 $code=$LASTEXITCODE;$financial=Get-Content -Raw $financialPath|ConvertFrom-Json
 if($definition.known -and $expectedLegacyFailures -gt 0){
  $expectedMissing=[decimal]$Runs * [decimal]::Parse('0.00007500',[Globalization.CultureInfo]::InvariantCulture)
  if($code -ne 2 -or !$financial.recordedSpendConservationPassed -or $financial.financialInvariantsPassed -or
   $financial.legacyResponsesFailures -ne $Runs -or @($financial.failedChecks).Count -ne 1 -or
   $financial.failedChecks[0] -cne 'noCompletedProviderWithoutKnownCharge' -or
   [decimal]::Parse($financial.missingKnownProviderCharge,[Globalization.CultureInfo]::InvariantCulture) -ne $expectedMissing){throw 'Legacy financial control has an unexpected result; never waive arbitrary failures'}
 }elseif($code -ne 0 -or !$financial.financialInvariantsPassed){throw "Financial invariants failed: $($definition.id)"}
 $financialResults.Add([ordered]@{cohort=$definition.id;sourceSha=$definition.sha;financialInvariantsPassed=$financial.financialInvariantsPassed;
  recordedSpendConservationPassed=$financial.recordedSpendConservationPassed;fullyReconciled=$financial.fullyReconciled;receipt=$financialPath;
  knownPersistedSpendDrain='Each cohort/run separately passed live read-only SQL and known-spend queue drain before shutdown; snapshots do not fabricate a cross-store atomic transaction'})
}
$mutated=[Collections.Generic.List[string]]::new()
foreach($binding in $sourceBindings){foreach($file in $binding.files){
 $actual=(Get-FileHash (Join-Path $work $file.path) -Algorithm SHA256).Hash.ToLowerInvariant()
 if($actual -cne $file.sha256){$mutated.Add($file.path)}
}}
[ordered]@{passed=($mutated.Count -eq 0);mutatedFiles=$mutated.ToArray()}|ConvertTo-Json -Depth 4|Set-Content "$out/published-input-integrity.json" -Encoding utf8NoBOM
if($mutated.Count){throw 'A published application input changed during qualification'}
[ordered]@{schemaVersion=1;profile=$Profile;candidateRevision=$CandidateRevision;qualificationCheckout=(& git rev-parse HEAD).Trim();architecture=$Architecture;
 primaryCollectionValidated=$summary.collectionValidated;coreCollectionValidated=$coreSummary.collectionValidated;coreAllWorkloadsPassed=$coreSummary.allWorkloadsPassed;
 allocationGateSatisfied=$false;allocationGateStatus=$summary.allocationGateStatus;
 financial=$financialResults.ToArray();performanceAcceptanceGranted=$false;productionRolloutPerformed=$false;
 next='Review paired metrics and diagnose every working-budget breach; this successful evidence job does not grant a release/performance tradeoff or production approval'}|
 ConvertTo-Json -Depth 8|Set-Content "$out/qualification-state.json" -Encoding utf8NoBOM
