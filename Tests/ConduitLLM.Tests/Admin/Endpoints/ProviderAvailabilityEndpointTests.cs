using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Security;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace ConduitLLM.Tests.Admin.Endpoints;

public class ProviderAvailabilityEndpointTests
{
    private readonly Mock<IProviderRepository> _providerRepository = new();
    private readonly Mock<ILLMClientFactory> _clientFactory = new();

    private ProviderCredentialsEndpoints CreateEndpoints() => new(
        _providerRepository.Object,
        Mock.Of<IProviderKeyCredentialRepository>(),
        _clientFactory.Object,
        Mock.Of<IProviderSecretProtector>(),
        Mock.Of<IEventPublisher>(),
        new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
        NullLogger<ProviderCredentialsEndpoints>.Instance);

    [Theory]
    [InlineData(ProviderType.Unknown)]
    [InlineData(ProviderType.Ultravox)]
    [InlineData(ProviderType.ElevenLabs)]
    [InlineData((ProviderType)999)]
    public async Task CreateProvider_Should_Reject_Unavailable_Types_Before_Persistence(ProviderType providerType)
    {
        var result = await CreateEndpoints().CreateProvider(new CreateProviderRequest
        {
            ProviderType = providerType,
            ProviderName = "Unavailable provider",
            BaseUrl = "https://override.example.test/v1"
        });

        var response = result.Should().BeAssignableTo<IStatusCodeHttpResult>().Subject;
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _providerRepository.VerifyNoOtherCalls();
        _clientFactory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ProviderType.Ultravox)]
    [InlineData(ProviderType.ElevenLabs)]
    public async Task TestCredentials_Should_Reject_Unimplemented_Types_Before_Client_Creation(ProviderType providerType)
    {
        var result = await CreateEndpoints().TestProviderConnectionWithCredentials(new TestProviderRequest
        {
            ProviderType = providerType,
            ApiKey = "test-key",
            BaseUrl = "https://override.example.test/v1"
        });

        var response = result.Should().BeAssignableTo<IStatusCodeHttpResult>().Subject;
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _clientFactory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ProviderType.Ultravox)]
    [InlineData(ProviderType.ElevenLabs)]
    public async Task UpdateProvider_Should_Allow_Disabling_Legacy_Records_Without_Adapter_Defaults(ProviderType providerType)
    {
        var provider = new Provider
        {
            Id = 7,
            ProviderType = providerType,
            ProviderName = "Legacy provider",
            IsEnabled = true
        };
        _providerRepository
            .Setup(repository => repository.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        var result = await CreateEndpoints().UpdateProvider(7, new UpdateProviderRequest { IsEnabled = false });

        var response = result.Should().BeOfType<Ok<ProviderDto>>().Subject.Value!;
        response.ProviderType.Should().Be(providerType);
        response.IsEnabled.Should().BeFalse();
        _providerRepository.Verify(repository => repository.UpdateAsync(provider, It.IsAny<CancellationToken>()), Times.Once);
    }
}
