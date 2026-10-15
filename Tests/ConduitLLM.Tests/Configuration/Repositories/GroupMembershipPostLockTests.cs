using System.Data.Common;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Configuration.Utilities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

namespace ConduitLLM.Tests.Configuration.Repositories;

/// <summary>A real PostgreSQL membership commit after the guard acquires its group row lock.</summary>
[Collection("MigrationEnvironment")]
public sealed class GroupMembershipPostLockTests
{
    private static readonly DateTime OriginalTime = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipCommittedAfterSuccessfulCas_IsRecheckedBeforeTheCallback(bool wildcard)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        await using var fixture = await OwnedDatabase.CreateAsync(token);
        ResourceVersion original;
        await using (var before = fixture.CreateContext())
        {
            original = (await ResourceMutationGuard.ReadVersionAsync<VirtualKeyGroup>(before, 1, token))!;
            Assert.Equal(0, original.VirtualKeyCount);
        }

        var interceptor = new AfterSuccessfulCas();
        await using var request = fixture.CreateContext(interceptor);
        var keys = new VirtualKeyRepository(fixture, NullLogger<VirtualKeyRepository>.Instance);
        interceptor.Arm(request, async ct =>
        {
            await keys.CreateAsync(new VirtualKey
            {
                KeyName = "post-lock-member", KeyHash = Guid.NewGuid().ToString("N"),
                VirtualKeyGroupId = 1, IsEnabled = true
            }, ct);
        });
        var callbackCount = 0;

        Task<VirtualKeyGroup?> MutateAsync() => ResourceMutationGuard.ExecuteAsync<VirtualKeyGroup, VirtualKeyGroup>(
            request, 1, new ResourcePrecondition([wildcard ? "*" : original.ETag]), async (db, group, ct) =>
            {
                callbackCount++;
                Assert.Equal(1, interceptor.CompetingCommits);
                Assert.Equal(1, await db.VirtualKeys.CountAsync(key => key.VirtualKeyGroupId == 1, ct));
                group.GroupName = "guarded-wildcard";
                group.UpdatedAt = OriginalTime.AddMinutes(1);
            }, group => group, token);

        if (wildcard)
        {
            var updated = await MutateAsync();
            Assert.NotNull(updated);
            Assert.Equal("guarded-wildcard", updated.GroupName);
            Assert.Equal(1, callbackCount);
        }
        else
        {
            await Assert.ThrowsAsync<ResourcePreconditionFailedException>(MutateAsync);
            Assert.Equal(0, callbackCount);
        }

        Assert.Equal(1, interceptor.SuccessfulCasCommands);
        Assert.Equal(1, interceptor.CompetingCommits);
        Assert.Contains("SELECT count", interceptor.CasSql, StringComparison.OrdinalIgnoreCase);
        await using var verify = fixture.CreateContext();
        var persisted = await verify.VirtualKeyGroups.AsNoTracking().SingleAsync(group => group.Id == 1, token);
        Assert.Equal(wildcard ? "guarded-wildcard" : "original-group", persisted.GroupName);
        Assert.Equal(wildcard ? OriginalTime.AddMinutes(1) : OriginalTime, persisted.UpdatedAt);
        Assert.Equal(100m, persisted.Balance);
        Assert.Equal(120m, persisted.LifetimeCreditsAdded);
        Assert.Equal(20m, persisted.LifetimeSpent);
        Assert.Equal(30, persisted.RateLimitRpm);
        Assert.Single(await verify.VirtualKeys.AsNoTracking().ToListAsync(token));
        Assert.Empty(await verify.VirtualKeyGroupTransactions.AsNoTracking().ToListAsync(token));
        var latest = (await ResourceMutationGuard.ReadVersionAsync<VirtualKeyGroup>(verify, 1, token))!;
        Assert.Equal(1, latest.VirtualKeyCount);
        Assert.NotEqual(original.ETag, latest.ETag);
    }

    private sealed class AfterSuccessfulCas : DbCommandInterceptor
    {
        private ConduitDbContext? _request;
        private Func<CancellationToken, Task>? _commit;
        public int SuccessfulCasCommands { get; private set; }
        public int CompetingCommits { get; private set; }
        public string CasSql { get; private set; } = string.Empty;

        public void Arm(ConduitDbContext request, Func<CancellationToken, Task> commit)
        {
            _request = request;
            _commit = commit;
        }

        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (result != 1 || !ReferenceEquals(eventData.Context, _request) ||
                !command.CommandText.StartsWith("UPDATE \"VirtualKeyGroups\"", StringComparison.Ordinal))
                return result;
            var commit = Interlocked.Exchange(ref _commit, null);
            if (commit is null) return result;
            Assert.NotNull(command.Transaction);
            SuccessfulCasCommands++;
            CasSql = command.CommandText;
            // FK KEY SHARE can commit while this no-key group UPDATE holds its row lock.
            // Return to the guard only after that independent membership write commits.
            await commit(cancellationToken);
            CompetingCommits++;
            return result;
        }
    }

    private sealed class OwnedDatabase : IDbContextFactory<ConduitDbContext>, IAsyncDisposable
    {
        private readonly string _name;
        private readonly string _admin;
        private readonly DbContextOptions<ConduitDbContext> _options;

        private OwnedDatabase(string name, string admin, DbContextOptions<ConduitDbContext> options)
        {
            _name = name;
            _admin = admin;
            _options = options;
        }

        public ConduitDbContext CreateDbContext() => CreateContext();
        public Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateContext());
        }

        public ConduitDbContext CreateContext(params IInterceptor[] interceptors) => new(
            new DbContextOptionsBuilder<ConduitDbContext>(_options).AddInterceptors(interceptors).Options);

        public static async Task<OwnedDatabase> CreateAsync(CancellationToken token)
        {
            var configured = Environment.GetEnvironmentVariable("DATABASE_URL");
            if (string.IsNullOrWhiteSpace(configured) &&
                (Environment.GetEnvironmentVariable("CI") == "true" ||
                 Environment.GetEnvironmentVariable("CONDUIT_CI_REQUIRED_INFRASTRUCTURE") == "true"))
                throw new InvalidOperationException("Required post-lock membership database DATABASE_URL is missing.");
            Skip.If(string.IsNullOrWhiteSpace(configured), "DATABASE_URL is required for PostgreSQL post-lock membership tests.");

            var source = ConfigurationDbContextFactory.ResolveNpgsqlConnectionString();
            var name = $"conduit_group_postlock_{Guid.NewGuid():N}";
            var admin = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false, CommandTimeout = 10 };
            var target = new NpgsqlConnectionStringBuilder(source) { Database = name, Pooling = false, CommandTimeout = 10 };
            var options = new DbContextOptionsBuilder<ConduitDbContext>()
                .UseNpgsql(target.ConnectionString, postgres => postgres.EnableRetryOnFailure()).Options;
            var fixture = new OwnedDatabase(name, admin.ConnectionString, options);
            try
            {
                await using (var connection = new NpgsqlConnection(admin.ConnectionString))
                {
                    await connection.OpenAsync(token);
                    await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
                    await command.ExecuteNonQueryAsync(token);
                }
                await using var db = fixture.CreateContext();
                await db.Database.EnsureCreatedAsync(token);
                db.VirtualKeyGroups.Add(new VirtualKeyGroup
                {
                    Id = 1, GroupName = "original-group", CreatedAt = OriginalTime, UpdatedAt = OriginalTime,
                    Balance = 100m, LifetimeCreditsAdded = 120m, LifetimeSpent = 20m, RateLimitRpm = 30
                });
                await db.SaveChangesAsync(token);
                return fixture;
            }
            catch (Exception primary)
            {
                // Any partial setup failure must release this fixture's exact
                // generated database, retaining both errors if cleanup also fails.
                try { await fixture.DisposeAsync(); }
                catch (Exception cleanup)
                {
                    throw new AggregateException("Post-lock fixture creation and cleanup failed.", primary, cleanup);
                }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var connection = new NpgsqlConnection(_admin);
            await connection.OpenAsync(cleanup.Token);
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync(cleanup.Token);
            await using var verify = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @name", connection);
            verify.Parameters.AddWithValue("name", _name);
            Assert.Equal(0L, (long)(await verify.ExecuteScalarAsync(cleanup.Token))!);
        }
    }
}
