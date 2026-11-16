using System.Diagnostics;
namespace Phase0;

// First publication is one atomic reference: timestamp and provenance cannot diverge.
public sealed class CancellationEvidence
{
 private sealed record Stamp(long Ticks,string Source);
 private Stamp? _observed;
 private long _rawCallbackTicks;
 public long ObservedTicks => Volatile.Read(ref _observed)?.Ticks ?? 0;
 public long RawCallbackTicks => Interlocked.Read(ref _rawCallbackTicks);
 public void RecordCallback(){
  var now=Stopwatch.GetTimestamp();
  Interlocked.CompareExchange(ref _rawCallbackTicks,now,0);
  Interlocked.CompareExchange(ref _observed,new Stamp(now,"request-aborted-callback"),null);
 }
 public bool ObserveAtCatch(CancellationToken publicRequestAborted){
  if(!publicRequestAborted.IsCancellationRequested)return false;
  // Observation is now, not the time the client initiated cancellation.
  Interlocked.CompareExchange(ref _observed,new Stamp(Stopwatch.GetTimestamp(),"request-aborted-observed-at-catch"),null);
  return true;
 }
 public CancellationSnapshot Capture(){
  var state=Volatile.Read(ref _observed);
  return new(state?.Ticks ?? 0,state?.Source,RawCallbackTicks);
 }
}
public readonly record struct CancellationSnapshot(long ObservedTicks,string? Source,long RawCallbackTicks);
