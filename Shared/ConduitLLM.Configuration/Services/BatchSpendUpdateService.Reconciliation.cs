using System.Globalization;
using System.Text.Json;
using ConduitLLM.Configuration.Interfaces;
using StackExchange.Redis;

namespace ConduitLLM.Configuration.Services;

public partial class BatchSpendUpdateService
{
    // These records and started holds never expire. Redis persistence requirements are
    // the same as the durable pending spend queue; resolved records retain replay/audit evidence.
    private readonly string _indeterminateReservationPrefix = "spend_reservations_indeterminate:group:";

    public async Task<SpendReservationSettlementResult> RecordIndeterminateSpendReservationAsync(
        int virtualKeyId, string reservationId, decimal? knownAmount, string reason,
        DateTime? billedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (knownAmount < 0m) throw new ArgumentOutOfRangeException(nameof(knownAmount));
        var groupId = await GetGroupIdAsync(virtualKeyId);
        if (groupId is null)
            return new(SpendReservationSettlementStatus.Missing, knownAmount ?? 0m);

        var billingWindow = GetBillingWindow(billedAtUtc);
        var window = billingWindow.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
        var db = (await _redisConnectionFactory.GetConnectionAsync()).GetDatabase();
        // A replay after recovery must use the first accepted window, even if the caller
        // supplies a new timestamp. The window never changes during a request's lifecycle.
        var previous = await ReadReconciliationAsync(db, groupId.Value, reservationId);
        if (previous is not null)
            window = previous.BillingWindowStartUtc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);

        var held = previous?.HeldAmount;
        var expectedHeld = held?.ToString(CultureInfo.InvariantCulture);
        if (!held.HasValue)
        {
            var rawHeld = await db.HashGetAsync($"{_startedReservationPrefix}{groupId}", reservationId);
            if (!rawHeld.HasValue)
                rawHeld = await db.HashGetAsync($"{_reservationPrefix}{groupId}", reservationId);
            if (!rawHeld.HasValue)
                return new(SpendReservationSettlementStatus.Missing, knownAmount ?? 0m);
            expectedHeld = rawHeld.ToString();
            held = decimal.Parse(rawHeld.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        var priorKnownUnits = ToSpendUnits(previous?.KnownAmount ?? 0m);
        var desiredUnits = knownAmount.HasValue ? ToSpendUnits(knownAmount.Value) : priorKnownUnits;
        var deltaUnits = desiredUnits - priorKnownUnits;
        if (deltaUnits < 0)
            return new(SpendReservationSettlementStatus.Conflict, knownAmount ?? 0m);
        var remainingHold = Math.Max(0m, held.Value - deltaUnits / SpendUnitScale);
        var releasedHold = held.Value - remainingHold;
        const string script = """
            if redis.call('HGET', KEYS[10], ARGV[1]) ~= ARGV[6] then return -1 end
            local raw = redis.call('HGET', KEYS[1], ARGV[1])
            local record
            if raw then
                record = cjson.decode(raw)
                if record.virtualKeyId ~= ARGV[6] then return -1 end
                if record.window ~= ARGV[7] then return -1 end
                if record.resolved then
                    if ARGV[2] == record.knownUnits then return 2 end
                    return -1
                end
                if ARGV[2] == record.knownUnits then return 2 end
            else
                if redis.call('HEXISTS', KEYS[5], ARGV[1]) == 1 then return -1 end
                local held = redis.call('HGET', KEYS[2], ARGV[1])
                    or redis.call('HGET', KEYS[3], ARGV[1])
                if not held then return 0 end
                record = { knownUnits = '0', heldAmount = held, window = ARGV[7],
                    reason = ARGV[4], observedAtUtc = ARGV[5], virtualKeyId = ARGV[6], resolved = false }
            end
            if record.knownUnits ~= ARGV[8] or record.heldAmount ~= ARGV[9] then return -1 end
            if ARGV[3] ~= '0' then
                redis.call('INCRBY', KEYS[7], ARGV[3])
                redis.call('INCRBY', KEYS[8], ARGV[3])
                redis.call('INCRBY', KEYS[9], ARGV[3])
            end
            redis.call('HDEL', KEYS[3], ARGV[1])
            redis.call('ZREM', KEYS[4], ARGV[1])
            redis.call('HSET', KEYS[2], ARGV[1], ARGV[10])
            if ARGV[11] ~= '0' then
                local total = tonumber(redis.call('INCRBYFLOAT', KEYS[6], '-' .. ARGV[11]))
                if total <= 0 then redis.call('DEL', KEYS[6]) end
            end
            record.knownUnits = ARGV[2]
            record.heldAmount = ARGV[10]
            redis.call('HSET', KEYS[1], ARGV[1], cjson.encode(record))
            return 1
            """;
        var result = (long)await db.ScriptEvaluateAsync(script,
            new RedisKey[]
            {
                $"{_indeterminateReservationPrefix}{groupId}", $"{_startedReservationPrefix}{groupId}",
                $"{_reservationPrefix}{groupId}", $"{_reservationExpiryPrefix}{groupId}",
                $"{_settledReservationPrefix}{groupId}", $"{_reservedSpendPrefix}{groupId}",
                $"{_windowedSpendUnitsPrefix}{groupId}:window:{window}",
                $"{_windowedPendingTotalUnitsPrefix}{groupId}",
                $"{_windowedKeyUsageUnitsPrefix}{groupId}:window:{window}:key:{virtualKeyId}",
                $"{_reservationOwnerPrefix}{groupId}"
            },
            new RedisValue[]
            {
                reservationId, desiredUnits.ToString(CultureInfo.InvariantCulture),
                deltaUnits.ToString(CultureInfo.InvariantCulture), reason,
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), virtualKeyId, window,
                priorKnownUnits.ToString(CultureInfo.InvariantCulture), expectedHeld!,
                remainingHold.ToString(CultureInfo.InvariantCulture), releasedHold.ToString(CultureInfo.InvariantCulture)
            });
        return ReconciliationResult(result, knownAmount ?? previous?.KnownAmount ?? 0m);
    }

    public async Task<SpendReservationReconciliation?> GetSpendReservationReconciliationAsync(
        int virtualKeyId, string reservationId)
    {
        var groupId = await GetGroupIdAsync(virtualKeyId);
        if (groupId is null) return null;
        var db = (await _redisConnectionFactory.GetConnectionAsync()).GetDatabase();
        const string script = """
            if redis.call('HGET', KEYS[2], ARGV[1]) ~= ARGV[2] then return nil end
            return redis.call('HGET', KEYS[1], ARGV[1])
            """;
        var value = (RedisValue)await db.ScriptEvaluateAsync(script,
            new RedisKey[] { $"{_indeterminateReservationPrefix}{groupId}", $"{_reservationOwnerPrefix}{groupId}" },
            new RedisValue[] { reservationId, virtualKeyId });
        var record = ParseReconciliation(reservationId, value);
        return record?.VirtualKeyId == virtualKeyId ? record : null;
    }

    private async Task<SpendReservationReconciliation?> ReadReconciliationAsync(
        IDatabase db, int groupId, string reservationId)
    {
        var value = await db.HashGetAsync($"{_indeterminateReservationPrefix}{groupId}", reservationId);
        return ParseReconciliation(reservationId, value);
    }

    private static SpendReservationReconciliation? ParseReconciliation(string reservationId, RedisValue value)
    {
        if (!value.HasValue) return null;
        using var json = JsonDocument.Parse(value.ToString());
        var record = json.RootElement;
        string? Optional(string name) => record.TryGetProperty(name, out var item) ? item.GetString() : null;
        decimal Units(string name) => decimal.Parse(record.GetProperty(name).GetString()!, CultureInfo.InvariantCulture) / SpendUnitScale;
        return new(reservationId,
            int.Parse(record.GetProperty("virtualKeyId").GetString()!, CultureInfo.InvariantCulture),
            Units("knownUnits"),
            decimal.Parse(record.GetProperty("heldAmount").GetString()!, CultureInfo.InvariantCulture),
            DateTime.SpecifyKind(DateTime.ParseExact(record.GetProperty("window").GetString()!, "yyyyMMddHH", CultureInfo.InvariantCulture), DateTimeKind.Utc),
            record.GetProperty("reason").GetString()!, record.GetProperty("resolved").GetBoolean(),
            record.TryGetProperty("providerUnits", out _) ? Units("providerUnits") : null,
            Optional("disposition"), Optional("auditActor"), Optional("auditNote"));
    }

    public async Task<SpendReservationSettlementResult> ReconcileSpendReservationAsync(
        int virtualKeyId, string reservationId, decimal providerAmount,
        SpendReconciliationDisposition disposition, string auditActor, string auditNote)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditActor);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditNote);
        if (providerAmount < 0m || !Enum.IsDefined(disposition) ||
            (disposition == SpendReconciliationDisposition.ProviderCostWaived && providerAmount != 0m))
            throw new ArgumentOutOfRangeException(nameof(providerAmount));
        var groupId = await GetGroupIdAsync(virtualKeyId);
        if (groupId is null) return new(SpendReservationSettlementStatus.Missing, providerAmount);
        var db = (await _redisConnectionFactory.GetConnectionAsync()).GetDatabase();
        var record = await ReadReconciliationAsync(db, groupId.Value, reservationId);
        if (record is null) return new(SpendReservationSettlementStatus.Missing, providerAmount);
        if (record.VirtualKeyId != virtualKeyId)
            return new(SpendReservationSettlementStatus.Conflict, providerAmount);
        var window = record.BillingWindowStartUtc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
        const string script = """
            if redis.call('HGET', KEYS[8], ARGV[1]) ~= ARGV[7] then return -1 end
            local raw = redis.call('HGET', KEYS[1], ARGV[1])
            if not raw then return 0 end
            local record = cjson.decode(raw)
            if record.virtualKeyId ~= ARGV[7] then return -1 end
            if record.resolved then
                if record.providerUnits == ARGV[2] and record.disposition == ARGV[3]
                    and record.auditActor == ARGV[4] and record.auditNote == ARGV[5] then return 2 end
                return -1
            end
            -- The read used to select window keys and calculate exact total units may
            -- precede another cumulative known-spend update. Reject that stale snapshot.
            if record.knownUnits ~= ARGV[9] or record.window ~= ARGV[10] then return -1 end
            if ARGV[2] ~= '0' then
                redis.call('INCRBY', KEYS[5], ARGV[2])
                redis.call('INCRBY', KEYS[6], ARGV[2])
                redis.call('INCRBY', KEYS[7], ARGV[2])
            end
            redis.call('HDEL', KEYS[2], ARGV[1])
            local remaining = tonumber(redis.call('INCRBYFLOAT', KEYS[3], '-' .. record.heldAmount))
            if remaining <= 0 then redis.call('DEL', KEYS[3]) end
            redis.call('HSET', KEYS[4], ARGV[1], ARGV[8])
            record.resolved = true
            record.heldAmount = '0'
            record.providerUnits = ARGV[2]
            record.disposition = ARGV[3]
            record.auditActor = ARGV[4]
            record.auditNote = ARGV[5]
            record.resolvedAtUtc = ARGV[6]
            redis.call('HSET', KEYS[1], ARGV[1], cjson.encode(record))
            return 1
            """;
        var result = (long)await db.ScriptEvaluateAsync(script,
            new RedisKey[]
            {
                $"{_indeterminateReservationPrefix}{groupId}", $"{_startedReservationPrefix}{groupId}",
                $"{_reservedSpendPrefix}{groupId}", $"{_settledReservationPrefix}{groupId}",
                $"{_windowedSpendUnitsPrefix}{groupId}:window:{window}",
                $"{_windowedPendingTotalUnitsPrefix}{groupId}",
                $"{_windowedKeyUsageUnitsPrefix}{groupId}:window:{window}:key:{virtualKeyId}",
                $"{_reservationOwnerPrefix}{groupId}"
            }, new RedisValue[]
            {
                reservationId, ToSpendUnits(providerAmount).ToString(CultureInfo.InvariantCulture),
                disposition.ToString(), auditActor, auditNote, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                virtualKeyId, ToSpendUnits(record.KnownAmount + providerAmount).ToString(CultureInfo.InvariantCulture),
                ToSpendUnits(record.KnownAmount).ToString(CultureInfo.InvariantCulture), window
            });
        return ReconciliationResult(result, record.KnownAmount + providerAmount);
    }

    private static SpendReservationSettlementResult ReconciliationResult(long result, decimal amount) =>
        new(result switch
        {
            1 => SpendReservationSettlementStatus.Settled,
            2 => SpendReservationSettlementStatus.AlreadySettled,
            -1 => SpendReservationSettlementStatus.Conflict,
            _ => SpendReservationSettlementStatus.Missing
        }, amount);
}
