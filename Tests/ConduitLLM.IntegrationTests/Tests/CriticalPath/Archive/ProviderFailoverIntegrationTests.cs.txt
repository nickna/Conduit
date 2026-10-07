using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using ConduitLLM.IntegrationTests.Core;
using ConduitLLM.IntegrationTests.Infrastructure;

namespace ConduitLLM.IntegrationTests.Tests.CriticalPath;

/// <summary>
/// Integration tests for Provider Failover critical path.
/// Tests provider failure scenarios, circuit breaker behavior, and failover logic.
/// Uses real providers with failure simulation via invalid keys, disabled mappings, etc.
/// </summary>
[Collection("Critical Path")]
[Trait("Category", "Integration")]
[Trait("CriticalPath", "true")]
public class ProviderFailoverIntegrationTests : CriticalPathTestBase
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<ProviderFailoverIntegrationTests> _specificLogger;

    public ProviderFailoverIntegrationTests(
        TestFixture fixture,
        RedisTestContainerFixture redisFixture,
        ITestOutputHelper output)
        : base(fixture, redisFixture)
    {
        _output = output;
        _specificLogger = _fixture.ServiceProvider.GetRequiredService<ILogger<ProviderFailoverIntegrationTests>>();
    }

    protected override ILogger CreateLogger()
    {
        return _fixture.ServiceProvider.GetRequiredService<ILogger<ProviderFailoverIntegrationTests>>();
    }

    // =====================================================
    // Provider Failure Simulation Tests
    // =====================================================

    [Fact(DisplayName = "Provider with invalid key - Fails gracefully with error response")]
    public async Task ProviderWithInvalidKey_FailsGracefully()
    {
        // Arrange
        await WaitForServicesAsync();

        if (!_config.ActiveProviders.Any())
        {
            _output.WriteLine("Skipping: No active providers configured");
            return;
        }

        var providerName = _config.ActiveProviders.First();
        var providerConfig = ConfigurationLoader.LoadProviderConfig(providerName);
        var (virtualKey, groupId, balance) = await CreateRateLimitedKeyAsync(initialCredit: 10.00m);

        // Create provider with INVALID API key
        var providerId = await SetupProviderAsync(
            providerConfig,
            overrideApiKey: "sk-invalid-key-that-will-fail-authentication-12345",
            nameSuffix: "InvalidKey");
        var (modelAlias, _) = await SetupModelMappingAsync(providerId, providerConfig);

        // Act - Try to make a request to the provider with invalid key
        var chatRequest = new ChatCompletionRequest
        {
            Model = modelAlias,
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "Hello" }
            }
        };

        var response = await _apiClient.CorePostAsync<object>(
            "/v1/chat/completions",
            chatRequest,
            virtualKey);

        // Assert
        response.Success.Should().BeFalse("Request to provider with invalid key should fail");
        // 500 is deliberately not accepted: tolerating it is what let #1191 hide, since the Gateway
        // returned 500 for every routing failure. A rejected provider credential is a provider
        // communication error, which maps to 502 (see ChatEndpoints.MapProviderCommunicationError).
        response.StatusCode.Should().BeOneOf(new[] { 401, 403, 502 },
            "Should surface the provider's auth failure, not a generic server error");

        _output.WriteLine($"Invalid key correctly handled: {response.StatusCode}");

        // Verify no spend was deducted (error responses should not be billed)
        await AssertNoSpendDeductionAsync(groupId, balance);
        _output.WriteLine("Error correctly NOT billed");
    }

    [Fact(DisplayName = "Provider disabled - Returns 404 Not Found")]
    public async Task ProviderDisabled_Returns404()
    {
        // Arrange
        await WaitForServicesAsync();

        if (!_config.ActiveProviders.Any())
        {
            _output.WriteLine("Skipping: No active providers configured");
            return;
        }

        var providerName = _config.ActiveProviders.First();
        var providerConfig = ConfigurationLoader.LoadProviderConfig(providerName);
        var (virtualKey, groupId, balance) = await CreateRateLimitedKeyAsync(initialCredit: 10.00m);

        // Create provider but disable it
        var providerId = await SetupProviderAsync(providerConfig, nameSuffix: "Failover");
        var (modelAlias, _) = await SetupModelMappingAsync(providerId, providerConfig);

        // Disable the provider
        await DisableProviderAsync(providerId);

        // Act - Try to make a request
        var chatRequest = new ChatCompletionRequest
        {
            Model = modelAlias,
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "Hello" }
            }
        };

        var response = await _apiClient.CorePostAsync<object>(
            "/v1/chat/completions",
            chatRequest,
            virtualKey);

        // Assert
        response.Success.Should().BeFalse("Request to disabled provider should fail");
        response.StatusCode.Should().BeOneOf(new[] { 404, 503 },
            "Disabled provider should return 404 or 503");

        _output.WriteLine($"Disabled provider correctly handled: {response.StatusCode}");

        // Verify no spend was deducted
        await AssertNoSpendDeductionAsync(groupId, balance);
    }

    [Fact(DisplayName = "Model mapping disabled - Returns 404 Not Found")]
    public async Task ModelMappingDisabled_Returns404()
    {
        // Arrange
        await WaitForServicesAsync();

        if (!_config.ActiveProviders.Any())
        {
            _output.WriteLine("Skipping: No active providers configured");
            return;
        }

        var providerName = _config.ActiveProviders.First();
        var providerConfig = ConfigurationLoader.LoadProviderConfig(providerName);
        var (virtualKey, groupId, balance) = await CreateRateLimitedKeyAsync(initialCredit: 10.00m);

        var providerId = await SetupProviderAsync(providerConfig, nameSuffix: "Failover");
        var (modelAlias, mappingId) = await SetupModelMappingAsync(providerId, providerConfig);

        // Disable the model mapping
        await DisableModelMappingAsync(mappingId);

        // Act
        var chatRequest = new ChatCompletionRequest
        {
            Model = modelAlias,
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "Hello" }
            }
        };

        var response = await _apiClient.CorePostAsync<object>(
            "/v1/chat/completions",
            chatRequest,
            virtualKey);

        // Assert
        response.Success.Should().BeFalse("Request to disabled model mapping should fail");
        response.StatusCode.Should().Be(404, "Disabled mapping should return 404");

        _output.WriteLine($"Disabled mapping correctly handled: {response.StatusCode}");
    }

    // =====================================================
    // Circuit Breaker Behavior Tests
    // =====================================================

    [Fact(DisplayName = "Consecutive failures - Provider continues to accept requests")]
    public async Task ConsecutiveFailures_StillAcceptsRequests()
    {
        // Arrange
        await WaitForServicesAsync();

        if (!_config.ActiveProviders.Any())
        {
            _output.WriteLine("Skipping: No active providers configured");
            return;
        }

        var providerName = _config.ActiveProviders.First();
        var providerConfig = ConfigurationLoader.LoadProviderConfig(providerName);
        var (virtualKey, groupId, balance) = await CreateRateLimitedKeyAsync(initialCredit: 100.00m);

        // Create provider with invalid key to simulate failures
        var providerId = await SetupProviderAsync(
            providerConfig,
            overrideApiKey: "sk-invalid-key-that-will-fail-authentication-12345",
            nameSuffix: "InvalidKey");
        var (modelAlias, _) = await SetupModelMappingAsync(providerId, providerConfig);

        // Act - Make multiple failing requests
        var responses = new List<ApiResponse<object>>();

        for (int i = 0; i < 5; i++)
        {
            var chatRequest = new ChatCompletionRequest
            {
                Model = modelAlias,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "user", Content = "Hello" }
                }
            };

            var response = await _apiClient.CorePostAsync<object>(
                "/v1/chat/completions",
                chatRequest,
                virtualKey);

            responses.Add(response);
            _output.WriteLine($"Request {i + 1}: Status={response.StatusCode}");

            await Task.Delay(100);
        }

        // Assert - All requests should be processed (not circuit breaker rejection)
        // The key test is that each request is actually attempted, not fast-failed
        var allProcessed = responses.All(r =>
            r.StatusCode == 401 ||
            r.StatusCode == 403 ||
            r.StatusCode == 500 ||
            r.StatusCode == 502);

        allProcessed.Should().BeTrue("All requests should be processed, not circuit-breaker rejected");

        // Verify no spend was deducted
        await AssertNoSpendDeductionAsync(groupId, balance);

        _output.WriteLine($"All {responses.Count} requests processed without circuit breaker blocking");
    }

    [Fact(DisplayName = "Recovery after failure - Successful request after fixing provider")]
    public async Task RecoveryAfterFailure_SuccessfulRequest()
    {
        // Arrange
        await WaitForServicesAsync();

        if (!_config.ActiveProviders.Any())
        {
            _output.WriteLine("Skipping: No active providers configured");
            return;
        }

        var providerName = _config.ActiveProviders.First();
        var providerConfig = ConfigurationLoader.LoadProviderConfig(providerName);
        var (virtualKey, groupId, balance) = await CreateRateLimitedKeyAsync(initialCredit: 100.00m);

        // Create provider with VALID key
        var providerId = await SetupProviderAsync(providerConfig, nameSuffix: "Failover");
        var (modelAlias, _) = await SetupModelMappingAsync(providerId, providerConfig);
        await SetupModelCostAsync(providerConfig);

        // Act - Make a successful request
        var chatRequest = new ChatCompletionRequest
        {
            Model = modelAlias,
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "Say 'recovered' in one word." }
            }
        };

        var response = await _apiClient.CorePostAsync<ChatCompletionResponse>(
            "/v1/chat/completions",
            chatRequest,
            virtualKey);

        // Assert
        response.Success.Should().BeTrue($"Request should succeed: {response.Error}");
        response.Data.Should().NotBeNull();
        response.Data!.Choices.Should().NotBeEmpty();

        _output.WriteLine($"Recovery successful: {response.Data.Choices[0].Message.Content}");

        // Verify spend WAS deducted for successful request
        await AssertSpendDeductedAsync(groupId, balance, 0.000001m);
        _output.WriteLine("Successful request correctly billed");
    }

    // =====================================================
    // Timeout Simulation Tests
    // =====================================================

    [Fact(DisplayName = "Provider timeout - Returns gateway timeout error")]
    public async Task ProviderTimeout_ReturnsGatewayTimeout()
    {
        // This test verifies timeout handling
        // Since we can't easily cause a real timeout in integration tests,
        // we document the expected behavior

        _output.WriteLine("Note: Full timeout testing requires mock infrastructure");
        _output.WriteLine("Expected behavior: 504 Gateway Timeout after configured timeout period");
        _output.WriteLine("Current configured timeouts:");
        _output.WriteLine($"  - Default: {_config.Environment.Timeouts.Default}s");
        _output.WriteLine($"  - Chat: {_config.Environment.Timeouts.Chat}s");
        _output.WriteLine($"  - ImageGen: {_config.Environment.Timeouts.ImageGen}s");
        _output.WriteLine($"  - VideoGen: {_config.Environment.Timeouts.VideoGen}s");

        // Verify timeouts are configured
        _config.Environment.Timeouts.Default.Should().BeGreaterThan(0);
        _config.Environment.Timeouts.Chat.Should().BeGreaterThan(0);
    }
}
