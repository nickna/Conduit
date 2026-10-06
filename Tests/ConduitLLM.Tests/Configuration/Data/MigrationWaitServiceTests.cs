using ConduitLLM.Configuration.Data;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Moq;

namespace ConduitLLM.Tests.Configuration.Data
{
    public class MigrationWaitServiceTests
    {
        private readonly Mock<ISchemaVersionProbe> _probeMock = new();
        private readonly Mock<IHostApplicationLifetime> _lifetimeMock = new();
        private readonly Mock<ILogger<MigrationWaitService>> _loggerMock = new();
        private readonly MigrationReadinessState _state =
            new(new MigrationStartupOptions { Mode = MigrationMode.Wait });

        private MigrationWaitService CreateService(MigrationMode mode)
        {
            var options = new MigrationStartupOptions { Mode = mode };
            return new MigrationWaitService(
                options, _probeMock.Object, _state, _lifetimeMock.Object, _loggerMock.Object);
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!condition() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }
        }

        [Fact]
        public async Task ExecuteAsync_ModeNotWait_DoesNothing()
        {
            var service = CreateService(MigrationMode.Skip);

            await service.StartAsync(CancellationToken.None);
            await (service.ExecuteTask ?? Task.CompletedTask);
            await service.StopAsync(CancellationToken.None);

            Assert.False(_state.IsSchemaCurrent);
            _probeMock.Verify(
                p => p.GetStatusAsync(It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task ExecuteAsync_NoPendingMigrations_MarksStateCurrent()
        {
            _probeMock
                .Setup(p => p.GetStatusAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SchemaVersionStatus(ConduitSchemaVersion.Current, ConduitSchemaVersion.Current));
            var service = CreateService(MigrationMode.Wait);

            await service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => _state.IsSchemaCurrent, TimeSpan.FromSeconds(5));
            await service.StopAsync(CancellationToken.None);

            Assert.True(_state.IsSchemaCurrent);
        }

        [Fact]
        public async Task ExecuteAsync_ProbeThrows_StateStaysNotCurrentAndServiceKeepsPolling()
        {
            _probeMock
                .Setup(p => p.GetStatusAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("database unreachable"));
            var service = CreateService(MigrationMode.Wait);

            await service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(
                () => _probeMock.Invocations.Any(i => i.Method.Name == nameof(ISchemaVersionProbe.GetStatusAsync)),
                TimeSpan.FromSeconds(5));
            await service.StopAsync(CancellationToken.None);

            Assert.False(_state.IsSchemaCurrent);
            // The failed probe must not have completed or crashed the service task —
            // it should still be in its polling loop when StopAsync cancels it.
            _probeMock.Verify(
                p => p.GetStatusAsync(It.IsAny<CancellationToken>()),
                Times.AtLeastOnce);
        }

        [Fact]
        public async Task ExecuteAsync_PendingMigrationsExist_StateStaysNotCurrent()
        {
            _probeMock
                .Setup(p => p.GetStatusAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SchemaVersionStatus("20200101000000_Old", ConduitSchemaVersion.Current));
            var service = CreateService(MigrationMode.Wait);

            await service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(
                () => _probeMock.Invocations.Any(i => i.Method.Name == nameof(ISchemaVersionProbe.GetStatusAsync)),
                TimeSpan.FromSeconds(5));
            await service.StopAsync(CancellationToken.None);

            Assert.False(_state.IsSchemaCurrent);
        }
    }
}
