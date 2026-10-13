using ConduitLLM.Admin.Extensions;
using ConduitLLM.Configuration.DTOs.IpFilter;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Core.Events;

namespace ConduitLLM.Admin.Services;

public partial class AdminIpFilterService
{
    public async Task<(bool Success, string? ErrorMessage, IpFilterDto? Filter)> UpdateFilterConditionalAsync(
        int id, UpdateIpFilterDto filter, ResourcePrecondition precondition, CancellationToken cancellationToken)
    {
        await using var db = await (_dbContextFactory ?? throw new InvalidOperationException(
            "Conditional IP filter updates require a database context factory.")).CreateDbContextAsync(cancellationToken);
        string? error = null;
        List<string> changed = [];
        IpFilterEntity? updated = null;
        var dto = await ResourceMutationGuard.ExecuteAsync<IpFilterEntity, IpFilterDto>(db, id, precondition,
            (_, entity, _) =>
            {
                (changed, error) = ApplyFilterPatch(entity, filter);
                if (changed.Count > 0) entity.UpdatedAt = DateTime.UtcNow;
                updated = entity;
                return Task.CompletedTask;
            }, entity => entity.ToDto(), cancellationToken);
        if (dto is null) return (false, $"IP filter with ID {id} not found", null);
        if (error is not null) return (false, error, null);
        if (changed.Count > 0) await NotifyConditionalFilterChangeAsync(updated!, "Updated", changed.ToArray());
        return (true, null, dto);
    }

    public async Task<(bool Success, string? ErrorMessage)> DeleteFilterConditionalAsync(int id,
        ResourcePrecondition precondition, CancellationToken cancellationToken)
    {
        await using var db = await (_dbContextFactory ?? throw new InvalidOperationException(
            "Conditional IP filter deletes require a database context factory.")).CreateDbContextAsync(cancellationToken);
        var deleted = await ResourceMutationGuard.ExecuteAsync<IpFilterEntity, IpFilterEntity>(db, id, precondition,
            (context, entity, _) =>
            {
                context.IpFilters.Remove(entity);
                return Task.CompletedTask;
            }, entity => entity, cancellationToken);
        if (deleted is null) return (false, $"IP filter with ID {id} not found");
        await NotifyConditionalFilterChangeAsync(deleted, "Deleted", []);
        return (true, null);
    }

    private Task NotifyConditionalFilterChangeAsync(IpFilterEntity entity, string changeType, string[] changed) =>
        PublishEventAsync(new IpFilterChanged
        {
            FilterId = entity.Id, IpAddressOrCidr = entity.IpAddressOrCidr,
            FilterType = entity.FilterType, IsEnabled = entity.IsEnabled, ChangeType = changeType,
            ChangedProperties = changed, Description = entity.Description ?? string.Empty,
            CorrelationId = Guid.NewGuid().ToString()
        }, $"{changeType} IP filter {entity.Id}", new { ChangedProperties = string.Join(", ", changed) });
}
