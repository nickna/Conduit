using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.Time.Testing;

namespace ConduitLLM.Tests.Core.Services
{
    public partial class PerformanceMetricsServiceTests
    {
        private readonly IPerformanceMetricsService _service;
        private readonly FakeTimeProvider _clock = new();

        public PerformanceMetricsServiceTests()
        {
            _service = new PerformanceMetricsService(_clock);
        }

        private static ChatCompletionResponse CreateTestResponse(string model = "gpt-4", Usage? usage = null, List<Choice>? choices = null)
        {
            return new ChatCompletionResponse
            {
                Id = "test-123",
                Model = model,
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Object = "chat.completion",
                Choices = choices ?? new List<Choice>(),
                Usage = usage
            };
        }
    }
}
