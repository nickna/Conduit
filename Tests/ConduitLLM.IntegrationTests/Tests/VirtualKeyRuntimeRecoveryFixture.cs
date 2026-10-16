using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

/// <summary>Owns only a fresh virtual-key recovery schema and its single-slot source.</summary>
internal sealed class VirtualKeyRuntimeRecoveryFixture : IAsyncDisposable
{
    private int _disposed;

    private VirtualKeyRuntimeRecoveryFixture(string fixtureConnection)
    {
        Schema = $"vk_recovery_{Guid.NewGuid():N}";
        ConnectionString = new NpgsqlConnectionStringBuilder(fixtureConnection)
        {
            SearchPath = Schema, ApplicationName = Schema, Pooling = true,
            MinPoolSize = 0, MaxPoolSize = 1, Timeout = 5, CommandTimeout = 10,
            CancellationTimeout = 1000
        }.ConnectionString;
        Source = NpgsqlDataSource.Create(ConnectionString);
    }

    public string Schema { get; }
    public string ConnectionString { get; }
    public NpgsqlDataSource Source { get; }

    public static async Task<VirtualKeyRuntimeRecoveryFixture> CreateAsync(
        string fixtureConnection, CancellationToken cancellationToken)
    {
        var owned = new VirtualKeyRuntimeRecoveryFixture(fixtureConnection);
        try
        {
            await owned.CreateSchemaAsync(cancellationToken);
            return owned;
        }
        catch (Exception exception)
        {
            await IntegrationProofCleanup.RunAsync(() => Task.FromException(exception),
                () => owned.DisposeAsync().AsTask(), "Recovery fixture creation and owned cleanup failed.");
            throw;
        }
    }

    public static async Task RunAsync(string fixtureConnection,
        Func<VirtualKeyRuntimeRecoveryFixture, CancellationToken, Task> work)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var owned = await CreateAsync(fixtureConnection, deadline.Token);
        await IntegrationProofCleanup.RunAsync(() => work(owned, deadline.Token),
            () => owned.DisposeAsync().AsTask(), "Virtual-key recovery proof and owned cleanup failed.");
    }

    public async Task<int> ExecuteAsync(string sql, CancellationToken token, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(UnpooledConnectionString());
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync(token);
    }

    public async Task<RecoveryGroupSnapshot> ReadGroupAsync(int id, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(UnpooledConnectionString());
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT "Balance", "LifetimeCreditsAdded", "LifetimeSpent"
            FROM "VirtualKeyGroups" WHERE "Id" = @id
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        Assert.True(await reader.ReadAsync(token));
        var result = new RecoveryGroupSnapshot(reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2));
        Assert.False(await reader.ReadAsync(token));
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await IntegrationProofCleanup.DrainIndependentlyAsync(
            [token => Source.DisposeAsync().AsTask().WaitAsync(token),
             token => ExecuteAsync($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE", token),
             async token =>
                {
                    await using var observer = new NpgsqlConnection(UnpooledConnectionString());
                    await observer.OpenAsync(token);
                    await using var command = new NpgsqlCommand(
                        "SELECT pg_catalog.to_regnamespace(@schema) IS NULL", observer);
                    command.Parameters.AddWithValue("schema", Schema);
                    Assert.True((bool)(await command.ExecuteScalarAsync(token))!);
                }],
            TimeSpan.FromSeconds(5), "Recovery source, schema drop, or independent absence verification failed.");
    }

    private string UnpooledConnectionString() => new NpgsqlConnectionStringBuilder(ConnectionString)
        { Pooling = false, ApplicationName = $"{Schema}_control" }.ConnectionString;

    private async Task CreateSchemaAsync(CancellationToken token)
    {
        await ExecuteAsync($"""
            CREATE SCHEMA "{Schema}";
            CREATE TABLE "{Schema}"."VirtualKeyGroups" (
                "Id" integer PRIMARY KEY,
                "ExternalGroupId" varchar(100) NULL, "GroupName" varchar(100) NOT NULL,
                "Balance" numeric(19,8) NOT NULL, "LifetimeCreditsAdded" numeric(19,8) NOT NULL,
                "LifetimeSpent" numeric(19,8) NOT NULL,
                "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL,
                "MediaRetentionPolicyId" integer NULL, "RateLimitRpm" integer NULL,
                "RateLimitRpd" integer NULL, "RateLimitTpm" integer NULL,
                "MaxParallelRequests" integer NULL, "RowVersion" bytea NULL);
            CREATE TABLE "{Schema}"."VirtualKeyGroupTransactions" (
                "Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "VirtualKeyGroupId" integer NOT NULL REFERENCES "{Schema}"."VirtualKeyGroups" ("Id"),
                "TransactionType" integer NOT NULL, "Amount" numeric(20,8) NOT NULL,
                "OriginalAmount" numeric(20,8) NULL,
                "OriginalAmountVerifiedBy" varchar(100) NULL,
                "OriginalAmountEvidence" varchar(500) NULL,
                "OriginalAmountVerifiedAtUtc" timestamptz NULL,
                "BalanceAfter" numeric(20,8) NOT NULL,
                "ReferenceType" integer NOT NULL, "ReferenceId" varchar(100) NULL,
                "Description" varchar(500) NULL, "InitiatedBy" varchar(50) NOT NULL,
                "InitiatedByUserId" varchar(100) NULL, "IdempotencyKey" varchar(100) NULL,
                "BillingWindowStartUtc" timestamptz NULL, "CreatedAt" timestamptz NOT NULL,
                "IsDeleted" boolean NOT NULL, "DeletedAt" timestamptz NULL);
            CREATE UNIQUE INDEX "IX_VKGT_IdempotencyKey"
                ON "{Schema}"."VirtualKeyGroupTransactions" ("IdempotencyKey")
                WHERE "IdempotencyKey" IS NOT NULL;
            INSERT INTO "{Schema}"."VirtualKeyGroups"
                ("Id", "GroupName", "Balance", "LifetimeCreditsAdded", "LifetimeSpent", "CreatedAt", "UpdatedAt")
            VALUES (1, 'owned recovery group', 100, 100, 0, @instant, @instant),
                   (2, 'unrelated recovery group', 200, 200, 0, @instant, @instant);
            """, token, new NpgsqlParameter("instant", new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc)));
    }
}

internal sealed record RecoveryGroupSnapshot(decimal Balance, decimal Credits, decimal Spent);
