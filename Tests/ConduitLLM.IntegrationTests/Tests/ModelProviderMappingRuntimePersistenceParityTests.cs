using AwesomeAssertions;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Models;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using System.Text.Json;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

/// <summary>
/// Runs the Gateway model-routing read contract against EF and typed Npgsql.
/// </summary>
[Collection("Postgres advisory locks")]
public sealed class ModelProviderMappingRuntimePersistenceParityTests
{
    private readonly PostgresLockTestContainerFixture _fixture;

    public ModelProviderMappingRuntimePersistenceParityTests(PostgresLockTestContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EfAndTypedNpgsqlImplementTheSameModelRoutingContract()
    {
        var efSchema = $"model_route_ef_{Guid.NewGuid():N}";
        var npgsqlSchema = $"model_route_np_{Guid.NewGuid():N}";

        try
        {
            var efIds = await CreateAndSeedSchemaAsync(efSchema);
            var npgsqlIds = await CreateAndSeedSchemaAsync(npgsqlSchema);

            await ExerciseContractAsync(
                new EfModelProviderMappingRuntimeStore(
                    new TestDbContextFactory(WithSearchPath(efSchema))),
                efIds);

            await using var dataSource = NpgsqlDataSource.Create(WithSearchPath(npgsqlSchema));
            await ExerciseContractAsync(
                new NpgsqlModelProviderMappingRuntimeStore(dataSource),
                npgsqlIds);
        }
        finally
        {
            await DropSchemaAsync(efSchema);
            await DropSchemaAsync(npgsqlSchema);
        }
    }

    private static async Task ExerciseContractAsync(
        IModelProviderMappingRuntimeStore store,
        SeedIds ids)
    {
        (await store.GetByIdAsync(-1)).Should().BeNull();
        (await store.GetByAliasAsync("missing")).Should().BeEmpty();
        (await store.GetCanonicalModelIdForAssociationAsync(-1)).Should().BeNull();
        (await store.GetRoutePolicyAsync("missing")).Should().BeNull();
        (await store.GetModelCostByIdAsync(-1)).Should().BeNull();
        (await store.GetModelCostForIdentifierAsync("missing")).Should().BeNull();

        var routes = await store.GetByAliasAsync("ROUTE-MODEL");
        routes.Should().HaveCount(2);
        routes.Select(route => route.Id).Should().Equal(ids.SecondaryMappingId, ids.PrimaryMappingId);

        var route = routes[0];
        route.ModelAlias.Should().Be("route-model");
        route.ProviderModelId.Should().Be("provider/secondary");
        route.RoutingPriority.Should().Be(10);
        route.RoutingWeight.Should().Be(1.25m);
        route.ProviderOptions.Should().Be("{\"route\":\"fallback\"}");
        route.Provider.ProviderType.Should().Be(ProviderType.OpenRouter);
        route.Provider.Settings.Should().Contain("region", "west");
        route.Provider.TrustProviderReportedCosts.Should().BeTrue();
        route.Provider.ProviderCostMarkupMultiplier.Should().Be(1.125m);

        route.Association.Identifier.Should().Be("provider/secondary");
        route.Association.ProviderType.Should().Be((int)ProviderType.OpenRouter);
        using var inputModalities = JsonDocument.Parse(route.Association.InputModalitiesJson!);
        inputModalities.RootElement.EnumerateArray()
            .Select(element => element.GetString())
            .Should().Equal("text", "image");
        using var operationalCapabilities = JsonDocument.Parse(route.Association.OperationalCapabilitiesJson!);
        operationalCapabilities.RootElement.GetProperty("supports_json_schema").GetBoolean().Should().BeTrue();
        route.Association.QualityScore.Should().Be(0.95m);
        route.Association.SpeedScore.Should().Be(1.5m);
        route.Association.Model.Id.Should().Be(ids.ModelId);
        route.Association.Model.SupportsChat.Should().BeTrue();
        route.Association.Model.SupportsVision.Should().BeTrue();
        route.Association.Model.MaxInputTokens.Should().Be(64_000);
        route.Association.Model.TokenizerType.Should().Be((int)TokenizerType.O200KBase);
        route.Association.Model.Series.Name.Should().Be("Runtime Series");
        using var seriesParameters = JsonDocument.Parse(route.Association.Model.Series.Parameters!);
        seriesParameters.RootElement.TryGetProperty("temperature", out _).Should().BeTrue();
        route.Association.ModelCost.Should().NotBeNull();
        route.Association.ModelCost!.InputCostPerMillionTokens.Should().Be(2.5m);
        route.Association.ModelCost.OutputCostPerMillionTokens.Should().Be(10m);
        route.Association.ModelCost.CachedInputCostPerMillionTokens.Should().Be(0.25m);
        route.Association.ModelCost.AudioCostPerMinute.Should().BeNull();

        var costById = await store.GetModelCostByIdAsync(ids.CostId);
        costById.Should().NotBeNull();
        costById!.InputCostPerMillionTokens.Should().Be(2.5m);
        costById.OutputCostPerMillionTokens.Should().Be(10m);
        (await store.GetModelCostForIdentifierAsync("provider/secondary"))
            .Should().BeEquivalentTo(costById);

        var primary = await store.GetByIdAsync(ids.PrimaryMappingId);
        primary.Should().NotBeNull();
        primary!.Association.ModelCost.Should().BeNull();
        primary.Provider.BaseUrl.Should().BeNull();
        primary.Provider.Settings.Should().BeNull();

        var page = await store.GetPaginatedAsync(1, 1);
        page.TotalCount.Should().Be(2);
        page.Items.Should().ContainSingle();

        var providerPage = await store.GetByProviderPaginatedAsync(route.ProviderId, 1, 10);
        providerPage.TotalCount.Should().Be(1);
        providerPage.Items.Should().ContainSingle().Which.Id.Should().Be(route.Id);

        var byModel = await store.GetByModelIdAsync(ids.ModelId);
        byModel.Should().HaveCount(2);
        (await store.GetCanonicalModelIdForAssociationAsync(ids.SecondaryAssociationId))
            .Should().Be(ids.ModelId);

        var policy = await store.GetRoutePolicyAsync("route-model");
        policy.Should().NotBeNull();
        policy!.CostWeight.Should().Be(0.5m);
        policy.SpeedWeight.Should().Be(0.3m);
        policy.QualityWeight.Should().Be(0.2m);
        policy.CacheAffinityEnabled.Should().BeTrue();
        policy.AffinityTtlSeconds.Should().Be(900);
        policy.MaxAffinityScorePenalty.Should().Be(0.075m);
        policy.IsEnabled.Should().BeTrue();
    }

    private async Task<SeedIds> CreateAndSeedSchemaAsync(string schema)
    {
        await using (var connection = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await command.ExecuteNonQueryAsync();
        }

        var factory = new TestDbContextFactory(WithSearchPath(schema));
        await using var context = await factory.CreateDbContextAsync();
        // EnsureCreated checks every schema in the database and sees Testcontainers'
        // resource-reaper table. Execute EF's deterministic creation script directly so
        // every object is created in this connection's isolated search path.
        await context.Database.OpenConnectionAsync();
        await using (var createCommand = context.Database.GetDbConnection().CreateCommand())
        {
            createCommand.CommandText = context.Database.GenerateCreateScript();
            await createCommand.ExecuteNonQueryAsync();
        }

        var author = new ModelAuthor { Name = "Runtime Author" };
        var series = new ModelSeries
        {
            Author = author,
            Name = "Runtime Series",
            Description = null,
            TokenizerType = TokenizerType.O200KBase,
            Parameters = "{\"temperature\":{}}"
        };
        var model = new Model
        {
            Name = "Runtime Model",
            Version = "v2",
            Description = "runtime description",
            ModelCardUrl = "https://example.invalid/model",
            Series = series,
            SupportsVision = true,
            SupportsChat = true,
            SupportsStreaming = true,
            SupportsFunctionCalling = true,
            InputModalitiesJson = "[\"text\",\"image\"]",
            OutputModalitiesJson = "[\"text\"]",
            CapabilitySource = ModelCapabilitySource.Curated,
            CapabilitiesLastVerifiedAt = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc),
            TokenizerType = TokenizerType.O200KBase,
            MaxInputTokens = 64_000,
            MaxOutputTokens = 8_000,
            ModelParameters = null,
            IsActive = true
        };
        var cost = new ModelCost
        {
            CostName = "Runtime route cost",
            PricingModel = PricingModel.Standard,
            InputCostPerMillionTokens = 2.5m,
            OutputCostPerMillionTokens = 10m,
            EmbeddingCostPerMillionTokens = null,
            CachedInputCostPerMillionTokens = 0.25m,
            CachedInputWriteCostPerMillionTokens = 3m,
            ReasoningCostPerMillionTokens = 12m,
            ModelType = "chat",
            IsActive = true,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Priority = 5
        };
        var primaryAssociation = new ModelProviderTypeAssociation
        {
            Model = model,
            Identifier = "provider/primary",
            Provider = ProviderType.OpenAI,
            IsEnabled = true,
            IsPrimary = true
        };
        var secondaryAssociation = new ModelProviderTypeAssociation
        {
            Model = model,
            Identifier = "provider/secondary",
            Provider = ProviderType.OpenRouter,
            IsEnabled = true,
            IsPrimary = false,
            MaxInputTokens = 32_000,
            InputModalitiesJson = "[\"text\",\"image\"]",
            OutputModalitiesJson = "[\"text\"]",
            OperationalCapabilitiesJson = "{\"supports_json_schema\":true}",
            CapabilitySource = ModelCapabilitySource.ProviderApi,
            CapabilitiesLastVerifiedAt = new DateTime(2026, 8, 27, 13, 0, 0, DateTimeKind.Utc),
            ProviderVariation = "fast",
            QualityScore = 0.95m,
            SpeedScore = 1.5m,
            ModelCost = cost,
            Metadata = "{\"source\":\"parity\"}"
        };
        var primaryProvider = new Provider
        {
            ProviderType = ProviderType.OpenAI,
            ProviderName = "Primary provider",
            IsEnabled = true
        };
        var secondaryProvider = new Provider
        {
            ProviderType = ProviderType.OpenRouter,
            ProviderName = "Secondary provider",
            BaseUrl = "https://example.invalid/openrouter",
            Settings = new Dictionary<string, string> { ["region"] = "west" },
            IsEnabled = true,
            TrustProviderReportedCosts = true,
            ProviderCostMarkupMultiplier = 1.125m
        };
        var primaryMapping = new ModelProviderMapping
        {
            ModelAlias = "route-model",
            ProviderModelId = "provider/primary",
            Provider = primaryProvider,
            ModelProviderTypeAssociation = primaryAssociation,
            RoutingPriority = 20,
            RoutingWeight = 1m,
            IsEnabled = true
        };
        var secondaryMapping = new ModelProviderMapping
        {
            ModelAlias = "route-model",
            ProviderModelId = "provider/secondary",
            Provider = secondaryProvider,
            ModelProviderTypeAssociation = secondaryAssociation,
            ProviderOptions = "{\"route\":\"fallback\"}",
            RoutingPriority = 10,
            RoutingWeight = 1.25m,
            IsEnabled = true
        };
        context.ModelProviderMappings.AddRange(primaryMapping, secondaryMapping);
        context.ModelRoutePolicies.Add(new ModelRoutePolicy
        {
            ModelAlias = "route-model",
            CostWeight = 0.5m,
            SpeedWeight = 0.3m,
            QualityWeight = 0.2m,
            CacheAffinityEnabled = true,
            AffinityTtlSeconds = 900,
            MaxAffinityScorePenalty = 0.075m,
            IsEnabled = true
        });
        await context.SaveChangesAsync();

        return new SeedIds(
            model.Id,
            cost.Id,
            primaryAssociation.Id,
            secondaryAssociation.Id,
            primaryMapping.Id,
            secondaryMapping.Id);
    }

    private async Task DropSchemaAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await command.ExecuteNonQueryAsync();
    }

    private string WithSearchPath(string schema)
    {
        var builder = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            SearchPath = schema
        };
        return builder.ConnectionString;
    }

    private sealed record SeedIds(
        int ModelId,
        int CostId,
        int PrimaryAssociationId,
        int SecondaryAssociationId,
        int PrimaryMappingId,
        int SecondaryMappingId);

    private sealed class TestDbContextFactory(string connectionString) :
        IDbContextFactory<ConduitDbContext>
    {
        private readonly DbContextOptions<ConduitDbContext> _options =
            new DbContextOptionsBuilder<ConduitDbContext>()
                .UseNpgsql(connectionString)
                .Options;

        public ConduitDbContext CreateDbContext() => new(_options);

        public Task<ConduitDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
