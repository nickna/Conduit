using System.Collections.Concurrent;
using System.Diagnostics;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Core.Messaging;
using ConduitLLM.Gateway.Consumers;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wolverine;
using Wolverine.Postgresql;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Tests.Core.Caching;

/// <summary>Three independent host/cache graphs, actual Admin mutation and durable PostgreSQL transport.</summary>
[Collection("AdminCompositionEnvironment")]
public sealed class DistributedDiscoveryCacheTests
{
    private static readonly (string? Capability, int? Key, bool Pricing)[] Variants =
        [(null, null, false), (null, null, true), ("chat", null, true), ("chat", 1, false), ("chat", 2, true)];

    [SkippableFact]
    public async Task AdminMutationRetriesAcrossRestartAndConvergesBothGatewaysWithMissedBackplane()
    {
        var redis = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_REDIS");
        var postgres = Environment.GetEnvironmentVariable("CONDUIT_CACHE_TEST_POSTGRES");
        Skip.If(string.IsNullOrWhiteSpace(redis) || string.IsNullOrWhiteSpace(postgres),
            "Set CONDUIT_CACHE_TEST_REDIS and CONDUIT_CACHE_TEST_POSTGRES for the distributed gate.");
        var run = Guid.NewGuid().ToString("N");
        var database = $"conduit_cachegate_{run}";
        var root = new NpgsqlConnectionStringBuilder(postgres);
        await using (var connection = new NpgsqlConnection(root.ConnectionString))
        {
            await connection.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection).ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(postgres) { Database = database }.ConnectionString;
        var factory = new ContextFactory(new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(connectionString).Options);
        var environment = $"gate-{run}";
        await using var proxy = new RedisNetworkProxy(redis);
        var state = new FailureState();
        var gatewaySchema = $"gateway_{run}";
        IHost? admin = null, gateway1 = null, gateway2 = null;
        try
        {
            var costId = await SeedAsync(factory);
            admin = BuildHost("admin", connectionString, proxy.ConnectionString, environment, factory, state, gatewaySchema);
            gateway1 = BuildHost("gateway", connectionString, proxy.ConnectionString, environment, factory, state, gatewaySchema);
            gateway2 = BuildHost("gateway", connectionString, proxy.ConnectionString, environment, factory, state, gatewaySchema);
            await admin.StartAsync();
            await gateway1.StartAsync();
            await gateway2.StartAsync();
            await AssertVariantsAsync(gateway1, factory, 0.25m);
            await AssertVariantsAsync(gateway2, factory, 0.25m);

            // Disconnect actual L2 and pub/sub TCP sockets below the domain, retaining PostgreSQL transport.
            proxy.Disconnect();
            using (var scope = admin.Services.CreateScope())
            {
                var result = await scope.ServiceProvider.GetRequiredService<AdminModelCostService>()
                    .UpdateModelCostAsync(costId, new UpdateModelCostDto { InputCostPerMillionTokens = 0.5m, PricingConfiguration = Pricing("""{"defaultRate":0.5,"rules":[]}""") });
                Assert.Equal(0.5m, result!.InputCostPerMillionTokens);
            }
            var failedMessage = await state.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(failedMessage);
            string scheduledTable;
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                // Weasel normalizes the transport identifier. Inspect the provisioned fixture metadata.
                scheduledTable = (string)(await new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'wolverine_queues' AND tablename LIKE '%gateway%scheduled'", connection).ExecuteScalarAsync())!;
                Assert.NotNull(scheduledTable);
            }
            await UntilAsync(async () =>
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                // PostgreSQL transport uses its native scheduled table, rather than inbox status.
                await using var query = new NpgsqlCommand($"SELECT count(*) FROM wolverine_queues.\"{scheduledTable.Replace("\"", "\"\"")}\" WHERE id = @id", connection);
                query.Parameters.AddWithValue("id", failedMessage.Value);
                var count = await query.ExecuteScalarAsync();
                return Convert.ToInt64(count) > 0;
            }, TimeSpan.FromSeconds(10), "Required failure must be persisted as a scheduled transport retry.");
            await UntilAsync(async () => await VariantsMatchAsync(gateway1.Services, factory, 0.5m) && await VariantsMatchAsync(gateway2.Services, factory, 0.5m),
                TimeSpan.FromSeconds(5), "Disconnected caches must fall back to current PostgreSQL results within the discovery outage bound.");
            using (var cold = ColdHost(proxy.ConnectionString, environment, factory))
                await UntilAsync(() => VariantsMatchAsync(cold, factory, 0.5m), TimeSpan.FromSeconds(5),
                    "A new cache instance starting during the Redis outage must serve current database results.");
            await gateway1.StopAsync();
            gateway1.Dispose();
            gateway1 = null;
            await gateway2.StopAsync();
            gateway2.Dispose();
            gateway2 = null;
            proxy.Reconnect();
            gateway1 = BuildHost("gateway", connectionString, proxy.ConnectionString, environment, factory, state, gatewaySchema);
            gateway2 = BuildHost("gateway", connectionString, proxy.ConnectionString, environment, factory, state, gatewaySchema);
            await gateway1.StartAsync();
            await gateway2.StartAsync();
            await UntilAsync(() => Task.FromResult(state.Succeeded.Contains(failedMessage.Value)), TimeSpan.FromSeconds(30),
                "The original persisted message must retry successfully after host restart.");
            await AssertVariantsAsync(gateway1, factory, 0.5m);
            await AssertVariantsAsync(gateway2, factory, 0.5m);
            Assert.True(state.Attempts > state.Succeeded.Count);

            // Drop all pub/sub delivery to one already-warmed node. Tag metadata must repair it through L2.
            gateway2.Services.GetRequiredKeyedService<IFusionCache>(ApplicationCacheOptions.ServiceKey).RemoveBackplane();
            var before = state.Succeeded.Count;
            using (var scope = admin.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<AdminModelCostService>()
                    .UpdateModelCostAsync(costId, new UpdateModelCostDto { InputCostPerMillionTokens = 0.75m, PricingConfiguration = Pricing("""{"defaultRate":0.75,"rules":[]}""") });
            await UntilAsync(() => Task.FromResult(state.Succeeded.Count > before), TimeSpan.FromSeconds(15), "Admin mutation must reach a Gateway handler.");
            await UntilAsync(async () => await VariantsMatchAsync(gateway1.Services, factory, 0.75m) && await VariantsMatchAsync(gateway2.Services, factory, 0.75m),
                TimeSpan.FromSeconds(2), "Every warmed variant must converge within two seconds after event completion, even without backplane.");

            // Replayed/duplicate events are idempotent; they do not resurrect the old L2 payloads.
            before = state.Succeeded.Count;
            using (var scope = admin.Services.CreateScope())
            {
                var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
                var duplicate = new ModelCostChanged { ModelCostId = costId, ChangeType = "Updated", CostName = "Gate price" };
                await bus.PublishAsync(duplicate);
                await bus.PublishAsync(duplicate);
            }
            await UntilAsync(() => Task.FromResult(state.Succeeded.Count >= before + 2), TimeSpan.FromSeconds(15), "Both duplicate transport deliveries must complete.");
            await AssertVariantsAsync(gateway1, factory, 0.75m);
            await AssertVariantsAsync(gateway2, factory, 0.75m);

        }
        finally
        {
            foreach (var host in new[] { gateway2, gateway1, admin }.Where(host => host is not null))
            {
                await host!.StopAsync();
                host.Dispose();
            }
            // Only the generated fixture database is eligible for removal.
            Assert.StartsWith("conduit_cachegate_", database);
            await using var connection = new NpgsqlConnection(root.ConnectionString);
            await connection.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", connection).ExecuteNonQueryAsync();
        }
    }

    private static IHost BuildHost(string role, string postgres, string redis, string environment,
        IDbContextFactory<ConduitDbContext> factory, FailureState state, string gatewaySchema)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApplicationCache:Environment"] = environment,
            [WolverineMessagingExtensions.SchemaNameKey] = role == "gateway" ? gatewaySchema : gatewaySchema.Replace("gateway_", "admin_"),
            [WolverineMessagingExtensions.AutoProvisionKey] = "true"
        }).Build();
        return Host.CreateDefaultBuilder().UseDefaultServiceProvider(options => options.ValidateScopes = true)
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton(factory);
                services.AddSingleton(state);
                services.AddScoped<IModelCostRepository, ModelCostRepository>();
                services.AddScoped<IModelProviderMappingRepository, ModelProviderMappingRepository>();
                services.AddScoped<IRequestLogRepository, RequestLogRepository>();
                services.AddScoped<AdminModelCostService>();
                services.AddWolverineEventBus();
                services.AddConduitApplicationCache(configuration, role, redis);
                services.AddDiscoveryCache(configuration);
                services.AddSingleton<IModelMappingCacheInvalidator, ModelMappingCacheInvalidator>();
                services.AddModelCostCache();
                services.AddPricingRulesCache();
                if (role == "gateway")
                {
                    services.AddScoped<ModelCostCacheInvalidationHandler>(provider => new(
                        provider.GetRequiredService<ConduitLLM.Configuration.Interfaces.IModelCostService>(), provider.GetRequiredService<ICachedPricingRulesService>(),
                        provider.GetRequiredService<IDiscoveryCacheService>(), provider.GetRequiredService<ILogger<ModelCostCacheInvalidationHandler>>(),
                        provider.GetRequiredService<IModelMappingCacheInvalidator>()));
                    services.AddScoped<IEventHandler<ModelCostChanged>, ObservingCostHandler>();
                }
            })
            .AddConduitWolverine(configuration, postgres, $"conduit-cachegate-{role}", options =>
            {
                // Test-only dynamic graph; production hosts retain their committed static adapters.
                options.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Dynamic;
                options.UseRuntimeCompilation();
                options.AddEventBridge<ModelCostChanged>();
                options.ApplyConduitPublishRouting();
                options.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(100);
                if (role == "gateway")
                    options.ListenToPostgresqlQueue(ConduitMessagingTopology.GatewayEventsQueue)
                        .PollingInterval(TimeSpan.FromMilliseconds(100)).MaximumMessagesToReceive(50);
            }).Build();
    }

    private static ServiceProvider ColdHost(string redis, string environment, IDbContextFactory<ConduitDbContext> factory)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ApplicationCache:Environment"] = environment }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(factory);
        services.AddScoped<IModelCostRepository, ModelCostRepository>();
        services.AddScoped<IModelProviderMappingRepository, ModelProviderMappingRepository>();
        services.AddConduitApplicationCache(configuration, "gateway", redis);
        services.AddDiscoveryCache(configuration);
        services.AddModelCostCache();
        services.AddPricingRulesCache();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static async Task<int> SeedAsync(IDbContextFactory<ConduitDbContext> factory)
    {
        await using var context = await factory.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
        var cost = new ModelCost { CostName = "Gate price", ModelType = "chat", IsActive = true,
            EffectiveDate = DateTime.UtcNow.AddDays(-1), InputCostPerMillionTokens = 0.25m,
            PricingConfiguration = """{"defaultRate":0.25,"rules":[]}""" };
        context.ModelProviderMappings.Add(new ModelProviderMapping
        {
            ModelAlias = "cache-gate", ProviderModelId = "cache-gate", IsEnabled = true,
            Provider = new Provider { ProviderName = "Gate provider", ProviderType = ProviderType.OpenAI, IsEnabled = true },
            ModelProviderTypeAssociation = new ModelProviderTypeAssociation
            {
                IsEnabled = true, Identifier = "cache-gate", Provider = ProviderType.OpenAI, ModelCost = cost,
                Model = new Model { Name = "Gate model", SupportsChat = true, Series = new ModelSeries
                    { Name = "Gate series", Author = new ModelAuthor { Name = "Gate author" } } }
            }
        });
        await context.SaveChangesAsync();
        return cost.Id;
    }

    private static Dictionary<string, System.Text.Json.JsonElement> Pricing(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
    }

    private static async Task<bool> VariantsMatchAsync(IServiceProvider provider, IDbContextFactory<ConduitDbContext> factory, decimal price)
    {
        // Requests read this exact decorator; failure must propagate from it to Wolverine retry.
        {
            using var scope = provider.CreateScope();
            var costs = scope.ServiceProvider.GetRequiredService<ConduitLLM.Configuration.Interfaces.IModelCostService>();
            var cost = await costs.GetCostForModelAsync("cache-gate");
            if (cost?.InputCostPerMillionTokens != price) return false;
            var rules = await provider.GetRequiredService<ICachedPricingRulesService>().GetConfigAsync(cost.Id, cost.PricingConfiguration!);
            if (rules?.DefaultRate != price) return false;
            var billing = new CostCalculationService(costs, NullLogger<CostCalculationService>.Instance);
            if (await billing.CalculateCostAsync("cache-gate", new ConduitLLM.Core.Models.Usage { PromptTokens = 1_000_000 }) != price) return false;
        }
        var service = provider.GetRequiredService<IDiscoveryCacheService>();
        foreach (var variant in Variants)
        {
            var result = await service.GetOrLoadAsync(ConduitLLM.Core.Caching.DiscoveryCacheKeys.Build(variant.Capability, variant.Key, variant.Pricing),
                token => DiscoveryCacheLoader.LoadAsync(factory, variant.Capability, variant.Pricing, GatewayJsonOptions.Create(), NullLogger.Instance, token));
            if (result.Data.Count != 1) return false;
            if (variant.Pricing)
            {
                if (result.Data[0].GetProperty("pricing").GetProperty("input_cost_per_million_tokens").GetDecimal() != price) return false;
            }
            else if (result.Data[0].TryGetProperty("pricing", out _)) return false;
        }
        return true;
    }

    private static async Task AssertVariantsAsync(IHost host, IDbContextFactory<ConduitDbContext> factory, decimal price) =>
        Assert.True(await VariantsMatchAsync(host.Services, factory, price));

    private static async Task AssertVariantsAsync(IServiceProvider provider, IDbContextFactory<ConduitDbContext> factory, decimal price) =>
        Assert.True(await VariantsMatchAsync(provider, factory, price));

    private static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string failure)
    {
        var stopwatch = Stopwatch.StartNew();
        do
        {
            if (await condition()) { Assert.True(stopwatch.Elapsed <= timeout, $"{failure} Elapsed: {stopwatch.Elapsed}."); return; }
            await Task.Delay(20);
        } while (stopwatch.Elapsed < timeout);
        Assert.Fail(failure);
    }

    private sealed class ContextFactory(DbContextOptions<ConduitDbContext> options) : IDbContextFactory<ConduitDbContext>
    {
        public ConduitDbContext CreateDbContext() => new(options);
    }

    public sealed class FailureState
    {
        public int Attempts;
        public ConcurrentBag<Guid> Succeeded { get; } = [];
        public TaskCompletionSource<Guid?> FirstFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class ObservingCostHandler(ModelCostCacheInvalidationHandler inner, FailureState state) : IEventHandler<ModelCostChanged>
    {
        public async Task HandleAsync(ModelCostChanged message, IEventContext context)
        {
            Interlocked.Increment(ref state.Attempts);
            try { await inner.HandleAsync(message, context); }
            catch (ApplicationCacheInvalidationException) { state.FirstFailure.TrySetResult(context.MessageId); throw; }
            state.Succeeded.Add(context.MessageId!.Value);
        }
    }

}
