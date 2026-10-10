using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Gateway.EventHandlers;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Tests.Messaging;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Moq;

using Xunit.Abstractions;

namespace ConduitLLM.Tests.Http.EventHandlers
{
    [Trait("Category", "Unit")]
    [Trait("Component", "EventHandlers")]
    public class SpendUpdateProcessorTests : TestBase
    {
        private readonly Mock<IServiceScopeFactory> _serviceScopeFactoryMock;
        private readonly Mock<IServiceScope> _serviceScopeMock;
        private readonly Mock<IServiceProvider> _serviceProviderMock;
        private readonly Mock<IEventBus> _eventBusMock;
        private readonly Mock<IVirtualKeyRepository> _virtualKeyRepositoryMock;
        private readonly Mock<IVirtualKeyGroupRepository> _groupRepositoryMock;
        private readonly SpendUpdateProcessor _processor;

        public SpendUpdateProcessorTests(ITestOutputHelper output) : base(output)
        {
            _serviceScopeFactoryMock = new Mock<IServiceScopeFactory>();
            _serviceScopeMock = new Mock<IServiceScope>();
            _serviceProviderMock = new Mock<IServiceProvider>();
            _eventBusMock = new Mock<IEventBus>();
            _virtualKeyRepositoryMock = new Mock<IVirtualKeyRepository>();
            _groupRepositoryMock = new Mock<IVirtualKeyGroupRepository>();
            
            // Setup scope factory to return scope
            _serviceScopeFactoryMock.Setup(x => x.CreateScope())
                .Returns(_serviceScopeMock.Object);
            
            // Setup scope to return service provider
            _serviceScopeMock.Setup(x => x.ServiceProvider)
                .Returns(_serviceProviderMock.Object);
            
            var logger = CreateLogger<SpendUpdateProcessor>();
            
            _processor = new SpendUpdateProcessor(
                _serviceScopeFactoryMock.Object,
                _eventBusMock.Object,
                logger.Object);
        }

        [Fact]
        public void GatewayAssembly_HasSingleSpendUpdateRequestedHandler()
        {
            // Keep spend processing on one registered, durable path. A second, unregistered
            // handler can drift from the production implementation and silently lose charges.
            var handlerType = typeof(IEventHandler<SpendUpdateRequested>);

            var handlers = typeof(SpendUpdateProcessor).Assembly
                .GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false } &&
                               handlerType.IsAssignableFrom(type));

            handlers.Should().ContainSingle()
                .Which.Should().Be<SpendUpdateProcessor>();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task HandleAsync_WithRuntimeStore_PreservesDebitAndRedeliveryNotifications(bool applied)
        {
            var store = new Mock<IVirtualKeyRuntimeStore>(MockBehavior.Strict);
            _serviceProviderMock.Setup(sp => sp.GetService(typeof(IVirtualKeyRuntimeStore)))
                .Returns(store.Object);
            store.Setup(s => s.GetByIdAsync(123, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VirtualKeyRuntimeRecord
                {
                    Id = 123, KeyName = "Runtime key", KeyHash = "runtime-hash", VirtualKeyGroupId = 1,
                    Group = new VirtualKeyGroupRuntimeRecord { Id = 1, Balance = 100m }
                });
            var request = new SpendUpdateRequested
            {
                KeyId = 123, Amount = 105m, RequestId = "runtime-spend", CorrelationId = "runtime-correlation",
                Timestamp = new DateTime(2026, 10, 7, 8, 45, 0, DateTimeKind.Utc)
            };
            store.Setup(s => s.AdjustBalanceAsync(It.Is<VirtualKeyBalanceAdjustment>(a =>
                    a.GroupId == 1 && a.Amount == -105m && a.IdempotencyKey == "spend:runtime-spend" &&
                    a.ReferenceType == VirtualKeyBalanceReferenceType.VirtualKey && a.ReferenceId == "123" &&
                    a.Description == "API usage by virtual key #123" && a.InitiatedBy == "System" &&
                    a.BillingWindowStartUtc == new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc)),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VirtualKeyBalanceAdjustmentResult(-5m, 105m, applied));
            var reservations = new Mock<IBatchSpendUpdateService>();
            var processor = new SpendUpdateProcessor(_serviceScopeFactoryMock.Object, _eventBusMock.Object,
                CreateLogger<SpendUpdateProcessor>().Object, reservations.Object);

            await processor.HandleAsync(request, new TestEventContext());

            store.VerifyAll();
            _serviceProviderMock.Verify(sp => sp.GetService(typeof(IVirtualKeyRepository)), Times.Never);
            _serviceProviderMock.Verify(sp => sp.GetService(typeof(IVirtualKeyGroupRepository)), Times.Never);
            reservations.Verify(s => s.ReleaseSpendReservationAsync(123, "runtime-spend"), Times.Once);
            _eventBusMock.Verify(b => b.PublishAsync(It.Is<SpendUpdated>(e =>
                e.KeyId == 123 && e.KeyHash == "runtime-hash" && e.Amount == 105m &&
                e.NewTotalSpend == 105m && e.RequestId == "runtime-spend" && e.CorrelationId == "runtime-correlation"),
                It.IsAny<CancellationToken>()), Times.Once);
            _eventBusMock.Verify(b => b.PublishAsync(It.Is<SpendThresholdExceeded>(e =>
                e.VirtualKeyId == 123 && e.VirtualKeyHash == "runtime-hash" && e.AmountOver == 5m),
                It.IsAny<CancellationToken>()), applied ? Times.Once() : Times.Never());
        }

        [Fact]
        public async Task HandleAsync_WhenRuntimeDebitFails_RetainsReservationAndThrowsForDurableRetry()
        {
            var store = new Mock<IVirtualKeyRuntimeStore>();
            _serviceProviderMock.Setup(sp => sp.GetService(typeof(IVirtualKeyRuntimeStore))).Returns(store.Object);
            store.Setup(s => s.GetByIdAsync(123, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VirtualKeyRuntimeRecord
                {
                    Id = 123, VirtualKeyGroupId = 1,
                    Group = new VirtualKeyGroupRuntimeRecord { Id = 1, Balance = 100m }
                });
            store.Setup(s => s.AdjustBalanceAsync(It.IsAny<VirtualKeyBalanceAdjustment>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Database unavailable"));
            var reservations = new Mock<IBatchSpendUpdateService>();
            var processor = new SpendUpdateProcessor(_serviceScopeFactoryMock.Object, _eventBusMock.Object,
                CreateLogger<SpendUpdateProcessor>().Object, reservations.Object);

            var act = () => processor.HandleAsync(new SpendUpdateRequested
                { KeyId = 123, Amount = 5m, RequestId = "retry-spend" }, new TestEventContext());

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Database unavailable");
            reservations.VerifyNoOtherCalls();
            _eventBusMock.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task HandleAsync_WithRepositoryAvailable_UpdatesSpendSuccessfully()
        {
            // Arrange
            _serviceProviderMock
                .Setup(sp => sp.GetService(typeof(IVirtualKeyRepository)))
                .Returns(_virtualKeyRepositoryMock.Object);
            _serviceProviderMock
                .Setup(sp => sp.GetService(typeof(IVirtualKeyGroupRepository)))
                .Returns(_groupRepositoryMock.Object);

            var virtualKey = new VirtualKey
            {
                Id = 123,
                KeyHash = "test-hash",
                VirtualKeyGroupId = 1,
                UpdatedAt = DateTime.UtcNow.AddHours(-1)
            };
            
            var group = new VirtualKeyGroup
            {
                Id = 1,
                GroupName = "Test Group",
                Balance = 100m,
                LifetimeCreditsAdded = 100m,
                LifetimeSpent = 100m
            };

            _virtualKeyRepositoryMock
                .Setup(r => r.GetByIdAsync(123, It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(virtualKey));
            
            _groupRepositoryMock
                .Setup(r => r.GetByIdAsync(1))
                .Returns(Task.FromResult(group));

            _groupRepositoryMock
                .Setup(r => r.AdjustBalanceIdempotentAsync(
                    1,
                    -50m,
                    "spend:req-123",
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    ReferenceType.VirtualKey,
                    It.IsAny<string>(),
                    It.IsAny<DateTime>()))
                .Returns(Task.FromResult(new BalanceAdjustmentResult(50m, 150m, Applied: true)));

            var @event = new SpendUpdateRequested
            {
                EventId = Guid.NewGuid().ToString(),
                KeyId = 123,
                Amount = 50m,
                RequestId = "req-123",
                CorrelationId = "corr-123"
            };

            // Act
            await _processor.HandleAsync(@event, new TestEventContext());

            // Assert
            // Verify group balance was adjusted idempotently with correct reference type
            _groupRepositoryMock.Verify(r => r.AdjustBalanceIdempotentAsync(
                1,
                -50m,
                "spend:req-123",
                "API usage by virtual key #123",
                "System",
                ReferenceType.VirtualKey,
                "123",
                It.IsAny<DateTime>()), Times.Once);

            _eventBusMock.Verify(p => p.PublishAsync(It.Is<SpendUpdated>(su =>
                su.KeyId == 123 &&
                su.Amount == 50m &&
                su.NewTotalSpend == 150m &&
                su.RequestId == "req-123" &&
                su.CorrelationId == "corr-123"), 
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task HandleAsync_WithRepositoryUnavailable_ThrowsForDurableRetry()
        {
            // Arrange
            _serviceProviderMock
                .Setup(sp => sp.GetService(typeof(IVirtualKeyRepository)))
                .Returns(null);
            _serviceProviderMock
                .Setup(sp => sp.GetService(typeof(IVirtualKeyGroupRepository)))
                .Returns(null);

            var @event = new SpendUpdateRequested
            {
                EventId = Guid.NewGuid().ToString(),
                KeyId = 456,
                Amount = 75m,
                RequestId = "req-456",
                CorrelationId = "corr-456"
            };

            // Act
            var act = () => _processor.HandleAsync(@event, new TestEventContext());

            // Assert
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Spend updates require both IVirtualKeyRepository and IVirtualKeyGroupRepository.");

            _eventBusMock.Verify(p => p.PublishAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
            _virtualKeyRepositoryMock.Verify(r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task HandleAsync_WithZeroAmount_SkipsProcessing()
        {
            // Arrange
            var @event = new SpendUpdateRequested
            {
                EventId = Guid.NewGuid().ToString(),
                KeyId = 789,
                Amount = 0m,
                RequestId = "req-789"
            };

            // Act
            await _processor.HandleAsync(@event, new TestEventContext());

            // Assert
            _serviceProviderMock.Verify(sp => sp.GetService(It.IsAny<Type>()), Times.Never);
            _eventBusMock.Verify(p => p.PublishAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task HandleAsync_WithNegativeAmount_SkipsProcessing()
        {
            // Arrange
            var @event = new SpendUpdateRequested
            {
                EventId = Guid.NewGuid().ToString(),
                KeyId = 999,
                Amount = -10m,
                RequestId = "req-999"
            };

            // Act
            await _processor.HandleAsync(@event, new TestEventContext());

            // Assert
            _serviceProviderMock.Verify(sp => sp.GetService(It.IsAny<Type>()), Times.Never);
            _eventBusMock.Verify(p => p.PublishAsync(It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task HandleAsync_WithNonExistentVirtualKey_SkipsUpdate()
        {
            // Arrange
            _serviceProviderMock
                .Setup(sp => sp.GetService(typeof(IVirtualKeyRepository)))
                .Returns(_virtualKeyRepositoryMock.Object);
            _serviceProviderMock
                .Setup(sp => sp.GetService(typeof(IVirtualKeyGroupRepository)))
                .Returns(_groupRepositoryMock.Object);

            _virtualKeyRepositoryMock
                .Setup(r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult<VirtualKey>(null));

            var @event = new SpendUpdateRequested
            {
                EventId = Guid.NewGuid().ToString(),
                KeyId = 111,
                Amount = 25m,
                RequestId = "req-111"
            };

            // Act
            await _processor.HandleAsync(@event, new TestEventContext());

            // Assert
            _virtualKeyRepositoryMock.Verify(r => r.GetByIdAsync(111, It.IsAny<CancellationToken>()), Times.Once);
            _groupRepositoryMock.Verify(r => r.GetByIdAsync(It.IsAny<int>()), Times.Never);
            _eventBusMock.Verify(p => p.PublishAsync(It.IsAny<SpendUpdated>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task HandleAsync_WithBalanceDepleted_PublishesThresholdExceeded()
        {
            // Arrange
            SetupRepositories(keyId: 222, keyHash: "test-hash-222", groupBalance: 20m, groupLifetimeSpent: 100m);

            // Debit takes the balance negative — a legitimate depletion, not a failure
            _groupRepositoryMock
                .Setup(r => r.AdjustBalanceIdempotentAsync(
                    1,
                    -30m,
                    "spend:req-222",
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    ReferenceType.VirtualKey,
                    It.IsAny<string>(),
                    It.IsAny<DateTime>()))
                .Returns(Task.FromResult(new BalanceAdjustmentResult(-10m, 130m, Applied: true)));

            var @event = new SpendUpdateRequested
            {
                EventId = Guid.NewGuid().ToString(),
                KeyId = 222,
                Amount = 30m,
                RequestId = "req-222",
                CorrelationId = "corr-222"
            };

            // Act
            await _processor.HandleAsync(@event, new TestEventContext());

            // Assert
            _eventBusMock.Verify(p => p.PublishAsync(It.Is<SpendUpdated>(su =>
                su.KeyId == 222 &&
                su.NewTotalSpend == 130m),
                It.IsAny<CancellationToken>()), Times.Once);

            _eventBusMock.Verify(p => p.PublishAsync(It.Is<SpendThresholdExceeded>(ste =>
                ste.VirtualKeyId == 222 &&
                ste.CurrentSpend == 130m &&
                ste.AmountOver == 10m),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task HandleAsync_WithDuplicateRequestId_SkipsThresholdButRepublishesNotification()
        {
            // Arrange - previous balance positive and result balance negative, so ONLY the
            // duplicate (Applied=false) guard prevents the threshold event from firing again
            SetupRepositories(keyId: 333, keyHash: "test-hash-333", groupBalance: 20m, groupLifetimeSpent: 130m);

            // Repository reports the idempotency key was already recorded
            _groupRepositoryMock
                .Setup(r => r.AdjustBalanceIdempotentAsync(
                    1,
                    -30m,
                    "spend:req-333",
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    ReferenceType.VirtualKey,
                    It.IsAny<string>(),
                    It.IsAny<DateTime>()))
                .Returns(Task.FromResult(new BalanceAdjustmentResult(-10m, 130m, Applied: false)));

            var @event = new SpendUpdateRequested
            {
                EventId = Guid.NewGuid().ToString(),
                KeyId = 333,
                Amount = 30m,
                RequestId = "req-333"
            };

            // Act
            await _processor.HandleAsync(@event, new TestEventContext());

            // Assert - notification republished (in case the first attempt crashed before
            // publishing), but the threshold crossing is not reported a second time
            _eventBusMock.Verify(p => p.PublishAsync(It.Is<SpendUpdated>(su =>
                su.KeyId == 333 &&
                su.NewTotalSpend == 130m),
                It.IsAny<CancellationToken>()), Times.Once);

            _eventBusMock.Verify(p => p.PublishAsync(It.IsAny<SpendThresholdExceeded>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task HandleAsync_WithoutRequestId_RejectsUnsafeDebit()
        {
            // Arrange
            SetupRepositories(keyId: 444, keyHash: "test-hash-444", groupBalance: 100m, groupLifetimeSpent: 100m);

            _groupRepositoryMock
                .Setup(r => r.AdjustBalanceAsync(
                    1,
                    -50m,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    ReferenceType.VirtualKey,
                    It.IsAny<string>(),
                    It.IsAny<DateTime>()))
                .Returns(Task.FromResult(50m));

            var @event = new SpendUpdateRequested
            {
                EventId = Guid.NewGuid().ToString(),
                KeyId = 444,
                Amount = 50m,
                RequestId = string.Empty
            };

            // Act
            var act = () => _processor.HandleAsync(@event, new TestEventContext());

            // Assert
            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*non-empty RequestId*");
            _groupRepositoryMock.Verify(r => r.AdjustBalanceAsync(
                1, -50m, It.IsAny<string>(), It.IsAny<string>(), ReferenceType.VirtualKey, It.IsAny<string>(), It.IsAny<DateTime>()),
                Times.Never);
            _groupRepositoryMock.Verify(r => r.AdjustBalanceIdempotentAsync(
                It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<ReferenceType>(), It.IsAny<string>(), It.IsAny<DateTime>()),
                Times.Never);

            _eventBusMock.Verify(p => p.PublishAsync(It.IsAny<SpendUpdated>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task HandleAsync_SequentialReplay_DebitsAndCrossesThresholdOnce()
        {
            SetupRepositories(keyId: 555, keyHash: "test-hash-555", groupBalance: 100m, groupLifetimeSpent: 0m);
            _groupRepositoryMock.SetupSequence(r => r.AdjustBalanceIdempotentAsync(
                    1, -150m, "spend:replay-555", It.IsAny<string>(), It.IsAny<string>(),
                    ReferenceType.VirtualKey, "555", It.IsAny<DateTime>()))
                .ReturnsAsync(new BalanceAdjustmentResult(-50m, 150m, true))
                .ReturnsAsync(new BalanceAdjustmentResult(-50m, 150m, false))
                .ReturnsAsync(new BalanceAdjustmentResult(-50m, 150m, false));
            var message = new SpendUpdateRequested
            {
                KeyId = 555,
                Amount = 150m,
                RequestId = "replay-555",
                Timestamp = DateTime.UtcNow
            };

            await _processor.HandleAsync(message, new TestEventContext());
            await _processor.HandleAsync(message, new TestEventContext());
            await _processor.HandleAsync(message, new TestEventContext());

            _groupRepositoryMock.Verify(r => r.AdjustBalanceIdempotentAsync(
                1, -150m, "spend:replay-555", It.IsAny<string>(), It.IsAny<string>(),
                ReferenceType.VirtualKey, "555", It.IsAny<DateTime>()), Times.Exactly(3));
            _eventBusMock.Verify(p => p.PublishAsync(It.IsAny<SpendThresholdExceeded>(),
                It.IsAny<CancellationToken>()), Times.Once);
            _eventBusMock.Verify(p => p.PublishAsync(It.IsAny<SpendUpdated>(),
                It.IsAny<CancellationToken>()), Times.Exactly(3));
        }

        private void SetupRepositories(int keyId, string keyHash, decimal groupBalance, decimal groupLifetimeSpent)
        {
            _serviceProviderMock
                .Setup(sp => sp.GetService(typeof(IVirtualKeyRepository)))
                .Returns(_virtualKeyRepositoryMock.Object);
            _serviceProviderMock
                .Setup(sp => sp.GetService(typeof(IVirtualKeyGroupRepository)))
                .Returns(_groupRepositoryMock.Object);

            var virtualKey = new VirtualKey
            {
                Id = keyId,
                KeyHash = keyHash,
                VirtualKeyGroupId = 1
            };

            var group = new VirtualKeyGroup
            {
                Id = 1,
                GroupName = "Test Group",
                Balance = groupBalance,
                LifetimeSpent = groupLifetimeSpent
            };

            _virtualKeyRepositoryMock
                .Setup(r => r.GetByIdAsync(keyId, It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(virtualKey));

            _groupRepositoryMock
                .Setup(r => r.GetByIdAsync(1))
                .Returns(Task.FromResult(group));
        }

        [Fact]
        public async Task HandleAsync_WithRepositoryException_ThrowsToTriggerRetry()
        {
            // Arrange
            _serviceProviderMock
                .Setup(sp => sp.GetService(typeof(IVirtualKeyRepository)))
                .Returns(_virtualKeyRepositoryMock.Object);
            _serviceProviderMock
                .Setup(sp => sp.GetService(typeof(IVirtualKeyGroupRepository)))
                .Returns(_groupRepositoryMock.Object);

            _virtualKeyRepositoryMock
                .Setup(r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Throws(new InvalidOperationException("Database connection error"));

            var @event = new SpendUpdateRequested
            {
                EventId = Guid.NewGuid().ToString(),
                KeyId = 333,
                Amount = 40m,
                RequestId = "req-333"
            };

            // Act
            var act = () => _processor.HandleAsync(@event, new TestEventContext());

            // Assert
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Database connection error");
        }

        [Fact]
        public void Constructor_WithNullServiceProvider_ThrowsArgumentNullException()
        {
            // Arrange
            var logger = CreateLogger<SpendUpdateProcessor>();
            
            // Act
            var act = () => new SpendUpdateProcessor(
                null,
                _eventBusMock.Object,
                logger.Object);

            // Assert
            act.Should().Throw<ArgumentNullException>()
                .WithParameterName("serviceScopeFactory");
        }

        [Fact]
        public void Constructor_WithNullEventBus_ThrowsArgumentNullException()
        {
            // Arrange
            var logger = CreateLogger<SpendUpdateProcessor>();
            
            // Act
            var act = () => new SpendUpdateProcessor(
                _serviceScopeFactoryMock.Object,
                null,
                logger.Object);

            // Assert
            act.Should().Throw<ArgumentNullException>()
                .WithParameterName("eventBus");
        }

        [Fact]
        public void Constructor_WithNullLogger_ThrowsArgumentNullException()
        {
            // Act
            var act = () => new SpendUpdateProcessor(
                _serviceScopeFactoryMock.Object,
                _eventBusMock.Object,
                null);

            // Assert
            act.Should().Throw<ArgumentNullException>()
                .WithParameterName("logger");
        }
    }
}
