using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Helpers;
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

public sealed class MediaTaskTerminalWriter(IWolverineRuntime runtime, IDistributedCache cache,
    IEventBus bus, ILogger<MediaTaskTerminalWriter> logger) : IMediaTaskTerminalWriter
{
    public async Task<bool> CommitAsync(MediaTaskTerminalTransition transition, CancellationToken cancellationToken = default)
    {
        if (transition.State is TaskState.Pending or TaskState.Processing)
            throw new ArgumentException("A terminal outcome is required.", nameof(transition));
        var outbox = new MessageContext(runtime);
        if (!outbox.TryFindMessageDatabase(out var database) || database == null)
            throw new InvalidOperationException("Media terminal commits require PostgreSQL-backed Wolverine persistence.");
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        outbox.EnlistInOutbox(new DatabaseEnvelopeTransaction(database, transaction));
        var now = DateTime.UtcNow;
        int oldState, owner, version;
        string type;
        string? metadataJson, worker;
        DateTime? lease, providerStarted, providerCompleted;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT "State", "VirtualKeyId", "Version", "Type", "Metadata", "LeasedBy", "LeaseExpiryTime",
                    "ProviderInvocationStartedAt", "ProviderInvocationCompletedAt"
                FROM "AsyncTasks" WHERE "Id" = @id FOR UPDATE
                """;
            select.Parameters.Add(new NpgsqlParameter<string>("id", transition.TaskId));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Media task does not exist.");
            oldState = reader.GetInt32(0); owner = reader.GetInt32(1); version = reader.GetInt32(2); type = reader.GetString(3);
            metadataJson = reader.IsDBNull(4) ? null : reader.GetString(4); worker = reader.IsDBNull(5) ? null : reader.GetString(5);
            lease = reader.IsDBNull(6) ? null : reader.GetDateTime(6);
            providerStarted = reader.IsDBNull(7) ? null : reader.GetDateTime(7);
            providerCompleted = reader.IsDBNull(8) ? null : reader.GetDateTime(8);
        }
        if (oldState >= (int)TaskState.Completed) return false;
        if (transition.ExpectedWorkerId != null &&
            (oldState != (int)TaskState.Processing || worker != transition.ExpectedWorkerId || lease <= now || lease == null))
            return false;

        var state = transition.State;
        var error = transition.Error;
        var webhook = transition.Webhook;
        if (providerStarted.HasValue && !providerCompleted.HasValue &&
            state is TaskState.Cancelled or TaskState.Failed or TaskState.TimedOut)
        {
            state = TaskState.Indeterminate;
            error = "Provider outcome is unknown; automatic retry is disabled.";
            webhook = null;
        }
        if (state == TaskState.Completed && providerStarted.HasValue && !providerCompleted.HasValue)
            throw new InvalidOperationException("Provider completion must be recorded before completing a media task.");

        // API/event cancellation can win the race with the running orchestrator. It
        // must commit the callback here, rather than clear the lease and lose it.
        if (webhook == null && oldState == (int)TaskState.Processing && state == TaskState.Cancelled &&
            type is "image_generation" or "video_generation")
            webhook = CancellationWebhook(transition.TaskId, type, metadataJson, owner, error);
        if (webhook != null)
        {
            if (webhook.TaskId != transition.TaskId || !WebhookPayloadHelper.IsWithinSizeLimit(webhook.PayloadJson))
                throw new InvalidOperationException("Invalid terminal callback intent.");
            webhook = webhook with
            {
                EventId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{transition.TaskId}:terminal:{version + 1}"))),
                VirtualKeyId = owner, Timestamp = now, DeliveryStartedAt = now,
                Headers = webhook.Headers == null ? null : new(webhook.Headers)
            };
        }
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE "AsyncTasks" SET "State" = @state, "Progress" = COALESCE(@progress, "Progress"),
                    "Result" = COALESCE(@result, "Result"), "Error" = @error, "CompletedAt" = @now, "UpdatedAt" = @now,
                    "Version" = "Version" + 1, "LeasedBy" = NULL, "LeaseExpiryTime" = NULL,
                    "IsRetryable" = CASE WHEN @state = 6 THEN false ELSE "IsRetryable" END
                WHERE "Id" = @id AND "Version" = @version
                """;
            update.Parameters.Add(new NpgsqlParameter<string>("id", transition.TaskId));
            update.Parameters.Add(new NpgsqlParameter<int>("state", (int)state));
            update.Parameters.Add(new NpgsqlParameter<int>("version", version));
            update.Parameters.Add(new NpgsqlParameter<DateTime>("now", now));
            update.Parameters.Add(new NpgsqlParameter("progress", NpgsqlDbType.Integer) { Value = (object?)transition.Progress ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter("result", NpgsqlDbType.Text) { Value = (object?)transition.ResultJson ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        }
        if (webhook != null) await outbox.PublishAsync(webhook);
        await transaction.CommitAsync(cancellationToken);
        try { await outbox.FlushOutgoingMessagesAsync(); }
        catch (Exception) { logger.LogWarning("Terminal callback dispatch deferred; the committed envelope remains recoverable"); }
        try { await cache.RemoveAsync($"{RedisKeys.AsyncTask.Prefix}{transition.TaskId}", CancellationToken.None); }
        catch (Exception) { logger.LogWarning("Terminal task cache invalidation failed for {TaskId}", transition.TaskId); }
        try
        {
            await bus.PublishAsync(new AsyncTaskUpdated { TaskId = transition.TaskId, State = state.ToString(),
                Progress = transition.Progress ?? 0, IsCompleted = true }, CancellationToken.None);
        }
        catch (Exception) { logger.LogWarning("Terminal task notification failed for {TaskId}", transition.TaskId); }
        return true;
    }

    private static WebhookDeliveryRequested? CancellationWebhook(string id, string type, string? json, int owner, string? error)
    {
        var metadata = JsonSerializer.Deserialize(json ?? "null", AsyncTaskJsonContext.Default.TaskMetadata);
        if (metadata == null) return null;
        if (metadata.VirtualKeyId != owner) throw new InvalidOperationException("Task callback ownership metadata is inconsistent.");
        var command = MediaGenerationCommandReconstruction.Build(id, type, metadata);
        var url = command.Image?.WebhookUrl ?? command.Video?.WebhookUrl;
        if (string.IsNullOrWhiteSpace(url)) return null;
        object payload;
        if (command.Image is { } image)
            payload = new ImageCompletionWebhookPayload { TaskId = id, Status = "cancelled", ImageUrls = [],
                ImagesGenerated = 0, ImagesRequested = image.Request.N, Model = image.Request.Model ?? "",
                Prompt = image.Request.Prompt, Size = image.Request.Size, ResponseFormat = image.Request.ResponseFormat ?? "url", Error = error };
        else
        {
            var video = command.Video!.ResolveRequest();
            payload = new VideoCompletionWebhookPayload { TaskId = id, Status = "cancelled", Model = video.Model, Prompt = video.Prompt, Error = error };
        }
        return new() { TaskId = id, TaskType = type == "image_generation" ? "image" : "video", VirtualKeyId = owner,
            EventType = WebhookEventType.TaskCancelled, WebhookUrl = url, PayloadJson = WebhookPayloadHelper.SerializePayload(payload),
            Headers = command.Image?.WebhookHeaders ?? command.Video?.WebhookHeaders, CorrelationId = metadata.CorrelationId ?? id };
    }
}
