using System.Diagnostics;
using System.Text.Json;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Functions.Interfaces;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services;

/// <summary>
/// Service for orchestrating agentic workflows with function calling.
/// Handles execution, dependency detection, parallel/sequential execution, and audit logging.
/// </summary>
public class AgenticOrchestrationService : IAgenticOrchestrationService
{
    private readonly IFunctionExecutionService _functionExecutionService;
    private readonly ILogger<AgenticOrchestrationService> _logger;

    public AgenticOrchestrationService(
        IFunctionExecutionService functionExecutionService,
        ILogger<AgenticOrchestrationService> logger)
    {
        _functionExecutionService = functionExecutionService ?? throw new ArgumentNullException(nameof(functionExecutionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AgenticExecutionResult> ExecuteToolCallsAsync(
        List<ToolCall> toolCalls,
        int virtualKeyId,
        Dictionary<string, FunctionRoute> functionRouteMap,
        string requestId,
        Guid chatCompletionId,
        int iterationNumber,
        CancellationToken cancellationToken = default)
    {
        if (toolCalls == null || toolCalls.Count == 0)
        {
            return new AgenticExecutionResult
            {
                AllSucceeded = true
            };
        }

        _logger.LogInformation("Executing {Count} tool calls for iteration {Iteration}, request {RequestId}",
            toolCalls.Count, iterationNumber, requestId);

        var result = new AgenticExecutionResult();
        var hasDependencies = HasDependencies(toolCalls);

        if (hasDependencies)
        {
            _logger.LogDebug("Dependencies detected, executing tool calls sequentially");
            await ExecuteSequentiallyAsync(toolCalls, virtualKeyId, functionRouteMap, requestId, chatCompletionId, iterationNumber, result, cancellationToken);
        }
        else
        {
            _logger.LogDebug("No dependencies detected, executing tool calls in parallel");
            await ExecuteInParallelAsync(toolCalls, virtualKeyId, functionRouteMap, requestId, chatCompletionId, iterationNumber, result, cancellationToken);
        }

        result.AllSucceeded = result.Errors.Count == 0;

        _logger.LogInformation("Completed {Count} tool calls. Success: {AllSucceeded}, Total cost: ${TotalCost}",
            toolCalls.Count, result.AllSucceeded, result.TotalFunctionCost);

        return result;
    }

    public bool HasDependencies(List<ToolCall> toolCalls)
    {
        if (toolCalls == null || toolCalls.Count <= 1)
        {
            return false;
        }

        // Collect all tool call IDs
        var toolCallIds = toolCalls.Select(tc => tc.Id).ToHashSet();

        // Check if any function arguments reference other tool call IDs
        foreach (var toolCall in toolCalls)
        {
            var arguments = toolCall.Function.Arguments;

            // Check if arguments contain references to other tool call IDs
            foreach (var id in toolCallIds)
            {
                if (id != toolCall.Id && arguments.Contains(id, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Dependency detected: Tool call {ToolCallId} references {DependencyId}",
                        toolCall.Id, id);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Executes tool calls in parallel using Task.WhenAll.
    /// </summary>
    private async Task ExecuteInParallelAsync(
        List<ToolCall> toolCalls,
        int virtualKeyId,
        Dictionary<string, FunctionRoute> functionRouteMap,
        string requestId,
        Guid chatCompletionId,
        int iterationNumber,
        AgenticExecutionResult result,
        CancellationToken cancellationToken)
    {
        var tasks = toolCalls.Select(tc => ExecuteSingleToolCallAsync(
            tc, virtualKeyId, functionRouteMap, requestId, chatCompletionId, iterationNumber, cancellationToken));

        var executionResults = await Task.WhenAll(tasks);

        foreach (var execResult in executionResults)
        {
            MergeExecutionResult(result, execResult);
        }
    }

    /// <summary>
    /// Executes tool calls sequentially, one at a time.
    /// </summary>
    private async Task ExecuteSequentiallyAsync(
        List<ToolCall> toolCalls,
        int virtualKeyId,
        Dictionary<string, FunctionRoute> functionRouteMap,
        string requestId,
        Guid chatCompletionId,
        int iterationNumber,
        AgenticExecutionResult result,
        CancellationToken cancellationToken)
    {
        foreach (var toolCall in toolCalls)
        {
            var execResult = await ExecuteSingleToolCallAsync(
                toolCall, virtualKeyId, functionRouteMap, requestId, chatCompletionId, iterationNumber, cancellationToken);

            MergeExecutionResult(result, execResult);
        }
    }

    /// <summary>
    /// Executes a single tool call and returns the result.
    /// </summary>
    private async Task<SingleToolCallResult> ExecuteSingleToolCallAsync(
        ToolCall toolCall,
        int virtualKeyId,
        Dictionary<string, FunctionRoute> functionRouteMap,
        string requestId,
        Guid chatCompletionId,
        int iterationNumber,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var singleResult = new SingleToolCallResult
        {
            ToolCall = toolCall
        };

        try
        {
            // Resolve function name to its route (configuration id + optional provider tool name)
            if (!functionRouteMap.TryGetValue(toolCall.Function.Name, out var route))
            {
                var errorMsg = $"Function '{toolCall.Function.Name}' not found in available functions";
                _logger.LogError(errorMsg);
                singleResult.Success = false;
                singleResult.ErrorMessage = errorMsg;
                return singleResult;
            }

            // Parse function arguments
            Dictionary<string, object> parameters;
            try
            {
                parameters = JsonSerializer.Deserialize(
                    toolCall.Function.Arguments,
                    AsyncTaskJsonContext.Default.DictionaryStringObject)
                    ?? new Dictionary<string, object>();
            }
            catch (JsonException ex)
            {
                var errorMsg = $"Failed to parse function arguments: {ex.Message}";
                _logger.LogError(ex, errorMsg);
                singleResult.Success = false;
                singleResult.ErrorMessage = errorMsg;
                return singleResult;
            }

            // Execute the function
            _logger.LogDebug("Executing function {FunctionName} (config {ConfigId}, tool {ProviderTool}) with tool call ID {ToolCallId}",
                toolCall.Function.Name, route.ConfigurationId, route.ProviderToolName ?? "(default)", toolCall.Id);

            var execution = await _functionExecutionService.ExecuteAsync(
                route.ConfigurationId,
                virtualKeyId,
                parameters,
                idempotencyKey: $"{chatCompletionId}_{toolCall.Id}",
                metadata: new Dictionary<string, object>
                {
                    ["chat_completion_id"] = chatCompletionId.ToString(),
                    ["tool_call_id"] = toolCall.Id,
                    ["iteration_number"] = iterationNumber,
                    ["request_id"] = requestId
                },
                providerToolName: route.ProviderToolName,
                cancellationToken);

            stopwatch.Stop();

            // Failed provider executions and post-execution bookkeeping failures can still incur
            // a provider charge. Always propagate the recorded cost into the aggregate result.
            singleResult.Cost = execution.ActualCost ?? execution.EstimatedCost ?? 0m;

            // Check execution state
            if (execution.State == Functions.Enums.ExecutionState.Completed)
            {
                singleResult.Success = true;
                singleResult.FunctionExecution = execution;
                singleResult.Result = execution.ResponseJson;

                _logger.LogInformation("Function {FunctionName} completed successfully. Cost: ${Cost}, Duration: {Duration}ms",
                    toolCall.Function.Name, singleResult.Cost, execution.Duration?.TotalMilliseconds);
            }
            else
            {
                singleResult.Success = false;
                singleResult.ErrorMessage = execution.ErrorMessage ?? "Function execution failed";
                singleResult.FunctionExecution = execution;

                _logger.LogWarning("Function {FunctionName} failed: {ErrorMessage}",
                    toolCall.Function.Name, singleResult.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            singleResult.Success = false;
            singleResult.ErrorMessage = $"Exception during function execution: {ex.Message}";

            _logger.LogError(ex, "Exception executing function {FunctionName} for tool call {ToolCallId}",
                toolCall.Function.Name, toolCall.Id);
        }

        singleResult.DurationMs = stopwatch.ElapsedMilliseconds;
        return singleResult;
    }

    /// <summary>
    /// Merges a single tool call result into the aggregate result.
    /// </summary>
    private void MergeExecutionResult(AgenticExecutionResult aggregateResult, SingleToolCallResult singleResult)
    {
        // Create tool result message
        var toolMessage = new Message
        {
            Role = MessageRole.Tool,
            ToolCallId = singleResult.ToolCall.Id,
            Content = singleResult.Success ? singleResult.Result : singleResult.ErrorMessage
        };

        aggregateResult.ToolResultMessages.Add(toolMessage);

        // Add function call summary
        var summary = new FunctionCallSummary
        {
            ToolCallId = singleResult.ToolCall.Id,
            FunctionName = singleResult.ToolCall.Function.Name,
            FunctionConfigurationId = singleResult.FunctionExecution?.FunctionConfigurationId ?? 0,
            FunctionExecutionId = singleResult.FunctionExecution?.Id,
            Success = singleResult.Success,
            Cost = singleResult.Cost,
            ErrorMessage = singleResult.ErrorMessage,
            DurationMs = singleResult.DurationMs
        };

        aggregateResult.FunctionCallSummaries.Add(summary);

        // Aggregate cost
        if (singleResult.Cost.HasValue)
        {
            aggregateResult.TotalFunctionCost += singleResult.Cost.Value;
        }

        // Track errors
        if (!singleResult.Success)
        {
            aggregateResult.Errors.Add($"{singleResult.ToolCall.Function.Name}: {singleResult.ErrorMessage}");
        }
    }

    /// <summary>
    /// Result of executing a single tool call.
    /// </summary>
    private class SingleToolCallResult
    {
        public required ToolCall ToolCall { get; set; }
        public bool Success { get; set; }
        public string? Result { get; set; }
        public string? ErrorMessage { get; set; }
        public decimal? Cost { get; set; }
        public long? DurationMs { get; set; }
        public Functions.Entities.FunctionExecution? FunctionExecution { get; set; }
    }
}
