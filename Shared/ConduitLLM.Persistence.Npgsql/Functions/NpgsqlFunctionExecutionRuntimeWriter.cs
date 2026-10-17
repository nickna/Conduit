using ConduitLLM.Functions.Entities;
using ConduitLLM.Persistence.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>Typed PostgreSQL scalar execution writes with version compare-and-swap.</summary>
public sealed class NpgsqlFunctionExecutionRuntimeWriter : IFunctionExecutionRuntimeWriter
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlFunctionExecutionRuntimeWriter(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<Guid> CreateAsync(FunctionExecution execution, CancellationToken cancellationToken = default)
    {
        RequireScalarSnapshot(execution);
        if (execution.Id == Guid.Empty) execution.Id = Guid.NewGuid();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "FunctionExecutions" (
                "Id", "FunctionConfigurationId", "VirtualKeyId", "ExecutionMode", "State",
                "RequestedAt", "StartedAt", "CompletedAt", "Duration", "RequestJson", "ResponseJson",
                "ErrorMessage", "EstimatedCost", "ActualCost", "CostCalculationDetails", "RetryCount",
                "NextRetryAt", "LeasedBy", "LeaseExpiryTime", "Version", "WebhookUrl", "WebhookDelivered",
                "ProgressPercentage", "StatusMessage")
            VALUES (@id, @configuration, @virtualKey, @mode, @state, @requested, @started, @completed,
                @duration, @request, @response, @error, @estimatedCost, @actualCost, @costDetails,
                @retries, @nextRetry, @leasedBy, @leaseExpiry, @version, @webhook, @delivered, @progress, @status)
            """;
        BindSnapshot(command, execution);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return execution.Id;
    }

    public async Task<bool> UpdateAsync(FunctionExecution execution, CancellationToken cancellationToken = default)
    {
        RequireScalarSnapshot(execution);
        var originalVersion = execution.Version;
        execution.Version = unchecked(originalVersion + 1);
        var saved = false;
        try
        {
            // EF's Attach generates the value-generated key for an empty detached ID.
            if (execution.Id == Guid.Empty) execution.Id = Guid.CreateVersion7();
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE "FunctionExecutions" SET
                    "FunctionConfigurationId" = @configuration, "VirtualKeyId" = @virtualKey,
                    "ExecutionMode" = @mode, "State" = @state, "RequestedAt" = @requested,
                    "StartedAt" = @started, "CompletedAt" = @completed, "Duration" = @duration,
                    "RequestJson" = @request, "ResponseJson" = @response, "ErrorMessage" = @error,
                    "EstimatedCost" = @estimatedCost, "ActualCost" = @actualCost, "CostCalculationDetails" = @costDetails,
                    "RetryCount" = @retries, "NextRetryAt" = @nextRetry, "LeasedBy" = @leasedBy,
                    "LeaseExpiryTime" = @leaseExpiry, "Version" = @version, "WebhookUrl" = @webhook,
                    "WebhookDelivered" = @delivered, "ProgressPercentage" = @progress, "StatusMessage" = @status
                WHERE "Id" = @id AND "Version" = @originalVersion
                """;
            BindSnapshot(command, execution);
            command.Parameters.AddWithValue("originalVersion", NpgsqlDbType.Integer, originalVersion);
            saved = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
            return saved;
        }
        finally
        {
            if (!saved) execution.Version = originalVersion;
        }
    }

    private static void BindSnapshot(NpgsqlCommand command, FunctionExecution execution)
    {
        Add(command, "id", NpgsqlDbType.Uuid, execution.Id);
        Add(command, "configuration", NpgsqlDbType.Integer, execution.FunctionConfigurationId);
        Add(command, "virtualKey", NpgsqlDbType.Integer, execution.VirtualKeyId);
        Add(command, "mode", NpgsqlDbType.Integer, (int)execution.ExecutionMode);
        Add(command, "state", NpgsqlDbType.Integer, (int)execution.State);
        Add(command, "requested", NpgsqlDbType.TimestampTz, execution.RequestedAt);
        Add(command, "started", NpgsqlDbType.TimestampTz, execution.StartedAt);
        Add(command, "completed", NpgsqlDbType.TimestampTz, execution.CompletedAt);
        Add(command, "duration", NpgsqlDbType.Interval, execution.Duration);
        Add(command, "request", NpgsqlDbType.Jsonb, execution.RequestJson);
        Add(command, "response", NpgsqlDbType.Jsonb, execution.ResponseJson);
        Add(command, "error", NpgsqlDbType.Text, execution.ErrorMessage);
        Add(command, "estimatedCost", NpgsqlDbType.Numeric, execution.EstimatedCost);
        Add(command, "actualCost", NpgsqlDbType.Numeric, execution.ActualCost);
        Add(command, "costDetails", NpgsqlDbType.Jsonb, execution.CostCalculationDetails);
        Add(command, "retries", NpgsqlDbType.Integer, execution.RetryCount);
        Add(command, "nextRetry", NpgsqlDbType.TimestampTz, execution.NextRetryAt);
        Add(command, "leasedBy", NpgsqlDbType.Text, execution.LeasedBy);
        Add(command, "leaseExpiry", NpgsqlDbType.TimestampTz, execution.LeaseExpiryTime);
        Add(command, "version", NpgsqlDbType.Integer, execution.Version);
        Add(command, "webhook", NpgsqlDbType.Text, execution.WebhookUrl);
        Add(command, "delivered", NpgsqlDbType.Boolean, execution.WebhookDelivered);
        Add(command, "progress", NpgsqlDbType.Integer, execution.ProgressPercentage);
        Add(command, "status", NpgsqlDbType.Text, execution.StatusMessage);
    }

    private static void Add(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.AddWithValue(name, type, value ?? DBNull.Value);

    private static void RequireScalarSnapshot(FunctionExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.FunctionConfiguration is not null || execution.AuditEvents.Count != 0)
            throw new ArgumentException("Execution navigation graphs are outside this scalar writer.", nameof(execution));
    }
}
