using System.Text.Json;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Pricing;
using ConduitLLM.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services;

/// <summary>
/// Interface for evaluating pricing rules.
/// </summary>
public interface IPricingRulesEvaluator
{
    /// <summary>
    /// Evaluates pricing rules and returns the calculated cost.
    /// </summary>
    /// <param name="config">The pricing rules configuration.</param>
    /// <param name="parameters">Input parameters to match against rules.</param>
    /// <param name="usage">Usage data containing quantities.</param>
    /// <param name="context">Context for audit logging.</param>
    /// <returns>The evaluation result including cost and matched rule.</returns>
    Task<PricingEvaluationResult> EvaluateAsync(
        PricingRulesConfig config,
        Dictionary<string, object> parameters,
        Usage usage,
        PricingContext context);

    /// <summary>
    /// Evaluates pricing rules synchronously without audit logging.
    /// Use for cost estimation/preview scenarios.
    /// </summary>
    PricingEvaluationResult Evaluate(
        PricingRulesConfig config,
        Dictionary<string, object> parameters,
        Usage usage);
}

/// <summary>
/// Evaluates pricing rules against input parameters to calculate costs.
/// Supports multiple pricing types: per_unit, per_second, per_step.
/// </summary>
public class PricingRulesEvaluator : IPricingRulesEvaluator
{
    private readonly ILogger<PricingRulesEvaluator> _logger;
    private readonly IPricingAuditService? _auditService;

    public PricingRulesEvaluator(
        ILogger<PricingRulesEvaluator> logger,
        IPricingAuditService? auditService = null)
    {
        _logger = logger;
        _auditService = auditService;
    }

    /// <inheritdoc />
    public async Task<PricingEvaluationResult> EvaluateAsync(
        PricingRulesConfig config,
        Dictionary<string, object> parameters,
        Usage usage,
        PricingContext context)
    {
        var result = Evaluate(config, parameters, usage);

        // Log audit event asynchronously if audit service is available
        if (_auditService != null)
        {
            var auditEvent = new PricingAuditEvent
            {
                VirtualKeyId = context.VirtualKeyId,
                ModelId = context.ModelId,
                ModelCostId = context.ModelCostId,
                PricingType = config.PricingType,
                InputParameters = JsonSerializer.Serialize(
                    parameters,
                    AsyncTaskJsonContext.Default.DictionaryStringObject),
                MatchedRule = result.MatchedRule != null
                    ? JsonSerializer.Serialize(result.MatchedRule, CorePricingJsonContext.Default.PricingRule)
                    : null,
                UsedDefaultRate = result.UsedDefaultRate,
                AppliedRate = result.Rate,
                Quantity = result.Quantity,
                CalculatedCost = result.Cost,
                RequestId = context.RequestId
            };

            // Fire-and-forget with error handling in the service
            _ = _auditService.LogAsync(auditEvent);
        }

        _logger.LogDebug(
            "Pricing evaluated: Model={ModelId}, Type={PricingType}, Rate={Rate}, Qty={Quantity}, Cost={Cost}, Rule={Rule}",
            context.ModelId,
            config.PricingType,
            result.Rate,
            result.Quantity,
            result.Cost,
            result.MatchedRule?.Description ?? "default");

        return result;
    }

    /// <inheritdoc />
    public PricingEvaluationResult Evaluate(
        PricingRulesConfig config,
        Dictionary<string, object> parameters,
        Usage usage)
    {
        // Find matching rule (highest priority first)
        var matchingRule = config.Rules
            .OrderByDescending(r => r.Priority)
            .FirstOrDefault(r => AllConditionsMatch(r.Conditions, parameters));

        var usedDefault = matchingRule == null;
        var rate = matchingRule?.Rate ?? config.DefaultRate;

        if (usedDefault && rate <= 0)
        {
            _logger.LogError(
                "BILLING ALERT: No pricing rule matched and the default rate is not positive (Rate={Rate})",
                rate);
            throw new InvalidOperationException(
                "No pricing rule matched and no positive default rate is configured.");
        }

        var quantity = GetQuantity(config, usage);
        if (quantity <= 0)
        {
            _logger.LogError(
                "BILLING ALERT: Rules-based pricing resolved a non-positive quantity for PricingType={PricingType}, UnitField={UnitField}, Quantity={Quantity}",
                config.PricingType, config.UnitField, quantity);
            throw new InvalidOperationException(
                $"Rules-based pricing requires a positive quantity for '{config.UnitField ?? config.PricingType}'.");
        }

        var cost = rate * quantity;

        return new PricingEvaluationResult
        {
            Cost = cost,
            Rate = rate,
            Quantity = quantity,
            MatchedRule = matchingRule,
            UsedDefaultRate = usedDefault
        };
    }

    /// <summary>
    /// Checks if all conditions in a rule match the provided parameters.
    /// </summary>
    private bool AllConditionsMatch(Dictionary<string, object> conditions, Dictionary<string, object> parameters)
    {
        // Empty conditions always match (catch-all rule)
        if (conditions.Count == 0)
            return true;

        foreach (var condition in conditions)
        {
            if (!parameters.TryGetValue(condition.Key, out var value))
            {
                _logger.LogTrace("Condition key '{Key}' not found in parameters", condition.Key);
                return false;
            }

            if (!ValuesEqual(condition.Value, value))
            {
                _logger.LogTrace(
                    "Condition '{Key}' value mismatch: expected '{Expected}', got '{Actual}'",
                    condition.Key, condition.Value, value);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Compares two values for equality, handling type coercion.
    /// JSON deserialization can produce JsonElement, so we need flexible comparison.
    /// </summary>
    private bool ValuesEqual(object? expected, object? actual)
    {
        if (expected == null && actual == null)
            return true;
        if (expected == null || actual == null)
            return false;

        // Handle JsonElement from deserialization
        var expectedValue = ExtractValue(expected);
        var actualValue = ExtractValue(actual);

        // String comparison (case-insensitive)
        var expectedStr = expectedValue?.ToString()?.ToLowerInvariant();
        var actualStr = actualValue?.ToString()?.ToLowerInvariant();

        return expectedStr == actualStr;
    }

    /// <summary>
    /// Extracts the underlying value from JsonElement if necessary.
    /// </summary>
    private object? ExtractValue(object? value)
    {
        if (value is JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                JsonValueKind.String => jsonElement.GetString(),
                JsonValueKind.Number => jsonElement.GetDecimal(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => jsonElement.ToString()
            };
        }

        return value;
    }

    /// <summary>
    /// Gets the quantity to multiply by the rate based on pricing type.
    /// </summary>
    private decimal GetQuantity(PricingRulesConfig config, Usage usage)
    {
        return config.PricingType switch
        {
            "per_second" => GetPerSecondQuantity(config, usage),
            "per_step" => GetPerStepQuantity(config, usage),
            "per_unit" => GetPerUnitQuantity(config, usage),
            _ => 1m
        };
    }

    private decimal GetPerSecondQuantity(PricingRulesConfig config, Usage usage)
    {
        // Primarily for video duration
        if (usage.VideoDurationSeconds.HasValue)
            return (decimal)usage.VideoDurationSeconds.Value;

        // Fall back to unit field if specified
        return GetQuantityFromUnitField(config.UnitField, usage);
    }

    private decimal GetPerStepQuantity(PricingRulesConfig config, Usage usage)
    {
        // For inference step-based pricing
        if (usage.InferenceSteps.HasValue)
            return usage.InferenceSteps.Value;

        return GetQuantityFromUnitField(config.UnitField, usage);
    }

    private decimal GetPerUnitQuantity(PricingRulesConfig config, Usage usage)
    {
        return GetQuantityFromUnitField(config.UnitField, usage);
    }

    private decimal GetQuantityFromUnitField(string? unitField, Usage usage)
    {
        if (string.IsNullOrEmpty(unitField))
            return 1m;

        return unitField switch
        {
            "ImageCount" => usage.ImageCount ?? 1,
            "VideoCount" => 1, // Flat per-video pricing
            "VideoDurationSeconds" => (decimal)(usage.VideoDurationSeconds ?? 0),
            "InferenceSteps" => usage.InferenceSteps ?? 1,
            _ => 1m
        };
    }
}
