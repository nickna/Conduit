namespace ConduitLLM.Core.Caching;

public static class DiscoveryCacheKeys
{
    public static string Build(string? capability = null, int? virtualKeyId = null, bool includePricing = false)
    {
        var suffix = includePricing ? ":with_pricing" : string.Empty;
        if (virtualKeyId.HasValue)
            return capability != null ? $"virtualkey:{virtualKeyId}:capability:{capability}{suffix}" : $"virtualkey:{virtualKeyId}{suffix}";
        return capability != null ? $"capability:{capability}{suffix}" : $"all{suffix}";
    }
}
