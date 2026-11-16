using System.Diagnostics;
using System.Text.Json;
using Phase0;

var checks=new List<string>();
void Require(bool condition,string name){if(!condition)throw new InvalidOperationException(name);checks.Add(name);}
// A normally executing cancellation callback retains its raw timestamp.
using(var tokenSource=new CancellationTokenSource()){
 var evidence=new CancellationEvidence();
 using var registration=tokenSource.Token.Register(evidence.RecordCallback);
 var before=Stopwatch.GetTimestamp();tokenSource.Cancel();var after=Stopwatch.GetTimestamp();
 var value=evidence.Capture();
 Require(value.ObservedTicks>=before&&value.ObservedTicks<=after,"callback-current-clock");
 Require(value.Source=="request-aborted-callback"&&value.RawCallbackTicks==value.ObservedTicks,"callback-source-and-raw-match");
 evidence.ObserveAtCatch(tokenSource.Token);
 Require(evidence.Capture()==value,"catch-does-not-overwrite-callback");
}
// Deterministic LIFO callback race: the newer callback removes the older
// timestamp registration before it can run, as an await continuation can.
using(var tokenSource=new CancellationTokenSource()){
 var evidence=new CancellationEvidence();
 var older=tokenSource.Token.Register(evidence.RecordCallback);
 using var newer=tokenSource.Token.Register(()=>older.Dispose());
 tokenSource.Cancel();
 Require(tokenSource.IsCancellationRequested&&evidence.ObservedTicks==0&&evidence.RawCallbackTicks==0,"disposed-callback-zero-reproduced");
 var before=Stopwatch.GetTimestamp();
 try{tokenSource.Token.ThrowIfCancellationRequested();}
 catch(OperationCanceledException){Require(evidence.ObserveAtCatch(tokenSource.Token),"public-requested-token-observed");}
 var after=Stopwatch.GetTimestamp();var value=evidence.Capture();
 Require(value.Source=="request-aborted-observed-at-catch"&&value.ObservedTicks>=before&&value.ObservedTicks<=after,"catch-current-clock-not-initiation");
 Require(value.RawCallbackTicks==0,"missing-raw-callback-remains-zero");
 using var late=tokenSource.Token.Register(evidence.RecordCallback);
 var later=evidence.Capture();
 Require(later.ObservedTicks==value.ObservedTicks&&later.Source==value.Source&&later.RawCallbackTicks>=value.ObservedTicks,"late-callback-does-not-backdate-source");
}
using(var notRequested=new CancellationTokenSource()){
 var evidence=new CancellationEvidence();
 Require(!evidence.ObserveAtCatch(notRequested.Token)&&evidence.ObservedTicks==0,"nonrequested-token-rejected");
 try{throw new OperationCanceledException();}
 catch(OperationCanceledException){Require(!evidence.ObserveAtCatch(notRequested.Token),"oce-alone-is-not-cancellation-evidence");}
 Require(evidence.Capture().Source is null&&evidence.RawCallbackTicks==0,"no-fabricated-source-or-callback");
 Require(!evidence.ObserveAtCatch(CancellationToken.None),"none-token-rejected");
}
using(var tokenSource=new CancellationTokenSource()){
 tokenSource.Cancel();var evidence=new CancellationEvidence();
 Parallel.For(0,1000,index=>{if((index&1)==0)evidence.RecordCallback();else evidence.ObserveAtCatch(tokenSource.Token);var state=evidence.Capture();if(state.ObservedTicks<=0||state.Source is null)throw new InvalidOperationException("Atomic provenance");});
 Require(evidence.Capture().ObservedTicks>0,"concurrent-publication-keeps-atomic-provenance");
}
// Capture a progress DTO, then finish before serializing it. It must remain
// unsettled, rather than mixing an old handlerEnd with new cancellation fields.
using(var requested=new CancellationTokenSource()){
 var observation=new Observation{Id="snapshot-race"};
 var progress=observation.CreateSnapshot();
 requested.Cancel();observation.CancellationRecorder.ObserveAtCatch(requested.Token);
 observation.Error="TaskCanceledException";observation.ContentChunksFlushed=10;observation.PublishFinal();
 var encoded=JsonSerializer.Serialize(progress);
 using var document=JsonDocument.Parse(encoded);
 Require(progress.HandlerEndTicks==0&&progress.CancellationTicks==0&&!progress.Completed&&progress.Error is null,"detached-progress-remains-unsettled");
 Require(document.RootElement.GetProperty("CancellationTicks").GetInt64()==0,"serialization-after-finish-cannot-change-progress");
 var final=observation.CreateSnapshot();
 Require(final.HandlerEndTicks>=final.CancellationTicks&&final.CancellationTicks>0&&final.Error=="TaskCanceledException","acquire-final-snapshot-provenance");
 Require(final.CancellationEvidence.ObservedTicks==final.CancellationTicks,"single-final-cancellation-snapshot");
 var serializedFinal=JsonSerializer.Serialize(final);
 observation.Completed=true;observation.Error="later mutation";observation.ContentChunksFlushed=99;
 observation.CancellationRecorder.RecordCallback();
 Require(JsonSerializer.Serialize(final)==serializedFinal,"final-dto-does-not-read-mutable-original");
}
for(var round=0;round<100;round++){
 using var requested=new CancellationTokenSource();var observation=new Observation{Id="concurrent-"+round};
 var writer=Task.Run(()=>{requested.Cancel();observation.CancellationRecorder.ObserveAtCatch(requested.Token);observation.Error="TaskCanceledException";observation.ContentChunksFlushed=10;observation.PublishFinal();});
 while(!writer.IsCompleted){
  var captured=observation.CreateSnapshot();
  if(captured.HandlerEndTicks==0){if(captured.CancellationTicks!=0||captured.Completed||captured.Error is not null)throw new InvalidOperationException("Progress incoherent");}
  else if(captured.CancellationTicks<=0||captured.CancellationTicks!=captured.CancellationEvidence.ObservedTicks||captured.Error!="TaskCanceledException")throw new InvalidOperationException("Final incoherent");
 }
 writer.GetAwaiter().GetResult();
}
Require(true,"concurrent-progress-final-publication-coherent");
var report=new{schemaVersion=1,passed=true,runtime=Environment.Version.ToString(),checks,scope="Pure framework cancellation provenance regression tests; no application performance or financial qualification"};
var json=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});
if(args is [var output])File.WriteAllText(output,json);
Console.WriteLine(json);
