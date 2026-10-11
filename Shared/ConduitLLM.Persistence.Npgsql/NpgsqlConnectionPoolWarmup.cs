using ConduitLLM.Persistence.Interfaces;

using Npgsql;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>Warms the DI-owned PostgreSQL pool without constructing an EF context.</summary>
public sealed class NpgsqlConnectionPoolWarmup(NpgsqlDataSource dataSource) : IConnectionPoolWarmup
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task WarmConnectionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(cancellationToken);
    }
}
