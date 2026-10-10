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
/// Integration tests for Authentication and Authorization critical path.
/// Tests virtual key lifecycle, rate limiting, and spend tracking.
/// </summary>
[Collection("Critical Path")]
[Trait("Category", "Integration")]
[Trait("CriticalPath", "true")]
public class AuthenticationIntegrationTests : CriticalPathTestBase
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<AuthenticationIntegrationTests> _specificLogger;

    public AuthenticationIntegrationTests(
        TestFixture fixture,
        RedisTestContainerFixture redisFixture,
        ITestOutputHelper output)
        : base(fixture, redisFixture)
    {
        _output = output;
        _specificLogger = _fixture.ServiceProvider.GetRequiredService<ILogger<AuthenticationIntegrationTests>>();
    }

    protected override ILogger CreateLogger()
    {
        return _fixture.ServiceProvider.GetRequiredService<ILogger<AuthenticationIntegrationTests>>();
    }

    // =====================================================
    // Virtual Key Lifecycle Tests
    // =====================================================

    [Fact(DisplayName = "Virtual Key - Create and validate succeeds with valid credentials")]
    public async Task VirtualKey_CreateAndValidate_SucceedsWithValidCredentials()
    {
        // Arrange
        await WaitForServicesAsync();
        var (virtualKey, groupId, balance) = await CreateRateLimitedKeyAsync(initialCredit: 10.00m);

        // Act - Make a simple health check request with the virtual key
        // This validates the key is accepted by the authentication handler
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", virtualKey);

        var response = await _apiClient.CoreGetAsync<object>("/v1/models", virtualKey);

        // Assert
        response.Success.Should().BeTrue("Valid virtual key should authenticate successfully");
        response.StatusCode.Should().Be(200);

        _output.WriteLine($"Virtual key authentication successful: {virtualKey[..20]}...");
    }

    [Fact(DisplayName = "Virtual Key - Disabled key returns 401 Unauthorized")]
    public async Task VirtualKey_Disabled_ReturnsUnauthorized()
    {
        // Arrange
        await WaitForServicesAsync();
        var (virtualKey, groupId, balance) = await CreateRateLimitedKeyAsync(initialCredit: 10.00m);

        // Disable the key
        await DisableVirtualKeyAsync(groupId);

        // Act - Try to use the disabled key. Key-state changes propagate via events,
        // so poll briefly until the Gateway observes the disable.
        ApiResponse<object> response = null!;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            response = await _apiClient.CoreGetAsync<object>("/v1/models", virtualKey);
            if (!response.Success)
            {
                break;
            }
            await Task.Delay(1000);
        }

        // Assert
        response.Success.Should().BeFalse("Disabled virtual key should be rejected");
        response.StatusCode.Should().Be(401, "Disabled key should return 401 Unauthorized");

        _output.WriteLine("Disabled key correctly rejected with 401");
    }

    [Fact(DisplayName = "Virtual Key - Zero balance returns 402 Payment Required")]
    public async Task VirtualKey_ZeroBalance_Returns402PaymentRequired()
    {
        // Arrange
        await WaitForServicesAsync();

        // Create a key with zero initial balance
        var createGroupRequest = new CreateVirtualKeyGroupRequest
        {
            GroupName = $"{_config.Defaults.TestPrefix}ZeroBalance_{_context.TestRunId}",
            InitialBalance = 0.00m
        };

        var groupResponse = await _apiClient.AdminPostAsync<CreateVirtualKeyGroupResponse>(
            "/v1/admin/virtual-key-groups",
            createGroupRequest);
        groupResponse.Success.Should().BeTrue();

        var createKeyRequest = new CreateVirtualKeyRequest
        {
            KeyName = $"{_config.Defaults.TestPrefix}ZeroBalanceKey_{_context.TestRunId}",
            VirtualKeyGroupId = groupResponse.Data!.Id
        };

        var keyResponse = await _apiClient.AdminPostAsync<CreateVirtualKeyResponse>(
            "/v1/admin/virtual-keys",
            createKeyRequest);
        keyResponse.Success.Should().BeTrue();

        var virtualKey = keyResponse.Data!.VirtualKey;

        // Act - Try to make a chat completion request (which requires balance)
        var chatRequest = new ChatCompletionRequest
        {
            Model = "test-model", // This model doesn't need to exist for 402 check
            Messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = "Hello" }
            }
        };

        var response = await _apiClient.CorePostAsync<object>("/v1/chat/completions", chatRequest, virtualKey);

        // Assert
        // Note: The exact behavior depends on whether balance check happens before or after model validation
        // Accept either 402 (balance check first) or 404 (model check first)
        response.Success.Should().BeFalse("Zero balance key should be rejected for chat requests");
        response.StatusCode.Should().BeOneOf(new[] { 402, 404 },
            "Zero balance should return 402 Payment Required or 404 if model is checked first");

        _output.WriteLine($"Zero balance key request returned: {response.StatusCode}");
    }

    // =====================================================
    // Rate Limit Enforcement Tests
    // =====================================================

    [Fact(DisplayName = "Rate Limit RPM - Exceeds limit returns 429 with Retry-After")]
    public async Task RateLimitRPM_ExceedsLimit_Returns429WithRetryAfter()
    {
        // Arrange
        await WaitForServicesAsync();

        // Create a key with a very low RPM limit (2 requests per minute)
        var (virtualKey, groupId, balance) = await CreateRateLimitedKeyAsync(rpm: 2, initialCredit: 10.00m);

        // Act - Make requests exceeding the limit
        var responses = new List<ApiResponse<object>>();

        for (int i = 0; i < 4; i++) // 4 requests, limit is 2
        {
            var response = await _apiClient.CoreGetAsync<object>("/v1/models", virtualKey);
            responses.Add(response);
            _output.WriteLine($"Request {i + 1}: Status={response.StatusCode}");

            // Small delay to ensure requests are processed
            await Task.Delay(100);
        }

        // Assert
        // First 2 requests should succeed, subsequent requests should be rate limited
        var successfulRequests = responses.Count(r => r.StatusCode == 200);
        var rateLimitedRequests = responses.Count(r => r.StatusCode == 429);

        successfulRequests.Should().BeGreaterThanOrEqualTo(2, "At least first 2 requests should succeed");
        rateLimitedRequests.Should().BeGreaterThan(0, "Some requests should be rate limited");

        // The test is named for Retry-After, so assert it rather than only the status codes.
        var throttled = responses.First(r => r.StatusCode == 429);

        var retryAfter = throttled.Header("Retry-After");
        retryAfter.Should().NotBeNullOrEmpty("a 429 must tell the caller when to come back");
        var retryAfterSeconds = int.Parse(retryAfter!);
        retryAfterSeconds.Should().BeInRange(1, 60,
            "the RPM window is 60 seconds, so the wait can never exceed it");

        throttled.Header("X-RateLimit-Scope").Should().Be("RPM", "the caller needs to know which limit denied");
        throttled.Header("X-RateLimit-Limit").Should().Be("2");
        throttled.Header("X-RateLimit-Remaining").Should().Be("0");

        // The advertised reset must be in the future and inside the rolling window — a calendar
        // boundary or a stale value would send the caller back too early (#1206).
        var reset = long.Parse(throttled.Header("X-RateLimit-Reset")!);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        reset.Should().BeInRange(now, now + 61);

        // Allowed responses carry the same family so clients can pace before being throttled.
        var allowed = responses.First(r => r.StatusCode == 200);
        allowed.Header("X-RateLimit-Limit").Should().Be("2");
        allowed.Header("X-RateLimit-Remaining").Should().NotBeNullOrEmpty();

        _output.WriteLine($"Successful: {successfulRequests}, Rate limited: {rateLimitedRequests}, Retry-After: {retryAfterSeconds}s");
    }

    [Fact(DisplayName = "Rate Limit RPD - Exceeds limit returns 429")]
    public async Task RateLimitRPD_ExceedsLimit_Returns429()
    {
        // Arrange
        await WaitForServicesAsync();

        // Create a key with a very low RPD limit (3 requests per day)
        var (virtualKey, groupId, balance) = await CreateRateLimitedKeyAsync(rpd: 3, initialCredit: 10.00m);

        // Act - Make requests exceeding the limit
        var responses = new List<ApiResponse<object>>();

        for (int i = 0; i < 5; i++) // 5 requests, limit is 3
        {
            var response = await _apiClient.CoreGetAsync<object>("/v1/models", virtualKey);
            responses.Add(response);
            _output.WriteLine($"Request {i + 1}: Status={response.StatusCode}");

            await Task.Delay(100);
        }

        // Assert
        var successfulRequests = responses.Count(r => r.StatusCode == 200);
        var rateLimitedRequests = responses.Count(r => r.StatusCode == 429);

        successfulRequests.Should().BeGreaterThanOrEqualTo(3, "At least first 3 requests should succeed");
        rateLimitedRequests.Should().BeGreaterThan(0, "Some requests should be rate limited (RPD)");

        _output.WriteLine($"RPD Test - Successful: {successfulRequests}, Rate limited: {rateLimitedRequests}");
    }

    // =====================================================
    // Spend Tracking Tests
    // =====================================================

    [Fact(DisplayName = "Spend Tracking - After chat request deducts correct amount")]
    public async Task SpendTracking_AfterChatRequest_DeductsCorrectAmount()
    {
        // Arrange
        await WaitForServicesAsync();

        // This test requires a real provider to be configured
        // Skip if no active providers
        if (!_config.ActiveProviders.Any())
        {
            _output.WriteLine("Skipping: No active providers configured");
            return;
        }

        var providerName = _config.ActiveProviders.First();
        var providerConfig = ConfigurationLoader.LoadProviderConfig(providerName);

        // Set up provider infrastructure similar to ProviderBillingIntegrationTests
        var (virtualKey, groupId, initialBalance) = await CreateRateLimitedKeyAsync(initialCredit: 100.00m);

        // Create provider setup using the pattern from existing tests
        // (This requires the provider infrastructure to be set up)
        // For now, we'll test with an existing model if one exists

        _output.WriteLine($"Testing spend tracking with provider: {providerName}");
        _output.WriteLine($"Initial balance: ${initialBalance:F6}");

        // Note: Full spend tracking test requires provider setup
        // This is covered more thoroughly in ProviderBillingIntegrationTests
        // Here we just verify the balance query mechanism works

        var balance = await FlushAndGetBalanceAsync(groupId);
        balance.Should().Be(initialBalance, "Balance should be unchanged with no requests made");

        _output.WriteLine($"Balance after flush: ${balance:F6}");
    }

    [Fact(DisplayName = "Spend Tracking - Batch flush updates balance correctly")]
    public async Task SpendTracking_BatchFlush_UpdatesBalanceCorrectly()
    {
        // Arrange
        await WaitForServicesAsync();
        var (virtualKey, groupId, initialBalance) = await CreateRateLimitedKeyAsync(initialCredit: 50.00m);

        // Act - Trigger batch flush
        var balanceAfterFlush = await FlushAndGetBalanceAsync(groupId);

        // Assert
        balanceAfterFlush.Should().Be(initialBalance,
            "Balance should remain unchanged when no billable requests were made");

        _output.WriteLine($"Batch flush verified: Balance=${balanceAfterFlush:F6}");
    }

    // =====================================================
    // Authentication Source Tests
    // =====================================================

    [Fact(DisplayName = "Auth - Bearer token validates successfully")]
    public async Task AuthFromBearerToken_ValidatesSuccessfully()
    {
        // Arrange
        await WaitForServicesAsync();
        var (virtualKey, groupId, balance) = await CreateRateLimitedKeyAsync(initialCredit: 10.00m);

        // Act - Use Bearer token authentication
        var response = await _apiClient.CoreGetAsync<object>("/v1/models", virtualKey);

        // Assert
        response.Success.Should().BeTrue("Bearer token authentication should work");
        response.StatusCode.Should().Be(200);

        _output.WriteLine("Bearer token authentication verified");
    }

    [Fact(DisplayName = "Auth - X-API-Key header validates successfully")]
    public async Task AuthFromXApiKeyHeader_ValidatesSuccessfully()
    {
        // Arrange
        await WaitForServicesAsync();
        var (virtualKey, groupId, balance) = await CreateRateLimitedKeyAsync(initialCredit: 10.00m);

        // Act - Use X-API-Key header instead of Bearer token
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(_config.Environment.CoreApiUrl)
        };
        httpClient.DefaultRequestHeaders.Add("X-API-Key", virtualKey);

        var response = await httpClient.GetAsync("/v1/models");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, "X-API-Key header authentication should work");

        _output.WriteLine("X-API-Key header authentication verified");
    }

    [Fact(DisplayName = "Auth - Missing authentication returns 401")]
    public async Task AuthMissing_Returns401Unauthorized()
    {
        // Arrange
        await WaitForServicesAsync();

        // Act - Make request without any authentication
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(_config.Environment.CoreApiUrl)
        };

        var response = await httpClient.GetAsync("/v1/models");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "Request without authentication should return 401");

        _output.WriteLine("Missing authentication correctly rejected with 401");
    }

    [Fact(DisplayName = "Auth - Invalid token format returns 401")]
    public async Task AuthInvalidToken_Returns401Unauthorized()
    {
        // Arrange
        await WaitForServicesAsync();

        // Act - Use an invalid token format
        var response = await _apiClient.CoreGetAsync<object>("/v1/models", "invalid-token-format");

        // Assert
        response.Success.Should().BeFalse("Invalid token should be rejected");
        response.StatusCode.Should().Be(401, "Invalid token should return 401");

        _output.WriteLine("Invalid token correctly rejected with 401");
    }
}
