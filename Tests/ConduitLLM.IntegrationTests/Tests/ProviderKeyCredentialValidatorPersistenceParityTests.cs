using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Services;
using ConduitLLM.IntegrationTests.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

/// <summary>Direct validator contract on real PostgreSQL, with independent stored-row and log oracles.</summary>
[Collection("Postgres advisory locks")]
public sealed class ProviderKeyCredentialValidatorPersistenceParityTests(PostgresLockTestContainerFixture fixture)
{
    private static readonly TimeSpan TestLimit = TimeSpan.FromSeconds(90);
    private enum Operation { Add, Primary, Disable, Enabled }

    [Fact]
    public void ConstructorValidatesBothDependencies()
    {
        var factory = new CredentialContextFactory(new DbContextOptionsBuilder<ConduitDbContext>().Options);
        Assert.Equal("dbContextFactory", Assert.Throws<ArgumentNullException>(() =>
            new ProviderKeyCredentialValidator(null!, NullLogger<ProviderKeyCredentialValidator>.Instance)).ParamName);
        Assert.Equal("logger", Assert.Throws<ArgumentNullException>(() =>
            new ProviderKeyCredentialValidator(factory, null!)).ParamName);
        Assert.Equal(0, factory.Attempts);
        Assert.Equal(0, factory.Created);
    }

    [Fact]
    public async Task CredentialLimitCountsEveryExactProviderRowAtZero31_32_33()
    {
        var schema = Schema("limit");
        using var deadline = new CancellationTokenSource(TestLimit);
        try
        {
            await CreateSchemaAsync(schema, deadline.Token);
            using var sql = new CredentialCommandGate();
            await using var source = Source(schema, 1, sql);
            var factory = new CredentialContextFactory(source);
            var logger = new CredentialWarnings();
            var validator = new ProviderKeyCredentialValidator(factory, logger);
            foreach (var count in new[] { 0, 31, 32, 33 })
            {
                await SeedCountAsync(schema, count, deadline.Token);
                var before = await SnapshotAsync(schema, deadline.Token);
                logger.Clear();
                var result = await validator.ValidateAddKeyAsync(100, deadline.Token);
                AssertResult(result, count < 32 ? null : "Provider already has the maximum of 32 keys");
                if (count < 32) Assert.Empty(logger.Events);
                else AssertWarning(Assert.Single(logger.Events),
                    "Credential add rejected: provider {ProviderId} already has {CurrentCount}/{Maximum} keys",
                    $"Credential add rejected: provider 100 already has {count}/32 keys",
                    ("ProviderId", 100), ("CurrentCount", count), ("Maximum", 32));
                Assert.Equal(before, await SnapshotAsync(schema, deadline.Token));
            }
            // The owner does not validate provider existence or positive identifiers.
            foreach (var id in new[] { -1, 0, 999999 })
            {
                logger.Clear();
                AssertResult(await validator.ValidateAddKeyAsync(id, deadline.Token), null);
                Assert.Empty(logger.Events);
            }
            Assert.Equal(7, factory.Created);
            Assert.Equal(factory.Created, factory.AsyncDisposed);
            Assert.All(factory.TrackedEntryCounts, count => Assert.Equal(0, count));
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Fact]
    public async Task ExistingAndMissingFlagsProduceExactResultsAndWarningsWithoutMutation()
    {
        var schema = Schema("flags");
        using var deadline = new CancellationTokenSource(TestLimit);
        try
        {
            await CreateSchemaAsync(schema, deadline.Token);
            await SeedFlagsAsync(schema, deadline.Token);
            using var sql = new CredentialCommandGate();
            await using var source = Source(schema, 1, sql);
            var factory = new CredentialContextFactory(source);
            var logger = new CredentialWarnings();
            var validator = new ProviderKeyCredentialValidator(factory, logger);
            var before = await SnapshotAsync(schema, deadline.Token);
            // These three pairs are admitted by the production primary-must-be-enabled check.
            foreach (var row in new[] { (Id: 100, Enabled: true, Primary: false),
                         (Id: 101, Enabled: true, Primary: true), (Id: 102, Enabled: false, Primary: false) })
            {
                logger.Clear();
                AssertResult(await validator.ValidateSetPrimaryAsync(row.Id, deadline.Token),
                    row.Enabled ? null : "Cannot set a disabled key as primary");
                if (row.Enabled) Assert.Empty(logger.Events);
                else AssertWarning(Assert.Single(logger.Events), "Set-primary rejected: key {KeyId} is disabled",
                    $"Set-primary rejected: key {row.Id} is disabled", ("KeyId", row.Id));
                logger.Clear();
                AssertResult(await validator.ValidateDisableKeyAsync(row.Id, deadline.Token),
                    row.Primary ? "Cannot disable a primary key. Set another key as primary first." : null);
                if (!row.Primary) Assert.Empty(logger.Events);
                else AssertWarning(Assert.Single(logger.Events), "Disable rejected: key {KeyId} is primary",
                    $"Disable rejected: key {row.Id} is primary", ("KeyId", row.Id));
            }
            foreach (var id in new[] { -1, 0, 999999 })
            {
                logger.Clear();
                AssertResult(await validator.ValidateSetPrimaryAsync(id, deadline.Token), "key not found");
                AssertWarning(Assert.Single(logger.Events), "Set-primary rejected: key {KeyId} not found",
                    $"Set-primary rejected: key {id} not found", ("KeyId", id));
                logger.Clear();
                AssertResult(await validator.ValidateDisableKeyAsync(id, deadline.Token), "key not found");
                AssertWarning(Assert.Single(logger.Events), "Disable rejected: key {KeyId} not found",
                    $"Disable rejected: key {id} not found", ("KeyId", id));
            }
            Assert.Equal(before, await SnapshotAsync(schema, deadline.Token));
            Assert.Equal(12, factory.Created);
            Assert.Equal(factory.Created, factory.AsyncDisposed);
            Assert.All(factory.TrackedEntryCounts, count => Assert.Equal(0, count));
            Assert.All(logger.Events, entry => Assert.DoesNotContain("fixture-secret", entry.Formatted));
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Fact]
    public async Task APreexistingDisabledPrimaryRowRetainsBothIndependentFlagRejections()
    {
        var schema = Schema("legacy_flags");
        using var deadline = new CancellationTokenSource(TestLimit);
        try
        {
            await CreateSchemaAsync(schema, deadline.Token);
            // The fourth flag pair is forbidden in current storage. First prove that constraint,
            // then admit one explicit legacy row before re-adding the same check NOT VALID.
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema,
                CredentialInsert(103, 100, false, true), deadline.Token));
            Assert.Equal(PostgresErrorCodes.CheckViolation, rejected.SqlState);
            Assert.Equal("CK_ProviderKeyCredential_PrimaryMustBeEnabled", rejected.ConstraintName);
            await ExecuteAsync(schema, $"""
                ALTER TABLE "{schema}"."ProviderKeyCredentials" DROP CONSTRAINT "CK_ProviderKeyCredential_PrimaryMustBeEnabled";
                {CredentialInsert(103, 100, false, true)};
                ALTER TABLE "{schema}"."ProviderKeyCredentials" ADD CONSTRAINT "CK_ProviderKeyCredential_PrimaryMustBeEnabled"
                    CHECK ("IsPrimary" = false OR "IsEnabled" = true) NOT VALID;
                """, deadline.Token);
            using var sql = new CredentialCommandGate();
            await using var source = Source(schema, 1, sql);
            var factory = new CredentialContextFactory(source);
            var logger = new CredentialWarnings();
            var validator = new ProviderKeyCredentialValidator(factory, logger);
            var before = await SnapshotAsync(schema, deadline.Token);
            AssertResult(await validator.ValidateSetPrimaryAsync(103, deadline.Token), "Cannot set a disabled key as primary");
            AssertWarning(Assert.Single(logger.Events), "Set-primary rejected: key {KeyId} is disabled",
                "Set-primary rejected: key 103 is disabled", ("KeyId", 103));
            logger.Clear();
            AssertResult(await validator.ValidateDisableKeyAsync(103, deadline.Token),
                "Cannot disable a primary key. Set another key as primary first.");
            AssertWarning(Assert.Single(logger.Events), "Disable rejected: key {KeyId} is primary",
                "Disable rejected: key 103 is primary", ("KeyId", 103));
            Assert.Equal(before, await SnapshotAsync(schema, deadline.Token));
            Assert.Equal(2, factory.AsyncDisposed);
            Assert.All(factory.TrackedEntryCounts, count => Assert.Equal(0, count));
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Fact]
    public async Task EnabledExistenceUsesOnlyTheExactProviderCredentialFlag()
    {
        var schema = Schema("enabled");
        using var deadline = new CancellationTokenSource(TestLimit);
        try
        {
            await CreateSchemaAsync(schema, deadline.Token);
            using var sql = new CredentialCommandGate();
            await using var source = Source(schema, 1, sql);
            var factory = new CredentialContextFactory(source);
            var logger = new CredentialWarnings();
            var validator = new ProviderKeyCredentialValidator(factory, logger);
            foreach (var flags in new[] { Array.Empty<bool>(), new[] { false, false }, new[] { false, true } })
            {
                await SeedCountAsync(schema, 0, deadline.Token);
                for (var index = 0; index < flags.Length; index++)
                    await ExecuteAsync(schema, CredentialInsert(100 + index, 100, flags[index], false), deadline.Token);
                var before = await SnapshotAsync(schema, deadline.Token);
                logger.Clear();
                var expected = flags.Any(enabled => enabled) ? null : "Provider must have at least one enabled key";
                AssertResult(await validator.ValidateProviderHasEnabledKeyAsync(100, deadline.Token), expected);
                if (expected is null) Assert.Empty(logger.Events);
                else AssertWarning(Assert.Single(logger.Events), "Validation failed: provider {ProviderId} has no enabled keys",
                    "Validation failed: provider 100 has no enabled keys", ("ProviderId", 100));
                Assert.Equal(before, await SnapshotAsync(schema, deadline.Token));
            }
            // Provider 100 itself is disabled; enabled credentials still qualify. Provider 200's
            // enabled keys never make the missing provider or a disabled-only provider succeed.
            foreach (var id in new[] { -1, 0, 999999 })
            {
                logger.Clear();
                AssertResult(await validator.ValidateProviderHasEnabledKeyAsync(id, deadline.Token),
                    "Provider must have at least one enabled key");
                AssertWarning(Assert.Single(logger.Events), "Validation failed: provider {ProviderId} has no enabled keys",
                    $"Validation failed: provider {id} has no enabled keys", ("ProviderId", id));
            }
            Assert.Equal(6, factory.AsyncDisposed);
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Fact]
    public async Task FactoryTokenAndAwaitedAsyncDisposalSurviveSuccessAndFailures()
    {
        var schema = Schema("lifetime");
        using var deadline = new CancellationTokenSource(TestLimit);
        try
        {
            await CreateSchemaAsync(schema, deadline.Token);
            await SeedSuccessAsync(schema, deadline.Token);
            using var sql = new CredentialCommandGate();
            await using var source = Source(schema, 1, sql);
            var before = await SnapshotAsync(schema, deadline.Token);
            foreach (var operation in Enum.GetValues<Operation>())
            {
                var factory = new CredentialContextFactory(source);
                var logger = new CredentialWarnings();
                var validator = new ProviderKeyCredentialValidator(factory, logger);
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                factory.BeforeDispose = async () => { entered.TrySetResult(true); await release.Task.WaitAsync(deadline.Token); };
                var pending = InvokeAsync(validator, operation, deadline.Token);
                try
                {
                    await entered.Task.WaitAsync(deadline.Token);
                    Assert.False(pending.IsCompleted);
                    Assert.Equal(deadline.Token, factory.LastToken);
                    Assert.Equal(1, factory.Created);
                    Assert.Equal(0, factory.AsyncDisposed);
                    release.TrySetResult(true);
                    AssertResult(await pending.WaitAsync(deadline.Token), null);
                    Assert.Equal(1, factory.AsyncDisposed);
                    Assert.Empty(logger.Events);
                    Assert.Equal(0, Assert.Single(factory.TrackedEntryCounts));
                }
                finally { release.TrySetResult(true); await pending.WaitAsync(TimeSpan.FromSeconds(5)); }

                var failure = new InvalidOperationException("fixture factory failure");
                var failingFactory = new CredentialContextFactory(source) { BeforeCreate = _ => Task.FromException(failure) };
                var failingValidator = new ProviderKeyCredentialValidator(failingFactory, logger);
                var beforeSql = sql.Started;
                Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    InvokeAsync(failingValidator, operation, deadline.Token)));
                Assert.Equal(deadline.Token, failingFactory.LastToken);
                Assert.Equal(1, failingFactory.Attempts);
                Assert.Equal(0, failingFactory.Created);
                Assert.Equal(0, failingFactory.AsyncDisposed);
                Assert.Equal(beforeSql, sql.Started);
                Assert.Empty(logger.Events);

                using var factoryCancelled = new CancellationTokenSource();
                var factoryEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var neverReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var waitingFactory = new CredentialContextFactory(source)
                {
                    BeforeCreate = async token => { factoryEntered.TrySetResult(true); await neverReleased.Task.WaitAsync(token); }
                };
                var factoryPending = InvokeAsync(new ProviderKeyCredentialValidator(waitingFactory, logger),
                    operation, factoryCancelled.Token);
                try
                {
                    await factoryEntered.Task.WaitAsync(deadline.Token);
                    Assert.False(factoryPending.IsCompleted);
                    Assert.Equal(factoryCancelled.Token, waitingFactory.LastToken);
                    factoryCancelled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factoryPending.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.Equal(1, waitingFactory.Attempts);
                    Assert.Equal(0, waitingFactory.Created);
                    Assert.Equal(0, waitingFactory.AsyncDisposed);
                    Assert.Equal(beforeSql, sql.Started);
                    Assert.Empty(logger.Events);
                }
                finally { await ReapAsync(factoryPending, factoryCancelled); }

                var disposalFailure = new InvalidOperationException("fixture disposal failure");
                var disposalFactory = new CredentialContextFactory(source)
                    { BeforeDispose = () => Task.FromException(disposalFailure) };
                Assert.Same(disposalFailure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    InvokeAsync(new ProviderKeyCredentialValidator(disposalFactory, logger), operation, deadline.Token)));
                Assert.Equal(deadline.Token, disposalFactory.LastToken);
                Assert.Equal(1, disposalFactory.Created);
                Assert.Equal(1, disposalFactory.AsyncDisposed);
                Assert.Empty(logger.Events);
            }
            await ExecuteAsync(schema, $"ALTER TABLE \"{schema}\".\"ProviderKeyCredentials\" RENAME TO \"UnavailableCredentials\"", deadline.Token);
            try
            {
                foreach (var operation in Enum.GetValues<Operation>())
                {
                    var factory = new CredentialContextFactory(source);
                    var logger = new CredentialWarnings();
                    var error = await Assert.ThrowsAsync<PostgresException>(() =>
                        InvokeAsync(new ProviderKeyCredentialValidator(factory, logger), operation, deadline.Token));
                    Assert.Equal(PostgresErrorCodes.UndefinedTable, error.SqlState);
                    Assert.Equal(deadline.Token, factory.LastToken);
                    Assert.Equal(1, factory.Created);
                    Assert.Equal(1, factory.AsyncDisposed);
                    Assert.Empty(logger.Events);
                }
            }
            finally { await ExecuteAsync(schema, $"ALTER TABLE \"{schema}\".\"UnavailableCredentials\" RENAME TO \"ProviderKeyCredentials\"", deadline.Token); }
            Assert.Equal(before, await SnapshotAsync(schema, deadline.Token));
            await PoolUsableAsync(source, deadline.Token);
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Fact]
    public async Task AllFourMethodsHonorPreCancellationBeforeContextOrSql()
    {
        var schema = Schema("precancel");
        using var deadline = new CancellationTokenSource(TestLimit);
        try
        {
            await CreateSchemaAsync(schema, deadline.Token);
            await SeedSuccessAsync(schema, deadline.Token);
            using var sql = new CredentialCommandGate();
            await using var source = Source(schema, 1, sql);
            var before = await SnapshotAsync(schema, deadline.Token);
            foreach (var operation in Enum.GetValues<Operation>())
            {
                var factory = new CredentialContextFactory(source);
                var logger = new CredentialWarnings();
                var validator = new ProviderKeyCredentialValidator(factory, logger);
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(validator, operation, cancelled.Token));
                Assert.Equal(cancelled.Token, factory.LastToken);
                Assert.Equal(1, factory.Attempts);
                Assert.Equal(0, factory.Created);
                Assert.Equal(0, factory.AsyncDisposed);
                Assert.Equal(0, sql.Started);
                Assert.Empty(logger.Events);
            }
            Assert.Equal(before, await SnapshotAsync(schema, deadline.Token));
            await PoolUsableAsync(source, deadline.Token);
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AllFourMethodsCancelAtAnOccupiedPoolAndRecover(int capacity)
    {
        var schema = Schema($"pool{capacity}");
        using var deadline = new CancellationTokenSource(TestLimit);
        try
        {
            await CreateSchemaAsync(schema, deadline.Token);
            await SeedSuccessAsync(schema, deadline.Token);
            using var sql = new CredentialCommandGate();
            await using var source = Source(schema, capacity, sql);
            var before = await SnapshotAsync(schema, deadline.Token);
            foreach (var operation in Enum.GetValues<Operation>())
            {
                var factory = new CredentialContextFactory(source);
                var logger = new CredentialWarnings();
                var validator = new ProviderKeyCredentialValidator(factory, logger);
                var holds = new List<NpgsqlConnection>();
                using var cancelled = new CancellationTokenSource();
                Task<ConduitValidationResult>? pending = null;
                try
                {
                    for (var slot = 0; slot < capacity; slot++) holds.Add(await source.OpenConnectionAsync(deadline.Token));
                    Assert.Equal(capacity, holds.Select(connection => connection.ProcessID).Distinct().Count());
                    var beforeSql = sql.Started;
                    pending = InvokeAsync(validator, operation, cancelled.Token);
                    Assert.False(pending.IsCompleted);
                    Assert.Equal(beforeSql, sql.Started);
                    cancelled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.Equal(cancelled.Token, factory.LastToken);
                    Assert.Equal(1, factory.Created);
                    Assert.Equal(1, factory.AsyncDisposed);
                    Assert.Empty(logger.Events);
                }
                finally
                {
                    cancelled.Cancel();
                    try { if (pending is not null) await ReapAsync(pending, cancelled); }
                    finally { foreach (var hold in holds) await hold.DisposeAsync(); }
                }
                Assert.Equal(before, await SnapshotAsync(schema, deadline.Token));
                await PoolUsableAsync(source, deadline.Token);
                AssertResult(await InvokeAsync(validator, operation, deadline.Token), null);
                Assert.Equal(2, factory.Created);
                Assert.Equal(2, factory.AsyncDisposed);
                Assert.Empty(logger.Events);
            }
        }
        finally { await DropSchemaAsync(schema); }
    }

    [Fact]
    public async Task AllFourTerminalsCancelAtObservedPostgreSqlWaitsAndRecover()
    {
        var schema = Schema("sql");
        using var deadline = new CancellationTokenSource(TestLimit);
        try
        {
            await CreateSchemaAsync(schema, deadline.Token);
            await SeedSuccessAsync(schema, deadline.Token);
            var before = await SnapshotAsync(schema, deadline.Token);
            foreach (var operation in Enum.GetValues<Operation>())
            {
                using var gate = new CredentialCommandGate(block: true);
                await using var source = Source(schema, 1, gate);
                var factory = new CredentialContextFactory(source);
                var logger = new CredentialWarnings();
                var validator = new ProviderKeyCredentialValidator(factory, logger);
                var readerPid = await PoolUsableAsync(source, deadline.Token);
                await using var blocker = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(gate.SourceConnectionString)
                    { Pooling = false, ApplicationName = "credential_blocker" }.ConnectionString);
                await blocker.OpenAsync(deadline.Token);
                await using var transaction = await blocker.BeginTransactionAsync(deadline.Token);
                using var cancelled = new CancellationTokenSource();
                gate.Arm();
                var pending = Task.Run(() => InvokeAsync(validator, operation, cancelled.Token));
                try
                {
                    var entry = await gate.Entered.WaitAsync(deadline.Token);
                    Assert.Equal(1, entry.Ordinal);
                    Assert.Equal(0, entry.CompletedBefore);
                    Assert.StartsWith("SELECT", entry.Sql.TrimStart(), StringComparison.OrdinalIgnoreCase);
                    await using (var command = blocker.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = $"LOCK TABLE \"{schema}\".\"ProviderKeyCredentials\" IN ACCESS EXCLUSIVE MODE";
                        await command.ExecuteNonQueryAsync(deadline.Token);
                    }
                    gate.Release();
                    await WaitForRelationWaitAsync(gate.SourceConnectionString, readerPid, blocker.ProcessID,
                        schema, pending, deadline.Token);
                    cancelled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.Equal(cancelled.Token, factory.LastToken);
                    Assert.Equal(1, factory.Created);
                    Assert.Equal(1, factory.AsyncDisposed);
                    Assert.Empty(logger.Events);
                }
                finally
                {
                    gate.Release();
                    try { await ReapAsync(pending, cancelled); }
                    finally { await transaction.RollbackAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
                }
                Assert.Equal(before, await SnapshotAsync(schema, deadline.Token));
                await PoolUsableAsync(source, deadline.Token);
                AssertResult(await InvokeAsync(validator, operation, deadline.Token), null);
                Assert.Equal(2, factory.Created);
                Assert.Equal(2, factory.AsyncDisposed);
                Assert.Empty(logger.Events);
            }
        }
        finally { await DropSchemaAsync(schema); }
    }

    private static Task<ConduitValidationResult> InvokeAsync(ProviderKeyCredentialValidator validator,
        Operation operation, CancellationToken token) => operation switch
    {
        Operation.Add => validator.ValidateAddKeyAsync(100, token),
        Operation.Primary => validator.ValidateSetPrimaryAsync(100, token),
        Operation.Disable => validator.ValidateDisableKeyAsync(100, token),
        Operation.Enabled => validator.ValidateProviderHasEnabledKeyAsync(100, token),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static void AssertResult(ConduitValidationResult result, string? errorMessage)
    {
        Assert.Equal(errorMessage is null, result.IsValid);
        Assert.Empty(result.Warnings);
        if (errorMessage is null) Assert.Empty(result.Errors);
        else
        {
            var error = Assert.Single(result.Errors);
            Assert.Equal(string.Empty, error.Field);
            Assert.Equal(errorMessage, error.Message);
            Assert.Null(error.RuleIndex);
        }
    }

    private static void AssertWarning(Warning entry, string template, string formatted,
        params (string Key, int Value)[] expected)
    {
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(0, entry.EventId.Id);
        Assert.Null(entry.Exception);
        Assert.Equal(template, entry.Template);
        Assert.Equal(formatted, entry.Formatted);
        Assert.Equal(expected.Length, entry.Fields.Count);
        foreach (var (key, value) in expected) Assert.Equal(value, Assert.IsType<int>(entry.Fields[key]));
    }

    private static string Schema(string purpose) => $"key_validator_{purpose}_{Guid.NewGuid():N}";

    private NpgsqlDataSource Source(string schema, int capacity, CredentialCommandGate logger)
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            SearchPath = schema, ApplicationName = $"pc_e_{Guid.NewGuid():N}",
            MaxPoolSize = capacity, MinPoolSize = 0, Multiplexing = false,
            Timeout = 5, CommandTimeout = 15
        };
        Assert.InRange(builder.ApplicationName.Length, 1, 63);
        logger.SourceConnectionString = builder.ConnectionString; // Private observer input; never logged.
        return new NpgsqlDataSourceBuilder(builder.ConnectionString).UseLoggerFactory(logger)
            .EnableParameterLogging(false).Build();
    }

    private async Task CreateSchemaAsync(string schema, CancellationToken token)
    {
        await ExecuteAsync(schema, $"""
            CREATE SCHEMA "{schema}";
            CREATE TABLE "{schema}"."Providers" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "ProviderType" integer NOT NULL, "ProviderName" varchar(100) NOT NULL,
                "BaseUrl" text NULL, "Settings" jsonb NULL, "IsEnabled" boolean NOT NULL,
                "TrustProviderReportedCosts" boolean NOT NULL, "ProviderCostMarkupMultiplier" numeric NOT NULL,
                "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL);
            CREATE INDEX "IX_Providers_ProviderType" ON "{schema}"."Providers" ("ProviderType");
            CREATE TABLE "{schema}"."ProviderKeyCredentials" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "ProviderId" integer NOT NULL REFERENCES "{schema}"."Providers" ("Id") ON DELETE CASCADE,
                "ProviderAccountGroup" smallint NOT NULL, "ApiKey" text NULL, "BaseUrl" text NULL,
                "SecretSettings" jsonb NULL, "KeyName" text NULL, "IsPrimary" boolean NOT NULL,
                "IsEnabled" boolean NOT NULL, "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL,
                CONSTRAINT "CK_ProviderKeyCredential_AccountGroupRange" CHECK ("ProviderAccountGroup" >= 0 AND "ProviderAccountGroup" <= 32),
                CONSTRAINT "CK_ProviderKeyCredential_PrimaryMustBeEnabled" CHECK ("IsPrimary" = false OR "IsEnabled" = true));
            CREATE INDEX "IX_ProviderKeyCredential_ProviderId" ON "{schema}"."ProviderKeyCredentials" ("ProviderId");
            CREATE UNIQUE INDEX "IX_ProviderKeyCredential_UniqueApiKeyPerProvider" ON "{schema}"."ProviderKeyCredentials" ("ProviderId", "ApiKey") WHERE "ApiKey" IS NOT NULL;
            CREATE UNIQUE INDEX "IX_ProviderKeyCredential_OnePrimaryPerProvider" ON "{schema}"."ProviderKeyCredentials" ("ProviderId", "IsPrimary") WHERE "IsPrimary" = true;
            INSERT INTO "{schema}"."Providers" VALUES
                (100, 0, 'disabled provider', NULL, jsonb_build_object('public', 'sentinel'), false, false, 1.25, '2022-04-05T06:07:08Z', '2022-04-06T06:07:08Z'),
                (200, 0, 'other provider', '', NULL, true, true, 0.75, '2022-04-05T06:07:08Z', '2022-04-06T06:07:08Z');
            """, token);
    }

    private async Task SeedCountAsync(string schema, int count, CancellationToken token)
    {
        await ExecuteAsync(schema, $"DELETE FROM \"{schema}\".\"ProviderKeyCredentials\"", token);
        // Thirty-three unrelated enabled credentials cannot count for provider100 or satisfy its Any.
        for (var index = 0; index < 33; index++)
            await ExecuteAsync(schema, CredentialInsert(1000 + index, 200, true, index == 0), token);
        for (var index = 0; index < count; index++)
            await ExecuteAsync(schema, CredentialInsert(100 + index, 100, index % 2 == 0, index == 0), token);
    }

    private async Task SeedFlagsAsync(string schema, CancellationToken token)
    {
        await ExecuteAsync(schema, CredentialInsert(100, 100, true, false), token);
        await ExecuteAsync(schema, CredentialInsert(101, 100, true, true), token);
        await ExecuteAsync(schema, CredentialInsert(102, 100, false, false), token);
        await ExecuteAsync(schema, CredentialInsert(1000, 200, true, true), token);
    }

    private Task SeedSuccessAsync(string schema, CancellationToken token) =>
        ExecuteAsync(schema, CredentialInsert(100, 100, true, false), token);

    private static string CredentialInsert(int id, int provider, bool enabled, bool primary) => $"""
        INSERT INTO "ProviderKeyCredentials"
            ("Id", "ProviderId", "ProviderAccountGroup", "ApiKey", "BaseUrl", "SecretSettings", "KeyName", "IsPrimary", "IsEnabled", "CreatedAt", "UpdatedAt")
        VALUES ({id}, {provider}, 32, 'fixture-secret-{id}', 'https://fixture.invalid/{id}', jsonb_build_object('fixture', 'secret-sentinel'),
            'key-{id}', {primary.ToString().ToLowerInvariant()}, {enabled.ToString().ToLowerInvariant()},
            '2022-04-05T06:07:08Z', '2022-04-06T06:07:08Z')
        """;

    private async Task ExecuteAsync(string schema, string text, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            { SearchPath = schema, Pooling = false, Timeout = 5, CommandTimeout = 15 }.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync(token);
    }

    private Task DropSchemaAsync(string schema) =>
        ExecuteAsync(schema, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", CancellationToken.None);

    private async Task<string[]> SnapshotAsync(string schema, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false, Timeout = 5, CommandTimeout = 15 }.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        // Independent SQL captures every stored column of both relations, including timestamps/JSON.
        command.CommandText = $"""
            SELECT 'provider:' || row_to_json(stored)::text FROM "{schema}"."Providers" stored ORDER BY stored."Id";
            SELECT 'credential:' || row_to_json(stored)::text FROM "{schema}"."ProviderKeyCredentials" stored ORDER BY stored."Id"
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<string>();
        do { while (await reader.ReadAsync(token)) rows.Add(reader.GetString(0)); }
        while (await reader.NextResultAsync(token));
        return rows.ToArray();
    }

    private static async Task<int> PoolUsableAsync(NpgsqlDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        Assert.Equal(1, await command.ExecuteScalarAsync(token));
        return connection.ProcessID;
    }

    private static async Task ReapAsync(Task pending, CancellationTokenSource cancelled)
    {
        cancelled.Cancel();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
    }

    private static async Task WaitForRelationWaitAsync(string connectionString, int readerPid, int blockerPid,
        string schema, Task pending, CancellationToken token)
    {
        var application = new NpgsqlConnectionStringBuilder(connectionString).ApplicationName;
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            { Pooling = false, ApplicationName = "credential_observer" }.ConnectionString);
        await observer.OpenAsync(token);
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT activity.pid
            FROM pg_catalog.pg_stat_activity activity
            JOIN pg_catalog.pg_locks waiting ON waiting.pid = activity.pid
            JOIN pg_catalog.pg_class relation ON relation.oid = waiting.relation
            JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
            JOIN pg_catalog.pg_locks blocking ON blocking.relation = waiting.relation
                AND blocking.pid = @blocker AND blocking.granted AND blocking.mode = 'AccessExclusiveLock'
            WHERE activity.pid = @reader AND activity.application_name = @application
                AND activity.datname = current_database() AND activity.wait_event_type = 'Lock'
                AND @blocker = ANY(pg_catalog.pg_blocking_pids(activity.pid))
                AND waiting.locktype = 'relation' AND NOT waiting.granted AND waiting.mode = 'AccessShareLock'
                AND waiting.relation = pg_catalog.to_regclass(@relation)
                AND namespace.nspname = @schema AND relation.relname = 'ProviderKeyCredentials'
            """;
        command.Parameters.AddWithValue("reader", readerPid);
        command.Parameters.AddWithValue("blocker", blockerPid);
        command.Parameters.AddWithValue("application", application);
        command.Parameters.AddWithValue("relation", $"\"{schema}\".\"ProviderKeyCredentials\"");
        command.Parameters.AddWithValue("schema", schema);
        while (true)
        {
            if (pending.IsCompleted) { await pending; throw new InvalidOperationException("Credential query completed before its SQL relation wait was observed."); }
            if (await command.ExecuteScalarAsync(token) is int observed) { Assert.Equal(readerPid, observed); return; }
            await Task.Delay(25, token);
        }
    }

    private sealed class CredentialContextFactory : IDbContextFactory<ConduitDbContext>
    {
        private readonly DbContextOptions<ConduitDbContext> _options;
        private int _attempts, _created, _disposed;
        public CredentialContextFactory(NpgsqlDataSource source) : this(
            new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(source).Options) { }
        public CredentialContextFactory(DbContextOptions<ConduitDbContext> options) => _options = options;
        public int Attempts => Volatile.Read(ref _attempts);
        public int Created => Volatile.Read(ref _created);
        public int AsyncDisposed => Volatile.Read(ref _disposed);
        public List<int> TrackedEntryCounts { get; } = new();
        public CancellationToken LastToken { get; private set; }
        public Func<CancellationToken, Task>? BeforeCreate { get; set; }
        public Func<Task>? BeforeDispose { get; set; }
        public ConduitDbContext CreateDbContext() => throw new InvalidOperationException("Validator must use the asynchronous factory.");
        public async Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            LastToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeCreate is not null) await BeforeCreate(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _created);
            return new CredentialTrackedContext(_options, BeforeDispose,
                count => TrackedEntryCounts.Add(count), () => Interlocked.Increment(ref _disposed));
        }
    }

    private sealed class CredentialTrackedContext(DbContextOptions<ConduitDbContext> options,
        Func<Task>? beforeDispose, Action<int> tracked, Action disposed) : ConduitDbContext(options)
    {
        public override async ValueTask DisposeAsync()
        {
            try
            {
                tracked(ChangeTracker.Entries().Count());
                await base.DisposeAsync();
                if (beforeDispose is not null) await beforeDispose();
            }
            finally { disposed(); }
        }
        public override int SaveChanges() => throw new InvalidOperationException("Validator must not mutate storage.");
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Validator must not mutate storage.");
    }

    private sealed record Warning(LogLevel Level, EventId EventId, string Template, string Formatted,
        Dictionary<string, object?> Fields, Exception? Exception);
    private sealed class CredentialWarnings : ILogger<ProviderKeyCredentialValidator>
    {
        public List<Warning> Events { get; } = new();
        public void Clear() => Events.Clear();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = ((IEnumerable<KeyValuePair<string, object?>>)state!).ToArray();
            Events.Add(new(level, eventId, (string)fields.Single(field => field.Key == "{OriginalFormat}").Value!,
                formatter(state, exception), fields.Where(field => field.Key != "{OriginalFormat}")
                    .ToDictionary(field => field.Key, field => field.Value), exception));
        }
    }

    private sealed record GateEntry(string Sql, int Ordinal, int CompletedBefore);
    private sealed class CredentialCommandGate(bool block = false) : ILoggerFactory, ILogger
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly TaskCompletionSource<GateEntry> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed, _claimed, _started, _completed;
        public string SourceConnectionString { get; set; } = string.Empty;
        public int Started => Volatile.Read(ref _started);
        public Task<GateEntry> Entered => _entered.Task;
        public void Arm() { Volatile.Write(ref _started, 0); Volatile.Write(ref _completed, 0); Volatile.Write(ref _armed, 1); }
        public void Release() => _release.Set();
        public ILogger CreateLogger(string categoryName) => categoryName == "Npgsql.Command" ? this : NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
            var values = fields.ToArray();
            if (values.Any(field => field.Key == "Parameters")) throw new InvalidOperationException("Credential SQL diagnostics must not receive parameter values.");
            var sql = values.FirstOrDefault(field => field.Key == "CommandText").Value as string;
            if (sql is null || !sql.Contains("\"ProviderKeyCredentials\"", StringComparison.Ordinal)) return;
            if (eventId.Id == NpgsqlEventId.CommandExecutionCompleted) { Interlocked.Increment(ref _completed); return; }
            if (eventId.Id != NpgsqlEventId.ExecutingCommand) return;
            var ordinal = Interlocked.Increment(ref _started);
            if (!block || Volatile.Read(ref _armed) == 0 || Interlocked.CompareExchange(ref _claimed, 1, 0) != 0) return;
            _entered.TrySetResult(new(sql, ordinal, Volatile.Read(ref _completed)));
            if (!_release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Credential SQL before-send gate was not released within its bound.");
        }
        public void Dispose() { Release(); _release.Dispose(); }
    }
}
