using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Services;

using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Admin.Services;

/// <summary>
/// Reports legacy pricing rows that predate current write-time invariant validation.
/// It intentionally does not disable them so upgrades cannot create a billing outage.
/// </summary>
public sealed class PricingConfigurationAuditHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PricingConfigurationAuditHostedService> _logger;

    public PricingConfigurationAuditHostedService(
        IServiceProvider serviceProvider,
        ILogger<PricingConfigurationAuditHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConduitDbContext>>();
            var audit = scope.ServiceProvider.GetRequiredService<IBillingAuditService>();
            await using var context = await contextFactory.CreateDbContextAsync(stoppingToken);
            var costs = await context.ModelCosts.AsNoTracking().ToListAsync(stoppingToken);
            var invalidCount = 0;

            foreach (var cost in costs)
            {
                try
                {
                    ModelPricingConfigurationValidator.Validate(cost.PricingModel, cost.PricingConfiguration);
                }
                catch (ArgumentException exception)
                {
                    invalidCount++;
                    _logger.LogError(
                        "Existing ModelCost {ModelCostId} ({CostName}) violates pricing invariants: {Reason}",
                        cost.Id, cost.CostName, exception.Message);
                    await audit.LogBillingEventAsync(new BillingAuditEvent
                    {
                        EventType = BillingAuditEventType.InvalidPricingConfiguration,
                        Model = cost.CostName,
                        FailureReason = exception.Message.Length <= 500 ? exception.Message : exception.Message[..500],
                        MetadataJson = AdminJson.Serialize(
                            new Dictionary<string, object?> { ["modelCostId"] = cost.Id })
                    });
                }
            }

            _logger.LogInformation(
                "Pricing configuration startup audit completed: {Total} checked, {Invalid} invalid",
                costs.Count, invalidCount);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Pricing configuration startup audit could not complete");
        }
    }
}
