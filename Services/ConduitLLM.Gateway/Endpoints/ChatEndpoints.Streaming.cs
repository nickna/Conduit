using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ConduitLLM.Gateway.Serialization;

using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Gateway.Constants;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Gateway.Models;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.UsageTracking;
using GatewayOpsMetrics = ConduitLLM.Gateway.Services.GatewayOperationsMetricsService;


namespace ConduitLLM.Gateway.Endpoints
{
    public partial class ChatEndpoints
    {
        private async Task<IResult> HandleNonStreamingRequestAsync(
            ChatCompletionRequest request,
            int? virtualKeyId,
            Stopwatch operationStopwatch,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Handling non-streaming request.");
            var response = await _conduit.CreateChatCompletionAsync(request, null, virtualKeyId, cancellationToken);
            await CaptureSelectedRouteAsync(request);

            if (response.Usage is not null)
            {
                HttpContext.GetOrCreateRequestAccountingContext().RecordProviderUsage(
                    response.Usage,
                    response.Model ?? request.Model,
                    UsageEvidenceSource.Provider);
            }
            else
            {
                // Provider returned no usage (e.g. Replicate) — estimate from the actual
                // content so the request is billed, recorded as estimated evidence.
                await EstimateNonStreamingUsageAsync(request, response, cancellationToken);
            }
            var responseToolCalls = response.Choices
                .SelectMany(choice => choice.Message?.ToolCalls ?? [])
                .ToList();
            if (responseToolCalls.Count > 0)
            {
                HttpContext.GetOrCreateRequestAccountingContext()
                    .RecordStreamingToolCalls(responseToolCalls);
            }
            if (response.ProviderToolUsage is not null)
            {
                HttpContext.GetOrCreateRequestAccountingContext()
                    .RecordProviderToolUsage(response.ProviderToolUsage);
            }
            HttpContext.Items[HttpContextKeys.PromptCachingEligible] =
                request.PromptCachingIntent is not null ||
                (HttpContext.Items.TryGetValue(HttpContextKeys.PromptCachingEligible, out var eligible) && eligible is true);

            if (response.AgenticMetrics?.FunctionCalls != null && response.AgenticMetrics.FunctionCalls.Count > 0)
            {
                StoreFunctionExecutionResults(response.AgenticMetrics);
            }
            if (response.AgenticMetrics?.ProviderCalls.Count > 0)
            {
                HttpContext.GetOrCreateRequestAccountingContext()
                    .RecordProviderCalls(response.AgenticMetrics.ProviderCalls);
            }

            // Stash the provider-reported cost via the side channel so the middleware can bill from it.
            // It is intentionally not serialized into the response body (server-only), so the middleware
            // cannot recover it by re-parsing the body the way it does for token counts.
            if (response.Usage?.ProviderReportedCostUsd is decimal providerReportedCost)
            {
                HttpContext.Items[HttpContextKeys.ProviderReportedCost] = providerReportedCost;
            }

            GatewayOpsMetrics.RecordLlmOperation("chat_completion", request.Model, "success", operationStopwatch.Elapsed.TotalSeconds);
            return Ok(response);
        }

        private void StoreFunctionExecutionResults(AgenticExecutionMetrics agenticMetrics)
        {
            var functionExecutionResults = agenticMetrics.FunctionCalls
                .Select(fc => new ToolExecutionEvent
                {
                    ToolCallId = fc.ToolCallId,
                    FunctionName = fc.FunctionName,
                    Status = fc.Success ? "completed" : "failed",
                    Cost = fc.Cost,
                    ErrorMessage = fc.ErrorMessage,
                    FunctionExecutionId = fc.FunctionExecutionId
                })
                .ToList();

            HttpContext.GetOrCreateRequestAccountingContext().RecordFunctionExecutions(
                functionExecutionResults,
                agenticMetrics.TotalFunctionCost);
            _logger.LogDebug(
                "Stored {Count} function execution results for non-streaming request logging, total cost: {Cost:C}",
                functionExecutionResults.Count, agenticMetrics.TotalFunctionCost);
        }

        private async Task HandleStreamingRequestAsync(
            ChatCompletionRequest request,
            int? virtualKeyId,
            Stopwatch operationStopwatch,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("Handling streaming request.");
            GatewayOpsMetrics.RecordStreamingRequest(request.Model, "started");

            var bufferingFeature = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
            bufferingFeature?.DisableBuffering();

            var response = HttpContext.Response;
            var sseWriter = response.CreateEnhancedSSEWriter(_jsonSerializerOptions);

            var accountingContext = HttpContext.GetOrCreateRequestAccountingContext();
            var requestId = accountingContext.BillingRequestId;
            response.Headers["x-request-id"] = requestId;

            var modelMapping = await _modelMappingService.GetMappingByModelAliasAsync(request.Model);
            var providerId = modelMapping?.ProviderId.ToString() ?? "unknown";

            _logger.LogInformation("Creating StreamingMetricsCollector for model {Model}, provider {Provider}", LoggingSanitizer.S(request.Model), providerId);
            var metricsCollector = new StreamingMetricsCollector(requestId, request.Model, providerId);
            var state = new StreamingAccumulatorState(
                GetCompletionAccumulatorLimit(request),
                _usageTrackingOptions.MaximumStreamingToolCallCharacters,
                _usageTrackingOptions.MaximumStreamingToolCalls);
            SseTransportMetrics.ActiveStreams.Add(1);
            var firstChunkTime = DateTime.UtcNow;

            try
            {
                await foreach (var chunk in _conduit.StreamChatCompletionAsync(
                    request, null, virtualKeyId,
                    CreateToolExecutionCallback(sseWriter, state),
                    cancellationToken))
                {
                    state.ChunkCount++;
                    if (state.ChunkCount == 1)
                    {
                        state.ProviderFirstChunkAt = DateTimeOffset.UtcNow;
                        _logger.LogInformation("First chunk received at {Time}ms", (DateTime.UtcNow - firstChunkTime).TotalMilliseconds);
                    }

                    AccumulateContent(chunk, state);
                    AccumulateToolCalls(chunk, state);
                    CaptureUsageData(chunk, request, state);
                    await WriteChunkToStream(chunk, sseWriter, metricsCollector, cancellationToken);
                    state.Outcome = StreamTransportOutcome.Emitting;
                    await EmitPeriodicMetrics(chunk, sseWriter, metricsCollector, cancellationToken);
                }

                await WriteStreamingCompletionEventsAsync(state, metricsCollector, sseWriter, cancellationToken);
                state.Outcome = StreamTransportOutcome.Completed;

                _logger.LogInformation("Streaming completed: {ChunkCount} chunks over {Duration}ms",
                    state.ChunkCount, (DateTime.UtcNow - firstChunkTime).TotalMilliseconds);
                GatewayOpsMetrics.RecordLlmOperation("chat_completion", request.Model, "success", operationStopwatch.Elapsed.TotalSeconds);
                GatewayOpsMetrics.RecordStreamingRequest(request.Model, "completed");
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                state.Outcome = StreamTransportOutcome.ClientDisconnected;
                _logger.LogInformation("Streaming request was aborted by the client after {ChunkCount} chunks", state.ChunkCount);
                GatewayOpsMetrics.RecordLlmOperation("chat_completion", request.Model, "cancelled", operationStopwatch.Elapsed.TotalSeconds);
                GatewayOpsMetrics.RecordStreamingRequest(request.Model, "client_disconnected");
            }
            catch (Exception streamEx)
            {
                state.Outcome = StreamTransportOutcome.ProviderFailed;
                _logger.LogError(streamEx, "Error in stream processing");

                if (!sseWriter.HasStarted)
                {
                    throw;
                }

                if (!HttpContext.RequestAborted.IsCancellationRequested)
                {
                    try
                    {
                        // Provider failures get the customer-mode translation (classified
                        // generic in External, detailed in Internal); anything else keeps
                        // the fixed transport message.
                        var providerException =
                            ConduitLLM.Core.Exceptions.LLMCommunicationException.FindWithStatus(streamEx);
                        var streamError = providerException is not null
                            ? _providerErrorTranslator.Translate(providerException)
                            : null;
                        await sseWriter.WriteErrorEventAsync(
                            streamError?.Message ?? "The provider stream terminated before completion.",
                            streamError?.Detail,
                            cancellationToken);
                    }
                    catch (Exception writeEx) when (writeEx is IOException or OperationCanceledException)
                    {
                        state.Outcome = StreamTransportOutcome.ClientDisconnected;
                        _logger.LogDebug(writeEx, "Client disconnected before the streaming error event could be written");
                    }
                }

                GatewayOpsMetrics.RecordLlmOperation("chat_completion", request.Model, "error", operationStopwatch.Elapsed.TotalSeconds);
                GatewayOpsMetrics.RecordStreamingRequest(request.Model, "error");
            }
            finally
            {
                var transportOutcome = state.EvidenceLimitExceeded && state.StreamingUsage is null
                    ? StreamTransportOutcome.AccountingIndeterminate
                    : state.Outcome;
                accountingContext.RecordTransport(new StreamTransportEvidence(
                    transportOutcome,
                    state.ChunkCount,
                    sseWriter.EventsWritten,
                    sseWriter.BytesWritten,
                    state.ProviderFirstChunkAt,
                    sseWriter.FirstClientFlushAt,
                    state.EvidenceLimitExceeded));

                try
                {
                    await CaptureSelectedRouteAsync(request);
                }
                catch (Exception routeCaptureEx)
                {
                    _logger.LogError(routeCaptureEx, "Failed to capture the selected streaming route for accounting");
                }

                // Billing data must survive provider failures and client disconnects. Do not use the
                // request token here: it is normally cancelled precisely when this fallback is needed.
                // The server-owned budget prevents a failed client transport from pinning the request forever.
                using var accountingTimeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(_usageTrackingOptions.AccountingFinalizationTimeoutSeconds));
                try
                {
                    await StoreStreamingResultsAsync(request, state, accountingTimeout.Token);
                }
                finally
                {
                    var providerLabel = modelMapping?.Provider?.ProviderType.ToString().ToLowerInvariant() ?? "unknown";
                    SseTransportMetrics.Streams.Add(
                        1,
                        new KeyValuePair<string, object?>(
                            "outcome",
                            transportOutcome.ToString().ToLowerInvariant()));
                    SseTransportMetrics.Chunks.Add(
                        state.ChunkCount,
                        new KeyValuePair<string, object?>("provider", providerLabel));
                    SseTransportMetrics.Bytes.Add(
                        sseWriter.BytesWritten,
                        new KeyValuePair<string, object?>("provider", providerLabel));
                    if (state.ProviderFirstChunkAt is { } providerFirstChunkAt)
                    {
                        SseTransportMetrics.TimeToProviderFirstChunk.Record(
                            (providerFirstChunkAt - state.StartedAt).TotalSeconds);
                    }
                    if (sseWriter.FirstClientFlushAt is { } clientFirstFlushAt)
                    {
                        SseTransportMetrics.TimeToClientFirstFlush.Record(
                            (clientFirstFlushAt - state.StartedAt).TotalSeconds);
                    }
                    if (transportOutcome == StreamTransportOutcome.ClientDisconnected)
                    {
                        SseTransportMetrics.ClientDisconnects.Add(
                            1,
                            new KeyValuePair<string, object?>(
                                "phase",
                                state.ChunkCount == 0 ? "before_first_chunk" : "after_first_chunk"));
                    }
                    SseTransportMetrics.ActiveStreams.Add(-1);
                }
            }
        }

        private Func<ToolExecutionEvent, CancellationToken, Task> CreateToolExecutionCallback(
            EnhancedSSEResponseWriter sseWriter,
            StreamingAccumulatorState state)
        {
            return async (toolEvent, ct) =>
            {
                await sseWriter.WriteToolExecutingEventAsync(toolEvent, ct);
                state.Outcome = StreamTransportOutcome.Emitting;

                if (toolEvent.Status == "completed" || toolEvent.Status == "failed")
                {
                    state.FunctionExecutionResults.Add(toolEvent);
                    state.TotalFunctionCost += toolEvent.Cost ?? 0m;
                }
            };
        }

        private static void AccumulateContent(ChatCompletionChunk chunk, StreamingAccumulatorState state)
        {
            if (chunk.Choices == null) return;
            foreach (var choice in chunk.Choices)
            {
                if (!string.IsNullOrEmpty(choice.Delta?.Content))
                {
                    state.ContentAccumulator.Append(choice.Delta.Content);
                }
                if (!string.IsNullOrEmpty(choice.Delta?.Reasoning))
                {
                    state.ContentAccumulator.Append(choice.Delta.Reasoning);
                }
            }
        }

        private static void AccumulateToolCalls(ChatCompletionChunk chunk, StreamingAccumulatorState state)
        {
            if (chunk.Choices == null) return;

            foreach (var choice in chunk.Choices)
            {
                if (choice.Delta?.ToolCalls is not { } toolCallDeltas) continue;

                foreach (var toolCallChunk in toolCallDeltas)
                {
                    if (!state.AccumulatedToolCalls.TryGetValue(toolCallChunk.Index, out var existing))
                    {
                        if (state.AccumulatedToolCalls.Count >= state.MaximumToolCalls)
                        {
                            state.ToolEvidenceLimitExceeded = true;
                            continue;
                        }

                        existing = new StreamingToolCallAccumulator();
                        state.AccumulatedToolCalls[toolCallChunk.Index] = existing;
                    }

                    existing.AppendId(state.RetainToolFragment(toolCallChunk.Id));
                    existing.AppendType(state.RetainToolFragment(toolCallChunk.Type));
                    existing.AppendName(state.RetainToolFragment(toolCallChunk.Function?.Name));
                    existing.AppendArguments(state.RetainToolFragment(toolCallChunk.Function?.Arguments));
                }
            }
        }

        private static void CaptureUsageData(ChatCompletionChunk chunk, ChatCompletionRequest request, StreamingAccumulatorState state)
        {
            if (chunk.ProviderCallUsage != null)
            {
                state.ProviderCalls[chunk.ProviderCallUsage.Iteration] = chunk.ProviderCallUsage.Usage;
            }
            if (chunk.Usage != null)
            {
                state.StreamingUsage = chunk.Usage;
                state.StreamingModel = chunk.Model ?? request.Model;
            }
            if (chunk.ProviderToolUsage is not null)
            {
                state.ProviderToolUsage = chunk.ProviderToolUsage;
            }
        }

        private async Task WriteChunkToStream(
            ChatCompletionChunk chunk,
            EnhancedSSEResponseWriter sseWriter,
            StreamingMetricsCollector metricsCollector,
            CancellationToken cancellationToken)
        {
            if (chunk.Choices?.Count > 0 && !string.IsNullOrEmpty(chunk.Choices[0].Delta?.Reasoning))
            {
                await sseWriter.WriteReasoningEventAsync(chunk.Choices[0].Delta.Reasoning!, cancellationToken);
                await sseWriter.WriteContentEventAsync(chunk, cancellationToken);
            }
            else
            {
                await sseWriter.WriteContentEventAsync(chunk, cancellationToken);
            }
        }

        private async Task EmitPeriodicMetrics(
            ChatCompletionChunk chunk,
            EnhancedSSEResponseWriter sseWriter,
            StreamingMetricsCollector metricsCollector,
            CancellationToken cancellationToken)
        {
            if (chunk?.Choices?.Count > 0)
            {
                var hasContent = chunk.Choices.Any(c => !string.IsNullOrEmpty(c.Delta?.Content));
                if (hasContent)
                {
                    if (metricsCollector.GetMetrics().TimeToFirstTokenMs == null)
                    {
                        metricsCollector.RecordFirstToken();
                    }
                    else
                    {
                        metricsCollector.RecordToken();
                    }
                }

                if (metricsCollector.ShouldEmitMetrics())
                {
                    _logger.LogDebug("Emitting streaming metrics");
                    await sseWriter.WriteMetricsEventAsync(metricsCollector.GetMetrics(), cancellationToken);
                }
            }
        }

        private async Task StoreStreamingResultsAsync(
            ChatCompletionRequest request,
            StreamingAccumulatorState state,
            CancellationToken cancellationToken)
        {
            HttpContext.Items[HttpContextKeys.PromptCachingEligible] =
                request.PromptCachingIntent is not null ||
                (HttpContext.Items.TryGetValue(HttpContextKeys.PromptCachingEligible, out var eligible) && eligible is true);

            // Store usage data for middleware
            if (state.StreamingUsage != null)
            {
                HttpContext.GetOrCreateRequestAccountingContext().RecordProviderUsage(
                    state.StreamingUsage,
                    state.StreamingModel ?? request.Model,
                    UsageEvidenceSource.Provider);
            }

            else if (state.EvidenceLimitExceeded)
            {
                const string reason = "Streaming usage evidence exceeded configured accumulator bounds";
                _logger.LogError("{Reason} for model {Model}; accounting is indeterminate", reason, LoggingSanitizer.S(request.Model));
                HttpContext.GetOrCreateRequestAccountingContext().MarkIndeterminate(reason);
                state.Outcome = StreamTransportOutcome.AccountingIndeterminate;
            }
            else if (state.ContentAccumulator.Length > 0 || state.AccumulatedToolCalls.Count > 0)
            {
                _logger.LogWarning("No usage data received from provider for streaming response, estimating usage for model {Model}", LoggingSanitizer.S(request.Model));
                await EstimateStreamingUsageAsync(request, state, cancellationToken);
            }
            else
            {
                _logger.LogWarning("No output accumulated from streaming response, cannot estimate usage");
                var accountingContext = HttpContext.GetOrCreateRequestAccountingContext();
                if (accountingContext.Snapshot().Reservation?.InvocationStarted == true)
                {
                    accountingContext.MarkIndeterminate(
                        "Provider invocation ended without usage or bounded output evidence");
                    state.Outcome = StreamTransportOutcome.AccountingIndeterminate;
                }
            }

            if (state.ProviderCalls.Count > 0)
            {
                HttpContext.GetOrCreateRequestAccountingContext().RecordProviderCalls(
                    state.ProviderCalls
                        .OrderBy(entry => entry.Key)
                        .Select(entry => new ProviderCallUsage { Iteration = entry.Key, Usage = entry.Value }));
            }

            if (state.ProviderToolUsage is not null)
            {
                HttpContext.GetOrCreateRequestAccountingContext()
                    .RecordProviderToolUsage(state.ProviderToolUsage);
            }

            // Store tool calls for request logging
            if (state.AccumulatedToolCalls.Count > 0)
            {
                var toolCalls = state.MaterializeToolCalls();
                HttpContext.GetOrCreateRequestAccountingContext().RecordStreamingToolCalls(toolCalls);
                _logger.LogDebug("Stored {Count} accumulated tool calls for request logging", state.AccumulatedToolCalls.Count);
            }

            // Store function execution results
            if (state.FunctionExecutionResults.Count > 0)
            {
                HttpContext.GetOrCreateRequestAccountingContext().RecordFunctionExecutions(
                    state.FunctionExecutionResults,
                    state.TotalFunctionCost);
                _logger.LogDebug(
                    "Stored {Count} function execution results for request logging, total cost: {Cost:C}",
                    state.FunctionExecutionResults.Count, state.TotalFunctionCost);
            }

        }

        private async Task WriteStreamingCompletionEventsAsync(
            StreamingAccumulatorState state,
            StreamingMetricsCollector metricsCollector,
            EnhancedSSEResponseWriter sseWriter,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("StreamingUsage before GetFinalMetrics: {Usage}",
                state.StreamingUsage != null ?
                $"Prompt={state.StreamingUsage.PromptTokens}, Completion={state.StreamingUsage.CompletionTokens}, Total={state.StreamingUsage.TotalTokens}" :
                "null");

            var finalMetrics = metricsCollector.GetFinalMetrics(state.StreamingUsage);

            _logger.LogInformation("FinalMetrics after GetFinalMetrics: PromptTokens={Prompt}, CompletionTokens={Completion}, TotalTokens={Total}",
                finalMetrics.PromptTokens, finalMetrics.CompletionTokens, finalMetrics.TotalTokens);

            await sseWriter.WriteFinalMetricsEventAsync(finalMetrics, cancellationToken);
            await sseWriter.WriteDoneEventAsync(cancellationToken);
        }

        private async Task EstimateNonStreamingUsageAsync(
            ChatCompletionRequest request,
            ChatCompletionResponse response,
            CancellationToken cancellationToken)
        {
            try
            {
                var completionOutput = string.Concat(
                    response.Choices.Select(choice => choice.Message?.Content?.ToString() ?? string.Empty));
                var responseToolCalls = response.Choices
                    .SelectMany(choice => choice.Message?.ToolCalls ?? [])
                    .ToList();
                if (responseToolCalls.Count > 0)
                {
                    completionOutput += JsonSerializer.Serialize(
                        responseToolCalls,
                        GatewayJsonTypeInfo.Require<List<ToolCall>>(_jsonSerializerOptions));
                }

                var estimatedUsage = await _usageEstimationService.EstimateUsageFromStreamingResponseAsync(
                    response.Model ?? request.Model,
                    request.Messages,
                    completionOutput,
                    request.Tools,
                    cancellationToken);

                HttpContext.GetOrCreateRequestAccountingContext().RecordProviderUsage(
                    estimatedUsage,
                    response.Model ?? request.Model,
                    UsageEvidenceSource.Estimated);

                _logger.LogInformation(
                    "Provider returned no usage for non-streaming response; estimated Prompt={PromptTokens}, Completion={CompletionTokens}, Total={TotalTokens}",
                    estimatedUsage.PromptTokens, estimatedUsage.CompletionTokens, estimatedUsage.TotalTokens);
            }
            catch (Exception estEx)
            {
                _logger.LogError(estEx, "Failed to estimate usage for non-streaming response");
            }
        }

        private async Task EstimateStreamingUsageAsync(
            ChatCompletionRequest request,
            StreamingAccumulatorState state,
            CancellationToken cancellationToken)
        {
            try
            {
                var completionOutput = state.ContentAccumulator.ToString();
                if (state.AccumulatedToolCalls.Count > 0)
                {
                    var materializedToolCalls = state.MaterializeToolCalls();
                    completionOutput += JsonSerializer.Serialize(
                        materializedToolCalls,
                        GatewayJsonTypeInfo.Require<List<ToolCall>>(_jsonSerializerOptions));
                }

                var estimatedUsage = await _usageEstimationService.EstimateUsageFromStreamingResponseAsync(
                    state.StreamingModel ?? request.Model,
                    request.Messages,
                    completionOutput,
                    request.Tools,
                    cancellationToken);

                HttpContext.GetOrCreateRequestAccountingContext().RecordProviderUsage(
                    estimatedUsage,
                    state.StreamingModel ?? request.Model,
                    UsageEvidenceSource.Estimated);

                _logger.LogInformation(
                    "Successfully estimated usage for streaming response: Prompt={PromptTokens}, Completion={CompletionTokens}, Total={TotalTokens}",
                    estimatedUsage.PromptTokens, estimatedUsage.CompletionTokens, estimatedUsage.TotalTokens);
            }
            catch (Exception estEx)
            {
                _logger.LogError(estEx, "Failed to estimate usage for streaming response");
            }
        }

        /// <summary>
        /// Mutable state accumulated during streaming chunk processing.
        /// </summary>
        private int GetCompletionAccumulatorLimit(ChatCompletionRequest request)
        {
            if (request.MaxTokens is not > 0)
            {
                return _usageTrackingOptions.MaximumStreamingCompletionCharacters;
            }

            var requestDerivedLimit = Math.Max(1024L, request.MaxTokens.Value * 8L);
            return (int)Math.Min(
                _usageTrackingOptions.MaximumStreamingCompletionCharacters,
                requestDerivedLimit);
        }

        private sealed class StreamingAccumulatorState
        {
            private readonly int _maximumToolCharacters;
            private int _retainedToolCharacters;

            public StreamingAccumulatorState(
                int maximumCompletionCharacters,
                int maximumToolCharacters,
                int maximumToolCalls)
            {
                ContentAccumulator = new BoundedStringAccumulator(maximumCompletionCharacters);
                _maximumToolCharacters = maximumToolCharacters;
                MaximumToolCalls = maximumToolCalls;
            }

            public int ChunkCount { get; set; }
            public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
            public StreamTransportOutcome Outcome { get; set; }
            public DateTimeOffset? ProviderFirstChunkAt { get; set; }
            public Usage? StreamingUsage { get; set; }
            public string? StreamingModel { get; set; }
            public BoundedStringAccumulator ContentAccumulator { get; }
            public Dictionary<int, StreamingToolCallAccumulator> AccumulatedToolCalls { get; } = new();
            public List<ToolExecutionEvent> FunctionExecutionResults { get; } = new();
            public decimal TotalFunctionCost { get; set; }
            public Dictionary<int, Usage> ProviderCalls { get; } = new();
            public ProviderToolUsage? ProviderToolUsage { get; set; }
            public int MaximumToolCalls { get; }
            public bool ToolEvidenceLimitExceeded { get; set; }
            public bool EvidenceLimitExceeded => ContentAccumulator.LimitExceeded || ToolEvidenceLimitExceeded;

            public string RetainToolFragment(string? fragment)
            {
                if (string.IsNullOrEmpty(fragment))
                {
                    return string.Empty;
                }

                var remaining = _maximumToolCharacters - _retainedToolCharacters;
                if (remaining <= 0)
                {
                    ToolEvidenceLimitExceeded = true;
                    return string.Empty;
                }

                var retainedLength = Math.Min(remaining, fragment.Length);
                _retainedToolCharacters += retainedLength;
                if (retainedLength != fragment.Length)
                {
                    ToolEvidenceLimitExceeded = true;
                }

                return fragment[..retainedLength];
            }

            public List<ToolCall> MaterializeToolCalls() => AccumulatedToolCalls
                .OrderBy(entry => entry.Key)
                .Select(entry => entry.Value.ToToolCall())
                .ToList();
        }

        private sealed class StreamingToolCallAccumulator
        {
            private readonly StringBuilder _id = new();
            private readonly StringBuilder _type = new();
            private readonly StringBuilder _name = new();
            private readonly StringBuilder _arguments = new();

            public void AppendId(string value) => _id.Append(value);
            public void AppendType(string value)
            {
                if (_type.Length == 0)
                {
                    _type.Append(value);
                }
            }
            public void AppendName(string value) => _name.Append(value);
            public void AppendArguments(string value) => _arguments.Append(value);

            public ToolCall ToToolCall() => new()
            {
                Id = _id.ToString(),
                Type = _type.Length > 0 ? _type.ToString() : "function",
                Function = new FunctionCall
                {
                    Name = _name.ToString(),
                    Arguments = _arguments.ToString()
                }
            };
        }
    }
}
