using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;

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
