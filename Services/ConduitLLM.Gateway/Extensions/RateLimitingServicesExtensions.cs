using ConduitLLM.Configuration.Options;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.RateLimiting;

using Microsoft.Extensions.Options;

using StackExchange.Redis;

namespace ConduitLLM.Gateway.Extensions;

/// <summary>
/// Registers the rate-limiting primitives shared by the middleware and the endpoint filters.
/// </summary>
public static class RateLimitingServicesExtensions
{
    public static IServiceCollection AddConduitRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.SectionName));
        services.PostConfigure<RateLimitOptions>(ApplyEnvironmentOverrides);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<RateLimitOptions>>().Value);
        services.AddSingleton<IRateLimitFailurePolicy, RateLimitFailurePolicy>();

        // Token counting uses scoped model capabilities and repositories. Keep the
        // endpoint filter in the request scope with that graph.
        services.AddScoped<RequestTokenEstimator>();
        services.AddScoped<TokenRateLimitFilter>();

        // The normal Gateway runtime requires Redis before calling this extension. Isolated
        // endpoint tests can compose these primitives without infrastructure; their unlimited
        // implementations do not make a Redis-free Gateway deployment supported.
        var redisConnectionString = ConduitLLM.Configuration.Utilities.RedisUrlParser.ResolveConnectionString();
        if (string.IsNullOrEmpty(redisConnectionString))
        {
            services.AddSingleton<ITokenRateLimitService, UnlimitedTokenRateLimitService>();
            services.AddSingleton<IConcurrencyRateLimitService, UnlimitedConcurrencyRateLimitService>();
            return services;
        }

        // The window primitive is shared by every scope (key, group, per-model) so they can be
        // evaluated together in one atomic call.
        services.AddSingleton<ISlidingWindowRateLimiter>(sp => new SlidingWindowRateLimiter(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            sp.GetRequiredService<ILogger<SlidingWindowRateLimiter>>()));

        services.AddSingleton<ITokenRateLimitService, TokenRateLimitService>();
        services.AddSingleton<IConcurrencyRateLimitService, ConcurrencyRateLimitService>();

        return services;
    }

    /// <summary>
    /// Honours CONDUIT_RATE_LIMIT_FAILURE_MODE and CONDUIT_RATE_LIMIT_SATURATION_THRESHOLD,
    /// the documented names for these switches.
    /// </summary>
    /// <remarks>
    /// An unrecognised value keeps the default rather than failing boot: a typo in an
    /// operational switch should not stop the gateway starting, but it must not silently be
    /// read as the stricter setting either.
    /// </remarks>
    private static void ApplyEnvironmentOverrides(RateLimitOptions options)
    {
        var configuredMode = Environment.GetEnvironmentVariable("CONDUIT_RATE_LIMIT_FAILURE_MODE");
        if (!string.IsNullOrWhiteSpace(configuredMode) &&
            Enum.TryParse<RateLimitFailureMode>(configuredMode.Trim(), ignoreCase: true, out var mode))
        {
            options.FailureMode = mode;
        }

        var configuredThreshold = Environment.GetEnvironmentVariable("CONDUIT_RATE_LIMIT_SATURATION_THRESHOLD");
        if (!string.IsNullOrWhiteSpace(configuredThreshold) &&
            double.TryParse(configuredThreshold.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var threshold) &&
            threshold > 0 && threshold <= 1)
        {
            options.PrioritySaturationThreshold = threshold;
        }
    }
}
