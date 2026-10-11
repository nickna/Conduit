using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConduitLLM.Core.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Gateway.HealthChecks
{
    /// <summary>
    /// Health check for monitoring leader election status of background services
    /// </summary>
    public class LeaderElectionHealthCheck : IHealthCheck
    {
        private readonly ILeaderElectionService _leaderElectionService;
        private readonly ILogger<LeaderElectionHealthCheck> _logger;
        private readonly string[] _criticalServices;

        public LeaderElectionHealthCheck(
            ILeaderElectionService leaderElectionService,
            ILogger<LeaderElectionHealthCheck> logger)
        {
            _leaderElectionService = leaderElectionService ?? throw new ArgumentNullException(nameof(leaderElectionService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            // Define critical services that must have a leader
            _criticalServices = new[]
            {
#if !CONDUIT_NATIVE_AOT
                "BillingAuditService",
#endif
                "SpendNotificationService",
                "BatchSpendUpdateService",
                "WebhookDeliveryNotificationService"
            };
        }

        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var data = new Dictionary<string, object>();
                var servicesWithoutLeader = new List<string>();
                var leadershipStatus = new Dictionary<string, string>();

                foreach (var serviceName in _criticalServices)
                {
                    var currentLeader = await _leaderElectionService.GetCurrentLeaderAsync(serviceName, cancellationToken);
                    
                    if (string.IsNullOrEmpty(currentLeader))
                    {
                        servicesWithoutLeader.Add(serviceName);
                        leadershipStatus[serviceName] = "No Leader";
                    }
                    else
                    {
                        var isLeader = await _leaderElectionService.IsLeaderAsync(serviceName, cancellationToken);
                        leadershipStatus[serviceName] = isLeader ? $"Leader (This Instance)" : $"Follower (Leader: {currentLeader})";
                    }
                }

                data["LeadershipStatus"] = leadershipStatus;
                data["ServicesWithoutLeader"] = servicesWithoutLeader.Count;
                data["TotalServices"] = _criticalServices.Length;

                if (servicesWithoutLeader.Any())
                {
                    var message = $"Services without leader: {string.Join(", ", servicesWithoutLeader)}";
                    _logger.LogWarning("Leader election health check degraded: {Message}", message);
                    
                    return HealthCheckResult.Degraded(
                        $"Leader election degraded: {servicesWithoutLeader.Count} services without leader",
                        data: data);
                }

                return HealthCheckResult.Healthy(
                    "All critical services have elected leaders",
                    data: data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during leader election health check");
                
                return HealthCheckResult.Unhealthy(
                    "Failed to check leader election status",
                    exception: ex,
                    data: new Dictionary<string, object>
                    {
                        ["Error"] = ex.Message
                    });
            }
        }
    }
}
