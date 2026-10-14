using System;
using System.Threading.Tasks;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Consumers;
using ConduitLLM.Core.Events;
using ConduitLLM.Tests.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ConduitLLM.Tests.Core.Consumers
{
    /// <summary>
    /// Unit tests for GlobalSettingCacheInvalidationHandler consumer
    /// Ensures event-driven cache synchronization works correctly across distributed instances
    /// </summary>
    [Trait("Category", "Unit")]
    [Trait("Component", "Consumer")]
    public class GlobalSettingCacheInvalidationHandlerTests
    {
        private readonly Mock<IGlobalSettingsCacheService> _mockCacheService;
        private readonly Mock<ILogger<GlobalSettingCacheInvalidationHandler>> _mockLogger;
        private readonly GlobalSettingCacheInvalidationHandler _consumer;

        public GlobalSettingCacheInvalidationHandlerTests()
        {
            _mockCacheService = new Mock<IGlobalSettingsCacheService>();
            _mockLogger = new Mock<ILogger<GlobalSettingCacheInvalidationHandler>>();
            _consumer = new GlobalSettingCacheInvalidationHandler(
                _mockCacheService.Object,
                _mockLogger.Object);
        }

        #region Basic Consumption Tests

        [Fact]
        public async Task Consume_WithValidEvent_CallsPublishReloadAsync()
        {
            // Arrange
            var @event = new GlobalSettingChanged
            {
                EventId = "setting-change-1",
                CorrelationId = "shared-admin-request",
                SettingId = 1,
                SettingKey = "max_agentic_iterations",
                ChangeType = "Updated",
                ChangedProperties = new[] { "Value" }
            };

            _mockCacheService
                .Setup(x => x.PublishReloadAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            // Act
            await _consumer.HandleAsync(@event, new TestEventContext());

            // Assert
            _mockCacheService.Verify(
                x => x.PublishReloadAsync(@event.EventId),
                Times.Once);
            _mockCacheService.Verify(x => x.InvalidateSettingAsync(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Consume_DuplicateEvent_PublishesTheSameReloadIdentifier()
        {
            var @event = new GlobalSettingChanged
            {
                EventId = "replayed-setting-change",
                CorrelationId = "admin-request",
                SettingId = 1,
                SettingKey = "Agentic.MaxIterations",
                ChangeType = "Created"
            };
            _mockCacheService.Setup(x => x.PublishReloadAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            await _consumer.HandleAsync(@event, new TestEventContext());
            await _consumer.HandleAsync(@event, new TestEventContext());

            _mockCacheService.Verify(x => x.PublishReloadAsync(@event.EventId), Times.Exactly(2));
            _mockCacheService.Verify(x => x.PublishReloadAsync(It.IsAny<string>()), Times.Exactly(2));
            _mockCacheService.Verify(x => x.InvalidateSettingAsync(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Consume_WithValidEvent_LogsInformationAboutReceivedEvent()
        {
            // Arrange
            var @event = new GlobalSettingChanged
            {
                SettingId = 5,
                SettingKey = "rate_limit",
                ChangeType = "Created"
            };

            _mockCacheService
                .Setup(x => x.PublishReloadAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            // Act
            await _consumer.HandleAsync(@event, new TestEventContext());

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) =>
                        o.ToString()!.Contains("Received GlobalSettingChanged event") &&
                        o.ToString()!.Contains("rate_limit") &&
                        o.ToString()!.Contains("5")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task Consume_AfterSuccessfulPublication_LogsAcknowledgement()
        {
            // Arrange
            var @event = new GlobalSettingChanged
            {
                SettingId = 1,
                SettingKey = "test_setting",
                ChangeType = "Updated"
            };

            _mockCacheService
                .Setup(x => x.PublishReloadAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            // Act
            await _consumer.HandleAsync(@event, new TestEventContext());

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) =>
                        o.ToString()!.Contains("Published cluster cache reload") &&
                        o.ToString()!.Contains("test_setting")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        #endregion

        #region Different Change Types

        [Theory]
        [InlineData("Created")]
        [InlineData("Updated")]
        [InlineData("Deleted")]
        public async Task Consume_WithDifferentChangeTypes_PublishesReload(string changeType)
        {
            // Arrange
            var @event = new GlobalSettingChanged
            {
                SettingId = 10,
                SettingKey = "dynamic_setting",
                ChangeType = changeType
            };

            _mockCacheService
                .Setup(x => x.PublishReloadAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            // Act
            await _consumer.HandleAsync(@event, new TestEventContext());

            // Assert
            _mockCacheService.Verify(
                x => x.PublishReloadAsync(@event.EventId),
                Times.Once);
        }

        [Fact]
        public async Task Consume_WithCreatedChangeType_LogsCorrectChangeType()
        {
            // Arrange
            var @event = new GlobalSettingChanged
            {
                SettingId = 1,
                SettingKey = "new_setting",
                ChangeType = "Created"
            };

            _mockCacheService
                .Setup(x => x.PublishReloadAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            // Act
            await _consumer.HandleAsync(@event, new TestEventContext());

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("ChangeType: Created")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        #endregion

        #region Error Handling Tests

        [Fact]
        public async Task Consume_WhenPublicationFails_DefersErrorLoggingToMessagingBoundary()
        {
            // Arrange
            var @event = new GlobalSettingChanged
            {
                SettingId = 1,
                SettingKey = "failing_setting",
                ChangeType = "Updated"
            };

            var exception = new InvalidOperationException("Cache service unavailable");
            _mockCacheService
                .Setup(x => x.PublishReloadAsync(It.IsAny<string>()))
                .ThrowsAsync(exception);

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await _consumer.HandleAsync(@event, new TestEventContext()));

            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Never);
        }

        [Fact]
        public async Task Consume_WhenPublicationFails_RethrowsExceptionForBusRetry()
        {
            // Arrange
            var @event = new GlobalSettingChanged
            {
                SettingId = 1,
                SettingKey = "error_setting",
                ChangeType = "Updated"
            };

            var exception = new InvalidOperationException("Database connection lost");
            _mockCacheService
                .Setup(x => x.PublishReloadAsync(It.IsAny<string>()))
                .ThrowsAsync(exception);

            // Act & Assert
            var thrownException = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await _consumer.HandleAsync(@event, new TestEventContext()));

            thrownException.Should().Be(exception);
        }

        [Fact]
        public async Task Consume_WhenPublicationThrowsNullReferenceException_StillRethrows()
        {
            // Arrange
            var @event = new GlobalSettingChanged
            {
                SettingId = 1,
                SettingKey = "null_setting",
                ChangeType = "Updated"
            };

            var exception = new NullReferenceException("Cache service is null");
            _mockCacheService
                .Setup(x => x.PublishReloadAsync(It.IsAny<string>()))
                .ThrowsAsync(exception);

            // Act & Assert
            await Assert.ThrowsAsync<NullReferenceException>(
                async () => await _consumer.HandleAsync(@event, new TestEventContext()));
        }

        #endregion

        #region Concurrent Operations Tests

        [Fact]
        public async Task Consume_MultipleEventsForSameSetting_PublishesDistinctReloads()
        {
            // Arrange
            var event1 = new GlobalSettingChanged
            {
                EventId = "setting-update-1",
                CorrelationId = "shared-admin-request",
                SettingId = 1,
                SettingKey = "concurrent_setting",
                ChangeType = "Updated"
            };

            var event2 = new GlobalSettingChanged
            {
                EventId = "setting-update-2",
                CorrelationId = "shared-admin-request",
                SettingId = 1,
                SettingKey = "concurrent_setting",
                ChangeType = "Updated"
            };

            _mockCacheService
                .Setup(x => x.PublishReloadAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            // Act
            await _consumer.HandleAsync(event1, new TestEventContext());
            await _consumer.HandleAsync(event2, new TestEventContext());

            // Assert
            _mockCacheService.Verify(
                x => x.PublishReloadAsync(event1.EventId), Times.Once);
            _mockCacheService.Verify(x => x.PublishReloadAsync(event2.EventId), Times.Once);
        }

        [Fact]
        public async Task Consume_MultipleEventsForDifferentSettings_PublishesEachEventIndependently()
        {
            // Arrange
            var event1 = new GlobalSettingChanged
            {
                SettingId = 1,
                SettingKey = "setting_a",
                ChangeType = "Updated"
            };

            var event2 = new GlobalSettingChanged
            {
                SettingId = 2,
                SettingKey = "setting_b",
                ChangeType = "Created"
            };

            _mockCacheService
                .Setup(x => x.PublishReloadAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            // Act
            await _consumer.HandleAsync(event1, new TestEventContext());
            await _consumer.HandleAsync(event2, new TestEventContext());

            // Assert
            _mockCacheService.Verify(x => x.PublishReloadAsync(event1.EventId), Times.Once);
            _mockCacheService.Verify(x => x.PublishReloadAsync(event2.EventId), Times.Once);
        }

        #endregion

        #region Edge Cases

        [Fact]
        public async Task Consume_WithEmptySettingKey_StillPublishesReload()
        {
            // Arrange
            var @event = new GlobalSettingChanged
            {
                SettingId = 1,
                SettingKey = "",
                ChangeType = "Updated"
            };

            _mockCacheService
                .Setup(x => x.PublishReloadAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            // Act
            await _consumer.HandleAsync(@event, new TestEventContext());

            // Assert
            _mockCacheService.Verify(
                x => x.PublishReloadAsync(@event.EventId),
                Times.Once);
        }

        [Fact]
        public async Task Consume_WithNullChangedProperties_ProcessesSuccessfully()
        {
            // Arrange
            var @event = new GlobalSettingChanged
            {
                SettingId = 1,
                SettingKey = "test_setting",
                ChangeType = "Updated",
                ChangedProperties = Array.Empty<string>()
            };

            _mockCacheService
                .Setup(x => x.PublishReloadAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            // Act
            await _consumer.HandleAsync(@event, new TestEventContext());

            // Assert
            _mockCacheService.Verify(
                x => x.PublishReloadAsync(@event.EventId),
                Times.Once);
        }

        [Fact]
        public async Task Consume_CompletesSuccessfully()
        {
            // Arrange
            var @event = new GlobalSettingChanged
            {
                SettingId = 1,
                SettingKey = "completion_test",
                ChangeType = "Updated"
            };

            _mockCacheService
                .Setup(x => x.PublishReloadAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            // Act
            var task = _consumer.HandleAsync(@event, new TestEventContext());
            await task;

            // Assert
            task.IsCompletedSuccessfully.Should().BeTrue();
        }

        #endregion

        #region Integration Scenario Tests

        [Fact]
        public async Task Consume_SimulatingFullCreateUpdateDeleteCycle_HandlesAllCorrectly()
        {
            // Arrange
            var createEvent = new GlobalSettingChanged
            {
                SettingId = 1,
                SettingKey = "lifecycle_setting",
                ChangeType = "Created"
            };

            var updateEvent = new GlobalSettingChanged
            {
                SettingId = 1,
                SettingKey = "lifecycle_setting",
                ChangeType = "Updated"
            };

            var deleteEvent = new GlobalSettingChanged
            {
                SettingId = 1,
                SettingKey = "lifecycle_setting",
                ChangeType = "Deleted"
            };

            _mockCacheService
                .Setup(x => x.PublishReloadAsync(It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            // Act
            await _consumer.HandleAsync(createEvent, new TestEventContext());
            await _consumer.HandleAsync(updateEvent, new TestEventContext());
            await _consumer.HandleAsync(deleteEvent, new TestEventContext());

            // Assert
            _mockCacheService.Verify(
                x => x.PublishReloadAsync(createEvent.EventId), Times.Once);
            _mockCacheService.Verify(x => x.PublishReloadAsync(updateEvent.EventId), Times.Once);
            _mockCacheService.Verify(x => x.PublishReloadAsync(deleteEvent.EventId), Times.Once);
        }

        #endregion
    }
}
