using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Responses;
using ConduitLLM.Gateway.Billing;
using ConduitLLM.Gateway.Constants;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Serialization;
using ConduitLLM.Gateway.UsageTracking;

using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Gateway.Endpoints;

public interface IResponsesChatExecutor
{
    Task<ChatCompletionResponse> CreateAsync(
        ChatCompletionRequest request,
        int? virtualKeyId,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ChatCompletionChunk> StreamAsync(
        ChatCompletionRequest request,
        int? virtualKeyId,
        CancellationToken cancellationToken);
}

internal sealed class ResponsesChatExecutor(Conduit conduit) : IResponsesChatExecutor
{
    public Task<ChatCompletionResponse> CreateAsync(
        ChatCompletionRequest request,
        int? virtualKeyId,
        CancellationToken cancellationToken) =>
        conduit.CreateChatCompletionAsync(request, null, virtualKeyId, cancellationToken);

    public IAsyncEnumerable<ChatCompletionChunk> StreamAsync(
        ChatCompletionRequest request,
        int? virtualKeyId,
        CancellationToken cancellationToken) =>
        conduit.StreamChatCompletionAsync(request, null, virtualKeyId, null, cancellationToken);
}

/// <summary>Implements the stateless, text-only Responses API subset from ADR 0001.</summary>
public sealed class ResponsesEndpoints : GatewayEndpointHandlerBase
{
    private readonly IResponsesChatExecutor _executor;
    private readonly IModelProviderMappingService _modelMappingService;
    private readonly IUsageEstimationService _usageEstimationService;
    private readonly IChatSpendEstimator? _chatSpendEstimator;
    private readonly ISpendReservationService? _spendReservationService;
    private readonly BillingAdmissionOptions _billingAdmissionOptions;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly IProviderErrorTranslator _providerErrorTranslator;

    public ResponsesEndpoints(
        IResponsesChatExecutor executor,
        IModelProviderMappingService modelMappingService,
        IUsageEstimationService usageEstimationService,
        JsonSerializerOptions jsonOptions,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ResponsesEndpoints> logger,
        IChatSpendEstimator? chatSpendEstimator = null,
        ISpendReservationService? spendReservationService = null,
        IOptions<BillingAdmissionOptions>? billingAdmissionOptions = null,
        IProviderErrorTranslator? providerErrorTranslator = null)
        : base(null, httpContextAccessor, logger)
    {
        _executor = executor;
        _modelMappingService = modelMappingService;
        _usageEstimationService = usageEstimationService;
        _jsonOptions = jsonOptions;
        _chatSpendEstimator = chatSpendEstimator;
        _spendReservationService = spendReservationService;
        _billingAdmissionOptions = billingAdmissionOptions?.Value ?? new BillingAdmissionOptions();
        // Default to External so an unwired construction sanitizes rather than leaks.
        _providerErrorTranslator = providerErrorTranslator
            ?? new ConduitLLM.Core.Services.ProviderErrorTranslator(
                new ConduitLLM.Core.Configuration.CustomerErrorOptions());
    }

    public async Task<IResult> CreateResponse(
        CreateResponseRequest request,
        CancellationToken cancellationToken = default)
    {
        var translation = Translate(request);
        if (translation.Error is not null)
        {
            return GatewayResults.OpenAIError(
                StatusCodes.Status400BadRequest,
                translation.Error.Message,
                "unsupported_parameter",
                param: translation.Error.Parameter);
        }

        var chatRequest = translation.Request!;
        var virtualKeyId = CurrentVirtualKeyId;
        var accounting = HttpContext.GetOrCreateRequestAccountingContext();
        accounting.SetOperation(RequestOperation.Responses, virtualKeyId, chatRequest.Model);
        Response.Headers["x-request-id"] = accounting.BillingRequestId;

        await PopulateProviderMetadataAsync(chatRequest);

        var admissionError = await ReserveSpendAsync(
            chatRequest,
            virtualKeyId,
            accounting,
            cancellationToken);
        if (admissionError is not null)
        {
            return admissionError;
        }

        var invocationError = await MarkInvocationStartedAsync(virtualKeyId, accounting);
        if (invocationError is not null)
        {
            return invocationError;
        }

        try
        {
            if (request.Stream == true)
            {
                await StreamResponseAsync(request, chatRequest, translation.Instructions, virtualKeyId, cancellationToken);
                return Results.Empty;
            }

            var chatResponse = await _executor.CreateAsync(chatRequest, virtualKeyId, cancellationToken);
            await CaptureSelectedRouteAsync(chatRequest);
            await CaptureNonStreamingUsageAsync(
                chatRequest,
                chatResponse,
                accounting,
                cancellationToken);

            if (chatResponse.Choices.Count > 1 ||
                chatResponse.Choices.FirstOrDefault()?.Message.Content is not (null or string))
            {
                return GatewayResults.OpenAIError(
                    StatusCodes.Status502BadGateway,
                    "The provider returned output that cannot be represented by the text-only Responses subset.",
                    "provider_response_error",
                    "server_error");
            }

            if (chatResponse.Choices.SelectMany(choice => choice.Message.ToolCalls ?? []).Any())
            {
                accounting.MarkIndeterminate("Provider returned an unsupported tool call for a text-only Responses request");
                return GatewayResults.OpenAIError(
                    StatusCodes.Status502BadGateway,
                    "The provider returned a tool call for a text-only Responses request.",
                    "provider_response_error",
                    "server_error");
            }

            return Ok(CreateCompletedResponse(request, chatResponse, translation.Instructions));
        }
        catch (LLMCommunicationException exception)
        {
            var mapped = ChatEndpoints.MapProviderCommunicationError(exception.StatusCode);
            var customerError = _providerErrorTranslator.Translate(exception);
            return GatewayResults.OpenAIError(
                mapped.StatusCode,
                customerError.Message,
                mapped.Code,
                mapped.Type,
                metadata: ChatEndpoints.BuildProviderErrorMetadata(null, customerError.Detail));
        }
        // No blanket catch: OpenAIErrorMiddleware maps exceptions to proper HTTP responses via
        // ExceptionToResponseMapper. A catch-all 500 here masked model-routing client errors (#1191).
    }

    internal static ResponseTranslation Translate(CreateResponseRequest request)
    {
        if (request.ExtensionData is { Count: > 0 })
        {
            var field = request.ExtensionData.Keys.Order(StringComparer.Ordinal).First();
            return ResponseTranslation.Failed(field, $"Unknown Responses parameter '{field}'.");
        }

        if (string.IsNullOrWhiteSpace(request.Model))
            return ResponseTranslation.Failed("model", "The 'model' parameter is required.");
        if (request.Store is not false)
            return ResponseTranslation.Failed("store", "Conduit's stateless Responses subset requires 'store' to be false.");
        if (request.MaxOutputTokens is < 16)
            return ResponseTranslation.Failed("max_output_tokens", "'max_output_tokens' must be at least 16.");
        if (request.Temperature is < 0 or > 2)
            return ResponseTranslation.Failed("temperature", "'temperature' must be between 0 and 2.");
        if (request.TopP is < 0 or > 1)
            return ResponseTranslation.Failed("top_p", "'top_p' must be between 0 and 1.");

        foreach (var unsupported in UnsupportedFields(request))
        {
            if (IsSpecified(unsupported.Value))
                return ResponseTranslation.Failed(
                    unsupported.Name,
                    $"The '{unsupported.Name}' parameter is not supported by Conduit's stateless Responses subset.");
        }

        string? instructions = null;
        if (IsSpecified(request.Instructions))
        {
            if (request.Instructions!.Value.ValueKind != JsonValueKind.String)
                return ResponseTranslation.Failed("instructions", "'instructions' must be a string.");
            instructions = request.Instructions.Value.GetString();
        }

        var inputResult = ParseInput(request.Input);
        if (inputResult.Error is not null)
            return ResponseTranslation.Failed(inputResult.Error.Parameter, inputResult.Error.Message);

        var messages = new List<Message>();
        if (!string.IsNullOrEmpty(instructions))
        {
            messages.Add(new Message { Role = "developer", Content = instructions });
        }
        messages.AddRange(inputResult.Messages!);

        return new ResponseTranslation(
            new ChatCompletionRequest
            {
                Model = request.Model,
                Messages = messages,
                Temperature = request.Temperature,
                TopP = request.TopP,
                MaxCompletionTokens = request.MaxOutputTokens,
                Stream = request.Stream,
                StreamOptions = request.Stream == true ? new StreamOptions { IncludeUsage = true } : null,
                User = request.User,
                Store = false,
                EnableAgenticMode = false,
                MaxAgenticIterations = 1
            },
            instructions,
            null);
    }

    private static InputParseResult ParseInput(JsonElement? input)
    {
        if (!IsSpecified(input))
            return InputParseResult.Failed("input", "The 'input' parameter is required.");

        var element = input!.Value;
        if (element.ValueKind == JsonValueKind.String)
        {
            return new InputParseResult(
                [new Message { Role = MessageRole.User, Content = element.GetString() ?? string.Empty }],
                null);
        }

        if (element.ValueKind != JsonValueKind.Array)
            return InputParseResult.Failed("input", "'input' must be a string or an array of text messages.");

        var messages = new List<Message>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return InputParseResult.Failed($"input[{index}]", "Each input item must be a message object.");

            var allowed = new HashSet<string>(StringComparer.Ordinal) { "role", "content", "type" };
            var extra = item.EnumerateObject().Select(property => property.Name)
                .FirstOrDefault(name => !allowed.Contains(name));
            if (extra is not null)
                return InputParseResult.Failed($"input[{index}].{extra}", $"Input message field '{extra}' is not supported.");

            if (!item.TryGetProperty("role", out var roleElement) ||
                roleElement.ValueKind != JsonValueKind.String)
            {
                return InputParseResult.Failed(
                    $"input[{index}].role",
                    "Input message role must be user, assistant, system, or developer.");
            }
            var role = roleElement.GetString();
            if (role is not ("user" or "assistant" or "system" or "developer"))
            {
                return InputParseResult.Failed(
                    $"input[{index}].role",
                    "Input message role must be user, assistant, system, or developer.");
            }

            if (!item.TryGetProperty("content", out var contentElement) ||
                contentElement.ValueKind != JsonValueKind.String)
            {
                return InputParseResult.Failed(
                    $"input[{index}].content",
                    "Only string message content is supported.");
            }

            if (item.TryGetProperty("type", out var typeElement) &&
                typeElement.ValueKind is not JsonValueKind.Null &&
                (typeElement.ValueKind != JsonValueKind.String || typeElement.GetString() != "message"))
            {
                return InputParseResult.Failed(
                    $"input[{index}].type",
                    "Input message type must be 'message'.");
            }

            messages.Add(new Message
            {
                Role = role,
                Content = contentElement.GetString() ?? string.Empty
            });
            index++;
        }

        return messages.Count == 0
            ? InputParseResult.Failed("input", "'input' must contain at least one message.")
            : new InputParseResult(messages, null);
    }

    private async Task StreamResponseAsync(
        CreateResponseRequest request,
        ChatCompletionRequest chatRequest,
        string? instructions,
        int? virtualKeyId,
        CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var responseId = $"resp_{Guid.NewGuid():N}";
        var messageId = $"msg_{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sequence = 0;
        var eventCount = 0L;
        var byteCount = 0L;
        var chunkCount = 0L;
        var output = new StringBuilder();
        var maximumOutputCharacters = (int)Math.Min(
            4L * 1024 * 1024,
            Math.Max(1024L, (long)(request.MaxOutputTokens ?? 32_768) * 8));
        var outputLimitExceeded = false;
        Usage? providerUsage = null;
        string? providerModel = null;
        var firstChunkAt = (DateTimeOffset?)null;
        var firstFlushAt = (DateTimeOffset?)null;
        var outcome = StreamTransportOutcome.NotStarted;
        string? finishReason = null;

        async Task WriteEventAsync(string type, ResponseStreamEvent payload, CancellationToken token)
        {
            var json = JsonSerializer.Serialize(
                payload,
                GatewayJsonTypeInfo.Require<ResponseStreamEvent>(_jsonOptions));
            var frame = $"event: {type}\ndata: {json}\n\n";
            await Response.WriteAsync(frame, token);
            await Response.Body.FlushAsync(token);
            firstFlushAt ??= DateTimeOffset.UtcNow;
            eventCount++;
            byteCount += Encoding.UTF8.GetByteCount(frame);
        }

        var initial = CreateResponseObject(
            request,
            responseId,
            createdAt,
            "in_progress",
            null,
            instructions,
            [],
            null);
        await WriteEventAsync(
            "response.created",
            Event("response.created", ++sequence, ("response", initial)),
            cancellationToken);
        await WriteEventAsync(
            "response.in_progress",
            Event("response.in_progress", ++sequence, ("response", initial)),
            cancellationToken);

        var pendingItem = CreateOutputMessage(messageId, "in_progress", string.Empty, includeContent: false);
        await WriteEventAsync(
            "response.output_item.added",
            Event("response.output_item.added", ++sequence, ("output_index", 0), ("item", pendingItem)),
            cancellationToken);
        await WriteEventAsync(
            "response.content_part.added",
            Event(
                "response.content_part.added",
                ++sequence,
                ("item_id", messageId),
                ("output_index", 0),
                ("content_index", 0),
                ("part", CreateOutputText(string.Empty))),
            cancellationToken);

        try
        {
            await foreach (var chunk in _executor.StreamAsync(chatRequest, virtualKeyId, cancellationToken))
            {
                chunkCount++;
                firstChunkAt ??= DateTimeOffset.UtcNow;
                if (chunk.Choices.Count > 1 ||
                    chunk.Choices.Any(choice => choice.Index != 0 || choice.FinishReason == FinishReason.ToolCalls))
                    throw new InvalidOperationException("Provider returned unsupported multiple-choice or tool output.");
                if (chunk.Choices.SelectMany(choice => choice.Delta?.ToolCalls ?? []).Any())
                    throw new InvalidOperationException("Provider returned an unsupported tool call.");

                if (chunk.Usage is not null)
                {
                    providerUsage = chunk.Usage;
                    providerModel = chunk.Model ?? chatRequest.Model;
                }
                finishReason = chunk.Choices
                    .Select(choice => choice.FinishReason)
                    .LastOrDefault(reason => reason is not null) ?? finishReason;

                foreach (var delta in chunk.Choices.Select(choice => choice.Delta?.Content)
                             .Where(content => !string.IsNullOrEmpty(content)))
                {
                    if (output.Length + delta!.Length > maximumOutputCharacters)
                    {
                        outputLimitExceeded = true;
                        throw new InvalidOperationException("Provider output exceeded the bounded Responses accumulator.");
                    }
                    output.Append(delta);
                    await WriteEventAsync(
                        "response.output_text.delta",
                        Event(
                            "response.output_text.delta",
                            ++sequence,
                            ("item_id", messageId),
                            ("output_index", 0),
                            ("content_index", 0),
                            ("delta", delta!),
                            ("logprobs", Array.Empty<object>())),
                        cancellationToken);
                }
            }

            await CaptureSelectedRouteAsync(chatRequest);
            var usage = await CaptureStreamingUsageAsync(
                chatRequest,
                output.ToString(),
                providerUsage,
                providerModel,
                cancellationToken);
            var incompleteReason = IncompleteReason(finishReason);
            var finalStatus = incompleteReason is null ? "completed" : "incomplete";
            var finalItem = CreateOutputMessage(messageId, finalStatus, output.ToString(), includeContent: true);

            await WriteEventAsync(
                "response.output_text.done",
                Event(
                    "response.output_text.done",
                    ++sequence,
                    ("item_id", messageId),
                    ("output_index", 0),
                    ("content_index", 0),
                    ("text", output.ToString()),
                    ("logprobs", Array.Empty<object>())),
                cancellationToken);
            await WriteEventAsync(
                "response.content_part.done",
                Event(
                    "response.content_part.done",
                    ++sequence,
                    ("item_id", messageId),
                    ("output_index", 0),
                    ("content_index", 0),
                    ("part", CreateOutputText(output.ToString()))),
                cancellationToken);
            await WriteEventAsync(
                "response.output_item.done",
                Event("response.output_item.done", ++sequence, ("output_index", 0), ("item", finalItem)),
                cancellationToken);

            var completed = CreateResponseObject(
                request,
                responseId,
                createdAt,
                finalStatus,
                incompleteReason is null ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : null,
                instructions,
                [finalItem],
                ConvertUsage(usage),
                incompleteDetails: incompleteReason is null ? null : new { reason = incompleteReason });
            var terminalEvent = incompleteReason is null ? "response.completed" : "response.incomplete";
            await WriteEventAsync(
                terminalEvent,
                Event(terminalEvent, ++sequence, ("response", completed)),
                cancellationToken);
            outcome = StreamTransportOutcome.Completed;
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            outcome = StreamTransportOutcome.ClientDisconnected;
        }
        catch (Exception exception)
        {
            outcome = StreamTransportOutcome.ProviderFailed;
            Logger.LogError(exception, "Responses stream failed for model {Model}", chatRequest.Model);
            if (!HttpContext.RequestAborted.IsCancellationRequested)
            {
                try
                {
                    // Provider failures get the customer-mode translation (classified
                    // generic in External, detailed in Internal); anything else keeps
                    // the fixed transport message.
                    var providerException = LLMCommunicationException.FindWithStatus(exception);
                    var customerError = providerException is not null
                        ? _providerErrorTranslator.Translate(providerException)
                        : null;
                    var errorMessage = customerError?.Message
                        ?? "The provider stream terminated before completion.";
                    await WriteEventAsync(
                        "error",
                        Event(
                            "error",
                            ++sequence,
                            ("code", "provider_response_error"),
                            ("message", errorMessage),
                            ("param", null),
                            ("provider_error", customerError?.Detail)),
                        CancellationToken.None);

                    var failed = CreateResponseObject(
                        request,
                        responseId,
                        createdAt,
                        "failed",
                        null,
                        instructions,
                        [],
                        null,
                        customerError?.Detail is { } detail
                            ? new { code = "provider_response_error", message = errorMessage, provider_error = detail }
                            : (object)new { code = "provider_response_error", message = errorMessage });
                    await WriteEventAsync(
                        "response.failed",
                        Event("response.failed", ++sequence, ("response", failed)),
                        CancellationToken.None);
                }
                catch (Exception writeException) when (writeException is IOException or OperationCanceledException)
                {
                    outcome = StreamTransportOutcome.ClientDisconnected;
                }
            }

        }
        finally
        {
            if (outcome != StreamTransportOutcome.Completed)
            {
                using var accountingTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await CaptureSelectedRouteAsync(chatRequest);
                    if (outputLimitExceeded)
                    {
                        HttpContext.GetOrCreateRequestAccountingContext()
                            .MarkIndeterminate("Responses provider output exceeded the bounded accounting accumulator");
                    }
                    else if (providerUsage is not null || output.Length > 0)
                    {
                        await CaptureStreamingUsageAsync(
                            chatRequest,
                            output.ToString(),
                            providerUsage,
                            providerModel,
                            accountingTimeout.Token);
                    }
                    else
                    {
                        HttpContext.GetOrCreateRequestAccountingContext()
                            .MarkIndeterminate("Responses provider stream ended without bounded usage evidence");
                    }
                }
                catch (Exception accountingException)
                {
                    Logger.LogError(accountingException, "Failed to finalize Responses streaming usage");
                    HttpContext.GetOrCreateRequestAccountingContext()
                        .MarkIndeterminate("Responses streaming usage finalization failed");
                }
            }

            HttpContext.GetOrCreateRequestAccountingContext().RecordTransport(
                new StreamTransportEvidence(
                    outcome,
                    chunkCount,
                    eventCount,
                    byteCount,
                    firstChunkAt,
                    firstFlushAt,
                    false));
        }
    }

    private async Task<Usage> CaptureStreamingUsageAsync(
        ChatCompletionRequest request,
        string output,
        Usage? providerUsage,
        string? providerModel,
        CancellationToken cancellationToken)
    {
        var accounting = HttpContext.GetOrCreateRequestAccountingContext();
        if (providerUsage is not null)
        {
            accounting.RecordProviderUsage(providerUsage, providerModel ?? request.Model, UsageEvidenceSource.Provider);
            CaptureProviderCost(providerUsage);
            return providerUsage;
        }

        var estimated = await _usageEstimationService.EstimateUsageFromStreamingResponseAsync(
            providerModel ?? request.Model,
            request.Messages,
            output,
            request.Tools,
            cancellationToken);
        accounting.RecordProviderUsage(estimated, providerModel ?? request.Model, UsageEvidenceSource.Estimated);
        return estimated;
    }

    private async Task CaptureNonStreamingUsageAsync(
        ChatCompletionRequest request,
        ChatCompletionResponse response,
        IRequestAccountingContext accounting,
        CancellationToken cancellationToken)
    {
        if (response.Usage is not null)
        {
            accounting.RecordProviderUsage(
                response.Usage,
                response.Model ?? request.Model,
                UsageEvidenceSource.Provider);
            CaptureProviderCost(response.Usage);
        }
        else
        {
            var output = response.Choices.FirstOrDefault()?.Message.Content switch
            {
                string value => value,
                null => string.Empty,
                var value => JsonSerializer.Serialize(
                    value,
                    ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.Object)
            };
            var estimated = await _usageEstimationService.EstimateUsageFromStreamingResponseAsync(
                response.Model ?? request.Model,
                request.Messages,
                output,
                request.Tools,
                cancellationToken);
            accounting.RecordProviderUsage(
                estimated,
                response.Model ?? request.Model,
                UsageEvidenceSource.Estimated);
        }
        if (response.ProviderToolUsage is not null)
            accounting.RecordProviderToolUsage(response.ProviderToolUsage);
        if (response.AgenticMetrics?.ProviderCalls.Count > 0)
            accounting.RecordProviderCalls(response.AgenticMetrics.ProviderCalls);
    }

    private void CaptureProviderCost(Usage usage)
    {
        if (usage.ProviderReportedCostUsd is decimal providerCost)
            HttpContext.Items[HttpContextKeys.ProviderReportedCost] = providerCost;
    }

    private async Task PopulateProviderMetadataAsync(ChatCompletionRequest request)
    {
        var mapping = await _modelMappingService.GetMappingByModelAliasAsync(request.Model);
        if (mapping is null)
            return;

        HttpContext.Items["ProviderId"] = mapping.ProviderId;
        HttpContext.Items["ProviderType"] = mapping.Provider?.ProviderType;
        if (mapping.ModelProviderTypeAssociation?.ModelCostId is int modelCostId)
            HttpContext.Items[HttpContextKeys.ModelCostId] = modelCostId;
        if (mapping.Provider?.TrustProviderReportedCosts == true)
        {
            HttpContext.Items[HttpContextKeys.ProviderBillingPolicy] = new ProviderCostBillingPolicy
            {
                TrustProviderReportedCost = true,
                MarkupMultiplier = mapping.Provider.ProviderCostMarkupMultiplier
            };
        }
    }

    private async Task CaptureSelectedRouteAsync(ChatCompletionRequest request)
    {
        if (request.SelectedMappingId is not int mappingId)
            return;
        var mapping = await _modelMappingService.GetMappingByIdAsync(mappingId);
        if (mapping is null)
            return;
        HttpContext.Items[HttpContextKeys.ModelProviderMappingId] = mappingId;
        HttpContext.Items["ProviderId"] = mapping.ProviderId;
        HttpContext.Items["ProviderType"] = mapping.Provider?.ProviderType;
        if (mapping.ModelProviderTypeAssociation?.ModelCostId is int modelCostId)
            HttpContext.Items[HttpContextKeys.ModelCostId] = modelCostId;
    }

    private async Task<IResult?> ReserveSpendAsync(
        ChatCompletionRequest request,
        int? virtualKeyId,
        IRequestAccountingContext accounting,
        CancellationToken cancellationToken)
    {
        if (_billingAdmissionOptions.Mode == BillingAdmissionMode.Off)
            return null;
        if (!virtualKeyId.HasValue || _chatSpendEstimator is null || _spendReservationService is null)
            return _billingAdmissionOptions.Mode == BillingAdmissionMode.Enforce
                ? BillingError(503, "Billing admission is unavailable.", "billing_unavailable")
                : null;

        ChatSpendEstimate estimate;
        try
        {
            estimate = await _chatSpendEstimator.EstimateMaximumCostAsync(request, cancellationToken);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to estimate maximum Responses spend");
            estimate = new ChatSpendEstimate(false, 0m, 0, 0, null, exception.Message);
        }

        if (!estimate.Succeeded)
            return _billingAdmissionOptions.Mode == BillingAdmissionMode.Enforce
                ? BillingError(503, "Pricing is unavailable for this request.", "pricing_unavailable")
                : null;

        var reservation = await _spendReservationService.ReserveAsync(
            virtualKeyId.Value,
            accounting.BillingRequestId,
            estimate.Amount);
        if (reservation.Outcome == SpendReservationOutcome.Reserved)
        {
            accounting.RecordReservation(estimate.Amount);
            return null;
        }
        if (_billingAdmissionOptions.Mode != BillingAdmissionMode.Enforce)
            return null;
        return reservation.Outcome == SpendReservationOutcome.InsufficientBalance
            ? BillingError(402, "Insufficient balance for the requested maximum usage.", "insufficient_balance")
            : BillingError(503, "Billing admission is temporarily unavailable.", "billing_unavailable");
    }

    private async Task<IResult?> MarkInvocationStartedAsync(
        int? virtualKeyId,
        IRequestAccountingContext accounting)
    {
        if (accounting.Snapshot().Reservation is null ||
            !virtualKeyId.HasValue ||
            _spendReservationService is null)
            return null;

        try
        {
            if (await _spendReservationService.MarkInvocationStartedAsync(
                    virtualKeyId.Value,
                    accounting.BillingRequestId))
            {
                accounting.MarkInvocationStarted();
                return null;
            }
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to persist Responses provider invocation start");
        }

        if (_billingAdmissionOptions.Mode == BillingAdmissionMode.Enforce)
            return BillingError(503, "Billing admission is temporarily unavailable.", "billing_unavailable");

        accounting.MarkInvocationStarted();
        accounting.MarkIndeterminate(
            "Responses provider invocation proceeded in shadow mode without a durable invocation-start transition");
        return null;
    }

    private static IResult BillingError(int status, string message, string code) =>
        GatewayResults.OpenAIError(status, message, code, "billing_error");

    private static ResponseObject CreateCompletedResponse(
        CreateResponseRequest request,
        ChatCompletionResponse chatResponse,
        string? instructions)
    {
        var text = chatResponse.Choices.FirstOrDefault()?.Message.Content switch
        {
            string value => value,
            null => string.Empty,
            var value => JsonSerializer.Serialize(
                value,
                ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.Object)
        };
        var finishReason = chatResponse.Choices.FirstOrDefault()?.FinishReason;
        var incompleteReason = IncompleteReason(finishReason);
        var status = incompleteReason is null ? "completed" : "incomplete";
        var item = CreateOutputMessage(
            $"msg_{Guid.NewGuid():N}",
            status,
            text,
            includeContent: true);
        var createdAt = chatResponse.Created > 0
            ? chatResponse.Created
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return CreateResponseObject(
            request,
            chatResponse.Id.StartsWith("resp_", StringComparison.Ordinal)
                ? chatResponse.Id
                : $"resp_{Guid.NewGuid():N}",
            createdAt,
            status,
            incompleteReason is null ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : null,
            instructions,
            [item],
            ConvertUsage(chatResponse.Usage),
            incompleteDetails: incompleteReason is null ? null : new { reason = incompleteReason });
    }

    private static ResponseObject CreateResponseObject(
        CreateResponseRequest request,
        string id,
        long createdAt,
        string status,
        long? completedAt,
        string? instructions,
        List<ResponseOutputMessage> output,
        ResponseUsage? usage,
        object? error = null,
        object? incompleteDetails = null) =>
        new()
        {
            Id = id,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = completedAt,
            Status = status,
            Error = error,
            IncompleteDetails = incompleteDetails,
            Instructions = instructions,
            Model = request.Model!,
            Output = output,
            OutputText = output.SelectMany(item => item.Content).FirstOrDefault()?.Text,
            ParallelToolCalls = true,
            Metadata = request.Metadata is null
                ? []
                : new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal),
            Temperature = request.Temperature ?? 1,
            TopP = request.TopP ?? 1,
            ToolChoice = "auto",
            Tools = [],
            MaxOutputTokens = request.MaxOutputTokens,
            Text = new ResponseTextConfiguration(),
            Truncation = "disabled",
            User = request.User,
            Usage = usage
        };

    private static ResponseOutputMessage CreateOutputMessage(
        string id,
        string status,
        string text,
        bool includeContent) =>
        new()
        {
            Id = id,
            Status = status,
            Content = includeContent ? [CreateOutputText(text)] : []
        };

    private static ResponseOutputText CreateOutputText(string text) => new() { Text = text };

    private static ResponseUsage? ConvertUsage(Usage? usage)
    {
        if (usage is null)
            return null;
        var input = usage.PromptTokens ?? 0;
        var output = usage.CompletionTokens ?? 0;
        return new ResponseUsage
        {
            InputTokens = input,
            InputTokensDetails = new ResponseInputTokenDetails
            {
                CachedTokens = usage.CachedInputTokens ?? 0,
                CacheWriteTokens = usage.CachedWriteTokens ?? 0
            },
            OutputTokens = output,
            OutputTokensDetails = new ResponseOutputTokenDetails
            {
                ReasoningTokens = usage.ReasoningTokens ?? 0
            },
            TotalTokens = usage.TotalTokens ?? checked(input + output)
        };
    }

    private static string? IncompleteReason(string? finishReason) =>
        finishReason switch
        {
            FinishReason.Length => "max_output_tokens",
            FinishReason.ContentFilter => "content_filter",
            _ => null
        };

    private static IEnumerable<(string Name, JsonElement? Value)> UnsupportedFields(CreateResponseRequest request)
    {
        yield return ("background", request.Background);
        yield return ("context_management", request.ContextManagement);
        yield return ("conversation", request.Conversation);
        yield return ("include", request.Include);
        yield return ("max_tool_calls", request.MaxToolCalls);
        yield return ("moderation", request.Moderation);
        yield return ("parallel_tool_calls", request.ParallelToolCalls);
        yield return ("previous_response_id", request.PreviousResponseId);
        yield return ("prompt", request.Prompt);
        yield return ("prompt_cache_key", request.PromptCacheKey);
        yield return ("prompt_cache_options", request.PromptCacheOptions);
        yield return ("prompt_cache_retention", request.PromptCacheRetention);
        yield return ("reasoning", request.Reasoning);
        yield return ("safety_identifier", request.SafetyIdentifier);
        yield return ("service_tier", request.ServiceTier);
        yield return ("stream_options", request.StreamOptions);
        yield return ("text", request.Text);
        yield return ("tool_choice", request.ToolChoice);
        yield return ("tools", request.Tools);
        yield return ("top_logprobs", request.TopLogprobs);
        yield return ("truncation", request.Truncation);
    }

    private static bool IsSpecified(JsonElement? element) =>
        element is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };

    private static ResponseStreamEvent Event(
        string type,
        int sequence,
        params (string Name, object? Value)[] values)
    {
        var payload = new ResponseStreamEvent
        {
            Type = type,
            SequenceNumber = sequence
        };
        foreach (var (name, value) in values)
        {
            switch (name)
            {
                case "response": payload.Response = (ResponseObject)value!; break;
                case "output_index": payload.OutputIndex = (int)value!; break;
                case "content_index": payload.ContentIndex = (int)value!; break;
                case "item_id": payload.ItemId = (string)value!; break;
                case "item": payload.Item = (ResponseOutputMessage)value!; break;
                case "part": payload.Part = (ResponseOutputText)value!; break;
                case "delta": payload.Delta = (string)value!; break;
                case "text": payload.Text = (string)value!; break;
                case "logprobs": payload.Logprobs = (IReadOnlyList<object>)value!; break;
                case "code": payload.Code = (string)value!; break;
                case "message": payload.Message = (string)value!; break;
                case "param": payload.Param = value as string; break;
                case "provider_error": payload.ProviderError = value as ProviderErrorDetail; break;
                default: throw new ArgumentOutOfRangeException(nameof(values), name, "Unknown Responses event field.");
            }
        }
        return payload;
    }

    internal sealed record ResponseTranslation(
        ChatCompletionRequest? Request,
        string? Instructions,
        ResponseValidationError? Error)
    {
        public static ResponseTranslation Failed(string parameter, string message) =>
            new(null, null, new ResponseValidationError(parameter, message));
    }

    internal sealed record ResponseValidationError(string Parameter, string Message);

    private sealed record InputParseResult(
        List<Message>? Messages,
        ResponseValidationError? Error)
    {
        public static InputParseResult Failed(string parameter, string message) =>
            new(null, new ResponseValidationError(parameter, message));
    }
}
