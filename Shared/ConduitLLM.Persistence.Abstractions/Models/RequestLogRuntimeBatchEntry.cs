namespace ConduitLLM.Persistence;

/// <summary>An owned immutable row with a sequence identity retained across retries.</summary>
public sealed record RequestLogRuntimeBatchEntry(int Id, RequestLogRuntimeRecord Record);
