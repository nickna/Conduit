using System.Globalization;
using System.Text;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ConduitLLM.Tests.Configuration.Services;

public sealed class BatchAuditShutdownMetricsTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task StopAsync_UpdatesMetricsForSubthresholdEventsOnlyOnce()
    {
        await using var fixture = new AuditFixture();
        var ids = await fixture.QueueAsync(3);
        Assert.Equal(new AuditMetrics(3, 0, 0, 3), await fixture.ReadMetricsAsync());

        await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);

        await fixture.AssertPersistedAsync(ids);
        var stoppedMetrics = new AuditMetrics(3, 3, 0, 0);
        Assert.Equal(stoppedMetrics, await fixture.ReadMetricsAsync());

        await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);

        await fixture.AssertPersistedAsync(ids);
        Assert.Equal(stoppedMetrics, await fixture.ReadMetricsAsync());
    }

    [Fact]
    public async Task StopAsync_RequeuesFailedSaveWithoutCountingItAsSuccessful()
    {
        var failure = new FailingSaveChangesInterceptor();
        await using var fixture = new AuditFixture(failure);
        var ids = await fixture.QueueAsync(3);
        var queuedMetrics = await fixture.ReadMetricsAsync();
        failure.Arm();

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout));

        await fixture.AssertPersistedAsync(Array.Empty<Guid>());
        Assert.Equal(queuedMetrics, await fixture.ReadMetricsAsync());

        await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);

        await fixture.AssertPersistedAsync(ids);
        Assert.Equal(new AuditMetrics(3, 3, queuedMetrics.Failed, 0),
            await fixture.ReadMetricsAsync());
    }

    [Fact]
    public async Task StopAsync_JoinsTimerFlushAndCountsRemainingEventsExactlyOnce()
    {
        var gate = new GatedSaveChangesInterceptor();
        await using var fixture = new AuditFixture(gate);
        Task? stopping = null;
        try
        {
            gate.Arm();
            await fixture.Service.StartAsync(CancellationToken.None);
            var timerIds = await fixture.QueueAsync(3);
            await gate.Entered.WaitAsync(TestTimeout);
            var remainingIds = await fixture.QueueAsync(2);
            Assert.Equal(0, (await fixture.ReadMetricsAsync()).Flushed);

            stopping = fixture.Service.StopAsync(CancellationToken.None);
            Assert.False(stopping.IsCompleted);
            gate.Release();
            await stopping.WaitAsync(TestTimeout);

            await fixture.AssertPersistedAsync(timerIds.Concat(remainingIds));
            Assert.Equal(new AuditMetrics(5, 5, 0, 0), await fixture.ReadMetricsAsync());
        }
        finally
        {
            gate.Release();
            if (stopping is not null)
            {
                await stopping.WaitAsync(TestTimeout);
            }
        }
    }

    [Fact]
    public async Task StopAsync_CancellationBeforeAcquisitionPreservesQueuedEvents()
    {
        await using var fixture = new AuditFixture();
        var ids = await fixture.QueueAsync(3);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.StopAsync(cancellation.Token).WaitAsync(TestTimeout));

        await fixture.AssertPersistedAsync(Array.Empty<Guid>());
        Assert.Equal(new AuditMetrics(3, 0, 0, 3), await fixture.ReadMetricsAsync());
        await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
        await fixture.AssertPersistedAsync(ids);
        Assert.Equal(new AuditMetrics(3, 3, 0, 0), await fixture.ReadMetricsAsync());
    }

    [Fact]
    public async Task StopAsync_CancellationDuringSaveRequeuesEventsForRetry()
    {
        var gate = new GatedSaveChangesInterceptor();
        await using var fixture = new AuditFixture(gate);
        using var cancellation = new CancellationTokenSource();
        var ids = await fixture.QueueAsync(3);
        Task? stopping = null;
        try
        {
            gate.Arm();
            stopping = fixture.Service.StopAsync(cancellation.Token);
            await gate.Entered.WaitAsync(TestTimeout);
            Assert.Equal(cancellation.Token, gate.SaveCancellationToken);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping.WaitAsync(TestTimeout));

            await fixture.AssertPersistedAsync(Array.Empty<Guid>());
            Assert.Equal(new AuditMetrics(3, 0, 0, 3), await fixture.ReadMetricsAsync());
            gate.Release();
            await fixture.Service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
            await fixture.AssertPersistedAsync(ids);
            Assert.Equal(new AuditMetrics(3, 3, 0, 0), await fixture.ReadMetricsAsync());
        }
        finally
        {
            gate.Release();
            if (stopping is not null)
            {
                try { await stopping.WaitAsync(TestTimeout); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
        }
    }

    private sealed record AuditMetrics(double Queued, double Flushed, double Failed, double Depth);

    private sealed class AuditFixture : IAsyncDisposable
    {
        private readonly SqliteTestDatabase _database;
        private readonly ServiceProvider _provider;

        public AuditFixture(params IInterceptor[] interceptors)
        {
            _database = new SqliteTestDatabase(interceptors);
            var services = new ServiceCollection();
            services.AddSingleton(_database.CreateDbContextFactory());
            services.AddScoped(provider => provider
                .GetRequiredService<IDbContextFactory<ConduitDbContext>>()
                .CreateDbContext());
            _provider = services.BuildServiceProvider();
            Service = new MetricsBillingAuditService(_provider);
        }

        public MetricsBillingAuditService Service { get; }

        public async Task<Guid[]> QueueAsync(int count)
        {
            var ids = new List<Guid>();
            for (var i = 0; i < count; i++)
            {
                var auditEvent = new BillingAuditEvent
                {
                    EventType = BillingAuditEventType.UsageTracked,
                    Model = "shutdown-metrics"
                };
                ids.Add(auditEvent.Id);
                await Service.LogBillingEventAsync(auditEvent);
            }
            return ids.ToArray();
        }

        public async Task AssertPersistedAsync(IEnumerable<Guid> expectedIds)
        {
            await using var context = _database.CreateContext();
            var savedIds = await context.BillingAuditEvents.Select(e => e.Id).ToListAsync();
            Assert.Equal(expectedIds.OrderBy(id => id), savedIds.OrderBy(id => id));
        }

        public async Task<AuditMetrics> ReadMetricsAsync()
        {
            await using var stream = new MemoryStream();
            await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
            var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
            var serviceLabel = $"service=\"{Service.MetricServiceName}\"";

            double Read(string metric, string? status = null)
            {
                var labels = status is null
                    ? serviceLabel
                    : $"{serviceLabel},status=\"{status}\"";
                var prefix = $"{metric}{{{labels}}} ";
                var line = lines.SingleOrDefault(value =>
                    value.StartsWith(prefix, StringComparison.Ordinal));
                return line is null ? 0 : double.Parse(line[prefix.Length..], CultureInfo.InvariantCulture);
            }

            return new AuditMetrics(
                Read("conduit_audit_events_queued_total"),
                Read("conduit_audit_events_flushed_total", "success"),
                Read("conduit_audit_events_flushed_total", "failure"),
                Read("conduit_audit_queue_depth"));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
            }
            finally
            {
                Service.Dispose();
                await _provider.DisposeAsync();
                await _database.DisposeAsync();
            }
        }
    }

    private sealed class MetricsBillingAuditService(IServiceProvider provider)
        : BillingAuditService(provider, NullLogger<BillingAuditService>.Instance)
    {
        public string MetricServiceName { get; } = $"BillingShutdownMetrics_{Guid.NewGuid():N}";

        protected override string EntityName => MetricServiceName;
        protected override int FlushIntervalSeconds => 1;
        protected override Task ScheduleDataRetentionAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class GatedSaveChangesInterceptor : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public Task Entered => _entered.Task;
        public CancellationToken SaveCancellationToken { get; private set; }
        public void Arm() => Volatile.Write(ref _armed, 1);
        public void Release() => _released.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                SaveCancellationToken = cancellationToken;
                _entered.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
