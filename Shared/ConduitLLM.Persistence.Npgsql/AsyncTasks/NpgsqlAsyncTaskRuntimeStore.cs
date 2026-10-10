using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Npgsql;
using NpgsqlTypes;

namespace ConduitLLM.Persistence.Npgsql;

/// <summary>
/// NativeAOT-compatible typed Npgsql store for Gateway async-task workflows.
/// </summary>
public sealed class NpgsqlAsyncTaskRuntimeStore : IAsyncTaskRuntimeStore
{
    private const string Columns = """
        "Id", "Type", "State", "Payload", "Progress", "ProgressMessage",
        "Result", "Error", "CreatedAt", "UpdatedAt", "CompletedAt",
        "VirtualKeyId", "Metadata", "IsArchived", "ArchivedAt", "LeasedBy",
        "LeaseExpiryTime", "ProviderInvocationStartedAt",
        "ProviderInvocationCompletedAt", "ProviderOperationId", "RetryDispatchId",
        "Version", "RetryCount", "MaxRetries", "IsRetryable", "NextRetryAt"
        """;

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlAsyncTaskRuntimeStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<string> CreateAsync(
        AsyncTaskRuntimeRecord task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ValidateTask(task);
        var now = DateTime.UtcNow;
        if (task.CreatedAt == default) task.CreatedAt = now;
        task.UpdatedAt = now;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "AsyncTasks" (
                "Id", "Type", "State", "Payload", "Progress", "ProgressMessage",
                "Result", "Error", "CreatedAt", "UpdatedAt", "CompletedAt",
                "VirtualKeyId", "Metadata", "IsArchived", "ArchivedAt", "LeasedBy",
                "LeaseExpiryTime", "ProviderInvocationStartedAt",
                "ProviderInvocationCompletedAt", "ProviderOperationId", "RetryDispatchId",
                "Version", "RetryCount", "MaxRetries", "IsRetryable", "NextRetryAt")
            VALUES (
                @id, @type, @state, @payload, @progress, @progressMessage,
                @result, @error, @createdAt, @updatedAt, @completedAt,
                @virtualKeyId, @metadata, @isArchived, @archivedAt, @leasedBy,
                @leaseExpiryTime, @providerInvocationStartedAt,
                @providerInvocationCompletedAt, @providerOperationId, @retryDispatchId,
                @version, @retryCount, @maxRetries, @isRetryable, @nextRetryAt)
            """;
        AddRecordParameters(command, task);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return task.Id;
    }

    public async Task<AsyncTaskRuntimeRecord?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM \"AsyncTasks\" WHERE \"Id\" = @id";
        command.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, id);
        return await ReadSingleAsync(command, cancellationToken);
    }

    public async Task<bool> UpdateAsync(
        AsyncTaskRuntimeRecord task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ValidateTask(task);
        task.UpdatedAt = DateTime.UtcNow;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "AsyncTasks" SET
                "Type" = @type, "State" = @state, "Payload" = @payload,
                "Progress" = @progress, "ProgressMessage" = @progressMessage,
                "Result" = @result, "Error" = @error, "CreatedAt" = @createdAt,
                "UpdatedAt" = @updatedAt, "CompletedAt" = @completedAt,
                "VirtualKeyId" = @virtualKeyId, "Metadata" = @metadata,
                "IsArchived" = @isArchived, "ArchivedAt" = @archivedAt,
                "LeasedBy" = @leasedBy, "LeaseExpiryTime" = @leaseExpiryTime,
                "ProviderInvocationStartedAt" = @providerInvocationStartedAt,
                "ProviderInvocationCompletedAt" = @providerInvocationCompletedAt,
                "ProviderOperationId" = @providerOperationId,
                "RetryDispatchId" = @retryDispatchId, "Version" = @version,
                "RetryCount" = @retryCount, "MaxRetries" = @maxRetries,
                "IsRetryable" = @isRetryable, "NextRetryAt" = @nextRetryAt
            WHERE "Id" = @id AND "Version" = @version
            """;
        AddRecordParameters(command, task);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> DeleteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM \"AsyncTasks\" WHERE \"Id\" = @id";
        command.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, id);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<int> ArchiveOldTasksAsync(
        TimeSpan completedOlderThan,
        TimeSpan? staleActiveOlderThan = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "AsyncTasks" SET
                "State" = CASE WHEN "State" IN (0, 1) THEN 5 ELSE "State" END,
                "CompletedAt" = CASE WHEN "State" IN (0, 1) THEN @now ELSE "CompletedAt" END,
                "Error" = CASE WHEN "State" IN (0, 1)
                    THEN COALESCE("Error", 'Task expired during retention cleanup.')
                    ELSE "Error" END,
                "LeasedBy" = CASE WHEN "State" IN (0, 1) THEN NULL ELSE "LeasedBy" END,
                "LeaseExpiryTime" = CASE WHEN "State" IN (0, 1) THEN NULL ELSE "LeaseExpiryTime" END,
                "IsArchived" = TRUE, "ArchivedAt" = @now, "UpdatedAt" = @now
            WHERE NOT "IsArchived" AND (
                ("CompletedAt" IS NOT NULL AND "CompletedAt" < @completedCutoff
                    AND "State" IN (2, 3, 4, 5)) OR
                (@staleCutoff IS NOT NULL AND "State" IN (0, 1)
                    AND "UpdatedAt" < @staleCutoff
                    AND ("LeaseExpiryTime" IS NULL OR "LeaseExpiryTime" < @now)
                    AND ("ProviderInvocationStartedAt" IS NULL
                        OR "ProviderInvocationCompletedAt" IS NOT NULL)))
            """;
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        command.Parameters.AddWithValue(
            "completedCutoff",
            NpgsqlDbType.TimestampTz,
            now.Subtract(completedOlderThan));
        AddNullableTimestamp(command, "staleCutoff", staleActiveOlderThan.HasValue
            ? now.Subtract(staleActiveOlderThan.Value)
            : null);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetTaskIdsForCleanupAsync(
        TimeSpan archivedOlderThan,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id" FROM "AsyncTasks"
            WHERE "IsArchived" AND "ArchivedAt" IS NOT NULL AND "ArchivedAt" < @cutoff
            ORDER BY "ArchivedAt"
            LIMIT @limit
            """;
        command.Parameters.AddWithValue(
            "cutoff",
            NpgsqlDbType.TimestampTz,
            DateTime.UtcNow.Subtract(archivedOlderThan));
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
        return ids;
    }

    public async Task<int> BulkDeleteAsync(
        IReadOnlyCollection<string> taskIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        if (taskIds.Count == 0) return 0;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM \"AsyncTasks\" WHERE \"Id\" = ANY(@ids)";
        command.Parameters.AddWithValue(
            "ids",
            NpgsqlDbType.Array | NpgsqlDbType.Varchar,
            taskIds.ToArray());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AsyncTaskRuntimePage> GetByStateAsync(
        int state,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = """
            SELECT count(*)::integer FROM "AsyncTasks"
            WHERE NOT "IsArchived" AND "State" = @state
            """;
        countCommand.Parameters.AddWithValue("state", NpgsqlDbType.Integer, state);
        var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM "AsyncTasks"
            WHERE NOT "IsArchived" AND "State" = @state
            ORDER BY "UpdatedAt" DESC, "Id"
            OFFSET @offset LIMIT @limit
            """;
        command.Parameters.AddWithValue("state", NpgsqlDbType.Integer, state);
        command.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, (page - 1) * pageSize);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize);
        return new AsyncTaskRuntimePage(
            await ReadManyAsync(command, cancellationToken),
            total);
    }

    public async Task<AsyncTaskRuntimeClaimStatus> TryClaimTaskAsync(
        string taskId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        var now = DateTime.UtcNow;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var claim = connection.CreateCommand())
        {
            claim.CommandText = """
                UPDATE "AsyncTasks" SET "State" = 1, "LeasedBy" = @workerId,
                    "LeaseExpiryTime" = @leaseExpiry, "UpdatedAt" = @now,
                    "Version" = "Version" + 1
                WHERE "Id" = @taskId AND NOT "IsArchived" AND
                    ("State" = 0 OR ("State" = 1
                        AND "ProviderInvocationStartedAt" IS NULL
                        AND "LeaseExpiryTime" < @now))
                """;
            AddClaimParameters(claim, taskId, workerId, now);
            claim.Parameters.AddWithValue(
                "leaseExpiry",
                NpgsqlDbType.TimestampTz,
                now.Add(leaseDuration));
            if (await claim.ExecuteNonQueryAsync(cancellationToken) == 1)
                return AsyncTaskRuntimeClaimStatus.Claimed;
        }

        await using (var uncertain = connection.CreateCommand())
        {
            uncertain.CommandText = """
                UPDATE "AsyncTasks" SET "State" = 6, "IsRetryable" = FALSE,
                    "Error" = 'Provider outcome is unknown after the processing lease expired.',
                    "LeasedBy" = NULL, "LeaseExpiryTime" = NULL, "CompletedAt" = @now,
                    "UpdatedAt" = @now, "Version" = "Version" + 1
                WHERE "Id" = @taskId AND "State" = 1
                    AND "ProviderInvocationStartedAt" IS NOT NULL
                    AND "LeaseExpiryTime" < @now
                """;
            uncertain.Parameters.AddWithValue("taskId", NpgsqlDbType.Varchar, taskId);
            uncertain.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
            if (await uncertain.ExecuteNonQueryAsync(cancellationToken) == 1)
                return AsyncTaskRuntimeClaimStatus.Indeterminate;
        }

        await using var stateCommand = connection.CreateCommand();
        stateCommand.CommandText = "SELECT \"State\" FROM \"AsyncTasks\" WHERE \"Id\" = @taskId";
        stateCommand.Parameters.AddWithValue("taskId", NpgsqlDbType.Varchar, taskId);
        var state = await stateCommand.ExecuteScalarAsync(cancellationToken);
        return state switch
        {
            null => AsyncTaskRuntimeClaimStatus.Missing,
            6 => AsyncTaskRuntimeClaimStatus.Indeterminate,
            2 or 3 or 4 or 5 => AsyncTaskRuntimeClaimStatus.Terminal,
            _ => AsyncTaskRuntimeClaimStatus.AlreadyClaimed
        };
    }

    public Task<bool> MarkProviderInvocationStartedAsync(
        string taskId,
        string workerId,
        CancellationToken cancellationToken = default) =>
        UpdateProviderPhaseAsync(taskId, workerId, completed: false, null, cancellationToken);

    public Task<bool> MarkProviderInvocationCompletedAsync(
        string taskId,
        string workerId,
        string? providerOperationId = null,
        CancellationToken cancellationToken = default) =>
        UpdateProviderPhaseAsync(
            taskId,
            workerId,
            completed: true,
            providerOperationId,
            cancellationToken);

    public async Task<bool> ExtendLeaseAsync(
        string taskId,
        string workerId,
        TimeSpan extension,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        var now = DateTime.UtcNow;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "AsyncTasks" SET "LeaseExpiryTime" = @leaseExpiry,
                "UpdatedAt" = @now, "Version" = "Version" + 1
            WHERE "Id" = @taskId AND "LeasedBy" = @workerId
                AND "LeaseExpiryTime" IS NOT NULL AND "LeaseExpiryTime" > @now
            """;
        AddClaimParameters(command, taskId, workerId, now);
        command.Parameters.AddWithValue(
            "leaseExpiry",
            NpgsqlDbType.TimestampTz,
            now.Add(extension));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> FailIndeterminateTaskWithoutChargeAsync(
        string taskId,
        string reason,
        string? providerOperationId = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE "AsyncTasks" SET "State" = 3, "IsRetryable" = FALSE,
                "Error" = @reason,
                "ProviderOperationId" = CASE WHEN @setProviderOperationId
                    THEN @providerOperationId ELSE "ProviderOperationId" END,
                "LeasedBy" = NULL, "LeaseExpiryTime" = NULL,
                "UpdatedAt" = @now, "CompletedAt" = @now,
                "Version" = "Version" + 1
            WHERE "Id" = @taskId AND "State" = 6
            """;
        command.Parameters.AddWithValue("taskId", NpgsqlDbType.Varchar, taskId);
        command.Parameters.AddWithValue("reason", NpgsqlDbType.Text, reason);
        command.Parameters.AddWithValue(
            "setProviderOperationId",
            NpgsqlDbType.Boolean,
            providerOperationId is not null);
        AddNullableText(command, "providerOperationId", providerOperationId);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<AsyncTaskRuntimeRetryPreparation> PrepareIndeterminateTaskRetryAsync(
        string taskId,
        string dispatchId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (dispatchId.Length > 64) throw new ArgumentOutOfRangeException(nameof(dispatchId));

        var now = DateTime.UtcNow;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE "AsyncTasks" SET "State" = 0, "IsRetryable" = TRUE,
                "Error" = @reason, "RetryDispatchId" = @dispatchId,
                "LeasedBy" = NULL, "LeaseExpiryTime" = NULL,
                "ProviderInvocationStartedAt" = NULL,
                "ProviderInvocationCompletedAt" = NULL, "ProviderOperationId" = NULL,
                "CompletedAt" = NULL, "NextRetryAt" = NULL, "UpdatedAt" = @now,
                "RetryCount" = "RetryCount" + 1, "Version" = "Version" + 1
            WHERE "Id" = @taskId AND "State" = 6
                AND "Type" IN ('image_generation', 'video_generation')
                AND "RetryCount" < "MaxRetries"
            RETURNING {Columns}
            """;
        command.Parameters.AddWithValue("taskId", NpgsqlDbType.Varchar, taskId);
        command.Parameters.AddWithValue("dispatchId", NpgsqlDbType.Varchar, dispatchId);
        command.Parameters.AddWithValue("reason", NpgsqlDbType.Text, reason);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        var prepared = await ReadSingleAsync(command, cancellationToken);
        if (prepared is not null)
            return new AsyncTaskRuntimeRetryPreparation(AsyncTaskRuntimeRetryStatus.Prepared, prepared);

        var current = await GetByIdAsync(taskId, cancellationToken);
        if (current is null)
            return new AsyncTaskRuntimeRetryPreparation(AsyncTaskRuntimeRetryStatus.Missing);
        if (current.State == 0 && current.RetryDispatchId == dispatchId)
            return new AsyncTaskRuntimeRetryPreparation(
                AsyncTaskRuntimeRetryStatus.AlreadyPrepared,
                current);
        if (current.Type is not ("image_generation" or "video_generation"))
            return new AsyncTaskRuntimeRetryPreparation(
                AsyncTaskRuntimeRetryStatus.UnsupportedTaskType,
                current);
        if (current.State == 6 && current.RetryCount >= current.MaxRetries)
            return new AsyncTaskRuntimeRetryPreparation(
                AsyncTaskRuntimeRetryStatus.RetryLimitExceeded,
                current);
        return new AsyncTaskRuntimeRetryPreparation(
            AsyncTaskRuntimeRetryStatus.NotIndeterminate,
            current);
    }

    private async Task<bool> UpdateProviderPhaseAsync(
        string taskId,
        string workerId,
        bool completed,
        string? providerOperationId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = completed
            ? """
                UPDATE "AsyncTasks" SET "ProviderInvocationCompletedAt" = @now,
                    "ProviderOperationId" = @providerOperationId, "UpdatedAt" = @now
                WHERE "Id" = @taskId AND "State" = 1 AND "LeasedBy" = @workerId
                """
            : """
                UPDATE "AsyncTasks" SET "ProviderInvocationStartedAt" = @now,
                    "UpdatedAt" = @now
                WHERE "Id" = @taskId AND "State" = 1 AND "LeasedBy" = @workerId
                """;
        AddClaimParameters(command, taskId, workerId, now);
        if (completed) AddNullableText(command, "providerOperationId", providerOperationId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static void ValidateTask(AsyncTaskRuntimeRecord task)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(task.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.Type);
    }

    private static void AddRecordParameters(NpgsqlCommand command, AsyncTaskRuntimeRecord task)
    {
        command.Parameters.AddWithValue("id", NpgsqlDbType.Varchar, task.Id);
        command.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, task.Type);
        command.Parameters.AddWithValue("state", NpgsqlDbType.Integer, task.State);
        AddNullableText(command, "payload", task.Payload);
        command.Parameters.AddWithValue("progress", NpgsqlDbType.Integer, task.Progress);
        AddNullableText(command, "progressMessage", task.ProgressMessage);
        AddNullableText(command, "result", task.Result);
        AddNullableText(command, "error", task.Error);
        command.Parameters.AddWithValue("createdAt", NpgsqlDbType.TimestampTz, task.CreatedAt);
        command.Parameters.AddWithValue("updatedAt", NpgsqlDbType.TimestampTz, task.UpdatedAt);
        AddNullableTimestamp(command, "completedAt", task.CompletedAt);
        command.Parameters.AddWithValue("virtualKeyId", NpgsqlDbType.Integer, task.VirtualKeyId);
        AddNullableText(command, "metadata", task.Metadata);
        command.Parameters.AddWithValue("isArchived", NpgsqlDbType.Boolean, task.IsArchived);
        AddNullableTimestamp(command, "archivedAt", task.ArchivedAt);
        AddNullableText(command, "leasedBy", task.LeasedBy);
        AddNullableTimestamp(command, "leaseExpiryTime", task.LeaseExpiryTime);
        AddNullableTimestamp(command, "providerInvocationStartedAt", task.ProviderInvocationStartedAt);
        AddNullableTimestamp(command, "providerInvocationCompletedAt", task.ProviderInvocationCompletedAt);
        AddNullableText(command, "providerOperationId", task.ProviderOperationId);
        AddNullableText(command, "retryDispatchId", task.RetryDispatchId);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Integer, task.Version);
        command.Parameters.AddWithValue("retryCount", NpgsqlDbType.Integer, task.RetryCount);
        command.Parameters.AddWithValue("maxRetries", NpgsqlDbType.Integer, task.MaxRetries);
        command.Parameters.AddWithValue("isRetryable", NpgsqlDbType.Boolean, task.IsRetryable);
        AddNullableTimestamp(command, "nextRetryAt", task.NextRetryAt);
    }

    private static void AddClaimParameters(
        NpgsqlCommand command,
        string taskId,
        string workerId,
        DateTime now)
    {
        command.Parameters.AddWithValue("taskId", NpgsqlDbType.Varchar, taskId);
        command.Parameters.AddWithValue("workerId", NpgsqlDbType.Varchar, workerId);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
    }

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = value is null ? DBNull.Value : value
        });

    private static void AddNullableTimestamp(
        NpgsqlCommand command,
        string name,
        DateTime? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });

    private static async Task<AsyncTaskRuntimeRecord?> ReadSingleAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    private static async Task<IReadOnlyList<AsyncTaskRuntimeRecord>> ReadManyAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var tasks = new List<AsyncTaskRuntimeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) tasks.Add(ReadRecord(reader));
        return tasks;
    }

    private static AsyncTaskRuntimeRecord ReadRecord(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Type = reader.GetString(1),
        State = reader.GetInt32(2),
        Payload = GetNullableString(reader, 3),
        Progress = reader.GetInt32(4),
        ProgressMessage = GetNullableString(reader, 5),
        Result = GetNullableString(reader, 6),
        Error = GetNullableString(reader, 7),
        CreatedAt = reader.GetDateTime(8),
        UpdatedAt = reader.GetDateTime(9),
        CompletedAt = GetNullableDateTime(reader, 10),
        VirtualKeyId = reader.GetInt32(11),
        Metadata = GetNullableString(reader, 12),
        IsArchived = reader.GetBoolean(13),
        ArchivedAt = GetNullableDateTime(reader, 14),
        LeasedBy = GetNullableString(reader, 15),
        LeaseExpiryTime = GetNullableDateTime(reader, 16),
        ProviderInvocationStartedAt = GetNullableDateTime(reader, 17),
        ProviderInvocationCompletedAt = GetNullableDateTime(reader, 18),
        ProviderOperationId = GetNullableString(reader, 19),
        RetryDispatchId = GetNullableString(reader, 20),
        Version = reader.GetInt32(21),
        RetryCount = reader.GetInt32(22),
        MaxRetries = reader.GetInt32(23),
        IsRetryable = reader.GetBoolean(24),
        NextRetryAt = GetNullableDateTime(reader, 25)
    };

    private static string? GetNullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTime? GetNullableDateTime(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
}
