using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Functions.Utilities;

/// <summary>
/// Invalidates the function-cost namespace across independent memory caches without
/// enumerating keys. A snapshot pins a cache miss and its database result to one generation.
/// </summary>
internal sealed class FunctionCostCache
{
    private const string GenerationKey = "FunctionCost:Generation:v1";
    private const string DataKeyPrefix = "FunctionCost:v2:";
    private static readonly TimeSpan MemoryExpiration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DistributedExpiration = TimeSpan.FromHours(1);
    private static string _localGeneration = NewGeneration();

    private readonly IMemoryCache _memoryCache;
    private readonly IDistributedCache? _distributedCache;
    private readonly ILogger _logger;

    public FunctionCostCache(IMemoryCache memoryCache, IDistributedCache? distributedCache, ILogger logger)
    {
        _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
        _distributedCache = distributedCache;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Snapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        if (_distributedCache is null)
            return new Snapshot(this, Volatile.Read(ref _localGeneration));

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var bytes = await _distributedCache.GetAsync(GenerationKey, cancellationToken);
            string generation;
            if (bytes is null)
            {
                // Never reuse a fixed initial generation: eviction of the marker must
                // not make a previous generation's still-live payloads visible again.
                generation = NewGeneration();
                await WriteGenerationAsync(generation, cancellationToken);
            }
            else
            {
                if (bytes.Length != 32)
                    throw new InvalidOperationException("Function cost cache generation is invalid.");
                generation = Encoding.UTF8.GetString(bytes);
                if (!Guid.TryParseExact(generation, "N", out var identifier) || identifier == Guid.Empty ||
                    !string.Equals(generation, identifier.ToString("N"), StringComparison.Ordinal))
                    throw new InvalidOperationException("Function cost cache generation is invalid.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new Snapshot(this, generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A remote clear may have succeeded while this host cannot read the
            // marker. Neither an old L1 entry nor a cached marker is authoritative.
            _logger.LogWarning(exception, "Function cost cache generation is unavailable; bypassing both cache tiers");
            return new Snapshot(this, null);
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = NewGeneration();
        if (_distributedCache is not null)
            await WriteGenerationAsync(generation, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _localGeneration, generation);
        // Retired payloads remain unreachable and expire under their existing TTLs.
        // A failed shared-marker write is deliberately not reported as a clear.
    }

    private Task WriteGenerationAsync(string generation, CancellationToken cancellationToken) =>
        _distributedCache!.SetAsync(GenerationKey, Encoding.UTF8.GetBytes(generation),
            new DistributedCacheEntryOptions(), cancellationToken);

    private static string NewGeneration() => Guid.NewGuid().ToString("N");

    internal sealed class Snapshot
    {
        private readonly FunctionCostCache _cache;
        private readonly string? _generation;

        internal Snapshot(FunctionCostCache cache, string? generation)
        {
            _cache = cache;
            _generation = generation;
        }

        public async Task<T?> GetAsync<T>(string key, JsonTypeInfo<T> jsonTypeInfo,
            CancellationToken cancellationToken = default)
        {
            if (_generation is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return default;
            }

            var fullKey = BuildKey(key);
            // Preserve the memory-only cache-hit behavior for canceled callers.
            // Shared-cache captures already observe cancellation before checking L1.
            if (_cache._memoryCache.TryGetValue(fullKey, out T? memoryValue))
                return memoryValue;

            cancellationToken.ThrowIfCancellationRequested();
            if (_cache._distributedCache is null)
                return default;

            try
            {
                var bytes = await _cache._distributedCache.GetAsync(fullKey, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (bytes is null || bytes.Length == 0)
                    return default;

                var value = JsonSerializer.Deserialize(bytes, jsonTypeInfo);
                if (value is not null)
                    _cache._memoryCache.Set(fullKey, value, MemoryExpiration);
                return value;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _cache._logger.LogWarning(exception, "Error retrieving function cost payload from distributed cache");
                return default;
            }
        }

        public async Task SetAsync<T>(string key, T value, JsonTypeInfo<T> jsonTypeInfo,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_generation is null || value is null)
                return;

            var fullKey = BuildKey(key);
            if (_cache._distributedCache is not null)
            {
                try
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(value, jsonTypeInfo);
                    await _cache._distributedCache.SetAsync(fullKey, bytes,
                        new DistributedCacheEntryOptions
                        {
                            AbsoluteExpirationRelativeToNow = DistributedExpiration
                        }, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _cache._logger.LogWarning(exception, "Error writing function cost payload to distributed cache");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            _cache._memoryCache.Set(fullKey, value, MemoryExpiration);
        }

        private string BuildKey(string key) => $"{DataKeyPrefix}{_generation}:{key}";
    }
}
