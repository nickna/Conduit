using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Core.OpenApi;
using System.Diagnostics.CodeAnalysis;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtualKeyUtilities = ConduitLLM.Configuration.Utilities.VirtualKeyUtilities;
using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.DTOs.VirtualKey;
using ConduitLLM.Core.Models;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Filters;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;

namespace ConduitLLM.Admin.Endpoints
{
    /// <summary>
    /// Controller for managing virtual key groups
    /// </summary>
    public class VirtualKeyGroupsEndpoints : AdminEndpointHandlerBase
    {
        private readonly IVirtualKeyGroupRepository _groupRepository;
        private readonly IVirtualKeyRepository _keyRepository;
        private readonly IConfigurationDbContext _context;
        private readonly IRefundService _refundService;
        private readonly IEventBus? _eventBus;

        /// <summary>
        /// Initializes the Virtual Key Groups endpoint handler.
        /// </summary>
        public VirtualKeyGroupsEndpoints(
            IVirtualKeyGroupRepository groupRepository,
            IVirtualKeyRepository keyRepository,
            IConfigurationDbContext context,
            IRefundService refundService,
            IHttpContextAccessor httpContextAccessor,
            ILogger<VirtualKeyGroupsEndpoints> logger,
            IEventBus? eventBus = null)
            : base(null, httpContextAccessor, logger)
        {
            _groupRepository = groupRepository;
            _keyRepository = keyRepository;
            _context = context;
            _refundService = refundService;
            _eventBus = eventBus;
        }

        public static IEndpointRouteBuilder MapVirtualKeyGroupsEndpoints(IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/v1/admin/virtual-key-groups")
                .RequireAuthorization()
                .AddEndpointFilter<VersionedResourceEndpointFilter>()
                .AddEndpointFilter<OperationLoggingEndpointFilter>()
                .WithTags("Virtual Key Groups");

            group.MapGet("/", ([FromServices] VirtualKeyGroupsEndpoints endpoints, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) => endpoints.GetAllGroups(page, pageSize, cancellationToken))
                .WithName("VirtualKeyGroups_GetAll")
                .WithSummary("Get all virtual key groups with pagination")
                .WithParameterDescription("page", "Page number (1-based, default: 1)")
                .WithParameterDescription("pageSize", "Number of items per page (default: 50, max: 100)").Produces<PagedResult<VirtualKeyGroupDto>>();
            group.MapGet("/{id}", ([FromServices] VirtualKeyGroupsEndpoints endpoints, int id) => endpoints.GetGroup(id))
                .WithName("VirtualKeyGroups_GetById")
                .WithSummary("Get a specific virtual key group by ID")
                .WithParameterDescription("id", "Identifier of the Virtual Key Groups resource.").Produces<VirtualKeyGroupDto>().Produces(StatusCodes.Status404NotFound);
            group.MapPost("/", ([FromServices] VirtualKeyGroupsEndpoints endpoints, CreateVirtualKeyGroupRequestDto request) => endpoints.CreateGroup(request))
                .WithName("VirtualKeyGroups_Create")
                .WithSummary("Create a new virtual key group").Produces<VirtualKeyGroupDto>(StatusCodes.Status201Created);
            group.MapPatch("/{id}", ([FromServices] VirtualKeyGroupsEndpoints endpoints, int id, JsonMergePatch<UpdateVirtualKeyGroupRequestDto> patch, HttpContext context) => endpoints.UpdateGroup(id, patch.Value, VersionedResourceEndpointFilter.ReadPrecondition(context), context.RequestAborted))
                .AcceptsJsonMergePatch<UpdateVirtualKeyGroupRequestDto>()
                .WithName("VirtualKeyGroups_Update")
                .WithSummary("Update a virtual key group")
                .WithParameterDescription("id", "Identifier of the Virtual Key Groups resource.").Produces<VirtualKeyGroupDto>().Produces(StatusCodes.Status404NotFound);
            group.MapPost("/{id}/adjust-balance", ([FromServices] VirtualKeyGroupsEndpoints endpoints, int id, AdjustBalanceDto request, HttpContext context) => endpoints.AdjustBalance(id, request, VersionedResourceEndpointFilter.ReadPrecondition(context), context.RequestAborted))
                .WithName("VirtualKeyGroups_AdjustBalance")
                .WithSummary("Adjust the balance of a virtual key group")
                .WithParameterDescription("id", "Identifier of the Virtual Key Groups resource.").Produces<VirtualKeyGroupDto>().Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound);
            group.MapDelete("/{id}", ([FromServices] VirtualKeyGroupsEndpoints endpoints, int id, HttpContext context) => endpoints.DeleteGroup(id, VersionedResourceEndpointFilter.ReadPrecondition(context), context.RequestAborted))
                .WithName("VirtualKeyGroups_Delete")
                .WithSummary("Delete a virtual key group")
                .WithParameterDescription("id", "Identifier of the Virtual Key Groups resource.").Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound);
            group.MapGet("/{id}/transactions", ([FromServices] VirtualKeyGroupsEndpoints endpoints, int id, int page = 1, int pageSize = 50) => endpoints.GetTransactionHistory(id, page, pageSize))
                .WithName("VirtualKeyGroups_GetTransactions")
                .WithSummary("Get transaction history for a virtual key group")
                .WithParameterDescription("id", "Identifier of the Virtual Key Groups resource.")
                .WithParameterDescription("page", "Page number, starting at 1.")
                .WithParameterDescription("pageSize", "Number of records per page; values are normalized to the supported pagination range.").Produces<PagedResult<VirtualKeyGroupTransactionDto>>().Produces(StatusCodes.Status404NotFound);
            group.MapGet("/{id}/keys", ([FromServices] VirtualKeyGroupsEndpoints endpoints, int id) => endpoints.GetKeysInGroup(id))
                .WithName("VirtualKeyGroups_GetKeys")
                .WithSummary("Get virtual keys in a group")
                .WithParameterDescription("id", "Identifier of the Virtual Key Groups resource.").Produces<List<VirtualKeyDto>>().Produces(StatusCodes.Status404NotFound);
            group.MapPost("/{id}/refund", ([FromServices] VirtualKeyGroupsEndpoints endpoints, int id, ProcessRefundRequestDto request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, HttpContext context) => endpoints.ProcessRefund(id, request, idempotencyKey, VersionedResourceEndpointFilter.ReadPrecondition(context), context.RequestAborted))
                .WithName("VirtualKeyGroups_Refund")
                .WithParameterDescription("Idempotency-Key", "Client-supplied key for deduplicating repeated requests. Reuse the same key only for the same intended operation.")
                .WithSummary("Process a refund for a virtual key group")
                .WithParameterDescription("id", "The virtual key group ID").Produces<RefundResultDto>().Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status409Conflict);
            return app;
        }

        /// <summary>
        /// Get all virtual key groups with pagination
        /// </summary>
        /// <param name="page">Page number (1-based, default: 1)</param>
        /// <param name="pageSize">Number of items per page (default: 50, max: 100)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<IResult> GetAllGroups(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            CancellationToken cancellationToken = default)
        {
            (page, pageSize) = Pagination.Normalize(page, pageSize);

            var (groups, totalCount) = await _groupRepository.GetPaginatedAsync(page, pageSize, cancellationToken);

            var dtos = groups.Select(VirtualKeyGroupDto.FromEntity).ToList();

            return Ok(new PagedResult<VirtualKeyGroupDto>
            {
                Data = dtos,
                Pagination = PaginationMetadata.Create(page, pageSize, totalCount)
            });
        }

        /// <summary>
        /// Get a specific virtual key group by ID
        /// </summary>
        public async Task<IResult> GetGroup(int id)
        {
            var group = await _groupRepository.GetByIdWithKeysAsync(id);
            if (group == null)
            {
                return AdminResults.NotFoundEntity("VirtualKeyGroup", id);
            }
            return Ok(VirtualKeyGroupDto.FromEntity(group));
        }

        /// <summary>
        /// Create a new virtual key group
        /// </summary>
        public async Task<IResult> CreateGroup(CreateVirtualKeyGroupRequestDto request)
        {
            var group = new VirtualKeyGroup
            {
                ExternalGroupId = request.ExternalGroupId,
                GroupName = request.GroupName,
                Balance = request.InitialBalance ?? 0,
                LifetimeCreditsAdded = request.InitialBalance ?? 0,
                LifetimeSpent = 0
            };

            var id = await _groupRepository.CreateAsync(group);
            group.Id = id;

            LogAdminAudit("Created", "VirtualKeyGroup", id,
                $"Name: {group.GroupName}, InitialBalance: {group.Balance}");
            AdminOperationsMetricsService.RecordConfigurationChange("virtualkeygroup", "create");

            var dto = VirtualKeyGroupDto.FromEntity(group);

            return Results.Created($"/v1/admin/virtual-key-groups/{group.Id}", dto);
        }

        /// <summary>
        /// Update a virtual key group
        /// </summary>
        public async Task<IResult> UpdateGroup(int id, UpdateVirtualKeyGroupRequestDto request,
            ResourcePrecondition? precondition = null, CancellationToken cancellationToken = default)
        {
            var changes = new List<(string Property, string? OldValue, string? NewValue)>();
            var rateLimitsChanged = false;
            var keyCount = 0;
            void ApplyPatch(VirtualKeyGroup group)
            {
                if (request.TryGetPatchedProperty(nameof(request.GroupName), group.GroupName, out string? groupName))
                {
                    if (string.IsNullOrWhiteSpace(groupName))
                        throw new InvalidOperationException("groupName cannot be null or empty.");
                    if (group.GroupName != groupName)
                    {
                        changes.Add(("GroupName", group.GroupName, groupName));
                        group.GroupName = groupName;
                    }
                }

                if (request.TryGetPatchedProperty(nameof(request.ExternalGroupId), group.ExternalGroupId, out string? externalGroupId) && group.ExternalGroupId != externalGroupId)
                {
                    changes.Add(("ExternalGroupId", group.ExternalGroupId, externalGroupId));
                    group.ExternalGroupId = externalGroupId;
                }

                if (request.TryGetPatchedProperty(nameof(request.RateLimitRpm), group.RateLimitRpm, out int? rateLimitRpm) &&
                    group.RateLimitRpm != rateLimitRpm)
                {
                    changes.Add(("RateLimitRpm", group.RateLimitRpm?.ToString(), rateLimitRpm?.ToString()));
                    group.RateLimitRpm = rateLimitRpm;
                    rateLimitsChanged = true;
                }

                if (request.TryGetPatchedProperty(nameof(request.RateLimitRpd), group.RateLimitRpd, out int? rateLimitRpd) &&
                    group.RateLimitRpd != rateLimitRpd)
                {
                    changes.Add(("RateLimitRpd", group.RateLimitRpd?.ToString(), rateLimitRpd?.ToString()));
                    group.RateLimitRpd = rateLimitRpd;
                    rateLimitsChanged = true;
                }

                if (request.TryGetPatchedProperty(nameof(request.RateLimitTpm), group.RateLimitTpm, out int? rateLimitTpm) &&
                    group.RateLimitTpm != rateLimitTpm)
                {
                    changes.Add(("RateLimitTpm", group.RateLimitTpm?.ToString(), rateLimitTpm?.ToString()));
                    group.RateLimitTpm = rateLimitTpm;
                    rateLimitsChanged = true;
                }

                if (request.TryGetPatchedProperty(nameof(request.MaxParallelRequests), group.MaxParallelRequests, out int? maxParallelRequests) &&
                    group.MaxParallelRequests != maxParallelRequests)
                {
                    changes.Add(("MaxParallelRequests", group.MaxParallelRequests?.ToString(), maxParallelRequests?.ToString()));
                    group.MaxParallelRequests = maxParallelRequests;
                    rateLimitsChanged = true;
                }

            }

            VirtualKeyGroupDto dto;
            if (precondition is null)
            {
                var group = await _groupRepository.GetByIdAsync(id) ?? throw new KeyNotFoundException();
                ApplyPatch(group);
                await _groupRepository.UpdateAsync(group);
                dto = VirtualKeyGroupDto.FromEntity(group);
            }
            else
            {
                var db = _context as ConduitDbContext ?? throw new InvalidOperationException("Conditional group updates require a database context.");
                dto = await ResourceMutationGuard.ExecuteAsync<VirtualKeyGroup, VirtualKeyGroupDto>(db, id, precondition,
                    async (context, group, ct) =>
                    {
                        ApplyPatch(group);
                        if (changes.Count > 0) group.UpdatedAt = DateTime.UtcNow;
                        keyCount = await context.VirtualKeys.CountAsync(key => key.VirtualKeyGroupId == id, ct);
                    }, group =>
                    {
                        var snapshot = VirtualKeyGroupDto.FromEntity(group);
                        snapshot.VirtualKeyCount = keyCount;
                        return snapshot;
                    }, cancellationToken) ?? throw new KeyNotFoundException();
            }

            if (rateLimitsChanged)
            {
                await InvalidateGroupKeyCachesAsync(id);
            }

            LogAdminAuditWithChanges("VirtualKeyGroup", id, changes);
            AdminOperationsMetricsService.RecordConfigurationChange("virtualkeygroup", "update");

            return Ok(dto);
        }

        /// <summary>
        /// Publishes a cache-invalidation event for every key in the group.
        /// </summary>
        /// <remarks>
        /// Group ceilings travel with the authenticated request, and the Gateway caches the key
        /// entity that carries them. Without this, a group limit change would not take effect
        /// until each key's cache entry expired — operators would see an edit that appears to
        /// do nothing.
        /// </remarks>
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2026",
            Justification = "Admin EF queries are outside the supported NativeAOT data-plane contract (ADR 0006). Owner: database/runtime. Upstream: dotnet/efcore#29754. Remove when this query uses a fixed-shape native store or EF expression construction is trim-safe.")]
        private async Task InvalidateGroupKeyCachesAsync(int groupId)
        {
            if (_eventBus is null)
            {
                Logger.LogWarning(
                    "No event bus is available, so group {GroupId} rate-limit changes will not reach the Gateway " +
                    "until its cached key entries expire", groupId);
                return;
            }

            var keys = await _context.VirtualKeys
                .AsNoTracking()
                .Where(k => k.VirtualKeyGroupId == groupId)
                .Select(k => new { k.Id, k.KeyHash })
                .ToListAsync();

            foreach (var key in keys)
            {
                await _eventBus.PublishAsync(new VirtualKeyUpdated
                {
                    KeyId = key.Id,
                    KeyHash = key.KeyHash,
                    ChangedProperties = new[] { "GroupRateLimits" },
                    CorrelationId = Guid.NewGuid().ToString()
                });
            }
        }

        /// <summary>
        /// Adjust the balance of a virtual key group
        /// </summary>
        public async Task<IResult> AdjustBalance(int id, AdjustBalanceDto request,
            ResourcePrecondition? precondition = null, CancellationToken cancellationToken = default)
        {
            // Get the authenticated user's identity
            var initiatedBy = User.Identity?.Name ?? "System";

            VirtualKeyGroup? conditionalGroup = null;
            decimal newBalance;
            if (precondition is null)
                newBalance = await _groupRepository.AdjustBalanceAsync(id, request.Amount, request.Description, initiatedBy);
            else
            {
                conditionalGroup = await _groupRepository.AdjustBalanceConditionalAsync(id, request.Amount,
                    request.Description, initiatedBy, precondition, cancellationToken) ?? throw new KeyNotFoundException();
                newBalance = conditionalGroup.Balance;
            }

            LogAdminAudit("AdjustedBalance", "VirtualKeyGroup", id,
                $"Amount: {request.Amount}, Description: {request.Description}, NewBalance: {newBalance}");

            var group = conditionalGroup ?? await _groupRepository.GetByIdAsync(id);
            if (group == null)
                throw new KeyNotFoundException();

            return Ok(VirtualKeyGroupDto.FromEntity(group));
        }

        /// <summary>
        /// Delete a virtual key group
        /// </summary>
        public async Task<IResult> DeleteGroup(int id, ResourcePrecondition? precondition = null, CancellationToken cancellationToken = default)
        {
            VirtualKeyGroup group;
            if (precondition is null)
            {
                group = await _groupRepository.GetByIdAsync(id) ?? throw new KeyNotFoundException();
                if (group.VirtualKeys?.Count > 0)
                    throw new InvalidOperationException("Cannot delete group with existing virtual keys");
                await _groupRepository.DeleteAsync(id);
            }
            else
            {
                var db = _context as ConduitDbContext ?? throw new InvalidOperationException("Conditional group deletes require a database context.");
                group = await ResourceMutationGuard.ExecuteAsync<VirtualKeyGroup, VirtualKeyGroup>(db, id, precondition,
                    async (context, entity, ct) =>
                    {
                        if (await context.VirtualKeys.AnyAsync(key => key.VirtualKeyGroupId == id, ct))
                            throw new InvalidOperationException("Cannot delete group with existing virtual keys");
                        context.VirtualKeyGroups.Remove(entity);
                    }, entity => entity, cancellationToken) ?? throw new KeyNotFoundException();
            }

            LogAdminAudit("Deleted", "VirtualKeyGroup", id,
                $"Name: {group.GroupName}");
            AdminOperationsMetricsService.RecordConfigurationChange("virtualkeygroup", "delete");

            return NoContent();
        }

        /// <summary>
        /// Get transaction history for a virtual key group
        /// </summary>
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2026",
            Justification = "Admin EF queries are outside the supported NativeAOT data-plane contract (ADR 0006). Owner: database/runtime. Upstream: dotnet/efcore#29754. Remove when this query uses a fixed-shape native store or EF expression construction is trim-safe.")]
        public async Task<IResult> GetTransactionHistory(
            int id,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            (page, pageSize) = Pagination.Normalize(page, pageSize);

            var group = await _groupRepository.GetByIdAsync(id);
            if (group == null)
                throw new KeyNotFoundException();

            // Get total count (soft delete filter applied automatically via named query filter)
            var totalCount = await _context.VirtualKeyGroupTransactions
                .Where(t => t.VirtualKeyGroupId == id)
                .CountAsync();

            // Calculate pagination
            var skip = (page - 1) * pageSize;

            // Get paginated transactions (soft delete filter applied automatically via named query filter)
            var transactions = await _context.VirtualKeyGroupTransactions
                .Where(t => t.VirtualKeyGroupId == id)
                .OrderByDescending(t => t.CreatedAt)
                .Skip(skip)
                .Take(pageSize)
                .Select(t => new VirtualKeyGroupTransactionDto
                {
                    Id = t.Id,
                    VirtualKeyGroupId = t.VirtualKeyGroupId,
                    TransactionType = t.TransactionType,
                    Amount = t.Amount,
                    BalanceAfter = t.BalanceAfter,
                    Description = t.Description,
                    ReferenceId = t.ReferenceId,
                    ReferenceType = t.ReferenceType,
                    InitiatedBy = t.InitiatedBy,
                    InitiatedByUserId = t.InitiatedByUserId,
                    CreatedAt = t.CreatedAt
                })
                .ToListAsync();

            return Ok(new PagedResult<VirtualKeyGroupTransactionDto>
            {
                Data = transactions,
                Pagination = PaginationMetadata.Create(page, pageSize, totalCount)
            });
        }

        /// <summary>
        /// Get virtual keys in a group
        /// </summary>
        public async Task<IResult> GetKeysInGroup(int id)
        {
            var group = await _groupRepository.GetByIdWithKeysAsync(id);
            if (group == null)
            {
                return AdminResults.NotFoundEntity("VirtualKeyGroup", id);
            }

            var keys = group.VirtualKeys?.Select(k => new VirtualKeyDto
            {
                Id = k.Id,
                KeyName = k.KeyName,
                KeyPrefix = VirtualKeyUtilities.GenerateKeyPrefix(k.KeyHash),
                AllowedModels = VirtualKeyUtilities.ParseAllowedModels(k.AllowedModels),
                VirtualKeyGroupId = k.VirtualKeyGroupId,
                IsEnabled = k.IsEnabled,
                ExpiresAt = k.ExpiresAt,
                CreatedAt = k.CreatedAt,
                UpdatedAt = k.UpdatedAt,
                Metadata = VirtualKeyUtilities.MapToDto(k).Metadata,
                RateLimitRpm = k.RateLimitRpm,
                RateLimitRpd = k.RateLimitRpd,
                RateLimitTpm = k.RateLimitTpm,
                MaxParallelRequests = k.MaxParallelRequests,
                RateLimitPriority = k.RateLimitPriority,
                Description = k.Description
            }).ToList() ?? new List<VirtualKeyDto>();

            return Ok(keys);
        }

        /// <summary>
        /// Process a refund for a virtual key group
        /// </summary>
        /// <param name="id">The virtual key group ID</param>
        /// <param name="request">The refund request details</param>
        /// <param name="idempotencyKey">Unique identifier for this refund operation</param>
        /// <param name="precondition">Explicit HTTP version condition, when provided.</param>
        /// <param name="cancellationToken">Request cancellation.</param>
        /// <returns>The refund result with transaction details</returns>
        public async Task<IResult> ProcessRefund(
            int id,
            [FromBody] ProcessRefundRequestDto request,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            ResourcePrecondition? precondition = null, CancellationToken cancellationToken = default)
        {
            // Validate request
            if (string.IsNullOrEmpty(request.ModelId))
            {
                return BadRequest("Model ID is required");
            }

            if (string.IsNullOrEmpty(request.RefundReason))
            {
                return BadRequest("Refund reason is required");
            }

            if (string.IsNullOrWhiteSpace(request.OriginalTransactionId))
            {
                return BadRequest("Original transaction ID is required");
            }

            if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 100)
            {
                return BadRequest("Idempotency-Key header is required and must be at most 100 characters");
            }

            // Get user info for audit trail
            var initiatedBy = User.Identity?.Name ?? "System";
            var initiatedByUserId = User.FindFirst("sub")?.Value; // Clerk user ID from JWT

            // Convert DTOs to core models
            var originalUsage = MapToUsage(request.OriginalUsage);
            var refundUsage = MapToUsage(request.RefundUsage);

            // Process the refund
            RefundResult refundResult;
            try
            {
                if (precondition is null)
                {
                    refundResult = await _refundService.ProcessRefundAsync(
                        id,
                        request.ModelId,
                        originalUsage,
                        refundUsage,
                        request.RefundReason,
                        request.OriginalTransactionId,
                        idempotencyKey.Trim(),
                        initiatedBy,
                        initiatedByUserId,
                        request.RequestLogId);
                }
                else
                {
                    refundResult = await _refundService.ProcessRefundConditionalAsync(id, request.ModelId,
                        originalUsage, refundUsage, request.RefundReason, request.OriginalTransactionId,
                        idempotencyKey.Trim(), initiatedBy, initiatedByUserId, request.RequestLogId,
                        precondition, cancellationToken);
                }
            }
            catch (Configuration.Exceptions.IdempotencyConflictException ex)
            {
                return Conflict(ex.Message);
            }

            // Conditional refunds already return their committed snapshot. A later group
            // deletion must not turn that committed success into a not-found response.
            if (precondition is null && await _groupRepository.GetByIdAsync(id) is null)
                throw new KeyNotFoundException();

            // Map to response DTO
            var responseDto = MapToRefundResultDto(refundResult, refundResult.BalanceAfter);
            responseDto.ResourceETag = refundResult.ResourceETag;

            LogAdminAudit("Refunded", "VirtualKeyGroup", id,
                $"Amount: {refundResult.RefundAmount:C}, Model: {request.ModelId}, Reason: {request.RefundReason}, TransactionId: {refundResult.RefundTransactionId}, OriginalTransactionId: {refundResult.OriginalTransactionId ?? "none"}");

            return Ok(responseDto);
        }

        /// <summary>
        /// Maps UsageDto to Usage core model
        /// </summary>
        private static Usage MapToUsage(UsageDto dto)
        {
            return new Usage
            {
                PromptTokens = dto.PromptTokens,
                CompletionTokens = dto.CompletionTokens,
                TotalTokens = dto.TotalTokens,
                CachedInputTokens = dto.CachedInputTokens,
                CachedInputTokensIncludedInPrompt = dto.CachedInputTokensIncludedInPrompt,
                CachedWriteTokens = dto.CachedWriteTokens,
                ReasoningTokens = dto.ReasoningTokens,
                ImageCount = dto.ImageCount,
                ImageQuality = dto.ImageQuality,
                ImageResolution = dto.ImageResolution,
                VideoDurationSeconds = dto.VideoDurationSeconds,
                VideoResolution = dto.VideoResolution,
                SearchUnits = dto.SearchUnits,
                InferenceSteps = dto.InferenceSteps,
                IsBatch = dto.IsBatch
            };
        }

        /// <summary>
        /// Maps RefundResult core model to RefundResultDto
        /// </summary>
        private static RefundResultDto MapToRefundResultDto(RefundResult result, decimal balanceAfter)
        {
            return new RefundResultDto
            {
                TransactionId = result.RefundTransactionId,
                ModelId = result.ModelId,
                OriginalUsage = MapToUsageDto(result.OriginalUsage),
                RefundUsage = MapToUsageDto(result.RefundUsage),
                RefundAmount = result.RefundAmount,
                BalanceAfter = balanceAfter,
                OriginalTransactionId = result.OriginalTransactionId,
                RefundReason = result.RefundReason,
                RefundedAt = result.RefundedAt,
                IsPartialRefund = result.IsPartialRefund,
                ValidationMessages = result.ValidationMessages,
                Breakdown = result.Breakdown != null ? new RefundBreakdownDto
                {
                    InputTokenRefund = result.Breakdown.InputTokenRefund,
                    OutputTokenRefund = result.Breakdown.OutputTokenRefund,
                    ImageRefund = result.Breakdown.ImageRefund,
                    VideoRefund = result.Breakdown.VideoRefund,
                    EmbeddingRefund = result.Breakdown.EmbeddingRefund,
                    SearchUnitRefund = result.Breakdown.SearchUnitRefund,
                    InferenceStepRefund = result.Breakdown.InferenceStepRefund
                } : null
            };
        }

        /// <summary>
        /// Maps Usage core model to UsageDto
        /// </summary>
        private static UsageDto MapToUsageDto(Usage usage)
        {
            return new UsageDto
            {
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
                CachedInputTokens = usage.CachedInputTokens,
                CachedInputTokensIncludedInPrompt = usage.CachedInputTokensIncludedInPrompt,
                CachedWriteTokens = usage.CachedWriteTokens,
                ReasoningTokens = usage.ReasoningTokens,
                ImageCount = usage.ImageCount,
                ImageQuality = usage.ImageQuality,
                ImageResolution = usage.ImageResolution,
                VideoDurationSeconds = usage.VideoDurationSeconds,
                VideoResolution = usage.VideoResolution,
                SearchUnits = usage.SearchUnits,
                InferenceSteps = usage.InferenceSteps,
                IsBatch = usage.IsBatch
            };
        }
    }
}
