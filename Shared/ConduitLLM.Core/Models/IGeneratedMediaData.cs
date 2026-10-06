namespace ConduitLLM.Core.Models;

/// <summary>
/// Typed output contract shared by generated image and video response items.
/// </summary>
public interface IGeneratedMediaData
{
    string? Url { get; }

    string? B64Json { get; }
}
