using ConduitLLM.Configuration.Data;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace ConduitLLM.Configuration.Messaging.Wolverine;

public static class WolverineStartupExtensions
{
    /// <summary>
    /// Opts web hosts into read-only startup gating. Call after AddConduitWolverine;
    /// release/tooling hosts deliberately retain the immediate runtime lifecycle.
    /// </summary>
    public static IHostBuilder DeferWolverineStartupUntilSchemaReady(this IHostBuilder host,
        IConfiguration configuration, string serviceName)
    {
        if (WolverineMessagingExtensions.UsesInMemoryTransport(configuration)) return host;
        var schema = configuration[WolverineMessagingExtensions.SchemaNameKey]
                     ?? $"wolverine_{serviceName.Replace('-', '_')}";
        var autoProvision = configuration.GetValue(WolverineMessagingExtensions.AutoProvisionKey, false);
        return host.ConfigureServices((_, services) =>
        {
            services.TryAddSingleton(provider =>
            {
                var state = new WolverineRuntimeStartupState();
                // Resolve the already registered migration options when the graph
                // activates, including hosts registering them after messaging.
                if (provider.GetRequiredService<MigrationStartupOptions>().Mode == MigrationMode.Wait)
                    state.MarkDeferred();
                return state;
            });
            services.TryAddSingleton<ReleaseMigrationStartupProbe>();
            services.TryAddSingleton(provider => new WolverineStorageStartupProbe(
                provider.GetRequiredService<NpgsqlDataSource>(), schema,
                WolverineMessagingExtensions.TransportSchemaName,
                WolverineQueueNames.Gateway.Concat(WolverineQueueNames.Admin)));

            for (var index = 0; index < services.Count; index++)
            {
                var descriptor = services[index];
                if (descriptor.ServiceType != typeof(IHostedService) || descriptor.IsKeyedService ||
                    descriptor.ImplementationFactory is not { } original) continue;
                // Wolverine 6.48.3 registers its runtime behind an opaque factory.
                // Forward each candidate without reflection; only the actual
                // runtime singleton receives a different hosted lifecycle.
                services[index] = ServiceDescriptor.Describe(typeof(IHostedService), provider =>
                {
                    var service = original(provider);
                    if (service is not IWolverineRuntime ||
                        !ReferenceEquals(service, provider.GetRequiredService<IWolverineRuntime>()) ||
                        provider.GetRequiredService<MigrationStartupOptions>().Mode == MigrationMode.Skip)
                        return service;
                    var release = provider.GetRequiredService<ReleaseMigrationStartupProbe>();
                    var storage = autoProvision ? null : provider.GetRequiredService<WolverineStorageStartupProbe>();
                    var store = provider.GetRequiredService<IMessageStore>();
                    return new SchemaGatedWolverineHostedService((IHostedService)service, async token =>
                    {
                        if (!await release.IsReadyAsync(token)) return false;
                        // Preserve the explicit development opt-in: the runtime
                        // may provision bus resources only when AutoProvision=true.
                        if (autoProvision) return true;
                        if (!await storage!.IsReadyAsync(token)) return false;
                        await store.Admin.AssertStorageProvisionedAsync(token);
                        return true;
                    }, provider.GetRequiredService<WolverineRuntimeStartupState>(),
                        provider.GetRequiredService<IHostApplicationLifetime>(),
                        provider.GetRequiredService<ILogger<SchemaGatedWolverineHostedService>>());
                }, descriptor.Lifetime);
            }
        });
    }
}
