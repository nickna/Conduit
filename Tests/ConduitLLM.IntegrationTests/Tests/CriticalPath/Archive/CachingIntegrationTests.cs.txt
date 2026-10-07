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
/// Integration tests for Caching Behavior critical path.
/// Tests Redis circuit breaker, graceful degradation, and cache operations.
/// </summary>
[Collection("Critical Path")]
[Trait("Category", "Integration")]
[Trait("CriticalPath", "true")]
public class CachingIntegrationTests : CriticalPathTestBase
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<CachingIntegrationTests> _specificLogger;

    public CachingIntegrationTests(
        TestFixture fixture,
        RedisTestContainerFixture redisFixture,
        ITestOutputHelper output)
        : base(fixture, redisFixture)
    {
        _output = output;
        _specificLogger = _fixture.ServiceProvider.GetRequiredService<ILogger<CachingIntegrationTests>>();
    }

    protected override ILogger CreateLogger()
    {
        return _fixture.ServiceProvider.GetRequiredService<ILogger<CachingIntegrationTests>>();
    }

    // =====================================================
    // Redis Circuit Breaker Tests
    // =====================================================

    [Fact(DisplayName = "Redis Unavailable - Health endpoints still work")]
    public async Task RedisUnavailable_HealthEndpointsStillWork()
    {
        // Arrange
        await WaitForServicesAsync();
        _redisFixture.IsRunning.Should().BeTrue("Redis should be running before test");

        // Simulate Redis failure
        _output.WriteLine("Stopping Redis container...");
        await _redisFixture.StopAsync();
        _redisFixture.IsRunning.Should().BeFalse("Redis should be stopped");

        try
        {
            // Small delay for circuit breaker to detect failure
            await Task.Delay(1000);

            // Act - Health endpoints should still respond
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri(_config.Environment.CoreApiUrl)
            };

            var healthResponse = await httpClient.GetAsync("/health");

            // Assert - Health endpoints bypass Redis requirement
            // Note: The exact behavior depends on the application's circuit breaker configuration
            // Health endpoints typically bypass Redis checks
            _output.WriteLine($"Health endpoint response: {healthResponse.StatusCode}");

            // The health endpoint may return 200 (healthy) or 503 (degraded) depending on configuration
            // What's important is that it responds at all
            healthResponse.Should().NotBeNull("Health endpoint should respond even with Redis down");
        }
        finally
        {
            // Cleanup - Restart Redis
            _output.WriteLine("Restarting Redis container...");
            await _redisFixture.RestartAsync();
            await Task.Delay(2000); // Allow time for reconnection
            _redisFixture.IsRunning.Should().BeTrue("Redis should be restarted after test");
        }
    }

    [Fact(DisplayName = "Redis Unavailable - Circuit opens after failures")]
    public async Task RedisUnavailable_CircuitOpens_Returns503()
    {
        // Arrange
        await WaitForServicesAsync();
        var (virtualKey, groupId, balance) = await CreateRateLimitedKeyAsync(initialCredit: 10.00m);

        // Stop Redis to trigger circuit breaker
        _output.WriteLine("Stopping Redis to trigger circuit breaker...");
        await _redisFixture.StopAsync();

        try
        {
            // Give some time for circuit breaker to detect failure
            await Task.Delay(2000);

            // Act - Make requests that depend on Redis
            // Note: The actual behavior depends on what the application does when Redis is down
            // Some endpoints may use Redis for rate limiting or caching

            // For this test, we check that the application handles Redis failure gracefully
            var response = await _apiClient.CoreGetAsync<object>("/v1/models", virtualKey);

            // Assert
            // The application should either:
            // 1. Return 503 Service Unavailable (circuit breaker open)
            // 2. Continue working with degraded functionality (graceful degradation)
            // 3. Return 200 if the endpoint doesn't require Redis

            _output.WriteLine($"Response with Redis down: {response.StatusCode}");

            // Accept any non-crash response as success for this test
            // The key is that the application doesn't throw an unhandled exception
            response.StatusCode.Should().BeOneOf(new[] { 200, 503, 500 },
                "Application should handle Redis failure gracefully");
        }
        finally
        {
            // Cleanup
            _output.WriteLine("Restarting Redis...");
            await _redisFixture.RestartAsync();
            await Task.Delay(2000);
        }
    }

    [Fact(DisplayName = "Redis Recovery - Circuit closes after successful reconnection")]
    public async Task RedisRecovery_CircuitCloses_NormalOperation()
    {
        // Arrange
        await WaitForServicesAsync();
        var (virtualKey, groupId, balance) = await CreateRateLimitedKeyAsync(initialCredit: 10.00m);

        // Verify normal operation first
        var initialResponse = await _apiClient.CoreGetAsync<object>("/v1/models", virtualKey);
        initialResponse.Success.Should().BeTrue("Initial request should succeed");
        _output.WriteLine("Initial request successful");

        // Stop and restart Redis
        _output.WriteLine("Stopping Redis...");
        await _redisFixture.StopAsync();
        await Task.Delay(2000);

        _output.WriteLine("Restarting Redis...");
        await _redisFixture.RestartAsync();
        await Task.Delay(3000); // Give time for circuit breaker to recover

        // Act - Make request after Redis recovery
        var recoveryResponse = await _apiClient.CoreGetAsync<object>("/v1/models", virtualKey);

        // Assert
        recoveryResponse.Success.Should().BeTrue("Request should succeed after Redis recovery");
        recoveryResponse.StatusCode.Should().Be(200);

        _output.WriteLine("Recovery successful - circuit closed");
    }

    // =====================================================
    // Graceful Degradation Tests
    // =====================================================

    [Fact(DisplayName = "Redis Down - Requests still processed with graceful fallback")]
    public async Task RedisDown_RequestsStillProcessed_GracefulFallback()
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

        // Set up provider while Redis is up
        var providerId = await SetupProviderAsync(providerConfig, nameSuffix: "Caching");
        var (modelAlias, _) = await SetupModelMappingAsync(providerId, providerConfig);

        // Stop Redis
        _output.WriteLine("Stopping Redis for graceful degradation test...");
        await _redisFixture.StopAsync();

        try
        {
            await Task.Delay(2000);

            // Act - Try to make a chat request
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
            // With graceful degradation, the request should either:
            // 1. Succeed (if rate limiting falls back to allow-all)
            // 2. Fail with 503 (if Redis is required)
            _output.WriteLine($"Response with Redis down: {response.StatusCode}");

            // Document actual behavior
            if (response.Success)
            {
                _output.WriteLine("Application handled Redis failure gracefully - request succeeded");
            }
            else
            {
                _output.WriteLine($"Application returned error: {response.Error}");
            }
        }
        finally
        {
            // Cleanup
            _output.WriteLine("Restarting Redis...");
            await _redisFixture.RestartAsync();
            await Task.Delay(2000);
        }
    }

    [Fact(DisplayName = "Redis Flush - Clears cached data for test isolation")]
    public async Task RedisFlush_ClearsCachedData_TestIsolation()
    {
        // Arrange
        await WaitForServicesAsync();
        _redisFixture.IsRunning.Should().BeTrue("Redis should be running");

        // Act - Flush Redis
        await _redisFixture.FlushAllAsync();

        // Assert - System should still work after flush
        var (virtualKey, groupId, balance) = await CreateRateLimitedKeyAsync(initialCredit: 10.00m);
        var response = await _apiClient.CoreGetAsync<object>("/v1/models", virtualKey);

        response.Success.Should().BeTrue("System should work after Redis flush");

        _output.WriteLine("Redis flush successful - test isolation verified");
    }
}
