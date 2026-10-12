using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Services
{
    /// <summary>
    /// Validates Redis URL format and logs warnings for invalid URLs
    /// </summary>
    public static class RedisUrlValidator
    {
        private static readonly Regex RedisUrlRegex = new Regex(
            @"^rediss?://(?:(?<username>[^:@]+)?:?(?<password>[^@]+)?@)?(?<host>[^:@/?#\s]+)(?::(?<port>\d+))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Validates a Redis URL and logs appropriate warnings
        /// </summary>
        /// <param name="redisUrl">The Redis URL to validate</param>
        /// <param name="logger">Logger for warnings</param>
        /// <param name="serviceName">Name of the service for logging context</param>
        /// <returns>True if valid, false otherwise</returns>
        public static bool ValidateAndLog(string? redisUrl, ILogger logger, string serviceName)
        {
            if (string.IsNullOrWhiteSpace(redisUrl))
            {
                return false;
            }

            try
            {
                // Validate the URL format first
                if (!RedisUrlRegex.IsMatch(redisUrl))
                {
                    logger.LogWarning(
                        "{ServiceName}: Redis URL format appears invalid. Expected format: redis://[username]:[password]@hostname:port",
                        serviceName);
                    return false;
                }
                
                // Try to parse it
                var parsed = Utilities.RedisUrlParser.ParseRedisUrl(redisUrl);

                // Check for common issues
                if (redisUrl.Contains(" "))
                {
                    logger.LogWarning(
                        "{ServiceName}: Redis URL contains spaces. This is likely invalid",
                        serviceName);
                    return false;
                }

                // Check for missing port (common mistake)
                if (!redisUrl.Contains(":637") && !redisUrl.Contains(":638") && !redisUrl.Contains(":"))
                {
                    logger.LogWarning(
                        "{ServiceName}: Redis URL appears to be missing port number. Default Redis port is 6379",
                        serviceName);
                }

                logger.LogInformation(
                    "{ServiceName}: Redis URL validated successfully",
                    serviceName);
                
                return true;
            }
            catch (Exception ex)
            {
                // Input and exception messages can both contain credentials. Omit them even
                // when malformed input cannot be parsed or sanitized safely.
                logger.LogError(
                    "{ServiceName}: Failed to validate Redis URL ({ExceptionType})",
                    serviceName,
                    ex.GetType().Name);
                return false;
            }
        }
    }
}
