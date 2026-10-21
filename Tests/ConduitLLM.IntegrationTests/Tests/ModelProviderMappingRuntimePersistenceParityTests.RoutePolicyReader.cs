using AwesomeAssertions;
using ConduitLLM.Configuration.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed partial class ModelProviderMappingRuntimePersistenceParityTests
{
    [Fact]
    public async Task GetByAliasAsync_PersistedDisabledMissingAndCanceled_PreservesDetachedPolicyAndCancellation()
    {
        var schema = $"named_route_policy_{Guid.NewGuid():N}";
        try
        {
            await CreateAndSeedSchemaAsync(schema);
            await using var source = NpgsqlDataSource.Create(WithSearchPath(schema));
            var factory = new TestDbContextFactory(source);
            var reader = new EfModelRoutePolicyReader(factory);
            var policy = await reader.GetByAliasAsync("route-model");

            policy.Should().NotBeNull();
            policy!.ModelAlias.Should().Be("route-model");
            policy.Strategy.Should().Be("Balanced");
            policy.CostWeight.Should().Be(0.5m);
            policy.SpeedWeight.Should().Be(0.3m);
            policy.QualityWeight.Should().Be(0.2m);
            policy.CacheAffinityEnabled.Should().BeTrue();
            policy.AffinityTtlSeconds.Should().Be(900);
            policy.MaxAffinityScorePenalty.Should().Be(0.075m);
            policy.IsEnabled.Should().BeTrue();

            // Returned snapshots remain detached and subsequent reads observe persisted updates.
            policy.CostWeight = 9m;
            (await reader.GetByAliasAsync("route-model"))!.CostWeight.Should().Be(0.5m);
            await using (var context = factory.CreateDbContext())
            {
                var persisted = await context.ModelRoutePolicies.SingleAsync();
                persisted.IsEnabled = false;
                await context.SaveChangesAsync();
            }
            var disabled = await reader.GetByAliasAsync("route-model");
            disabled.Should().NotBeNull();
            disabled!.Id.Should().Be(policy.Id);
            disabled.IsEnabled.Should().BeFalse();
            disabled.CreatedAt.Should().Be(policy.CreatedAt);
            (await reader.GetByAliasAsync("absent-model")).Should().BeNull();
            (await reader.GetByAliasAsync("ROUTE-MODEL")).Should().BeNull();

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => reader.GetByAliasAsync("route-model", cancellation.Token));
            (await reader.GetByAliasAsync("route-model"))!.IsEnabled.Should().BeFalse();
        }
        finally { await DropSchemaAsync(schema); }
    }
}
