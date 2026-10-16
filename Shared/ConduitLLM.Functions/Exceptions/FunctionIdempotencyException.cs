namespace ConduitLLM.Functions.Exceptions;

/// <summary>A safe, explicit outcome of a durable function idempotency claim.</summary>
public sealed class FunctionIdempotencyException(
    int statusCode, string code, Guid executionId, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public Guid ExecutionId { get; } = executionId;
}
