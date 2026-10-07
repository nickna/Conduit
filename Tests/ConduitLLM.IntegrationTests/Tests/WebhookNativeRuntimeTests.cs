using System.Net;
using System.Text;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Helpers;
using ConduitLLM.IntegrationTests.Infrastructure;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.Redis;
using Wolverine;
using Xunit.Abstractions;

namespace ConduitLLM.IntegrationTests.Tests;

// Explicit opt-in: build both current native Dockerfiles before running this gate.
public sealed class NativeWebhookFactAttribute : FactAttribute
{
    public NativeWebhookFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CONDUIT_WEBHOOK_NATIVE_TEST") != "1")
            Skip = "Requires current conduit-http-native:epic-1419 and conduit-admin-native:epic-1419 images and CONDUIT_WEBHOOK_NATIVE_TEST=1.";
    }
}

[Collection("Durable media dispatch")]
[Trait("Category", "NativeRuntime")]
[Trait("Component", "Webhooks")]
public sealed class WebhookNativeRuntimeTests(MediaDispatchFixture fixture, ITestOutputHelper output)
{
    [NativeWebhookFact]
    public async Task NativeGatewayAndAdmin_RealDeliveryInspectionDeadLettersAndReplay()
    {
        await fixture.ResetAsync();
        await using var receiver = await WebhookReceiver.StartAsync(containerAccessible: true);
        // Exercise the native scheduled retry, then permanent exhaustion/replay.
        receiver.Respond = context => { context.Response.StatusCode = receiver.Posts.Count == 1 ? 503 : 400; return Task.CompletedTask; };
        await using var redis = new RedisBuilder().WithImage("redis:7.4-alpine").Build();
        await redis.StartAsync();
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        var database = $"postgresql://{Uri.EscapeDataString(connection.Username!)}:{Uri.EscapeDataString(connection.Password!)}@host.docker.internal:{connection.Port}/{connection.Database}";
        const string master = "native-webhook-test-master-key-32-bytes";
        IContainer Container(string image, string schema) => new ContainerBuilder().WithImage(image).WithPortBinding(8080, true)
            .WithEnvironment("DATABASE_URL", database)
            .WithEnvironment("REDIS_URL", $"host.docker.internal:{redis.GetMappedPublicPort(6379)}")
            .WithEnvironment("CONDUIT_API_TO_API_BACKEND_AUTH_KEY", master).WithEnvironment("CONDUIT_MIGRATION_MODE", "Skip")
            .WithEnvironment("CONDUIT_ENABLE_HTTPS_REDIRECTION", "false")
            .WithEnvironment("ConduitLLM__Messaging__Wolverine__SchemaName", schema)
            .WithEnvironment("ConduitLLM__Messaging__Wolverine__AutoProvision", "true")
            .WithEnvironment("Webhooks__GatewayDurabilitySchema", "wolverine_media_test").Build();
        await using var gateway = Container("conduit-http-native:epic-1419", "wolverine_media_test");
        await using var admin = Container("conduit-admin-native:epic-1419", "wolverine_native_admin_test");
        using var publisher = fixture.Host(worker: false);
        await publisher.StartAsync();
        using (var provision = fixture.Host(worker: false, durabilitySchema: "wolverine_native_admin_test"))
        { await provision.StartAsync(); await provision.StopAsync(); }
        try
        {
            await gateway.StartAsync(); await admin.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{admin.GetMappedPublicPort(8080)}"), Timeout = TimeSpan.FromSeconds(5) };
            await MediaDispatchFixture.EventuallyAsync(async () => { try { return (await client.GetAsync("/health/live")).IsSuccessStatusCode; } catch (HttpRequestException) { return false; } });
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/admin/webhook-deliveries/backlog")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", master);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/admin/webhook-deliveries/backlog")).StatusCode); // #1448
            client.DefaultRequestHeaders.Authorization = null;
            client.DefaultRequestHeaders.Add("X-API-Key", master);
            var request = new WebhookDeliveryRequested { TaskId = "native-webhook", VirtualKeyId = 1,
                WebhookUrl = receiver.Url.Replace("0.0.0.0", "host.docker.internal"), PayloadJson = "{\"status\":\"completed\"}",
                Headers = new() { ["Authorization"] = "Bearer native-receiver" } };
            await publisher.Services.GetRequiredService<IMessageBus>().PublishAsync(request);
            var id = WebhookIdentity.DeliveryKey(request);
            await MediaDispatchFixture.EventuallyAsync(async () => { await using var db = fixture.Db(); return await db.WebhookDeliveries.AnyAsync(r => r.Id == id && r.State == "Exhausted"); });
            var inspect = await client.GetAsync("/v1/admin/webhook-deliveries/?owner=1&taskId=native-webhook");
            Assert.Equal(HttpStatusCode.OK, inspect.StatusCode);
            Assert.Contains(request.EventId, await inspect.Content.ReadAsStringAsync());
            await MediaDispatchFixture.EventuallyAsync(async () => {
                var response = await client.GetAsync("/v1/admin/webhook-deliveries/dead-letters");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return (await response.Content.ReadAsStringAsync()).Contains(id);
            });
            receiver.Respond = context => { context.Response.StatusCode = 202; return Task.CompletedTask; };
            using var body = new StringContent($$"""{"operationId":"{{Guid.NewGuid()}}","virtualKeyId":1,"expectedCycle":0}""", Encoding.UTF8, "application/json");
            var replay = await client.PostAsync($"/v1/admin/webhook-deliveries/{id}/replay", body);
            Assert.True(replay.IsSuccessStatusCode, await replay.Content.ReadAsStringAsync());
            await MediaDispatchFixture.EventuallyAsync(async () => { await using var db = fixture.Db(); return await db.WebhookDeliveries.AnyAsync(r => r.Id == id && r.State == "Delivered" && r.Cycle == 1); });
            Assert.Equal(3, receiver.Posts.Count);
            Assert.All(receiver.Posts, p => { Assert.Equal(request.EventId, p.EventId); Assert.Equal("Bearer native-receiver", p.Authorization); Assert.Equal(request.PayloadJson, p.Json); });
        }
        finally
        {
            output.WriteLine("Gateway: " + (await gateway.GetLogsAsync()).Stdout);
            output.WriteLine("Admin: " + (await admin.GetLogsAsync()).Stdout);
            await publisher.StopAsync();
        }
    }
}
