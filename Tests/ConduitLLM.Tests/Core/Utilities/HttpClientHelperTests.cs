using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

using ConduitLLM.Core.Exceptions;
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

    private const string PrivateDiagnosticText = "private-provider-url-password-prompt-and-response";

    [Fact]
    public async Task JsonFailureDiagnosticDistinguishesAnActualUpstream502FromTransportStatusMetadata()
    {
        using var activity = new Activity("owned-diagnostic").SetIdFormat(ActivityIdFormat.W3C).Start();
        var upstreamLogger = new DiagnosticLogger();
        using var upstream = new HttpClient(new StaticResponseHandler(HttpStatusCode.BadGateway, PrivateDiagnosticText));
        var upstreamFailure = await Assert.ThrowsAsync<LLMCommunicationException>(() => SendForDiagnosticsAsync(upstream, upstreamLogger));
        Assert.Equal(HttpStatusCode.BadGateway, upstreamFailure.StatusCode);
        Assert.Equal(PrivateDiagnosticText, upstreamFailure.ResponseBody);
        AssertDiagnostic(upstreamLogger, "response-processing", "upstream-http-response", 502,
            expectedTrace: activity.TraceId.ToHexString());

        var transportLogger = new DiagnosticLogger();
        var transportFailure = new HttpRequestException(HttpRequestError.ConnectionError,
            PrivateDiagnosticText, statusCode: HttpStatusCode.BadGateway);
        using var transport = new HttpClient(new DelegateResponseHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(transportFailure)));
        var wrapped = await Assert.ThrowsAsync<LLMCommunicationException>(() => SendForDiagnosticsAsync(transport, transportLogger));
        Assert.Same(transportFailure, wrapped.InnerException);
        Assert.Null(wrapped.StatusCode);
        AssertDiagnostic(transportLogger, "send-buffered", "http-request", null, "connection-error",
            activity.TraceId.ToHexString());
    }

    [Fact]
    public async Task JsonFailureDiagnosticDoesNotTreatAResponseBodyBufferingFailureAsObservedHeaders()
    {
        var logger = new DiagnosticLogger();
        var failure = new HttpRequestException(HttpRequestError.ResponseEnded, PrivateDiagnosticText);
        using var client = new HttpClient(new DelegateResponseHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new FailingBufferedContent(failure) })));

        var wrapped = await Assert.ThrowsAsync<LLMCommunicationException>(() => SendForDiagnosticsAsync(client, logger));

        Assert.Same(failure, wrapped.InnerException);
        Assert.Null(wrapped.StatusCode);
        AssertDiagnostic(logger, "send-buffered", "http-request", null, "response-ended");
    }

    [Theory]
    [InlineData("{", "invalid-json")]
    [InlineData("null", "null-json-result")]
    public async Task JsonFailureDiagnosticRetainsObservedSuccessStatusForInvalidResponseJson(string body, string category)
    {
        var logger = new DiagnosticLogger();
        using var client = new HttpClient(new StaticResponseHandler(HttpStatusCode.OK, body));

        var failure = await Assert.ThrowsAsync<LLMCommunicationException>(() => SendForDiagnosticsAsync(client, logger));

        Assert.Null(failure.StatusCode); // The diagnostic does not change response mapping.
        if (category == "invalid-json") Assert.IsType<JsonException>(failure.InnerException);
        AssertDiagnostic(logger, "response-processing", category, 200);
    }

    [Fact]
    public async Task JsonFailureDiagnosticLocatesSerializationFailureBeforeAnySend()
    {
        var logger = new DiagnosticLogger();
        var handler = new DelegateResponseHandler((_, _) => throw new InvalidOperationException("No send is expected."));
        using var client = new HttpClient(handler);

        var failure = await Assert.ThrowsAsync<LLMCommunicationException>(() => HttpClientHelper.SendJsonRequestAsync(
            client, HttpMethod.Post, "https://private-provider.invalid/" + PrivateDiagnosticText,
            new Dictionary<string, object> { [PrivateDiagnosticText] = double.NaN },
            ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.DictionaryStringObject,
            ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.DictionaryStringObject,
            CredentialHeaders, logger));

        Assert.IsType<ArgumentException>(failure.InnerException);
        Assert.Equal(0, handler.Calls);
        AssertDiagnostic(logger, "request-create", "unexpected", null);
    }

    [Theory]
    [InlineData(true, "caller-cancelled")]
    [InlineData(false, "timeout")]
    public async Task JsonFailureDiagnosticSeparatesCallerCancellationFromTimeout(bool cancelCaller, string category)
    {
        using var caller = new CancellationTokenSource();
        var logger = new DiagnosticLogger();
        using var client = new HttpClient(new DelegateResponseHandler((_, _) =>
        {
            if (cancelCaller) caller.Cancel();
            return Task.FromException<HttpResponseMessage>(new TaskCanceledException(PrivateDiagnosticText));
        }));

        var failure = await Assert.ThrowsAsync<LLMCommunicationException>(() =>
            SendForDiagnosticsAsync(client, logger, caller.Token));

        Assert.IsAssignableFrom<TaskCanceledException>(failure.InnerException);
        AssertDiagnostic(logger, "send-buffered", category, null);
    }

    [Fact]
    public async Task JsonFailureDiagnosticUsesClosedCodesForUnexpectedFailuresAndUnknownRequestErrors()
    {
        foreach (var failure in new Exception[]
        {
            new InvalidOperationException(PrivateDiagnosticText),
            new HttpRequestException((HttpRequestError)int.MaxValue, PrivateDiagnosticText)
        })
        {
            var logger = new DiagnosticLogger();
            using var client = new HttpClient(new DelegateResponseHandler((_, _) => Task.FromException<HttpResponseMessage>(failure)));
            var wrapped = await Assert.ThrowsAsync<LLMCommunicationException>(() => SendForDiagnosticsAsync(client, logger));
            Assert.Same(failure, wrapped.InnerException);
            AssertDiagnostic(logger, "send-buffered", failure is HttpRequestException ? "http-request" : "unexpected",
                null, failure is HttpRequestException ? "unknown" : "none");
        }
    }

    [Fact]
    public async Task SuccessfulJsonRequestDoesNotEmitAFailureDiagnostic()
    {
        var logger = new DiagnosticLogger();
        using var client = new HttpClient(new StaticResponseHandler(HttpStatusCode.OK, "{}"));
        await SendForDiagnosticsAsync(client, logger);
        Assert.Empty(logger.Records);
    }

    private static Task<Dictionary<string, object>> SendForDiagnosticsAsync(
        HttpClient client, DiagnosticLogger logger, CancellationToken cancellationToken = default) =>
        HttpClientHelper.SendJsonRequestAsync(client, HttpMethod.Post,
            "https://private-provider.invalid/" + PrivateDiagnosticText,
            new Dictionary<string, string> { ["prompt"] = PrivateDiagnosticText },
            ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.DictionaryStringString,
            ConduitLLM.Core.Serialization.CoreHttpJsonContext.Default.DictionaryStringObject,
            CredentialHeaders, logger, cancellationToken);

    private static void AssertDiagnostic(DiagnosticLogger logger, string stage, string category,
        int? observedStatus, string requestError = "none", string? expectedTrace = null)
    {
        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Equal("ProviderJsonFailureDiagnostic", record.Event.Name);
        Assert.Equal(1702, record.Event.Id);
        Assert.Equal(new[] { "Category", "HttpRequestError", "ObservedStatus", "Stage", "TraceId", "{OriginalFormat}" },
            record.Fields.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(stage, record.Fields["Stage"]);
        Assert.Equal(category, record.Fields["Category"]);
        Assert.Equal(observedStatus, record.Fields["ObservedStatus"]);
        Assert.Equal(requestError, record.Fields["HttpRequestError"]);
        var trace = Assert.IsType<string>(record.Fields["TraceId"]);
        if (expectedTrace is not null) Assert.Equal(expectedTrace, trace);
        else Assert.True(trace == "none" || trace.Length == 32 && trace.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f'));
        Assert.True(record.Message.Length < 256);
        Assert.DoesNotContain(PrivateDiagnosticText, record.Message);
        Assert.DoesNotContain("https://", record.Message);
        foreach (var value in CredentialHeaders.Values) Assert.DoesNotContain(value, record.Message);
        Assert.All(record.Fields.Values.OfType<string>(), value => Assert.DoesNotContain(PrivateDiagnosticText, value));
    }

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

    private sealed class DelegateResponseHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }

    private sealed class FailingBufferedContent(Exception failure) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.FromException(failure);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed record DiagnosticRecord(LogLevel Level, EventId Event, Dictionary<string, object?> Fields, string Message);

    private sealed class DiagnosticLogger : ILogger
    {
        internal List<DiagnosticRecord> Records { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != 1702) return; // Existing detailed logs are deliberately outside this safe record.
            Assert.Null(exception);
            var fields = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state).ToDictionary();
            Records.Add(new(logLevel, eventId, fields, formatter(state, exception)));
        }
    }
}
