using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Models;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Moq;

namespace ConduitLLM.Tests.Configuration.Repositories;

public sealed class StoreBackedModelProviderMappingRepositoryTests
{
    private readonly Mock<IModelProviderMappingRuntimeStore> _store = new();
    private readonly StoreBackedModelProviderMappingRepository _repository;

    public StoreBackedModelProviderMappingRepositoryTests()
    {
        _repository = new StoreBackedModelProviderMappingRepository(_store.Object);
    }

    [Fact]
    public async Task AliasLookupRehydratesTheCompleteLegacyRoutingGraph()
    {
        var record = NewRecord();
        _store.Setup(store => store.GetByAliasAsync("route-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync([record]);

        var mapping = Assert.Single(await _repository.GetAllByModelNameAsync("route-model"));

        Assert.Equal(record.Id, mapping.Id);
        Assert.Equal(record.ProviderOptions, mapping.ProviderOptions);
        Assert.Equal(ProviderType.OpenRouter, mapping.Provider.ProviderType);
        Assert.Equal("west", mapping.Provider.Settings!["region"]);
        Assert.Equal(ModelCapabilitySource.ProviderApi,
            mapping.ModelProviderTypeAssociation.CapabilitySource);
        Assert.Equal(TokenizerType.O200KBase,
            mapping.ModelProviderTypeAssociation.Model.TokenizerType);
        Assert.Equal("runtime series",
            mapping.ModelProviderTypeAssociation.Model.Series.Name);
        Assert.Equal(2.5m,
            mapping.ModelProviderTypeAssociation.ModelCost!.InputCostPerMillionTokens);
    }

    [Fact]
    public async Task PagesAndCanonicalLookupDelegateToFixedShapeStore()
    {
        var record = NewRecord();
        _store.Setup(store => store.GetByProviderPaginatedAsync(
                7, 2, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelProviderMappingRuntimePage([record], 11));
        _store.Setup(store => store.GetCanonicalModelIdForAssociationAsync(
                record.Association.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(record.Association.ModelId);

        var page = await _repository.GetByProviderPaginatedAsync(7, 2, 10);

        Assert.Equal(11, page.TotalCount);
        Assert.Equal(record.Id, Assert.Single(page.Items).Id);
        Assert.Equal(record.Association.ModelId,
            await _repository.GetCanonicalModelIdForAssociationAsync(record.Association.Id));
    }

    [Fact]
    public async Task ManagementWritesRemainOutsideNativeGateway()
    {
        var mapping = new ModelProviderMapping();

        await Assert.ThrowsAsync<NotSupportedException>(() => _repository.CreateAsync(mapping));
        await Assert.ThrowsAsync<NotSupportedException>(() => _repository.UpdateAsync(mapping));
        await Assert.ThrowsAsync<NotSupportedException>(() => _repository.DeleteAsync(1));
    }

    private static ModelProviderMappingRuntimeRecord NewRecord()
    {
        var now = DateTime.UtcNow;
        return new ModelProviderMappingRuntimeRecord
        {
            Id = 1,
            ModelAlias = "route-model",
            ProviderModelId = "provider/model",
            ProviderId = 7,
            IsEnabled = true,
            RoutingPriority = 10,
            RoutingWeight = 1.25m,
            ProviderOptions = "{\"route\":\"fallback\"}",
            CreatedAt = now,
            UpdatedAt = now,
            Provider = new Provider
            {
                Id = 7,
                ProviderType = ProviderType.OpenRouter,
                ProviderName = "runtime provider",
                Settings = new Dictionary<string, string> { ["region"] = "west" },
                IsEnabled = true,
                TrustProviderReportedCosts = true,
                ProviderCostMarkupMultiplier = 1.125m,
                CreatedAt = now,
                UpdatedAt = now
            },
            Association = new ModelProviderTypeAssociationRuntimeRecord
            {
                Id = 8,
                ModelId = 9,
                IsEnabled = true,
                Identifier = "provider/model",
                ProviderType = (int)ProviderType.OpenRouter,
                CapabilitySource = (int)ModelCapabilitySource.ProviderApi,
                ModelCostId = 10,
                IsPrimary = true,
                Model = new ModelRuntimeRecord
                {
                    Id = 9,
                    Name = "runtime model",
                    ModelSeriesId = 11,
                    SupportsChat = true,
                    SupportsStreaming = true,
                    SupportsFunctionCalling = true,
                    CapabilitySource = (int)ModelCapabilitySource.ProviderApi,
                    TokenizerType = (int)TokenizerType.O200KBase,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Series = new ModelSeriesRuntimeRecord
                    {
                        Id = 11,
                        AuthorId = 12,
                        Name = "runtime series",
                        TokenizerType = (int)TokenizerType.O200KBase,
                        Parameters = "{}"
                    }
                },
                ModelCost = new ModelCostRuntimeRecord
                {
                    Id = 10,
                    CostName = "runtime cost",
                    PricingModel = (int)PricingModel.Standard,
                    InputCostPerMillionTokens = 2.5m,
                    OutputCostPerMillionTokens = 10m,
                    CreatedAt = now,
                    UpdatedAt = now,
                    ModelType = "chat",
                    IsActive = true,
                    EffectiveDate = now
                }
            }
        };
    }
}
