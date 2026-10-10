using ConduitLLM.Configuration.DTOs;

namespace ConduitLLM.Configuration.Interfaces;

/// <summary>
/// Narrow request-time contract for durably writing Gateway accounting rows.
/// </summary>
public interface IRequestLogRuntimeWriter
{
    Task LogRequestAsync(LogRequestDto request);
}
