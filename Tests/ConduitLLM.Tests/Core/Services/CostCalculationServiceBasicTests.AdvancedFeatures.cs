using ConduitLLM.Core.Models;
using ConduitLLM.Tests.TestHelpers;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using ConduitLLM.Configuration.Entities;

namespace ConduitLLM.Tests.Core.Services
{
    public partial class CostCalculationServiceBasicTests
    {
        // Attribute numeric literals are doubles. Keep financial expectations
        // decimal end to end instead of relying on test-runner coercion.
        public static TheoryData<int, int, decimal, decimal, decimal> TokenCostCases => new()
        {
            { 1000, 500, 10.00m, 30.00m, 0.025m },
            { 0, 100, 10.00m, 30.00m, 0.003m },
            { 500, 0, 10.00m, 30.00m, 0.005m },
            { 10000, 5000, 5.00m, 15.00m, 0.125m }
        };

        [Theory]
        [MemberData(nameof(TokenCostCases))]
        public async Task CalculateCostAsync_WithVariousTokenCounts_CalculatesCorrectly(
            int promptTokens, int completionTokens, decimal inputCost, decimal outputCost, decimal expectedCost)
        {
            // Arrange
            var modelId = "test/model";
            var usage = new Usage
            {
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = promptTokens + completionTokens
            };
            var modelCost = new ModelCost
            {
                CostName = modelId,
                InputCostPerMillionTokens = inputCost,
                OutputCostPerMillionTokens = outputCost
            };

            _modelCostServiceMock
                .Setup(x => x.GetCostForModelAsync(modelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(modelCost);

            // Act
            var result = await _service.CalculateCostAsync(modelId, usage);

            // Assert
            result.Should().Be(expectedCost);
        }

        [Fact]
        public async Task CalculateCostAsync_WithDecimalPrecision_MaintainsAccuracy()
        {
            // Arrange
            var modelId = "precision/model";
            var usage = new Usage
            {
                PromptTokens = 1,
                CompletionTokens = 1,
                TotalTokens = 2
            };
            var modelCost = new ModelCost
            {
                CostName = modelId,
                InputCostPerMillionTokens = 0.000001m,  // Very small cost
                OutputCostPerMillionTokens = 0.000002m  // Very small cost
            };

            _modelCostServiceMock
                .Setup(x => x.GetCostForModelAsync(modelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(modelCost);

            // Act
            var result = await _service.CalculateCostAsync(modelId, usage);

            // Assert
            // Expected: (1 * 0.000001 / 1_000_000) + (1 * 0.000002 / 1_000_000) = 0.000000000001 + 0.000000000002 = 0.000000000003
            result.Should().Be(0.000000000003m);
        }

        [Fact]
        public async Task CalculateCostAsync_WithReasoningTokens_PricesCompletionSubsetOnce()
        {
            var modelId = "reasoning/model";
            var usage = new Usage
            {
                PromptTokens = 1_000,
                CompletionTokens = 500,
                ReasoningTokens = 200,
                TotalTokens = 1_500
            };
            var modelCost = new ModelCost
            {
                CostName = modelId,
                InputCostPerMillionTokens = 10m,
                OutputCostPerMillionTokens = 30m,
                ReasoningCostPerMillionTokens = 60m
            };

            _modelCostServiceMock
                .Setup(x => x.GetCostForModelAsync(modelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(modelCost);

            var result = await _service.CalculateCostAsync(modelId, usage);

            // Input: .010; ordinary output: 300 * 30 / 1M = .009; reasoning: 200 * 60 / 1M = .012.
            result.Should().Be(0.031m);
        }

        [Fact]
        public async Task CalculateCostAsync_WithReasoningAtOutputRate_DoesNotIncreaseCompletionCost()
        {
            var modelId = "reasoning/model";
            var usage = new Usage { CompletionTokens = 500, ReasoningTokens = 200 };
            var modelCost = new ModelCost
            {
                CostName = modelId,
                InputCostPerMillionTokens = 10m,
                OutputCostPerMillionTokens = 30m
            };

            _modelCostServiceMock
                .Setup(x => x.GetCostForModelAsync(modelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(modelCost);

            var result = await _service.CalculateCostAsync(modelId, usage);

            result.Should().Be(0.015m);
        }

        [Fact]
        public async Task CalculateCostAsync_WithCancellationToken_PropagatesToken()
        {
            // Arrange
            var modelId = "openai/gpt-4o";
            var usage = new Usage { PromptTokens = 100, CompletionTokens = 50, TotalTokens = 150 };
            var modelCost = new ModelCost
            {
                CostName = modelId,
                InputCostPerMillionTokens = 10.00m,
                OutputCostPerMillionTokens = 30.00m
            };

            using var cts = new CancellationTokenSource();

            _modelCostServiceMock
                .Setup(x => x.GetCostForModelAsync(modelId, cts.Token))
                .ReturnsAsync(modelCost);

            // Act
            var result = await _service.CalculateCostAsync(modelId, usage, cts.Token);

            // Assert
            result.Should().BePositive();
            _modelCostServiceMock.Verify(x => x.GetCostForModelAsync(modelId, cts.Token), Times.Once);
        }

        [Fact]
        public async Task CalculateCostAsync_LogsDebugInformation()
        {
            // Arrange
            var modelId = "test/model";
            var usage = new Usage
            {
                PromptTokens = 100,
                CompletionTokens = 50,
                TotalTokens = 150
            };
            var modelCost = new ModelCost
            {
                CostName = modelId,
                InputCostPerMillionTokens = 1000.00m,
                OutputCostPerMillionTokens = 2000.00m
            };

            _modelCostServiceMock
                .Setup(x => x.GetCostForModelAsync(modelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(modelCost);

            // Act
            await _service.CalculateCostAsync(modelId, usage);

            // Assert
            // With polymorphic pricing, we now log twice - once in the specific calculation method and once in the main method
            _loggerMock.VerifyLog(LogLevel.Debug, "Calculated cost", Times.Exactly(2));
        }
    }
}
