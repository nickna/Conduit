using System.Globalization;
using ConduitLLM.Configuration.Interfaces;
using StackExchange.Redis;

namespace ConduitLLM.Configuration.Services;

public partial class BatchSpendUpdateService
{
    // Recovery evidence is immutable and retained with settled/reconciliation replay evidence.
    private readonly string _reservationOwnerRecoveryPrefix = "spend_reservation_owner_recovery:group:";

    public async Task<SpendReservationOwnerRecoveryStatus> RecoverLegacySpendReservationOwnerAsync(
        int virtualKeyId, string reservationId, string auditActor, string auditNote)
    {
        if (virtualKeyId <= 0) throw new ArgumentOutOfRangeException(nameof(virtualKeyId));
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditActor);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditNote);
        var groupId = await GetGroupIdAsync(virtualKeyId);
        if (groupId is null) return SpendReservationOwnerRecoveryStatus.Missing;
        var db = (await _redisConnectionFactory.GetConnectionAsync()).GetDatabase();
        const string script = """
            local reserved = redis.call('HEXISTS', KEYS[1], ARGV[1]) == 1
            local started = redis.call('HEXISTS', KEYS[2], ARGV[1]) == 1
            local settled = redis.call('HEXISTS', KEYS[3], ARGV[1]) == 1
            local raw = redis.call('HGET', KEYS[4], ARGV[1])
            if not reserved and not started and not settled and not raw then return 0 end
            if reserved and not started and not settled and not raw then
                local expires = redis.call('ZSCORE', KEYS[7], ARGV[1])
                if expires and tonumber(expires) <= tonumber(ARGV[6]) then return 0 end
            end
            -- A legacy reconciliation record already establishes a key identity; audit
            -- recovery may never replace it, including after the record was resolved.
            if raw and cjson.decode(raw).virtualKeyId ~= ARGV[2] then return -1 end
            local owner = redis.call('HGET', KEYS[5], ARGV[1])
            local audit = redis.call('HGET', KEYS[6], ARGV[1])
            if audit then
                local evidence = cjson.decode(audit)
                if evidence.virtualKeyId == ARGV[2] and evidence.auditActor == ARGV[3]
                    and evidence.auditNote == ARGV[4] then
                    if owner == ARGV[2] then return 2 end
                    if not owner then
                        redis.call('HSET', KEYS[5], ARGV[1], ARGV[2])
                        return 1
                    end
                end
                return -1
            end
            if owner then return -1 end
            redis.call('HSET', KEYS[5], ARGV[1], ARGV[2])
            redis.call('HSET', KEYS[6], ARGV[1], cjson.encode({ virtualKeyId = ARGV[2],
                auditActor = ARGV[3], auditNote = ARGV[4], observedAtUtc = ARGV[5] }))
            return 1
            """;
        var result = (long)await db.ScriptEvaluateAsync(script,
            new RedisKey[]
            {
                $"{_reservationPrefix}{groupId}", $"{_startedReservationPrefix}{groupId}",
                $"{_settledReservationPrefix}{groupId}", $"{_indeterminateReservationPrefix}{groupId}",
                $"{_reservationOwnerPrefix}{groupId}", $"{_reservationOwnerRecoveryPrefix}{groupId}",
                $"{_reservationExpiryPrefix}{groupId}"
            }, new RedisValue[]
            {
                reservationId, virtualKeyId, auditActor, auditNote,
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        return result switch
        {
            1 => SpendReservationOwnerRecoveryStatus.Recovered,
            2 => SpendReservationOwnerRecoveryStatus.AlreadyRecovered,
            -1 => SpendReservationOwnerRecoveryStatus.Conflict,
            _ => SpendReservationOwnerRecoveryStatus.Missing
        };
    }
}
