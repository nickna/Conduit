using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Audio;
using ConduitLLM.Core.Services;
using ConduitLLM.Security.Interfaces;
using ConduitLLM.Security.Models;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Moq;
using Testcontainers.Redis;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("OpenAiSdkSmoke")]
public sealed class OpenAiSdkSmokeTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] ExpectedOperations =
    [
        "models", "chat", "responses", "embeddings", "images", "speech"
    ];

    [Fact]
    [Trait("Component", "OpenAiSdkSmoke")]
    public async Task Official_sdks_complete_against_the_real_gateway()
    {
        var redisConnectionString = Environment.GetEnvironmentVariable("TEST_REDIS_CONNECTION");
        await using var redis = string.IsNullOrWhiteSpace(redisConnectionString)
            ? new RedisBuilder().WithImage("redis:7.4-alpine").Build()
            : null;
        if (redis is not null)
        {
            await redis.StartAsync();
            redisConnectionString = redis.GetConnectionString();
        }

        using var environment = new EnvironmentScope(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Test",
            ["CONDUIT_MIGRATION_MODE"] = "Skip",
            ["ConduitLLM__Messaging__Backend"] = "Wolverine",
            ["ConduitLLM__Messaging__Wolverine__Transport"] = "InMemory",
            ["BillingAdmission__Mode"] = "Off",
            ["UsageTracking__GracefulShutdownSeconds"] = "5",
            ["Logging__EventLog__LogLevel__Default"] = "None",
            ["DATABASE_URL"] = "postgresql://smoke:smoke@127.0.0.1:1/conduit_sdk_smoke",
            ["REDIS_URL"] = null,
            ["CONDUIT_REDIS_CONNECTION_STRING"] = redisConnectionString
        });

        var counters = new GatewayInvocationCounters();
        var factory = new SmokeGatewayFactory(counters);
        try
        {
            factory.UseKestrel(0);
            using var client = factory.CreateClient();
            var baseUrl = new Uri(client.BaseAddress!, "/v1").AbsoluteUri.TrimEnd('/');
            var smokeDirectory = Path.Combine(FindRepositoryRoot(), "tools", "openapi", "sdk-smoke");

            await RunSdkAsync(
                "Node",
                Environment.GetEnvironmentVariable("CONDUIT_SMOKE_NODE") ?? "node",
                "node-smoke.mjs",
                smokeDirectory,
                baseUrl);
            AssertOperations(counters, "Node");

            counters.Reset();
            await RunSdkAsync(
                "Python",
                Environment.GetEnvironmentVariable("CONDUIT_SMOKE_PYTHON") ?? "python",
                "python-smoke.py",
                smokeDirectory,
                baseUrl);
            AssertOperations(counters, "Python");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    private static async Task RunSdkAsync(
        string sdk,
        string executable,
        string script,
        string workingDirectory,
        string baseUrl)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(script);
        startInfo.Environment["CONDUIT_SMOKE_BASE_URL"] = baseUrl;
        startInfo.Environment["CONDUIT_SMOKE_API_KEY"] = "smoke-key";
        startInfo.Environment["CONDUIT_SMOKE_CHAT_MODEL"] = "chat-model";
        startInfo.Environment["CONDUIT_SMOKE_EMBEDDING_MODEL"] = "embedding-model";
        startInfo.Environment["CONDUIT_SMOKE_IMAGE_MODEL"] = "image-model";
        startInfo.Environment["CONDUIT_SMOKE_SPEECH_MODEL"] = "speech-model";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                Assert.Fail($"{sdk} SDK smoke process could not be started: {executable} {script}");
            }
        }
        catch (Exception exception)
        {
            Assert.Fail($"{sdk} SDK smoke process could not be started: {executable} {script}{Environment.NewLine}{exception}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            await process.WaitForExitAsync();
            Assert.Fail(BuildProcessFailure(
                sdk,
                $"timed out after {ProcessTimeout.TotalSeconds:0} seconds",
                await stdoutTask,
                await stderrTask));
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            Assert.Fail(BuildProcessFailure(sdk, $"exited with code {process.ExitCode}", stdout, stderr));
        }
    }

    private static void AssertOperations(GatewayInvocationCounters counters, string sdk)
    {
        var missing = ExpectedOperations.Where(operation => counters[operation] == 0).ToArray();
        Assert.True(
            missing.Length == 0,
            $"{sdk} SDK did not reach the real Gateway operations: {string.Join(", ", missing)}. " +
            $"Observed: {counters.Describe()}");
    }

    private static string BuildProcessFailure(string sdk, string reason, string stdout, string stderr) =>
        $"{sdk} SDK smoke {reason}.{Environment.NewLine}" +
        $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}" +
        $"stderr:{Environment.NewLine}{stderr}";

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tools", "openapi", "sdk-smoke")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Services", "ConduitLLM.Gateway")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repository root from {AppContext.BaseDirectory}.");
    }
}

internal sealed class SmokeGatewayFactory(GatewayInvocationCounters counters) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
        });
        builder.ConfigureTestServices(services => ConfigureSmokeServices(services, counters));
    }

    private static void ConfigureSmokeServices(IServiceCollection services, GatewayInvocationCounters counters)
    {
        services.RemoveAll<IHostedService>();
        services.AddHealthChecks();

        var key = new VirtualKey
        {
            Id = 1,
            KeyName = "SDK smoke",
            KeyHash = "sdk-smoke-key-hash",
            IsEnabled = true,
            VirtualKeyGroupId = 1,
            VirtualKeyGroup = new VirtualKeyGroup { Id = 1, GroupName = "SDK smoke", Balance = 1_000_000m }
        };
        var virtualKeys = new Mock<IVirtualKeyService>();
        virtualKeys.Setup(service => service.ValidateVirtualKeyForAuthenticationAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((string candidate, string? _) => ValidateSmokeKey(candidate, key));
        virtualKeys.Setup(service => service.ValidateVirtualKeyAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((string candidate, string? _) => ValidateSmokeKey(candidate, key));
        virtualKeys.Setup(service => service.UpdateSpendAsync(It.IsAny<int>(), It.IsAny<decimal>()))
            .ReturnsAsync(true);
        virtualKeys.Setup(service => service.GetVirtualKeyInfoForValidationAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(key);
        services.RemoveAll<IVirtualKeyService>();
        services.AddSingleton(virtualKeys.Object);
        services.RemoveAll<IVirtualKeyRateLimitService>();
        services.AddSingleton(Mock.Of<IVirtualKeyRateLimitService>());
        services.RemoveAll<IRedisCircuitBreaker>();
        services.AddSingleton(Mock.Of<IRedisCircuitBreaker>());

        var batchSpend = new Mock<IBatchSpendUpdateService>();
        batchSpend.SetupGet(service => service.IsHealthy).Returns(true);
        batchSpend.Setup(service => service.QueueSpendUpdateAsync(
                It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()))
            .Returns(Task.CompletedTask);
        services.RemoveAll<IBatchSpendUpdateService>();
        services.AddSingleton(batchSpend.Object);

        var mappings = CreateMappings();
        var mappingRepository = new Mock<IModelProviderMappingRepository>();
        mappingRepository
            .Setup(repository => repository.GetPaginatedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int page, int _, CancellationToken _) =>
            {
                counters.Increment("models");
                return page == 1 ? (mappings, mappings.Count) : ([], mappings.Count);
            });
        mappingRepository
            .Setup(repository => repository.GetByModelNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string alias, CancellationToken _) =>
            {
                counters.Increment("models");
                return mappings.FirstOrDefault(mapping => mapping.ModelAlias == alias);
            });
        services.RemoveAll<IModelProviderMappingRepository>();
        services.AddSingleton(mappingRepository.Object);

        var mappingService = new Mock<IModelProviderMappingService>();
        mappingService.Setup(service => service.GetMappingByModelAliasAsync(It.IsAny<string>()))
            .ReturnsAsync((string alias) => mappings.FirstOrDefault(mapping => mapping.ModelAlias == alias));
        mappingService.Setup(service => service.GetMappingsByModelAliasAsync(It.IsAny<string>()))
            .ReturnsAsync((string alias) => mappings.Where(mapping => mapping.ModelAlias == alias).ToList());
        mappingService.Setup(service => service.GetMappingByIdAsync(It.IsAny<int>()))
            .ReturnsAsync((int id) => mappings.FirstOrDefault(mapping => mapping.Id == id));
        services.RemoveAll<IModelProviderMappingService>();
        services.AddSingleton(mappingService.Object);

        services.RemoveAll<ILLMClientFactory>();
        services.AddSingleton<ILLMClientFactory>(provider =>
            new SmokeClientFactory(new SmokeProviderClient(
                counters,
                provider.GetRequiredService<IHttpContextAccessor>())));

        var settings = new Mock<IGlobalSettingsCacheService>();
        settings.Setup(service => service.GetMaxAgenticIterationsAsync()).ReturnsAsync(1);
        settings.Setup(service => service.GetMinAgenticIterationsAsync()).ReturnsAsync(1);
        settings.Setup(service => service.GetDefaultAgenticModeEnabledAsync()).ReturnsAsync(false);
        services.RemoveAll<IGlobalSettingsCacheService>();
        services.AddSingleton(settings.Object);

        var usage = new Mock<IUsageEstimationService>();
        usage.Setup(service => service.EstimateUsageFromStreamingResponseAsync(
                It.IsAny<string>(), It.IsAny<List<Message>>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Usage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 });
        usage.Setup(service => service.EstimateUsageFromTextAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Usage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 });
        services.RemoveAll<IUsageEstimationService>();
        services.AddSingleton(usage.Object);

        var costs = new Mock<ICostCalculationService>();
        costs.Setup(service => service.CalculateCostAsync(
                It.IsAny<string>(), It.IsAny<Usage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);
        costs.Setup(service => service.CalculateCostByIdAsync(
                It.IsAny<int>(), It.IsAny<Usage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);
        costs.Setup(service => service.CalculateCacheSavingsAsync(
                It.IsAny<string>(), It.IsAny<Usage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);
        costs.Setup(service => service.CalculateCacheSavingsByIdAsync(
                It.IsAny<int>(), It.IsAny<Usage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);
        costs.Setup(service => service.CalculateCacheWritePremiumAsync(
                It.IsAny<string>(), It.IsAny<Usage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);
        costs.Setup(service => service.CalculateCacheWritePremiumByIdAsync(
                It.IsAny<int>(), It.IsAny<Usage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);
        services.RemoveAll<ICostCalculationService>();
        services.AddSingleton(costs.Object);

        var security = new Mock<ISecurityService>();
        security.Setup(service => service.IsRequestAllowedAsync(It.IsAny<HttpContext>()))
            .ReturnsAsync(SecurityCheckResult.Allowed());
        services.RemoveAll<ISecurityService>();
        services.AddSingleton(security.Object);

        var requestLog = new Mock<IRequestLogService>();
        requestLog.Setup(service => service.LogRequestAsync(It.IsAny<Configuration.DTOs.LogRequestDto>()))
            .Returns(Task.CompletedTask);
        services.RemoveAll<IRequestLogService>();
        services.AddSingleton(requestLog.Object);

        var billingAudit = new Mock<IBillingAuditService>();
        billingAudit.Setup(service => service.LogBillingEventAsync(It.IsAny<BillingAuditEvent>()))
            .Returns(Task.CompletedTask);
        services.RemoveAll<IBillingAuditService>();
        services.AddSingleton(billingAudit.Object);

        var eventPublisher = new Mock<IEventPublisher>();
        eventPublisher.SetupGet(publisher => publisher.IsEnabled).Returns(false);
        services.RemoveAll<IEventPublisher>();
        services.AddSingleton(eventPublisher.Object);

        var mediaLifecycle = new Mock<IMediaLifecycleService>();
        mediaLifecycle.Setup(service => service.TrackMediaAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MediaLifecycleMetadata>()))
            .ReturnsAsync(new MediaRecord());
        services.RemoveAll<IMediaLifecycleService>();
        services.AddSingleton(mediaLifecycle.Object);
        services.RemoveAll<IProviderErrorTrackingService>();
        services.AddSingleton(Mock.Of<IProviderErrorTrackingService>());

        services.RemoveAll<IMediaStorageService>();
        services.AddSingleton<IMediaStorageService>(provider => new InMemoryMediaStorageService(
            provider.GetRequiredService<ILogger<InMemoryMediaStorageService>>()));
    }

    private static List<ModelProviderMapping> CreateMappings()
    {
        var provider = new Provider
        {
            Id = 1,
            ProviderName = "SDK smoke provider",
            ProviderType = ProviderType.OpenAI,
            IsEnabled = true
        };

        return new[]
        {
            (Alias: "chat-model", Image: false, Embedding: false, Speech: false, Chat: true),
            (Alias: "embedding-model", Image: false, Embedding: true, Speech: false, Chat: false),
            (Alias: "image-model", Image: true, Embedding: false, Speech: false, Chat: false),
            (Alias: "speech-model", Image: false, Embedding: false, Speech: true, Chat: false)
        }.Select((definition, index) =>
        {
            var model = new Model
            {
                Id = index + 1,
                Name = definition.Alias,
                SupportsChat = definition.Chat,
                SupportsStreaming = definition.Chat,
                SupportsEmbeddings = definition.Embedding,
                SupportsImageGeneration = definition.Image,
                SupportsTextToSpeech = definition.Speech
            };
            var association = new ModelProviderTypeAssociation
            {
                Id = index + 1,
                ModelId = model.Id,
                Identifier = definition.Alias,
                Provider = ProviderType.OpenAI,
                Model = model,
                IsEnabled = true
            };
            return new ModelProviderMapping
            {
                Id = index + 1,
                ModelAlias = definition.Alias,
                ProviderModelId = definition.Alias,
                ProviderId = provider.Id,
                Provider = provider,
                ModelProviderTypeAssociationId = association.Id,
                ModelProviderTypeAssociation = association,
                IsEnabled = true,
                CreatedAt = DateTime.UnixEpoch.AddSeconds(index + 1)
            };
        }).ToList();
    }

    private static VirtualKeyValidationOutcome ValidateSmokeKey(string candidate, VirtualKey key) =>
        candidate == "smoke-key"
            ? VirtualKeyValidationOutcome.Success(key)
            : VirtualKeyValidationOutcome.Failure(
                VirtualKeyValidationFailureCodes.KeyNotFound,
                StatusCodes.Status401Unauthorized,
                "Invalid virtual key.");
}

[CollectionDefinition("OpenAiSdkSmoke", DisableParallelization = true)]
public sealed class OpenAiSdkSmokeCollection;

internal sealed class SmokeClientFactory(ILLMClient client) : ILLMClientFactory
{
    public Task<ILLMClient> GetClientAsync(string modelAlias, CancellationToken cancellationToken = default) =>
        Task.FromResult(client);

    public Task<ILLMClient> GetClientForChatAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(client);

    public Task<ILLMClient> GetClientByProviderIdAsync(int providerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(client);

    public Task<ILLMClient> GetClientByProviderIdAsync(int providerId, string providerModelId, CancellationToken cancellationToken = default) =>
        Task.FromResult(client);

    public Task<ILLMClient> GetClientByProviderTypeAsync(ProviderType providerType, CancellationToken cancellationToken = default) =>
        Task.FromResult(client);

    public ILLMClient CreateTestClient(Provider provider, ProviderKeyCredential keyCredential) => client;
}

internal sealed class SmokeProviderClient(
    GatewayInvocationCounters counters,
    IHttpContextAccessor httpContextAccessor) : ILLMClient, ITextToSpeechClient
{
    public Task<ChatCompletionResponse> CreateChatCompletionAsync(
        ChatCompletionRequest request,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        CountChatOperation();
        return Task.FromResult(new ChatCompletionResponse
        {
            Id = "chatcmpl_smoke",
            Object = "chat.completion",
            Created = 1,
            Model = request.Model,
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    FinishReason = FinishReason.Stop,
                    Message = new Message { Role = MessageRole.Assistant, Content = "ok" }
                }
            ],
            Usage = new Usage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 }
        });
    }

    public async IAsyncEnumerable<ChatCompletionChunk> StreamChatCompletionAsync(
        ChatCompletionRequest request,
        string? apiKey = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CountChatOperation();
        yield return new ChatCompletionChunk
        {
            Id = "chatcmpl_smoke",
            Created = 1,
            Model = request.Model,
            Choices =
            [
                new StreamingChoice
                {
                    Index = 0,
                    Delta = new DeltaContent { Role = MessageRole.Assistant, Content = "ok" }
                }
            ]
        };
        await Task.Yield();
        yield return new ChatCompletionChunk
        {
            Id = "chatcmpl_smoke",
            Created = 1,
            Model = request.Model,
            Choices =
            [
                new StreamingChoice
                {
                    Index = 0,
                    Delta = new DeltaContent(),
                    FinishReason = FinishReason.Stop
                }
            ],
            Usage = new Usage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 }
        };
    }

    public Task<List<string>> ListModelsAsync(string? apiKey = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new List<string> { "chat-model", "embedding-model", "image-model", "speech-model" });

    public Task<EmbeddingResponse> CreateEmbeddingAsync(
        EmbeddingRequest request,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        counters.Increment("embeddings");
        return Task.FromResult(new EmbeddingResponse
        {
            Object = "list",
            Model = request.Model,
            Data = [new EmbeddingData { Object = "embedding", Index = 0, Embedding = [0.1f] }],
            Usage = new Usage { PromptTokens = 1, TotalTokens = 1 }
        });
    }

    public Task<ImageGenerationResponse> CreateImageAsync(
        ImageGenerationRequest request,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        counters.Increment("images");
        return Task.FromResult(new ImageGenerationResponse
        {
            Created = 1,
            Data = [new ImageData { B64Json = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=" }],
            Usage = new Usage { ImageCount = 1 }
        });
    }

    public Task<TextToSpeechResponse> CreateSpeechAsync(
        TextToSpeechRequest request,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        counters.Increment("speech");
        return Task.FromResult(new TextToSpeechResponse
        {
            AudioData = [0x49, 0x44, 0x33, 0x04, 0x00, 0x00],
            ContentType = "audio/mpeg",
            Model = request.Model,
            Usage = new Usage { TtsCharacters = request.Input.Length }
        });
    }

    private void CountChatOperation()
    {
        var path = httpContextAccessor.HttpContext?.Request.Path.Value;
        counters.Increment(string.Equals(path, "/v1/responses", StringComparison.Ordinal) ? "responses" : "chat");
    }
}

internal sealed class GatewayInvocationCounters
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    public int this[string operation] => _counts.GetValueOrDefault(operation);

    public void Increment(string operation) => _counts.AddOrUpdate(operation, 1, static (_, count) => count + 1);

    public void Reset() => _counts.Clear();

    public string Describe() => string.Join(", ", _counts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));
}

internal sealed class EnvironmentScope : IDisposable
{
    private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);

    public EnvironmentScope(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (name, value) in values)
        {
            _original[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _original)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
