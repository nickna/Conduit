using System.Security.Cryptography;
using System.Text;
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

            // Existing scoped operations remain readable even after their configuration
            // changes. The execution service verifies the complete request fingerprint.
            if (idempotencyKey is not null)
            {
                if (Request.Headers.TryGetValue("Idempotency-Key", out var headerValues) && headerValues.Count != 1)
                    return OpenAIError(400, "Supply exactly one Idempotency-Key header value.", "invalid_idempotency_key");
                if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
                    return OpenAIError(400, "Idempotency-Key must contain 1 to 200 nonblank characters.", "invalid_idempotency_key");
                var store = HttpContext.RequestServices.GetService<IFunctionExecutionIdempotencyStore>();
                if (store is null)
                    return OpenAIError(503, "Durable function idempotency is unavailable.", "idempotency_unavailable", "server_error");
                var keyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)));
                bool existing;
                try { existing = await store.FindAsync(keyId, keyHash, cancellationToken) is not null; }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not read durable function claims for virtual key {VirtualKeyId}", keyId);
                    return OpenAIError(503, "Durable function idempotency is unavailable.", "idempotency_unavailable", "server_error");
                }
                if (existing)
                {
                    var replay = await _executionService.ExecuteAsync(
                        request.FunctionConfigurationId, keyId,
                        request.Parameters ?? new Dictionary<string, object>(),
                        idempotencyKey, request.Metadata, cancellationToken: cancellationToken);
                    return ExecutionResult(replay, keyId,
                        replay.FunctionConfiguration?.ConfigurationName ?? $"function:{replay.FunctionConfigurationId}");
                }
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

            return ExecutionResult(execution, keyId, configuration.ConfigurationName);
        }
        catch (FunctionIdempotencyException ex)
        {
            if (ex.ExecutionId != Guid.Empty)
            {
                Response.Headers["Function-Execution-Id"] = ex.ExecutionId.ToString();
                Response.Headers.Location = $"/v1/conduit/functions/executions/{ex.ExecutionId}";
            }
            if (ex.Code == "idempotency_in_progress") Response.Headers.RetryAfter = "3";
            return OpenAIError(ex.StatusCode, ex.Message, ex.Code,
                ex.StatusCode >= 500 ? "server_error" : "invalid_request_error");
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

    private IResult ExecutionResult(FunctionExecution execution, int virtualKeyId, string operationName)
    {
        HttpContext.Items["FunctionExecutionId"] = execution.Id;
        HttpContext.Items["EstimatedCost"] = execution.EstimatedCost;
        HttpContext.Items["ActualCost"] = execution.ActualCost;
        var accounting = HttpContext.GetOrCreateRequestAccountingContext();
        accounting.SetOperation(RequestOperation.Function, virtualKeyId, operationName);
        var actualCost = execution.IsIdempotencyReplay ? 0m : execution.ActualCost ?? execution.EstimatedCost ?? 0m;
        accounting.RecordDirectCost(new DirectCostEvidence(
            operationName, actualCost, execution.Id.ToString(),
            JsonSerializer.Serialize(
                new FunctionCostMetadata(execution.FunctionConfigurationId, execution.Id, execution.State.ToString()),
                GatewayInternalJsonContext.Default.FunctionCostMetadata),
            SpendAlreadySettled: execution.IsIdempotencyBillingSettled));
        Response.Headers["Function-Execution-Id"] = execution.Id.ToString();
        if (execution.IsIdempotencyReplay) Response.Headers["Idempotency-Replayed"] = "true";
        var dto = ToCustomerDto(execution);
        if (execution.IsIdempotencyBillingSettled && execution.State == ExecutionState.Failed
            && dto.Cost.Breakdown?.TryGetValue("httpStatusCode", out var status) == true
            && status.TryGetInt32(out var code) && code >= 400)
        {
            // A definitive provider HTTP error is a replayable terminal result too.
            var translated = _errorTranslator.Translate((System.Net.HttpStatusCode)code,
                execution.ErrorMessage, execution.FunctionConfiguration?.ProviderType.ToString());
            var mapping = ExceptionToResponseMapper.MapProviderCommunicationStatus(
                (System.Net.HttpStatusCode)code, translated.Message);
            return OpenAIError(mapping.StatusCode, mapping.ResponseMessage, mapping.ErrorCode,
                mapping.OpenAIErrorType, mapping.Param,
                ChatEndpoints.BuildProviderErrorMetadata(null, translated.Detail));
        }
        return Ok(dto);
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
