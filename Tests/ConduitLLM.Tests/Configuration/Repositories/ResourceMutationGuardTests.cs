using System.Data.Common;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Tests.TestInfrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Npgsql;

namespace ConduitLLM.Tests.Configuration.Repositories;

/// <summary>Real transactions with a deterministic commit between the version read and CAS.</summary>
[Collection("MigrationEnvironment")]
public sealed class ResourceMutationGuardTests
{
    private static readonly DateTime InitialTime = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WinnerTime = InitialTime.AddMinutes(1);
    private static readonly byte[] StaticRowVersion = [1, 2, 3, 4];

    public static IEnumerable<object[]> Resources()
    {
        foreach (var provider in new[] { "SQLite", "PostgreSQL" })
        foreach (var resource in new[] { "VirtualKey", "VirtualKeyGroup", "IpFilter" })
            yield return [provider, resource];
    }

    public static IEnumerable<object[]> Writes()
    {
        foreach (var row in Resources())
        foreach (var delete in new[] { false, true })
            yield return [row[0], row[1], delete];
    }

    public static IEnumerable<object[]> LatestConditions()
    {
        foreach (var row in Resources())
        foreach (var wildcard in new[] { false, true })
            yield return [row[0], row[1], wildcard];
    }

    [SkippableTheory]
    [MemberData(nameof(Writes))]
    public async Task InterveningCommit_RejectsStaleUpdateOrDeleteWithoutRunningCallback(
        string provider, string resource, bool delete)
    {
        var race = new BeforeTransaction();
        await using var database = await GuardDatabase.CreateAsync(provider, race);
        var scenario = CreateScenario(resource);
        await scenario.SeedAsync(database);
        var original = await scenario.ReadAsync(database);
        var callbackCount = 0;
        race.Arm(() => scenario.WriteWinnerAsync(database));

        await using var request = database.CreateContext();
        await Assert.ThrowsAsync<ResourcePreconditionFailedException>(() => scenario.ExecuteAsync(
            request, Condition(original.Version.ETag), (db, entity, _) =>
            {
                callbackCount++;
                if (delete) db.Remove(entity);
                else scenario.Change(db, entity, "stale-overwrite", "stale-extra", InitialTime.AddHours(1));
                return Task.CompletedTask;
            }));

        var persisted = await scenario.ReadAsync(database);
        Assert.Equal(0, callbackCount);
        Assert.Equal(1, race.CompetingCommits);
        Assert.Equal("concurrent-winner", persisted.Name);
        Assert.Equal("winner-extra", persisted.Retained);
        Assert.Equal(WinnerTime, persisted.Version.UpdatedAt);
        Assert.Equal(StaticRowVersion, persisted.Version.RowVersion);
        Assert.NotEqual(original.Version.ETag, persisted.Version.ETag);
    }

    [SkippableTheory]
    [MemberData(nameof(LatestConditions))]
    public async Task WildcardOrMatchingAlternative_RetriesLockAndMergesLatestSnapshotOnce(
        string provider, string resource, bool wildcard)
    {
        var race = new BeforeTransaction();
        await using var database = await GuardDatabase.CreateAsync(provider, race);
        var scenario = CreateScenario(resource);
        await scenario.SeedAsync(database);
        var original = await scenario.ReadAsync(database);
        var winnerTag = new ResourceVersion(StaticRowVersion, WinnerTime)
            { VirtualKeyCount = resource == "VirtualKeyGroup" ? 0 : null }.ETag;
        var condition = wildcard ? Condition("*") : Condition(original.Version.ETag + ", " + winnerTag);
        var callbackCount = 0;
        race.Arm(() => scenario.WriteWinnerAsync(database));

        await using var request = database.CreateContext();
        var result = await scenario.ExecuteAsync(request, condition, (db, entity, _) =>
        {
            callbackCount++;
            Assert.Equal("concurrent-winner", scenario.Snapshot(db, entity).Name);
            // A merge patch changes one field and retains the concurrently committed field.
            db.Entry(entity).Property(scenario.NameProperty).CurrentValue = "merged-request";
            db.Entry(entity).Property("UpdatedAt").CurrentValue = InitialTime.AddMinutes(2);
            return Task.CompletedTask;
        });

        Assert.NotNull(result);
        Assert.Equal(1, callbackCount);
        Assert.Equal(1, race.CompetingCommits);
        Assert.Equal(2, race.RequestTransactions);
        Assert.Equal("merged-request", result.Name);
        Assert.Equal("winner-extra", result.Retained);
        var persisted = await scenario.ReadAsync(database);
        Assert.Equal(result, persisted);
    }

    [SkippableTheory]
    [MemberData(nameof(LatestConditions))]
    public async Task PretrackedTarget_RefreshesForWildcardMergeOrRejectsStaleStrongTag(
        string provider, string resource, bool wildcard)
    {
        var race = new BeforeTransaction();
        await using var database = await GuardDatabase.CreateAsync(provider, race);
        var scenario = CreateScenario(resource);
        await scenario.SeedAsync(database);
        var original = await scenario.ReadAsync(database);
        await using var request = database.CreateContext();
        var tracked = await scenario.TrackAsync(request);
        Assert.Equal(original, scenario.Snapshot(request, tracked));
        var callbackCount = 0;
        race.Arm(() => scenario.WriteWinnerAsync(database));

        Task<Snapshot?> Execute() => scenario.ExecuteAsync(request,
            Condition(wildcard ? "*" : original.Version.ETag), (db, entity, _) =>
            {
                callbackCount++;
                Assert.Same(tracked, entity);
                var refreshed = scenario.Snapshot(db, entity);
                Assert.Equal("concurrent-winner", refreshed.Name);
                Assert.Equal("winner-extra", refreshed.Retained);
                Assert.Equal(WinnerTime, refreshed.Version.UpdatedAt);
                db.Entry(entity).Property(scenario.NameProperty).CurrentValue = "merged-from-tracked-context";
                db.Entry(entity).Property("UpdatedAt").CurrentValue = InitialTime.AddMinutes(2);
                return Task.CompletedTask;
            });

        if (wildcard)
        {
            var result = await Execute();
            Assert.NotNull(result);
            Assert.Equal(1, callbackCount);
            Assert.Equal("merged-from-tracked-context", result.Name);
            Assert.Equal("winner-extra", result.Retained);
            Assert.Equal(result, await scenario.ReadAsync(database));
        }
        else
        {
            await Assert.ThrowsAsync<ResourcePreconditionFailedException>(Execute);
            Assert.Equal(0, callbackCount);
            var persisted = await scenario.ReadAsync(database);
            Assert.Equal("concurrent-winner", persisted.Name);
            Assert.Equal("winner-extra", persisted.Retained);
            Assert.Equal(WinnerTime, persisted.Version.UpdatedAt);
        }
        Assert.Equal(1, race.CompetingCommits);
    }

    [SkippableTheory]
    [MemberData(nameof(Resources))]
    public async Task ReturnedTag_UsesPersistedPrecisionAndCanImmediatelyGuardTheNextWrite(
        string provider, string resource)
    {
        await using var database = await GuardDatabase.CreateAsync(provider);
        var scenario = CreateScenario(resource);
        await scenario.SeedAsync(database);
        var original = await scenario.ReadAsync(database);
        var requestedTime = InitialTime.AddMinutes(2).AddTicks(7);

        Snapshot updated;
        await using (var request = database.CreateContext())
        {
            updated = await scenario.ExecuteAsync(request, Condition(original.Version.ETag), (db, entity, _) =>
            {
                scenario.Change(db, entity, "updated", "extra", requestedTime);
                return Task.CompletedTask;
            });
        }

        Assert.NotNull(updated);
        var persisted = await scenario.ReadAsync(database);
        Assert.Equal(persisted, updated);
        Assert.Equal(StaticRowVersion, updated.Version.RowVersion);
        Assert.NotEqual(original.Version.ETag, updated.Version.ETag);
        if (provider == "PostgreSQL")
        {
            Assert.NotEqual(requestedTime.Ticks, updated.Version.UpdatedAt.Ticks);
            Assert.Equal(0, updated.Version.UpdatedAt.Ticks % 10);
        }

        // A no-op patch must still acquire the lock, and its response tag remains usable.
        await using var next = database.CreateContext();
        var noOp = await scenario.ExecuteAsync(next, Condition(updated.Version.ETag), (_, _, _) => Task.CompletedTask);
        Assert.Equal(updated, noOp);
    }

    [SkippableTheory]
    [MemberData(nameof(Resources))]
    public async Task WeakTagFails_WhileStrongTagInMultipleHeaderValuesSucceeds(
        string provider, string resource)
    {
        await using var database = await GuardDatabase.CreateAsync(provider);
        var scenario = CreateScenario(resource);
        await scenario.SeedAsync(database);
        var original = await scenario.ReadAsync(database);
        var callbackCount = 0;

        await using (var weak = database.CreateContext())
        {
            await Assert.ThrowsAsync<ResourcePreconditionFailedException>(() => scenario.ExecuteAsync(
                weak, Condition("W/" + original.Version.ETag), (_, _, _) =>
                {
                    callbackCount++;
                    return Task.CompletedTask;
                }));
        }

        await using var matching = database.CreateContext();
        var result = await scenario.ExecuteAsync(matching,
            new ResourcePrecondition(["\"unrelated\", W/" + original.Version.ETag, " " + original.Version.ETag + " "]),
            (_, _, _) =>
            {
                callbackCount++;
                return Task.CompletedTask;
            });
        Assert.Equal(1, callbackCount);
        Assert.Equal(original, result);
    }

    [SkippableTheory]
    [MemberData(nameof(Resources))]
    public async Task CallbackFailure_RollsBackAllSavedChangesAndDoesNotReplayCallback(
        string provider, string resource)
    {
        await using var database = await GuardDatabase.CreateAsync(provider);
        var scenario = CreateScenario(resource);
        await scenario.SeedAsync(database);
        var original = await scenario.ReadAsync(database);
        var callbackCount = 0;

        await using var request = database.CreateContext();
        await Assert.ThrowsAsync<InjectedFailure>(() => scenario.ExecuteAsync(
            request, Condition(original.Version.ETag), async (db, entity, token) =>
            {
                callbackCount++;
                scenario.Change(db, entity, "rolled-back", "rolled-back-extra", WinnerTime);
                db.VirtualKeyGroups.Add(new VirtualKeyGroup { Id = 99, GroupName = "rolled-back-related-write" });
                await db.SaveChangesAsync(token);
                throw new InjectedFailure();
            }));

        Assert.Equal(1, callbackCount);
        Assert.Equal(original, await scenario.ReadAsync(database));
        await using var verify = database.CreateContext();
        Assert.False(await verify.VirtualKeyGroups.AnyAsync(group => group.Id == 99));
    }

    [SkippableTheory]
    [MemberData(nameof(Resources))]
    public async Task RowVersionOnlyChange_IsAlsoPartOfTheAtomicPrecondition(
        string provider, string resource)
    {
        var race = new BeforeTransaction();
        await using var database = await GuardDatabase.CreateAsync(provider, race);
        var scenario = CreateScenario(resource);
        await scenario.SeedAsync(database);
        var original = await scenario.ReadAsync(database);
        var callbackCount = 0;
        race.Arm(() => scenario.WriteWinnerAsync(database, onlyRowVersion: true));

        await using var request = database.CreateContext();
        await Assert.ThrowsAsync<ResourcePreconditionFailedException>(() => scenario.ExecuteAsync(
            request, Condition(original.Version.ETag), (_, _, _) =>
            {
                callbackCount++;
                return Task.CompletedTask;
            }));

        var persisted = await scenario.ReadAsync(database);
        Assert.Equal(0, callbackCount);
        Assert.Equal(InitialTime, persisted.Version.UpdatedAt);
        Assert.Equal(new byte[] { 9, 8, 7 }, persisted.Version.RowVersion);
        Assert.NotEqual(original.Version.ETag, persisted.Version.ETag);
    }

    private static ResourcePrecondition Condition(string value) => new([value]);

    private static Scenario CreateScenario(string resource) => resource switch
    {
        "VirtualKey" => new Scenario<VirtualKey>(() => new VirtualKey
        {
            Id = 41, KeyName = "initial", KeyHash = "guard-test-key", Description = "initial-extra",
            VirtualKeyGroupId = 31, UpdatedAt = InitialTime
        }, "KeyName", "Description", db =>
            db.VirtualKeyGroups.Add(new VirtualKeyGroup { Id = 31, GroupName = "parent" })),
        "VirtualKeyGroup" => new Scenario<VirtualKeyGroup>(() => new VirtualKeyGroup
        {
            Id = 41, GroupName = "initial", ExternalGroupId = "initial-extra", UpdatedAt = InitialTime
        }, "GroupName", "ExternalGroupId"),
        "IpFilter" => new Scenario<IpFilterEntity>(() => new IpFilterEntity
        {
            Id = 41, Name = "initial", Description = "initial-extra", IpAddressOrCidr = "192.0.2.0/24",
            UpdatedAt = InitialTime
        }, "Name", "Description"),
        _ => throw new ArgumentOutOfRangeException(nameof(resource))
    };

    private sealed record Snapshot(string Name, string Retained, ResourceVersion Version)
    {
        // Arrays in ResourceVersion intentionally preserve database bytes; compare snapshot contents.
        public bool Equals(Snapshot? other) => other is not null && Name == other.Name &&
            Retained == other.Retained && Version.ETag == other.Version.ETag;
        public override int GetHashCode() => HashCode.Combine(Name, Retained, Version.ETag);
    }

    private abstract class Scenario(string nameProperty, string retainedProperty)
    {
        public string NameProperty { get; } = nameProperty;
        protected string RetainedProperty { get; } = retainedProperty;
        public abstract Task SeedAsync(GuardDatabase database);
        public abstract Task<Snapshot> ReadAsync(GuardDatabase database);
        public abstract Task<object> TrackAsync(ConduitDbContext db);
        public abstract Task WriteWinnerAsync(GuardDatabase database, bool onlyRowVersion = false);
        public abstract Task<Snapshot?> ExecuteAsync(ConduitDbContext db, ResourcePrecondition condition,
            Func<ConduitDbContext, object, CancellationToken, Task> mutation);

        public Snapshot Snapshot(ConduitDbContext db, object entity)
        {
            var entry = db.Entry(entity);
            return new Snapshot((string)entry.Property(NameProperty).CurrentValue!,
                (string)entry.Property(RetainedProperty).CurrentValue!,
                new ResourceVersion((byte[]?)entry.Property("RowVersion").CurrentValue,
                    (DateTime)entry.Property("UpdatedAt").CurrentValue!)
                    { VirtualKeyCount = entity is VirtualKeyGroup ? 0 : null });
        }

        public void Change(ConduitDbContext db, object entity, string name, string retained, DateTime timestamp)
        {
            var entry = db.Entry(entity);
            entry.Property(NameProperty).CurrentValue = name;
            entry.Property(RetainedProperty).CurrentValue = retained;
            entry.Property("UpdatedAt").CurrentValue = timestamp;
        }
    }

    private sealed class Scenario<TEntity>(Func<TEntity> createEntity, string nameProperty,
        string retainedProperty, Action<ConduitDbContext>? seedDependencies = null)
        : Scenario(nameProperty, retainedProperty) where TEntity : class
    {
        private static IQueryable<TEntity> Resource(ConduitDbContext db) => db.Set<TEntity>()
            .Where(entity => EF.Property<int>(entity, "Id") == 41);

        public override async Task SeedAsync(GuardDatabase database)
        {
            await using var db = database.CreateContext();
            seedDependencies?.Invoke(db);
            db.Add(createEntity());
            await db.SaveChangesAsync();
            // PostgreSQL does not generate the legacy bytea [Timestamp] column. Exercise
            // constant nonempty bytes as well as the timestamp used by repository updates.
            await Resource(db).ExecuteUpdateAsync(setters => setters.SetProperty(
                entity => EF.Property<byte[]?>(entity, "RowVersion"), StaticRowVersion));
        }

        public override async Task<Snapshot> ReadAsync(GuardDatabase database)
        {
            await using var db = database.CreateContext();
            return Snapshot(db, await Resource(db).AsNoTracking().SingleAsync());
        }

        public override async Task<object> TrackAsync(ConduitDbContext db) => await Resource(db).SingleAsync();

        public override async Task WriteWinnerAsync(GuardDatabase database, bool onlyRowVersion = false)
        {
            await using var db = database.CreateContext();
            if (onlyRowVersion)
            {
                await Resource(db).ExecuteUpdateAsync(setters => setters.SetProperty(
                    entity => EF.Property<byte[]?>(entity, "RowVersion"), new byte[] { 9, 8, 7 }));
            }
            else
            {
                await Resource(db).ExecuteUpdateAsync(setters => setters
                    .SetProperty(entity => EF.Property<string>(entity, NameProperty), "concurrent-winner")
                    .SetProperty(entity => EF.Property<string>(entity, RetainedProperty), "winner-extra")
                    .SetProperty(entity => EF.Property<DateTime>(entity, "UpdatedAt"), WinnerTime));
            }
        }

        public override Task<Snapshot?> ExecuteAsync(ConduitDbContext db, ResourcePrecondition condition,
            Func<ConduitDbContext, object, CancellationToken, Task> mutation) =>
            ResourceMutationGuard.ExecuteAsync<TEntity, Snapshot>(db, 41, condition,
                (context, entity, token) => mutation(context, entity, token), entity => Snapshot(db, entity));
    }

    private sealed class BeforeTransaction : DbTransactionInterceptor
    {
        private Func<Task>? _competingCommit;
        public int CompetingCommits { get; private set; }
        public int RequestTransactions { get; private set; }

        public void Arm(Func<Task> competingCommit)
        {
            RequestTransactions = 0;
            _competingCommit = competingCommit;
        }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            RequestTransactions++;
            var competingCommit = Interlocked.Exchange(ref _competingCommit, null);
            if (competingCommit is not null)
            {
                await competingCommit();
                CompetingCommits++;
            }
            return result;
        }
    }

    private sealed class InjectedFailure : Exception { }

    private sealed class GuardDatabase : IAsyncDisposable
    {
        private readonly SqliteTestDatabase? _sqlite;
        private readonly string? _databaseName;
        private readonly string? _adminConnectionString;
        private readonly DbContextOptions<ConduitDbContext>? _options;

        private GuardDatabase(SqliteTestDatabase sqlite) => _sqlite = sqlite;
        private GuardDatabase(string name, string admin, DbContextOptions<ConduitDbContext> options)
        {
            _databaseName = name;
            _adminConnectionString = admin;
            _options = options;
        }

        public ConduitDbContext CreateContext() => _sqlite?.CreateContext() ?? new ConduitDbContext(_options!);

        public static async Task<GuardDatabase> CreateAsync(string provider, params IInterceptor[] interceptors)
        {
            if (provider == "SQLite") return new GuardDatabase(new SqliteTestDatabase(interceptors));
            var configured = Environment.GetEnvironmentVariable("DATABASE_URL");
            if (string.IsNullOrWhiteSpace(configured) &&
                (Environment.GetEnvironmentVariable("CI") == "true" ||
                 Environment.GetEnvironmentVariable("CONDUIT_CI_REQUIRED_INFRASTRUCTURE") == "true"))
                throw new InvalidOperationException("Required conditional-mutation database DATABASE_URL is missing.");
            Skip.If(string.IsNullOrWhiteSpace(configured), "DATABASE_URL is required for PostgreSQL conditional-mutation tests.");

            var source = ConfigurationDbContextFactory.ResolveNpgsqlConnectionString();
            var name = $"conduit_resource_guard_{Guid.NewGuid():N}";
            var admin = new NpgsqlConnectionStringBuilder(source) { Database = "postgres", Pooling = false };
            var target = new NpgsqlConnectionStringBuilder(source) { Database = name, Pooling = false };
            await using (var connection = new NpgsqlConnection(admin.ConnectionString))
            {
                await connection.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
                await create.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<ConduitDbContext>()
                .UseNpgsql(target.ConnectionString, postgres => postgres.EnableRetryOnFailure())
                .AddInterceptors(interceptors).Options;
            var database = new GuardDatabase(name, admin.ConnectionString, options);
            try
            {
                await using var db = database.CreateContext();
                await db.Database.EnsureCreatedAsync();
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_sqlite is not null)
            {
                await _sqlite.DisposeAsync();
                return;
            }
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
