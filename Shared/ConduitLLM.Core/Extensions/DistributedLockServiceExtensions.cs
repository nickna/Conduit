using ConduitLLM.Core.Data;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Extensions;

public static class DistributedLockServiceExtensions
{
    public static IServiceCollection AddConduitDistributedLocks(this IServiceCollection services)
    {
        services.TryAddSingleton<PostgresDistributedLockProvider>(provider =>
        {
            var (_, connectionString) = new ConnectionStringManager().GetProviderAndConnectionString();
            return new PostgresDistributedLockProvider(connectionString,
                provider.GetRequiredService<ILogger<PostgresDistributedLockProvider>>());
        });
        // DL-2: adapter is resolvable, but remains inactive until caller/integration gates.
        services.TryAddSingleton<IDistributedLockService, PostgresDistributedLockService>();
        services.TryAddSingleton<IDistributedLockProvider, LegacyDistributedLockProvider>();
        return services;
    }
}
