using ConduitLLM.Core.Models;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Gateway.Constants;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.UsageTracking;
using ConduitLLM.Gateway.Billing;
using ConduitLLM.Tests.Http.Middleware.Builders;
using ConduitLLM.Tests.Http.Middleware.Assertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace ConduitLLM.Tests.Http.Middleware
{
    /// <summary>
    /// Tests for UsageTrackingMiddleware edge cases including streaming,
    /// fallback behavior, billing policy, and error handling.
    /// </summary>
    public partial class UsageTrackingMiddlewareTests
    {
        [Fact]
        public async Task NonStreaming_Response_Without_Model_Emits_RevenueLoss_Audit()
        {
            var context = new HttpContextBuilder().ForChatCompletions().WithVirtualKey(651).Build();

            await Invoker.WithResponse(new
                {
                    usage = new { prompt_tokens = 10, completion_tokens = 2 }
                })
                .InvokeAsync(context);

            var audit = Assert.Single(Fixture.CapturedBillingEvents);
            Assert.Equal(BillingAuditEventType.MissingUsageData, audit.EventType);
            Assert.Equal("Controller did not publish provider usage evidence", audit.FailureReason);
            UsageTrackingAssertions.VerifyNoCostCalculation(Fixture.CostService);
        }

        [Fact]
        public async Task NonStreaming_Response_With_Unparseable_Usage_Emits_RevenueLoss_Audit()
        {
            var context = new HttpContextBuilder().ForChatCompletions().WithVirtualKey(652).Build();

            await Invoker.WithResponse(new { model = "gpt-test", usage = new { } }).InvokeAsync(context);

            var audit = Assert.Single(Fixture.CapturedBillingEvents);
            Assert.Equal(BillingAuditEventType.MissingUsageData, audit.EventType);
            Assert.Equal("gpt-test", audit.Model);
            Assert.Equal("Controller did not publish provider usage evidence", audit.FailureReason);
            UsageTrackingAssertions.VerifyNoCostCalculation(Fixture.CostService);
        }

        [Fact]
        public async Task NonStreaming_Response_With_TotalTokensOnly_Is_Billed_As_Estimated_Input()
        {
            var context = new HttpContextBuilder().ForChatCompletions().WithVirtualKey(653).Build();
            Fixture.SetupCostForModel("gpt-test", 0.004m);

            await Invoker
                .WithProviderUsage("gpt-test", new Usage
                {
                    TotalTokens = 80,
                    PromptTokens = 80,
                    CompletionTokens = 0,
                    PricingFallbackReason =
                        "Provider reported total_tokens without prompt/completion breakdown; billed as input tokens"
                })
                .WithResponse(new
                {
                    model = "gpt-test",
                    usage = new { total_tokens = 80 }
                })
                .InvokeAsync(context);

            UsageTrackingAssertions.VerifyCostCalculated(Fixture.CostService, "gpt-test", 80, 0);
            UsageTrackingAssertions.VerifySpendQueued(Fixture.BatchSpendService, 653, 0.004m);
            Assert.Contains(Fixture.CapturedBillingEvents, e =>
                e.EventType == BillingAuditEventType.UsageEstimated && e.IsEstimated);
        }

        [Fact]
        public async Task Streaming_Response_Uses_StreamingUsage_From_Context()
        {
            // Arrange
            var streamingUsage = new Usage
            {
                PromptTokens = 50,
                CompletionTokens = 150,
                TotalTokens = 200
            };

            var context = new HttpContextBuilder()
                .ForChatCompletions()
                .WithVirtualKey(654)
                .AsOpenAI()
                .AsStreaming(streamingUsage, "gpt-4")
                .Build();

            Fixture.SetupCostForModel("gpt-4", 0.006m);

            // Act
            await Invoker
                .AsStreamingResponse()
                .InvokeAsync(context);

            // Assert
            UsageTrackingAssertions.VerifyCostCalculated(Fixture.CostService, "gpt-4", 50, 150);
            UsageTrackingAssertions.VerifySpendQueued(Fixture.BatchSpendService, 654, 0.006m);
        }

        [Fact]
        public async Task Streaming_Response_UsesTypedAccountingWithoutLegacyUsageKeys()
        {
            var context = new HttpContextBuilder()
                .ForChatCompletions()
                .WithVirtualKey(658)
                .AsOpenAI()
                .Build();
            var usage = new Usage { PromptTokens = 11, CompletionTokens = 13, TotalTokens = 24 };
            var accounting = context.GetOrCreateRequestAccountingContext();
            accounting.SetOperation(RequestOperation.ChatCompletion, 658, "requested-model");
            accounting.RecordProviderUsage(usage, "resolved-model", UsageEvidenceSource.Provider);
            Fixture.SetupCostForModel("resolved-model", 0.003m);

            await Invoker.AsStreamingResponse().InvokeAsync(context);

            UsageTrackingAssertions.VerifyCostCalculated(Fixture.CostService, "resolved-model", 11, 13);
            UsageTrackingAssertions.VerifySpendQueued(Fixture.BatchSpendService, 658, 0.003m);
            Assert.False(context.Items.ContainsKey("StreamingUsage"));
            Assert.False(context.Items.ContainsKey("StreamingModel"));
        }

        [Fact]
        public async Task Streaming_Response_WithReservation_UsesAtomicSettlementInsteadOfQueueingAgain()
        {
            var context = new HttpContextBuilder()
                .ForChatCompletions()
                .WithVirtualKey(659)
                .AsOpenAI()
                .Build();
            var usage = new Usage { PromptTokens = 3, CompletionTokens = 4, TotalTokens = 7 };
            var accounting = context.GetOrCreateRequestAccountingContext();
            accounting.SetOperation(RequestOperation.ChatCompletion, 659, "model");
            accounting.RecordProviderUsage(usage, "model", UsageEvidenceSource.Provider);
            accounting.RecordReservation(0.01m);
            accounting.MarkInvocationStarted();
            var reservationService = new Mock<ISpendReservationService>();
            reservationService.Setup(x => x.SettleAsync(
                    659,
                    accounting.BillingRequestId,
                    0.004m,
                    It.IsAny<DateTime?>()))
                .ReturnsAsync(new SpendReservationSettlementResult(
                    SpendReservationSettlementStatus.Settled,
                    0.004m));
            context.RequestServices = new ServiceCollection()
                .AddSingleton(reservationService.Object)
                .BuildServiceProvider();
            Fixture.SetupCostForModel("model", 0.004m);

            await Invoker.AsStreamingResponse().InvokeAsync(context);

            reservationService.VerifyAll();
            Fixture.BatchSpendService.Verify(x => x.QueueSpendUpdateAsync(
                It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()), Times.Never);
            Assert.True(accounting.Snapshot().Reservation!.Closed);
        }

        [Fact]
        public async Task Reservation_IsReleased_WhenRequestEndsBeforeProviderInvocation()
        {
            var context = new HttpContextBuilder()
                .ForChatCompletions()
                .WithVirtualKey(660)
                .Build();
            var accounting = context.GetOrCreateRequestAccountingContext();
            accounting.SetOperation(RequestOperation.ChatCompletion, 660, "model");
            accounting.RecordReservation(0.02m);
            var reservationService = new Mock<ISpendReservationService>();
            reservationService.Setup(x => x.ReleaseAsync(660, accounting.BillingRequestId))
                .Returns(Task.CompletedTask);
            context.RequestServices = new ServiceCollection()
                .AddSingleton(reservationService.Object)
                .BuildServiceProvider();

            await Invoker.WithNextDelegate(ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return Task.CompletedTask;
            }).InvokeAsync(context);

            reservationService.VerifyAll();
            reservationService.Verify(x => x.MarkIndeterminateAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.True(accounting.Snapshot().Reservation!.Closed);
        }

        [Fact]
        public async Task Reservation_IsRetained_WhenProviderInvocationStartedWithoutUsage()
        {
            var context = new HttpContextBuilder()
                .ForChatCompletions()
                .WithVirtualKey(661)
                .Build();
            var accounting = context.GetOrCreateRequestAccountingContext();
            accounting.SetOperation(RequestOperation.ChatCompletion, 661, "model");
            accounting.RecordReservation(0.02m);
            accounting.MarkInvocationStarted();
            var reservationService = new Mock<ISpendReservationService>();
            reservationService.Setup(x => x.MarkIndeterminateAsync(
                    661,
                    accounting.BillingRequestId,
                    It.Is<string>(reason => reason.Contains("without durable settlement"))))
                .Returns(Task.CompletedTask);
            context.RequestServices = new ServiceCollection()
                .AddSingleton(reservationService.Object)
                .BuildServiceProvider();

            await Invoker.WithNextDelegate(ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                return Task.CompletedTask;
            }).InvokeAsync(context);

            reservationService.VerifyAll();
            reservationService.Verify(x => x.ReleaseAsync(
                It.IsAny<int>(), It.IsAny<string>()), Times.Never);
            var snapshot = accounting.Snapshot();
            Assert.False(snapshot.Reservation!.Closed);
            Assert.True(snapshot.IsIndeterminate);
        }

        [Fact]
        public async Task Streaming_Response_WritesDirectlyToOriginalBodyBeforeDownstreamCompletes()
        {
            var usage = new Usage { PromptTokens = 5, CompletionTokens = 7, TotalTokens = 12 };
            var context = new HttpContextBuilder()
                .ForChatCompletions()
                .WithVirtualKey(657)
                .AsOpenAI()
                .AsStreaming(usage, "gpt-4")
                .Build();
            var originalBody = new MemoryStream();
            context.Response.Body = originalBody;
            var observedDuringDownstream = false;
            Fixture.SetupCostForModel("gpt-4", 0.001m);

            await Invoker.WithNextDelegate(async ctx =>
            {
                ctx.Response.ContentType = "text/event-stream";
                await ctx.Response.Body.FlushAsync();
                await ctx.Response.Body.WriteAsync("data: first\n\n"u8.ToArray());
                await ctx.Response.Body.FlushAsync();
                observedDuringDownstream = originalBody.Length > 0;
            }).InvokeAsync(context);

            Assert.True(observedDuringDownstream);
            Assert.Equal("data: first\n\n", System.Text.Encoding.UTF8.GetString(originalBody.ToArray()));
        }

        [Fact]
        public async Task Streaming_Response_Without_Usage_Bills_Known_Function_Cost()
        {
            var context = new HttpContextBuilder()
                .ForChatCompletions()
                .WithVirtualKey(656)
                .AsOpenAI()
                .AsStreaming()
                .Build();
            context.GetOrCreateRequestAccountingContext()
                .RecordFunctionExecutions([], 0.05m);

            await Invoker
                .AsStreamingResponse()
                .InvokeAsync(context);

            UsageTrackingAssertions.VerifySpendQueued(Fixture.BatchSpendService, 656, 0.05m);
            UsageTrackingAssertions.VerifyNoCostCalculation(Fixture.CostService);
            UsageTrackingAssertions.VerifyRequestLogged(Fixture.RequestLogService, dto =>
            {
                Assert.Equal(656, dto.VirtualKeyId);
                Assert.Equal("unknown", dto.ModelName);
                Assert.Equal(0, dto.InputTokens);
                Assert.Equal(0, dto.OutputTokens);
                Assert.Equal(0.05m, dto.Cost);
            });
            Assert.Contains(Fixture.CapturedBillingEvents,
                e => e.EventType == BillingAuditEventType.StreamingUsageMissing);
        }

        [Fact]
        public async Task Streaming_Response_Is_Billed_Before_Copy_To_Disconnected_Client()
        {
            var streamingUsage = new Usage
            {
                PromptTokens = 50,
                CompletionTokens = 150,
                TotalTokens = 200
            };

            var context = new HttpContextBuilder()
                .ForChatCompletions()
                .WithVirtualKey(655)
                .AsOpenAI()
                .AsStreaming(streamingUsage, "gpt-4")
                .Build();
            context.Response.Body = new ThrowingWriteStream();

            Fixture.SetupCostForModel("gpt-4", 0.006m);

            await Assert.ThrowsAsync<IOException>(() => Invoker
                .WithNextDelegate(async ctx =>
                {
                    ctx.Response.ContentType = "text/event-stream";
                    await ctx.Response.Body.WriteAsync("data: partial\n\n"u8.ToArray());
                })
                .InvokeAsync(context));

            UsageTrackingAssertions.VerifySpendQueued(Fixture.BatchSpendService, 655, 0.006m);
        }

        private sealed class ThrowingWriteStream : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) =>
                throw new IOException("Client disconnected");

            public override ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default) =>
                ValueTask.FromException(new IOException("Client disconnected"));

            public override Task WriteAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken) =>
                Task.FromException(new IOException("Client disconnected"));
        }

        [Fact]
        public async Task BatchSpendService_Unhealthy_Falls_Back_To_Direct_Update()
        {
            // Arrange
            var context = new HttpContextBuilder()
                .ForChatCompletions()
                .WithVirtualKey(987)
                .Build();

            Fixture.SetupCostForModel("gpt-3.5-turbo", 0.0001m);
            Fixture.SetupBatchSpendServiceHealth(false); // Batch service is unhealthy

            // Act
            await Invoker
                .WithResponse(ResponseBuilders.OpenAI()
                    .WithModel("gpt-3.5-turbo")
                    .WithUsage(10, 20)
                    .Build())
                .InvokeAsync(context);

            // Assert - Should use direct update instead of batch
            Fixture.BatchSpendService.Verify(
                x => x.QueueSpendUpdateAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()),
                Times.Never);
            UsageTrackingAssertions.VerifyDirectSpendUpdate(
                Fixture.VirtualKeyService,
                987,
                0.0001m);
        }

        [Fact]
        public async Task SpendPersistenceFailure_DoesNotLogSuccessfulBilling()
        {
            // Arrange
            var context = new HttpContextBuilder()
                .ForChatCompletions()
                .WithVirtualKey(988)
                .Build();

            Fixture.SetupCostForModel("gpt-3.5-turbo", 0.0001m);
            Fixture.BatchSpendService
                .Setup(x => x.QueueSpendUpdateAsync(988, 0.0001m, It.IsAny<DateTime?>()))
                .ThrowsAsync(new InvalidOperationException("Redis unavailable"));
            Fixture.VirtualKeyService
                .Setup(x => x.UpdateSpendAsync(988, 0.0001m, It.IsAny<DateTime?>()))
                .ReturnsAsync(false);

            // Act
            await Invoker
                .WithResponse(ResponseBuilders.OpenAI()
                    .WithModel("gpt-3.5-turbo")
                    .WithUsage(10, 20)
                    .Build())
                .InvokeAsync(context);

            // Assert
            Assert.DoesNotContain(Fixture.CapturedBillingEvents, e =>
                e.EventType == BillingAuditEventType.UsageTracked ||
                e.EventType == BillingAuditEventType.ToolUsageTracked);
            Assert.Contains(Fixture.CapturedBillingEvents,
                e => e.EventType == BillingAuditEventType.UnexpectedError);
            Fixture.BatchSpendService.Verify(
                x => x.QueueFallbackUpdate(988, 0.0001m, It.IsAny<DateTime?>()),
                Times.Once);
        }

        [Fact]
        public async Task Zero_Cost_Does_Not_Update_Spend()
        {
            // Arrange
            var context = new HttpContextBuilder()
                .ForChatCompletions()
                .WithVirtualKey(111)
                .Build();

            Fixture.SetupCostForModel("test-free-model", 0m);

            // Act
            await Invoker
                .WithResponse(ResponseBuilders.OpenAI()
                    .WithModel("test-free-model")
                    .WithUsage(10, 20)
                    .Build())
                .InvokeAsync(context);

            // Assert - No spend update for zero cost
            UsageTrackingAssertions.VerifyNoSpendUpdate(
                Fixture.BatchSpendService,
                Fixture.VirtualKeyService);
        }

        [Fact]
        public async Task Non_API_Request_Skips_Tracking()
        {
            // Arrange
            var context = new HttpContextBuilder()
                .ForNonApiPath()
                .Build();

            // Act
            await Invoker
                .AsPassthrough()
                .InvokeAsync(context);

            // Assert
            UsageTrackingAssertions.VerifyNoCostCalculation(Fixture.CostService);
            Fixture.BatchSpendService.Verify(
                x => x.QueueSpendUpdateAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()),
                Times.Never);
        }

        [Fact]
        public async Task Error_Response_Skips_Tracking()
        {
            // Arrange
            var context = new HttpContextBuilder()
                .ForChatCompletions()
                .WithVirtualKey(123)
                .AsError(400)
                .Build();

            // Act
            await Invoker
                .AsPassthrough()
                .InvokeAsync(context);

            // Assert - No cost calculation or spend update should occur
            UsageTrackingAssertions.VerifyNoCostCalculation(Fixture.CostService);
            Fixture.BatchSpendService.Verify(
                x => x.QueueSpendUpdateAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()),
                Times.Never);

            // Assert - Debug log should indicate billing was skipped due to error response
            UsageTrackingAssertions.VerifyDebugLog(
                Fixture.Logger,
                "Billing Policy: Skipping billing for error response");
        }

        [Theory]
        [InlineData(400)] // Bad Request
        [InlineData(401)] // Unauthorized
        [InlineData(404)] // Not Found
        [InlineData(429)] // Rate Limited
        [InlineData(500)] // Internal Server Error
        [InlineData(503)] // Service Unavailable
        public async Task Billing_Policy_Skips_All_Error_Status_Codes(int statusCode)
        {
            // Arrange
            var context = new HttpContextBuilder()
                .ForChatCompletions()
                .WithVirtualKey(123)
                .AsError(statusCode)
                .Build();

            // Act
            await Invoker
                .AsPassthrough()
                .InvokeAsync(context);

            // Assert - No billing should occur for any error status
            UsageTrackingAssertions.VerifyNoCostCalculation(Fixture.CostService);
            Fixture.BatchSpendService.Verify(
                x => x.QueueSpendUpdateAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()),
                Times.Never);

            // Assert - Appropriate debug logging
            UsageTrackingAssertions.VerifyDebugLog(
                Fixture.Logger,
                "Billing Policy: Skipping billing for error response");
        }

        [Fact]
        public async Task Missing_VirtualKey_Skips_Tracking()
        {
            // Arrange - Don't add VirtualKeyId to context
            var context = new HttpContextBuilder()
                .ForChatCompletions()
                // Note: NOT calling WithVirtualKey()
                .Build();

            // Act
            await Invoker
                .AsPassthrough()
                .InvokeAsync(context);

            // Assert
            UsageTrackingAssertions.VerifyNoCostCalculation(Fixture.CostService);

            // Assert - Debug log should indicate no virtual key
            UsageTrackingAssertions.VerifyDebugLog(
                Fixture.Logger,
                "Billing Policy: Skipping billing - no virtual key found");
        }

        [Fact]
        public async Task Response_Time_Tracking()
        {
            // Arrange
            var startTime = DateTime.UtcNow.AddMilliseconds(-250); // 250ms ago

            var context = new HttpContextBuilder()
                .ForChatCompletions()
                .WithVirtualKey(555)
                .WithRequestStartTime(startTime)
                .Build();

            Fixture.SetupCostForModel("gpt-4", 0.001m);

            // Act
            await Invoker
                .WithResponse(ResponseBuilders.OpenAI()
                    .WithModel("gpt-4")
                    .WithUsage(10, 20)
                    .Build())
                .InvokeAsync(context);

            // Assert - Response time should be captured
            UsageTrackingAssertions.VerifyRequestLogged(Fixture.RequestLogService, dto =>
            {
                Assert.True(dto.ResponseTimeMs >= 250, $"Expected ResponseTimeMs >= 250, got {dto.ResponseTimeMs}");
                Assert.True(dto.ResponseTimeMs <= 1000, $"Expected ResponseTimeMs <= 1000, got {dto.ResponseTimeMs}");
            });
        }
    }
}
