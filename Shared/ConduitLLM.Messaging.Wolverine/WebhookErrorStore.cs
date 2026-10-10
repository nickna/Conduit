using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;

namespace ConduitLLM.Messaging.Wolverine;

/// <summary>
/// Admin uses a different durability schema from Gateway. Build Wolverine's public
/// persistence adapter for query/discard only, without registering a store/agent in
/// the running host or joining Gateway's node cluster.
/// </summary>
public sealed class WebhookErrorStore : IAsyncDisposable
{
    private readonly NpgsqlDataSource _source;
    private readonly ServiceProvider _services;
    public IDeadLetters Letters { get; }
    public WebhookErrorStore(IWolverineRuntime runtime, string connectionString, string gatewaySchema)
    {
        _source = NpgsqlDataSource.Create(connectionString);
        var options = new WolverineOptions();
        options.PersistMessagesWithPostgresql(_source, gatewaySchema, MessageStoreRole.Ancillary)
            .OverrideAutoCreateResources(AutoCreate.None).EnableCommandQueues(false);
        options.Services.AddSingleton(runtime);
        _services = options.Services.BuildServiceProvider();
        Letters = _services.GetRequiredService<IMessageStore>().DeadLetters;
    }
    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _source.DisposeAsync();
    }
}
