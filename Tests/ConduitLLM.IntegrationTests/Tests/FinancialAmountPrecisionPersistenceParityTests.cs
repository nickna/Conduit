using AwesomeAssertions;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Functions.Entities;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;
using Npgsql;
using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

/// <summary>Verifies financial precision on PostgreSQL with the production EF schema.</summary>
[Collection("Postgres advisory locks")]
[Trait("Category", "Integration")]
[Trait("Component", "BillingPersistence")]
public sealed class FinancialAmountPrecisionPersistenceParityTests(PostgresLockTestContainerFixture fixture)
{
    private const string PreviousMigration = "20261006232832_AddWebhookReplayAudit";
    private const string PrecisionMigration = "20261011192146_PreserveEightDecimalFinancialAmounts";
    private static readonly DateTime BilledAt = new(2026, 10, 11, 10, 5, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupedFlush_EightDecimalCharges_ReconcilesExecutionUsageAndLedger(bool typedNpgsql)
    {
        var schema = $"financial_precision_{Guid.NewGuid():N}";
        var factory = new TestDbContextFactory(WithSearchPath(schema));
        try
        {
            await CreateSchemaAsync(schema, factory);
            var ids = await SeedAsync(factory);
            await using var dataSource = NpgsqlDataSource.Create(WithSearchPath(schema));
            IVirtualKeyRuntimeStore runtimeStore = typedNpgsql
                ? new NpgsqlVirtualKeyRuntimeStore(dataSource)
                : new EfVirtualKeyRuntimeStore(factory);
            IRequestLogRuntimeStore requestLogs = typedNpgsql
                ? new NpgsqlRequestLogRuntimeStore(dataSource)
                : new EfRequestLogRuntimeStore(factory);

            var services = new ServiceCollection();
            services.AddSingleton<IDbContextFactory<ConduitDbContext>>(factory);
            services.AddScoped<IConfigurationDbContext>(_ => factory.CreateDbContext());
            services.AddScoped<IVirtualKeyGroupRepository>(_ => new VirtualKeyGroupRepository(
                factory, NullLogger<VirtualKeyGroupRepository>.Instance));
            using var provider = services.BuildServiceProvider();
            using var redisFactory = new RedisConnectionFactory(
                Options.Create(new CacheOptions { RedisConnectionString = fixture.RedisConnectionString }),
                NullLogger<RedisConnectionFactory>.Instance);
            // A long timer interval leaves this test in control of the grouped flush.
            using var batch = new BatchSpendUpdateService(
                provider.GetRequiredService<IServiceScopeFactory>(), redisFactory,
                Options.Create(new BatchSpendingOptions { FlushIntervalSeconds = 3600 }),
                NullLogger<BatchSpendUpdateService>.Instance,
                Mock.Of<IBillingAlertingService>(),
                runtimeStore: typedNpgsql ? runtimeStore : null);

            decimal[] charges = [0.00000001m, 0.00000049m, 0.00000050m, 0.12500001m, 0.12500051m];
            for (var index = 0; index < charges.Length; index++)
            {
                var keyId = index % 2 == 0 ? ids.KeyId : ids.SecondKeyId;
                await WriteEvidenceAsync(factory, requestLogs, ids.FunctionId, keyId, charges[index]);
                await batch.QueueSpendUpdateAsync(keyId, charges[index], BilledAt);
            }

            var total = charges.Sum();
            (await batch.GetPendingSpendAsync(ids.KeyId)).Should().Be(total);
            (await batch.FlushPendingUpdatesAsync()).Should().Be(1);
            (await batch.FlushPendingUpdatesAsync()).Should().Be(0);
            (await batch.GetPendingSpendAsync(ids.KeyId)).Should().Be(0m);

            await using var context = factory.CreateDbContext();
            var group = await context.VirtualKeyGroups.SingleAsync();
            var ledger = await context.VirtualKeyGroupTransactions.SingleAsync();
            group.Balance.Should().Be(100m - total);
            group.LifetimeSpent.Should().Be(total);
            ledger.Amount.Should().Be(total);
            ledger.BalanceAfter.Should().Be(group.Balance);
            ledger.BillingWindowStartUtc.Should().Be(new DateTime(2026, 10, 11, 10, 0, 0, DateTimeKind.Utc));
            (await context.RequestLogs.Select(row => row.Cost).ToListAsync()).Should()
                .BeEquivalentTo(charges);
            (await context.VirtualKeySpendHistory.Select(row => row.Amount).ToListAsync()).Should()
                .BeEquivalentTo(charges);
            (await context.FunctionExecutions.Select(row => row.ActualCost!.Value).ToListAsync()).Should()
                .BeEquivalentTo(charges);
            (await context.BillingAuditEvents.Select(row => row.CalculatedCost!.Value).ToListAsync()).Should()
                .BeEquivalentTo(charges);
            (await context.BillingAuditEvents.Select(row => row.ToolUsageCost!.Value).ToListAsync()).Should()
                .BeEquivalentTo(charges);
            (await context.PricingAuditEvents.Select(row => row.CalculatedCost).ToListAsync()).Should()
                .BeEquivalentTo(charges);

            // Replaying a sub-microdollar debit must compare the exact persisted amount.
            var tinyDebit = new VirtualKeyBalanceAdjustment(ids.GroupId, -0.00000001m,
                "precision replay", "Test", VirtualKeyBalanceReferenceType.VirtualKey,
                ids.KeyId.ToString(), "precision:tiny-debit");
            (await runtimeStore.AdjustBalanceAsync(tinyDebit)).Applied.Should().BeTrue();
            (await runtimeStore.AdjustBalanceAsync(tinyDebit)).Applied.Should().BeFalse();
        }
        finally
        {
            await DropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task Migration_PreviouslyRoundedRows_PreservesStoredValuesAndIntegralRange()
    {
        var schema = $"financial_upgrade_{Guid.NewGuid():N}";
        var factory = new TestDbContextFactory(WithSearchPath(schema));
        try
        {
            await CreateSchemaAsync(schema, factory, oldPrecision: true);
            var ids = await SeedAsync(factory);
            await WriteEvidenceAsync(factory, new EfRequestLogRuntimeStore(factory),
                ids.FunctionId, ids.KeyId, 0.12500001m);
            await new EfVirtualKeyRuntimeStore(factory).AdjustBalanceAsync(new(
                ids.GroupId, -0.12500001m, "historical", "Test",
                VirtualKeyBalanceReferenceType.VirtualKey, ids.KeyId.ToString()));

            await using var context = factory.CreateDbContext();
            var migrationScript = context.GetService<IMigrator>().GenerateScript(
                PreviousMigration, PrecisionMigration, MigrationsSqlGenerationOptions.NoTransactions);
            // GenerateScript assumes the previous history table exists. This fixture uses
            // EF's production create script, so create that bookkeeping table explicitly.
            await ExecuteScriptAsync(context, """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
                """);
            await ExecuteScriptAsync(context, migrationScript);

            (await context.RequestLogs.SingleAsync()).Cost.Should().Be(0.125000m);
            (await context.VirtualKeySpendHistory.SingleAsync()).Amount.Should().Be(0.125000m);
            var historicalAudit = await context.BillingAuditEvents.SingleAsync();
            historicalAudit.CalculatedCost.Should().Be(0.125000m);
            historicalAudit.ToolUsageCost.Should().Be(0.125000m);
            (await context.PricingAuditEvents.SingleAsync()).CalculatedCost.Should().Be(0.125000m);
            var historicalLedger = await context.VirtualKeyGroupTransactions.SingleAsync();
            historicalLedger.Amount.Should().Be(0.125000m);
            historicalLedger.BalanceAfter.Should().Be(99.875000m);
            (await context.FunctionExecutions.SingleAsync()).ActualCost.Should().Be(0.12500001m);
            (await context.VirtualKeyGroups.SingleAsync()).Balance.Should().Be(99.87499999m);

            await WriteEvidenceAsync(factory, new EfRequestLogRuntimeStore(factory),
                ids.FunctionId, ids.KeyId, 9999.99999999m);
            context.VirtualKeyGroupTransactions.Add(new VirtualKeyGroupTransaction
            {
                VirtualKeyGroupId = ids.GroupId,
                Amount = 999999999999.99999999m,
                BalanceAfter = -999999999999.99999999m,
                InitiatedBy = "Test"
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            (await context.RequestLogs.MaxAsync(row => row.Cost)).Should().Be(9999.99999999m);
            (await context.VirtualKeySpendHistory.MaxAsync(row => row.Amount)).Should().Be(9999.99999999m);
            (await context.VirtualKeyGroupTransactions.MaxAsync(row => row.Amount)).Should()
                .Be(999999999999.99999999m);
            (await context.VirtualKeyGroupTransactions.MinAsync(row => row.BalanceAfter)).Should()
                .Be(-999999999999.99999999m);
            (await context.BillingAuditEvents.MaxAsync(row => row.CalculatedCost)).Should().Be(9999.99999999m);
            (await context.PricingAuditEvents.MaxAsync(row => row.CalculatedCost)).Should().Be(9999.99999999m);
        }
        finally
        {
            await DropSchemaAsync(schema);
        }
    }

    private static async Task WriteEvidenceAsync(TestDbContextFactory factory,
        IRequestLogRuntimeStore logs, int functionId, int keyId, decimal amount)
    {
        await logs.WriteAsync(new RequestLogRuntimeRecord
        {
            VirtualKeyId = keyId, ModelName = "financial-precision", RequestType = "function",
            Cost = amount, Timestamp = BilledAt, BilledAtUtc = BilledAt
        });
        await using var context = factory.CreateDbContext();
        context.FunctionExecutions.Add(new FunctionExecution
        {
            FunctionConfigurationId = functionId, VirtualKeyId = keyId,
            ActualCost = amount, EstimatedCost = amount
        });
        context.VirtualKeySpendHistory.Add(new VirtualKeySpendHistory
        {
            VirtualKeyId = keyId, Amount = amount, Date = BilledAt, Timestamp = BilledAt
        });
        context.BillingAuditEvents.Add(new BillingAuditEvent
        {
            VirtualKeyId = keyId, CalculatedCost = amount, ToolUsageCost = amount
        });
        context.PricingAuditEvents.Add(new PricingAuditEvent
        {
            VirtualKeyId = keyId, ModelId = "financial-precision", PricingType = "per_unit",
            AppliedRate = 0.00000001m, Quantity = 1, CalculatedCost = amount
        });
        await context.SaveChangesAsync();
    }

    private static async Task<SeedIds> SeedAsync(TestDbContextFactory factory)
    {
        await using var context = factory.CreateDbContext();
        var group = new VirtualKeyGroup
        {
            Id = Random.Shared.Next(1_000_000, 1_000_000_000), GroupName = "financial precision",
            Balance = 100m, LifetimeCreditsAdded = 100m
        };
        var firstKey = new VirtualKey
        {
            KeyName = "first", KeyHash = Guid.NewGuid().ToString(), VirtualKeyGroup = group
        };
        var secondKey = new VirtualKey
        {
            KeyName = "second", KeyHash = Guid.NewGuid().ToString(), VirtualKeyGroup = group
        };
        var function = new FunctionConfiguration { ConfigurationName = "precision function" };
        context.AddRange(firstKey, secondKey, function);
        await context.SaveChangesAsync();
        return new SeedIds(group.Id, firstKey.Id, secondKey.Id, function.Id);
    }

    private async Task CreateSchemaAsync(string schema, TestDbContextFactory factory, bool oldPrecision = false)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA \"{schema}\"";
        await command.ExecuteNonQueryAsync();

        await using var context = factory.CreateDbContext();
        var script = context.Database.GenerateCreateScript();
        if (oldPrecision)
        {
            // Reproduce the former production column widths without a hand-maintained
            // test-only schema. The new migration is then run as generated by EF.
            script = script.Replace("numeric(12,8)", "numeric(10,6)")
                .Replace("numeric(20,8)", "numeric(18,6)");
        }
        await ExecuteScriptAsync(context, script);
    }

    private static async Task ExecuteScriptAsync(ConduitDbContext context, string script)
    {
        // EF's raw-SQL formatter interprets the JSON default braces in its own schema
        // script as format placeholders. Execute the generated SQL unchanged.
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropSchemaAsync(string schema)
    {
        // EF and direct connections use a separate string-keyed pool from the
        // disposable data source. Release only this generated schema's pool.
        using (var ownedPool = new NpgsqlConnection(WithSearchPath(schema)))
            NpgsqlConnection.ClearPool(ownedPool);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await command.ExecuteNonQueryAsync();
    }

    private string WithSearchPath(string schema) => new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
    {
        SearchPath = schema
    }.ConnectionString;

    private sealed record SeedIds(int GroupId, int KeyId, int SecondKeyId, int FunctionId);

    private sealed class TestDbContextFactory(string connectionString) : IDbContextFactory<ConduitDbContext>
    {
        public ConduitDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConduitDbContext>()
            .UseNpgsql(connectionString).Options);

        public Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
