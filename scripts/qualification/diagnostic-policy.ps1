# A deadline is a failed diagnostic observation, never successful protocol evidence.
function Test-PressureDiagnosticDeadline($Report,[bool]$Enabled){
 if(!$Enabled -or $null -eq $Report){return $false}
 try{
  $failed=@($Report.measured.requests);if($failed.Count -ne 1){return $false}
  $row=$failed[0];$config=$Report.configuration;$elapsed=$Report.measured.elapsedMilliseconds
  if($Report.passed -isnot [bool] -or $row.validated -isnot [bool] -or $row.provider.completed -isnot [bool]){return $false}
  foreach($value in @($config.timeoutSeconds,$config.chunks,$config.receiveBufferBytes,$row.status,$row.bytesRead,$row.contentChunks,$row.usageEvents,$row.provider.contentChunksFlushed)){
   if($value -isnot [int] -and $value -isnot [long]){return $false}
  }
  if(($elapsed -isnot [double] -and $elapsed -isnot [int] -and $elapsed -isnot [long]) -or ![double]::IsFinite([double]$elapsed)){return $false}
  $deadline = !$Report.passed -and $config.endpoint -ceq 'standard-sse' -and $config.cancel -ceq 'none' -and
   $config.timeoutSeconds -eq 120 -and $config.chunks -eq 10000 -and $config.receiveBufferBytes -eq 8192 -and
   $row.status -eq 200 -and !$row.validated -and $row.error -is [string] -and $row.error -cmatch '^TaskCanceledException:' -and
   $row.bytesRead -gt 0 -and $row.contentChunks -gt 0 -and $row.contentChunks -lt 10000 -and $row.usageEvents -eq 0 -and
   $row.lastSsePayloadPreview -is [string] -and $row.lastSsePayloadPreview -cne '[DONE]' -and $elapsed -ge 119000
  if(!$deadline){return $false}
  # Preserve the existing completed-provider deadline signature.
  if($row.provider.completed){return $row.provider.contentChunksFlushed -eq 10000}

  # Backpressure can keep the provider in a content/finish write until the same
  # client deadline cancels it. This is another FAILED diagnostic observation,
  # not a successful response, usage settlement, or a performance acceptance.
  $provider=$row.provider
  if($config.endpoint -isnot [string] -or $config.cancel -isnot [string] -or
   $provider.error -isnot [string] -or $provider.error -cne 'OperationCanceledException'){return $false}
  foreach($value in @($Report.stopwatchFrequency,$row.requestStartTicks,$row.cancelInitiatedTicks,
    $provider.requestAcceptedTicks,$provider.roleWriteBeginTicks,$provider.firstContentWriteBeginTicks,
    $provider.firstContentFlushEndTicks,$provider.lastFlushEndTicks,$provider.cancellationTicks,$provider.handlerEndTicks,
    $provider.usageWriteBeginTicks,$provider.usageFlushEndTicks,$provider.doneWriteBeginTicks)){
   if($value -isnot [int] -and $value -isnot [long]){return $false}
  }
  if($Report.stopwatchFrequency -le 0 -or $row.requestStartTicks -le 0){return $false}
  $cancelMilliseconds=([double]$provider.cancellationTicks-[double]$row.requestStartTicks)*1000.0/[double]$Report.stopwatchFrequency
  return $row.cancelInitiatedTicks -eq 0 -and $provider.contentChunksFlushed -gt 0 -and
   $provider.contentChunksFlushed -le 10000 -and $row.contentChunks -le $provider.contentChunksFlushed -and
   $provider.usageWriteBeginTicks -eq 0 -and $provider.usageFlushEndTicks -eq 0 -and $provider.doneWriteBeginTicks -eq 0 -and
   $provider.requestAcceptedTicks -ge $row.requestStartTicks -and $provider.roleWriteBeginTicks -ge $provider.requestAcceptedTicks -and
   $provider.firstContentWriteBeginTicks -ge $provider.roleWriteBeginTicks -and
   $provider.firstContentFlushEndTicks -ge $provider.firstContentWriteBeginTicks -and
   $provider.lastFlushEndTicks -ge $provider.firstContentFlushEndTicks -and
   $provider.cancellationTicks -ge $provider.lastFlushEndTicks -and $provider.handlerEndTicks -ge $provider.cancellationTicks -and
   $cancelMilliseconds -ge 119000
 }catch{return $false} # Malformed evidence rejects; original client failure remains authoritative.
}
