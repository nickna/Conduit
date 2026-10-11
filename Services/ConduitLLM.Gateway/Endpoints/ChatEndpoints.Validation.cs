using ConduitLLM.Core.Models;
using ConduitLLM.Core.Extensions;


namespace ConduitLLM.Gateway.Endpoints
{
    public partial class ChatEndpoints
    {
        /// <summary>
        /// Validates function calling request parameters.
        /// </summary>
        /// <returns>Error result if validation fails, null if validation succeeds.</returns>
        private async Task<IResult?> ValidateFunctionCallRequestAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            if (_runtimeConfigurationStore is null && _functionConfigRepository is null)
            {
                _logger.LogError("Function calling requested but IFunctionConfigurationRepository is not available");
                return OpenAIError(500, "Function calling is not configured on this server", "function_calling_unavailable", "server_error");
            }

            try
            {
                var functionConfigs = _runtimeConfigurationStore is not null
                    ? await _runtimeConfigurationStore.GetConfigurationsAsync(
                        request.FunctionConfigurationIds!, cancellationToken)
                    : await _functionConfigRepository!.GetByIdsAsync(
                        request.FunctionConfigurationIds!, cancellationToken);

                var missingIds = request.FunctionConfigurationIds!
                    .Except(functionConfigs.Select(fc => fc.Id))
                    .ToList();

                if (missingIds.Count > 0)
                {
                    _logger.LogWarning("Function calling request includes non-existent function configuration IDs: {MissingIds}",
                        string.Join(", ", missingIds));
                    return OpenAIError(400, $"Function configuration IDs not found: {string.Join(", ", missingIds)}", "invalid_function_configuration_ids");
                }

                var disabledConfigs = functionConfigs.Where(fc => !fc.IsEnabled).ToList();
                if (disabledConfigs.Count > 0)
                {
                    var disabledIds = string.Join(", ", disabledConfigs.Select(fc => fc.Id));
                    _logger.LogWarning("Function calling request includes disabled function configurations: {DisabledIds}", disabledIds);
                    return OpenAIError(400, $"Function configurations are disabled: {disabledIds}", "disabled_function_configurations");
                }

                if (request.MaxAgenticIterations.HasValue)
                {
                    var minIterations = await _globalSettingsCacheService.GetMinAgenticIterationsAsync();
                    var maxIterations = await _globalSettingsCacheService.GetMaxAgenticIterationsAsync();

                    if (request.MaxAgenticIterations.Value < minIterations || request.MaxAgenticIterations.Value > maxIterations)
                    {
                        _logger.LogWarning("Invalid MaxAgenticIterations value: {Value}, valid range is {Min}-{Max}",
                            request.MaxAgenticIterations.Value, minIterations, maxIterations);
                        return OpenAIError(400, $"MaxAgenticIterations must be between {minIterations} and {maxIterations}", "invalid_max_agentic_iterations");
                    }
                }

                _logger.LogDebug("Function calling validation passed for {Count} function configurations",
                    functionConfigs.Count);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating function calling request");
                return OpenAIError(500, "Error validating function calling request", "function_validation_error", "server_error");
            }
        }
    }
}
