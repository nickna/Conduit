using System.Text;
using System.Text.Json;
using ConduitLLM.Core.Serialization;

namespace ConduitLLM.Core.Helpers
{
    /// <summary>
    /// Helper class for webhook payload serialization with size limits
    /// </summary>
    public static class WebhookPayloadHelper
    {
        private const int MAX_PAYLOAD_SIZE_BYTES = 1024 * 1024; // 1MB limit
        private static readonly JsonSerializerOptions JsonOptions = Serialization.ConduitJsonOptions.Compact;
        
        /// <summary>
        /// Serializes a webhook payload to JSON with size validation
        /// </summary>
        /// <param name="payload">The payload object to serialize</param>
        /// <param name="maxSizeBytes">Maximum allowed size in bytes (default 1MB)</param>
        /// <returns>Serialized JSON string</returns>
        /// <exception cref="InvalidOperationException">Thrown when payload exceeds size limit</exception>
        public static string SerializePayload(object payload, int maxSizeBytes = MAX_PAYLOAD_SIZE_BYTES)
        {
            if (payload == null)
            {
                return "{}";
            }
            
            var json = JsonSerializer.Serialize(
                payload,
                CoreJsonTypeInfo.Require(payload.GetType(), JsonOptions));
            var sizeInBytes = Encoding.UTF8.GetByteCount(json);
            
            if (sizeInBytes > maxSizeBytes)
            {
                // Try to create a truncated version with error info
                var truncatedPayload = new WebhookPayloadTooLarge(
                    "Payload too large",
                    sizeInBytes,
                    maxSizeBytes,
                    true);
                
                json = JsonSerializer.Serialize(
                    truncatedPayload,
                    new CoreInternalJsonContext(
                        new JsonSerializerOptions(JsonOptions)).WebhookPayloadTooLarge);
            }
            
            return json;
        }
        
        /// <summary>
        /// Validates if a JSON string is within size limits
        /// </summary>
        /// <param name="json">The JSON string to validate</param>
        /// <param name="maxSizeBytes">Maximum allowed size in bytes</param>
        /// <returns>True if within limits, false otherwise</returns>
        public static bool IsWithinSizeLimit(string json, int maxSizeBytes = MAX_PAYLOAD_SIZE_BYTES)
        {
            if (string.IsNullOrEmpty(json))
            {
                return true;
            }
            
            var sizeInBytes = Encoding.UTF8.GetByteCount(json);
            return sizeInBytes <= maxSizeBytes;
        }
    }
}
