using System.Data.Common;
using System.Diagnostics;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
public sealed class VirtualKeyRuntimeRetryPersistenceTests(PostgresLockTestContainerFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RealSerializationFailureRetriesFreshAttemptScalarsOrHonorsCallerCancellation(bool cancelAtRetry) =>
        RunSerializationRetryAsync(cancelAtRetry, failedAttempts: 1);

    [Fact]
    public Task CallerCancellationInterruptsRealNonzeroRetryBackoff() =>
        RunSerializationRetryAsync(cancelAtRetry: true, failedAttempts: 2);

    private Task RunSerializationRetryAsync(bool cancelAtRetry, int failedAttempts) =>
        VirtualKeyRuntimeRecoveryFixture.RunAsync(fixture.ConnectionString, async (owned, deadline) =>
        {
            await InstallInsertFailuresAsync(owned, failedAttempts, deadline);
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline);
            var trace = new RetryTrace(cancelAtRetry ? caller : null, failedAttempts);
            var factory = new RetryContextFactory(owned.Source, trace, failedAttempts);
            var store = new EfVirtualKeyRuntimeStore(factory);
            var adjustment = RetryAdjustment();
            var pending = store.AdjustBalanceAsync(adjustment, caller.Token);
            var completedAt = pending.ContinueWith(_ => Stopwatch.GetTimestamp(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await IntegrationProofCleanup.RunAsync(async () =>
            {
                if (cancelAtRetry)
                {
                    var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        pending.WaitAsync(TimeSpan.FromSeconds(10)));
                    Assert.Equal(caller.Token, cancellation.CancellationToken);
                    if (failedAttempts > 1)
                    {
                        // Npgsql's first retry is immediate; the second has a real
                        // nonzero backoff. Cancel synchronously in its typed retry
                        // diagnostic and require completion before that delay ends.
                        var scheduledDelay = trace.Retries[^1].Delay;
                        Assert.True(scheduledDelay > TimeSpan.Zero);
                        var cancellationElapsed = Stopwatch.GetElapsedTime(
                            trace.CancellationStartedTimestamp, await completedAt);
                        Assert.True(cancellationElapsed < scheduledDelay,
                            $"Cancellation took {cancellationElapsed} for a scheduled {scheduledDelay} retry delay.");
                    }
                }
                else
                {
                    Assert.Equal(new VirtualKeyBalanceAdjustmentResult(97m, 3m, true),
                        await pending.WaitAsync(TimeSpan.FromSeconds(10)));
                }
            }, async () =>
            {
                caller.Cancel();
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (pending.IsCanceled) { }
            }, "Real EF retry work and its pending adjustment drain failed.");

            // These are real provider diagnostics, not a substituted execution strategy.
            Assert.Equal(failedAttempts, trace.CommandFailures.Count);
            Assert.All(trace.CommandFailures, failure =>
                Assert.Equal(PostgresErrorCodes.SerializationFailure, failure.SqlState));
            Assert.Contains("INSERT INTO \"VirtualKeyGroupTransactions\"", trace.FailedCommandText, StringComparison.Ordinal);
            Assert.Equal(1, trace.FailedTrackedLedgerCount);
            Assert.Equal(failedAttempts, trace.Retries.Count);
            Assert.All(trace.Retries, retry =>
            {
                Assert.All(retry.ExceptionsEncountered, exception =>
                    Assert.Equal(PostgresErrorCodes.SerializationFailure, FindPostgres(exception)!.SqlState));
                Assert.True(retry.IsAsync);
            });
            Assert.Equal(TimeSpan.Zero, trace.Retries[0].Delay);
            Assert.Single(trace.ContextsCreated);
            Assert.Equal(trace.ContextsCreated, trace.ContextsDisposed);

            var started = trace.Transactions.Where(row => row.Kind == "started").ToArray();
            var disposed = trace.Transactions.Where(row => row.Kind == "disposed").ToArray();
            var expectedAttempts = failedAttempts + (cancelAtRetry ? 0 : 1);
            Assert.Equal(expectedAttempts, started.Length);
            Assert.Equal(started.Length, disposed.Length);
            Assert.Equal(started.Length, started.Select(row => row.Id).Distinct().Count());
            Assert.Equal(started.Select(row => row.Id), disposed.Select(row => row.Id));
            Assert.True(disposed[failedAttempts - 1].Order < trace.RetryOrder);
            Assert.Equal(expectedAttempts, trace.CompletedUpdates);
            Assert.Equal(expectedAttempts, trace.DuplicateReads.Count);
            Assert.Equal(expectedAttempts, trace.StateReads.Count);
            Assert.All(trace.DuplicateReads, parameters => Assert.Contains(parameters,
                parameter => Equals(parameter.Value, "retry-owned")));
            Assert.All(trace.StateReads, parameters => Assert.Contains(parameters,
                parameter => Equals(parameter.Value, 1)));
            Assert.All(trace.Updates, update =>
            {
                Assert.Equal(trace.ContextsCreated.Single(), update.ContextId);
                Assert.Equal(0, update.TrackedLedgerCount);
                Assert.Contains(update.Parameters, parameter => parameter.Name.Contains("updateGroupId", StringComparison.Ordinal)
                    && Equals(parameter.Value, 1));
                Assert.Contains(update.Parameters, parameter => parameter.Name.Contains("updateAmount", StringComparison.Ordinal)
                    && Equals(parameter.Value, -3m));
                Assert.Equal(DateTimeKind.Utc, update.UpdatedAt.Kind);
            });

            if (cancelAtRetry)
            {
                Assert.Equal(failedAttempts, trace.Updates.Count); // No SQL attempt follows caller cancellation.
                Assert.DoesNotContain(trace.Transactions, row => row.Kind == "committed");
                Assert.Equal((long)failedAttempts, await ReadAttemptSequenceAsync(owned, deadline));
                Assert.Equal(new RecoveryGroupSnapshot(100m, 100m, 0m), await owned.ReadGroupAsync(1, deadline));
                await AssertRetryLedgerAsync(owned, expected: false, deadline);
            }
            else
            {
                Assert.Equal(2, trace.Updates.Count);
                Assert.NotEqual(started[0].Id, started[1].Id);
                Assert.True(disposed[0].Order < started[1].Order);
                Assert.True(trace.Updates[1].UpdatedAt > trace.Updates[0].UpdatedAt);
                Assert.True(trace.Updates[1].UpdatedAt >= trace.RetryNotBeforeUtc);
                Assert.Equal(started[1].Id,
                    Assert.Single(trace.Transactions, row => row.Kind == "committed").Id);
                Assert.Equal(2L, await ReadAttemptSequenceAsync(owned, deadline));
                Assert.Equal(new RecoveryGroupSnapshot(97m, 100m, 3m), await owned.ReadGroupAsync(1, deadline));
                await AssertRetryLedgerAsync(owned, expected: true, deadline);
            }
            Assert.Equal(new RecoveryGroupSnapshot(200m, 200m, 0m), await owned.ReadGroupAsync(2, deadline));

            // Use the same MaxPoolSize=1 source after failure/cancellation. The
            // first failed transaction must no longer own its sole connection.
            await AssertRetryPoolReusableAsync(owned.Source, deadline);
            var recoveryStore = new EfVirtualKeyRuntimeStore(new RecoveryContextFactory(owned.Source));
            var recovery = await recoveryStore.AdjustBalanceAsync(adjustment, deadline);
            Assert.Equal(new VirtualKeyBalanceAdjustmentResult(97m, 3m, cancelAtRetry), recovery);
            Assert.Equal(new VirtualKeyBalanceAdjustmentResult(97m, 3m, false),
                await recoveryStore.AdjustBalanceAsync(adjustment, deadline));
            await Assert.ThrowsAsync<VirtualKeyBalanceConflictException>(() =>
                recoveryStore.AdjustBalanceAsync(adjustment with { Amount = -4m }, deadline));
            Assert.Equal(new RecoveryGroupSnapshot(97m, 100m, 3m), await owned.ReadGroupAsync(1, deadline));
            Assert.Equal(new RecoveryGroupSnapshot(200m, 200m, 0m), await owned.ReadGroupAsync(2, deadline));
            Assert.Equal(failedAttempts + 1L, await ReadAttemptSequenceAsync(owned, deadline));
            await AssertRetryLedgerAsync(owned, expected: true, deadline);
            await AssertRetryPoolReusableAsync(owned.Source, deadline);
        });

    private static readonly DateTime BillingWindow = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    private static VirtualKeyBalanceAdjustment RetryAdjustment() => new(
        1, -3m, "retry exact debit", "RetryTest", VirtualKeyBalanceReferenceType.System,
        ReferenceId: "retry-reference", IdempotencyKey: "retry-owned", BillingWindowStartUtc: BillingWindow);

    private static Task InstallInsertFailuresAsync(
        VirtualKeyRuntimeRecoveryFixture owned, int failedAttempts, CancellationToken token) =>
        owned.ExecuteAsync($"""
            CREATE SEQUENCE "{owned.Schema}"."retry_attempts";
            CREATE FUNCTION "{owned.Schema}"."fail_ledger_insert"() RETURNS trigger
            LANGUAGE plpgsql AS $body$
            BEGIN
                IF nextval('"{owned.Schema}"."retry_attempts"') <= {failedAttempts} THEN
                    RAISE EXCEPTION USING ERRCODE = '40001', MESSAGE = 'Owned deterministic serialization failure';
                END IF;
                RETURN NEW;
            END;
            $body$;
            CREATE TRIGGER fail_ledger_insert BEFORE INSERT
                ON "{owned.Schema}"."VirtualKeyGroupTransactions"
                FOR EACH ROW WHEN (NEW."IdempotencyKey" = 'retry-owned')
                EXECUTE FUNCTION "{owned.Schema}"."fail_ledger_insert"();
            """, token);

    private static async Task<long> ReadAttemptSequenceAsync(VirtualKeyRuntimeRecoveryFixture owned, CancellationToken token)
    {
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(owned.ConnectionString)
            { Pooling = false }.ConnectionString);
        await observer.OpenAsync(token);
        await using var command = new NpgsqlCommand($"SELECT last_value FROM \"{owned.Schema}\".\"retry_attempts\"", observer);
        return (long)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task AssertRetryLedgerAsync(VirtualKeyRuntimeRecoveryFixture owned, bool expected, CancellationToken token)
    {
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(owned.ConnectionString)
            { Pooling = false }.ConnectionString);
        await observer.OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT "VirtualKeyGroupId", "TransactionType", "Amount", "OriginalAmount", "BalanceAfter",
                "ReferenceType", "ReferenceId", "Description", "InitiatedBy", "IdempotencyKey",
                "BillingWindowStartUtc", "IsDeleted", "DeletedAt", "OriginalAmountVerifiedBy",
                "OriginalAmountEvidence", "OriginalAmountVerifiedAtUtc"
            FROM "VirtualKeyGroupTransactions" ORDER BY "Id"
            """, observer);
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.Equal(expected, await reader.ReadAsync(token));
        if (!expected) return;
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(2, reader.GetInt32(1));
        Assert.Equal(3m, reader.GetDecimal(2));
        Assert.Equal(3m, reader.GetDecimal(3));
        Assert.Equal(97m, reader.GetDecimal(4));
        Assert.Equal(3, reader.GetInt32(5));
        Assert.Equal("retry-reference", reader.GetString(6));
        Assert.Equal("retry exact debit", reader.GetString(7));
        Assert.Equal("RetryTest", reader.GetString(8));
        Assert.Equal("retry-owned", reader.GetString(9));
        Assert.Equal(BillingWindow, reader.GetDateTime(10));
        Assert.False(reader.GetBoolean(11));
        for (var index = 12; index < 16; index++) Assert.True(reader.IsDBNull(index));
        Assert.False(await reader.ReadAsync(token));
    }

    private static async Task AssertRetryPoolReusableAsync(NpgsqlDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        Assert.Equal(1, await command.ExecuteScalarAsync(token));
    }

    private static PostgresException? FindPostgres(Exception exception) => exception is PostgresException postgres
        ? postgres : exception.InnerException is { } inner ? FindPostgres(inner) : null;

    private sealed record ParameterCapture(string Name, object Value);
    private sealed record UpdateCapture(Guid ContextId, DateTime UpdatedAt, int TrackedLedgerCount,
        IReadOnlyList<ParameterCapture> Parameters);
    private sealed record TransactionCapture(string Kind, Guid Id, int Order);

    private sealed class RetryTrace(CancellationTokenSource? cancelAtRetry, int cancellationRetry) : DbCommandInterceptor
    {
        private int _order;
        public List<UpdateCapture> Updates { get; } = [];
        public List<PostgresException> CommandFailures { get; } = [];
        public List<ExecutionStrategyEventData> Retries { get; } = [];
        public List<TransactionCapture> Transactions { get; } = [];
        public List<Guid> ContextsCreated { get; } = [];
        public List<Guid> ContextsDisposed { get; } = [];
        public List<IReadOnlyList<ParameterCapture>> DuplicateReads { get; } = [];
        public List<IReadOnlyList<ParameterCapture>> StateReads { get; } = [];
        public int CompletedUpdates { get; private set; }
        public string FailedCommandText { get; private set; } = string.Empty;
        public int FailedTrackedLedgerCount { get; private set; }
        public int RetryOrder { get; private set; }
        public DateTime RetryNotBeforeUtc { get; private set; }
        public long CancellationStartedTimestamp { get; private set; }

        public static bool Includes(EventId id, LogLevel _) =>
            id == CoreEventId.ExecutionStrategyRetrying || id == RelationalEventId.CommandError ||
            id == RelationalEventId.TransactionStarted || id == RelationalEventId.TransactionDisposed ||
            id == RelationalEventId.TransactionCommitted;

        public void Observe(EventData data)
        {
            var order = Interlocked.Increment(ref _order);
            if (data is CommandErrorEventData failed)
            {
                if (FindPostgres(failed.Exception) is { } postgres) CommandFailures.Add(postgres);
                FailedCommandText = failed.Command.CommandText;
                FailedTrackedLedgerCount = failed.Context!.ChangeTracker.Entries<VirtualKeyGroupTransaction>().Count();
            }
            else if (data is ExecutionStrategyEventData retry)
            {
                Retries.Add(retry);
                RetryOrder = order;
                var firstTimestamp = Updates[^1].UpdatedAt;
                var observation = Stopwatch.StartNew();
                DateTime observed;
                // A clock-value barrier distinguishes a fresh timestamp from an
                // outer-loop capture. No arbitrary delay is the retry oracle.
                while ((observed = DateTime.UtcNow) <= firstTimestamp)
                {
                    if (observation.Elapsed > TimeSpan.FromSeconds(1))
                        throw new TimeoutException("UTC clock did not advance after the first real failed attempt.");
                    Thread.Yield();
                }
                RetryNotBeforeUtc = observed;
                if (cancelAtRetry is not null && Retries.Count == cancellationRetry)
                {
                    CancellationStartedTimestamp = Stopwatch.GetTimestamp();
                    cancelAtRetry.Cancel();
                }
            }
            else if (data is TransactionEventData transaction)
            {
                var kind = data.EventId == RelationalEventId.TransactionStarted ? "started"
                    : data.EventId == RelationalEventId.TransactionDisposed ? "disposed" : "committed";
                Transactions.Add(new TransactionCapture(kind, transaction.TransactionId, order));
            }
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"VirtualKeyGroups\"", StringComparison.Ordinal))
            {
                var parameters = command.Parameters.Cast<DbParameter>()
                    .Select(parameter => new ParameterCapture(parameter.ParameterName, parameter.Value!)).ToArray();
                Updates.Add(new UpdateCapture(eventData.Context!.ContextId.InstanceId,
                    parameters.Select(parameter => parameter.Value).OfType<DateTime>().Single(),
                    eventData.Context.ChangeTracker.Entries<VirtualKeyGroupTransaction>().Count(), parameters));
            }
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"VirtualKeyGroups\"", StringComparison.Ordinal))
            {
                Assert.Equal(1, result);
                CompletedUpdates++;
            }
            return ValueTask.FromResult(result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal))
            {
                var parameters = command.Parameters.Cast<DbParameter>()
                    .Select(parameter => new ParameterCapture(parameter.ParameterName, parameter.Value!)).ToArray();
                if (command.CommandText.Contains("FROM \"VirtualKeyGroupTransactions\"", StringComparison.Ordinal))
                {
                    Assert.False(result.HasRows);
                    DuplicateReads.Add(parameters);
                }
                else if (command.CommandText.Contains("FROM \"VirtualKeyGroups\"", StringComparison.Ordinal))
                {
                    Assert.True(result.HasRows);
                    StateReads.Add(parameters);
                }
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RetryContextFactory : IDbContextFactory<ConduitDbContext>
    {
        private readonly DbContextOptions<ConduitDbContext> _options;
        private readonly RetryTrace _trace;
        public RetryContextFactory(NpgsqlDataSource source, RetryTrace trace, int failedAttempts)
        {
            _trace = trace;
            _options = new DbContextOptionsBuilder<ConduitDbContext>()
                .UseNpgsql(source, provider => provider.EnableRetryOnFailure(
                    failedAttempts, failedAttempts > 1 ? TimeSpan.FromSeconds(2) : TimeSpan.Zero, null))
                .AddInterceptors(trace).LogTo(RetryTrace.Includes, trace.Observe).Options;
        }
        public ConduitDbContext CreateDbContext()
        {
            var context = new RetryContext(_options, _trace);
            _trace.ContextsCreated.Add(context.ContextId.InstanceId);
            return context;
        }
        public Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }

    private sealed class RetryContext(DbContextOptions<ConduitDbContext> options, RetryTrace trace) : ConduitDbContext(options)
    {
        public override async ValueTask DisposeAsync()
        {
            var id = ContextId.InstanceId;
            await base.DisposeAsync();
            trace.ContextsDisposed.Add(id);
        }
    }

    // The strict retry observer deliberately expects an empty idempotency lookup
    // in both attempts. Normal replay must read the already committed winner.
    private sealed class RecoveryContextFactory(NpgsqlDataSource source) : IDbContextFactory<ConduitDbContext>
    {
        private readonly DbContextOptions<ConduitDbContext> _options =
            new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(source).Options;

        public ConduitDbContext CreateDbContext() => new(_options);
        public Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
