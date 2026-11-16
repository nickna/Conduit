[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'diagnostic-policy.ps1')
# Relevant fields copied from whole authenticated ZIPs, both SHA-256 verified:
# X64 artifact14185604897: 4d2c5798824aca415620926d38e8687ae6300647bab24da473504df73767d7d8
# ARM64 artifact14186086835: b21fbda2f1d0677861b1a64ea8540dcda4c6349696a39b0cbf60ddf9d8175af9
# These replace the older interrupted-ZIP/CRC-only fixture. Original hosted jobs
# remain failed under their original predicate. No protocol/financial/performance
# pass is granted by replaying a failed observation through this pure policy.
$fixtureJson=@'
{
  "completed-x64": {
    "passed": false,
    "stopwatchFrequency": 1000000000,
    "runtime": {
      "runtime": "10.0.15",
      "os": "Ubuntu 24.04.5 LTS",
      "architecture": "X64",
      "synthetic": true,
      "syntheticUsage": "chat10input/5output; audio1.25seconds independent of text/chunk/uploadsize"
    },
    "configuration": {
      "caseId": "pressure8-r1-pressure-standard-fast",
      "baseUrl": "http://127.0.0.1:18080",
      "providerUrl": "http://127.0.0.1:18083",
      "model": "native-aot-model",
      "endpoint": "standard-sse",
      "requests": 1,
      "warmupRequests": 0,
      "concurrency": 1,
      "chunks": 10000,
      "chunkCharacters": 1024,
      "gapMilliseconds": 0,
      "burstSize": 0,
      "burstPauseMilliseconds": 0,
      "toolCalls": false,
      "slowReadBytes": 16384,
      "slowReadDelayMilliseconds": 0,
      "cancel": "none",
      "cancelAfterContentChunks": 3,
      "inputBytes": 1048576,
      "timeoutSeconds": 120,
      "maxResponseBytes": 33554432,
      "receiveBufferBytes": 8192
    },
    "measured": {
      "elapsedMilliseconds": 120020.1365,
      "requests": [
        {
          "requestStartTicks": 360777555429,
          "cancelInitiatedTicks": 0,
          "status": 200,
          "validated": false,
          "error": "TaskCanceledException: The operation was canceled.",
          "bytesRead": 7109391,
          "contentChunks": 5828,
          "usageEvents": 0,
          "lastSsePayloadPreview": "{\"id\":\"chatcmpl_pressure8-r1-pressure-standard-fast-m0000\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx|\"}}],\"created\":1760000000,\"model\":\"native-aot-model\",\"object\":\"chat.completion.chunk\"}",
          "provider": {
            "id": "pressure8-r1-pressure-standard-fast-m0000",
            "requestAcceptedTicks": 361519616283,
            "roleWriteBeginTicks": 361519745447,
            "firstContentWriteBeginTicks": 361530633698,
            "firstContentFlushEndTicks": 361530703167,
            "usageWriteBeginTicks": 445478044371,
            "usageFlushEndTicks": 445481537540,
            "doneWriteBeginTicks": 445481597617,
            "lastFlushEndTicks": 445481621263,
            "handlerEndTicks": 445481675216,
            "contentChunksFlushed": 10000,
            "bytesFlushed": 12499736,
            "uploadBytes": 0,
            "uploadSha256": null,
            "completed": true,
            "error": null,
            "cancellationTicks": 0
          }
        }
      ]
    }
  },
  "cancelled-x64-candidate": {
    "passed": false,
    "stopwatchFrequency": 1000000000,
    "runtime": {
      "runtime": "10.0.15",
      "os": "Ubuntu 24.04.5 LTS",
      "architecture": "X64",
      "synthetic": true,
      "syntheticUsage": "chat10input/5output; audio1.25seconds independent of text/chunk/uploadsize"
    },
    "configuration": {
      "caseId": "pressure8-r1-pressure-standard-fast",
      "baseUrl": "http://127.0.0.1:18080",
      "providerUrl": "http://127.0.0.1:18083",
      "model": "native-aot-model",
      "endpoint": "standard-sse",
      "requests": 1,
      "warmupRequests": 0,
      "concurrency": 1,
      "chunks": 10000,
      "chunkCharacters": 1024,
      "gapMilliseconds": 0,
      "burstSize": 0,
      "burstPauseMilliseconds": 0,
      "toolCalls": false,
      "slowReadBytes": 16384,
      "slowReadDelayMilliseconds": 0,
      "cancel": "none",
      "cancelAfterContentChunks": 3,
      "inputBytes": 1048576,
      "timeoutSeconds": 120,
      "maxResponseBytes": 33554432,
      "receiveBufferBytes": 8192
    },
    "measured": {
      "elapsedMilliseconds": 120018.5592,
      "requests": [
        {
          "requestStartTicks": 496920956326,
          "cancelInitiatedTicks": 0,
          "status": 200,
          "validated": false,
          "error": "TaskCanceledException: The operation was canceled.; Provider did not complete",
          "bytesRead": 5509971,
          "contentChunks": 4517,
          "usageEvents": 0,
          "lastSsePayloadPreview": "{\"id\":\"chatcmpl_pressure8-r1-pressure-standard-fast-m0000\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx|\"}}],\"created\":1760000000,\"model\":\"native-aot-model\",\"object\":\"chat.completion.chunk\"}",
          "provider": {
            "id": "pressure8-r1-pressure-standard-fast-m0000",
            "requestAcceptedTicks": 497688029114,
            "roleWriteBeginTicks": 497688143754,
            "firstContentWriteBeginTicks": 497699314865,
            "firstContentFlushEndTicks": 497699396152,
            "usageWriteBeginTicks": 0,
            "usageFlushEndTicks": 0,
            "doneWriteBeginTicks": 0,
            "lastFlushEndTicks": 616906421931,
            "handlerEndTicks": 616907447786,
            "contentChunksFlushed": 9808,
            "bytesFlushed": 12259268,
            "uploadBytes": 0,
            "uploadSha256": null,
            "completed": false,
            "error": "OperationCanceledException",
            "cancellationTicks": 616906953169
          }
        }
      ]
    }
  },
  "cancelled-arm64-legacy": {
    "passed": false,
    "stopwatchFrequency": 1000000000,
    "runtime": {
      "runtime": "10.0.15",
      "os": "Ubuntu 24.04.5 LTS",
      "architecture": "Arm64",
      "synthetic": true,
      "syntheticUsage": "chat10input/5output; audio1.25seconds independent of text/chunk/uploadsize"
    },
    "configuration": {
      "caseId": "pressure8-r1-pressure-standard-fast",
      "baseUrl": "http://127.0.0.1:18080",
      "providerUrl": "http://127.0.0.1:18083",
      "model": "native-aot-model",
      "endpoint": "standard-sse",
      "requests": 1,
      "warmupRequests": 0,
      "concurrency": 1,
      "chunks": 10000,
      "chunkCharacters": 1024,
      "gapMilliseconds": 0,
      "burstSize": 0,
      "burstPauseMilliseconds": 0,
      "toolCalls": false,
      "slowReadBytes": 16384,
      "slowReadDelayMilliseconds": 0,
      "cancel": "none",
      "cancelAfterContentChunks": 3,
      "inputBytes": 1048576,
      "timeoutSeconds": 120,
      "maxResponseBytes": 33554432,
      "receiveBufferBytes": 8192
    },
    "measured": {
      "elapsedMilliseconds": 120029.731,
      "requests": [
        {
          "requestStartTicks": 862381261073,
          "cancelInitiatedTicks": 0,
          "status": 200,
          "validated": false,
          "error": "TaskCanceledException: The operation was canceled.; Provider did not complete",
          "bytesRead": 5069551,
          "contentChunks": 4156,
          "usageEvents": 0,
          "lastSsePayloadPreview": "{\"id\":\"chatcmpl_pressure8-r1-pressure-standard-fast-m0000\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx|\"}}],\"created\":1760000000,\"model\":\"native-aot-model\",\"object\":\"chat.completion.chunk\"}",
          "provider": {
            "id": "pressure8-r1-pressure-standard-fast-m0000",
            "requestAcceptedTicks": 863624764222,
            "roleWriteBeginTicks": 863624954572,
            "firstContentWriteBeginTicks": 863642283937,
            "firstContentFlushEndTicks": 863642422229,
            "usageWriteBeginTicks": 0,
            "usageFlushEndTicks": 0,
            "doneWriteBeginTicks": 0,
            "lastFlushEndTicks": 982366358109,
            "handlerEndTicks": 982367837417,
            "contentChunksFlushed": 9103,
            "bytesFlushed": 11378018,
            "uploadBytes": 0,
            "uploadSha256": null,
            "completed": false,
            "error": "OperationCanceledException",
            "cancellationTicks": 982366721624
          }
        }
      ]
    }
  },
  "cancelled-arm64-fixed": {
    "passed": false,
    "stopwatchFrequency": 1000000000,
    "runtime": {
      "runtime": "10.0.15",
      "os": "Ubuntu 24.04.5 LTS",
      "architecture": "Arm64",
      "synthetic": true,
      "syntheticUsage": "chat10input/5output; audio1.25seconds independent of text/chunk/uploadsize"
    },
    "configuration": {
      "caseId": "pressure8-r1-pressure-standard-fast",
      "baseUrl": "http://127.0.0.1:18080",
      "providerUrl": "http://127.0.0.1:18083",
      "model": "native-aot-model",
      "endpoint": "standard-sse",
      "requests": 1,
      "warmupRequests": 0,
      "concurrency": 1,
      "chunks": 10000,
      "chunkCharacters": 1024,
      "gapMilliseconds": 0,
      "burstSize": 0,
      "burstPauseMilliseconds": 0,
      "toolCalls": false,
      "slowReadBytes": 16384,
      "slowReadDelayMilliseconds": 0,
      "cancel": "none",
      "cancelAfterContentChunks": 3,
      "inputBytes": 1048576,
      "timeoutSeconds": 120,
      "maxResponseBytes": 33554432,
      "receiveBufferBytes": 8192
    },
    "measured": {
      "elapsedMilliseconds": 120022.6559,
      "requests": [
        {
          "requestStartTicks": 996536345654,
          "cancelInitiatedTicks": 0,
          "status": 200,
          "validated": false,
          "error": "TaskCanceledException: The operation was canceled.; Provider did not complete",
          "bytesRead": 6709231,
          "contentChunks": 5500,
          "usageEvents": 0,
          "lastSsePayloadPreview": "{\"id\":\"chatcmpl_pressure8-r1-pressure-standard-fast-m0000\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx|\"}}],\"created\":1760000000,\"model\":\"native-aot-model\",\"object\":\"chat.completion.chunk\"}",
          "provider": {
            "id": "pressure8-r1-pressure-standard-fast-m0000",
            "requestAcceptedTicks": 997615715751,
            "roleWriteBeginTicks": 997615887012,
            "firstContentWriteBeginTicks": 997632636000,
            "firstContentFlushEndTicks": 997632806477,
            "usageWriteBeginTicks": 0,
            "usageFlushEndTicks": 0,
            "doneWriteBeginTicks": 0,
            "lastFlushEndTicks": 1116520567154,
            "handlerEndTicks": 1116521860256,
            "contentChunksFlushed": 10000,
            "bytesFlushed": 12499268,
            "uploadBytes": 0,
            "uploadSha256": null,
            "completed": false,
            "error": "OperationCanceledException",
            "cancellationTicks": 1116521286887
          }
        }
      ]
    }
  }
}
'@
$checks=[Collections.Generic.List[object]]::new()
function New-Fixture([string]$Name){($fixtureJson|ConvertFrom-Json).PSObject.Properties[$Name].Value}
function Check([string]$Name,$Value,[bool]$Enabled,[bool]$Expected){
 $observed=Test-PressureDiagnosticDeadline $Value $Enabled
 if($observed -isnot [bool] -or $observed -ne $Expected){throw "Unexpected deadline classification: $Name"}
 $checks.Add([ordered]@{case=$Name;expected=$Expected;observed=$observed;passed=$true})
}
$names=@('completed-x64','cancelled-x64-candidate','cancelled-arm64-legacy','cancelled-arm64-fixed')
foreach($fixture in $names){
 Check "$fixture-failed-diagnostic-only" (New-Fixture $fixture) $true $true
 Check "$fixture-disabled-profile-rejected" (New-Fixture $fixture) $false $false
 foreach($name in @('http500','other-io-error','usage-seen','done-seen','all-content','empty-body','early-timeout','different-timeout','different-buffer','different-endpoint','explicit-cancellation','string-status','string-provider-completed','missing-preview','multiple-requests','passed-response')){
  $value=New-Fixture $fixture;$request=$value.measured.requests[0]
  switch($name){
   'http500'{$request.status=500}
   'other-io-error'{$request.error='IOException: reset'}
   'usage-seen'{$request.usageEvents=1}
   'done-seen'{$request.lastSsePayloadPreview='[DONE]'}
   'all-content'{$request.contentChunks=10000}
   'empty-body'{$request.bytesRead=0}
   'early-timeout'{$value.measured.elapsedMilliseconds=1000}
   'different-timeout'{$value.configuration.timeoutSeconds=180}
   'different-buffer'{$value.configuration.receiveBufferBytes=65536}
   'different-endpoint'{$value.configuration.endpoint='enhanced-sse'}
   'explicit-cancellation'{$value.configuration.cancel='before-usage'}
   'string-status'{$request.status='200'}
   'string-provider-completed'{$request.provider.completed='false'}
   'missing-preview'{$request.PSObject.Properties.Remove('lastSsePayloadPreview')}
   'multiple-requests'{$value.measured.requests=@($request,$request)}
   'passed-response'{$value.passed=$true;$request.validated=$true}
  }
  Check "$fixture-$name" $value $true $false
 }
}
# The completed-provider signature still requires all content to be flushed,
# and incomplete evidence without the second signature remains rejected.
$value=New-Fixture 'completed-x64';$value.measured.requests[0].provider.contentChunksFlushed=9999
Check 'completed-provider-short-rejected' $value $true $false
$value=New-Fixture 'completed-x64';$value.measured.requests[0].provider.completed=$false
Check 'incomplete-provider-without-cancellation-error-rejected' $value $true $false

foreach($fixture in $names|Where-Object {$_ -like 'cancelled-*'}){
 foreach($name in @('provider-io-error','provider-error-null','provider-error-boolean','provider-error-case','provider-error-suffix','no-provider-cancellation','no-provider-end','no-last-flush','no-first-flush','zero-flushed','too-many-flushed','less-flushed-than-read','usage-write-started','usage-flushed','done-write-started','last-flush-after-cancellation','end-before-cancellation','client-explicit-cancel-ticks','client-start-after-accept','early-provider-cancellation','array-endpoint','array-cancel','string-passed','string-validated')){
  $value=New-Fixture $fixture;$request=$value.measured.requests[0];$provider=$request.provider
  switch($name){
   'provider-io-error'{$provider.error='IOException'}
   'provider-error-null'{$provider.error=$null}
   'provider-error-boolean'{$provider.error=$false}
   'provider-error-case'{$provider.error='operationcanceledexception'}
   'provider-error-suffix'{$provider.error='OperationCanceledException: unrelated'}
   'no-provider-cancellation'{$provider.cancellationTicks=0}
   'no-provider-end'{$provider.handlerEndTicks=0}
   'no-last-flush'{$provider.lastFlushEndTicks=0}
   'no-first-flush'{$provider.firstContentFlushEndTicks=0}
   'zero-flushed'{$provider.contentChunksFlushed=0}
   'too-many-flushed'{$provider.contentChunksFlushed=10001}
   'less-flushed-than-read'{$provider.contentChunksFlushed=$request.contentChunks-1}
   'usage-write-started'{$provider.usageWriteBeginTicks=$provider.firstContentFlushEndTicks}
   'usage-flushed'{$provider.usageFlushEndTicks=$provider.firstContentFlushEndTicks}
   'done-write-started'{$provider.doneWriteBeginTicks=$provider.firstContentFlushEndTicks}
   'last-flush-after-cancellation'{$provider.lastFlushEndTicks=$provider.cancellationTicks+1}
   'end-before-cancellation'{$provider.handlerEndTicks=$provider.cancellationTicks-1}
   'client-explicit-cancel-ticks'{$request.cancelInitiatedTicks=$provider.cancellationTicks}
   'client-start-after-accept'{$request.requestStartTicks=$provider.requestAcceptedTicks+1}
   'early-provider-cancellation'{
    $offset=0
    foreach($field in @('requestAcceptedTicks','roleWriteBeginTicks','firstContentWriteBeginTicks','firstContentFlushEndTicks','lastFlushEndTicks','cancellationTicks','handlerEndTicks')){
     $offset+=10000;$provider.PSObject.Properties[$field].Value=$request.requestStartTicks+$offset
    }
   }
   'array-endpoint'{$value.configuration.endpoint=@('standard-sse')}
   'array-cancel'{$value.configuration.cancel=@('none')}
   'string-passed'{$value.passed='false'}
   'string-validated'{$request.validated='false'}
  }
  Check "$fixture-$name" $value $true $false
 }
}
# Every numeric field consumed by the cancellation signature rejects coercion,
# null/missing values, floating-point numbers and booleans.
$fields=@(
 @{target='report';name='stopwatchFrequency'},
 @{target='request';name='requestStartTicks'},@{target='request';name='cancelInitiatedTicks'},
 @{target='provider';name='requestAcceptedTicks'},@{target='provider';name='roleWriteBeginTicks'},
 @{target='provider';name='firstContentWriteBeginTicks'},@{target='provider';name='firstContentFlushEndTicks'},
 @{target='provider';name='lastFlushEndTicks'},@{target='provider';name='cancellationTicks'},@{target='provider';name='handlerEndTicks'},
 @{target='provider';name='usageWriteBeginTicks'},@{target='provider';name='usageFlushEndTicks'},@{target='provider';name='doneWriteBeginTicks'},
 @{target='configuration';name='timeoutSeconds'},@{target='configuration';name='chunks'},@{target='configuration';name='receiveBufferBytes'},
 @{target='request';name='status'},@{target='request';name='bytesRead'},@{target='request';name='contentChunks'},@{target='request';name='usageEvents'},
 @{target='provider';name='contentChunksFlushed'}
)
foreach($field in $fields){foreach($kind in @('string','double','boolean','null','missing')){
 $value=New-Fixture 'cancelled-x64-candidate'
 $target=switch($field.target){'report'{$value};'request'{$value.measured.requests[0]};'provider'{$value.measured.requests[0].provider};'configuration'{$value.configuration}}
 $property=$target.PSObject.Properties[$field.name]
 switch($kind){
  'string'{$property.Value=[string]$property.Value}
  'double'{$property.Value=[double]$property.Value}
  'boolean'{$property.Value=$false}
  'null'{$property.Value=$null}
  'missing'{$target.PSObject.Properties.Remove($field.name)}
 }
 Check "strict-$($field.target)-$($field.name)-$kind" $value $true $false
}}
foreach($name in @('zero-frequency','negative-frequency','zero-client-start','negative-usage-ticks','reversed-role','reversed-first-write','reversed-first-flush')){
 $value=New-Fixture 'cancelled-x64-candidate';$request=$value.measured.requests[0];$provider=$request.provider
 switch($name){
  'zero-frequency'{$value.stopwatchFrequency=0}
  'negative-frequency'{$value.stopwatchFrequency=-1}
  'zero-client-start'{$request.requestStartTicks=0}
  'negative-usage-ticks'{$provider.usageWriteBeginTicks=-1}
  'reversed-role'{$provider.roleWriteBeginTicks=$provider.requestAcceptedTicks-1}
  'reversed-first-write'{$provider.firstContentWriteBeginTicks=$provider.roleWriteBeginTicks-1}
  'reversed-first-flush'{$provider.firstContentFlushEndTicks=$provider.firstContentWriteBeginTicks-1}
 }
 Check $name $value $true $false
}
Check 'null-report-rejected' $null $true $false
Check 'malformed-report-rejected' ([pscustomobject]@{}) $true $false
New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($OutputPath))) -Force|Out-Null
[ordered]@{schemaVersion=1;passed=$true;checks=$checks.ToArray();scope='A literal deadline remains a failed protocol observation in explicit pressure8 only';performanceAcceptanceGranted=$false;financialQualification=$false;actualNativeDiagnosticExecuted=$false;originalHostedJobOutcomeChanged=$false;fixtureSources=@{x64=14185604897;arm64=14186086835}}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Output "PASS: $($checks.Count) pressure diagnostic policy checks; no runtime/performance qualification."
