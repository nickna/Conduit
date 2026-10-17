using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Services;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;

using StackExchange.Redis;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed class FunctionCostCacheRedisTests(FunctionCredentialRedisFixture fixture)
    : IClassFixture<FunctionCredentialRedisFixture>
{
    private const string GenerationKey = "FunctionCost:Generation:v1";
    private const int CostId = 1570;
    private const int ConfigurationId = 1571;
    private const int TypedConfigurationId = 1572;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdClearRefreshesEveryCostKeyOnBothWarmHosts(bool typedClearer)
    {
        await WithOwnedCacheAsync(async (services, database, prefix, token) =>
        {
            await AssertWarmHostsAsync(services, "old", 1.25m, token);
            var retiredGeneration = await ReadGenerationAsync(services.Distributed, database, prefix, token);
            services.Current = Cost("replacement", 9.75m);

            // Both independent L1 caches and the typed host retain their old snapshots.
            await AssertWarmHostsAsync(services, "old", 1.25m, token);

            // Neither clearer has read a cost, so invalidation cannot depend on a local key registry.
            await (typedClearer
                ? services.ColdTyped.ClearCacheAsync()
                : services.ColdLegacy.ClearCacheAsync()).WaitAsync(token);

            var replacementGeneration = await ReadGenerationAsync(services.Distributed, database, prefix, token);
            Assert.NotEqual(retiredGeneration, replacementGeneration);
            await AssertRetiredPayloadsHaveBoundedTtlAsync(database, prefix, retiredGeneration, token);
            await AssertWarmHostsAsync(services, "replacement", 9.75m, token);
            services.TypedStore.Verify(store => store.FindEligibleCostAsync(TypedConfigurationId,
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        });
    }

    [Fact]
    public async Task EvictedGenerationMarkerCannotResurrectStillLivePayloadsOrWarmMemory()
    {
        await WithOwnedCacheAsync(async (services, database, prefix, token) =>
        {
            await AssertWarmHostsAsync(services, "old", 1.25m, token);
            var retiredGeneration = await ReadGenerationAsync(services.Distributed, database, prefix, token);
            services.Current = Cost("after-marker-eviction", 4.5m);

            Assert.True(await database.KeyDeleteAsync(prefix + GenerationKey).WaitAsync(token));
            await AssertRetiredPayloadsHaveBoundedTtlAsync(database, prefix, retiredGeneration, token);
            await AssertWarmHostsAsync(services, "after-marker-eviction", 4.5m, token);

            var replacementGeneration = await ReadGenerationAsync(services.Distributed, database, prefix, token);
            Assert.NotEqual(retiredGeneration, replacementGeneration);
            // Old payloads remain physically present; freshness comes from a new namespace.
            await AssertRetiredPayloadsHaveBoundedTtlAsync(database, prefix, retiredGeneration, token);
            services.TypedStore.Verify(store => store.FindEligibleCostAsync(TypedConfigurationId,
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        });
    }

    private async Task WithOwnedCacheAsync(
        Func<Services, IDatabase, string, CancellationToken, Task> exercise)
    {
        var prefix = $"issue1570:{Guid.NewGuid():N}:";
        var options = ConfigurationOptions.Parse(await fixture.GetConnectionStringAsync());
        options.ConnectTimeout = options.SyncTimeout = options.AsyncTimeout = 5000;
        options.ConnectRetry = 0;
        options.AbortOnConnectFail = true;
        using var redis = await ConnectionMultiplexer.ConnectAsync(options);
        var database = redis.GetDatabase();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var sentinel = prefix + "unrelated:sentinel";
        var sentinelValue = Guid.NewGuid().ToString("N");
        try
        {
            Assert.True(await database.StringSetAsync(sentinel, sentinelValue, when: When.NotExists)
                .WaitAsync(deadline.Token));
            using (var services = new Services(options, prefix))
            {
                await exercise(services, database, prefix, deadline.Token);
                Assert.Equal(sentinelValue, (string?)await database.StringGetAsync(sentinel).WaitAsync(deadline.Token));
                Assert.Null(await database.KeyTimeToLiveAsync(sentinel).WaitAsync(deadline.Token));
            }
        }
        finally
        {
            // All cache producers and their Redis connection are disposed before enumeration.
            // The inspector owns only this GUID prefix; never flush the borrowed Redis database.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ownedKeys = new HashSet<RedisKey>();
            foreach (var endpoint in redis.GetEndPoints())
            {
                var server = redis.GetServer(endpoint);
                if (server.IsReplica) continue;
                await foreach (var key in server.KeysAsync(database.Database, prefix + "*", pageSize: 100)
                    .WithCancellation(cleanup.Token))
                {
                    Assert.StartsWith(prefix, key.ToString(), StringComparison.Ordinal);
                    ownedKeys.Add(key);
                }
            }
            if (ownedKeys.Count != 0)
            {
                await database.KeyDeleteAsync(ownedKeys.ToArray()).WaitAsync(cleanup.Token);
                foreach (var key in ownedKeys)
                    Assert.False(await database.KeyExistsAsync(key).WaitAsync(cleanup.Token));
            }
        }
    }

    private static async Task AssertWarmHostsAsync(
        Services services, string name, decimal price, CancellationToken token)
    {
        foreach (var legacy in new[] { services.WarmA, services.WarmB })
        {
            AssertCost(Assert.Single(await legacy.ListCostsAsync(cancellationToken: token)), name, price);
            AssertCost(Assert.Single(await legacy.ListCostsAsync(activeOnly: true, cancellationToken: token)), name, price);
            AssertCost(await legacy.GetCostByIdAsync(CostId, token), name, price);
            AssertCost(await legacy.GetCostForConfigurationAsync(ConfigurationId, token), name, price);
        }
        // This distinct key belongs to the typed service, even though the second host shares its L1.
        AssertCost(await services.WarmTyped.GetCostForConfigurationAsync(TypedConfigurationId, token), name, price);
    }

    private static void AssertCost(FunctionCost? actual, string name, decimal price)
    {
        Assert.NotNull(actual);
        Assert.Equal(CostId, actual.Id);
        Assert.Equal(name, actual.CostName);
        Assert.Equal(price, actual.CostPerExecution);
    }

    private static async Task<string> ReadGenerationAsync(
        IDistributedCache distributed, IDatabase database, string prefix, CancellationToken token)
    {
        var generation = await distributed.GetStringAsync(GenerationKey, token);
        Assert.NotNull(generation);
        Assert.True(Guid.TryParseExact(generation, "N", out var identifier));
        Assert.NotEqual(Guid.Empty, identifier);
        Assert.True(await database.KeyExistsAsync(prefix + GenerationKey).WaitAsync(token));
        Assert.Null(await database.KeyTimeToLiveAsync(prefix + GenerationKey).WaitAsync(token));
        return generation;
    }

    private static async Task AssertRetiredPayloadsHaveBoundedTtlAsync(
        IDatabase database, string prefix, string generation, CancellationToken token)
    {
        foreach (var suffix in new[] { "All", "All_Active", $"Id:{CostId}",
                     $"Config:{ConfigurationId}", $"Config:{TypedConfigurationId}" })
        {
            var key = $"{prefix}FunctionCost:v2:{generation}:{suffix}";
            Assert.True(await database.KeyExistsAsync(key).WaitAsync(token), key);
            var ttl = await database.KeyTimeToLiveAsync(key).WaitAsync(token);
            var lifetime = Assert.IsType<TimeSpan>(ttl);
            Assert.InRange(lifetime, TimeSpan.FromTicks(1), TimeSpan.FromHours(1));
        }
    }

    private static FunctionCost Cost(string name, decimal price) => new()
    {
        Id = CostId,
        CostName = name,
        ProviderType = FunctionProviderType.Exa,
        PricingModel = FunctionPricingModel.FlatRate,
        CostPerExecution = price,
        IsActive = true,
        EffectiveDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private sealed class Services : IDisposable
    {
        private readonly MemoryCache _warmMemoryA = new(new MemoryCacheOptions());
        private readonly MemoryCache _warmMemoryB = new(new MemoryCacheOptions());
        private readonly MemoryCache _coldMemory = new(new MemoryCacheOptions());

        public FunctionCost Current { get; set; } = Cost("old", 1.25m);
        public RedisCache Distributed { get; }
        public FunctionCostService WarmA { get; }
        public FunctionCostService WarmB { get; }
        public StoreBackedFunctionCostService WarmTyped { get; }
        public FunctionCostService ColdLegacy { get; }
        public StoreBackedFunctionCostService ColdTyped { get; }
        public Mock<IFunctionPricingRuntimeStore> TypedStore { get; } = new(MockBehavior.Strict);

        public Services(ConfigurationOptions options, string prefix)
        {
            Distributed = new RedisCache(Options.Create(new RedisCacheOptions
            {
                ConfigurationOptions = options.Clone(),
                InstanceName = prefix
            }));
            var repository = new Mock<IFunctionCostRepository>(MockBehavior.Strict);
            repository.Setup(value => value.GetByIdAsync(CostId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Current);
            repository.Setup(value => value.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new List<FunctionCost> { Current });
            var mappings = new Mock<IFunctionCostMappingRepository>(MockBehavior.Strict);
            mappings.Setup(value => value.GetByFunctionConfigurationIdAsync(ConfigurationId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new List<FunctionCostMapping>
                {
                    new() { FunctionConfigurationId = ConfigurationId, FunctionCostId = CostId, IsActive = true }
                });
            TypedStore.Setup(value => value.FindEligibleCostAsync(TypedConfigurationId,
                    It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Current);
            WarmA = Legacy(_warmMemoryA);
            WarmB = Legacy(_warmMemoryB);
            ColdLegacy = Legacy(_coldMemory);
            WarmTyped = Typed(_warmMemoryB);
            ColdTyped = Typed(_coldMemory);

            FunctionCostService Legacy(IMemoryCache memory) => new(repository.Object, mappings.Object, memory,
                NullLogger<FunctionCostService>.Instance, Distributed);
            StoreBackedFunctionCostService Typed(IMemoryCache memory) => new(TypedStore.Object, memory,
                NullLogger<StoreBackedFunctionCostService>.Instance, Distributed);
        }

        public void Dispose()
        {
            _warmMemoryA.Dispose();
            _warmMemoryB.Dispose();
            _coldMemory.Dispose();
            Distributed.Dispose();
        }
    }
}
