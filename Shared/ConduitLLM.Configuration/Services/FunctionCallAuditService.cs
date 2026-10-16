using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Services;

/// <summary>
/// Service for logging function call audit events with batch processing.
/// Implements IHostedService for background batch flushing.
/// </summary>
public class FunctionCallAuditService : BatchAuditServiceBase<FunctionCallAudit>, IFunctionCallAuditService
{
    /// <summary>
    /// Creates a new instance of the FunctionCallAuditService.
    /// </summary>
    /// <param name="serviceProvider">Service provider for creating scoped DbContexts</param>
    /// <param name="logger">Logger instance</param>
    public FunctionCallAuditService(
        IServiceProvider serviceProvider,
        ILogger<FunctionCallAuditService> logger)
        : base(serviceProvider, logger)
    {
    }

    #region Template Method Implementations

    /// <inheritdoc/>
    protected override DbSet<FunctionCallAudit> GetDbSet(ConduitDbContext context)
        => context.FunctionCallAudits;

    /// <inheritdoc/>
    protected override string EntityName => "FunctionCall";

    #endregion

    #region Configuration Overrides

    /// <summary>
    /// Uses bulk delete (ExecuteDeleteAsync) for more efficient cleanup.
    /// </summary>
    protected override bool UseBulkDelete => true;

    /// <summary>
    /// Overrides the default data retention scheduling to run only once on startup.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    protected override async Task ScheduleDataRetentionAsync(CancellationToken cancellationToken)
    {
        // Run cleanup once on startup (no periodic cleanup)
        try
        {
            await CleanupOldEventsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during startup {EntityName} audit event cleanup", EntityName);
        }
    }

    #endregion

    #region IFunctionCallAuditService Implementation (Wrapper Methods)

    /// <inheritdoc/>
    public void LogFunctionCallEvent(FunctionCallAudit auditEvent)
        => LogEvent(auditEvent);

    /// <inheritdoc/>
    public Task LogFunctionCallEventAsync(FunctionCallAudit auditEvent)
        => LogEventAsync(auditEvent);

    /// <inheritdoc/>
    public new Task FlushEventsAsync()
        => base.FlushEventsAsync();

    #endregion

    #region Domain-Specific Query Methods

    /// <inheritdoc/>
    public async Task<(List<FunctionCallAudit> Events, int TotalCount)> GetAuditEventsAsync(
        DateTime from,
        DateTime to,
        FunctionCallAuditEventType? eventType = null,
        int? virtualKeyId = null,
        int? functionConfigurationId = null,
        Guid? chatCompletionId = null,
        int pageNumber = 1,
        int pageSize = 100)
    {
        return await GetPagedEventsAsync(from, to, pageNumber, pageSize, query =>
        {
            if (eventType.HasValue)
                query = query.Where(e => e.EventType == eventType.Value);
            if (virtualKeyId.HasValue)
                query = query.Where(e => e.VirtualKeyId == virtualKeyId.Value);
            if (functionConfigurationId.HasValue)
                query = query.Where(e => e.FunctionConfigurationId == functionConfigurationId.Value);
            if (chatCompletionId.HasValue)
                query = query.Where(e => e.ChatCompletionId == chatCompletionId.Value);
            return query;
        });
    }

    /// <inheritdoc/>
    public async Task<FunctionCallAuditSummary> GetAuditSummaryAsync(
        DateTime from,
        DateTime to,
        int? virtualKeyId = null)
    {
        return await ExecuteQueryAsync(async context =>
        {
            var events = await GetFilteredEventsAsync(from, to, query =>
            {
                if (virtualKeyId.HasValue)
                    query = query.Where(e => e.VirtualKeyId == virtualKeyId.Value);
                return query;
            });

            var summary = new FunctionCallAuditSummary
            {
                TotalFunctionCalls = events.Count,
                SuccessfulCalls = events.Count(e => e.EventType == FunctionCallAuditEventType.FunctionCallExecutionCompleted),
                FailedCalls = events.Count(e => e.EventType == FunctionCallAuditEventType.FunctionCallExecutionFailed),
                TotalCost = events.Where(e => e.Cost.HasValue).Sum(e => e.Cost!.Value),
                CallsByEventType = events.GroupBy(e => e.EventType)
                    .ToDictionary(g => g.Key, g => g.Count())
            };

            // Get calls by function configuration
            var functionConfigs = await context.FunctionConfigurations
                .Where(fc => events.Select(e => e.FunctionConfigurationId).Contains(fc.Id))
                .ToDictionaryAsync(fc => fc.Id, fc => fc.ConfigurationName);

            summary.CallsByFunction = events
                .GroupBy(e => e.FunctionConfigurationId)
                .ToDictionary(
                    g => functionConfigs.TryGetValue(g.Key, out var name) ? name : $"Unknown ({g.Key})",
                    g => g.Count());

            return summary;
        });
    }

    #endregion
}
