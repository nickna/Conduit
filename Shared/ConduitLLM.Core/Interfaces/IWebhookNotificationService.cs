namespace ConduitLLM.Core.Interfaces
{
    /// <summary>
    /// Result of a webhook send attempt.
    /// </summary>
    /// <param name="Success">Whether the endpoint returned a success status code.</param>
    /// <param name="StatusCode">The HTTP status code returned by the endpoint, or null when no response was received (timeout, connection error).</param>
    /// <param name="Error">Description of the failure, or null on success.</param>
    public enum WebhookFailureKind { None, Http, Network, Timeout, InvalidRequest }

    public record WebhookSendResult(bool Success, int? StatusCode, string? Error,
        WebhookFailureKind FailureKind = WebhookFailureKind.None, TimeSpan? RetryAfter = null)
    {
        public static WebhookSendResult Ok(int statusCode) => new(true, statusCode, null);
        public static WebhookSendResult Failed(int? statusCode, string error,
            WebhookFailureKind kind = WebhookFailureKind.Http, TimeSpan? retryAfter = null) =>
            new(false, statusCode, error, kind, retryAfter);
    }

    /// <summary>
    /// Service for sending webhook notifications to external endpoints.
    /// </summary>
    public interface IWebhookNotificationService
    {
        /// <summary>
        /// Sends a webhook notification about task completion.
        /// </summary>
        /// <param name="webhookUrl">The URL to send the notification to.</param>
        /// <param name="payload">The payload to send in the webhook request.</param>
        /// <param name="headers">Optional headers to include in the request.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The send result, including the endpoint's actual HTTP status code when a response was received.</returns>
        Task<WebhookSendResult> SendTaskCompletionWebhookAsync(
            string webhookUrl,
            object payload,
            Dictionary<string, string>? headers = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a webhook notification about task progress.
        /// </summary>
        /// <param name="webhookUrl">The URL to send the notification to.</param>
        /// <param name="payload">The payload to send in the webhook request.</param>
        /// <param name="headers">Optional headers to include in the request.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The send result, including the endpoint's actual HTTP status code when a response was received.</returns>
        Task<WebhookSendResult> SendTaskProgressWebhookAsync(
            string webhookUrl,
            object payload,
            Dictionary<string, string>? headers = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a webhook with custom timeout support.
        /// </summary>
        /// <param name="webhookUrl">The URL to send the notification to.</param>
        /// <param name="payload">The payload to send in the webhook request.</param>
        /// <param name="headers">Optional headers to include in the request.</param>
        /// <param name="customTimeout">Custom timeout for this specific request.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The send result, including the endpoint's actual HTTP status code when a response was received.</returns>
        Task<WebhookSendResult> SendWebhookAsync(
            string webhookUrl,
            object payload,
            Dictionary<string, string>? headers = null,
            TimeSpan? customTimeout = null,
            CancellationToken cancellationToken = default);
    }
}
