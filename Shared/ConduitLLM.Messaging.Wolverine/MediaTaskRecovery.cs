using System.Text.Json;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Core.Utilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace ConduitLLM.Messaging.Wolverine;

/// <summary>Reuses Wolverine's outbox for lease recovery and historical Pending reconciliation.</summary>
public sealed class MediaTaskRecovery(IWolverineRuntime runtime, IEventBus eventBus, IDistributedCache cache,
    ILogger<MediaTaskRecovery> logger) : IMediaTaskRecovery
{
    private static readonly System.Diagnostics.Metrics.Meter Meter = new("ConduitLLM.Media.Dispatch");
    private static readonly System.Diagnostics.Metrics.Counter<long> Recoveries = Meter.CreateCounter<long>("media_task_dispatch_recoveries");

    public async Task<MediaTaskRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var outbox = new MessageContext(runtime);
        if (!outbox.TryFindMessageDatabase(out var database) || database == null)
            throw new InvalidOperationException("Media recovery requires PostgreSQL-backed Wolverine persistence.");
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        outbox.EnlistInOutbox(new DatabaseEnvelopeTransaction(database, transaction));
        var now = DateTime.UtcNow;
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT "Id", "Type", "State", "Metadata", "VirtualKeyId", "ProviderInvocationStartedAt", "ProviderInvocationCompletedAt"
            FROM "AsyncTasks"
            WHERE NOT "IsArchived" AND "Type" IN ('image_generation', 'video_generation')
              AND (("State" = 0 AND "UpdatedAt" < @cutoff AND ("NextRetryAt" IS NULL OR "NextRetryAt" <= @now))
                OR ("State" = 1 AND "LeaseExpiryTime" < @now))
            ORDER BY "UpdatedAt", "Id" LIMIT 100 FOR UPDATE SKIP LOCKED
            """;
        select.Parameters.Add(new NpgsqlParameter<DateTime>("cutoff", now.AddMinutes(-2)));
        select.Parameters.Add(new NpgsqlParameter<DateTime>("now", now));
        var candidates = new List<Candidate>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                candidates.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4), !reader.IsDBNull(5) || !reader.IsDBNull(6)));

        var reset = 0; var dispatch = 0; var indeterminate = 0; var blocked = 0;
        var states = new List<(string Id, int State)>();
        foreach (var task in candidates)
        {
            string? error = null;
            ReconstructedMediaCommand? command = null;
            var state = task.ProviderMayHaveRun ? 6 : 0;
            if (!task.ProviderMayHaveRun)
            {
                try
                {
                    var metadata = JsonSerializer.Deserialize(task.Metadata ?? "null", AsyncTaskJsonContext.Default.TaskMetadata)
                        ?? throw new InvalidOperationException("Missing metadata.");
                    if (metadata.VirtualKeyId != task.VirtualKeyId || !HasVirtualKey(metadata))
                        throw new InvalidOperationException("Incomplete authorization metadata.");
                    command = MediaGenerationCommandReconstruction.Build(task.Id, task.Type, metadata);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or ArgumentException)
                {
                    // Do not log deserialization exceptions or the persisted request/key.
                    error = "Dispatch recovery blocked: persisted media request or authorization metadata is malformed or unsupported.";
                    state = task.State;
                    blocked++;
                    logger.LogWarning("Automatic media recovery blocked for task {TaskId} ({TaskType}); inspect its persisted metadata", task.Id, task.Type);
                }
            }
            else
            {
                error = "Provider outcome is unknown after the processing lease expired; reconciliation is required.";
                indeterminate++;
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            // Blocked rows keep their state/ownership and move to the back of the bounded scan.
            update.CommandText = command == null && !task.ProviderMayHaveRun
                ? "UPDATE \"AsyncTasks\" SET \"Error\" = @error, \"UpdatedAt\" = @now, \"Version\" = \"Version\" + 1 WHERE \"Id\" = @id"
                : """
                    UPDATE "AsyncTasks" SET "State" = @state, "LeasedBy" = NULL, "LeaseExpiryTime" = NULL,
                        "Error" = @error, "IsRetryable" = @retryable, "CompletedAt" = @completed,
                        "NextRetryAt" = NULL, "UpdatedAt" = @now, "Version" = "Version" + 1 WHERE "Id" = @id
                    """;
            update.Parameters.Add(new NpgsqlParameter<string>("id", task.Id));
            update.Parameters.Add(new NpgsqlParameter<int>("state", state));
            update.Parameters.Add(new NpgsqlParameter("error", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter<bool>("retryable", state != 6));
            update.Parameters.Add(new NpgsqlParameter("completed", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = state == 6 ? now : DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter<DateTime>("now", now));
            await update.ExecuteNonQueryAsync(cancellationToken);
            if (command != null)
            {
                if (command.Image != null) await outbox.PublishAsync(command.Image);
                else await outbox.PublishAsync(command.Video!);
                dispatch++;
                if (task.State == 1) reset++;
            }
            states.Add((task.Id, state));
        }
        // A crash before this commit rolls back BOTH the state change and envelopes.
        // A crash after it leaves dispatch entirely in Wolverine's durable store.
        await transaction.CommitAsync(cancellationToken);
        try { await outbox.FlushOutgoingMessagesAsync(); }
        catch (Exception ex) { logger.LogWarning(ex, "Immediate recovery dispatch failed; persisted envelopes remain recoverable"); }
        foreach (var task in states)
        {
            try { await cache.RemoveAsync($"{RedisKeys.AsyncTask.Prefix}{task.Id}", cancellationToken); }
            catch (Exception ex) { logger.LogWarning(ex, "Recovery cache invalidation failed for task {TaskId}", task.Id); }
            try { await eventBus.PublishAsync(new AsyncTaskUpdated { TaskId = task.Id, State = ((TaskState)task.State).ToString(), IsCompleted = task.State == 6 }); }
            catch (Exception ex) { logger.LogWarning(ex, "Recovery notification failed for task {TaskId}", task.Id); }
        }
        Record("redispatched", dispatch); Record("indeterminate", indeterminate); Record("blocked", blocked);
        return new(reset, dispatch, indeterminate, blocked);
    }

    private static bool HasVirtualKey(TaskMetadata metadata) => metadata.ExtensionData?.TryGetValue("VirtualKey", out var key) == true &&
        (key is string text && !string.IsNullOrWhiteSpace(text) || key is JsonElement json && json.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(json.GetString()));
    private static void Record(string outcome, int count) => Recoveries.Add(count, new KeyValuePair<string, object?>("outcome", outcome));
    private sealed record Candidate(string Id, string Type, int State, string? Metadata, int VirtualKeyId, bool ProviderMayHaveRun);
}
