using System.Text.Json;

using ConduitLLM.Functions.Utilities;
using ConduitLLM.Functions.Serialization;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace ConduitLLM.Tests.Functions.Utilities;

public sealed class HybridCacheAccessorTests
{
    [Fact]
    public async Task SetAsync_DistributedWriteFails_StillPopulatesMemory()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var distributedCache = new Mock<IDistributedCache>();
        distributedCache
            .Setup(cache => cache.SetAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Redis unavailable"));
        var accessor = CreateAccessor(memoryCache, distributedCache.Object, "test:");

        await accessor.SetAsync("key", "value", FunctionsJsonContext.Default.String);

        Assert.Equal("value", await accessor.GetAsync("key", FunctionsJsonContext.Default.String));
    }

    [Fact]
    public async Task Accessors_WithDifferentPrefixes_DoNotCollide()
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var first = CreateAccessor(memoryCache, distributedCache: null, "first:");
        var second = CreateAccessor(memoryCache, distributedCache: null, "second:");

        await first.SetAsync("same", "one", FunctionsJsonContext.Default.String);
        await second.SetAsync("same", "two", FunctionsJsonContext.Default.String);

        Assert.Equal("one", await first.GetAsync("same", FunctionsJsonContext.Default.String));
        Assert.Equal("two", await second.GetAsync("same", FunctionsJsonContext.Default.String));
    }

    private static HybridCacheAccessor CreateAccessor(
        IMemoryCache memoryCache,
        IDistributedCache? distributedCache,
        string prefix) =>
        new(
            memoryCache,
            distributedCache,
            NullLogger.Instance,
            prefix,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2));
}
