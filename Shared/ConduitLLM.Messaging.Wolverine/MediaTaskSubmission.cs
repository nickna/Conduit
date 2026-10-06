using System.Text.Json;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace ConduitLLM.Messaging.Wolverine;

/// <summary>
/// Uses the message store's connection and transaction for both the application row
/// and Wolverine's outgoing envelope. Repository-created DbContexts are deliberately
/// excluded from this unit of work. No delivery happens before its commit.
/// </summary>
public sealed class MediaTaskSubmission(IWolverineRuntime runtime, IEventBus eventBus,
    ILogger<MediaTaskSubmission> logger) : IMediaTaskSubmission
{
    private static readonly System.Diagnostics.Metrics.Meter Meter = new("ConduitLLM.Media.Dispatch");
    private static readonly System.Diagnostics.Metrics.Counter<long> Acceptances = Meter.CreateCounter<long>("media_task_acceptances");
    private static void Record(string type, string outcome) => Acceptances.Add(1,
        new KeyValuePair<string, object?>("task_type", type), new KeyValuePair<string, object?>("outcome", outcome));

    public Task<string> SubmitAsync(ImageGenerationRequested request, TaskMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        if (request.VirtualKeyId != metadata.VirtualKeyId)
            throw new ArgumentException("Generation command and task owner must match.");
        var id = $"task_{Guid.NewGuid():N}";
        var command = request with { TaskId = id };
        metadata.Payload = JsonSerializer.Serialize(command, CoreMessagingJsonContext.Default.ImageGenerationRequested);
        return CommitAsync(id, "image_generation", metadata,
            outbox => outbox.PublishAsync(command).AsTask(), cancellationToken);
    }

    public Task<string> SubmitAsync(VideoGenerationRequested request, TaskMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        if (request.VirtualKeyId != metadata.VirtualKeyId.ToString() || request.Request == null || !request.IsAsync)
            throw new ArgumentException("An async generation command with a matching task owner and full request is required.");
        var id = $"task_{Guid.NewGuid():N}";
        var command = request with { RequestId = id, CorrelationId = request.CorrelationId ?? id };
        // Keep the existing video metadata format for operator retry and older readers.
        metadata.Payload = JsonSerializer.Serialize(command.Request, CoreHttpJsonContext.Default.VideoGenerationRequest);
        metadata.CorrelationId = command.CorrelationId;
        return CommitAsync(id, "video_generation", metadata,
            outbox => outbox.PublishAsync(command).AsTask(), cancellationToken);
    }

    private async Task<string> CommitAsync(string id, string type, TaskMetadata metadata,
        Func<MessageContext, Task> publish, CancellationToken cancellationToken)
    {
        var outbox = new MessageContext(runtime);
        if (!outbox.TryFindMessageDatabase(out var database) || database == null)
            throw new InvalidOperationException("Async media acceptance requires PostgreSQL-backed Wolverine persistence.");

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        outbox.EnlistInOutbox(new DatabaseEnvelopeTransaction(database, transaction));
        var now = DateTime.UtcNow;
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO "AsyncTasks" ("Id", "Type", "State", "Payload", "Metadata", "VirtualKeyId",
                "CreatedAt", "UpdatedAt", "Progress", "IsArchived", "Version", "RetryCount", "MaxRetries", "IsRetryable")
            VALUES (@id, @type, 0, @payload, @metadata, @key, @now, @now, 0, false, 0, 0, 3, true)
            """;
        insert.Parameters.Add(new NpgsqlParameter<string>("id", id));
        insert.Parameters.Add(new NpgsqlParameter<string>("type", type));
        insert.Parameters.Add(new NpgsqlParameter<string>("payload", metadata.Payload!));
        insert.Parameters.Add(new NpgsqlParameter<string>("metadata",
            JsonSerializer.Serialize(metadata, AsyncTaskJsonContext.Default.TaskMetadata)));
        insert.Parameters.Add(new NpgsqlParameter<int>("key", metadata.VirtualKeyId));
        insert.Parameters.Add(new NpgsqlParameter<DateTime>("now", now));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await publish(outbox);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // No automatic retry of an unknown commit outcome. The caller receives an
            // error; if PostgreSQL did commit, the intent remains independently runnable.
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Record(type, "commit_unconfirmed");
            logger.LogError(ex, "Media acceptance commit for task {TaskId} did not return confirmation; outcome may be unknown", id);
            throw;
        }
        Record(type, "accepted");

        try { await outbox.FlushOutgoingMessagesAsync(); }
        catch (Exception ex)
        {
            Record(type, "dispatch_deferred");
            logger.LogWarning(ex, "Immediate dispatch for accepted task {TaskId} failed; Wolverine will deliver its persisted envelope", id);
        }
        try
        {
            await eventBus.PublishAsync(new AsyncTaskCreated { TaskId = id, TaskType = type, VirtualKeyId = metadata.VirtualKeyId });
        }
        catch (Exception ex)
        {
            Record(type, "notification_failed");
            logger.LogWarning(ex, "Task-created notification failed for accepted task {TaskId}", id);
        }
        return id;
    }
}
