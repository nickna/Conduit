using Microsoft.Extensions.Configuration;
using ZiggyCreatures.Caching.Fusion;

namespace ConduitLLM.Core.Caching;

public enum ApplicationCacheImplementation { Legacy, FusionCache }
public enum ApplicationCacheDomain { Discovery, Functions, Mappings, Costs, PricingRules }

/// <summary>Explicit application cache policies; independent of auth, tasks and other Redis stores.</summary>
public sealed class ApplicationCacheOptions
{
    public const string ServiceKey = "conduit-application-cache";
    public string Environment { get; init; } = "development";
    public string Prefix => $"conduit:app-cache:{Environment}:v1:";
    public TimeSpan LocalDuration { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromDays(7);
    public ApplicationCacheImplementation Discovery { get; init; }
    public ApplicationCacheImplementation Functions { get; init; }
    public ApplicationCacheImplementation Mappings { get; init; }
    public ApplicationCacheImplementation Costs { get; init; }
    public ApplicationCacheImplementation PricingRules { get; init; }

    public bool UsesFusionCache(ApplicationCacheDomain domain) => (domain switch
    {
        ApplicationCacheDomain.Discovery => Discovery,
        ApplicationCacheDomain.Functions => Functions,
        ApplicationCacheDomain.Mappings => Mappings,
        ApplicationCacheDomain.Costs => Costs,
        ApplicationCacheDomain.PricingRules => PricingRules,
        _ => throw new ArgumentOutOfRangeException(nameof(domain))
    }) == ApplicationCacheImplementation.FusionCache;

    public static string Tag(ApplicationCacheDomain domain) => domain switch
    {
        ApplicationCacheDomain.Discovery => "discovery",
        ApplicationCacheDomain.Functions => "functions",
        ApplicationCacheDomain.Mappings => "mappings",
        ApplicationCacheDomain.Costs => "costs",
        ApplicationCacheDomain.PricingRules => "rules",
        _ => throw new ArgumentOutOfRangeException(nameof(domain))
    };

    public FusionCacheEntryOptions Entry(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > MaximumDuration)
            throw new ArgumentOutOfRangeException(nameof(duration), "Cache duration must be positive and within ApplicationCache:MaximumDuration.");
        return new FusionCacheEntryOptions
        {
            Duration = duration < LocalDuration ? duration : LocalDuration,
            DistributedCacheDuration = duration,
            IsFailSafeEnabled = false,
            EnableAutoClone = true,
            EagerRefreshThreshold = null,
            AllowTimedOutFactoryBackgroundCompletion = false,
            AllowBackgroundDistributedCacheOperations = false,
            AllowBackgroundBackplaneOperations = false,
            ReThrowSerializationExceptions = true,
            ReThrowDistributedCacheExceptions = true,
            ReThrowBackplaneExceptions = true
        };
    }

    public FusionCacheOptions FusionOptions() => new()
    {
        CacheName = ServiceKey,
        CacheKeyPrefix = Prefix,
        BackplaneChannelPrefix = Prefix,
        WaitForInitialBackplaneSubscribe = true,
        EnableAutoRecovery = false,
        DistributedCacheCircuitBreakerDuration = TimeSpan.Zero,
        BackplaneCircuitBreakerDuration = TimeSpan.Zero,
        DefaultEntryOptions = Entry(TimeSpan.FromMinutes(15)),
        TagsDefaultEntryOptions = new FusionCacheEntryOptions
        {
            Duration = TimeSpan.FromSeconds(1),
            DistributedCacheDuration = MaximumDuration + TimeSpan.FromDays(1),
            IsFailSafeEnabled = false,
            AllowTimedOutFactoryBackgroundCompletion = false,
            AllowBackgroundDistributedCacheOperations = false,
            AllowBackgroundBackplaneOperations = false,
            ReThrowSerializationExceptions = true,
            ReThrowDistributedCacheExceptions = true,
            ReThrowBackplaneExceptions = true
        }
    };

    internal static ApplicationCacheOptions Read(IConfiguration configuration, string hostEnvironment)
    {
        var section = configuration.GetSection("ApplicationCache");
        var environment = (section["Environment"] ?? hostEnvironment).ToLowerInvariant();
        if (environment.Length is < 1 or > 64 || environment.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidOperationException("ApplicationCache:Environment must have 1–64 ASCII letters, digits, hyphens or underscores.");
        var result = new ApplicationCacheOptions
        {
            Environment = environment,
            LocalDuration = section.GetValue("LocalDuration", TimeSpan.FromSeconds(5)),
            MaximumDuration = section.GetValue("MaximumDuration", TimeSpan.FromDays(7)),
            Discovery = section.GetValue<ApplicationCacheImplementation>("Implementations:Discovery"),
            Functions = section.GetValue<ApplicationCacheImplementation>("Implementations:Functions"),
            Mappings = section.GetValue<ApplicationCacheImplementation>("Implementations:Mappings"),
            Costs = section.GetValue<ApplicationCacheImplementation>("Implementations:Costs"),
            PricingRules = section.GetValue<ApplicationCacheImplementation>("Implementations:PricingRules")
        };
        if (result.LocalDuration <= TimeSpan.Zero || result.LocalDuration > TimeSpan.FromSeconds(5) ||
            result.MaximumDuration < TimeSpan.FromHours(12) || result.MaximumDuration > TimeSpan.FromDays(30))
            throw new InvalidOperationException("Application cache L1 duration must be >0 and <=5s, and maximum L2 duration must be 12h–30d.");
        if (!Enum.IsDefined(result.Discovery) || !Enum.IsDefined(result.Functions) || !Enum.IsDefined(result.Mappings) ||
            !Enum.IsDefined(result.Costs) || !Enum.IsDefined(result.PricingRules))
            throw new InvalidOperationException("Application cache implementation must be Legacy or FusionCache.");
        return result;
    }
}
