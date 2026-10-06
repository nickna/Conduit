using System.Text.Json;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Helpers;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Serialization;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace ConduitLLM.Messaging.Wolverine;

/// <summary>One database transaction for receipts/claims and every future Wolverine envelope.</summary>
public sealed class WebhookDeliveryStore(IWolverineRuntime runtime, ILogger<WebhookDeliveryStore> logger,
    TimeProvider? clock = null) : IWebhookDeliveryStore
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public Task<WebhookClaim> TryClaimAsync(WebhookDeliveryRequested request, DateTime deadline, TimeSpan lease,
        CancellationToken cancellationToken = default) => TransactAsync(async (connection, transaction, outbox) =>
    {
        var id = WebhookIdentity.DeliveryKey(request);
        var now = _clock.GetUtcNow().UtcDateTime;
        var json = JsonSerializer.Serialize(request, CoreMessagingJsonContext.Default.WebhookDeliveryRequested);
        await using (var insert = Command(connection, transaction, """
            INSERT INTO "WebhookDeliveries" ("Id", "EventId", "TaskId", "VirtualKeyId", "State", "RequestJson",
                "CreatedAt", "UpdatedAt", "Deadline", "NextAttemptAt", "RetainUntil", "Attempts", "Cycle")
            VALUES (@id, @event, @task, @owner, 'Pending', @json, @now, @now, @deadline, @now, @retain, @attempts, @cycle)
            ON CONFLICT ("Id") DO NOTHING
            """))
        {
            Add(insert, "id", id); Add(insert, "event", request.EventId); Add(insert, "task", request.TaskId);
            Add(insert, "owner", request.VirtualKeyId); Add(insert, "json", json); Add(insert, "now", now);
            Add(insert, "deadline", deadline); Add(insert, "retain", deadline.AddDays(30));
            Add(insert, "attempts", Math.Max(0, request.RetryCount)); Add(insert, "cycle", request.DeliveryCycle);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        string state, storedJson;
        int attempts, cycle;
        DateTime storedDeadline, due;
        DateTime? expires;
        await using (var select = Command(connection, transaction, """
            SELECT "State", "RequestJson", "Attempts", "Cycle", "Deadline", "NextAttemptAt", "ClaimExpiresAt"
            FROM "WebhookDeliveries" WHERE "Id" = @id FOR UPDATE
            """))
        {
            Add(select, "id", id);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            state = reader.GetString(0); storedJson = reader.GetString(1); attempts = reader.GetInt32(2);
            cycle = reader.GetInt32(3); storedDeadline = reader.GetDateTime(4); due = reader.GetDateTime(5);
            expires = reader.IsDBNull(6) ? null : reader.GetDateTime(6);
        }
        var original = JsonSerializer.Deserialize(storedJson, CoreMessagingJsonContext.Default.WebhookDeliveryRequested)!;
        var status = state switch
        {
            "Delivered" => WebhookClaimStatus.Delivered,
            "Exhausted" => WebhookClaimStatus.Exhausted,
            _ when cycle != request.DeliveryCycle => WebhookClaimStatus.Obsolete,
            _ when expires > now || (due > now && storedDeadline > now) => WebhookClaimStatus.Busy,
            _ => WebhookClaimStatus.Acquired
        };
        if (status != WebhookClaimStatus.Acquired)
            return new WebhookClaim(status, id, Guid.Empty, original, attempts, storedDeadline, expires ?? due);

        var token = Guid.NewGuid();
        var leaseExpiry = now.Add(lease);
        await using (var claim = Command(connection, transaction, """
            UPDATE "WebhookDeliveries" SET "ClaimToken" = @token, "ClaimExpiresAt" = @expiry, "UpdatedAt" = @now
            WHERE "Id" = @id
            """))
        {
            Add(claim, "id", id); Add(claim, "token", token); Add(claim, "expiry", leaseExpiry); Add(claim, "now", now);
            await claim.ExecuteNonQueryAsync(cancellationToken);
        }
        // A lease recovery envelope is committed BEFORE any HTTP side effect. It is
        // harmless after success, and survives a killed worker or a failed follow-up write.
        await outbox.PublishAsync(original with { RetryCount = attempts, NextRetryAt = leaseExpiry },
            new DeliveryOptions { ScheduledTime = new DateTimeOffset(leaseExpiry) });
        return new WebhookClaim(status, id, token, original, attempts, storedDeadline, leaseExpiry);
    }, cancellationToken);

    public Task<int?> BeginAttemptAsync(WebhookClaim claim, CancellationToken cancellationToken = default) =>
        TransactAsync<int?>(async (connection, transaction, _) =>
        {
            await using var command = Command(connection, transaction, """
                UPDATE "WebhookDeliveries" SET "Attempts" = "Attempts" + 1, "UpdatedAt" = @now
                WHERE "Id" = @id AND "ClaimToken" = @token AND "ClaimExpiresAt" > @now AND "State" = 'Pending'
                RETURNING "Attempts"
                """);
            Fence(command, claim);
            var count = await command.ExecuteScalarAsync(cancellationToken);
            return count is int value ? value : null;
        }, cancellationToken);

    public Task<bool> CompleteAsync(WebhookClaim claim, WebhookSendResult result, bool exhausted,
        CancellationToken cancellationToken = default) => TransactAsync(async (connection, transaction, _) =>
    {
        await using var command = Command(connection, transaction, """
            UPDATE "WebhookDeliveries" SET "State" = @state, "LastStatusCode" = @status, "LastError" = @error,
                "UpdatedAt" = @now, "ClaimToken" = NULL, "ClaimExpiresAt" = NULL
            WHERE "Id" = @id AND "ClaimToken" = @token AND "ClaimExpiresAt" > @now AND "State" = 'Pending'
            """);
        Fence(command, claim); Add(command, "state", exhausted ? "Exhausted" : "Delivered");
        Result(command, result);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }, cancellationToken);

    public Task<bool> ScheduleAsync(WebhookClaim claim, DateTime dueAt, WebhookSendResult? result = null,
        CancellationToken cancellationToken = default) => TransactAsync(async (connection, transaction, outbox) =>
    {
        await using var command = Command(connection, transaction, """
            UPDATE "WebhookDeliveries" SET "NextAttemptAt" = @due, "LastStatusCode" = @status, "LastError" = @error,
                "UpdatedAt" = @now, "ClaimToken" = NULL, "ClaimExpiresAt" = NULL
            WHERE "Id" = @id AND "ClaimToken" = @token AND "ClaimExpiresAt" > @now AND "State" = 'Pending'
            RETURNING "Attempts"
            """);
        Fence(command, claim); Add(command, "due", dueAt); Result(command, result);
        var attempts = await command.ExecuteScalarAsync(cancellationToken);
        if (attempts is not int count) return false;
        await outbox.PublishAsync(claim.Request with { RetryCount = count, NextRetryAt = dueAt },
            new DeliveryOptions { ScheduledTime = new DateTimeOffset(dueAt) });
        return true;
    }, cancellationToken);

    private void Fence(NpgsqlCommand command, WebhookClaim claim)
    {
        Add(command, "id", claim.Id); Add(command, "token", claim.Token);
        Add(command, "now", _clock.GetUtcNow().UtcDateTime);
    }

    private static void Result(NpgsqlCommand command, WebhookSendResult? result)
    {
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Integer) { Value = (object?)result?.StatusCode ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Varchar) { Value = (object?)result?.Error ?? DBNull.Value });
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql) =>
        new(sql, connection, transaction);
    private static void Add<T>(NpgsqlCommand command, string name, T value) where T : notnull =>
        command.Parameters.Add(new NpgsqlParameter<T>(name, value));

    private async Task<T> TransactAsync<T>(Func<NpgsqlConnection, NpgsqlTransaction, MessageContext, Task<T>> work,
        CancellationToken cancellationToken)
    {
        var outbox = new MessageContext(runtime);
        if (!outbox.TryFindMessageDatabase(out var database) || database == null)
            throw new InvalidOperationException("Webhook delivery requires PostgreSQL-backed Wolverine persistence.");
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        outbox.EnlistInOutbox(new DatabaseEnvelopeTransaction(database, transaction));
        var result = await work((NpgsqlConnection)connection, (NpgsqlTransaction)transaction, outbox);
        await transaction.CommitAsync(cancellationToken);
        try { await outbox.FlushOutgoingMessagesAsync(); }
        catch (Exception) { logger.LogWarning("Webhook dispatch deferred; the committed Wolverine envelope remains recoverable"); }
        return result;
    }
}
