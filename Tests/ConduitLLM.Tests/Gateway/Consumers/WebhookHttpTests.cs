using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace ConduitLLM.Tests.Gateway.Consumers;

public sealed class WebhookHttpTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ProductionClient_ReportsActualPostAmplification()
    {
        var posts = 0;
        await using var receiver = await Receiver.StartAsync(context =>
        {
            Interlocked.Increment(ref posts);
            context.Response.StatusCode = context.Request.Path == "/healthy" ? 202 : 503;
            return Task.CompletedTask;
        });
        using var services = Services();
        var sender = services.GetRequiredService<IWebhookNotificationService>();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
            Assert.Equal(202, (await sender.SendTaskCompletionWebhookAsync(receiver.Url + "/healthy", Payload)).StatusCode);
        output.WriteLine($"Healthy: events=20, posts={posts}, elapsed_ms={clock.Elapsed.TotalMilliseconds:F2}");
        clock.Restart();
        Assert.Equal(503, (await sender.SendTaskCompletionWebhookAsync(receiver.Url + "/unavailable", Payload)).StatusCode);
        output.WriteLine($"Unavailable: events=1, posts={posts - 20}, elapsed_ms={clock.Elapsed.TotalMilliseconds:F2}");
        Assert.Equal(21, posts);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    [InlineData(204)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task ProductionClient_PreservesPayloadHeadersAndStatus(int status)
    {
        var posts = 0;
        await using var receiver = await Receiver.StartAsync(async context =>
        {
            Interlocked.Increment(ref posts);
            Assert.Equal("Bearer callback-test", context.Request.Headers.Authorization);
            Assert.Equal("completion", context.Request.Headers["X-Webhook-Type"]);
            Assert.True(long.TryParse(context.Request.Headers["X-Webhook-Timestamp"], out _));
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            Assert.Equal("completed", body.RootElement.GetProperty("status").GetString());
            context.Response.Headers.RetryAfter = "120";
            context.Response.StatusCode = status;
        });
        using var services = Services();
        var result = await services.GetRequiredService<IWebhookNotificationService>()
            .SendTaskCompletionWebhookAsync(receiver.Url, Payload, new() { ["Authorization"] = "Bearer callback-test" });
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(status is >= 200 and < 300, result.Success);
        if (!result.Success) Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task ProductionClient_DoesNotFollowRedirectOrReadSlowBody()
    {
        var posts = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var receiver = await Receiver.StartAsync(async context =>
        {
            Interlocked.Increment(ref posts);
            context.Response.StatusCode = 307;
            context.Response.Headers.Location = "/redirected";
            context.Response.ContentLength = 1024L * 1024 * 1024;
            await context.Response.StartAsync();
            await context.Response.WriteAsync("x");
            await context.Response.Body.FlushAsync();
            await release.Task;
        });
        using var services = Services();
        try
        {
            var result = await services.GetRequiredService<IWebhookNotificationService>()
                .SendTaskCompletionWebhookAsync(receiver.Url, Payload).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(307, result.StatusCode);
            Assert.Equal(1, posts);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ProductionClient_CustomTimeoutAndShutdownAreDistinct()
    {
        await using var receiver = await Receiver.StartAsync(async context =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted); }
            catch (OperationCanceledException) { }
        });
        using var services = Services();
        var sender = services.GetRequiredService<IWebhookNotificationService>();
        var result = await sender.SendWebhookAsync(receiver.Url, Payload, customTimeout: TimeSpan.FromMilliseconds(100));
        Assert.Equal(WebhookFailureKind.Timeout, result.FailureKind);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendWebhookAsync(receiver.Url, Payload,
            customTimeout: TimeSpan.FromSeconds(2), cancellationToken: shutdown.Token));
    }

    [Theory]
    [InlineData("120", true)]
    [InlineData("date", true)]
    [InlineData("invalid", false)]
    public async Task ProductionClient_ParsesRetryAfter(string value, bool parsed)
    {
        await using var receiver = await Receiver.StartAsync(context =>
        {
            context.Response.StatusCode = 429;
            context.Response.Headers.RetryAfter = value == "date" ? DateTimeOffset.UtcNow.AddMinutes(2).ToString("R") : value;
            return Task.CompletedTask;
        });
        using var services = Services();
        var result = await services.GetRequiredService<IWebhookNotificationService>()
            .SendTaskProgressWebhookAsync(receiver.Url, Payload);
        Assert.Equal(parsed, result.RetryAfter.HasValue);
        if (parsed) Assert.InRange(result.RetryAfter!.Value.TotalSeconds, 110, 120);
    }

    [Fact]
    public async Task ProductionClient_DoesNotShareReceiverCookiesBetweenCallbacks()
    {
        var posts = 0;
        await using var receiver = await Receiver.StartAsync(context =>
        {
            Assert.Empty(context.Request.Headers.Cookie.ToString());
            Interlocked.Increment(ref posts);
            context.Response.Headers.SetCookie = "tenant_session=private; Path=/";
            context.Response.StatusCode = 204;
            return Task.CompletedTask;
        });
        using var services = Services();
        var sender = services.GetRequiredService<IWebhookNotificationService>();
        Assert.True((await sender.SendTaskCompletionWebhookAsync(receiver.Url, Payload)).Success);
        Assert.True((await sender.SendTaskCompletionWebhookAsync(receiver.Url, Payload)).Success);
        Assert.Equal(2, posts);
    }

    [Fact]
    public async Task ProductionClient_ConfiguredTimeoutTakesEffect()
    {
        await using var receiver = await Receiver.StartAsync(async context =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted); }
            catch (OperationCanceledException) { }
        });
        using var services = Services(new() { ["Webhooks:Delivery:AttemptTimeoutSeconds"] = "1", ["Webhooks:Delivery:ConnectTimeoutSeconds"] = "1" });
        var elapsed = Stopwatch.StartNew();
        var result = await services.GetRequiredService<IWebhookNotificationService>().SendTaskCompletionWebhookAsync(receiver.Url, Payload);
        Assert.Equal(WebhookFailureKind.Timeout, result.FailureKind);
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 0.5, 5);
    }

    private static JsonElement Payload => JsonSerializer.Deserialize<JsonElement>("""{"task_id":"test","status":"completed"}""");

    private static ServiceProvider Services(Dictionary<string, string?>? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddWebhookServices(new ConfigurationBuilder().AddInMemoryCollection(configuration ?? new()).Build());
        return services.BuildServiceProvider();
    }

    private sealed class Receiver(WebApplication app) : IAsyncDisposable
    {
        public string Url => app.Urls.Single();
        public static async Task<Receiver> StartAsync(RequestDelegate handler)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.Run(handler);
            await app.StartAsync();
            return new Receiver(app);
        }
        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
}
