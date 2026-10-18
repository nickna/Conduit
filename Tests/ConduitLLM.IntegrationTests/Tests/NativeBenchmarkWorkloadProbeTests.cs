using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConduitLLM.IntegrationTests.Tests;

[CollectionDefinition("NativeBenchmarkWorkload", DisableParallelization = true)]
public sealed class NativeBenchmarkWorkloadCollection;

[Collection("NativeBenchmarkWorkload")]
public sealed class NativeBenchmarkWorkloadProbeTests
{
    private const string SecretVariable = "CONDUIT_NATIVE_BENCHMARK_API_KEY";
    private const string Secret = "native-benchmark-private-fixture-key";
    private const string SettingKey = "native_benchmark_café";
    private const string Content = "reply café 雪 🔎";

    [Theory]
    [InlineData("admin-setting-read")]
    [InlineData("gateway-chat-nonstream")]
    [InlineData("gateway-chat-sse")]
    public async Task AuthenticatedWorkloadsValidateCompleteUnicodeResponsesAndSeparateWarmupFromMeasuredCounts(string workload)
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(ValidResponse(workload));
        var configuration = Configuration(server, workload, requests: 3, concurrency: 2, warmup: 1);

        var (exitCode, report) = await server.RunAsync(configuration);

        Assert.Equal(0, exitCode);
        var row = AssertReport(report, workload, requested: 3, completed: 3, failures: 0, warmup: 1, concurrency: 2);
        Assert.Equal(4, server.Requests.Count);
        foreach (var request in server.Requests)
        {
            Assert.Equal("Bearer " + Secret, request.Headers["Authorization"]);
            Assert.False(request.Headers.ContainsKey("traceparent"));
            if (workload == "admin-setting-read")
            {
                Assert.Equal("GET", request.Method);
                Assert.Equal("/v1/admin/global-settings/by-key/" + Uri.EscapeDataString(SettingKey), request.Path);
                Assert.Empty(request.Body);
            }
            else
            {
                Assert.Equal("POST", request.Method);
                Assert.Equal("/v1/chat/completions", request.Path);
                using var body = JsonDocument.Parse(request.Body);
                Assert.Equal("native-model", body.RootElement.GetProperty("model").GetString());
                var message = Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray());
                Assert.Equal("user", message.GetProperty("role").GetString());
                Assert.Equal("prompt café 雪 🔎", message.GetProperty("content").GetString());
                Assert.Equal(workload == "gateway-chat-sse", body.RootElement.GetProperty("stream").GetBoolean());
                if (workload == "gateway-chat-sse")
                    Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            }
        }
        Assert.Equal(workload == "admin-setting-read" ? "GET" : "POST", row.GetProperty("method").GetString());
        Assert.False(report.GetRawText().Contains(Secret, StringComparison.Ordinal));
        Assert.False(report.GetRawText().Contains(Content, StringComparison.Ordinal));
        Assert.DoesNotContain(SettingKey, report.GetRawText());
    }

    [Theory]
    [InlineData("admin-setting-read")]
    [InlineData("gateway-chat-nonstream")]
    [InlineData("gateway-chat-sse")]
    public async Task OptionalClientDiagnosticsRetainEveryPhaseAndRequestWithoutChangingValidatedWorkload(string workload)
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(ValidResponse(workload));
        var warmupArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var measuredArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWarmup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMeasured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warmupWaiting = 0;
        var measuredWaiting = 0;
        server.BeforeResponse = async (ordinal, token) =>
        {
            if (ordinal <= 3)
            {
                if (Interlocked.Increment(ref warmupWaiting) == 3) warmupArrived.TrySetResult();
                await releaseWarmup.Task.WaitAsync(token);
            }
            else if (ordinal <= 6)
            {
                if (Interlocked.Increment(ref measuredWaiting) == 3) measuredArrived.TrySetResult();
                await releaseMeasured.Task.WaitAsync(token);
            }
        };
        var pending = server.RunWithDiagnosticsAsync(Configuration(server, workload, requests: 9, concurrency: 3, warmup: 3));
        try
        {
            await AwaitPhaseRequestsAsync(warmupArrived.Task, pending, server.Token);
            Assert.Equal(3, server.Requests.Count);
            releaseWarmup.TrySetResult();
            await AwaitPhaseRequestsAsync(measuredArrived.Task, pending, server.Token);
            Assert.Equal(6, server.Requests.Count);
            releaseMeasured.TrySetResult();

            var (exitCode, report, diagnostics) = await pending.WaitAsync(server.Token);

            Assert.Equal(0, exitCode);
            AssertReport(report, workload, requested: 9, completed: 9, failures: 0, warmup: 3, concurrency: 3);
            AssertDiagnostics(diagnostics, report, server, warmup: 3, requested: 9, completed: 9, concurrency: 3);
            foreach (var phase in diagnostics.GetProperty("phases").EnumerateArray())
                Assert.Equal(new[] { 0, 1, 2 }, phase.GetProperty("requests").EnumerateArray()
                    .Select(row => row.GetProperty("worker").GetInt32()).Distinct().Order().ToArray());
            Assert.Equal(12, server.Requests.Count);
            Assert.All(server.Requests, request => Assert.Equal("Bearer " + Secret, request.Headers["Authorization"]));
        }
        finally
        {
            releaseWarmup.TrySetResult();
            releaseMeasured.TrySetResult();
            // Observe the owned probe even when an earlier latch/assertion failed.
            // Its original failure remains the test's primary diagnostic.
            _ = await Record.ExceptionAsync(() => pending.WaitAsync(server.Token));
        }
    }

    [Theory]
    [InlineData("status")]
    [InlineData("json")]
    [InlineData("value")]
    [InlineData("timestamp")]
    public async Task DiagnosticFailuresPreserveFullAdminResponseValidationAndOnlyNumericFailureStages(string scenario)
    {
        using var secret = new EnvironmentSecret(Secret);
        var response = scenario switch
        {
            "status" => new Response("application/json", "{\"private\":\"" + Secret + "\"}", StatusCode: 401),
            "json" => new Response("application/json", "private invalid JSON " + Secret),
            "value" => new Response("application/json", AdminResponse(value: "private wrong value " + Secret)),
            _ => new Response("application/json", AdminResponse(updatedAt: "2024-01-01T00:00:00Z"))
        };
        await using var server = new LoopbackServer(response);

        var (exitCode, report, diagnostics) = await server.RunWithDiagnosticsAsync(
            Configuration(server, "admin-setting-read", requests: 3, concurrency: 2));

        Assert.Equal(1, exitCode);
        AssertReport(report, "admin-setting-read", requested: 3, completed: 0, failures: 3, concurrency: 2);
        AssertDiagnostics(diagnostics, report, server, warmup: 0, requested: 3, completed: 0, concurrency: 2);
        foreach (var request in diagnostics.GetProperty("phases")[1].GetProperty("requests").EnumerateArray())
        {
            Assert.False(request.GetProperty("succeeded").GetBoolean());
            Assert.NotEqual(JsonValueKind.Null, request.GetProperty("headersOffsetTicks").ValueKind);
            Assert.Equal(JsonValueKind.Null, request.GetProperty("validatedOffsetTicks").ValueKind);
            if (scenario == "status")
            {
                Assert.Equal(JsonValueKind.Null, request.GetProperty("bodyOffsetTicks").ValueKind);
                Assert.Equal(JsonValueKind.Null, request.GetProperty("responseBytes").ValueKind);
            }
            else
            {
                Assert.NotEqual(JsonValueKind.Null, request.GetProperty("bodyOffsetTicks").ValueKind);
                Assert.Equal(Encoding.UTF8.GetByteCount(response.Body), request.GetProperty("responseBytes").GetInt32());
            }
        }
    }

    [Fact]
    public async Task DiagnosticRequestDeadlineRetainsFailedIntervalWithoutInventingResponseStages()
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(new Response("application/json", AdminResponse(), Hold: true));
        var configuration = Configuration(server, "admin-setting-read");
        configuration["timeoutSeconds"] = 1;
        var pending = server.RunWithDiagnosticsAsync(configuration);
        await server.RequestReceived.WaitAsync(server.Token);

        var (exitCode, report, diagnostics) = await pending.WaitAsync(server.Token);

        Assert.Equal(1, exitCode);
        AssertReport(report, "admin-setting-read", requested: 1, completed: 0, failures: 1, timeout: 1);
        AssertDiagnostics(diagnostics, report, server, warmup: 0, requested: 1, completed: 0, concurrency: 1);
        var request = Assert.Single(diagnostics.GetProperty("phases")[1].GetProperty("requests").EnumerateArray());
        foreach (var name in new[] { "headersOffsetTicks", "bodyOffsetTicks", "validatedOffsetTicks", "responseBytes" })
            Assert.Equal(JsonValueKind.Null, request.GetProperty(name).ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticCallerCancellationDuringEitherPhaseEmitsNeitherPartialOutput(bool duringWarmup)
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(new Response("application/json", AdminResponse(), Hold: true));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(server.Token);
        using var output = new MemoryStream();
        using var diagnostics = new MemoryStream();
        await server.WriteConfigurationAsync(Configuration(server, "admin-setting-read", warmup: duringWarmup ? 1 : 0));
        var pending = NativeBenchmarkWorkloadProbe.RunAsync(server.ConfigurationPath, output, caller.Token, diagnostics);
        await server.RequestReceived.WaitAsync(server.Token);

        caller.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(server.Token));
        Assert.DoesNotContain(Secret, exception.ToString());
        Assert.Equal(0, output.Length);
        Assert.Equal(0, diagnostics.Length);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task DiagnosticConfigurationFailureEmitsNeitherPartialOutput()
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(new Response("application/json", "private invalid " + Secret));
        using var output = new MemoryStream();
        using var diagnostics = new MemoryStream();
        var configuration = Configuration(server, "admin-setting-read", warmup: 1);
        configuration["private-" + Secret] = true;
        await server.WriteConfigurationAsync(configuration);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => NativeBenchmarkWorkloadProbe.RunAsync(
            server.ConfigurationPath, output, server.Token, diagnostics));

        Assert.DoesNotContain(Secret, exception.ToString());
        Assert.Equal(0, output.Length);
        Assert.Equal(0, diagnostics.Length);
        Assert.Empty(server.Requests);
        Assert.Equal(0, server.AcceptedConnections);
    }

    [Theory]
    [InlineData("admin-setting-read", "status", "http-status")]
    [InlineData("admin-setting-read", "json", "response-validation")]
    [InlineData("gateway-chat-nonstream", "content", "response-validation")]
    [InlineData("gateway-chat-nonstream", "media-type", "response-validation")]
    [InlineData("gateway-chat-sse", "content", "response-validation")]
    [InlineData("gateway-chat-sse", "size", "response-validation")]
    [InlineData("admin-setting-read", "transport", "transport")]
    [InlineData("admin-setting-read", "deadline", "request-deadline")]
    public async Task CompletedFailedWarmupEmitsOnlyBoundedClosedFailureDiagnostics(
        string workload, string scenario, string category)
    {
        using var secret = new EnvironmentSecret(Secret);
        var valid = ValidResponse(workload);
        var response = scenario switch
        {
            "status" => valid with { StatusCode = 401, Body = "private invalid " + Secret },
            "json" => valid with { Body = "private invalid " + Secret },
            "content" => valid with { Body = workload == "gateway-chat-sse"
                ? SseResponse(includeDone: false) : ChatResponse("private wrong value " + Secret) },
            "media-type" => valid with { ContentType = "text/private-" + Secret },
            "size" => valid with { Body = new string('x', NativeBenchmarkWorkloadProbe.MaximumResponseBytes + 1) },
            "transport" => valid with { Truncated = true },
            "deadline" => valid with { Hold = true },
            _ => throw new InvalidOperationException()
        };
        await using var server = new LoopbackServer(response);
        using var output = new MemoryStream();
        using var diagnostics = new MemoryStream();
        var configuration = Configuration(server, workload, requests: 3, warmup: 1);
        configuration["timeoutSeconds"] = 1;
        await server.WriteConfigurationAsync(configuration);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeBenchmarkWorkloadProbe.RunAsync(
            server.ConfigurationPath, output, server.Token, diagnostics));

        Assert.Equal("Benchmark warm-up failed.", exception.Message);
        Assert.Equal(0, output.Length);
        Assert.Single(server.Requests);
        using var document = JsonDocument.Parse(diagnostics.ToArray());
        AssertFailedWarmupDiagnostics(document.RootElement, server, workload, requested: 1, completed: 0,
            new Dictionary<string, int> { [category] = 1 });
        Assert.InRange(diagnostics.Length, 1, NativeBenchmarkWorkloadProbe.MaximumFailedWarmupDiagnosticBytes);
    }

    [Fact]
    public async Task FailedWarmupCountsEveryValidatedSuccessAndClosedFailureWithoutStartingMeasuredRequests()
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(ValidResponse("admin-setting-read"));
        server.BeforeResponse = (ordinal, _) =>
        {
            server.Response = ordinal switch
            {
                2 => new Response("application/json", "private invalid " + Secret, StatusCode: 502),
                3 => new Response("application/json", AdminResponse("private wrong value " + Secret)),
                _ => ValidResponse("admin-setting-read")
            };
            return Task.CompletedTask;
        };
        using var output = new MemoryStream();
        using var diagnostics = new MemoryStream();
        await server.WriteConfigurationAsync(Configuration(server, "admin-setting-read", requests: 3, concurrency: 1, warmup: 4));

        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeBenchmarkWorkloadProbe.RunAsync(
            server.ConfigurationPath, output, server.Token, diagnostics));

        Assert.Equal(0, output.Length);
        Assert.Equal(4, server.Requests.Count);
        using var document = JsonDocument.Parse(diagnostics.ToArray());
        AssertFailedWarmupDiagnostics(document.RootElement, server, "admin-setting-read", requested: 4, completed: 2,
            new Dictionary<string, int> { ["http-status"] = 1, ["response-validation"] = 1 });
    }

    [Fact]
    public async Task InterruptedWarmupWithAlreadyCompletedFailuresEmitsNoDiagnosticDocument()
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(new Response("application/json", "private invalid " + Secret));
        var secondArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforeResponse = async (ordinal, token) =>
        {
            if (ordinal != 2) return;
            secondArrived.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(server.Token);
        using var output = new MemoryStream();
        using var diagnostics = new MemoryStream();
        await server.WriteConfigurationAsync(Configuration(server, "admin-setting-read", requests: 3, concurrency: 1, warmup: 3));
        var pending = NativeBenchmarkWorkloadProbe.RunAsync(server.ConfigurationPath, output, caller.Token, diagnostics);
        await AwaitPhaseRequestsAsync(secondArrived.Task, pending, server.Token);

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(server.Token));
        Assert.Equal(0, output.Length);
        Assert.Equal(0, diagnostics.Length);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task FailedWarmupDiagnosticSizeIsIndependentOfPrivateInputAndRequestRows()
    {
        using var secret = new EnvironmentSecret(Secret);
        var privateText = Secret + new string('x', 16_384 - Secret.Length);
        await using var server = new LoopbackServer(new Response("application/json", "private invalid " + privateText));
        using var output = new MemoryStream();
        using var diagnostics = new MemoryStream();
        var configuration = Configuration(server, "gateway-chat-nonstream", requests: 4, concurrency: 4, warmup: 64);
        foreach (var field in new[] { "model", "prompt", "expectedContent" }) configuration[field] = privateText;
        await server.WriteConfigurationAsync(configuration);

        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeBenchmarkWorkloadProbe.RunAsync(
            server.ConfigurationPath, output, server.Token, diagnostics));

        Assert.Equal(0, output.Length);
        Assert.Equal(64, server.Requests.Count);
        Assert.InRange(diagnostics.Length, 1, NativeBenchmarkWorkloadProbe.MaximumFailedWarmupDiagnosticBytes);
        using var document = JsonDocument.Parse(diagnostics.ToArray());
        AssertFailedWarmupDiagnostics(document.RootElement, server, "gateway-chat-nonstream", requested: 64, completed: 0,
            new Dictionary<string, int> { ["response-validation"] = 64 });
        Assert.DoesNotContain(privateText, document.RootElement.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticOutputMustBeWritableAndSeparateBeforeAnyRequest(bool sharedOutput)
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(ValidResponse("admin-setting-read"));
        await server.WriteConfigurationAsync(Configuration(server, "admin-setting-read"));
        using var output = new MemoryStream();
        using var readOnly = new MemoryStream([], writable: false);

        await Assert.ThrowsAsync<ArgumentException>(() => NativeBenchmarkWorkloadProbe.RunAsync(
            server.ConfigurationPath, output, server.Token, sharedOutput ? output : readOnly));

        Assert.Equal(0, output.Length);
        Assert.Equal(0, server.AcceptedConnections);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\r", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    public async Task StreamingDiagnosticsObserveFramingWithoutChangingCompleteUnicodeValidation(string newline, bool chunked)
    {
        using var secret = new EnvironmentSecret(Secret);
        // Comments and empty data frames are not nonempty data events. JSON events
        // span multiple data lines, while seven-byte socket writes split UTF8 and framing.
        var body = "data:\n\n" + SseResponse().Replace("data: {", "data: {\ndata: ", StringComparison.Ordinal);
        body = body.Replace("\n", newline, StringComparison.Ordinal);
        await using var server = new LoopbackServer(new Response("text/event-stream", body, Chunked: chunked));

        var (exitCode, report, diagnostics) = await server.RunWithDiagnosticsAsync(Configuration(server, "gateway-chat-sse"));

        Assert.Equal(0, exitCode);
        AssertReport(report, "gateway-chat-sse", requested: 1, completed: 1, failures: 0);
        AssertDiagnostics(diagnostics, report, server, warmup: 0, requested: 1, completed: 1, concurrency: 1);
        var request = Assert.Single(diagnostics.GetProperty("phases")[1].GetProperty("requests").EnumerateArray());
        var streaming = request.GetProperty("streaming");
        Assert.Equal(6, streaming.GetProperty("dataEventCount").GetInt32());
        Assert.Equal(1, streaming.GetProperty("doneEventCount").GetInt32());
        Assert.Equal(Encoding.UTF8.GetByteCount(body), request.GetProperty("responseBytes").GetInt32());
    }

    [Fact]
    public async Task StreamingDiagnosticsStillReadToEofAfterDoneAndPreserveDeadlineFailure()
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(new Response("text/event-stream", SseResponse(), HoldAfterBody: true));
        var configuration = Configuration(server, "gateway-chat-sse");
        configuration["timeoutSeconds"] = 1;

        var (exitCode, report, diagnostics) = await server.RunWithDiagnosticsAsync(configuration);

        Assert.Equal(1, exitCode);
        AssertReport(report, "gateway-chat-sse", requested: 1, completed: 0, failures: 1, timeout: 1);
        AssertDiagnostics(diagnostics, report, server, warmup: 0, requested: 1, completed: 0, concurrency: 1);
        var request = Assert.Single(diagnostics.GetProperty("phases")[1].GetProperty("requests").EnumerateArray());
        var streaming = request.GetProperty("streaming");
        Assert.Equal(6, streaming.GetProperty("dataEventCount").GetInt32());
        Assert.Equal(1, streaming.GetProperty("doneEventCount").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, streaming.GetProperty("doneEventOffsetTicks").ValueKind);
        foreach (var field in new[] { "bodyOffsetTicks", "validatedOffsetTicks", "responseBytes" })
            Assert.Equal(JsonValueKind.Null, request.GetProperty(field).ValueKind);
    }

    [Theory]
    [InlineData("missing-usage", 5, 1)]
    [InlineData("missing-done", 5, 0)]
    [InlineData("duplicate-done", 7, 2)]
    [InlineData("after-done", 7, 1)]
    public async Task StreamingObservationDoesNotAcceptInvalidContentUsageOrTerminalFraming(string scenario, int events, int done)
    {
        using var secret = new EnvironmentSecret(Secret);
        var body = scenario switch
        {
            "missing-usage" => SseResponse(includeUsage: false),
            "missing-done" => SseResponse(includeDone: false),
            "duplicate-done" => SseResponse() + "data: [DONE]\n\n",
            _ => SseResponse() + "data: " + Chunk("private " + Secret, role: false) + "\n\n"
        };
        await using var server = new LoopbackServer(new Response("text/event-stream", body));

        var (exitCode, report, diagnostics) = await server.RunWithDiagnosticsAsync(Configuration(server, "gateway-chat-sse"));

        Assert.Equal(1, exitCode);
        AssertDiagnostics(diagnostics, report, server, warmup: 0, requested: 1, completed: 0, concurrency: 1);
        var request = Assert.Single(diagnostics.GetProperty("phases")[1].GetProperty("requests").EnumerateArray());
        var streaming = request.GetProperty("streaming");
        Assert.Equal(events, streaming.GetProperty("dataEventCount").GetInt32());
        Assert.Equal(done, streaming.GetProperty("doneEventCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("validatedOffsetTicks").ValueKind);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("missing-fields")]
    [InlineData("wrong-content")]
    [InlineData("wrong-usage")]
    [InlineData("invalid-admin-time")]
    [InlineData("sse-premature-eof")]
    [InlineData("sse-no-usage")]
    [InlineData("sse-no-done")]
    [InlineData("sse-after-done")]
    [InlineData("unauthorized")]
    public async Task HttpSuccessRequiresValidCompleteWorkloadAndHttpFailureProducesOnlyAggregateFailureEvidence(string scenario)
    {
        using var secret = new EnvironmentSecret(Secret);
        var workload = scenario.StartsWith("sse-", StringComparison.Ordinal) ? "gateway-chat-sse"
            : scenario == "invalid-admin-time" ? "admin-setting-read" : "gateway-chat-nonstream";
        var response = scenario switch
        {
            "invalid-json" => new Response("application/json", Secret + " invalid private diagnostics"),
            "missing-fields" => new Response("application/json", "{}"),
            "wrong-content" => new Response("application/json", ChatResponse("private unexpected " + Secret)),
            "wrong-usage" => new Response("application/json", ChatResponse(completionTokens: 6)),
            "invalid-admin-time" => new Response("application/json", AdminResponse(updatedAt: "2024-01-01T00:00:00Z")),
            "sse-premature-eof" => new Response("text/event-stream", SseResponse().TrimEnd('\n')),
            "sse-no-usage" => new Response("text/event-stream", SseResponse(includeUsage: false)),
            "sse-no-done" => new Response("text/event-stream", SseResponse(includeDone: false)),
            "sse-after-done" => new Response("text/event-stream", SseResponse() + "data: " + Chunk("", role: false) + "\n\n"),
            _ => new Response("application/json", "{\"private\":\"" + Secret + "\"}", StatusCode: 401)
        };
        await using var server = new LoopbackServer(response);

        var (exitCode, report) = await server.RunAsync(Configuration(server, workload));

        Assert.Equal(1, exitCode);
        AssertReport(report, workload, requested: 1, completed: 0, failures: 1);
        Assert.Single(server.Requests);
        Assert.DoesNotContain(Secret, report.GetRawText());
        Assert.DoesNotContain("private unexpected", report.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseLimitRejectsOtherwiseValidJsonByDeclaredLengthAndAccumulatedChunkedBytes(bool chunked)
    {
        using var secret = new EnvironmentSecret(Secret);
        var body = ChatResponse();
        body += new string(' ', NativeBenchmarkWorkloadProbe.MaximumResponseBytes + 1 - Encoding.UTF8.GetByteCount(body));
        await using var server = new LoopbackServer(new Response("application/json", body, Chunked: chunked));

        var (exitCode, report) = await server.RunAsync(Configuration(server, "gateway-chat-nonstream"));

        Assert.Equal(1, exitCode);
        AssertReport(report, "gateway-chat-nonstream", requested: 1, completed: 0, failures: 1);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task PerRequestDeadlineBoundsAReceivedRequestWithoutAResponse()
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(new Response("application/json", ChatResponse(), Hold: true));
        var configuration = Configuration(server, "gateway-chat-nonstream");
        configuration["timeoutSeconds"] = 1;
        var pending = server.RunAsync(configuration);
        await server.RequestReceived.WaitAsync(server.Token);

        var (exitCode, report) = await pending.WaitAsync(server.Token);

        Assert.Equal(1, exitCode);
        AssertReport(report, "gateway-chat-nonstream", requested: 1, completed: 0, failures: 1, timeout: 1);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task CallerCancellationAfterActualRequestProducesNoPartialReport()
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(new Response("text/event-stream", SseResponse(), Hold: true));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(server.Token);
        using var output = new MemoryStream();
        await server.WriteConfigurationAsync(Configuration(server, "gateway-chat-sse"));
        var pending = NativeBenchmarkWorkloadProbe.RunAsync(server.ConfigurationPath, output, caller.Token);
        await server.RequestReceived.WaitAsync(server.Token);

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(server.Token));
        Assert.Equal(0, output.Length);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task TruncatedRequestRemainsObservableDuringOwnedServerCleanupWithoutLeakingCredentials()
    {
        var server = new LoopbackServer(ValidResponse("gateway-chat-nonstream"));
        Exception? cleanupFailure = null;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Endpoint.Port, server.Token);
            using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "POST /v1/chat/completions HTTP/1.1\r\nContent-Length: 4\r\nAuthorization: Bearer " + Secret + "\r\n\r\nx"), server.Token);
            client.Client.Shutdown(SocketShutdown.Send);
            await server.FailureObserved.WaitAsync(server.Token);
        }
        finally
        {
            cleanupFailure = await Record.ExceptionAsync(() => server.DisposeAsync().AsTask());
        }
        Assert.NotNull(cleanupFailure);
        Assert.Contains(nameof(EndOfStreamException), cleanupFailure.ToString());
        Assert.DoesNotContain(Secret, cleanupFailure.ToString());
        Assert.Empty(server.Requests);
        Assert.False(Directory.Exists(Path.GetDirectoryName(server.ConfigurationPath)));
    }

    [Fact]
    public async Task FailedWarmupStopsBeforeMeasurementAndEmitsNoReport()
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(new Response("application/json", "{\"secret\":\"" + Secret + "\"}"));
        using var output = new MemoryStream();
        await server.WriteConfigurationAsync(Configuration(server, "gateway-chat-nonstream", warmup: 1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeBenchmarkWorkloadProbe.RunAsync(server.ConfigurationPath, output, server.Token));

        Assert.DoesNotContain(Secret, exception.ToString());
        Assert.Equal(0, output.Length);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData("unknown-workload")]
    [InlineData("zero-requests")]
    [InlineData("missing-requests")]
    [InlineData("string-requests")]
    [InlineData("fractional-requests")]
    [InlineData("too-many-requests")]
    [InlineData("zero-concurrency")]
    [InlineData("too-many-concurrency")]
    [InlineData("concurrency-exceeds-requests")]
    [InlineData("zero-timeout")]
    [InlineData("too-large-timeout")]
    [InlineData("negative-warmup")]
    [InlineData("unknown-property")]
    [InlineData("wrong-workload-field")]
    [InlineData("uri-userinfo")]
    [InlineData("uri-query")]
    [InlineData("uri-path")]
    [InlineData("uri-fragment")]
    public async Task InvalidConfigurationRejectsBeforeTcpAndDoesNotExposeSecrets(string scenario)
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(ValidResponse("gateway-chat-nonstream"));
        var configuration = Configuration(server, "gateway-chat-nonstream");
        switch (scenario)
        {
            case "unknown-workload": configuration["workload"] = "private-" + Secret; break;
            case "zero-requests": configuration["requests"] = 0; break;
            case "missing-requests": configuration.Remove("requests"); break;
            case "string-requests": configuration["requests"] = "1"; break;
            case "fractional-requests": configuration["requests"] = 1.5; break;
            case "too-many-requests": configuration["requests"] = NativeBenchmarkWorkloadProbe.MaximumRequests + 1; break;
            case "zero-concurrency": configuration["concurrency"] = 0; break;
            case "too-many-concurrency":
                configuration["requests"] = NativeBenchmarkWorkloadProbe.MaximumConcurrency + 1;
                configuration["concurrency"] = NativeBenchmarkWorkloadProbe.MaximumConcurrency + 1;
                break;
            case "concurrency-exceeds-requests": configuration["concurrency"] = 2; break;
            case "zero-timeout": configuration["timeoutSeconds"] = 0; break;
            case "too-large-timeout": configuration["timeoutSeconds"] = NativeBenchmarkWorkloadProbe.MaximumTimeoutSeconds + 1; break;
            case "negative-warmup": configuration["warmupRequests"] = -1; break;
            case "unknown-property": configuration["apiKey"] = Secret; break;
            case "wrong-workload-field": configuration["settingKey"] = Secret; break;
            case "uri-userinfo": configuration["baseUrl"] = server.Endpoint.AbsoluteUri.Replace("http://", "http://" + Secret + "@"); break;
            case "uri-query": configuration["baseUrl"] = server.Endpoint.AbsoluteUri + "?key=" + Secret; break;
            case "uri-path": configuration["baseUrl"] = server.Endpoint.AbsoluteUri + Secret; break;
            case "uri-fragment": configuration["baseUrl"] = server.Endpoint.AbsoluteUri + "#" + Secret; break;
        }
        await AssertConfigurationRejectedAsync(server, configuration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" \t")]
    public async Task AbsentOrBlankEnvironmentCredentialRejectsBeforeTcp(string? value)
    {
        using var secret = new EnvironmentSecret(value);
        await using var server = new LoopbackServer(ValidResponse("admin-setting-read"));

        await AssertConfigurationRejectedAsync(server, Configuration(server, "admin-setting-read"));
    }

    [Fact]
    public async Task ConfigurationByteLimitRejectsOtherwiseValidDocumentBeforeTcp()
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(ValidResponse("gateway-chat-nonstream"));
        var json = Configuration(server, "gateway-chat-nonstream").ToJsonString();
        json += new string(' ', NativeBenchmarkWorkloadProbe.MaximumConfigurationBytes + 1 - Encoding.UTF8.GetByteCount(json));
        await File.WriteAllTextAsync(server.ConfigurationPath, json, new UTF8Encoding(false), server.Token);
        using var output = new MemoryStream();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            NativeBenchmarkWorkloadProbe.RunAsync(server.ConfigurationPath, output, server.Token));

        Assert.DoesNotContain(Secret, exception.ToString());
        Assert.Equal(0, output.Length);
        Assert.Equal(0, server.AcceptedConnections);
    }

    [Fact]
    public async Task DuplicateRawConfigurationPropertyRejectsBeforeTcpEvenWhenValuesAgree()
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(ValidResponse("gateway-chat-nonstream"));
        var json = Configuration(server, "gateway-chat-nonstream").ToJsonString();
        json = json[..^1] + ",\"requests\":1}";
        await File.WriteAllTextAsync(server.ConfigurationPath, json, new UTF8Encoding(false), server.Token);
        using var output = new MemoryStream();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeBenchmarkWorkloadProbe.RunAsync(server.ConfigurationPath, output, server.Token));

        Assert.DoesNotContain(Secret, exception.ToString());
        Assert.Equal(0, output.Length);
        Assert.Equal(0, server.AcceptedConnections);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("admin-setting-read")]
    [InlineData("gateway-chat-nonstream")]
    public async Task FingerprintIgnoresTransportAndRunControlsButChangesWhenValidatedInputsChange(string workload)
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var first = new LoopbackServer(ValidResponse(workload));
        var (originalExitCode, original) = await first.RunAsync(Configuration(first, workload));
        Assert.Equal(0, originalExitCode);
        var fingerprint = Row(original).GetProperty("inputFingerprint").GetString();
        await using var second = new LoopbackServer(ValidResponse(workload));
        var controls = Configuration(second, workload, requests: 2, concurrency: 2, warmup: 1);
        controls["timeoutSeconds"] = 2;
        Environment.SetEnvironmentVariable(SecretVariable, "other-private-fixture-key");

        var (equivalentExitCode, equivalent, diagnostics) = await second.RunWithDiagnosticsAsync(controls);

        Assert.Equal(0, equivalentExitCode);
        Assert.Equal(fingerprint, Row(equivalent).GetProperty("inputFingerprint").GetString());
        AssertDiagnostics(diagnostics, equivalent, second, warmup: 1, requested: 2, completed: 2, concurrency: 2);
        Assert.DoesNotContain("other-private-fixture-key", diagnostics.GetRawText());
        Assert.All(second.Requests, request => Assert.Equal("Bearer other-private-fixture-key", request.Headers["Authorization"]));
        if (workload == "admin-setting-read")
        {
            controls["expectedValue"] = "different café";
            second.Response = new Response("application/json", AdminResponse(value: "different café"));
        }
        else
        {
            controls["prompt"] = "different prompt 雪";
            controls["expectedCompletionTokens"] = 6;
            second.Response = new Response("application/json", ChatResponse(completionTokens: 6));
        }

        var (exitCode, different) = await second.RunAsync(controls);

        Assert.Equal(0, exitCode);
        Assert.NotEqual(fingerprint, Row(different).GetProperty("inputFingerprint").GetString());
    }

    private static async Task AssertConfigurationRejectedAsync(LoopbackServer server, JsonObject configuration)
    {
        await server.WriteConfigurationAsync(configuration);
        using var output = new MemoryStream();
        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            NativeBenchmarkWorkloadProbe.RunAsync(server.ConfigurationPath, output, server.Token));
        Assert.DoesNotContain(Secret, exception.ToString());
        Assert.Equal(0, output.Length);
        Assert.Equal(0, server.AcceptedConnections);
        Assert.Empty(server.Requests);
    }

    private static JsonObject Configuration(LoopbackServer server, string workload, int requests = 1, int concurrency = 1, int warmup = 0)
    {
        var result = new JsonObject
        {
            ["workload"] = workload, ["baseUrl"] = server.Endpoint.AbsoluteUri,
            ["requests"] = requests, ["concurrency"] = concurrency,
            ["warmupRequests"] = warmup, ["timeoutSeconds"] = 5
        };
        if (workload == "admin-setting-read")
        {
            result["settingKey"] = SettingKey;
            result["expectedValue"] = Content;
        }
        else
        {
            result["model"] = "native-model";
            result["prompt"] = "prompt café 雪 🔎";
            result["expectedContent"] = Content;
            result["expectedPromptTokens"] = 10;
            result["expectedCompletionTokens"] = 5;
        }
        return result;
    }

    private static JsonElement AssertReport(JsonElement report, string workload, int requested, int completed, int failures,
        int warmup = 0, int concurrency = 1, int timeout = 5)
    {
        AssertKeys(report, "schemaVersion", "service", "generatedAt", "workloads");
        Assert.Equal(2, report.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(workload == "admin-setting-read" ? "admin" : "gateway", report.GetProperty("service").GetString());
        Assert.True(report.GetProperty("generatedAt").TryGetDateTimeOffset(out var generatedAt));
        Assert.Equal(TimeSpan.Zero, generatedAt.Offset);
        var row = Row(report);
        AssertKeys(row, "id", "method", "path", "authenticated", "inputFingerprint", "requested", "executed",
            "completed", "failures", "warmupRequests", "concurrency", "timeoutSeconds", "elapsedMilliseconds",
            "throughputPerSecond", "p50Milliseconds", "p95Milliseconds", "p99Milliseconds");
        Assert.Equal(workload, row.GetProperty("id").GetString());
        Assert.Equal(workload == "admin-setting-read" ? "/v1/admin/global-settings/by-key/{key}" : "/v1/chat/completions",
            row.GetProperty("path").GetString());
        Assert.True(row.GetProperty("authenticated").GetBoolean());
        Assert.Equal(requested, row.GetProperty("requested").GetInt32());
        Assert.Equal(requested, row.GetProperty("executed").GetInt32());
        Assert.Equal(completed, row.GetProperty("completed").GetInt32());
        Assert.Equal(failures, row.GetProperty("failures").GetInt32());
        Assert.Equal(warmup, row.GetProperty("warmupRequests").GetInt32());
        Assert.Equal(concurrency, row.GetProperty("concurrency").GetInt32());
        Assert.Equal(timeout, row.GetProperty("timeoutSeconds").GetInt32());
        Assert.Matches("^[0-9a-f]{64}$", row.GetProperty("inputFingerprint").GetString()!);
        var elapsed = row.GetProperty("elapsedMilliseconds").GetDouble();
        Assert.True(double.IsFinite(elapsed) && elapsed > 0);
        var throughput = row.GetProperty("throughputPerSecond").GetDouble();
        Assert.True(double.IsFinite(throughput) && throughput >= 0);
        var p50 = row.GetProperty("p50Milliseconds").GetDouble();
        var p95 = row.GetProperty("p95Milliseconds").GetDouble();
        var p99 = row.GetProperty("p99Milliseconds").GetDouble();
        Assert.True(double.IsFinite(p50) && p50 >= 0 && p50 <= p95 && p95 <= p99 && double.IsFinite(p99));
        return row;
    }

    private static void AssertDiagnostics(JsonElement diagnostics, JsonElement report, LoopbackServer server,
        int warmup, int requested, int completed, int concurrency)
    {
        AssertKeys(diagnostics, "schemaVersion", "kind", "scope", "service", "workload", "inputFingerprint",
            "stopwatchFrequency", "runtimeTickFrequency", "phases");
        var isStream = diagnostics.GetProperty("workload").GetString() == "gateway-chat-sse";
        Assert.Equal(isStream ? 2 : 1, diagnostics.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("conduit-native-benchmark-workload-diagnostics", diagnostics.GetProperty("kind").GetString());
        Assert.Equal("client", diagnostics.GetProperty("scope").GetString());
        Assert.Equal(report.GetProperty("service").GetString(), diagnostics.GetProperty("service").GetString());
        Assert.Equal(Row(report).GetProperty("id").GetString(), diagnostics.GetProperty("workload").GetString());
        Assert.Equal(Row(report).GetProperty("inputFingerprint").GetString(), diagnostics.GetProperty("inputFingerprint").GetString());
        var frequency = diagnostics.GetProperty("stopwatchFrequency").GetInt64();
        Assert.Equal(System.Diagnostics.Stopwatch.Frequency, frequency);
        Assert.Equal(TimeSpan.TicksPerSecond, diagnostics.GetProperty("runtimeTickFrequency").GetInt64());
        var phases = diagnostics.GetProperty("phases").EnumerateArray().ToArray();
        Assert.Equal(2, phases.Length);
        var expectedRequests = new[] { warmup, requested };
        var expectedCompleted = new[] { warmup, completed };
        var carriers = new HashSet<string>(StringComparer.Ordinal);
        var traceIds = new HashSet<string>(StringComparer.Ordinal);
        var spanIds = new HashSet<string>(StringComparer.Ordinal);
        long previousPhaseEnd = 0;
        for (var phaseIndex = 0; phaseIndex < phases.Length; phaseIndex++)
        {
            var phase = phases[phaseIndex];
            AssertKeys(phase, "name", "requested", "executed", "completed", "failures", "elapsedTicks",
                "startOffsetTicks", "endOffsetTicks", "runtimeBefore", "runtimeAfter", "requests");
            Assert.Equal(phaseIndex == 0 ? "warmup" : "measured", phase.GetProperty("name").GetString());
            Assert.Equal(expectedRequests[phaseIndex], phase.GetProperty("requested").GetInt32());
            Assert.Equal(expectedRequests[phaseIndex], phase.GetProperty("executed").GetInt32());
            Assert.Equal(expectedCompleted[phaseIndex], phase.GetProperty("completed").GetInt32());
            Assert.Equal(expectedRequests[phaseIndex] - expectedCompleted[phaseIndex], phase.GetProperty("failures").GetInt32());
            var phaseStart = phase.GetProperty("startOffsetTicks").GetInt64();
            var phaseEnd = phase.GetProperty("endOffsetTicks").GetInt64();
            var elapsedTicks = phase.GetProperty("elapsedTicks").GetInt64();
            Assert.InRange(phaseStart, previousPhaseEnd, long.MaxValue);
            Assert.InRange(phaseEnd, phaseStart, long.MaxValue);
            Assert.InRange(elapsedTicks, 0, phaseEnd - phaseStart);
            if (expectedRequests[phaseIndex] > 0) Assert.True(elapsedTicks > 0);
            previousPhaseEnd = phaseEnd;
            AssertRuntimeCounters(phase.GetProperty("runtimeBefore"), phase.GetProperty("runtimeAfter"));
            var requests = phase.GetProperty("requests").EnumerateArray().ToArray();
            Assert.Equal(expectedRequests[phaseIndex], requests.Length);
            Assert.Equal(Enumerable.Range(0, requests.Length), requests.Select(row => row.GetProperty("index").GetInt32()));
            Assert.Equal(expectedCompleted[phaseIndex], requests.Count(row => row.GetProperty("succeeded").GetBoolean()));
            var workerEnds = new Dictionary<int, long>();
            foreach (var request in requests)
            {
                var requestKeys = new[] { "index", "worker", "traceId", "spanId", "startOffsetTicks", "headersOffsetTicks",
                    "bodyOffsetTicks", "validatedOffsetTicks", "endOffsetTicks", "responseBytes", "succeeded" };
                AssertKeys(request, isStream ? [.. requestKeys, "streaming"] : requestKeys);
                var worker = request.GetProperty("worker").GetInt32();
                Assert.InRange(worker, 0, concurrency - 1);
                var start = request.GetProperty("startOffsetTicks").GetInt64();
                var end = request.GetProperty("endOffsetTicks").GetInt64();
                Assert.InRange(start, phaseStart, phaseEnd);
                Assert.InRange(end, start, phaseEnd);
                if (workerEnds.TryGetValue(worker, out var priorEnd)) Assert.True(start >= priorEnd);
                workerEnds[worker] = end;
                var previousStage = start;
                var missingStage = false;
                foreach (var name in new[] { "headersOffsetTicks", "bodyOffsetTicks", "validatedOffsetTicks" })
                {
                    var stage = request.GetProperty(name);
                    if (stage.ValueKind == JsonValueKind.Null)
                    {
                        missingStage = true;
                        continue;
                    }
                    Assert.False(missingStage);
                    Assert.InRange(stage.GetInt64(), previousStage, end);
                    previousStage = stage.GetInt64();
                }
                var succeeded = request.GetProperty("succeeded").GetBoolean();
                if (succeeded) Assert.False(missingStage);
                if (isStream)
                {
                    var streaming = request.GetProperty("streaming");
                    AssertKeys(streaming, "firstBodyReadOffsetTicks", "lastBodyReadOffsetTicks", "firstDataEventOffsetTicks", "doneEventOffsetTicks",
                        "bodyReadCount", "dataEventCount", "doneEventCount");
                    var readCount = streaming.GetProperty("bodyReadCount").GetInt32();
                    var eventCount = streaming.GetProperty("dataEventCount").GetInt32();
                    var doneCount = streaming.GetProperty("doneEventCount").GetInt32();
                    Assert.InRange(readCount, 0, NativeBenchmarkWorkloadProbe.MaximumResponseBytes);
                    Assert.InRange(doneCount, 0, eventCount);
                    if (readCount == 0)
                    {
                        Assert.Equal(0, eventCount);
                        Assert.Equal(JsonValueKind.Null, streaming.GetProperty("firstBodyReadOffsetTicks").ValueKind);
                        Assert.Equal(JsonValueKind.Null, streaming.GetProperty("lastBodyReadOffsetTicks").ValueKind);
                    }
                    else
                    {
                        var first = streaming.GetProperty("firstBodyReadOffsetTicks").GetInt64();
                        var last = streaming.GetProperty("lastBodyReadOffsetTicks").GetInt64();
                        Assert.InRange(first, request.GetProperty("headersOffsetTicks").GetInt64(), end);
                        Assert.InRange(last, first, end);
                        if (request.GetProperty("bodyOffsetTicks").ValueKind != JsonValueKind.Null)
                            Assert.True(last <= request.GetProperty("bodyOffsetTicks").GetInt64());
                        foreach (var field in new[] { "firstDataEventOffsetTicks", "doneEventOffsetTicks" })
                            if (streaming.GetProperty(field).ValueKind != JsonValueKind.Null)
                                Assert.InRange(streaming.GetProperty(field).GetInt64(), first, last);
                    }
                    Assert.Equal(eventCount == 0, streaming.GetProperty("firstDataEventOffsetTicks").ValueKind == JsonValueKind.Null);
                    Assert.Equal(doneCount == 0, streaming.GetProperty("doneEventOffsetTicks").ValueKind == JsonValueKind.Null);
                    if (doneCount > 0)
                        Assert.True(streaming.GetProperty("firstDataEventOffsetTicks").GetInt64() <= streaming.GetProperty("doneEventOffsetTicks").GetInt64());
                    if (succeeded) Assert.Equal(1, doneCount);
                }
                var bytes = request.GetProperty("responseBytes");
                if (request.GetProperty("bodyOffsetTicks").ValueKind == JsonValueKind.Null)
                    Assert.Equal(JsonValueKind.Null, bytes.ValueKind);
                else
                    Assert.InRange(bytes.GetInt32(), 0, NativeBenchmarkWorkloadProbe.MaximumResponseBytes);
                var traceId = request.GetProperty("traceId").GetString()!;
                var spanId = request.GetProperty("spanId").GetString()!;
                Assert.Matches("^(?!0+$)[0-9a-f]{32}$", traceId);
                Assert.Matches("^(?!0+$)[0-9a-f]{16}$", spanId);
                Assert.True(traceIds.Add(traceId));
                Assert.True(spanIds.Add(spanId));
                Assert.True(carriers.Add("00-" + traceId + "-" + spanId + "-01"));
            }
            if (phaseIndex == 1)
            {
                var row = Row(report);
                var elapsedMilliseconds = elapsedTicks * 1000d / frequency;
                // The schema-2 TimeSpan rounds to 100ns; diagnostics retain raw Stopwatch ticks.
                Assert.InRange(Math.Abs(row.GetProperty("elapsedMilliseconds").GetDouble() - elapsedMilliseconds), 0, 0.0001);
                if (requests.Length > 0)
                {
                    var durations = requests.Select(row => row.GetProperty("endOffsetTicks").GetInt64()
                        - row.GetProperty("startOffsetTicks").GetInt64()).Order().ToArray();
                    foreach (var (name, percentile) in new[] { ("p50Milliseconds", 0.50), ("p95Milliseconds", 0.95), ("p99Milliseconds", 0.99) })
                    {
                        var rank = Math.Clamp((int)Math.Ceiling(durations.Length * percentile) - 1, 0, durations.Length - 1);
                        Assert.Equal(row.GetProperty(name).GetDouble(), durations[rank] * 1000d / frequency, precision: 6);
                    }
                }
            }
        }
        Assert.Equal(warmup + requested, server.Requests.Count);
        var actualCarriers = server.Requests.Select(request => request.Headers["traceparent"]).ToArray();
        Assert.Equal(carriers.Count, actualCarriers.Distinct(StringComparer.Ordinal).Count());
        Assert.True(carriers.SetEquals(actualCarriers));
        AssertPrivateInputsAbsent(report, server);
        AssertPrivateInputsAbsent(diagnostics, server);
    }

    private static void AssertRuntimeCounters(JsonElement before, JsonElement after)
    {
        var counterKeys = new[] { "cpuTimeTicks", "totalAllocatedBytes", "gcGen0", "gcGen1", "gcGen2", "gcPauseTicks",
            "threadPoolThreads", "threadPoolPending", "threadPoolCompleted", "minWorkerThreads", "maxWorkerThreads",
            "availableWorkerThreads", "serverGc" };
        foreach (var runtime in new[] { before, after })
        {
            AssertKeys(runtime, counterKeys);
            foreach (var name in counterKeys.Where(name => name != "serverGc"))
                Assert.InRange(runtime.GetProperty(name).GetInt64(), 0, long.MaxValue);
            Assert.InRange(runtime.GetProperty("minWorkerThreads").GetInt32(), 1, runtime.GetProperty("maxWorkerThreads").GetInt32());
            Assert.InRange(runtime.GetProperty("availableWorkerThreads").GetInt32(), 0, runtime.GetProperty("maxWorkerThreads").GetInt32());
            Assert.True(runtime.GetProperty("serverGc").ValueKind is JsonValueKind.True or JsonValueKind.False);
        }
        foreach (var name in new[] { "cpuTimeTicks", "totalAllocatedBytes", "gcGen0", "gcGen1", "gcGen2", "gcPauseTicks", "threadPoolCompleted" })
            Assert.True(after.GetProperty(name).GetInt64() >= before.GetProperty(name).GetInt64(), name);
        Assert.Equal(before.GetProperty("serverGc").GetBoolean(), after.GetProperty("serverGc").GetBoolean());
    }

    private static void AssertFailedWarmupDiagnostics(JsonElement diagnostic, LoopbackServer server,
        string workload, int requested, int completed, Dictionary<string, int> categories)
    {
        AssertKeys(diagnostic, "schemaVersion", "kind", "scope", "status", "phase", "service", "workload",
            "inputFingerprint", "requested", "executed", "completed", "failures", "failureCategories");
        Assert.Equal(1, diagnostic.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("conduit-native-benchmark-failed-warmup-diagnostics", diagnostic.GetProperty("kind").GetString());
        Assert.Equal("client", diagnostic.GetProperty("scope").GetString());
        Assert.Equal("failed", diagnostic.GetProperty("status").GetString());
        Assert.Equal("warmup", diagnostic.GetProperty("phase").GetString());
        Assert.Equal(workload == "admin-setting-read" ? "admin" : "gateway", diagnostic.GetProperty("service").GetString());
        Assert.Equal(workload, diagnostic.GetProperty("workload").GetString());
        Assert.Matches("^[0-9a-f]{64}$", diagnostic.GetProperty("inputFingerprint").GetString()!);
        Assert.Equal(requested, diagnostic.GetProperty("requested").GetInt32());
        Assert.Equal(requested, diagnostic.GetProperty("executed").GetInt32());
        Assert.Equal(completed, diagnostic.GetProperty("completed").GetInt32());
        Assert.Equal(requested - completed, diagnostic.GetProperty("failures").GetInt32());
        var failures = diagnostic.GetProperty("failureCategories");
        AssertKeys(failures, "http-status", "response-validation", "transport", "request-deadline");
        foreach (var category in failures.EnumerateObject())
            Assert.Equal(categories.GetValueOrDefault(category.Name), category.Value.GetInt32());
        Assert.Equal(requested - completed, failures.EnumerateObject().Sum(category => category.Value.GetInt32()));
        AssertPrivateInputsAbsent(diagnostic, server);
    }

    private static void AssertPrivateInputsAbsent(JsonElement document, LoopbackServer server)
    {
        var json = document.GetRawText();
        foreach (var value in new[] { Secret, SecretVariable, SettingKey, Content, "private invalid", "private wrong value",
            "Authorization", "Bearer ", "prompt café", server.Endpoint.AbsoluteUri, server.ConfigurationPath })
            Assert.DoesNotContain(value, json, StringComparison.Ordinal);
    }

    private static void AssertKeys(JsonElement value, params string[] keys) =>
        Assert.Equal(keys.Order(StringComparer.Ordinal).ToArray(), value.EnumerateObject()
            .Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());

    private static async Task AwaitPhaseRequestsAsync(Task arrived, Task probe, CancellationToken token)
    {
        if (await Task.WhenAny(arrived, probe).WaitAsync(token) == probe)
        {
            await probe;
            Assert.Fail("The probe completed before all gated phase requests arrived.");
        }
        await arrived.WaitAsync(token);
    }

    private static JsonElement Row(JsonElement report) => Assert.Single(report.GetProperty("workloads").EnumerateArray());

    private static Response ValidResponse(string workload) => workload switch
    {
        "admin-setting-read" => new Response("application/json", AdminResponse()),
        "gateway-chat-sse" => new Response("text/event-stream", SseResponse()),
        _ => new Response("application/json", ChatResponse())
    };

    private static string AdminResponse(string value = Content, string updatedAt = "2025-01-02T00:00:00Z") => Json(writer =>
    {
        writer.WriteNumber("id", 42); writer.WriteString("key", SettingKey); writer.WriteString("value", value);
        writer.WriteNull("description"); writer.WriteString("createdAt", "2025-01-01T00:00:00Z"); writer.WriteString("updatedAt", updatedAt);
    });

    private static string ChatResponse(string content = Content, int completionTokens = 5) => Json(writer =>
    {
        Envelope(writer, "chat.completion");
        writer.WriteStartArray("choices"); writer.WriteStartObject(); writer.WriteNumber("index", 0);
        writer.WriteStartObject("message"); writer.WriteString("role", "assistant"); writer.WriteString("content", content); writer.WriteEndObject();
        writer.WriteString("finish_reason", "stop"); writer.WriteEndObject(); writer.WriteEndArray(); Usage(writer, completionTokens);
    });

    private static string SseResponse(bool includeUsage = true, bool includeDone = true)
    {
        var result = ": fixture Unicode chunks\n\n"
            + "data: " + Chunk("", role: true) + "\n\n"
            + "data: " + Chunk("reply café ", role: false) + "\n\n"
            + "data: " + Chunk("雪 🔎", role: false) + "\n\n"
            + "data: " + Chunk(null, role: false, stop: true) + "\n\n";
        if (includeUsage)
            result += "data: " + Json(writer => { Envelope(writer, "chat.completion.chunk"); writer.WriteStartArray("choices"); writer.WriteEndArray(); Usage(writer); }) + "\n\n";
        return result + (includeDone ? "data: [DONE]\n\n" : "");
    }

    private static string Chunk(string? content, bool role, bool stop = false) => Json(writer =>
    {
        Envelope(writer, "chat.completion.chunk");
        writer.WriteStartArray("choices");
        writer.WriteStartObject();
        writer.WriteNumber("index", 0);
        writer.WriteStartObject("delta");
        if (role)
        {
            writer.WriteString("role", "assistant");
        }
        if (content is not null)
        {
            writer.WriteString("content", content);
        }
        writer.WriteEndObject();
        if (stop)
        {
            writer.WriteString("finish_reason", "stop");
        }
        else
        {
            writer.WriteNull("finish_reason");
        }
        writer.WriteEndObject();
        writer.WriteEndArray();
    });

    private static void Envelope(Utf8JsonWriter writer, string objectName)
    {
        writer.WriteString("id", "chat-native-benchmark"); writer.WriteString("object", objectName);
        writer.WriteNumber("created", 1735689600); writer.WriteString("model", "provider-model");
    }

    private static void Usage(Utf8JsonWriter writer, int completionTokens = 5)
    {
        writer.WriteStartObject("usage"); writer.WriteNumber("prompt_tokens", 10);
        writer.WriteNumber("completion_tokens", completionTokens); writer.WriteNumber("total_tokens", 10 + completionTokens); writer.WriteEndObject();
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject(); write(writer); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class EnvironmentSecret : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable(SecretVariable);
        public EnvironmentSecret(string? value) => Environment.SetEnvironmentVariable(SecretVariable, value);
        public void Dispose() => Environment.SetEnvironmentVariable(SecretVariable, _previous);
    }

    private sealed record Response(string ContentType, string Body, int StatusCode = 200, bool Hold = false,
        bool Chunked = false, bool HoldAfterBody = false, bool Truncated = false);

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(10));
        private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
        private readonly ConcurrentBag<Task> _handlers = [];
        private readonly ConcurrentQueue<Exception> _failures = new();
        private readonly TaskCompletionSource _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _accept;
        private readonly string _directory;
        private int _accepted;
        private int _requestOrdinal;

        public LoopbackServer(Response response)
        {
            Response = response;
            _directory = Path.Combine(Path.GetTempPath(), "conduit-benchmark-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            ConfigurationPath = Path.Combine(_directory, "workload.json");
            _listener.Start();
            Endpoint = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) + "/");
            _accept = AcceptAsync();
        }

        public Uri Endpoint { get; }
        public string ConfigurationPath { get; }
        public Response Response { get; set; }
        public Func<int, CancellationToken, Task>? BeforeResponse { get; set; }
        public ConcurrentQueue<NativeProviderHttpRequest> Requests { get; } = new();
        public Task RequestReceived => _received.Task;
        public Task FailureObserved => _failed.Task;
        public CancellationToken Token => _deadline.Token;
        public int AcceptedConnections => Volatile.Read(ref _accepted);

        public Task WriteConfigurationAsync(JsonObject configuration) =>
            File.WriteAllTextAsync(ConfigurationPath, configuration.ToJsonString(), new UTF8Encoding(false), Token);

        public async Task<(int ExitCode, JsonElement Report)> RunAsync(JsonObject configuration)
        {
            await WriteConfigurationAsync(configuration);
            using var output = new MemoryStream();
            var result = await NativeBenchmarkWorkloadProbe.RunAsync(ConfigurationPath, output, Token).WaitAsync(Token);
            using var report = JsonDocument.Parse(output.ToArray());
            return (result, report.RootElement.Clone());
        }

        public async Task<(int ExitCode, JsonElement Report, JsonElement Diagnostics)> RunWithDiagnosticsAsync(JsonObject configuration)
        {
            await WriteConfigurationAsync(configuration);
            using var output = new MemoryStream();
            using var diagnostics = new MemoryStream();
            var result = await NativeBenchmarkWorkloadProbe.RunAsync(ConfigurationPath, output, Token, diagnostics).WaitAsync(Token);
            using var report = JsonDocument.Parse(output.ToArray());
            using var detail = JsonDocument.Parse(diagnostics.ToArray());
            return (result, report.RootElement.Clone(), detail.RootElement.Clone());
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var id = Interlocked.Increment(ref _accepted);
                    _clients[id] = client;
                    _handlers.Add(HandleAsync(id, client));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
            // Supervise the owned background accept task: teardown must still join
            // every handler, then fail with this unexpected fault rather than hide it.
            catch (Exception exception) { RecordFailure(exception); }
        }

        private async Task HandleAsync(int id, TcpClient client)
        {
            var writingResponse = false;
            try
            {
                using var stream = client.GetStream();
                var request = await NativeProviderHttpRequestReader.ReadAsync(stream, _stop.Token);
                if (request is null) return;
                Requests.Enqueue(request);
                _received.TrySetResult();
                var ordinal = Interlocked.Increment(ref _requestOrdinal);
                if (BeforeResponse is { } beforeResponse) await beforeResponse(ordinal, _stop.Token);
                var response = Response;
                if (response.Hold) await Task.Delay(Timeout.Infinite, _stop.Token);
                var body = Encoding.UTF8.GetBytes(response.Body);
                var framing = response.HoldAfterBody ? string.Empty : response.Chunked ? "Transfer-Encoding: chunked\r\n"
                    : "Content-Length: " + (body.Length + (response.Truncated ? 1 : 0)).ToString(CultureInfo.InvariantCulture) + "\r\n";
                var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + response.StatusCode.ToString(CultureInfo.InvariantCulture)
                    + " fixture\r\nContent-Type: " + response.ContentType + "; charset=utf-8\r\n" + framing + "Connection: close\r\n\r\n");
                writingResponse = true;
                await stream.WriteAsync(header, _stop.Token);
                if (response.Chunked)
                {
                    for (var offset = 0; offset < body.Length; offset += 16384)
                    {
                        var length = Math.Min(16384, body.Length - offset);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(length.ToString("x", CultureInfo.InvariantCulture) + "\r\n"), _stop.Token);
                        await stream.WriteAsync(body.AsMemory(offset, length), _stop.Token);
                        await stream.WriteAsync("\r\n"u8.ToArray(), _stop.Token);
                    }
                    await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), _stop.Token);
                }
                else if (response.ContentType == "text/event-stream")
                {
                    // Seven-byte writes force multibyte text across network writes without sleeps.
                    for (var offset = 0; offset < body.Length; offset += 7)
                        await stream.WriteAsync(body.AsMemory(offset, Math.Min(7, body.Length - offset)), _stop.Token);
                }
                else await stream.WriteAsync(body, _stop.Token);
                if (response.HoldAfterBody) await Task.Delay(Timeout.Infinite, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
            catch (IOException) when (writingResponse)
            {
                // A cancelled or size-limited client may close its response socket.
                // Request framing failures must remain visible in fixture teardown.
            }
            // Preserve every unexpected handler failure for the teardown assertion
            // and release FailureObserved waiters; expected client closures are above.
            catch (Exception exception) { RecordFailure(exception); }
            finally
            {
                _clients.TryRemove(id, out _);
                client.Dispose();
            }
        }

        private void RecordFailure(Exception exception)
        {
            _failures.Enqueue(exception);
            _failed.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                _stop.Cancel();
                _listener.Stop();
                foreach (var client in _clients.Values) client.Dispose();
                await _accept.WaitAsync(cleanup.Token);
                await Task.WhenAll(_handlers.ToArray()).WaitAsync(cleanup.Token);
                Assert.Empty(_clients);
                Assert.Empty(_failures);
            }
            finally
            {
                _listener.Dispose();
                _deadline.Dispose(); _stop.Dispose();
                if (File.Exists(ConfigurationPath)) File.Delete(ConfigurationPath);
                Directory.Delete(_directory);
            }
        }
    }
}
