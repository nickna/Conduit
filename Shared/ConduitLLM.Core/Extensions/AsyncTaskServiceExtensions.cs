using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Extensions;

public static class AsyncTaskServiceExtensions
{
    /// <summary>Registers the durable async-task service for any Conduit host.</summary>
    public static IServiceCollection AddAsyncTaskServices(this IServiceCollection services)
    {
        services.AddScoped<IAsyncTaskService>(serviceProvider =>
        {
            var repository = serviceProvider.GetRequiredService<IAsyncTaskRepository>();
            var cache = serviceProvider.GetRequiredService<IDistributedCache>();
            var eventBus = serviceProvider.GetService<IEventBus>();
            var logger = serviceProvider.GetRequiredService<ILogger<HybridAsyncTaskService>>();
            var terminalWriter = serviceProvider.GetService<IMediaTaskTerminalWriter>();

            return eventBus is null
                ? new HybridAsyncTaskService(repository, cache, logger, terminalWriter)
                : new HybridAsyncTaskService(repository, cache, eventBus, logger, terminalWriter);
        });

        return services;
    }
}
