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
    private static readonly Uri McpProvider = new("http://127.0.0.1:15103/mcp");

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
    [InlineData("foreign-canonical-cost")]
    [InlineData("moved-canonical-cost")]
    [InlineData("foreign-canonical-audit")]
    [InlineData("moved-canonical-audit")]
    [InlineData("foreign-audit-key")]
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
            case "foreign-canonical-cost":
                await database.ExecuteAsync("""
                    UPDATE "FunctionCostMappings" SET "FunctionCostId" = @owned
                    WHERE "Id" = @foreign
                    """, ("owned", owned.CostId), ("foreign", foreign.MappingId));
                break;
            case "moved-canonical-cost":
                await database.ExecuteAsync("""
                    UPDATE "FunctionCostMappings" SET "FunctionCostId" = @foreign
                    WHERE "Id" = @owned
                    """, ("foreign", foreign.CostId), ("owned", owned.MappingId));
                break;
            case "foreign-canonical-audit":
                await database.ExecuteAsync("""
                    UPDATE "FunctionExecutionAudits" SET "FunctionExecutionId" = @owned
                    WHERE "FunctionExecutionId" = @foreign
                    """, ("owned", ownedExecution), ("foreign", foreignExecution));
                // Moving the relationship must not rewrite the audit's independent owner.
                Assert.Equal(new object[] { foreign.KeyId }, await database.RowAsync("""
                    SELECT "VirtualKeyId" FROM "FunctionExecutionAudits"
                    WHERE "FunctionExecutionId" = @execution AND "VirtualKeyId" = @key
                    """, ("execution", ownedExecution), ("key", foreign.KeyId)));
                break;
            case "moved-canonical-audit":
                await database.ExecuteAsync("""
                    UPDATE "FunctionExecutionAudits" SET "FunctionExecutionId" = @foreign
                    WHERE "FunctionExecutionId" = @owned
                    """, ("foreign", foreignExecution), ("owned", ownedExecution));
                Assert.Equal(new object[] { owned.KeyId }, await database.RowAsync("""
                    SELECT "VirtualKeyId" FROM "FunctionExecutionAudits"
                    WHERE "FunctionExecutionId" = @execution AND "VirtualKeyId" = @key
                    """, ("execution", foreignExecution), ("key", owned.KeyId)));
                break;
            case "foreign-audit-key":
                await database.ExecuteAsync("""
                    UPDATE "FunctionExecutionAudits" SET "VirtualKeyId" = @foreign
                    WHERE "FunctionExecutionId" = @owned
                    """, ("foreign", foreign.KeyId), ("owned", ownedExecution));
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
        // The fingerprint includes every scalar, canonical relationship and audit owner:
        // equal row counts alone would miss a relationship or wrong-owner overwrite.
        await AssertRejectedWithoutMutationAsync(database, owned.Owner);
    }

    [Fact]
    public async Task ActualSchemaRetainsEightDecimalFunctionGroupLedgerAndRequestLogValues()
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
            Price, 99.87499999m, Price, Price }, values);
    }

    [Fact]
    public async Task McpProfilePersistsExactIndependentEfScalarsJsonbAndScopedCredentialWithGeneratedPositiveIds()
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var owner = Guid.NewGuid();
        var snapshot = await CreateMcpAsync(database, owner);

        Assert.Equal(owner, snapshot.Owner);
        Assert.Equal(NativeFunctionFixtureProfile.Mcp, snapshot.Profile);
        Assert.Equal($"native_function_{owner:N}:mcp", snapshot.ConfigurationName);
        Assert.Equal($"condt_native_function_{owner:N}", snapshot.VirtualKey);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.VirtualKey)))
            .ToLowerInvariant(), snapshot.VirtualKeyHash);
        Assert.All(new[] { snapshot.GroupId, snapshot.KeyId, snapshot.ConfigurationId,
            snapshot.CostId, snapshot.CredentialId, snapshot.MappingId }, id => Assert.True(id > 0));
        await AssertMcpFixtureRowsAsync(database, snapshot);
        Assert.Equal(snapshot, await NativeFunctionFixture.ReadAsync(database.Source, owner,
            NativeFunctionFixtureProfile.Mcp, database.Token));
        Assert.Equal(new long[] { 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0 }, await database.CountsAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedProfileCleanupPreservesTheOtherOwnerItsDependentsAndUnrelatedGlobalRows(bool deleteMcp)
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var exa = await CreateAsync(database, Guid.NewGuid());
        var mcp = await CreateMcpAsync(database, Guid.NewGuid());
        await AddDependentsAsync(database, exa);
        await AddDependentsAsync(database, mcp);
        int sentinelCost;
        int sentinelCredential;
        int sentinelIp;
        await using (var context = database.CreateContext())
        {
            var cost = new FunctionCost
            {
                CostName = "unrelated MCP sentinel price", ProviderType = FunctionProviderType.Mcp,
                PricingModel = FunctionPricingModel.FlatRate, CostPerExecution = 0.77777777m
            };
            var credential = new FunctionCredential
            {
                ProviderType = FunctionProviderType.Mcp, KeyName = "unrelated MCP global credential",
                ApiKey = "unrelated-mcp-global-key", IsPrimary = true
            };
            var ip = new IpFilterEntity
            {
                Name = "unrelated mixed-profile global IP", FilterType = "whitelist", IpAddressOrCidr = "127.0.0.1"
            };
            context.AddRange(cost, credential, ip);
            await context.SaveChangesAsync(database.Token);
            sentinelCost = cost.Id;
            sentinelCredential = credential.Id;
            sentinelIp = ip.Id;
        }
        var removed = deleteMcp ? mcp : exa;
        var survivor = deleteMcp ? exa : mcp;

        Assert.Equal(removed, await NativeFunctionFixture.DeleteAsync(database.Source, removed.Owner,
            removed.Profile, database.Token));
        Assert.Null(await NativeFunctionFixture.ReadAsync(database.Source, removed.Owner, removed.Profile, database.Token));
        Assert.Null(await NativeFunctionFixture.DeleteAsync(database.Source, removed.Owner, removed.Profile, database.Token));
        Assert.Equal(survivor, await NativeFunctionFixture.ReadAsync(database.Source, survivor.Owner,
            survivor.Profile, database.Token));
        if (deleteMcp) await AssertFixtureRowsAsync(database, survivor);
        else await AssertMcpFixtureRowsAsync(database, survivor);
        Assert.Equal(new long[] { 1, 1, 2, 1, 2, 2, 1, 1, 1, 1, 1, 1, 1 }, await database.CountsAsync());
        Assert.Equal(new object[] { (int)FunctionProviderType.Mcp, 0.77777777m, "unrelated MCP sentinel price" },
            await database.RowAsync("""
                SELECT "ProviderType", "CostPerExecution", "CostName" FROM "FunctionCosts" WHERE "Id" = @id
                """, ("id", sentinelCost)));
        Assert.Equal(new object[] { (int)FunctionProviderType.Mcp, DBNull.Value, "unrelated-mcp-global-key", true, true },
            await database.RowAsync("""
                SELECT "ProviderType", "FunctionConfigurationId", "ApiKey", "IsPrimary", "IsEnabled"
                FROM "FunctionCredentials" WHERE "Id" = @id
                """, ("id", sentinelCredential)));
        Assert.Equal(new object[] { DBNull.Value, "unrelated mixed-profile global IP", "127.0.0.1" },
            await database.RowAsync("""
                SELECT "VirtualKeyId", "Name", "IpAddressOrCidr" FROM "IpFilters" WHERE "Id" = @id
                """, ("id", sentinelIp)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WrongProfileCannotReadOrDeleteAnOwnedGraphAndCorrectProfileRemainsReusable(bool ownedMcp)
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var owned = ownedMcp ? await CreateMcpAsync(database, Guid.NewGuid()) : await CreateAsync(database, Guid.NewGuid());
        await AddDependentsAsync(database, owned);
        var wrongProfile = ownedMcp ? NativeFunctionFixtureProfile.Exa : NativeFunctionFixtureProfile.Mcp;
        var before = await database.FingerprintAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeFunctionFixture.ReadAsync(
            database.Source, owned.Owner, wrongProfile, database.Token));
        Assert.Equal(before, await database.FingerprintAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeFunctionFixture.DeleteAsync(
            database.Source, owned.Owner, wrongProfile, database.Token));
        Assert.Equal(before, await database.FingerprintAsync());
        Assert.Equal(owned, await NativeFunctionFixture.ReadAsync(database.Source, owned.Owner, owned.Profile, database.Token));
    }

    [Fact]
    public async Task McpLateMappingFailureRollsBackTheEntireGraphAndReleasesTheSingleConnectionSlot()
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        await database.ExecuteAsync("""
            ALTER TABLE "FunctionCostMappings" ADD CONSTRAINT fixture_reject_mcp_mapping
                CHECK ("FunctionConfigurationId" < 0)
            """);
        var owner = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<PostgresException>(() => CreateMcpAsync(database, owner));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.All(await database.CountsAsync(), count => Assert.Equal(0, count));
        Assert.Null(await NativeFunctionFixture.ReadAsync(database.Source, owner, NativeFunctionFixtureProfile.Mcp, database.Token));
        await database.ExecuteAsync("ALTER TABLE \"FunctionCostMappings\" DROP CONSTRAINT fixture_reject_mcp_mapping");
        var recovered = await CreateMcpAsync(database, owner);
        await AssertMcpFixtureRowsAsync(database, recovered);
    }

    [Fact]
    public async Task McpProfileRejectsAnExaOwnersExecutionReferencingItsConfigurationWithoutChangingAnyScalar()
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var mcp = await CreateMcpAsync(database, Guid.NewGuid());
        var exa = await CreateAsync(database, Guid.NewGuid());
        await AddDependentsAsync(database, mcp);
        var foreignExecution = await AddDependentsAsync(database, exa);
        await database.ExecuteAsync("""
            UPDATE "FunctionExecutions" SET "FunctionConfigurationId" = @owned WHERE "Id" = @foreign
            """, ("owned", mcp.ConfigurationId), ("foreign", foreignExecution));
        var before = await database.FingerprintAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeFunctionFixture.ReadAsync(
            database.Source, mcp.Owner, NativeFunctionFixtureProfile.Mcp, database.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeFunctionFixture.DeleteAsync(
            database.Source, mcp.Owner, NativeFunctionFixtureProfile.Mcp, database.Token));

        Assert.Equal(before, await database.FingerprintAsync());
    }

    [Fact]
    public async Task McpConfigurationAndCredentialCannotBothBeRetargetedToAnUnownedEndpointBeforeReadOrDeletion()
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var mcp = await CreateMcpAsync(database, Guid.NewGuid());
        await AddDependentsAsync(database, mcp);
        await database.ExecuteAsync("""
            UPDATE "FunctionConfigurations" SET "BaseUrl" = @url WHERE "Id" = @configuration;
            UPDATE "FunctionCredentials" SET "BaseUrl" = @url WHERE "Id" = @credential;
            """, ("url", "http://203.0.113.8/mcp"), ("configuration", mcp.ConfigurationId), ("credential", mcp.CredentialId));
        var before = await database.FingerprintAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeFunctionFixture.ReadAsync(
            database.Source, mcp.Owner, NativeFunctionFixtureProfile.Mcp, database.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeFunctionFixture.DeleteAsync(
            database.Source, mcp.Owner, NativeFunctionFixtureProfile.Mcp, database.Token));

        Assert.Equal(before, await database.FingerprintAsync());
    }

    [Theory]
    [InlineData("create")]
    [InlineData("read")]
    [InlineData("delete")]
    public async Task McpPreCancelledOperationsPreserveBothProfilesAndTheSingleSlotCanBeReused(string operation)
    {
        await using var database = await OwnedDatabase.CreateAsync(fixture.ConnectionString);
        var mcp = await CreateMcpAsync(database, Guid.NewGuid());
        var exa = await CreateAsync(database, Guid.NewGuid());
        var before = await database.FingerprintAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (operation)
            {
                case "create":
                    await NativeFunctionFixture.CreateAsync(database.Source, Guid.NewGuid(), McpProvider,
                        OpaqueProtectedKey, NativeFunctionFixtureProfile.Mcp, cancelled.Token);
                    break;
                case "read":
                    await NativeFunctionFixture.ReadAsync(database.Source, mcp.Owner, NativeFunctionFixtureProfile.Mcp, cancelled.Token);
                    break;
                case "delete":
                    await NativeFunctionFixture.DeleteAsync(database.Source, mcp.Owner, NativeFunctionFixtureProfile.Mcp, cancelled.Token);
                    break;
            }
        });

        Assert.Equal(before, await database.FingerprintAsync());
        Assert.Equal(mcp, await NativeFunctionFixture.ReadAsync(database.Source, mcp.Owner, NativeFunctionFixtureProfile.Mcp, database.Token));
        Assert.Equal(exa, await NativeFunctionFixture.ReadAsync(database.Source, exa.Owner, database.Token));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("read")]
    [InlineData("delete")]
    public async Task UndefinedProfileIsRejectedBeforeOpeningEvenADisposedDataSource(string operation)
    {
        await using var source = NpgsqlDataSource.Create(fixture.ConnectionString);
        await source.DisposeAsync();
        var owner = Guid.NewGuid();
        var undefined = (NativeFunctionFixtureProfile)999;

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            switch (operation)
            {
                case "create":
                    await NativeFunctionFixture.CreateAsync(source, owner, McpProvider, OpaqueProtectedKey, undefined, CancellationToken.None);
                    break;
                case "read":
                    await NativeFunctionFixture.ReadAsync(source, owner, undefined, CancellationToken.None);
                    break;
                case "delete":
                    await NativeFunctionFixture.DeleteAsync(source, owner, undefined, CancellationToken.None);
                    break;
            }
        });

        Assert.Equal("profile", error.ParamName);
    }

    [Fact]
    public async Task ClosedProfilesRejectEachOthersEndpointPathBeforeOpeningTheDataSource()
    {
        await using var source = NpgsqlDataSource.Create(fixture.ConnectionString);
        await source.DisposeAsync();
        var mcpError = await Assert.ThrowsAsync<ArgumentException>(() => NativeFunctionFixture.CreateAsync(
            source, Guid.NewGuid(), Provider, OpaqueProtectedKey, NativeFunctionFixtureProfile.Mcp, CancellationToken.None));
        var exaError = await Assert.ThrowsAsync<ArgumentException>(() => NativeFunctionFixture.CreateAsync(
            source, Guid.NewGuid(), McpProvider, OpaqueProtectedKey, NativeFunctionFixtureProfile.Exa, CancellationToken.None));
        Assert.Equal("providerUri", mcpError.ParamName);
        Assert.Equal("providerUri", exaError.ParamName);
    }

    private static Task<NativeFunctionFixtureSnapshot> CreateMcpAsync(OwnedDatabase database, Guid owner) =>
        NativeFunctionFixture.CreateAsync(database.Source, owner, McpProvider, OpaqueProtectedKey,
            NativeFunctionFixtureProfile.Mcp, database.Token);

    private static async Task AssertMcpFixtureRowsAsync(OwnedDatabase database, NativeFunctionFixtureSnapshot snapshot)
    {
        var stem = $"native_function_{snapshot.Owner:N}";
        var description = $"Native function fixture {snapshot.Owner:N}";
        await using var context = database.CreateContext();
        var group = await context.VirtualKeyGroups.AsNoTracking().SingleAsync(value => value.Id == snapshot.GroupId, database.Token);
        Assert.Equal(stem, group.ExternalGroupId);
        Assert.Equal($"Native function {snapshot.Owner:N}", group.GroupName);
        Assert.Equal(100m, group.Balance);
        Assert.Equal(100m, group.LifetimeCreditsAdded);
        Assert.Equal(0m, group.LifetimeSpent);
        Assert.Null(group.MediaRetentionPolicyId);
        var key = await context.VirtualKeys.AsNoTracking().SingleAsync(value => value.Id == snapshot.KeyId, database.Token);
        Assert.Equal(stem + ":key", key.KeyName);
        Assert.Equal(snapshot.VirtualKeyHash, key.KeyHash);
        Assert.Equal(description, key.Description);
        Assert.True(key.IsEnabled);
        Assert.Equal(snapshot.GroupId, key.VirtualKeyGroupId);
        Assert.Equal(100, key.RateLimitRpm);
        Assert.Equal(1000, key.RateLimitRpd);
        Assert.Null(key.ExpiresAt);
        var configuration = await context.Set<FunctionConfiguration>().AsNoTracking()
            .SingleAsync(value => value.Id == snapshot.ConfigurationId, database.Token);
        Assert.Equal(stem + ":mcp", configuration.ConfigurationName);
        Assert.Equal(FunctionProviderType.Mcp, configuration.ProviderType);
        Assert.Equal(FunctionPurpose.Search, configuration.Purpose);
        Assert.Equal(ExecutionMode.Synchronous, configuration.DefaultExecutionMode);
        Assert.Equal(McpProvider.AbsoluteUri, configuration.BaseUrl);
        Assert.True(configuration.IsEnabled);
        Assert.Equal(30, configuration.TimeoutSeconds);
        Assert.Equal(0, configuration.MaxRetries);
        Assert.Null(configuration.CacheTtlMinutes);
        Assert.Equal(description, configuration.Description);
        AssertJson("""{"allowedTools":["native_search"],"allowPrivateNetwork":true}""", configuration.ProviderSettings);
        AssertJson("""{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""", configuration.ParameterSchema);
        var credential = await context.Set<FunctionCredential>().AsNoTracking()
            .SingleAsync(value => value.Id == snapshot.CredentialId, database.Token);
        Assert.Equal(FunctionProviderType.Mcp, credential.ProviderType);
        Assert.Equal(snapshot.ConfigurationId, credential.FunctionConfigurationId);
        Assert.Equal(OpaqueProtectedKey, credential.ApiKey);
        Assert.Equal(McpProvider.AbsoluteUri, credential.BaseUrl);
        Assert.Null(credential.Organization);
        Assert.Equal((short)0, credential.FunctionAccountGroup);
        Assert.True(credential.IsPrimary);
        Assert.True(credential.IsEnabled);
        Assert.Equal(stem + ":credential", credential.KeyName);
        var cost = await context.Set<FunctionCost>().AsNoTracking().SingleAsync(value => value.Id == snapshot.CostId, database.Token);
        Assert.Equal(stem + ":cost", cost.CostName);
        Assert.Equal(FunctionProviderType.Mcp, cost.ProviderType);
        Assert.Equal(FunctionPurpose.Search, cost.Purpose);
        Assert.Equal(description, cost.Description);
        Assert.Equal(FunctionPricingModel.FlatRate, cost.PricingModel);
        Assert.Equal(Price, cost.CostPerExecution);
        Assert.Null(cost.BaseCost);
        Assert.Null(cost.CostPerResult);
        Assert.Null(cost.CostPerToken);
        Assert.Null(cost.CostPerMinute);
        Assert.Null(cost.TieredPricing);
        Assert.Null(cost.PricingConfiguration);
        Assert.True(cost.IsActive);
        Assert.Equal(1, cost.Priority);
        Assert.Null(cost.ExpiryDate);
        Assert.Equal(cost.CreatedAt.AddDays(-1), cost.EffectiveDate);
        var mapping = await context.Set<FunctionCostMapping>().AsNoTracking()
            .SingleAsync(value => value.Id == snapshot.MappingId, database.Token);
        Assert.Equal(snapshot.ConfigurationId, mapping.FunctionConfigurationId);
        Assert.Equal(snapshot.CostId, mapping.FunctionCostId);
        Assert.True(mapping.IsActive);
        Assert.Equal(new object[] { snapshot.ConfigurationId, snapshot.CostId }, await database.RowAsync(
            "SELECT \"FunctionConfigurationId\", \"FunctionCostId\" FROM \"FunctionCostMappings\" WHERE \"Id\" = @id", ("id", snapshot.MappingId)));
        var ip = await context.Set<IpFilterEntity>().AsNoTracking().SingleAsync(value => value.VirtualKeyId == snapshot.KeyId, database.Token);
        Assert.True(ip.Id > 0);
        Assert.Equal("whitelist", ip.FilterType);
        Assert.Equal("127.0.0.1", ip.IpAddressOrCidr);
        Assert.Equal(stem + ":ip", ip.Name);
        Assert.Equal(description, ip.Description);
        Assert.True(ip.IsEnabled);
        Assert.Equal(stem, ip.CreatedBy);
        Assert.Equal(stem, ip.UpdatedBy);
        Assert.All(new[] { group.CreatedAt, key.CreatedAt, configuration.CreatedAt, credential.CreatedAt,
            cost.CreatedAt, mapping.CreatedAt, ip.CreatedAt }, value =>
        {
            Assert.Equal(DateTimeKind.Utc, value.Kind);
            Assert.Equal(group.CreatedAt, value);
            Assert.True(value <= DateTime.UtcNow);
        });
        Assert.All(new[] { group.UpdatedAt, key.UpdatedAt, configuration.UpdatedAt, credential.UpdatedAt,
            cost.UpdatedAt, ip.UpdatedAt }, value => Assert.Equal(group.CreatedAt, value));
    }

    private static void AssertJson(string expectedJson, string? actualJson)
    {
        using var expected = JsonDocument.Parse(expectedJson);
        using var actual = JsonDocument.Parse(Assert.IsType<string>(actualJson));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement), "Persisted JSONB differs from the independent profile contract.");
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
        Assert.Equal(new object[] { snapshot.ConfigurationId, snapshot.CostId, true },
            await database.RowAsync("""
                SELECT "FunctionConfigurationId", "FunctionCostId", "IsActive"
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
        public NpgsqlDataSource EfSource { get; }
        public CancellationToken Token => _deadline.Token;

        private OwnedDatabase(string management, string schema)
        {
            _management = management;
            _schema = schema;
            _connectionString = new NpgsqlConnectionStringBuilder(management)
                { SearchPath = schema, ApplicationName = schema, MaxPoolSize = 1 }.ConnectionString;
            Source = NpgsqlDataSource.Create(_connectionString);
            try { EfSource = NpgsqlDataSource.Create(_connectionString); }
            catch (Exception primary)
            {
                try { Source.Dispose(); }
                catch (Exception cleanup)
                {
                    throw new AggregateException("Owned function data source initialization and cleanup failed.", primary, cleanup);
                }
                finally { _deadline.Dispose(); }
                throw;
            }
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
            .UseNpgsql(EfSource).Options);

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
            var failures = new List<Exception>();
            try { await EfSource.DisposeAsync(); }
            catch (Exception exception) { failures.Add(exception); }
            finally
            {
                try { await Source.DisposeAsync(); }
                catch (Exception exception) { failures.Add(exception); }
                finally
                {
                    try
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
                    catch (Exception exception) { failures.Add(exception); }
                }
            }
            if (failures.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1)
                throw new AggregateException("Owned function fixture cleanup failed.", failures);
        }
    }
}
