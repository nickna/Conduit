using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Auditing;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Events;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ConduitLLM.Admin.Endpoints
{
    /// <summary>
    /// Controller for managing provider error tracking and key status
    /// </summary>
    public class ProviderErrorsEndpoints
    {
        private readonly IProviderErrorTrackingService _errorService;
        private readonly IProviderKeyCredentialRepository _keyRepo;
        private readonly IProviderRepository _providerRepo;
        private readonly IEventPublisher _eventPublisher;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<ProviderErrorsEndpoints> _logger;

        /// <summary>
        /// Initializes the Provider Errors endpoint handler.
        /// </summary>
        public ProviderErrorsEndpoints(
            IProviderErrorTrackingService errorService,
            IProviderKeyCredentialRepository keyRepo,
            IProviderRepository providerRepo,
            IEventPublisher eventPublisher,
            IHttpContextAccessor httpContextAccessor,
            ILogger<ProviderErrorsEndpoints> logger)
        {
            _errorService = errorService ?? throw new ArgumentNullException(nameof(errorService));
            _keyRepo = keyRepo ?? throw new ArgumentNullException(nameof(keyRepo));
            _providerRepo = providerRepo ?? throw new ArgumentNullException(nameof(providerRepo));
            _eventPublisher = eventPublisher;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        public static IEndpointRouteBuilder MapProviderErrorsEndpoints(IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/v1/admin/provider-errors")
                .RequireAuthorization("MasterKeyPolicy")
                .AddEndpointFilter<OperationLoggingEndpointFilter>()
                .WithTags("Provider Errors");
            group.MapGet("/recent", ([FromServices] ProviderErrorsEndpoints e, int? providerId = null, int? keyId = null, int limit = 100) => e.GetRecentErrors(providerId, keyId, limit))
                .WithName("ProviderErrors_GetRecent")
                .WithDescription("Returns a bounded tail window of the most recent provider errors; this is intentionally not a paged collection.")
                .Produces<List<ProviderErrorDto>>();
            group.MapGet("/summary", ([FromServices] ProviderErrorsEndpoints e) => e.GetErrorSummary())
                .WithName("ProviderErrors_GetSummary").Produces<List<ProviderErrorSummaryDto>>();
            group.MapGet("/keys/{keyId}", ([FromServices] ProviderErrorsEndpoints e, int keyId) => e.GetKeyErrors(keyId))
                .WithName("ProviderErrors_GetKeyErrors").Produces<KeyErrorDetailsDto>().Produces<AdminProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json");
            group.MapPost("/keys/{keyId}/clear", ([FromServices] ProviderErrorsEndpoints e, int keyId, ClearErrorsRequest request) => e.ClearKeyErrors(keyId, request))
                .WithName("ProviderErrors_ClearKeyErrors").Produces<ClearKeyErrorsResponseDto>().Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json");
            group.MapGet("/stats", ([FromServices] ProviderErrorsEndpoints e, int hours = 24) => e.GetErrorStatistics(hours))
                .WithName("ProviderErrors_GetStatistics").Produces<ErrorStatisticsDto>();
            group.MapGet("/providers/{providerId}/key-errors", ([FromServices] ProviderErrorsEndpoints e, int providerId, int hours = 1) => e.GetErrorCountsByKey(providerId, hours))
                .WithName("ProviderErrors_GetCountsByKey").Produces<Dictionary<int, int>>();
            group.MapPost("/keys/{keyId}/disable", ([FromServices] ProviderErrorsEndpoints e, int keyId, string reason) => e.DisableKey(keyId, reason))
                .WithName("ProviderErrors_DisableKey").Accepts<string>("application/json").Produces<DisableKeyResponseDto>().Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json");
            return app;
        }

        /// <summary>
        /// Get recent errors across all providers
        /// </summary>
        /// <param name="providerId">Optional provider ID filter</param>
        /// <param name="keyId">Optional key ID filter</param>
        /// <param name="limit">Maximum number of errors to return (default: 100)</param>
        /// <returns>List of recent provider errors</returns>
        public async Task<IResult> GetRecentErrors(
            [FromQuery] int? providerId = null,
            [FromQuery] int? keyId = null,
            [FromQuery] int limit = 100)
        {
            limit = Math.Clamp(limit, 1, 1000);

            var errors = await _errorService.GetRecentErrorsAsync(providerId, keyId, limit);

            // Get provider names for display using efficient lookup
            var providerMap = await _providerRepo.GetProviderNameMapAsync();

            var dtos = errors.Select(e => new ProviderErrorDto
            {
                KeyCredentialId = e.KeyCredentialId,
                ProviderId = e.ProviderId,
                ProviderName = providerMap.GetValueOrDefault(e.ProviderId),
                ErrorType = e.ErrorType.ToString(),
                ErrorMessage = e.ErrorMessage,
                HttpStatusCode = e.HttpStatusCode,
                OccurredAt = e.OccurredAt,
                IsFatal = e.IsFatal,
                ModelName = e.ModelName
            }).ToList();

            return Results.Ok(dtos);
        }

        /// <summary>
        /// Get error summary for all providers
        /// </summary>
        /// <returns>List of provider error summaries</returns>
        public async Task<IResult> GetErrorSummary()
        {
            // Use paginated retrieval - get all providers in batches
            var allProviders = new List<ConduitLLM.Configuration.Entities.Provider>();
            var pageNumber = 1;
            const int pageSize = 100;
            int totalCount;

            do
            {
                var (items, count) = await _providerRepo.GetPaginatedAsync(pageNumber, pageSize);
                allProviders.AddRange(items);
                totalCount = count;
                pageNumber++;
            } while (allProviders.Count < totalCount);

            // Fetch all provider summaries in parallel to avoid N+1
            var summaryTasks = allProviders.Select(async provider =>
            {
                var summary = await _errorService.GetProviderSummaryAsync(provider.Id);
                return (provider, summary);
            });

            var results = await Task.WhenAll(summaryTasks);

            var summaries = results
                .Where(r => r.summary != null)
                .Select(r => new ProviderErrorSummaryDto
                {
                    ProviderId = r.provider.Id,
                    ProviderName = r.provider.ProviderName,
                    TotalErrors = r.summary!.TotalErrors,
                    FatalErrors = r.summary.FatalErrors,
                    Warnings = r.summary.Warnings,
                    DisabledKeyIds = r.summary.DisabledKeyIds,
                    DisabledKeyCount = r.summary.DisabledKeyIds.Count,
                    LastError = r.summary.LastError,
                    ProviderDisabledAt = r.summary.ProviderDisabledAt,
                    ProviderDisableReason = r.summary.ProviderDisableReason
                })
                .ToList();

            return Results.Ok(summaries);
        }

        /// <summary>
        /// Get detailed error information for a specific key
        /// </summary>
        /// <param name="keyId">ID of the key</param>
        /// <returns>Detailed error information for the key</returns>
        public async Task<IResult> GetKeyErrors(int keyId)
        {
            var details = await _errorService.GetKeyErrorDetailsAsync(keyId);
            if (details == null)
            {
                throw new KeyNotFoundException($"No error data found for key {keyId}");
            }

            var dto = new KeyErrorDetailsDto
            {
                KeyId = details.KeyId,
                KeyName = details.KeyName,
                IsDisabled = details.IsDisabled,
                DisabledAt = details.DisabledAt
            };

            if (details.FatalError != null)
            {
                dto.FatalError = new FatalErrorDto
                {
                    ErrorType = details.FatalError.ErrorType.ToString(),
                    Count = details.FatalError.Count,
                    FirstSeen = details.FatalError.FirstSeen,
                    LastSeen = details.FatalError.LastSeen,
                    LastErrorMessage = details.FatalError.LastErrorMessage,
                    LastStatusCode = details.FatalError.LastStatusCode
                };
            }

            dto.RecentWarnings = details.RecentWarnings.Select(w => new WarningErrorDto
            {
                Type = w.Type.ToString(),
                Message = w.Message,
                Timestamp = w.Timestamp
            }).ToList();

            return Results.Ok(dto);
        }

        /// <summary>
        /// Clear errors and optionally re-enable a key
        /// </summary>
        /// <param name="keyId">ID of the key</param>
        /// <param name="request">Clear errors request</param>
        /// <returns>Operation result</returns>
        public async Task<IResult> ClearKeyErrors(
            int keyId,
            [FromBody] ClearErrorsRequest request)
        {
            if (!request.ConfirmReenable && request.ReenableKey)
            {
                return AdminResults.BadRequest("Must confirm re-enabling the key");
            }

            // Look up the key to get its providerId for proper cleanup
            var key = await _keyRepo.GetByIdAsync(keyId);
            int? providerId = key?.ProviderId;
            var keyErrorDetails = key != null
                ? await _errorService.GetKeyErrorDetailsAsync(keyId)
                : null;
            var providerSummary = providerId.HasValue
                ? await _errorService.GetProviderSummaryAsync(providerId.Value)
                : null;
            var providerWasAutoDisabled =
                providerSummary?.ProviderDisabledAt != null &&
                providerSummary.ProviderDisableReason == ProviderErrorTrackingService.AllKeysDisabledReason;

            var keysToRecover = new List<ConduitLLM.Configuration.Entities.ProviderKeyCredential>();
            if (key != null)
            {
                keysToRecover.Add(key);

                var recoverAccountGroup =
                    request.ReenableKey &&
                    key.ProviderAccountGroup > 0 &&
                    keyErrorDetails?.FatalError?.ErrorType == ProviderErrorType.InsufficientBalance;
                if (recoverAccountGroup)
                {
                    var allProviderKeys = await RepositoryPaginationExtensions.GetAllViaPaginationAsync(
                        _keyRepo.GetByProviderIdPaginatedAsync, key.ProviderId);
                    foreach (var candidate in allProviderKeys.Where(candidate =>
                                 candidate.Id != keyId &&
                                 candidate.ProviderAccountGroup == key.ProviderAccountGroup))
                    {
                        var candidateErrors =
                            await _errorService.GetKeyErrorDetailsAsync(candidate.Id);
                        if (candidateErrors?.FatalError?.ErrorType ==
                            ProviderErrorType.InsufficientBalance)
                        {
                            keysToRecover.Add(candidate);
                        }
                    }
                }
            }

            // Clear error state for every key that will be restored together.
            if (keysToRecover.Count > 0)
            {
                foreach (var recoveryKey in keysToRecover)
                {
                    await _errorService.ClearErrorsForKeyAsync(
                        recoveryKey.Id, recoveryKey.ProviderId);
                }
            }
            else
            {
                await _errorService.ClearErrorsForKeyAsync(keyId, providerId);
            }

            // Re-enable the key or its nonzero shared-account group if requested.
            if (request.ReenableKey && key != null)
            {
                foreach (var recoveryKey in keysToRecover.Where(candidate => !candidate.IsEnabled))
                {
                    recoveryKey.IsEnabled = true;
                    await _keyRepo.UpdateAsync(recoveryKey);
                }

                _eventPublisher.PublishFireAndForget(new ProviderKeyReenabledEvent
                {
                    KeyId = keyId,
                    ProviderId = key.ProviderId,
                    ReenabledBy = _httpContextAccessor.HttpContext?.User.Identity?.Name ?? "Admin",
                    Reason = request.Reason ?? "Manual re-enable after error resolution",
                    ReenabledAt = DateTime.UtcNow,
                    AffectedKeyIds = keysToRecover.Select(candidate => candidate.Id).ToArray(),
                    ProviderAccountGroup = keysToRecover.Count > 1
                        ? key.ProviderAccountGroup
                        : (short)0
                }, "ClearKeyErrors");

                LogAdminAudit("ClearedErrorsAndReenabled", "ProviderKeyCredential", keyId,
                    $"ProviderId: {key.ProviderId}, KeyIds: {string.Join(",", keysToRecover.Select(candidate => candidate.Id))}");
            }
            else
            {
                LogAdminAudit("ClearedErrors", "ProviderKeyCredential", keyId);
            }

            if (request.ReenableKey && providerWasAutoDisabled && providerId.HasValue)
            {
                var provider = await _providerRepo.GetByIdAsync(providerId.Value);
                if (provider != null && !provider.IsEnabled)
                {
                    provider.IsEnabled = true;
                    await _providerRepo.UpdateAsync(provider);
                }

                await _errorService.ClearProviderDisabledAsync(providerId.Value);
            }

            return Results.Ok(new ClearKeyErrorsResponseDto
            {
                Message = request.ReenableKey
                    ? "Errors cleared and key re-enabled successfully"
                    : "Errors cleared successfully",
                KeyId = keyId,
                Reenabled = request.ReenableKey
            });
        }

        /// <summary>
        /// Get error statistics for dashboard
        /// </summary>
        /// <param name="hours">Time window in hours (default: 24)</param>
        /// <returns>Error statistics</returns>
        public async Task<IResult> GetErrorStatistics(
            [FromQuery] int hours = 24)
        {
            hours = Math.Clamp(hours, 1, 168);

            var window = TimeSpan.FromHours(hours);
            var stats = await _errorService.GetErrorStatisticsAsync(window);

            // Map provider IDs to names for the statistics
            var providerNameMap = await _providerRepo.GetProviderNameMapAsync();
            var errorsByProviderName = stats.ErrorsByProvider.ToDictionary(
                kvp => providerNameMap.GetValueOrDefault(int.Parse(kvp.Key), $"Provider {kvp.Key}"),
                kvp => kvp.Value);

            var dto = new ErrorStatisticsDto
            {
                TotalErrors = stats.TotalErrors,
                FatalErrors = stats.FatalErrors,
                Warnings = stats.Warnings,
                DisabledKeys = stats.DisabledKeys,
                ErrorsByType = stats.ErrorsByType,
                ErrorsByProvider = errorsByProviderName,
                TimeWindow = window,
                GeneratedAt = DateTime.UtcNow
            };

            return Results.Ok(dto);
        }

        /// <summary>
        /// Get error counts by key for a specific provider
        /// </summary>
        /// <param name="providerId">Provider ID</param>
        /// <param name="hours">Time window in hours (default: 1)</param>
        /// <returns>Dictionary of key ID to error count</returns>
        public async Task<IResult> GetErrorCountsByKey(
            int providerId,
            [FromQuery] int hours = 1)
        {
            hours = Math.Clamp(hours, 1, 24);

            var window = TimeSpan.FromHours(hours);
            var counts = await _errorService.GetErrorCountsByKeyAsync(providerId, window);

            return Results.Ok(counts);
        }

        /// <summary>
        /// Manually disable a key due to errors
        /// </summary>
        /// <param name="keyId">ID of the key to disable</param>
        /// <param name="reason">Reason for disabling</param>
        /// <returns>Operation result</returns>
        public async Task<IResult> DisableKey(
            int keyId,
            [FromBody] string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return AdminResults.BadRequest("Reason is required for disabling a key");
            }

            await _errorService.DisableKeyAsync(keyId, $"Manual disable: {reason}");

            LogAdminAudit("Disabled", "ProviderKeyCredential", keyId,
                $"Reason: {reason}");

            return Results.Ok(new DisableKeyResponseDto
            {
                Message = "Key disabled successfully",
                KeyId = keyId
            });
        }

        private void LogAdminAudit(string operation, string entityType, object? entityId = null, string? detail = null) =>
            AdminAudit.Log(_httpContextAccessor.HttpContext!, _logger, operation, entityType, entityId, detail);
    }
}
