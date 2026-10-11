using System.Text.Json;
using System.Data.Common;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Core.Utilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace ConduitLLM.Messaging.Wolverine;

/// <summary>Recovery intent lives in task metadata; its reservation and outgoing command commit together.</summary>
public sealed class MediaTaskReconciliationStore(IWolverineRuntime runtime, IDistributedCache cache,
    ILogger<MediaTaskReconciliationStore> logger) : IMediaTaskReconciliationStore
{
    public static string Worker(string dispatchId) => $"reconcile:{dispatchId}";

    public async Task<MediaReconciliationAcceptance> AcceptAsync(string taskId, MediaTaskReconciliation decision, CancellationToken cancellationToken = default)
    {
        if (decision.ValidationError() != null) return MediaReconciliationAcceptance.Invalid;
        var outbox = new MessageContext(runtime);
        if (!outbox.TryFindMessageDatabase(out var database) || database == null)
            throw new InvalidOperationException("Media reconciliation requires durable PostgreSQL messaging.");
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        using var envelopeTransaction = new DatabaseEnvelopeTransaction(database, transaction);
        outbox.EnlistInOutbox(envelopeTransaction);
        var row = await ReadAsync(connection, transaction, taskId, cancellationToken);
        if (row == null) return MediaReconciliationAcceptance.Missing;
        var metadata = row.Metadata;
        if (metadata?.Reconciliation is { } previous && previous.DispatchId == decision.DispatchId)
            return previous.SameRequest(decision) ? MediaReconciliationAcceptance.AlreadyAccepted : MediaReconciliationAcceptance.Conflict;
        if (row.State != 6 || row.Worker?.StartsWith("reconcile:", StringComparison.Ordinal) == true)
            return MediaReconciliationAcceptance.Conflict;
        if (metadata == null || metadata.VirtualKeyId != row.Owner || row.Owner != decision.VirtualKeyId)
            return MediaReconciliationAcceptance.Invalid;
        ReconstructedMediaCommand command;
        try { command = MediaGenerationCommandReconstruction.Build(taskId, row.Type, metadata); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return MediaReconciliationAcceptance.Invalid; }
        var model = command.Image?.Request.Model ?? command.Video!.ResolveRequest().Model;
        var count = command.Image?.Request.N ?? 1;
        var matchesRequest = model == decision.Model && count == decision.ResultUrls.Count;
        var matchesOperation = row.OperationId == null || row.OperationId == decision.ProviderOperationId;
        var matchesProvider = metadata.MediaProvider is not { } provider ||
            provider.ProviderId == decision.ProviderId && provider.ProviderModelId == decision.ProviderModelId;
        if (!matchesRequest || !matchesOperation || !matchesProvider)
            return MediaReconciliationAcceptance.Invalid;
        if (metadata.Reconciliation is { } history)
        {
            metadata.ReconciliationHistory ??= [];
            metadata.ReconciliationHistory.Add(history);
        }
        metadata.Reconciliation = decision with { Outcome = "accepted", BillingDisposition = "no_customer_charge" };
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE "AsyncTasks" SET "Metadata" = @metadata, "LeasedBy" = @worker, "LeaseExpiryTime" = NULL,
                "Error" = NULL, "UpdatedAt" = @now, "Version" = "Version" + 1 WHERE "Id" = @id
            """;
        Add(update, "id", taskId); Add(update, "worker", Worker(decision.DispatchId));
        Add(update, "metadata", JsonSerializer.Serialize(metadata, AsyncTaskJsonContext.Default.TaskMetadata));
        update.Parameters.Add(new NpgsqlParameter<DateTime>("now", DateTime.UtcNow));
        await update.ExecuteNonQueryAsync(cancellationToken);
        await outbox.PublishAsync(new IndeterminateMediaTaskRecoveryRequested { TaskId = taskId, DispatchId = decision.DispatchId });
        await transaction.CommitAsync(cancellationToken);
        try { await outbox.FlushOutgoingMessagesAsync(); }
        catch (Exception) { logger.LogWarning("Media recovery dispatch deferred for task {TaskId}; durable intent remains", taskId); }
        await InvalidateAsync(taskId);
        return MediaReconciliationAcceptance.Accepted;
    }

    public async Task<MediaReconciliationTask?> GetAsync(string taskId, string dispatchId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await ReadAsync(connection, null, taskId, cancellationToken);
        return row is { State: 6, Metadata.Reconciliation: { Outcome: "accepted" } decision } &&
            row.Worker == Worker(dispatchId) && decision.DispatchId == dispatchId
            ? new(taskId, row.Type, row.Metadata, decision) : null;
    }

    public async Task BlockAsync(string taskId, string dispatchId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await ReadAsync(connection, transaction, taskId, cancellationToken);
        if (row?.State != 6 || row.Worker != Worker(dispatchId) || row.Metadata?.Reconciliation?.DispatchId != dispatchId) return;
        row.Metadata.Reconciliation = row.Metadata.Reconciliation with { Outcome = "blocked" };
        await using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = """
            UPDATE "AsyncTasks" SET "Metadata" = @metadata, "LeasedBy" = NULL, "LeaseExpiryTime" = NULL,
                "Error" = 'Recovery blocked: verified output could not be validated or stored; reconcile with a new dispatch ID or close without charge.',
                "UpdatedAt" = @now, "Version" = "Version" + 1 WHERE "Id" = @id
            """;
        Add(update, "id", taskId); Add(update, "metadata", JsonSerializer.Serialize(row.Metadata, AsyncTaskJsonContext.Default.TaskMetadata));
        update.Parameters.Add(new NpgsqlParameter<DateTime>("now", DateTime.UtcNow));
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await InvalidateAsync(taskId);
    }

    public Task RecordInvocationAsync(string taskId, string workerId, MediaProviderOperation provider, CancellationToken cancellationToken = default) =>
        RecordAsync(taskId, workerId, provider, accepted: false, cancellationToken);
    public Task RecordOperationAsync(string taskId, string workerId, MediaProviderOperation provider, CancellationToken cancellationToken = default) =>
        RecordAsync(taskId, workerId, provider, accepted: true, cancellationToken);

    private async Task RecordAsync(string taskId, string workerId, MediaProviderOperation provider, bool accepted, CancellationToken token)
    {
        if (accepted && (string.IsNullOrWhiteSpace(provider.OperationId) || provider.OperationId.Length > 200))
            throw new InvalidOperationException("Provider returned an invalid operation ID.");
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        var row = await ReadAsync(connection, transaction, taskId, token);
        // A late acceptance may still identify a cancelled/expired original invocation,
        // but it cannot change a replacement attempt or a reserved reconciliation.
        var active = row?.State == 1 && row.Worker == workerId && row.Lease > DateTime.UtcNow;
        var late = accepted && row?.State == 6 && row.Worker == null && row.Metadata?.ProviderInvocationWorkerId == workerId;
        if ((!active && !late) || row?.Metadata == null || row.Metadata.VirtualKeyId != row.Owner)
            throw new InvalidOperationException("Media provider context could not be durably recorded for its owner.");
        if (accepted && row.OperationId != null && row.OperationId != provider.OperationId)
            throw new InvalidOperationException("A different provider operation is already recorded.");
        row.Metadata.MediaProvider = provider;
        row.Metadata.ProviderInvocationWorkerId = workerId;
        await using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = """
            UPDATE "AsyncTasks" SET "Metadata" = @metadata,
                "ProviderOperationId" = COALESCE(@operation, "ProviderOperationId"), "UpdatedAt" = @now
            WHERE "Id" = @id
            """;
        Add(update, "id", taskId); Add(update, "operation", provider.OperationId);
        Add(update, "metadata", JsonSerializer.Serialize(row.Metadata, AsyncTaskJsonContext.Default.TaskMetadata));
        update.Parameters.Add(new NpgsqlParameter<DateTime>("now", DateTime.UtcNow));
        await update.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        await InvalidateAsync(taskId);
    }

    private async Task<DbConnection> OpenAsync(CancellationToken token)
    {
        var context = new MessageContext(runtime);
        if (!context.TryFindMessageDatabase(out var database) || database == null)
            throw new InvalidOperationException("Media reconciliation requires durable PostgreSQL messaging.");
        return await database.DataSource.OpenConnectionAsync(token);
    }
    private static async Task<Row?> ReadAsync(DbConnection connection, DbTransaction? transaction, string id, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT "State", "VirtualKeyId", "Type", "Metadata", "LeasedBy", "LeaseExpiryTime", "ProviderOperationId"
            FROM "AsyncTasks" WHERE "Id" = @id AND NOT "IsArchived"
            """ + (transaction != null ? " FOR UPDATE" : "");
        Add(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : JsonSerializer.Deserialize(reader.GetString(3), AsyncTaskJsonContext.Default.TaskMetadata),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }
    private async Task InvalidateAsync(string id)
    {
        try { await cache.RemoveAsync($"{RedisKeys.AsyncTask.Prefix}{id}", CancellationToken.None); }
        catch (Exception) { logger.LogWarning("Media reconciliation cache invalidation failed for {TaskId}", id); }
    }
    private static void Add(DbCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });
    private sealed record Row(int State, int Owner, string Type, TaskMetadata? Metadata, string? Worker, DateTime? Lease, string? OperationId);
}
