using System.Net;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Middleware;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Billing;
using ConduitLLM.Gateway.Constants;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.UsageTracking;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.Tests.Gateway.Middleware;

public class UsageTrackingErrorResponseTests
{
    [Theory]
    [InlineData(400, false)]
    [InlineData(502, false)]
    [InlineData(503, false)]
    [InlineData(400, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(502, false, UsageEvidenceSource.Estimated)]
    [InlineData(502, true, UsageEvidenceSource.Estimated)]
    public async Task Published_provider_usage_is_billed_and_logged_after_error_response(
        int statusCode, bool withReservation, UsageEvidenceSource source = UsageEvidenceSource.Provider)
    {
        using var request = new TrackedRequest(withReservation);

        await request.InvokeAsync(context =>
        {
            request.Accounting.RecordProviderUsage(TokenUsage(), "test-model", source);
            context.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        });

        // Responses publishes aggregate usage even when it has no per-call invocation IDs.
        Assert.Empty(request.Accounting.Snapshot().ProviderInvocations!);
        var log = Assert.Single(request.Logs);
        Assert.Equal(statusCode, log.StatusCode);
        Assert.Equal(10, log.InputTokens);
        Assert.Equal(5, log.OutputTokens);
        Assert.Equal(0.000075m, log.Cost);
        var audit = Assert.Single(request.Audits);
        Assert.Equal(BillingAuditEventType.UsageTracked, audit.EventType);
        Assert.Equal(statusCode, audit.HttpStatusCode);
        Assert.Equal(0.000075m, audit.CalculatedCost);
        Assert.False(request.Accounting.Snapshot().IsIndeterminate);
        request.AssertChargedExactlyOnce(0.000075m);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_provider_calls_without_aggregate_usage_are_billed_at_their_original_boundaries(bool withReservation)
    {
        using var request = new TrackedRequest(withReservation);
        await request.InvokeAsync(context =>
        {
            request.Accounting.RecordProviderCalls([
                new ProviderCallUsage { Iteration = 1, Usage = TokenUsage() },
                new ProviderCallUsage { Iteration = 2, Usage = TokenUsage() }
            ]);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return Task.CompletedTask;
        });

        var log = Assert.Single(request.Logs);
        Assert.Equal(0.00015m, log.Cost);
        Assert.Equal(20, log.InputTokens);
        Assert.Equal(10, log.OutputTokens);
        request.ModelCosts.Verify(service => service.GetCostForModelAsync("test-model", It.IsAny<CancellationToken>()), Times.Exactly(2));
        request.AssertChargedExactlyOnce(0.00015m);
    }

    [Fact]
    public async Task Completed_call_without_aggregate_and_unresolved_invocation_preserves_known_cost_and_hold()
    {
        using var request = new TrackedRequest(withReservation: true);
        await request.InvokeAsync(context =>
        {
            request.Accounting.RecordProviderInvocation(1);
            request.Accounting.RecordProviderInvocation(2);
            request.Accounting.RecordProviderCalls([new ProviderCallUsage { Iteration = 1, Usage = TokenUsage() }]);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return Task.CompletedTask;
        });

        var log = Assert.Single(request.Logs);
        Assert.Equal(0.000075m, log.Cost);
        Assert.Equal(10, log.InputTokens);
        Assert.Equal(5, log.OutputTokens);
        request.AssertPartialKnownSpend(0.000075m);
    }

    [Theory]
    [InlineData(SpendReservationSettlementStatus.Settled)]
    [InlineData(SpendReservationSettlementStatus.AlreadySettled)]
    public async Task Post_provider_error_uses_the_original_reservation_without_queuing_another_charge(
        SpendReservationSettlementStatus settlementStatus)
    {
        using var request = new TrackedRequest(withReservation: true);
        request.Reservations.Setup(service => service.SettleAsync(
                42, request.Accounting.BillingRequestId, 0.000075m, It.IsAny<DateTime?>()))
            .ReturnsAsync(new SpendReservationSettlementResult(settlementStatus, 0.000075m));

        await request.InvokeAsync(context =>
        {
            request.Accounting.RecordProviderUsage(TokenUsage(), "test-model", UsageEvidenceSource.Provider);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return Task.CompletedTask;
        });

        request.AssertChargedExactlyOnce(0.000075m);
        Assert.Single(request.Logs);
        Assert.True(request.Accounting.Snapshot().Reservation!.Closed);
    }

    [Fact]
    public async Task Error_after_known_usage_preserves_partial_tool_charge_and_unresolved_hold()
    {
        using var request = new TrackedRequest(withReservation: true);
        request.Tools.Setup(service => service.CalculateToolCostsAsync(
                It.IsAny<ToolUsageData>(), ProviderType.OpenAI))
            .ThrowsAsync(new ToolCostCalculationException("A provider tool has no price", knownCost: 0.002m));

        await request.InvokeAsync(context =>
        {
            request.Accounting.RecordProviderUsage(TokenUsage(), "test-model", UsageEvidenceSource.Provider);
            request.Accounting.RecordProviderToolUsage(new ProviderToolUsage
            {
                Tools = [new ProviderToolUsageItem { ToolName = "unpriced-tool", Count = 1 }]
            });
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return Task.CompletedTask;
        });

        const decimal knownCost = 0.002075m;
        Assert.Equal(knownCost, Assert.Single(request.Logs).Cost);
        var audit = Assert.Single(request.Audits);
        Assert.Equal(BillingAuditEventType.PricingCalculationFailed, audit.EventType);
        Assert.Equal(StatusCodes.Status502BadGateway, audit.HttpStatusCode);
        Assert.Contains("no price", audit.FailureReason);
        request.AssertPartialKnownSpend(knownCost);
        Assert.Null(request.Accounting.Snapshot().ProviderToolPricing!.Cost);
        Assert.Equal(0.002m, request.Accounting.Snapshot().ProviderToolPricing!.KnownCost);
    }

    [Fact]
    public async Task Error_with_a_missing_provider_invocation_retains_known_call_cost_and_hold()
    {
        using var request = new TrackedRequest(withReservation: true);

        await request.InvokeAsync(context =>
        {
            request.Accounting.RecordProviderInvocation(1);
            request.Accounting.RecordProviderInvocation(2);
            request.Accounting.RecordProviderCalls([new ProviderCallUsage { Iteration = 1, Usage = TokenUsage() }]);
            request.Accounting.RecordProviderUsage(TokenUsage(), "test-model", UsageEvidenceSource.Provider);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return Task.CompletedTask;
        });

        Assert.Equal(0.000075m, Assert.Single(request.Logs).Cost);
        Assert.Contains(request.Audits, audit =>
            audit.EventType == BillingAuditEventType.PricingCalculationFailed &&
            audit.HttpStatusCode == StatusCodes.Status502BadGateway);
        request.AssertPartialKnownSpend(0.000075m);
    }

    [Fact]
    public async Task Published_usage_survives_a_later_response_writing_exception()
    {
        using var request = new TrackedRequest(withReservation: true);
        var failure = new IOException("Response writing failed");

        var actual = await Assert.ThrowsAsync<IOException>(() => request.InvokeAsync(context =>
        {
            request.Accounting.RecordProviderUsage(TokenUsage(), "test-model", UsageEvidenceSource.Provider);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            throw failure;
        }));

        Assert.Same(failure, actual);
        Assert.Equal(0.000075m, Assert.Single(request.Logs).Cost);
        request.AssertChargedExactlyOnce(0.000075m);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outer_error_handler_and_accounting_agree_after_unstarted_response_writing_failure(bool withReservation)
    {
        using var request = new TrackedRequest(withReservation);
        var failure = new IOException("Private response-writing details");

        await request.InvokeWithErrorHandlingAsync(context =>
        {
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            request.Accounting.RecordProviderUsage(TokenUsage(), "test-model", UsageEvidenceSource.Provider);
            throw failure;
        });

        Assert.Same(failure, request.ExceptionObservedByOuter);
        Assert.Equal(StatusCodes.Status500InternalServerError, request.Context.Response.StatusCode);
        Assert.Equal(StatusCodes.Status500InternalServerError, Assert.Single(request.Logs).StatusCode);
        Assert.Equal(StatusCodes.Status500InternalServerError, Assert.Single(request.Audits).HttpStatusCode);
        using var body = JsonDocument.Parse(request.ResponseText());
        Assert.Equal("internal_error", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(failure.Message, request.ResponseText());
        Assert.Equal(request.Context.TraceIdentifier, request.Context.Response.Headers["x-request-id"]);
        request.AssertChargedExactlyOnce(0.000075m);
    }

    [Theory]
    [InlineData(CustomerErrorMode.External, HttpStatusCode.Unauthorized, StatusCodes.Status502BadGateway)]
    [InlineData(CustomerErrorMode.Internal, HttpStatusCode.Unauthorized, StatusCodes.Status502BadGateway)]
    [InlineData(CustomerErrorMode.External, HttpStatusCode.TooManyRequests, StatusCodes.Status429TooManyRequests)]
    [InlineData(CustomerErrorMode.Internal, HttpStatusCode.TooManyRequests, StatusCodes.Status429TooManyRequests)]
    public async Task Wrapped_provider_error_is_translated_once_for_accounting_and_outer_envelope(
        CustomerErrorMode mode, HttpStatusCode upstreamStatus, int expectedStatus)
    {
        var translator = new Mock<IProviderErrorTranslator>();
        var actualTranslator = new ProviderErrorTranslator(new CustomerErrorOptions { Mode = mode });
        translator.Setup(service => service.MapProviderError(It.IsAny<LLMCommunicationException>()))
            .Returns<LLMCommunicationException>(actualTranslator.MapProviderError);
        using var request = new TrackedRequest(withReservation: true, translator: translator.Object);
        var providerFailure = new LLMCommunicationException("Provider rejected request", upstreamStatus, "Private upstream text")
        { ProviderName = "private-provider" };
        var failure = new IOException("Response wrapper", new LLMCommunicationException("Statusless wrapper", providerFailure));

        await request.InvokeWithErrorHandlingAsync(context =>
        {
            request.Accounting.RecordProviderUsage(TokenUsage(), "test-model", UsageEvidenceSource.Provider);
            throw failure;
        });

        Assert.Same(failure, request.ExceptionObservedByOuter);
        Assert.Equal(expectedStatus, request.Context.Response.StatusCode);
        Assert.Equal(expectedStatus, Assert.Single(request.Logs).StatusCode);
        Assert.Equal(expectedStatus, Assert.Single(request.Audits).HttpStatusCode);
        translator.Verify(service => service.MapProviderError(providerFailure), Times.Once);
        using var body = JsonDocument.Parse(request.ResponseText());
        var error = body.RootElement.GetProperty("error");
        if (mode == CustomerErrorMode.External)
        {
            Assert.DoesNotContain("Private upstream text", request.ResponseText());
            Assert.DoesNotContain("private-provider", request.ResponseText());
            Assert.False(error.TryGetProperty("metadata", out _));
        }
        else
        {
            Assert.Contains("private-provider", error.GetProperty("message").GetString());
            Assert.Equal("Private upstream text", error.GetProperty("metadata").GetProperty("provider_error")
                .GetProperty("raw_message").GetString());
        }
        request.AssertChargedExactlyOnce(0.000075m);
    }

    [Fact]
    public async Task Provisional_translation_failure_preserves_original_exception_and_known_charge()
    {
        var translator = new Mock<IProviderErrorTranslator>();
        translator.Setup(service => service.MapProviderError(It.IsAny<LLMCommunicationException>()))
            .Throws(new InvalidOperationException("Translator unavailable"));
        using var request = new TrackedRequest(withReservation: true, translator: translator.Object);
        var failure = new IOException("Original downstream failure",
            new LLMCommunicationException("Provider failure", HttpStatusCode.Unauthorized, "private"));

        var actual = await Assert.ThrowsAsync<IOException>(() => request.InvokeAsync(context =>
        {
            request.Accounting.RecordProviderUsage(TokenUsage(), "test-model", UsageEvidenceSource.Provider);
            throw failure;
        }));

        Assert.Same(failure, actual);
        translator.Verify(service => service.MapProviderError(It.IsAny<LLMCommunicationException>()), Times.Once);
        Assert.Single(request.Logs);
        request.AssertChargedExactlyOnce(0.000075m);
    }

    [Fact]
    public async Task Outer_translation_retry_and_accounting_agree_when_provisional_translation_fails()
    {
        var translator = new Mock<IProviderErrorTranslator>();
        var providerFailure = new LLMCommunicationException("Provider failure", HttpStatusCode.Unauthorized, "private");
        var mapping = new ProviderErrorTranslator(new CustomerErrorOptions()).MapProviderError(providerFailure);
        translator.SetupSequence(service => service.MapProviderError(providerFailure))
            .Throws(new IOException("Temporary translation failure"))
            .Returns(mapping);
        using var request = new TrackedRequest(withReservation: true, translator: translator.Object);
        var failure = new IOException("Original downstream failure", providerFailure);

        await request.InvokeWithErrorHandlingAsync(context =>
        {
            request.Accounting.RecordProviderUsage(TokenUsage(), "test-model", UsageEvidenceSource.Provider);
            throw failure;
        });

        Assert.Same(failure, request.ExceptionObservedByOuter);
        Assert.Equal(StatusCodes.Status502BadGateway, request.Context.Response.StatusCode);
        Assert.Equal(StatusCodes.Status502BadGateway, Assert.Single(request.Logs).StatusCode);
        Assert.Equal(StatusCodes.Status502BadGateway, Assert.Single(request.Audits).HttpStatusCode);
        using var body = JsonDocument.Parse(request.ResponseText());
        Assert.Equal(mapping.ResponseMessage, body.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain("private", request.ResponseText());
        translator.Verify(service => service.MapProviderError(providerFailure), Times.Exactly(2));
        request.AssertChargedExactlyOnce(0.000075m);
    }

    [Fact]
    public async Task Provisional_translation_and_diagnostic_logger_failures_do_not_replace_downstream_exception()
    {
        var translator = new Mock<IProviderErrorTranslator>();
        translator.Setup(service => service.MapProviderError(It.IsAny<LLMCommunicationException>()))
            .Throws(new InvalidOperationException("Translator unavailable"));
        using var request = new TrackedRequest(withReservation: true, translator: translator.Object);
        var failure = new IOException("Original downstream failure",
            new LLMCommunicationException("Provider failure", HttpStatusCode.Unauthorized, "private"));

        var actual = await Assert.ThrowsAsync<IOException>(() => request.InvokeAsync(context =>
        {
            request.Accounting.RecordProviderUsage(TokenUsage(), "test-model", UsageEvidenceSource.Provider);
            throw failure;
        }, new ThrowingErrorLogger()));

        Assert.Same(failure, actual);
        Assert.Equal(StatusCodes.Status502BadGateway, Assert.Single(request.Logs).StatusCode);
        request.AssertChargedExactlyOnce(0.000075m);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/event-stream")]
    public async Task Already_started_response_keeps_sent_status_headers_and_body_after_exception(string contentType)
    {
        using var request = new TrackedRequest(withReservation: true);
        var response = new StartedResponseFeature
        {
            StatusCode = StatusCodes.Status200OK
        };
        request.Context.Features.Set<IHttpResponseFeature>(response);
        request.Context.Response.ContentType = contentType;
        request.Context.Response.Headers["x-sent"] = "original";
        var prefix = Encoding.UTF8.GetBytes("already-sent-body");
        await request.Context.Response.Body.WriteAsync(prefix);
        response.Started = true;
        var failure = new IOException("Writing failed after response started");

        await request.InvokeWithErrorHandlingAsync(context =>
        {
            request.Accounting.RecordProviderUsage(TokenUsage(), "test-model", UsageEvidenceSource.Provider);
            throw failure;
        });

        Assert.Same(failure, request.ExceptionObservedByOuter);
        Assert.Equal(StatusCodes.Status200OK, request.Context.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, Assert.Single(request.Logs).StatusCode);
        Assert.Equal(StatusCodes.Status200OK, Assert.Single(request.Audits).HttpStatusCode);
        Assert.Equal(contentType, request.Context.Response.ContentType);
        Assert.Equal("original", request.Context.Response.Headers["x-sent"]);
        Assert.False(request.Context.Response.Headers.ContainsKey("x-request-id"));
        Assert.Equal("already-sent-body", request.ResponseText());
        request.AssertChargedExactlyOnce(0.000075m);
    }

    [Fact]
    public async Task Error_before_provider_work_remains_unbilled_and_releases_unstarted_reservation()
    {
        using var request = new TrackedRequest(withReservation: true, invocationStarted: false);

        await request.InvokeAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        });

        Assert.Empty(request.Logs);
        Assert.Equal(BillingAuditEventType.ErrorResponseSkipped, Assert.Single(request.Audits).EventType);
        request.Reservations.Verify(service => service.ReleaseAsync(42, request.Accounting.BillingRequestId), Times.Once);
        request.Reservations.Verify(service => service.SettleAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()), Times.Never);
        request.Reservations.Verify(service => service.RecordKnownIndeterminateSpendAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<DateTime?>()), Times.Never);
        request.Batch.Verify(service => service.QueueSpendUpdateAsync(
            It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()), Times.Never);
        request.ModelCosts.Verify(service => service.GetCostForModelAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Usage TokenUsage() => new() { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 };

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public bool Started { get; set; }
        public override bool HasStarted => Started;
    }

    private sealed class ThrowingErrorLogger : ILogger<UsageTrackingMiddleware>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            if (logLevel == LogLevel.Error) throw new InvalidOperationException("Diagnostic logger failed");
        }
    }

    private sealed class TrackedRequest : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly bool _withReservation;
        private readonly IProviderErrorTranslator _translator;
        public HttpContext Context { get; }
        public Exception ExceptionObservedByOuter { get; private set; }
        public IRequestAccountingContext Accounting { get; }
        public Mock<ISpendReservationService> Reservations { get; } = new();
        public Mock<IBatchSpendUpdateService> Batch { get; } = new();
        public Mock<IModelCostService> ModelCosts { get; } = new();
        public Mock<IToolCostCalculationService> Tools { get; } = new();
        public List<LogRequestDto> Logs { get; } = [];
        public List<BillingAuditEvent> Audits { get; } = [];

        public TrackedRequest(bool withReservation, bool invocationStarted = true, IProviderErrorTranslator translator = null)
        {
            _withReservation = withReservation;
            _translator = translator;
            var services = new ServiceCollection().AddSingleton(Reservations.Object);
            if (translator is not null) services.AddSingleton(translator);
            _services = services.BuildServiceProvider();
            Context = new DefaultHttpContext { RequestServices = _services };
            Context.Response.Body = new MemoryStream();
            Context.Request.Path = "/v1/responses";
            Context.Items[HttpContextKeys.VirtualKeyId] = 42;
            Context.Items["ProviderType"] = ProviderType.OpenAI;
            Context.Response.ContentType = "application/json";
            Accounting = Context.GetOrCreateRequestAccountingContext();
            Accounting.SetOperation(RequestOperation.Responses, 42, "test-model");
            if (withReservation)
            {
                Accounting.RecordReservation(0.02m);
                if (invocationStarted) Accounting.MarkInvocationStarted();
            }
            ModelCosts.Setup(service => service.GetCostForModelAsync("test-model", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ModelCost
                {
                    IsActive = true, InputCostPerMillionTokens = 2.5m, OutputCostPerMillionTokens = 10m
                });
            Batch.SetupGet(service => service.IsHealthy).Returns(true);
            Batch.Setup(service => service.QueueSpendUpdateAsync(42, It.IsAny<decimal>(), It.IsAny<DateTime?>()))
                .Returns(Task.CompletedTask);
            Reservations.Setup(service => service.SettleAsync(
                    42, Accounting.BillingRequestId, It.IsAny<decimal>(), It.IsAny<DateTime?>()))
                .ReturnsAsync(new SpendReservationSettlementResult(SpendReservationSettlementStatus.Settled, 0m));
            Reservations.Setup(service => service.RecordKnownIndeterminateSpendAsync(
                    42, Accounting.BillingRequestId, It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<DateTime?>()))
                .ReturnsAsync(new SpendReservationSettlementResult(SpendReservationSettlementStatus.Settled, 0m));
            Reservations.Setup(service => service.MarkIndeterminateAsync(
                42, Accounting.BillingRequestId, It.IsAny<string>())).Returns(Task.CompletedTask);
            Reservations.Setup(service => service.ReleaseAsync(42, Accounting.BillingRequestId)).Returns(Task.CompletedTask);
            Tools.Setup(service => service.SerializeToolUsage(It.IsAny<ToolUsageData>())).Returns("{}");
        }

        public Task InvokeAsync(RequestDelegate next, ILogger<UsageTrackingMiddleware> logger = null)
        {
            var writer = new Mock<IRequestLogRuntimeWriter>();
            writer.Setup(service => service.LogRequestAsync(It.IsAny<LogRequestDto>()))
                .Callback<LogRequestDto>(Logs.Add).Returns(Task.CompletedTask);
            var audit = new Mock<IBillingAuditService>();
            audit.Setup(service => service.LogBillingEvent(It.IsAny<BillingAuditEvent>()))
                .Callback<BillingAuditEvent>(Audits.Add);
            var pricing = new CostCalculationService(ModelCosts.Object, NullLogger<CostCalculationService>.Instance);
            var middleware = new UsageTrackingMiddleware(next, logger ?? NullLogger<UsageTrackingMiddleware>.Instance);
            return middleware.InvokeAsync(Context, pricing, Batch.Object, writer.Object,
                Mock.Of<IVirtualKeyRuntimeService>(), audit.Object, Tools.Object);
        }

        public Task InvokeWithErrorHandlingAsync(RequestDelegate next)
        {
            var environment = Mock.Of<IWebHostEnvironment>(value => value.EnvironmentName == Environments.Production);
            var outer = new OpenAIErrorMiddleware(async _ =>
            {
                try { await InvokeAsync(next); }
                catch (Exception exception)
                {
                    ExceptionObservedByOuter = exception;
                    throw;
                }
            }, NullLogger<OpenAIErrorMiddleware>.Instance, environment, _translator);
            return outer.InvokeAsync(Context);
        }

        public string ResponseText() => Encoding.UTF8.GetString(((MemoryStream)Context.Response.Body).ToArray());

        public void AssertChargedExactlyOnce(decimal cost)
        {
            Reservations.Verify(service => service.SettleAsync(
                42, Accounting.BillingRequestId, cost, It.IsAny<DateTime?>()), _withReservation ? Times.Once() : Times.Never());
            Batch.Verify(service => service.QueueSpendUpdateAsync(
                42, cost, It.IsAny<DateTime?>()), _withReservation ? Times.Never() : Times.Once());
            Reservations.Verify(service => service.RecordKnownIndeterminateSpendAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<DateTime?>()), Times.Never);
            Reservations.Verify(service => service.ReleaseAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
            Reservations.Verify(service => service.MarkIndeterminateAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        public void AssertPartialKnownSpend(decimal cost)
        {
            Reservations.Verify(service => service.RecordKnownIndeterminateSpendAsync(
                42, Accounting.BillingRequestId, cost, It.IsAny<string>(), It.IsAny<DateTime?>()), Times.Once);
            Reservations.Verify(service => service.MarkIndeterminateAsync(
                42, Accounting.BillingRequestId, It.IsAny<string>()), Times.Once);
            Reservations.Verify(service => service.SettleAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()), Times.Never);
            Reservations.Verify(service => service.ReleaseAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
            Batch.Verify(service => service.QueueSpendUpdateAsync(
                It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()), Times.Never);
            Assert.True(Accounting.Snapshot().IsIndeterminate);
            Assert.False(Accounting.Snapshot().Reservation!.Closed);
        }

        public void Dispose() => _services.Dispose();
    }
}
