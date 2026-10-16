using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Services;

/// <summary>Validates the shared Redis configuration contract without logging credentials.</summary>
public static class RedisUrlValidator
{
    /// <summary>Validates a URI, endpoint, or options string using the same parser as the hosts.</summary>
    public static bool ValidateAndLog(string? redisUrl, ILogger logger, string serviceName)
    {
        if (string.IsNullOrWhiteSpace(redisUrl)) return false;

        try
        {
            try
            {
                Utilities.RedisUrlParser.ParseConfiguration(redisUrl);
            }
            catch (ArgumentException)
            {
                logger.LogWarning("{ServiceName}: Redis configuration is invalid. Expected a Redis URI, TCP endpoint, or supported StackExchange.Redis options.",
                    serviceName);
                return false;
            }

            logger.LogInformation("{ServiceName}: Redis configuration validated successfully", serviceName);
            return true;
        }
        catch (Exception exception)
        {
            // Logging providers can also throw exceptions containing their inputs.
            // Keep only the exception type, without arbitrary exception text.
            logger.LogError("{ServiceName}: Failed to validate Redis configuration ({ExceptionType})",
                serviceName, exception.GetType().Name);
            return false;
        }
    }
}
