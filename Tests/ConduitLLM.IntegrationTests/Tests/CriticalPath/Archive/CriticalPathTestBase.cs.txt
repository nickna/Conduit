using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using ConduitLLM.IntegrationTests.Core;

namespace ConduitLLM.IntegrationTests.Infrastructure;

/// <summary>
/// Base class for critical path integration tests.
/// Extends ProviderIntegrationTestBase with Redis fixture support and common assertion helpers.
/// </summary>
public abstract class CriticalPathTestBase : IClassFixture<TestFixture>, IClassFixture<RedisTestContainerFixture>
{
    protected readonly TestFixture _fixture;
    protected readonly RedisTestContainerFixture _redisFixture;
    protected readonly ILogger _logger;
    protected readonly ConduitApiClient _apiClient;
    protected readonly TestConfiguration _config;
    protected readonly TestContext _context;

    // Financial precision for billing verification (one-millionth of a dollar)
    protected const decimal BillingTolerance = 0.000001m;

    protected CriticalPathTestBase(TestFixture fixture, RedisTestContainerFixture redisFixture)
    {
        _fixture = fixture;
        _redisFixture = redisFixture;
        _logger = CreateLogger();
        _apiClient = _fixture.ServiceProvider.GetRequiredService<ConduitApiClient>();
        _config = _fixture.Configuration;
        _context = new TestContext();
    }

    protected abstract ILogger CreateLogger();

    /// <summary>
    /// Creates a virtual key group and virtual key with specified rate limits.
    /// Returns the virtual key string for use in API requests.
    /// </summary>
    protected async Task<(string virtualKey, int groupId, decimal initialBalance)> CreateRateLimitedKeyAsync(
        int? rpm = null,
        int? rpd = null,
        decimal initialCredit = 10.00m)
    {
        // Create virtual key group with initial credit
        var createGroupRequest = new CreateVirtualKeyGroupRequest
        {
            GroupName = $"{_config.Defaults.TestPrefix}CriticalPath_{_context.TestRunId}",
            InitialBalance = initialCredit
        };

        var groupResponse = await _apiClient.AdminPostAsync<CreateVirtualKeyGroupResponse>(
            "/v1/admin/virtual-key-groups",
            createGroupRequest);

        groupResponse.Success.Should().BeTrue($"Virtual key group creation should succeed: {groupResponse.Error}");
        var groupId = groupResponse.Data!.Id;
        var balance = groupResponse.Data.Balance;

        // Create virtual key with rate limits
        var createKeyRequest = new CreateVirtualKeyRequest
        {
            KeyName = $"{_config.Defaults.TestPrefix}CriticalPath_Key_{_context.TestRunId}",
            VirtualKeyGroupId = groupId,
            RateLimitRpm = rpm,
            RateLimitRpd = rpd
        };

        var keyResponse = await _apiClient.AdminPostAsync<CreateVirtualKeyResponse>(
            "/v1/admin/virtual-keys",
            createKeyRequest);

        keyResponse.Success.Should().BeTrue($"Virtual key creation should succeed: {keyResponse.Error}");
        var virtualKey = keyResponse.Data!.VirtualKey;

        _logger.LogInformation(
            "Created rate-limited key: GroupId={GroupId}, Balance=${Balance}, RPM={RPM}, RPD={RPD}",
            groupId, balance, rpm, rpd);

        return (virtualKey, groupId, balance);
    }

    /// <summary>
    /// Flushes batch spending updates and returns the updated group balance.
    /// </summary>
    protected async Task<decimal> FlushAndGetBalanceAsync(int groupId)
    {
        // Trigger batch spend flush (parameters are query-string based on the v1 endpoint)
        var flushResponse = await _apiClient.AdminPostAsync<object>(
            $"/v1/admin/batch-spending-jobs/flush?reason={Uri.EscapeDataString("Critical path test balance verification")}&priority=Normal");

        if (!flushResponse.Success)
        {
            _logger.LogWarning("Flush request failed: {Error}, falling back to delay", flushResponse.Error);
            await Task.Delay(3000);
        }
        else
        {
            // Brief delay for async flush to complete
            await Task.Delay(1000);
        }

        // Get updated balance
        var balanceResponse = await _apiClient.AdminGetAsync<CreateVirtualKeyGroupResponse>(
            $"/v1/admin/virtual-key-groups/{groupId}");

        balanceResponse.Success.Should().BeTrue($"Failed to fetch updated balance: {balanceResponse.Error}");
        return balanceResponse.Data!.Balance;
    }

    /// <summary>
    /// Asserts that no spend was deducted from the virtual key group.
    /// Useful for verifying that error responses are not billed.
    /// </summary>
    protected async Task AssertNoSpendDeductionAsync(int groupId, decimal expectedBalance)
    {
        var actualBalance = await FlushAndGetBalanceAsync(groupId);

        actualBalance.Should().Be(
            expectedBalance,
            $"Balance should remain unchanged at ${expectedBalance:F6}, but was ${actualBalance:F6}");

        _logger.LogInformation("Verified no spend deduction: Balance=${Balance}", actualBalance);
    }

    /// <summary>
    /// Asserts that spend was deducted from the virtual key group.
    /// </summary>
    protected async Task AssertSpendDeductedAsync(int groupId, decimal initialBalance, decimal minExpectedDeduction)
    {
        var actualBalance = await FlushAndGetBalanceAsync(groupId);
        var actualDeduction = initialBalance - actualBalance;

        actualDeduction.Should().BeGreaterThanOrEqualTo(
            minExpectedDeduction,
            $"Deduction ${actualDeduction:F6} should be at least ${minExpectedDeduction:F6}");

        _logger.LogInformation(
            "Verified spend deduction: Initial=${Initial}, Current=${Current}, Deducted=${Deducted}",
            initialBalance, actualBalance, actualDeduction);
    }

    /// <summary>
    /// Asserts that the deducted amount matches the expected cost within billing tolerance.
    /// </summary>
    protected async Task AssertExactSpendDeductionAsync(int groupId, decimal initialBalance, decimal expectedDeduction)
    {
        var actualBalance = await FlushAndGetBalanceAsync(groupId);
        var actualDeduction = initialBalance - actualBalance;

        Math.Abs(actualDeduction - expectedDeduction).Should().BeLessThan(
            BillingTolerance,
            $"Billing discrepancy: Expected ${expectedDeduction:F6}, Actually deducted ${actualDeduction:F6}");

        _logger.LogInformation(
            "Verified exact spend: Expected=${Expected}, Actual=${Actual}",
            expectedDeduction, actualDeduction);
    }

    /// <summary>
    /// Waits for all services to be healthy before running tests.
    /// </summary>
    protected async Task WaitForServicesAsync()
    {
        var servicesReady = await TestHelpers.HealthChecks.WaitForServicesAsync(_config, _logger);
        servicesReady.Should().BeTrue("All services should be ready before running tests");
    }

    /// <summary>
    /// Disables a virtual key by updating it via the Admin API.
    /// Filters the key list by group — the unfiltered list is paginated and the
    /// newly created key may not be on the first page.
    /// </summary>
    protected async Task DisableVirtualKeyAsync(int groupId)
    {
        var response = await _apiClient.AdminGetAsync<VirtualKeyListResponse>(
            $"/v1/admin/virtual-keys?virtualKeyGroupId={groupId}");
        response.Success.Should().BeTrue($"Failed to list virtual keys: {response.Error}");

        var keyInfo = response.Data?.Data.FirstOrDefault(k => k.KeyName.Contains(_context.TestRunId));
        keyInfo.Should().NotBeNull($"Expected to find a key for test run {_context.TestRunId} in group {groupId}");

        var updateResponse = await _apiClient.AdminPatchAsync<object>(
            $"/v1/admin/virtual-keys/{keyInfo!.Id}",
            new { isEnabled = false },
            ifMatch: "*");

        updateResponse.Success.Should().BeTrue($"Failed to disable virtual key: {updateResponse.Error}");
        _logger.LogInformation("Disabled virtual key: {KeyId}", keyInfo.Id);
    }

    // =====================================================
    // Provider Setup Helper Methods
    // =====================================================

    /// <summary>
    /// Converts a provider type string to its corresponding enum value.
    /// </summary>
    protected static int GetProviderTypeEnum(string providerType)
    {
        return providerType.ToLower() switch
        {
            "openai" => 1,
            "groq" => 2,
            "replicate" => 3,
            "fireworks" => 4,
            "openaicompatible" => 5,
            "minimax" => 6,
            "ultravox" => 7,
            "elevenlabs" => 8,
            "cerebras" => 9,
            "sambanova" => 10,
            "deepinfra" => 11,
            _ => throw new InvalidOperationException($"Unknown provider type: {providerType}")
        };
    }

    /// <summary>
    /// Creates a provider with API key credentials.
    /// </summary>
    /// <param name="providerConfig">The provider configuration.</param>
    /// <param name="overrideApiKey">Optional API key override (e.g., for testing invalid keys).</param>
    /// <param name="nameSuffix">Optional suffix for the provider name (defaults to "Test").</param>
    /// <returns>The created provider's ID.</returns>
    protected async Task<int> SetupProviderAsync(
        ProviderConfig providerConfig,
        string? overrideApiKey = null,
        string? nameSuffix = null)
    {
        var suffix = nameSuffix ?? "Test";

        var createProviderRequest = new CreateProviderRequest
        {
            ProviderName = $"{_config.Defaults.TestPrefix}{suffix}_{_context.TestRunId}",
            ProviderType = providerConfig.Provider.Type,
            BaseUrl = providerConfig.Provider.BaseUrl,
            IsEnabled = true
        };

        var providerResponse = await _apiClient.AdminPostAsync<CreateProviderResponse>(
            "/v1/admin/providers",
            createProviderRequest);
        providerResponse.Success.Should().BeTrue($"Provider creation failed: {providerResponse.Error}");

        // Add provider key (use override if provided, otherwise use config)
        var apiKey = overrideApiKey ?? providerConfig.Provider.ApiKey;
        var createKeyRequest = new CreateProviderKeyRequest
        {
            ApiKey = apiKey,
            KeyName = $"{_config.Defaults.TestPrefix}Key_{_context.TestRunId}",
            IsPrimary = true
        };

        var keyResponse = await _apiClient.AdminPostAsync<CreateProviderKeyResponse>(
            $"/v1/admin/providers/{providerResponse.Data!.Id}/keys",
            createKeyRequest);
        keyResponse.Success.Should().BeTrue($"Key creation failed: {keyResponse.Error}");

        _logger.LogInformation("Created provider: Id={ProviderId}, Name={Name}",
            providerResponse.Data.Id, createProviderRequest.ProviderName);

        return providerResponse.Data.Id;
    }

    /// <summary>
    /// Creates a model mapping for a provider.
    /// Stores the mapping ID and alias in _context for use by other methods.
    /// </summary>
    /// <param name="providerId">The provider ID to map to.</param>
    /// <param name="providerConfig">The provider configuration containing model info.</param>
    /// <returns>The model alias and mapping ID.</returns>
    protected async Task<(string modelAlias, int mappingId)> SetupModelMappingAsync(
        int providerId,
        ProviderConfig providerConfig)
    {
        var modelConfig = providerConfig.Models[0];
        var modelAlias = $"{modelConfig.Alias}_{_context.TestRunId}";

        // The mapping must reference a model catalog association; resolve it first
        _context.ModelProviderTypeAssociationId ??= await ModelCatalogSetup.ResolveAssociationAsync(
            _apiClient,
            providerId,
            modelAlias,
            modelConfig.Actual,
            _logger);

        var createMappingRequest = new CreateModelMappingRequest
        {
            ModelAlias = modelAlias,
            ProviderId = providerId,
            ProviderModelId = modelConfig.Actual,
            ModelProviderTypeAssociationId = _context.ModelProviderTypeAssociationId.Value
        };

        var mappingResponse = await _apiClient.AdminPostAsync<CreateModelMappingResponse>(
            "/v1/admin/model-provider-mappings",
            createMappingRequest);
        mappingResponse.Success.Should().BeTrue($"Model mapping failed: {mappingResponse.Error}");

        // Store in context for other methods
        _context.ModelMappingId = mappingResponse.Data!.Id;
        _context.ModelAlias = modelAlias;

        _logger.LogInformation("Created model mapping: Alias={Alias}, MappingId={MappingId}",
            modelAlias, mappingResponse.Data.Id);

        return (modelAlias, mappingResponse.Data.Id);
    }

    /// <summary>
    /// Creates a model cost configuration.
    /// Requires SetupModelMappingAsync to have been called first.
    /// </summary>
    protected async Task SetupModelCostAsync(ProviderConfig providerConfig)
    {
        if (_context.ModelMappingId == null || _context.ModelAlias == null)
        {
            throw new InvalidOperationException("SetupModelMappingAsync must be called before SetupModelCostAsync");
        }

        var modelConfig = providerConfig.Models[0];

        var createCostRequest = new CreateModelCostRequest
        {
            CostName = $"{_context.ModelAlias}_cost",
            ModelProviderTypeAssociationIds = new List<int> { _context.ModelProviderTypeAssociationId!.Value },
            InputCostPerMillionTokens = modelConfig.Cost.InputPerMillion,
            OutputCostPerMillionTokens = modelConfig.Cost.OutputPerMillion
        };

        var costResponse = await _apiClient.AdminPostAsync<CreateModelCostResponse>(
            "/v1/admin/model-costs",
            createCostRequest);
        costResponse.Success.Should().BeTrue($"Model cost creation failed: {costResponse.Error}");

        _logger.LogInformation("Created model cost: Name={CostName}", createCostRequest.CostName);
    }

    /// <summary>
    /// Disables a provider by ID.
    /// </summary>
    protected async Task DisableProviderAsync(int providerId)
    {
        var updateResponse = await _apiClient.AdminPatchAsync<object>(
            $"/v1/admin/providers/{providerId}",
            new { isEnabled = false });

        updateResponse.Success.Should().BeTrue($"Failed to disable provider: {updateResponse.Error}");
        _logger.LogInformation("Disabled provider: {ProviderId}", providerId);
    }

    /// <summary>
    /// Disables a model mapping by ID.
    /// </summary>
    protected async Task DisableModelMappingAsync(int mappingId)
    {
        var updateResponse = await _apiClient.AdminPatchAsync<object>(
            $"/v1/admin/model-provider-mappings/{mappingId}",
            new { isEnabled = false });

        updateResponse.Success.Should().BeTrue($"Failed to disable mapping: {updateResponse.Error}");
        _logger.LogInformation("Disabled model mapping: {MappingId}", mappingId);
    }
}

/// <summary>
/// DTO for listing virtual keys. The v1 endpoint wraps the list in a {data: [...]} envelope.
/// </summary>
public class VirtualKeyListResponse
{
    public List<VirtualKeyListItem> Data { get; set; } = new();
}

public class VirtualKeyListItem
{
    public int Id { get; set; }
    public string KeyName { get; set; } = "";
    public bool IsEnabled { get; set; }
    public int VirtualKeyGroupId { get; set; }
}

/// <summary>
/// xUnit collection definition for critical path tests that share Redis.
/// </summary>
[CollectionDefinition("Critical Path")]
public class CriticalPathCollection : ICollectionFixture<RedisTestContainerFixture>, ICollectionFixture<TestFixture>
{
    // This class has no code - it's used to wire up fixtures with xUnit collection
}
