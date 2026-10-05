using System.Diagnostics;
using System.Text.Json;
using ConduitLLM.Configuration;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Configuration.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Gateway.Services
{
    /// <summary>
    /// Hosted service that warms the discovery cache on application startup
    /// </summary>
    public class DiscoveryCacheWarmingService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IDiscoveryCacheService _discoveryCacheService;
        private readonly DiscoveryCacheOptions _options;
        private readonly JsonSerializerOptions _wireJsonOptions;
        private readonly ILogger<DiscoveryCacheWarmingService> _logger;

        public DiscoveryCacheWarmingService(
            IServiceProvider serviceProvider,
            IDiscoveryCacheService discoveryCacheService,
            IOptions<DiscoveryCacheOptions> options,
            JsonSerializerOptions wireJsonOptions,
            ILogger<DiscoveryCacheWarmingService> logger)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _discoveryCacheService = discoveryCacheService ?? throw new ArgumentNullException(nameof(discoveryCacheService));
            _options = options.Value ?? throw new ArgumentNullException(nameof(options));
            _wireJsonOptions = wireJsonOptions ?? throw new ArgumentNullException(nameof(wireJsonOptions));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.WarmCacheOnStartup || !_options.EnableCaching)
            {
                _logger.LogInformation("Discovery cache warming is disabled");
                return;
            }

            // Wait for the application to fully start using configurable delay
            var startupDelay = TimeSpan.FromSeconds(_options.WarmupStartupDelaySeconds);
            _logger.LogDebug("Waiting {Seconds} seconds before starting cache warming", _options.WarmupStartupDelaySeconds);
            await Task.Delay(startupDelay, stoppingToken);

            if (!_options.UseDistributedLockForWarming)
            {
                await WarmCachesAsync(stoppingToken);
                return;
            }

            using var lockScope = _serviceProvider.CreateScope();
            var lockService = lockScope.ServiceProvider.GetService<IDistributedLockService>();
            _logger.LogDebug("Attempting to acquire distributed lock for cache warming");

            var result = await lockService.RunWithOptionalLockAsync(
                "discovery:cache:warming",
                TimeSpan.FromMinutes(5),
                TimeSpan.FromSeconds(_options.DistributedLockTimeoutSeconds),
                TimeSpan.FromSeconds(1),
                async lockAcquired =>
                {
                    if (lockAcquired)
                    {
                        _logger.LogDebug("Acquired distributed lock for cache warming");
                    }

                    await WarmCachesAsync(stoppingToken);
                    return true;
                },
                _logger,
                stoppingToken,
                skipOnTimeout: true);

            if (!result.Executed)
            {
                _logger.LogInformation("Another instance is performing cache warming, skipping");
            }
        }

        private async Task WarmCachesAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("Starting discovery cache warming");
                var stopwatch = Stopwatch.StartNew();

                using var scope = _serviceProvider.CreateScope();
                var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConduitDbContext>>();

                // Warm cache for common capability filters
                var commonCapabilities = _options.WarmupCapabilities ?? new List<string> 
                { 
                    "chat", "image_input", "video_input", "audio_input", "file_input",
                    "image_generation", "video_generation"
                };

                // First, warm the cache with all models (no filter)
                await WarmCacheForCapability(dbContextFactory, null, stoppingToken);

                // Then warm cache for each common capability
                foreach (var capability in commonCapabilities)
                {
                    if (stoppingToken.IsCancellationRequested)
                        break;

                    await WarmCacheForCapability(dbContextFactory, capability, stoppingToken);
                    
                    // Small delay between cache warming operations
                    await Task.Delay(100, stoppingToken);
                }

                stopwatch.Stop();
                _logger.LogInformation(
                    "Discovery cache warming completed in {ElapsedMs}ms. Warmed {Count} cache entries",
                    stopwatch.ElapsedMilliseconds,
                    commonCapabilities.Count + 1); // +1 for the "all" entry
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Cache warming cancelled due to application shutdown");
            }
            catch (Exception ex)
            {
                // Log error but don't throw - we don't want cache warming failures to prevent startup
                _logger.LogError(ex, "Error during discovery cache warming - application will continue without warmed cache");
            }
        }

        internal async Task WarmCacheForCapability(
            IDbContextFactory<ConduitDbContext> dbContextFactory,
            string? capability,
            CancellationToken cancellationToken)
        {
            try
            {
                var cacheKey = DiscoveryCacheService.BuildCacheKey(
                    capability,
                    includePricing: _options.ExposePricing);
                var result = await _discoveryCacheService.GetOrLoadAsync(cacheKey, token =>
                    DiscoveryCacheLoader.LoadAsync(dbContextFactory, capability, _options.ExposePricing,
                        _wireJsonOptions, _logger, token), cancellationToken);
                
                _logger.LogInformation(
                    "Warmed discovery cache for capability '{Capability}' with {Count} models",
                    capability ?? "all",
                    result.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error warming cache for capability: {Capability}", capability ?? "all");
            }
        }
    }
}
