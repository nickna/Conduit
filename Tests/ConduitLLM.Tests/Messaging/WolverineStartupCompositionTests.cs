using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Migrator;
using ConduitLLM.SchemaStartupFixtures;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Wolverine.Runtime;
using Wolverine.Postgresql;

namespace ConduitLLM.Tests.Messaging;

[Collection("MigrationEnvironment")]
public sealed class WolverineStartupCompositionTests
{
    private const string Unavailable = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1;Command Timeout=1";

    [SkippableFact]
    public async Task AutoProvisionOptIn_StillGatesApplicationSchemaAndThenProvisionsRuntimeStorage()
    {
        var management = Environment.GetEnvironmentVariable("CONDUIT_LOCK_TEST_POSTGRES");
        Skip.If(string.IsNullOrEmpty(management), "Requires PostgreSQL database creation rights.");
        var fixture = await SchemaStartupDatabase.CreateAsync(management!, previousSchema: false);
        var variables = new[] { "DATABASE_URL", "ConduitLLM__Messaging__Backend", "ConduitLLM__Messaging__Wolverine__Transport" };
        var originals = variables.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            using var host = CreateHost();
            var state = host.Services.GetRequiredService<WolverineRuntimeStartupState>();
            await host.StartAsync();
            Assert.False(state.IsReady);
            var source = host.Services.GetRequiredService<NpgsqlDataSource>();
            var storage = new WolverineStorageStartupProbe(source, "wolverine_conduit_gateway", "wolverine_queues", [WolverineQueueNames.GatewayEvents]);
            Assert.False(await storage.IsReadyAsync());
            Environment.SetEnvironmentVariable(variables[0], fixture.ConnectionString);
            Environment.SetEnvironmentVariable(variables[1], "Wolverine");
            Environment.SetEnvironmentVariable(variables[2], "InMemory");
            // The real migrator applies only application DDL in this test;
            // Wolverine storage must be created by the explicit runtime opt-in.
            Assert.Equal(0, await MigrationRunner.RunAsync());
            var deferred = Assert.Single(host.Services.GetServices<IHostedService>().OfType<SchemaGatedWolverineHostedService>());
            await deferred.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(state.IsReady);
            Assert.True(await storage.IsReadyAsync());
            await host.StopAsync();

            IHost CreateHost()
            {
                var configuration = Configuration("Postgresql", true);
                return Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
                    .AddConduitWolverine(configuration, fixture.ConnectionString, "conduit-gateway",
                        options => options.ListenToPostgresqlQueue(WolverineQueueNames.GatewayEvents))
                    .DeferWolverineStartupUntilSchemaReady(configuration, "conduit-gateway")
                    .ConfigureServices((_, services) =>
                    {
                        services.AddSingleton(new MigrationStartupOptions { Mode = MigrationMode.Wait });
                        services.AddSingleton(_ => NpgsqlDataSource.Create(fixture.ConnectionString));
                    }).Build();
            }
        }
        finally
        {
            for (var index = 0; index < variables.Length; index++) Environment.SetEnvironmentVariable(variables[index], originals[index]);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await SchemaStartupDatabase.DropAsync(management!, fixture.Name, cleanup.Token);
        }
    }

    [Theory]
    [InlineData(false, false, MigrationMode.Wait, "Postgresql", false)]
    [InlineData(false, true, MigrationMode.Skip, "Postgresql", false)]
    [InlineData(false, true, MigrationMode.Wait, "InMemory", false)]
    [InlineData(false, true, MigrationMode.Wait, "Postgresql", false)]
    [InlineData(false, true, MigrationMode.Wait, "Postgresql", true)]
    [InlineData(true, false, MigrationMode.Wait, "Postgresql", false)]
    [InlineData(true, true, MigrationMode.Skip, "Postgresql", false)]
    [InlineData(true, true, MigrationMode.Wait, "InMemory", false)]
    [InlineData(true, true, MigrationMode.Wait, "Postgresql", false)]
    [InlineData(true, true, MigrationMode.Wait, "Postgresql", true)]
    public async Task HostRegistration_WrapsOnlyDeferredRuntimeAndPreservesOtherHostedServices(
        bool webBuilder, bool enable, MigrationMode mode, string transport, bool autoProvision)
    {
        var configuration = Configuration(transport, autoProvision);
        var instance = new Sentinel();
        var factory = new Sentinel();
        var keyed = new Sentinel();
        var factoryCalls = 0;
        var services = new ServiceCollection();
        services.AddSingleton<IHostedService>(instance);
        services.AddSingleton<IHostedService, TypeSentinel>();
        services.AddSingleton<IHostedService>(_ => { factoryCalls++; return factory; });
        services.AddKeyedSingleton<IHostedService>("sentinel", (_, _) => keyed);
        IHost host;
        if (webBuilder)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            foreach (var descriptor in services) builder.Services.Add(descriptor);
            Configure(builder.Host);
            // Deliberately registered after the defer callback, as in Gateway.
            RegisterMigration(builder.Services);
            host = builder.Build();
        }
        else
        {
            var builder = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureServices((_, collection) => { foreach (var descriptor in services) collection.Add(descriptor); });
            Configure(builder);
            builder.ConfigureServices((_, collection) => RegisterMigration(collection));
            host = builder.Build();
        }
        TypeSentinel? typed = null;
        try
        {
            var deferred = enable && mode == MigrationMode.Wait && transport == "Postgresql";
            var state = host.Services.GetService<WolverineRuntimeStartupState>();
            if (state is not null) Assert.Equal(!deferred, state.IsReady); // Before hosted-service enumeration.
            var hosted = host.Services.GetServices<IHostedService>().ToArray();
            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            Assert.Same(runtime, host.Services.GetRequiredService<IWolverineRuntime>());
            Assert.Contains(instance, hosted);
            Assert.Contains(factory, hosted);
            typed = Assert.Single(hosted.OfType<TypeSentinel>());
            Assert.Same(keyed, host.Services.GetRequiredKeyedService<IHostedService>("sentinel"));
            Assert.Equal(1, factoryCalls);
            if (deferred)
            {
                var startupState = state ?? throw new InvalidOperationException("Deferred Wolverine startup state was not registered.");
                Assert.Single(hosted.OfType<SchemaGatedWolverineHostedService>());
                Assert.DoesNotContain(hosted, service => ReferenceEquals(service, runtime));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await host.StartAsync(deadline.Token);
                Assert.False(startupState.IsReady);
                Assert.Equal(1, instance.Starts);
                Assert.Equal(1, factory.Starts);
                await host.StopAsync(deadline.Token);
                Assert.False(startupState.IsReady);
            }
            else
            {
                Assert.Empty(hosted.OfType<SchemaGatedWolverineHostedService>());
                Assert.Contains(hosted, service => ReferenceEquals(service, runtime));
            }
        }
        finally
        {
            // Own this exact host; its async path joins/disposes hosted services
            // before the assertions inspect factory-created sentinel ownership.
            if (host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync();
            else host.Dispose();
        }
        Assert.Equal(0, instance.Disposals); // A supplied instance remains caller owned.
        Assert.Equal(1, factory.Disposals);
        Assert.Equal(1, typed!.Disposals);
        Assert.Equal(1, keyed.Disposals);

        void Configure(IHostBuilder builder)
        {
            builder.AddConduitWolverine(configuration, Unavailable, "conduit-gateway");
            if (enable) builder.DeferWolverineStartupUntilSchemaReady(configuration, "conduit-gateway");
        }
        void RegisterMigration(IServiceCollection collection)
        {
            collection.AddSingleton(new MigrationStartupOptions { Mode = mode });
            collection.AddSingleton(_ => NpgsqlDataSource.Create(Unavailable));
        }
    }

    private static IConfiguration Configuration(string transport, bool autoProvision) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WolverineMessagingExtensions.TransportKey] = transport,
            [WolverineMessagingExtensions.AutoProvisionKey] = autoProvision.ToString()
        }).Build();

    private class Sentinel : IHostedService, IDisposable
    {
        public int Starts;
        public int Disposals;
        public Task StartAsync(CancellationToken token) { Starts++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
        public void Dispose() => Disposals++;
    }

    private sealed class TypeSentinel : Sentinel
    {
        public TypeSentinel() { }
    }
}
