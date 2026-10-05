namespace ConduitLLM.Core.Caching;

/// <summary>A required application-cache mutation failed and must remain pending in durable transport.</summary>
public sealed class ApplicationCacheInvalidationException(ApplicationCacheDomain domain, Exception innerException)
    : Exception($"Required {ApplicationCacheOptions.Tag(domain)} cache invalidation failed.", innerException)
{
    public ApplicationCacheDomain Domain { get; } = domain;
}
