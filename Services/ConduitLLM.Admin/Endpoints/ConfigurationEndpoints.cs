using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Data;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Admin.Endpoints;

/// <summary>Minimal API endpoints for routing configuration.</summary>
public static class ConfigurationEndpoints
{
    public static IEndpointRouteBuilder MapConfigurationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/admin/routing-configurations")
            .RequireAuthorization("MasterKeyPolicy")
            .AddEndpointFilter<OperationLoggingEndpointFilter>()
            .WithTags("Configuration");

        group.MapGet("/routing", GetRoutingConfig)
            .WithName("Configuration_GetRouting")
            .Produces<RoutingConfigurationDto>();
        group.MapGet("/routing/defaults", GetRoutingDefaults)
            .WithName("Configuration_GetRoutingDefaults")
            .Produces<RoutingDefaultsDto>();
        group.MapPut("/routing/defaults", PutRoutingDefaults)
            .WithName("Configuration_UpdateRoutingDefaults")
            .Produces<RoutingDefaultsDto>()
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json");
        group.MapGet("/routing/aliases/{alias}", GetAliasRouting)
            .WithName("Configuration_GetAliasRouting")
            .Produces<RoutePolicyDto>()
            .Produces(StatusCodes.Status404NotFound);
        group.MapPut("/routing/aliases/{alias}", PutAliasRouting)
            .WithName("Configuration_UpdateAliasRouting")
            .Produces<RoutePolicyDto>()
            .Produces<AdminProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Admin EF queries are outside the supported NativeAOT data-plane contract (ADR 0006). Owner: database/runtime. Upstream: dotnet/efcore#29754. Remove when this query uses a fixed-shape native store or EF expression construction is trim-safe.")]
    private static async Task<IResult> GetRoutingConfig(
        [FromServices] IDbContextFactory<ConduitDbContext> dbContextFactory,
        [FromServices] IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var modelMappings = await dbContext.ModelProviderMappings
            .Include(mapping => mapping.Provider)
            .Select(mapping => new RoutingRuleDto
            {
                Id = mapping.Id,
                ModelAlias = mapping.ModelAlias,
                ProviderModelId = mapping.ProviderModelId,
                IsEnabled = mapping.IsEnabled,
                Priority = mapping.RoutingPriority,
                Weight = mapping.RoutingWeight,
                Provider = new RoutingRuleProviderDto
                {
                    Id = mapping.Provider.Id,
                    Name = mapping.Provider.ProviderName,
                    Type = mapping.Provider.ProviderType,
                    IsEnabled = mapping.Provider.IsEnabled
                }
            })
            .ToListAsync(cancellationToken);

        var loadBalancers = new List<LoadBalancerDto>
        {
            new()
            {
                Id = "primary",
                Name = "Primary Load Balancer",
                Algorithm = configuration["LoadBalancing:Algorithm"] ?? "round-robin",
                HealthCheckInterval = 30,
                FailoverThreshold = 3,
                Endpoints = await GetProviderEndpoints(dbContext, cancellationToken)
            }
        };

        var routingStats = await GetRoutingStatistics(dbContext, cancellationToken);
        var aliasPolicies = await dbContext.ModelRoutePolicies.AsNoTracking()
            .Select(policy => new RoutePolicyDto
            {
                ModelAlias = policy.ModelAlias,
                Strategy = policy.Strategy,
                CostWeight = policy.CostWeight,
                SpeedWeight = policy.SpeedWeight,
                QualityWeight = policy.QualityWeight,
                CacheAffinityEnabled = policy.CacheAffinityEnabled,
                AffinityTtlSeconds = policy.AffinityTtlSeconds,
                MaxAffinityScorePenalty = policy.MaxAffinityScorePenalty,
                IsEnabled = policy.IsEnabled
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(new RoutingConfigurationDto
        {
            Timestamp = DateTime.UtcNow,
            RoutingRules = modelMappings,
            LoadBalancers = loadBalancers,
            Statistics = routingStats,
            Configuration = new RoutingSettingsDto
            {
                EnableFailover = configuration.GetValue("Routing:EnableFailover", true),
                EnableLoadBalancing = configuration.GetValue("Routing:EnableLoadBalancing", true),
                RequestTimeout = configuration.GetValue("Routing:RequestTimeoutSeconds", 30),
                CircuitBreakerThreshold = configuration.GetValue("Routing:CircuitBreakerThreshold", 5)
            },
            AliasPolicies = aliasPolicies
        });
    }

    private static async Task<IResult> GetRoutingDefaults(
        [FromServices] IDbContextFactory<ConduitDbContext> dbContextFactory,
        CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var setting = await context.GlobalSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Key == "Routing.Defaults", cancellationToken);
        return Results.Ok(setting is null
            ? new RoutingDefaultsDto()
            : AdminJson.Deserialize<RoutingDefaultsDto>(setting.Value) ?? new RoutingDefaultsDto());
    }

    private static async Task<IResult> PutRoutingDefaults(
        [FromBody] RoutingDefaultsDto dto,
        [FromServices] IAdminGlobalSettingService globalSettingService,
        CancellationToken cancellationToken)
    {
        if (dto.CostWeight + dto.SpeedWeight + dto.QualityWeight <= 0)
        {
            return AdminResults.BadRequest("At least one route score weight must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await globalSettingService.UpdateSettingByKeyAsync(new UpdateGlobalSettingByKeyDto
        {
            Key = "Routing.Defaults",
            Value = AdminJson.Serialize(dto),
            Description = "Default provider-aware chat routing policy"
        });
        await globalSettingService.UpdateSettingByKeyAsync(new UpdateGlobalSettingByKeyDto
        {
            Key = "Routing.Chat.Enabled",
            Value = dto.ChatRoutingEnabled.ToString(),
            Description = "Emergency provider-aware chat routing switch"
        });
        return Results.Ok(dto);
    }

    private static async Task<IResult> GetAliasRouting(
        string alias,
        [FromServices] IDbContextFactory<ConduitDbContext> dbContextFactory,
        CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var policy = await context.ModelRoutePolicies.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ModelAlias == alias, cancellationToken);
        return policy is null ? AdminResults.NotFound("Alias routing policy not found") : Results.Ok(ToRoutePolicyDto(policy));
    }

    private static async Task<IResult> PutAliasRouting(
        string alias,
        [FromBody] RoutePolicyDto dto,
        [FromServices] IDbContextFactory<ConduitDbContext> dbContextFactory,
        CancellationToken cancellationToken)
    {
        if (!dto.Strategy.Equals("Balanced", StringComparison.OrdinalIgnoreCase)
            || dto.CostWeight + dto.SpeedWeight + dto.QualityWeight <= 0)
        {
            return AdminResults.BadRequest("A valid Balanced policy is required.");
        }

        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (!await context.ModelProviderMappings.AnyAsync(mapping => mapping.ModelAlias == alias, cancellationToken))
        {
            return AdminResults.NotFound("Alias routing policy not found");
        }

        var policy = await context.ModelRoutePolicies.SingleOrDefaultAsync(
            item => item.ModelAlias == alias, cancellationToken);
        if (policy is null)
        {
            policy = new ModelRoutePolicy { ModelAlias = alias };
            context.ModelRoutePolicies.Add(policy);
        }

        policy.Strategy = "Balanced";
        policy.CostWeight = dto.CostWeight;
        policy.SpeedWeight = dto.SpeedWeight;
        policy.QualityWeight = dto.QualityWeight;
        policy.CacheAffinityEnabled = dto.CacheAffinityEnabled;
        policy.AffinityTtlSeconds = dto.AffinityTtlSeconds;
        policy.MaxAffinityScorePenalty = dto.MaxAffinityScorePenalty;
        policy.IsEnabled = dto.IsEnabled;
        policy.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToRoutePolicyDto(policy));
    }

    private static RoutePolicyDto ToRoutePolicyDto(ModelRoutePolicy policy) => new()
    {
        ModelAlias = policy.ModelAlias,
        Strategy = policy.Strategy,
        CostWeight = policy.CostWeight,
        SpeedWeight = policy.SpeedWeight,
        QualityWeight = policy.QualityWeight,
        CacheAffinityEnabled = policy.CacheAffinityEnabled,
        AffinityTtlSeconds = policy.AffinityTtlSeconds,
        MaxAffinityScorePenalty = policy.MaxAffinityScorePenalty,
        IsEnabled = policy.IsEnabled
    };

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Admin EF queries are outside the supported NativeAOT data-plane contract (ADR 0006). Owner: database/runtime. Upstream: dotnet/efcore#29754. Remove when this query uses a fixed-shape native store or EF expression construction is trim-safe.")]
    private static async Task<List<LoadBalancerEndpointDto>> GetProviderEndpoints(
        ConduitDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providers = await dbContext.Providers
            .Where(provider => provider.IsEnabled)
            .Select(provider => new
            {
                provider.Id,
                provider.ProviderName,
                provider.ProviderType,
                provider.BaseUrl
            })
            .ToListAsync(cancellationToken);

        return providers.Select(provider => new LoadBalancerEndpointDto
        {
            Id = provider.Id,
            Name = provider.ProviderName,
            Type = provider.ProviderType.ToString(),
            Url = provider.BaseUrl ?? $"https://api.{provider.ProviderType.ToString().ToLower()}.com",
            Weight = 1
        }).ToList();
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Admin EF queries are outside the supported NativeAOT data-plane contract (ADR 0006). Owner: database/runtime. Upstream: dotnet/efcore#29754. Remove when this query uses a fixed-shape native store or EF expression construction is trim-safe.")]
    private static async Task<RoutingStatisticsDto> GetRoutingStatistics(
        ConduitDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var oneDayAgo = DateTime.UtcNow.AddDays(-1);
        var stats = await dbContext.RequestLogs
            .Where(request => request.Timestamp >= oneDayAgo)
            .GroupBy(request => request.ModelName)
            .Select(group => new ProviderDistributionDto
            {
                Provider = group.Key,
                RequestCount = group.Count(),
                SuccessRate = group.Count(request => request.StatusCode < 400) * 100.0 / group.Count(),
                AvgLatency = group.Average(request => request.ResponseTimeMs)
            })
            .ToListAsync(cancellationToken);

        return new RoutingStatisticsDto
        {
            TotalRequests = stats.Sum(stat => stat.RequestCount),
            ProviderDistribution = stats
        };
    }
}
