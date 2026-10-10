using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Repositories;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace ConduitLLM.Tests.Admin.Services;

public class AdminModelProviderMappingServiceBulkTests
{
    private readonly Mock<IModelProviderMappingRepository> _mappingRepository = new();
    private readonly Mock<IProviderRepository> _providerRepository = new();
    private readonly Mock<IModelRepository> _modelRepository = new();
    private readonly List<ModelProviderMapping> _mappings = new();
    private readonly List<Provider> _providers = new();
    private readonly List<ModelProviderTypeAssociation> _associations = new();
    private readonly AdminModelProviderMappingService _service;
    private int _nextMappingId = 100;

    public AdminModelProviderMappingServiceBulkTests()
    {
        _providers.AddRange([
            new Provider { Id = 1, ProviderName = "OpenAI", ProviderType = ProviderType.OpenAI },
            new Provider { Id = 2, ProviderName = "Groq", ProviderType = ProviderType.Groq },
            new Provider { Id = 3, ProviderName = "OpenAI Secondary", ProviderType = ProviderType.OpenAI }
        ]);

        _providerRepository
            .Setup(repository => repository.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _providers.ToList());
        _providerRepository
            .Setup(repository => repository.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => _providers.FirstOrDefault(provider => provider.Id == id));
        _mappingRepository
            .Setup(repository => repository.GetAllUnboundedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _mappings.ToList());
        _mappingRepository
            .Setup(repository => repository.GetAllByModelNameAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string alias, CancellationToken _) => _mappings
                .Where(mapping => mapping.ModelAlias.Equals(alias, StringComparison.OrdinalIgnoreCase))
                .ToList());
        _mappingRepository
            .Setup(repository => repository.CreateAsync(
                It.IsAny<ModelProviderMapping>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModelProviderMapping mapping, CancellationToken _) =>
            {
                mapping.Id = _nextMappingId++;
                _mappings.Add(mapping);
                return mapping.Id;
            });
        _modelRepository
            .Setup(repository => repository.GetProviderTypeAssociationsByIdentifiersAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> identifiers, CancellationToken _) => _associations
                .Where(association => identifiers.Contains(
                    association.Identifier, StringComparer.OrdinalIgnoreCase))
                .ToList());
        _modelRepository
            .Setup(repository => repository.GetProviderTypeAssociationByIdAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) =>
                _associations.FirstOrDefault(association => association.Id == id));

        _service = new AdminModelProviderMappingService(
            _mappingRepository.Object,
            _providerRepository.Object,
            _modelRepository.Object,
            NullLogger<AdminModelProviderMappingService>.Instance);
    }

    [Fact]
    public async Task PreviewBulkMappingsAsync_ReportsMissingAndWrongProviderAssociations()
    {
        AddAssociation(20, "groq-test", ProviderType.Groq, 200);
        var request = new BulkModelMappingPreviewRequest
        {
            Mappings =
            [
                Item("missing", 1, "not-configured"),
                Item("wrong-provider", 1, "groq-test")
            ]
        };

        var result = await _service.PreviewBulkMappingsAsync(request);

        Assert.Equal(2, result.ConflictCount);
        Assert.Equal(BulkModelMappingErrorType.AssociationNotFound, result.Items[0].ErrorType);
        Assert.Equal(BulkModelMappingErrorType.AssociationProviderMismatch, result.Items[1].ErrorType);
    }

    [Fact]
    public async Task PreviewBulkMappingsAsync_KeysConflictsByAliasAndProvider()
    {
        var openAiAssociation = AddAssociation(10, "shared-model", ProviderType.OpenAI, 100);
        var groqAssociation = AddAssociation(20, "groq-shared", ProviderType.Groq, 100);
        AddExistingMapping("shared", _providers[0], openAiAssociation, "shared-model");
        var request = new BulkModelMappingPreviewRequest
        {
            Mappings =
            [
                Item("shared", 1, "shared-model"),
                Item("shared", 2, "groq-shared")
            ]
        };

        var result = await _service.PreviewBulkMappingsAsync(request);

        Assert.True(result.Items[0].HasConflict);
        Assert.Equal(BulkModelMappingErrorType.ExistingMapping, result.Items[0].ErrorType);
        Assert.False(result.Items[1].HasConflict);
        Assert.Equal(groqAssociation.Id, result.Items[1].ModelProviderTypeAssociationId);
    }

    [Fact]
    public async Task PreviewBulkMappingsAsync_ReportsDuplicateAliasProviderWithinBatch()
    {
        AddAssociation(10, "duplicate-model", ProviderType.OpenAI, 100);
        var request = new BulkModelMappingPreviewRequest
        {
            Mappings =
            [
                Item("duplicate", 1, "duplicate-model"),
                Item("DUPLICATE", 1, "duplicate-model")
            ]
        };

        var result = await _service.PreviewBulkMappingsAsync(request);

        Assert.False(result.Items[0].HasConflict);
        Assert.Equal(BulkModelMappingErrorType.DuplicateRequest, result.Items[1].ErrorType);
    }

    [Fact]
    public async Task CreateBulkMappingsAsync_CommitsValidItemsAndReportsPartialFailure()
    {
        var association = AddAssociation(10, "valid-model", ProviderType.OpenAI, 100);
        var request = new BulkModelMappingCreateRequest
        {
            Mappings =
            [
                Item("valid", 1, "valid-model"),
                Item("missing", 1, "missing-model")
            ],
            Priority = 25,
            Weight = 1.2m,
            IsEnabled = false
        };

        var result = await _service.CreateBulkMappingsAsync(request);

        Assert.True(result.IsPartialSuccess);
        Assert.Single(result.Created);
        Assert.Single(result.Failed);
        Assert.Equal(association.Id, result.Created[0].ModelProviderTypeAssociationId);
        Assert.Equal(BulkModelMappingErrorType.AssociationNotFound, result.Failed[0].ErrorType);
        Assert.Equal(25, _mappings[0].RoutingPriority);
        Assert.Equal(1.2m, _mappings[0].RoutingWeight);
        Assert.False(_mappings[0].IsEnabled);
    }

    [Fact]
    public async Task CreateBulkMappingsAsync_IsIdempotentForDuplicatesAndRetries()
    {
        AddAssociation(10, "retry-model", ProviderType.OpenAI, 100);
        var request = new BulkModelMappingCreateRequest
        {
            Mappings =
            [
                Item("retry", 1, "retry-model"),
                Item("RETRY", 1, "retry-model")
            ]
        };

        var first = await _service.CreateBulkMappingsAsync(request);
        var retry = await _service.CreateBulkMappingsAsync(request);

        Assert.Single(first.Created);
        Assert.Single(first.Existing);
        Assert.Empty(first.Failed);
        Assert.Empty(retry.Created);
        Assert.Equal(2, retry.ExistingCount);
        Assert.Empty(retry.Failed);
        Assert.Single(_mappings);
    }

    [Fact]
    public async Task CreateBulkMappingsAsync_PropagatesRequestedCancellation()
    {
        AddAssociation(10, "cancelled-model", ProviderType.OpenAI, 100);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _mappingRepository
            .Setup(repository => repository.CreateAsync(
                It.IsAny<ModelProviderMapping>(), cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var request = new BulkModelMappingCreateRequest
        {
            Mappings =
            [
                Item("cancelled", 1, "cancelled-model")
            ]
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.CreateBulkMappingsAsync(request, cancellation.Token));
    }

    [Fact]
    public async Task AddMappingAsync_RejectsAssociationFromAnotherProviderType()
    {
        var association = AddAssociation(20, "groq-test", ProviderType.Groq, 200);
        var mapping = new ModelProviderMapping
        {
            ModelAlias = "claude-test",
            ProviderId = 1,
            ProviderModelId = association.Identifier,
            ModelProviderTypeAssociationId = association.Id
        };

        var result = await _service.AddMappingAsync(mapping);

        Assert.False(result);
        _mappingRepository.Verify(repository => repository.CreateAsync(
            It.IsAny<ModelProviderMapping>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private ModelProviderTypeAssociation AddAssociation(
        int id,
        string identifier,
        ProviderType providerType,
        int modelId)
    {
        var association = new ModelProviderTypeAssociation
        {
            Id = id,
            Identifier = identifier,
            Provider = providerType,
            ModelId = modelId,
            Model = new Model { Id = modelId, Name = $"model-{modelId}" },
            IsEnabled = true
        };
        _associations.Add(association);
        return association;
    }

    private void AddExistingMapping(
        string alias,
        Provider provider,
        ModelProviderTypeAssociation association,
        string providerModelId)
    {
        _mappings.Add(new ModelProviderMapping
        {
            Id = _nextMappingId++,
            ModelAlias = alias,
            ProviderId = provider.Id,
            Provider = provider,
            ProviderModelId = providerModelId,
            ModelProviderTypeAssociationId = association.Id,
            ModelProviderTypeAssociation = association
        });
    }

    private static BulkModelMappingItemDto Item(string alias, int providerId, string providerModelId) => new()
    {
        ModelAlias = alias,
        ProviderId = providerId,
        ProviderModelId = providerModelId
    };
}
