using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Auditing;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Core.Extensions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Entities;

namespace ConduitLLM.Admin.Endpoints
{
    /// <summary>
    /// Admin controller for managing media retention policies.
    /// </summary>
    public class MediaRetentionEndpoints
    {
        private readonly IConfigurationDbContext _context;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<MediaRetentionEndpoints> _logger;

        /// <summary>
        /// Initializes the Media Retention endpoint handler.
        /// </summary>
        /// <param name="context">The database context for configuration operations.</param>
        /// <param name="httpContextAccessor">Accessor for the current request context.</param>
        /// <param name="logger">The logger instance for diagnostic logging.</param>
        public MediaRetentionEndpoints(
            IConfigurationDbContext context,
            IHttpContextAccessor httpContextAccessor,
            ILogger<MediaRetentionEndpoints> logger)
        {
            _context = context;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        public static IEndpointRouteBuilder MapMediaRetentionEndpoints(IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/v1/admin/media-retention-policies")
                .RequireAuthorization("MasterKeyPolicy")
                .AddEndpointFilter<OperationLoggingEndpointFilter>()
                .WithTags("Media Retention");
            group.MapGet("", ([FromServices] MediaRetentionEndpoints e) => e.GetPolicies()).WithName("MediaRetention_GetPolicies").Produces<List<MediaRetentionPolicyDto>>();
            group.MapGet("/{id}", ([FromServices] MediaRetentionEndpoints e, int id) => e.GetPolicy(id)).WithName("MediaRetention_GetPolicy").Produces<MediaRetentionPolicyDetailDto>().Produces(StatusCodes.Status404NotFound);
            group.MapPost("", ([FromServices] MediaRetentionEndpoints e, CreateMediaRetentionPolicyRequest request) => e.CreatePolicy(request)).WithName("MediaRetention_CreatePolicy").Produces<MediaRetentionPolicyDto>(StatusCodes.Status201Created).Produces(StatusCodes.Status400BadRequest);
            group.MapPatch("/{id}", ([FromServices] MediaRetentionEndpoints e, int id, JsonMergePatch<UpdateMediaRetentionPolicyRequest> patch) => e.UpdatePolicy(id, patch.Value)).AcceptsJsonMergePatch<UpdateMediaRetentionPolicyRequest>().WithName("MediaRetention_UpdatePolicy").Produces<MediaRetentionPolicyDto>().Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status400BadRequest);
            group.MapDelete("/{id}", ([FromServices] MediaRetentionEndpoints e, int id) => e.DeletePolicy(id)).WithName("MediaRetention_DeletePolicy").Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status400BadRequest);
            group.MapPost("/{policyId}/group-assignments/{groupId}", ([FromServices] MediaRetentionEndpoints e, int groupId, int policyId) => e.AssignPolicyToGroup(groupId, policyId)).WithName("MediaRetention_AssignPolicyToGroup").Produces<MessageResponse>().Produces(StatusCodes.Status404NotFound);
            group.MapPost("/{id}/set-default", ([FromServices] MediaRetentionEndpoints e, int id) => e.SetDefaultPolicy(id)).WithName("MediaRetention_SetDefaultPolicy").Produces<MessageResponse>().Produces(StatusCodes.Status404NotFound);
            group.MapPost("/{groupId}/cleanup-jobs", ([FromServices] MediaRetentionEndpoints e, int groupId, bool dryRun = true) => e.TriggerCleanup(groupId, dryRun)).WithName("MediaRetention_TriggerCleanup").Produces<AdminProblemDetails>(StatusCodes.Status501NotImplemented, "application/problem+json");
            return app;
        }

        /// <summary>
        /// Get all media retention policies.
        /// </summary>
        /// <returns>List of all retention policies</returns>
        public async Task<IResult> GetPolicies()
        {
            var policies = await _context.MediaRetentionPolicies
                .Include(p => p.VirtualKeyGroups)
                .OrderBy(p => p.Name)
                .Select(p => new MediaRetentionPolicyDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    Description = p.Description,
                    PositiveBalanceRetentionDays = p.PositiveBalanceRetentionDays,
                    ZeroBalanceRetentionDays = p.ZeroBalanceRetentionDays,
                    NegativeBalanceRetentionDays = p.NegativeBalanceRetentionDays,
                    SoftDeleteGracePeriodDays = p.SoftDeleteGracePeriodDays,
                    RespectRecentAccess = p.RespectRecentAccess,
                    RecentAccessWindowDays = p.RecentAccessWindowDays,
                    IsDefault = p.IsDefault,
                    MaxStorageSizeBytes = p.MaxStorageSizeBytes,
                    MaxFileCount = p.MaxFileCount,
                    QuotaExceededBehavior = p.QuotaExceededBehavior,
                    IsActive = p.IsActive,
                    CreatedAt = p.CreatedAt,
                    UpdatedAt = p.UpdatedAt,
                    VirtualKeyGroupCount = p.VirtualKeyGroups.Count
                })
                .ToListAsync();

            return Results.Ok(policies);
        }

        /// <summary>
        /// Get a specific media retention policy by ID.
        /// </summary>
        /// <param name="id">Policy ID</param>
        /// <returns>The requested retention policy</returns>
        public async Task<IResult> GetPolicy(int id)
        {
            var policy = await _context.MediaRetentionPolicies
                .Include(p => p.VirtualKeyGroups)
                    .ThenInclude(vkg => vkg.VirtualKeys)
                .FirstOrDefaultAsync(p => p.Id == id);

            if (policy == null)
            {
                return AdminResults.NotFoundEntity("Retention policy", id);
            }

            return Results.Ok(new MediaRetentionPolicyDetailDto
            {
                Id = policy.Id,
                Name = policy.Name,
                Description = policy.Description,
                PositiveBalanceRetentionDays = policy.PositiveBalanceRetentionDays,
                ZeroBalanceRetentionDays = policy.ZeroBalanceRetentionDays,
                NegativeBalanceRetentionDays = policy.NegativeBalanceRetentionDays,
                SoftDeleteGracePeriodDays = policy.SoftDeleteGracePeriodDays,
                RespectRecentAccess = policy.RespectRecentAccess,
                RecentAccessWindowDays = policy.RecentAccessWindowDays,
                IsDefault = policy.IsDefault,
                MaxStorageSizeBytes = policy.MaxStorageSizeBytes,
                MaxFileCount = policy.MaxFileCount,
                QuotaExceededBehavior = policy.QuotaExceededBehavior,
                IsActive = policy.IsActive,
                CreatedAt = policy.CreatedAt,
                UpdatedAt = policy.UpdatedAt,
                VirtualKeyGroups = policy.VirtualKeyGroups.Select(vkg => new VirtualKeyGroupSummaryDto
                {
                    Id = vkg.Id,
                    Balance = vkg.Balance,
                    VirtualKeyCount = vkg.VirtualKeys.Count
                }).ToList()
            });
        }

        /// <summary>
        /// Create a new media retention policy.
        /// </summary>
        /// <param name="request">Policy creation request</param>
        /// <returns>The created retention policy</returns>
        public async Task<IResult> CreatePolicy(CreateMediaRetentionPolicyRequest request)
        {
            if (request.PositiveBalanceRetentionDays <= 0)
            {
                return AdminResults.BadRequest("Positive balance retention days must be greater than 0");
            }

            if (request.IsDefault)
            {
                // Ensure only one default policy exists
                var existingDefault = await _context.MediaRetentionPolicies
                    .FirstOrDefaultAsync(p => p.IsDefault);
                if (existingDefault != null)
                {
                    existingDefault.IsDefault = false;
                }
            }

            var policy = new MediaRetentionPolicy
            {
                Name = request.Name,
                Description = request.Description,
                PositiveBalanceRetentionDays = request.PositiveBalanceRetentionDays,
                ZeroBalanceRetentionDays = request.ZeroBalanceRetentionDays,
                NegativeBalanceRetentionDays = request.NegativeBalanceRetentionDays,
                SoftDeleteGracePeriodDays = request.SoftDeleteGracePeriodDays,
                RespectRecentAccess = request.RespectRecentAccess,
                RecentAccessWindowDays = request.RecentAccessWindowDays,
                IsDefault = request.IsDefault,
                MaxStorageSizeBytes = request.MaxStorageSizeBytes,
                MaxFileCount = request.MaxFileCount,
                QuotaExceededBehavior = request.QuotaExceededBehavior,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.MediaRetentionPolicies.Add(policy);
            await _context.SaveChangesAsync();

            LogAdminAudit("Created", "MediaRetentionPolicy", policy.Id, $"Name: {LoggingSanitizer.S(policy.Name)}");

            var dto = new MediaRetentionPolicyDto
            {
                Id = policy.Id,
                Name = policy.Name,
                Description = policy.Description,
                PositiveBalanceRetentionDays = policy.PositiveBalanceRetentionDays,
                ZeroBalanceRetentionDays = policy.ZeroBalanceRetentionDays,
                NegativeBalanceRetentionDays = policy.NegativeBalanceRetentionDays,
                SoftDeleteGracePeriodDays = policy.SoftDeleteGracePeriodDays,
                RespectRecentAccess = policy.RespectRecentAccess,
                RecentAccessWindowDays = policy.RecentAccessWindowDays,
                IsDefault = policy.IsDefault,
                MaxStorageSizeBytes = policy.MaxStorageSizeBytes,
                MaxFileCount = policy.MaxFileCount,
                QuotaExceededBehavior = policy.QuotaExceededBehavior,
                IsActive = policy.IsActive,
                CreatedAt = policy.CreatedAt,
                UpdatedAt = policy.UpdatedAt,
                VirtualKeyGroupCount = 0
            };

            return Results.Created($"/v1/admin/media-retention-policies/{dto.Id}", dto);
        }

        /// <summary>
        /// Update an existing media retention policy.
        /// </summary>
        /// <param name="id">Policy ID</param>
        /// <param name="request">Policy update request</param>
        /// <returns>The updated retention policy</returns>
        public async Task<IResult> UpdatePolicy(int id, UpdateMediaRetentionPolicyRequest request)
        {
            var policy = await _context.MediaRetentionPolicies
                .Include(p => p.VirtualKeyGroups)
                .FirstOrDefaultAsync(p => p.Id == id);

            if (policy == null)
            {
                return AdminResults.NotFoundEntity("Retention policy", id);
            }

            var isDefaultDefined = request.TryGetPatchedProperty(
                nameof(request.IsDefault), policy.IsDefault, out bool isDefault);
            if (isDefaultDefined && isDefault && !policy.IsDefault)
            {
                // Ensure only one default policy exists
                var existingDefault = await _context.MediaRetentionPolicies
                    .FirstOrDefaultAsync(p => p.IsDefault && p.Id != id);
                if (existingDefault != null)
                {
                    existingDefault.IsDefault = false;
                }
            }

            // Update fields
            if (request.TryGetPatchedProperty(nameof(request.Name), policy.Name, out string? name))
                policy.Name = name ?? throw new InvalidOperationException("name cannot be null.");
            if (request.TryGetPatchedProperty(nameof(request.Description), policy.Description, out string? description))
                policy.Description = description;
            if (request.TryGetPatchedProperty(nameof(request.PositiveBalanceRetentionDays), policy.PositiveBalanceRetentionDays, out int positiveDays))
                policy.PositiveBalanceRetentionDays = positiveDays;
            if (request.TryGetPatchedProperty(nameof(request.ZeroBalanceRetentionDays), policy.ZeroBalanceRetentionDays, out int zeroDays))
                policy.ZeroBalanceRetentionDays = zeroDays;
            if (request.TryGetPatchedProperty(nameof(request.NegativeBalanceRetentionDays), policy.NegativeBalanceRetentionDays, out int negativeDays))
                policy.NegativeBalanceRetentionDays = negativeDays;
            if (request.TryGetPatchedProperty(nameof(request.SoftDeleteGracePeriodDays), policy.SoftDeleteGracePeriodDays, out int graceDays))
                policy.SoftDeleteGracePeriodDays = graceDays;
            if (request.TryGetPatchedProperty(nameof(request.RespectRecentAccess), policy.RespectRecentAccess, out bool respectRecentAccess))
                policy.RespectRecentAccess = respectRecentAccess;
            if (request.TryGetPatchedProperty(nameof(request.RecentAccessWindowDays), policy.RecentAccessWindowDays, out int recentAccessDays))
                policy.RecentAccessWindowDays = recentAccessDays;
            if (isDefaultDefined)
                policy.IsDefault = isDefault;
            if (request.TryGetPatchedProperty(
                    nameof(request.MaxStorageSizeBytes),
                    policy.MaxStorageSizeBytes,
                    out long? maxStorageSizeBytes))
                policy.MaxStorageSizeBytes = maxStorageSizeBytes;
            else if (request.HasMaxStorageSizeBytes)
                policy.MaxStorageSizeBytes = request.MaxStorageSizeBytes;
            if (request.TryGetPatchedProperty(
                    nameof(request.MaxFileCount),
                    policy.MaxFileCount,
                    out int? maxFileCount))
                policy.MaxFileCount = maxFileCount;
            else if (request.HasMaxFileCount)
                policy.MaxFileCount = request.MaxFileCount;
            if (request.TryGetPatchedProperty(nameof(request.QuotaExceededBehavior), policy.QuotaExceededBehavior, out MediaQuotaExceededBehavior quotaBehavior))
                policy.QuotaExceededBehavior = quotaBehavior;
            if (request.TryGetPatchedProperty(nameof(request.IsActive), policy.IsActive, out bool isActive))
                policy.IsActive = isActive;
            policy.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            LogAdminAudit("Updated", "MediaRetentionPolicy", policy.Id, $"Name: {LoggingSanitizer.S(policy.Name)}");

            return Results.Ok(new MediaRetentionPolicyDto
            {
                Id = policy.Id,
                Name = policy.Name,
                Description = policy.Description,
                PositiveBalanceRetentionDays = policy.PositiveBalanceRetentionDays,
                ZeroBalanceRetentionDays = policy.ZeroBalanceRetentionDays,
                NegativeBalanceRetentionDays = policy.NegativeBalanceRetentionDays,
                SoftDeleteGracePeriodDays = policy.SoftDeleteGracePeriodDays,
                RespectRecentAccess = policy.RespectRecentAccess,
                RecentAccessWindowDays = policy.RecentAccessWindowDays,
                IsDefault = policy.IsDefault,
                MaxStorageSizeBytes = policy.MaxStorageSizeBytes,
                MaxFileCount = policy.MaxFileCount,
                QuotaExceededBehavior = policy.QuotaExceededBehavior,
                IsActive = policy.IsActive,
                CreatedAt = policy.CreatedAt,
                UpdatedAt = policy.UpdatedAt,
                VirtualKeyGroupCount = policy.VirtualKeyGroups.Count
            });
        }

        /// <summary>
        /// Delete a media retention policy.
        /// </summary>
        /// <param name="id">Policy ID</param>
        /// <returns>No content on success</returns>
        public async Task<IResult> DeletePolicy(int id)
        {
            var policy = await _context.MediaRetentionPolicies
                .Include(p => p.VirtualKeyGroups)
                .FirstOrDefaultAsync(p => p.Id == id);

            if (policy == null)
            {
                return AdminResults.NotFoundEntity("Retention policy", id);
            }

            if (policy.IsDefault)
            {
                return AdminResults.BadRequest("Cannot delete the default retention policy");
            }

            if (policy.VirtualKeyGroups.Any())
            {
                return AdminResults.BadRequest(
                    $"Cannot delete policy - it is assigned to {policy.VirtualKeyGroups.Count} virtual key group(s)");
            }

            _context.MediaRetentionPolicies.Remove(policy);
            await _context.SaveChangesAsync();

            LogAdminAudit("Deleted", "MediaRetentionPolicy", policy.Id, $"Name: {LoggingSanitizer.S(policy.Name)}");

            return Results.NoContent();
        }

        /// <summary>
        /// Assign a retention policy to a virtual key group.
        /// </summary>
        /// <param name="groupId">Virtual key group ID</param>
        /// <param name="policyId">Retention policy ID</param>
        /// <returns>Success result</returns>
        public async Task<IResult> AssignPolicyToGroup(int groupId, int policyId)
        {
            var group = await _context.VirtualKeyGroups.FindAsync(groupId);
            if (group == null)
            {
                return AdminResults.NotFoundEntity("Virtual key group", groupId);
            }

            var policy = await _context.MediaRetentionPolicies.FindAsync(policyId);
            if (policy == null)
            {
                return AdminResults.NotFoundEntity("Retention policy", policyId);
            }

            group.MediaRetentionPolicyId = policyId;
            await _context.SaveChangesAsync();

            LogAdminAudit("AssignedPolicy", "MediaRetentionPolicy", policyId, $"GroupId: {groupId}");

            return Results.Ok(new MessageResponse($"Successfully assigned policy '{policy.Name}' to group {groupId}"));
        }

        /// <summary>
        /// Sets a policy as the new default retention policy.
        /// Only one policy can be the default at a time.
        /// </summary>
        /// <param name="id">Policy ID to set as default</param>
        /// <returns>Success result</returns>
        public async Task<IResult> SetDefaultPolicy(int id)
        {
            var policy = await _context.MediaRetentionPolicies.FindAsync(id);

            if (policy == null)
            {
                return AdminResults.NotFoundEntity("Retention policy", id);
            }

            if (!policy.IsActive)
            {
                return AdminResults.BadRequest("Cannot set an inactive policy as default");
            }

            // Clear existing default
            var currentDefault = await _context.MediaRetentionPolicies
                .FirstOrDefaultAsync(p => p.IsDefault && p.Id != id);
            if (currentDefault != null)
            {
                currentDefault.IsDefault = false;
            }

            // Set new default
            policy.IsDefault = true;
            policy.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            LogAdminAudit("SetDefault", "MediaRetentionPolicy", policy.Id, $"Name: {LoggingSanitizer.S(policy.Name)}");

            return Results.Ok(new MessageResponse($"'{policy.Name}' is now the default retention policy"));
        }

        /// <summary>
        /// Trigger a manual media cleanup for a specific virtual key group.
        /// </summary>
        /// <param name="groupId">Virtual key group ID</param>
        /// <param name="dryRun">Whether to perform a dry run (default: true)</param>
        /// <returns>501 Not Implemented — manual cleanup is not available; use the scheduled cleanup system</returns>
        public IResult TriggerCleanup(int groupId, bool dryRun = true) =>
            AdminResults.Problem(
                StatusCodes.Status501NotImplemented,
                "Manual cleanup trigger is not implemented. Use the scheduled cleanup system.",
                "not_implemented");

        private void LogAdminAudit(string operation, string entityType, object? entityId = null, string? detail = null) =>
            AdminAudit.Log(_httpContextAccessor.HttpContext!, _logger, operation, entityType, entityId, detail);
    }

}
