using System.Security.Cryptography;
using System.Text;
using ConduitLLM.Core.Events;

namespace ConduitLLM.Core.Helpers;

public static class WebhookIdentity
{
    public const string HeaderName = "X-Webhook-Id";
    public static string DeliveryKey(WebhookDeliveryRequested request) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{request.EventId}\0{request.VirtualKeyId}\0{request.WebhookUrl}")));

    public static Dictionary<string, string> Headers(WebhookDeliveryRequested request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.Headers != null)
            foreach (var header in request.Headers)
                if (!header.Key.Equals(HeaderName, StringComparison.OrdinalIgnoreCase)) headers[header.Key] = header.Value;
        headers[HeaderName] = request.EventId;
        return headers;
    }
}
