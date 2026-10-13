using System.Text.Json;
using ConduitLLM.Functions.DTOs;
using ConduitLLM.Functions.Extensions;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Exceptions;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Services;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Gateway.UsageTracking;
using ConduitLLM.Gateway.Serialization;
using ConduitLLM.Persistence.Interfaces;

namespace ConduitLLM.Gateway.Endpoints;

/// <summary>
/// Controller for executing functions (e.g., Exa search) through the Gateway API.
/// </summary>
public class FunctionsEndpoints : GatewayEndpointHandlerBase
{
    private readonly IFunctionExecutionService _executionService;
    private readonly IFunctionConfigurationRepository? _configurationRepository;
    private readonly IFunctionExecutionConfigurationStore? _runtimeConfigurationStore;
    private readonly ConduitLLM.Functions.Services.FunctionParameterValidationService _validationService;
    private readonly ILogger<FunctionsEndpoints> _logger;
    private readonly IProviderErrorTranslator _errorTranslator;

    /// <summary>
    /// Initializes the Functions endpoint handler.
    /// </summary>
    public FunctionsEndpoints(
        IFunctionExecutionService executionService,
        IFunctionConfigurationRepository configurationRepository,
        ConduitLLM.Functions.Services.FunctionParameterValidationService validationService,
        IEventPublisher eventPublisher,
        IHttpContextAccessor httpContextAccessor,
        ILogger<FunctionsEndpoints> logger,
        IProviderErrorTranslator errorTranslator)
        : base(eventPublisher, httpContextAccessor, logger)
    {
        _executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
        _configurationRepository = configurationRepository ?? throw new ArgumentNullException(nameof(configurationRepository));
        _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _errorTranslator = errorTranslator ?? throw new ArgumentNullException(nameof(errorTranslator));
    }

    /// <summary>
    /// Creates a handler over the extracted configuration read while preserving
    /// the public repository constructor and its existing DI behavior.
    /// </summary>
    public static FunctionsEndpoints CreateWithRuntimePersistence(
        IFunctionExecutionService executionService,
        IFunctionExecutionConfigurationStore configurationStore,
        ConduitLLM.Functions.Services.FunctionParameterValidationService validationService,
        IEventPublisher eventPublisher,
        IHttpContextAccessor httpContextAccessor,
        ILogger<FunctionsEndpoints> logger,
        IProviderErrorTranslator errorTranslator) =>
        new(executionService, configurationStore, validationService, eventPublisher, httpContextAccessor, logger, errorTranslator);

    private FunctionsEndpoints(
        IFunctionExecutionService executionService,
        IFunctionExecutionConfigurationStore configurationStore,
        ConduitLLM.Functions.Services.FunctionParameterValidationService validationService,
        IEventPublisher eventPublisher,
        IHttpContextAccessor httpContextAccessor,
        ILogger<FunctionsEndpoints> logger,
        IProviderErrorTranslator errorTranslator)
        : base(eventPublisher, httpContextAccessor, logger)
    {
        _executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
        _runtimeConfigurationStore = configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
        _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _errorTranslator = errorTranslator ?? throw new ArgumentNullException(nameof(errorTranslator));
    }

    /// <summary>
    /// Executes a function with the provided parameters.
    /// </summary>
    /// <param name="request">The function execution request</param>
    /// <param name="idempotencyKey">Optional key used to deduplicate retries.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The function execution result</returns>
    public async Task<IResult> ExecuteFunction(
        FunctionExecutionRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (request == null)
            {
                return OpenAIError(400, "Request body is required", "invalid_request");
            }

            // Get virtual key ID from authentication context
            var virtualKeyId = User.FindFirst("VirtualKeyId")?.Value;
            if (string.IsNullOrEmpty(virtualKeyId) || !int.TryParse(virtualKeyId, out var keyId))
            {
                _logger.LogWarning("Invalid or missing VirtualKeyId claim");
                return OpenAIError(401, "Invalid authentication", "invalid_auth", "authentication_error");
            }

            // Validate function configuration exists and is enabled
            var configuration = _runtimeConfigurationStore is not null
                ? await _runtimeConfigurationStore.GetConfigurationAsync(request.FunctionConfigurationId, cancellationToken)
                : await _configurationRepository!.GetByIdAsync(request.FunctionConfigurationId, cancellationToken);
            if (configuration == null)
            {
                return OpenAIError(404, $"Function configuration {request.FunctionConfigurationId} not found", "not_found", "not_found_error");
            }

            if (!configuration.IsEnabled)
            {
                return OpenAIError(400, $"Function configuration {request.FunctionConfigurationId} is disabled", "invalid_request");
            }

            // Validate parameters against schema if available
            var parameters = request.Parameters ?? new Dictionary<string, object>();
            var validationResult = _validationService.ValidateParameters(parameters, configuration.ParameterSchema);

            if (!validationResult.IsValid)
            {
                _logger.LogWarning(
                    "Parameter validation failed for function {FunctionName} (config {ConfigId}): {Errors}",
                    configuration.ConfigurationName,
                    configuration.Id,
                    string.Join(", ", validationResult.Errors));

                return OpenAIError(400, $"Parameter validation failed: {string.Join("; ", validationResult.Errors)}", "invalid_request");
            }

            if (validationResult.Warnings.Count > 0)
            {
                _logger.LogWarning(
                    "Parameter validation warnings for function {FunctionName} (config {ConfigId}): {Warnings}",
                    configuration.ConfigurationName,
                    configuration.Id,
                    string.Join(", ", validationResult.Warnings));
            }

            // Store provider info for middleware usage tracking
            HttpContext.Items["ProviderId"] = configuration.ProviderType;
            HttpContext.Items["ProviderType"] = configuration.ProviderType;
            HttpContext.Items["FunctionConfigurationId"] = configuration.Id;
            HttpContext.Items["FunctionConfigurationName"] = configuration.ConfigurationName;
            var accounting = HttpContext.GetOrCreateRequestAccountingContext();
            accounting.SetOperation(RequestOperation.Function, keyId, configuration.ConfigurationName);

            _logger.LogInformation(
                "Executing function {FunctionName} (config {ConfigId}) for virtual key {VirtualKeyId}",
                configuration.ConfigurationName,
                configuration.Id,
                keyId);

            // Execute the function
            var execution = await _executionService.ExecuteAsync(
                request.FunctionConfigurationId,
                keyId,
                request.Parameters ?? new Dictionary<string, object>(),
                idempotencyKey,
                request.Metadata,
                cancellationToken: cancellationToken);

            // Store execution info for middleware billing
            HttpContext.Items["FunctionExecutionId"] = execution.Id;
            HttpContext.Items["EstimatedCost"] = execution.EstimatedCost;
            HttpContext.Items["ActualCost"] = execution.ActualCost;
            var actualCost = execution.ActualCost ?? execution.EstimatedCost ?? 0m;
            accounting.RecordDirectCost(new DirectCostEvidence(
                configuration.ConfigurationName,
                actualCost,
                execution.Id.ToString(),
                JsonSerializer.Serialize(
                    new FunctionCostMetadata(
                        configuration.Id,
                        execution.Id,
                        execution.State.ToString()),
                    GatewayInternalJsonContext.Default.FunctionCostMetadata)));

            // Return execution result
            return Ok(ToCustomerDto(execution));
        }
        catch (FunctionCommunicationException ex)
        {
            var translated = _errorTranslator.Translate(ex.StatusCode, ex.ResponseBody ?? ex.Message, ex.ProviderName);
            var mapping = ExceptionToResponseMapper.MapProviderCommunicationStatus(ex.StatusCode, translated.Message);
            _logger.Log(
                mapping.LogLevel,
                ex,
                "Function provider {Provider} returned {StatusCode}",
                ex.ProviderName,
                ex.StatusCode);
            return OpenAIError(
                mapping.StatusCode,
                mapping.ResponseMessage,
                mapping.ErrorCode,
                mapping.OpenAIErrorType,
                mapping.Param,
                ChatEndpoints.BuildProviderErrorMetadata(null, translated.Detail));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid function execution request");
            return OpenAIError(400, ex.Message, "invalid_request");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing function");
            return OpenAIError(500, "An unexpected error occurred during function execution", "internal_error", "server_error");
        }
    }

    /// <summary>
    /// Gets the status and result of a function execution.
    /// </summary>
    /// <param name="executionId">The execution ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The function execution details</returns>
    public async Task<IResult> GetExecution(
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Get virtual key ID from authentication context
            var virtualKeyId = User.FindFirst("VirtualKeyId")?.Value;
            if (string.IsNullOrEmpty(virtualKeyId) || !int.TryParse(virtualKeyId, out var keyId))
            {
                _logger.LogWarning("Invalid or missing VirtualKeyId claim");
                return OpenAIError(401, "Invalid authentication", "invalid_auth", "authentication_error");
            }

            var execution = await _executionService.GetExecutionAsync(executionId, cancellationToken);

            if (execution == null)
            {
                return OpenAIError(404, $"Function execution {executionId} not found", "not_found", "not_found_error");
            }

            // Verify the execution belongs to the authenticated virtual key
            if (execution.VirtualKeyId != keyId)
            {
                _logger.LogWarning(
                    "Virtual key {VirtualKeyId} attempted to access execution {ExecutionId} owned by key {OwnerKeyId}",
                    keyId, executionId, execution.VirtualKeyId);
                return OpenAIError(404, $"Function execution {executionId} not found", "not_found", "not_found_error");
            }

            return Ok(ToCustomerDto(execution));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting function execution {ExecutionId}", executionId);
            return OpenAIError(500, "An unexpected error occurred", "internal_error", "server_error");
        }
    }

    private FunctionExecutionDto ToCustomerDto(FunctionExecution execution)
    {
        var dto = execution.ToContractDto();
        if (!string.IsNullOrWhiteSpace(dto.Error))
        {
            // Persisted diagnostics are private. Translate at read time so another host's
            // customer mode also applies, including records created before this policy.
            System.Net.HttpStatusCode? status = null;
            if (dto.Cost.Breakdown?.TryGetValue("httpStatusCode", out var value) == true &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var code))
            {
                status = (System.Net.HttpStatusCode)code;
            }
            dto.Error = _errorTranslator.Translate(
                status, execution.ErrorMessage, execution.FunctionConfiguration?.ProviderType.ToString()).Message;
        }
        return dto;
    }

    /// <summary>
    /// Request model for function execution.
    /// </summary>
    public class FunctionExecutionRequest
    {
        /// <summary>
        /// The function configuration ID to execute.
        /// </summary>
        public int FunctionConfigurationId { get; set; }

        /// <summary>
        /// Parameters to pass to the function.
        /// </summary>
        public Dictionary<string, object>? Parameters { get; set; }

        /// <summary>
        /// Optional metadata to associate with the execution.
        /// </summary>
        public Dictionary<string, object>? Metadata { get; set; }

    }

}
