using System.Data.Common;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Configuration.Utilities;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
public sealed class VirtualKeyRuntimeUniqueConflictPersistenceTests(PostgresLockTestContainerFixture fixture)
{
    private const decimal Charge = 0.12500001m;
    private const decimal LaterCredit = 5.00000003m;
    private static readonly DateTime Window = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("matching")]
    [InlineData("amount")]
    [InlineData("sign")]
    [InlineData("group")]
    [InlineData("reference-type")]
    [InlineData("reference-id")]
    [InlineData("billing-window")]
    public Task RealUniqueInsertFailureDisposesLoserAndRecoversWithFreshCurrentState(string identity) =>
        VirtualKeyRuntimeRecoveryFixture.RunAsync(fixture.ConnectionString, async (database, token) =>
        {
            var winner = WinningAdjustment();
            var loser = identity switch
            {
                "matching" => winner,
                "amount" => winner with { Amount = -0.12500002m },
                "sign" => winner with { Amount = Charge },
                "group" => winner with { GroupId = 2 },
                "reference-type" => winner with { ReferenceType = VirtualKeyBalanceReferenceType.Manual },
                "reference-id" => winner with { ReferenceId = "other-reference" },
                "billing-window" => winner with { BillingWindowStartUtc = Window.AddHours(1) },
                _ => throw new ArgumentOutOfRangeException(nameof(identity))
            };
            var proof = new ConflictProof(database, winner);
            var store = new EfVirtualKeyRuntimeStore(new TrackingFactory(proof));

            if (identity == "matching")
            {
                var result = await store.AdjustBalanceAsync(loser, token);
                Assert.Equal(new VirtualKeyBalanceAdjustmentResult(100m - Charge + LaterCredit, Charge, false), result);
                Assert.Equal(1, proof.RecoveryStateQueries);
                // The winning row records the balance before the separate credit.
                Assert.NotEqual(100m - Charge, result.NewBalance);
            }
            else
            {
                await Assert.ThrowsAsync<VirtualKeyBalanceConflictException>(() => store.AdjustBalanceAsync(loser, token));
                Assert.Equal(0, proof.RecoveryStateQueries);
            }

            AssertRecoveryBranch(proof, winnerReadCompleted: true);
            await AssertCommittedStateAsync(database, winner, token);
            await AssertPoolAndReplayRecoverAsync(database, winner, token);
        });

    [Fact]
    public Task CancellationOfTheFreshWinnerReadDisposesBothContextsAndReusesTheSingleSlot() =>
        VirtualKeyRuntimeRecoveryFixture.RunAsync(fixture.ConnectionString, async (database, token) =>
        {
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
            var winner = WinningAdjustment();
            var proof = new ConflictProof(database, winner) { CancelWinnerRead = caller };
            var store = new EfVirtualKeyRuntimeStore(new TrackingFactory(proof));

            var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.AdjustBalanceAsync(winner, caller.Token));
            Assert.Equal(caller.Token, cancelled.CancellationToken);
            Assert.True(proof.CancellationInjected);
            Assert.Equal(0, proof.RecoveryStateQueries);
            AssertRecoveryBranch(proof, winnerReadCompleted: false);
            await AssertCommittedStateAsync(database, winner, token);
            await AssertPoolAndReplayRecoverAsync(database, winner, token);
        });

    private static VirtualKeyBalanceAdjustment WinningAdjustment() => new(
        1, -Charge, "winning debit", "integration", VirtualKeyBalanceReferenceType.VirtualKey,
        "request-reference", $"unique-catch:{Guid.NewGuid():N}", Window);

    private static void AssertRecoveryBranch(ConflictProof proof, bool winnerReadCompleted)
    {
        Assert.Equal(2, proof.Contexts.Count);
        Assert.NotEqual(proof.Contexts[0].ContextId.InstanceId, proof.Contexts[1].ContextId.InstanceId);
        Assert.All(proof.Contexts, context => Assert.True(context.Disposed));
        Assert.True(proof.EmptyLookupObserved);
        Assert.True(proof.WinnerCommitted);
        Assert.Equal(1, proof.LoserUpdates);
        Assert.Equal(1, proof.InsertFailures);
        Assert.Equal(1, proof.SaveFailures);
        Assert.Equal(1, proof.TransactionDisposals);
        Assert.Equal(1, proof.RecoveryWinnerQueries);
        Assert.Equal(winnerReadCompleted ? 1 : 0, proof.RecoveryWinnerReads);
        Assert.True(proof.LaterCreditApplied);
        Assert.Equal(new[]
        {
            "empty-lookup", "winner-committed", "loser-update", "insert-23505", "save-23505",
            "transaction-disposed", "context-1-disposed", "fresh-factory", "later-credit", "winner-query"
        }, proof.Events.Take(10));
        Assert.Equal("context-2-disposed", proof.Events[^1]);
    }

    private static async Task AssertCommittedStateAsync(VirtualKeyRuntimeRecoveryFixture database,
        VirtualKeyBalanceAdjustment winner, CancellationToken token)
    {
        Assert.Equal(new RecoveryGroupSnapshot(100m - Charge + LaterCredit, 100m + LaterCredit, Charge),
            await database.ReadGroupAsync(1, token));
        Assert.Equal(new RecoveryGroupSnapshot(200m, 200m, 0m), await database.ReadGroupAsync(2, token));
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString)
            { Pooling = false }.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "IdempotencyKey", "VirtualKeyGroupId", "Amount", "OriginalAmount", "BalanceAfter",
                "TransactionType", "ReferenceType", "ReferenceId", "BillingWindowStartUtc"
            FROM "VirtualKeyGroupTransactions" ORDER BY "Id"
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(winner.IdempotencyKey, reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(Charge, reader.GetDecimal(2));
        Assert.Equal(Charge, reader.GetDecimal(3));
        Assert.Equal(100m - Charge, reader.GetDecimal(4));
        Assert.Equal(2, reader.GetInt32(5)); // Persisted Debit enum.
        Assert.Equal((int)winner.ReferenceType, reader.GetInt32(6));
        Assert.Equal(winner.ReferenceId, reader.GetString(7));
        Assert.Equal(Window, reader.GetDateTime(8));
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(winner.IdempotencyKey + ":later", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(LaterCredit, reader.GetDecimal(2));
        Assert.Equal(LaterCredit, reader.GetDecimal(3));
        Assert.Equal(100m - Charge + LaterCredit, reader.GetDecimal(4));
        Assert.Equal(1, reader.GetInt32(5)); // Persisted Credit enum.
        Assert.False(await reader.ReadAsync(token)); // Winner + distinct credit; no loser ledger.
    }

    private static async Task AssertPoolAndReplayRecoverAsync(VirtualKeyRuntimeRecoveryFixture database,
        VirtualKeyBalanceAdjustment winner, CancellationToken token)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(5));
        await using (var lease = await database.Source.OpenConnectionAsync(bounded.Token))
        await using (var command = lease.CreateCommand())
        {
            command.CommandText = "SELECT 1";
            Assert.Equal(1, await command.ExecuteScalarAsync(bounded.Token));
        }
        var replay = await new EfVirtualKeyRuntimeStore(new PlainFactory(database.Source))
            .AdjustBalanceAsync(winner, bounded.Token);
        Assert.Equal(new VirtualKeyBalanceAdjustmentResult(100m - Charge + LaterCredit, Charge, false), replay);
        await AssertCommittedStateAsync(database, winner, bounded.Token);
    }

    private sealed class ConflictProof(VirtualKeyRuntimeRecoveryFixture database, VirtualKeyBalanceAdjustment winner)
    {
        internal VirtualKeyRuntimeRecoveryFixture Database { get; } = database;
        internal VirtualKeyBalanceAdjustment Winner { get; } = winner;
        internal List<TrackingContext> Contexts { get; } = [];
        internal List<string> Events { get; } = [];
        internal CancellationTokenSource? CancelWinnerRead { get; init; }
        internal bool EmptyLookupObserved, WinnerCommitted, LaterCreditApplied, CancellationInjected;
        internal int LoserUpdates, InsertFailures, SaveFailures, TransactionDisposals, RecoveryWinnerQueries,
            RecoveryWinnerReads, RecoveryStateQueries;
        internal PostgresException? InsertException;

        internal bool IsFirst(DbContext? context) => Contexts.Count > 0 && ReferenceEquals(context, Contexts[0]);
        internal bool IsRecovery(DbContext? context) => Contexts.Count == 2 && ReferenceEquals(context, Contexts[1]);

        internal async Task CommitWinnerAsync(CancellationToken token)
        {
            // A separate unpooled writer can commit while the EF reader still owns
            // the only pooled slot. It runs before the loser takes the group lock.
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Database.ConnectionString)
                { Pooling = false }.ConnectionString);
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE "VirtualKeyGroups" SET "Balance" = "Balance" - @amount,
                    "LifetimeSpent" = "LifetimeSpent" + @amount, "UpdatedAt" = now() WHERE "Id" = 1;
                INSERT INTO "VirtualKeyGroupTransactions" (
                    "VirtualKeyGroupId", "TransactionType", "Amount", "OriginalAmount", "BalanceAfter",
                    "ReferenceType", "ReferenceId", "Description", "InitiatedBy", "IdempotencyKey",
                    "BillingWindowStartUtc", "CreatedAt", "IsDeleted")
                VALUES (1, 2, @amount, @amount, @balance, @referenceType, @referenceId,
                    'winning debit', 'integration', @key, @window, now(), false)
                """;
            command.Parameters.AddWithValue("amount", Charge);
            command.Parameters.AddWithValue("balance", 100m - Charge);
            command.Parameters.AddWithValue("referenceType", (int)Winner.ReferenceType);
            command.Parameters.AddWithValue("referenceId", Winner.ReferenceId!);
            command.Parameters.AddWithValue("key", Winner.IdempotencyKey!);
            command.Parameters.AddWithValue("window", Window);
            Assert.Equal(2, await command.ExecuteNonQueryAsync(token));
            await transaction.CommitAsync(token);
            WinnerCommitted = true;
            Events.Add("winner-committed");
        }
    }

    private sealed class TrackingFactory(ConflictProof proof) : IDbContextFactory<ConduitDbContext>
    {
        public ConduitDbContext CreateDbContext() => throw new NotSupportedException("This proof observes the async factory path.");

        public async Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (proof.Contexts.Count == 1)
            {
                Assert.True(proof.Contexts[0].Disposed);
                Assert.Equal(1, proof.TransactionDisposals);
                Assert.Equal(1, proof.InsertFailures);
                Assert.Equal(1, proof.SaveFailures);
                proof.Events.Add("fresh-factory");
                // This read also proves the failed UPDATE rolled back before the
                // catch creates its fresh context and reads the winning ledger.
                Assert.Equal(new RecoveryGroupSnapshot(100m - Charge, 100m, Charge),
                    await proof.Database.ReadGroupAsync(1, cancellationToken));
                Assert.Equal(new RecoveryGroupSnapshot(200m, 200m, 0m),
                    await proof.Database.ReadGroupAsync(2, cancellationToken));
                var later = await new NpgsqlVirtualKeyRuntimeStore(proof.Database.Source).AdjustBalanceAsync(new(
                    1, LaterCredit, "separate later credit", "integration", VirtualKeyBalanceReferenceType.Manual,
                    "later-reference", proof.Winner.IdempotencyKey + ":later"), cancellationToken);
                Assert.Equal(new VirtualKeyBalanceAdjustmentResult(100m - Charge + LaterCredit, Charge, true), later);
                proof.LaterCreditApplied = true;
                proof.Events.Add("later-credit");
            }
            Assert.InRange(proof.Contexts.Count, 0, 1);
            var options = new DbContextOptionsBuilder<ConduitDbContext>()
                .UseNpgsql(proof.Database.Source)
                .AddInterceptors(new ConflictCommands(proof), new FailedSaveObserver(proof))
                .LogTo((id, _) => id == RelationalEventId.TransactionDisposed, data =>
                {
                    var disposed = Assert.IsType<TransactionEventData>(data);
                    Assert.True(proof.IsFirst(disposed.Context));
                    proof.TransactionDisposals++;
                    proof.Events.Add("transaction-disposed");
                }).Options;
            var context = new TrackingContext(options, proof, proof.Contexts.Count + 1);
            proof.Contexts.Add(context);
            return context;
        }
    }

    private sealed class TrackingContext(DbContextOptions<ConduitDbContext> options, ConflictProof proof, int ordinal)
        : ConduitDbContext(options)
    {
        internal bool Disposed { get; private set; }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Disposed = true;
            proof.Events.Add($"context-{ordinal}-disposed");
        }
    }

    private sealed class ConflictCommands(ConflictProof proof) : DbCommandInterceptor
    {
        private static bool LedgerSelect(DbCommand command) =>
            command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.Ordinal) &&
            command.CommandText.Contains("\"VirtualKeyGroupTransactions\"", StringComparison.Ordinal);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (proof.IsRecovery(eventData.Context))
            {
                Assert.True(proof.Contexts[0].Disposed);
                Assert.Equal(1, proof.TransactionDisposals);
                if (LedgerSelect(command))
                {
                    proof.RecoveryWinnerQueries++;
                    proof.Events.Add("winner-query");
                    if (proof.CancelWinnerRead is not null)
                    {
                        Assert.Equal(proof.CancelWinnerRead.Token, cancellationToken);
                        proof.CancellationInjected = true;
                        proof.CancelWinnerRead.Cancel();
                    }
                }
                else if (command.CommandText.Contains("\"VirtualKeyGroups\"", StringComparison.Ordinal))
                    proof.RecoveryStateQueries++;
            }
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (proof.IsFirst(eventData.Context) && LedgerSelect(command))
            {
                Assert.False(proof.EmptyLookupObserved);
                Assert.False(result.HasRows);
                proof.EmptyLookupObserved = true;
                proof.Events.Add("empty-lookup");
                await proof.CommitWinnerAsync(cancellationToken);
            }
            else if (proof.IsRecovery(eventData.Context) && LedgerSelect(command))
            {
                Assert.True(result.HasRows);
                proof.RecoveryWinnerReads++;
            }
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (proof.IsFirst(eventData.Context) && command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.Ordinal))
            {
                Assert.Contains("\"VirtualKeyGroups\"", command.CommandText, StringComparison.Ordinal);
                Assert.Equal(1, result);
                proof.LoserUpdates++;
                proof.Events.Add("loser-update");
            }
            return ValueTask.FromResult(result);
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (proof.IsFirst(eventData.Context) && command.CommandText.Contains("INSERT INTO \"VirtualKeyGroupTransactions\"", StringComparison.Ordinal))
            {
                var postgres = Assert.IsType<PostgresException>(eventData.Exception);
                Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
                Assert.Contains("IdempotencyKey", postgres.ConstraintName!, StringComparison.Ordinal);
                proof.InsertException = postgres;
                proof.InsertFailures++;
                proof.Events.Add("insert-23505");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FailedSaveObserver(ConflictProof proof) : SaveChangesInterceptor
    {
        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Assert.True(proof.IsFirst(eventData.Context));
            var failure = Assert.IsType<DbUpdateException>(eventData.Exception);
            Assert.True(DbUpdateExceptions.IsUniqueViolation(failure, "IdempotencyKey"));
            Assert.Same(proof.InsertException, failure.InnerException);
            proof.SaveFailures++;
            proof.Events.Add("save-23505");
            return Task.CompletedTask;
        }
    }

    private sealed class PlainFactory(NpgsqlDataSource source) : IDbContextFactory<ConduitDbContext>
    {
        public ConduitDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(source).Options);
        public Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
