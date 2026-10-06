using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Metrics;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Policies;
using ConduitLLM.Core.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IVirtualKeyService = ConduitLLM.Core.Interfaces.IVirtualKeyService;
using IModelProviderMappingService = ConduitLLM.Configuration.Interfaces.IModelProviderMappingService;
using IBatchSpendUpdateService = ConduitLLM.Configuration.Interfaces.IBatchSpendUpdateService;

namespace ConduitLLM.Core.Services.Abstractions
{
    /// <summary>
    /// Abstract base orchestrator for all media generation workflows.
    /// Implements Template Method pattern for consistent processing flow.
    /// </summary>
    /// <typeparam name="TRequest">The generation request type</typeparam>
    /// <typeparam name="TResponse">The generation response type</typeparam>
    /// <typeparam name="TEventRequest">The event request type</typeparam>
    public abstract class MediaGenerationOrchestrator<TRequest, TResponse, TEventRequest>
        : IEventHandler<TEventRequest>
        where TRequest : class
        where TResponse : class
        where TEventRequest : class
    {
        // Abstract methods to get properties from specific event types
        protected abstract string GetRequestId(TEventRequest request);
        protected abstract string GetModel(TEventRequest request);
        protected abstract string GetPrompt(TEventRequest request);
        protected abstract string GetVirtualKeyId(TEventRequest request);
        protected abstract string? GetWebhookUrl(TEventRequest request);
        protected abstract string? GetCorrelationId(TEventRequest request);
        protected abstract bool GetIsAsync(TEventRequest request);
        protected virtual Dictionary<string, string>? GetWebhookHeaders(TEventRequest request) => null;
        // Core dependencies shared across all media orchestrators
        protected readonly ILLMClientFactory _clientFactory;
        protected readonly IAsyncTaskService _taskService;
        protected readonly IMediaStorageService _storageService;
        protected readonly IEventBus _eventBus;
        protected readonly IModelProviderMappingService _modelMappingService;
        protected readonly IVirtualKeyService _virtualKeyService;
        protected readonly ICostCalculationService _costService;
        protected readonly ICancellableTaskRegistry _taskRegistry;
        protected readonly IWebhookNotificationService _webhookService;
        protected readonly IHttpClientFactory _httpClientFactory;
        protected readonly MinimalParameterValidator _parameterValidator;
        protected readonly MediaGenerationMetrics _metrics;
        protected readonly IProviderErrorTrackingService _errorTrackingService;
        protected readonly ILogger _logger;
        protected readonly IProviderErrorTranslator _providerErrorTranslator;
        private readonly IBatchSpendUpdateService? _batchSpendService;

        protected MediaGenerationOrchestrator(
            ILLMClientFactory clientFactory,
            IAsyncTaskService taskService,
            IMediaStorageService storageService,
            IEventBus eventBus,
            IModelProviderMappingService modelMappingService,
            IVirtualKeyService virtualKeyService,
            ICostCalculationService costService,
            ICancellableTaskRegistry taskRegistry,
            IWebhookNotificationService webhookService,
            IHttpClientFactory httpClientFactory,
            MinimalParameterValidator parameterValidator,
            MediaGenerationMetrics metrics,
            IProviderErrorTrackingService errorTrackingService,
            ILogger logger,
            IBatchSpendUpdateService? batchSpendService = null,
            IProviderErrorTranslator? providerErrorTranslator = null)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
            _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
            _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _modelMappingService = modelMappingService ?? throw new ArgumentNullException(nameof(modelMappingService));
            _virtualKeyService = virtualKeyService ?? throw new ArgumentNullException(nameof(virtualKeyService));
            _costService = costService ?? throw new ArgumentNullException(nameof(costService));
            _taskRegistry = taskRegistry ?? throw new ArgumentNullException(nameof(taskRegistry));
            _webhookService = webhookService ?? throw new ArgumentNullException(nameof(webhookService));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _parameterValidator = parameterValidator ?? throw new ArgumentNullException(nameof(parameterValidator));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _errorTrackingService = errorTrackingService ?? throw new ArgumentNullException(nameof(errorTrackingService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _batchSpendService = batchSpendService;
            // Default to External so an unwired construction sanitizes rather than leaks.
            _providerErrorTranslator = providerErrorTranslator
                ?? new ProviderErrorTranslator(new CustomerErrorOptions());
        }

        /// <summary>
        /// Template method defining the main processing flow for media generation.
        /// </summary>
        public async Task HandleAsync(TEventRequest request, IEventContext context)
        {
            var stopwatch = Stopwatch.StartNew();
            GenerationModelInfo? modelInfo = null;
            var reservationCreated = false;
            var reservationHandedOff = false;
            var providerInvocationStarted = false;
            var providerInvocationCompleted = false;
            var reservationId = GetRequestId(request);
            var reservationVirtualKeyId = 0;
            var workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

            // Check if request should be processed
            if (!ShouldProcessRequest(request))
            {
                _logger.LogDebug("Skipping request {RequestId} - processing criteria not met", GetRequestId(request));
                return;
            }

            var claimResult = await _taskService.TryClaimTaskAsync(
                GetRequestId(request), workerId, TimeSpan.FromMinutes(15), context.CancellationToken);
            if (claimResult != ConduitLLM.Configuration.Interfaces.AsyncTaskClaimResult.Claimed)
            {
                if (claimResult == ConduitLLM.Configuration.Interfaces.AsyncTaskClaimResult.Missing)
                {
                    throw new InvalidOperationException(
                        $"Media generation task {GetRequestId(request)} does not exist.");
                }

                _logger.LogInformation(
                    "Skipping duplicate media request {RequestId}; claim result was {ClaimResult}",
                    GetRequestId(request), claimResult);
                return;
            }

            using var leaseHeartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var leaseHeartbeat = MaintainTaskLeaseAsync(
                GetRequestId(request), workerId, leaseHeartbeatCts.Token);

            // Start distributed tracing span for the entire generation pipeline
            using var activity = MediaGenerationMetrics.StartGenerationActivity(
                $"media.{GetMediaType().ToLowerInvariant()}.generate",
                GetMediaType(),
                GetModel(request),
                "pending"); // Provider not yet known; updated below after model resolution
            activity?.SetTag("media.request_id", GetRequestId(request));
            activity?.SetTag("media.virtual_key_id", GetVirtualKeyId(request));

            // Create linked cancellation token for this task
            using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);

            // Register task for cancellation support
            _taskRegistry.RegisterTask(GetRequestId(request), taskCts);

            try
            {
                _logger.LogInformation("Processing {MediaType} generation task {RequestId} for model {Model}",
                    GetMediaType(), GetRequestId(request), GetModel(request));

                // The durable claim already moved the task to Processing.
                await PublishStartedEventAsync(request);

                // 3. Get and validate model information
                var virtualKeyIdStr = GetVirtualKeyId(request);
                if (!int.TryParse(virtualKeyIdStr, out var virtualKeyId))
                {
                    throw new ArgumentException($"Virtual key ID must be a valid integer, got: {virtualKeyIdStr}");
                }
                modelInfo = await GetModelInfoAsync(GetModel(request), virtualKeyId);
                if (modelInfo == null)
                {
                    throw new InvalidOperationException($"Model '{GetModel(request)}' is not configured or mapped to a provider. Please check your model configuration.");
                }

                // Update the activity with resolved provider information
                activity?.SetTag("media.provider", modelInfo.ProviderName);
                activity?.SetTag("media.model", modelInfo.ModelId);

                ValidateModelSupport(modelInfo, request);

                // Record generation started metrics
                _metrics.RecordGenerationStarted(
                    GetMediaType(),
                    GetModel(request),
                    modelInfo.ProviderName,
                    virtualKeyIdStr);

                // Update task registry size
                _metrics.UpdateTaskRegistrySize(1);

                // 4. Extract and validate virtual key
                var virtualKey = await ExtractAndValidateVirtualKeyAsync(request);

                // 5. Build the generation request
                var generationRequest = await BuildGenerationRequestAsync(request, modelInfo);

                // 6. Validate parameters
                ValidateParameters(generationRequest);

                // Reserve the request's estimated cost before invoking a high-cost media
                // provider. Redis serializes concurrent reservations for the same group,
                // preventing a thundering herd from all spending the same balance.
                if (_batchSpendService != null)
                {
                    var estimatedCost = await CalculateCostForUsageAsync(
                        modelInfo,
                        CreateEstimatedUsageObject(request),
                        reservationId,
                        "estimate",
                        suppressErrors: false);
                    if (estimatedCost > 0)
                    {
                        reservationVirtualKeyId = virtualKey.Id;
                        reservationCreated = await _batchSpendService.TryReserveSpendAsync(
                            virtualKey.Id,
                            estimatedCost,
                            reservationId);
                        if (!reservationCreated)
                        {
                            throw new UnauthorizedAccessException(
                                $"Insufficient balance to reserve the estimated {GetMediaType().ToLowerInvariant()} generation cost");
                        }
                    }
                }

                // 7. Log generation details
                LogGenerationDetails(request, modelInfo, generationRequest);

                // 8. Execute the actual generation
                if (!await _taskService.MarkProviderInvocationStartedAsync(
                        GetRequestId(request), workerId, taskCts.Token))
                {
                    throw new InvalidOperationException("Media task claim was lost before provider invocation.");
                }
                providerInvocationStarted = true;
                var response = await ExecuteGenerationAsync(generationRequest, modelInfo, virtualKey, taskCts.Token);
                providerInvocationCompleted = await _taskService.MarkProviderInvocationCompletedAsync(
                    GetRequestId(request), workerId, cancellationToken: taskCts.Token);
                if (!providerInvocationCompleted)
                {
                    throw new InvalidOperationException("Media task claim was lost after provider completion.");
                }

                // 9. Calculate cost as soon as the provider has completed generation. Media
                // download/storage remains cancellable, but the provider work is no longer
                // refundable at this point and must be billed even if that later work is cancelled.
                var cost = await CalculateCostAsync(request, modelInfo, response);

                // 10. Update spend before entering the user-cancellable media processing phase.
                if (cost > 0)
                {
                    if (int.TryParse(GetVirtualKeyId(request), out var vkId))
                    {
                        await UpdateSpendAsync(vkId, cost, GetRequestId(request), GetCorrelationId(request));
                        reservationHandedOff = reservationCreated;
                    }
                }

                // 11. Process and store the generated media
                var processedMedia = await ProcessMediaAsync(response, request, modelInfo, virtualKey, taskCts.Token);

                // 12. Complete the task
                await CompleteTaskAsync(request, processedMedia, cost, modelInfo, stopwatch);

                // 13. Send webhook notification if configured
                if (!string.IsNullOrEmpty(GetWebhookUrl(request)))
                {
                    await SendWebhookNotificationAsync(request, processedMedia, stopwatch, "completed");
                }

                activity?.SetTag("media.cost", cost);
                activity?.SetTag("media.duration_seconds", stopwatch.Elapsed.TotalSeconds);

                _logger.LogInformation("Completed {MediaType} generation task {RequestId} in {Duration}s",
                    GetMediaType(), GetRequestId(request), stopwatch.Elapsed.TotalSeconds);
            }
            catch (OperationCanceledException) when (taskCts.Token.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");
                activity?.SetTag("media.outcome", "cancelled");
                if (providerInvocationStarted && !providerInvocationCompleted)
                {
                    await _taskService.UpdateTaskStatusAsync(
                        GetRequestId(request),
                        TaskState.Indeterminate,
                        error: "Provider outcome is unknown after cancellation; automatic retry is disabled.");
                    MediaTaskIdempotencyMetrics.RecordIndeterminate("orchestrator_cancellation");
                    _metrics.UpdateTaskRegistrySize(-1);
                    _logger.LogCritical(
                        "Media generation task {RequestId} was cancelled with an indeterminate provider outcome",
                        GetRequestId(request));
                }
                else
                {
                    await HandleCancellationAsync(request, stopwatch, modelInfo);
                }
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("media.outcome", "failed");
                activity?.SetTag("media.error_type", ex.GetType().Name);
                if (providerInvocationStarted && !providerInvocationCompleted)
                {
                    await _taskService.UpdateTaskStatusAsync(
                        GetRequestId(request),
                        TaskState.Indeterminate,
                        error: $"Provider outcome is unknown; automatic retry is disabled. {ToCustomerError(ex, modelInfo).Message}");
                    MediaTaskIdempotencyMetrics.RecordIndeterminate("orchestrator_exception");
                    _metrics.UpdateTaskRegistrySize(-1);
                    _logger.LogCritical(ex,
                        "Media generation task {RequestId} has an indeterminate provider outcome",
                        GetRequestId(request));
                }
                else
                {
                    await HandleFailureAsync(
                        request,
                        ex,
                        stopwatch,
                        modelInfo,
                        context.CancellationToken);
                }
            }
            finally
            {
                leaseHeartbeatCts.Cancel();
                await leaseHeartbeat;

                if (reservationCreated && !reservationHandedOff && _batchSpendService != null)
                {
                    try
                    {
                        await _batchSpendService.ReleaseSpendReservationAsync(
                            reservationVirtualKeyId,
                            reservationId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Failed to release spend reservation {ReservationId} for Virtual Key {VirtualKeyId}",
                            reservationId,
                            reservationVirtualKeyId);
                    }
                }

                // Always unregister the task from the cancellation registry
                _taskRegistry.UnregisterTask(GetRequestId(request));
            }
        }

        private async Task MaintainTaskLeaseAsync(
            string taskId,
            string workerId,
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                    if (!await _taskService.ExtendTaskLeaseAsync(
                            taskId, workerId, TimeSpan.FromMinutes(15), cancellationToken))
                    {
                        _logger.LogWarning("Lost media task lease for {RequestId}", taskId);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal completion path.
            }
        }

        // Abstract methods that MUST be implemented by derived classes
        protected abstract bool ShouldProcessRequest(TEventRequest request);
        protected abstract Task<TResponse> ExecuteGenerationAsync(TRequest request, GenerationModelInfo modelInfo, VirtualKey virtualKey, CancellationToken cancellationToken);
        protected abstract Task<ProcessedMedia> ProcessMediaAsync(TResponse response, TEventRequest request, GenerationModelInfo modelInfo, VirtualKey virtualKey, CancellationToken cancellationToken);
        protected abstract void ValidateParameters(TRequest request);
        protected abstract Task<TRequest> BuildGenerationRequestAsync(TEventRequest request, GenerationModelInfo modelInfo);
        protected abstract void ValidateModelSupport(GenerationModelInfo modelInfo, TEventRequest request);
        protected abstract Usage CreateUsageObject(TEventRequest request, TResponse response);
        protected abstract Usage CreateEstimatedUsageObject(TEventRequest request);
        protected abstract Task PublishStartedEventAsync(TEventRequest request);
        protected abstract Task PublishCompletedEventAsync(TEventRequest request, ProcessedMedia media, decimal cost, GenerationModelInfo modelInfo, TimeSpan duration);
        /// <summary>
        /// Publishes the failed event for SignalR/webhook consumers. Receives the
        /// customer-mode translated error rather than the raw exception so derived
        /// orchestrators cannot leak provider text past CONDUIT_CUSTOMER_MODE.
        /// </summary>
        protected abstract Task PublishFailedEventAsync(TEventRequest request, CustomerFacingProviderError customerError, bool isRetryable, int retryCount, int maxRetries);
        protected abstract Task PublishProgressEventAsync(TEventRequest request, int current, int total, string status);
        protected abstract object CreateWebhookPayload(TEventRequest request, ProcessedMedia media, TimeSpan duration, string status, string? error = null);
        protected abstract string GetMediaType();
        protected abstract void LogGenerationDetails(TEventRequest request, GenerationModelInfo modelInfo, TRequest generationRequest);

        // Virtual methods with default implementations
        protected virtual async Task<GenerationModelInfo?> GetModelInfoAsync(string modelAlias, int virtualKeyId)
        {
            // Get model mapping
            var mapping = await _modelMappingService.GetMappingByModelAliasAsync(modelAlias);
            if (mapping == null)
            {
                _logger.LogWarning("Model mapping not found for alias {ModelAlias}", modelAlias);
                return null;
            }
            
            var association = mapping.ModelProviderTypeAssociation;

            return new GenerationModelInfo
            {
                // The legacy ProviderModelId column can be stale on mappings created through the
                // model-catalog flow; fall back to the association's canonical identifier so the
                // provider call and cost lookup never receive an empty model id.
                ModelId = !string.IsNullOrWhiteSpace(mapping.ProviderModelId)
                    ? mapping.ProviderModelId
                    : association?.Identifier ?? mapping.ModelAlias,
                ModelAlias = mapping.ModelAlias,
                ProviderId = mapping.ProviderId,
                Provider = mapping.Provider, // Use the Provider navigation property directly
                ModelCostId = association?.ModelCostId,
                CostIdentifier = association?.Identifier
            };
        }

        protected virtual async Task<VirtualKey> ExtractAndValidateVirtualKeyAsync(TEventRequest request)
        {
            // Get task to retrieve the actual virtual key from metadata
            var task = await _taskService.GetTaskStatusAsync(GetRequestId(request));
            if (task?.Metadata == null)
            {
                throw new InvalidOperationException($"Task {GetRequestId(request)} not found or has no metadata");
            }
            
            // Extract virtual key from metadata
            string? virtualKey = null;
            if (task.Metadata is TaskMetadata taskMetadata && taskMetadata.ExtensionData != null)
            {
                if (taskMetadata.ExtensionData.TryGetValue("VirtualKey", out var virtualKeyObj))
                {
                    virtualKey = virtualKeyObj switch
                    {
                        string vk => vk,
                        System.Text.Json.JsonElement jsonElement when jsonElement.ValueKind == System.Text.Json.JsonValueKind.String 
                            => jsonElement.GetString(),
                        _ => null
                    };
                }
            }
            
            if (string.IsNullOrEmpty(virtualKey))
            {
                throw new InvalidOperationException("Virtual key not found in task metadata");
            }
            
            // Validate and get virtual key info
            VirtualKeyValidationOutcome validation;
            try
            {
                validation = await _virtualKeyService.ValidateVirtualKeyAsync(virtualKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate virtual key for task {RequestId}", GetRequestId(request));
                throw new InvalidOperationException($"Virtual key validation failed: {ex.Message}", ex);
            }
            
            if (!validation.IsValid || validation.Key is null)
            {
                throw new UnauthorizedAccessException(
                    $"Virtual key validation failed ({validation.FailureCode ?? "unknown"}): {validation.Reason}");
            }
            
            return validation.Key;
        }

        protected virtual async Task<decimal> CalculateCostAsync(TEventRequest request, GenerationModelInfo modelInfo, TResponse response)
        {
            var usage = CreateUsageObject(request, response);
            return await CalculateCostForUsageAsync(
                modelInfo,
                usage,
                GetRequestId(request),
                "actual",
                suppressErrors: true);
        }

        private async Task<decimal> CalculateCostForUsageAsync(
            GenerationModelInfo modelInfo,
            Usage usage,
            string requestId,
            string calculationKind,
            bool suppressErrors)
        {
            try
            {
                // Prefer the ModelCost link resolved from the model association — string matching can
                // silently return 0 when the mapping's legacy ProviderModelId doesn't match any cost
                // record's identifier (see issue #955).
                if (modelInfo.ModelCostId.HasValue)
                {
                    return await _costService.CalculateCostByIdAsync(modelInfo.ModelCostId.Value, usage);
                }

                // Fall back to string matching using the association's canonical identifier, which is
                // the value cost records are matched against.
                var costLookupModelId = !string.IsNullOrWhiteSpace(modelInfo.CostIdentifier)
                    ? modelInfo.CostIdentifier
                    : modelInfo.ModelId;
                return await _costService.CalculateCostAsync(costLookupModelId, usage);
            }
            catch (Exception ex)
            {
                // The provider has already completed generation. A billing configuration failure
                // must not turn that successful generation into a failed task. Completion at zero
                // cost preserves the existing request log for reconciliation, while this alert makes
                // the revenue-impacting configuration error visible to operators.
                _logger.LogError(ex,
                    "BILLING ALERT: Failed to calculate {CalculationKind} {MediaType} cost for request {RequestId}. " +
                    "The operation cannot be safely billed",
                    calculationKind, GetMediaType(), requestId);

                if (!suppressErrors)
                {
                    throw new InvalidOperationException(
                        $"Unable to estimate the {GetMediaType().ToLowerInvariant()} generation cost",
                        ex);
                }

                return 0m;
            }
        }

        protected virtual bool IsRetryableError(
            Exception ex,
            CancellationToken callerToken) =>
            TransientErrorPolicy.IsTransient(ex, callerToken);

        protected virtual async Task UpdateTaskStatusAsync(string taskId, TaskState state, CancellationToken cancellationToken)
        {
            await _taskService.UpdateTaskStatusAsync(taskId, state, cancellationToken: cancellationToken);
        }

        protected virtual async Task CompleteTaskAsync(TEventRequest request, ProcessedMedia media, decimal cost, GenerationModelInfo modelInfo, Stopwatch stopwatch)
        {
            // Build data array from processed media items in OpenAI-compatible format
            // This format is expected by SDKs: { created, data: [{ url, metadata }], model, usage }
            var dataItems = new List<MediaGenerationTaskResultItem>();

            if (media.Items.Any())
            {
                foreach (var item in media.Items)
                {
                    dataItems.Add(new MediaGenerationTaskResultItem
                    {
                        Url = item.Url,
                        Metadata = item.Metadata.Count > 0 ? item.Metadata : null
                    });
                }
            }
            else if (!string.IsNullOrEmpty(media.Url))
            {
                // Single item case - wrap in data array
                dataItems.Add(new MediaGenerationTaskResultItem
                {
                    Url = media.Url,
                    Metadata = media.Metadata.Count > 0 ? media.Metadata : null
                });
            }

            // Create result in OpenAI-compatible format that SDKs expect
            // Both ImageGenerationResponse and VideoGenerationResponse share this structure
            var result = new MediaGenerationTaskResult
            {
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Data = dataItems,
                Model = modelInfo.ModelId,
                Usage = new MediaGenerationTaskUsage
                {
                    // Generic usage info - specific orchestrators can override if needed
                    Count = media.Count,
                    DurationSeconds = stopwatch.Elapsed.TotalSeconds
                },
                // Additional metadata for internal use (not part of OpenAI spec but useful)
                InternalMetadata = new MediaGenerationTaskInternalMetadata
                {
                    Cost = cost,
                    Provider = modelInfo.ProviderName,
                    GenerationDurationSeconds = stopwatch.Elapsed.TotalSeconds
                }
            };

            // Record completion metrics
            _metrics.RecordGenerationCompleted(
                GetMediaType(),
                modelInfo.ModelId,
                modelInfo.ProviderName,
                GetVirtualKeyId(request),
                stopwatch.Elapsed.TotalSeconds,
                (double)cost);

            // Update task registry size
            _metrics.UpdateTaskRegistrySize(-1);

            await _taskService.UpdateTaskStatusAsync(
                GetRequestId(request),
                TaskState.Completed,
                progress: 100,
                result: result);

            await PublishCompletedEventAsync(request, media, cost, modelInfo, stopwatch.Elapsed);
        }

        protected virtual async Task HandleCancellationAsync(TEventRequest request, Stopwatch stopwatch, GenerationModelInfo? modelInfo)
        {
            _logger.LogInformation("{MediaType} generation task {RequestId} was cancelled after {Duration}ms",
                GetMediaType(), GetRequestId(request), stopwatch.ElapsedMilliseconds);
                
            // Record cancellation metrics if model info is available
            if (modelInfo != null)
            {
                _metrics.RecordGenerationCancelled(
                    GetMediaType(),
                    modelInfo.ModelId,
                    modelInfo.ProviderName,
                    GetVirtualKeyId(request),
                    "user_request",
                    stopwatch.Elapsed.TotalSeconds);
            }
            else
            {
                // Record cancellation with minimal info if model info not available
                _metrics.RecordGenerationCancelled(
                    GetMediaType(),
                    GetModel(request),
                    "unknown",
                    GetVirtualKeyId(request),
                    "early_cancellation",
                    stopwatch.Elapsed.TotalSeconds);
            }
            
            // Update task registry size
            _metrics.UpdateTaskRegistrySize(-1);
            
            await _taskService.UpdateTaskStatusAsync(
                GetRequestId(request),
                TaskState.Cancelled,
                error: "Task was cancelled by user request");
            
            if (!string.IsNullOrEmpty(GetWebhookUrl(request)))
            {
                await SendWebhookNotificationAsync(request, null, stopwatch, "cancelled");
            }
        }

        protected virtual async Task HandleFailureAsync(
            TEventRequest request,
            Exception ex,
            Stopwatch stopwatch,
            GenerationModelInfo? modelInfo,
            CancellationToken callerToken)
        {
            _logger.LogError(ex, "{MediaType} generation failed for task {RequestId}", 
                GetMediaType(), GetRequestId(request));
            
            // Check if error is retryable
            var isRetryable = IsRetryableError(ex, callerToken);
            
            // Categorize error for metrics
            var (errorType, errorCategory) = CategorizeError(ex);
            
            // Record failure metrics
            if (modelInfo != null)
            {
                _metrics.RecordGenerationFailed(
                    GetMediaType(),
                    modelInfo.ModelId,
                    modelInfo.ProviderName,
                    GetVirtualKeyId(request),
                    errorType,
                    errorCategory,
                    stopwatch.Elapsed.TotalSeconds,
                    isRetryable);
            }
            else
            {
                // Record failure with minimal info if model info not available
                _metrics.RecordGenerationFailed(
                    GetMediaType(),
                    GetModel(request),
                    "unknown",
                    GetVirtualKeyId(request),
                    errorType,
                    errorCategory,
                    stopwatch.Elapsed.TotalSeconds,
                    isRetryable);
            }
            
            // Update task registry size
            _metrics.UpdateTaskRegistrySize(-1);
            
            // Everything below reaches customers (task status polls, SignalR events,
            // webhook payloads), so it carries the customer-mode translated error.
            // The raw exception stays in the log above and in provider error tracking.
            var customerError = ToCustomerError(ex, modelInfo);

            await _taskService.UpdateTaskStatusAsync(
                GetRequestId(request),
                TaskState.Failed,
                error: customerError.Message);

            // Track in provider error system for dashboard visibility and auto-disable policies
            await TrackProviderErrorFromExceptionAsync(ex, modelInfo);

            await PublishFailedEventAsync(request, customerError, isRetryable, 0, 0);

            if (!string.IsNullOrEmpty(GetWebhookUrl(request)))
            {
                await SendWebhookNotificationAsync(request, null, stopwatch, "failed", customerError.Message);
            }
        }

        /// <summary>
        /// Produces the customer-visible error for a failed generation. Provider errors go
        /// through the CONDUIT_CUSTOMER_MODE translator; Conduit's own failures (virtual-key
        /// validation, model mapping, storage) keep their message — that text is generated by
        /// Conduit, is safe in either mode, and tells the customer what to fix.
        /// </summary>
        private CustomerFacingProviderError ToCustomerError(Exception ex, GenerationModelInfo? modelInfo)
        {
            var isProviderError = LLMCommunicationException.FindWithStatus(ex) is not null
                || ProviderErrorClassifier.ClassifyException(ex) != ProviderErrorType.Unknown;

            return isProviderError
                ? _providerErrorTranslator.Translate(ex, modelInfo?.ProviderName)
                : new CustomerFacingProviderError(
                    ex.Message, ProviderErrorType.Unknown, Detail: null,
                    ErrorCodeOverride: ex.GetType().Name);
        }

        /// <summary>
        /// Tracks a provider error from an exception using the provider error tracking system.
        /// </summary>
        private async Task TrackProviderErrorFromExceptionAsync(Exception ex, GenerationModelInfo? modelInfo)
        {
            try
            {
                if (modelInfo?.Provider == null)
                {
                    _logger.LogDebug("Cannot track provider error — no provider context available");
                    return;
                }

                var keyCredentialId = modelInfo.Provider.ProviderKeyCredentials?
                    .FirstOrDefault(k => k.IsPrimary)?.Id
                    ?? modelInfo.Provider.ProviderKeyCredentials?.FirstOrDefault()?.Id;

                if (keyCredentialId == null)
                {
                    _logger.LogDebug("Cannot track provider error — no key credential found for provider {ProviderId}",
                        modelInfo.ProviderId);
                    return;
                }

                var errorType = ProviderErrorClassifier.ClassifyException(ex);
                if (!ProviderErrorClassifier.ShouldTrack(errorType))
                {
                    return;
                }

                var communicationException = LLMCommunicationException.FindWithStatus(ex);

                var errorInfo = new ProviderErrorInfo
                {
                    KeyCredentialId = keyCredentialId.Value,
                    ProviderId = modelInfo.ProviderId,
                    ErrorType = errorType,
                    ErrorMessage = ex.Message,
                    HttpStatusCode = communicationException?.StatusCode is { } statusCode
                        ? (int)statusCode
                        : null,
                    ModelName = modelInfo.ModelId,
                    OccurredAt = DateTime.UtcNow
                };

                await _errorTrackingService.TrackErrorAsync(errorInfo);

                _logger.LogInformation("Tracked {MediaType} generation provider error: Type={ErrorType}, Provider={ProviderId}, Key={KeyCredentialId}, Model={Model}",
                    GetMediaType(), errorType, modelInfo.ProviderId, keyCredentialId, modelInfo.ModelId);
            }
            catch (Exception trackEx)
            {
                _logger.LogWarning(trackEx, "Failed to track provider error for {MediaType} generation", GetMediaType());
            }
        }

        protected virtual async Task UpdateSpendAsync(int virtualKeyId, decimal amount, string requestId, string? correlationId)
        {
            await _eventBus.PublishAsync(new SpendUpdateRequested
            {
                KeyId = virtualKeyId,
                Amount = amount,
                RequestId = requestId,
                CorrelationId = correlationId ?? string.Empty
            });
        }

        protected virtual async Task SendWebhookNotificationAsync(TEventRequest request, ProcessedMedia? media, Stopwatch stopwatch, string status, string? error = null)
        {
            var payload = CreateWebhookPayload(request, media ?? new ProcessedMedia(), stopwatch.Elapsed, status, error);
            
            var eventType = status switch
            {
                "completed" => WebhookEventType.TaskCompleted,
                "failed" => WebhookEventType.TaskFailed,
                "cancelled" => WebhookEventType.TaskCancelled,
                _ => WebhookEventType.TaskProgress
            };
            
            await _eventBus.PublishAsync(new WebhookDeliveryRequested
            {
                TaskId = GetRequestId(request),
                TaskType = GetMediaType().ToLowerInvariant(),
                WebhookUrl = GetWebhookUrl(request)!,
                EventType = eventType,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                    payload,
                    Serialization.CoreJsonTypeInfo.Require(
                        payload.GetType(),
                        Serialization.ConduitJsonOptions.Compact)),
                Headers = GetWebhookHeaders(request),
                CorrelationId = GetCorrelationId(request) ?? Guid.NewGuid().ToString()
            });
            
            _logger.LogDebug("Published webhook delivery event for {Status} {MediaType} task {RequestId}",
                status, GetMediaType(), GetRequestId(request));
        }

        /// <summary>
        /// Categorizes exceptions into error types and categories for metrics tracking
        /// </summary>
        protected virtual (string ErrorType, string ErrorCategory) CategorizeError(Exception ex)
        {
            var providerErrorType = ProviderErrorClassifier.ClassifyException(ex);
            if (providerErrorType == ProviderErrorType.Unknown)
            {
                providerErrorType = ProviderErrorClassifier.ClassifyFailure(errorCode: null, ex.Message);
            }

            if (providerErrorType != ProviderErrorType.Unknown)
            {
                return (
                    ProviderErrorClassifier.ToMetricLabel(providerErrorType),
                    ProviderErrorClassifier.ToMetricCategory(providerErrorType));
            }

            return ex switch
            {
                TaskCanceledException => ("timeout", "task_timeout"),
                OperationCanceledException => ("timeout", "cancellation"),
                TimeoutException => ("timeout", "provider_timeout"),
                HttpRequestException => ("network", "http_error"),
                ArgumentException => ("validation", "parameter_error"),
                UnauthorizedAccessException => ("authentication", "auth_error"),
                InvalidOperationException => ("validation", "operation_error"),
                NotImplementedException => ("provider", "not_implemented"),
                System.Net.Sockets.SocketException => ("network", "socket_error"),
                System.IO.IOException => ("storage", "io_error"),
                OutOfMemoryException => ("resource", "memory_error"),
                _ when ex.Message.Contains("bad request", StringComparison.OrdinalIgnoreCase) => ("validation", "bad_request"),
                _ => ("unknown", "unclassified")
            };
        }
    }
}
