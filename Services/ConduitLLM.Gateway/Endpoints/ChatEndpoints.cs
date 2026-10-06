using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using ConduitLLM.Core;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.UsageTracking;
using ConduitLLM.Gateway.Billing;
using ConduitLLM.Gateway.Models;
using GatewayOpsMetrics = ConduitLLM.Gateway.Services.GatewayOperationsMetricsService;

using Microsoft.Extensions.Options;

namespace ConduitLLM.Gateway.Endpoints
{
    /// <summary>
    /// Handles chat completion requests following OpenAI's API format.
    /// </summary>
    public partial class ChatEndpoints : GatewayEndpointHandlerBase
    {
        private readonly Conduit _conduit;
        private readonly ILogger<ChatEndpoints> _logger;
        private readonly ConduitLLM.Configuration.Interfaces.IModelProviderMappingService _modelMappingService;
        private readonly JsonSerializerOptions _jsonSerializerOptions;
        private readonly ConduitLLM.Core.Interfaces.IUsageEstimationService _usageEstimationService;
        private readonly ConduitLLM.Functions.Interfaces.IFunctionConfigurationRepository? _functionConfigRepository;
        private readonly ConduitLLM.Configuration.Interfaces.IGlobalSettingsCacheService _globalSettingsCacheService;
        private readonly UsageTrackingOptions _usageTrackingOptions;
        private readonly IChatSpendEstimator? _chatSpendEstimator;
        private readonly ISpendReservationService? _spendReservationService;
        private readonly BillingAdmissionOptions _billingAdmissionOptions;
        private readonly ConduitLLM.Core.Interfaces.IProviderErrorTranslator _providerErrorTranslator;

        public ChatEndpoints(
            Conduit conduit,
            ILogger<ChatEndpoints> logger,
            ConduitLLM.Configuration.Interfaces.IModelProviderMappingService modelMappingService,
            JsonSerializerOptions jsonSerializerOptions,
            IEventPublisher eventPublisher,
            ConduitLLM.Configuration.Interfaces.IGlobalSettingsCacheService globalSettingsCacheService,
            ConduitLLM.Core.Interfaces.IUsageEstimationService usageEstimationService,
            ConduitLLM.Functions.Interfaces.IFunctionConfigurationRepository? functionConfigRepository = null,
            IOptions<UsageTrackingOptions>? usageTrackingOptions = null,
            IChatSpendEstimator? chatSpendEstimator = null,
            ISpendReservationService? spendReservationService = null,
            IOptions<BillingAdmissionOptions>? billingAdmissionOptions = null,
            IHttpContextAccessor? httpContextAccessor = null,
            ConduitLLM.Core.Interfaces.IProviderErrorTranslator? providerErrorTranslator = null) : base(eventPublisher, httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor)), logger)
        {
            _conduit = conduit ?? throw new ArgumentNullException(nameof(conduit));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _modelMappingService = modelMappingService ?? throw new ArgumentNullException(nameof(modelMappingService));
            _jsonSerializerOptions = jsonSerializerOptions ?? throw new ArgumentNullException(nameof(jsonSerializerOptions));
            _globalSettingsCacheService = globalSettingsCacheService ?? throw new ArgumentNullException(nameof(globalSettingsCacheService));
            _usageEstimationService = usageEstimationService ?? throw new ArgumentNullException(nameof(usageEstimationService));
            _functionConfigRepository = functionConfigRepository;
            _usageTrackingOptions = usageTrackingOptions?.Value ?? new UsageTrackingOptions();
            _chatSpendEstimator = chatSpendEstimator;
            _spendReservationService = spendReservationService;
            _billingAdmissionOptions = billingAdmissionOptions?.Value ?? new BillingAdmissionOptions();
            // Default to External so an unwired construction sanitizes rather than leaks.
            _providerErrorTranslator = providerErrorTranslator
                ?? new ProviderErrorTranslator(new ConduitLLM.Core.Configuration.CustomerErrorOptions());
        }

        /// <summary>
        /// Creates a chat completion.
        /// </summary>
        public async Task<IResult> CreateChatCompletion(
            ChatCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            using var activity = GatewayRequestMetrics.StartChatCompletionActivity(
                request.Model, request.Stream == true);
            var operationStopwatch = Stopwatch.StartNew();

            _logger.LogInformation("Received /v1/chat/completions request for model: {Model}", LoggingSanitizer.S(request.Model));

            HttpContext.Items["IsStreamingRequest"] = request.Stream == true;

            ApplySessionAffinity(request);

            await PopulateProviderMetadataAsync(request, activity);

            var modalitySummary = ConduitLLM.Core.Utilities.MultimodalContentInspector.Inspect(request.Messages);
            _logger.LogDebug(
                "Chat input modalities: text={TextParts}, images={ImageParts}, files={FileParts}, audio={AudioParts}, videos={VideoParts}, remote={RemoteParts}, inline={InlineParts}, provider_file_ids={ProviderFileIds}",
                modalitySummary.TextParts,
                modalitySummary.ImageParts,
                modalitySummary.FileParts,
                modalitySummary.AudioParts,
                modalitySummary.VideoParts,
                modalitySummary.RemoteParts,
                modalitySummary.InlineParts,
                modalitySummary.ProviderFileIdParts);

            if (request.FunctionConfigurationIds != null && request.FunctionConfigurationIds.Count > 0)
            {
                var validationError = await ValidateFunctionCallRequestAsync(request, cancellationToken);
                if (validationError != null)
                    return validationError;
            }

            await ApplyAgenticDefaultsAsync(request);

            try
            {
                var virtualKeyId = CurrentVirtualKeyId;
                var accountingContext = HttpContext.GetOrCreateRequestAccountingContext();
                accountingContext.SetOperation(RequestOperation.ChatCompletion, virtualKeyId, request.Model);
                Response.Headers["x-request-id"] = accountingContext.BillingRequestId;

                var admissionError = await ReserveChatSpendAsync(
                    request,
                    virtualKeyId,
                    accountingContext,
                    cancellationToken);
                if (admissionError is not null)
                {
                    return admissionError;
                }

                var invocationError = await MarkChatInvocationStartedAsync(
                    virtualKeyId,
                    accountingContext);
                if (invocationError is not null)
                {
                    return invocationError;
                }

                if (request.Stream != true)
                {
                    return await HandleNonStreamingRequestAsync(request, virtualKeyId, operationStopwatch, cancellationToken);
                }
                else
                {
                    await HandleStreamingRequestAsync(request, virtualKeyId, operationStopwatch, cancellationToken);
                    return Results.Empty;
                }
            }
            catch (LLMCommunicationException ex)
            {
                var error = MapProviderCommunicationError(ex.StatusCode);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error.type", ex.GetType().Name);
                activity?.SetTag("http.response.status_code", error.StatusCode);
                activity?.SetTag("provider.http_status_code", ex.StatusCode is null ? null : (int)ex.StatusCode.Value);

                if (error.StatusCode >= StatusCodes.Status500InternalServerError)
                {
                    _logger.LogError(ex,
                        "Provider communication failed for model {Model}; upstream status {UpstreamStatus} mapped to HTTP {StatusCode}",
                        LoggingSanitizer.S(request.Model), ex.StatusCode, error.StatusCode);
                }
                else
                {
                    _logger.LogWarning(ex,
                        "Provider rejected chat request for model {Model}; upstream status {UpstreamStatus} mapped to HTTP {StatusCode}",
                        LoggingSanitizer.S(request.Model), ex.StatusCode, error.StatusCode);
                }

                GatewayOpsMetrics.RecordLlmOperation(
                    "chat_completion",
                    request.Model,
                    error.MetricOutcome,
                    operationStopwatch.Elapsed.TotalSeconds);
                var customerError = _providerErrorTranslator.Translate(ex);
                return OpenAIError(
                    error.StatusCode,
                    customerError.Message,
                    error.Code,
                    error.Type,
                    metadata: BuildProviderErrorMetadata(
                        TryExtractFileAnnotationMetadata(ex.ResponseBody), customerError.Detail));
            }
            catch (Exception ex)
            {
                // Observe, then rethrow — OpenAIErrorMiddleware maps exceptions to proper HTTP responses
                // via ExceptionToResponseMapper. Returning a 500 here instead turned model-routing client
                // errors (unknown alias, disabled mapping/provider) into 500s and leaked ex.Message (#1191).
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("error.type", ex.GetType().Name);
                GatewayOpsMetrics.RecordLlmOperation("chat_completion", request.Model, "error", operationStopwatch.Elapsed.TotalSeconds);
                throw;
            }
        }

        internal static ProviderCommunicationError MapProviderCommunicationError(HttpStatusCode? upstreamStatus)
        {
            // The status table is shared with OpenAIErrorMiddleware via ExceptionToResponseMapper,
            // so chat and non-chat routes report identical statuses for the same provider failure.
            var mapping = ConduitLLM.Core.Exceptions.ExceptionToResponseMapper
                .MapProviderCommunicationStatus(upstreamStatus, string.Empty);
            var metricOutcome = mapping.ErrorCode switch
            {
                "rate_limit_exceeded" => "rate_limited",
                "provider_timeout" or "provider_unavailable" or "provider_bad_gateway" => "provider_unavailable",
                "provider_request_error" => "provider_rejected",
                _ => "provider_error"
            };
            return new(mapping.StatusCode, mapping.ErrorCode, mapping.OpenAIErrorType, metricOutcome);
        }

        internal readonly record struct ProviderCommunicationError(
            int StatusCode,
            string Code,
            string Type,
            string MetricOutcome);

        /// <summary>
        /// Merges file-annotation metadata mined from the provider body with the
        /// customer-mode <c>provider_error</c> detail (Internal mode only) into one
        /// <c>error.metadata</c> element.
        /// </summary>
        internal static JsonElement? BuildProviderErrorMetadata(
            JsonElement? fileAnnotationMetadata,
            ConduitLLM.Core.Interfaces.ProviderErrorDetail? providerErrorDetail)
        {
            if (providerErrorDetail is null)
            {
                return fileAnnotationMetadata;
            }

            var combined = new Dictionary<string, JsonElement>
            {
                ["provider_error"] = JsonSerializer.SerializeToElement(
                    providerErrorDetail,
                    ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.ProviderErrorDetail)
            };

            if (fileAnnotationMetadata is { } annotations &&
                annotations.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in annotations.EnumerateObject())
                {
                    combined[property.Name] = property.Value.Clone();
                }
            }

            return JsonSerializer.SerializeToElement(
                combined,
                ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.DictionaryStringJsonElement);
        }

        internal static JsonElement? TryExtractFileAnnotationMetadata(string? responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
                return null;

            try
            {
                using var document = JsonDocument.Parse(responseBody);
                if (!document.RootElement.TryGetProperty("error", out var error) ||
                    !error.TryGetProperty("metadata", out var metadata) ||
                    !metadata.TryGetProperty("file_annotations", out var annotations) ||
                    annotations.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                return JsonSerializer.SerializeToElement(
                    new Dictionary<string, JsonElement>
                    {
                        ["file_annotations"] = annotations.Clone()
                    },
                    ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.DictionaryStringJsonElement);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private void ApplySessionAffinity(ChatCompletionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.SessionId) &&
                Request.Headers.TryGetValue("X-Conduit-Session-Id", out var headerSession))
            {
                request.SessionId = headerSession.FirstOrDefault();
            }

            if (request.SessionId?.Length > 256)
                throw new ArgumentException("session_id must not exceed 256 characters.", nameof(request.SessionId));

            if (HttpContext.Items["VirtualKey.KeyHash"] is not string virtualKeyHash ||
                string.IsNullOrWhiteSpace(virtualKeyHash)) return;

            var affinitySource = request.SessionId;
            if (string.IsNullOrWhiteSpace(affinitySource))
            {
                var firstSystem = request.Messages.FirstOrDefault(message =>
                    message.Role.Equals("system", StringComparison.OrdinalIgnoreCase) ||
                    message.Role.Equals("developer", StringComparison.OrdinalIgnoreCase));
                var firstUser = request.Messages.FirstOrDefault(message =>
                    message.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
                affinitySource = JsonSerializer.Serialize(
                    new[] { firstSystem?.Content, firstUser?.Content },
                    ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.ObjectArray);
            }

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(virtualKeyHash));
            request.RoutingAffinityKey = Convert.ToHexString(
                hmac.ComputeHash(Encoding.UTF8.GetBytes(affinitySource ?? string.Empty))).ToLowerInvariant();
        }

        private async Task PopulateProviderMetadataAsync(ChatCompletionRequest request, Activity? activity)
        {
            try
            {
                var modelMapping = await _modelMappingService.GetMappingByModelAliasAsync(request.Model);
                if (modelMapping != null)
                {
                    HttpContext.Items["ProviderId"] = modelMapping.ProviderId;
                    HttpContext.Items["ProviderType"] = modelMapping.Provider?.ProviderType;
                    if (modelMapping.Provider is not null)
                    {
                        HttpContext.Items[ConduitLLM.Gateway.Constants.HttpContextKeys.PromptCachingEligible] =
                            PromptCachingCapabilityCatalog.IsEligible(
                                modelMapping.Provider.ProviderType.ToString(), modelMapping.ProviderModelId);
                    }
                    activity?.SetTag("gateway.provider_id", modelMapping.ProviderId);
                    activity?.SetTag("gateway.provider_type", modelMapping.Provider?.ProviderType.ToString());

                    if (modelMapping.ModelProviderTypeAssociation?.ModelCostId != null)
                    {
                        HttpContext.Items[ConduitLLM.Gateway.Constants.HttpContextKeys.ModelCostId] = modelMapping.ModelProviderTypeAssociation.ModelCostId;
                    }

                    if (modelMapping.Provider?.TrustProviderReportedCosts == true)
                    {
                        HttpContext.Items[ConduitLLM.Gateway.Constants.HttpContextKeys.ProviderBillingPolicy] =
                            new ConduitLLM.Core.Models.ProviderCostBillingPolicy
                            {
                                TrustProviderReportedCost = true,
                                MarkupMultiplier = modelMapping.Provider.ProviderCostMarkupMultiplier
                            };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get provider info for model {Model}", LoggingSanitizer.S(request.Model));
            }
        }

        private async Task CaptureSelectedRouteAsync(ChatCompletionRequest request)
        {
            if (request.SelectedMappingId is not int mappingId) return;
            var mapping = await _modelMappingService.GetMappingByIdAsync(mappingId);
            if (mapping is null) return;
            HttpContext.Items[ConduitLLM.Gateway.Constants.HttpContextKeys.ModelProviderMappingId] = mappingId;
            HttpContext.Items["ProviderId"] = mapping.ProviderId;
            HttpContext.Items["ProviderType"] = mapping.Provider?.ProviderType;
            if (mapping.Provider is not null)
                HttpContext.Items[ConduitLLM.Gateway.Constants.HttpContextKeys.PromptCachingEligible] =
                    PromptCachingCapabilityCatalog.IsEligible(mapping.Provider.ProviderType.ToString(), mapping.ProviderModelId);
            HttpContext.Items[ConduitLLM.Gateway.Constants.HttpContextKeys.RoutingAffinityUsed] = request.RoutingAffinityUsed;
            HttpContext.Items[ConduitLLM.Gateway.Constants.HttpContextKeys.RoutingDecisionReason] = request.RoutingDecisionReason;
            HttpContext.Items[ConduitLLM.Gateway.Constants.HttpContextKeys.RoutingFailoverCount] = request.RoutingFailoverCount;
            HttpContext.Items[ConduitLLM.Gateway.Constants.HttpContextKeys.PromptCachingPolicyApplied] = request.PromptCachingIntent is not null;
            if (mapping.ModelProviderTypeAssociation?.ModelCostId is int modelCostId)
                HttpContext.Items[ConduitLLM.Gateway.Constants.HttpContextKeys.ModelCostId] = modelCostId;
            PromptCachingMetrics.RecordRouting(request.Model, mapping.Provider?.ProviderType.ToString() ?? "unknown",
                mappingId, request.RoutingDecisionReason ?? "unknown", request.RoutingFailoverCount);
        }

        private async Task ApplyAgenticDefaultsAsync(ChatCompletionRequest request)
        {
            if (!request.MaxAgenticIterations.HasValue)
            {
                request.MaxAgenticIterations = await _globalSettingsCacheService.GetMaxAgenticIterationsAsync();
            }
            if (!request.EnableAgenticMode.HasValue)
            {
                request.EnableAgenticMode = await _globalSettingsCacheService.GetDefaultAgenticModeEnabledAsync();
            }
        }

        private async Task<IResult?> ReserveChatSpendAsync(
            ChatCompletionRequest request,
            int? virtualKeyId,
            IRequestAccountingContext accountingContext,
            CancellationToken cancellationToken)
        {
            if (_billingAdmissionOptions.Mode == BillingAdmissionMode.Off)
            {
                return null;
            }

            if (!virtualKeyId.HasValue || _chatSpendEstimator is null || _spendReservationService is null)
            {
                return _billingAdmissionOptions.Mode == BillingAdmissionMode.Enforce
                    ? AdmissionFailure(
                        StatusCodes.Status503ServiceUnavailable,
                        "Billing admission is unavailable.",
                        "billing_unavailable")
                    : null;
            }

            ChatSpendEstimate estimate;
            try
            {
                estimate = await _chatSpendEstimator.EstimateMaximumCostAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to estimate maximum chat spend");
                estimate = new ChatSpendEstimate(false, 0m, 0, 0, null, ex.Message);
            }

            if (!estimate.Succeeded)
            {
                _logger.LogError(
                    "Chat billing estimate failed in {Mode} mode: {FailureReason}",
                    _billingAdmissionOptions.Mode,
                    estimate.FailureReason);
                return _billingAdmissionOptions.Mode == BillingAdmissionMode.Enforce
                    ? AdmissionFailure(
                        StatusCodes.Status503ServiceUnavailable,
                        "Pricing is unavailable for this request.",
                        "pricing_unavailable")
                    : null;
            }

            var reservation = await _spendReservationService.ReserveAsync(
                virtualKeyId.Value,
                accountingContext.BillingRequestId,
                estimate.Amount);
            if (reservation.Outcome == SpendReservationOutcome.Reserved)
            {
                accountingContext.RecordReservation(estimate.Amount);
                return null;
            }

            _logger.LogWarning(
                "Chat spend reservation failed in {Mode} mode with outcome {Outcome}",
                _billingAdmissionOptions.Mode,
                reservation.Outcome);
            if (_billingAdmissionOptions.Mode != BillingAdmissionMode.Enforce)
            {
                return null;
            }

            return reservation.Outcome == SpendReservationOutcome.InsufficientBalance
                ? AdmissionFailure(
                    StatusCodes.Status402PaymentRequired,
                    "Insufficient balance for the requested maximum usage.",
                    "insufficient_balance")
                : AdmissionFailure(
                    StatusCodes.Status503ServiceUnavailable,
                    "Billing admission is temporarily unavailable.",
                    "billing_unavailable");
        }

        private async Task<IResult?> MarkChatInvocationStartedAsync(
            int? virtualKeyId,
            IRequestAccountingContext accountingContext)
        {
            var snapshot = accountingContext.Snapshot();
            if (snapshot.Reservation is null || !virtualKeyId.HasValue || _spendReservationService is null)
            {
                return null;
            }

            try
            {
                if (await _spendReservationService.MarkInvocationStartedAsync(
                        virtualKeyId.Value,
                        accountingContext.BillingRequestId))
                {
                    accountingContext.MarkInvocationStarted();
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist chat provider invocation start");
            }

            if (_billingAdmissionOptions.Mode == BillingAdmissionMode.Enforce)
            {
                return AdmissionFailure(
                    StatusCodes.Status503ServiceUnavailable,
                    "Billing admission is temporarily unavailable.",
                    "billing_unavailable");
            }

            accountingContext.MarkInvocationStarted();
            accountingContext.MarkIndeterminate(
                "Provider invocation proceeded in shadow mode without a durable invocation-start transition");
            return null;
        }

        private IResult AdmissionFailure(int statusCode, string message, string code) =>
            OpenAIError(statusCode, message, code, "billing_error");

    }

}
