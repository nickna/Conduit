using Microsoft.AspNetCore.SignalR;
using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Gateway.Hubs;

namespace ConduitLLM.Gateway.Services.SpendNotification
{
    /// <summary>
    /// Analyzes spending patterns for unusual activity
    /// </summary>
    public interface ISpendPatternAnalyzer
    {
        /// <summary>
        /// Checks for unusual spending patterns for a virtual key
        /// </summary>
        Task CheckUnusualSpendingAsync(int virtualKeyId);
        Task CheckUnusualSpendingAsync(int virtualKeyId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CheckUnusualSpendingAsync(virtualKeyId);
        }
        
        /// <summary>
        /// Analyzes all spending patterns periodically
        /// </summary>
        Task AnalyzeAllPatternsAsync();
        Task AnalyzeAllPatternsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return AnalyzeAllPatternsAsync();
        }
    }

    /// <summary>
    /// Implementation of spending pattern analyzer
    /// </summary>
    public class SpendPatternAnalyzer : ISpendPatternAnalyzer
    {
        private readonly IHubContext<SpendNotificationHub> _hubContext;
        private readonly ISpendDataRepository _repository;
        private readonly ILogger<SpendPatternAnalyzer> _logger;
        
        private readonly TimeSpan _unusualAlertCooldown = TimeSpan.FromHours(1);
        
        // Thresholds for unusual patterns
        private const int HighFrequencyThreshold = 60; // requests per hour
        private const decimal SpendingSpikeThreshold = 0.1m; // 10% of daily average
        
        public SpendPatternAnalyzer(
            IHubContext<SpendNotificationHub> hubContext,
            ISpendDataRepository repository,
            ILogger<SpendPatternAnalyzer> logger)
        {
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task CheckUnusualSpendingAsync(int virtualKeyId) => CheckUnusualSpendingAsync(virtualKeyId, CancellationToken.None);

        public async Task CheckUnusualSpendingAsync(int virtualKeyId, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pattern = await _repository.GetSpendingPatternAsync(virtualKeyId);
                cancellationToken.ThrowIfCancellationRequested();
                if (pattern == null) return;
                
                var analysis = AnalyzePattern(pattern);
                if (!analysis.IsUnusual) return;
                
                // Check cooldown for unusual spending alerts
                var cooldownKey = $"unusual:{analysis.PatternType}";
                var inCooldown = await _repository.IsAlertInCooldownAsync(virtualKeyId, cooldownKey);
                cancellationToken.ThrowIfCancellationRequested();
                if (inCooldown)
                {
                    return;
                }
                cancellationToken.ThrowIfCancellationRequested();
                
                // Set cooldown
                await _repository.SetAlertCooldownAsync(virtualKeyId, cooldownKey, _unusualAlertCooldown);
                cancellationToken.ThrowIfCancellationRequested();
                
                // Send unusual spending notification
                await SendUnusualSpendingNotificationAsync(virtualKeyId, analysis, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking unusual spending patterns for VirtualKey {VirtualKeyId}", virtualKeyId);
            }
        }

        public Task AnalyzeAllPatternsAsync() => AnalyzeAllPatternsAsync(CancellationToken.None);

        public async Task AnalyzeAllPatternsAsync(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var virtualKeyIds = await _repository.GetAllPatternKeysAsync(cancellationToken);
                
                foreach (var virtualKeyId in virtualKeyIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await CheckUnusualSpendingAsync(virtualKeyId, cancellationToken);
                }
                
                _logger.LogDebug("Analyzed {Count} spending patterns", virtualKeyIds.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing all spending patterns");
            }
        }

        private PatternAnalysis AnalyzePattern(SpendingPatternData pattern)
        {
            var analysis = new PatternAnalysis { IsUnusual = false };
            
            // High frequency pattern detection
            if (pattern.HourlyCount > HighFrequencyThreshold)
            {
                analysis.IsUnusual = true;
                analysis.PatternType = "High Frequency";
                analysis.Description = $"Unusually high request rate: {pattern.HourlyCount} requests in the last hour";
                analysis.CurrentRate = pattern.HourlyCount;
                // Baseline from the key's own 24h history (average hourly request count),
                // mirroring how the spending-spike branch derives its baseline
                analysis.NormalRate = pattern.DailyCount / 24m;
            }
            // Spike detection (hourly spend > 10% of daily average)
            else if (pattern.DailyCount > 0 && pattern.HourlyTotal > (pattern.DailyTotal * SpendingSpikeThreshold))
            {
                analysis.IsUnusual = true;
                analysis.PatternType = "Spending Spike";
                analysis.Description = $"Sudden increase in spending: ${pattern.HourlyTotal:F2} in the last hour";
                analysis.CurrentRate = pattern.HourlyTotal;
                analysis.NormalRate = pattern.DailyTotal / 24m; // Daily average per hour
            }
            
            if (analysis.IsUnusual)
            {
                analysis.DeviationPercentage = analysis.NormalRate > 0 
                    ? ((analysis.CurrentRate - analysis.NormalRate) / analysis.NormalRate * 100) 
                    : 100;
            }
            
            return analysis;
        }

        private async Task SendUnusualSpendingNotificationAsync(int virtualKeyId, PatternAnalysis analysis, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var notification = new UnusualSpendingNotification
                {
                    ActivityType = analysis.PatternType,
                    Description = analysis.Description,
                    CurrentRate = analysis.CurrentRate,
                    NormalRate = analysis.NormalRate,
                    DeviationPercentage = (double)analysis.DeviationPercentage,
                    Recommendations = GetUnusualSpendingRecommendations()
                };
                
                var groupName = $"vkey-{virtualKeyId}";
                await _hubContext.Clients.Group(groupName).SendAsync("UnusualSpendingDetected", notification, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                
                _logger.LogWarning(
                    "[UnusualSpending] Detected - VirtualKey: {VirtualKeyId}, Pattern: {PatternType}, " +
                    "Description: {Description}",
                    virtualKeyId, analysis.PatternType, analysis.Description);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending unusual spending notification for VirtualKey {VirtualKeyId}", 
                    virtualKeyId);
            }
        }

        private static List<string> GetUnusualSpendingRecommendations()
        {
            return new List<string>
            {
                "Review recent API usage for anomalies",
                "Check for potential runaway processes",
                "Consider implementing rate limiting",
                "Verify API keys haven't been compromised"
            };
        }

        private class PatternAnalysis
        {
            public bool IsUnusual { get; set; }
            public string PatternType { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public decimal CurrentRate { get; set; }
            public decimal NormalRate { get; set; }
            public decimal DeviationPercentage { get; set; }
        }
    }
}
