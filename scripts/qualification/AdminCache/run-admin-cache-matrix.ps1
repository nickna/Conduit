[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$CandidateRevision,
      [Parameter(Mandatory)][ValidateSet('X64','Arm64')][string]$Architecture,
      [Parameter(Mandatory)][string]$OutputDirectory,
      [ValidateRange(3,6)][int]$Runs=5,
      [switch]$FunctionalSmoke,
      [switch]$BuildOnly,
      [string]$LegacyDotnet=(Get-Command dotnet).Source,
      [string]$CandidateDotnet=(Get-Command dotnet).Source,
      [string]$ObserverProject=(Join-Path $PSScriptRoot '../ObserverV2/Conduit.ServerObserver.csproj'))
$ErrorActionPreference='Stop'
if(!$IsLinux -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() -cne $Architecture){throw 'Require the requested native Linux process architecture'}
$repository=(& git rev-parse --show-toplevel).Trim();if($LASTEXITCODE -ne 0){throw 'Require Git checkout'}
& git merge-base --is-ancestor $CandidateRevision HEAD;if($LASTEXITCODE -ne 0){throw 'Candidate must be an ancestor of the qualification checkout'}
if((Test-Path $OutputDirectory) -and @(Get-ChildItem $OutputDirectory -Force).Count){throw 'Fresh output directory required'}
New-Item -ItemType Directory -Force -Path $OutputDirectory|Out-Null;$out=(Resolve-Path $OutputDirectory).Path
$work=Join-Path $out 'work';New-Item -ItemType Directory -Path $work|Out-Null
$task='conduit-phase2-admin-cache-'+[Guid]::NewGuid().ToString('N').Substring(0,12)
$containers=[Collections.Generic.List[string]]::new();$cohorts=[Collections.Generic.List[object]]::new();$bindings=[Collections.Generic.List[object]]::new()
$network=$null;$failure=$null;$prepared=$false
$dockerExecutable=(Get-Command docker -CommandType Application|Select-Object -First 1).Source
$gcOverrides=@(Get-ChildItem Env:|Where-Object{$_.Name -match '^(DOTNET|COMPlus)_(GC|gc|Heap|heap)'})
$gcOverrides|Select-Object Name,Value|ConvertTo-Json -AsArray|Set-Content "$out/external-gc-overrides.json" -Encoding utf8NoBOM
if($gcOverrides.Count){throw 'External GC overrides are not permitted for the default-GC matrix'}
$sourceCopy=Join-Path $out 'harness-source';New-Item -ItemType Directory -Path $sourceCopy|Out-Null
foreach($name in @('run-admin-cache-matrix.ps1','run-admin-cache.ps1','summarize.py','test_summarize.py','README.txt')){Copy-Item (Join-Path $PSScriptRoot $name) $sourceCopy}
foreach($name in @('frozen','MicroObserver')){$destination=Join-Path $sourceCopy $name;New-Item -ItemType Directory -Path $destination|Out-Null;Get-ChildItem (Join-Path $PSScriptRoot $name) -File|Copy-Item -Destination $destination}
Copy-Item $PSCommandPath "$out/run-admin-cache-matrix.source.ps1"
Copy-Item (Join-Path $PSScriptRoot 'frozen/origin.json') "$out/frozen-source-origin.json"
foreach($origin in @(Get-Content (Join-Path $PSScriptRoot 'frozen/origin.json') -Raw|ConvertFrom-Json).files){
 $current=(Get-FileHash (Join-Path $PSScriptRoot "frozen/$($origin.copy)") -Algorithm SHA256).Hash.ToLowerInvariant()
 if($current -cne $origin.sha256){throw 'Frozen source was modified'}
}
function Checked([string]$Program,[string[]]$Arguments,[string]$Log){& $Program @Arguments *> $Log;if($LASTEXITCODE -ne 0){throw "Command failed; inspect $Log"}}
function Docker([string[]]$Arguments){$value=(& $dockerExecutable @Arguments)-join "`n";if($LASTEXITCODE -ne 0){throw "Docker $($Arguments[0]) failed"};return $value}
function Publish([string]$Source,[string]$Project,[string]$Destination,[string]$Log,[string]$Dotnet,[string]$Sdk,[string]$Revision='', [bool]$BuildReferences=$true){
 $toolchain=Join-Path $work "toolchains/$Sdk"
 New-Item -ItemType Directory -Force -Path $toolchain|Out-Null
 [ordered]@{sdk=[ordered]@{version=$Sdk;rollForward='disable';allowPrerelease=$false}}|ConvertTo-Json|Set-Content "$toolchain/global.json" -Encoding utf8NoBOM
 $projectPath=Join-Path $Source $Project
 Push-Location $toolchain
 try{
  $selected=(& $Dotnet --version)-join "`n";if($LASTEXITCODE -ne 0 -or $selected.Trim() -cne $Sdk){throw "Require compilerSDK $Sdk, got $selected"}
  Checked $Dotnet @('--info') "$Log.toolchain.txt"
  $arguments=@('publish',$projectPath,'-c','Release','-o',$Destination,'-m:1','-nr:false','-p:UseSharedCompilation=false')
  if($Revision){$arguments+="-p:ConduitCommitSha=$Revision"}
  if(!$BuildReferences){$arguments+='-p:BuildProjectReferences=false'}
  $arguments|ConvertTo-Json|Set-Content "$Log.arguments.json" -Encoding utf8NoBOM
  Checked $Dotnet $arguments $Log
 }finally{Pop-Location}
}
function New-MicroProject([string]$Kind,[string]$Source,[string]$Destination,[string]$Sdk,[string]$Framework){
 New-Item -ItemType Directory -Force -Path $Destination|Out-Null
 '<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><Version>3.0.0</Version><AssemblyVersion>3.0.0</AssemblyVersion><FileVersion>3.0.0</FileVersion></PropertyGroup></Project>'|Set-Content "$Destination/Directory.Build.props" -Encoding utf8NoBOM
 '<Project />'|Set-Content "$Destination/Directory.Build.targets" -Encoding utf8NoBOM
 [ordered]@{sdk=[ordered]@{version=$Sdk;rollForward='disable';allowPrerelease=$false}}|ConvertTo-Json|Set-Content "$Destination/global.json" -Encoding utf8NoBOM
 $name=if($Kind -eq 'cache'){'Conduit.CacheMeasurements'}else{'Conduit.CollectorMeasurements'}
 $frozen=Join-Path $PSScriptRoot $(if($Kind -eq 'cache'){'frozen/Cache.Program.cs'}else{'frozen/Collector.Program.cs'})
 $refs='<ProjectReference Include="'+[Security.SecurityElement]::Escape((Join-Path $Source 'Shared/ConduitLLM.Core/ConduitLLM.Core.csproj'))+'" />'
 if($Kind -eq 'cache'){$refs+='<ProjectReference Include="'+[Security.SecurityElement]::Escape((Join-Path $Source 'Shared/ConduitLLM.Persistence.Composition/ConduitLLM.Persistence.Composition.csproj'))+'" />'}
 $project='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>'+ $Framework+'</TargetFramework><AssemblyName>'+$name+'</AssemblyName><JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="'+[Security.SecurityElement]::Escape($frozen)+'" Link="Program.cs" />'+$refs+'</ItemGroup></Project>'
 $project|Set-Content "$Destination/$name.csproj" -Encoding utf8NoBOM
 return "$name.csproj"
}
function Copy-HarnessProject([string]$Source,[string]$Destination){
 New-Item -ItemType Directory -Path $Destination|Out-Null
 foreach($file in @(Get-ChildItem $Source -File|Where-Object{$_.Extension -in '.cs','.csproj','.props','.targets' -or $_.Name -eq 'global.json'})){
  Copy-Item $file.FullName (Join-Path $Destination $file.Name)
 }
}
$definitions=@(
 @{id='legacy';sha='f68cb5fae1130bbe1ef4fc30aca7a3266094dbaa';sdk='10.0.404';framework='net10.0';runtime='10.0.15';dotnet=$LegacyDotnet;db=2},
 @{id='fixed';sha='911d7b6bbe54b4a6b77058b75ee2a86bc2d59097';sdk='10.0.404';framework='net10.0';runtime='10.0.15';dotnet=$LegacyDotnet;db=3},
 @{id='candidate';sha=$CandidateRevision;sdk='11.0.100';framework='net11.0';runtime='11.0.0';dotnet=$CandidateDotnet;db=4})
foreach($definition in $definitions){
 $source=Join-Path $work $definition.id;New-Item -ItemType Directory -Path $source|Out-Null
 $archive=Join-Path $work "$($definition.id).tar"
 Checked 'git' @('archive','--format=tar',"--output=$archive",$definition.sha) "$out/$($definition.id)-archive.log"
 Checked 'tar' @('-xf',$archive,'-C',$source) "$out/$($definition.id)-extract.log"
 $admin=Join-Path $work "$($definition.id)-admin";$migrator=Join-Path $work "$($definition.id)-migrator"
 Publish $source 'Services/ConduitLLM.Admin/ConduitLLM.Admin.csproj' $admin "$out/$($definition.id)-admin-build.log" $definition.dotnet $definition.sdk $definition.sha
 Publish $source 'tools/ConduitLLM.Migrator/ConduitLLM.Migrator.csproj' $migrator "$out/$($definition.id)-migrator-build.log" $definition.dotnet $definition.sdk $definition.sha
 $cacheOut=Join-Path $work "$($definition.id)-cache";$collectorOut=Join-Path $work "$($definition.id)-collector"
 foreach($kind in @('cache','collector')){
  $projectDirectory=Join-Path $work "$($definition.id)-$kind-project"
  $project=New-MicroProject $kind $source $projectDirectory $definition.sdk $definition.framework
  $destination=if($kind -eq 'cache'){$cacheOut}else{$collectorOut}
  Publish $projectDirectory $project $destination "$out/$($definition.id)-$kind-build.log" $definition.dotnet $definition.sdk $definition.sha $false
 }
 $bindings.Add([ordered]@{id=$definition.id;sourceSha=$definition.sha;sourceSdk=$definition.sdk;sourceTree=(& git rev-parse "$($definition.sha)^{tree}").Trim();archiveSha256=(Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant();
  files=@(foreach($directory in @($admin,$migrator,$cacheOut,$collectorOut)){Get-ChildItem $directory -Recurse -File|ForEach-Object{[ordered]@{path=$_.FullName.Substring($work.Length+1);sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant();bytes=$_.Length}}})})
 $cohorts.Add([ordered]@{id=$definition.id;sourceSha=$definition.sha;sourceSdk=$definition.sdk;expectedRuntime=$definition.runtime;dotnet=$definition.dotnet;sourceDirectory=$source;
  admin=(Join-Path $admin 'ConduitLLM.Admin.dll');migrator=(Join-Path $migrator 'ConduitLLM.Migrator.dll');cache=(Join-Path $cacheOut 'Conduit.CacheMeasurements.dll');collector=(Join-Path $collectorOut 'Conduit.CollectorMeasurements.dll');
  seedProbe=(Join-Path $work 'seed/ConduitLLM.GatewayNativeAotTests.dll');database="phase2_ac_$($definition.id)";redisDatabase=$definition.db;
  databaseUrl="postgresql://phase2:isolated-phase2-fixture@127.0.0.1:15477/phase2_ac_$($definition.id)";redisUrl="redis://127.0.0.1:16477/$($definition.db)";redisEndpoint="127.0.0.1:16477,defaultDatabase=$($definition.db)"})
}
$bindings.ToArray()|ConvertTo-Json -Depth 8|Set-Content "$out/source-bindings.json" -Encoding utf8NoBOM
Publish (Join-Path $work 'legacy') 'Tests/ConduitLLM.GatewayNativeAotTests/ConduitLLM.GatewayNativeAotTests.csproj' (Join-Path $work 'seed') "$out/seed-build.log" $LegacyDotnet '10.0.404' 'f68cb5fae1130bbe1ef4fc30aca7a3266094dbaa'
$observerDirectory=Split-Path -Parent $ObserverProject
$observerCopy=Join-Path $work 'observer-project';Copy-HarnessProject $observerDirectory $observerCopy
$microCopy=Join-Path $work 'micro-observer-project';Copy-HarnessProject (Join-Path $PSScriptRoot 'MicroObserver') $microCopy
Publish $observerCopy (Split-Path -Leaf $ObserverProject) (Join-Path $work 'observer') "$out/observer-build.log" $LegacyDotnet '10.0.404'
Publish $microCopy 'Conduit.MicroObserver.csproj' (Join-Path $work 'micro-observer') "$out/micro-observer-build.log" $LegacyDotnet '10.0.404'
[ordered]@{schemaVersion=1;files=@(foreach($directory in @((Join-Path $work 'seed'),(Join-Path $work 'observer'),(Join-Path $work 'micro-observer'))){Get-ChildItem $directory -Recurse -File|ForEach-Object{[ordered]@{path=$_.FullName.Substring($work.Length+1);sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant();bytes=$_.Length}}})}|ConvertTo-Json -Depth 7|Set-Content "$out/fixed-harness-bindings.json" -Encoding utf8NoBOM
$configuration=[ordered]@{schemaVersion=1;profile='admin-cache';runs=$(if($FunctionalSmoke){1}else{$Runs});functionalSmoke=[bool]$FunctionalSmoke;architecture=$Architecture;workingDirectory=$repository;
 adminRequests=$(if($FunctionalSmoke){20}else{1000});adminWarmups=$(if($FunctionalSmoke){2}else{100});cacheWarmups=$(if($FunctionalSmoke){2}else{100});cacheSamples=$(if($FunctionalSmoke){5}else{1000});
 clientDotnet=$LegacyDotnet;observer=(Join-Path $work 'observer/Conduit.ServerObserver.dll');microObserver=(Join-Path $work 'micro-observer/Conduit.MicroObserver.dll');cohorts=$cohorts.ToArray();infrastructurePrepared=$false}
$configPath=Join-Path $out 'configuration.json';$configuration|ConvertTo-Json -Depth 8|Set-Content $configPath -Encoding utf8NoBOM
if($BuildOnly){Write-Output 'BUILD ONLY PASS; no infrastructure or measurements started.';exit 0}
$snapshotQuery=@'
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SELECT json_build_object(
 'requestLogs',(SELECT json_build_object('count',count(*),'digest',md5(coalesce(string_agg(to_jsonb(r)::text,E'\n' ORDER BY "Id"),''))) FROM "RequestLogs" r),
 'ledger',(SELECT json_build_object('count',count(*),'digest',md5(coalesce(string_agg(to_jsonb(r)::text,E'\n' ORDER BY "Id"),''))) FROM "VirtualKeyGroupTransactions" r),
 'groups',(SELECT json_build_object('count',count(*),'digest',md5(coalesce(string_agg(to_jsonb(r)::text,E'\n' ORDER BY "Id"),''))) FROM "VirtualKeyGroups" r));
COMMIT;
'@
try{
 $network=Docker @('network','create','--label',"conduit.admin-cache.owner=$task",$task)
 $pg="$task-postgres";$redis="$task-redis"
 [void](Docker @('run','--detach','--name',$pg,'--label',"conduit.admin-cache.owner=$task",'--network',$network,'--publish','127.0.0.1:15477:5432','--env','POSTGRES_USER=phase2','--env','POSTGRES_PASSWORD=isolated-phase2-fixture','--env','POSTGRES_DB=phase2_ac_legacy','postgres:17.12@sha256:0d938e975177bef9f1f61f3f9dbcf2e0aa0a139568f66d3415ed8892eb97f6ac'));$containers.Add($pg)
 [void](Docker @('run','--detach','--name',$redis,'--label',"conduit.admin-cache.owner=$task",'--network',$network,'--publish','127.0.0.1:16477:6379','redis:7.4.2-alpine@sha256:02419de7eddf55aa5bcf49efb74e88fa8d931b4d77c07eff8a6b2144472b6952','redis-server','--maxmemory-policy','noeviction'));$containers.Add($redis)
 $deadline=[DateTime]::UtcNow.AddSeconds(60)
  while($true){& $dockerExecutable exec $pg pg_isready -h 127.0.0.1 -U phase2 -d phase2_ac_legacy *> $null;if($LASTEXITCODE -eq 0){break};if([DateTime]::UtcNow -gt $deadline){throw 'PostgreSQL readiness timeout'};Start-Sleep -Milliseconds 250}
 foreach($database in @('phase2_ac_fixed','phase2_ac_candidate')){[void](Docker @('exec',$pg,'createdb','-U','phase2',$database))}
 Docker (@('inspect')+$containers.ToArray())|Set-Content "$out/infrastructure.json" -Encoding utf8NoBOM
 & (Join-Path $PSScriptRoot 'run-admin-cache.ps1') -ConfigurationPath $configPath -OutputDirectory "$out/prepare" -Stage prepare
 foreach($cohort in $cohorts){
  $snapshot=Docker @('exec',$pg,'psql','-X','-q','-t','-A','-v','ON_ERROR_STOP=1','-U','phase2','-d',$cohort.database,'-c',$snapshotQuery)
  $snapshot|Set-Content "$out/$($cohort.id)-state-before.json" -Encoding utf8NoBOM
 }
 $configuration.infrastructurePrepared=$true;$prepared=$true;$configuration|ConvertTo-Json -Depth 8|Set-Content $configPath -Encoding utf8NoBOM
 & (Join-Path $PSScriptRoot 'run-admin-cache.ps1') -ConfigurationPath $configPath -OutputDirectory "$out/measure" -Stage measure
 foreach($cohort in $cohorts){
  $snapshot=Docker @('exec',$pg,'psql','-X','-q','-t','-A','-v','ON_ERROR_STOP=1','-U','phase2','-d',$cohort.database,'-c',$snapshotQuery)
  $snapshot|Set-Content "$out/$($cohort.id)-state-after.json" -Encoding utf8NoBOM
  if(($snapshot|ConvertFrom-Json|ConvertTo-Json -Depth 8 -Compress) -cne (Get-Content "$out/$($cohort.id)-state-before.json" -Raw|ConvertFrom-Json|ConvertTo-Json -Depth 8 -Compress)){throw 'Read-only Admin/cache fixture state changed'}
 }
}catch{$failure=$_.ToString();throw}finally{
 $cleanup=[Collections.Generic.List[object]]::new()
 if($prepared){foreach($cohort in $cohorts){try{Docker @('exec',$pg,'psql','-X','-q','-t','-A','-v','ON_ERROR_STOP=1','-U','phase2','-d',$cohort.database,'-c',$snapshotQuery)|Set-Content "$out/$($cohort.id)-state-final.json" -Encoding utf8NoBOM}catch{$cleanup.Add([ordered]@{evidenceError=$_.ToString();removed=$false})}}}
 foreach($container in $containers){try{$identity=(Docker @('inspect',$container)|ConvertFrom-Json)[0];if($identity.Config.Labels.'conduit.admin-cache.owner' -cne $task){throw 'Ownership changed'};[void](Docker @('rm','--force','--volumes',$identity.Id));$cleanup.Add([ordered]@{id=$identity.Id;removed=$true})}catch{$cleanup.Add([ordered]@{container=$container;removed=$false;error=$_.ToString()})}}
 if($network){try{$identity=(Docker @('network','inspect',$network)|ConvertFrom-Json)[0];if($identity.Labels.'conduit.admin-cache.owner' -cne $task){throw 'Network ownership changed'};[void](Docker @('network','rm',$network));$cleanup.Add([ordered]@{network=$network;removed=$true})}catch{$cleanup.Add([ordered]@{network=$network;removed=$false;error=$_.ToString()})}}
 [ordered]@{failure=$failure;cleanup=$cleanup.ToArray();onlyOwnedFixturesRemoved=$true}|ConvertTo-Json -Depth 6|Set-Content "$out/cleanup.json" -Encoding utf8NoBOM
 if(!$failure -and @($cleanup|Where-Object{$_.removed -ne $true}).Count){throw 'Cleanup or evidence capture failed'}
}
Checked 'python' @((Join-Path $PSScriptRoot 'summarize.py'),'--directory',"$out/measure",'--configuration',$configPath,'--output',"$out/summary.json") "$out/summarize.log"
$mutated=[Collections.Generic.List[string]]::new()
foreach($binding in $bindings){foreach($file in $binding.files){if((Get-FileHash (Join-Path $work $file.path) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256){$mutated.Add($file.path)}}}
foreach($file in (Get-Content "$out/fixed-harness-bindings.json" -Raw|ConvertFrom-Json).files){if((Get-FileHash (Join-Path $work $file.path) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256){$mutated.Add($file.path)}}
[ordered]@{passed=($mutated.Count -eq 0);mutatedFiles=$mutated.ToArray()}|ConvertTo-Json|Set-Content "$out/published-input-integrity.json" -Encoding utf8NoBOM
if($mutated.Count){throw 'Published production input changed'}
[ordered]@{schemaVersion=1;profile='admin-cache';collectionValidated=$true;functionalSmoke=[bool]$FunctionalSmoke;candidateRevision=$CandidateRevision;qualificationCheckout=(& git rev-parse HEAD).Trim();architecture=$Architecture;
 financialQualification=$false;allocationGateSatisfied=$false;performanceAcceptanceGranted=$false;productionRolloutPerformed=$false;microbenchmarkScope='Frozen complete-cache and actual collector call patterns; no application or delivery capacity claim.'}|ConvertTo-Json -Depth 6|Set-Content "$out/qualification-state.json" -Encoding utf8NoBOM
