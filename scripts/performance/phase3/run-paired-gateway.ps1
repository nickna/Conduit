# Phase3 before/after adapter. Uses the unchanged, source-bound phase2 measurement/financial method.
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$CandidateRevision,
      [ValidatePattern('^[a-f0-9]{40}$')][string]$BaselineRevision='b1d49922b8dfdc6719c6d46d123f2eb5a265dcca',
      [Parameter(Mandatory)][ValidateSet('X64','Arm64')][string]$Architecture,
      [Parameter(Mandatory)][string]$OutputDirectory,
      [ValidateSet('main','pressure')][string]$Profile='main',
      [ValidateRange(3,12)][int]$Runs=5,
      [switch]$SameRevisionControl,
      [ValidateSet(0,100,1000,10000)][int]$IsolatedStandardChunks=0,
      [switch]$ExcludeIsolatedStandardCases,
      [switch]$InterleavedControl,
      [switch]$CalibrateControl)
$ErrorActionPreference='Stop'
$isolatedStandard = $IsolatedStandardChunks -ne 0
$threeArms = $isolatedStandard -or $InterleavedControl
$repetitions = if ($Profile -eq 'pressure' -and $CalibrateControl) { 12 } else { 6 }
if ($CalibrateControl -and !$threeArms) {
 throw 'Calibration requires a byte-identical control'
}
if ($InterleavedControl -and ($isolatedStandard -or $SameRevisionControl -or
 $ExcludeIsolatedStandardCases -or $Runs -ne $repetitions)) {
 throw "The interleaved control requires a full profile and $repetitions repetitions"
}
if (!$threeArms -and $Runs -gt 6) { throw 'Uncontrolled profiles support up to six repetitions' }
$standardCases = @('standard-100', 'standard-1000', 'standard-10000')
if ($ExcludeIsolatedStandardCases -and ($Architecture -ne 'X64' -or $Profile -ne 'main' -or
 $isolatedStandard -or $SameRevisionControl)) {
 throw 'Only the amd64 main candidate can exclude cases covered by its isolated performance job'
}
if ($isolatedStandard -and ($Profile -ne 'main' -or $Runs -ne 6 -or $SameRevisionControl)) {
 throw 'An isolated standard SSE case requires six three-cohort repetitions'
}
if(!$IsLinux -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() -cne $Architecture){throw 'Require matching native Linux hardware'}
$repository=(& git rev-parse --show-toplevel).Trim()
$qualificationRoot=Join-Path $repository 'scripts/qualification'
if($LASTEXITCODE -ne 0){throw 'Require a Git checkout'}
$cpuPlacement = $null
$fixtureCpuArgs = @()
if ($CalibrateControl) {
 . (Join-Path $qualificationRoot 'gateway-cpu-placement.ps1')
 $cpuPlacement = Get-GatewayCpuPlacement
 $taskset = (Get-Command taskset -CommandType Application | Select-Object -First 1).Source
 $pwsh = (Get-Process -Id $PID).Path
 $fixtureCpuArgs = @('--cpuset-cpus', $cpuPlacement.fixture)
}
& git merge-base --is-ancestor $CandidateRevision HEAD
if($LASTEXITCODE -ne 0){throw 'Candidate must be an ancestor of the qualification checkout'}
& git merge-base --is-ancestor $BaselineRevision HEAD
if($LASTEXITCODE -ne 0){throw 'Baseline must be an ancestor of the qualification checkout'}
if(($CandidateRevision -ceq $BaselineRevision) -ne $SameRevisionControl.IsPresent){
 throw 'Only the same-revision control can compare identical revisions'
}
if((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count){throw 'Use a fresh output directory; never overwrite previous qualification evidence'}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$out=(Resolve-Path $OutputDirectory).Path
$harnessSource=Join-Path $out 'harness-source';New-Item -ItemType Directory -Path $harnessSource|Out-Null
$sourceFiles=@(Get-ChildItem $qualificationRoot -Recurse -File -Force|Where-Object{
 $_.FullName -notmatch '[\\/](bin|obj|published|__pycache__)[\\/]' -and $_.Extension -in @('.ps1','.py','.cs','.csproj','.props','.targets','.json','.sql','.md')
})
$harnessBindings=@(foreach($file in $sourceFiles){
 $relative=[IO.Path]::GetRelativePath($qualificationRoot,$file.FullName);$copy=Join-Path $harnessSource $relative
 New-Item -ItemType Directory -Path (Split-Path $copy) -Force|Out-Null
 Copy-Item -LiteralPath $file.FullName -Destination $copy
 [ordered]@{path=$relative;bytes=$file.Length;sha256=(Get-FileHash $copy -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$harnessBindings|ConvertTo-Json -Depth 4|Set-Content "$out/harness-source-bindings.json" -Encoding utf8NoBOM
Copy-Item -LiteralPath $PSCommandPath -Destination "$out/phase3-driver.source.ps1"
[ordered]@{schemaVersion=1;baselineRevision=$BaselineRevision;candidateRevision=$CandidateRevision;originalPhase2DriverRevision='b1d49922b8dfdc6719c6d46d123f2eb5a265dcca';originalPhase2DriverPath='scripts/qualification/run-runtime-matrix.ps1';originalPhase2DriverCanonicalSha256='25313e5cb7a4b698fcfc777964b0a253166d895b81f9780ef58db26f2f7cd909';originalPhase2DriverWorkspaceSha256=(Get-FileHash (Join-Path $qualificationRoot 'run-runtime-matrix.ps1') -Algorithm SHA256).Hash.ToLowerInvariant();driverSha256=(Get-FileHash $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant();fixtureNamespaceCompatibility='Existing phase2 task labels/database names required by unchanged capture probe; experiment remains phase3 and separate from historical artifacts';sourceSdk='11.0.100';actualRuntimeExpected='11.0.0';performanceAcceptanceGranted=$false}|ConvertTo-Json -Depth 4|Set-Content "$out/phase3-method.json" -Encoding utf8NoBOM
$work=Join-Path $out 'work';New-Item -ItemType Directory -Path $work | Out-Null
$task="conduit-phase2-phase3-runtime-$([Guid]::NewGuid().ToString('N').Substring(0,12))"
$containers=[Collections.Generic.List[string]]::new();$failure=$null;$prepared=$false
$cohortDefinitions=@(
 @{id='fixed';sha=$BaselineRevision;sdk='11.0.100';runtime='11.0.0';db=3;known=$false},
 @{id='candidate';sha=$CandidateRevision;sdk='11.0.100';runtime='11.0.0';db=4;known=$false})
if ($threeArms) {
 # The capture tool reserves these three database names. Here legacy is a
 # byte-identical copy of the .NET 11 fixed arm, with its own fixture state.
 $cohortDefinitions = @($cohortDefinitions[0],
  @{id='legacy';sha=$BaselineRevision;sdk='11.0.100';runtime='11.0.0';db=2;known=$false},
  $cohortDefinitions[1])
}
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
function Invoke-MatchedStage([string]$Configuration, [string]$Directory, [string]$Stage) {
 $driver = Join-Path $qualificationRoot 'run-matched.ps1'
 if (!$cpuPlacement) {
  & $driver -ConfigurationPath $Configuration -OutputDirectory $Directory -Stage $Stage
  return
 }
 & $taskset --cpu-list $cpuPlacement.fixture $pwsh -NoLogo -NoProfile -File $driver `
  -ConfigurationPath $Configuration -OutputDirectory $Directory -Stage $Stage
 if ($LASTEXITCODE -ne 0) { throw "CPU-pinned Gateway $Stage failed. Inspect $Directory." }
}
function Get-PublishedInputDelta {
 $expected=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
 foreach($binding in $sourceBindings){foreach($file in $binding.files){$expected.Add($file.path.Replace([char]92,[char]47),$file)}}
 $actual=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
 $errors=[Collections.Generic.List[string]]::new()
 foreach($folder in @($expected.Keys|ForEach-Object{$_.Split('/')[0]}|Sort-Object -Unique)){
  $directory=Join-Path $work $folder
  if(Test-Path -LiteralPath $directory -PathType Container){
   foreach($file in @(Get-ChildItem -LiteralPath $directory -Recurse -File -Force)){
    $relative=[IO.Path]::GetRelativePath($work,$file.FullName).Replace([char]92,[char]47)
    $actual.Add($relative,$file)
   }
  }
 }
 $added=@($actual.Keys|Where-Object{!$expected.ContainsKey($_)})
 $missing=@($expected.Keys|Where-Object{!$actual.ContainsKey($_)})
 $modified=@(foreach($path in $expected.Keys){if($actual.ContainsKey($path)){
  try{if($actual[$path].Length -ne $expected[$path].bytes -or (Get-FileHash -LiteralPath $actual[$path].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected[$path].sha256){$path}}
  catch{$errors.Add("Cannot verify published input: $path")}
 }})
 return [pscustomobject]@{passed=(!$added.Count -and !$missing.Count -and !$modified.Count -and !$errors.Count);addedFiles=$added;missingFiles=$missing;modifiedFiles=$modified;errors=$errors.ToArray();expectedFiles=$expected.Count;actualFiles=$actual.Count}
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
 if (($SameRevisionControl -or $threeArms) -and $definition.id -cne 'fixed' -and $definition.sha -ceq $BaselineRevision) {
  Copy-Item -Recurse -LiteralPath (Join-Path $work 'fixed-gateway') -Destination $gateway
  Copy-Item -Recurse -LiteralPath (Join-Path $work 'fixed-migrator') -Destination $migrator
 }else{
  Publish-Project $source 'Services/ConduitLLM.Gateway/ConduitLLM.Gateway.csproj' $gateway "$out/$($definition.id)-gateway-build.log" $definition.sdk $definition.sha
  Publish-Project $source 'tools/ConduitLLM.Migrator/ConduitLLM.Migrator.csproj' $migrator "$out/$($definition.id)-migrator-build.log" $definition.sdk $definition.sha
 }
 $sourceBindings.Add([ordered]@{id=$definition.id;sourceSha=$definition.sha;sourceSdk=$definition.sdk;sourceTree=(& git rev-parse "$($definition.sha)^{tree}").Trim();
  archiveSha256=(Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant();
  files=@(foreach($directory in @($gateway,$migrator)){
   Get-ChildItem $directory -Recurse -File -Force|ForEach-Object{
    [ordered]@{path=$_.FullName.Substring($work.Length+1);sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant();bytes=$_.Length}
   }
  })})
 $cohorts.Add([ordered]@{id=$definition.id;sourceSha=$definition.sha;sourceSdk=$definition.sdk;expectedRuntime=$definition.runtime;dotnet=(Get-Command dotnet).Source;
  gateway=(Join-Path $gateway 'ConduitLLM.Gateway.dll');migrator=(Join-Path $migrator 'ConduitLLM.Migrator.dll');
  seedProbe=(Join-Path $work 'seed/ConduitLLM.GatewayNativeAotTests.dll');databaseUrl="postgresql://phase2:isolated-phase2-fixture@127.0.0.1:15476/phase2_$($definition.id)";
  redisUrl="redis://127.0.0.1:16476/$($definition.db)";knownResponsesFailure=$definition.known})
}
$sourceBindings.ToArray()|ConvertTo-Json -Depth 8|Set-Content "$out/source-bindings.json" -Encoding utf8NoBOM
if ($threeArms) {
 $referenceFiles = @($sourceBindings[0].files | ForEach-Object {
  $_.path.Substring('fixed'.Length) + ':' + $_.bytes + ':' + $_.sha256
 })
 $controlFiles = @($sourceBindings[1].files | ForEach-Object {
  $_.path.Substring('legacy'.Length) + ':' + $_.bytes + ':' + $_.sha256
 })
 if (Compare-Object $referenceFiles $controlFiles -CaseSensitive) {
  throw 'The byte-identical control differs from the published baseline'
 }
}
$seedSource=Join-Path $work 'seed-source';New-Item -ItemType Directory -Path $seedSource|Out-Null
$seedArchive=Join-Path $work 'seed-source.tar'
Invoke-Checked 'git' @('archive','--format=tar',"--output=$seedArchive",'f68cb5fae1130bbe1ef4fc30aca7a3266094dbaa') "$out/seed-archive.log"
Invoke-Checked 'tar' @('-xf',$seedArchive,'-C',$seedSource) "$out/seed-extract.log"
Publish-Project $seedSource 'Tests/ConduitLLM.GatewayNativeAotTests/ConduitLLM.GatewayNativeAotTests.csproj' (Join-Path $work 'seed') "$out/seed-build.log" '10.0.404' 'f68cb5fae1130bbe1ef4fc30aca7a3266094dbaa'
$clientProject=if($Profile -eq 'pressure'){'Pressure'}else{'Workload'}
Publish-Project (Join-Path $qualificationRoot $clientProject) "$clientProject.csproj" (Join-Path $work 'client') "$out/client-build.log" '10.0.404'
Publish-Project (Join-Path $qualificationRoot 'ProviderV2') 'Conduit.ProviderV2.csproj' (Join-Path $work 'provider') "$out/provider-build.log" '10.0.404'
Publish-Project (Join-Path $qualificationRoot 'ObserverV2') 'Conduit.ServerObserver.csproj' (Join-Path $work 'observer') "$out/observer-build.log" '10.0.404'
Publish-Project (Join-Path $qualificationRoot 'Drain') 'DrainProbe.csproj' (Join-Path $work 'drain') "$out/drain-build.log" '10.0.404'
$sourceBindings.Add([ordered]@{id='fixed-tools';sourceSdk='10.0.404';files=@(
 foreach($name in @('seed','client','provider','observer','drain')){
  Get-ChildItem (Join-Path $work $name) -Recurse -File -Force|ForEach-Object{
   [ordered]@{path=[IO.Path]::GetRelativePath($work,$_.FullName);sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant();bytes=$_.Length}
  }
 }
)})
$sourceBindings.ToArray()|ConvertTo-Json -Depth 8|Set-Content "$out/source-bindings.json" -Encoding utf8NoBOM
$primaryCatalog=Join-Path $qualificationRoot 'pressure-native-catalog.json'
if ($Profile -eq 'pressure' -and $InterleavedControl) {
 $pressureCatalog = Get-Content -Raw -LiteralPath $primaryCatalog | ConvertFrom-Json
 $pressureCatalog.repetitions = $Runs
 $pressureCatalog.order = "$Runs repetitions of the original four pressure cases in balanced cohort orders."
 $pressureCatalog | Add-Member -NotePropertyName phase3BaseCatalogSha256 -NotePropertyValue (Get-FileHash -LiteralPath $primaryCatalog).Hash.ToLowerInvariant()
 $primaryCatalog = Join-Path $out 'pressure-catalog.json'
 $pressureCatalog | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $primaryCatalog -Encoding utf8NoBOM
}
if($Profile -eq 'main'){
 # Keep endpoints, payloads and concurrency identical to the frozen catalog.
 # The short windows of the original fixture could breach working thresholds
 # even when comparing identical binaries. Use longer warmups and more requests
 # to reduce cold tiering and single-request leverage without relaxing budgets.
 $catalogSource=Join-Path $qualificationRoot 'phase0-catalog.json'
 $mainCatalog=Get-Content -Raw -LiteralPath $catalogSource|ConvertFrom-Json
 $mainCatalog.repetitions=$Runs
 $mainCatalog.warmupMethod='Separate warmup client invocation excluded from the measured window; extended counts for phase 3'
 $mainCatalog | Add-Member -NotePropertyName phase3BaseCatalogSha256 -NotePropertyValue (Get-FileHash -LiteralPath $catalogSource).Hash.ToLowerInvariant()
 foreach($case in $mainCatalog.cases){
  # Slow-reader and cancellation cases retain catalog counts unless calibrated below.
  if($case.slowReadDelayMilliseconds -or $case.cancel){continue}
  if($case.requests -ge 100){
   $case.requests=300
   $case.warmupRequests=100
  }else{
   $case.requests*=3
   $case.warmupRequests=[Math]::Max($case.warmupRequests*3, $case.concurrency)
  }
 }
 if ($CalibrateControl -and !$isolatedStandard) {
  # Extend short windows that repeatedly produced unstable A/A estimates.
  # Preserve each payload, concurrency, pacing, and workload position.
  $sampleCounts = @{
   'responses-json' = @(600, 150)
   'standard-100' = @(180, 40)
   'enhanced-100' = @(180, 40)
   'standard-1000' = @(72, 16)
   'enhanced-1000' = @(72, 16)
   'standard-10000' = @(18, 6)
   'enhanced-10000' = @(18, 6)
   # Count tiered compilation during early cancellations in the full CPU window.
   'cancel-before-usage' = @(110, 1)
   'audio-1MiB-c1' = @(180, 30)
   'audio-1MiB-c4' = @(180, 30)
   'audio-8MiB-c1' = @(90, 15)
   'audio-8MiB-c4' = @(90, 15)
  }
  foreach ($case in $mainCatalog.cases) {
   if ($sampleCounts.ContainsKey($case.id)) {
    $case.requests, $case.warmupRequests = $sampleCounts[$case.id]
   }
  }
 }
 if ($isolatedStandard) {
  $mainCatalog.cases = @($mainCatalog.cases | Where-Object id -ceq "standard-$IsolatedStandardChunks")
  if ($mainCatalog.cases.Count -ne 1) { throw 'Expected one frozen standard SSE workload' }
  $sampleCounts = @{
   100 = @{requests=240;warmups=40}
   1000 = @{requests=120;warmups=24}
   10000 = @{requests=60;warmups=12}
  }[$IsolatedStandardChunks]
  if ($CalibrateControl -and $IsolatedStandardChunks -eq 100) {
   $sampleCounts = @{requests = 600; warmups = 80}
  } elseif ($CalibrateControl -and $IsolatedStandardChunks -eq 1000) {
   $sampleCounts = @{requests = 480; warmups = 80}
  } elseif ($CalibrateControl -and $IsolatedStandardChunks -eq 10000) {
   $sampleCounts = @{requests = 240; warmups = 12}
  }
  $mainCatalog.cases[0].requests = $sampleCounts.requests
  $mainCatalog.cases[0].warmupRequests = $sampleCounts.warmups
 }
 if ($ExcludeIsolatedStandardCases) {
  $covered = @($mainCatalog.cases | Where-Object id -cin $standardCases)
  if ($covered.Count -ne $standardCases.Count) { throw 'The isolated standard workload inventory changed' }
  $mainCatalog.cases = @($mainCatalog.cases | Where-Object id -cnotin $standardCases)
 }
 $primaryCatalog=Join-Path $out 'main-catalog.json'
 $mainCatalog|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $primaryCatalog -Encoding utf8NoBOM
}
$config=[ordered]@{schemaVersion=1;profile=$(if($Profile -eq 'pressure'){'pressure64'}else{'phase0'});diagnostic=$false;runs=$Runs;architecture=$Architecture;workingDirectory=$repository;infrastructurePrepared=$false;
 baselineRevision=$BaselineRevision;sameRevisionControl=$SameRevisionControl.IsPresent;requireStreamQuiescence=$true;comparisons=@(@{baseline='fixed';candidate='candidate'});
 scope='Phase 3: two .NET11 SDK11.0.100/runtime11.0.0 native JIT sources on one runner. Fixed is the retained before source, not a .NET10 control. Phase2 fixture namespaces are reused solely to preserve unchanged read-only capture/drain contracts. No original dataset is modified.';
 clientDotnet=(Get-Command dotnet).Source;workload=(Join-Path $work "client/Conduit.Phase0.$clientProject.dll");
 provider=(Join-Path $work 'provider/Conduit.Qualification.ProviderV2.dll');providerObservationVersion=2;
 observer=(Join-Path $work 'observer/Conduit.ServerObserver.dll');drainProbe=(Join-Path $work 'drain/DrainProbe.dll');catalog=$primaryCatalog;cohorts=$cohorts.ToArray()}
if ($threeArms) {
 $config.comparisons += @(
  @{baseline='fixed';candidate='legacy'},
  @{baseline='legacy';candidate='fixed'},
  @{baseline='legacy';candidate='candidate'})
}
if ($isolatedStandard) {
 $config.profile = 'sse-isol'
 $config.scope = 'One standard SSE workload with a fresh Gateway per arm and repetition. Three matched .NET 11 arms with independent seeded databases. The legacy arm copies the baseline binaries.'
}
if ($InterleavedControl) {
 $config.interleavedControl = $true
 $config.scope = "Full Gateway $Profile workloads on one native runner with three balanced .NET 11 arms. The legacy arm copies the baseline binaries and has separate seeded fixture state."
}
if ($cpuPlacement) { $config.cpuPlacement = $cpuPlacement }
$configPath=Join-Path $out 'configuration.json';$config|ConvertTo-Json -Depth 8|Set-Content $configPath -Encoding utf8NoBOM
$before=Invoke-Docker @('ps','-aq');$before|Set-Content "$out/outside-containers-before.txt" -Encoding utf8NoBOM
try{
 [void](Invoke-Docker @('network','create','--label','conduit.task=dotnet11-phase2',$task))
 $pg="$task-postgres";$redis="$task-redis"
 [void](Invoke-Docker (@('run','--detach','--name',$pg,'--label','conduit.task=dotnet11-phase2','--network',$task) + $fixtureCpuArgs + @('--publish','127.0.0.1:15476:5432',
  '--env','POSTGRES_USER=phase2','--env','POSTGRES_PASSWORD=isolated-phase2-fixture','--env','POSTGRES_DB=phase2_fixed',
  'postgres:16.15@sha256:f9a7b9ebf6f56201f949db745e47bc423c0f0f8294bafe991cc10bf74b029657')));$containers.Add($pg)
 [void](Invoke-Docker (@('run','--detach','--name',$redis,'--label','conduit.task=dotnet11-phase2','--network',$task) + $fixtureCpuArgs + @('--publish','127.0.0.1:16476:6379',
  'redis:7.4.2-alpine@sha256:02419de7eddf55aa5bcf49efb74e88fa8d931b4d77c07eff8a6b2144472b6952')));$containers.Add($redis)
 $deadline=[DateTime]::UtcNow.AddSeconds(60)
 while($true){& docker exec $pg pg_isready -h 127.0.0.1 -U phase2 -d phase2_fixed *> $null;if($LASTEXITCODE -eq 0){break};if([DateTime]::UtcNow -gt $deadline){throw 'PostgreSQL readiness failed'};Start-Sleep -Milliseconds 500}
 foreach($database in @($cohortDefinitions | Where-Object id -cne 'fixed' | ForEach-Object { "phase2_$($_.id)" })){[void](Invoke-Docker @('exec',$pg,'createdb','-U','phase2',$database))}
 Invoke-Docker (@('inspect')+$containers.ToArray())|Set-Content "$out/infrastructure.json" -Encoding utf8NoBOM
 Invoke-MatchedStage $configPath (Join-Path $out 'prepare') 'prepare'
 foreach($database in @($cohortDefinitions | ForEach-Object { "phase2_$($_.id)" })){
  Get-Content -Raw (Join-Path $qualificationRoot 'fixture-controls.sql')|& docker exec -i $pg psql -X -v ON_ERROR_STOP=1 -U phase2 -d $database *> "$out/$database-controls.log"
  if($LASTEXITCODE -ne 0){throw 'Fixture controls failed'}
 }
 $config.infrastructurePrepared=$true;$prepared=$true;$config|ConvertTo-Json -Depth 8|Set-Content $configPath -Encoding utf8NoBOM
 Invoke-MatchedStage $configPath (Join-Path $out 'measure') 'measure'
 if ($Profile -eq 'main' -and !$isolatedStandard) {
 # Expand ordinary protocol workloads before evaluating p99 point estimates.
 # Long streams, pressure, cancellation and paced readers remain separately
 # labelled diagnostic distributions; no small sample becomes a release p99 SLO.
 $coreCases=@((Get-Content -Raw (Join-Path $qualificationRoot 'phase0-catalog.json')|ConvertFrom-Json).cases|Where-Object{
  $_.id -in 'chat-json','standard-short','enhanced-short','standard-tools','enhanced-tools','responses-json','responses-sse'
 })
 # The frozen Workload client accepts at most 1,000 requests per invocation.
 foreach($case in $coreCases){$case.requests=1000;$case.warmupRequests=100}
 $coreCatalog=Join-Path $out 'core-catalog.json'
 [ordered]@{schemaVersion=1;profile='core';scope='1000 measured requests and 100 warmups per run; per-run quantiles, not pooled';cases=$coreCases}|ConvertTo-Json -Depth 8|Set-Content $coreCatalog -Encoding utf8NoBOM
 $coreConfig=[ordered]@{};foreach($key in $config.Keys){$coreConfig[$key]=$config[$key]}
 $coreConfig.profile='core';$coreConfig.catalog=$coreCatalog;$coreConfig.cohorts=$cohorts.ToArray()
 $coreConfig.initialLogCounts=[ordered]@{}
 foreach($definition in $cohortDefinitions){$coreConfig.initialLogCounts[$definition.id]=0}
 $coreConfig.initialLogCountsSourceManifestSha256=(Get-FileHash (Join-Path $out 'measure/manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
 foreach($row in @(Get-Content -Raw (Join-Path $out 'measure/manifest.json')|ConvertFrom-Json)){
  $coreConfig.initialLogCounts[$row.cohort]+=[int]$row.warmups
  if(!$row.knownProductionFinding){$coreConfig.initialLogCounts[$row.cohort]+=[int]$row.requests}
 }
 $coreConfigPath=Join-Path $out 'core-configuration.json';$coreConfig|ConvertTo-Json -Depth 8|Set-Content $coreConfigPath -Encoding utf8NoBOM
 Invoke-MatchedStage $coreConfigPath (Join-Path $out 'core-measure') 'measure'
 }
}catch{$failure=$_.ToString();throw}finally{
 $cleanupFailures=[Collections.Generic.List[string]]::new()
 $evidenceFailures=[Collections.Generic.List[string]]::new()
 # Preserve financial evidence on unexpected workload failure as well as success,
 # before removing any owned infrastructure. No dump is represented as atomic.
 if($prepared){foreach($definition in $cohortDefinitions){
  try{
   & (Join-Path $qualificationRoot 'capture-accounting.ps1') -Label $definition.id -Database "phase2_$($definition.id)" -RedisDatabase $definition.db -PostgresContainer $pg -RedisContainer $redis -OutputDirectory (Join-Path $out "accounting/$($definition.id)")
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
# and removed. Both sources must pass every protocol and financial invariant;
# a green evidence-collection job does not accept a performance budget.
Invoke-Checked 'python' @((Join-Path $qualificationRoot 'summarize.py'),'--directory',(Join-Path $out 'measure'),
 '--configuration',$configPath,'--catalog',(Join-Path $out 'measure/catalog.source.json'),'--output',(Join-Path $out 'summary.json')) "$out/summarize.log"
$summary=Get-Content -Raw "$out/summary.json"|ConvertFrom-Json
$coreSummary=$null
if ($Profile -eq 'main' -and !$isolatedStandard) {
 Invoke-Checked 'python' @((Join-Path $qualificationRoot 'summarize.py'),'--directory',(Join-Path $out 'core-measure'),
  '--configuration',$coreConfigPath,'--catalog',(Join-Path $out 'core-measure/catalog.source.json'),'--output',(Join-Path $out 'core-summary.json')) "$out/core-summarize.log"
 $coreSummary=Get-Content -Raw "$out/core-summary.json"|ConvertFrom-Json
 if(!$coreSummary.collectionValidated -or !$coreSummary.allWorkloadsPassed){throw 'Candidate core protocol collection is incomplete or failed'}
}
if(!$summary.collectionValidated){throw 'Primary protocol collection is incomplete'}
$legacyFailures=@($summary.runs|Where-Object{$_.kind -eq 'known-legacy-failure'})
$expectedLegacyFailures=0 # Both phase3 sources are the Responses-fixed .NET11 implementation.
if($legacyFailures.Count -ne $expectedLegacyFailures -or @($summary.runs|Where-Object{$_.kind -eq 'failed'}).Count){throw 'Phase3 sources must pass every protocol'}
$financialResults=[Collections.Generic.List[object]]::new()
foreach($definition in $cohortDefinitions){
 $directories=@((Join-Path $out 'measure'))
 if ($Profile -eq 'main' -and !$isolatedStandard) {$directories+=(Join-Path $out 'core-measure')}
 $expectation=[ordered]@{schemaVersion=1;cohort=$definition.id;postgresDatabase="phase2_$($definition.id)";redisDatabase=$definition.db;
  seedBalance='100.00000000';seedSpent='0.00000000';seedCredits='100.00000000';chatCost='0.00007500';audioCost='0.00125000';
  beforeUsageCancellationState='settled-bounded-estimate';knownResponsesFailure=$definition.known;sourceSha=$definition.sha;
  profileSources=@($directories|ForEach-Object{(Get-FileHash (Join-Path $_ 'manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()})}
 $expectationPath=Join-Path $out "$($definition.id)-financial-expectation.json"
 $expectation|ConvertTo-Json -Depth 6|Set-Content $expectationPath -Encoding utf8NoBOM
 $financialPath=Join-Path $out "$($definition.id)-financial.json"
 $arguments=@((Join-Path $qualificationRoot 'reconcile.py'),'--snapshot',(Join-Path $out "accounting/$($definition.id)/snapshot.json"),
  '--expectation',$expectationPath,'--output',$financialPath)
 foreach($directory in $directories){$arguments+=@('--directory',$directory)}
 & python @arguments *> "$out/$($definition.id)-financial.log"
 $code=$LASTEXITCODE;$financial=Get-Content -Raw $financialPath|ConvertFrom-Json
 if($code -ne 0 -or !$financial.financialInvariantsPassed){throw "Financial invariants failed: $($definition.id)"}
 $financialResults.Add([ordered]@{cohort=$definition.id;sourceSha=$definition.sha;financialInvariantsPassed=$financial.financialInvariantsPassed;
  recordedSpendConservationPassed=$financial.recordedSpendConservationPassed;fullyReconciled=$financial.fullyReconciled;receipt=$financialPath;
  knownPersistedSpendDrain='Each cohort/run separately passed live read-only SQL and known-spend queue drain before shutdown; snapshots do not fabricate a cross-store atomic transaction'})
}
$inputDelta=Get-PublishedInputDelta
$inputDelta|ConvertTo-Json -Depth 4|Set-Content "$out/published-input-integrity.json" -Encoding utf8NoBOM
if(!$inputDelta.passed){throw 'Published input file set or content changed during qualification'}
[ordered]@{schemaVersion=1;profile=$Profile;isolatedStandardChunks=$IsolatedStandardChunks;interleavedControl=$InterleavedControl.IsPresent;baselineRevision=$BaselineRevision;candidateRevision=$CandidateRevision;sameRevisionControl=$SameRevisionControl.IsPresent;qualificationCheckout=(& git rev-parse HEAD).Trim();architecture=$Architecture;
 primaryCollectionValidated=$summary.collectionValidated;coreCollectionValidated=$coreSummary.collectionValidated;coreAllWorkloadsPassed=$coreSummary.allWorkloadsPassed;
 allocationGateSatisfied=$false;allocationGateStatus=$summary.allocationGateStatus;
 financial=$financialResults.ToArray();performanceAcceptanceGranted=$false;productionRolloutPerformed=$false;
 next='Review paired metrics and diagnose every working-budget breach; this successful evidence job does not grant a release/performance tradeoff or production approval'}|
 ConvertTo-Json -Depth 8|Set-Content "$out/qualification-state.json" -Encoding utf8NoBOM
