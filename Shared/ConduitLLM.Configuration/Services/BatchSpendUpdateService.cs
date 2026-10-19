using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Exceptions;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

namespace ConduitLLM.Configuration.Services
{
    /// <summary>
    /// Background service that batches Virtual Key spend updates to reduce database writes
    /// Provides events for cache invalidation integration
    /// </summary>
    public partial class BatchSpendUpdateService : BackgroundService, IBatchSpendUpdateService, IAsyncDisposable
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ILogger<BatchSpendUpdateService> _logger;
        private readonly RedisConnectionFactory _redisConnectionFactory;
        private readonly IBillingAlertingService _alertingService;
        private readonly IRedisCircuitBreaker? _circuitBreaker;
        private readonly IVirtualKeyRuntimeStore? _runtimeStore;
        private readonly BatchSpendingOptions _options;
        private readonly TimeSpan _flushInterval;
        private readonly TimeSpan _reservationTtl;
        private readonly string _redisKeyPrefix = "pending_spend:group:";
        private readonly string _redisUnitsKeyPrefix = "pending_spend_units:group:";
        private readonly string _processingKeyPrefix = "processing_spend:group:";
        private readonly string _processingUnitsKeyPrefix = "processing_spend_units:group:";
        private readonly string _keyUsagePrefix = "key_usage:group:";
        private readonly string _keyUsageUnitsPrefix = "key_usage_units:group:";
        private readonly string _processingKeyUsagePrefix = "processing_key_usage:group:";
        private readonly string _processingKeyUsageUnitsPrefix = "processing_key_usage_units:group:";
        private readonly string _windowedSpendUnitsPrefix = "pending_spend_window_units:group:";
        private readonly string _windowedPendingTotalUnitsPrefix = "pending_spend_window_total_units:group:";
        private readonly string _windowedProcessingUnitsPrefix = "processing_spend_window_units:group:";
        private readonly string _windowedKeyUsageUnitsPrefix = "key_usage_window_units:group:";
        private readonly string _windowedProcessingKeyUsageUnitsPrefix = "processing_key_usage_window_units:group:";
        private readonly string _reservedSpendPrefix = "reserved_spend:group:";
        private readonly string _reservationPrefix = "spend_reservations:group:";
        private readonly string _reservationExpiryPrefix = "spend_reservation_expiry:group:";
        private readonly string _startedReservationPrefix = "spend_reservations_started:group:";
        private readonly string _settledReservationPrefix = "spend_reservations_settled:group:";
        private readonly string _reservationOwnerPrefix = "spend_reservation_owners:group:";
        private readonly string _reservationBoundPrefix = "spend_reservation_bounds:group:";
        private readonly ConcurrentQueue<FallbackSpendUpdate> _fallbackQueue = new();
        private const decimal SpendUnitScale = 100_000_000m;

        /// <summary>
        /// Event raised after successful batch spend updates with the key hashes that were updated
        /// Allows external cache invalidation without tight coupling
        /// </summary>
        public event Func<string[], CancellationToken, Task>? SpendUpdatesCompleted;

        /// <summary>
        /// Initializes a new instance of the BatchSpendUpdateService
        /// </summary>
        /// <param name="serviceScopeFactory">Service scope factory for creating scoped services</param>
        /// <param name="redisConnectionFactory">Redis connection factory</param>
        /// <param name="options">Batch spending configuration options</param>
        /// <param name="logger">Logger instance</param>
        public BatchSpendUpdateService(
            IServiceScopeFactory serviceScopeFactory,
            RedisConnectionFactory redisConnectionFactory,
            IOptions<BatchSpendingOptions> options,
            ILogger<BatchSpendUpdateService> logger,
            IBillingAlertingService alertingService,
            IRedisCircuitBreaker? circuitBreaker = null,
            IVirtualKeyRuntimeStore? runtimeStore = null)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _redisConnectionFactory = redisConnectionFactory;
            _options = options.Value;
            _logger = logger;
            _alertingService = alertingService;
            _circuitBreaker = circuitBreaker;
            _runtimeStore = runtimeStore;

            // Validate and apply configuration
            var validationResult = _options.Validate();
            if (validationResult != null)
            {
                _logger.LogError("Invalid BatchSpending configuration: {ValidationError}", validationResult.ErrorMessage);
                throw new InvalidOperationException($"Invalid BatchSpending configuration: {validationResult.ErrorMessage}");
            }

            _flushInterval = _options.GetValidatedFlushInterval();
            _reservationTtl = _options.GetRedisTtl();
            _logger.LogInformation(
                "BatchSpendUpdateService configured with flush interval: {FlushInterval}; pending billing keys do not expire",
                _flushInterval);

        }

        /// <summary>
        /// Gets whether the service is healthy and able to accept updates
        /// </summary>
        public bool IsHealthy =>
            !_admissionClosed && Volatile.Read(ref _leaderActive) != 0 &&
            _circuitBreaker?.IsOpen != true;

        private CancellationTokenSource? _cancellationTokenSource;

        /// <summary>
        /// Queues a spend update to Redis for batch processing.
        /// Throws on failure so the caller can fall back to alternative paths.
        /// </summary>
        /// <param name="virtualKeyId">Virtual Key ID to update</param>
        /// <param name="cost">Cost to add to the current spend</param>
        public Task QueueSpendUpdateAsync(int virtualKeyId, decimal cost, DateTime? billedAtUtc = null)
        {
            lock (_lifetimeSync)
            {
                ThrowIfAdmissionClosed();
                var task = QueueSpendCoreAsync(virtualKeyId, cost, billedAtUtc, _queueLifetime.Token);
                TrackTask(_queueTasks, task);
                return task;
            }
        }

        private async Task QueueSpendCoreAsync(int virtualKeyId, decimal cost, DateTime? billedAtUtc, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Check circuit breaker if available
            if (_circuitBreaker?.IsOpen == true)
            {
                throw new RedisCircuitBreakerOpenException(
                    "Cannot update spend - Redis circuit breaker is open",
                    CircuitState.Open);
            }

            var groupId = await GetGroupIdAsync(virtualKeyId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!groupId.HasValue)
            {
                throw new BillingSystemException(
                    $"Virtual Key {virtualKeyId} was not found while queueing spend",
                    virtualKeyId,
                    "database_update_failed");
            }

            // Execute Redis operations through circuit breaker if available
            if (_circuitBreaker != null)
            {
                await _circuitBreaker.ExecuteAsync(async () =>
                {
                    await PerformRedisUpdate(virtualKeyId, groupId.Value, cost, GetBillingWindow(billedAtUtc), cancellationToken);
                });
            }
            else
            {
                await PerformRedisUpdate(virtualKeyId, groupId.Value, cost, GetBillingWindow(billedAtUtc), cancellationToken);
            }

            _logger.LogDebug("Queued spend update to Redis for Virtual Key {VirtualKeyId} (Group {GroupId}): {Cost:C}",
                virtualKeyId, groupId.Value, cost);
        }

        /// <inheritdoc />
        public void QueueFallbackUpdate(int virtualKeyId, decimal cost, DateTime? billedAtUtc = null) =>
            QueueFallbackUpdate(virtualKeyId, cost, billedAtUtc, $"fallback-spend:{Guid.NewGuid():N}");

        /// <inheritdoc />
        public void QueueFallbackUpdate(int virtualKeyId, decimal cost, DateTime? billedAtUtc, string idempotencyKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
            lock (_lifetimeSync)
            {
                ThrowIfAdmissionClosed();
                _fallbackQueue.Enqueue(new FallbackSpendUpdate(
                    virtualKeyId, cost, GetBillingWindow(billedAtUtc), idempotencyKey));
            }
            _logger.LogWarning(
                "Spend update for Virtual Key {VirtualKeyId} ({Cost:C}) queued to in-memory fallback. Will be flushed on next cycle.",
                virtualKeyId, cost);
        }

        private async Task PerformRedisUpdate(int virtualKeyId, int groupId, decimal cost, DateTime billingWindowStartUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var redis = await _redisConnectionFactory.GetConnectionAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var db = redis.GetDatabase();

            // Use group ID for accumulation
            var costUnits = ToSpendUnits(cost);
            var windowToken = billingWindowStartUtc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
            var key = $"{_windowedSpendUnitsPrefix}{groupId}:window:{windowToken}";
            var keyUsageKey = $"{_windowedKeyUsageUnitsPrefix}{groupId}:window:{windowToken}:key:{virtualKeyId}";
            const string script = """
                redis.call('INCRBY', KEYS[1], ARGV[1])
                redis.call('INCRBY', KEYS[2], ARGV[1])
                redis.call('INCRBY', KEYS[3], ARGV[1])
                return 1
                """;
            await db.ScriptEvaluateAsync(
                script,
                new RedisKey[] { key, $"{_windowedPendingTotalUnitsPrefix}{groupId}", keyUsageKey },
                new RedisValue[] { costUnits });
            cancellationToken.ThrowIfCancellationRequested();

            // Billing data must never have a TTL. It is acknowledged only after the
            // corresponding database debit commits; expiry would silently lose revenue
            // during a prolonged database outage or an extended service shutdown.
        }

        /// <summary>
        /// Get the current pending spend and active reservations for a Virtual Key
        /// </summary>
        /// <param name="virtualKeyId">Virtual Key ID</param>
        /// <returns>Pending spend amount</returns>
        public async Task<decimal> GetPendingSpendAsync(int virtualKeyId)
        {
            try
            {
                var groupId = await GetGroupIdAsync(virtualKeyId);
                if (!groupId.HasValue)
                {
                    _logger.LogWarning("Cannot read pending spend because Virtual Key {VirtualKeyId} was not found", virtualKeyId);
                    return 0;
                }

                return await ReadPendingSpendForGroupAsync(groupId.Value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get pending spend from Redis for Virtual Key {VirtualKeyId}", virtualKeyId);
                return 0;
            }
        }

        /// <inheritdoc />
        public async Task<decimal> GetPendingSpendForGroupAsync(int virtualKeyGroupId)
        {
            try
            {
                return await ReadPendingSpendForGroupAsync(virtualKeyGroupId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get pending spend from Redis for Virtual Key group {GroupId}", virtualKeyGroupId);
                return 0;
            }
        }

        private async Task<decimal> ReadPendingSpendForGroupAsync(int groupId)
        {
            var redis = await _redisConnectionFactory.GetConnectionAsync();
            var db = redis.GetDatabase();
            var values = await db.StringGetAsync(new RedisKey[]
            {
                $"{_redisKeyPrefix}{groupId}",
                $"{_redisUnitsKeyPrefix}{groupId}",
                $"{_reservedSpendPrefix}{groupId}",
                $"{_windowedPendingTotalUnitsPrefix}{groupId}"
            });

            return ParseRedisDecimal(values[0]) + ParseRedisUnits(values[1]) +
                ParseRedisDecimal(values[2]) + ParseRedisUnits(values[3]);
        }

        private static DateTime GetBillingWindow(DateTime? billedAtUtc) =>
            Utilities.BillingTimeUtilities.GetBillingWindowStartUtc(billedAtUtc);

        /// <inheritdoc />
        public async Task<bool> TryReserveSpendAsync(int virtualKeyId, decimal amount, string reservationId)
        {
            if (amount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), "A reservation amount cannot be negative");
            }

            if (string.IsNullOrWhiteSpace(reservationId))
            {
                throw new ArgumentException("A reservation ID is required", nameof(reservationId));
            }

            var keyAndBalance = await GetKeyAndBalanceAsync(virtualKeyId);

            if (keyAndBalance == null)
            {
                return false;
            }
            var account = keyAndBalance.Value;

            var redis = await _redisConnectionFactory.GetConnectionAsync();
            var db = redis.GetDatabase();
            var reservationsKey = $"{_reservationPrefix}{account.GroupId}";
            var reservationExpiryKey = $"{_reservationExpiryPrefix}{account.GroupId}";
            var reservedTotalKey = $"{_reservedSpendPrefix}{account.GroupId}";
            var pendingKey = $"{_redisKeyPrefix}{account.GroupId}";
            var pendingUnitsKey = $"{_redisUnitsKeyPrefix}{account.GroupId}";
            var windowedPendingTotalUnitsKey = $"{_windowedPendingTotalUnitsPrefix}{account.GroupId}";
            var startedReservationsKey = $"{_startedReservationPrefix}{account.GroupId}";
            var settledReservationsKey = $"{_settledReservationPrefix}{account.GroupId}";

            const string script = """
                local expired = redis.call('ZRANGEBYSCORE', KEYS[4], '-inf', ARGV[4])
                for _, id in ipairs(expired) do
                    local expiredAmount = tonumber(redis.call('HGET', KEYS[3], id) or '0')
                    if expiredAmount > 0 then
                        redis.call('INCRBYFLOAT', KEYS[2], -expiredAmount)
                    end
                    redis.call('HDEL', KEYS[3], id)
                    redis.call('HDEL', KEYS[9], id)
                    redis.call('HDEL', KEYS[10], id)
                    redis.call('ZREM', KEYS[4], id)
                end
                if redis.call('HEXISTS', KEYS[3], ARGV[3]) == 1
                    or redis.call('HEXISTS', KEYS[7], ARGV[3]) == 1
                    or redis.call('HEXISTS', KEYS[8], ARGV[3]) == 1 then
                    if redis.call('HGET', KEYS[9], ARGV[3]) == ARGV[8]
                        and redis.call('HGET', KEYS[10], ARGV[3]) == ARGV[2] then return 1 end
                    return 0
                end
                local pending = tonumber(redis.call('GET', KEYS[1]) or '0')
                    + (tonumber(redis.call('GET', KEYS[5]) or '0') / tonumber(ARGV[7]))
                    + (tonumber(redis.call('GET', KEYS[6]) or '0') / tonumber(ARGV[7]))
                local reserved = tonumber(redis.call('GET', KEYS[2]) or '0')
                if reserved < 0 then
                    redis.call('DEL', KEYS[2])
                    reserved = 0
                end
                local balance = tonumber(ARGV[1])
                local amount = tonumber(ARGV[2])
                if pending + reserved + amount > balance then
                    return 0
                end
                if amount > 0 then
                    redis.call('INCRBYFLOAT', KEYS[2], amount)
                end
                redis.call('HSET', KEYS[3], ARGV[3], amount)
                redis.call('HSET', KEYS[9], ARGV[3], ARGV[8])
                redis.call('HSET', KEYS[10], ARGV[3], ARGV[2])
                redis.call('ZADD', KEYS[4], ARGV[5], ARGV[3])
                return 1
                """;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ttlMilliseconds = (long)_reservationTtl.TotalMilliseconds;

            var result = await db.ScriptEvaluateAsync(
                script,
                new RedisKey[]
                {
                    pendingKey,
                    reservedTotalKey,
                    reservationsKey,
                    reservationExpiryKey,
                    pendingUnitsKey,
                    windowedPendingTotalUnitsKey,
                    startedReservationsKey,
                    settledReservationsKey,
                    $"{_reservationOwnerPrefix}{account.GroupId}",
                    $"{_reservationBoundPrefix}{account.GroupId}"
                },
                new RedisValue[]
                {
                    account.Balance.ToString(CultureInfo.InvariantCulture),
                    // Exact canonical decimals preserve numeric-equivalent replays without
                    // comparing the original estimate to an indeterminate request's reduced hold.
                    amount.ToString("G29", CultureInfo.InvariantCulture),
                    reservationId,
                    now,
                    now + ttlMilliseconds,
                    ttlMilliseconds,
                    SpendUnitScale.ToString(CultureInfo.InvariantCulture),
                    virtualKeyId
                });

            return (long)result == 1;
        }

        /// <inheritdoc />
        public async Task<bool> MarkSpendReservationInvocationStartedAsync(int virtualKeyId, string reservationId)
        {
            if (string.IsNullOrWhiteSpace(reservationId))
            {
                return false;
            }

            var groupId = await GetGroupIdAsync(virtualKeyId);
            if (!groupId.HasValue)
            {
                return false;
            }

            var redis = await _redisConnectionFactory.GetConnectionAsync();
            var db = redis.GetDatabase();
            const string script = """
                if redis.call('HGET', KEYS[5], ARGV[1]) ~= ARGV[2] then return 0 end
                if redis.call('HEXISTS', KEYS[4], ARGV[1]) == 1 then
                    return 1
                end
                if redis.call('HEXISTS', KEYS[3], ARGV[1]) == 1 then
                    return 1
                end
                local amount = redis.call('HGET', KEYS[1], ARGV[1])
                if not amount then
                    return 0
                end
                redis.call('HDEL', KEYS[1], ARGV[1])
                redis.call('ZREM', KEYS[2], ARGV[1])
                redis.call('HSET', KEYS[3], ARGV[1], amount)
                return 1
                """;
            var result = await db.ScriptEvaluateAsync(
                script,
                new RedisKey[]
                {
                    $"{_reservationPrefix}{groupId.Value}",
                    $"{_reservationExpiryPrefix}{groupId.Value}",
                    $"{_startedReservationPrefix}{groupId.Value}",
                    $"{_settledReservationPrefix}{groupId.Value}",
                    $"{_reservationOwnerPrefix}{groupId.Value}"
                },
                new RedisValue[] { reservationId, virtualKeyId });

            return (long)result == 1;
        }

        /// <inheritdoc />
        public async Task<SpendReservationSettlementResult> SettleSpendReservationAsync(
            int virtualKeyId,
            string reservationId,
            decimal actualAmount,
            DateTime? billedAtUtc = null)
        {
            if (string.IsNullOrWhiteSpace(reservationId) || actualAmount < 0m)
            {
                return new SpendReservationSettlementResult(SpendReservationSettlementStatus.Conflict, actualAmount);
            }

            var groupId = await GetGroupIdAsync(virtualKeyId);
            if (!groupId.HasValue)
            {
                return new SpendReservationSettlementResult(SpendReservationSettlementStatus.Missing, actualAmount);
            }

            var billingWindow = GetBillingWindow(billedAtUtc);
            var windowToken = billingWindow.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
            var actualUnits = ToSpendUnits(actualAmount);
            var redis = await _redisConnectionFactory.GetConnectionAsync();
            var db = redis.GetDatabase();
            const string script = """
                local owner = redis.call('HGET', KEYS[10], ARGV[1])
                if owner ~= ARGV[4] then
                    if owner or redis.call('HEXISTS', KEYS[1], ARGV[1]) == 1
                        or redis.call('HEXISTS', KEYS[3], ARGV[1]) == 1
                        or redis.call('HEXISTS', KEYS[4], ARGV[1]) == 1
                        or redis.call('HEXISTS', KEYS[9], ARGV[1]) == 1 then return -1 end
                    return 0
                end
                local settledAmount = redis.call('HGET', KEYS[4], ARGV[1])
                if settledAmount then
                    if tonumber(settledAmount) == tonumber(ARGV[2]) then
                        return 2
                    end
                    return -1
                end

                -- Partial known spend is already queued. Only audited reconciliation may
                -- charge the unresolved provider portion and release its remaining hold.
                if redis.call('HEXISTS', KEYS[9], ARGV[1]) == 1 then
                    return -1
                end

                local reservedAmount = redis.call('HGET', KEYS[3], ARGV[1])
                if not reservedAmount then
                    reservedAmount = redis.call('HGET', KEYS[1], ARGV[1])
                end
                if not reservedAmount then
                    return 0
                end

                redis.call('HDEL', KEYS[1], ARGV[1])
                redis.call('HDEL', KEYS[3], ARGV[1])
                redis.call('ZREM', KEYS[2], ARGV[1])
                if ARGV[2] ~= '0' then
                    redis.call('INCRBY', KEYS[6], ARGV[2])
                    redis.call('INCRBY', KEYS[7], ARGV[2])
                    redis.call('INCRBY', KEYS[8], ARGV[2])
                end
                redis.call('HSET', KEYS[4], ARGV[1], ARGV[2])

                local remaining = tonumber(redis.call('INCRBYFLOAT', KEYS[5], -tonumber(reservedAmount)))
                if remaining <= 0 then
                    redis.call('DEL', KEYS[5])
                end

                if tonumber(ARGV[3]) > tonumber(reservedAmount) then
                    return 3
                end
                return 1
                """;
            var result = (long)await db.ScriptEvaluateAsync(
                script,
                new RedisKey[]
                {
                    $"{_reservationPrefix}{groupId.Value}",
                    $"{_reservationExpiryPrefix}{groupId.Value}",
                    $"{_startedReservationPrefix}{groupId.Value}",
                    $"{_settledReservationPrefix}{groupId.Value}",
                    $"{_reservedSpendPrefix}{groupId.Value}",
                    $"{_windowedSpendUnitsPrefix}{groupId.Value}:window:{windowToken}",
                    $"{_windowedPendingTotalUnitsPrefix}{groupId.Value}",
                    $"{_windowedKeyUsageUnitsPrefix}{groupId.Value}:window:{windowToken}:key:{virtualKeyId}",
                    $"{_indeterminateReservationPrefix}{groupId.Value}",
                    $"{_reservationOwnerPrefix}{groupId.Value}"
                },
                new RedisValue[]
                {
                    reservationId,
                    actualUnits,
                    actualAmount.ToString(CultureInfo.InvariantCulture),
                    virtualKeyId
                });

            var status = result switch
            {
                1 => SpendReservationSettlementStatus.Settled,
                2 => SpendReservationSettlementStatus.AlreadySettled,
                3 => SpendReservationSettlementStatus.SettledOverEstimate,
                -1 => SpendReservationSettlementStatus.Conflict,
                _ => SpendReservationSettlementStatus.Missing
            };
            return new SpendReservationSettlementResult(status, actualAmount);
        }

        /// <inheritdoc />
        public async Task ReleaseSpendReservationAsync(int virtualKeyId, string reservationId)
        {
            if (string.IsNullOrWhiteSpace(reservationId))
            {
                return;
            }

            var groupId = await GetGroupIdAsync(virtualKeyId);
            if (!groupId.HasValue)
            {
                return;
            }

            var redis = await _redisConnectionFactory.GetConnectionAsync();
            var db = redis.GetDatabase();
            var reservationsKey = $"{_reservationPrefix}{groupId.Value}";
            var reservationExpiryKey = $"{_reservationExpiryPrefix}{groupId.Value}";
            var reservedTotalKey = $"{_reservedSpendPrefix}{groupId.Value}";

            const string script = """
                if redis.call('HGET', KEYS[4], ARGV[1]) ~= ARGV[2] then return 0 end
                local rawAmount = redis.call('HGET', KEYS[1], ARGV[1])
                if not rawAmount then
                    return 0
                end
                local amount = tonumber(rawAmount)
                redis.call('HDEL', KEYS[1], ARGV[1])
                redis.call('HDEL', KEYS[4], ARGV[1])
                redis.call('HDEL', KEYS[5], ARGV[1])
                redis.call('ZREM', KEYS[2], ARGV[1])
                if amount > 0 then
                    local remaining = tonumber(redis.call('INCRBYFLOAT', KEYS[3], -amount))
                    if remaining <= 0 then
                        redis.call('DEL', KEYS[3])
                    end
                end
                return amount
                """;

            await db.ScriptEvaluateAsync(
                script,
                new RedisKey[] { reservationsKey, reservationExpiryKey, reservedTotalKey,
                    $"{_reservationOwnerPrefix}{groupId.Value}", $"{_reservationBoundPrefix}{groupId.Value}" },
                new RedisValue[] { reservationId, virtualKeyId });
        }

        private async Task<int?> GetGroupIdAsync(int virtualKeyId, CancellationToken cancellationToken = default)
        {
            if (_runtimeStore is not null)
            {
                return (await _runtimeStore.GetByIdAsync(virtualKeyId, cancellationToken))?.VirtualKeyGroupId;
            }

#if CONDUIT_NATIVE_AOT
            throw new InvalidOperationException(
                "NativeAOT batch spending requires IVirtualKeyRuntimeStore.");
#else
            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IConfigurationDbContext>();
            return await context.VirtualKeys
                .Where(vk => vk.Id == virtualKeyId)
                .Select(vk => (int?)vk.VirtualKeyGroupId)
                .FirstOrDefaultAsync(cancellationToken);
#endif
        }

        private async Task<(int GroupId, decimal Balance)?> GetKeyAndBalanceAsync(int virtualKeyId)
        {
            if (_runtimeStore is not null)
            {
                var key = await _runtimeStore.GetByIdAsync(virtualKeyId);
                return key is null ? null : (key.VirtualKeyGroupId, key.Group.Balance);
            }

#if CONDUIT_NATIVE_AOT
            throw new InvalidOperationException(
                "NativeAOT batch spending requires IVirtualKeyRuntimeStore.");
#else
            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IConfigurationDbContext>();
            var keyAndBalance = await context.VirtualKeys
                .Where(key => key.Id == virtualKeyId)
                .Select(key => new { key.VirtualKeyGroupId, key.VirtualKeyGroup!.Balance })
                .FirstOrDefaultAsync();
            return keyAndBalance is null
                ? null
                : (keyAndBalance.VirtualKeyGroupId, keyAndBalance.Balance);
#endif
        }

        private static decimal ParseRedisDecimal(RedisValue value)
        {
            return value.HasValue && decimal.TryParse(
                value.ToString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var amount)
                ? amount
                : 0m;
        }

        private static long ToSpendUnits(decimal amount)
        {
            return checked((long)decimal.Round(amount * SpendUnitScale, 0, MidpointRounding.AwayFromZero));
        }

        private static decimal ParseRedisUnits(RedisValue value)
        {
            return value.HasValue && long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var units)
                ? units / SpendUnitScale
                : 0m;
        }

        /// <summary>
        /// Force flush all pending updates immediately
        /// </summary>
        /// <returns>Number of finalized Redis claims plus successfully persisted fallback updates</returns>
        private async Task<int> FlushCoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var redis = await _redisConnectionFactory.GetConnectionAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var db = redis.GetDatabase();
            var server = redis.GetServer(redis.GetEndPoints()[0]);

            // Recover durable claims left by a database failure or process crash first,
            // then atomically move current pending amounts into new claims. New usage
            // can continue accumulating under the original pending keys while a claim
            // is written to PostgreSQL.
            var claims = await GetProcessingClaimsAsync(server, db, cancellationToken);
            claims.AddRange(await ClaimPendingSpendAsync(server, db, cancellationToken));

            if (claims.Count == 0 && _fallbackQueue.IsEmpty)
            {
                _logger.LogDebug("No pending spend updates to flush");
                return 0;
            }

            _logger.LogDebug("Flushing {PendingCount} durable spend claims from Redis", claims.Count);

            // Process each group
            var updatedKeyHashes = new List<string>();
            var flushStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var processedCount = 0;
            decimal totalSpend = 0;

            foreach (var claim in claims)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Create a description that includes which keys were used
                var description = BuildUsageDescription(claim.KeyUsageByKeyId);

                // The claim ID is persisted on the ledger row in the same transaction as
                // the debit. If the process dies after the DB commit but before deleting
                // the Redis claim, recovery observes Applied=false and only acknowledges
                // the already-recorded claim.
                var result = await ApplyClaimAsync(claim, description, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                // Acknowledge only after the database commit (or idempotent duplicate
                // confirmation). Until this delete succeeds, the claim remains durable
                // and retryable in Redis.
                await DeleteClaimAsync(db, claim, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                processedCount++;
                totalSpend += result.Applied ? claim.TotalCost : 0;
                _logger.LogDebug(
                    "Batch flush: finalized claim {ClaimId} for group {GroupId} — amount {Cost:C}, new balance: {NewBalance:C}, applied: {Applied} ({Processed}/{Total})",
                    claim.ClaimId, claim.GroupId, claim.TotalCost, result.NewBalance, result.Applied, processedCount, claims.Count);

                // Note: We don't need to create additional transaction records here
                // because AdjustBalanceIdempotentAsync already creates one with the correct balance.
                // The individual key usage tracking is already handled in the description.

                updatedKeyHashes.AddRange(await GetKeyHashesByGroupIdAsync(claim.GroupId, cancellationToken));
            }

            flushStopwatch.Stop();
            _logger.LogInformation(
                "Batch flush completed: {ClaimCount} claims, total newly deducted: {TotalSpend:C}, affected keys: {KeyCount}, elapsed: {ElapsedMs}ms",
                processedCount, totalSpend, updatedKeyHashes.Count, flushStopwatch.ElapsedMilliseconds);

            // Drain in-memory fallback queue
            var fallbackCount = 0;
            while (_fallbackQueue.TryDequeue(out var fallbackItem))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fallbackKeyHash = await ApplyFallbackAsync(fallbackItem, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (fallbackKeyHash is not null)
                    {
                        updatedKeyHashes.Add(fallbackKeyHash);
                        fallbackCount++;
                    }
                    else
                    {
                        _logger.LogWarning("Virtual Key {VirtualKeyId} from fallback queue not found — spend update lost",
                            fallbackItem.VirtualKeyId);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // A commit can have succeeded before cancellation was observed. The
                    // same operation identity is retained for a safe in-process retry.
                    _fallbackQueue.Enqueue(fallbackItem);
                    throw;
                }
                catch (Exception fallbackEx)
                {
                    _logger.LogError(fallbackEx,
                        "Failed to process fallback spend update for Virtual Key {VirtualKeyId}. Re-queuing.",
                        fallbackItem.VirtualKeyId);
                    // Re-queue for the next flush cycle
                    _fallbackQueue.Enqueue(fallbackItem);
                    break; // Stop processing fallback queue on error to avoid infinite loop
                }
            }

            if (fallbackCount > 0)
            {
                _logger.LogInformation("Recovered {Count} spend updates from in-memory fallback queue", fallbackCount);
            }

            // Raise event for cache invalidation (if any subscribers)
            if (updatedKeyHashes.Any() && SpendUpdatesCompleted is { } subscribers)
            {
                foreach (Func<string[], CancellationToken, Task> subscriber in subscribers.GetInvocationList())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { await subscriber(updatedKeyHashes.ToArray(), cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception eventEx) { _logger.LogWarning(eventEx, "Error in SpendUpdatesCompleted event handler"); }
                }
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogDebug("Raised SpendUpdatesCompleted event for {Count} Virtual Keys", updatedKeyHashes.Count());
            }

            return processedCount + fallbackCount;
        }

        private async Task<VirtualKeyBalanceAdjustmentResult> ApplyClaimAsync(
            SpendClaim claim,
            string description, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var idempotencyKey = $"batch-spend:{claim.ClaimId}";
            if (_runtimeStore is not null)
            {
                return await _runtimeStore.AdjustBalanceAsync(new VirtualKeyBalanceAdjustment(
                    claim.GroupId,
                    -claim.TotalCost,
                    description,
                    "System",
                    VirtualKeyBalanceReferenceType.System,
                    claim.ClaimId,
                    idempotencyKey,
                    claim.BillingWindowStartUtc), cancellationToken);
            }

#if CONDUIT_NATIVE_AOT
            throw new InvalidOperationException(
                "NativeAOT batch spending requires IVirtualKeyRuntimeStore.");
#else
            using var scope = _serviceScopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IVirtualKeyGroupRepository>();
            cancellationToken.ThrowIfCancellationRequested();
            var result = claim.BillingWindowStartUtc.HasValue
                ? await repository.AdjustBalanceIdempotentAsync(
                    claim.GroupId, -claim.TotalCost, idempotencyKey,
                    description, "System", ReferenceType.System, claim.ClaimId,
                    claim.BillingWindowStartUtc.Value)
                : await repository.AdjustBalanceIdempotentAsync(
                    claim.GroupId, -claim.TotalCost, idempotencyKey,
                    description, "System", ReferenceType.System, claim.ClaimId);
            return new VirtualKeyBalanceAdjustmentResult(
                result.NewBalance,
                result.LifetimeSpent,
                result.Applied);
#endif
        }

        private async Task<IReadOnlyList<string>> GetKeyHashesByGroupIdAsync(int groupId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_runtimeStore is not null)
            {
                return await _runtimeStore.GetKeyHashesByGroupIdAsync(groupId, cancellationToken);
            }

#if CONDUIT_NATIVE_AOT
            throw new InvalidOperationException(
                "NativeAOT batch spending requires IVirtualKeyRuntimeStore.");
#else
            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IConfigurationDbContext>();
            return await context.VirtualKeys
                .AsNoTracking()
                .Where(key => key.VirtualKeyGroupId == groupId)
                .OrderBy(key => key.Id)
                .Select(key => key.KeyHash)
                .ToListAsync(cancellationToken);
#endif
        }

        private async Task<string?> ApplyFallbackAsync(
            FallbackSpendUpdate fallback, CancellationToken cancellationToken)
        {
            if (_runtimeStore is not null)
            {
                var key = await _runtimeStore.GetByIdAsync(fallback.VirtualKeyId, cancellationToken);
                if (key is null)
                {
                    return null;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await _runtimeStore.AdjustBalanceAsync(new VirtualKeyBalanceAdjustment(
                    key.VirtualKeyGroupId,
                    -fallback.Cost,
                    $"API usage by virtual key #{fallback.VirtualKeyId} (recovered from fallback queue)",
                    "System",
                    VirtualKeyBalanceReferenceType.VirtualKey,
                    fallback.VirtualKeyId.ToString(CultureInfo.InvariantCulture),
                    IdempotencyKey: fallback.IdempotencyKey,
                    BillingWindowStartUtc: fallback.BillingWindowStartUtc), cancellationToken);
                return key.KeyHash;
            }

#if CONDUIT_NATIVE_AOT
            throw new InvalidOperationException(
                "NativeAOT batch spending requires IVirtualKeyRuntimeStore.");
#else
            using var scope = _serviceScopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IConfigurationDbContext>();
            var repository = scope.ServiceProvider.GetRequiredService<IVirtualKeyGroupRepository>();
            var keyRecord = await context.VirtualKeys
                .Where(key => key.Id == fallback.VirtualKeyId)
                .Select(key => new { key.VirtualKeyGroupId, key.KeyHash })
                .FirstOrDefaultAsync(cancellationToken);
            if (keyRecord is null)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await repository.AdjustBalanceIdempotentAsync(
                keyRecord.VirtualKeyGroupId,
                -fallback.Cost,
                fallback.IdempotencyKey,
                $"API usage by virtual key #{fallback.VirtualKeyId} (recovered from fallback queue)",
                "System",
                ReferenceType.VirtualKey,
                fallback.VirtualKeyId.ToString(CultureInfo.InvariantCulture),
                fallback.BillingWindowStartUtc);
            return keyRecord.KeyHash;
#endif
        }

        private async Task<List<SpendClaim>> ClaimPendingSpendAsync(IServer server, IDatabase db, CancellationToken cancellationToken)
        {
            var claims = new List<SpendClaim>();
            claims.AddRange(await ClaimWindowedPendingSpendAsync(server, db, cancellationToken));
            claims.AddRange(await ClaimPendingSpendAsync(
                server, db, _redisKeyPrefix, _processingKeyPrefix, _keyUsagePrefix, _processingKeyUsagePrefix, false, cancellationToken));
            claims.AddRange(await ClaimPendingSpendAsync(
                server, db, _redisUnitsKeyPrefix, _processingUnitsKeyPrefix, _keyUsageUnitsPrefix, _processingKeyUsageUnitsPrefix, true, cancellationToken));
            return claims;
        }

        private async Task<List<SpendClaim>> ClaimWindowedPendingSpendAsync(IServer server, IDatabase db, CancellationToken cancellationToken)
        {
            var claims = new List<SpendClaim>();
            foreach (var pendingKey in ScanKeys(server, $"{_windowedSpendUnitsPrefix}*:window:*", cancellationToken))
            {
                if (!TryParseWindowedKey(pendingKey.ToString(), _windowedSpendUnitsPrefix, out var groupId, out var window, out _))
                    continue;

                var token = window.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
                var claimId = Guid.NewGuid().ToString("N");
                var processingKey = $"{_windowedProcessingUnitsPrefix}{groupId}:window:{token}:claim:{claimId}";
                bool claimed;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    claimed = await db.KeyRenameAsync(pendingKey, processingKey, When.NotExists);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (RedisServerException ex) when (ex.Message.Contains("no such key", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!claimed) continue;

                var usageKeys = new List<RedisKey>();
                foreach (var usageKey in ScanKeys(server, $"{_windowedKeyUsageUnitsPrefix}{groupId}:window:{token}:key:*", cancellationToken))
                {
                    if (!TryParseWindowedKey(usageKey.ToString(), _windowedKeyUsageUnitsPrefix, out _, out _, out var keyId) || !keyId.HasValue)
                        continue;

                    var processingUsageKey = $"{_windowedProcessingKeyUsageUnitsPrefix}{groupId}:window:{token}:key:{keyId.Value}:claim:{claimId}";
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (await db.KeyRenameAsync(usageKey, processingUsageKey, When.NotExists))
                            usageKeys.Add(processingUsageKey);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    catch (RedisServerException ex) when (ex.Message.Contains("no such key", StringComparison.OrdinalIgnoreCase))
                    {
                        // Another flusher claimed it.
                    }
                }

                var claim = await ReadWindowedClaimAsync(db, processingKey, groupId, claimId, window, usageKeys, cancellationToken);
                if (claim != null) claims.Add(claim);
            }

            return claims;
        }

        private async Task<List<SpendClaim>> GetWindowedProcessingClaimsAsync(IServer server, IDatabase db, CancellationToken cancellationToken)
        {
            var claims = new List<SpendClaim>();
            foreach (var processingKey in ScanKeys(server, $"{_windowedProcessingUnitsPrefix}*:window:*:claim:*", cancellationToken))
            {
                if (!TryParseWindowedKey(processingKey.ToString(), _windowedProcessingUnitsPrefix, out var groupId, out var window, out _, out var claimId))
                    continue;

                var token = window.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
                var usageKeys = ScanKeys(server, $"{_windowedProcessingKeyUsageUnitsPrefix}{groupId}:window:{token}:key:*:claim:{claimId}", cancellationToken);
                var claim = await ReadWindowedClaimAsync(db, processingKey, groupId, claimId!, window, usageKeys, cancellationToken);
                if (claim != null) claims.Add(claim);
            }

            return claims;
        }

        private async Task<SpendClaim?> ReadWindowedClaimAsync(
            IDatabase db,
            RedisKey processingKey,
            int groupId,
            string claimId,
            DateTime window,
            List<RedisKey> usageKeys, CancellationToken cancellationToken)
        {
            if (!TryParseRedisAmount(await ReadClaimValueAsync(db, processingKey, cancellationToken), true, out var totalCost))
                return null;

            var usage = new Dictionary<int, decimal>();
            foreach (var usageKey in usageKeys)
            {
                if (TryParseWindowedKey(usageKey.ToString(), _windowedProcessingKeyUsageUnitsPrefix, out _, out _, out var keyId, out _) &&
                    keyId.HasValue &&
                    TryParseRedisAmount(await ReadClaimValueAsync(db, usageKey, cancellationToken), true, out var cost))
                {
                    usage[keyId.Value] = cost;
                }
            }

            return new SpendClaim(groupId, claimId, processingKey, totalCost, usage, usageKeys, window);
        }

        private static bool TryParseWindowedKey(
            string key,
            string prefix,
            out int groupId,
            out DateTime window,
            out int? keyId,
            out string? claimId)
        {
            groupId = default;
            window = default;
            keyId = null;
            claimId = null;
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) return false;

            var parts = key[prefix.Length..].Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[1] != "window" ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out groupId) ||
                !DateTime.TryParseExact(parts[2], "yyyyMMddHH", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out window))
                return false;

            for (var index = 3; index + 1 < parts.Length; index += 2)
            {
                if (parts[index] == "key" && int.TryParse(parts[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedKey))
                    keyId = parsedKey;
                else if (parts[index] == "claim")
                    claimId = parts[index + 1];
            }

            return true;
        }

        private static bool TryParseWindowedKey(
            string key,
            string prefix,
            out int groupId,
            out DateTime window,
            out int? keyId) => TryParseWindowedKey(key, prefix, out groupId, out window, out keyId, out _);

        private async Task<List<SpendClaim>> ClaimPendingSpendAsync(
            IServer server,
            IDatabase db,
            string pendingPrefix,
            string processingPrefix,
            string usagePrefix,
            string processingUsagePrefix,
            bool storedAsUnits, CancellationToken cancellationToken)
        {
            var claims = new List<SpendClaim>();
            var pendingKeys = ScanKeys(server, $"{pendingPrefix}*", cancellationToken);

            foreach (var pendingKey in pendingKeys)
            {
                var groupId = int.Parse(pendingKey.ToString().Substring(pendingPrefix.Length), CultureInfo.InvariantCulture);
                var claimId = Guid.NewGuid().ToString("N");
                var processingKey = $"{processingPrefix}{groupId}:claim:{claimId}";

                bool claimed;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // RENAME is atomic in Redis. Once it completes, concurrent writers create
                    // a fresh pending key and cannot be erased when this claim is acknowledged.
                    claimed = await db.KeyRenameAsync(pendingKey, processingKey, When.NotExists);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (RedisServerException ex) when (ex.Message.Contains("no such key", StringComparison.OrdinalIgnoreCase))
                {
                    // Another flusher claimed the key after the server scan.
                    continue;
                }

                if (!claimed)
                {
                    continue;
                }

                var usageKeys = await ClaimKeyUsageAsync(
                    server, db, groupId, claimId, usagePrefix, processingUsagePrefix, cancellationToken);
                var claim = await ReadClaimAsync(db, processingKey, groupId, claimId, usageKeys, storedAsUnits, cancellationToken);
                if (claim != null)
                {
                    claims.Add(claim);
                }
            }

            return claims;
        }

        private async Task<List<SpendClaim>> GetProcessingClaimsAsync(IServer server, IDatabase db, CancellationToken cancellationToken)
        {
            var claims = new List<SpendClaim>();
            claims.AddRange(await GetWindowedProcessingClaimsAsync(server, db, cancellationToken));
            claims.AddRange(await GetProcessingClaimsAsync(
                server, db, _processingKeyPrefix, _processingKeyUsagePrefix, false, cancellationToken));
            claims.AddRange(await GetProcessingClaimsAsync(
                server, db, _processingUnitsKeyPrefix, _processingKeyUsageUnitsPrefix, true, cancellationToken));
            return claims;
        }

        private async Task<List<SpendClaim>> GetProcessingClaimsAsync(
            IServer server,
            IDatabase db,
            string processingPrefix,
            string processingUsagePrefix,
            bool storedAsUnits, CancellationToken cancellationToken)
        {
            var claims = new List<SpendClaim>();
            var processingKeys = ScanKeys(server, $"{processingPrefix}*", cancellationToken);

            foreach (var processingKey in processingKeys)
            {
                if (!TryParseProcessingSpendKey(processingKey.ToString(), processingPrefix, out var groupId, out var claimId))
                {
                    _logger.LogWarning("Ignoring malformed batch spend claim key {ClaimKey}", processingKey);
                    continue;
                }

                var usageKeys = ScanKeys(server, $"{processingUsagePrefix}{groupId}:key:*:claim:{claimId}", cancellationToken);
                var claim = await ReadClaimAsync(db, processingKey, groupId, claimId, usageKeys, storedAsUnits, cancellationToken);
                if (claim != null)
                {
                    claims.Add(claim);
                }
            }

            return claims;
        }

        private async Task<List<RedisKey>> ClaimKeyUsageAsync(
            IServer server,
            IDatabase db,
            int groupId,
            string claimId,
            string usagePrefix,
            string processingUsagePrefix, CancellationToken cancellationToken)
        {
            var claimedKeys = new List<RedisKey>();
            var pendingUsageKeys = ScanKeys(server, $"{usagePrefix}{groupId}:key:*", cancellationToken);

            foreach (var pendingUsageKey in pendingUsageKeys)
            {
                if (!TryParsePendingKeyUsageKey(pendingUsageKey.ToString(), out var keyId))
                {
                    continue;
                }

                var processingUsageKey = $"{processingUsagePrefix}{groupId}:key:{keyId}:claim:{claimId}";
                bool claimed;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    claimed = await db.KeyRenameAsync(pendingUsageKey, processingUsageKey, When.NotExists);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (RedisServerException ex) when (ex.Message.Contains("no such key", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (claimed)
                {
                    claimedKeys.Add(processingUsageKey);
                }
            }

            return claimedKeys;
        }

        private async Task<SpendClaim?> ReadClaimAsync(
            IDatabase db,
            RedisKey processingKey,
            int groupId,
            string claimId,
            List<RedisKey> usageKeys,
            bool storedAsUnits, CancellationToken cancellationToken)
        {
            var value = await ReadClaimValueAsync(db, processingKey, cancellationToken);
            if (!TryParseRedisAmount(value, storedAsUnits, out var totalCost))
            {
                _logger.LogError("Batch spend claim {ClaimId} for group {GroupId} has an invalid amount", claimId, groupId);
                return null;
            }

            var keyUsageByKeyId = new Dictionary<int, decimal>();
            foreach (var usageKey in usageKeys)
            {
                if (!TryParseProcessingKeyUsageKey(usageKey.ToString(), out var keyId) ||
                    !TryParseRedisAmount(await ReadClaimValueAsync(db, usageKey, cancellationToken), storedAsUnits, out var cost))
                {
                    continue;
                }

                keyUsageByKeyId[keyId] = cost;
            }

            return new SpendClaim(groupId, claimId, processingKey, totalCost, keyUsageByKeyId, usageKeys, null);
        }

        private async Task DeleteClaimAsync(IDatabase db, SpendClaim claim, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keys = claim.ProcessingKeyUsageKeys
                .Append(claim.ProcessingSpendKey)
                .ToList();
            if (!claim.BillingWindowStartUtc.HasValue)
            {
                await db.KeyDeleteAsync(keys.ToArray());
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            // Acknowledge the durable claim and reduce the reservation-facing total
            // atomically. On retry, the total can only be reduced while the processing
            // claim exists, so a crash after the database commit cannot double-decrement.
            const string script = """
                if redis.call('EXISTS', KEYS[#KEYS - 1]) == 0 then
                    return 0
                end
                for index = 1, #KEYS - 1 do
                    redis.call('DEL', KEYS[index])
                end
                local remaining = redis.call('INCRBY', KEYS[#KEYS], -tonumber(ARGV[1]))
                if remaining <= 0 then
                    redis.call('DEL', KEYS[#KEYS])
                end
                return 1
                """;
            keys.Add($"{_windowedPendingTotalUnitsPrefix}{claim.GroupId}");
            await db.ScriptEvaluateAsync(
                script,
                keys.ToArray(),
                new RedisValue[] { ToSpendUnits(claim.TotalCost) });
            cancellationToken.ThrowIfCancellationRequested();
        }

        private static List<RedisKey> ScanKeys(IServer server, string pattern, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keys = new List<RedisKey>();
            using var enumerator = server.Keys(pattern: pattern).GetEnumerator();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!enumerator.MoveNext()) break;
                cancellationToken.ThrowIfCancellationRequested();
                keys.Add(enumerator.Current);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return keys;
        }

        private static async Task<RedisValue> ReadClaimValueAsync(IDatabase db, RedisKey key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await db.StringGetAsync(key);
            cancellationToken.ThrowIfCancellationRequested();
            return value;
        }

        private static bool TryParseProcessingSpendKey(
            string key,
            string processingPrefix,
            out int groupId,
            out string claimId)
        {
            groupId = default;
            claimId = string.Empty;
            if (!key.StartsWith(processingPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var parts = key[processingPrefix.Length..].Split(":claim:", 2, StringSplitOptions.None);
            return parts.Length == 2 &&
                   int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out groupId) &&
                   !string.IsNullOrWhiteSpace(claimId = parts[1]);
        }

        private bool TryParsePendingKeyUsageKey(string key, out int keyId)
        {
            keyId = default;
            var parts = key.Split(':');
            return parts.Length == 5 &&
                   int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out keyId);
        }

        private bool TryParseProcessingKeyUsageKey(string key, out int keyId)
        {
            keyId = default;
            var parts = key.Split(':');
            return parts.Length == 7 &&
                   int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out keyId);
        }

        private static bool TryParseRedisAmount(RedisValue value, bool storedAsUnits, out decimal amount)
        {
            amount = default;
            if (!value.HasValue)
            {
                return false;
            }

            if (storedAsUnits)
            {
                return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var units) &&
                       (amount = units / SpendUnitScale) >= 0;
            }

            return decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out amount) &&
                   amount >= 0;
        }

        private sealed record SpendClaim(
            int GroupId,
            string ClaimId,
            RedisKey ProcessingSpendKey,
            decimal TotalCost,
            Dictionary<int, decimal> KeyUsageByKeyId,
            List<RedisKey> ProcessingKeyUsageKeys,
            DateTime? BillingWindowStartUtc);

        /// <summary>
        /// Builds a human-readable description of API usage for a given group,
        /// including which virtual keys contributed to the spend.
        /// </summary>
        /// <param name="keyUsageByKeyId">Dictionary of virtual key ID to cost for this claim.</param>
        /// <returns>A description string such as "API usage by virtual key #5"</returns>
        private static string BuildUsageDescription(Dictionary<int, decimal> keyUsageByKeyId)
        {
            if (keyUsageByKeyId.Count == 0)
            {
                return "API usage";
            }

            var keyIds = keyUsageByKeyId.Keys.ToList();
            if (keyIds.Count == 1)
            {
                return $"API usage by virtual key #{keyIds[0]}";
            }

            return $"API usage by {keyIds.Count} virtual keys";
        }

        /// <summary>
        /// Get statistics about the batching service
        /// </summary>
        /// <returns>Dictionary with service statistics</returns>
        public async Task<Dictionary<string, object>> GetStatisticsAsync()
        {
            // Snapshot the process-local recovery backlog even when Redis is unavailable.
            var fallbackUpdates = _fallbackQueue.ToArray();
            var fallbackPendingCost = fallbackUpdates.Sum(update => update.Cost);
            try
            {
                var redis = await _redisConnectionFactory.GetConnectionAsync();
                var db = redis.GetDatabase();
                var server = redis.GetServer(redis.GetEndPoints()[0]);

                // One total key is maintained per group by the current windowed write path.
                var pattern = $"{_windowedPendingTotalUnitsPrefix}*";
                var keys = server.Keys(pattern: pattern).ToList();
                var values = keys.Count == 0
                    ? Array.Empty<RedisValue>()
                    : await db.StringGetAsync(keys.ToArray());
                var totalPending = values.Sum(ParseRedisUnits);

                return new Dictionary<string, object>
                {
                    ["PendingUpdates"] = keys.Count(),
                    ["TotalPendingCost"] = totalPending,
                    ["FlushIntervalSeconds"] = _flushInterval.TotalSeconds,
                    ["PendingSpendKeysExpire"] = false,
                    ["RedisTtlHours"] = _reservationTtl.TotalHours,
                    ["FallbackPendingUpdates"] = fallbackUpdates.Length,
                    ["FallbackPendingCost"] = fallbackPendingCost,
                    ["ConfiguredFlushInterval"] = _options.FlushIntervalSeconds,
                    ["MinimumInterval"] = _options.MinimumIntervalSeconds,
                    ["MaximumInterval"] = _options.MaximumIntervalSeconds
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting statistics");
                return new Dictionary<string, object>
                {
                    ["Error"] = ex.Message,
                    ["FlushIntervalSeconds"] = _flushInterval.TotalSeconds,
                    ["FallbackPendingUpdates"] = fallbackUpdates.Length,
                    ["FallbackPendingCost"] = fallbackPendingCost,
                    ["ConfiguredFlushInterval"] = _options.FlushIntervalSeconds
                };
            }
        }
    }
}
