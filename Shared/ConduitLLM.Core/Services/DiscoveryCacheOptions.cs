namespace ConduitLLM.Core.Services;

public class DiscoveryCacheOptions
{
    /// <summary>
    /// Cache duration in minutes for discovery results
    /// </summary>
    public int CacheDurationMinutes { get; set; } = 360; // 6 hours

    /// <summary>
    /// Whether caching is enabled
    /// </summary>
    public bool EnableCaching { get; set; } = true;

    /// <summary>
    /// Whether discovery responses include the operator-configured model pricing.
    /// Virtual keys already observe billed spend in these units, so rates are
    /// derivable either way; operators that resell access at a markup can set
    /// this to false (Discovery:ExposePricing) to keep their cost basis private.
    /// </summary>
    public bool ExposePricing { get; set; } = true;

    /// <summary>
    /// Whether to warm cache on startup
    /// </summary>
    public bool WarmCacheOnStartup { get; set; } = false;

    /// <summary>
    /// Delay in seconds before starting cache warming to allow application to fully start
    /// </summary>
    public int WarmupStartupDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Whether to use distributed lock for cache warming coordination across instances
    /// </summary>
    public bool UseDistributedLockForWarming { get; set; } = true;

    /// <summary>
    /// Timeout in seconds for acquiring distributed lock
    /// </summary>
    public int DistributedLockTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Priority models for cache warming
    /// </summary>
    public List<string> PriorityModels { get; set; } = new() { "gpt-4", "claude-3", "gemini-pro" };

    /// <summary>
    /// Common capability filters to warm
    /// </summary>
    public List<string> WarmupCapabilities { get; set; } = new()
    {
        "chat",
        "image_input",
        "video_input",
        "audio_input",
        "file_input",
        "pdf_input",
        "image_generation",
        "video_generation"
    };
}
