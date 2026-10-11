using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Middleware;
using IVirtualKeyService = ConduitLLM.Core.Interfaces.IVirtualKeyService;

namespace ConduitLLM.Tests.Http.Middleware.Assertions
{
    /// <summary>
    /// Assertion helpers for UsageTrackingMiddleware tests.
    /// Provides fluent, readable assertions for common verification patterns.
    /// </summary>
    public static class UsageTrackingAssertions
    {
        /// <summary>
        /// Verifies that cost was calculated for the specified model with expected usage.
        /// </summary>
        /// <param name="costService">The mocked cost service.</param>
        /// <param name="expectedModel">The expected model name.</param>
        /// <param name="expectedPromptTokens">Optional expected prompt token count.</param>
        /// <param name="expectedCompletionTokens">Optional expected completion token count.</param>
        public static void VerifyCostCalculated(
            Mock<ICostCalculationService> costService,
            string expectedModel,
            int? expectedPromptTokens = null,
            int? expectedCompletionTokens = null)
        {
            costService.Verify(x => x.CalculateCostAsync(
                expectedModel,
                It.Is<Usage>(u =>
                    (!expectedPromptTokens.HasValue || u.PromptTokens == expectedPromptTokens) &&
                    (!expectedCompletionTokens.HasValue || u.CompletionTokens == expectedCompletionTokens)),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        /// <summary>
        /// Verifies that cost was calculated for any model.
        /// </summary>
        /// <param name="costService">The mocked cost service.</param>
        public static void VerifyCostCalculatedOnce(Mock<ICostCalculationService> costService)
        {
            costService.Verify(
                x => x.CalculateCostAsync(It.IsAny<string>(), It.IsAny<Usage>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies that no cost calculation was performed.
        /// </summary>
        /// <param name="costService">The mocked cost service.</param>
        public static void VerifyNoCostCalculation(Mock<ICostCalculationService> costService)
        {
            costService.Verify(
                x => x.CalculateCostAsync(It.IsAny<string>(), It.IsAny<Usage>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// Verifies that spend was queued via the batch service.
        /// </summary>
        /// <param name="batchService">The mocked batch spend service.</param>
        /// <param name="expectedVirtualKeyId">The expected virtual key ID.</param>
        /// <param name="expectedCost">The expected cost amount.</param>
        public static void VerifySpendQueued(
            Mock<IBatchSpendUpdateService> batchService,
            int expectedVirtualKeyId,
            decimal expectedCost)
        {
            batchService.Verify(
                x => x.QueueSpendUpdateAsync(expectedVirtualKeyId, expectedCost, It.IsAny<DateTime?>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies that spend was queued for any amount.
        /// </summary>
        /// <param name="batchService">The mocked batch spend service.</param>
        /// <param name="expectedVirtualKeyId">The expected virtual key ID.</param>
        public static void VerifySpendQueuedAny(
            Mock<IBatchSpendUpdateService> batchService,
            int expectedVirtualKeyId)
        {
            batchService.Verify(
                x => x.QueueSpendUpdateAsync(expectedVirtualKeyId, It.IsAny<decimal>(), It.IsAny<DateTime?>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies that no spend updates occurred.
        /// </summary>
        /// <param name="batchService">The mocked batch spend service.</param>
        /// <param name="virtualKeyService">The mocked virtual key service.</param>
        public static void VerifyNoSpendUpdate(
            Mock<IBatchSpendUpdateService> batchService,
            Mock<IVirtualKeyService> virtualKeyService)
        {
            batchService.Verify(
                x => x.QueueSpendUpdateAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()),
                Times.Never);
            virtualKeyService.Verify(
                x => x.UpdateSpendAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()),
                Times.Never);
        }

        /// <summary>
        /// Verifies that direct spend update was called (fallback when batch service is unhealthy).
        /// </summary>
        /// <param name="virtualKeyService">The mocked virtual key service.</param>
        /// <param name="expectedVirtualKeyId">The expected virtual key ID.</param>
        /// <param name="expectedCost">The expected cost amount.</param>
        public static void VerifyDirectSpendUpdate(
            Mock<IVirtualKeyService> virtualKeyService,
            int expectedVirtualKeyId,
            decimal expectedCost)
        {
            virtualKeyService.Verify(
                x => x.UpdateSpendAsync(expectedVirtualKeyId, expectedCost, It.IsAny<DateTime?>()),
                Times.Once);
        }

        /// <summary>
        /// Verifies request was logged with expected properties.
        /// </summary>
        /// <param name="requestLogService">The mocked request log service.</param>
        /// <param name="assertions">Action to perform assertions on the captured DTO.</param>
        public static void VerifyRequestLogged(
            Mock<IRequestLogService> requestLogService,
            Action<LogRequestDto> assertions)
        {
            LogRequestDto? capturedDto = null;
            requestLogService.Verify(x => x.LogRequestAsync(It.IsAny<LogRequestDto>()), Times.Once);

            // Extract the captured DTO from the invocations
            var invocation = requestLogService.Invocations
                .FirstOrDefault(i => i.Method.Name == "LogRequestAsync");
            if (invocation != null && invocation.Arguments.Count > 0)
            {
                capturedDto = invocation.Arguments[0] as LogRequestDto;
            }

            Assert.NotNull(capturedDto);
            assertions(capturedDto!);
        }

        /// <summary>
        /// Verifies request was logged for a specific virtual key and model.
        /// </summary>
        /// <param name="requestLogService">The mocked request log service.</param>
        /// <param name="expectedVirtualKeyId">The expected virtual key ID.</param>
        /// <param name="expectedModel">The expected model name.</param>
        /// <param name="expectedRequestType">The expected request type.</param>
        public static void VerifyRequestLogged(
            Mock<IRequestLogService> requestLogService,
            int expectedVirtualKeyId,
            string expectedModel,
            string expectedRequestType = "chat")
        {
            requestLogService.Verify(x => x.LogRequestAsync(It.Is<LogRequestDto>(dto =>
                dto.VirtualKeyId == expectedVirtualKeyId &&
                dto.ModelName == expectedModel &&
                dto.RequestType == expectedRequestType)), Times.Once);
        }

        /// <summary>
        /// Verifies that no request was logged.
        /// </summary>
        /// <param name="requestLogService">The mocked request log service.</param>
        public static void VerifyNoRequestLogged(Mock<IRequestLogService> requestLogService)
        {
            requestLogService.Verify(
                x => x.LogRequestAsync(It.IsAny<LogRequestDto>()),
                Times.Never);
        }

        /// <summary>
        /// Verifies that cached tokens were tracked correctly for Anthropic responses.
        /// </summary>
        /// <param name="costService">The mocked cost service.</param>
        /// <param name="model">The expected model name.</param>
        /// <param name="expectedCacheCreation">Expected cache creation tokens.</param>
        /// <param name="expectedCacheRead">Expected cache read tokens.</param>
        public static void VerifyAnthropicCaching(
            Mock<ICostCalculationService> costService,
            string model,
            int expectedCacheCreation,
            int expectedCacheRead)
        {
            costService.Verify(x => x.CalculateCostAsync(
                model,
                It.Is<Usage>(u =>
                    u.CachedWriteTokens == expectedCacheCreation &&
                    u.CachedInputTokens == expectedCacheRead),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        /// <summary>
        /// Verifies image usage was tracked correctly.
        /// </summary>
        /// <param name="costService">The mocked cost service.</param>
        /// <param name="model">The expected model name.</param>
        /// <param name="expectedImageCount">The expected number of images.</param>
        /// <param name="expectedQuality">Optional expected image quality.</param>
        /// <param name="expectedSize">Optional expected image size.</param>
        public static void VerifyImageUsage(
            Mock<ICostCalculationService> costService,
            string model,
            int expectedImageCount,
            string? expectedQuality = null,
            string? expectedSize = null)
        {
            costService.Verify(x => x.CalculateCostAsync(
                model,
                It.Is<Usage>(u =>
                    u.ImageCount == expectedImageCount &&
                    (expectedQuality == null || u.ImageQuality == expectedQuality) &&
                    (expectedSize == null || u.ImageResolution == expectedSize)),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        /// <summary>
        /// Verifies billing audit event was captured with expected type.
        /// </summary>
        /// <param name="events">The list of captured billing events.</param>
        /// <param name="expectedType">The expected event type.</param>
        /// <param name="additionalAssertions">Optional additional assertions.</param>
        public static void VerifyBillingEvent(
            List<BillingAuditEvent> events,
            BillingAuditEventType expectedType,
            Action<BillingAuditEvent>? additionalAssertions = null)
        {
            var matchingEvent = events.FirstOrDefault(e => e.EventType == expectedType);
            Assert.NotNull(matchingEvent);
            additionalAssertions?.Invoke(matchingEvent);
        }

        /// <summary>
        /// Verifies that a billing event with tool usage was captured.
        /// </summary>
        /// <param name="events">The list of captured billing events.</param>
        /// <param name="expectedToolName">The expected tool name in the JSON.</param>
        /// <param name="expectedToolCost">The expected tool usage cost.</param>
        public static void VerifyToolUsageBillingEvent(
            List<BillingAuditEvent> events,
            string expectedToolName,
            decimal expectedToolCost)
        {
            var evt = events.FirstOrDefault(e => e.EventType == BillingAuditEventType.ToolUsageTracked);
            Assert.NotNull(evt);
            Assert.NotNull(evt.ToolUsageJson);
            Assert.Contains(expectedToolName, evt.ToolUsageJson);
            Assert.Equal(expectedToolCost, evt.ToolUsageCost);
        }

        /// <summary>
        /// Verifies that no billing events were captured.
        /// </summary>
        /// <param name="events">The list of captured billing events.</param>
        public static void VerifyNoBillingEvents(List<BillingAuditEvent> events)
        {
            Assert.Empty(events);
        }

        /// <summary>
        /// Verifies that exactly one billing event was captured.
        /// </summary>
        /// <param name="events">The list of captured billing events.</param>
        /// <returns>The single captured event for further assertions.</returns>
        public static BillingAuditEvent VerifySingleBillingEvent(List<BillingAuditEvent> events)
        {
            Assert.Single(events);
            return events[0];
        }

        /// <summary>
        /// Verifies that a specific log message was emitted.
        /// </summary>
        /// <param name="logger">The mocked logger.</param>
        /// <param name="level">The expected log level.</param>
        /// <param name="containsMessage">Text that should be in the log message.</param>
        public static void VerifyLogMessage(
            Mock<ILogger<UsageTrackingMiddleware>> logger,
            LogLevel level,
            string containsMessage)
        {
            logger.Verify(
                x => x.Log(
                    level,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v != null && v.ToString()!.Contains(containsMessage)),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce);
        }

        /// <summary>
        /// Verifies that a debug log message was emitted.
        /// </summary>
        /// <param name="logger">The mocked logger.</param>
        /// <param name="containsMessage">Text that should be in the log message.</param>
        public static void VerifyDebugLog(
            Mock<ILogger<UsageTrackingMiddleware>> logger,
            string containsMessage)
        {
            VerifyLogMessage(logger, LogLevel.Debug, containsMessage);
        }

        /// <summary>
        /// Verifies that a warning log message was emitted.
        /// </summary>
        /// <param name="logger">The mocked logger.</param>
        /// <param name="containsMessage">Text that should be in the log message.</param>
        public static void VerifyWarningLog(
            Mock<ILogger<UsageTrackingMiddleware>> logger,
            string containsMessage)
        {
            VerifyLogMessage(logger, LogLevel.Warning, containsMessage);
        }

        /// <summary>
        /// Verifies the full usage tracking flow for a successful request.
        /// </summary>
        /// <param name="costService">The mocked cost service.</param>
        /// <param name="batchService">The mocked batch spend service.</param>
        /// <param name="requestLogService">The mocked request log service.</param>
        /// <param name="model">The expected model name.</param>
        /// <param name="virtualKeyId">The expected virtual key ID.</param>
        /// <param name="cost">The expected cost.</param>
        public static void VerifySuccessfulUsageTracking(
            Mock<ICostCalculationService> costService,
            Mock<IBatchSpendUpdateService> batchService,
            Mock<IRequestLogService> requestLogService,
            string model,
            int virtualKeyId,
            decimal cost)
        {
            VerifyCostCalculated(costService, model);
            VerifySpendQueued(batchService, virtualKeyId, cost);
            VerifyRequestLogged(requestLogService, virtualKeyId, model);
        }
    }
}
