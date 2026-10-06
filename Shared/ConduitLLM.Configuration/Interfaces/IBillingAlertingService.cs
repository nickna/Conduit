using System.Threading.Tasks;

namespace ConduitLLM.Configuration.Interfaces
{
    /// <summary>
    /// Service for handling critical billing system alerts and notifications
    /// </summary>
    public interface IBillingAlertingService
    {
        /// <summary>
        /// Sends a critical alert for billing system failures
        /// </summary>
        /// <param name="message">Alert message describing the failure</param>
        /// <param name="virtualKeyId">Optional virtual key ID associated with the failure</param>
        /// <param name="additionalContext">Optional additional context data</param>
        /// <returns>Task representing the async operation</returns>
        Task SendCriticalAlertAsync(
            string message,
            int? virtualKeyId = null,
            Dictionary<string, object?>? additionalContext = null);

    }
}
