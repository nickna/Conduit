using System.Net;

using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Services;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ConduitLLM.Tests.Http.Middleware;

public class SsePassthroughSocketTests
{
    [Fact]
    public async Task FirstEventArrivesBeforeEndpointCompletesOverRealSocket()
    {
        var allowCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endpointCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();

        app.MapGet("/stream", async context =>
        {
            var writer = context.Response.CreateEnhancedSSEWriter(
                GatewayJsonOptions.Create());
            await writer.WriteContentEventAsync(
                new Dictionary<string, string> { ["value"] = "first" },
                context.RequestAborted);
            await allowCompletion.Task.WaitAsync(context.RequestAborted);
            await writer.WriteContentEventAsync(
                new Dictionary<string, string> { ["value"] = "second" },
                context.RequestAborted);
            await writer.WriteDoneEventAsync(context.RequestAborted);
            endpointCompleted.TrySetResult();
        });

        try
        {
            await app.StartAsync();
            var server = app.Services.GetRequiredService<IServer>();
            var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
            using var client = new HttpClient();
            using var response = await client.GetAsync(
                $"{address}/stream",
                HttpCompletionOption.ResponseHeadersRead);
            await using var responseStream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(responseStream);

            var firstLine = await reader.ReadLineAsync();

            Assert.Contains("first", firstLine);
            Assert.False(endpointCompleted.Task.IsCompleted);

            allowCompletion.TrySetResult();
            var remainder = await reader.ReadToEndAsync();
            Assert.Contains("second", remainder);
            Assert.Contains("data: [DONE]", remainder);
            await endpointCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            allowCompletion.TrySetResult();
            await app.StopAsync();
        }
    }
}
