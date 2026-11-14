using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ConduitLLM.OpenApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace ConduitLLM.Tests.OpenApi;

[Trait("Category", "Unit")]
[Trait("Component", "OpenApi")]
public sealed class PublishedOpenApiTests
{
    [Theory]
    [InlineData("3.1.1")]
    [InlineData("3.1.2")]
    public void Normalize_ChangesOnlyRootVersionAndPreservesLiteralBytes(string version)
    {
        var source = "{\r\n  \"info\": {\"description\": \"3.1.2\"},\r\n  \"openapi\": \"" + version +
            "\", \"example\": {\"openapi\": \"3.1.2\", \"literal\": \"line\\r\\n3.1.2\"}\r\n}";
        var actual = Encoding.UTF8.GetBytes(source);
        PublishedOpenApiExtensions.NormalizeVersion(actual);
        var expected = source.Replace("\"openapi\": \"" + version + "\", \"example\"", "\"openapi\": \"3.1.1\", \"example\"", StringComparison.Ordinal);
        Assert.Equal(expected, Encoding.UTF8.GetString(actual));
    }

    [Theory]
    [InlineData("{\"openapi\":\"3.2.0\"}")]
    [InlineData("{\"openapi\":\"3.1.2\",\"openapi\":\"3.1.1\"}")]
    [InlineData("{\"openapi\":31}")]
    [InlineData("{\"info\":{\"openapi\":\"3.1.2\"}}")]
    [InlineData("[]")]
    public void Normalize_RejectsUnexpectedOrAmbiguousVersionWithoutMutation(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        Assert.Throws<InvalidDataException>(() => PublishedOpenApiExtensions.NormalizeVersion(bytes));
        Assert.Equal(source, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Normalize_RejectsMalformedOrTrailingJsonWithoutMutation()
    {
        foreach (var source in new[] { "{\"openapi\":\"3.1.2\",", "{\"openapi\":\"3.1.2\"} {}" })
        {
            var bytes = Encoding.UTF8.GetBytes(source);
            Assert.ThrowsAny<JsonException>(() => PublishedOpenApiExtensions.NormalizeVersion(bytes));
            Assert.Equal(source, Encoding.UTF8.GetString(bytes));
        }
    }

    [Fact]
    public async Task OfflineSerializer_PreservesEverythingExceptTheRootPatchByteAndLeavesDestinationOpen()
    {
        var document = new OpenApiDocument
        {
            Info = new() { Title = "3.1.2", Version = "v1", Description = "literal 3.1.2\r\ntext" },
            Extensions = new Dictionary<string, IOpenApiExtension>
            {
                ["x-example"] = new JsonNodeExtension(new JsonObject { ["openapi"] = "3.1.2" })
            }
        };
        await using var original = new MemoryStream();
        await document.SerializeAsJsonAsync(original, OpenApiSpecVersion.OpenApi3_1);
        await using var actual = new MemoryStream();
        await document.SerializeAsPublishedJsonAsync(actual);
        var before = original.ToArray();
        var after = actual.ToArray();
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(1, before.Zip(after).Count(pair => pair.First != pair.Second));
        using var json = JsonDocument.Parse(after);
        Assert.Equal("3.1.1", json.RootElement.GetProperty("openapi").GetString());
        Assert.Equal("3.1.2", json.RootElement.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("3.1.2", json.RootElement.GetProperty("x-example").GetProperty("openapi").GetString());
        Assert.True(actual.CanWrite);
    }

    [Fact]
    public async Task OfflineSerializer_RejectsOversizedDocumentBeforeWritingDestination()
    {
        var document = new OpenApiDocument { Info = new() { Title = "Fixture", Version = "v1",
            Description = new string('x', PublishedOpenApiExtensions.MaximumDocumentBytes) } };
        await using var destination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => document.SerializeAsPublishedJsonAsync(destination));
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task ResponseAdapter_RestoresOriginalBodyWhenGenerationFails()
    {
        var context = new DefaultHttpContext();
        await using var original = new MemoryStream();
        context.Response.Body = original;
        await Assert.ThrowsAsync<InvalidOperationException>(() => PublishedOpenApiExtensions.WritePublishedResponseAsync(context,
            _ => throw new InvalidOperationException("fixture generation failure")));
        Assert.Same(original, context.Response.Body);
        Assert.Equal(0, original.Length);
    }

    [Fact]
    public async Task ResponseAdapter_PreservesFrameworkErrorResponseAndContentLength()
    {
        var context = new DefaultHttpContext();
        await using var original = new MemoryStream();
        context.Response.Body = original;
        System.IO.Pipelines.PipeWriter temporaryWriter = null!;
        await PublishedOpenApiExtensions.WritePublishedResponseAsync(context, async request =>
        {
            temporaryWriter = request.Response.BodyWriter;
            request.Response.StatusCode = StatusCodes.Status404NotFound;
            request.Response.ContentType = "text/plain;charset=utf-8";
            request.Response.ContentLength = 7;
            await request.Response.Body.WriteAsync("missing"u8.ToArray());
        });
        Assert.Same(original, context.Response.Body);
        Assert.Equal("missing", Encoding.UTF8.GetString(original.ToArray()));
        Assert.Equal(7, context.Response.ContentLength);
        Assert.Throws<InvalidOperationException>(() => temporaryWriter.GetMemory());
    }

    [Fact]
    public async Task ResponseAdapter_BoundsFrameworkBodyWriterAndRestoresBodyOnOverflow()
    {
        var context = new DefaultHttpContext();
        await using var original = new MemoryStream();
        context.Response.Body = original;
        await Assert.ThrowsAsync<InvalidDataException>(() => PublishedOpenApiExtensions.WritePublishedResponseAsync(context,
            async request => { await request.Response.BodyWriter.WriteAsync(new byte[PublishedOpenApiExtensions.MaximumDocumentBytes + 1]); }));
        Assert.Same(original, context.Response.Body);
        Assert.Equal(0, original.Length);
    }

    [Fact]
    public async Task FrameworkEndpoint_Publishes311AndPreservesScopedRequestGeneration()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddScoped<RequestScopedMarker>();
        builder.Services.AddOpenApi("v1", options =>
        {
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            options.AddDocumentTransformer((document, context, _) =>
            {
                document.Info.Description = context.ApplicationServices.GetRequiredService<RequestScopedMarker>().Value;
                return Task.CompletedTask;
            });
        });
        await using var app = builder.Build();
        app.MapGet("/fixture", () => "3.1.2");
        app.MapPublishedOpenApi("/openapi/v1.json");
        await app.StartAsync();
        var client = app.GetTestClient();
        var first = await client.GetStringAsync("/openapi/v1.json");
        var second = await client.GetStringAsync("/openapi/v1.json");
        using var firstJson = JsonDocument.Parse(first);
        using var secondJson = JsonDocument.Parse(second);
        Assert.Equal("3.1.1", firstJson.RootElement.GetProperty("openapi").GetString());
        Assert.NotEqual(firstJson.RootElement.GetProperty("info").GetProperty("description").GetString(),
            secondJson.RootElement.GetProperty("info").GetProperty("description").GetString());
        Assert.True(firstJson.RootElement.GetProperty("paths").TryGetProperty("/fixture", out _));
        Assert.False(firstJson.RootElement.GetProperty("paths").TryGetProperty("/openapi/v1.json", out _));
    }

    private sealed class RequestScopedMarker
    {
        public RequestScopedMarker() { }
        public string Value { get; } = Guid.NewGuid().ToString();
    }
}
