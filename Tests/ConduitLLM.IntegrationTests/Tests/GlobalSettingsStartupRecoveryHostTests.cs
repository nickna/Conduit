using System.Net;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Models;
using ConduitLLM.IntegrationTests.Infrastructure;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace ConduitLLM.IntegrationTests.Tests;

[CollectionDefinition("Global settings startup recovery", DisableParallelization = true)]
public sealed class GlobalSettingsStartupRecoveryCollection : ICollectionFixture<PostgresLockTestContainerFixture>;

[Collection("Global settings startup recovery")]
[Trait("Category", "Integration")]
[Trait("Component", "GlobalSettings")]
public sealed class GlobalSettingsStartupRecoveryHostTests(PostgresLockTestContainerFixture fixture)
{
    [Fact]
    public async Task RunningAdminRecoversStoredSettingsAndItsCachedHttpBehaviorWithoutReloadOrRestart()
    {
        await using var database = await SettingsDatabase.CreateAsync(fixture.ConnectionString);
        using var environment = ConfigureEnvironment(database.ConnectionString);
        var fault = new StartupLoadFault();
        await using var factory = new RecoveryAdminFactory(fault);
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", RecoveryAdminFactory.MasterKey);
        var cache = Assert.IsType<GlobalSettingsCacheService>(factory.Services.GetRequiredService<IGlobalSettingsCacheService>());
        Assert.Same(cache, Assert.Single(factory.Services.GetServices<IHostedService>().OfType<GlobalSettingsCacheService>()));
        var originalHost = factory.RunningHost;
        await fault.InitialFailure.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await fault.RecoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(5, await cache.GetMaxAgenticIterationsAsync());
        Assert.True(await cache.GetDefaultAgenticModeEnabledAsync());
        Assert.False(await PromptCachingEnabledAsync(client));

        fault.AllowRecovery.TrySetResult();
        await fault.DatabaseReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (await cache.GetMaxAgenticIterationsAsync() != 17 || await cache.GetDefaultAgenticModeEnabledAsync())
                await Task.Delay(20, deadline.Token);
        }

        Assert.True(await PromptCachingEnabledAsync(client));
        Assert.Same(originalHost, factory.RunningHost);
        Assert.Same(cache, factory.Services.GetRequiredService<IGlobalSettingsCacheService>());
        Assert.Equal(2, fault.ListCalls);
        var stats = await cache.GetCacheStatsAsync();
        Assert.Contains("Agentic.MaxIterations", stats.CachedKeys);
        Assert.Contains("Agentic.DefaultEnabled", stats.CachedKeys);
        Assert.Contains(PromptCachingConstants.SettingsKey, stats.CachedKeys);
    }

    [Fact]
    public async Task AdminHostShutdownCancelsAndDrainsAnOutstandingStartupRecovery()
    {
        await using var database = await SettingsDatabase.CreateAsync(fixture.ConnectionString);
        using var environment = ConfigureEnvironment(database.ConnectionString);
        var fault = new StartupLoadFault();
        await using var factory = new RecoveryAdminFactory(fault);
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        var cache = Assert.IsType<GlobalSettingsCacheService>(factory.Services.GetRequiredService<IGlobalSettingsCacheService>());
        Assert.Same(cache, Assert.Single(factory.Services.GetServices<IHostedService>().OfType<GlobalSettingsCacheService>()));
        await fault.RecoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));

        await factory.RunningHost.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        await fault.RecoveryCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fault.DatabaseReadCompleted.Task.IsCompleted);
        Assert.Equal(2, fault.ListCalls);
        fault.AllowRecovery.TrySetResult();
        Assert.Equal(0, (await cache.GetCacheStatsAsync()).EntryCount);
    }

    private static EnvironmentScope ConfigureEnvironment(string connectionString)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString);
        return new EnvironmentScope(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = $"postgresql://{Uri.EscapeDataString(database.Username!)}:{Uri.EscapeDataString(database.Password!)}@{database.Host}:{database.Port}/{database.Database}",
            ["REDIS_URL"] = null,
            ["CONDUIT_REDIS_CONNECTION_STRING"] = null,
            ["CONDUIT_API_TO_API_BACKEND_AUTH_KEY"] = RecoveryAdminFactory.MasterKey,
            ["CONDUIT_MIGRATION_MODE"] = "Skip",
            ["CONDUIT_OPENAPI_GENERATION"] = null,
            ["CONDUIT_ENABLE_HTTPS_REDIRECTION"] = "false",
            ["CONDUIT_MEDIA_STORAGE_TYPE"] = "InMemory",
            ["ConduitLLM__Messaging__Wolverine__Transport"] = "InMemory",
            ["Logging__EventLog__LogLevel__Default"] = "None",
            ["OpenRouterSync__Enabled"] = "false"
        });
    }

    private static async Task<bool> PromptCachingEnabledAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/v1/admin/prompt-cache-settings/config");
        var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, json);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("enabled").GetBoolean();
    }

    private sealed class RecoveryAdminFactory(StartupLoadFault fault) : WebApplicationFactory<ConduitLLM.Admin.Program>
    {
        public const string MasterKey = "startup-recovery-master-key-32-bytes";
        public IHost RunningHost { get; private set; } = null!;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                var original = Assert.Single(services, value => value.ServiceType == typeof(IGlobalSettingRepository));
                services.RemoveAll<IGlobalSettingRepository>();
                services.Add(new ServiceDescriptor(typeof(IGlobalSettingRepository), provider =>
                {
                    var repository = original.ImplementationInstance as IGlobalSettingRepository ??
                        original.ImplementationFactory?.Invoke(provider) as IGlobalSettingRepository ??
                        (IGlobalSettingRepository)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!);
                    return new FaultingSettingsRepository(repository, fault);
                }, original.Lifetime));
                services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            RunningHost = base.CreateHost(builder);
            return RunningHost;
        }
    }

    private sealed class StartupLoadFault
    {
        private int _listCalls;
        public int ListCalls => Volatile.Read(ref _listCalls);
        public TaskCompletionSource InitialFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RecoveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowRecovery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DatabaseReadCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RecoveryCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<GlobalSetting>> ListAsync(IGlobalSettingRepository inner, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _listCalls) == 1)
            {
                InitialFailure.TrySetResult();
                throw new NpgsqlException("Injected first startup settings load failure.");
            }
            RecoveryStarted.TrySetResult();
            try
            {
                await AllowRecovery.Task.WaitAsync(cancellationToken);
                var settings = await inner.ListAsync(cancellationToken);
                DatabaseReadCompleted.TrySetResult();
                return settings;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RecoveryCancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class FaultingSettingsRepository(IGlobalSettingRepository inner, StartupLoadFault fault) : IGlobalSettingRepository
    {
        public Task<IReadOnlyList<GlobalSetting>> ListAsync(CancellationToken cancellationToken = default) => fault.ListAsync(inner, cancellationToken);
        public Task<GlobalSetting?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => inner.GetByIdAsync(id, cancellationToken);
        public Task<GlobalSetting?> GetByKeyAsync(string key, CancellationToken cancellationToken = default) => inner.GetByKeyAsync(key, cancellationToken);
        public Task<int> CreateAsync(GlobalSetting setting, CancellationToken cancellationToken = default) => inner.CreateAsync(setting, cancellationToken);
        public Task<bool> UpdateAsync(GlobalSetting setting, CancellationToken cancellationToken = default) => inner.UpdateAsync(setting, cancellationToken);
        public Task<bool> UpsertAsync(string key, string value, string? description = null, CancellationToken cancellationToken = default) =>
            inner.UpsertAsync(key, value, description, cancellationToken);
        public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, cancellationToken);
        public Task<bool> DeleteByKeyAsync(string key, CancellationToken cancellationToken = default) => inner.DeleteByKeyAsync(key, cancellationToken);
    }

    private sealed class SettingsDatabase : IAsyncDisposable
    {
        private readonly string _ownerConnectionString;
        private readonly string _databaseName;
        public string ConnectionString { get; }

        private SettingsDatabase(string ownerConnectionString)
        {
            _ownerConnectionString = ownerConnectionString;
            _databaseName = "conduit_settings_recovery_" + Guid.NewGuid().ToString("N");
            ConnectionString = new NpgsqlConnectionStringBuilder(ownerConnectionString) { Database = _databaseName }.ConnectionString;
        }

        public static async Task<SettingsDatabase> CreateAsync(string ownerConnectionString)
        {
            var database = new SettingsDatabase(ownerConnectionString);
            await using var owner = new NpgsqlConnection(ownerConnectionString);
            await owner.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database._databaseName}\"", owner))
                await create.ExecuteNonQueryAsync();
            try
            {
                await using var context = new ConduitDbContext(new DbContextOptionsBuilder<ConduitDbContext>()
                    .UseNpgsql(database.ConnectionString).Options);
                await context.Database.MigrateAsync();
                var values = new Dictionary<string, string>
                {
                    ["Agentic.MaxIterations"] = "17",
                    ["Agentic.DefaultEnabled"] = "false",
                    [PromptCachingConstants.SettingsKey] = $$"""{"schema_version":{{PromptCachingConstants.SchemaVersion}},"enabled":true,"rules":[]}"""
                };
                foreach (var (key, value) in values)
                {
                    var stored = await context.GlobalSettings.SingleOrDefaultAsync(setting => setting.Key == key);
                    if (stored is null) context.GlobalSettings.Add(new GlobalSetting { Key = key, Value = value });
                    else stored.Value = value;
                }
                await context.SaveChangesAsync();
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await using var owner = new NpgsqlConnection(_ownerConnectionString);
            await owner.OpenAsync();
            // This identifier is generated entirely from this test's prefix and GUID.
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", owner);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
