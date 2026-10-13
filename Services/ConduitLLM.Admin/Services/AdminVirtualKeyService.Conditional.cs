using System.Text.Json;
using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Configuration.DTOs.VirtualKey;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Utilities;
using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Admin.Services;

public partial class AdminVirtualKeyService
{
    public async Task<VirtualKeyUpdateResult?> UpdateVirtualKeyConditionalAsync(int id,
        UpdateVirtualKeyRequestDto request, ResourcePrecondition precondition, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        VirtualKeyDto? before = null;
        string[] changed = [];
        VirtualKey? updated = null;
        var current = await ResourceMutationGuard.ExecuteAsync<VirtualKey, VirtualKeyDto>(db, id, precondition,
            async (context, key, ct) =>
            {
                before = VirtualKeyUtilities.MapToDto(key);
                // Object-member merges must use the locked current snapshot, including wildcard requests.
                if (request.TryGetPatchedProperty(nameof(request.Metadata), before.Metadata,
                        out Dictionary<string, JsonElement>? metadata))
                    request.Metadata = metadata;
                if (request.TryGetPatchedProperty(nameof(request.ModelRateLimits), before.ModelRateLimits,
                        out Dictionary<string, ModelRateLimitDto>? limits))
                    request.ModelRateLimits = limits;
                await ValidateModelRateLimitsAsync(request.ModelRateLimits, context, ct);
                changed = await ApplyVirtualKeyUpdateAsync(key, request,
                    groupId => context.VirtualKeyGroups.SingleOrDefaultAsync(group => group.Id == groupId, ct));
                if (changed.Length > 0) key.UpdatedAt = DateTime.UtcNow;
                updated = key;
            }, VirtualKeyUtilities.MapToDto, cancellationToken);
        if (current is null) return null;
        if (changed.Length > 0) await NotifyVirtualKeyUpdatedAsync(updated!, changed);
        return new VirtualKeyUpdateResult(before!, current);
    }

    public async Task<bool> DeleteVirtualKeyConditionalAsync(int id, ResourcePrecondition precondition,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var deleted = await ResourceMutationGuard.ExecuteAsync<VirtualKey, VirtualKey>(db, id, precondition,
            async (context, key, ct) =>
            {
                await CleanupVirtualKeyMediaAsync(id, ct, context);
                ct.ThrowIfCancellationRequested();
                context.VirtualKeys.Remove(key);
            }, key => key, cancellationToken);
        if (deleted is null) return false;
        await NotifyVirtualKeyDeletedAsync(deleted);
        return true;
    }
}
