using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ConduitLLM.Functions.Serialization;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Functions.Services;

/// <summary>
/// Service for validating function execution parameters against parameter schemas.
/// Provides basic validation of required parameters and type checking.
/// </summary>
/// <remarks>
/// CLR and JSON primitive values obey the same type and bound checks without changing
/// the supplied dictionary. Null/undefined values do not satisfy typed fields. Missing
/// schemas skip validation; malformed schemas retain the provider-validation fallback
/// with an explicit warning. Numbers must fit the finite double range; bounds use
/// exact decimal digits without rounding. Required fields remain presence checks.
/// Object fields accept JsonElement objects, JsonObject nodes, object-valued
/// string-key dictionaries (mutable or read-only), non-generic dictionaries with
/// only string keys, and non-enumerable POCO reference objects. JsonDocument is
/// unsupported regardless of its root value. Strings, URI/version scalars,
/// delegates, arrays, scalar JSON nodes, and value types are not objects. Caller
/// object-inspection failures reject the parameter rather than invoking the
/// malformed-schema fallback.
/// Array fields accept JsonElement arrays, JsonArray nodes, CLR arrays, and IList
/// collections. Other IEnumerable values must be materialized as an array or
/// list; enumeration alone cannot distinguish sequences from generic maps.
/// Unavailable JsonElement inputs fail validation without using the malformed
/// schema fallback or discarding errors for other parameters.
/// </remarks>
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
            using var schemaDoc = JsonDocument.Parse(schemaJson);
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
                        var validationError = ValidateParameterType(param.Key, param.Value, paramDef, result.Warnings);
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
            // Preserve the malformed-schema fallback; the provider validates the request.
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
    private string? ValidateParameterType(string paramName, object? value, JsonElement paramDef, List<string> warnings)
    {
        try
        {
            if (!paramDef.TryGetProperty("type", out var typeElement))
            {
                return null; // No type defined, skip validation
            }

            var expectedType = typeElement.GetString();
            if (expectedType == "object")
                return ValidateObjectParameter(paramName, value);

            if (value is null or JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
                return $"Parameter '{paramName}' must not be null or undefined";

            switch (expectedType)
            {
                case "number":
                    if (!TryGetNumber(value, out var numValue))
                    {
                        return $"Parameter '{paramName}' must be a number";
                    }

                    // Check min/max constraints
                    if (paramDef.TryGetProperty("min", out var minElement))
                    {
                        if (!TryGetNumber(minElement, out var min))
                            throw new InvalidOperationException("Numeric minimum is outside the supported range.");
                        if (numValue.CompareTo(min) < 0)
                        {
                            return $"Parameter '{paramName}' must be at least {minElement.GetRawText()}";
                        }
                    }

                    if (paramDef.TryGetProperty("max", out var maxElement))
                    {
                        if (!TryGetNumber(maxElement, out var max))
                            throw new InvalidOperationException("Numeric maximum is outside the supported range.");
                        if (numValue.CompareTo(max) > 0)
                        {
                            return $"Parameter '{paramName}' must be at most {maxElement.GetRawText()}";
                        }
                    }
                    break;

                case "string":
                    if (!TryGetString(value, out _))
                    {
                        return $"Parameter '{paramName}' must be a string";
                    }
                    break;

                case "boolean":
                    if (value is not bool and not JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False })
                    {
                        return $"Parameter '{paramName}' must be a boolean";
                    }
                    break;

                case "array":
                    if (value is IDictionary or IDictionary<string, object> or IReadOnlyDictionary<string, object> or JsonObject
                        || value is not JsonElement { ValueKind: JsonValueKind.Array }
                            and not JsonArray and not Array and not IList)
                        return $"Parameter '{paramName}' must be an array";
                    break;

                case "select":
                    if (!TryGetString(value, out var strValue))
                        return $"Parameter '{paramName}' must be a string";
                    // Validate against options if provided
                    if (paramDef.TryGetProperty("options", out var optionsElement))
                    {
                        var options = JsonSerializer.Deserialize(
                            optionsElement.GetRawText(),
                            FunctionsJsonContext.Default.ListString);
                        if (options != null)
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
        catch (ObjectDisposedException) when (value is JsonElement)
        {
            // The schema document belongs to this validation call. A disposed
            // caller element is invalid input, including disposal during a read.
            return $"Parameter '{paramName}' contains an unavailable JSON value";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error validating parameter type for {ParamName}", paramName);
            warnings.Add($"Schema validation error for parameter '{paramName}', proceeding without validation for this parameter");
            return null;
        }
    }

    private static string? ValidateObjectParameter(string paramName, object? value)
    {
        var error = $"Parameter '{paramName}' must be an object";
        try
        {
            if (value is null)
                return $"Parameter '{paramName}' must not be null or undefined";
            if (value is JsonElement element)
                return element.ValueKind switch
                {
                    JsonValueKind.Object => null,
                    JsonValueKind.Null or JsonValueKind.Undefined =>
                        $"Parameter '{paramName}' must not be null or undefined",
                    _ => error
                };
            if (value is JsonNode)
                return value is JsonObject ? null : error;
            if (value is JsonDocument)
                return error;
            if (value is IDictionary<string, object> or IReadOnlyDictionary<string, object>)
                return null;
            if (value is IDictionary dictionary)
            {
                foreach (var key in dictionary.Keys)
                    if (key is not string) return error;
                return null;
            }

            return value is not IEnumerable and not string and not Uri and not Version and not Delegate &&
                value.GetType().IsClass ? null : error;
        }
        catch (Exception)
        {
            // Caller containers and JSON document lifetimes are input failures,
            // not malformed schemas that may use the provider-validation fallback.
            return error;
        }
    }

    /// <summary>
    /// Reads primitive values without replacing the caller's JSON elements.
    /// </summary>
    private static bool TryGetNumber(object value, out ParameterNumber number)
    {
        number = default;
        string? text;
        var underflowCheck = false;
        if (value is JsonElement { ValueKind: JsonValueKind.Number } element)
        {
            if (!element.TryGetDouble(out var finite) || !double.IsFinite(finite)) return false;
            underflowCheck = finite == 0;
            text = element.GetRawText();
        }
        else text = value switch
        {
            byte or sbyte or short or ushort or int or uint or long or ulong or decimal =>
                ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
            float numeric when float.IsFinite(numeric) => numeric.ToString("R", CultureInfo.InvariantCulture),
            double numeric when double.IsFinite(numeric) => numeric.ToString("R", CultureInfo.InvariantCulture),
            _ => null
        };
        if (text is null) return false;
        var negative = text[0] == '-';
        if (negative) text = text[1..];
        var exponentAt = text.AsSpan().IndexOfAny('e', 'E');
        var significand = exponentAt < 0 ? text : text[..exponentAt];
        var pointAt = significand.IndexOf('.');
        var fractionalDigits = pointAt < 0 ? 0 : significand.Length - pointAt - 1;
        var digits = significand.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0)
        {
            number = new("0", 0, false);
            return true;
        }
        // JSON underflow must not turn a nonzero argument or bound into zero.
        if (underflowCheck) return false;
        long exponent = 0;
        if (exponentAt >= 0 && !long.TryParse(text.AsSpan(exponentAt + 1),
            NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent)) return false;
        // Comparing digit strings avoids double rounding and constructing huge powers of ten.
        number = new(digits.TrimEnd('0'), exponent + digits.Length - fractionalDigits, negative);
        return true;
    }

    private readonly record struct ParameterNumber(string Digits, long Magnitude, bool Negative)
    {
        public int CompareTo(ParameterNumber other)
        {
            var sign = Digits == "0" ? 0 : Negative ? -1 : 1;
            var otherSign = other.Digits == "0" ? 0 : other.Negative ? -1 : 1;
            if (sign != otherSign) return sign.CompareTo(otherSign);
            if (sign == 0) return 0;
            var comparison = Magnitude.CompareTo(other.Magnitude);
            if (comparison == 0)
            {
                for (var index = 0; index < Math.Max(Digits.Length, other.Digits.Length); index++)
                {
                    var digit = index < Digits.Length ? Digits[index] : '0';
                    var otherDigit = index < other.Digits.Length ? other.Digits[index] : '0';
                    comparison = digit.CompareTo(otherDigit);
                    if (comparison != 0) break;
                }
            }
            return sign * comparison;
        }
    }

    private static bool TryGetString(object value, out string? text)
    {
        text = value switch
        {
            string str => str,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };
        return text is not null;
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
