using System.Text.Json;

using ConduitLLM.Admin.Auditing;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Admin.Endpoints;

public static class PromptCachingEndpoints
{
    private const string SettingKey = PromptCachingConstants.SettingsKey;
    public static IEndpointRouteBuilder MapPromptCachingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/prompt-cache-settings")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("PromptCaching");
        group.MapGet("/config", GetConfig).WithName("PromptCaching_GetConfig")
            .Produces<PromptCachingConfigDto>().Produces<ProblemDetails>(StatusCodes.Status409Conflict);
        group.MapPut("/config", UpdateConfig).WithName("PromptCaching_UpdateConfig")
            .Produces<PromptCachingConfigDto>().Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json");
        group.MapGet("/capabilities", GetCapabilities).WithName("PromptCaching_GetCapabilities")
            .Produces<IReadOnlyList<PromptCachingCapability>>();
        group.MapGet("/analytics", GetAnalytics).WithName("PromptCaching_GetAnalytics")
            .Produces<PromptCachingAnalyticsDto>().Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<AdminProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");
        return app;
    }

    private static async Task<IResult> GetConfig([FromServices] IGlobalSettingsCacheService cacheService)
    {
        var json = await cacheService.GetSettingValueAsync(SettingKey);
        if (json is null)
        {
            return Results.Ok(new PromptCachingConfigDto
            {
                SchemaVersion = PromptCachingConstants.SchemaVersion,
                Enabled = false
            });
        }

        PromptCachingConfig? config;
        try
        {
            config = AdminJson.Deserialize<PromptCachingConfig>(
                json, PromptCachingSerialization.Options);
        }
        catch (JsonException)
        {
            config = null;
        }
        if (config is not null)
        {
            config = PromptCachingPolicyResolver.Migrate(config);
        }
        var errors = config is null ? Array.Empty<string>() : PromptCachingPolicyResolver.Validate(config);
        if (config is null || errors.Count > 0)
        {
            return AdminResults.Problem(
                StatusCodes.Status409Conflict,
                config is null ? "The stored configuration is malformed." : string.Join("; ", errors),
                "prompt_caching_config_version_unsupported");
        }
        return Results.Ok(ToDto(config));
    }

    private static async Task<IResult> UpdateConfig(
        [FromBody] UpdatePromptCachingConfigDto dto,
        [FromServices] IAdminGlobalSettingService globalSettingService,
        [FromServices] IGlobalSettingsCacheService cacheService,
        HttpContext httpContext,
        ILoggerFactory loggerFactory)
    {
        var config = new PromptCachingConfig
        {
            SchemaVersion = dto.SchemaVersion,
            Enabled = dto.Enabled,
            Rules = dto.Rules
        };
        var validationErrors = PromptCachingPolicyResolver.Validate(config).ToList();
        for (var index = 0; index < dto.Rules.Count; index++)
        {
            if (!Enum.IsDefined(dto.Rules[index].Strategy))
            {
                validationErrors.Add($"rules[{index}].strategy is not supported.");
            }
        }
        if (validationErrors.Count > 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["config"] = validationErrors.ToArray()
            });
        }

        var json = AdminJson.Serialize(config, PromptCachingSerialization.Options);
        if (await globalSettingService.GetSettingByKeyAsync(SettingKey) is not null)
        {
            await globalSettingService.UpdateSettingByKeyAsync(new UpdateGlobalSettingByKeyDto
            {
                Key = SettingKey,
                Value = json,
                Description = "Provider-aware prompt caching policy"
            });
        }
        else
        {
            await globalSettingService.CreateSettingAsync(new CreateGlobalSettingDto
            {
                Key = SettingKey,
                Value = json,
                Description = "Provider-aware prompt caching policy"
            });
        }
        await cacheService.InvalidateSettingAsync(SettingKey);
        AdminAudit.Log(httpContext,
            loggerFactory.CreateLogger("ConduitLLM.Admin.Endpoints.PromptCaching"),
            "Updated", "PromptCachingConfig", detail: $"Enabled={dto.Enabled}, Rules={dto.Rules.Count}");
        return Results.Ok(ToDto(config));
    }

    private static IResult GetCapabilities() =>
        Results.Ok(PromptCachingProviderAdapters.Capabilities);

    private static async Task<IResult> GetAnalytics(
        [FromServices] IServiceProvider services,
        CancellationToken cancellationToken,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? alias = null,
        [FromQuery] string? provider = null,
        [FromQuery] int? mappingId = null)
    {
        var dbContextFactory = services.GetService<IDbContextFactory<ConduitDbContext>>();
        if (dbContextFactory is null)
        {
            return AdminResults.ServiceUnavailable("Analytics storage is unavailable.");
        }
        var end = to?.ToUniversalTime() ?? DateTime.UtcNow;
        var start = from?.ToUniversalTime() ?? end.AddHours(-24);
        if (start > end || end - start > TimeSpan.FromDays(90))
        {
            return AdminResults.BadRequest("The analytics range must be ordered and no longer than 90 days.");
        }

        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.RequestLogs.AsNoTracking()
            .Where(log => log.Timestamp >= start && log.Timestamp <= end && log.RequestType == "chat");
        if (!string.IsNullOrWhiteSpace(alias)) query = query.Where(log => log.ModelName == alias);
        if (!string.IsNullOrWhiteSpace(provider)) query = query.Where(log => log.ProviderType == provider);
        if (mappingId.HasValue) query = query.Where(log => log.ModelProviderMappingId == mappingId);
        var rows = await query.Select(log => new
        {
            log.ProviderType,
            log.ModelProviderMappingId,
            log.PromptCachingEligible,
            log.CachedInputTokens,
            log.CachedWriteTokens,
            log.CachedReadSavings,
            log.CacheWritePremium,
            log.ResponseTimeMs,
            log.RoutingAffinityUsed,
            log.RoutingFailoverCount
        }).ToListAsync(cancellationToken);
        var hits = rows.Where(row => row.CachedInputTokens is > 0).ToList();
        var misses = rows.Where(row => row.PromptCachingEligible && row.CachedInputTokens is not > 0).ToList();
        return Results.Ok(new PromptCachingAnalyticsDto
        {
            From = start,
            To = end,
            Requests = rows.Count,
            EligibleMisses = misses.Count,
            ReadEvents = hits.Count,
            WriteEvents = rows.Count(row => row.CachedWriteTokens is > 0),
            UnknownOutcomes = rows.Count(row => row.PromptCachingEligible && !row.CachedInputTokens.HasValue && !row.CachedWriteTokens.HasValue),
            CachedTokens = rows.Sum(row => (long)(row.CachedInputTokens ?? 0)),
            GrossSavings = rows.Sum(row => row.CachedReadSavings),
            WritePremium = rows.Sum(row => row.CacheWritePremium),
            HitLatencyMs = hits.Count == 0 ? null : hits.Average(row => row.ResponseTimeMs),
            MissLatencyMs = misses.Count == 0 ? null : misses.Average(row => row.ResponseTimeMs),
            AffinityReuse = rows.Count(row => row.RoutingAffinityUsed),
            Failovers = rows.Sum(row => row.RoutingFailoverCount),
            ProviderDistribution = rows.GroupBy(row => new
            {
                Provider = row.ProviderType ?? "unknown",
                row.ModelProviderMappingId
            }).Select(group => new PromptCachingProviderDistributionDto
            {
                Provider = group.Key.Provider,
                MappingId = group.Key.ModelProviderMappingId,
                Requests = group.Count()
            }).ToList()
        });
    }

    private static PromptCachingConfigDto ToDto(PromptCachingConfig config) => new()
    {
        SchemaVersion = config.SchemaVersion,
        Enabled = config.Enabled,
        Rules = config.Rules
    };
}
