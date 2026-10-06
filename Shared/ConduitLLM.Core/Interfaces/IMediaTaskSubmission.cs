using ConduitLLM.Core.Events;
using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Interfaces;

/// <summary>Accepts a media task only when its generation command is durably committed.</summary>
public interface IMediaTaskSubmission
{
    Task<string> SubmitAsync(ImageGenerationRequested request, TaskMetadata metadata, CancellationToken cancellationToken = default);
    Task<string> SubmitAsync(VideoGenerationRequested request, TaskMetadata metadata, CancellationToken cancellationToken = default);
}
