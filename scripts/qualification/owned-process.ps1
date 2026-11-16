$ownedProcesses=[Collections.Generic.List[object]]::new()
function Start-Owned([string]$Name,[string]$Program,[string[]]$Arguments,[bool]$Observed=$false){
 $psi=[Diagnostics.ProcessStartInfo]::new($Program);$psi.WorkingDirectory=$configuration.workingDirectory
 $psi.UseShellExecute=$false;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
 foreach($argument in $Arguments){[void]$psi.ArgumentList.Add($argument)}
 if($Observed){$psi.Environment['DOTNET_STARTUP_HOOKS']=$configuration.observer;$psi.Environment['CONDUIT_OBSERVER_PORT']='18084';$psi.Environment['CONDUIT_OBSERVER_TOKEN']=$observerToken;$psi.Environment['CONDUIT_OBSERVER_SAMPLE_MS']='100'}
 $stdout=[IO.File]::Create("$out/$Name.stdout.log");$stderr=[IO.File]::Create("$out/$Name.stderr.log")
 try{$p=[Diagnostics.Process]::Start($psi)}catch{$stdout.Dispose();$stderr.Dispose();throw}
 $owned=[pscustomobject]@{Process=$p;Out=$stdout;Err=$stderr;OutTask=$p.StandardOutput.BaseStream.CopyToAsync($stdout);ErrTask=$p.StandardError.BaseStream.CopyToAsync($stderr);Name=$Name;Closed=$false}
 $ownedProcesses.Add($owned)
 return $owned
}
function Finish-Owned($owned,[int]$TimeoutSeconds=180,[bool]$Graceful=$false){
 if($owned.Closed){return}
 $timedOut=$false
 try{
  if($Graceful -and !$owned.Process.HasExited){& /bin/kill -TERM -- ([string]$owned.Process.Id);if($LASTEXITCODE -ne 0){throw "SIGTERM failed: $($owned.Name)"}}
  if(!$owned.Process.WaitForExit($TimeoutSeconds*1000)){
   $timedOut=$true;$owned.Process.Kill($true)
   if(!$owned.Process.WaitForExit(10000)){throw "Owned process could not be joined after forced termination: $($owned.Name)"}
  }
  if(![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($owned.OutTask,$owned.ErrTask),10000)){throw "Output capture did not finish: $($owned.Name)"}
  [void]$owned.OutTask.GetAwaiter().GetResult();[void]$owned.ErrTask.GetAwaiter().GetResult()
  if($timedOut){throw "Timeout required forced termination: $($owned.Name)"}
  if($owned.Process.ExitCode -ne 0){throw "Exit $($owned.Process.ExitCode): $($owned.Name)"}
 }finally{
  $exited=$owned.Process.HasExited
  [ordered]@{name=$owned.Name;processId=$owned.Process.Id;exitCode=$(if($exited){$owned.Process.ExitCode}else{$null});graceful=$Graceful;timedOut=$timedOut;exitConfirmed=$exited;outputCaptureCompleted=($owned.OutTask.IsCompletedSuccessfully -and $owned.ErrTask.IsCompletedSuccessfully)}|ConvertTo-Json|Set-Content "$out/$($owned.Name).exit.json" -Encoding utf8NoBOM
  if($exited -and $owned.OutTask.IsCompleted -and $owned.ErrTask.IsCompleted){$owned.Out.Dispose();$owned.Err.Dispose();$owned.Closed=$true}
 }
}
function Invoke-Owned([string]$Name,[string]$Program,[string[]]$Arguments,[int]$TimeoutSeconds=300){$owned=Start-Owned $Name $Program $Arguments;Finish-Owned $owned $TimeoutSeconds}
function Clear-OwnedProcesses{
 $errors=[Collections.Generic.List[string]]::new()
 foreach($owned in $ownedProcesses){if(!$owned.Closed){
  try{
   if(!$owned.Process.HasExited){$owned.Process.Kill($true)}
   Finish-Owned $owned 10
  }catch{$errors.Add($_.ToString())}
  if(!$owned.Closed){$errors.Add("Owned process or output capture remains unjoined: $($owned.Name)")}
 }}
 return $errors.ToArray()
}
