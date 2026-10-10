using ConduitLLM.Persistence;

namespace ConduitLLM.Persistence.Interfaces;

/// <summary>
/// Fixed-shape persistence used to durably record Gateway request accounting.
/// </summary>
public interface IRequestLogRuntimeStore
{
    Task<int> WriteAsync(
        RequestLogRuntimeRecord record,
        CancellationToken cancellationToken = default);
}
