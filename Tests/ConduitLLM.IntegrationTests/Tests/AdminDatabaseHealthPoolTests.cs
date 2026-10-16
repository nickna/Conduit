using System.Data;

using ConduitLLM.Admin.Extensions;
using ConduitLLM.Core.Data.Interfaces;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using Npgsql;

namespace ConduitLLM.IntegrationTests.Tests;

[CollectionDefinition("Admin database health pool", DisableParallelization = true)]
public sealed class AdminDatabaseHealthPoolCollection : ICollectionFixture<PostgresLockTestContainerFixture>;

[Collection("Admin database health pool")]
[Trait("Category", "Integration")]
[Trait("Component", "DatabaseHealth")]
public sealed class AdminDatabaseHealthPoolTests(PostgresLockTestContainerFixture fixture)
{
    [Fact]
    public async Task HealthAndRawConnectionsReuseTheWarmedAdminHostPool()
    {
        await using var admin = AdminServices.Create(fixture.ConnectionString);
        var settings = new NpgsqlConnectionStringBuilder(admin.Source.ConnectionString);
        Assert.Equal(5, settings.MinPoolSize);
        Assert.Equal(75, settings.MaxPoolSize);
        await admin.Provider.GetRequiredService<IConnectionPoolWarmup>().WarmConnectionAsync();
        int warmedPid;
        await using (var warmed = await admin.Source.OpenConnectionAsync())
            warmedPid = warmed.ProcessID;

        var factory = admin.Provider.GetRequiredService<IDatabaseConnectionFactory>();
        Assert.Equal("postgres", factory.ProviderName);
        await using (var raw = factory.CreateConnection())
        {
            Assert.Equal(ConnectionState.Closed, raw.State);
            await raw.OpenAsync();
            Assert.Equal(warmedPid, Assert.IsType<NpgsqlConnection>(raw).ProcessID);
        }
        await using (var raw = await factory.CreateConnectionAsync())
            Assert.Equal(warmedPid, Assert.IsType<NpgsqlConnection>(raw).ProcessID);

        AssertHealthy(await admin.CheckHealthAsync());
        await using var observer = await OpenObserverAsync();
        Assert.Equal(new[] { warmedPid }, await HostSessionsAsync(observer, admin.ApplicationName));
        await AssertSourceUsableAsync(admin.Source);
    }

    [Fact]
    public async Task ExhaustedHostPoolMakesHealthUnhealthyAndRecoversWhenTheBorrowedLeaseReturns()
    {
        await using var admin = AdminServices.Create(fixture.ConnectionString, singleConnection: true);
        await admin.Provider.GetRequiredService<IConnectionPoolWarmup>().WarmConnectionAsync();
        await using var occupied = await admin.Source.OpenConnectionAsync();
        var occupiedPid = occupied.ProcessID;

        var waitingHealth = admin.CheckHealthAsync();
        Assert.False(waitingHealth.IsCompleted);
        var unavailable = await waitingHealth;
        Assert.Equal(HealthStatus.Unhealthy, unavailable.Status);
        Assert.NotNull(unavailable.Entries["database_health"].Exception);

        await occupied.DisposeAsync();
        AssertHealthy(await admin.CheckHealthAsync());
        await using var raw = await admin.Provider.GetRequiredService<IDatabaseConnectionFactory>().CreateConnectionAsync();
        Assert.Equal(occupiedPid, Assert.IsType<NpgsqlConnection>(raw).ProcessID);
    }

    [Fact]
    public async Task DatabaseRejectsConnections_HealthReportsFailureThenRecoversOnTheSameHostSource()
    {
        // The failure is confined to a newly created database owned by this test.
        var databaseName = "conduit_admin_health_" + Guid.NewGuid().ToString("N");
        await using var observer = await OpenObserverAsync();
        await ExecuteAsync(observer, $"CREATE DATABASE \"{databaseName}\"");
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName };
            await using var admin = AdminServices.Create(settings.ConnectionString, singleConnection: true);
            await admin.Provider.GetRequiredService<IConnectionPoolWarmup>().WarmConnectionAsync();
            int warmedPid;
            await using (var connection = await admin.Source.OpenConnectionAsync())
                warmedPid = connection.ProcessID;
            AssertHealthy(await admin.CheckHealthAsync());

            await ExecuteAsync(observer, $"ALTER DATABASE \"{databaseName}\" ALLOW_CONNECTIONS false");
            await using (var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(@pid, 5000)", observer))
            {
                terminate.Parameters.AddWithValue("pid", warmedPid);
                Assert.Equal(true, await terminate.ExecuteScalarAsync());
            }
            var failed = await admin.CheckHealthAsync();
            Assert.Equal(HealthStatus.Unhealthy, failed.Status);
            Assert.NotNull(failed.Entries["database_health"].Exception);

            await ExecuteAsync(observer, $"ALTER DATABASE \"{databaseName}\" ALLOW_CONNECTIONS true");
            AssertHealthy(await admin.CheckHealthAsync());
            await AssertSourceUsableAsync(admin.Source);
            await using var recovered = await admin.Source.OpenConnectionAsync();
            Assert.NotEqual(warmedPid, recovered.ProcessID);
        }
        finally
        {
            // The identifier comes only from the fixed prefix and a fresh GUID above.
            await ExecuteAsync(observer, $"DROP DATABASE \"{databaseName}\" WITH (FORCE)");
        }
    }

    [Fact]
    public async Task HostDisposalClosesItsPoolWhileAdvisoryOwnershipUsesAnIndependentPool()
    {
        await using var admin = AdminServices.Create(fixture.ConnectionString, singleConnection: true);
        var source = admin.Source;
        var locks = admin.Provider.GetRequiredService<IDistributedLockProvider>();
        var key = "test:admin-health-pool:" + Guid.NewGuid().ToString("N");
        await using var hostLease = await source.OpenConnectionAsync();
        // MaxPoolSize=1 is occupied. A lock can still acquire its separate session.
        await using var ownership = await locks.TryAcquireAsync(key).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(ownership);
        Assert.False(ownership.HandleLostToken.IsCancellationRequested);
        await using var observer = await OpenObserverAsync();
        Assert.Contains(hostLease.ProcessID, await HostSessionsAsync(observer, admin.ApplicationName));

        await hostLease.DisposeAsync();
        AssertHealthy(await admin.CheckHealthAsync());
        await admin.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.OpenConnectionAsync().AsTask());
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while ((await HostSessionsAsync(observer, admin.ApplicationName)).Length != 0)
                await Task.Delay(25, deadline.Token);
        }
        Assert.Empty(await HostSessionsAsync(observer, admin.ApplicationName));
        Assert.False(ownership.HandleLostToken.IsCancellationRequested);
        Assert.Null(await locks.TryAcquireAsync(key));
        await ownership.DisposeAsync();
        await using var successor = await locks.TryAcquireAsync(key);
        Assert.NotNull(successor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledRawOpensPreserveCancellationAndReuseTheHostPool(bool alreadyCancelled)
    {
        await using var admin = AdminServices.Create(fixture.ConnectionString, singleConnection: true);
        var factory = admin.Provider.GetRequiredService<IDatabaseConnectionFactory>();
        await using var occupied = await factory.CreateConnectionAsync();
        var occupiedPid = Assert.IsType<NpgsqlConnection>(occupied).ProcessID;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var cancellation = new CancellationTokenSource();
            if (alreadyCancelled) cancellation.Cancel();
            var opening = factory.CreateConnectionAsync(cancellation.Token);
            if (!alreadyCancelled)
            {
                // CreateConnectionAsync runs synchronously until the real max-one
                // pool has to wait for the connection retained above.
                Assert.False(opening.IsCompleted);
                cancellation.Cancel();
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(ConnectionState.Open, occupied.State);
            await using var command = occupied.CreateCommand();
            command.CommandText = "SELECT 1";
            Assert.Equal(1, await command.ExecuteScalarAsync());
        }

        await occupied.DisposeAsync();
        await using (var recovered = await factory.CreateConnectionAsync())
            Assert.Equal(occupiedPid, Assert.IsType<NpgsqlConnection>(recovered).ProcessID);
        await using (var reused = await factory.CreateConnectionAsync())
            Assert.Equal(occupiedPid, Assert.IsType<NpgsqlConnection>(reused).ProcessID);
        await AssertSourceUsableAsync(admin.Source);
        AssertHealthy(await admin.CheckHealthAsync());
    }

    [Fact]
    public async Task RegisteredHealthCheckPropagatesCallerCancellationAndLeavesTheHostPoolUsable()
    {
        await using var admin = AdminServices.Create(fixture.ConnectionString, singleConnection: true);
        var factory = admin.Provider.GetRequiredService<IDatabaseConnectionFactory>();
        await using var occupied = await factory.CreateConnectionAsync();
        var occupiedPid = Assert.IsType<NpgsqlConnection>(occupied).ProcessID;
        var registration = Assert.Single(admin.Provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations, value => value.Name == "database_health");
        var check = registration.Factory(admin.Provider);
        using var cancellation = new CancellationTokenSource();

        // Invoke the production registration directly so cancellation happens
        // after its factory has entered the occupied pool, rather than before
        // HealthCheckService schedules the check's work.
        var checking = check.CheckHealthAsync(new HealthCheckContext { Registration = registration }, cancellation.Token);
        Assert.False(checking.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checking.WaitAsync(TimeSpan.FromSeconds(5)));

        await occupied.DisposeAsync();
        AssertHealthy(await admin.CheckHealthAsync());
        await using (var recovered = await factory.CreateConnectionAsync())
            Assert.Equal(occupiedPid, Assert.IsType<NpgsqlConnection>(recovered).ProcessID);
        await AssertSourceUsableAsync(admin.Source);
    }

    [Fact]
    public async Task FailedRawOpensKeepPostgresDiagnosticsAndRecoverOnTheSameHostSource()
    {
        var databaseName = "conduit_admin_open_" + Guid.NewGuid().ToString("N");
        await using var observer = await OpenObserverAsync();
        await ExecuteAsync(observer, $"CREATE DATABASE \"{databaseName}\"");
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName };
            await using var admin = AdminServices.Create(settings.ConnectionString, singleConnection: true);
            var source = admin.Source;
            var factory = admin.Provider.GetRequiredService<IDatabaseConnectionFactory>();
            await ExecuteAsync(observer, $"ALTER DATABASE \"{databaseName}\" ALLOW_CONNECTIONS false");

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    factory.CreateConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5)));
                var postgres = Assert.IsType<PostgresException>(failure.InnerException);
                Assert.Equal("55000", postgres.SqlState);
            }

            await ExecuteAsync(observer, $"ALTER DATABASE \"{databaseName}\" ALLOW_CONNECTIONS true");
            await using (var recovered = await factory.CreateConnectionAsync())
            {
                await using var command = recovered.CreateCommand();
                command.CommandText = "SELECT 1";
                Assert.Equal(1, await command.ExecuteScalarAsync());
            }
            Assert.Same(source, admin.Source);
            await AssertSourceUsableAsync(source);
            AssertHealthy(await admin.CheckHealthAsync());
        }
        finally
        {
            // The identifier comes only from this test's fixed prefix and GUID.
            await ExecuteAsync(observer, $"DROP DATABASE \"{databaseName}\" WITH (FORCE)");
        }
    }

    private async Task<NpgsqlConnection> OpenObserverAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection observer, string sql)
    {
        await using var command = new NpgsqlCommand(sql, observer) { CommandTimeout = 10 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int[]> HostSessionsAsync(NpgsqlConnection observer, string applicationName)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pid FROM pg_stat_activity WHERE application_name = @application ORDER BY pid", observer);
        command.Parameters.AddWithValue("application", applicationName);
        var sessions = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) sessions.Add(reader.GetInt32(0));
        return sessions.ToArray();
    }

    private static void AssertHealthy(HealthReport report)
    {
        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Equal(HealthStatus.Healthy, Assert.Single(report.Entries).Value.Status);
    }

    private static async Task AssertSourceUsableAsync(NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        Assert.Equal(1, await command.ExecuteScalarAsync());
    }

    private sealed class AdminServices : IAsyncDisposable
    {
        private readonly EnvironmentScope _environment;
        private bool _disposed;
        public ServiceProvider Provider { get; }
        public NpgsqlDataSource Source => Provider.GetRequiredService<NpgsqlDataSource>();
        public string ApplicationName { get; } = "conduit-admin-health-" + Guid.NewGuid().ToString("N");

        private AdminServices(string connectionString, bool singleConnection)
        {
            var database = new NpgsqlConnectionStringBuilder(connectionString);
            var url = $"postgresql://{database.Username}:{database.Password}@{database.Host}:{database.Port}/{database.Database}" +
                $"?ApplicationName={ApplicationName}";
            if (singleConnection)
                url += "&Pooling=true&MinPoolSize=0&MaxPoolSize=1&Timeout=1&CommandTimeout=2";
            _environment = new EnvironmentScope(new Dictionary<string, string?> { ["DATABASE_URL"] = url });
            try
            {
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddCoreServices(new ConfigurationBuilder().Build());
                services.AddConduitDistributedLocks();
                Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            }
            catch
            {
                _environment.Dispose();
                throw;
            }
        }

        public static AdminServices Create(string connectionString, bool singleConnection = false) =>
            new(connectionString, singleConnection);

        public Task<HealthReport> CheckHealthAsync() => Provider.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration => registration.Name == "database_health")
            .WaitAsync(TimeSpan.FromSeconds(10));

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { await Provider.DisposeAsync(); }
            finally { _environment.Dispose(); }
        }
    }
}
