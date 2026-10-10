using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Tests.Helpers;
using ConduitLLM.Tests.TestInfrastructure;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Moq;

using StackExchange.Redis;

namespace ConduitLLM.Tests.Configuration.Services
{

    /// <summary>
    /// Unit tests for the BatchSpendUpdateService to ensure correct balance tracking
    /// </summary>
    [Trait("Category", "Unit")]
    [Trait("Component", "BatchSpendUpdateService")]
    public class BatchSpendUpdateServiceTests : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
        private readonly TestRedisConnectionFactory _testRedisFactory;
        private readonly Mock<ILogger<BatchSpendUpdateService>> _mockLogger;
        private readonly Mock<IConnectionMultiplexer> _mockRedisConnection;
        private readonly Mock<IDatabase> _mockRedisDb;
        private readonly Mock<IServer> _mockRedisServer;
        private readonly Mock<IBillingAlertingService> _mockAlertingService;
        private readonly BatchSpendUpdateService _service;
        private readonly IConfigurationDbContext _dbContext;
        private readonly ConduitDbContext _concreteDbContext;
        private readonly Mock<IVirtualKeyGroupRepository> _mockGroupRepository;
        private readonly SqliteTestDatabase _database;

        public BatchSpendUpdateServiceTests()
        {
            _database = new SqliteTestDatabase();
            _concreteDbContext = _database.CreateContext();
            _dbContext = _concreteDbContext;

            // Setup Redis mocks
            _mockRedisDb = new Mock<IDatabase>();
            _mockRedisServer = new Mock<IServer>();
            _mockRedisConnection = new Mock<IConnectionMultiplexer>();
            _testRedisFactory = new TestRedisConnectionFactory(_mockRedisConnection.Object);
            
            var endPoint = new System.Net.DnsEndPoint("localhost", 6379);
            _mockRedisConnection.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
                .Returns(_mockRedisDb.Object);
            _mockRedisConnection.Setup(x => x.GetServer(It.IsAny<System.Net.EndPoint>(), It.IsAny<object>()))
                .Returns(_mockRedisServer.Object);
            _mockRedisConnection.Setup(x => x.GetEndPoints(It.IsAny<bool>()))
                .Returns(new[] { endPoint });
            
            // Redis factory is now handled by TestRedisConnectionFactory

            // Setup service provider
            _mockGroupRepository = new Mock<IVirtualKeyGroupRepository>();
            
            var services = new ServiceCollection();
            services.AddSingleton<IConfigurationDbContext>(_dbContext);
            services.AddSingleton(_mockGroupRepository.Object);
            services.AddLogging();
            
            _serviceProvider = services.BuildServiceProvider();
            
            _mockScopeFactory = new Mock<IServiceScopeFactory>();
            var mockScope = new Mock<IServiceScope>();
            mockScope.Setup(x => x.ServiceProvider).Returns(_serviceProvider);
            _mockScopeFactory.Setup(x => x.CreateScope()).Returns(mockScope.Object);
            
            _mockLogger = new Mock<ILogger<BatchSpendUpdateService>>();
            _mockAlertingService = new Mock<IBillingAlertingService>();
            
            var batchOptions = Microsoft.Extensions.Options.Options.Create(new BatchSpendingOptions()); // Use defaults
            _service = new BatchSpendUpdateService(
                _mockScopeFactory.Object,
                _testRedisFactory,
                batchOptions,
                _mockLogger.Object,
                _mockAlertingService.Object);
        }

        [Fact]
        public async Task FlushPendingUpdates_ShouldCreateSingleTransactionWithCorrectBalance()
        {
            // Arrange
            var groupId = 1;
            var initialBalance = 100m;
            var usageCost = 5.25m;
            var expectedBalance = initialBalance - usageCost;
            
            // Setup virtual key group in database
            var group = new VirtualKeyGroup
            {
                Id = groupId,
                GroupName = "Test Group",
                Balance = initialBalance,
                LifetimeCreditsAdded = initialBalance,
                LifetimeSpent = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.VirtualKeyGroups.Add(group);
            
            // Setup virtual key
            var virtualKey = new VirtualKey
            {
                Id = 1,
                VirtualKeyGroupId = groupId,
                KeyName = "Test Key",
                KeyHash = "testhash123",
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow
            };
            _dbContext.VirtualKeys.Add(virtualKey);
            await _dbContext.SaveChangesAsync();
            
            SetupPendingSpendClaim(groupId, usageCost, new Dictionary<int, decimal> { [1] = usageCost });
            
            // Setup the repository mock to adjust balance correctly
            _mockGroupRepository.Setup(x => x.AdjustBalanceIdempotentAsync(
                groupId, 
                -usageCost, 
                It.Is<string>(key => key.StartsWith("batch-spend:")),
                It.IsAny<string>(), 
                "System",
                ReferenceType.System,
                It.IsAny<string>()))
                .ReturnsAsync(new BalanceAdjustmentResult(expectedBalance, usageCost, Applied: true))
                .Callback<int, decimal, string, string, string, ReferenceType, string>((gId, amount, _, desc, initiatedBy, _, _) =>
                {
                    // Simulate what the real repository does
                    group.Balance += amount;
                    group.LifetimeSpent += Math.Abs(amount);
                    group.UpdatedAt = DateTime.UtcNow;
                    
                    var transaction = new VirtualKeyGroupTransaction
                    {
                        VirtualKeyGroupId = gId,
                        TransactionType = TransactionType.Debit,
                        Amount = Math.Abs(amount),
                        BalanceAfter = group.Balance,
                        Description = desc,
                        InitiatedBy = initiatedBy,
                        CreatedAt = DateTime.UtcNow
                    };
                    _dbContext.VirtualKeyGroupTransactions.Add(transaction);
                    _concreteDbContext.SaveChanges();
                });
            
            // Act
            var result = await _service.FlushPendingUpdatesAsync();
            
            // Assert
            Assert.Equal(1, result); // One group was updated
            
            // Verify only one transaction was created
            var transactions = await _dbContext.VirtualKeyGroupTransactions
                .Where(t => t.VirtualKeyGroupId == groupId)
                .ToListAsync();
            
            Assert.Single(transactions);
            
            var transaction = transactions.First();
            Assert.Equal(TransactionType.Debit, transaction.TransactionType);
            Assert.Equal(usageCost, transaction.Amount);
            Assert.Equal(expectedBalance, transaction.BalanceAfter); // This is the key assertion
            Assert.Contains("API usage", transaction.Description);
            Assert.Equal("System", transaction.InitiatedBy);
            
            // Verify the group balance was updated correctly
            var updatedGroup = await _dbContext.VirtualKeyGroups.FindAsync(groupId);
            Assert.Equal(expectedBalance, updatedGroup.Balance);
            Assert.Equal(usageCost, updatedGroup.LifetimeSpent);

            _mockRedisDb.Verify(x => x.KeyExpireAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<ExpireWhen>(),
                It.IsAny<CommandFlags>()), Times.Never);
        }

        [Fact]
        public async Task FlushPendingUpdates_WithMultipleKeys_ShouldCreateSingleTransactionWithAggregatedUsage()
        {
            // Arrange
            var groupId = 1;
            var initialBalance = 100m;
            var key1Usage = 3.50m;
            var key2Usage = 2.75m;
            var totalUsage = key1Usage + key2Usage;
            var expectedBalance = initialBalance - totalUsage;
            
            // Setup virtual key group
            var group = new VirtualKeyGroup
            {
                Id = groupId,
                GroupName = "Test Group",
                Balance = initialBalance,
                LifetimeCreditsAdded = initialBalance,
                LifetimeSpent = 0
            };
            _dbContext.VirtualKeyGroups.Add(group);
            
            // Setup virtual keys
            _dbContext.VirtualKeys.AddRange(
                new VirtualKey { Id = 1, VirtualKeyGroupId = groupId, KeyHash = "key1" },
                new VirtualKey { Id = 2, VirtualKeyGroupId = groupId, KeyHash = "key2" }
            );
            await _dbContext.SaveChangesAsync();
            
            SetupPendingSpendClaim(groupId, totalUsage, new Dictionary<int, decimal>
            {
                [1] = key1Usage,
                [2] = key2Usage
            });
            
            // Setup repository mock
            _mockGroupRepository.Setup(x => x.AdjustBalanceIdempotentAsync(
                groupId, 
                -totalUsage, 
                It.Is<string>(key => key.StartsWith("batch-spend:")),
                It.Is<string>(s => s.Contains("2 virtual keys")), 
                "System",
                ReferenceType.System,
                It.IsAny<string>()))
                .ReturnsAsync(new BalanceAdjustmentResult(expectedBalance, totalUsage, Applied: true))
                .Callback<int, decimal, string, string, string, ReferenceType, string>((gId, amount, _, desc, initiatedBy, _, _) =>
                {
                    group.Balance += amount;
                    group.LifetimeSpent += Math.Abs(amount);
                    
                    var transaction = new VirtualKeyGroupTransaction
                    {
                        VirtualKeyGroupId = gId,
                        TransactionType = TransactionType.Debit,
                        Amount = Math.Abs(amount),
                        BalanceAfter = group.Balance,
                        Description = desc,
                        InitiatedBy = initiatedBy,
                        CreatedAt = DateTime.UtcNow
                    };
                    _dbContext.VirtualKeyGroupTransactions.Add(transaction);
                    _concreteDbContext.SaveChanges();
                });
            
            // Act
            var result = await _service.FlushPendingUpdatesAsync();
            
            // Assert
            Assert.Equal(1, result);
            
            var transactions = await _dbContext.VirtualKeyGroupTransactions
                .Where(t => t.VirtualKeyGroupId == groupId)
                .ToListAsync();
            
            // Should only have ONE transaction for the aggregated usage
            Assert.Single(transactions);
            
            var transaction = transactions.First();
            Assert.Equal(totalUsage, transaction.Amount);
            Assert.Equal(expectedBalance, transaction.BalanceAfter);
            Assert.Contains("2 virtual keys", transaction.Description);
        }

        [Fact]
        public async Task QueueSpendUpdateAsync_ShouldAccumulateSpendWithoutExpiration()
        {
            // Arrange
            var virtualKeyId = 1;
            var groupId = 1;
            var cost = 0.12345678m;
            var billedAt = new DateTime(2026, 7, 19, 22, 42, 0, DateTimeKind.Utc);
            const long expectedUnits = 12_345_678;
            
            // Setup virtual key in database
            _dbContext.VirtualKeyGroups.Add(new VirtualKeyGroup
            {
                Id = groupId,
                GroupName = "Queue spend group"
            });
            var virtualKey = new VirtualKey
            {
                Id = virtualKeyId,
                VirtualKeyGroupId = groupId,
                KeyHash = "testkey"
            };
            _dbContext.VirtualKeys.Add(virtualKey);
            await _dbContext.SaveChangesAsync();
            
            // Setup Redis mocks
            _mockRedisDb.Setup(x => x.ScriptEvaluateAsync(
                    It.IsAny<string>(),
                    It.Is<RedisKey[]>(keys =>
                        keys.Length == 3 &&
                        keys[0] == $"pending_spend_window_units:group:{groupId}:window:2026071922" &&
                        keys[1] == $"pending_spend_window_total_units:group:{groupId}" &&
                        keys[2] == $"key_usage_window_units:group:{groupId}:window:2026071922:key:{virtualKeyId}"),
                    It.Is<RedisValue[]>(values => values.Length == 1 && values[0] == expectedUnits),
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync(RedisResult.Create((RedisValue)1));
            
            // Act
            await _service.QueueSpendUpdateAsync(virtualKeyId, cost, billedAt);
            
            // Assert
            _mockRedisDb.Verify(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()), Times.Once);

            _mockRedisDb.Verify(x => x.KeyExpireAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<ExpireWhen>(),
                It.IsAny<CommandFlags>()), Times.Never);
            
        }

        [Fact]
        public async Task QueueSpendUpdateAsync_WithRuntimeStore_DoesNotResolveEfServices()
        {
            const int virtualKeyId = 41;
            const int groupId = 13;
            const decimal cost = 0.5m;
            var billedAt = new DateTime(2026, 8, 27, 22, 0, 0, DateTimeKind.Utc);
            var runtimeStore = new Mock<IVirtualKeyRuntimeStore>(MockBehavior.Strict);
            runtimeStore.Setup(store => store.GetByIdAsync(
                    virtualKeyId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VirtualKeyRuntimeRecord
                {
                    Id = virtualKeyId,
                    KeyHash = "native-hash-41",
                    VirtualKeyGroupId = groupId,
                    Group = new VirtualKeyGroupRuntimeRecord
                    {
                        Id = groupId,
                        Balance = 100m
                    }
                });
            _mockRedisDb.Setup(database => database.ScriptEvaluateAsync(
                    It.IsAny<string>(),
                    It.Is<RedisKey[]>(keys => keys.All(key => key.ToString().Contains($"group:{groupId}"))),
                    It.IsAny<RedisValue[]>(),
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync(RedisResult.Create((RedisValue)1));
            using var service = new BatchSpendUpdateService(
                _mockScopeFactory.Object,
                _testRedisFactory,
                Microsoft.Extensions.Options.Options.Create(new BatchSpendingOptions()),
                _mockLogger.Object,
                _mockAlertingService.Object,
                runtimeStore: runtimeStore.Object);

            await service.QueueSpendUpdateAsync(virtualKeyId, cost, billedAt);

            runtimeStore.VerifyAll();
            _mockScopeFactory.Verify(factory => factory.CreateScope(), Times.Never);
        }

        [Fact]
        public async Task FlushPendingUpdates_WithWindowedClaim_ShouldPersistWindowAndAcknowledgeAtomically()
        {
            // Arrange
            const int groupId = 3;
            const decimal usageCost = 1.25m;
            const long usageUnits = 125_000_000;
            var billingWindow = new DateTime(2026, 7, 19, 22, 0, 0, DateTimeKind.Utc);
            RedisKey pendingKey = $"pending_spend_window_units:group:{groupId}:window:2026071922";

            SetupServerKeys("processing_spend_window_units:group:*:window:*:claim:*", Array.Empty<RedisKey>());
            SetupServerKeys("pending_spend_window_units:group:*:window:*", new[] { pendingKey });
            SetupServerKeys(
                $"key_usage_window_units:group:{groupId}:window:2026071922:key:*",
                Array.Empty<RedisKey>());

            _mockRedisDb.Setup(x => x.KeyRenameAsync(
                    pendingKey,
                    It.Is<RedisKey>(key => key.ToString().StartsWith(
                        $"processing_spend_window_units:group:{groupId}:window:2026071922:claim:")),
                    When.NotExists,
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);
            _mockRedisDb.Setup(x => x.StringGetAsync(
                    It.Is<RedisKey>(key => key.ToString().StartsWith(
                        $"processing_spend_window_units:group:{groupId}:window:2026071922:claim:")),
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync(new RedisValue(usageUnits.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            _mockRedisDb.Setup(x => x.ScriptEvaluateAsync(
                    It.IsAny<string>(),
                    It.Is<RedisKey[]>(keys =>
                        keys.Any(key => key.ToString().StartsWith(
                            $"processing_spend_window_units:group:{groupId}:window:2026071922:claim:")) &&
                        keys[keys.Length - 1] == $"pending_spend_window_total_units:group:{groupId}"),
                    It.Is<RedisValue[]>(values => values.Length == 1 && values[0] == usageUnits),
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync(RedisResult.Create((RedisValue)1));

            _mockGroupRepository.Setup(x => x.AdjustBalanceIdempotentAsync(
                    groupId,
                    -usageCost,
                    It.Is<string>(key => key.StartsWith("batch-spend:")),
                    "API usage",
                    "System",
                    ReferenceType.System,
                    It.IsAny<string>(),
                    billingWindow))
                .ReturnsAsync(new BalanceAdjustmentResult(98.75m, usageCost, Applied: true));

            // Act
            var result = await _service.FlushPendingUpdatesAsync();

            // Assert
            Assert.Equal(1, result);
            _mockGroupRepository.VerifyAll();
            _mockRedisDb.Verify(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()), Times.Once);
        }

        [Fact]
        public async Task FlushPendingUpdates_WithNoData_ShouldReturnZero()
        {
            // Arrange
            _mockRedisServer.Setup(x => x.Keys(
                It.IsAny<int>(), 
                It.IsAny<RedisValue>(), 
                It.IsAny<int>(), 
                It.IsAny<long>(), 
                It.IsAny<int>(), 
                It.IsAny<CommandFlags>()))
                .Returns(Array.Empty<RedisKey>());
            
            // Act
            var result = await _service.FlushPendingUpdatesAsync();
            
            // Assert
            Assert.Equal(0, result);
            
            // Verify no transactions were created
            var transactions = await _dbContext.VirtualKeyGroupTransactions.ToListAsync();
            Assert.Empty(transactions);
        }

        [Fact]
        public async Task GetPendingSpendAsync_ShouldUseGroupNamespaceAndIncludeReservations()
        {
            // Arrange
            const int virtualKeyId = 17;
            const int groupId = 42;
            _dbContext.VirtualKeyGroups.Add(new VirtualKeyGroup
            {
                Id = groupId,
                GroupName = "Pending spend group"
            });
            _dbContext.VirtualKeys.Add(new VirtualKey
            {
                Id = virtualKeyId,
                VirtualKeyGroupId = groupId,
                KeyHash = "pending-spend-test"
            });
            await _dbContext.SaveChangesAsync();

            _mockRedisDb.Setup(x => x.StringGetAsync(
                    It.Is<RedisKey[]>(keys =>
                        keys.Length == 4 &&
                        keys[0] == $"pending_spend:group:{groupId}" &&
                        keys[1] == $"pending_spend_units:group:{groupId}" &&
                        keys[2] == $"reserved_spend:group:{groupId}" &&
                        keys[3] == $"pending_spend_window_total_units:group:{groupId}"),
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync(new RedisValue[] { "3.25", "125000000", "1.75", "0" });

            // Act
            var pendingSpend = await _service.GetPendingSpendAsync(virtualKeyId);

            // Assert
            Assert.Equal(6.25m, pendingSpend);
        }

        [Fact]
        public async Task TryReserveSpendAsync_ShouldAtomicallyCheckPendingAndReservedSpend()
        {
            // Arrange
            const int virtualKeyId = 7;
            const int groupId = 8;
            var group = new VirtualKeyGroup
            {
                Id = groupId,
                GroupName = "Reservation Test",
                Balance = 10m
            };
            _dbContext.VirtualKeyGroups.Add(group);
            _dbContext.VirtualKeys.Add(new VirtualKey
            {
                Id = virtualKeyId,
                VirtualKeyGroupId = groupId,
                KeyHash = "reservation-test"
            });
            await _dbContext.SaveChangesAsync();

            _mockRedisDb.Setup(x => x.ScriptEvaluateAsync(
                    It.IsAny<string>(),
                    It.Is<RedisKey[]>(keys =>
                        keys[0] == $"pending_spend:group:{groupId}" &&
                        keys[1] == $"reserved_spend:group:{groupId}" &&
                        keys[2] == $"spend_reservations:group:{groupId}" &&
                        keys[3] == $"spend_reservation_expiry:group:{groupId}" &&
                        keys[4] == $"pending_spend_units:group:{groupId}" &&
                        keys[5] == $"pending_spend_window_total_units:group:{groupId}" &&
                        keys[6] == $"spend_reservations_started:group:{groupId}" &&
                        keys[7] == $"spend_reservations_settled:group:{groupId}"),
                    It.Is<RedisValue[]>(values =>
                        values[0] == "10" && values[1] == "4.5" && values[2] == "request-123"),
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync(RedisResult.Create((RedisValue)1));

            // Act
            var reserved = await _service.TryReserveSpendAsync(virtualKeyId, 4.5m, "request-123");

            // Assert
            Assert.True(reserved);
        }

        [Fact]
        public async Task MarkReservationStarted_MovesReservationToNonExpiringState()
        {
            const int virtualKeyId = 17;
            const int groupId = 18;
            _dbContext.VirtualKeyGroups.Add(new VirtualKeyGroup
            {
                Id = groupId,
                GroupName = "Started Reservation Test",
                Balance = 10m
            });
            _dbContext.VirtualKeys.Add(new VirtualKey
            {
                Id = virtualKeyId,
                VirtualKeyGroupId = groupId,
                KeyHash = "started-reservation-test"
            });
            await _dbContext.SaveChangesAsync();
            _mockRedisDb.Setup(x => x.ScriptEvaluateAsync(
                    It.IsAny<string>(),
                    It.Is<RedisKey[]>(keys =>
                        keys[0] == $"spend_reservations:group:{groupId}" &&
                        keys[1] == $"spend_reservation_expiry:group:{groupId}" &&
                        keys[2] == $"spend_reservations_started:group:{groupId}" &&
                        keys[3] == $"spend_reservations_settled:group:{groupId}"),
                    It.Is<RedisValue[]>(values => values[0] == "request-started"),
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync(RedisResult.Create((RedisValue)1));

            var started = await _service.MarkSpendReservationInvocationStartedAsync(
                virtualKeyId,
                "request-started");

            Assert.True(started);
        }

        [Fact]
        public async Task SettleReservation_AtomicallyQueuesActualSpendAndReportsOverEstimate()
        {
            const int virtualKeyId = 27;
            const int groupId = 28;
            _dbContext.VirtualKeyGroups.Add(new VirtualKeyGroup
            {
                Id = groupId,
                GroupName = "Settlement Test",
                Balance = 10m
            });
            _dbContext.VirtualKeys.Add(new VirtualKey
            {
                Id = virtualKeyId,
                VirtualKeyGroupId = groupId,
                KeyHash = "settlement-test"
            });
            await _dbContext.SaveChangesAsync();
            _mockRedisDb.Setup(x => x.ScriptEvaluateAsync(
                    It.IsAny<string>(),
                    It.Is<RedisKey[]>(keys =>
                        keys[0] == $"spend_reservations:group:{groupId}" &&
                        keys[2] == $"spend_reservations_started:group:{groupId}" &&
                        keys[3] == $"spend_reservations_settled:group:{groupId}" &&
                        keys[4] == $"reserved_spend:group:{groupId}" &&
                        keys[6] == $"pending_spend_window_total_units:group:{groupId}" &&
                        keys[7].ToString().Contains($"key:{virtualKeyId}")),
                    It.Is<RedisValue[]>(values =>
                        values[0] == "request-settle" &&
                        values[1] == 250000000 &&
                        values[2] == "2.5"),
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync(RedisResult.Create((RedisValue)3));

            var result = await _service.SettleSpendReservationAsync(
                virtualKeyId,
                "request-settle",
                2.5m);

            Assert.Equal(SpendReservationSettlementStatus.SettledOverEstimate, result.Status);
            Assert.Equal(2.5m, result.ActualAmount);
        }

        [Fact]
        public async Task FlushPendingUpdates_WhenDatabaseWriteFails_LeavesDurableClaimForRetry()
        {
            // Arrange
            const int groupId = 7;
            const decimal usageCost = 12.50m;
            SetupPendingSpendClaim(groupId, usageCost, new Dictionary<int, decimal>());

            _mockGroupRepository.Setup(x => x.AdjustBalanceIdempotentAsync(
                    groupId,
                    -usageCost,
                    It.Is<string>(key => key.StartsWith("batch-spend:")),
                    It.IsAny<string>(),
                    "System",
                    ReferenceType.System,
                    It.IsAny<string>()))
                .ThrowsAsync(new InvalidOperationException("database unavailable"));

            // Act
            var act = () => _service.FlushPendingUpdatesAsync();

            // Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(act);
            Assert.Equal("database unavailable", exception.Message);
            _mockRedisDb.Verify(x => x.KeyDeleteAsync(
                It.IsAny<RedisKey[]>(),
                It.IsAny<CommandFlags>()), Times.Never);
        }

        [Fact]
        public async Task FlushPendingUpdates_WithClaimFromPreviousAttempt_RetriesIdempotentlyThenDeletesClaim()
        {
            // Arrange - this models a process crash after the DB commit but before
            // Redis acknowledgement. The repository reports the ledger key as a duplicate.
            const int groupId = 9;
            const decimal usageCost = 4.75m;
            const string claimId = "recovered-claim";
            RedisKey processingKey = $"processing_spend:group:{groupId}:claim:{claimId}";

            SetupServerKeys("processing_spend:group:*", new[] { processingKey });
            SetupServerKeys("pending_spend:group:*", Array.Empty<RedisKey>());
            SetupServerKeys(
                $"processing_key_usage:group:{groupId}:key:*:claim:{claimId}",
                Array.Empty<RedisKey>());
            _mockRedisDb.Setup(x => x.StringGetAsync(processingKey, It.IsAny<CommandFlags>()))
                .ReturnsAsync(new RedisValue(usageCost.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            _mockRedisDb.Setup(x => x.KeyDeleteAsync(
                    It.Is<RedisKey[]>(keys => keys.Length == 1 && keys[0] == processingKey),
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync(1L);

            _mockGroupRepository.Setup(x => x.AdjustBalanceIdempotentAsync(
                    groupId,
                    -usageCost,
                    $"batch-spend:{claimId}",
                    "API usage",
                    "System",
                    ReferenceType.System,
                    claimId))
                .ReturnsAsync(new BalanceAdjustmentResult(95.25m, usageCost, Applied: false));

            // Act
            var result = await _service.FlushPendingUpdatesAsync();

            // Assert
            Assert.Equal(1, result);
            _mockGroupRepository.VerifyAll();
            _mockRedisDb.Verify(x => x.KeyDeleteAsync(
                It.Is<RedisKey[]>(keys => keys.Length == 1 && keys[0] == processingKey),
                It.IsAny<CommandFlags>()), Times.Once);
        }

        [Fact]
        public async Task FlushPendingUpdates_WithRuntimeStore_DoesNotResolveEfServices()
        {
            const int groupId = 13;
            const int virtualKeyId = 41;
            const decimal usageCost = 2.75m;
            SetupPendingSpendClaim(groupId, usageCost, new Dictionary<int, decimal>
            {
                [virtualKeyId] = usageCost
            });
            var runtimeStore = new Mock<IVirtualKeyRuntimeStore>(MockBehavior.Strict);
            runtimeStore.Setup(store => store.AdjustBalanceAsync(
                    It.Is<VirtualKeyBalanceAdjustment>(adjustment =>
                        adjustment.GroupId == groupId &&
                        adjustment.Amount == -usageCost &&
                        adjustment.IdempotencyKey!.StartsWith("batch-spend:")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VirtualKeyBalanceAdjustmentResult(97.25m, usageCost, true));
            runtimeStore.Setup(store => store.GetKeyHashesByGroupIdAsync(
                    groupId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(["native-hash-41"]);
            using var service = new BatchSpendUpdateService(
                _mockScopeFactory.Object,
                _testRedisFactory,
                Microsoft.Extensions.Options.Options.Create(new BatchSpendingOptions()),
                _mockLogger.Object,
                _mockAlertingService.Object,
                runtimeStore: runtimeStore.Object);

            var updatedHashes = Array.Empty<string>();
            service.SpendUpdatesCompleted += hashes => updatedHashes = hashes;
            var result = await service.FlushPendingUpdatesAsync();

            Assert.Equal(1, result);
            Assert.Equal(["native-hash-41"], updatedHashes);
            runtimeStore.VerifyAll();
            _mockScopeFactory.Verify(factory => factory.CreateScope(), Times.Never);
        }

        private void SetupPendingSpendClaim(
            int groupId,
            decimal totalCost,
            IReadOnlyDictionary<int, decimal> keyUsage)
        {
            RedisKey pendingKey = $"pending_spend:group:{groupId}";
            SetupServerKeys("processing_spend:group:*", Array.Empty<RedisKey>());
            SetupServerKeys("pending_spend:group:*", new[] { pendingKey });
            SetupServerKeys(
                $"key_usage:group:{groupId}:key:*",
                keyUsage.Keys.Select(keyId => (RedisKey)$"key_usage:group:{groupId}:key:{keyId}").ToArray());

            _mockRedisDb.Setup(x => x.KeyRenameAsync(
                    pendingKey,
                    It.Is<RedisKey>(key => key.ToString().StartsWith($"processing_spend:group:{groupId}:claim:")),
                    When.NotExists,
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync(true);
            _mockRedisDb.Setup(x => x.StringGetAsync(
                    It.Is<RedisKey>(key => key.ToString().StartsWith($"processing_spend:group:{groupId}:claim:")),
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync(new RedisValue(totalCost.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            foreach (var (keyId, cost) in keyUsage)
            {
                RedisKey pendingUsageKey = $"key_usage:group:{groupId}:key:{keyId}";
                _mockRedisDb.Setup(x => x.KeyRenameAsync(
                        pendingUsageKey,
                        It.Is<RedisKey>(key => key.ToString().StartsWith($"processing_key_usage:group:{groupId}:key:{keyId}:claim:")),
                        When.NotExists,
                        It.IsAny<CommandFlags>()))
                    .ReturnsAsync(true);
                _mockRedisDb.Setup(x => x.StringGetAsync(
                        It.Is<RedisKey>(key => key.ToString().StartsWith($"processing_key_usage:group:{groupId}:key:{keyId}:claim:")),
                        It.IsAny<CommandFlags>()))
                    .ReturnsAsync(new RedisValue(cost.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            _mockRedisDb.Setup(x => x.KeyDeleteAsync(
                    It.IsAny<RedisKey[]>(),
                    It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey[] keys, CommandFlags _) => keys.LongLength);
        }

        private void SetupServerKeys(string pattern, RedisKey[] keys)
        {
            _mockRedisServer.Setup(x => x.Keys(
                    It.IsAny<int>(),
                    It.Is<RedisValue>(value => value == pattern),
                    It.IsAny<int>(),
                    It.IsAny<long>(),
                    It.IsAny<int>(),
                    It.IsAny<CommandFlags>()))
                .Returns(keys);
        }

        public void Dispose()
        {
            _concreteDbContext?.Dispose();
            _serviceProvider?.Dispose();
            _service?.Dispose();
            _database.Dispose();
        }
    }
}
