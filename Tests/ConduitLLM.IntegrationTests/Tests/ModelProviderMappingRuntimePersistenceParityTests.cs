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
using Xunit.Abstractions;

namespace ConduitLLM.IntegrationTests.Tests;

/// <summary>
/// Runs the Gateway model-routing read contract against EF and typed Npgsql.
/// </summary>
[Collection("Postgres advisory locks")]
public sealed partial class ModelProviderMappingRuntimePersistenceParityTests
{
    private readonly PostgresLockTestContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ModelProviderMappingRuntimePersistenceParityTests(PostgresLockTestContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
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

            await using var efSource = NpgsqlDataSource.Create(WithSearchPath(efSchema));

            await ExerciseContractAsync(
                new EfModelProviderMappingRuntimeStore(
                    new TestDbContextFactory(efSource)),
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeReadsPreserveEveryGraphFieldNullsEnumsUtcAndIndependentProviderSnapshots(bool typedNpgsql)
    {
        var schema = $"mr_snapshot_{Guid.NewGuid():N}";
        var connectionString = WithSearchPath(schema);
        try
        {
            await CreateAndSeedSchemaAsync(schema);
            var matrix = await SeedSnapshotMatrixAsync(schema);
            await using var source = NpgsqlDataSource.Create(connectionString);
            var store = CreateMatrixStore(source, typedNpgsql);

            AssertGraphSnapshot(await store.GetByIdAsync(matrix.Populated.Id), matrix.Populated);
            AssertGraphSnapshot(await store.GetByIdAsync(matrix.Nulls.Id), matrix.Nulls);
            var aliases = await store.GetByAliasAsync("SNAPSHOT-ROUTE");
            aliases.Select(route => route.Id).Should().Equal(new[] { matrix.Populated.Id, matrix.Shared.Id }.Order());
            foreach (var route in aliases)
                AssertGraphSnapshot(route, route.Id == matrix.Populated.Id ? matrix.Populated : matrix.Shared);
            var page = await store.GetPaginatedAsync(1, 100);
            page.TotalCount.Should().Be(5);
            AssertGraphSnapshot(page.Items.Single(route => route.Id == matrix.Populated.Id), matrix.Populated);
            var providerPage = await store.GetByProviderPaginatedAsync(matrix.Populated.ProviderId, 1, 100);
            providerPage.TotalCount.Should().Be(2);
            foreach (var route in providerPage.Items)
                AssertGraphSnapshot(route, route.Id == matrix.Populated.Id ? matrix.Populated : matrix.Shared);
            var byModel = await store.GetByModelIdAsync(matrix.Populated.ModelProviderTypeAssociation.ModelId);
            byModel.Select(route => route.Id).Should().BeEquivalentTo(new[] { matrix.Populated.Id, matrix.Shared.Id });
            foreach (var route in byModel)
                AssertGraphSnapshot(route, route.Id == matrix.Populated.Id ? matrix.Populated : matrix.Shared);
            (await store.GetCanonicalModelIdForAssociationAsync(matrix.Nulls.ModelProviderTypeAssociationId))
                .Should().Be(matrix.Nulls.ModelProviderTypeAssociation.ModelId);
            AssertCostSnapshot(await store.GetModelCostByIdAsync(matrix.Populated.ModelProviderTypeAssociation.ModelCostId!.Value),
                matrix.Populated.ModelProviderTypeAssociation.ModelCost!);
            AssertCostSnapshot(await store.GetModelCostByIdAsync(matrix.NullCost.Id), matrix.NullCost);
            AssertPolicySnapshot(await store.GetRoutePolicyAsync(matrix.Policy.ModelAlias), matrix.Policy);

            aliases[0].Provider.Should().NotBeSameAs(aliases[1].Provider);
            aliases[0].Provider.Settings.Should().NotBeSameAs(aliases[1].Provider.Settings);
            aliases[0].Provider.ProviderName = "mutated returned provider";
            aliases[0].Provider.Settings!["region"] = "mutated returned setting";
            aliases[0].Provider.Settings["new"] = "caller-only";
            AssertGraphSnapshot(aliases[1], aliases[1].Id == matrix.Populated.Id ? matrix.Populated : matrix.Shared);
            AssertGraphSnapshot(await store.GetByIdAsync(matrix.Populated.Id), matrix.Populated);

            (await store.GetByIdAsync(-1)).Should().BeNull();
            (await store.GetByAliasAsync("absent-snapshot")).Should().BeEmpty();
            (await store.GetByModelIdAsync(-1)).Should().BeEmpty();
            (await store.GetByProviderPaginatedAsync(-1, 1, 100)).Should()
                .BeEquivalentTo(new ModelProviderMappingRuntimePage([], 0));
            (await store.GetCanonicalModelIdForAssociationAsync(-1)).Should().BeNull();
            (await store.GetRoutePolicyAsync("absent-snapshot")).Should().BeNull();
            (await store.GetModelCostByIdAsync(-1)).Should().BeNull();
            (await store.GetModelCostForIdentifierAsync("absent-snapshot")).Should().BeNull();
        }
        finally { await DropMatrixSchemaAsync(schema, connectionString); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeReadsPreserveOrderingPaginationDisabledGraphsAndCostSelection(bool typedNpgsql)
    {
        var schema = $"mr_selection_{Guid.NewGuid():N}";
        var connectionString = WithSearchPath(schema);
        try
        {
            var ids = await CreateAndSeedSchemaAsync(schema);
            var matrix = await SeedSelectionMatrixAsync(schema, ids);
            await using var source = NpgsqlDataSource.Create(connectionString);
            var store = CreateMatrixStore(source, typedNpgsql);
            var allIds = matrix.Mappings.Select(mapping => new { Alias = mapping.ModelAlias, mapping.Id })
                .Append(new { Alias = matrix.Mappings[99].ModelAlias, Id = ids.PrimaryMappingId })
                .Append(new { Alias = "route-model", Id = ids.SecondaryMappingId })
                .OrderBy(mapping => mapping.Alias, StringComparer.Ordinal).ThenBy(mapping => mapping.Id)
                .Select(mapping => mapping.Id).ToArray();
            var providerIds = matrix.Mappings.OrderBy(mapping => mapping.ModelAlias, StringComparer.Ordinal)
                .ThenBy(mapping => mapping.Id).Select(mapping => mapping.Id)
                .Append(ids.SecondaryMappingId).ToArray();
            var aliasIds = new[] { ids.PrimaryMappingId, matrix.Mappings[99].Id }.Order();
            (await store.GetByAliasAsync("PAGE-ROUTE-099")).Select(route => route.Id).Should().Equal(aliasIds);
            (await store.GetByAliasAsync(" page-route-099 ")).Should().BeEmpty();
            foreach (var (pageNumber, pageSize, expectedPage, expectedSize) in new[]
                { (-3, 0, 1, 20), (0, -8, 1, 20), (0, 1, 1, 1), (1, 500, 1, 100),
                  (2, 100, 2, 100), (2, 1, 2, 1), (9, 100, 9, 100) })
            {
                var page = await store.GetPaginatedAsync(pageNumber, pageSize);
                page.TotalCount.Should().Be(103);
                page.Items.Select(route => route.Id).Should()
                    .Equal(allIds.Skip((expectedPage - 1) * expectedSize).Take(expectedSize));
                var providerPage = await store.GetByProviderPaginatedAsync(matrix.ProviderId, pageNumber, pageSize);
                providerPage.TotalCount.Should().Be(102);
                providerPage.Items.Select(route => route.Id).Should()
                    .Equal(providerIds.Skip((expectedPage - 1) * expectedSize).Take(expectedSize));
            }
            (await store.GetByProviderPaginatedAsync(-1, 0, 500)).Should()
                .BeEquivalentTo(new ModelProviderMappingRuntimePage([], 0));
            (await store.GetByModelIdAsync(matrix.OtherModelId)).Select(route => route.Id)
                .Should().Equal(ids.PrimaryMappingId);
            (await store.GetByModelIdAsync(ids.ModelId)).Select(route => route.Id)
                .Should().Equal(providerIds);
            (await store.GetByModelIdAsync(-1)).Should().BeEmpty();
            var disabled = (await store.GetByIdAsync(matrix.Mappings[0].Id))!;
            disabled.IsEnabled.Should().BeFalse();
            disabled.Provider.IsEnabled.Should().BeFalse();
            disabled.Association.IsEnabled.Should().BeFalse();
            disabled.Association.Model.IsActive.Should().BeFalse();
            (await store.GetCanonicalModelIdForAssociationAsync(ids.SecondaryAssociationId)).Should().Be(ids.ModelId);
            (await store.GetCanonicalModelIdForAssociationAsync(-1)).Should().BeNull();
            (await store.GetRoutePolicyAsync("route-model"))!.IsEnabled.Should().BeFalse();
            (await store.GetRoutePolicyAsync("ROUTE-MODEL")).Should().BeNull();
            (await store.GetModelCostForIdentifierAsync("COST-CHOICE")).Should().BeNull();
            (await store.GetModelCostForIdentifierAsync("cost-choice"))!.Id.Should().Be(matrix.Winner.Id);
            // Wide time margins avoid asserting that the two adapters sample their clock at the same point.
            await SetCostActiveAsync(schema, matrix.Winner.Id, false);
            (await store.GetModelCostForIdentifierAsync("cost-choice"))!.Id.Should().Be(matrix.Older.Id);
            await SetCostActiveAsync(schema, matrix.Older.Id, false);
            (await store.GetModelCostForIdentifierAsync("cost-choice"))!.Id.Should().Be(matrix.LowerPriority.Id);
            await SetCostActiveAsync(schema, matrix.LowerPriority.Id, false);
            (await store.GetModelCostForIdentifierAsync("cost-choice")).Should().BeNull();
            foreach (var excluded in matrix.ExcludedCosts)
                (await store.GetModelCostByIdAsync(excluded.Id))!.Id.Should().Be(excluded.Id);
            (await store.GetModelCostByIdAsync(matrix.Winner.Id))!.IsActive.Should().BeFalse();
        }
        finally { await DropMatrixSchemaAsync(schema, connectionString); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllNineRuntimeReadsCancelBeforeExecutionInPoolAndInBothPageSqlPhasesThenReuseBothSlots(bool typedNpgsql)
    {
        var schema = $"mr_cancel_{Guid.NewGuid():N}";
        var ownedConnectionStrings = new List<string>();
        try
        {
            var ids = await CreateAndSeedSchemaAsync(schema);
            foreach (var operation in Enum.GetValues<ReadOperation>())
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var applicationName = $"mr_cancel_{Guid.NewGuid():N}_{(int)operation}";
                System.Text.Encoding.UTF8.GetByteCount(applicationName).Should().BeLessThan(64);
                // This wider matrix deliberately exercises a two-slot source;
                // the dedicated single-slot proof covers pagination ownership.
                var connectionString = new NpgsqlConnectionStringBuilder(WithSearchPath(schema))
                    { ApplicationName = applicationName, MaxPoolSize = 2 }.ConnectionString;
                ownedConnectionStrings.Add(connectionString);
                await using var source = NpgsqlDataSource.Create(connectionString);
                var store = CreateMatrixStore(source, typedNpgsql);
                var providerId = (await store.GetByIdAsync(ids.SecondaryMappingId, deadline.Token))!.ProviderId;
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        InvokeReadAsync(store, ids, providerId, operation, cancelled.Token));
                    if (operation == ReadOperation.Alias)
                        foreach (var invalid in new[] { null, "", " \t\r\n" })
                        {
                            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetByAliasAsync(invalid!, cancelled.Token));
                            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetRoutePolicyAsync(invalid!, cancelled.Token));
                            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetModelCostForIdentifierAsync(invalid!, cancelled.Token));
                        }
                }
                await AssertBothPoolSlotsReusableAsync(source, connectionString, typedNpgsql, deadline.Token);
                await AssertReadRecoversAsync(store, ids, providerId, operation, deadline.Token);
                await using (var first = await OpenMatchingPoolConnectionAsync(source, connectionString, typedNpgsql, deadline.Token))
                await using (var second = await OpenMatchingPoolConnectionAsync(source, connectionString, typedNpgsql, deadline.Token))
                {
                    first.ProcessID.Should().NotBe(second.ProcessID);
                    using var cancelled = new CancellationTokenSource();
                    var pending = InvokeReadAsync(store, ids, providerId, operation, cancelled.Token);
                    try
                    {
                        pending.IsCompleted.Should().BeFalse("both acquired connections occupy this store's two-slot pool");
                        cancelled.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                    }
                    finally { await DrainCancelledReadAsync(pending, cancelled); }
                }
                await AssertBothPoolSlotsReusableAsync(source, connectionString, typedNpgsql, deadline.Token);
                await AssertReadRecoversAsync(store, ids, providerId, operation, deadline.Token);
                var table = operation switch
                {
                    ReadOperation.Canonical => "ModelIdentifiers",
                    ReadOperation.Policy => "ModelRoutePolicies",
                    ReadOperation.CostId or ReadOperation.CostIdentifier => "ModelCosts",
                    _ => "ModelProviderMappings"
                };
                var phase = operation is ReadOperation.Page or ReadOperation.ProviderPage ? SqlPhase.Count : SqlPhase.Read;
                await CancelObservedSqlWaitAsync(store, ids, providerId, operation, schema, connectionString,
                    applicationName, table, phase, deadline.Token);
                await AssertBothPoolSlotsReusableAsync(source, connectionString, typedNpgsql, deadline.Token);
                await AssertReadRecoversAsync(store, ids, providerId, operation, deadline.Token);
                if (operation is ReadOperation.Page or ReadOperation.ProviderPage)
                {
                    // The count touches only mappings. Locking Providers blocks the later graph SQL.
                    await CancelObservedSqlWaitAsync(store, ids, providerId, operation, schema, connectionString,
                        applicationName, "Providers", SqlPhase.PageItems, deadline.Token);
                    await AssertBothPoolSlotsReusableAsync(source, connectionString, typedNpgsql, deadline.Token);
                    await AssertReadRecoversAsync(store, ids, providerId, operation, deadline.Token);
                }
            }
        }
        finally { await DropMatrixSchemaAsync(schema, ownedConnectionStrings.ToArray()); }
    }

    private static readonly DateTime SnapshotInstant =
        new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_560);

    private static IModelProviderMappingRuntimeStore CreateMatrixStore(
        NpgsqlDataSource source, bool typedNpgsql) => typedNpgsql
            ? new NpgsqlModelProviderMappingRuntimeStore(source)
            : new EfModelProviderMappingRuntimeStore(new TestDbContextFactory(source));

    private async Task<SnapshotMatrix> SeedSnapshotMatrixAsync(string schema, CancellationToken cancellationToken = default)
    {
        await using var efSource = NpgsqlDataSource.Create(WithSearchPath(schema));
        await using var context = new TestDbContextFactory(efSource).CreateDbContext();
        var populated = NewSnapshotGraph("snapshot-route", populated: true);
        var nulls = NewSnapshotGraph("null-route", populated: false);
        var shared = new ModelProviderMapping
        {
            // Raw aliases remain unique for this provider while both match the case-insensitive lookup.
            ModelAlias = "SNAPSHOT-ROUTE", ProviderModelId = "snapshot/shared", Provider = populated.Provider,
            ModelProviderTypeAssociation = populated.ModelProviderTypeAssociation, IsEnabled = true,
            RoutingPriority = populated.RoutingPriority, RoutingWeight = 1.01m,
            ProviderOptions = null, CreatedAt = SnapshotInstant.AddDays(-3), UpdatedAt = SnapshotInstant.AddDays(-2)
        };
        var nullCost = NewSnapshotCost(populated: false);
        var policy = new ModelRoutePolicy
        {
            ModelAlias = populated.ModelAlias, Strategy = "FutureStrategy", CostWeight = 0.123m,
            SpeedWeight = 0.234m, QualityWeight = 0.345m, CacheAffinityEnabled = false,
            AffinityTtlSeconds = 137, MaxAffinityScorePenalty = 0.067m, IsEnabled = false,
            CreatedAt = SnapshotInstant.AddDays(-4), UpdatedAt = SnapshotInstant.AddDays(-1)
        };
        context.AddRange(populated, nulls, shared, nullCost, policy);
        await context.SaveChangesAsync(cancellationToken);
        return new SnapshotMatrix(populated, nulls, shared, nullCost, policy);
    }

    private static ModelProviderMapping NewSnapshotGraph(string alias, bool populated)
    {
        var series = new ModelSeries
        {
            Author = new ModelAuthor { Name = $"snapshot-author-{alias}" }, Name = $"snapshot-series-{alias}",
            Description = populated ? "series description" : null, TokenizerType = (TokenizerType)27001,
            Parameters = populated ? "{\"temperature\":{\"default\":0.7},\"extra\":[true,null]}" : "{}"
        };
        var model = new Model
        {
            Series = series, Name = $"snapshot-model-{alias}", Version = populated ? "future-version" : null,
            Description = populated ? "model description" : null, ModelCardUrl = populated ? "https://example.invalid/card" : null,
            SupportsVision = populated, SupportsImageGeneration = populated, SupportsVideoGeneration = populated,
            SupportsEmbeddings = populated, SupportsSpeechToText = populated, SupportsTextToSpeech = populated,
            SupportsRerank = populated, SupportsChat = populated, SupportsFunctionCalling = populated, SupportsStreaming = populated,
            InputModalitiesJson = populated ? "[\"text\",\"image\",\"future\"]" : null,
            OutputModalitiesJson = populated ? "[\"audio\",\"text\"]" : null,
            CapabilitySource = (ModelCapabilitySource)27002,
            CapabilitiesLastVerifiedAt = populated ? SnapshotInstant.AddHours(-2) : null,
            TokenizerType = (TokenizerType)27003, MaxInputTokens = populated ? 123_456 : null,
            MaxOutputTokens = populated ? 12_345 : null, IsActive = !populated,
            ModelParameters = populated ? "{\"model\":true,\"nested\":{\"value\":7}}" : null,
            CreatedAt = SnapshotInstant.AddDays(-5), UpdatedAt = SnapshotInstant.AddDays(-4)
        };
        var association = new ModelProviderTypeAssociation
        {
            Model = model, IsEnabled = !populated, MaxInputTokens = populated ? 23_456 : null,
            MaxOutputTokens = populated ? 3_456 : null, Identifier = $"snapshot/{alias}",
            Provider = populated ? (ProviderType)27004 : null, CapabilitySource = populated ? (ModelCapabilitySource)27005 : null,
            InputModalitiesJson = populated ? "[\"image\",\"text\"]" : null,
            OutputModalitiesJson = populated ? "[\"future-output\"]" : null,
            OperationalCapabilitiesJson = populated ? "{\"unknown_capability\":true,\"nested\":{\"count\":2}}" : null,
            CapabilitiesLastVerifiedAt = populated ? SnapshotInstant.AddHours(-1) : null,
            ProviderVariation = populated ? "future-variation" : null, QualityScore = populated ? 0.87654321m : null,
            SpeedScore = populated ? 12.345678m : null, IsPrimary = populated,
            Metadata = populated ? "{\"source\":\"snapshot\",\"array\":[1,true,null]}" : null,
            ModelCost = populated ? NewSnapshotCost(populated: true) : null
        };
        return new ModelProviderMapping
        {
            ModelAlias = alias, ProviderModelId = $"snapshot/provider/{alias}", ModelProviderTypeAssociation = association,
            Provider = new Provider
            {
                ProviderType = (ProviderType)27006, ProviderName = $"snapshot-provider-{alias}",
                BaseUrl = populated ? "https://example.invalid/snapshot" : null,
                Settings = populated ? new Dictionary<string, string> { ["region"] = "west", ["escaped"] = "a\"b\\c" } : null,
                IsEnabled = !populated, TrustProviderReportedCosts = populated, ProviderCostMarkupMultiplier = 1.2345m,
                CreatedAt = SnapshotInstant.AddDays(-7), UpdatedAt = SnapshotInstant.AddDays(-6)
            },
            IsEnabled = !populated, RoutingPriority = populated ? 17 : 29, RoutingWeight = populated ? 1.37m : 0.91m,
            ProviderOptions = populated ? "{\"future-option\":{\"enabled\":true}}" : null,
            CreatedAt = SnapshotInstant.AddDays(-3), UpdatedAt = SnapshotInstant.AddDays(-2)
        };
    }

    private static ModelCost NewSnapshotCost(bool populated) => new()
    {
        CostName = populated ? "populated snapshot cost" : "null snapshot cost", PricingModel = (PricingModel)27007,
        PricingConfiguration = populated ? "{\"future-pricing\":{\"units\":3}}" : null,
        InputCostPerMillionTokens = 1.1234567890m, OutputCostPerMillionTokens = 2.2345678901m,
        EmbeddingCostPerMillionTokens = populated ? 3.3456789012m : null,
        CreatedAt = SnapshotInstant.AddDays(-9), UpdatedAt = SnapshotInstant.AddDays(-8), ModelType = "future-model-type",
        IsActive = false, EffectiveDate = SnapshotInstant.AddDays(-11), ExpiryDate = populated ? SnapshotInstant.AddDays(-10) : null,
        Description = populated ? "cost description" : null, Priority = 37,
        BatchProcessingMultiplier = populated ? 0.6789m : null, SupportsBatchProcessing = populated,
        CachedInputCostPerMillionTokens = populated ? 4.4567890123m : null,
        CachedInputWriteCostPerMillionTokens = populated ? 5.5678901234m : null,
        CostPerSearchUnit = populated ? 0.12345678m : null, AudioCostPerMinute = populated ? 0.23456789m : null,
        AudioCostPerThousandCharacters = populated ? 0.34567890m : null,
        ReasoningCostPerMillionTokens = populated ? 6.6789012345m : null
    };

    private static void AssertGraphSnapshot(ModelProviderMappingRuntimeRecord? actual, ModelProviderMapping expected)
    {
        actual.Should().NotBeNull();
        actual!.Should().BeEquivalentTo(new
        {
            expected.Id, expected.ModelAlias, expected.ProviderModelId, expected.ProviderId, expected.IsEnabled,
            expected.RoutingPriority, expected.RoutingWeight, expected.ProviderOptions, expected.CreatedAt, expected.UpdatedAt
        });
        actual.Provider.Should().BeEquivalentTo(new
        {
            expected.Provider.Id, expected.Provider.ProviderType, expected.Provider.ProviderName, expected.Provider.BaseUrl,
            expected.Provider.Settings, expected.Provider.IsEnabled, expected.Provider.TrustProviderReportedCosts,
            expected.Provider.ProviderCostMarkupMultiplier, expected.Provider.CreatedAt, expected.Provider.UpdatedAt
        });
        actual.Provider.ProviderKeyCredentials.Should().BeEmpty();
        var association = expected.ModelProviderTypeAssociation;
        actual.Association.Should().BeEquivalentTo(new
        {
            association.Id, association.ModelId, association.IsEnabled, association.MaxInputTokens, association.MaxOutputTokens,
            CapabilitySource = association.CapabilitySource is null ? (int?)null : (int)association.CapabilitySource.Value,
            association.CapabilitiesLastVerifiedAt, association.ProviderVariation, association.QualityScore, association.SpeedScore,
            association.Identifier, ProviderType = association.Provider is null ? (int?)null : (int)association.Provider.Value,
            association.ModelCostId, association.IsPrimary, association.Metadata
        });
        AssertJsonSnapshot(actual.Association.InputModalitiesJson, association.InputModalitiesJson);
        AssertJsonSnapshot(actual.Association.OutputModalitiesJson, association.OutputModalitiesJson);
        AssertJsonSnapshot(actual.Association.OperationalCapabilitiesJson, association.OperationalCapabilitiesJson);
        var model = association.Model;
        actual.Association.Model.Should().BeEquivalentTo(new
        {
            model.Id, model.Name, model.Version, model.Description, model.ModelCardUrl, model.ModelSeriesId,
            model.SupportsVision, model.SupportsImageGeneration, model.SupportsVideoGeneration, model.SupportsEmbeddings,
            model.SupportsSpeechToText, model.SupportsTextToSpeech, model.SupportsRerank, model.SupportsChat,
            model.SupportsFunctionCalling, model.SupportsStreaming, CapabilitySource = (int)model.CapabilitySource,
            model.CapabilitiesLastVerifiedAt, TokenizerType = (int)model.TokenizerType, model.MaxInputTokens, model.MaxOutputTokens,
            model.IsActive, model.ModelParameters, model.CreatedAt, model.UpdatedAt
        });
        AssertJsonSnapshot(actual.Association.Model.InputModalitiesJson, model.InputModalitiesJson);
        AssertJsonSnapshot(actual.Association.Model.OutputModalitiesJson, model.OutputModalitiesJson);
        actual.Association.Model.Series.Should().BeEquivalentTo(new
        {
            model.Series.Id, model.Series.AuthorId, model.Series.Name, model.Series.Description,
            TokenizerType = (int)model.Series.TokenizerType, model.Series.Parameters
        });
        if (association.ModelCost is null) actual.Association.ModelCost.Should().BeNull();
        else AssertCostSnapshot(actual.Association.ModelCost, association.ModelCost);
        foreach (var instant in new[] { actual.CreatedAt, actual.UpdatedAt, actual.Provider.CreatedAt,
            actual.Provider.UpdatedAt, actual.Association.Model.CreatedAt, actual.Association.Model.UpdatedAt })
            instant.Kind.Should().Be(DateTimeKind.Utc);
        if (actual.Association.CapabilitiesLastVerifiedAt.HasValue)
            actual.Association.CapabilitiesLastVerifiedAt.Value.Kind.Should().Be(DateTimeKind.Utc);
        if (actual.Association.Model.CapabilitiesLastVerifiedAt.HasValue)
            actual.Association.Model.CapabilitiesLastVerifiedAt.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    private static void AssertCostSnapshot(ModelCostRuntimeRecord? actual, ModelCost expected)
    {
        actual.Should().NotBeNull();
        actual!.Should().BeEquivalentTo(new
        {
            expected.Id, expected.CostName, PricingModel = (int)expected.PricingModel, expected.PricingConfiguration,
            expected.InputCostPerMillionTokens, expected.OutputCostPerMillionTokens, expected.EmbeddingCostPerMillionTokens,
            expected.CreatedAt, expected.UpdatedAt, expected.ModelType, expected.IsActive, expected.EffectiveDate, expected.ExpiryDate,
            expected.Description, expected.Priority, expected.BatchProcessingMultiplier, expected.SupportsBatchProcessing,
            expected.CachedInputCostPerMillionTokens, expected.CachedInputWriteCostPerMillionTokens, expected.CostPerSearchUnit,
            expected.AudioCostPerMinute, expected.AudioCostPerThousandCharacters, expected.ReasoningCostPerMillionTokens
        });
        foreach (var instant in new[] { actual.CreatedAt, actual.UpdatedAt, actual.EffectiveDate })
            instant.Kind.Should().Be(DateTimeKind.Utc);
        if (actual.ExpiryDate.HasValue) actual.ExpiryDate.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    private static void AssertPolicySnapshot(ModelRoutePolicyRuntimeRecord? actual, ModelRoutePolicy expected)
    {
        actual.Should().NotBeNull();
        actual!.Should().BeEquivalentTo(new
        {
            expected.Id, expected.ModelAlias, expected.Strategy, expected.CostWeight, expected.SpeedWeight, expected.QualityWeight,
            expected.CacheAffinityEnabled, expected.AffinityTtlSeconds, expected.MaxAffinityScorePenalty, expected.IsEnabled,
            expected.CreatedAt, expected.UpdatedAt
        });
        actual.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        actual.UpdatedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    private static void AssertJsonSnapshot(string? actual, string? expected)
    {
        if (expected is null) { actual.Should().BeNull(); return; }
        actual.Should().NotBeNull();
        using var expectedJson = JsonDocument.Parse(expected);
        using var actualJson = JsonDocument.Parse(actual!);
        JsonElement.DeepEquals(actualJson.RootElement, expectedJson.RootElement).Should().BeTrue();
    }

    private async Task<SelectionMatrix> SeedSelectionMatrixAsync(string schema, SeedIds ids,
        CancellationToken cancellationToken = default)
    {
        await using var efSource = NpgsqlDataSource.Create(WithSearchPath(schema));
        await using var context = new TestDbContextFactory(efSource).CreateDbContext();
        var mappings = await context.ModelProviderMappings.Include(mapping => mapping.Provider)
            .Include(mapping => mapping.ModelProviderTypeAssociation).ThenInclude(association => association.Model)
            .ToListAsync(cancellationToken);
        var secondary = mappings.Single(mapping => mapping.Id == ids.SecondaryMappingId);
        secondary.Provider.IsEnabled = false;
        secondary.ModelProviderTypeAssociation.IsEnabled = false;
        secondary.ModelProviderTypeAssociation.Model.IsActive = false;
        var primary = mappings.Single(mapping => mapping.Id == ids.PrimaryMappingId);
        var otherModel = new Model { Name = "other page model", Series = new ModelSeries
            { Name = "other page series", Author = new ModelAuthor { Name = "other page author" } } };
        primary.ModelProviderTypeAssociation.Model = otherModel;
        var added = Enumerable.Range(0, 101).Select(index => new ModelProviderMapping
        {
            ModelAlias = $"page-route-{index:D3}", ProviderModelId = $"page/{index}", Provider = secondary.Provider,
            ModelProviderTypeAssociation = secondary.ModelProviderTypeAssociation, RoutingPriority = index % 3,
            IsEnabled = index % 2 != 0
        }).ToArray();
        // An existing route on the other provider creates a valid alias/Id tie at the page-size boundary.
        primary.ModelAlias = added[99].ModelAlias;
        primary.RoutingPriority = added[99].RoutingPriority;
        context.AddRange(added);
        (await context.ModelRoutePolicies.SingleAsync(cancellationToken)).IsEnabled = false;
        var now = DateTime.UtcNow;
        var winner = NewSelectionCost("winning effective date", 10, now.AddDays(-2), now.AddDays(2));
        var older = NewSelectionCost("older same priority", 10, now.AddDays(-3), null);
        var lower = NewSelectionCost("newer lower priority", 5, now.AddDays(-1), null);
        var inactive = NewSelectionCost("inactive higher priority", 100, now.AddDays(-1), null);
        inactive.IsActive = false;
        var future = NewSelectionCost("future higher priority", 101, now.AddDays(2), null);
        var expired = NewSelectionCost("expired higher priority", 102, now.AddDays(-3), now.AddDays(-2));
        var disabled = NewSelectionCost("disabled association higher priority", 103, now.AddDays(-1), null);
        var costs = new[] { winner, older, lower, inactive, future, expired, disabled };
        for (var index = 0; index < costs.Length; index++)
            context.ModelProviderTypeAssociations.Add(new ModelProviderTypeAssociation
            {
                Model = secondary.ModelProviderTypeAssociation.Model, Identifier = "cost-choice",
                Provider = (ProviderType)(28000 + index), ModelCost = costs[index], IsEnabled = costs[index] != disabled
            });
        await context.SaveChangesAsync(cancellationToken);
        return new SelectionMatrix(added, secondary.ProviderId, otherModel.Id, winner, older, lower,
            [inactive, future, expired, disabled]);
    }

    private static ModelCost NewSelectionCost(string name, int priority, DateTime effective, DateTime? expiry) => new()
    {
        CostName = name, IsActive = true, Priority = priority, EffectiveDate = effective, ExpiryDate = expiry,
        InputCostPerMillionTokens = 0.1234567890m, OutputCostPerMillionTokens = 0.2345678901m
    };

    private async Task SetCostActiveAsync(string schema, int costId, bool isActive)
    {
        await using var efSource = NpgsqlDataSource.Create(WithSearchPath(schema));
        await using var context = new TestDbContextFactory(efSource).CreateDbContext();
        (await context.ModelCosts.SingleAsync(cost => cost.Id == costId)).IsActive = isActive;
        await context.SaveChangesAsync();
    }

    private enum ReadOperation { Id, Alias, Page, ProviderPage, Model, Canonical, Policy, CostId, CostIdentifier }
    private enum SqlPhase { Read, Count, PageItems }

    private static Task InvokeReadAsync(IModelProviderMappingRuntimeStore store, SeedIds ids,
        int providerId, ReadOperation operation, CancellationToken token) => operation switch
    {
        ReadOperation.Id => store.GetByIdAsync(ids.SecondaryMappingId, token),
        ReadOperation.Alias => store.GetByAliasAsync("route-model", token),
        ReadOperation.Page => store.GetPaginatedAsync(1, 10, token),
        ReadOperation.ProviderPage => store.GetByProviderPaginatedAsync(providerId, 1, 10, token),
        ReadOperation.Model => store.GetByModelIdAsync(ids.ModelId, token),
        ReadOperation.Canonical => store.GetCanonicalModelIdForAssociationAsync(ids.SecondaryAssociationId, token),
        ReadOperation.Policy => store.GetRoutePolicyAsync("route-model", token),
        ReadOperation.CostId => store.GetModelCostByIdAsync(ids.CostId, token),
        ReadOperation.CostIdentifier => store.GetModelCostForIdentifierAsync("provider/secondary", token),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static async Task AssertReadRecoversAsync(IModelProviderMappingRuntimeStore store, SeedIds ids,
        int providerId, ReadOperation operation, CancellationToken token)
    {
        switch (operation)
        {
            case ReadOperation.Id:
                (await store.GetByIdAsync(ids.SecondaryMappingId, token))!.ProviderId.Should().Be(providerId); break;
            case ReadOperation.Alias:
                (await store.GetByAliasAsync("route-model", token)).Select(route => route.Id)
                    .Should().Equal(ids.SecondaryMappingId, ids.PrimaryMappingId); break;
            case ReadOperation.Page:
                var page = await store.GetPaginatedAsync(1, 10, token);
                page.TotalCount.Should().Be(2);
                page.Items.Select(route => route.Id).Should().Equal(ids.PrimaryMappingId, ids.SecondaryMappingId); break;
            case ReadOperation.ProviderPage:
                var providerPage = await store.GetByProviderPaginatedAsync(providerId, 1, 10, token);
                providerPage.TotalCount.Should().Be(1);
                providerPage.Items.Should().ContainSingle().Which.Id.Should().Be(ids.SecondaryMappingId); break;
            case ReadOperation.Model:
                (await store.GetByModelIdAsync(ids.ModelId, token)).Select(route => route.Id)
                    .Should().Equal(ids.PrimaryMappingId, ids.SecondaryMappingId); break;
            case ReadOperation.Canonical:
                (await store.GetCanonicalModelIdForAssociationAsync(ids.SecondaryAssociationId, token)).Should().Be(ids.ModelId); break;
            case ReadOperation.Policy:
                (await store.GetRoutePolicyAsync("route-model", token))!.AffinityTtlSeconds.Should().Be(900); break;
            case ReadOperation.CostId:
                (await store.GetModelCostByIdAsync(ids.CostId, token))!.InputCostPerMillionTokens.Should().Be(2.5m); break;
            case ReadOperation.CostIdentifier:
                (await store.GetModelCostForIdentifierAsync("provider/secondary", token))!.Id.Should().Be(ids.CostId); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static Task<NpgsqlConnection> OpenMatchingPoolConnectionAsync(
        NpgsqlDataSource source, string connectionString, bool typedNpgsql, CancellationToken token) =>
        source.OpenConnectionAsync(token).AsTask();

    private static async Task AssertBothPoolSlotsReusableAsync(
        NpgsqlDataSource source, string connectionString, bool typedNpgsql, CancellationToken token)
    {
        await using var first = await OpenMatchingPoolConnectionAsync(source, connectionString, typedNpgsql, token);
        await using var second = await OpenMatchingPoolConnectionAsync(source, connectionString, typedNpgsql, token);
        first.ProcessID.Should().NotBe(second.ProcessID);
    }

    private static async Task CancelObservedSqlWaitAsync(IModelProviderMappingRuntimeStore store, SeedIds ids,
        int providerId, ReadOperation operation, string schema, string connectionString, string applicationName,
        string table, SqlPhase phase, CancellationToken token)
    {
        await using var blocker = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            { Pooling = false, ApplicationName = $"{applicationName}_blocker" }.ConnectionString);
        await blocker.OpenAsync(token);
        await using var transaction = await blocker.BeginTransactionAsync(token);
        try
        {
            await using (var command = blocker.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"LOCK TABLE \"{schema}\".\"{table}\" IN ACCESS EXCLUSIVE MODE";
                await command.ExecuteNonQueryAsync(token);
            }
            using var cancelled = new CancellationTokenSource();
            var pending = InvokeReadAsync(store, ids, providerId, operation, cancelled.Token);
            try
            {
                await WaitForObservedSqlWaitAsync(connectionString, applicationName, blocker.ProcessID, schema, table, phase, pending, token);
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally { await DrainCancelledReadAsync(pending, cancelled); }
        }
        finally { await transaction.RollbackAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task WaitForObservedSqlWaitAsync(string connectionString, string applicationName,
        int blockerPid, string schema, string table, SqlPhase phase, Task pending, CancellationToken token)
    {
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            { Pooling = false, ApplicationName = $"{applicationName}_observer" }.ConnectionString);
        await observer.OpenAsync(token);
        await using var command = observer.CreateCommand();
        command.CommandText = """
            SELECT activity.query FROM pg_catalog.pg_stat_activity AS activity
            WHERE activity.application_name = @application AND activity.datname = current_database()
                AND activity.wait_event_type = 'Lock'
                AND @blocker = ANY(pg_catalog.pg_blocking_pids(activity.pid))
                AND EXISTS (
                    SELECT 1 FROM pg_catalog.pg_locks AS waiting
                    WHERE waiting.pid = activity.pid AND waiting.locktype = 'relation' AND NOT waiting.granted
                        AND waiting.relation = pg_catalog.to_regclass(@relation))
            """;
        command.Parameters.AddWithValue("application", applicationName);
        command.Parameters.AddWithValue("blocker", blockerPid);
        command.Parameters.AddWithValue("relation", $"\"{schema}\".\"{table}\"");
        while (true)
        {
            if (pending.IsCompleted)
            {
                await pending;
                throw new InvalidOperationException("Model-routing read completed before its SQL lock wait was observed.");
            }
            if (await command.ExecuteScalarAsync(token) is string sql)
            {
                // Relation OID proves the blocked table even when pg_stat_activity truncates long graph SQL.
                var isCount = sql.Contains("count(", StringComparison.OrdinalIgnoreCase);
                if (phase == SqlPhase.Count) isCount.Should().BeTrue("the mappings lock must block the first pagination statement");
                if (phase == SqlPhase.PageItems)
                {
                    isCount.Should().BeFalse("the count must have completed before the Providers join waits");
                }
                return;
            }
            await Task.Delay(25, token);
        }
    }

    private static async Task DrainCancelledReadAsync(Task pending, CancellationTokenSource cancelled)
    {
        cancelled.Cancel();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
    }

    private async Task DropMatrixSchemaAsync(string schema, params string[] connectionStrings)
    {
        foreach (var connectionString in connectionStrings.Append(WithSearchPath(schema)).Distinct())
        {
            using var ownedPool = new NpgsqlConnection(connectionString);
            NpgsqlConnection.ClearPool(ownedPool);
        }
        await DropSchemaAsync(schema);
    }

    private sealed record SnapshotMatrix(ModelProviderMapping Populated, ModelProviderMapping Nulls,
        ModelProviderMapping Shared, ModelCost NullCost, ModelRoutePolicy Policy);
    private sealed record SelectionMatrix(ModelProviderMapping[] Mappings, int ProviderId, int OtherModelId,
        ModelCost Winner, ModelCost Older, ModelCost LowerPriority, ModelCost[] ExcludedCosts);

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

    private async Task<SeedIds> CreateAndSeedSchemaAsync(string schema, CancellationToken cancellationToken = default)
    {
        await using (var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
            { Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var efSource = NpgsqlDataSource.Create(WithSearchPath(schema));
        var factory = new TestDbContextFactory(efSource);
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        // EnsureCreated checks every schema in the database and sees Testcontainers'
        // resource-reaper table. Execute EF's deterministic creation script directly so
        // every object is created in this connection's isolated search path.
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using (var createCommand = context.Database.GetDbConnection().CreateCommand())
        {
            createCommand.CommandText = context.Database.GenerateCreateScript();
            await createCommand.ExecuteNonQueryAsync(cancellationToken);
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
        await context.SaveChangesAsync(cancellationToken);

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

    private sealed class TestDbContextFactory(NpgsqlDataSource source) :
        IDbContextFactory<ConduitDbContext>
    {
        private readonly DbContextOptions<ConduitDbContext> _options =
            new DbContextOptionsBuilder<ConduitDbContext>()
                .UseNpgsql(source)
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
