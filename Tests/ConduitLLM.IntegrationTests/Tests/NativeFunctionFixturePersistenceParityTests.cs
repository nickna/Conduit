using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.IntegrationTests.Infrastructure;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
public sealed class NativeFunctionFixturePersistenceParityTests(PostgresLockTestContainerFixture fixture)
{
    private const string OpaqueProtectedKey = "enc:v1:fixture-only-opaque-ciphertext";
    private const decimal Price = 0.12500001m;
    private static readonly Uri Provider = new("http://127.0.0.1:15103/");

    [Fact]
    public async Task CreateAndReadPreserveGeneratedIdsOwnedLinksAndExactPersistedScalars()
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var owner = Guid.NewGuid();
        var snapshot = await CreateAsync(database, owner);
        Assert.Equal(owner, snapshot.Owner);
        foreach (var id in new[] { snapshot.GroupId, snapshot.KeyId, snapshot.ConfigurationId,
                     snapshot.CostId, snapshot.CredentialId, snapshot.MappingId })
            Assert.True(id > 0);
        Assert.Equal($"condt_native_function_{owner:N}", snapshot.VirtualKey);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.VirtualKey)))
            .ToLowerInvariant(), snapshot.VirtualKeyHash);
        Assert.Equal($"native_function_{owner:N}:exa", snapshot.ConfigurationName);
        await AssertFixtureRowsAsync(database, snapshot);
        Assert.Equal(snapshot, await NativeFunctionFixture.ReadAsync(database.Source, owner, database.Token));
        Assert.Equal(new long[] { 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0 },
            await database.CountsAsync());
    }

    [Fact]
    public async Task DeleteRemovesOnlyOwnedRowsAndDependentsAndIsIdempotent()
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var first = await CreateAsync(database, Guid.NewGuid());
        var second = await CreateAsync(database, Guid.NewGuid());
        await AddDependentsAsync(database, first);
        await AddDependentsAsync(database, second);
        int sentinelCost;
        int sentinelCredential;
        int sentinelIp;
        await using (var context = database.CreateContext())
        {
            var cost = new FunctionCost
            {
                CostName = "unrelated sentinel price", ProviderType = FunctionProviderType.Exa,
                PricingModel = FunctionPricingModel.FlatRate, CostPerExecution = 0.77777777m
            };
            var credential = new FunctionCredential
            {
                ProviderType = FunctionProviderType.Exa, KeyName = "unrelated global credential",
                ApiKey = "unrelated-global-fixture-key", IsPrimary = false
            };
            var ip = new IpFilterEntity
            {
                Name = "unrelated global IP", FilterType = "whitelist", IpAddressOrCidr = "127.0.0.1"
            };
            context.AddRange(cost, credential, ip);
            await context.SaveChangesAsync(database.Token);
            sentinelCost = cost.Id;
            sentinelCredential = credential.Id;
            sentinelIp = ip.Id;
        }

        Assert.Equal(first, await NativeFunctionFixture.DeleteAsync(database.Source, first.Owner, database.Token));
        Assert.Null(await NativeFunctionFixture.ReadAsync(database.Source, first.Owner, database.Token));
        Assert.Null(await NativeFunctionFixture.DeleteAsync(database.Source, first.Owner, database.Token));
        Assert.Equal(second, await NativeFunctionFixture.ReadAsync(database.Source, second.Owner, database.Token));
        await AssertFixtureRowsAsync(database, second);
        Assert.Equal(new long[] { 1, 1, 2, 1, 2, 2, 1, 1, 1, 1, 1, 1, 1 },
            await database.CountsAsync());
        Assert.Equal(0.77777777m, (await database.RowAsync(
            "SELECT \"CostPerExecution\" FROM \"FunctionCosts\" WHERE \"Id\" = @id", ("id", sentinelCost)))[0]);
        Assert.Equal("unrelated-global-fixture-key", (await database.RowAsync(
            "SELECT \"ApiKey\" FROM \"FunctionCredentials\" WHERE \"Id\" = @id", ("id", sentinelCredential)))[0]);
        Assert.Equal(DBNull.Value, (await database.RowAsync(
            "SELECT \"VirtualKeyId\" FROM \"IpFilters\" WHERE \"Id\" = @id", ("id", sentinelIp)))[0]);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("read")]
    [InlineData("delete")]
    public async Task PreCancelledOperationsDoNotMutateRowsAndLeaveTheSingleSlotReusable(string operation)
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var existing = await CreateAsync(database, Guid.NewGuid());
        var before = await database.FingerprintAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (operation)
            {
                case "create":
                    await NativeFunctionFixture.CreateAsync(database.Source, Guid.NewGuid(), Provider,
                        OpaqueProtectedKey, cancelled.Token);
                    break;
                case "read":
                    await NativeFunctionFixture.ReadAsync(database.Source, existing.Owner, cancelled.Token);
                    break;
                case "delete":
                    await NativeFunctionFixture.DeleteAsync(database.Source, existing.Owner, cancelled.Token);
                    break;
            }
        });
        Assert.Equal(before, await database.FingerprintAsync());
        Assert.Equal(existing, await NativeFunctionFixture.ReadAsync(database.Source, existing.Owner, database.Token));
    }

    [Fact]
    public async Task LateCreateSqlFailureRollsBackEveryOwnedRowAndReleasesTheConnection()
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        await database.ExecuteAsync("""
            ALTER TABLE "FunctionCostMappings" ADD CONSTRAINT fixture_reject_mapping
                CHECK ("FunctionConfigurationId" < 0)
            """);
        var owner = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<PostgresException>(() => CreateAsync(database, owner));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.All(await database.CountsAsync(), count => Assert.Equal(0, count));
        Assert.Null(await NativeFunctionFixture.ReadAsync(database.Source, owner, database.Token));
        await database.ExecuteAsync("ALTER TABLE \"FunctionCostMappings\" DROP CONSTRAINT fixture_reject_mapping");
        var recovered = await CreateAsync(database, owner);
        await AssertFixtureRowsAsync(database, recovered);
    }

    [Fact]
    public async Task DuplicateCreateRejectsAnExistingOwnerWithoutOverwritingItsRows()
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var existing = await CreateAsync(database, Guid.NewGuid());
        var before = await database.FingerprintAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeFunctionFixture.CreateAsync(
            database.Source, existing.Owner, new Uri("http://127.0.0.1:15199/"),
            "enc:v1:different-opaque-ciphertext", database.Token));
        Assert.Equal(before, await database.FingerprintAsync());
        Assert.Equal(existing, await NativeFunctionFixture.ReadAsync(database.Source, existing.Owner, database.Token));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("ambiguous")]
    public async Task IncompleteOrAmbiguousOwnershipRejectsReadsAndDeletionWithoutFurtherMutation(string damage)
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var owned = await CreateAsync(database, Guid.NewGuid());
        if (damage == "partial")
            await database.ExecuteAsync("DELETE FROM \"FunctionCredentials\" WHERE \"Id\" = @id", ("id", owned.CredentialId));
        else
            await database.ExecuteAsync("""
                INSERT INTO "VirtualKeyGroups" ("ExternalGroupId", "GroupName", "Balance",
                    "LifetimeCreditsAdded", "LifetimeSpent", "CreatedAt", "UpdatedAt")
                SELECT "ExternalGroupId", "GroupName", "Balance", "LifetimeCreditsAdded",
                    "LifetimeSpent", "CreatedAt", "UpdatedAt" FROM "VirtualKeyGroups" WHERE "Id" = @id
                """, ("id", owned.GroupId));
        await AssertRejectedWithoutMutationAsync(database, owned.Owner);
    }

    [Theory]
    [InlineData("shadow-cost")]
    [InlineData("shadow-audit")]
    [InlineData("foreign-key-execution")]
    [InlineData("notification-cascade")]
    public async Task ForeignReferencesCannotBeDeletedOrSilentlyChangedThroughOwnedParents(string reference)
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var owned = await CreateAsync(database, Guid.NewGuid());
        var foreign = await CreateAsync(database, Guid.NewGuid());
        var ownedExecution = await AddDependentsAsync(database, owned);
        var foreignExecution = await AddDependentsAsync(database, foreign);
        switch (reference)
        {
            case "shadow-cost":
                await database.ExecuteAsync("""
                    UPDATE "FunctionCostMappings" SET "FunctionCostId1" = @owned
                    WHERE "Id" = @foreign
                    """, ("owned", owned.CostId), ("foreign", foreign.MappingId));
                break;
            case "shadow-audit":
                await database.ExecuteAsync("""
                    UPDATE "FunctionExecutionAudits" SET "FunctionExecutionId1" = @owned
                    WHERE "FunctionExecutionId" = @foreign
                    """, ("owned", ownedExecution), ("foreign", foreignExecution));
                break;
            case "foreign-key-execution":
                await database.ExecuteAsync("""
                    UPDATE "FunctionExecutions" SET "FunctionConfigurationId" = @owned
                    WHERE "Id" = @foreign
                    """, ("owned", owned.ConfigurationId), ("foreign", foreignExecution));
                break;
            case "notification-cascade":
                await using (var context = database.CreateContext())
                {
                    context.Add(new Notification
                    {
                        VirtualKeyId = owned.KeyId, Type = NotificationType.System,
                        Message = "Unrelated notification must survive rejected fixture cleanup."
                    });
                    await context.SaveChangesAsync(database.Token);
                }
                break;
        }
        // The fingerprint includes every scalar, including nullable shadow FKs:
        // equal row counts alone would miss a SetNull update or wrong-owner overwrite.
        await AssertRejectedWithoutMutationAsync(database, owned.Owner);
    }

    [Fact]
    public async Task ActualSchemaRetainsEightDecimalFunctionAndGroupValuesButRoundsLedgerAndRequestLogValues()
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var owned = await CreateAsync(database, Guid.NewGuid());
        var execution = await AddDependentsAsync(database, owned);
        await database.ExecuteAsync("""
            UPDATE "VirtualKeyGroups" SET "LifetimeSpent" = @price, "Balance" = 100 - @price
            WHERE "Id" = @group
            """, ("price", Price), ("group", owned.GroupId));
        var values = await database.RowAsync("""
            SELECT c."CostPerExecution", e."ActualCost", g."LifetimeSpent", g."Balance",
                t."Amount", t."BalanceAfter", l."Cost", s."Amount"
            FROM "FunctionCosts" c JOIN "FunctionExecutions" e ON e."Id" = @execution
            JOIN "VirtualKeyGroups" g ON g."Id" = @group
            JOIN "VirtualKeyGroupTransactions" t ON t."VirtualKeyGroupId" = g."Id"
            JOIN "RequestLogs" l ON l."VirtualKeyId" = @key
            JOIN "VirtualKeySpendHistory" s ON s."VirtualKeyId" = @key
            WHERE c."Id" = @cost
            """, ("execution", execution), ("group", owned.GroupId), ("key", owned.KeyId), ("cost", owned.CostId));
        Assert.Equal(new object[] { Price, Price, Price, 99.87499999m,
            0.125000m, 99.875000m, 0.125000m, 0.125000m }, values);
    }

    private static Task<NativeFunctionFixtureSnapshot> CreateAsync(OwnedDatabase database, Guid owner) =>
        NativeFunctionFixture.CreateAsync(database.Source, owner, Provider, OpaqueProtectedKey, database.Token);

    private static async Task AssertRejectedWithoutMutationAsync(OwnedDatabase database, Guid owner)
    {
        var before = await database.FingerprintAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeFunctionFixture.ReadAsync(database.Source, owner, database.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeFunctionFixture.DeleteAsync(database.Source, owner, database.Token));
        Assert.Equal(before, await database.FingerprintAsync());
    }

    private static async Task AssertFixtureRowsAsync(OwnedDatabase database, NativeFunctionFixtureSnapshot snapshot)
    {
        var stem = $"native_function_{snapshot.Owner:N}";
        var description = $"Native function fixture {snapshot.Owner:N}";
        Assert.Equal(new object[] { stem, $"Native function {snapshot.Owner:N}", 100m, 100m, 0m },
            await database.RowAsync("""
                SELECT "ExternalGroupId", "GroupName", "Balance", "LifetimeCreditsAdded", "LifetimeSpent"
                FROM "VirtualKeyGroups" WHERE "Id" = @id
                """, ("id", snapshot.GroupId)));
        Assert.Equal(new object[] { stem + ":key", snapshot.VirtualKeyHash, description, true,
                snapshot.GroupId, 100, 1000, DBNull.Value },
            await database.RowAsync("""
                SELECT "KeyName", "KeyHash", "Description", "IsEnabled", "VirtualKeyGroupId",
                    "RateLimitRpm", "RateLimitRpd", "ExpiresAt" FROM "VirtualKeys" WHERE "Id" = @id
                """, ("id", snapshot.KeyId)));
        var configuration = await database.RowAsync("""
            SELECT "ConfigurationName", "ProviderType", "Purpose", "DefaultExecutionMode", "BaseUrl",
                "IsEnabled", "TimeoutSeconds", "MaxRetries", "CacheTtlMinutes", "ProviderSettings",
                "Description", "ParameterSchema" FROM "FunctionConfigurations" WHERE "Id" = @id
            """, ("id", snapshot.ConfigurationId));
        Assert.Equal(new object[] { snapshot.ConfigurationName, (int)FunctionProviderType.Exa,
            (int)FunctionPurpose.Search, (int)ExecutionMode.Synchronous, Provider.ToString().TrimEnd('/'),
            true, 30, 0, DBNull.Value, DBNull.Value, description }, configuration[..11]);
        using (var schema = JsonDocument.Parse(Assert.IsType<string>(configuration[11])))
        {
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
            Assert.Equal("string", schema.RootElement.GetProperty("properties").GetProperty("query")
                .GetProperty("type").GetString());
            Assert.Equal("query", Assert.Single(schema.RootElement.GetProperty("required").EnumerateArray()).GetString());
        }
        Assert.Equal(new object[] { (int)FunctionProviderType.Exa, snapshot.ConfigurationId, OpaqueProtectedKey,
                stem + ":credential", true, true, (short)0, Provider.ToString().TrimEnd('/'), DBNull.Value },
            await database.RowAsync("""
                SELECT "ProviderType", "FunctionConfigurationId", "ApiKey", "KeyName", "IsPrimary",
                    "IsEnabled", "FunctionAccountGroup", "BaseUrl", "Organization"
                FROM "FunctionCredentials" WHERE "Id" = @id
                """, ("id", snapshot.CredentialId)));
        Assert.Equal(new object[] { stem + ":cost", (int)FunctionProviderType.Exa, (int)FunctionPurpose.Search,
                (int)FunctionPricingModel.FlatRate, Price, true, 1, DBNull.Value },
            await database.RowAsync("""
                SELECT "CostName", "ProviderType", "Purpose", "PricingModel", "CostPerExecution", "IsActive",
                    "Priority", "ExpiryDate" FROM "FunctionCosts" WHERE "Id" = @id
                """, ("id", snapshot.CostId)));
        Assert.Equal(new object[] { snapshot.ConfigurationId, snapshot.CostId, DBNull.Value, true },
            await database.RowAsync("""
                SELECT "FunctionConfigurationId", "FunctionCostId", "FunctionCostId1", "IsActive"
                FROM "FunctionCostMappings" WHERE "Id" = @id
                """, ("id", snapshot.MappingId)));
        Assert.Equal(new object[] { "whitelist", "127.0.0.1", stem + ":ip", description, true,
                snapshot.KeyId, stem, stem },
            await database.RowAsync("""
                SELECT "FilterType", "IpAddressOrCidr", "Name", "Description", "IsEnabled", "VirtualKeyId",
                    "CreatedBy", "UpdatedBy" FROM "IpFilters" WHERE "VirtualKeyId" = @id
                """, ("id", snapshot.KeyId)));
    }

    private static async Task<Guid> AddDependentsAsync(OwnedDatabase database, NativeFunctionFixtureSnapshot snapshot)
    {
        await using var context = database.CreateContext();
        var execution = new FunctionExecution
        {
            FunctionConfigurationId = snapshot.ConfigurationId, VirtualKeyId = snapshot.KeyId,
            ExecutionMode = ExecutionMode.Synchronous, State = ExecutionState.Completed, ActualCost = Price
        };
        context.AddRange(execution,
            new FunctionExecutionAudit { FunctionExecutionId = execution.Id, VirtualKeyId = snapshot.KeyId, Cost = Price },
            new FunctionCallAudit
            {
                FunctionExecutionId = execution.Id, FunctionConfigurationId = snapshot.ConfigurationId,
                VirtualKeyId = snapshot.KeyId, Cost = Price
            },
            new RequestLog { VirtualKeyId = snapshot.KeyId, ModelName = "fixture-function", RequestType = "function", Cost = Price },
            new VirtualKeySpendHistory
            {
                VirtualKeyId = snapshot.KeyId, Amount = Price, Date = DateTime.UtcNow, Timestamp = DateTime.UtcNow
            },
            new VirtualKeyGroupTransaction
            {
                VirtualKeyGroupId = snapshot.GroupId, TransactionType = TransactionType.Debit,
                ReferenceType = ReferenceType.VirtualKey, Amount = Price, BalanceAfter = 100m - Price,
                ReferenceId = execution.Id.ToString(), InitiatedBy = "Fixture"
            });
        await context.SaveChangesAsync(database.Token);
        return execution.Id;
    }

    private sealed class OwnedDatabase : IAsyncDisposable
    {
        private static readonly string[] Tables =
        ["VirtualKeyGroups", "VirtualKeys", "IpFilters", "FunctionConfigurations", "FunctionCredentials",
            "FunctionCosts", "FunctionCostMappings", "FunctionExecutions", "FunctionExecutionAudits",
            "FunctionCallAudits", "RequestLogs", "VirtualKeySpendHistory", "VirtualKeyGroupTransactions"];
        private readonly string _management;
        private readonly string _schema;
        private readonly string _connectionString;
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
        public NpgsqlDataSource Source { get; }
        public CancellationToken Token => _deadline.Token;

        private OwnedDatabase(string management, string schema)
        {
            _management = management;
            _schema = schema;
            _connectionString = new NpgsqlConnectionStringBuilder(management)
                { SearchPath = schema, ApplicationName = schema, MaxPoolSize = 1 }.ConnectionString;
            Source = NpgsqlDataSource.Create(_connectionString);
        }

        public static async Task<OwnedDatabase> CreateAsync(string management)
        {
            var database = new OwnedDatabase(management, $"native_function_fixture_{Guid.NewGuid():N}");
            try
            {
                await using (var connection = new NpgsqlConnection(management))
                {
                    await connection.OpenAsync(database.Token);
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"CREATE SCHEMA \"{database._schema}\"";
                    await command.ExecuteNonQueryAsync(database.Token);
                }
                await using var context = database.CreateContext();
                // EnsureCreated considers tables in all schemas. Execute the actual EF
                // model's creation script on this isolated search path instead.
                await context.Database.OpenConnectionAsync(database.Token);
                await using var create = context.Database.GetDbConnection().CreateCommand();
                create.CommandText = context.Database.GenerateCreateScript();
                await create.ExecuteNonQueryAsync(database.Token);
                return database;
            }
            catch (Exception primary)
            {
                try { await database.DisposeAsync(); }
                catch (Exception cleanup)
                {
                    throw new AggregateException("Function fixture schema creation and cleanup both failed.", primary, cleanup);
                }
                throw;
            }
        }

        public ConduitDbContext CreateContext() => new(new DbContextOptionsBuilder<ConduitDbContext>()
            .UseNpgsql(_connectionString).Options);

        public async Task<object[]> RowAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await Source.OpenConnectionAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await using var reader = await command.ExecuteReaderAsync(Token);
            Assert.True(await reader.ReadAsync(Token));
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            Assert.False(await reader.ReadAsync(Token));
            return values;
        }

        public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await Source.OpenConnectionAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync(Token);
        }

        public async Task<long[]> CountsAsync()
        {
            var counts = new List<long>();
            foreach (var table in Tables)
                counts.Add(Assert.IsType<long>((await RowAsync($"SELECT count(*) FROM \"{table}\""))[0]));
            return counts.ToArray();
        }

        public async Task<string[]> FingerprintAsync()
        {
            var result = new List<string>();
            foreach (var table in Tables.Append("Notifications"))
                result.Add(Assert.IsType<string>((await RowAsync($"""
                    SELECT md5(COALESCE(jsonb_agg(to_jsonb(row) ORDER BY row."Id")::text, '[]'))
                    FROM "{table}" row
                    """))[0]));
            return result.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            try { await Source.DisposeAsync(); }
            finally
            {
                _deadline.Dispose();
                if (!_schema.StartsWith("native_function_fixture_", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(_schema["native_function_fixture_".Length..], "N", out _))
                    throw new InvalidOperationException("Cleanup requires an owned function fixture schema.");
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await using var connection = new NpgsqlConnection(_management);
                await connection.OpenAsync(cleanup.Token);
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE";
                await command.ExecuteNonQueryAsync(cleanup.Token);
            }
        }
    }
}
