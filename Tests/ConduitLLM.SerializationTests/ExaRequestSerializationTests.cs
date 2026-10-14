using System.Net;
using System.Text;
using System.Text.Json;

using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Providers.Exa;

using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.SerializationTests;

public sealed class ExaRequestSerializationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_SerializesJsonOptionsWithoutReflection(
        bool contents, bool arrays)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var routing = contents
            ? "\"urls\": [\"https://fixture.invalid\"]"
            : "\"query\": \"fixture\"";
        var summary = arrays
            ? """
              {
                "query": "Summarize",
                "schema": {
                  "type": "object",
                  "properties": {
                    "topic": {"type": "string", "enum": ["news", "research"]},
                    "sample": {
                      "default": [null, false, 0, 0.25, {"CaseSensitiveKey": "preserved"}]
                    }
                  },
                  "required": ["topic"],
                  "anyOf": [
                    {"required": ["topic"]},
                    {"properties": {"topic": {"const": "news"}}}
                  ]
                }
              }
              """
            : """
              {
                "schema": {
                  "type": "object",
                  "properties": {
                    "count": {
                      "type": "integer",
                      "minimum": -3000000000,
                      "maximum": 3000000000
                    }
                  }
                }
              }
              """;
        var contentsOptions = contents && arrays
            ? """
              , "subpageTarget": ["news", "research"],
                "extras": {"links": 0, "imageLinks": 1},
                "context": true
              """
            : string.Empty;
        using var expected = JsonDocument.Parse($$"""
            {
              {{routing}},
              "text": false,
              "highlights": {"query": "fixture", "numSentences": 0},
              "summary": {{summary}}
              {{contentsOptions}}
            }
            """);
        var parameters = expected.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object)property.Value.Clone());
        using var handler = new RecordingHandler();
        var factory = new ClientFactory(handler);
        var client = new ExaClient(
            new FunctionConfiguration
            {
                ConfigurationName = "fixture",
                ProviderType = FunctionProviderType.Exa
            },
            new FunctionCredential
            {
                ProviderType = FunctionProviderType.Exa,
                ApiKey = "fixture-key"
            },
            factory,
            NullLogger<ExaClient>.Instance);

        var result = await client.ExecuteAsync(parameters);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Equal("ExaFunctionClient", factory.RequestedName);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri($"https://api.exa.ai/{(contents ? "contents" : "search")}"), request.Uri);
        Assert.Equal("fixture-key", request.ApiKey);
        Assert.Equal("application/json", request.ContentType);
        using var actual = JsonDocument.Parse(request.Body);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement), request.Body);
    }

    private sealed class ClientFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public string? RequestedName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            RequestedName = name;
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.GetValues("x-api-key").Single(),
                request.Content!.Headers.ContentType?.MediaType,
                await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "requestId": "fixture-request",
                      "resolvedSearchType": "neural",
                      "results": [{"url": "https://fixture.invalid", "text": "content"}]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method, Uri Uri, string ApiKey, string? ContentType, string Body);
}
