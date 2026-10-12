using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Gateway.Constants;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.UsageTracking;

namespace ConduitLLM.Gateway.Services;

/// <summary>Prices provider usage once, preserving the call boundaries and original evidence.</summary>
public static class ProviderUsagePricing
{
    /// <summary>Replaces the request-local reported-cost policy with the current routed provider's policy.</summary>
    public static void SetProviderBillingPolicy(HttpContext context, Provider? provider)
    {
        context.Items.Remove(HttpContextKeys.ProviderBillingPolicy);
        if (provider?.TrustProviderReportedCosts == true)
            context.Items[HttpContextKeys.ProviderBillingPolicy] = new ProviderCostBillingPolicy
            {
                TrustProviderReportedCost = true,
                MarkupMultiplier = provider.ProviderCostMarkupMultiplier
            };
    }

    public static async Task<ProviderToolPricingEvidence> GetOrCalculateToolsAsync(
        HttpContext context, IToolCostCalculationService toolCostCalculationService)
    {
        var accounting = context.GetOrCreateRequestAccountingContext();
        var snapshot = accounting.Snapshot();
        if (snapshot.ProviderToolPricing is { } cached)
            return cached;
        if (snapshot.ProviderToolUsage is null)
            return new ProviderToolPricingEvidence(0m);

        var usage = new ToolUsageData
        {
            Tools = snapshot.ProviderToolUsage.Tools.Select(tool => new ToolUsageItem
            {
                ToolName = tool.ToolName, Count = tool.Count, DurationSeconds = tool.DurationSeconds
            }).ToList()
        };
        string? usageJson = null;
        try
        {
            usageJson = toolCostCalculationService.SerializeToolUsage(usage);
            var provider = context.Items.TryGetValue("ProviderType", out var providerValue) &&
                Enum.TryParse<ProviderType>(providerValue?.ToString(), true, out var parsed)
                    ? parsed : ProviderType.OpenAI;
            var cost = await toolCostCalculationService.CalculateToolCostsAsync(usage, provider);
            var result = new ProviderToolPricingEvidence(
                cost.Failed || snapshot.HasIncompleteProviderToolUsage ? null : cost.TotalCost,
                usageJson,
                cost.Failed ? "Provider-hosted tool pricing failed" : snapshot.HasIncompleteProviderToolUsage
                    ? ProviderToolPricingEvidence.IncompleteStreamingUsageReason : null,
                cost.HasUnconfiguredTools ? string.Join(", ", cost.UnconfiguredToolNames) : null,
                cost.Failed ? 0m : cost.TotalCost);
            accounting.RecordProviderToolPricing(result);
            return result;
        }
        catch (Exception exception)
        {
            var partial = exception as ToolCostCalculationException;
            var result = new ProviderToolPricingEvidence(null, usageJson, exception.Message,
                partial?.UnconfiguredToolNames.Count > 0 ? string.Join(", ", partial.UnconfiguredToolNames) : null,
                partial?.KnownCost ?? 0m);
            accounting.RecordProviderToolPricing(result);
            return result;
        }
    }

    public static async Task<ProviderPricingEvidence> GetOrCalculateAsync(
        HttpContext context, string model, Usage usage, ICostCalculationService costCalculationService)
    {
        var accounting = context.GetOrCreateRequestAccountingContext();
        var snapshot = accounting.Snapshot();
        if (snapshot.ProviderPricing is { } cached)
            return cached;

        try
        {
            var calls = snapshot.ProviderCalls.OrderBy(call => call.Iteration).ToList();
            var hasCalls = calls.Count > 0;
            var fallbackReasons = new List<string>();
            var failures = new List<string>();
            if (snapshot.HasIncompleteProviderUsage)
                failures.Add("One or more provider invocations ended without complete usage evidence");
            var pricedCallCount = 0;
            var modelCostId = context.Items.TryGetValue(HttpContextKeys.ModelCostId, out var costId)
                ? costId as int?
                : null;

            async Task<decimal> CalculateCallAsync(Usage callUsage)
            {
                var label = hasCalls ? $"iteration {calls[pricedCallCount].Iteration}: " : string.Empty;
                pricedCallCount++;
                try
                {
                    var billingUsage = callUsage.CopyForBilling();
                    ApplyProviderBillingPolicy(context, billingUsage, includeAggregateReportedCost: !hasCalls);
                    var callCost = modelCostId is int id
                        ? await costCalculationService.CalculateCostByIdAsync(id, billingUsage)
                        : await costCalculationService.CalculateCostAsync(model, billingUsage);
                    if (!string.IsNullOrEmpty(billingUsage.PricingFallbackReason))
                        fallbackReasons.Add(label + billingUsage.PricingFallbackReason);
                    return callCost;
                }
                catch (Exception exception)
                {
                    failures.Add(label + exception.Message);
                    return 0m;
                }
            }

            var cost = await BillingCostComposition.CalculateProviderCostAsync(calls, usage, CalculateCallAsync);
            var result = new ProviderPricingEvidence(failures.Count == 0 ? cost : null,
                string.Join("; ", fallbackReasons), failures.Count == 0 ? null : string.Join("; ", failures), cost);
            accounting.RecordProviderPricing(result);
            return result;
        }
        catch (Exception exception)
        {
            var result = new ProviderPricingEvidence(null, FailureReason: exception.Message);
            accounting.RecordProviderPricing(result);
            return result;
        }
    }

    public static void ApplyProviderBillingPolicy(
        HttpContext context, Usage usage, bool includeAggregateReportedCost = true)
    {
        if (context.Items.TryGetValue(HttpContextKeys.ProviderBillingPolicy, out var policyValue) &&
            policyValue is ProviderCostBillingPolicy policy)
            usage.ProviderCostPolicy = policy;

        // Only aggregate-only accounting may read the aggregate reported-cost side channel.
        if (includeAggregateReportedCost && usage.ProviderReportedCostUsd is null &&
            context.Items.TryGetValue(HttpContextKeys.ProviderReportedCost, out var costValue) &&
            costValue is decimal reportedCost)
            usage.ProviderReportedCostUsd = reportedCost;
    }
}
