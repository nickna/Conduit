using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ConduitLLM.Configuration.Interfaces;
namespace ConduitLLM.Core
{
    /// <summary>
    /// Main entry point for interacting with the ConduitLLM library.
    /// Orchestrates calls to different LLM providers based on configuration via an <see cref="ILLMClientFactory"/>.
    /// </summary>
    public class Conduit : IConduit
    {
        private readonly ILLMClientFactory _clientFactory;
        private readonly IContextManager? _contextManager;
        private readonly IModelProviderMappingService? _modelProviderMappingService;
        private readonly IOptions<ContextManagementOptions>? _contextOptions;
        private readonly IFunctionDiscoveryService? _functionDiscoveryService;
        private readonly IAgenticOrchestrationService? _agenticOrchestrationService;
        private readonly ILogger<Conduit> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="Conduit"/> class.
        /// </summary>
        /// <param name="clientFactory">The factory used to obtain provider-specific LLM clients.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="contextManager">Optional context manager for handling token limits.</param>
        /// <param name="modelProviderMappingService">Optional service to retrieve model mappings.</param>
        /// <param name="contextOptions">Optional configuration for context management.</param>
        /// <param name="functionDiscoveryService">Optional service for discovering and converting function configurations.</param>
        /// <param name="agenticOrchestrationService">Optional service for orchestrating agentic function calling workflows.</param>
        /// <exception cref="ArgumentNullException">Thrown if clientFactory is null.</exception>
        public Conduit(
            ILLMClientFactory clientFactory,
            ILogger<Conduit> logger,
            IContextManager? contextManager = null,
            IModelProviderMappingService? modelProviderMappingService = null,
            IOptions<ContextManagementOptions>? contextOptions = null,
            IFunctionDiscoveryService? functionDiscoveryService = null,
            IAgenticOrchestrationService? agenticOrchestrationService = null)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _contextManager = contextManager;
            _modelProviderMappingService = modelProviderMappingService;
            _contextOptions = contextOptions;
            _functionDiscoveryService = functionDiscoveryService;
            _agenticOrchestrationService = agenticOrchestrationService;
        }

        /// <summary>
        /// Creates a chat completion using the configured LLM providers.
        /// </summary>
        /// <param name="request">The chat completion request, including the target model alias.</param>
        /// <param name="apiKey">Optional API key to override the configured key for this request.</param>
        /// <param name="virtualKeyId">Optional virtual key ID for function execution billing. Required when using function calling.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The chat completion response from the selected LLM provider.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the request is null.</exception>
        /// <exception cref="ArgumentException">Thrown if the request.Model is null or whitespace.</exception>
        /// <exception cref="ConfigurationException">Thrown if configuration for the requested model is invalid or missing.</exception>
        /// <exception cref="UnsupportedProviderException">Thrown if the provider for the requested model is not supported.</exception>
        /// <exception cref="LLMCommunicationException">Thrown if communication with the LLM provider fails.</exception>
        public async Task<ChatCompletionResponse> CreateChatCompletionAsync(
            ChatCompletionRequest request,
            string? apiKey = null,
            int? virtualKeyId = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (string.IsNullOrWhiteSpace(request.Model))
            {
                throw new ArgumentException("The request must specify a target Model alias.", "request.Model");
            }

            var originalRequest = request;
            request = await ApplyContextManagementAsync(request);
            try
            {
                // Check if function calling is requested
                if (request.FunctionConfigurationIds?.Any() == true)
                {
                    return await CreateChatCompletionWithFunctionsAsync(request, apiKey, virtualKeyId ?? 0, cancellationToken);
                }

                // Get a chat-routed client; non-chat APIs continue to use deterministic alias mapping.
                ILLMClient client = await GetChatClientAsync(request, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                if (request.ProviderInvocationStarting is { } starting)
                    await starting(1, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var response = await client.CreateChatCompletionAsync(request, apiKey, cancellationToken).ConfigureAwait(false);
                request.ProviderInvocationCompleted?.Invoke(1, response, response.Usage);
                return response;
            }
            finally
            {
                request.CopyRuntimeStateTo(originalRequest);
            }
        }

        /// <summary>
        /// Creates a streaming chat completion using the configured LLM providers.
        /// </summary>
        /// <param name="request">The chat completion request, including the target model alias.</param>
        /// <param name="apiKey">Optional API key to override the configured key for this request.</param>
        /// <param name="virtualKeyId">Optional virtual key ID for function execution billing. Required when using function calling.</param>
        /// <param name="onToolExecutingEvent">Optional callback invoked when tool execution status changes (started, completed, failed). Used to emit real-time SSE events for agentic workflows.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>An asynchronous enumerable of chat completion chunks from the selected LLM provider.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the request is null.</exception>
        /// <exception cref="ArgumentException">Thrown if the request.Model is null or whitespace.</exception>
        /// <exception cref="ConfigurationException">Thrown if configuration for the requested model is invalid or missing.</exception>
        /// <exception cref="UnsupportedProviderException">Thrown if the provider for the requested model is not supported.</exception>
        /// <exception cref="LLMCommunicationException">Thrown if communication with the LLM provider fails during streaming.</exception>
        public async IAsyncEnumerable<ChatCompletionChunk> StreamChatCompletionAsync(
            ChatCompletionRequest request,
            string? apiKey = null,
            int? virtualKeyId = null,
            Func<ToolExecutionEvent, CancellationToken, Task>? onToolExecutingEvent = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (string.IsNullOrWhiteSpace(request.Model))
            {
                throw new ArgumentException("The request must specify a target Model alias.", "request.Model");
            }

            var originalRequest = request;
            request = await ApplyContextManagementAsync(request);
            try
            {
                var hasFunctionConfigs = request.FunctionConfigurationIds != null && request.FunctionConfigurationIds.Count > 0;

                if (hasFunctionConfigs)
                {
                    await foreach (var chunk in StreamChatCompletionWithFunctionsAsync(request, apiKey, virtualKeyId ?? 0, onToolExecutingEvent, cancellationToken))
                    {
                        request.CopyRuntimeStateTo(originalRequest);
                        yield return chunk;
                    }
                }
                else
                {
                    ILLMClient client = await GetChatClientAsync(request, cancellationToken);
                    await foreach (var chunk in StreamProviderInvocationAsync(request, client, apiKey, 1, null, cancellationToken))
                    {
                        request.CopyRuntimeStateTo(originalRequest);
                        yield return chunk;
                    }
                }
            }
            finally
            {
                request.CopyRuntimeStateTo(originalRequest);
            }
        }

        private async IAsyncEnumerable<ChatCompletionChunk> StreamProviderInvocationAsync(
            ChatCompletionRequest request,
            ILLMClient client,
            string? apiKey,
            int invocation,
            Usage? previousUsage,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.ProviderInvocationStarting is { } starting)
                await starting(invocation, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var provider = client.StreamChatCompletionAsync(request, apiKey, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            Usage? currentUsage = null;
            var exhausted = false;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!await provider.MoveNextAsync().ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        break;
                    }
                    var chunk = provider.Current;
                    chunk.ProviderIteration = invocation;
                    if (chunk.Usage is { } rawUsage)
                    {
                        currentUsage = rawUsage;
                        chunk.ProviderCallUsage = new ProviderCallUsage { Iteration = invocation, Usage = rawUsage };
                        chunk.Usage = AggregateUsage(previousUsage, rawUsage);
                    }
                    // Publish cumulative raw evidence before the caller can yield to client transport.
                    request.ProviderStreamUsageObserved?.Invoke(invocation, chunk, AggregateUsage(previousUsage, currentUsage));
                    yield return chunk;
                }
                exhausted = true;
            }
            finally
            {
                try { await provider.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanupEx) when (!exhausted)
                {
                    // Cleanup must preserve a provider/cancellation/client-write failure that
                    // already interrupted this invocation. Its last raw usage is still retained.
                    _logger.LogError(cleanupEx, "Provider stream cleanup failed after invocation {Invocation} was interrupted", invocation);
                }
            }
            // A finish-reason chunk may precede a revised usage report. Only exhaustion
            // establishes that this invocation cannot produce additional billable usage.
            request.ProviderStreamCompleted?.Invoke(invocation);
        }

        /// <summary>
        /// Creates a chat completion with function calling support and optional agentic mode.
        /// </summary>
        /// <param name="request">The chat completion request.</param>
        /// <param name="apiKey">Optional API key override.</param>
        /// <param name="virtualKeyId">The virtual key ID for function execution billing.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        private async Task<ChatCompletionResponse> CreateChatCompletionWithFunctionsAsync(
            ChatCompletionRequest request,
            string? apiKey,
            int virtualKeyId,
            CancellationToken cancellationToken)
        {
            if (_functionDiscoveryService == null || _agenticOrchestrationService == null)
            {
                throw new InvalidOperationException(
                    "Function calling requires FunctionDiscoveryService and AgenticOrchestrationService to be configured.");
            }

            var chatCompletionId = Guid.NewGuid();
            var requestId = Guid.NewGuid().ToString();

            // Load function definitions and create name-to-ID mapping
            var tools = await _functionDiscoveryService.GetToolsForFunctionConfigurationsAsync(
                request.FunctionConfigurationIds!,
                virtualKeyId,
                cancellationToken);

            var functionNameToIdMap = await _functionDiscoveryService.GetFunctionNameToIdMappingAsync(
                request.FunctionConfigurationIds!,
                cancellationToken);

            // Inject tools into request
            request.Tools = tools;

            // Initialize agentic metrics
            var agenticMetrics = new AgenticExecutionMetrics
            {
                TotalIterations = 0
            };

            var iteration = 0;
            // Defaults are now applied by the Chat endpoints from GlobalSettings, so these are just safety fallbacks
            var maxIterations = request.MaxAgenticIterations ?? 5;
            var agenticModeEnabled = request.EnableAgenticMode ?? true;

            ILLMClient client = await GetChatClientAsync(request, cancellationToken);
            ChatCompletionResponse? response = null;
            Usage? accumulatedUsage = null;
            var invocationOrdinal = 0;

            async Task<ChatCompletionResponse> InvokeProviderAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ordinal = invocationOrdinal + 1;
                if (request.ProviderInvocationStarting is { } starting)
                    await starting(ordinal, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                invocationOrdinal = ordinal;
                var result = await client.CreateChatCompletionAsync(request, apiKey, cancellationToken).ConfigureAwait(false);
                CaptureProviderCall(agenticMetrics, ordinal, result.Usage);
                accumulatedUsage = AggregateUsage(accumulatedUsage, result.Usage);
                request.ProviderInvocationCompleted?.Invoke(ordinal, result, accumulatedUsage);
                return result;
            }

            while (iteration < maxIterations)
            {
                iteration++;
                agenticMetrics.TotalIterations = iteration;

                _logger.LogDebug("Agentic iteration {Iteration} for request {RequestId}", iteration, requestId);

                // Call LLM
                response = await InvokeProviderAsync();

                // Check if LLM wants to call functions
                var choice = response.Choices.FirstOrDefault();
                if (choice?.Message?.ToolCalls == null || choice.Message.ToolCalls.Count == 0 ||
                    choice.FinishReason != FinishReason.ToolCalls)
                {
                    // No tool calls, we're done
                    break;
                }

                // Edge case: Model returned tool_calls but no functions were configured in the request
                // This can happen with some models (e.g., gpt-oss-20b) that try to call functions even when none are available
                if (functionNameToIdMap.Count == 0)
                {
                    _logger.LogWarning(
                        "Model returned finish_reason='tool_calls' but no functions were configured in the request. " +
                        "Model: {Model}, Tool calls: {ToolCalls}",
                        request.Model,
                        string.Join(", ", choice.Message.ToolCalls.Select(tc => tc.Function?.Name ?? "unknown")));

                    // Add a system message instructing the model to answer without functions
                    request.Messages.Add(new Message
                    {
                        Role = MessageRole.System,
                        Content = "No functions are available. Please provide a direct answer to the user's question without attempting to call any functions."
                    });

                    // Make one more call to get a proper response
                    response = await InvokeProviderAsync();
                    break;
                }

                // If agentic mode is disabled, return the response with tool calls for manual handling
                if (!agenticModeEnabled)
                {
                    _logger.LogInformation("Tool calls detected but agentic mode is disabled. Returning for manual handling.");
                    break;
                }

                // Execute tool calls
                var executionResult = await _agenticOrchestrationService.ExecuteToolCallsAsync(
                    choice.Message.ToolCalls,
                    virtualKeyId,
                    functionNameToIdMap,
                    requestId,
                    chatCompletionId,
                    iteration,
                    cancellationToken);

                // Aggregate metrics
                agenticMetrics.FunctionCalls.AddRange(executionResult.FunctionCallSummaries);
                agenticMetrics.TotalFunctionCalls += executionResult.FunctionCallSummaries.Count;
                agenticMetrics.TotalFunctionCost += executionResult.TotalFunctionCost;
                request.FunctionExecutionCompleted?.Invoke(agenticMetrics);

                // Append assistant's message with tool calls to conversation
                request.Messages.Add(choice.Message);

                // Append tool result messages to conversation
                request.Messages.AddRange(executionResult.ToolResultMessages);

                // If all functions failed, break the loop
                if (!executionResult.AllSucceeded)
                {
                    _logger.LogWarning("Some function executions failed in iteration {Iteration}. Errors: {Errors}",
                        iteration, string.Join("; ", executionResult.Errors));
                    // Continue anyway - let the LLM handle the errors
                }
            }

            // Check if we hit the iteration limit
            if (iteration >= maxIterations && response?.Choices.FirstOrDefault()?.FinishReason == FinishReason.ToolCalls)
            {
                _logger.LogWarning("Reached maximum agentic iterations ({MaxIterations}) with pending tool calls", maxIterations);

                // Append a system message explaining we hit the limit
                request.Messages.Add(new Message
                {
                    Role = MessageRole.System,
                    Content = $"Maximum iteration limit ({maxIterations}) reached. Please provide a response based on the information available."
                });

                // Make one final call to get a response
                response = await InvokeProviderAsync();
            }

            if (response != null)
            {
                response.Usage = accumulatedUsage;
            }

            // Attach agentic metrics to response
            if (response != null && agenticMetrics.TotalIterations > 0)
            {
                agenticMetrics.TotalCost = agenticMetrics.TotalLLMCost + agenticMetrics.TotalFunctionCost;
                response.AgenticMetrics = agenticMetrics;
            }

            return response ?? throw new InvalidOperationException("No response generated from LLM");
        }

        /// <summary>
        /// Streams a chat completion with function calling support and optional agentic mode.
        /// Pauses streaming to execute functions, then resumes for the next iteration.
        /// </summary>
        /// <param name="request">The chat completion request.</param>
        /// <param name="apiKey">Optional API key override.</param>
        /// <param name="virtualKeyId">The virtual key ID for function execution billing.</param>
        /// <param name="onToolExecutingEvent">Optional callback for tool execution events.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        private async IAsyncEnumerable<ChatCompletionChunk> StreamChatCompletionWithFunctionsAsync(
            ChatCompletionRequest request,
            string? apiKey,
            int virtualKeyId,
            Func<ToolExecutionEvent, CancellationToken, Task>? onToolExecutingEvent,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_functionDiscoveryService == null || _agenticOrchestrationService == null)
            {
                throw new InvalidOperationException(
                    "Function calling requires FunctionDiscoveryService and AgenticOrchestrationService to be configured.");
            }

            var chatCompletionId = Guid.NewGuid();
            var requestId = Guid.NewGuid().ToString();

            // Load function definitions
            var tools = await _functionDiscoveryService.GetToolsForFunctionConfigurationsAsync(
                request.FunctionConfigurationIds!,
                virtualKeyId,
                cancellationToken);

            var functionNameToIdMap = await _functionDiscoveryService.GetFunctionNameToIdMappingAsync(
                request.FunctionConfigurationIds!,
                cancellationToken);

            // Inject tools into request
            request.Tools = tools;

            // Defaults are now applied by the Chat endpoints from GlobalSettings, so these are just safety fallbacks
            var agenticModeEnabled = request.EnableAgenticMode ?? true;
            var maxIterations = request.MaxAgenticIterations ?? 5;

            ILLMClient client = await GetChatClientAsync(request, cancellationToken);
            var iteration = 0;
            Usage? accumulatedUsage = null;
            var invocationOrdinal = 0;

            // Track tool calls outside the loop for iteration limit check
            var accumulatedToolCalls = new Dictionary<int, ToolCall>();

            while (iteration < maxIterations)
            {
                iteration++;

                // Reset tool calls for each iteration
                accumulatedToolCalls.Clear();
                string? finishReason = null;
                var assistantMessageContent = "";
                Usage? iterationUsage = null;

                // Stream the LLM response
                await foreach (var chunk in StreamProviderInvocationAsync(
                    request, client, apiKey, ++invocationOrdinal, accumulatedUsage, cancellationToken))
                {
                    if (chunk.ProviderCallUsage is { } providerCall)
                        iterationUsage = providerCall.Usage;

                    // Forward the chunk to the client
                    yield return chunk;

                    // Track finish reason
                    if (chunk.Choices?.Count > 0 && !string.IsNullOrEmpty(chunk.Choices[0].FinishReason))
                    {
                        finishReason = chunk.Choices[0].FinishReason;
                    }

                    // Accumulate tool calls from chunks
                    if (chunk.Choices?.Count > 0 && chunk.Choices[0].Delta?.ToolCalls is { } toolCalls)
                    {
                        foreach (var toolCallChunk in toolCalls)
                        {
                            if (!accumulatedToolCalls.ContainsKey(toolCallChunk.Index))
                            {
                                accumulatedToolCalls[toolCallChunk.Index] = new ToolCall
                                {
                                    Id = toolCallChunk.Id ?? "",
                                    Type = toolCallChunk.Type ?? "function",
                                    Function = new FunctionCall
                                    {
                                        Name = toolCallChunk.Function?.Name ?? "",
                                        Arguments = toolCallChunk.Function?.Arguments ?? ""
                                    }
                                };
                            }
                            else
                            {
                                // Append to existing tool call
                                var existing = accumulatedToolCalls[toolCallChunk.Index];
                                if (!string.IsNullOrEmpty(toolCallChunk.Id))
                                    existing.Id = toolCallChunk.Id;
                                if (toolCallChunk.Function?.Name != null)
                                    existing.Function.Name += toolCallChunk.Function.Name;
                                if (toolCallChunk.Function?.Arguments != null)
                                    existing.Function.Arguments += toolCallChunk.Function.Arguments;
                            }
                        }
                    }

                    // Accumulate content
                    if (chunk.Choices?.Count > 0 && chunk.Choices[0].Delta?.Content != null)
                    {
                        assistantMessageContent += chunk.Choices[0].Delta.Content;
                    }
                }

                accumulatedUsage = AggregateUsage(accumulatedUsage, iterationUsage);

                // Check if we have tool calls to execute
                if (finishReason != FinishReason.ToolCalls || accumulatedToolCalls.Count == 0)
                {
                    // No tool calls, streaming is complete
                    break;
                }

                // Edge case: Model returned tool_calls but no functions were configured in the request
                // This can happen with some models (e.g., gpt-oss-20b) that try to call functions even when none are available
                if (functionNameToIdMap.Count == 0)
                {
                    _logger.LogWarning(
                        "Model returned finish_reason='tool_calls' in streaming mode but no functions were configured in the request. " +
                        "Model: {Model}, Tool calls: {ToolCalls}",
                        request.Model,
                        string.Join(", ", accumulatedToolCalls.Values.Select(tc => tc.Function?.Name ?? "unknown")));

                    // Add a system message instructing the model to answer without functions
                    request.Messages.Add(new Message
                    {
                        Role = MessageRole.Assistant,
                        Content = assistantMessageContent,
                        ToolCalls = accumulatedToolCalls.Values.ToList()
                    });
                    request.Messages.Add(new Message
                    {
                        Role = MessageRole.System,
                        Content = "No functions are available. Please provide a direct answer to the user's question without attempting to call any functions."
                    });

                    // Stream one more response
                    await foreach (var chunk in StreamProviderInvocationAsync(
                        request, client, apiKey, ++invocationOrdinal, accumulatedUsage, cancellationToken))
                    {
                        yield return chunk;
                    }
                    // The fallback is the final provider response. End the iterator so
                    // pending calls from the preceding stream cannot trigger another
                    // forced-final call when discovery fails at the iteration limit.
                    yield break;
                }

                // If agentic mode is disabled, stop here
                if (!agenticModeEnabled)
                {
                    _logger.LogInformation("Tool calls detected but agentic mode is disabled. Streaming stopped.");
                    break;
                }

                // Send function execution status events (via callback)
                var toolCallsList = accumulatedToolCalls.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();

                if (onToolExecutingEvent != null)
                {
                    foreach (var toolCall in toolCallsList)
                    {
                        try
                        {
                            await onToolExecutingEvent(new ToolExecutionEvent
                            {
                                Iteration = iteration,
                                ToolCallId = toolCall.Id,
                                FunctionName = toolCall.Function.Name,
                                Status = "started"
                            }, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error emitting tool-executing event for {ToolCallId}", toolCall.Id);
                            // Continue streaming even if SSE event emission fails
                        }
                    }
                }

                // Execute tool calls (this pauses streaming)
                var executionResult = await _agenticOrchestrationService.ExecuteToolCallsAsync(
                    toolCallsList,
                    virtualKeyId,
                    functionNameToIdMap,
                    requestId,
                    chatCompletionId,
                    iteration,
                    cancellationToken);

                // Send completion status for each function (via callback)
                if (onToolExecutingEvent != null)
                {
                    foreach (var summary in executionResult.FunctionCallSummaries)
                    {
                        try
                        {
                            await onToolExecutingEvent(new ToolExecutionEvent
                            {
                                Iteration = summary.Iteration,
                                ToolCallId = summary.ToolCallId,
                                FunctionName = summary.FunctionName,
                                Status = summary.Success ? "completed" : "failed",
                                Cost = summary.Cost,
                                ErrorMessage = summary.ErrorMessage,
                                FunctionExecutionId = summary.FunctionExecutionId
                            }, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error emitting tool-executing event for {ToolCallId}", summary.ToolCallId);
                            // Continue streaming even if SSE event emission fails
                        }
                    }
                }

                // Create the assistant message with tool calls
                var assistantMessage = new Message
                {
                    Role = MessageRole.Assistant,
                    Content = assistantMessageContent,
                    ToolCalls = toolCallsList
                };

                // Append messages to conversation
                request.Messages.Add(assistantMessage);
                request.Messages.AddRange(executionResult.ToolResultMessages);

                // If all functions failed, break
                if (!executionResult.AllSucceeded)
                {
                    _logger.LogWarning("Some function executions failed in iteration {Iteration}", iteration);
                }

                // Continue to next iteration (will stream again)
            }

            // Check if we hit iteration limit with pending tool calls
            if (iteration >= maxIterations && accumulatedToolCalls.Count > 0)
            {
                _logger.LogWarning("Reached maximum agentic iterations ({MaxIterations}) with pending tool calls", maxIterations);

                // Send a final chunk indicating iteration limit reached
                yield return new ChatCompletionChunk
                {
                    Id = chatCompletionId.ToString(),
                    Object = "chat.completion.chunk",
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Model = request.Model,
                    Choices = new List<StreamingChoice>
                    {
                        new StreamingChoice
                        {
                            Index = 0,
                            Delta = new DeltaContent
                            {
                                Role = MessageRole.System,
                                Content = $"[System: Maximum iteration limit ({maxIterations}) reached. Providing response based on available information.]"
                            },
                            FinishReason = FinishReason.Stop
                        }
                    }
                };

                // Make one final streaming call
                request.Messages.Add(new Message
                {
                    Role = MessageRole.System,
                    Content = $"Maximum iteration limit ({maxIterations}) reached. Please provide a response based on the information available."
                });

                await foreach (var chunk in StreamProviderInvocationAsync(
                    request, client, apiKey, ++invocationOrdinal, accumulatedUsage, cancellationToken))
                {
                    yield return chunk;
                }
            }
        }

        /// <summary>
        /// Adds billable usage from one provider call to the usage accumulated for the request.
        /// Non-additive descriptors are taken from the latest call.
        /// </summary>
        private static Usage? AggregateUsage(Usage? accumulated, Usage? current)
        {
            if (current == null)
            {
                return accumulated;
            }

            return new Usage
            {
                PromptTokens = Sum(accumulated?.PromptTokens, current.PromptTokens),
                CompletionTokens = Sum(accumulated?.CompletionTokens, current.CompletionTokens),
                TotalTokens = Sum(accumulated?.TotalTokens, current.TotalTokens),
                ImageCount = Sum(accumulated?.ImageCount, current.ImageCount),
                VideoDurationSeconds = Sum(accumulated?.VideoDurationSeconds, current.VideoDurationSeconds),
                VideoResolution = current.VideoResolution ?? accumulated?.VideoResolution,
                IsBatch = current.IsBatch ?? accumulated?.IsBatch,
                ImageQuality = current.ImageQuality ?? accumulated?.ImageQuality,
                ImageResolution = current.ImageResolution ?? accumulated?.ImageResolution,
                CachedInputTokens = Sum(accumulated?.CachedInputTokens, current.CachedInputTokens),
                CachedInputTokensIncludedInPrompt = current.CachedInputTokensIncludedInPrompt,
                CachedWriteTokens = Sum(accumulated?.CachedWriteTokens, current.CachedWriteTokens),
                SearchUnits = Sum(accumulated?.SearchUnits, current.SearchUnits),
                SearchMetadata = current.SearchMetadata ?? accumulated?.SearchMetadata,
                InferenceSteps = Sum(accumulated?.InferenceSteps, current.InferenceSteps),
                ReasoningTokens = Sum(accumulated?.ReasoningTokens, current.ReasoningTokens),
                AudioDurationSeconds = Sum(accumulated?.AudioDurationSeconds, current.AudioDurationSeconds),
                TtsCharacters = Sum(accumulated?.TtsCharacters, current.TtsCharacters),
                Metadata = current.Metadata ?? accumulated?.Metadata,
                PricingParameters = current.PricingParameters ?? accumulated?.PricingParameters,
                ProviderReportedCostUsd = Sum(accumulated?.ProviderReportedCostUsd, current.ProviderReportedCostUsd),
                ProviderCostPolicy = current.ProviderCostPolicy ?? accumulated?.ProviderCostPolicy,
                ExtensionData = current.ExtensionData ?? accumulated?.ExtensionData
            };
        }

        private static void CaptureProviderCall(AgenticExecutionMetrics metrics, int iteration, Usage? usage)
        {
            if (usage != null)
            {
                metrics.ProviderCalls.Add(new ProviderCallUsage { Iteration = iteration, Usage = usage });
            }
        }

        private static int? Sum(int? left, int? right) =>
            left.HasValue || right.HasValue ? checked(left.GetValueOrDefault() + right.GetValueOrDefault()) : null;

        private static double? Sum(double? left, double? right) =>
            left.HasValue || right.HasValue ? left.GetValueOrDefault() + right.GetValueOrDefault() : null;

        private static decimal? Sum(decimal? left, decimal? right) =>
            left.HasValue || right.HasValue ? left.GetValueOrDefault() + right.GetValueOrDefault() : null;

        /// <summary>
        /// Applies context window management to trim message history if needed.
        /// </summary>
        /// <param name="request">The original chat completion request</param>
        /// <returns>The request with potentially trimmed messages</returns>
        private async Task<ChatCompletionRequest> ApplyContextManagementAsync(ChatCompletionRequest request)
        {
            // Skip if context management is disabled or services aren't available
            if (_contextManager == null || _modelProviderMappingService == null || _contextOptions == null ||
                !_contextOptions.Value.EnableAutomaticContextManagement)
            {
                return request;
            }

            try
            {
                // Get model context window limit (MaxInputTokens) from database entities only
                int? maxInputTokens = null;

                // Try to get model-specific context limit from database
                var mapping = await _modelProviderMappingService.GetMappingByModelAliasAsync(request.Model);

                if (mapping != null)
                {
                    // Check for provider-specific override first (ModelProviderTypeAssociation.MaxInputTokens)
                    if (mapping.ModelProviderTypeAssociation?.MaxInputTokens.HasValue == true)
                    {
                        maxInputTokens = mapping.ModelProviderTypeAssociation.MaxInputTokens.Value;
                        _logger.LogDebug("Using provider-specific MaxInputTokens of {Tokens} tokens for {Model} from {Provider}",
                            maxInputTokens, request.Model, mapping.Provider?.ProviderName ?? "Unknown");
                    }
                    // Fall back to base model's MaxInputTokens
                    else if (mapping.ModelProviderTypeAssociation?.Model?.MaxInputTokens.HasValue == true)
                    {
                        maxInputTokens = mapping.ModelProviderTypeAssociation.Model.MaxInputTokens.Value;
                        _logger.LogDebug("Using model's MaxInputTokens of {Tokens} tokens for {Model}",
                            maxInputTokens, request.Model);
                    }
                    else
                    {
                        _logger.LogDebug("No MaxInputTokens configured for {Model} - context management will not be applied",
                            request.Model);
                    }
                }
                else
                {
                    _logger.LogWarning("No model mapping found for {Model} - context management cannot be applied",
                        request.Model);
                }

                // Apply context management only if we have a limit from the database
                if (maxInputTokens.HasValue && _contextManager != null)
                {
                    var managedRequest = await _contextManager.ManageContextAsync(request, maxInputTokens.Value);
                    request.CopyRuntimeStateTo(managedRequest);
                    return managedRequest;
                }
            }
            catch (Exception ex)
            {
                // Log error but don't fail the request - just pass through without context management
                _logger.LogError(ex, "Error applying context management for model {Model}", request.Model);
            }

            return request;
        }

        private async Task<ILLMClient> GetChatClientAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            // Some third-party and test factories compiled against the legacy
            // interface return null for an unconfigured default-interface call.
            // Preserve single-route compatibility while provider-aware factories
            // override GetClientForChatAsync.
            return await _clientFactory.GetClientForChatAsync(request, cancellationToken)
                ?? await _clientFactory.GetClientAsync(request.Model, cancellationToken);
        }

        /// <summary>
        /// Creates an embedding using the configured LLM providers.
        /// </summary>
        /// <param name="request">The embedding request, including the target model alias.</param>
        /// <param name="apiKey">Optional API key to override the configured key for this request.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The embedding response from the selected LLM provider.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the request is null.</exception>
        /// <exception cref="ArgumentException">Thrown if the request.Model is null or whitespace.</exception>
        /// <exception cref="ConfigurationException">Thrown if configuration for the requested model is invalid or missing.</exception>
        /// <exception cref="UnsupportedProviderException">Thrown if the provider for the requested model is not supported.</exception>
        /// <exception cref="LLMCommunicationException">Thrown if communication with the LLM provider fails.</exception>
        public async Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (string.IsNullOrWhiteSpace(request.Model))
                throw new ArgumentException("The request must specify a target Model alias.", "request.Model");

            // No router for embeddings (OpenAI spec does not support routing for embeddings)
            ILLMClient client = await _clientFactory.GetClientAsync(request.Model, cancellationToken);
            return await client.CreateEmbeddingAsync(request, apiKey, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Creates an image generation using the configured LLM providers.
        /// </summary>
        /// <param name="request">The image generation request, including the target model alias.</param>
        /// <param name="apiKey">Optional API key to override the configured key for this request.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The image generation response from the selected LLM provider.</returns>
        /// <exception cref="ArgumentNullException">Thrown if the request is null.</exception>
        /// <exception cref="ArgumentException">Thrown if the request.Model is null or whitespace.</exception>
        /// <exception cref="ConfigurationException">Thrown if configuration for the requested model is invalid or missing.</exception>
        /// <exception cref="UnsupportedProviderException">Thrown if the provider for the requested model is not supported.</exception>
        /// <exception cref="LLMCommunicationException">Thrown if communication with the LLM provider fails.</exception>
        public async Task<ImageGenerationResponse> CreateImageAsync(
            ImageGenerationRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (string.IsNullOrWhiteSpace(request.Model))
                throw new ArgumentException("The request must specify a target Model alias.", "request.Model");

            // No router for image generation (OpenAI spec does not support routing for images)
            ILLMClient client = await _clientFactory.GetClientAsync(request.Model, cancellationToken);
            return await client.CreateImageAsync(request, apiKey, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Asynchronously gets an LLM client for the specified model.
        /// </summary>
        /// <param name="modelAlias">The model alias to get a client for.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The LLM client for the specified model.</returns>
        public async Task<ILLMClient> GetClientAsync(string modelAlias, CancellationToken cancellationToken = default)
        {
            return await _clientFactory.GetClientAsync(modelAlias, cancellationToken);
        }

        // Add other high-level methods as needed.
    }
}
