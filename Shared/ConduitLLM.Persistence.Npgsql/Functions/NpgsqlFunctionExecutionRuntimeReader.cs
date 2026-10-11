using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Persistence.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>Typed execution/configuration reads preserving the legacy no-tracking graph.</summary>
public sealed class NpgsqlFunctionExecutionRuntimeReader : IFunctionExecutionRuntimeReader
{
    private readonly NpgsqlDataSource _dataSource;
    private const string Select = """
        SELECT e."Id", e."FunctionConfigurationId", e."VirtualKeyId", e."ExecutionMode", e."State",
            e."RequestedAt", e."StartedAt", e."CompletedAt", e."Duration", e."RequestJson", e."ResponseJson",
            e."ErrorMessage", e."EstimatedCost", e."ActualCost", e."CostCalculationDetails", e."RetryCount",
            e."NextRetryAt", e."LeasedBy", e."LeaseExpiryTime", e."Version", e."WebhookUrl", e."WebhookDelivered",
            e."ProgressPercentage", e."StatusMessage",
            c."Id", c."ProviderType", c."ConfigurationName", c."Purpose", c."DefaultExecutionMode",
            c."BaseUrl", c."IsEnabled", c."TimeoutSeconds", c."CacheTtlMinutes", c."MaxRetries",
            c."ProviderSettings", c."ParameterSchema", c."Description", c."CreatedAt", c."UpdatedAt"
        FROM "FunctionExecutions" AS e INNER JOIN "FunctionConfigurations" AS c
            ON e."FunctionConfigurationId" = c."Id"
        """;

    public NpgsqlFunctionExecutionRuntimeReader(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<FunctionExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE e.\"Id\" = @id LIMIT 1";
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<FunctionExecution>> ListByVirtualKeyAsync(
        int virtualKeyId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE e.\"VirtualKeyId\" = @virtualKey ORDER BY e.\"RequestedAt\" DESC";
        command.Parameters.AddWithValue("virtualKey", NpgsqlDbType.Integer, virtualKeyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<FunctionExecution>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(Read(reader));
        return rows;
    }

    private static FunctionExecution Read(NpgsqlDataReader reader)
    {
        var execution = new FunctionExecution
        {
            Id = reader.GetGuid(0), FunctionConfigurationId = reader.GetInt32(1), VirtualKeyId = reader.GetInt32(2),
            ExecutionMode = (ExecutionMode)reader.GetInt32(3), State = (ExecutionState)reader.GetInt32(4),
            RequestedAt = reader.GetDateTime(5), StartedAt = Date(reader, 6), CompletedAt = Date(reader, 7),
            Duration = reader.IsDBNull(8) ? null : reader.GetTimeSpan(8), RequestJson = Text(reader, 9),
            ResponseJson = Text(reader, 10), ErrorMessage = Text(reader, 11),
            EstimatedCost = reader.IsDBNull(12) ? null : reader.GetDecimal(12),
            ActualCost = reader.IsDBNull(13) ? null : reader.GetDecimal(13), CostCalculationDetails = Text(reader, 14),
            RetryCount = reader.GetInt32(15), NextRetryAt = Date(reader, 16), LeasedBy = Text(reader, 17),
            LeaseExpiryTime = Date(reader, 18), Version = reader.GetInt32(19), WebhookUrl = Text(reader, 20),
            WebhookDelivered = reader.GetBoolean(21), ProgressPercentage = reader.IsDBNull(22) ? null : reader.GetInt32(22),
            StatusMessage = Text(reader, 23), FunctionConfiguration = FunctionConfigurationRowReader.Read(reader, 24)
        };
        // EF's no-tracking Include fixes up the inverse for this row, without
        // resolving configuration identities across independently materialized rows.
        execution.FunctionConfiguration.Executions.Add(execution);
        return execution;
    }

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTime? Date(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
}
