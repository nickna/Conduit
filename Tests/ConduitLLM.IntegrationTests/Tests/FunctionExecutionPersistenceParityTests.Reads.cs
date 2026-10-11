using System.Text.Json.Nodes;

using AwesomeAssertions;

using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed partial class FunctionExecutionPersistenceParityTests
{
    [Fact]
    public async Task ExecutionReadsPreserveEveryScalarAndLegacyIncludedGraph()
    {
        await InSchemaAsync(async (connectionString, source) =>
        {
            var legacy = new FunctionExecutionRepository(new ContextFactory(connectionString),
                NullLogger<FunctionExecutionRepository>.Instance);
            var rows = new[] { Snapshot(true), Snapshot(false) };
            rows[1].FunctionConfigurationId = 2;
            foreach (var row in rows) await legacy.CreateAsync(row);
            foreach (var reader in Readers(connectionString, source))
            {
                (await reader.GetByIdAsync(Guid.Empty)).Should().BeNull();
                (await reader.GetByIdAsync(Guid.NewGuid())).Should().BeNull();
                foreach (var row in rows)
                {
                    var expected = (await legacy.GetByIdAsync(row.Id))!;
                    AssertIncludedGraph(expected); // Pin actual EF inverse fixup, not just the new adapters.
                    var actual = (await reader.GetByIdAsync(row.Id))!;
                    AssertReadEquivalent(actual, expected);
                    actual.RequestedAt.Kind.Should().Be(DateTimeKind.Utc);
                    actual.FunctionConfiguration!.IsEnabled.Should().Be(row.FunctionConfigurationId == 1);
                }
                var detached = (await reader.GetByIdAsync(rows[0].Id))!;
                detached.ErrorMessage = "detached execution";
                detached.FunctionConfiguration!.ConfigurationName = "detached configuration";
                detached.FunctionConfiguration.Executions.Clear();
                var fresh = (await reader.GetByIdAsync(rows[0].Id))!;
                fresh.Should().NotBeSameAs(detached);
                fresh.FunctionConfiguration.Should().NotBeSameAs(detached.FunctionConfiguration);
                AssertReadEquivalent(fresh, (await legacy.GetByIdAsync(rows[0].Id))!);
            }
        });
    }

    [Fact]
    public async Task ExecutionListsRetainUnboundedKeyIsolationOrderingAndSeparateConfigurations()
    {
        await InSchemaAsync(async (connectionString, source) =>
        {
            var legacy = new FunctionExecutionRepository(new ContextFactory(connectionString),
                NullLogger<FunctionExecutionRepository>.Instance);
            for (var index = 0; index < 105; index++)
            {
                var row = Snapshot(index % 2 == 0);
                row.VirtualKeyId = -11;
                row.FunctionConfigurationId = index % 2 + 1;
                row.RequestedAt = Instant.AddMilliseconds(index / 2);
                await legacy.CreateAsync(row);
            }
            var other = Snapshot(true);
            other.VirtualKeyId = 0;
            await legacy.CreateAsync(other);
            var expected = await legacy.GetByVirtualKeyIdAsync(-11);
            expected.Should().HaveCount(105);
            foreach (var reader in Readers(connectionString, source))
            {
                (await reader.ListByVirtualKeyAsync(int.MinValue)).Should().BeEmpty();
                (await reader.ListByVirtualKeyAsync(0)).Select(row => row.Id).Should().Equal(other.Id);
                var actual = await reader.ListByVirtualKeyAsync(-11);
                actual.Should().HaveCount(105);
                actual.Select(row => row.RequestedAt).Should().Equal(expected.Select(row => row.RequestedAt));
                actual.Select(row => row.Id).Should().BeEquivalentTo(expected.Select(row => row.Id));
                var byId = expected.ToDictionary(row => row.Id);
                foreach (var row in actual) AssertReadEquivalent(row, byId[row.Id]);
                var sameConfiguration = actual.Where(row => row.FunctionConfigurationId == 1).ToArray();
                for (var index = 1; index < sameConfiguration.Length; index++)
                    sameConfiguration[index].FunctionConfiguration.Should().NotBeSameAs(sameConfiguration[0].FunctionConfiguration);
            }
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecutionReadsCancelPoolAndObservedSqlWaitsAndReuseSlots(int adapter)
    {
        await InSchemaAsync(async (connectionString, source) =>
        {
            var seed = Snapshot(true);
            await Writers(connectionString, source)[0].CreateAsync(seed);
            var reader = Readers(connectionString, source)[adapter];
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.GetByIdAsync(Guid.Empty, cancelled.Token));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ListByVirtualKeyAsync(int.MinValue, cancelled.Token));
            }
            await using (var held = adapter == 2 ? await source.OpenConnectionAsync(deadline.Token) : new NpgsqlConnection(connectionString))
            {
                if (adapter != 2) await held.OpenAsync(deadline.Token);
                foreach (var list in new[] { false, true })
                {
                    using var cancelled = new CancellationTokenSource();
                    Task read = list ? reader.ListByVirtualKeyAsync(42, cancelled.Token) : reader.GetByIdAsync(seed.Id, cancelled.Token);
                    try
                    {
                        read.IsCompleted.Should().BeFalse();
                        cancelled.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
                    }
                    finally { await DrainAsync(read, cancelled); }
                }
            }
            AssertIncludedGraph((await reader.GetByIdAsync(seed.Id, deadline.Token))!);
            await using var blocker = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
                { Pooling = false, ApplicationName = "execution_read_blocker" }.ConnectionString);
            await blocker.OpenAsync(deadline.Token);
            await using var transaction = await blocker.BeginTransactionAsync(deadline.Token);
            await using (var command = blocker.CreateCommand())
            {
                command.CommandText = "LOCK TABLE \"FunctionExecutions\" IN ACCESS EXCLUSIVE MODE";
                await command.ExecuteNonQueryAsync(deadline.Token);
            }
            foreach (var list in new[] { false, true })
            {
                using var cancelled = new CancellationTokenSource();
                Task read = list ? reader.ListByVirtualKeyAsync(42, cancelled.Token) : reader.GetByIdAsync(seed.Id, cancelled.Token);
                try
                {
                    await WaitForLockWaitAsync(connectionString, read, deadline.Token);
                    cancelled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                finally { await DrainAsync(read, cancelled); }
            }
            await transaction.RollbackAsync(deadline.Token);
            (await reader.ListByVirtualKeyAsync(42, deadline.Token)).Should().ContainSingle();
            AssertReadEquivalent((await reader.GetByIdAsync(seed.Id, deadline.Token))!,
                (await Readers(connectionString, source)[0].GetByIdAsync(seed.Id, deadline.Token))!);
        });
    }

    [Fact]
    public async Task ExecutionReaderGraphsRemainOutsideScalarWritersBeforeCallerMutation()
    {
        await InSchemaAsync(async (connectionString, source) =>
        {
            var seed = Snapshot(true);
            await Writers(connectionString, source)[0].CreateAsync(seed);
            foreach (var reader in Readers(connectionString, source))
            foreach (var writer in Writers(connectionString, source).Skip(1))
            {
                var graph = (await reader.GetByIdAsync(seed.Id))!;
                graph.Id = Guid.Empty;
                await Assert.ThrowsAsync<ArgumentException>(() => writer.CreateAsync(graph));
                graph.Id.Should().BeEmpty();
                graph.Id = seed.Id;
                await Assert.ThrowsAsync<ArgumentException>(() => writer.UpdateAsync(graph));
                graph.Id.Should().Be(seed.Id);
                graph.Version.Should().Be(seed.Version);
                await AssertRowAsync(connectionString, seed);
            }
        });
    }

    private static IFunctionExecutionRuntimeReader[] Readers(string connectionString, NpgsqlDataSource source)
    {
        var factory = new ContextFactory(connectionString);
        return [new LegacyReader(new FunctionExecutionRepository(factory, NullLogger<FunctionExecutionRepository>.Instance)),
            new EfFunctionExecutionRuntimeReader(factory), new NpgsqlFunctionExecutionRuntimeReader(source)];
    }

    private static void AssertIncludedGraph(FunctionExecution row)
    {
        row.Should().NotBeNull();
        row.AuditEvents.Should().BeEmpty();
        row.FunctionConfiguration.Should().NotBeNull();
        row.FunctionConfiguration!.CostMappings.Should().BeEmpty();
        row.FunctionConfiguration.Executions.Should().ContainSingle().Which.Should().BeSameAs(row);
    }

    private static void AssertReadEquivalent(FunctionExecution actual, FunctionExecution expected)
    {
        AssertIncludedGraph(actual);
        AssertIncludedGraph(expected);
        actual.Should().BeEquivalentTo(expected, options => options.Excluding(row => row.FunctionConfiguration)
            .Excluding(row => row.AuditEvents).Excluding(row => row.RequestJson).Excluding(row => row.ResponseJson)
            .Excluding(row => row.CostCalculationDetails));
        foreach (var pair in new[] { (actual.RequestJson, expected.RequestJson), (actual.ResponseJson, expected.ResponseJson),
                     (actual.CostCalculationDetails, expected.CostCalculationDetails) })
        {
            (pair.Item1 is null).Should().Be(pair.Item2 is null);
            if (pair.Item1 is not null) JsonNode.DeepEquals(JsonNode.Parse(pair.Item1), JsonNode.Parse(pair.Item2!)).Should().BeTrue();
        }
        actual.FunctionConfiguration.Should().BeEquivalentTo(expected.FunctionConfiguration,
            options => options.Excluding(row => row!.Executions).Excluding(row => row!.CostMappings));
    }

    private sealed class LegacyReader(FunctionExecutionRepository repository) : IFunctionExecutionRuntimeReader
    {
        public Task<FunctionExecution?> GetByIdAsync(Guid id, CancellationToken token = default) => repository.GetByIdAsync(id, token);
        public async Task<IReadOnlyList<FunctionExecution>> ListByVirtualKeyAsync(int key, CancellationToken token = default) =>
            await repository.GetByVirtualKeyIdAsync(key, token);
    }
}
