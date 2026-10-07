using System.Text.Json;
using System.Text.Json.Serialization;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Helpers;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Serialization;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine;

namespace ConduitLLM.Messaging.Wolverine;

public sealed partial class WebhookDeliveryStore
{
    private IDeadLetters Errors => errorStore?.Letters ?? runtime.Storage.DeadLetters;
    private static readonly string WebhookMessageType = MessageTypeName();
    private static readonly CoreMessagingJsonContext DeadLetterJson = new(new JsonSerializerOptions
    { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter<WebhookEventType>() } });
    private static string MessageTypeName()
    {
        var envelope = new Envelope();
        envelope.SetMessageType<WebhookDeliveryRequested>();
        return envelope.MessageType!;
    }
    public Task<List<WebhookDeliveryInspection>> InspectAsync(int? owner, string? taskId, string? eventId, int limit, CancellationToken ct) =>
        TransactAsync(async (connection, transaction, _) =>
        {
            await using var command = Command(connection, transaction, """
                SELECT "Id", "EventId", "TaskId", "VirtualKeyId", "State", "Attempts", "Cycle", "CreatedAt", "UpdatedAt",
                    "Deadline", "NextAttemptAt", "RetainUntil", "LastStatusCode", "LastError"
                FROM "WebhookDeliveries" WHERE (@owner IS NULL OR "VirtualKeyId" = @owner)
                    AND (@task IS NULL OR "TaskId" = @task) AND (@event IS NULL OR "EventId" = @event)
                ORDER BY "CreatedAt" DESC LIMIT @limit
                """);
            command.Parameters.Add(new NpgsqlParameter("owner", NpgsqlDbType.Integer) { Value = (object?)owner ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("task", NpgsqlDbType.Text) { Value = (object?)taskId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("event", NpgsqlDbType.Text) { Value = (object?)eventId ?? DBNull.Value });
            Add(command, "limit", Math.Clamp(limit, 1, 100));
            await using var reader = await command.ExecuteReaderAsync(ct);
            List<WebhookDeliveryInspection> rows = [];
            while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetDateTime(7),
                reader.GetDateTime(8), reader.GetDateTime(9), reader.GetDateTime(10), reader.GetDateTime(11),
                reader.IsDBNull(12) ? null : reader.GetInt32(12), reader.IsDBNull(13) ? null : reader.GetString(13)));
            return rows;
        }, ct);

    public async Task<WebhookReplayResult> ReplayAsync(string id, WebhookReplayRequest replay, string actor, CancellationToken ct)
    {
        if (replay.OperationId == Guid.Empty || replay.VirtualKeyId < 0 || replay.ExpectedCycle < 0 ||
            string.IsNullOrWhiteSpace(actor) || actor.Length > 128) return new("Invalid", 0);
        var result = await TransactAsync(async (connection, transaction, outbox) =>
        {
            string state, json;
            int cycle, attempts, owner;
            DateTime retain;
            await using (var select = Command(connection, transaction, """
                SELECT "State", "RequestJson", "Cycle", "Attempts", "VirtualKeyId", "RetainUntil"
                FROM "WebhookDeliveries" WHERE "Id" = @id FOR UPDATE
                """))
            {
                Add(select, "id", id);
                await using var reader = await select.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return new WebhookReplayResult("NotFound", 0);
                state = reader.GetString(0); json = reader.GetString(1); cycle = reader.GetInt32(2);
                attempts = reader.GetInt32(3); owner = reader.GetInt32(4); retain = reader.GetDateTime(5);
            }
            if (owner != replay.VirtualKeyId) return new WebhookReplayResult("NotFound", 0);
            await using (var prior = Command(connection, transaction, "SELECT \"Cycle\" FROM \"WebhookReplayAudits\" WHERE \"OperationId\" = @op AND \"DeliveryId\" = @id"))
            {
                Add(prior, "op", replay.OperationId); Add(prior, "id", id);
                if (await prior.ExecuteScalarAsync(ct) is int previous) return new WebhookReplayResult("AlreadyRequested", previous);
            }
            var now = _clock.GetUtcNow().UtcDateTime;
            if (state != "Exhausted" || cycle != replay.ExpectedCycle || retain <= now)
                return new WebhookReplayResult("Conflict", cycle);
            if (replay.DeadLetterId is { } envelopeId)
            {
                var letter = await Errors.DeadLetterEnvelopeByIdAsync(envelopeId);
                var message = ReadWebhook(letter);
                if (message == null || WebhookIdentity.DeliveryKey(message) != id || message.VirtualKeyId != replay.VirtualKeyId)
                    return new WebhookReplayResult("NotFound", cycle);
            }
            var request = JsonSerializer.Deserialize(json, CoreMessagingJsonContext.Default.WebhookDeliveryRequested)! with
            { DeliveryCycle = cycle + 1, RetryCount = 0, NextRetryAt = now, DeliveryStartedAt = now };
            var deadline = (policy ?? new()).Deadline(request);
            retain = deadline.AddDays(policy?.Options.RetentionDays ?? 30);
            await using (var audit = Command(connection, transaction, """
                INSERT INTO "WebhookReplayAudits" ("OperationId", "DeliveryId", "Actor", "RequestedAt", "RetainUntil",
                    "Cycle", "PreviousAttempts", "DeadLetterId") VALUES (@op, @id, @actor, @now, @retain, @cycle, @attempts, @letter)
                ON CONFLICT ("OperationId") DO NOTHING RETURNING "Cycle"
                """))
            {
                Add(audit, "op", replay.OperationId); Add(audit, "id", id); Add(audit, "actor", actor);
                Add(audit, "now", now); Add(audit, "retain", retain); Add(audit, "cycle", cycle + 1); Add(audit, "attempts", attempts);
                audit.Parameters.Add(new NpgsqlParameter("letter", NpgsqlDbType.Uuid) { Value = (object?)replay.DeadLetterId ?? DBNull.Value });
                if (await audit.ExecuteScalarAsync(ct) is not int) return new WebhookReplayResult("Conflict", cycle);
            }
            await using (var update = Command(connection, transaction, """
                UPDATE "WebhookDeliveries" SET "State" = 'Pending', "Attempts" = 0, "Cycle" = @cycle, "RequestJson" = @json,
                    "UpdatedAt" = @now, "Deadline" = @deadline, "NextAttemptAt" = @now, "RetainUntil" = @retain,
                    "ClaimToken" = NULL, "ClaimExpiresAt" = NULL, "LastError" = NULL, "LastStatusCode" = NULL WHERE "Id" = @id
                """))
            {
                Add(update, "id", id); Add(update, "cycle", cycle + 1); Add(update, "now", now); Add(update, "deadline", deadline);
                Add(update, "retain", retain); Add(update, "json", JsonSerializer.Serialize(request, CoreMessagingJsonContext.Default.WebhookDeliveryRequested));
                await update.ExecuteNonQueryAsync(ct);
            }
            await outbox.PublishAsync(request);
            return new WebhookReplayResult("Accepted", cycle + 1);
        }, ct);
        // Canonical replay intent and audit have already committed together. Failure
        // to discard a historical error cannot undo that intent or start another cycle.
        if (result.Outcome is "Accepted" or "AlreadyRequested" && replay.DeadLetterId is { } discarded)
        {
            try { await Errors.DiscardAsync(new([discarded]), ct); }
            catch (Exception) { logger.LogWarning("Historical webhook error cleanup deferred after durable replay"); }
        }
        return result;
    }

    public async Task<List<WebhookDeadLetterInspection>> DeadLettersAsync(int limit, CancellationToken ct)
    {
        var results = await Errors.QueryAsync(new() { MessageType = WebhookMessageType,
            PageSize = Math.Clamp(limit, 1, 100) }, ct);
        return results.Envelopes.Select(letter => new WebhookDeadLetterInspection(letter.Id,
            ReadWebhook(letter) is { } request ? WebhookIdentity.DeliveryKey(request) : null, letter.SentAt)).ToList();
    }

    public Task<WebhookBacklog> BacklogAsync(CancellationToken ct) => TransactAsync(async (connection, transaction, _) =>
    {
        await using var command = Command(connection, transaction, """
            SELECT COUNT(*) FILTER (WHERE "State" = 'Pending'), COUNT(*) FILTER (WHERE "State" = 'Delivered'),
                COUNT(*) FILTER (WHERE "State" = 'Exhausted'), COALESCE(SUM("Attempts"), 0)::bigint,
                COALESCE(EXTRACT(EPOCH FROM (@now - MIN("CreatedAt") FILTER (WHERE "State" = 'Pending'))), 0)::double precision,
                (SELECT COUNT(*) FROM "WebhookReplayAudits")
            FROM "WebhookDeliveries"
            """);
        Add(command, "now", _clock.GetUtcNow().UtcDateTime);
        await using var reader = await command.ExecuteReaderAsync(ct); await reader.ReadAsync(ct);
        return new WebhookBacklog(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), Math.Max(0, reader.GetDouble(4)), reader.GetInt64(5));
    }, ct);

    public async Task<int> PurgeAsync(int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var count = await TransactAsync(async (connection, transaction, _) =>
        {
            await using var command = Command(connection, transaction, """
                DELETE FROM "WebhookReplayAudits" WHERE "OperationId" IN
                    (SELECT "OperationId" FROM "WebhookReplayAudits" WHERE "RetainUntil" < @now ORDER BY "RetainUntil" LIMIT @limit);
                DELETE FROM "WebhookDeliveries" WHERE "Id" IN
                    (SELECT "Id" FROM "WebhookDeliveries" WHERE "RetainUntil" < @now AND "State" <> 'Pending'
                        ORDER BY "RetainUntil" LIMIT @limit)
                """);
            Add(command, "now", _clock.GetUtcNow().UtcDateTime); Add(command, "limit", limit);
            return await command.ExecuteNonQueryAsync(ct);
        }, ct);
        var cutoff = _clock.GetUtcNow().AddDays(-(policy?.Options.RetentionDays ?? 30));
        // Supported Wolverine API only. Bound a page of webhook errors, never a
        // transport-table delete or an unscoped bulk replay/discard.
        var letters = await Errors.QueryAsync(new() { MessageType = WebhookMessageType, PageSize = Math.Min(limit, 100),
            Range = new JasperFx.Core.TimeRange(null, cutoff) }, ct);
        var old = letters.Envelopes.Select(l => l.Id).ToArray();
        if (old.Length > 0) await Errors.DiscardAsync(new(old), ct);
        return count + old.Length;
    }

    private WebhookDeliveryRequested? ReadWebhook(DeadLetterEnvelope? letter)
    {
        if (letter == null || letter.MessageType != WebhookMessageType) return null;
        if (letter.Message is WebhookDeliveryRequested known) return known;
        try { return JsonSerializer.Deserialize(letter.Envelope.Data!, DeadLetterJson.WebhookDeliveryRequested); }
        catch (JsonException) { return null; }
    }
}
