using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Data;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.SchemaStartupFixtures;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Npgsql;

namespace ConduitLLM.Tests.Configuration.Migrations;

/// <summary>Real PostgreSQL upgrade, rollback, and relationship lifecycle proofs owned by the required migration gate.</summary>
[Collection("MigrationEnvironment")]
[Trait("Component", "ReleaseMigration")]
public sealed class FunctionInverseRelationshipMigrationTests
{
    private const string PreviousMigration = "20261011204857_AddExactOriginalBalanceAdjustmentEvidence";
    private const string Migration = "20261015050045_PairFunctionInverseRelationships";

    [SkippableFact]
    public async Task ExplicitChildForeignKeys_LoadThroughFreshParentCollectionsAndAllowChildDeletion()
    {
        await using var database = await TestDatabase.CreateAsync(Migration);
        var graph = await SeedGraphAsync(database);

        await using (var read = database.CreateContext())
        {
            var cost = await read.Set<FunctionCost>().Include(parent => parent.FunctionMappings)
                .SingleAsync(parent => parent.Id == graph.CostId, database.Token);
            var execution = await read.Set<FunctionExecution>().Include(parent => parent.AuditEvents)
                .SingleAsync(parent => parent.Id == graph.ExecutionId, database.Token);
            var mapping = Assert.Single(cost.FunctionMappings);
            var audit = Assert.Single(execution.AuditEvents);
            Assert.Equal(graph.MappingId, mapping.Id);
            Assert.Equal(graph.AuditId, audit.Id);
            Assert.Same(cost, mapping.FunctionCost);
            Assert.Same(execution, audit.FunctionExecution);
            Assert.Equal(await read.Set<FunctionCostMapping>().CountAsync(child => child.FunctionCostId == graph.CostId, database.Token), cost.FunctionMappings.Count);
            Assert.Equal(await read.Set<FunctionExecutionAudit>().CountAsync(child => child.FunctionExecutionId == graph.ExecutionId, database.Token), execution.AuditEvents.Count);
            read.Remove(mapping);
            read.Remove(audit);
            await read.SaveChangesAsync(database.Token);
        }

        await using var verify = database.CreateContext();
        Assert.Empty((await verify.Set<FunctionCost>().Include(parent => parent.FunctionMappings)
            .SingleAsync(parent => parent.Id == graph.CostId, database.Token)).FunctionMappings);
        Assert.Empty((await verify.Set<FunctionExecution>().Include(parent => parent.AuditEvents)
            .SingleAsync(parent => parent.Id == graph.ExecutionId, database.Token)).AuditEvents);
    }

    [SkippableFact]
    public async Task ParentCollections_CreateCanonicalForeignKeysAndDatabaseCascadesRemoveOnlyDependents()
    {
        await using var database = await TestDatabase.CreateAsync(Migration);
        int configurationId;
        int costId;
        int mappingId;
        Guid executionId;
        Guid auditId;
        await using (var write = database.CreateContext())
        {
            var configuration = NewConfiguration();
            write.Add(configuration);
            await write.SaveChangesAsync(database.Token);
            configurationId = configuration.Id;
            var mapping = new FunctionCostMapping { FunctionConfigurationId = configurationId };
            var audit = NewAudit();
            var cost = NewCost("Collection-created cost");
            cost.FunctionMappings.Add(mapping);
            var execution = NewExecution(configurationId);
            execution.AuditEvents.Add(audit);
            write.AddRange(cost, execution);
            await write.SaveChangesAsync(database.Token);
            Assert.Equal(cost.Id, mapping.FunctionCostId);
            Assert.Equal(execution.Id, audit.FunctionExecutionId);
            Assert.Same(cost, mapping.FunctionCost);
            Assert.Same(execution, audit.FunctionExecution);
            costId = cost.Id;
            mappingId = mapping.Id;
            executionId = execution.Id;
            auditId = audit.Id;
        }

        await using (var read = database.CreateContext())
        {
            Assert.Equal(mappingId, Assert.Single((await read.Set<FunctionCost>().Include(parent => parent.FunctionMappings)
                .SingleAsync(parent => parent.Id == costId, database.Token)).FunctionMappings).Id);
            Assert.Equal(auditId, Assert.Single((await read.Set<FunctionExecution>().Include(parent => parent.AuditEvents)
                .SingleAsync(parent => parent.Id == executionId, database.Token)).AuditEvents).Id);
        }

        // Delete with unloaded children so the relational FK cascade, rather than tracked fixup, is exercised.
        await using (var delete = database.CreateContext())
        {
            delete.Remove(await delete.Set<FunctionCost>().SingleAsync(parent => parent.Id == costId, database.Token));
            delete.Remove(await delete.Set<FunctionExecution>().SingleAsync(parent => parent.Id == executionId, database.Token));
            await delete.SaveChangesAsync(database.Token);
        }
        await using var verify = database.CreateContext();
        Assert.False(await verify.Set<FunctionCostMapping>().AnyAsync(child => child.Id == mappingId, database.Token));
        Assert.False(await verify.Set<FunctionExecutionAudit>().AnyAsync(child => child.Id == auditId, database.Token));
        Assert.True(await verify.Set<FunctionConfiguration>().AnyAsync(parent => parent.Id == configurationId, database.Token));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradeAcceptsNullOrMatchingLegacyRelationshipsWithoutChangingCanonicalRows(bool matching)
    {
        await using var database = await TestDatabase.CreateAsync(PreviousMigration);
        await SeedGraphAsync(database);
        await AssertShadowArtifactsAsync(database, present: true);
        if (matching) await SetMatchingShadowsAsync(database);
        var before = await ReadRowsAsync(database, includeShadows: false);

        await database.MigrateAsync(Migration);

        await AssertShadowArtifactsAsync(database, present: false);
        AssertRowsEqual(before, await ReadRowsAsync(database, includeShadows: false));
        await using var context = database.CreateContext();
        Assert.Contains(Migration, await context.Database.GetAppliedMigrationsAsync(database.Token));
        var mapping = await context.Set<FunctionCostMapping>().SingleAsync(database.Token);
        Assert.Equal(mapping.Id, Assert.Single((await context.Set<FunctionCost>().AsNoTracking()
            .Include(parent => parent.FunctionMappings)
            .SingleAsync(parent => parent.Id == mapping.FunctionCostId, database.Token)).FunctionMappings).Id);
        var audit = await context.Set<FunctionExecutionAudit>().SingleAsync(database.Token);
        Assert.Equal(audit.Id, Assert.Single((await context.Set<FunctionExecution>().AsNoTracking()
            .Include(parent => parent.AuditEvents)
            .SingleAsync(parent => parent.Id == audit.FunctionExecutionId, database.Token)).AuditEvents).Id);
    }

    [SkippableTheory]
    [InlineData("FunctionCostId1")]
    [InlineData("FunctionExecutionId1")]
    public async Task ConflictingLegacyRelationshipRollsBackAllDdlAndCanRetryAfterExplicitRepair(string shadow)
    {
        await using var database = await TestDatabase.CreateAsync(PreviousMigration);
        var graph = await SeedGraphAsync(database);
        await SetMatchingShadowsAsync(database);
        if (shadow == "FunctionCostId1")
            await ExecuteAsync(database, "UPDATE \"FunctionCostMappings\" SET \"FunctionCostId1\" = @foreign", graph.OtherCostId);
        else
            await ExecuteAsync(database, "UPDATE \"FunctionExecutionAudits\" SET \"FunctionExecutionId1\" = @foreign", graph.OtherExecutionId);
        var before = await ReadRowsAsync(database, includeShadows: true);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => database.MigrateAsync(Migration));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Contains(shadow, exception.MessageText);
        await AssertShadowArtifactsAsync(database, present: true);
        AssertRowsEqual(before, await ReadRowsAsync(database, includeShadows: true));
        await using (var check = database.CreateContext())
        {
            var applied = await check.Database.GetAppliedMigrationsAsync(database.Token);
            Assert.Contains(PreviousMigration, applied);
            Assert.DoesNotContain(Migration, applied);
        }

        // The operator's explicit choice retains the intended child FK and permits a safe retry.
        await SetMatchingShadowsAsync(database);
        var repaired = await ReadRowsAsync(database, includeShadows: false);
        await database.MigrateAsync(Migration);
        await AssertShadowArtifactsAsync(database, present: false);
        AssertRowsEqual(repaired, await ReadRowsAsync(database, includeShadows: false));
    }

    [SkippableFact]
    public async Task DownThenUpRestoresUsableLegacyRelationshipsAndPreservesAllChildPayloads()
    {
        await using var database = await TestDatabase.CreateAsync(Migration);
        await SeedGraphAsync(database);
        var before = await ReadRowsAsync(database, includeShadows: false);

        await database.MigrateAsync(PreviousMigration);

        await AssertShadowArtifactsAsync(database, present: true);
        Assert.Equal(0L, await ScalarAsync(database, """
            SELECT (SELECT COUNT(*) FROM "FunctionCostMappings" WHERE "FunctionCostId1" IS DISTINCT FROM "FunctionCostId")
                + (SELECT COUNT(*) FROM "FunctionExecutionAudits" WHERE "FunctionExecutionId1" IS DISTINCT FROM "FunctionExecutionId")
            """));
        AssertRowsEqual(before, await ReadRowsAsync(database, includeShadows: false));

        await database.MigrateAsync(Migration);

        await AssertShadowArtifactsAsync(database, present: false);
        AssertRowsEqual(before, await ReadRowsAsync(database, includeShadows: false));
    }

    [SkippableFact]
    public async Task GeneratedIdempotentDeploymentScriptAppliesAndReplaysTwiceWithoutChangingRowsOrHistory()
    {
        await using var database = await TestDatabase.CreateAsync(PreviousMigration);
        await SeedGraphAsync(database);
        await SetMatchingShadowsAsync(database);
        var before = await ReadRowsAsync(database, includeShadows: false);
        var expectedHistory = (await ReadHistoryAsync(database)).Append(Migration).ToArray();
        var script = GenerateIdempotentScript(database);

        for (var invocation = 0; invocation < 3; invocation++)
        {
            // Send the exact complete deployment artifact, including its transaction
            // and PostgreSQL DO blocks, rather than executing individual operations.
            await ExecuteAsync(database, script);
            await AssertShadowArtifactsAsync(database, present: false);
            AssertRowsEqual(before, await ReadRowsAsync(database, includeShadows: false));
            Assert.Equal(expectedHistory, await ReadHistoryAsync(database));
        }
    }

    [SkippableTheory]
    [InlineData("FunctionCostId1")]
    [InlineData("FunctionExecutionId1")]
    public async Task GeneratedIdempotentScriptRollsBackConflictsAndCanApplyAndReplayAfterRepair(string shadow)
    {
        await using var database = await TestDatabase.CreateAsync(PreviousMigration);
        var graph = await SeedGraphAsync(database);
        await SetMatchingShadowsAsync(database);
        if (shadow == "FunctionCostId1")
            await ExecuteAsync(database, "UPDATE \"FunctionCostMappings\" SET \"FunctionCostId1\" = @foreign", graph.OtherCostId);
        else
            await ExecuteAsync(database, "UPDATE \"FunctionExecutionAudits\" SET \"FunctionExecutionId1\" = @foreign", graph.OtherExecutionId);
        var before = await ReadRowsAsync(database, includeShadows: true);
        var previousHistory = await ReadHistoryAsync(database);
        var script = GenerateIdempotentScript(database);

        // ExecuteAsync owns an unpooled connection. Disposal rolls back the script's
        // failed transaction before independent connections inspect persisted state.
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, script));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Contains(shadow, exception.MessageText);
        await AssertShadowArtifactsAsync(database, present: true);
        AssertRowsEqual(before, await ReadRowsAsync(database, includeShadows: true));
        Assert.Equal(previousHistory, await ReadHistoryAsync(database));

        await SetMatchingShadowsAsync(database);
        var repaired = await ReadRowsAsync(database, includeShadows: false);
        var expectedHistory = previousHistory.Append(Migration).ToArray();
        for (var invocation = 0; invocation < 3; invocation++)
        {
            await ExecuteAsync(database, script);
            await AssertShadowArtifactsAsync(database, present: false);
            AssertRowsEqual(repaired, await ReadRowsAsync(database, includeShadows: false));
            Assert.Equal(expectedHistory, await ReadHistoryAsync(database));
        }
    }

    private static string GenerateIdempotentScript(TestDatabase database)
    {
        using var context = database.CreateContext();
        return context.GetService<IMigrator>().GenerateScript(PreviousMigration, Migration,
            MigrationsSqlGenerationOptions.Idempotent);
    }

    private static async Task<string[]> ReadHistoryAsync(TestDatabase database)
    {
        await using var context = database.CreateContext();
        return (await context.Database.GetAppliedMigrationsAsync(database.Token)).ToArray();
    }

    private static async Task<Graph> SeedGraphAsync(TestDatabase database)
    {
        await using var write = database.CreateContext();
        var configuration = NewConfiguration();
        var cost = NewCost("Canonical cost 雪");
        var otherCost = NewCost("Other legacy target");
        write.AddRange(configuration, cost, otherCost);
        await write.SaveChangesAsync(database.Token);
        var execution = NewExecution(configuration.Id);
        var otherExecution = NewExecution(configuration.Id);
        write.AddRange(execution, otherExecution);
        await write.SaveChangesAsync(database.Token);
        var mapping = new FunctionCostMapping { FunctionConfigurationId = configuration.Id, FunctionCostId = cost.Id };
        var audit = NewAudit();
        audit.FunctionExecutionId = execution.Id;
        write.AddRange(mapping, audit);
        await write.SaveChangesAsync(database.Token);
        return new(configuration.Id, cost.Id, otherCost.Id, mapping.Id, execution.Id, otherExecution.Id, audit.Id);
    }

    private static FunctionConfiguration NewConfiguration() => new()
    {
        ConfigurationName = "Inverse relationship fixture",
        ProviderType = FunctionProviderType.Exa,
        Purpose = FunctionPurpose.Search
    };

    private static FunctionCost NewCost(string name) => new()
    {
        CostName = name,
        ProviderType = FunctionProviderType.Exa,
        PricingModel = FunctionPricingModel.FlatRate,
        CostPerExecution = 0.00000001m
    };

    private static FunctionExecution NewExecution(int configurationId) => new()
    {
        FunctionConfigurationId = configurationId,
        VirtualKeyId = 7,
        ExecutionMode = ExecutionMode.Synchronous,
        State = ExecutionState.Completed,
        RequestJson = """{"query":"O'Hare 雪"}""",
        ResponseJson = """{"results":[0,false,null]}""",
        ActualCost = 0.12345678m
    };

    private static FunctionExecutionAudit NewAudit() => new()
    {
        EventType = FunctionAuditEventType.CostCalculated,
        EventDetails = """{"quoted":"O'Hare 雪","zero":0,"flag":false,"empty":null}""",
        VirtualKeyId = 7,
        Cost = 0.12345678m,
        IsEstimated = false,
        FailureReason = "Retained audit payload"
    };

    private static Task SetMatchingShadowsAsync(TestDatabase database) => ExecuteAsync(database, """
        UPDATE "FunctionCostMappings" SET "FunctionCostId1" = "FunctionCostId";
        UPDATE "FunctionExecutionAudits" SET "FunctionExecutionId1" = "FunctionExecutionId";
        """);

    private static async Task ExecuteAsync(TestDatabase database, string sql, object foreign = null)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(database.Token);
        await using var command = new NpgsqlCommand(sql, connection);
        if (foreign is not null) command.Parameters.AddWithValue("foreign", foreign);
        await command.ExecuteNonQueryAsync(database.Token);
    }

    private static async Task<long> ScalarAsync(TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(database.Token);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(database.Token))!;
    }

    private static async Task AssertShadowArtifactsAsync(TestDatabase database, bool present)
    {
        Assert.Equal(present ? 2L : 0L, await ScalarAsync(database, """
            SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public'
                AND ((table_name = 'FunctionCostMappings' AND column_name = 'FunctionCostId1')
                    OR (table_name = 'FunctionExecutionAudits' AND column_name = 'FunctionExecutionId1'))
            """));
        Assert.Equal(present ? 2L : 0L, await ScalarAsync(database, """
            SELECT COUNT(*) FROM pg_indexes WHERE schemaname = 'public'
                AND indexname IN ('IX_FunctionCostMappings_FunctionCostId1', 'IX_FunctionExecutionAudits_FunctionExecutionId1')
            """));
        Assert.Equal(present ? 2L : 0L, await ScalarAsync(database, """
            SELECT COUNT(*) FROM pg_constraint constraint_info
            JOIN pg_namespace schema_info ON schema_info.oid = constraint_info.connamespace
            WHERE schema_info.nspname = 'public' AND constraint_info.conname IN
                ('FK_FunctionCostMappings_FunctionCosts_FunctionCostId1', 'FK_FunctionExecutionAudits_FunctionExecutions_FunctionExecuti~1')
            """));
    }

    private static async Task<Rows> ReadRowsAsync(TestDatabase database, bool includeShadows)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(database.Token);
        async Task<string[]> ReadAsync(string table, string shadow)
        {
            // Table/column identifiers are fixed test constants, never fixture input.
            var projection = includeShadows ? "to_jsonb(child)" : $"to_jsonb(child) - '{shadow}'";
            await using var command = new NpgsqlCommand($"SELECT ({projection})::text FROM \"{table}\" child ORDER BY \"Id\"", connection);
            await using var reader = await command.ExecuteReaderAsync(database.Token);
            var rows = new List<string>();
            while (await reader.ReadAsync(database.Token)) rows.Add(reader.GetString(0));
            return rows.ToArray();
        }
        return new(await ReadAsync("FunctionCostMappings", "FunctionCostId1"),
            await ReadAsync("FunctionExecutionAudits", "FunctionExecutionId1"));
    }

    private static void AssertRowsEqual(Rows expected, Rows actual)
    {
        Assert.Equal(expected.Mappings, actual.Mappings);
        Assert.Equal(expected.Audits, actual.Audits);
    }

    private sealed record Graph(int ConfigurationId, int CostId, int OtherCostId, int MappingId,
        Guid ExecutionId, Guid OtherExecutionId, Guid AuditId);
    private sealed record Rows(string[] Mappings, string[] Audits);

    private sealed class TestDatabase(string management, SchemaStartupDatabase fixture) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromMinutes(2));
        public string ConnectionString => fixture.ConnectionString;
        public CancellationToken Token => _deadline.Token;
        public ConduitDbContext CreateContext() => new(new DbContextOptionsBuilder<ConduitDbContext>()
            .UseNpgsql(ConnectionString).Options);

        public async Task MigrateAsync(string target)
        {
            await using var context = CreateContext();
            await context.GetService<IMigrator>().MigrateAsync(target, Token);
        }

        public static async Task<TestDatabase> CreateAsync(string target)
        {
            var configured = Environment.GetEnvironmentVariable("DATABASE_URL");
            if (string.IsNullOrWhiteSpace(configured) &&
                (Environment.GetEnvironmentVariable("CI") == "true" ||
                 Environment.GetEnvironmentVariable("CONDUIT_CI_REQUIRED_INFRASTRUCTURE") == "true"))
                throw new InvalidOperationException("Required CI migration database DATABASE_URL is missing.");
            Skip.If(string.IsNullOrWhiteSpace(configured), "DATABASE_URL is required for real PostgreSQL migration tests.");
            var management = ConfigurationDbContextFactory.ResolveNpgsqlConnectionString();
            using var creation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var fixture = await SchemaStartupDatabase.CreateAsync(management, previousSchema: false, creation.Token);
            var database = new TestDatabase(management, fixture);
            try
            {
                await database.MigrateAsync(target);
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
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await SchemaStartupDatabase.DropAsync(management, fixture.Name, cleanup.Token); }
            finally { _deadline.Dispose(); }
        }
    }
}
