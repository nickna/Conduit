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
  return !$Report.passed -and $config.endpoint -ceq 'standard-sse' -and $config.cancel -ceq 'none' -and
   $config.timeoutSeconds -eq 120 -and $config.chunks -eq 10000 -and $config.receiveBufferBytes -eq 8192 -and
   $row.status -eq 200 -and !$row.validated -and $row.error -is [string] -and $row.error -cmatch '^TaskCanceledException:' -and
   $row.bytesRead -gt 0 -and $row.contentChunks -gt 0 -and $row.contentChunks -lt 10000 -and $row.usageEvents -eq 0 -and
   $row.lastSsePayloadPreview -is [string] -and $row.lastSsePayloadPreview -cne '[DONE]' -and
   $row.provider.completed -and $row.provider.contentChunksFlushed -eq 10000 -and $elapsed -ge 119000
 }catch{return $false} # Malformed evidence rejects; original client failure remains authoritative.
}
