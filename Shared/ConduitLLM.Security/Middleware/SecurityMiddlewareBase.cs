using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ConduitLLM.Security.Models;
using ConduitLLM.Core.Utilities;

namespace ConduitLLM.Security.Middleware
{
    /// <summary>
    /// Base class for security middleware that provides common security check flow.
    /// Derived classes can add API-specific security handling.
    /// </summary>
    public abstract class SecurityMiddlewareBase
    {
        /// <summary>
        /// The next middleware in the pipeline
        /// </summary>
        protected readonly RequestDelegate Next;

        /// <summary>
        /// Logger instance for security events
        /// </summary>
        protected readonly ILogger Logger;

        /// <summary>
        /// Initializes a new instance of the security middleware base
        /// </summary>
        protected SecurityMiddlewareBase(RequestDelegate next, ILogger logger)
        {
            Next = next ?? throw new ArgumentNullException(nameof(next));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Processes the HTTP request through security checks.
        /// Template method that calls the derived class's security check implementation.
        /// </summary>
        protected async Task ProcessRequestAsync(HttpContext context, Func<HttpContext, Task<SecurityCheckResult>> securityCheck)
        {
            var clientIp = GetClientIpAddress(context);

            // Check for early exit conditions (e.g., prior authentication failure)
            if (ShouldSkipSecurityCheck(context))
            {
                return;
            }

            // Perform the security check
            var result = await securityCheck(context);

            if (!result.IsAllowed)
            {
                await HandleSecurityViolationAsync(context, result, clientIp);
                return;
            }

            await Next(context);
        }

        /// <summary>
        /// Determines whether to skip security checks for this request.
        /// Override in derived classes to add API-specific skip conditions.
        /// </summary>
        protected virtual bool ShouldSkipSecurityCheck(HttpContext context)
        {
            // Gateway-specific: if authentication already failed, don't continue
            if (context.Response.StatusCode == 401)
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Handles a security violation by logging, recording events, and sending the error response.
        /// Override OnSecurityViolationAsync for additional handling (e.g., event monitoring).
        /// </summary>
        protected virtual async Task HandleSecurityViolationAsync(HttpContext context, SecurityCheckResult result, string clientIp)
        {
            Logger.LogWarning("Request blocked: {Reason} for path {Path} from IP {IP}",
                result.Reason,
                context.Request.Path,
                clientIp);

            // Allow derived classes to record events or perform additional actions
            await OnSecurityViolationAsync(context, result, clientIp);

            if (result.RateLimit is { } rateLimit)
            {
                RateLimitResponseContract.SetHeaders(
                    context,
                    rateLimit.Limit,
                    rateLimit.Remaining,
                    rateLimit.ResetsAt,
                    rateLimit.Scope);

                foreach (var header in result.Headers)
                {
                    context.Response.Headers[header.Key] = header.Value;
                }

                await RateLimitResponseContract.WriteAsync(
                    context,
                    rateLimit.Scope,
                    rateLimit.Limit,
                    rateLimit.ResetsAt,
                    result.Reason);
                return;
            }

            context.Response.StatusCode = result.StatusCode ?? 403;

            // Add response headers (e.g., rate limit headers)
            foreach (var header in result.Headers)
            {
                context.Response.Headers.Append(header.Key, header.Value);
            }

            // Return JSON error response
            await context.Response.WriteAsJsonAsync(
                new Serialization.SecurityErrorResponse(result.Reason, result.StatusCode),
                Serialization.SecurityCacheJsonContext.Default.SecurityErrorResponse);
        }

        /// <summary>
        /// Logs and records a security violation before the error response is sent.
        /// </summary>
        protected virtual Task OnSecurityViolationAsync(HttpContext context, SecurityCheckResult result, string clientIp)
        {
            var method = context.Request.Method;
            var path = context.Request.Path.Value ?? "";
            var attemptedKey = context.Items["AttemptedKey"] as string ?? "";
            var statusCode = result.StatusCode ?? StatusCodes.Status403Forbidden;

            switch (statusCode)
            {
                case StatusCodes.Status401Unauthorized:
                    Logger.LogWarning(
                        "Security event: AuthenticationFailure — {Method} {Path} from {ClientIp} [AttemptedKey: {AttemptedKey}]. Reason: {Reason}",
                        method, path, clientIp, attemptedKey, result.Reason);
                    break;
                case StatusCodes.Status429TooManyRequests:
                    Logger.LogWarning(
                        "Security event: RateLimitExceeded — {Method} {Path} from {ClientIp} [AttemptedKey: {AttemptedKey}]. Reason: {Reason}",
                        method, path, clientIp, attemptedKey, result.Reason);
                    break;
                case StatusCodes.Status403Forbidden:
                    Logger.LogWarning(
                        "Security event: AccessDenied — {Method} {Path} from {ClientIp}. Reason: {Reason}",
                        method, path, clientIp, result.Reason);
                    break;
                default:
                    Logger.LogWarning(
                        "Security event: Blocked ({StatusCode}) — {Method} {Path} from {ClientIp}. Reason: {Reason}",
                        statusCode, method, path, clientIp, result.Reason);
                    break;
            }

            RecordViolationMetric(statusCode);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Records an API-specific security violation metric.
        /// </summary>
        protected virtual void RecordViolationMetric(int statusCode)
        {
        }

        /// <summary>
        /// Gets the client IP address from the request, considering proxy headers.
        /// </summary>
        protected virtual string GetClientIpAddress(HttpContext context)
        {
            return IpAddressHelper.GetClientIpAddress(context);
        }
    }
}
