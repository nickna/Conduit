using System.Diagnostics;
using System.Text.Json;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Exceptions;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Providers;
using ConduitLLM.Functions.Security;
using ConduitLLM.Functions.Serialization;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
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
/// Unkeyed requests are billed by the calling layer. Keyed requests settle their
/// durable execution identity before returning a result.
/// A result-write concurrency miss returns a failed, billable snapshot for reconciliation;
/// it never re-dispatches the provider or overwrites the competing stored snapshot.
/// </remarks>
public partial class FunctionExecutionService : IFunctionExecutionService
{
    private readonly IFunctionConfigurationRepository? _functionConfigurationRepository;
    private readonly IFunctionCredentialRepository? _credentialRepository;
    private readonly IFunctionExecutionRepository? _executionRepository;
    private readonly IFunctionExecutionConfigurationStore? _runtimeConfigurationStore;
    private readonly IFunctionExecutionRuntimeWriter? _runtimeWriter;
    private readonly IFunctionExecutionRuntimeReader? _runtimeReader;
    private readonly IFunctionCostCalculationService _costCalculationService;
    private readonly IFunctionClientFactory _clientFactory;
    private readonly IFunctionCredentialProtector _credentialProtector;
    private readonly ILogger<FunctionExecutionService> _logger;
    private IFunctionExecutionIdempotencyStore? _idempotencyStore;

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

    /// <summary>
    /// Creates an execution service over the extracted persistence operations. The
    /// named factory preserves the public repository constructor and its DI behavior.
    /// </summary>
    public static FunctionExecutionService CreateWithRuntimePersistence(
        IFunctionExecutionConfigurationStore configurationStore,
        IFunctionExecutionRuntimeWriter writer,
        IFunctionExecutionRuntimeReader reader,
        IFunctionCostCalculationService costCalculationService,
        IFunctionClientFactory clientFactory,
        IFunctionCredentialProtector credentialProtector,
        ILogger<FunctionExecutionService> logger,
        IFunctionExecutionIdempotencyStore? idempotencyStore = null) =>
        new(configurationStore, writer, reader, costCalculationService, clientFactory, credentialProtector, logger, idempotencyStore);

    private FunctionExecutionService(
        IFunctionExecutionConfigurationStore configurationStore,
        IFunctionExecutionRuntimeWriter writer,
        IFunctionExecutionRuntimeReader reader,
        IFunctionCostCalculationService costCalculationService,
        IFunctionClientFactory clientFactory,
        IFunctionCredentialProtector credentialProtector,
        ILogger<FunctionExecutionService> logger,
        IFunctionExecutionIdempotencyStore? idempotencyStore)
    {
        _runtimeConfigurationStore = configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
        _runtimeWriter = writer ?? throw new ArgumentNullException(nameof(writer));
        _runtimeReader = reader ?? throw new ArgumentNullException(nameof(reader));
        _costCalculationService = costCalculationService ?? throw new ArgumentNullException(nameof(costCalculationService));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _credentialProtector = credentialProtector ?? throw new ArgumentNullException(nameof(credentialProtector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _idempotencyStore = idempotencyStore;
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

        return await (idempotencyKey is null
            ? ExecuteCoreAsync(functionConfigurationId, virtualKeyId, parameters, metadata,
                providerToolName, null, null, cancellationToken)
            : ExecuteIdempotentAsync(functionConfigurationId, virtualKeyId, parameters,
                idempotencyKey, metadata, providerToolName, cancellationToken));
    }

    private async Task<FunctionExecution> ExecuteCoreAsync(
        int functionConfigurationId,
        int virtualKeyId,
        Dictionary<string, object> parameters,
        Dictionary<string, object>? metadata,
        string? providerToolName,
        Guid? claimedExecutionId,
        FunctionExecutionClaim? claim,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        FunctionExecution? execution = null;
        var providerExecutionCompleted = false;
        decimal? incurredCost = null;

        try
        {
            // 1. Load function configuration
            var configuration = _runtimeConfigurationStore is not null
                ? await _runtimeConfigurationStore.GetConfigurationAsync(functionConfigurationId, cancellationToken)
                : await _functionConfigurationRepository!.GetByIdAsync(functionConfigurationId, cancellationToken);
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
                IdempotencyKey = null
            };

            execution = new FunctionExecution
            {
                Id = claimedExecutionId ?? Guid.NewGuid(),
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

            await CreateExecutionAsync(execution, cancellationToken);

            _logger.LogInformation("Created execution record {ExecutionId} for function {ConfigName}",
                execution.Id, configuration.ConfigurationName);

            // 4. Get credentials for this provider type
            IReadOnlyList<FunctionCredential> credentials = _runtimeConfigurationStore is not null
                ? await _runtimeConfigurationStore.ListCredentialsForProviderAsync(configuration.ProviderType, cancellationToken)
                : await _credentialRepository!.GetByProviderTypeAsync(configuration.ProviderType, cancellationToken);

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
            if (claim is not null && client is FunctionClientBase httpProvider)
                httpProvider.UseSingleDispatchTransport();

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

            if (claim is not null)
            {
                var store = _idempotencyStore ?? throw IdempotencyUnavailable(claim.ExecutionId);
                if (!await store.MarkDispatchedAsync(claim, cancellationToken))
                    throw IdempotencyUnavailable(claim.ExecutionId);
            }

            var result = await client.ExecuteAsync(executeParameters, apiKey, cancellationToken);
            if (claim is not null && !result.IsSuccess && (result.HttpStatusCode is null or 0) && result.ResponseJson is null)
            {
                // Providers may report timeouts or transport loss as a failed result
                // rather than throw. No response proves whether the external operation ran.
                throw IdempotencyOutcomeUnknown(claim.ExecutionId);
            }

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

            if (!await UpdateExecutionAsync(execution, cancellationToken))
                ReportPersistenceConflict(execution, providerExecutionCompleted: true);

            return execution;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            _logger.LogError(ex, "Error executing function {ConfigId}: {Error}",
                functionConfigurationId, ex.Message);
            var errorMessage = claim is not null && ex is FunctionCommunicationException providerError
                ? providerError.ResponseBody ?? providerError.Message
                : ex.Message;
            if (claim is not null && errorMessage.Length > 2000) errorMessage = errorMessage[..2000];

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
                        IdempotencyKey = null
                    };

                    execution = new FunctionExecution
                    {
                        Id = claimedExecutionId ?? Guid.NewGuid(),
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
                        ErrorMessage = errorMessage,
                        RetryCount = 0,
                        Version = 1,
                        WebhookDelivered = false
                    };

                    if (ex is ConduitLLM.Functions.Exceptions.FunctionCommunicationException communicationError)
                    {
                        execution.CostCalculationDetails = JsonSerializer.Serialize(
                            new FunctionExecutionCostDetails { Usage = new(), HttpStatusCode = (int)communicationError.StatusCode },
                            FunctionsJsonContext.Default.FunctionExecutionCostDetails);
                    }
                    await CreateExecutionAsync(execution, cancellationToken);
                }
                else
                {
                    // Update existing execution with error
                    execution.State = ExecutionState.Failed;
                    execution.CompletedAt = DateTime.UtcNow;
                    execution.Duration = stopwatch.Elapsed;
                    execution.ErrorMessage = errorMessage;
                    execution.ActualCost = providerExecutionCompleted
                        ? incurredCost ?? execution.ActualCost ?? execution.EstimatedCost ?? 0m
                        : 0m;

                    if (ex is ConduitLLM.Functions.Exceptions.FunctionCommunicationException communicationError)
                    {
                        execution.CostCalculationDetails = JsonSerializer.Serialize(
                            new FunctionExecutionCostDetails { Usage = new(), HttpStatusCode = (int)communicationError.StatusCode },
                            FunctionsJsonContext.Default.FunctionExecutionCostDetails);
                    }

                    if (!await UpdateExecutionAsync(execution, cancellationToken))
                        ReportPersistenceConflict(execution, providerExecutionCompleted);
                }
            }
            catch (Exception recordEx)
            {
                _logger.LogError(recordEx, "Failed to record execution error for function {ConfigId}",
                    functionConfigurationId);
            }

            if (claim is not null && execution is not null && ex is FunctionCommunicationException)
            {
                // An actual provider error response is a known terminal result. Preserve
                // it for replay instead of treating it as an uncertain transport outcome.
                return execution;
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

    private void ReportPersistenceConflict(FunctionExecution execution, bool providerExecutionCompleted)
    {
        var diagnostic = $"Execution {execution.Id} could not be saved because of a concurrency conflict. " +
            (providerExecutionCompleted
                ? "The provider operation already ran and remains billable. Reconcile this execution before dispatching a replacement operation."
                : "Reconcile the persisted execution before retrying.");
        var original = execution.ErrorMessage;
        execution.State = ExecutionState.Failed;
        // Keep the original provider/bookkeeping diagnostic while fitting the entity's limit.
        execution.ErrorMessage = string.IsNullOrEmpty(original) ? diagnostic :
            original[..Math.Min(original.Length, 2000 - diagnostic.Length - 1)] + " " + diagnostic;
        _logger.LogWarning(
            "Execution result persistence conflict for {ExecutionId}; provider completed: {ProviderCompleted}, incurred cost: {ActualCost}. The competing snapshot was preserved. {Reconciliation}",
            execution.Id, providerExecutionCompleted, execution.ActualCost, diagnostic);
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
        return _runtimeReader is not null
            ? await _runtimeReader.GetByIdAsync(executionId, cancellationToken)
            : await _executionRepository!.GetByIdAsync(executionId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<FunctionExecution>> ListExecutionsAsync(
        int virtualKeyId,
        CancellationToken cancellationToken = default)
    {
        return _runtimeReader is not null
            ? (await _runtimeReader.ListByVirtualKeyAsync(virtualKeyId, cancellationToken)).ToList()
            : await _executionRepository!.GetByVirtualKeyIdAsync(virtualKeyId, cancellationToken);
    }

    private Task<Guid> CreateExecutionAsync(FunctionExecution execution, CancellationToken cancellationToken) =>
        _runtimeWriter is not null
            ? _runtimeWriter.CreateAsync(execution, cancellationToken)
            : _executionRepository!.CreateAsync(execution, cancellationToken);

    private Task<bool> UpdateExecutionAsync(FunctionExecution execution, CancellationToken cancellationToken) =>
        _runtimeWriter is not null
            ? _runtimeWriter.UpdateAsync(execution, cancellationToken)
            : _executionRepository!.UpdateAsync(execution, cancellationToken);
}
