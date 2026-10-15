using System.Collections.Concurrent;
using System.Net;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Authentication;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Models;
using ConduitLLM.Gateway.Serialization;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Security.Options;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Moq;

namespace ConduitLLM.IntegrationTests.Infrastructure;

/// <summary>
/// Runs production hub dispatch, authentication, and generated JSON contracts without
/// external infrastructure. Only persisted key/task lookups are replaced by fixtures.
/// </summary>
internal sealed class GatewaySignalRDispatchHost : IAsyncDisposable
{
    public const int OwnerA = 101;
    public const int OwnerB = 202;
    public const string TokenA = "condt-dispatch-owner-a";
    public const string TokenB = "condt-dispatch-owner-b";
    public const string TaskA = "dispatch-task-a";
    public const string TaskB = "dispatch-task-b";
    public const string VideoTaskA = "dispatch-video-a";
    public const string VideoTaskB = "dispatch-video-b";
    public const string EphemeralTokenA = "ek_dispatch-owner-a";
    public const string EphemeralTokenB = "ek_dispatch-owner-b";
    public const string ExpiredEphemeralToken = "ek_dispatch-expired";
    public const string PublicVideoPath = "/hubs/public/video-generation";
    private const string BarrierEvent = "DispatchTestBarrier";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private readonly List<HubConnection> _connections = [];
    private WebApplication? _app;
    private string _address = null!;

    public Mock<IVirtualKeyRuntimeService> RuntimeKeys { get; } = new();
    public Mock<IAsyncTaskService> TaskService { get; } = new();
    public Mock<IEphemeralKeyService> EphemeralKeys { get; } = new();
    public ConcurrentDictionary<string, EphemeralKeyData> EphemeralTokens { get; } = new();
    public ConcurrentDictionary<string, AsyncTaskStatus> Tasks { get; } = new();
    public IServiceProvider Services => _app!.Services;

    public GatewaySignalRDispatchHost()
    {
        var keys = new Dictionary<string, VirtualKey>
        {
            [TokenA] = new() { Id = OwnerA, KeyName = "Owner A", KeyHash = "dispatch-a", IsEnabled = true },
            [TokenB] = new() { Id = OwnerB, KeyName = "Owner B", KeyHash = "dispatch-b", IsEnabled = true }
        };
        RuntimeKeys.Setup(service => service.ValidateVirtualKeyForAuthenticationAsync(
                It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((string token, string? _) => keys.TryGetValue(token, out var key)
                ? VirtualKeyValidationOutcome.Success(key)
                : VirtualKeyValidationOutcome.Failure(VirtualKeyValidationFailureCodes.KeyNotFound, 401, "Invalid fixture key"));
        RuntimeKeys.Setup(service => service.GetVirtualKeyInfoForValidationAsync(
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => keys.Values.FirstOrDefault(key => key.Id == id));
        TaskService.Setup(service => service.GetTaskStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => Tasks.GetValueOrDefault(id));
        Tasks[TaskA] = CreateTask(TaskA, OwnerA);
        Tasks[TaskB] = CreateTask(TaskB, OwnerB);
        Tasks[VideoTaskA] = CreateTask(VideoTaskA, OwnerA, "video_generation");
        Tasks[VideoTaskB] = CreateTask(VideoTaskB, OwnerB, "video_generation");
        EphemeralTokens[EphemeralTokenA] = new() { VirtualKeyId = OwnerA, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
        EphemeralTokens[EphemeralTokenB] = new() { VirtualKeyId = OwnerB, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
        EphemeralTokens[ExpiredEphemeralToken] = new() { VirtualKeyId = OwnerA, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        EphemeralKeys.Setup(service => service.GetKeyDataAsync(It.IsAny<string>()))
            .ReturnsAsync((string token) => EphemeralTokens.GetValueOrDefault(token));
    }

    public async Task StartAsync(
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureEndpoints = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        // Default Windows EventLog logging requires permissions unrelated to hub dispatch.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(RuntimeKeys.Object);
        builder.Services.AddSingleton(TaskService.Object);
        builder.Services.AddSingleton(EphemeralKeys.Object);
        builder.Services.Configure<GatewaySecurityOptions>(_ => { });
        builder.Services.AddAuthentication("VirtualKey")
            .AddScheme<AuthenticationSchemeOptions, VirtualKeyAuthenticationHandler>("VirtualKey", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("RequireVirtualKey", policy =>
        {
            policy.AuthenticationSchemes.Add("VirtualKey");
            policy.RequireAuthenticatedUser();
            policy.RequireClaim("VirtualKeyId");
        }));
        builder.Services.AddScoped<VirtualKeyHubFilter>();
        builder.Services.AddScoped<ISignalRAuthenticationService, SignalRAuthenticationService>();
        builder.Services.AddConduitSignalR(builder.Environment, null, "dispatch-fixture:", 2, "Dispatch fixture",
            configureHubOptions: options => options.AddFilter<VirtualKeyHubFilter>(),
            additionalPayloadResolver: GatewaySignalRJsonContext.Default);
        configureServices?.Invoke(builder.Services);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapHub<TaskHub>("/hubs/tasks");
        _app.MapHub<PublicVideoGenerationHub>(PublicVideoPath);
        configureEndpoints?.Invoke(_app);
        await _app.StartAsync().WaitAsync(Deadline);
        _address = Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
    }

    public async Task<HubConnection> ConnectAsync(string? token, string path = "/hubs/tasks")
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"{_address}{path}", options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                if (token is not null) options.Headers["Authorization"] = $"Bearer {token}";
            })
            .Build();
        try
        {
            await connection.StartAsync().WaitAsync(Deadline);
            _connections.Add(connection);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public ITaskHub CreateTaskBroadcaster() => ActivatorUtilities.CreateInstance<TaskHub>(Services);
    public VideoGenerationNotificationService CreateVideoBroadcaster() =>
        ActivatorUtilities.CreateInstance<VideoGenerationNotificationService>(Services);

    public static async Task InvokeAsync(HubConnection connection, string method, params object?[] arguments)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        await connection.InvokeCoreAsync(method, arguments, deadline.Token);
    }

    /// <summary>
    /// Acknowledges all earlier frames on each connection before checking its inbox.
    /// This avoids treating an arbitrary delay as proof that no notification arrived.
    /// </summary>
    public Task FlushAsync<THub>(params HubConnection[] connections) where THub : Hub =>
        Task.WhenAll(connections.Select(async connection =>
        {
            var marker = Guid.NewGuid().ToString("N");
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = connection.On<string>(BarrierEvent, value =>
            {
                if (value == marker) received.TrySetResult();
            });
            await Services.GetRequiredService<IHubContext<THub>>().Clients.Client(connection.ConnectionId!)
                .SendAsync(BarrierEvent, marker);
            await received.Task.WaitAsync(Deadline);
        }));

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var connection in _connections)
                await connection.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
        finally
        {
            if (_app is not null)
            {
                using var deadline = new CancellationTokenSource(Deadline);
                try { await _app.StopAsync(deadline.Token); }
                finally { await _app.DisposeAsync().AsTask().WaitAsync(Deadline); }
            }
        }
    }

    private static AsyncTaskStatus CreateTask(string id, int owner, string taskType = "image_generation") => new()
    {
        TaskId = id,
        TaskType = taskType,
        State = TaskState.Processing,
        Metadata = new TaskMetadata(owner)
    };
}
