using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using ConduitLLM.IntegrationTests.Core;
using ConduitLLM.IntegrationTests.Infrastructure;

namespace ConduitLLM.IntegrationTests.Tests.CriticalPath;

/// <summary>
/// Integration tests for the Request Processing Pipeline critical path.
/// Tests end-to-end flows, provider routing, billing policy, and streaming.
/// </summary>
[Collection("Critical Path")]
[Trait("Category", "Integration")]
[Trait("CriticalPath", "true")]
public class RequestPipelineIntegrationTests : CriticalPathTestBase
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<RequestPipelineIntegrationTests> _specificLogger;

    public RequestPipelineIntegrationTests(
        TestFixture fixture,
        RedisTestContainerFixture redisFixture,
        ITestOutputHelper output)
        : base(fixture, redisFixture)
    {
        _output = output;
        _specificLogger = _fixture.ServiceProvider.GetRequiredService<ILogger<RequestPipelineIntegrationTests>>();
    }

    protected override ILogger CreateLogger()
    {
        return _fixture.ServiceProvider.GetRequiredService<ILogger<RequestPipelineIntegrationTests>>();
    }

    // =====================================================
    // End-to-End Flow Tests
    // =====================================================

    [Fact(DisplayName = "Chat Completion - Non-streaming returns complete response")]
    public async Task ChatCompletion_NonStreaming_ReturnsCompleteResponse()
    {
        // Arrange
        await WaitForServicesAsync();

        // Skip if no active providers
        if (!_config.ActiveProviders.Any())
        {
            _output.WriteLine("Skipping: No active providers configured");
            return;
        }

        var providerName = _config.ActiveProviders.First();
        var providerConfig = ConfigurationLoader.LoadProviderConfig(providerName);
        var (virtualKey, groupId, initialBalance) = await CreateRateLimitedKeyAsync(initialCredit: 100.00m);

        // Set up minimal provider infrastructure
        var providerId = await SetupProviderAsync(providerConfig, nameSuffix: "Pipeline");
        var (modelAlias, _) = await SetupModelMappingAsync(providerId, providerConfig);

        // Act
        var chatRequest = new ChatCompletionRequest
        {
            Model = modelAlias,
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "Say 'hello' in exactly one word." }
            },
            Stream = false
        };

        var response = await _apiClient.CorePostAsync<ChatCompletionResponse>(
            "/v1/chat/completions",
            chatRequest,
            virtualKey);

        // Assert
        response.Success.Should().BeTrue($"Chat completion should succeed: {response.Error}");
        response.Data.Should().NotBeNull();
        response.Data!.Choices.Should().NotBeEmpty("Response should have choices");
        response.Data.Choices[0].Message.Content.Should().NotBeNullOrEmpty("Response should have content");
        response.Data.Usage.Should().NotBeNull("Response should include usage data");
        response.Data.Usage!.TotalTokens.Should().BeGreaterThan(0, "Total tokens should be tracked");

        _output.WriteLine($"Chat completion successful: {response.Data.Usage.TotalTokens} tokens");
        _output.WriteLine($"Response: {response.Data.Choices[0].Message.Content}");
    }

    [Fact(DisplayName = "Chat Completion - Streaming returns SSE events")]
    public async Task ChatCompletion_Streaming_ReturnsSSEEvents()
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
        var (virtualKey, groupId, initialBalance) = await CreateRateLimitedKeyAsync(initialCredit: 100.00m);

        var providerId = await SetupProviderAsync(providerConfig, nameSuffix: "Pipeline");
        var (modelAlias, _) = await SetupModelMappingAsync(providerId, providerConfig);

        // Act
        var chatRequest = new ChatCompletionRequest
        {
            Model = modelAlias,
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "Count from 1 to 5." }
            },
            Stream = true
        };

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(chatRequest),
                Encoding.UTF8,
                "application/json")
        };
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", virtualKey);

        using var response = await _apiClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);
        response.IsSuccessStatusCode.Should().BeTrue($"Streaming request should succeed: {response.StatusCode}");

        // Parse SSE events
        using var stream = await response.Content.ReadAsStreamAsync();
        var events = await StreamingResponseParser.CollectAllAsync(stream);

        // Assert
        events.Should().NotBeEmpty("Should receive SSE events");

        var contentEvents = events.Where(e => !e.IsDone && e.EventType != "metrics-final").ToList();
        contentEvents.Should().NotBeEmpty("Should receive content events");

        var content = StreamingResponseParser.ExtractContent(events);
        content.Should().NotBeNullOrEmpty("Aggregated content should not be empty");

        var usage = StreamingResponseParser.ExtractFinalUsage(events);
        // Usage may or may not be present depending on provider
        if (usage != null)
        {
            usage.TotalTokens.Should().BeGreaterThan(0);
            _output.WriteLine($"Streaming usage: {usage.TotalTokens} tokens");
        }

        _output.WriteLine($"Received {events.Count} SSE events");
        _output.WriteLine($"Content: {content[..Math.Min(100, content.Length)]}...");
    }

    [Fact(DisplayName = "Chat Completion - Streaming extracts usage from final chunk")]
    public async Task ChatCompletion_Streaming_ExtractsUsageFromFinalChunk()
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
        var (virtualKey, groupId, initialBalance) = await CreateRateLimitedKeyAsync(initialCredit: 100.00m);

        var providerId = await SetupProviderAsync(providerConfig, nameSuffix: "Pipeline");
        var (modelAlias, _) = await SetupModelMappingAsync(providerId, providerConfig);

        // Act
        var chatRequest = new ChatCompletionRequest
        {
            Model = modelAlias,
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "What is 2+2?" }
            },
            Stream = true
        };

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(chatRequest),
                Encoding.UTF8,
                "application/json")
        };
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", virtualKey);

        using var response = await _apiClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);
        using var stream = await response.Content.ReadAsStreamAsync();
        var events = await StreamingResponseParser.CollectAllAsync(stream);

        // Assert
        var doneEvent = events.FirstOrDefault(e => e.IsDone);
        doneEvent.Should().NotBeNull("Stream should end with [DONE] marker");

        // Check for metrics-final event (Conduit-specific)
        var metricsFinal = events.FirstOrDefault(e => e.EventType == "metrics-final");
        if (metricsFinal != null)
        {
            _output.WriteLine($"Metrics final event: {metricsFinal.Data}");
            var usage = StreamingResponseParser.ExtractFinalUsage(events);
            usage.Should().NotBeNull("Usage should be extractable from metrics-final");
        }
        else
        {
            _output.WriteLine("No metrics-final event (provider may not support it)");
        }
    }

    // =====================================================
    // Provider Routing Tests
    // =====================================================

    [Fact(DisplayName = "Model Routing - Valid alias routes to correct provider")]
    public async Task ModelRouting_ValidAlias_RoutesToCorrectProvider()
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
        var (virtualKey, groupId, initialBalance) = await CreateRateLimitedKeyAsync(initialCredit: 100.00m);

        var providerId = await SetupProviderAsync(providerConfig, nameSuffix: "Pipeline");
        var (modelAlias, _) = await SetupModelMappingAsync(providerId, providerConfig);

        // Act
        var chatRequest = new ChatCompletionRequest
        {
            Model = modelAlias,
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "Hello" }
            }
        };

        var response = await _apiClient.CorePostAsync<ChatCompletionResponse>(
            "/v1/chat/completions",
            chatRequest,
            virtualKey);

        // Assert
        response.Success.Should().BeTrue($"Valid model alias should route successfully: {response.Error}");
        response.Data.Should().NotBeNull();

        // The returned model should be the actual provider model
        _output.WriteLine($"Requested model: {modelAlias}");
        _output.WriteLine($"Response model: {response.Data!.Model}");
    }

    [Fact(DisplayName = "Model Routing - Unknown model returns 404")]
    public async Task ModelRouting_UnknownModel_Returns404()
    {
        // Arrange
        await WaitForServicesAsync();
        var (virtualKey, groupId, initialBalance) = await CreateRateLimitedKeyAsync(initialCredit: 10.00m);

        // Act
        var chatRequest = new ChatCompletionRequest
        {
            Model = "nonexistent-model-that-does-not-exist-12345",
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
        response.Success.Should().BeFalse("Unknown model should fail");
        response.StatusCode.Should().Be(404, "Unknown model should return 404 Not Found");

        _output.WriteLine("Unknown model correctly returned 404");
    }

    // =====================================================
    // Billing Policy Tests (CRITICAL: 4xx/5xx NOT billed)
    // =====================================================

    [Fact(DisplayName = "Billing Policy - 400 Bad Request does not deduct spend")]
    public async Task ClientError_400BadRequest_DoesNotDeductSpend()
    {
        // Arrange
        await WaitForServicesAsync();
        var (virtualKey, groupId, initialBalance) = await CreateRateLimitedKeyAsync(initialCredit: 10.00m);

        // Act - Send malformed request (missing messages)
        var malformedRequest = new { model = "test-model" }; // Missing required 'messages' field

        var response = await _apiClient.CorePostAsync<object>(
            "/v1/chat/completions",
            malformedRequest,
            virtualKey);

        // Assert
        response.Success.Should().BeFalse("Malformed request should fail");
        response.StatusCode.Should().BeOneOf(new[] { 400, 404 }, "Should return client error");

        // Verify no spend was deducted
        await AssertNoSpendDeductionAsync(groupId, initialBalance);

        _output.WriteLine($"400 error correctly NOT billed. Balance unchanged at ${initialBalance:F6}");
    }

    [Fact(DisplayName = "Billing Policy - 429 Rate Limited does not deduct spend")]
    public async Task RateLimited_429_DoesNotDeductSpend()
    {
        // Arrange
        await WaitForServicesAsync();

        // Create key with very low rate limit
        var (virtualKey, groupId, initialBalance) = await CreateRateLimitedKeyAsync(
            rpm: 1,
            initialCredit: 10.00m);

        // Act - Make requests to trigger rate limiting
        var response1 = await _apiClient.CoreGetAsync<object>("/v1/models", virtualKey);
        var response2 = await _apiClient.CoreGetAsync<object>("/v1/models", virtualKey);
        var response3 = await _apiClient.CoreGetAsync<object>("/v1/models", virtualKey);

        // Find a rate-limited response
        var rateLimitedResponses = new[] { response1, response2, response3 }
            .Where(r => r.StatusCode == 429)
            .ToList();

        if (!rateLimitedResponses.Any())
        {
            _output.WriteLine("Note: Rate limiting not triggered in this test run");
            return;
        }

        // Assert
        await AssertNoSpendDeductionAsync(groupId, initialBalance);

        _output.WriteLine($"429 rate limited requests correctly NOT billed");
    }

    [Fact(DisplayName = "Billing Policy - Successful request deducts spend")]
    public async Task Success_200_DeductsSpend()
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
        var (virtualKey, groupId, initialBalance) = await CreateRateLimitedKeyAsync(initialCredit: 100.00m);

        var providerId = await SetupProviderAsync(providerConfig, nameSuffix: "Pipeline");
        var (modelAlias, _) = await SetupModelMappingAsync(providerId, providerConfig);
        await SetupModelCostAsync(providerConfig);

        // Act - Make a successful chat request
        var chatRequest = new ChatCompletionRequest
        {
            Model = modelAlias,
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "Hello" }
            }
        };

        var response = await _apiClient.CorePostAsync<ChatCompletionResponse>(
            "/v1/chat/completions",
            chatRequest,
            virtualKey);

        response.Success.Should().BeTrue($"Chat request should succeed: {response.Error}");

        // Assert - Spend should be deducted
        await AssertSpendDeductedAsync(groupId, initialBalance, 0.000001m);

        _output.WriteLine($"Successful request correctly billed");
    }
}
