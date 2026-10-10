using ConduitLLM.Admin.Middleware;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace ConduitLLM.Tests.Core.Middleware;

[Trait("Category", "Unit")]
[Trait("Component", "Core")]
public class HttpMetricsMiddlewareBaseTests
{
    [Theory]
    [InlineData(false, 400, LogLevel.Warning)]
    [InlineData(false, 404, LogLevel.Warning)]
    [InlineData(false, 500, LogLevel.Error)]
    [InlineData(true, 400, LogLevel.Warning)]
    [InlineData(true, 404, LogLevel.Warning)]
    [InlineData(true, 500, LogLevel.Error)]
    public async Task InvokeAsync_EscapingException_IsLoggedOnceByResponseBoundary(
        bool gateway, int expectedStatus, LogLevel expectedLevel)
    {
        Exception exception = expectedStatus switch
        {
            400 => new ArgumentException("Invalid parameter"),
            404 => new ModelNotFoundException("missing-model"),
            _ => new Exception("Unexpected failure")
        };
        var logs = new List<(LogLevel Level, Exception Exception)>();
        var metrics = new TestMetricsMiddleware(
            _ => Task.FromException(exception),
            new RecordingLogger<HttpMetricsMiddlewareBase>(logs));
        var environment = Mock.Of<IWebHostEnvironment>(e => e.EnvironmentName == Environments.Production);
        ExceptionHandlingMiddlewareBase boundary = gateway
            ? new OpenAIErrorMiddleware(metrics.InvokeAsync, new RecordingLogger<OpenAIErrorMiddleware>(logs), environment)
            : new AdminExceptionMiddleware(metrics.InvokeAsync, new RecordingLogger<AdminExceptionMiddleware>(logs), environment);
        var context = new DefaultHttpContext();
        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        await boundary.InvokeAsync(context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        var log = Assert.Single(logs);
        Assert.Equal(expectedLevel, log.Level);
        Assert.Same(exception, log.Exception);
        Assert.Equal(exception.GetType().Name, Assert.Single(metrics.ErrorTypes));
        Assert.Equal(1, metrics.ExceptionHooks);
        Assert.Equal(1, metrics.StartedRequests);
        Assert.Equal(0, metrics.ActiveRequests);
        Assert.Equal(1, metrics.RecordedResponses);
        Assert.Same(responseBody, context.Response.Body);
        Assert.True(responseBody.Length > 0);
    }

    [Fact]
    public async Task InvokeAsync_Cancellation_PreservesExceptionAndMetricsWithoutLogging()
    {
        var exception = new TaskCanceledException();
        var logs = new List<(LogLevel Level, Exception Exception)>();
        var metrics = new TestMetricsMiddleware(
            _ => Task.FromException(exception),
            new RecordingLogger<HttpMetricsMiddlewareBase>(logs));
        var context = new DefaultHttpContext();
        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var thrown = await Assert.ThrowsAsync<TaskCanceledException>(() => metrics.InvokeAsync(context));

        Assert.Same(exception, thrown);
        Assert.Empty(logs);
        Assert.Equal(499, context.Response.StatusCode);
        Assert.Equal("client_cancelled", Assert.Single(metrics.ErrorTypes));
        Assert.Equal(0, metrics.ExceptionHooks);
        Assert.Equal(0, metrics.ActiveRequests);
        Assert.Equal(1, metrics.RecordedResponses);
        Assert.Same(responseBody, context.Response.Body);
    }

    private sealed class TestMetricsMiddleware(RequestDelegate next, ILogger logger)
        : HttpMetricsMiddlewareBase(next, logger)
    {
        public int StartedRequests { get; private set; }
        public int ActiveRequests { get; private set; }
        public int RecordedResponses { get; private set; }
        public int ExceptionHooks { get; private set; }
        public List<string> ErrorTypes { get; } = new();

        protected override double SlowRequestWarningThresholdSeconds => 0;
        protected override string GetNormalizedPath(HttpContext context) => "/test";
        protected override bool ShouldSkipMetrics(HttpContext context) => false;
        protected override void IncrementActiveRequests(string method, string path)
        {
            StartedRequests++;
            ActiveRequests++;
        }
        protected override void DecrementActiveRequests(string method, string path) => ActiveRequests--;
        protected override void RecordRequestSize(string method, string path, long bytes) { }
        protected override void RecordResponseMetrics(
            string method, string path, int statusCode, double durationSeconds, long responseBytes, HttpContext context)
            => RecordedResponses++;
        protected override void RecordError(string method, string path, int statusCode, string errorType)
            => ErrorTypes.Add(errorType);
        protected override void OnException(HttpContext context) => ExceptionHooks++;
    }

    private sealed class RecordingLogger<T>(List<(LogLevel Level, Exception Exception)> logs) : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter) => logs.Add((logLevel, exception));
    }
}
