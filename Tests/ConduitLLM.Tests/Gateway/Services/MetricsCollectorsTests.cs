using System.Text;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Tests.TestInfrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Moq;

namespace ConduitLLM.Tests.Gateway.Services;

public sealed class MetricsCollectorsTests : IDisposable
{
    private readonly List<SqliteTestDatabase> _databases = [];

    [Fact]
    public async Task TaskMetrics_GroupActiveTasksAndRemoveStaleLabels()
    {
        var databaseName = $"task-metrics-{Guid.NewGuid()}";
        var options = CreateOptions(databaseName);
        var taskType = $"issue_1076_{Guid.NewGuid():N}";
        await using (var context = new ConduitDbContext(options))
        {
            SeedVirtualKey(context);
            context.AsyncTasks.AddRange(
                CreateTask("pending-1", taskType, state: 0, DateTime.UtcNow.AddSeconds(-30)),
                CreateTask("pending-2", taskType, state: 0, DateTime.UtcNow.AddSeconds(-10)),
                CreateTask("processing", taskType, state: 1, DateTime.UtcNow.AddSeconds(-20)),
                CreateTask("completed", taskType, state: 2, DateTime.UtcNow.AddMinutes(-1)),
                CreateTask("archived", taskType, state: 0, DateTime.UtcNow.AddMinutes(-2), isArchived: true));
            await context.SaveChangesAsync();
        }

        await using var provider = CreateServices(options).BuildServiceProvider();
        var service = new TaskProcessingMetricsService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<TaskProcessingMetricsService>>());
        using var scope = provider.CreateScope();

        await service.CollectAsyncTaskMetrics(scope);

        var exposition = await ExportMetricsAsync();
        Assert.Contains(
            $"conduit_tasks_queue_depth{{task_type=\"{taskType}\",status=\"pending\"}} 2",
            exposition);
        Assert.Contains(
            $"conduit_tasks_queue_depth{{task_type=\"{taskType}\",status=\"processing\"}} 1",
            exposition);
        Assert.Contains($"conduit_task_wait_time_seconds{{task_type=\"{taskType}\"}}", exposition);

        await using (var context = new ConduitDbContext(options))
        {
            context.AsyncTasks.RemoveRange(context.AsyncTasks.Where(task => task.Type == taskType));
            await context.SaveChangesAsync();
        }

        await service.CollectAsyncTaskMetrics(scope);

        exposition = await ExportMetricsAsync();
        Assert.DoesNotContain($"conduit_tasks_queue_depth{{task_type=\"{taskType}\"", exposition);
        Assert.DoesNotContain($"conduit_task_wait_time_seconds{{task_type=\"{taskType}\"}}", exposition);
    }

    [Fact]
    public async Task ActiveModelMetrics_ExcludeDisabledMappingsAndProvidersAndRemoveStaleLabels()
    {
        var options = CreateOptions($"active-model-metrics-{Guid.NewGuid()}");
        const int enabledProviderId = 1076001;
        const int disabledProviderId = 1076002;
        await using (var context = new ConduitDbContext(options))
        {
            var enabledProvider = new Provider
            {
                Id = enabledProviderId,
                ProviderName = "Issue 1076 enabled",
                ProviderType = ProviderType.OpenAI,
                IsEnabled = true
            };
            var disabledProvider = new Provider
            {
                Id = disabledProviderId,
                ProviderName = "Issue 1076 disabled",
                ProviderType = ProviderType.Groq,
                IsEnabled = false
            };
            context.Providers.AddRange(enabledProvider, disabledProvider);
            for (var id = 1076011; id <= 1076013; id++)
            {
                var author = new ModelAuthor
                {
                    Id = id,
                    Name = $"issue-1076-author-{id}"
                };
                var series = new ModelSeries
                {
                    Id = id,
                    Author = author,
                    Name = $"issue-1076-series-{id}",
                    Parameters = "{}"
                };
                context.Models.Add(new Model
                {
                    Id = id,
                    Name = $"issue-1076-model-{id}",
                    Series = series
                });
                context.ModelProviderTypeAssociations.Add(new ModelProviderTypeAssociation
                {
                    Id = id,
                    ModelId = id,
                    Identifier = $"issue-1076-model-{id}",
                    Provider = ProviderType.OpenAI,
                    IsEnabled = true
                });
            }
            context.ModelProviderMappings.AddRange(
                CreateMapping(1076011, enabledProvider, isEnabled: true),
                CreateMapping(1076012, enabledProvider, isEnabled: false),
                CreateMapping(1076013, disabledProvider, isEnabled: true));
            await context.SaveChangesAsync();
        }

        await using var provider = CreateServices(options).BuildServiceProvider();
        var service = new BusinessMetricsService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<BusinessMetricsService>>());
        using var scope = provider.CreateScope();

        await service.CollectActiveEntityMetrics(scope);

        var exposition = await ExportMetricsAsync();
        Assert.Contains($"conduit_models_active_count{{provider=\"{enabledProviderId}\"}} 1", exposition);
        Assert.DoesNotContain($"conduit_models_active_count{{provider=\"{disabledProviderId}\"}}", exposition);

        await using (var context = new ConduitDbContext(options))
        {
            var mapping = await context.ModelProviderMappings.SingleAsync(row => row.Id == 1076011);
            mapping.IsEnabled = false;
            await context.SaveChangesAsync();
        }

        await service.CollectActiveEntityMetrics(scope);

        exposition = await ExportMetricsAsync();
        Assert.DoesNotContain($"conduit_models_active_count{{provider=\"{enabledProviderId}\"}}", exposition);
    }

    [Fact]
    public async Task CollectorFailures_AreExported()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var service = new BusinessMetricsService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<BusinessMetricsService>>());
        using var scope = provider.CreateScope();

        await service.CollectActiveEntityMetrics(scope);

        var exposition = await ExportMetricsAsync();
        Assert.Contains(
            "conduit_metrics_collection_failures_total{collector=\"business/active_entities\"}",
            exposition);
    }

    private DbContextOptions<ConduitDbContext> CreateOptions(string databaseName)
    {
        var database = new SqliteTestDatabase();
        _databases.Add(database);
        return database.Options;
    }

    private static void SeedVirtualKey(ConduitDbContext context)
    {
        context.VirtualKeyGroups.Add(new VirtualKeyGroup
        {
            Id = 1,
            GroupName = "Metrics test group"
        });
        context.VirtualKeys.Add(new VirtualKey
        {
            Id = 1,
            VirtualKeyGroupId = 1,
            KeyName = "Metrics test key",
            KeyHash = $"metrics-{Guid.NewGuid():N}",
            IsEnabled = true
        });
    }

    private static ServiceCollection CreateServices(DbContextOptions<ConduitDbContext> options)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<ConduitDbContext>>(new TestDbContextFactory(options));
        services.AddScoped<IGatewayMetricsStore, EfGatewayMetricsStore>();
        return services;
    }

    private static ConduitLLM.Configuration.Entities.AsyncTask CreateTask(
        string id,
        string type,
        int state,
        DateTime createdAt,
        bool isArchived = false) => new()
        {
            Id = $"{type}-{id}",
            Type = type,
            State = state,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            VirtualKeyId = 1,
            IsArchived = isArchived
        };

    private static ModelProviderMapping CreateMapping(int id, Provider provider, bool isEnabled) => new()
    {
        Id = id,
        ModelAlias = $"model-{id}",
        ProviderModelId = $"provider-model-{id}",
        ProviderId = provider.Id,
        Provider = provider,
        ModelProviderTypeAssociationId = id,
        IsEnabled = isEnabled
    };

    private static RequestLog CreateRequestLog(
        string model,
        string provider,
        decimal cost,
        int inputTokens,
        int outputTokens,
        double responseTimeMs,
        int statusCode,
        DateTime timestamp) => new()
        {
            VirtualKeyId = 1,
            ModelName = model,
            ProviderType = provider,
            RequestType = "chat",
            Cost = cost,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ResponseTimeMs = responseTimeMs,
            StatusCode = statusCode,
            Timestamp = timestamp,
            BilledAtUtc = timestamp
        };

    private static async Task<string> ExportMetricsAsync()
    {
        await using var stream = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream, default);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class TestDbContextFactory(DbContextOptions<ConduitDbContext> options)
        : IDbContextFactory<ConduitDbContext>
    {
        public ConduitDbContext CreateDbContext() => new(options);

        public Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    public void Dispose()
    {
        foreach (var database in _databases)
        {
            database.Dispose();
        }
    }
}
