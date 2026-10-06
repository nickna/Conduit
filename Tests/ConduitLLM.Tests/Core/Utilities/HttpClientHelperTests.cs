using System.Net;
using System.Text;
using System.Text.Json;

using ConduitLLM.Core.Utilities;
using ConduitLLM.Tests.TestHelpers;

using Microsoft.Extensions.Logging;

using Moq;

using Xunit;

namespace ConduitLLM.Tests.Core.Utilities;

[Trait("Category", "Unit")]
[Trait("Component", "Core")]
public class HttpClientHelperTests
{
    private static readonly Dictionary<string, string> CredentialHeaders = new()
    {
        ["Authorization"] = "Bearer bearer-secret-value",
        ["Proxy-Authorization"] = "Basic basic-secret-value",
        ["X-Api-Key"] = "api-key-secret-value",
        ["Cookie"] = "session=cookie-secret-value",
        ["X-Custom-Credential"] = "custom-secret-value"
    };

    [Fact]
    public async Task SendJsonRequestAsync_LogsHeaderNamesWithoutCredentialValues()
    {
        using var client = new HttpClient(new StaticResponseHandler(
            HttpStatusCode.OK,
            "{}"));
        var (logger, messages) = CreateDebugLogger();

        await HttpClientHelper.SendJsonRequestAsync(
            client,
            HttpMethod.Post,
            "https://provider.example/v1/chat",
            new Dictionary<string, string> { ["prompt"] = "hello" },
            ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.DictionaryStringString,
            ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.DictionaryStringObject,
            CredentialHeaders,
            logger: logger.Object);

        AssertCredentialValuesWereNotLogged(messages);
    }

    [Fact]
    public async Task SendStreamingRequestAsync_LogsHeaderNamesWithoutCredentialValues()
    {
        using var client = new HttpClient(new StaticResponseHandler(
            HttpStatusCode.OK,
            "data: [DONE]\n\n",
            "text/event-stream"));
        var (logger, messages) = CreateDebugLogger();

        using var response = await HttpClientHelper.SendStreamingRequestAsync(
            client,
            HttpMethod.Post,
            "https://provider.example/v1/chat",
            new Dictionary<string, string>
            {
                ["prompt"] = "hello",
                ["stream"] = "true"
            },
            ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.DictionaryStringString,
            CredentialHeaders,
            logger: logger.Object);

        AssertCredentialValuesWereNotLogged(messages);
    }

    [Fact]
    public async Task ProcessSseStreamAsync_LogsResponseHeaderNamesWithoutValues()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {}\n\ndata: [DONE]\n\n",
                Encoding.UTF8,
                "text/event-stream")
        };
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=response-cookie-secret");
        response.Content.Headers.TryAddWithoutValidation(
            "X-Content-Token",
            "response-content-secret");

        var (logger, messages) = CreateDebugLogger();

        await foreach (var _ in StreamHelper.ProcessSseStreamAsync<JsonElement>(
            response,
            ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.JsonElement,
            logger.Object))
        {
        }

        var output = string.Join(
            Environment.NewLine,
            messages.Select(message => message.Message));

        Assert.Contains("Response header names:", output);
        Assert.Contains("Set-Cookie", output);
        Assert.Contains("Content header names:", output);
        Assert.Contains("X-Content-Token", output);
        Assert.DoesNotContain("response-cookie-secret", output);
        Assert.DoesNotContain("response-content-secret", output);
    }

    private static (Mock<ILogger<HttpClientHelperTests>> Logger, List<LogMessage> Messages)
        CreateDebugLogger()
    {
        var logger = new Mock<ILogger<HttpClientHelperTests>>();
        logger.Setup(value => value.IsEnabled(LogLevel.Debug)).Returns(true);
        var messages = logger.CaptureLogMessages();
        return (logger, messages);
    }

    private static void AssertCredentialValuesWereNotLogged(IEnumerable<LogMessage> messages)
    {
        var output = string.Join(
            Environment.NewLine,
            messages.Select(message => message.Message));

        Assert.Contains("Request header names:", output);
        Assert.Contains("Authorization", output);
        Assert.Contains("Proxy-Authorization", output);
        Assert.Contains("X-Api-Key", output);
        Assert.Contains("Cookie", output);
        Assert.Contains("X-Custom-Credential", output);

        foreach (var credentialValue in CredentialHeaders.Values)
        {
            Assert.False(
                output.Contains(credentialValue, StringComparison.Ordinal),
                $"Credential value '{credentialValue}' appeared in captured logs.");
        }
    }

    private sealed class StaticResponseHandler(
        HttpStatusCode statusCode,
        string body,
        string contentType = "application/json") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            });
        }
    }
}
