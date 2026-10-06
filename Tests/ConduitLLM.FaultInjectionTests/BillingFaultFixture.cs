using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Configuration.Services;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace ConduitLLM.FaultInjectionTests;

[CollectionDefinition(Name)]
public sealed class BillingFaultCollection : ICollectionFixture<BillingFaultFixture>
{
    public const string Name = "billing-fault-injection";
}

public sealed class BillingFaultFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("conduit_faults")
        .WithUsername("conduit")
        .WithPassword("conduitpass")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7.4-alpine")
        .Build();

    public string PostgreSqlConnectionString => _postgres.GetConnectionString();
    public string RedisConnectionString => $"{_redis.Hostname}:{_redis.GetMappedPublicPort(6379)},abortConnect=false,connectTimeout=1000,syncTimeout=1000";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }

    public ConduitDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ConduitDbContext>()
            .UseNpgsql(PostgreSqlConnectionString)
            .Options;
        return new ConduitDbContext(options);
    }

    public async Task<(int GroupId, int KeyId)> SeedAccountAsync(decimal balance = 100m)
    {
        await using var db = CreateDbContext();
        var suffix = Guid.NewGuid().ToString("N");
        var group = new VirtualKeyGroup
        {
            GroupName = $"fault-{suffix}",
            Balance = balance,
            LifetimeCreditsAdded = balance
        };
        var key = new VirtualKey
        {
            KeyName = $"fault-{suffix}",
            KeyHash = $"hash-{suffix}",
            VirtualKeyGroup = group
        };
        db.Add(key);
        await db.SaveChangesAsync();
        return (group.Id, key.Id);
    }

    public BatchSpendUpdateService CreateBatchService(
        IVirtualKeyGroupRepository? repository = null,
        ILogger<BatchSpendUpdateService>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<ConduitDbContext>(options =>
            options.UseNpgsql(PostgreSqlConnectionString));
        services.AddScoped<IConfigurationDbContext>(provider => provider.GetRequiredService<ConduitDbContext>());
        services.AddScoped<IVirtualKeyGroupRepository>(provider => repository ??
            new VirtualKeyGroupRepository(
                provider.GetRequiredService<IDbContextFactory<ConduitDbContext>>(),
                NullLogger<VirtualKeyGroupRepository>.Instance));

        var provider = services.BuildServiceProvider();
        var redisFactory = new RedisConnectionFactory(
            Options.Create(new CacheOptions { RedisConnectionString = RedisConnectionString }),
            NullLogger<RedisConnectionFactory>.Instance);
        return new BatchSpendUpdateService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            redisFactory,
            Options.Create(new BatchSpendingOptions
            {
                FlushIntervalSeconds = 3600,
                MinimumIntervalSeconds = 1,
                MaximumIntervalSeconds = 7200,
                RedisTtlHours = 24
            }),
            logger ?? NullLogger<BatchSpendUpdateService>.Instance,
            Mock.Of<IBillingAlertingService>());
    }

    public async Task<BatchSpendUpdateService> StartBatchServiceAsync()
    {
        var startup = new InitialFlushLogger();
        var batch = CreateBatchService(logger: startup);
        try
        {
            await batch.StartAsync(CancellationToken.None);
            // StartAsync does not await ExecuteAsync's initial recovery scan. Let
            // that scan finish before enqueueing the fault scenario so accounting
            // assertions cannot observe a DB debit plus its unacknowledged Redis claim.
            await startup.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            batch.IsHealthy.Should().BeTrue();
            return batch;
        }
        catch
        {
            await batch.DisposeAsync();
            throw;
        }
    }

    private sealed class InitialFlushLogger : ILogger<BatchSpendUpdateService>
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message == "No pending spend updates to flush" ||
                message.StartsWith("Flushed ", StringComparison.Ordinal) &&
                message.EndsWith("pending updates from previous session", StringComparison.Ordinal))
                Completed.TrySetResult();
            else if (message == "Error flushing pending updates on startup")
                Completed.TrySetException(exception ?? new InvalidOperationException(message));
        }
    }

    public VirtualKeyGroupRepository CreateGroupRepository() => new(
        new TestDbContextFactory(PostgreSqlConnectionString),
        NullLogger<VirtualKeyGroupRepository>.Instance);

    public VirtualKeyRepository CreateKeyRepository() => new(
        new TestDbContextFactory(PostgreSqlConnectionString),
        NullLogger<VirtualKeyRepository>.Instance);

    public async Task StopRedisAsync() => await _redis.StopAsync();
    public async Task StartRedisAsync() => await _redis.StartAsync();
    public async Task StopPostgresAsync() => await _postgres.StopAsync();
    public async Task StartPostgresAsync() => await _postgres.StartAsync();

    public async Task AssertAccountedForAsync(int groupId, decimal expected)
    {
        await using var db = CreateDbContext();
        var committed = await db.VirtualKeyGroupTransactions
            .Where(transaction => transaction.VirtualKeyGroupId == groupId &&
                                  transaction.TransactionType == TransactionType.Debit)
            .SumAsync(transaction => (decimal?)transaction.Amount) ?? 0m;

        await using var redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
        var database = redis.GetDatabase();
        var server = redis.GetServer(redis.GetEndPoints()[0]);
        var queued = 0m;
        queued += await SumUnitsAsync(server, database, $"pending_spend_window_units:group:{groupId}:window:*");
        queued += await SumUnitsAsync(server, database, $"processing_spend_window_units:group:{groupId}:window:*:claim:*");
        queued += await ReadDecimalAsync(database, $"pending_spend:group:{groupId}");
        queued += await ReadUnitsAsync(database, $"pending_spend_units:group:{groupId}");
        queued += await SumDecimalAsync(server, database, $"processing_spend:group:{groupId}:claim:*");
        queued += await SumUnitsAsync(server, database, $"processing_spend_units:group:{groupId}:claim:*");

        (committed + queued).Should().Be(expected,
            "incurred spend must be committed or durably queued");

        var idempotencyKeys = await db.VirtualKeyGroupTransactions
            .Where(transaction => transaction.VirtualKeyGroupId == groupId &&
                                  transaction.TransactionType == TransactionType.Debit &&
                                  transaction.IdempotencyKey != null)
            .Select(transaction => transaction.IdempotencyKey!)
            .ToListAsync();
        idempotencyKeys.Should().OnlyHaveUniqueItems();
    }

    private static async Task<decimal> SumUnitsAsync(IServer server, IDatabase database, string pattern)
    {
        decimal total = 0;
        foreach (var key in server.Keys(pattern: pattern)) total += await ReadUnitsAsync(database, key);
        return total;
    }

    private static async Task<decimal> SumDecimalAsync(IServer server, IDatabase database, string pattern)
    {
        decimal total = 0;
        foreach (var key in server.Keys(pattern: pattern)) total += await ReadDecimalAsync(database, key);
        return total;
    }

    private static async Task<decimal> ReadUnitsAsync(IDatabase database, RedisKey key)
    {
        var value = await database.StringGetAsync(key);
        return value.HasValue ? long.Parse(value!) / 100_000_000m : 0m;
    }

    private static async Task<decimal> ReadDecimalAsync(IDatabase database, RedisKey key)
    {
        var value = await database.StringGetAsync(key);
        return value.HasValue ? decimal.Parse(value.ToString(), System.Globalization.CultureInfo.InvariantCulture) : 0m;
    }

    private sealed class TestDbContextFactory(string connectionString) : IDbContextFactory<ConduitDbContext>
    {
        public ConduitDbContext CreateDbContext() => new DbContextOptionsBuilder<ConduitDbContext>()
            .UseNpgsql(connectionString)
            .Options is var options ? new ConduitDbContext(options) : throw new InvalidOperationException();
    }
}
