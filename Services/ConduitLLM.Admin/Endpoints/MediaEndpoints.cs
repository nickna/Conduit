using ConduitLLM.Core.Extensions;
using System.Diagnostics.CodeAnalysis;
using ConduitLLM.Admin.Auditing;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Admin.Endpoints;

/// <summary>Administrative endpoints for media lifecycle management.</summary>
public static class MediaEndpoints
{
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        var media = app.MapGroup("/v1/admin/media-assets")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Media");

        media.MapGet("/stats", GetOverallStats).WithName("Media_GetOverallStats")
            .Produces<OverallMediaStorageStats>();
        media.MapGet("/stats/virtual-key/{virtualKeyId}", GetStatsByVirtualKey).WithName("Media_GetStatsByVirtualKey")
            .Produces<MediaStorageStats>();
        media.MapGet("/stats/by-provider", GetStatsByProvider).WithName("Media_GetStatsByProvider")
            .Produces<Dictionary<string, long>>();
        media.MapGet("/stats/by-type", GetStatsByMediaType).WithName("Media_GetStatsByMediaType")
            .Produces<Dictionary<string, long>>();
        media.MapGet("/virtual-key/{virtualKeyId}", GetMediaByVirtualKey).WithName("Media_GetByVirtualKey")
            .Produces<List<MediaRecordResponse>>();
        media.MapGet("/search", SearchMedia).WithName("Media_Search")
            .Produces<List<MediaRecordResponse>>()
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json");
        media.MapDelete("/{mediaId}", DeleteMedia).WithName("Media_Delete")
            .Produces<MediaDeletionResponseDto>()
            .Produces(StatusCodes.Status404NotFound);
        media.MapPost("/restore/{mediaId}", RestoreMedia).WithName("Media_Restore")
            .WithSummary("Restore soft-deleted media within its recovery window")
            .Produces<MediaRestoreResponseDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces<AdminProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json");
        media.MapPost("/cleanup/expired", CleanupExpiredMedia).WithName("Media_CleanupExpired")
            .Produces<MediaCleanupResponseDto>()
            .Produces<AdminProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");
        // Keep the legacy route for compatibility; it now performs storage-side reconciliation.
        media.MapPost("/cleanup/orphaned", ReconcileStorage).WithName("Media_ReconcileStorage")
            .WithSummary("Reconcile storage objects against MediaRecord tracking rows")
            .Produces<MediaCleanupResponseDto>()
            .Produces<AdminProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");
        media.MapPost("/cleanup/prune/preview", PreviewPruneMedia).WithName("Media_PreviewPrune")
            .WithSummary("Preview the files and bytes matched by a prune operation")
            .Produces<MediaCleanupPreviewDto>()
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json");
        media.MapPost("/cleanup/prune", PruneOldMedia).WithName("Media_Prune")
            .Produces<MediaCleanupResponseDto>()
            .Produces<AdminProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json");

        var cleanup = app.MapGroup("/v1/admin/media-cleanup-jobs")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Media Cleanup");

        cleanup.MapGet("/status", GetCleanupStatus).WithName("MediaCleanup_GetStatus")
            .Produces<MediaCleanupStatusDto>();
        cleanup.MapGet("/enabled", GetCleanupEnabled).WithName("MediaCleanup_GetEnabled")
            .Produces<MediaCleanupEnabledDto>();
        cleanup.MapPost("/enabled", SetCleanupEnabled).WithName("MediaCleanup_SetEnabled")
            .Produces<MediaCleanupEnabledChangedDto>();
        cleanup.MapGet("/simple-retention", GetSimpleRetention).WithName("MediaCleanup_GetSimpleRetention")
            .Produces<SimpleRetentionResponse>();
        cleanup.MapPost("/simple-retention", SetSimpleRetention).WithName("MediaCleanup_SetSimpleRetention")
            .Produces<SimpleRetentionResponse>();
        cleanup.MapGet("/approvals", GetPendingApprovals)
            .WithName("MediaCleanup_GetPendingApprovals")
            .WithSummary("List large scheduled cleanup scopes awaiting approval")
            .Produces<List<MediaCleanupApprovalDto>>()
            .Produces<AdminProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json");
        cleanup.MapPost("/approvals/{approvalId:guid}/approve", ApproveCleanup)
            .WithName("MediaCleanup_Approve")
            .WithSummary("Approve a fresh execution of a large scheduled cleanup scope")
            .Produces<MediaCleanupApprovalActionDto>()
            .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json");
        cleanup.MapPost("/approvals/{approvalId:guid}/reject", RejectCleanup)
            .WithName("MediaCleanup_Reject")
            .WithSummary("Reject a large scheduled cleanup scope")
            .Produces<MediaCleanupApprovalActionDto>()
            .Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json");
        return app;
    }

    private static async Task<IResult> GetOverallStats(
        [FromServices] IAdminMediaService mediaService,
        [FromQuery] int? virtualKeyGroupId = null) =>
        Results.Ok(await mediaService.GetOverallStorageStatsAsync(virtualKeyGroupId));

    private static async Task<IResult> GetStatsByVirtualKey(
        int virtualKeyId,
        [FromServices] IAdminMediaService mediaService) =>
        Results.Ok(await mediaService.GetStorageStatsByVirtualKeyAsync(virtualKeyId));

    private static async Task<IResult> GetStatsByProvider([FromServices] IAdminMediaService mediaService) =>
        Results.Ok(await mediaService.GetStorageStatsByProviderAsync());

    private static async Task<IResult> GetStatsByMediaType([FromServices] IAdminMediaService mediaService) =>
        Results.Ok(await mediaService.GetStorageStatsByMediaTypeAsync());

    private static async Task<IResult> GetMediaByVirtualKey(
        int virtualKeyId,
        [FromServices] IAdminMediaService mediaService,
        [FromQuery] bool includeDeleted = false) =>
        Results.Ok((await mediaService.GetMediaByVirtualKeyAsync(
            virtualKeyId,
            includeDeleted)).Select(ToResponse).ToList());

    private static async Task<IResult> SearchMedia(
        [FromServices] IAdminMediaService mediaService,
        [FromQuery] string? pattern = null)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return AdminResults.BadRequest("Search pattern is required");
        return Results.Ok((await mediaService.SearchMediaByStorageKeyAsync(pattern)).Select(ToResponse).ToList());
    }

    private static async Task<IResult> DeleteMedia(
        Guid mediaId,
        HttpContext context,
        [FromServices] IAdminMediaService mediaService,
        [FromServices] ILogger<MediaEndpointLog> logger)
    {
        var result = await mediaService.DeleteMediaAsync(mediaId, context.RequestAborted);
        if (result == null)
            throw new KeyNotFoundException();
        AdminAudit.Log(
            context,
            logger,
            result.IsSoftDeleted ? "SoftDeleted" : "Deleted",
            "Media",
            mediaId);
        return Results.Ok(new MediaDeletionResponseDto
        {
            Message = result.IsSoftDeleted
                ? "Media moved to deleted items and can be restored during its recovery window"
                : "Media permanently deleted",
            IsSoftDeleted = result.IsSoftDeleted,
            DeletedAt = result.DeletedAt
        });
    }

    private static async Task<IResult> RestoreMedia(
        Guid mediaId,
        HttpContext context,
        [FromServices] IAdminMediaService mediaService,
        [FromServices] ILogger<MediaEndpointLog> logger)
    {
        var outcome = await mediaService.RestoreMediaAsync(mediaId, context.RequestAborted);
        if (outcome == MediaRestoreOutcome.NotFound)
        {
            throw new KeyNotFoundException();
        }

        if (outcome == MediaRestoreOutcome.NotDeleted)
        {
            return AdminResults.Conflict(
                "Media is active and does not need to be restored.",
                "media_not_deleted");
        }

        if (outcome == MediaRestoreOutcome.GracePeriodElapsed)
        {
            return AdminResults.Conflict(
                "The media recovery window has elapsed and the record is awaiting purge.",
                "media_restore_window_elapsed");
        }

        if (outcome == MediaRestoreOutcome.CleanupInProgress)
        {
            return AdminResults.Conflict(
                "Media cleanup is running; retry the restore after it completes.",
                "media_cleanup_in_progress");
        }

        AdminAudit.Log(context, logger, "Restored", "Media", mediaId);
        return Results.Ok(new MediaRestoreResponseDto
        {
            MediaId = mediaId,
            Message = "Media restored successfully"
        });
    }

    private static async Task<IResult> CleanupExpiredMedia(
        HttpContext context,
        [FromServices] IConfigurationDbContext configurationContext,
        [FromServices] IMediaDeletionEngine deletionEngine,
        [FromServices] IDistributedLockProvider lockService,
        [FromServices] IMediaCleanupStatusService statusService,
        [FromServices] ILogger<MediaEndpointLog> logger,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteManualCleanupAsync(
            MediaCleanupTypes.Expiration,
            "expired media",
            force,
            context,
            deletionEngine,
            lockService,
            statusService,
            logger,
            (operation, protectedToken) => DeleteManualCandidatesAsync(
                deletionEngine,
                operation,
                () => configurationContext.MediaRecords
                    .AsNoTracking()
                    .Where(media => media.ExpiresAt != null && media.ExpiresAt <= DateTime.UtcNow)
                    .ToListAsync(protectedToken),
                protectedToken),
            cancellationToken);
    }

    private static async Task<IResult> ReconcileStorage(
        HttpContext context,
        [FromServices] IMediaReconciliationService reconciliationService,
        [FromServices] IMediaDeletionEngine deletionEngine,
        [FromServices] IDistributedLockProvider lockService,
        [FromServices] IMediaCleanupStatusService statusService,
        [FromServices] ILogger<MediaEndpointLog> logger,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteManualCleanupAsync(
            MediaCleanupTypes.Reconciliation,
            "untracked storage objects",
            force,
            context,
            deletionEngine,
            lockService,
            statusService,
            logger,
            (operation, protectedToken) => reconciliationService.ReconcileAsync(
                operation,
                protectedToken),
            cancellationToken);
    }

    private static async Task<IResult> PreviewPruneMedia(
        PruneMediaRequest request,
        [FromServices] IConfigurationDbContext configurationContext,
        [FromServices] IMediaDeletionEngine deletionEngine,
        CancellationToken cancellationToken = default)
    {
        if (request.DaysToKeep is null or <= 0)
            return AdminResults.BadRequest("DaysToKeep must be a positive number");

        var candidates = await QueryPruneCandidatesAsync(
            configurationContext,
            request.DaysToKeep.Value,
            cancellationToken);
        var preview = deletionEngine.Preview(candidates);
        return Results.Ok(new MediaCleanupPreviewDto
        {
            FileCount = preview.FileCount,
            SizeBytes = preview.SizeBytes,
            ConfirmationPhrase = $"DELETE {preview.FileCount}"
        });
    }

    private static async Task<IResult> PruneOldMedia(
        PruneMediaRequest request,
        HttpContext context,
        [FromServices] IConfigurationDbContext configurationContext,
        [FromServices] IMediaDeletionEngine deletionEngine,
        [FromServices] IDistributedLockProvider lockService,
        [FromServices] IMediaCleanupStatusService statusService,
        [FromServices] ILogger<MediaEndpointLog> logger,
        CancellationToken cancellationToken = default)
    {
        if (request.DaysToKeep is null or <= 0)
            return AdminResults.BadRequest("DaysToKeep must be a positive number");

        return await ExecuteManualCleanupAsync(
            MediaCleanupTypes.Retention,
            $"media files older than {request.DaysToKeep} days",
            request.Force,
            context,
            deletionEngine,
            lockService,
            statusService,
            logger,
            (operation, protectedToken) => DeleteManualCandidatesAsync(
                deletionEngine,
                operation,
                () => QueryPruneCandidatesAsync(
                    configurationContext,
                    request.DaysToKeep.Value,
                    protectedToken),
                protectedToken),
            cancellationToken);
    }

    private static async Task<IResult> GetCleanupStatus([FromServices] IMediaCleanupStatusService service) =>
        Results.Ok(await service.GetStatusAsync());

    private static async Task<IResult> GetCleanupEnabled([FromServices] IMediaCleanupStatusService service) =>
        Results.Ok(new MediaCleanupEnabledDto { Enabled = await service.IsEnabledAsync() });

    private static async Task<IResult> SetCleanupEnabled(
        UpdateMediaCleanupEnabledRequest request,
        HttpContext context,
        [FromServices] IMediaCleanupStatusService service,
        [FromServices] ILogger<MediaEndpointLog> logger)
    {
        await service.SetEnabledAsync(request.Enabled);
        AdminAudit.Log(context, logger, "SetEnabled", "MediaCleanupService", detail: $"Enabled: {request.Enabled}");
        return Results.Ok(new MediaCleanupEnabledChangedDto
        {
            Enabled = request.Enabled,
            Message = request.Enabled ? "Media cleanup service has been enabled" : "Media cleanup service has been disabled"
        });
    }

    private static async Task<IResult> GetSimpleRetention([FromServices] IMediaCleanupStatusService service)
    {
        var days = await service.GetSimpleRetentionOverrideAsync();
        return Results.Ok(new SimpleRetentionResponse { RetentionDays = days, IsOverrideActive = days.HasValue });
    }

    private static async Task<IResult> SetSimpleRetention(
        UpdateSimpleRetentionRequest request,
        HttpContext context,
        [FromServices] IMediaCleanupStatusService service,
        [FromServices] ILogger<MediaEndpointLog> logger)
    {
        await service.SetSimpleRetentionOverrideAsync(request.RetentionDays);
        var message = request.RetentionDays.HasValue
            ? $"Simple retention override set to {request.RetentionDays} days - all media will be deleted after this period"
            : "Simple retention override cleared - using policy-based retention";
        AdminAudit.Log(context, logger, "SetSimpleRetention", "MediaCleanupService",
            detail: $"RetentionDays: {request.RetentionDays?.ToString() ?? "cleared"}");
        return Results.Ok(new SimpleRetentionResponse
        {
            RetentionDays = request.RetentionDays,
            IsOverrideActive = request.RetentionDays.HasValue,
            Message = message
        });
    }

    private static async Task<IResult> GetPendingApprovals(
        [FromServices] IMediaCleanupApprovalService approvalService,
        CancellationToken cancellationToken) =>
        Results.Ok(await approvalService.ListPendingAsync(cancellationToken));

    private static async Task<IResult> ApproveCleanup(
        Guid approvalId,
        HttpContext context,
        [FromServices] IMediaCleanupApprovalService approvalService,
        [FromServices] MediaCleanupService cleanupService,
        [FromServices] ILogger<MediaEndpointLog> logger,
        CancellationToken cancellationToken)
    {
        var approval = await approvalService.ApproveAsync(
            approvalId,
            GetAdminActor(context),
            cancellationToken);
        if (approval == null)
        {
            throw new KeyNotFoundException();
        }

        AdminAudit.Log(
            context,
            logger,
            "Approved",
            "MediaCleanupApproval",
            approvalId,
            $"Type: {approval.CleanupType}, GroupId: {approval.VirtualKeyGroupId?.ToString() ?? "all"}, " +
            $"SnapshotCount: {approval.CandidateCount}, SnapshotBytes: {approval.CandidateBytes}");

        await cleanupService.RunScheduledCleanupAsync(cancellationToken);
        return Results.Ok(new MediaCleanupApprovalActionDto
        {
            Approval = MediaCleanupApprovalDto.FromEntity(approval),
            Message = "Approval recorded and a fresh cleanup evaluation was triggered"
        });
    }

    private static async Task<IResult> RejectCleanup(
        Guid approvalId,
        HttpContext context,
        [FromServices] IMediaCleanupApprovalService approvalService,
        [FromServices] ILogger<MediaEndpointLog> logger,
        CancellationToken cancellationToken)
    {
        var approval = await approvalService.RejectAsync(
            approvalId,
            GetAdminActor(context),
            cancellationToken);
        if (approval == null)
        {
            throw new KeyNotFoundException();
        }

        AdminAudit.Log(
            context,
            logger,
            "Rejected",
            "MediaCleanupApproval",
            approvalId,
            $"Type: {approval.CleanupType}, GroupId: {approval.VirtualKeyGroupId?.ToString() ?? "all"}");
        return Results.Ok(new MediaCleanupApprovalActionDto
        {
            Approval = MediaCleanupApprovalDto.FromEntity(approval),
            Message = "Cleanup approval rejected; the next scheduler evaluation may raise a new request"
        });
    }

    private static string GetAdminActor(HttpContext context) =>
        context.User.Identity?.Name ?? $"master-key:{context.TraceIdentifier}";

    // RDG emits service-resolution code outside this containing type. Keep the
    // category non-public while making it accessible to assembly-generated code.
    internal sealed class MediaEndpointLog;

    private static async Task<IResult> ExecuteManualCleanupAsync(
        string cleanupType,
        string description,
        bool force,
        HttpContext context,
        IMediaDeletionEngine deletionEngine,
        IDistributedLockProvider lockService,
        IMediaCleanupStatusService statusService,
        ILogger<MediaEndpointLog> logger,
        Func<MediaDeletionOperationContext, CancellationToken, Task<MediaDeletionEngineResult>> executeCleanup,
        CancellationToken cancellationToken)
    {
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            context.RequestServices.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None);
        cancellationToken = requestCancellation.Token;
        await using var lockHandle = await lockService.TryAcquireAsync(
            MediaCleanupLock.Key,
            TimeSpan.Zero,
            cancellationToken);
        if (lockHandle == null)
        {
            return AdminResults.Conflict(
                "Media cleanup is already running; retry after the current run completes.",
                "media_cleanup_in_progress");
        }

        var instanceId = $"manual:{context.TraceIdentifier}";
        using var operationCancellation = lockHandle.CreateOperationCancellation(
            cancellationToken, MediaCleanupLock.OperationDeadline);
        var protectedToken = operationCancellation.Token;
        var operation = new MediaDeletionOperationContext(
            cleanupType,
            "manual",
            instanceId,
            force);
        var result = await deletionEngine.ExecuteOperationAsync(
            operation,
            () => executeCleanup(operation, protectedToken),
            protectedToken);
        protectedToken.ThrowIfCancellationRequested();

        await statusService.RecordRunCompletionAsync(
            result.FilesDeleted,
            result.BytesFreed,
            result.DurationSeconds,
            result.OperationStatus ?? "Completed",
            instanceId,
            "manual",
            protectedToken);
        AdminAudit.Log(
            context,
            logger,
            force ? "ForceCleanup" : "Cleanup",
            "Media",
            detail:
                $"Type: {cleanupType}, Force: {force}, DeletedCount: {result.FilesDeleted}, " +
                $"FailedCount: {result.Failures}, WouldDeleteCount: {result.WouldDeleteCount}");

        return Results.Ok(ToCleanupResponse(description, result));
    }

    private static async Task<MediaDeletionEngineResult> DeleteManualCandidatesAsync(
        IMediaDeletionEngine deletionEngine,
        MediaDeletionOperationContext operation,
        Func<Task<List<MediaRecord>>> getCandidates,
        CancellationToken cancellationToken)
    {
        var candidates = await getCandidates();
        return await deletionEngine.DeleteAsync(
            new MediaDeletionRequest(candidates, operation),
            cancellationToken);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Admin EF queries are outside the supported NativeAOT data-plane contract (ADR 0006). Owner: database/runtime. Upstream: dotnet/efcore#29754. Remove when this query uses a fixed-shape native store or EF expression construction is trim-safe.")]
    internal static async Task<List<MediaRecord>> QueryPruneCandidatesAsync(
        IConfigurationDbContext context,
        int daysToKeep,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.AddDays(-daysToKeep);
        var now = DateTime.UtcNow;
        var baseQuery = context.MediaRecords
            .AsNoTracking()
            .Where(media => media.CreatedAt < cutoff);
        var policySettings = await context.MediaRetentionPolicies
            .AsNoTracking()
            .Select(policy => new
            {
                policy.Id,
                policy.IsDefault,
                policy.IsActive,
                policy.RespectRecentAccess,
                policy.RecentAccessWindowDays
            })
            .ToListAsync(cancellationToken);
        var defaultPolicy = policySettings
            .FirstOrDefault(policy => policy.IsDefault && policy.IsActive);

        var unassignedQuery = baseQuery.Where(media => context.VirtualKeys.Any(key =>
            key.Id == media.VirtualKeyId &&
            key.VirtualKeyGroup.MediaRetentionPolicyId == null));
        if (defaultPolicy?.RespectRecentAccess == true)
        {
            var recentAccessCutoff = now.AddDays(
                -Math.Max(0, defaultPolicy.RecentAccessWindowDays));
            unassignedQuery = unassignedQuery.Where(media =>
                media.LastAccessedAt == null ||
                media.LastAccessedAt < recentAccessCutoff);
        }

        IQueryable<MediaRecord> candidates = unassignedQuery;
        foreach (var policy in policySettings)
        {
            var policyId = policy.Id;
            var assignedQuery = baseQuery.Where(media => context.VirtualKeys.Any(key =>
                key.Id == media.VirtualKeyId &&
                key.VirtualKeyGroup.MediaRetentionPolicyId == policyId));
            if (policy.RespectRecentAccess)
            {
                var recentAccessCutoff = now.AddDays(
                    -Math.Max(0, policy.RecentAccessWindowDays));
                assignedQuery = assignedQuery.Where(media =>
                    media.LastAccessedAt == null ||
                    media.LastAccessedAt < recentAccessCutoff);
            }

            candidates = candidates.Concat(assignedQuery);
        }

        return await candidates.ToListAsync(cancellationToken);
    }

    private static MediaCleanupResponseDto ToCleanupResponse(
        string description,
        MediaDeletionEngineResult result) => new()
    {
        Message = result.IsDryRun
            ? $"Dry run matched {result.WouldDeleteCount} permanent deletions and {result.WouldTombstoneCount} tombstones for {description}"
            : result.Failures == 0
                ? $"Permanently deleted {result.FilesDeleted} and tombstoned {result.RecordsTombstoned} {description}"
                : $"Permanently deleted {result.FilesDeleted} and tombstoned {result.RecordsTombstoned} {description}; {result.Failures} failed and remain tracked for retry",
        DeletedCount = result.FilesDeleted,
        TombstonedCount = result.RecordsTombstoned,
        FailedCount = result.Failures,
        IsDryRun = result.IsDryRun,
        WouldDeleteCount = result.WouldDeleteCount,
        WouldTombstoneCount = result.WouldTombstoneCount,
        BytesWouldFree = result.BytesWouldFree,
        TriggeredBy = "manual"
    };

    private static MediaRecordResponse ToResponse(MediaRecord media) => new(
        media.Id,
        media.StorageKey,
        media.VirtualKeyId,
        media.MediaType,
        media.ContentType,
        media.SizeBytes,
        media.ContentHash,
        media.Provider,
        media.Model,
        media.Prompt,
        media.StorageUrl,
        media.PublicUrl,
        media.ExpiresAt,
        media.CreatedAt,
        media.LastAccessedAt,
        media.AccessCount,
        media.DeletedAt);
}
