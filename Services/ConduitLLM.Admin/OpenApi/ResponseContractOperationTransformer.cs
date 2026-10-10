using ConduitLLM.Core.OpenApi;

using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ConduitLLM.Admin.OpenApi;

/// <summary>Normalizes bodyless, file, multipart, and JSON media contracts.</summary>
public sealed class ResponseContractOperationTransformer : IOpenApiOperationTransformer
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>> OptionalQueryParameters =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Media_Search"] = ["pattern"],
            ["Model_Search"] = ["query"],
            ["BillingAudit_GetSummary"] = ["from", "to"],
            ["BillingAudit_DetectAnomalies"] = ["from", "to"],
            ["BillingAudit_GetRevenueLoss"] = ["from", "to"],
            ["Pricing_GetAuditSummary"] = ["from", "to"],
            ["ModelCosts_GetOverview"] = ["startDate", "endDate"]
        };

    private static readonly IReadOnlyDictionary<string, string[]> FileContentTypes =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Analytics_ExportAnalytics"] = ["text/csv", "application/json"],
            ["BillingAudit_ExportAuditEvents"] = ["text/csv", "application/json"],
            ["ModelCosts_ExportCsv"] = ["text/csv"],
            ["ModelCosts_ExportJson"] = ["application/json"]
        };

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        Normalize(operation, context.Description.HttpMethod);
        return Task.CompletedTask;
    }

    internal static void Normalize(OpenApiOperation operation, string? httpMethod)
    {
        OpenApiMediaTypeNormalizer.RemoveLegacyAliases(operation.RequestBody?.Content);

        if (operation.OperationId is not null &&
            OptionalQueryParameters.TryGetValue(operation.OperationId, out var optionalParameters) &&
            operation.Parameters is not null)
        {
            foreach (var parameter in operation.Parameters.OfType<OpenApiParameter>())
            {
                if (parameter.Name is null || !optionalParameters.Contains(parameter.Name))
                {
                    continue;
                }
                parameter.Required = false;
                if (parameter.Schema is OpenApiSchema schema)
                {
                    schema.Type = JsonSchemaType.String;
                }
            }
        }

        if (operation.OperationId is "ModelCosts_ImportCsv" or "ModelCosts_ImportJson" &&
            operation.RequestBody?.Content?["multipart/form-data"].Schema is OpenApiSchema importSchema)
        {
            importSchema.Required = new HashSet<string>(StringComparer.Ordinal) { "file" };
            operation.RequestBody.Description = operation.OperationId == "ModelCosts_ImportCsv"
                ? "CSV file containing model costs"
                : "JSON file containing model costs";
        }

        if (operation.Responses is null)
        {
            return;
        }

        foreach (var responseEntry in operation.Responses)
        {
            if (responseEntry.Value is not OpenApiResponse response)
            {
                continue;
            }

            OpenApiMediaTypeNormalizer.RemoveLegacyAliases(response.Content);
            if (responseEntry.Key == "200" && operation.OperationId == "Metrics_GetAllMetrics")
            {
                // The registered Prometheus middleware handles the canonical /metrics
                // request before the legacy JSON handler is reached.
                response.Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["text/plain"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String } },
                    ["application/openmetrics-text"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.String } }
                };
                continue;
            }
            if (!int.TryParse(responseEntry.Key, out var status) || status is < 200 or >= 300)
            {
                continue;
            }

            if (status is 204 or 205 || string.Equals(httpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                response.Content = new Dictionary<string, OpenApiMediaType>();
                continue;
            }

            if (responseEntry.Key == "200" && operation.OperationId is not null &&
                FileContentTypes.TryGetValue(operation.OperationId, out var contentTypes))
            {
                response.Content = contentTypes.ToDictionary(
                    contentType => contentType,
                    _ => new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" }
                    });
            }
        }
    }

}
