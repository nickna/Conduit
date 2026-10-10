using ConduitLLM.Core.OpenApi;

using Microsoft.OpenApi;

namespace ConduitLLM.Admin.OpenApi;

/// <summary>
/// Transforms the OpenAPI document for the Admin API
/// </summary>
public sealed class AdminApiDocumentTransformer : ApiDocumentTransformer
{
    protected override IReadOnlyDictionary<string, string> TagDescriptions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ModelAuthor"] = "Organizations and authors that publish models.",
            ["ModelSeries"] = "Model families and their associated models.",
            ["Notifications"] = "Administrative notifications and read status.",
            ["Tasks"] = "Task retention and operator resolution of indeterminate media jobs.",
            ["Auth"] = "Short-lived credentials for administrative requests.",
            ["SystemInfo"] = "Deployment information, health, and discovery cache management.",
            ["Metrics"] = "Prometheus and OpenMetrics scrapes for monitoring the deployment.",
            ["Configuration"] = "Routing defaults and per-alias provider selection policies.",
            ["BundledModelCatalog"] = "Import of the model catalog bundled with Conduit.",
            ["FunctionExecutions"] = "Function execution history, leases, retries, and retention.",
            ["FunctionCosts"] = "Pricing for configured function calls and their cache.",
            ["FunctionCredentials"] = "Credentials used by external function providers; returned API keys are masked.",
            ["BatchSpending"] = "Flush requests and status for batched spending updates.",
            ["PromptCaching"] = "Prompt cache configuration, capabilities, and analytics.",
            ["ProviderSync"] = "Provider synchronization runs and review of detected configuration drift.",
            ["GlobalSettings"] = "Global setting definitions, values, and cache management.",
            ["Media"] = "Media inventory, storage statistics, deletion, recovery, and reconciliation.",
            ["Media Cleanup"] = "Scheduled media cleanup settings and approval of large cleanup scopes.",
            ["Webhook recovery"] = "Webhook delivery inspection, dead letters, replay, and purge.",
            ["Provider Credentials"] = "Provider accounts, credentials, and connection testing.",
            ["Models"] = "Model metadata, identifiers, capabilities, and provider associations.",
            ["Virtual Key Groups"] = "Shared virtual-key balances, transactions, and refunds.",
            ["Virtual Keys"] = "Virtual-key issuance, access policies, validation, and usage.",
            ["IP Filters"] = "IP access rules and their configuration.",
            ["Health Monitoring"] = "Service health, incident summaries, and historical measurements.",
            ["Analytics"] = "Request logs, costs, usage, exports, and analytics cache metrics.",
            ["Function Configurations"] = "Function definitions, execution policies, testing, and import/export.",
            ["Provider Errors"] = "Provider credential failures and error tracking.",
            ["Media Retention"] = "Retention policies, group assignments, and cleanup availability.",
            ["Provider Tools"] = "Provider tool definitions, billing units, costs, and import/export.",
            ["Pricing"] = "Pricing evaluation, rule validation, and pricing audit records.",
            ["Model Provider Mappings"] = "Provider routing mappings for model aliases.",
            ["Model Costs"] = "Model pricing records, pricing rules, and bulk import/export."
        };

    public AdminApiDocumentTransformer()
        : base(
            "ConduitLLM Admin API",
            "Administrative API for ConduitLLM - Requires X-Master-Key header for authentication",
            ApiKeySecurityOperationTransformer.SecuritySchemeName,
            new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                Name = "X-Master-Key",
                In = ParameterLocation.Header,
                Description = "ConduitLLM Admin API master key."
            })
    {
    }
}
