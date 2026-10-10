using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Events;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ConduitLLM.Tests.Admin.Endpoints;

public class ProviderErrorsEndpointsTests
{
    private readonly Mock<IProviderErrorTrackingService> _errorService = new();
    private readonly Mock<IProviderKeyCredentialRepository> _keyRepository = new();
    private readonly Mock<IProviderRepository> _providerRepository = new();
    private readonly Mock<IEventPublisher> _eventPublisher = new();
    private readonly ProviderErrorsEndpoints _endpoints;

    public ProviderErrorsEndpointsTests()
    {
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext()
        };

        _endpoints = new ProviderErrorsEndpoints(
            _keyRepository.Object,
            _providerRepository.Object,
            _eventPublisher.Object,
            httpContextAccessor,
            Mock.Of<ILogger<ProviderErrorsEndpoints>>(),
            _errorService.Object);
    }

    [Fact]
    public async Task GetErrorSummary_ExposesProviderDisableMetadata()
    {
        var disabledAt = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
        var provider = new Provider { Id = 22, ProviderName = "Example" };
        _providerRepository
            .Setup(x => x.GetPaginatedAsync(1, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<Provider> { provider }, 1));
        _errorService
            .Setup(x => x.GetProviderSummaryAsync(provider.Id))
            .ReturnsAsync(new ProviderErrorSummary
            {
                ProviderId = provider.Id,
                ProviderDisabledAt = disabledAt,
                ProviderDisableReason = ProviderErrorTrackingService.AllKeysDisabledReason
            });

        var result = await _endpoints.GetErrorSummary();

        var summaries = Assert.IsType<List<ProviderErrorSummaryDto>>(
            Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        var summary = Assert.Single(summaries);
        Assert.Equal(disabledAt, summary.ProviderDisabledAt);
        Assert.Equal(
            ProviderErrorTrackingService.AllKeysDisabledReason,
            summary.ProviderDisableReason);
    }

    [Fact]
    public async Task ClearKeyErrors_ReenablesProviderWhenAllKeysAutoDisabledIt()
    {
        const int keyId = 11;
        const int providerId = 22;
        var key = new ProviderKeyCredential
        {
            Id = keyId,
            ProviderId = providerId,
            IsEnabled = false
        };
        var provider = new Provider
        {
            Id = providerId,
            IsEnabled = false
        };

        _keyRepository.Setup(x => x.GetByIdAsync(keyId)).ReturnsAsync(key);
        _providerRepository
            .Setup(x => x.GetByIdAsync(providerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);
        _errorService
            .Setup(x => x.GetProviderSummaryAsync(providerId))
            .ReturnsAsync(new ProviderErrorSummary
            {
                ProviderId = providerId,
                ProviderDisabledAt = DateTime.UtcNow,
                ProviderDisableReason = ProviderErrorTrackingService.AllKeysDisabledReason
            });

        await _endpoints.ClearKeyErrors(keyId, new ClearErrorsRequest
        {
            ReenableKey = true,
            ConfirmReenable = true
        });

        _keyRepository.Verify(
            x => x.UpdateAsync(
                It.Is<ProviderKeyCredential>(candidate => candidate.Id == keyId && candidate.IsEnabled),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _providerRepository.Verify(
            x => x.UpdateAsync(
                It.Is<Provider>(candidate => candidate.Id == providerId && candidate.IsEnabled),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _errorService.Verify(x => x.ClearProviderDisabledAsync(providerId), Times.Once);
    }

    [Fact]
    public async Task ClearKeyErrors_DoesNotOverrideManualProviderDisable()
    {
        const int keyId = 11;
        const int providerId = 22;
        var key = new ProviderKeyCredential
        {
            Id = keyId,
            ProviderId = providerId,
            IsEnabled = false
        };

        _keyRepository.Setup(x => x.GetByIdAsync(keyId)).ReturnsAsync(key);
        _errorService
            .Setup(x => x.GetProviderSummaryAsync(providerId))
            .ReturnsAsync(new ProviderErrorSummary
            {
                ProviderId = providerId,
                ProviderDisabledAt = null,
                ProviderDisableReason = null
            });

        await _endpoints.ClearKeyErrors(keyId, new ClearErrorsRequest
        {
            ReenableKey = true,
            ConfirmReenable = true
        });

        _providerRepository.Verify(
            x => x.UpdateAsync(It.IsAny<Provider>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _errorService.Verify(
            x => x.ClearProviderDisabledAsync(It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task ClearKeyErrors_BalanceGroup_ReenablesEveryGroupDisabledKey()
    {
        const int providerId = 22;
        var requestedKey = new ProviderKeyCredential
        {
            Id = 11,
            ProviderId = providerId,
            ProviderAccountGroup = 3,
            IsEnabled = false
        };
        var sharedAccountKey = new ProviderKeyCredential
        {
            Id = 12,
            ProviderId = providerId,
            ProviderAccountGroup = 3,
            IsEnabled = false
        };
        var unrelatedKey = new ProviderKeyCredential
        {
            Id = 13,
            ProviderId = providerId,
            ProviderAccountGroup = 4,
            IsEnabled = false
        };
        var providerKeys = new List<ProviderKeyCredential>
        {
            requestedKey,
            sharedAccountKey,
            unrelatedKey
        };

        _keyRepository.Setup(x => x.GetByIdAsync(requestedKey.Id))
            .ReturnsAsync(requestedKey);
        _keyRepository.Setup(x => x.GetByProviderIdPaginatedAsync(
                providerId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((providerKeys, providerKeys.Count));
        _errorService.Setup(x => x.GetKeyErrorDetailsAsync(requestedKey.Id))
            .ReturnsAsync(BalanceErrorDetails(requestedKey.Id));
        _errorService.Setup(x => x.GetKeyErrorDetailsAsync(sharedAccountKey.Id))
            .ReturnsAsync(BalanceErrorDetails(sharedAccountKey.Id));

        await _endpoints.ClearKeyErrors(requestedKey.Id, new ClearErrorsRequest
        {
            ReenableKey = true,
            ConfirmReenable = true
        });

        _keyRepository.Verify(x => x.UpdateAsync(
            It.Is<ProviderKeyCredential>(key =>
                (key.Id == requestedKey.Id || key.Id == sharedAccountKey.Id) &&
                key.IsEnabled),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        _keyRepository.Verify(x => x.UpdateAsync(
            It.Is<ProviderKeyCredential>(key => key.Id == unrelatedKey.Id),
            It.IsAny<CancellationToken>()), Times.Never);
        _errorService.Verify(x => x.ClearErrorsForKeyAsync(
            requestedKey.Id, providerId), Times.Once);
        _errorService.Verify(x => x.ClearErrorsForKeyAsync(
            sharedAccountKey.Id, providerId), Times.Once);
        _eventPublisher.Verify(x => x.PublishFireAndForget(
            It.Is<ProviderKeyReenabledEvent>(message =>
                message.ProviderAccountGroup == 3 &&
                message.AffectedKeyIds.OrderBy(id => id)
                    .SequenceEqual(new[] { 11, 12 })),
            "ClearKeyErrors",
            It.IsAny<object?>()), Times.Once);
    }

    private static KeyErrorDetails BalanceErrorDetails(int keyId) => new()
    {
        KeyId = keyId,
        FatalError = new FatalErrorInfo
        {
            ErrorType = ProviderErrorType.InsufficientBalance
        }
    };
}
