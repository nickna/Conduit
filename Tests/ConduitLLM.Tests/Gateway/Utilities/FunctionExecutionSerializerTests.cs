using System.Text.Json;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Utilities;

namespace ConduitLLM.Tests.Http.Utilities
{
    public class FunctionExecutionSerializerTests
    {
        #region SerializeFunctionExecutionResults Tests

        [Fact]
        public void SerializeFunctionExecutionResults_WithNull_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                FunctionExecutionSerializer.SerializeFunctionExecutionResults(null!));
        }

        [Fact]
        public void SerializeFunctionExecutionResults_WithEmptyList_ReturnsValidJson()
        {
            // Arrange
            var results = new List<ToolExecutionEvent>();

            // Act
            var json = FunctionExecutionSerializer.SerializeFunctionExecutionResults(results);

            // Assert
            Assert.NotNull(json);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("chat_with_functions", root.GetProperty("type").GetString());
            Assert.Equal(0, root.GetProperty("functionCallCount").GetInt32());
            Assert.Equal(0m, root.GetProperty("totalCost").GetDecimal());
            Assert.Equal(0, root.GetProperty("successCount").GetInt32());
            Assert.Equal(0, root.GetProperty("failedCount").GetInt32());
            Assert.Equal(0, root.GetProperty("functionCalls").GetArrayLength());
        }

        [Fact]
        public void SerializeFunctionExecutionResults_WithSingleCompletedResult_SerializesCorrectly()
        {
            // Arrange
            var executionId = Guid.NewGuid();
            var results = new List<ToolExecutionEvent>
            {
                new ToolExecutionEvent
                {
                    ToolCallId = "call_123",
                    FunctionName = "get_weather",
                    Status = "completed",
                    Cost = 0.005m,
                    ErrorMessage = null,
                    FunctionExecutionId = executionId
                }
            };

            // Act
            var json = FunctionExecutionSerializer.SerializeFunctionExecutionResults(results);

            // Assert
            Assert.NotNull(json);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("chat_with_functions", root.GetProperty("type").GetString());
            Assert.Equal(1, root.GetProperty("functionCallCount").GetInt32());
            Assert.Equal(0.005m, root.GetProperty("totalCost").GetDecimal());
            Assert.Equal(1, root.GetProperty("successCount").GetInt32());
            Assert.Equal(0, root.GetProperty("failedCount").GetInt32());

            var functionCalls = root.GetProperty("functionCalls");
            Assert.Equal(1, functionCalls.GetArrayLength());

            var call = functionCalls[0];
            Assert.Equal("call_123", call.GetProperty("tool_call_id").GetString());
            Assert.Equal("get_weather", call.GetProperty("function_name").GetString());
            Assert.Equal("completed", call.GetProperty("status").GetString());
            Assert.Equal(0.005m, call.GetProperty("cost").GetDecimal());
            Assert.False(call.TryGetProperty("error_message", out _));
            Assert.Equal(executionId.ToString(), call.GetProperty("function_execution_id").GetString());
        }

        [Fact]
        public void SerializeFunctionExecutionResults_WithSingleFailedResult_SerializesCorrectly()
        {
            // Arrange
            var executionId = Guid.NewGuid();
            var results = new List<ToolExecutionEvent>
            {
                new ToolExecutionEvent
                {
                    ToolCallId = "call_456",
                    FunctionName = "search_web",
                    Status = "failed",
                    Cost = 0.001m,
                    ErrorMessage = "Rate limit exceeded",
                    FunctionExecutionId = executionId
                }
            };

            // Act
            var json = FunctionExecutionSerializer.SerializeFunctionExecutionResults(results);

            // Assert
            Assert.NotNull(json);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal(1, root.GetProperty("functionCallCount").GetInt32());
            Assert.Equal(0.001m, root.GetProperty("totalCost").GetDecimal());
            Assert.Equal(0, root.GetProperty("successCount").GetInt32());
            Assert.Equal(1, root.GetProperty("failedCount").GetInt32());

            var call = root.GetProperty("functionCalls")[0];
            Assert.Equal("failed", call.GetProperty("status").GetString());
            Assert.Equal("Rate limit exceeded", call.GetProperty("error_message").GetString());
        }

        [Fact]
        public void SerializeFunctionExecutionResults_WithMixedResults_CalculatesCountsCorrectly()
        {
            // Arrange
            var results = new List<ToolExecutionEvent>
            {
                new ToolExecutionEvent
                {
                    ToolCallId = "call_1",
                    FunctionName = "func1",
                    Status = "completed",
                    Cost = 0.01m,
                    FunctionExecutionId = Guid.NewGuid()
                },
                new ToolExecutionEvent
                {
                    ToolCallId = "call_2",
                    FunctionName = "func2",
                    Status = "completed",
                    Cost = 0.02m,
                    FunctionExecutionId = Guid.NewGuid()
                },
                new ToolExecutionEvent
                {
                    ToolCallId = "call_3",
                    FunctionName = "func3",
                    Status = "failed",
                    Cost = 0.005m,
                    ErrorMessage = "Timeout",
                    FunctionExecutionId = Guid.NewGuid()
                },
                new ToolExecutionEvent
                {
                    ToolCallId = "call_4",
                    FunctionName = "func4",
                    Status = "completed",
                    Cost = null, // No cost
                    FunctionExecutionId = Guid.NewGuid()
                }
            };

            // Act
            var json = FunctionExecutionSerializer.SerializeFunctionExecutionResults(results);

            // Assert
            Assert.NotNull(json);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal(4, root.GetProperty("functionCallCount").GetInt32());
            Assert.Equal(0.035m, root.GetProperty("totalCost").GetDecimal()); // 0.01 + 0.02 + 0.005 + 0 = 0.035
            Assert.Equal(3, root.GetProperty("successCount").GetInt32());
            Assert.Equal(1, root.GetProperty("failedCount").GetInt32());
            Assert.Equal(4, root.GetProperty("functionCalls").GetArrayLength());
        }

        [Fact]
        public void SerializeFunctionExecutionResults_WithNullOptionalFields_OmitsThem()
        {
            // Arrange
            var results = new List<ToolExecutionEvent>
            {
                new ToolExecutionEvent
                {
                    ToolCallId = null,
                    FunctionName = null,
                    Status = string.Empty,
                    Cost = null,
                    ErrorMessage = null,
                    FunctionExecutionId = null
                }
            };

            // Act
            var json = FunctionExecutionSerializer.SerializeFunctionExecutionResults(results);

            // Assert
            Assert.NotNull(json);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Empty status doesn't match "completed" or "failed".
            Assert.Equal(0, root.GetProperty("successCount").GetInt32());
            Assert.Equal(0, root.GetProperty("failedCount").GetInt32());

            var call = root.GetProperty("functionCalls")[0];
            Assert.Equal(string.Empty, call.GetProperty("status").GetString());
            Assert.False(call.TryGetProperty("tool_call_id", out _));
            Assert.False(call.TryGetProperty("function_name", out _));
            Assert.False(call.TryGetProperty("cost", out _));
            Assert.False(call.TryGetProperty("error_message", out _));
            Assert.False(call.TryGetProperty("function_execution_id", out _));
        }

        [Fact]
        public void SerializeFunctionExecutionResults_ReturnsNonIndentedJson()
        {
            // Arrange
            var results = new List<ToolExecutionEvent>
            {
                new ToolExecutionEvent
                {
                    ToolCallId = "call_1",
                    FunctionName = "test",
                    Status = "completed"
                }
            };

            // Act
            var json = FunctionExecutionSerializer.SerializeFunctionExecutionResults(results);

            // Assert
            Assert.DoesNotContain("\n", json);
            Assert.DoesNotContain("  ", json); // No indentation
        }

        [Fact]
        public void SerializeFunctionExecutionResults_WithZeroCosts_CalculatesTotalCorrectly()
        {
            // Arrange
            var results = new List<ToolExecutionEvent>
            {
                new ToolExecutionEvent { Status = "completed", Cost = 0m },
                new ToolExecutionEvent { Status = "completed", Cost = 0m },
                new ToolExecutionEvent { Status = "completed", Cost = null }
            };

            // Act
            var json = FunctionExecutionSerializer.SerializeFunctionExecutionResults(results);

            // Assert
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(0m, doc.RootElement.GetProperty("totalCost").GetDecimal());
        }

        [Fact]
        public void SerializeFunctionExecutionResults_WithLargeCost_PreservesPrecision()
        {
            // Arrange
            var results = new List<ToolExecutionEvent>
            {
                new ToolExecutionEvent
                {
                    Status = "completed",
                    Cost = 123.456789m
                }
            };

            // Act
            var json = FunctionExecutionSerializer.SerializeFunctionExecutionResults(results);

            // Assert
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(123.456789m, doc.RootElement.GetProperty("totalCost").GetDecimal());
        }

        #endregion

        #region DeserializeFunctionExecutionMetadata Tests

        [Fact]
        public void DeserializeFunctionExecutionMetadata_WithNull_ReturnsNull()
        {
            var result = FunctionExecutionSerializer.DeserializeFunctionExecutionMetadata(null);
            Assert.Null(result);
        }

        [Fact]
        public void DeserializeFunctionExecutionMetadata_WithEmptyString_ReturnsNull()
        {
            var result = FunctionExecutionSerializer.DeserializeFunctionExecutionMetadata("");
            Assert.Null(result);
        }

        [Fact]
        public void DeserializeFunctionExecutionMetadata_WithWhitespace_ReturnsNull()
        {
            var result = FunctionExecutionSerializer.DeserializeFunctionExecutionMetadata("   ");
            Assert.Null(result);
        }

        [Fact]
        public void DeserializeFunctionExecutionMetadata_WithInvalidJson_ReturnsNull()
        {
            var result = FunctionExecutionSerializer.DeserializeFunctionExecutionMetadata("{ invalid json }");
            Assert.Null(result);
        }

        [Fact]
        public void DeserializeFunctionExecutionMetadata_WithValidJson_ReturnsMetadata()
        {
            // Arrange
            var json = @"{
                ""type"": ""chat_with_functions"",
                ""functionCallCount"": 2,
                ""totalCost"": 0.015,
                ""successCount"": 1,
                ""failedCount"": 1
            }";

            // Act
            var result = FunctionExecutionSerializer.DeserializeFunctionExecutionMetadata(json);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("chat_with_functions", result.Type);
            Assert.Equal(2, result.FunctionCallCount);
            Assert.Equal(0.015m, result.TotalCost);
            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(1, result.FailedCount);
        }

        [Fact]
        public void DeserializeFunctionExecutionMetadata_WithLegacyCamelCaseCall_ReadsCall()
        {
            var executionId = Guid.NewGuid();
            var json = $$"""
                {
                  "type": "chat_with_functions",
                  "functionCallCount": 1,
                  "totalCost": 0.015,
                  "successCount": 1,
                  "failedCount": 0,
                  "functionCalls": [{
                    "toolCallId": "legacy-call",
                    "functionName": "legacy-function",
                    "status": "completed",
                    "cost": 0.015,
                    "errorMessage": null,
                    "functionExecutionId": "{{executionId}}"
                  }]
                }
                """;

            var result = FunctionExecutionSerializer.DeserializeFunctionExecutionMetadata(json);

            Assert.NotNull(result);
            var call = Assert.Single(result.FunctionCalls!);
            Assert.Equal("legacy-call", call.ToolCallId);
            Assert.Equal("legacy-function", call.FunctionName);
            Assert.Equal(executionId, call.FunctionExecutionId);
        }

        #endregion

        #region Roundtrip Tests

        [Fact]
        public void SerializeAndDeserialize_RoundTrip_PreservesStructure()
        {
            // Arrange
            var executionId = Guid.NewGuid();
            var results = new List<ToolExecutionEvent>
            {
                new ToolExecutionEvent
                {
                    ToolCallId = "call_roundtrip",
                    FunctionName = "test_function",
                    Status = "completed",
                    Cost = 0.123m,
                    FunctionExecutionId = executionId
                }
            };

            // Act
            var json = FunctionExecutionSerializer.SerializeFunctionExecutionResults(results);
            var deserialized = FunctionExecutionSerializer.DeserializeFunctionExecutionMetadata(json);

            // Assert
            Assert.NotNull(deserialized);
            Assert.Equal("chat_with_functions", deserialized.Type);
            Assert.Equal(1, deserialized.FunctionCallCount);
            Assert.Equal(0.123m, deserialized.TotalCost);
            Assert.Equal(1, deserialized.SuccessCount);
            Assert.Equal(0, deserialized.FailedCount);
        }

        [Fact]
        public void SerializeAndDeserialize_WithBillingComponents_PreservesCorrelationAndIteration()
        {
            var results = new List<ToolExecutionEvent>
            {
                new()
                {
                    Iteration = 3,
                    ToolCallId = "failed-call",
                    Status = "failed",
                    Cost = 0.02m,
                    FunctionExecutionId = Guid.NewGuid()
                }
            };
            var breakdown = new FunctionExecutionCostBreakdown(0.10m, 0.02m, 0m, 0.12m);

            var json = FunctionExecutionSerializer.SerializeFunctionExecutionResults(
                results, "billing-parent", "http-parent", breakdown);
            var restored = FunctionExecutionSerializer.DeserializeFunctionExecutionMetadata(json)!;

            Assert.Equal("billing-parent", restored.BillingRequestId);
            Assert.Equal("http-parent", restored.RequestId);
            Assert.Equal(breakdown, restored.CostBreakdown);
            Assert.Equal(0.02m, restored.TotalCost);
            Assert.Equal(3, Assert.Single(restored.FunctionCalls!).Iteration);
            Assert.Equal(results[0].FunctionExecutionId, restored.FunctionCalls[0].FunctionExecutionId);
        }

        [Fact]
        public void SerializeFunctionExecutionResults_WithoutBillingComponents_PreservesLegacyShape()
        {
            var json = FunctionExecutionSerializer.SerializeFunctionExecutionResults(
                [new ToolExecutionEvent { Status = "completed", Cost = 0.01m }]);
            using var document = System.Text.Json.JsonDocument.Parse(json);

            Assert.False(document.RootElement.TryGetProperty("billingRequestId", out _));
            Assert.False(document.RootElement.TryGetProperty("requestId", out _));
            Assert.False(document.RootElement.TryGetProperty("costBreakdown", out _));
            Assert.False(document.RootElement.TryGetProperty("providerToolUsage", out _));
            Assert.False(document.RootElement.GetProperty("functionCalls")[0].TryGetProperty("iteration", out _));
        }

        #endregion
    }
}
