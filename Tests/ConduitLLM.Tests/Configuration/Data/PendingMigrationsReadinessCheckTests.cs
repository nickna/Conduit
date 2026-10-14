using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ConduitLLM.Tests.Configuration.Data
{
    public class PendingMigrationsReadinessCheckTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("20200101000000_OldSchema")]
        [InlineData("99991231235959_FutureSchema")]
        public async Task CheckHealthAsync_MissingOrDifferentSchema_ReturnsUnhealthy(
            string? appliedVersion)
        {
            var probe = new Probe(_ => Task.FromResult(Status(appliedVersion)));
            var check = CreateCheck(probe);

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Contains("migrate", result.Description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(ConduitSchemaVersion.Current, result.Description);
            Assert.Equal(1, probe.Calls);
        }

        [Fact]
        public async Task CheckHealthAsync_SchemaChangesAfterStartup_RechecksAndRecovers()
        {
            var versions = new Queue<string?>(
                [ConduitSchemaVersion.Current, "99991231235959_FutureSchema", ConduitSchemaVersion.Current]);
            var probe = new Probe(_ => Task.FromResult(Status(versions.Dequeue())));
            var check = CreateCheck(probe);

            var first = await check.CheckHealthAsync(new HealthCheckContext());
            var changed = await check.CheckHealthAsync(new HealthCheckContext());
            var recovered = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Healthy, first.Status);
            Assert.Equal(HealthStatus.Unhealthy, changed.Status);
            Assert.Equal(HealthStatus.Healthy, recovered.Status);
            Assert.Equal(3, probe.Calls);
        }

        [Fact]
        public async Task CheckHealthAsync_SchemaCaseDiffers_ReturnsUnhealthy()
        {
            var probe = new Probe(_ =>
                Task.FromResult(Status(ConduitSchemaVersion.Current.ToLowerInvariant())));

            var result = await CreateCheck(probe).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CheckHealthAsync_ProbeFails_ReturnsUnhealthy(bool timeout)
        {
            Exception failure = timeout
                ? new OperationCanceledException("Probe timed out")
                : new InvalidOperationException("Database unavailable");
            var probe = new Probe(_ => Task.FromException<SchemaVersionStatus>(failure));

            var result = await CreateCheck(probe).CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Same(failure, result.Exception);
        }

        [Fact]
        public async Task CheckHealthAsync_CallerCancels_PropagatesCancellation()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var probe = new Probe(token => Task.FromCanceled<SchemaVersionStatus>(token));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CreateCheck(probe).CheckHealthAsync(new HealthCheckContext(), cancellation.Token));

            Assert.Equal(cancellation.Token, probe.LastToken);
        }

        [Fact]
        public async Task CheckHealthAsync_SkipMode_DoesNotProbeSchema()
        {
            var probe = new Probe(_ => throw new InvalidOperationException("Must not probe"));

            var result = await CreateCheck(probe, MigrationMode.Skip)
                .CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Healthy, result.Status);
            Assert.Equal(0, probe.Calls);
        }

        private static PendingMigrationsReadinessCheck CreateCheck(
            Probe probe, MigrationMode mode = MigrationMode.Wait) =>
            new(probe, new MigrationStartupOptions { Mode = mode });

        private static SchemaVersionStatus Status(string? appliedVersion) =>
            new(appliedVersion, ConduitSchemaVersion.Current);

        private sealed class Probe(
            Func<CancellationToken, Task<SchemaVersionStatus>> read) : ISchemaVersionProbe
        {
            public int Calls { get; private set; }
            public CancellationToken LastToken { get; private set; }

            public Task<SchemaVersionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            {
                Calls++;
                LastToken = cancellationToken;
                return read(cancellationToken);
            }
        }
    }
}
