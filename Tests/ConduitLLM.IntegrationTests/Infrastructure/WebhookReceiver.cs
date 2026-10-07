using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.IntegrationTests.Infrastructure;

public sealed record ReceivedWebhook(string EventId, string Authorization, string Type, string Json, DateTime ReceivedAt);

/// <summary>Real loopback receiver shared by durable delivery fault and performance tests.</summary>
public sealed class WebhookReceiver(WebApplication app) : IAsyncDisposable
{
    public string Url => app.Urls.Single();
    public ConcurrentQueue<ReceivedWebhook> Posts { get; } = new();
    public Func<HttpContext, Task> Respond { get; set; } = context =>
    {
        context.Response.StatusCode = 204;
        return Task.CompletedTask;
    };
    public static async Task<WebhookReceiver> StartAsync(bool containerAccessible = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(containerAccessible ? IPAddress.Any : IPAddress.Loopback, 0));
        var app = builder.Build();
        var receiver = new WebhookReceiver(app);
        app.Run(async context =>
        {
            using var body = new StreamReader(context.Request.Body);
            receiver.Posts.Enqueue(new(context.Request.Headers["X-Webhook-Id"].ToString(),
                context.Request.Headers.Authorization.ToString(), context.Request.Headers["X-Webhook-Type"].ToString(),
                await body.ReadToEndAsync(), DateTime.UtcNow));
            await receiver.Respond(context);
        });
        await app.StartAsync();
        return receiver;
    }
    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}
