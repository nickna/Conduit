using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Interfaces;

/// <summary>
/// Compile-time capability for providers and decorators that support video generation.
/// </summary>
public interface IVideoGenerationClient
{
    Task<VideoGenerationResponse> CreateVideoAsync(
        VideoGenerationRequest request,
        string? apiKey = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional compile-time capability for video providers that publish polling progress.
/// </summary>
public interface IVideoProgressCallbackClient
{
    void SetProgressCallback(Func<string, string, int, Task> callback);
}
