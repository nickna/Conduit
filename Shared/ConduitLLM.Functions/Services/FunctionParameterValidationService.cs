using System.Text.Json;
using ConduitLLM.Functions.Serialization;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Functions.Services;

/// <summary>
/// Service for validating function execution parameters against parameter schemas.
/// Provides basic validation of required parameters and type checking.
/// </summary>
public class FunctionParameterValidationService
{
    private readonly ILogger<FunctionParameterValidationService> _logger;

    public FunctionParameterValidationService(ILogger<FunctionParameterValidationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Validates parameters against a parameter schema.
    /// </summary>
    /// <param name="parameters">The parameters to validate</param>
    /// <param name="schemaJson">The JSON schema to validate against</param>
    /// <returns>Validation result with any errors</returns>
    public ParameterValidationResult ValidateParameters(
        Dictionary<string, object> parameters,
        string? schemaJson)
    {
        var result = new ParameterValidationResult { IsValid = true };

        // If no schema provided, skip validation (assume all parameters are valid)
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            _logger.LogDebug("No parameter schema provided, skipping validation");
            return result;
        }

        try
        {
            // Parse the schema
            var schemaDoc = JsonDocument.Parse(schemaJson);
            var schema = schemaDoc.RootElement;

            // Validate required parameters
            if (schema.TryGetProperty("required", out var requiredElement))
            {
                var requiredParams = JsonSerializer.Deserialize(
                    requiredElement.GetRawText(),
                    FunctionsJsonContext.Default.ListString);
                if (requiredParams != null)
                {
                    foreach (var requiredParam in requiredParams)
                    {
                        if (!parameters.ContainsKey(requiredParam))
                        {
                            result.IsValid = false;
                            result.Errors.Add($"Missing required parameter: '{requiredParam}'");
                        }
                    }
                }
            }

            // Validate parameter types (basic validation)
            if (schema.TryGetProperty("optional", out var optionalElement))
            {
                var optionalSchema = optionalElement;

                foreach (var param in parameters)
                {
                    if (optionalSchema.TryGetProperty(param.Key, out var paramDef))
                    {
                        var validationError = ValidateParameterType(param.Key, param.Value, paramDef);
                        if (validationError != null)
                        {
                            result.IsValid = false;
                            result.Errors.Add(validationError);
                        }
                    }
                }
            }

            if (!result.IsValid)
            {
                _logger.LogWarning("Parameter validation failed: {Errors}", string.Join(", ", result.Errors));
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating parameters against schema");
            // On validation error, fail safe and allow the request
            // (provider will do its own validation)
            return new ParameterValidationResult
            {
                IsValid = true,
                Warnings = new List<string> { "Schema validation error, proceeding without validation" }
            };
        }
    }

    /// <summary>
    /// Validates a single parameter against its definition.
    /// </summary>
    private string? ValidateParameterType(string paramName, object value, JsonElement paramDef)
    {
        try
        {
            if (!paramDef.TryGetProperty("type", out var typeElement))
            {
                return null; // No type defined, skip validation
            }

            var expectedType = typeElement.GetString();

            switch (expectedType)
            {
                case "number":
                    if (!IsNumeric(value))
                    {
                        return $"Parameter '{paramName}' must be a number";
                    }

                    // Check min/max constraints
                    if (paramDef.TryGetProperty("min", out var minElement) && minElement.TryGetDouble(out var min))
                    {
                        var numValue = Convert.ToDouble(value);
                        if (numValue < min)
                        {
                            return $"Parameter '{paramName}' must be at least {min}";
                        }
                    }

                    if (paramDef.TryGetProperty("max", out var maxElement) && maxElement.TryGetDouble(out var max))
                    {
                        var numValue = Convert.ToDouble(value);
                        if (numValue > max)
                        {
                            return $"Parameter '{paramName}' must be at most {max}";
                        }
                    }
                    break;

                case "string":
                    if (value is not string)
                    {
                        return $"Parameter '{paramName}' must be a string";
                    }
                    break;

                case "boolean":
                    if (value is not bool)
                    {
                        return $"Parameter '{paramName}' must be a boolean";
                    }
                    break;

                case "array":
                    if (value is not JsonElement jsonElement || jsonElement.ValueKind != JsonValueKind.Array)
                    {
                        if (value is not System.Collections.IEnumerable || value is string)
                        {
                            return $"Parameter '{paramName}' must be an array";
                        }
                    }
                    break;

                case "object":
                    if (value is not JsonElement objElement || objElement.ValueKind != JsonValueKind.Object)
                    {
                        if (value is not Dictionary<string, object> && value.GetType().IsClass && !value.GetType().IsPrimitive)
                        {
                            // Might be a complex object, allow it
                        }
                        else
                        {
                            return $"Parameter '{paramName}' must be an object";
                        }
                    }
                    break;

                case "select":
                    // Validate against options if provided
                    if (paramDef.TryGetProperty("options", out var optionsElement))
                    {
                        var options = JsonSerializer.Deserialize(
                            optionsElement.GetRawText(),
                            FunctionsJsonContext.Default.ListString);
                        if (options != null && value is string strValue)
                        {
                            if (!options.Contains(strValue))
                            {
                                return $"Parameter '{paramName}' must be one of: {string.Join(", ", options)}";
                            }
                        }
                    }
                    break;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error validating parameter type for {ParamName}", paramName);
            return null; // Fail safe
        }
    }

    /// <summary>
    /// Checks if a value is numeric.
    /// </summary>
    private static bool IsNumeric(object value)
    {
        return value is int || value is long || value is float || value is double ||
               value is decimal || value is short || value is byte ||
               (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Number);
    }
}

/// <summary>
/// Result of parameter validation.
/// </summary>
public class ParameterValidationResult
{
    /// <summary>
    /// Whether the parameters are valid.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// List of validation errors.
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// List of validation warnings (non-blocking).
    /// </summary>
    public List<string> Warnings { get; set; } = new();
}
