using System.Security.Cryptography;
using System.Text;

using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Persistence.Interfaces;

namespace ConduitLLM.Gateway.Endpoints;

/// <summary>Minimal-API balance check for billable Gateway endpoints.</summary>
public sealed class RequireBalanceEndpointFilter : IEndpointFilter
{
    private readonly IVirtualKeyRuntimeService _virtualKeyService;
    private readonly ILogger<RequireBalanceEndpointFilter> _logger;

    public RequireBalanceEndpointFilter(
        IVirtualKeyRuntimeService virtualKeyService,
        ILogger<RequireBalanceEndpointFilter> logger)
    {
        _virtualKeyService = virtualKeyService;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return GatewayResults.OpenAIError(
                StatusCodes.Status401Unauthorized,
                "Authentication required",
                "unauthorized",
                "authentication_error");
        }

        var virtualKey = httpContext.GetVirtualKey();
        if (virtualKey is null)
        {
            _logger.LogWarning("No virtual key found in authenticated request for balance check");
            return GatewayResults.OpenAIError(
                StatusCodes.Status401Unauthorized,
                "Virtual key not found in authentication context",
                "unauthorized",
                "authentication_error");
        }

        // Authentication remains mandatory. An existing operation has already passed
        // its admission check and must remain replayable after its debit depletes balance.
        var isFunctionExecution = HttpMethods.IsPost(httpContext.Request.Method)
            && string.Equals(httpContext.Request.Path.Value?.TrimEnd('/'),
                "/v1/conduit/functions/execute", StringComparison.OrdinalIgnoreCase);
        if (isFunctionExecution && httpContext.Request.Headers.TryGetValue("Idempotency-Key", out var values)
            && values.Count != 1)
            return GatewayResults.OpenAIError(400, "Supply exactly one Idempotency-Key header value.", "invalid_idempotency_key");
        if (isFunctionExecution
            && httpContext.Request.Headers.TryGetValue("Idempotency-Key", out var keyHeader)
            && keyHeader.Count == 1
            && keyHeader.ToString() is { Length: > 0 and <= 200 } operationKey
            && !string.IsNullOrWhiteSpace(operationKey)
            && int.TryParse(httpContext.User.FindFirst("VirtualKeyId")?.Value, out var keyId)
            && httpContext.RequestServices.GetService<IFunctionExecutionIdempotencyStore>() is { } claims)
        {
            var keyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(operationKey)));
            bool existing;
            try { existing = await claims.FindAsync(keyId, keyHash, httpContext.RequestAborted) is not null; }
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not read durable function claims for virtual key {VirtualKeyId}", keyId);
                return GatewayResults.OpenAIError(503, "Durable function idempotency is unavailable.",
                    "idempotency_unavailable", "server_error");
            }
            if (existing) return await next(context);
        }

        VirtualKeyValidationOutcome validation;
        try
        {
            var model = httpContext.Request.RouteValues.TryGetValue("model", out var modelValue)
                ? modelValue?.ToString()
                : null;
            validation = await _virtualKeyService.ValidateVirtualKeyAsync(virtualKey, model);
        }
        catch (Exception ex)
        {
            // Scoped deliberately to the balance check only. Downstream exceptions must NOT be caught
            // here — OpenAIErrorMiddleware maps them to their real status via ExceptionToResponseMapper,
            // and swallowing them turned every client error on 9 route groups into a 500 (#1191).
            _logger.LogError(ex, "Error during balance authorization check");
            return GatewayResults.OpenAIError(
                StatusCodes.Status500InternalServerError,
                "An error occurred while checking account balance.",
                "balance_check_error",
                "server_error");
        }

        if (!validation.IsValid || validation.Key is null)
        {
            _logger.LogWarning(
                "Virtual key validation failed with {FailureCode} during balance check for key prefix {KeyPrefix}",
                validation.FailureCode ?? "unknown",
                LoggingSanitizer.S(ConduitLLM.Core.Utilities.SpanHelper.MaskSecret(virtualKey)));
            return GatewayResults.OpenAIError(
                validation.HttpStatusCode,
                validation.Reason ?? "Virtual key validation failed.",
                validation.FailureCode ?? VirtualKeyValidationFailureCodes.ValidationError,
                GatewayResults.OpenAIErrorTypeFor(validation.HttpStatusCode));
        }

        httpContext.Items["ValidatedVirtualKey"] = validation.Key;
        return await next(context);
    }
}
