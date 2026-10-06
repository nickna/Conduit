using System.Diagnostics;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Messaging;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Metrics;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Core.Validation;
using ConduitLLM.Messaging.Wolverine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Runtime;

namespace ConduitLLM.IntegrationTests.Infrastructure;

[CollectionDefinition("Durable media dispatch")]
public sealed class MediaDispatchCollection : ICollectionFixture<MediaDispatchFixture>;

public sealed class MediaDispatchFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").WithDatabase("media_dispatch")
        .WithUsername("conduit").WithPassword("conduitpass").Build();
    public string ConnectionString => _postgres.GetConnectionString();
    public Mock<ILLMClient> Provider { get; } = new();
    public Mock<IDistributedCache> Cache { get; } = new();
    public int HandledImages;
    public Func<Task>? BeforeKeyValidation { get; set; }
    public IBatchSpendUpdateService? Reservations { get; set; }

    public ConduitDbContext Db() => new(new DbContextOptionsBuilder<ConduitDbContext>()
        .UseNpgsql(ConnectionString).Options);
    public IAsyncTaskRepository Repository() => new AsyncTaskRepository(new Factory(this), NullLogger<AsyncTaskRepository>.Instance);
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var db = Db();
        await db.Database.MigrateAsync();
        db.VirtualKeyGroups.Add(new VirtualKeyGroup { Id = 1, GroupName = "media-test", Balance = 100 });
        db.VirtualKeys.Add(new VirtualKey { Id = 1, KeyName = "media-test", KeyHash = "test-hash", VirtualKeyGroupId = 1 });
        await db.SaveChangesAsync();
        using var host = Host(worker: false);
        await host.StartAsync();
        await host.StopAsync();
    }
    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    public IHost Host(bool worker, bool notificationsFail = false)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WolverineMessagingExtensions.SchemaNameKey] = "wolverine_media_test",
            [WolverineMessagingExtensions.AutoProvisionKey] = "true"
        }).Build();
        return Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(log => log.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddWolverineEventBus();
                services.AddMetrics();
                if (notificationsFail)
                {
                    var failedBus = new Mock<IEventBus>();
                    failedBus.Setup(bus => bus.PublishAsync(It.IsAny<AsyncTaskCreated>(), It.IsAny<CancellationToken>()))
                        .ThrowsAsync(new IOException("Optional notification unavailable"));
                    services.AddSingleton(failedBus.Object);
                }
                services.AddScoped<IMediaTaskSubmission, MediaTaskSubmission>();
                services.AddSingleton(Cache.Object);
                services.AddScoped<IMediaTaskRecovery, MediaTaskRecovery>();
                if (worker)
                {
                    AddOrchestratorDependencies(services);
                    services.AddScoped<IAsyncTaskService>(sp => new HybridAsyncTaskService(
                        Repository(), Cache.Object, sp.GetRequiredService<IEventBus>(), NullLogger<HybridAsyncTaskService>.Instance));
                    services.AddScoped<ImageGenerationOrchestrator>();
                    services.AddScoped<IEventHandler<ImageGenerationRequested>>(sp => new ObservedImages(sp.GetRequiredService<ImageGenerationOrchestrator>(), this));
                    services.AddScoped<IEventHandler<VideoGenerationRequested>, VideoGenerationOrchestrator>();
                    services.AddScoped<IEventHandler<SpendUpdateRequested>>(_ => new DebitHandler(this));
                }
            })
            .AddConduitWolverine(config, ConnectionString, "media-dispatch-test", options =>
            {
                options.ApplicationAssembly = typeof(ConduitLLM.Gateway.Endpoints.ImagesEndpoints).Assembly;
                options.ApplyConduitPublishRouting();
                if (worker)
                {
                    options.AddEventBridge<ImageGenerationRequested>();
                    options.AddEventBridge<VideoGenerationRequested>();
                    options.AddEventBridge<SpendUpdateRequested>();
                    options.ListenToPostgresqlQueue("image-generation-events").PollingInterval(TimeSpan.FromMilliseconds(50));
                    options.ListenToPostgresqlQueue("video-generation-events").PollingInterval(TimeSpan.FromMilliseconds(50));
                    options.ListenToPostgresqlQueue("spend-update-events").PollingInterval(TimeSpan.FromMilliseconds(50));
                }
            }).Build();
    }

    private void AddOrchestratorDependencies(IServiceCollection services)
    {
        var factory = new Mock<ILLMClientFactory>();
        factory.Setup(f => f.GetClientByProviderIdAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Provider.Object);
        var mapping = new Mock<IModelProviderMappingService>();
        mapping.Setup(m => m.GetMappingByModelAliasAsync(It.IsAny<string>())).ReturnsAsync(new ModelProviderMapping
        {
            Id = 1, ProviderId = 1, ModelAlias = "test-model", ProviderModelId = "provider-model", IsEnabled = true,
            Provider = new Provider { Id = 1, ProviderName = "Test", ProviderType = ProviderType.OpenAI, IsEnabled = true }
        });
        var key = new Mock<ConduitLLM.Core.Interfaces.IVirtualKeyService>();
        key.Setup(k => k.ValidateVirtualKeyAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(async () =>
            {
                if (BeforeKeyValidation != null) await BeforeKeyValidation();
                return VirtualKeyValidationOutcome.Success(new VirtualKey { Id = 1, VirtualKeyGroupId = 1, IsEnabled = true });
            });
        var cost = new Mock<ICostCalculationService>();
        cost.Setup(c => c.CalculateCostAsync(It.IsAny<string>(), It.IsAny<Usage>(), It.IsAny<CancellationToken>())).ReturnsAsync(0.01m);
        services.AddSingleton(factory.Object);
        services.AddSingleton(mapping.Object);
        services.AddSingleton(key.Object);
        services.AddSingleton(cost.Object);
        if (Reservations != null) services.AddSingleton(Reservations);
        services.AddSingleton(Mock.Of<IMediaStorageService>());
        services.AddSingleton<ICancellableTaskRegistry, CancellableTaskRegistry>();
        services.AddSingleton(Mock.Of<IWebhookNotificationService>());
        services.AddSingleton(Mock.Of<IHttpClientFactory>());
        services.AddSingleton(Mock.Of<IProviderErrorTrackingService>());
        services.AddSingleton(new MinimalParameterValidator(NullLogger<MinimalParameterValidator>.Instance));
        services.AddSingleton<MediaGenerationMetrics>();
        services.AddSingleton(Options.Create(new VideoGenerationRetryConfiguration()));
    }

    public async Task ResetAsync()
    {
        Provider.Reset(); Cache.Reset();
        HandledImages = 0; BeforeKeyValidation = null; Reservations = null;
        Provider.As<IVideoGenerationClient>().Setup(p => p.CreateVideoAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoGenerationResponse { Created = 1, Data = [] });
        Provider.Setup(p => p.CreateImageAsync(It.IsAny<ImageGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageGenerationResponse { Created = 1, Data = [] });
        await SqlAsync("TRUNCATE TABLE \"AsyncTasks\", \"VirtualKeyGroupTransactions\"; UPDATE \"VirtualKeyGroups\" SET \"Balance\" = 100");
        // Every host/subprocess from the preceding case has stopped. Isolate the
        // disposable test database's transport queues from historical deliveries.
        await SqlAsync("""
            TRUNCATE TABLE wolverine_media_test.wolverine_incoming_envelopes, wolverine_media_test.wolverine_outgoing_envelopes;
            DO $$ DECLARE row record; BEGIN
            FOR row IN SELECT tablename FROM pg_tables WHERE schemaname = 'wolverine_transport' LOOP
              EXECUTE format('TRUNCATE TABLE wolverine_transport.%I CASCADE', row.tablename);
            END LOOP; END $$;
            """);
    }

    public async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<(Process Process, string Id)> StartPublisherAsync(string type)
    {
        var process = StartProbe(type);
        var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (line?.StartsWith("ACCEPTED:", StringComparison.Ordinal) != true)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            var error = await process.StandardError.ReadToEndAsync();
            process.Dispose();
            throw new InvalidOperationException($"Publisher failed: {line} {error}");
        }
        return (process, line[9..]);
    }

    public Process StartProbe(params string[] args)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Conduit.slnx"))) root = root.Parent;
        var configuration = AppContext.BaseDirectory.Contains("Release", StringComparison.Ordinal) ? "Release" : "Debug";
        var dll = Path.Combine(root!.FullName, "tools", "ConduitLLM.MediaDispatchProbe", "bin", configuration, "net10.0", "ConduitLLM.MediaDispatchProbe.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(dll);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["CONDUIT_MEDIA_TEST_POSTGRES"] = $"{ConnectionString};Application Name=media-dispatch-probe";
        return Process.Start(start)!;
    }

    public static async Task EventuallyAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (!await predicate()) await Task.Delay(100, timeout.Token);
    }

    private sealed class Factory(MediaDispatchFixture fixture) : IDbContextFactory<ConduitDbContext>
    {
        public ConduitDbContext CreateDbContext() => fixture.Db();
    }
    private sealed class ObservedImages(ImageGenerationOrchestrator inner, MediaDispatchFixture fixture) : IEventHandler<ImageGenerationRequested>
    {
        public async Task HandleAsync(ImageGenerationRequested request, IEventContext context)
        {
            await inner.HandleAsync(request, context);
            Interlocked.Increment(ref fixture.HandledImages);
        }
    }
    private sealed class DebitHandler(MediaDispatchFixture fixture) : IEventHandler<SpendUpdateRequested>
    {
        public async Task HandleAsync(SpendUpdateRequested request, IEventContext context)
        {
            var repository = new VirtualKeyGroupRepository(new Factory(fixture), NullLogger<VirtualKeyGroupRepository>.Instance);
            await repository.AdjustBalanceIdempotentAsync(1, -request.Amount, $"spend:{request.RequestId}",
                "Media dispatch integration test", "test", ReferenceType.VirtualKey, "1");
        }
    }
}
