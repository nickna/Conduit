using ConduitLLM.Core.Services;

using AwesomeAssertions;

using Microsoft.Extensions.Logging;

using Moq;

namespace ConduitLLM.Tests.Core.Services
{
    public partial class CancellableTaskRegistryTests
    {
        [Fact]
        public void ReplacedClaim_OldCleanupOrCancellationCannotRemoveNewOwnersCancellationSource()
        {
            using var old = new CancellationTokenSource();
            using var replacement = new CancellationTokenSource();
            _registry.RegisterTask("recovered", old);
            _registry.RegisterClaimedTask("recovered", replacement);
            old.Cancel();
            _registry.UnregisterTask("recovered", old);
            _registry.TryGetCancellationToken("recovered", out var current).Should().BeTrue();
            current.Should().Be(replacement.Token);
            _registry.TryCancel("recovered").Should().BeTrue();
            replacement.IsCancellationRequested.Should().BeTrue();
        }

        [Fact]
        public void UnregisterTask_RemovesTaskFromRegistry()
        {
            // Arrange
            var taskId = "unregister-test";
            using var cts = new CancellationTokenSource();
            _registry.RegisterTask(taskId, cts);

            // Verify task is registered
            _registry.TryGetCancellationToken(taskId, out _).Should().BeTrue();

            // Act
            _registry.UnregisterTask(taskId);

            // Assert
            _registry.TryGetCancellationToken(taskId, out _).Should().BeFalse();
            
            // Verify logging
            _loggerMock.Verify(
                logger => logger.Log(
                    LogLevel.Debug,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Unregistered task") && v.ToString()!.Contains(taskId)),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public void UnregisterTask_WithNullTaskId_DoesNothing()
        {
            // Act & Assert
            var act = () => _registry.UnregisterTask(null!);
            act.Should().NotThrow();
        }

        [Fact]
        public void UnregisterTask_WithNonExistentTask_DoesNothing()
        {
            // Act & Assert
            var act = () => _registry.UnregisterTask("non-existent-task");
            act.Should().NotThrow();
        }

        [Fact]
        public void CancelAll_CancelsAllRegisteredTasks()
        {
            // Arrange
            var tasks = new[]
            {
                ("task-1", new CancellationTokenSource()),
                ("task-2", new CancellationTokenSource()),
                ("task-3", new CancellationTokenSource())
            };

            foreach (var (taskId, cts) in tasks)
            {
                _registry.RegisterTask(taskId, cts);
            }

            // Act
            _registry.CancelAll();

            // Assert
            foreach (var (taskId, cts) in tasks)
            {
                cts.Token.IsCancellationRequested.Should().BeTrue();
                // Verify tasks are removed from registry
                _registry.TryGetCancellationToken(taskId, out _).Should().BeFalse();
                cts.Dispose();
            }

            // Verify logging
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Cancelling all 3 registered tasks")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public void CancelAll_WithNoRegisteredTasks_LogsZeroCount()
        {
            // Act
            _registry.CancelAll();

            // Assert
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Cancelling all 0 registered tasks")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public void AutomaticUnregistration_WhenTokenCancelled_KeepsTaskInRegistryDuringGracePeriod()
        {
            // Arrange
            var taskId = "auto-unregister-test";
            using var cts = new CancellationTokenSource();
            _registry.RegisterTask(taskId, cts);

            // Act
            cts.Cancel();

            // Assert - Task should still be in registry during grace period
            _registry.TryGetCancellationToken(taskId, out var token).Should().BeTrue();
            token.Value.IsCancellationRequested.Should().BeTrue();
        }

        [Fact]
        public async Task CleanupTimer_RemovesMultipleCancelledTasksAfterGracePeriod()
        {
            // Arrange - Use a very short grace period for testing
            var shortGracePeriod = TimeSpan.FromMilliseconds(50);
            using var registry = new CancellableTaskRegistry(_loggerMock.Object, shortGracePeriod);

            var taskIds = new[] { "cleanup-1", "cleanup-2", "cleanup-3" };
            var tokenSources = new CancellationTokenSource[3];

            // Register multiple tasks
            for (int i = 0; i < taskIds.Length; i++)
            {
                tokenSources[i] = new CancellationTokenSource();
                registry.RegisterTask(taskIds[i], tokenSources[i]);
            }

            // Cancel all tasks
            foreach (var cts in tokenSources)
            {
                cts.Cancel();
            }

            // Wait for cleanup to occur (grace period + cleanup timer interval of 1 second + buffer)
            // Adding extra buffer to ensure cleanup timer has definitely run on slower CI machines
            // CI environments may have unpredictable thread scheduling, so we need a larger buffer
            await Task.Delay(TimeSpan.FromMilliseconds(2100));

            // Assert - Tasks should be removed from registry
            foreach (var taskId in taskIds)
            {
                registry.TryGetCancellationToken(taskId, out _).Should().BeFalse();
            }

            // Cleanup
            foreach (var cts in tokenSources)
            {
                cts.Dispose();
            }
        }

        [Fact]
        public async Task NonCancelledTasks_AreNotRemovedByCleanupTimer()
        {
            // Arrange
            var shortGracePeriod = TimeSpan.FromMilliseconds(50);
            using var registry = new CancellableTaskRegistry(_loggerMock.Object, shortGracePeriod);
            
            var activeCts = new CancellationTokenSource();
            var cancelledCts = new CancellationTokenSource();
            
            registry.RegisterTask("active-task", activeCts);
            registry.RegisterTask("cancelled-task", cancelledCts);
            
            // Cancel only one task
            cancelledCts.Cancel();
            
            // Wait beyond grace period and for cleanup timer to run (runs every 1 second)
            // Adding extra buffer to ensure cleanup timer has definitely run on slower CI machines
            // CI environments may have unpredictable thread scheduling, so we need a larger buffer
            await Task.Delay(TimeSpan.FromMilliseconds(2100));
            
            // Assert
            registry.TryGetCancellationToken("active-task", out _).Should().BeTrue();
            registry.TryGetCancellationToken("cancelled-task", out _).Should().BeFalse();
            
            // Cleanup
            activeCts.Dispose();
            cancelledCts.Dispose();
        }
    }
}
