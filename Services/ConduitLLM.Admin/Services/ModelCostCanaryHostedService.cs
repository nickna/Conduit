using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Pricing;
using ConduitLLM.Core.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Admin.Services;

/// <summary>
/// Exercises the production cost lookup and calculation path for every active model mapping.
/// No provider request is made and no customer spend is recorded.
/// </summary>
public sealed class ModelCostCanaryHostedService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IServiceProvider _serviceProvider;
    private readonly BillingCostCanaryOptions _options;
    private readonly ILogger<ModelCostCanaryHostedService> _logger;

    public ModelCostCanaryHostedService(
        IServiceProvider serviceProvider,
        IOptions<BillingCostCanaryOptions> options,
        ILogger<ModelCostCanaryHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Model cost canary is disabled");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromMinutes(_options.IntervalMinutes), stoppingToken);
        }
    }

    /// <summary>
    /// Executes one complete canary pass. Public to support operational and integration testing.
    /// </summary>
    public async Task<ModelCostCanaryRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var checkedCount = 0;
        var failedCount = 0;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConduitDbContext>>();
            var costService = scope.ServiceProvider.GetRequiredService<ICostCalculationService>();
            var auditService = scope.ServiceProvider.GetRequiredService<IBillingAuditService>();
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            var mappings = await context.ModelProviderMappings
                .AsNoTracking()
                .Include(mapping => mapping.Provider)
                .Include(mapping => mapping.ModelProviderTypeAssociation)
                    .ThenInclude(association => association.ModelCost)
                .Where(mapping => mapping.IsEnabled && mapping.Provider.IsEnabled &&
                    mapping.ModelProviderTypeAssociation.IsEnabled)
                .OrderBy(mapping => mapping.ModelAlias)
                .ToListAsync(cancellationToken);

            foreach (var mapping in mappings)
            {
                checkedCount++;
                var modelCost = mapping.ModelProviderTypeAssociation.ModelCost;
                var modelCostLabel = modelCost?.Id.ToString() ?? "missing";

                try
                {
                    if (modelCost == null)
                        throw new ModelCostCanaryException("missing_cost", "No ModelCost is associated with the active mapping.");

                    ModelPricingConfigurationValidator.Validate(modelCost.PricingModel, modelCost.PricingConfiguration);
                    var usage = CreateSyntheticUsage(modelCost);
                    var calculatedCost = await costService.CalculateCostByIdAsync(modelCost.Id, usage, cancellationToken);

                    if (calculatedCost <= 0m)
                        throw new ModelCostCanaryException("zero_cost", "Synthetic positive usage calculated a non-positive cost.");

                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    ModelCostCanaryMetrics.Status.WithLabels(mapping.ModelAlias, modelCostLabel).Set(1);
                    ModelCostCanaryMetrics.LastSuccessTimestamp.WithLabels(mapping.ModelAlias, modelCostLabel).Set(now);
                    ModelCostCanaryMetrics.Runs.WithLabels("success", "none").Inc();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failedCount++;
                    var reason = ClassifyFailure(exception);
                    ModelCostCanaryMetrics.Status.WithLabels(mapping.ModelAlias, modelCostLabel).Set(0);
                    ModelCostCanaryMetrics.Runs.WithLabels("failure", reason).Inc();

                    _logger.LogError(exception,
                        "BILLING ALERT: Model cost canary failed for {ModelAlias} (mapping {MappingId}, ModelCost {ModelCostId})",
                        mapping.ModelAlias, mapping.Id, modelCostLabel);

                    await TryLogFailureAuditAsync(auditService, new BillingAuditEvent
                    {
                        EventType = BillingAuditEventType.ModelCostCanaryFailed,
                        Model = mapping.ModelAlias,
                        ProviderType = mapping.Provider.ProviderType.ToString(),
                        CalculatedCost = 0m,
                        FailureReason = Truncate(exception.Message, 500),
                        MetadataJson = AdminJson.Serialize(new Dictionary<string, object?>
                        {
                            ["mappingId"] = mapping.Id,
                            ["modelCostId"] = modelCost?.Id,
                            ["pricingModel"] = modelCost?.PricingModel.ToString(),
                            ["reason"] = reason
                        })
                    });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failedCount++;
            ModelCostCanaryMetrics.Runs.WithLabels("failure", "run_error").Inc();
            _logger.LogError(exception, "BILLING ALERT: Model cost canary run could not complete");
        }
        finally
        {
            ModelCostCanaryMetrics.ActiveModels.Set(checkedCount);
            ModelCostCanaryMetrics.FailedModels.Set(failedCount);
            ModelCostCanaryMetrics.LastRunTimestamp.Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        _logger.LogInformation(
            "Model cost canary completed: {CheckedCount} active mappings checked, {FailedCount} failed",
            checkedCount, failedCount);
        return new ModelCostCanaryRunResult(checkedCount, failedCount);
    }

    /// <summary>
    /// Creates positive usage that reaches the configured pricing strategy without calling a provider.
    /// </summary>
    public static Usage CreateSyntheticUsage(ModelCost modelCost)
    {
        var usage = new Usage
        {
            PromptTokens = 1_000_000,
            CompletionTokens = 1_000_000,
            ImageCount = 1,
            VideoDurationSeconds = 1,
            InferenceSteps = 1,
            SearchUnits = 1_000,
            AudioDurationSeconds = 60,
            TtsCharacters = 1_000
        };

        if (modelCost.PricingModel == PricingModel.PerVideo &&
            !string.IsNullOrWhiteSpace(modelCost.PricingConfiguration))
        {
            var config = AdminJson.Deserialize<PerVideoPricingConfig>(modelCost.PricingConfiguration, JsonOptions);
            var rateKey = config?.Rates?.Keys.FirstOrDefault();
            var separator = rateKey?.LastIndexOf('_') ?? -1;
            if (separator > 0 && int.TryParse(rateKey![(separator + 1)..], out var duration))
            {
                usage.VideoResolution = rateKey[..separator];
                usage.VideoDurationSeconds = duration;
            }
        }

        return usage;
    }

    private static string ClassifyFailure(Exception exception) => exception switch
    {
        ModelCostCanaryException canary => canary.Reason,
        ArgumentException => "invalid_configuration",
        JsonException => "invalid_configuration",
        InvalidOperationException when exception.Message.Contains("No active model cost", StringComparison.OrdinalIgnoreCase)
            => "inactive_or_expired_cost",
        InvalidOperationException => "calculation_error",
        _ => "unexpected_error"
    };

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private async Task TryLogFailureAuditAsync(IBillingAuditService auditService, BillingAuditEvent auditEvent)
    {
        try
        {
            await auditService.LogBillingEventAsync(auditEvent);
        }
        catch (Exception exception)
        {
            // Metrics and the remaining model checks must survive a failure in the audit sink.
            _logger.LogError(exception,
                "Failed to persist model cost canary audit event for {Model}; continuing canary run",
                auditEvent.Model);
        }
    }

    private sealed class ModelCostCanaryException(string reason, string message) : Exception(message)
    {
        internal string Reason { get; } = reason;
    }
}

public sealed record ModelCostCanaryRunResult(int CheckedCount, int FailedCount);
