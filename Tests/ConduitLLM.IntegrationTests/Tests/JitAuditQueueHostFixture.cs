using System.Collections.Concurrent;
using System.Net;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Extensions;
using ConduitLLM.Gateway.Interfaces;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Moq;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

internal sealed class JitAuditQueueHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly NpgsqlDataSource _source;
    private readonly BeforeElectionLogGate _gate;
    private HttpClient? _client;
    private Task? _stopping;
    public AuditCommitWitness Writes { get; } = new();
    public AuditFollowerElection Election { get; } = new();
    public Task BeforeFirstElectionEntered => _gate.BothEntered;

    private JitAuditQueueHost(string connectionString, string name, bool holdBeforeElection)
    {
        _gate = new BeforeElectionLogGate(holdBeforeElection);
        _source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"jit_audit_{name}_{Guid.NewGuid():N}",
            MaxPoolSize = 1, MinPoolSize = 0, Timeout = 5, CommandTimeout = 5
        }.ConnectionString);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Production,
                ApplicationName = typeof(JitAuditQueueHost).Assembly.GetName().Name
            });
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddProvider(_gate);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.Configure<HostOptions>(options =>
            {
                options.ServicesStopConcurrently = true;
                options.ShutdownTimeout = TimeSpan.FromSeconds(15);
            });
            var options = new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(_source).Options;
            builder.Services.AddSingleton<IDbContextFactory<ConduitDbContext>>(new AuditContextFactory(options, Writes));
            builder.Services.AddScoped<ConduitDbContext>(_ => new AuditObservedContext(options, Writes));
            builder.Services.AddSingleton<ILeaderElectionService>(Election);
            builder.Services.AddSingleton(Mock.Of<IOperationalAlertPublisher>());
            builder.Services.AddAuditServices();
            builder.Services.AddBillingAndPricingServices();
            builder.Services.Configure<BillingReconciliationOptions>(value => value.Enabled = false);
            _app = builder.Build();
            _app.MapPost("/audit/{requestId:guid}", async (Guid requestId,
                IBillingAuditService billing, IRequestLogRuntimeWriter requests) =>
            {
                var path = "/audit/" + requestId.ToString("N");
                await billing.LogBillingEventAsync(new BillingAuditEvent
                {
                    Id = requestId, RequestId = requestId.ToString("N"), Timestamp = DateTime.UtcNow,
                    EventType = BillingAuditEventType.UsageTracked, VirtualKeyId = 41, VirtualKeyGroupId = 7,
                    Model = name, CalculatedCost = 0.12500001m, ProviderType = "fixture",
                    HttpStatusCode = 200, RequestPath = path, UsageJson = """{"count":1}"""
                });
                await requests.LogRequestAsync(new LogRequestDto
                {
                    VirtualKeyId = 41, ModelName = name, ProviderType = "fixture", RequestType = "chat",
                    InputTokens = 2, OutputTokens = 3, Cost = 0.12500001m,
                    Timestamp = DateTime.UtcNow, RequestPath = path, StatusCode = 200, ResponseTimeMs = 12.5
                });
                return Results.NoContent();
            });
        }
        catch
        {
            _gate.Release();
            _source.Dispose();
            _gate.DisposeAfterWorkersJoined();
            throw;
        }
    }

    public static JitAuditQueueHost Create(string connectionString, string name, bool holdBeforeElection) =>
        new(connectionString, name, holdBeforeElection);

    public async Task StartAsync(CancellationToken token)
    {
        await _app.StartAsync(token).WaitAsync(TimeSpan.FromSeconds(15), token);
        var server = _app.Services.GetRequiredService<IServer>();
        var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
        _client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
    }

    public void AssertProductionComposition()
    {
        Assert.IsType<BillingAuditService>(_app.Services.GetRequiredService<IBillingAuditService>());
        var requests = Assert.IsType<RequestLogService>(_app.Services.GetRequiredService<IRequestLogService>());
        Assert.Same(requests, _app.Services.GetRequiredService<IRequestLogRuntimeWriter>());
        var names = _app.Services.GetServices<IHostedService>().OfType<LeaderElectedServiceWrapper>()
            .Select(service => service.ServiceName).ToArray();
        Assert.Contains("BillingAuditService", names);
        Assert.Contains("RequestLogService", names);
    }

    public async Task EnqueueThroughHttpAsync(Guid request, CancellationToken token)
    {
        using var response = await _client!.PostAsync("/audit/" + request.ToString("N"), null, token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public Task StopAsync(CancellationToken token) => _stopping ??= _app.StopAsync(token);

    public async Task StopBeforeFirstElectionAsync(CancellationToken token)
    {
        var stopping = StopAsync(token);
        _gate.Release();
        await stopping.WaitAsync(TimeSpan.FromSeconds(15), token);
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Release();
        await IntegrationProofCleanup.RunAsync(async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await StopAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(20));
        }, async () =>
        {
            _client?.Dispose();
            try
            {
                await IntegrationProofCleanup.RunAsync(() => _app.DisposeAsync().AsTask(),
                    () => _source.DisposeAsync().AsTask(), "Audit host disposal and data-source cleanup failed.");
            }
            finally
            {
                _gate.DisposeAfterWorkersJoined();
            }
        }, "Audit host stopping and owned cleanup failed.");
    }

    private sealed class AuditContextFactory(DbContextOptions<ConduitDbContext> options, AuditCommitWitness writes)
        : IDbContextFactory<ConduitDbContext>
    {
        public ConduitDbContext CreateDbContext() => new AuditObservedContext(options, writes);
        public Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }

    private sealed class AuditObservedContext(DbContextOptions<ConduitDbContext> options, AuditCommitWitness writes)
        : ConduitDbContext(options)
    {
        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var billing = ChangeTracker.Entries<BillingAuditEvent>().Any(entry => entry.State == EntityState.Added);
            var requests = ChangeTracker.Entries<RequestLog>().Any(entry => entry.State == EntityState.Added);
            var saved = await base.SaveChangesAsync(cancellationToken);
            if (billing) writes.BillingCommitted.TrySetResult();
            if (requests) writes.RequestCommitted.TrySetResult();
            return saved;
        }
    }

    private sealed class BeforeElectionLogGate(bool enabled) : ILoggerProvider
    {
        private readonly ManualResetEventSlim _release = new(!enabled);
        private readonly ConcurrentDictionary<string, byte> _entered = new();
        private readonly TaskCompletionSource _bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task BothEntered => _bothEntered.Task;
        public ILogger CreateLogger(string categoryName) => new GateLogger(this);
        public void Release() => _release.Set();
        public void Dispose() => Release();
        public void DisposeAfterWorkersJoined() => _release.Dispose();

        private void Observe<TState>(TState state)
        {
            if (!enabled || state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
            var values = fields.ToDictionary(pair => pair.Key, pair => pair.Value);
            if (!values.TryGetValue("{OriginalFormat}", out var format) ||
                !Equals(format, "Starting leader-elected service {ServiceName}") ||
                !values.TryGetValue("ServiceName", out var name) ||
                name is not string service || !AuditFollowerElection.AuditNames.Contains(service)) return;
            _entered.TryAdd(service, 0);
            if (_entered.Count == 2) _bothEntered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("Owned audit pre-election logger latch was not released.");
        }

        private sealed class GateLogger(BeforeElectionLogGate owner) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => true;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => owner.Observe(state);
        }
    }
}

internal sealed class AuditCommitWitness
{
    public TaskCompletionSource BillingCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource RequestCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class AuditFollowerElection : ILeaderElectionService
{
    internal static readonly string[] AuditNames = ["BillingAuditService", "RequestLogService"];
    private readonly ConcurrentDictionary<string, int> _checks = new();
    private readonly ConcurrentDictionary<string, int> _acquisitions = new();
    private readonly TaskCompletionSource _bothFollowers = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task BothAuditFollowersObserved => _bothFollowers.Task;

    public Task<bool> IsLeaderAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _checks.AddOrUpdate(serviceName, 1, (_, count) => count + 1);
        return Task.FromResult(false);
    }

    public Task<LeadershipAcquisitionResult> TryAcquireLeadershipAsync(
        string serviceName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _acquisitions.AddOrUpdate(serviceName, 1, (_, count) => count + 1);
        if (AuditNames.All(_acquisitions.ContainsKey)) _bothFollowers.TrySetResult();
        return Task.FromResult(LeadershipAcquisitionResult.Failure());
    }

    public void AssertFollowerChecksOccurred()
    {
        foreach (var service in AuditNames)
        {
            Assert.True(_checks.TryGetValue(service, out var checks) && checks > 0, service);
            Assert.True(_acquisitions.TryGetValue(service, out var attempts) && attempts > 0, service);
        }
    }

    public void AssertNoAuditChecksOrAcquisitions()
    {
        foreach (var service in AuditNames)
        {
            Assert.False(_checks.ContainsKey(service), service);
            Assert.False(_acquisitions.ContainsKey(service), service);
        }
    }

    public Task ReleaseLeadershipAsync(string serviceName, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("A never-leader must not release a lease.");
    public Task<bool> ValidateFencingTokenAsync(string serviceName, long fencingToken,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<string?> GetCurrentLeaderAsync(string serviceName, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>("independent-leader");
    public Task<long?> GetCurrentFencingTokenAsync(string serviceName, CancellationToken cancellationToken = default) =>
        Task.FromResult<long?>(null);
    public Task StartLeadershipMaintenanceAsync(string serviceName, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("A never-leader must not maintain a lease.");
    public Task StopLeadershipMaintenanceAsync(string serviceName) => Task.CompletedTask;
}

internal sealed class JitAuditQueueDatabase : IAsyncDisposable
{
    private readonly string _management;
    private readonly string _schema = "jit_audit_host_" + Guid.NewGuid().ToString("N");
    private readonly NpgsqlDataSource _setupSource;
    public string ConnectionString { get; }

    private JitAuditQueueDatabase(string management)
    {
        _management = new NpgsqlConnectionStringBuilder(management) { Pooling = false }.ConnectionString;
        ConnectionString = new NpgsqlConnectionStringBuilder(management)
        {
            SearchPath = _schema, ApplicationName = _schema, MaxPoolSize = 1,
            MinPoolSize = 0, Timeout = 5, CommandTimeout = 10
        }.ConnectionString;
        _setupSource = NpgsqlDataSource.Create(ConnectionString);
    }

    public static async Task<JitAuditQueueDatabase> CreateAsync(string management)
    {
        var owned = new JitAuditQueueDatabase(management);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using (var connection = new NpgsqlConnection(owned._management))
            {
                await connection.OpenAsync(deadline.Token);
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE SCHEMA \"{owned._schema}\"";
                await command.ExecuteNonQueryAsync(deadline.Token);
            }
            await using var context = new ConduitDbContext(new DbContextOptionsBuilder<ConduitDbContext>()
                .UseNpgsql(owned._setupSource).Options);
            await context.Database.OpenConnectionAsync(deadline.Token);
            await using (var command = context.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = context.Database.GenerateCreateScript();
                await command.ExecuteNonQueryAsync(deadline.Token);
            }
            context.AddRange(new VirtualKeyGroup { Id = 7, GroupName = "owned audit hosts" },
                new VirtualKey { Id = 41, KeyName = "owned host key", KeyHash = "owned-audit-host-key", VirtualKeyGroupId = 7 });
            await context.SaveChangesAsync(deadline.Token);
            return owned;
        }
        catch (Exception primary)
        {
            try { await owned.DisposeAsync(); }
            catch (Exception cleanup) { throw new AggregateException("Audit schema setup and cleanup failed.", primary, cleanup); }
            throw;
        }
    }

    public async Task AssertExactRowsAsync(IReadOnlyList<Guid> expected, CancellationToken token)
    {
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ConnectionString)
            { Pooling = false, ApplicationName = "jit_audit_independent_observer" }.ConnectionString);
        await observer.OpenAsync(token);
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT "Id", "RequestId", "CalculatedCost", "RequestPath", "HttpStatusCode"
            FROM "BillingAuditEvents" ORDER BY "RequestId";
            SELECT "Id", "Cost", "RequestPath", "StatusCode", "InputTokens", "OutputTokens"
            FROM "RequestLogs" ORDER BY "RequestPath";
            """;
        await using var rows = await command.ExecuteReaderAsync(token);
        var billing = new List<Guid>();
        while (await rows.ReadAsync(token))
        {
            var id = rows.GetGuid(0);
            billing.Add(id);
            Assert.Equal(id.ToString("N"), rows.GetString(1));
            Assert.Equal(0.12500001m, rows.GetDecimal(2));
            Assert.Equal("/audit/" + id.ToString("N"), rows.GetString(3));
            Assert.Equal(200, rows.GetInt32(4));
        }
        Assert.Equal(expected.Order(), billing.Order());
        Assert.Equal(billing.Count, billing.Distinct().Count());
        Assert.True(await rows.NextResultAsync(token));
        var requests = new List<string>();
        while (await rows.ReadAsync(token))
        {
            Assert.True(rows.GetInt32(0) > 0);
            Assert.Equal(0.12500001m, rows.GetDecimal(1));
            requests.Add(rows.GetString(2));
            Assert.Equal(200, rows.GetInt32(3));
            Assert.Equal(2, rows.GetInt32(4));
            Assert.Equal(3, rows.GetInt32(5));
        }
        Assert.Equal(expected.Select(id => "/audit/" + id.ToString("N")).Order(), requests.Order());
        Assert.Equal(requests.Count, requests.Distinct().Count());
    }

    public async ValueTask DisposeAsync()
    {
        await _setupSource.DisposeAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var connection = new NpgsqlConnection(_management);
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE";
        await command.ExecuteNonQueryAsync(deadline.Token);
        command.CommandText = "SELECT count(*) FROM pg_namespace WHERE nspname = @schema";
        command.Parameters.AddWithValue("schema", _schema);
        Assert.Equal(0L, await command.ExecuteScalarAsync(deadline.Token));
    }
}
