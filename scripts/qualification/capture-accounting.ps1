[CmdletBinding()]
param(
  [ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$Label='final',
  [string]$OutputDirectory='',
  [ValidatePattern('^phase2_(legacy|fixed|candidate)$')][Parameter(Mandatory)][string]$Database,
  [ValidateRange(0,15)][Parameter(Mandatory)][int]$RedisDatabase,
  [ValidatePattern('^conduit-phase2-[a-z0-9-]+$')][Parameter(Mandatory)][string]$PostgresContainer,
  [ValidatePattern('^conduit-phase2-[a-z0-9-]+$')][Parameter(Mandatory)][string]$RedisContainer
)
$ErrorActionPreference='Stop'
if(!$OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot $Label}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$commands=[Collections.Generic.List[object]]::new()
$pgContainer=$PostgresContainer
$redisContainer=$RedisContainer
foreach($container in @($pgContainer,$redisContainer)){
  $identity=(& docker inspect $container | ConvertFrom-Json)[0]
  if($LASTEXITCODE -ne 0 -or $identity.Config.Labels.'conduit.task' -cne 'dotnet11-phase2'){
    throw 'Financial capture requires an explicitly task-owned fixture'
  }
}
$sql=Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'accounting.sql')
$pgArgs=@('exec','-i',$pgContainer,'psql','-X','-q','-A','-t','-v','ON_ERROR_STOP=1','-U','phase2','-d',$Database,'-f','-')
$started=[DateTime]::UtcNow
$pgText=($sql | & docker @pgArgs 2> (Join-Path $OutputDirectory 'postgres-stderr.log')) -join "`n"
$exitCode=$LASTEXITCODE
$commands.Add([ordered]@{program='docker';arguments=$pgArgs;startedUtc=$started.ToString('o');exitCode=$exitCode;stdin='snapshot.sql (read-only transaction)'})
if($exitCode -ne 0){throw "Read-only PostgreSQL snapshot failed with exit $exitCode"}
$pg=$pgText | ConvertFrom-Json
if($pg.database -cne $Database -or $pg.transactionReadOnly -cne 'on'){throw 'Database/read-only identity mismatch'}
$pgText | Set-Content -LiteralPath (Join-Path $OutputDirectory 'postgres.json') -Encoding utf8

function Read-RedisJson([string[]]$RedisArguments){
  $nativeArgs=@('exec',$redisContainer,'redis-cli','-n',([string]$RedisDatabase),'--json')+$RedisArguments
  $started=[DateTime]::UtcNow
  $text=(& docker @nativeArgs 2>> (Join-Path $OutputDirectory 'redis-stderr.log')) -join "`n"
  $code=$LASTEXITCODE
  $commands.Add([ordered]@{program='docker';arguments=$nativeArgs;startedUtc=$started.ToString('o');exitCode=$code})
  if($code -ne 0){throw "Read-only Redis command failed: $($RedisArguments[0])"}
  if($text -match '^\(error\)' -or $text -match '^error:'){throw 'Redis command returned an error'}
  return ConvertFrom-Json -InputObject $text -NoEnumerate
}
function Hash-Map($value){
  $map=[ordered]@{}
  if($null -eq $value){return $map}
  if($value -is [Array]){
    if($value.Count%2 -ne 0){throw 'Unexpected odd Redis hash response'}
    for($i=0;$i -lt $value.Count;$i+=2){$map[[string]$value[$i]]=[string]$value[$i+1]}
  }else{foreach($property in $value.PSObject.Properties){$map[$property.Name]=[string]$property.Value}}
  return $map
}
$redisGroups=[Collections.Generic.List[object]]::new()
foreach($group in $pg.groups){
  $groupId=[int]$group.id
  $hashes=[ordered]@{}
  foreach($prefix in @('spend_reservations','spend_reservations_started','spend_reservations_settled','spend_reservation_owners','spend_reservation_bounds','spend_reservations_indeterminate')){
    $hashes[$prefix]=Hash-Map (Read-RedisJson @('HGETALL',"${prefix}:group:$groupId"))
  }
  $scalars=[ordered]@{}
  foreach($prefix in @('reserved_spend','pending_spend','pending_spend_units','pending_spend_window_total_units')){
    $scalars[$prefix]=Read-RedisJson @('GET',"${prefix}:group:$groupId")
  }
  $pending=[Collections.Generic.List[object]]::new()
  foreach($prefix in @('pending_spend_window_units','processing_spend_window_units','processing_spend_units','processing_spend')){
    $pattern="${prefix}:group:${groupId}:*"
    $scanArgs=@('exec',$redisContainer,'redis-cli','-n',([string]$RedisDatabase),'--scan','--pattern',$pattern)
    $keys=@(& docker @scanArgs 2>> (Join-Path $OutputDirectory 'redis-stderr.log'))
    $code=$LASTEXITCODE
    $commands.Add([ordered]@{program='docker';arguments=$scanArgs;exitCode=$code})
    if($code -ne 0){throw 'Redis SCAN failed'}
    foreach($key in $keys | Sort-Object -Unique){
      if([string]::IsNullOrWhiteSpace($key)){continue}
      $pending.Add([ordered]@{key=[string]$key;unit=$(if($prefix -match '_units$'){'1e-8 USD integer'}else{'decimal USD'});value=(Read-RedisJson @('GET',[string]$key))})
    }
  }
  $indeterminate=[ordered]@{}
  foreach($id in $hashes.spend_reservations_indeterminate.Keys){
    $record=$hashes.spend_reservations_indeterminate[$id] | ConvertFrom-Json
    $indeterminate[$id]=[ordered]@{knownUnits=[string]$record.knownUnits;heldAmount=[string]$record.heldAmount;window=[string]$record.window;virtualKeyId=[string]$record.virtualKeyId;resolved=[bool]$record.resolved;providerUnits=[string]$record.providerUnits;disposition=[string]$record.disposition;reasonPresent=![string]::IsNullOrEmpty([string]$record.reason)}
  }
  $hashes.spend_reservations_indeterminate=$indeterminate
  $redisGroups.Add([ordered]@{groupId=$groupId;scalars=$scalars;hashes=$hashes;pendingAndProcessingValues=$pending.ToArray()})
}
[ordered]@{schemaVersion=1;label=$Label;capturedAtUtc=[DateTime]::UtcNow.ToString('o');postgres=$pg;redis=[ordered]@{database=$RedisDatabase;container=$redisContainer;groups=$redisGroups.ToArray();unitScale='100000000';note='Snapshot is read-only and spans several commands, not a cross-PostgreSQL/Redis atomic transaction. Pending window total includes processing; do not add it to its child keys.'};isolation="Task-owned PostgreSQL $Database and Redis DB$RedisDatabase only; other cohorts excluded."} | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'snapshot.json') -Encoding utf8
[ordered]@{schemaVersion=1;commands=$commands.ToArray()} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'commands.json') -Encoding utf8
Write-Output (Join-Path $OutputDirectory 'snapshot.json')
