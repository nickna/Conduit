[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'diagnostic-policy.ps1')
# Actual relevant fields: ARM64 artifact14184855512 first original8KiB case.
# Interrupted authenticated ZIP member CRC verified; whole ZIP NOT verified.
# This policy fixture grants no runtime, protocol, financial or performance pass.
$fixtureJson=@'
{
  "passed": false,
  "runtime": {
    "runtime": "10.0.15",
    "os": "Ubuntu 24.04.5 LTS",
    "architecture": "Arm64",
    "synthetic": true,
    "syntheticUsage": "chat10input/5output; audio1.25seconds independent of text/chunk/uploadsize"
  },
  "configuration": {
    "caseId": "pressure-r1-pressure-standard-fast",
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
    "elapsedMilliseconds": 120022.6357,
    "requests": [
      {
        "status": 200,
        "validated": false,
        "error": "TaskCanceledException: The operation was canceled.",
        "bytesRead": 5392085,
        "contentChunks": 4424,
        "usageEvents": 0,
        "lastSsePayloadPreview": "{\"id\":\"chatcmpl_pressure-r1-pressure-standard-fast-m0000\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx|\"}}],\"created\":1760000000,\"model\":\"native-aot-model\",\"object\":\"chat.completion.chunk\"}",
        "provider": {
          "id": "pressure-r1-pressure-standard-fast-m0000",
          "requestAcceptedTicks": 650866930587,
          "roleWriteBeginTicks": 650867128609,
          "firstContentWriteBeginTicks": 650884296184,
          "firstContentFlushEndTicks": 650884424972,
          "usageWriteBeginTicks": 728968255754,
          "usageFlushEndTicks": 728971303041,
          "doneWriteBeginTicks": 728971366947,
          "lastFlushEndTicks": 728971385988,
          "handlerEndTicks": 728971437997,
          "contentChunksFlushed": 10000,
          "bytesFlushed": 12489732,
          "uploadBytes": 0,
          "uploadSha256": null,
          "completed": true,
          "error": null,
          "cancellationTicks": 0
        }
      }
    ]
  }
}
'@
$checks=[Collections.Generic.List[object]]::new()
function Check([string]$Name,$Value,[bool]$Enabled,[bool]$Expected){
 $observed=Test-PressureDiagnosticDeadline $Value $Enabled
 if($observed -ne $Expected){throw "Unexpected deadline classification: $Name"}
 $checks.Add([ordered]@{case=$Name;expected=$Expected;observed=$observed;passed=$true})
}
Check 'actual-deadline-enabled-diagnostic-only' ($fixtureJson|ConvertFrom-Json) $true $true
Check 'same-deadline-disabled-profile-rejected' ($fixtureJson|ConvertFrom-Json) $false $false
foreach($name in @('http500','other-net11-error','provider-incomplete','provider-short','usage-seen','done-seen','all-content','empty-body','early-timeout','different-timeout','different-buffer','different-endpoint','explicit-cancellation','string-status','string-provider-completed','missing-preview','multiple-requests','passed-response')){
 $value=$fixtureJson|ConvertFrom-Json;$request=$value.measured.requests[0]
 switch($name){
  'http500'{$request.status=500}
  'other-net11-error'{$value.runtime.runtime='11.0.0';$request.error='IOException: reset'}
  'provider-incomplete'{$request.provider.completed=$false}
  'provider-short'{$request.provider.contentChunksFlushed=9999}
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
  'string-provider-completed'{$request.provider.completed='true'}
  'missing-preview'{$request.PSObject.Properties.Remove('lastSsePayloadPreview')}
  'multiple-requests'{$value.measured.requests=@($request,$request)}
  'passed-response'{$value.passed=$true;$request.validated=$true}
 }
 Check $name $value $true $false
}
Check 'null-report-rejected' $null $true $false
Check 'malformed-report-rejected' ([pscustomobject]@{}) $true $false
New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($OutputPath))) -Force|Out-Null
[ordered]@{schemaVersion=1;passed=$true;checks=$checks.ToArray();scope='A literal deadline remains a failed protocol observation in explicit pressure8 only';performanceAcceptanceGranted=$false;financialQualification=$false;actualNativeDiagnosticExecuted=$false}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Output "PASS: $($checks.Count) pressure diagnostic policy checks; no runtime/performance qualification."
