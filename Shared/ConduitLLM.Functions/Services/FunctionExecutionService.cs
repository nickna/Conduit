using System.Diagnostics;
using System.Text.Json;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Security;
using ConduitLLM.Functions.Serialization;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Functions.Services;

/// <summary>
/// Service for executing functions.
/// </summary>
/// <remarks>
/// This service orchestrates function execution:
/// 1. Load function configuration and credentials
/// 2. Create execution record
/// 3. Execute function via provider client
/// 4. Calculate actual usage and cost
/// 5. Update execution record with results
///
/// Billing is handled by the calling layer (middleware/controller).
/// </remarks>
public class FunctionExecutionService : IFunctionExecutionService
{
    private readonly IFunctionConfigurationRepository _functionConfigurationRepository;
    private readonly IFunctionCredentialRepository _credentialRepository;
    private readonly IFunctionExecutionRepository _executionRepository;
    private readonly IFunctionCostCalculationService _costCalculationService;
    private readonly IFunctionClientFactory _clientFactory;
    private readonly IFunctionCredentialProtector _credentialProtector;
    private readonly ILogger<FunctionExecutionService> _logger;

    public FunctionExecutionService(
        IFunctionConfigurationRepository functionConfigurationRepository,
        IFunctionCredentialRepository credentialRepository,
        IFunctionExecutionRepository executionRepository,
        IFunctionCostCalculationService costCalculationService,
        IFunctionClientFactory clientFactory,
        IFunctionCredentialProtector credentialProtector,
        ILogger<FunctionExecutionService> logger)
    {
        _functionConfigurationRepository = functionConfigurationRepository ?? throw new ArgumentNullException(nameof(functionConfigurationRepository));
        _credentialRepository = credentialRepository ?? throw new ArgumentNullException(nameof(credentialRepository));
        _executionRepository = executionRepository ?? throw new ArgumentNullException(nameof(executionRepository));
        _costCalculationService = costCalculationService ?? throw new ArgumentNullException(nameof(costCalculationService));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _credentialProtector = credentialProtector ?? throw new ArgumentNullException(nameof(credentialProtector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<FunctionExecution> ExecuteAsync(
        int functionConfigurationId,
        int virtualKeyId,
        Dictionary<string, object> parameters,
        string? idempotencyKey = null,
        Dictionary<string, object>? metadata = null,
        string? providerToolName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var stopwatch = Stopwatch.StartNew();
        FunctionExecution? execution = null;
        var providerExecutionCompleted = false;
        decimal? incurredCost = null;

        try
        {
            // 1. Load function configuration
            var configuration = await _functionConfigurationRepository.GetByIdAsync(functionConfigurationId, cancellationToken);
            if (configuration == null)
            {
                throw new InvalidOperationException($"Function configuration {functionConfigurationId} not found");
            }

             if (!configuration.IsEnabled)
            {
                throw new InvalidOperationException($"Function configuration {functionConfigurationId} is not enabled");
            }

            _logger.LogInformation("Executing function {ConfigName} (Config: {ConfigId}) for VirtualKey {VirtualKeyId}",
                configuration.ConfigurationName, functionConfigurationId, virtualKeyId);

            // 2. Estimate cost conservatively (for logging and execution record)
            var estimatedCost = await EstimateCostForConfigAsync(configuration, functionConfigurationId, parameters, cancellationToken);

            _logger.LogDebug("Estimated cost for function {ConfigName}: ${EstimatedCost:F4}",
                configuration.ConfigurationName, estimatedCost);

            // 3. Create execution record
            var requestData = new FunctionExecutionRequestData
            {
                Parameters = parameters,
                Metadata = metadata,
                IdempotencyKey = idempotencyKey
            };

            execution = new FunctionExecution
            {
                Id = Guid.NewGuid(),
                FunctionConfigurationId = functionConfigurationId,
                VirtualKeyId = virtualKeyId,
                ExecutionMode = configuration.DefaultExecutionMode,
                State = ExecutionState.Running,
                RequestedAt = DateTime.UtcNow,
                StartedAt = DateTime.UtcNow,
                RequestJson = JsonSerializer.Serialize(
                    requestData,
                    FunctionsJsonContext.Default.FunctionExecutionRequestData),
                EstimatedCost = estimatedCost,
                RetryCount = 0,
                Version = 1,
                WebhookDelivered = false
            };

            await _executionRepository.CreateAsync(execution, cancellationToken);

            _logger.LogInformation("Created execution record {ExecutionId} for function {ConfigName}",
                execution.Id, configuration.ConfigurationName);

            // 4. Get credentials for this provider type
            var credentials = await _credentialRepository.GetByProviderTypeAsync(
                configuration.ProviderType, cancellationToken);

            if (!credentials.Any())
            {
                throw new InvalidOperationException(
                    $"No credentials configured for provider type {configuration.ProviderType}");
            }

            // Prefer a config-scoped credential (MCP each server has its own token), falling back
            // to a provider-global credential (the Exa/Tavily model).
            var credential = CredentialSelector.SelectForConfiguration(credentials, functionConfigurationId);
            if (credential == null)
            {
                throw new InvalidOperationException(
                    $"No enabled credentials found for provider type {configuration.ProviderType}");
            }

            // Reveal the (possibly encrypted) secret before use.
            var apiKey = _credentialProtector.Reveal(credential.ApiKey);

            // 5. Execute function via provider client
            var client = await _clientFactory.GetClientAsync(configuration.ProviderType, functionConfigurationId);

            _logger.LogInformation("Executing {ProviderType} function via client...",
                configuration.ProviderType);

            // For dynamic multi-tool providers (MCP), thread the target tool name to the client via
            // a reserved parameter key without disturbing the model-supplied arguments used for
            // usage calculation.
            var executeParameters = parameters;
            if (!string.IsNullOrWhiteSpace(providerToolName))
            {
                executeParameters = new Dictionary<string, object>(parameters)
                {
                    [McpReservedParameterKeys.ToolName] = providerToolName
                };
            }

            var result = await client.ExecuteAsync(executeParameters, apiKey, cancellationToken);

            stopwatch.Stop();
            providerExecutionCompleted = true;

            // Preserve the provider result before cost calculation/persistence. Any failure after
            // this point is internal bookkeeping; the external call has already been incurred.
            execution.State = result.IsSuccess ? ExecutionState.Completed : ExecutionState.Failed;
            execution.CompletedAt = DateTime.UtcNow;
            execution.Duration = stopwatch.Elapsed;
            execution.ResponseJson = result.ResponseJson;
            execution.ErrorMessage = result.ErrorMessage;

            // 6. Calculate actual usage and cost
            var usage = client.CalculateUsageFromResponse(parameters, result);
            var actualCost = await CalculateCostForConfigAsync(
                configuration, functionConfigurationId, usage, cancellationToken);
            incurredCost = actualCost;

            _logger.LogInformation("Function execution completed. Estimated: ${Estimated:F4}, Actual: ${Actual:F4}, Duration: {Duration}ms",
                estimatedCost, actualCost, stopwatch.ElapsedMilliseconds);

            // 7. Update execution record with results
            var costDetails = new FunctionExecutionCostDetails
            {
                Usage = usage,
                EstimatedCost = estimatedCost,
                ActualCost = actualCost,
                HttpStatusCode = result.HttpStatusCode
            };

            execution.CostCalculationDetails = JsonSerializer.Serialize(
                costDetails,
                FunctionsJsonContext.Default.FunctionExecutionCostDetails);
            execution.ActualCost = actualCost;

            await _executionRepository.UpdateAsync(execution, cancellationToken);

            return execution;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            _logger.LogError(ex, "Error executing function {ConfigId}: {Error}",
                functionConfigurationId, ex.Message);

            // Create/update execution record with error if we can
            try
            {
                if (execution == null)
                {
                    // Create failed execution record
                    var requestData = new FunctionExecutionRequestData
                    {
                        Parameters = parameters,
                        Metadata = metadata,
                        IdempotencyKey = idempotencyKey
                    };

                    execution = new FunctionExecution
                    {
                        Id = Guid.NewGuid(),
                        FunctionConfigurationId = functionConfigurationId,
                        VirtualKeyId = virtualKeyId,
                        ExecutionMode = ExecutionMode.Synchronous, // Default for error case
                        State = ExecutionState.Failed,
                        RequestedAt = DateTime.UtcNow,
                        CompletedAt = DateTime.UtcNow,
                        Duration = stopwatch.Elapsed,
                        RequestJson = JsonSerializer.Serialize(
                            requestData,
                            FunctionsJsonContext.Default.FunctionExecutionRequestData),
                        ActualCost = 0m,
                        ErrorMessage = ex.Message,
                        RetryCount = 0,
                        Version = 1,
                        WebhookDelivered = false
                    };

                    await _executionRepository.CreateAsync(execution, cancellationToken);
                }
                else
                {
                    // Update existing execution with error
                    execution.State = ExecutionState.Failed;
                    execution.CompletedAt = DateTime.UtcNow;
                    execution.Duration = stopwatch.Elapsed;
                    execution.ErrorMessage = ex.Message;
                    execution.ActualCost = providerExecutionCompleted
                        ? incurredCost ?? execution.ActualCost ?? execution.EstimatedCost ?? 0m
                        : 0m;

                    await _executionRepository.UpdateAsync(execution, cancellationToken);
                }
            }
            catch (Exception recordEx)
            {
                _logger.LogError(recordEx, "Failed to record execution error for function {ConfigId}",
                    functionConfigurationId);
            }

            if (providerExecutionCompleted && execution != null)
            {
                // Return a failed, billable execution so callers can charge the incurred provider
                // cost even when usage calculation or persistence failed after provider success.
                return execution;
            }

            throw;
        }
    }

    /// <summary>
    /// Estimates cost, defaulting to $0 for MCP configurations that have no cost mapping (MCP tool
    /// pricing is optional). Non-MCP providers keep the strict "cost required" behavior.
    /// </summary>
    private async Task<decimal> EstimateCostForConfigAsync(
        FunctionConfiguration configuration,
        int functionConfigurationId,
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _costCalculationService.EstimateCostAsync(functionConfigurationId, parameters, cancellationToken);
        }
        catch (InvalidOperationException ex) when (configuration.ProviderType == FunctionProviderType.Mcp)
        {
            _logger.LogWarning(ex,
                "No usable cost configuration for MCP function {ConfigId}; reserving $0. Add a FunctionCost to bill MCP calls.",
                functionConfigurationId);
            return 0m;
        }
    }

    /// <summary>
    /// Calculates actual cost, defaulting to $0 for MCP configurations that have no cost mapping.
    /// </summary>
    private async Task<decimal> CalculateCostForConfigAsync(
        FunctionConfiguration configuration,
        int functionConfigurationId,
        Models.FunctionExecutionUsage usage,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _costCalculationService.CalculateCostAsync(functionConfigurationId, usage, cancellationToken);
        }
        catch (InvalidOperationException ex) when (configuration.ProviderType == FunctionProviderType.Mcp)
        {
            _logger.LogWarning(ex,
                "No usable cost configuration for MCP function {ConfigId}; charging $0.",
                functionConfigurationId);
            return 0m;
        }
    }

    /// <inheritdoc />
    public async Task<FunctionExecution?> GetExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        return await _executionRepository.GetByIdAsync(executionId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<FunctionExecution>> ListExecutionsAsync(
        int virtualKeyId,
        CancellationToken cancellationToken = default)
    {
        return await _executionRepository.GetByVirtualKeyIdAsync(virtualKeyId, cancellationToken);
    }
}
