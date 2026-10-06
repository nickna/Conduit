using System.Text.Json;

using ConduitLLM.Core.Models.Pricing;
using Microsoft.AspNetCore.Mvc;
using Prometheus;

namespace ConduitLLM.Admin.Endpoints
{
    public partial class PricingEndpoints
    {
        /// <summary>
        /// Validate pricing configuration JSON
        /// </summary>
        /// <param name="request">The pricing configuration to validate</param>
        /// <returns>Validation result with any errors</returns>
        public async Task<IResult> ValidatePricingConfiguration(PricingValidationRequest request)
        {
            await Task.CompletedTask;

            using var timer = PricingOperationDuration.WithLabels("validate").NewTimer();

            var serializedConfiguration = AdminJson.Serialize(request.PricingConfiguration);
            if (!TryDeserializePricingConfig<PricingRulesConfig>(serializedConfiguration, out var config, out var errorMessage))
            {
                PricingValidations.WithLabels(errorMessage!.StartsWith("Invalid JSON") ? "invalid_json" : "null_config").Inc();
                return Results.Ok(new PricingValidationResponse
                {
                    IsValid = false,
                    Errors = new[] { errorMessage! }
                });
            }

            var result = _pricingValidator.Validate(config!);

            PricingValidations.WithLabels(result.IsValid ? "valid" : "invalid").Inc();
            LogAdminAudit("Validated", "PricingConfiguration", detail: $"IsValid: {result.IsValid}, Errors: {result.Errors.Count}");
            return Results.Ok(new PricingValidationResponse
            {
                IsValid = result.IsValid,
                Errors = result.Errors.Select(e => $"[{e.Field}] {e.Message}" + (e.RuleIndex.HasValue ? $" (rule {e.RuleIndex})" : "")).ToArray(),
                Warnings = result.Warnings.ToArray()
            });
        }

        /// <summary>
        /// Simulate pricing calculation with test parameters
        /// </summary>
        /// <param name="request">The simulation request with configuration and test parameters</param>
        /// <returns>Calculated pricing result</returns>
        public async Task<IResult> SimulatePricing(PricingSimulationRequest request)
        {
            await Task.CompletedTask;

            using var timer = PricingOperationDuration.WithLabels("simulate").NewTimer();

            // Parse pricing configuration
            var serializedConfiguration = AdminJson.Serialize(request.PricingConfiguration);
            if (!TryDeserializePricingConfig<PricingRulesConfig>(serializedConfiguration, out var config, out var errorMessage))
            {
                PricingSimulations.WithLabels(errorMessage!.StartsWith("Invalid JSON") ? "invalid_json" : "null_config").Inc();
                throw new ArgumentException($"Invalid pricing configuration JSON: {errorMessage}");
            }

            // Validate configuration first
            var validationResult = _pricingValidator.Validate(config!);
            if (!validationResult.IsValid)
            {
                PricingSimulations.WithLabels("invalid_config").Inc();
                throw new ArgumentException("Pricing configuration is invalid");
            }

            // Build usage object for simulation
            var usage = new ConduitLLM.Core.Models.Usage
            {
                VideoDurationSeconds = request.VideoDurationSeconds,
                VideoResolution = request.VideoResolution,
                ImageCount = request.ImageCount,
                ImageResolution = request.ImageResolution,
                ImageQuality = request.ImageQuality,
                PricingParameters = request.Parameters ?? new Dictionary<string, object>()
            };

            // Evaluate the pricing rules
            var result = _pricingEvaluator.Evaluate(config!, request.Parameters ?? new Dictionary<string, object>(), usage);

            PricingSimulations.WithLabels("success").Inc();
            LogAdminAudit("Simulated", "PricingCalculation", detail: $"Cost: {result.Cost}, UsedDefault: {result.UsedDefaultRate}");
            return Results.Ok(new PricingSimulationResponse
            {
                CalculatedCost = result.Cost,
                AppliedRate = result.Rate,
                Quantity = result.Quantity,
                MatchedRule = result.MatchedRule != null ? new MatchedRuleInfo
                {
                    Description = result.MatchedRule.Description,
                    Priority = result.MatchedRule.Priority,
                    Rate = result.MatchedRule.Rate,
                    ConditionsSummary = result.MatchedRule.Conditions?.Select(c => $"{c.Key} = {c.Value}").ToArray()
                } : null,
                UsedDefaultRate = result.UsedDefaultRate,
                WarningMessage = result.UsedDefaultRate ? "No matching rule found, default rate was used" : null
            });
        }
    }
}
