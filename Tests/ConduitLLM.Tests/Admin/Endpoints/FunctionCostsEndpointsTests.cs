using System.Reflection;
using System.Text.Json;

using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Admin.Serialization;
using ConduitLLM.Functions.Interfaces;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace ConduitLLM.Tests.Admin.Endpoints;

public sealed class FunctionCostsEndpointsTests
{
    [Fact]
    public async Task ClearCache_UsesGeneratedResponseMetadataAndPreservesWireContract()
    {
        var service = new Mock<IFunctionCostService>(MockBehavior.Strict);
        service.Setup(value => value.ClearCacheAsync()).Returns(Task.CompletedTask);
        using var services = new ServiceCollection()
            .AddLogging()
            .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
                AdminJsonOptions.Configure(options.SerializerOptions))
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        var handler = typeof(FunctionCostsEndpoints).GetMethod(
            "ClearCache", BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = await (Task<IResult>)handler.Invoke(
            null, [service.Object, context, NullLoggerFactory.Instance])!;
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(
            "Function cost cache cleared successfully",
            body.RootElement.GetProperty("message").GetString());
        Assert.Single(body.RootElement.EnumerateObject());
        service.Verify(value => value.ClearCacheAsync(), Times.Once);
    }
}
