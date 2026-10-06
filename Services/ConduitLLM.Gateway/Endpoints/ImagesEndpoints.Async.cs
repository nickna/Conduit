using System.Diagnostics;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Gateway.Constants;
using ConduitLLM.Gateway.UsageTracking;
using GatewayOpsMetrics = ConduitLLM.Gateway.Services.GatewayOperationsMetricsService;
using ConduitLLM.Gateway.DTOs;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Gateway.Serialization;

namespace ConduitLLM.Gateway.Endpoints
{
    /// <summary>
    /// Images controller - Asynchronous image generation functionality
    /// </summary>
    public partial class ImagesEndpoints
    {
        /// <summary>
        /// Creates an async image generation task.
        /// </summary>
        /// <param name="request">The image generation request.</param>
        /// <returns>Task information with status URL.</returns>
        public async Task<IResult> CreateImageAsync(ConduitLLM.Core.Models.ImageGenerationRequest request)
        {
            using var activity = GatewayRequestMetrics.StartImageGenerationActivity(
                request.Model ?? "unknown", isAsync: true);

            try
            {
                // Validate request
                if (string.IsNullOrWhiteSpace(request.Prompt))
                {
                    return OpenAIError(400, "Prompt is required", "missing_parameter", "invalid_request_error", "prompt");
                }

                var modelName = request.Model ?? "dall-e-2";
                request.Model = modelName;
                
                // Check model capabilities
                var mapping = await _modelMappingService.GetMappingByModelAliasAsync(modelName);
                bool supportsImageGen = false;
                
                if (mapping != null)
                {
                    supportsImageGen = mapping.ModelProviderTypeAssociation?.Model?.SupportsImageGeneration ?? false;
                    _logger.LogInformation("Model {Model} mapping found, supports image generation: {Supports}",
                        LoggingSanitizer.S(modelName), supportsImageGen);
                    HttpContext.Items["ProviderId"] = mapping.ProviderId;
                    HttpContext.Items["ProviderType"] = mapping.Provider?.ProviderType;
                    if (mapping.ModelProviderTypeAssociation?.ModelCostId is int modelCostId)
                        HttpContext.Items[HttpContextKeys.ModelCostId] = modelCostId;
                }
                else
                {
                    _logger.LogWarning("No mapping found for model {Model}. Model must be configured in model mappings.", LoggingSanitizer.S(modelName));
                    supportsImageGen = false;
                }
                
                if (!supportsImageGen)
                {
                    return OpenAIError(400, $"Model {modelName} does not support image generation", "unsupported_model", "invalid_request_error", "model");
                }

                // The raw key is required too: MediaGenerationOrchestrator re-validates it
                // from task metadata, so a task created without it would always fail.
                var virtualKeyValue = CurrentVirtualKey;
                if (CurrentVirtualKeyId == null || string.IsNullOrEmpty(virtualKeyValue))
                {
                    return OpenAIError(401, "Virtual key not found in request context", "unauthorized");
                }
                var virtualKeyId = CurrentVirtualKeyId.Value;
                HttpContext.SetUsageContext(new ImageUsageContext
                {
                    Model = modelName,
                    Quality = request.Quality,
                    Size = request.Size,
                    N = request.N,
                    Style = request.Style
                });
                var accounting = HttpContext.GetOrCreateRequestAccountingContext();
                accounting.SetOperation(RequestOperation.Image, virtualKeyId, modelName);
                accounting.RecordProviderUsage(new Usage
                {
                    ImageCount = request.N,
                    ImageQuality = request.Quality,
                    ImageResolution = request.Size
                }, modelName, UsageEvidenceSource.Estimated);

                // Get virtual key information from service
                var virtualKey = await _virtualKeyService.GetVirtualKeyInfoForValidationAsync(virtualKeyId);
                if (virtualKey == null)
                {
                    return OpenAIError(401, "Virtual key not found", "unauthorized");
                }

                // Create correlation ID
                var correlationId = Guid.NewGuid().ToString();

                // Create the generation request event first so we can store it as metadata
                var generationRequest = new ImageGenerationRequested
                {
                    TaskId = "", // Will be filled in after task creation
                    VirtualKeyId = virtualKeyId,
                    VirtualKeyHash = virtualKey.KeyHash,
                    Request = request,
                    UserId = HttpContext.User.FindFirst("sub")?.Value ?? "anonymous",
                    Priority = 0, // Normal priority
                    RequestedAt = DateTime.UtcNow,
                    CorrelationId = correlationId
                };

                // Create metadata for the task including the serialized request.
                // The orchestrator reads ExtensionData["VirtualKey"] for re-validation
                // (MediaGenerationOrchestrator.ProcessRequestAsync) — without it every
                // async image task fails with "Virtual key not found in task metadata".
                var metadata = new TaskMetadata(virtualKeyId)
                {
                    Model = modelName,
                    Prompt = request.Prompt,
                    CorrelationId = correlationId,
                    Payload = System.Text.Json.JsonSerializer.Serialize(
                        generationRequest,
                        CoreMessagingJsonContext.Default.ImageGenerationRequested),
                    ExtensionData = new Dictionary<string, object>
                    {
                        // MediaGenerationOrchestrator re-validates the raw key from task metadata
                        ["VirtualKey"] = virtualKeyValue
                    }
                };

                // Create the task using the correct method signature
                var taskId = await _taskService.CreateTaskAsync(
                    taskType: "image_generation",
                    virtualKeyId: virtualKeyId,
                    metadata: metadata);

                // Update the request with the actual task ID
                generationRequest = generationRequest with { TaskId = taskId };

                // Publish the event directly to the event bus for immediate processing
                PublishEventFireAndForget(generationRequest, "create async image generation", new { TaskId = taskId, Model = modelName });
                
                _logger.LogInformation("Created async image generation task {TaskId} for model {Model} and published event",
                    taskId, LoggingSanitizer.S(modelName));

                // Return accepted response with task information
                var response = new AsyncTaskResponse
                {
                    TaskId = taskId,
                    Status = TaskStateConstants.Queued, 
                    CheckStatusUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/v1/images/generations/{Uri.EscapeDataString(taskId)}/status",
                    CreatedAt = DateTime.UtcNow
                };
                accounting.RecordMetadata(System.Text.Json.JsonSerializer.Serialize(
                    new MediaTaskAccountingMetadata(
                        "image",
                        taskId,
                        TaskStateConstants.Queued,
                        ImageCount: request.N,
                        Quality: request.Quality,
                        Size: request.Size,
                        Style: request.Style),
                    GatewayInternalJsonContext.Default.MediaTaskAccountingMetadata));

                GatewayOpsMetrics.RecordMediaOperation("generate", "image_async", "queued");
                return Accepted(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating async image generation task");
                GatewayOpsMetrics.RecordMediaOperation("generate", "image_async", "error");
                return OpenAIError(500, "An error occurred while creating the task", "internal_error", "server_error");
            }
        }

        /// <summary>
        /// Gets the status of an async image generation task.
        /// </summary>
        /// <param name="taskId">The task ID.</param>
        /// <returns>Current task status and results if completed.</returns>
        public async Task<IResult> GetGenerationStatus(string taskId)
        {
            try
            {
                _logger.LogInformation("GetGenerationStatus called for task {TaskId}", taskId);
                
                // Get task from service
                var task = await _taskService.GetTaskStatusAsync(taskId);
                if (task == null)
                {
                    _logger.LogWarning("Task {TaskId} not found by task service", taskId);
                    return OpenAIError(404, "Task not found", "not_found", "invalid_request_error", "task_id");
                }
                
                _logger.LogInformation("Task {TaskId} retrieved, State: {State}, HasMetadata: {HasMetadata}",
                    taskId, task.State, task.Metadata != null);

                // Verify user owns this task. Return 404 (not 403) to avoid leaking task existence.
                var callerVirtualKeyId = CurrentVirtualKeyId;
                if (callerVirtualKeyId != null && task.Metadata != null && task.Metadata.VirtualKeyId != callerVirtualKeyId.Value)
                {
                    _logger.LogWarning("Virtual key {CallerKeyId} attempted to access task {TaskId} owned by {OwnerKeyId}",
                        callerVirtualKeyId.Value, taskId, task.Metadata.VirtualKeyId);
                    return OpenAIError(404, "Task not found", "not_found", "invalid_request_error", "task_id");
                }

                // Build response
                var response = new AsyncTaskStatusResponse
                {
                    TaskId = task.TaskId,
                    Status = TaskStateConstants.FromTaskState(task.State),
                    CreatedAt = task.CreatedAt,
                    UpdatedAt = task.UpdatedAt,
                    CompletedAt = task.CompletedAt,
                    Progress = task.Progress,
                    Result = task.State == TaskState.Completed ? task.Result : null,
                    Error = task.State == TaskState.Failed ? task.Error : null
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting task status for {TaskId}", taskId);
                return OpenAIError(500, "An error occurred while getting task status", "internal_error", "server_error");
            }
        }

        /// <summary>
        /// Cancels an async image generation task.
        /// </summary>
        /// <param name="taskId">The task ID to cancel.</param>
        /// <returns>Cancellation result.</returns>
        public async Task<IResult> CancelGeneration(string taskId)
        {
            try
            {
                // Get task from service
                var task = await _taskService.GetTaskStatusAsync(taskId);
                if (task == null)
                {
                    return OpenAIError(404, "Task not found", "not_found", "invalid_request_error", "task_id");
                }

                // Verify user owns this task. Return 404 (not 403) to avoid leaking task existence.
                var callerVirtualKeyId = CurrentVirtualKeyId;
                if (callerVirtualKeyId != null && task.Metadata != null && task.Metadata.VirtualKeyId != callerVirtualKeyId.Value)
                {
                    _logger.LogWarning("Virtual key {CallerKeyId} attempted to cancel task {TaskId} owned by {OwnerKeyId}",
                        callerVirtualKeyId.Value, taskId, task.Metadata.VirtualKeyId);
                    return OpenAIError(404, "Task not found", "not_found", "invalid_request_error", "task_id");
                }

                // Check if task can be cancelled
                if (task.State == TaskState.Completed || task.State == TaskState.Failed || task.State == TaskState.Cancelled)
                {
                    return OpenAIError(400, "Task has already completed", "invalid_operation");
                }

                // Publish cancellation event using the task owner's virtual key ID
                PublishEventFireAndForget(new ImageGenerationCancelled
                {
                    TaskId = taskId,
                    VirtualKeyId = task.Metadata?.VirtualKeyId ?? 0,
                    Reason = "Cancelled by user request",
                    CancelledAt = DateTime.UtcNow,
                    CorrelationId = Guid.NewGuid().ToString()
                }, "cancel image generation", new { TaskId = taskId });

                _logger.LogInformation("Published cancellation event for image generation task {TaskId}", taskId);

                return Ok(new TaskCancellationResponse("Task cancellation requested", taskId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling task {TaskId}", taskId);
                return OpenAIError(500, "An error occurred while cancelling the task", "internal_error", "server_error");
            }
        }
    }
}
