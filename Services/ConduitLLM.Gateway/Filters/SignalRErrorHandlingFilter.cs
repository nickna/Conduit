using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using ConduitLLM.Gateway.Metrics;

namespace ConduitLLM.Gateway.Filters
{
    /// <summary>
    /// Global error handling filter for SignalR hubs.
    /// Provides consistent error handling, logging, and metrics for all hub methods.
    /// </summary>
    public class SignalRErrorHandlingFilter : IHubFilter
    {
        private readonly ILogger<SignalRErrorHandlingFilter> _logger;
        private readonly SignalRMetrics _metrics;

        public SignalRErrorHandlingFilter(
            ILogger<SignalRErrorHandlingFilter> logger,
            SignalRMetrics metrics)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        }

        /// <summary>
        /// Intercepts hub method invocations to provide error handling and logging.
        /// </summary>
        public async ValueTask<object?> InvokeMethodAsync(
            HubInvocationContext invocationContext,
            Func<HubInvocationContext, ValueTask<object?>> next)
        {
            var hubName = invocationContext.Hub.GetType().Name;
            var methodName = invocationContext.HubMethodName;
            var connectionId = invocationContext.Context.ConnectionId;
            var virtualKeyId = invocationContext.Context.Items.TryGetValue("VirtualKeyId", out var vkId) 
                ? vkId?.ToString() 
                : "unknown";

            var stopwatch = Stopwatch.StartNew();

            try
            {
                _logger.LogDebug(
                    "SignalR method invocation started: {Hub}.{Method} for VirtualKey {VirtualKeyId} on connection {ConnectionId}",
                    hubName, methodName, virtualKeyId, connectionId);

                var result = await next(invocationContext);

                stopwatch.Stop();
                _logger.LogDebug(
                    "SignalR method invocation completed: {Hub}.{Method} in {ElapsedMs}ms",
                    hubName, methodName, stopwatch.ElapsedMilliseconds);

                // Record successful method invocation metric
                _metrics.HubMethodInvocations.Add(1, new TagList 
                { 
                    { "hub", hubName }, 
                    { "method", methodName },
                    { "success", "true" }
                });

                return result;
            }
            catch (HubException hubEx)
            {
                // HubExceptions are intended for the client, log them but don't wrap
                stopwatch.Stop();
                _logger.LogWarning(hubEx,
                    "SignalR hub exception in {Hub}.{Method} for VirtualKey {VirtualKeyId}: {Message}",
                    hubName, methodName, virtualKeyId, hubEx.Message);

                _metrics.HubMethodInvocations.Add(1, new TagList 
                { 
                    { "hub", hubName }, 
                    { "method", methodName },
                    { "success", "false" },
                    { "error_type", "hub_exception" }
                });

                throw; // Re-throw HubException as-is
            }
            catch (OperationCanceledException cancelEx) when (cancelEx.InnerException is TimeoutException)
            {
                // Handle timeout specifically
                stopwatch.Stop();
                _logger.LogWarning(
                    "SignalR method timeout in {Hub}.{Method} for VirtualKey {VirtualKeyId} after {ElapsedMs}ms",
                    hubName, methodName, virtualKeyId, stopwatch.ElapsedMilliseconds);

                _metrics.HubMethodInvocations.Add(1, new TagList 
                { 
                    { "hub", hubName }, 
                    { "method", methodName },
                    { "success", "false" },
                    { "error_type", "timeout" }
                });

                throw new HubException("The operation timed out. Please try again.");
            }
            catch (OperationCanceledException)
            {
                // Handle cancellation (usually client disconnect)
                stopwatch.Stop();
                _logger.LogInformation(
                    "SignalR method cancelled in {Hub}.{Method} for VirtualKey {VirtualKeyId}",
                    hubName, methodName, virtualKeyId);

                _metrics.HubMethodInvocations.Add(1, new TagList 
                { 
                    { "hub", hubName }, 
                    { "method", methodName },
                    { "success", "false" },
                    { "error_type", "cancelled" }
                });

                throw new HubException("The operation was cancelled.");
            }
            catch (Exception ex)
            {
                // Catch all other exceptions
                stopwatch.Stop();
                var errorId = Guid.NewGuid().ToString();

                _logger.LogError(ex,
                    "Unhandled exception in SignalR {Hub}.{Method} for VirtualKey {VirtualKeyId}. ErrorId: {ErrorId}",
                    hubName, methodName, virtualKeyId, errorId);

                _metrics.HubMethodInvocations.Add(1, new TagList 
                { 
                    { "hub", hubName }, 
                    { "method", methodName },
                    { "success", "false" },
                    { "error_type", "unhandled" }
                });

                // Return a generic error to the client with an error ID for support
                throw new HubException($"An unexpected error occurred. Please try again. If the problem persists, contact support with error ID: {errorId}");
            }
        }

        /// <summary>
        /// Handles errors during connection lifecycle events.
        /// </summary>
        public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
        {
            try
            {
                await next(context);
            }
            catch (Exception ex)
            {
                var hubName = context.Hub.GetType().Name;
                _logger.LogError(ex,
                    "Error during OnConnectedAsync for {Hub} on connection {ConnectionId}",
                    hubName, context.Context.ConnectionId);

                // Connection filters use exceptions to reject unauthenticated or
                // rate-limited clients. Swallowing the exception leaves the transport
                // open even though the rest of the connection pipeline never ran.
                throw;
            }
        }

        /// <summary>
        /// Handles errors during disconnection lifecycle events.
        /// </summary>
        public async Task OnDisconnectedAsync(
            HubLifetimeContext context,
            Exception? exception,
            Func<HubLifetimeContext, Exception?, Task> next)
        {
            try
            {
                await next(context, exception);
            }
            catch (Exception ex)
            {
                var hubName = context.Hub.GetType().Name;
                _logger.LogError(ex,
                    "Error during OnDisconnectedAsync for {Hub} on connection {ConnectionId}",
                    hubName, context.Context.ConnectionId);
                
                // Don't re-throw - disconnection should complete regardless
            }
        }
    }
}
