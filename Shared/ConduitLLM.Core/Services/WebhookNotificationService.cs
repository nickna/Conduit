using System.Net.Http.Json;
using System.Text.Json;
using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Core.Services;

/// <summary>One bounded POST. Durable delivery owns retries and receiver admission.</summary>
public class WebhookNotificationService : IWebhookNotificationService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebhookNotificationService> _logger;
    private readonly WebhookDeliveryOptions _options;
    private readonly TimeProvider _clock;

    public WebhookNotificationService(HttpClient httpClient, ILogger<WebhookNotificationService> logger,
        IOptions<WebhookDeliveryOptions>? options = null, TimeProvider? clock = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? new();
        if (!_options.IsValid()) throw new ArgumentException("Invalid webhook delivery timeouts.", nameof(options));
        _clock = clock ?? TimeProvider.System;
    }

    public Task<WebhookSendResult> SendTaskCompletionWebhookAsync(string webhookUrl, object payload,
        Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) =>
        SendAsync(webhookUrl, payload, headers, "completion", null, cancellationToken);

    public Task<WebhookSendResult> SendTaskProgressWebhookAsync(string webhookUrl, object payload,
        Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) =>
        SendAsync(webhookUrl, payload, headers, "progress", null, cancellationToken);

    public Task<WebhookSendResult> SendWebhookAsync(string webhookUrl, object payload,
        Dictionary<string, string>? headers = null, TimeSpan? customTimeout = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(webhookUrl, payload, headers, "custom", customTimeout, cancellationToken);

    private async Task<WebhookSendResult> SendAsync(string webhookUrl, object payload,
        Dictionary<string, string>? headers, string type, TimeSpan? customTimeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timeout = customTimeout ?? TimeSpan.FromSeconds(_options.AttemptTimeoutSeconds);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(300) ||
            !Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return WebhookSendResult.Failed(null, "Invalid callback URL or timeout.", WebhookFailureKind.InvalidRequest);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            if (headers != null)
                foreach (var header in headers)
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Headers.TryAddWithoutValidation("X-Webhook-Type", type);
            request.Headers.TryAddWithoutValidation("X-Webhook-Timestamp", _clock.GetUtcNow().ToUnixTimeSeconds().ToString());
            request.Content = JsonContent.Create(payload,
                CoreJsonTypeInfo.Require(payload.GetType(), ConduitJsonOptions.Wire));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode) return WebhookSendResult.Ok(status);

            var retryAfter = response.Headers.RetryAfter;
            var delay = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - _clock.GetUtcNow() : (TimeSpan?)null);
            return WebhookSendResult.Failed(status, $"Endpoint returned HTTP {status}.", WebhookFailureKind.Http, delay);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return WebhookSendResult.Failed(null, "Receiver attempt timed out.", WebhookFailureKind.Timeout);
        }
        catch (HttpRequestException)
        {
            _logger.LogDebug("Webhook receiver connection failed");
            return WebhookSendResult.Failed(null, "Receiver connection failed.", WebhookFailureKind.Network);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or NotSupportedException)
        {
            // Neither receiver reason phrases nor exception text are safe diagnostics.
            return WebhookSendResult.Failed(null, "Invalid callback request or payload.", WebhookFailureKind.InvalidRequest);
        }
    }
}
